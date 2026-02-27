using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Services;

public class MfaChallengeOrchestrator : IMfaChallengeOrchestrator
{
    private readonly MfaSrvDbContext _db;
    private readonly IEnumerable<IMfaProvider> _providers;
    private readonly ILogger<MfaChallengeOrchestrator> _logger;
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromMinutes(5);

    public MfaChallengeOrchestrator(
        MfaSrvDbContext db,
        IEnumerable<IMfaProvider> providers,
        ILogger<MfaChallengeOrchestrator> logger)
    {
        _db = db;
        _providers = providers;
        _logger = logger;
    }

    public async Task<ChallengeResult> IssueChallengeAsync(string userId, MfaMethod method, ChallengeContext context, CancellationToken ct = default)
    {
        var provider = _providers.FirstOrDefault(p => p.MethodId == method.ToString().ToUpperInvariant());
        if (provider == null)
        {
            return new ChallengeResult
            {
                Success = false,
                Error = $"MFA provider not found for method: {method}",
                Status = ChallengeStatus.Failed
            };
        }

        var enrollment = await _db.MfaEnrollments
            .FirstOrDefaultAsync(e => e.UserId == userId && e.Method == method && e.Status == EnrollmentStatus.Active, ct);

        if (enrollment == null)
        {
            return new ChallengeResult
            {
                Success = false,
                Error = $"No active enrollment found for user {userId} with method {method}",
                Status = ChallengeStatus.Failed
            };
        }

        var challengeCtx = context with
        {
            EnrollmentId = enrollment.Id,
            EncryptedSecret = enrollment.EncryptedSecret,
            SecretNonce = enrollment.SecretNonce
        };

        var result = await provider.IssueChallengeAsync(challengeCtx, ct);

        if (result.Success)
        {
            var challenge = new MfaChallenge
            {
                Id = result.ChallengeId ?? Guid.NewGuid().ToString(),
                UserId = userId,
                EnrollmentId = enrollment.Id,
                Method = method,
                Status = ChallengeStatus.Issued,
                SourceIp = context.SourceIp,
                TargetResource = context.TargetResource,
                ExpiresAt = DateTimeOffset.UtcNow.Add(ChallengeTimeout)
            };

            _db.MfaChallenges.Add(challenge);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Issued {Method} challenge {ChallengeId} for user {UserId}",
                method, challenge.Id, userId);
        }

        return result;
    }

    public async Task<VerificationResult> VerifyChallengeAsync(string challengeId, string response, CancellationToken ct = default)
    {
        var challenge = await _db.MfaChallenges.FindAsync(new object[] { challengeId }, ct);
        if (challenge == null)
        {
            return new VerificationResult { Success = false, Error = "Challenge not found" };
        }

        if (challenge.Status != ChallengeStatus.Issued)
        {
            return new VerificationResult { Success = false, Error = $"Challenge is in state: {challenge.Status}" };
        }

        if (challenge.ExpiresAt < DateTimeOffset.UtcNow)
        {
            challenge.Status = ChallengeStatus.Expired;
            await _db.SaveChangesAsync(ct);
            return new VerificationResult { Success = false, Error = "Challenge expired" };
        }

        if (challenge.AttemptCount >= challenge.MaxAttempts)
        {
            challenge.Status = ChallengeStatus.Failed;
            await _db.SaveChangesAsync(ct);
            return new VerificationResult { Success = false, Error = "Max attempts exceeded", ShouldLockout = true };
        }

        challenge.AttemptCount++;

        var enrollment = await _db.MfaEnrollments.FindAsync(new object[] { challenge.EnrollmentId }, ct);
        if (enrollment == null)
        {
            return new VerificationResult { Success = false, Error = "Enrollment not found" };
        }

        var provider = _providers.FirstOrDefault(p => p.MethodId == challenge.Method.ToString().ToUpperInvariant());
        if (provider == null)
        {
            return new VerificationResult { Success = false, Error = "Provider not found" };
        }

        var verificationCtx = new VerificationContext
        {
            ChallengeId = challengeId,
            UserId = challenge.UserId,
            EncryptedSecret = enrollment.EncryptedSecret,
            SecretNonce = enrollment.SecretNonce
        };

        var result = await provider.VerifyAsync(verificationCtx, response, ct);

        if (result.Success)
        {
            challenge.Status = ChallengeStatus.Approved;
            challenge.RespondedAt = DateTimeOffset.UtcNow;
            enrollment.LastUsedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Challenge {ChallengeId} verified successfully for user {UserId}", challengeId, challenge.UserId);
        }
        else
        {
            if (challenge.AttemptCount >= challenge.MaxAttempts)
                challenge.Status = ChallengeStatus.Failed;
            _logger.LogWarning("Challenge {ChallengeId} verification failed (attempt {Attempt}/{Max})",
                challengeId, challenge.AttemptCount, challenge.MaxAttempts);
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<AsyncVerificationStatus> CheckChallengeStatusAsync(string challengeId, CancellationToken ct = default)
    {
        var challenge = await _db.MfaChallenges.FindAsync(new object[] { challengeId }, ct);
        if (challenge == null)
        {
            return new AsyncVerificationStatus { Status = ChallengeStatus.Failed, Error = "Challenge not found" };
        }

        if (challenge.ExpiresAt < DateTimeOffset.UtcNow && challenge.Status == ChallengeStatus.Issued)
        {
            challenge.Status = ChallengeStatus.Expired;
            await _db.SaveChangesAsync(ct);
        }

        // For async providers (push), also check with the provider
        var provider = _providers.FirstOrDefault(p => p.MethodId == challenge.Method.ToString().ToUpperInvariant());
        if (provider != null && provider.SupportsAsynchronousVerification && challenge.Status == ChallengeStatus.Issued)
        {
            var providerStatus = await provider.CheckAsyncStatusAsync(challengeId, ct);
            if (providerStatus.Status != ChallengeStatus.Issued)
            {
                challenge.Status = providerStatus.Status;
                challenge.RespondedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            return providerStatus;
        }

        return new AsyncVerificationStatus { Status = challenge.Status };
    }
}
