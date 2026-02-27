using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;

namespace MfaSrv.Core.Interfaces;

public interface IMfaChallengeOrchestrator
{
    Task<ChallengeResult> IssueChallengeAsync(string userId, MfaMethod method, ChallengeContext context, CancellationToken ct = default);
    Task<VerificationResult> VerifyChallengeAsync(string challengeId, string response, CancellationToken ct = default);
    Task<AsyncVerificationStatus> CheckChallengeStatusAsync(string challengeId, CancellationToken ct = default);
}
