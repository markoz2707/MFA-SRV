using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MfaSrv.EndpointAgent.Services;

public class CachedEndpointSession
{
    public required string SessionId { get; init; }
    public required string UserName { get; init; }
    public required string Domain { get; init; }
    public required string Workstation { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string VerifiedMethod { get; init; }
    public bool Revoked { get; set; }
}

public class EndpointSessionCache
{
    private readonly ConcurrentDictionary<string, CachedEndpointSession> _sessions = new();
    private readonly ILogger<EndpointSessionCache> _logger;

    public EndpointSessionCache(ILogger<EndpointSessionCache> logger)
    {
        _logger = logger;
    }

    public CachedEndpointSession? FindSession(string userName, string? workstation = null)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var session in _sessions.Values)
        {
            if (session.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)
                && (workstation == null || session.Workstation.Equals(workstation, StringComparison.OrdinalIgnoreCase))
                && session.ExpiresAt > now
                && !session.Revoked)
            {
                return session;
            }
        }

        return null;
    }

    public void AddOrUpdateSession(CachedEndpointSession session)
    {
        _sessions.AddOrUpdate(session.SessionId, session, (_, _) => session);
        _logger.LogDebug("Cached session {SessionId} for {UserName}", session.SessionId, session.UserName);
    }

    public bool RevokeSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Revoked = true;
            _logger.LogInformation("Revoked cached session {SessionId}", sessionId);
            return true;
        }
        return false;
    }

    public void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _sessions
            .Where(kv => kv.Value.ExpiresAt < now || kv.Value.Revoked)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
            _sessions.TryRemove(key, out _);

        if (expired.Count > 0)
            _logger.LogDebug("Cleaned up {Count} expired/revoked sessions from cache", expired.Count);
    }

    public int ActiveSessionCount => _sessions.Count(kv => kv.Value.ExpiresAt > DateTimeOffset.UtcNow && !kv.Value.Revoked);

    public IEnumerable<CachedEndpointSession> GetAllSessions() => _sessions.Values;
}
