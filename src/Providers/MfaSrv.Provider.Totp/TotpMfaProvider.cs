using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Cryptography;

namespace MfaSrv.Provider.Totp;

public class TotpMfaProvider : IMfaProvider
{
    public string MethodId => "TOTP";
    public string DisplayName => "Time-based One-Time Password";
    public bool SupportsSynchronousVerification => true;
    public bool SupportsAsynchronousVerification => false;
    public bool RequiresEndpointAgent => false;

    private const string DefaultIssuer = "MfaSrv";

    public Task<EnrollmentInitResult> BeginEnrollmentAsync(EnrollmentContext ctx, CancellationToken ct = default)
    {
        var secret = TotpGenerator.GenerateSecret();
        var issuer = ctx.Issuer ?? DefaultIssuer;
        var provisioningUri = TotpGenerator.GenerateProvisioningUri(secret, ctx.UserName, issuer);

        return Task.FromResult(new EnrollmentInitResult
        {
            Success = true,
            Secret = secret,
            ProvisioningUri = provisioningUri,
            QrCodeDataUri = null // QR code generation can be handled by the client
        });
    }

    public Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(EnrollmentContext ctx, string response, CancellationToken ct = default)
    {
        // Verification of the TOTP code is handled by the EnrollmentsController
        // using the secret directly. This method is for providers that need
        // additional server-side completion logic.
        return Task.FromResult(new EnrollmentCompleteResult
        {
            Success = true
        });
    }

    public Task<ChallengeResult> IssueChallengeAsync(ChallengeContext ctx, CancellationToken ct = default)
    {
        // TOTP is synchronous - the user already has their authenticator app.
        // We just create a challenge record and wait for the code.
        var challengeId = Guid.NewGuid().ToString();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        return Task.FromResult(new ChallengeResult
        {
            Success = true,
            ChallengeId = challengeId,
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiresAt,
            UserPrompt = "Enter the 6-digit code from your authenticator app"
        });
    }

    public Task<VerificationResult> VerifyAsync(VerificationContext ctx, string response, CancellationToken ct = default)
    {
        if (ctx.EncryptedSecret == null || ctx.SecretNonce == null)
        {
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "No secret available for verification"
            });
        }

        // Note: The actual decryption happens in the caller (MfaChallengeOrchestrator or Controller)
        // because they have access to the encryption key. This method receives the encrypted secret
        // and would need the key injected. For now, we handle TOTP verification at the orchestrator level.
        //
        // In a real implementation, you'd inject the encryption key or have a secret resolution service.

        return Task.FromResult(new VerificationResult
        {
            Success = false,
            Error = "TOTP verification should be performed with decrypted secret"
        });
    }

    public Task<AsyncVerificationStatus> CheckAsyncStatusAsync(string challengeId, CancellationToken ct = default)
    {
        // TOTP is synchronous, so this always returns Issued (waiting for user input)
        return Task.FromResult(new AsyncVerificationStatus
        {
            Status = ChallengeStatus.Issued
        });
    }
}
