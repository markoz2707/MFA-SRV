using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Services;

public class SessionManager : ISessionManager
{
    private readonly MfaSrvDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly ILogger<SessionManager> _logger;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(8);

    public SessionManager(MfaSrvDbContext db, ITokenService tokenService, ILogger<SessionManager> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<MfaSession> CreateSessionAsync(string userId, string sourceIp, string targetResource, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString();
        var expiry = DateTimeOffset.UtcNow.Add(ttl ?? DefaultTtl);
        var tokenBytes = _tokenService.GenerateSessionToken(sessionId, userId, expiry);
        var tokenHash = SHA256.HashData(tokenBytes);

        var session = new MfaSession
        {
            Id = sessionId,
            UserId = userId,
            TokenHash = tokenHash,
            SourceIp = sourceIp,
            TargetResource = targetResource,
            Status = SessionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiry
        };

        _db.MfaSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created MFA session {SessionId} for user {UserId}, expires at {Expiry}",
            sessionId, userId, expiry);

        return session;
    }

    public async Task<MfaSession?> ValidateSessionAsync(string sessionToken, CancellationToken ct = default)
    {
        byte[] tokenBytes;
        try
        {
            tokenBytes = Convert.FromBase64String(sessionToken);
        }
        catch
        {
            return null;
        }

        var payload = _tokenService.ValidateSessionToken(tokenBytes);
        if (payload == null) return null;

        var session = await _db.MfaSessions
            .FirstOrDefaultAsync(s => s.Id == payload.SessionId && s.Status == SessionStatus.Active, ct);

        if (session == null || session.ExpiresAt < DateTimeOffset.UtcNow)
            return null;

        var tokenHash = SHA256.HashData(tokenBytes);
        if (!CryptographicOperations.FixedTimeEquals(session.TokenHash, tokenHash))
            return null;

        return session;
    }

    public async Task<MfaSession?> FindActiveSessionAsync(string userId, string sourceIp, CancellationToken ct = default)
    {
        return await _db.MfaSessions
            .Where(s => s.UserId == userId
                && s.SourceIp == sourceIp
                && s.Status == SessionStatus.Active
                && s.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task RevokeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await _db.MfaSessions.FindAsync(new object[] { sessionId }, ct);
        if (session != null)
        {
            session.Status = SessionStatus.Revoked;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Revoked session {SessionId}", sessionId);
        }
    }

    public async Task CleanupExpiredSessionsAsync(CancellationToken ct = default)
    {
        var expired = await _db.MfaSessions
            .Where(s => s.Status == SessionStatus.Active && s.ExpiresAt < DateTimeOffset.UtcNow)
            .ToListAsync(ct);

        foreach (var session in expired)
            session.Status = SessionStatus.Expired;

        if (expired.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Cleaned up {Count} expired sessions", expired.Count);
        }
    }
}
