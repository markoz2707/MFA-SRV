using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MfaSrv.DcAgent.Services;

public class CachedSession
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public required string SourceIp { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string VerifiedMethod { get; init; }
    public bool Revoked { get; set; }
}

public class SessionCacheService
{
    private readonly ConcurrentDictionary<string, CachedSession> _sessions = new();
    private readonly ILogger<SessionCacheService> _logger;
    private readonly SqliteCacheStore _store;

    public SessionCacheService(ILogger<SessionCacheService> logger, SqliteCacheStore store)
    {
        _logger = logger;
        _store = store;
    }

    /// <summary>
    /// Loads active (non-expired, non-revoked) sessions from SQLite into the in-memory cache.
    /// Call once at startup after SqliteCacheStore.InitializeAsync().
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var sessions = await _store.LoadAllSessionsAsync();
            foreach (var session in sessions)
            {
                _sessions.TryAdd(session.SessionId, session);
            }

            _logger.LogInformation(
                "Session cache initialized from SQLite: {Count} active sessions loaded",
                sessions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sessions from SQLite cache; starting with empty cache");
        }
    }

    public CachedSession? FindSession(string userName, string? sourceIp)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var session in _sessions.Values)
        {
            if (session.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)
                && (sourceIp == null || session.SourceIp == sourceIp)
                && session.ExpiresAt > now
                && !session.Revoked)
            {
                return session;
            }
        }

        return null;
    }

    public void AddOrUpdateSession(CachedSession session)
    {
        _sessions.AddOrUpdate(session.SessionId, session, (_, _) => session);
        _logger.LogDebug("Cached session {SessionId} for {UserName}", session.SessionId, session.UserName);

        // Fire-and-forget persistence to avoid blocking the hot path
        _ = PersistSaveSessionAsync(session);
    }

    public bool RevokeSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Revoked = true;
            _logger.LogInformation("Revoked cached session {SessionId}", sessionId);

            // Fire-and-forget persistence — save the updated revoked state
            _ = PersistSaveSessionAsync(session);
            return true;
        }
        return false;
    }

    public void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _sessions.Where(kv => kv.Value.ExpiresAt < now || kv.Value.Revoked).Select(kv => kv.Key).ToList();

        foreach (var key in expired)
            _sessions.TryRemove(key, out _);

        if (expired.Count > 0)
            _logger.LogDebug("Cleaned up {Count} expired/revoked sessions from cache", expired.Count);

        // Fire-and-forget cleanup in SQLite as well
        _ = PersistCleanupExpiredAsync();
    }

    public int ActiveSessionCount => _sessions.Count(kv => kv.Value.ExpiresAt > DateTimeOffset.UtcNow && !kv.Value.Revoked);

    public IEnumerable<CachedSession> GetAllSessions() => _sessions.Values;

    // ─── Private persistence helpers (fire-and-forget) ───────────────────

    private async Task PersistSaveSessionAsync(CachedSession session)
    {
        try
        {
            await _store.SaveSessionAsync(session);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist session {SessionId} to SQLite", session.SessionId);
        }
    }

    private async Task PersistCleanupExpiredAsync()
    {
        try
        {
            await _store.CleanupExpiredSessionsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up expired sessions in SQLite");
        }
    }
}
