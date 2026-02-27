using MfaSrv.Core.ValueObjects;

namespace MfaSrv.Core.Interfaces;

public interface IMfaProvider
{
    string MethodId { get; }
    string DisplayName { get; }
    bool SupportsSynchronousVerification { get; }
    bool SupportsAsynchronousVerification { get; }
    bool RequiresEndpointAgent { get; }

    Task<EnrollmentInitResult> BeginEnrollmentAsync(EnrollmentContext ctx, CancellationToken ct = default);
    Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(EnrollmentContext ctx, string response, CancellationToken ct = default);
    Task<ChallengeResult> IssueChallengeAsync(ChallengeContext ctx, CancellationToken ct = default);
    Task<VerificationResult> VerifyAsync(VerificationContext ctx, string response, CancellationToken ct = default);
    Task<AsyncVerificationStatus> CheckAsyncStatusAsync(string challengeId, CancellationToken ct = default);
}
