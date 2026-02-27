using MfaSrv.Core.Entities;

namespace MfaSrv.Core.Interfaces;

public interface ISessionManager
{
    Task<MfaSession> CreateSessionAsync(string userId, string sourceIp, string targetResource, TimeSpan? ttl = null, CancellationToken ct = default);
    Task<MfaSession?> ValidateSessionAsync(string sessionToken, CancellationToken ct = default);
    Task<MfaSession?> FindActiveSessionAsync(string userId, string sourceIp, CancellationToken ct = default);
    Task RevokeSessionAsync(string sessionId, CancellationToken ct = default);
    Task CleanupExpiredSessionsAsync(CancellationToken ct = default);
}
