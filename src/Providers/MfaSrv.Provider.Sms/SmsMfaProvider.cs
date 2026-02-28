using System.Security.Cryptography;
using System.Text;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.Sms;

public class SmsMfaProvider : IMfaProvider
{
    private readonly SmsSettings _settings;
    private readonly SmsGatewayClient _smsClient;
    private readonly IChallengeStore _store;
    private readonly ILogger<SmsMfaProvider> _logger;

    private const string ChallengePrefix = "sms:otp:";

    public SmsMfaProvider(
        IOptions<SmsSettings> settings,
        SmsGatewayClient smsClient,
        IChallengeStore store,
        ILogger<SmsMfaProvider> logger)
    {
        _settings = settings.Value;
        _smsClient = smsClient;
        _store = store;
        _logger = logger;
    }

    public string MethodId => "SMS";
    public string DisplayName => "SMS One-Time Password";
    public bool SupportsSynchronousVerification => true;
    public bool SupportsAsynchronousVerification => false;
    public bool RequiresEndpointAgent => false;

    public Task<EnrollmentInitResult> BeginEnrollmentAsync(EnrollmentContext ctx, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ctx.UserPhone))
        {
            return Task.FromResult(new EnrollmentInitResult
            {
                Success = false,
                Error = "Phone number is required for SMS enrollment"
            });
        }

        _logger.LogInformation("SMS enrollment initiated for user {UserId} with phone {Phone}",
            ctx.UserId, MaskPhoneNumber(ctx.UserPhone));

        return Task.FromResult(new EnrollmentInitResult
        {
            Success = true,
            Metadata = new Dictionary<string, string>
            {
                ["phoneNumber"] = ctx.UserPhone
            }
        });
    }

    public async Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(
        EnrollmentContext ctx, string response, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ctx.UserPhone))
        {
            return new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Phone number is required to complete SMS enrollment"
            };
        }

        // Send a test OTP to verify the phone number is reachable
        var testCode = GenerateOtpCode();
        var message = _settings.MessageTemplate.Replace("{code}", testCode);

        var sent = await _smsClient.SendSmsAsync(ctx.UserPhone, message, ct);
        if (!sent)
        {
            return new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Failed to send test SMS to the provided phone number"
            };
        }

        // Store the test challenge so the enrollment verification code can be validated
        var challengeId = $"enroll-{ctx.UserId}-{Guid.NewGuid():N}";
        var expiry = DateTimeOffset.UtcNow.AddMinutes(_settings.CodeExpiryMinutes);

        await _store.SetAsync(ChallengePrefix + challengeId, new PendingChallenge(testCode, expiry, 0, ctx.UserPhone), TimeSpan.FromMinutes(_settings.CodeExpiryMinutes), ct);

        _logger.LogInformation("SMS enrollment test OTP sent to {Phone} for user {UserId}",
            MaskPhoneNumber(ctx.UserPhone), ctx.UserId);

        // For enrollment, we verify the response code matches the test code
        if (!ConstantTimeEquals(response, testCode))
        {
            await _store.RemoveAsync(ChallengePrefix + challengeId, ct);
            return new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Verification code does not match"
            };
        }

        await _store.RemoveAsync(ChallengePrefix + challengeId, ct);

        return new EnrollmentCompleteResult
        {
            Success = true
        };
    }

    public async Task<ChallengeResult> IssueChallengeAsync(ChallengeContext ctx, CancellationToken ct = default)
    {
        // Decrypt the stored phone number from the enrollment secret
        string phoneNumber;
        try
        {
            if (ctx.EncryptedSecret == null || ctx.SecretNonce == null)
            {
                return new ChallengeResult
                {
                    Success = false,
                    Error = "No enrolled phone number found",
                    Status = ChallengeStatus.Failed
                };
            }

            // The encrypted secret contains the phone number as UTF-8 bytes
            var secretBytes = MfaSrv.Cryptography.AesGcmEncryption.Decrypt(
                ctx.EncryptedSecret, ctx.SecretNonce,
                GetEncryptionKey(ctx));

            phoneNumber = Encoding.UTF8.GetString(secretBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt phone number for user {UserId}", ctx.UserId);
            return new ChallengeResult
            {
                Success = false,
                Error = "Failed to retrieve enrolled phone number",
                Status = ChallengeStatus.Failed
            };
        }

        var code = GenerateOtpCode();
        var challengeId = Guid.NewGuid().ToString();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(_settings.CodeExpiryMinutes);

        var message = _settings.MessageTemplate.Replace("{code}", code);
        var sent = await _smsClient.SendSmsAsync(phoneNumber, message, ct);

        if (!sent)
        {
            _logger.LogError("Failed to send SMS challenge to user {UserId}", ctx.UserId);
            return new ChallengeResult
            {
                Success = false,
                Error = "Failed to send SMS verification code",
                Status = ChallengeStatus.Failed
            };
        }

        await _store.SetAsync(ChallengePrefix + challengeId, new PendingChallenge(code, expiry, 0, phoneNumber), TimeSpan.FromMinutes(_settings.CodeExpiryMinutes + 1), ct);

        _logger.LogInformation("SMS challenge {ChallengeId} issued for user {UserId} to {Phone}",
            challengeId, ctx.UserId, MaskPhoneNumber(phoneNumber));

        return new ChallengeResult
        {
            Success = true,
            ChallengeId = challengeId,
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiry,
            UserPrompt = $"Enter the {_settings.CodeLength}-digit code sent to {MaskPhoneNumber(phoneNumber)}"
        };
    }

    public async Task<VerificationResult> VerifyAsync(VerificationContext ctx, string response, CancellationToken ct = default)
    {
        var challenge = await _store.GetAsync<PendingChallenge>(ChallengePrefix + ctx.ChallengeId, ct);
        if (challenge == null)
        {
            _logger.LogWarning("SMS verification attempted for unknown challenge {ChallengeId}", ctx.ChallengeId);
            return new VerificationResult
            {
                Success = false,
                Error = "Challenge not found or already consumed"
            };
        }

        // Check expiry
        if (DateTimeOffset.UtcNow > challenge.Expiry)
        {
            await _store.RemoveAsync(ChallengePrefix + ctx.ChallengeId, ct);
            _logger.LogWarning("SMS challenge {ChallengeId} expired for user {UserId}", ctx.ChallengeId, ctx.UserId);
            return new VerificationResult
            {
                Success = false,
                Error = "Verification code has expired"
            };
        }

        // Check max attempts
        if (challenge.Attempts >= _settings.MaxAttempts)
        {
            await _store.RemoveAsync(ChallengePrefix + ctx.ChallengeId, ct);
            _logger.LogWarning("SMS challenge {ChallengeId} exceeded max attempts for user {UserId}",
                ctx.ChallengeId, ctx.UserId);
            return new VerificationResult
            {
                Success = false,
                Error = "Maximum verification attempts exceeded",
                ShouldLockout = true
            };
        }

        // Increment attempts
        var updated = challenge with { Attempts = challenge.Attempts + 1 };
        await _store.SetAsync(ChallengePrefix + ctx.ChallengeId, updated, TimeSpan.FromMinutes(_settings.CodeExpiryMinutes + 1), ct);

        // Constant-time comparison
        if (!ConstantTimeEquals(response, challenge.Code))
        {
            var remainingAttempts = _settings.MaxAttempts - (challenge.Attempts + 1);
            _logger.LogWarning("SMS verification failed for challenge {ChallengeId}, {Remaining} attempts remaining",
                ctx.ChallengeId, remainingAttempts);
            return new VerificationResult
            {
                Success = false,
                Error = $"Invalid verification code. {remainingAttempts} attempt(s) remaining."
            };
        }

        // Success - remove the challenge
        await _store.RemoveAsync(ChallengePrefix + ctx.ChallengeId, ct);
        _logger.LogInformation("SMS verification succeeded for challenge {ChallengeId}, user {UserId}",
            ctx.ChallengeId, ctx.UserId);

        return new VerificationResult
        {
            Success = true
        };
    }

    public Task<AsyncVerificationStatus> CheckAsyncStatusAsync(string challengeId, CancellationToken ct = default)
    {
        // SMS is synchronous only - async status check is not supported
        return Task.FromResult(new AsyncVerificationStatus
        {
            Status = ChallengeStatus.Failed,
            Error = "SMS provider does not support asynchronous verification"
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
    /// Masks a phone number for logging purposes (e.g., +1***4567).
    /// </summary>
    private static string MaskPhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrEmpty(phoneNumber) || phoneNumber.Length < 4)
            return "***";

        var visibleEnd = phoneNumber[^4..];
        var visibleStart = phoneNumber.Length > 6 ? phoneNumber[..2] : string.Empty;
        return $"{visibleStart}***{visibleEnd}";
    }

    /// <summary>
    /// Retrieves the encryption key from the challenge context.
    /// In production, this would be injected or resolved from a key management service.
    /// </summary>
    private static byte[] GetEncryptionKey(ChallengeContext ctx)
    {
        return ctx.EncryptionKey ?? throw new InvalidOperationException(
            "SMS provider requires an encryption key. Configure MfaSrv:EncryptionKey in appsettings.");
    }

    /// <summary>
    /// Represents a pending SMS challenge awaiting verification.
    /// </summary>
    internal sealed record PendingChallenge(string Code, DateTimeOffset Expiry, int Attempts, string PhoneNumber);
}
