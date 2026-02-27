using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.Email;

public class EmailMfaProvider : IMfaProvider
{
    private readonly EmailSettings _settings;
    private readonly EmailSender _emailSender;
    private readonly ILogger<EmailMfaProvider> _logger;

    private static readonly ConcurrentDictionary<string, PendingChallenge> PendingChallenges = new();

    public EmailMfaProvider(
        IOptions<EmailSettings> settings,
        EmailSender emailSender,
        ILogger<EmailMfaProvider> logger)
    {
        _settings = settings.Value;
        _emailSender = emailSender;
        _logger = logger;
    }

    public string MethodId => "EMAIL";
    public string DisplayName => "Email One-Time Password";
    public bool SupportsSynchronousVerification => true;
    public bool SupportsAsynchronousVerification => false;
    public bool RequiresEndpointAgent => false;

    public Task<EnrollmentInitResult> BeginEnrollmentAsync(EnrollmentContext ctx, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ctx.UserEmail))
        {
            return Task.FromResult(new EnrollmentInitResult
            {
                Success = false,
                Error = "Email address is required for email enrollment"
            });
        }

        _logger.LogInformation("Email enrollment initiated for user {UserId} with email {Email}",
            ctx.UserId, MaskEmailAddress(ctx.UserEmail));

        return Task.FromResult(new EnrollmentInitResult
        {
            Success = true,
            Metadata = new Dictionary<string, string>
            {
                ["emailAddress"] = ctx.UserEmail
            }
        });
    }

    public async Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(
        EnrollmentContext ctx, string response, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ctx.UserEmail))
        {
            return new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Email address is required to complete email enrollment"
            };
        }

        // Send a test OTP to verify the email address is reachable
        var testCode = GenerateOtpCode();
        var subject = _settings.SubjectTemplate;
        var body = _settings.BodyTemplate.Replace("{code}", testCode);

        var sent = await _emailSender.SendEmailAsync(ctx.UserEmail, subject, body, ct);
        if (!sent)
        {
            return new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Failed to send test email to the provided address"
            };
        }

        // Store the test challenge so the enrollment verification code can be validated
        var challengeId = $"enroll-{ctx.UserId}-{Guid.NewGuid():N}";
        var expiry = DateTimeOffset.UtcNow.AddMinutes(_settings.CodeExpiryMinutes);

        PendingChallenges[challengeId] = new PendingChallenge(testCode, expiry, 0, ctx.UserEmail);

        _logger.LogInformation("Email enrollment test OTP sent to {Email} for user {UserId}",
            MaskEmailAddress(ctx.UserEmail), ctx.UserId);

        // Verify the response code matches the test code
        if (!ConstantTimeEquals(response, testCode))
        {
            PendingChallenges.TryRemove(challengeId, out _);
            return new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Verification code does not match"
            };
        }

        PendingChallenges.TryRemove(challengeId, out _);

        return new EnrollmentCompleteResult
        {
            Success = true
        };
    }

    public async Task<ChallengeResult> IssueChallengeAsync(ChallengeContext ctx, CancellationToken ct = default)
    {
        // Decrypt the stored email address from the enrollment secret
        string emailAddress;
        try
        {
            if (ctx.EncryptedSecret == null || ctx.SecretNonce == null)
            {
                return new ChallengeResult
                {
                    Success = false,
                    Error = "No enrolled email address found",
                    Status = ChallengeStatus.Failed
                };
            }

            // The encrypted secret contains the email address as UTF-8 bytes
            var secretBytes = MfaSrv.Cryptography.AesGcmEncryption.Decrypt(
                ctx.EncryptedSecret, ctx.SecretNonce,
                GetEncryptionKey(ctx));

            emailAddress = Encoding.UTF8.GetString(secretBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt email address for user {UserId}", ctx.UserId);
            return new ChallengeResult
            {
                Success = false,
                Error = "Failed to retrieve enrolled email address",
                Status = ChallengeStatus.Failed
            };
        }

        var code = GenerateOtpCode();
        var challengeId = Guid.NewGuid().ToString();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(_settings.CodeExpiryMinutes);

        var subject = _settings.SubjectTemplate;
        var body = _settings.BodyTemplate.Replace("{code}", code);

        var sent = await _emailSender.SendEmailAsync(emailAddress, subject, body, ct);

        if (!sent)
        {
            _logger.LogError("Failed to send email challenge to user {UserId}", ctx.UserId);
            return new ChallengeResult
            {
                Success = false,
                Error = "Failed to send email verification code",
                Status = ChallengeStatus.Failed
            };
        }

        PendingChallenges[challengeId] = new PendingChallenge(code, expiry, 0, emailAddress);

        _logger.LogInformation("Email challenge {ChallengeId} issued for user {UserId} to {Email}",
            challengeId, ctx.UserId, MaskEmailAddress(emailAddress));

        return new ChallengeResult
        {
            Success = true,
            ChallengeId = challengeId,
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiry,
            UserPrompt = $"Enter the {_settings.CodeLength}-digit code sent to {MaskEmailAddress(emailAddress)}"
        };
    }

    public Task<VerificationResult> VerifyAsync(VerificationContext ctx, string response, CancellationToken ct = default)
    {
        if (!PendingChallenges.TryGetValue(ctx.ChallengeId, out var challenge))
        {
            _logger.LogWarning("Email verification attempted for unknown challenge {ChallengeId}", ctx.ChallengeId);
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Challenge not found or already consumed"
            });
        }

        // Check expiry
        if (DateTimeOffset.UtcNow > challenge.Expiry)
        {
            PendingChallenges.TryRemove(ctx.ChallengeId, out _);
            _logger.LogWarning("Email challenge {ChallengeId} expired for user {UserId}", ctx.ChallengeId, ctx.UserId);
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Verification code has expired"
            });
        }

        // Check max attempts
        if (challenge.Attempts >= _settings.MaxAttempts)
        {
            PendingChallenges.TryRemove(ctx.ChallengeId, out _);
            _logger.LogWarning("Email challenge {ChallengeId} exceeded max attempts for user {UserId}",
                ctx.ChallengeId, ctx.UserId);
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Maximum verification attempts exceeded",
                ShouldLockout = true
            });
        }

        // Increment attempts atomically
        PendingChallenges[ctx.ChallengeId] = challenge with { Attempts = challenge.Attempts + 1 };

        // Constant-time comparison
        if (!ConstantTimeEquals(response, challenge.Code))
        {
            var remainingAttempts = _settings.MaxAttempts - (challenge.Attempts + 1);
            _logger.LogWarning("Email verification failed for challenge {ChallengeId}, {Remaining} attempts remaining",
                ctx.ChallengeId, remainingAttempts);
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = $"Invalid verification code. {remainingAttempts} attempt(s) remaining."
            });
        }

        // Success - remove the challenge
        PendingChallenges.TryRemove(ctx.ChallengeId, out _);
        _logger.LogInformation("Email verification succeeded for challenge {ChallengeId}, user {UserId}",
            ctx.ChallengeId, ctx.UserId);

        return Task.FromResult(new VerificationResult
        {
            Success = true
        });
    }

    public Task<AsyncVerificationStatus> CheckAsyncStatusAsync(string challengeId, CancellationToken ct = default)
    {
        // Email is synchronous only - async status check is not supported
        return Task.FromResult(new AsyncVerificationStatus
        {
            Status = ChallengeStatus.Failed,
            Error = "Email provider does not support asynchronous verification"
        });
    }

    /// <summary>
    /// Generates a cryptographically random numeric OTP code.
    /// </summary>
    private string GenerateOtpCode()
    {
        var codeLength = _settings.CodeLength;
        var maxValue = (int)Math.Pow(10, codeLength);
        var code = RandomNumberGenerator.GetInt32(0, maxValue);
        return code.ToString().PadLeft(codeLength, '0');
    }

    /// <summary>
    /// Performs a constant-time comparison of two strings to prevent timing attacks.
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a == null || b == null)
            return false;

        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    /// <summary>
    /// Masks an email address for logging purposes (e.g., u***@example.com).
    /// </summary>
    private static string MaskEmailAddress(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "***";

        var atIndex = email.IndexOf('@');
        if (atIndex <= 0)
            return "***";

        var localPart = email[..atIndex];
        var domain = email[atIndex..];
        var visibleStart = localPart.Length > 1 ? localPart[..1] : localPart;
        return $"{visibleStart}***{domain}";
    }

    /// <summary>
    /// Retrieves the encryption key from the challenge context.
    /// In production, this would be injected or resolved from a key management service.
    /// </summary>
    private static byte[] GetEncryptionKey(ChallengeContext ctx)
    {
        // The encryption key should be provided via a key management service.
        // For now, we use the EncryptedSecret/SecretNonce fields directly,
        // meaning the caller must pass the already-decrypted secret in EncryptedSecret
        // when the system handles key resolution externally.
        throw new InvalidOperationException(
            "Email provider requires an encryption key management service. " +
            "Override IssueChallengeAsync in a derived class or configure key resolution.");
    }

    /// <summary>
    /// Represents a pending email challenge awaiting verification.
    /// </summary>
    private sealed record PendingChallenge(string Code, DateTimeOffset Expiry, int Attempts, string EmailAddress);
}
