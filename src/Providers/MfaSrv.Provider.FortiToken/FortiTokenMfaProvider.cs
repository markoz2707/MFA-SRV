using System.Security.Cryptography;
using System.Text;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.FortiToken;

/// <summary>
/// MFA provider that integrates with FortiAuthenticator REST API for
/// hardware token (FortiToken 200) and software token (FortiToken Mobile) authentication.
/// Supports both synchronous OTP verification and asynchronous push notification flows.
/// </summary>
public class FortiTokenMfaProvider : IMfaProvider
{
    private readonly FortiTokenSettings _settings;
    private readonly FortiAuthClient _fortiClient;
    private readonly IChallengeStore _store;
    private readonly ILogger<FortiTokenMfaProvider> _logger;

    private const string ChallengePrefix = "forti:challenge:";

    public FortiTokenMfaProvider(
        FortiAuthClient fortiClient,
        IOptions<FortiTokenSettings> settings,
        IChallengeStore store,
        ILogger<FortiTokenMfaProvider> logger)
    {
        _fortiClient = fortiClient;
        _settings = settings.Value;
        _store = store;
        _logger = logger;
    }

    // ── IMfaProvider metadata ────────────────────────────────────────────

    public string MethodId => "FORTITOKEN";
    public string DisplayName => "FortiToken (FortiAuthenticator)";
    public bool SupportsSynchronousVerification => true;   // OTP mode
    public bool SupportsAsynchronousVerification => true;   // Push mode
    public bool RequiresEndpointAgent => false;

    // ── Enrollment ──────────────────────────────────────────────────────

    /// <summary>
    /// Begins enrollment by assigning a FortiToken to the user via the
    /// FortiAuthenticator API. The token serial number must be provided
    /// in the enrollment context metadata (passed via <see cref="EnrollmentContext.UserPhone"/>
    /// as a transport field, or the caller populates Metadata["serialNumber"]).
    ///
    /// For FortiToken, enrollment means associating a physical or mobile token
    /// with the user's account on FortiAuthenticator.
    /// </summary>
    public async Task<EnrollmentInitResult> BeginEnrollmentAsync(
        EnrollmentContext ctx, CancellationToken ct = default)
    {
        // The serial number can be passed via UserPhone (repurposed transport field)
        // or, if a more structured approach is used, extracted from context.
        // For FortiToken enrollment, the admin provides the token serial number.
        var serialNumber = ctx.UserPhone?.Trim();

        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            _logger.LogWarning(
                "FortiToken enrollment for user {UserId} missing token serial number", ctx.UserId);
            return new EnrollmentInitResult
            {
                Success = false,
                Error = "Token serial number is required for FortiToken enrollment. " +
                        "Provide the serial number of the hardware or mobile token to assign."
            };
        }

        _logger.LogInformation(
            "FortiToken enrollment started for user {UserId} ({UserName}) with serial {Serial}",
            ctx.UserId, ctx.UserName, serialNumber);

        // Assign the token to the user via FortiAuthenticator API
        var assignResult = await _fortiClient.AssignTokenAsync(ctx.UserName, serialNumber, ct);

        if (!assignResult.Success)
        {
            _logger.LogError(
                "FortiToken assignment failed for user {UserId}, serial {Serial}: {Error}",
                ctx.UserId, serialNumber, assignResult.Error);
            return new EnrollmentInitResult
            {
                Success = false,
                Error = $"Failed to assign token: {assignResult.Error}"
            };
        }

        // Store the serial number and username as enrollment metadata so that
        // CompleteEnrollmentAsync and IssueChallengeAsync can reference them.
        var secretData = Encoding.UTF8.GetBytes(
            $"{ctx.UserName}:{serialNumber}");

        return new EnrollmentInitResult
        {
            Success = true,
            Secret = secretData,
            Metadata = new Dictionary<string, string>
            {
                ["serialNumber"] = serialNumber,
                ["username"] = ctx.UserName,
                ["instruction"] = $"FortiToken {serialNumber} has been assigned. " +
                                  "Enter a one-time password from the token to complete enrollment."
            }
        };
    }

    /// <summary>
    /// Completes enrollment by verifying that the user can produce a valid OTP
    /// from the assigned token. The <paramref name="response"/> should be the
    /// OTP code displayed on the FortiToken device.
    /// </summary>
    public async Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(
        EnrollmentContext ctx, string response, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return new EnrollmentCompleteResult
            {
                Success = false,
                Error = "A one-time password code is required to complete FortiToken enrollment"
            };
        }

        var username = ctx.UserName;

        _logger.LogInformation(
            "FortiToken enrollment completion: verifying OTP for user {UserId} ({UserName})",
            ctx.UserId, username);

        // Validate the OTP against FortiAuthenticator to prove the token is working
        var authResult = await _fortiClient.AuthenticateAsync(username, response.Trim(), ct);

        if (!authResult.Success)
        {
            _logger.LogWarning(
                "FortiToken enrollment verification failed for user {UserId}: {Error}",
                ctx.UserId, authResult.Error);
            return new EnrollmentCompleteResult
            {
                Success = false,
                Error = $"Token verification failed: {authResult.Error}"
            };
        }

        _logger.LogInformation(
            "FortiToken enrollment completed successfully for user {UserId} ({UserName})",
            ctx.UserId, username);

        return new EnrollmentCompleteResult
        {
            Success = true
        };
    }

    // ── Challenge / Verify ──────────────────────────────────────────────

    /// <summary>
    /// Issues an MFA challenge. If <see cref="FortiTokenSettings.UsePushNotification"/>
    /// is enabled, a push notification is sent via FortiToken Mobile. Otherwise,
    /// the user is prompted to enter the OTP displayed on their hardware/software token.
    /// </summary>
    public async Task<ChallengeResult> IssueChallengeAsync(
        ChallengeContext ctx, CancellationToken ct = default)
    {
        // Extract the username from the stored enrollment secret.
        string username;
        try
        {
            username = ExtractUsernameFromContext(ctx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to extract username from enrollment context for user {UserId}", ctx.UserId);
            return new ChallengeResult
            {
                Success = false,
                Error = "Failed to retrieve enrollment data",
                Status = ChallengeStatus.Failed
            };
        }

        var challengeId = Guid.NewGuid().ToString();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.ChallengeExpiryMinutes);

        if (_settings.UsePushNotification)
        {
            return await IssuePushChallengeAsync(ctx, username, challengeId, expiresAt, ct);
        }

        return await IssueOtpChallengeAsync(ctx, username, challengeId, expiresAt, ct);
    }

    /// <summary>
    /// Verifies an OTP response against FortiAuthenticator.
    /// For push-based challenges, the OTP can still be used as a fallback.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(
        VerificationContext ctx, string response, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return new VerificationResult
            {
                Success = false,
                Error = "Verification code is required"
            };
        }

        var challenge = await _store.GetAsync<PendingFortiChallenge>(ChallengePrefix + ctx.ChallengeId, ct);
        if (challenge == null)
        {
            _logger.LogWarning(
                "FortiToken verification attempted for unknown challenge {ChallengeId}",
                ctx.ChallengeId);
            return new VerificationResult
            {
                Success = false,
                Error = "Challenge not found or already consumed"
            };
        }

        // Check expiry
        if (DateTimeOffset.UtcNow > challenge.ExpiresAt)
        {
            await _store.RemoveAsync(ChallengePrefix + ctx.ChallengeId, ct);
            _logger.LogWarning(
                "FortiToken challenge {ChallengeId} expired for user {UserId}",
                ctx.ChallengeId, ctx.UserId);
            return new VerificationResult
            {
                Success = false,
                Error = "Challenge has expired"
            };
        }

        // Check that the challenge has not already been resolved (push may have resolved it)
        if (challenge.Status != ChallengeStatus.Issued)
        {
            return new VerificationResult
            {
                Success = challenge.Status == ChallengeStatus.Approved,
                Error = challenge.Status == ChallengeStatus.Approved
                    ? null
                    : $"Challenge already resolved with status: {challenge.Status}"
            };
        }

        // Check max attempts
        if (challenge.Attempts >= _settings.MaxAttempts)
        {
            challenge.Status = ChallengeStatus.Failed;
            await _store.RemoveAsync(ChallengePrefix + ctx.ChallengeId, ct);
            _logger.LogWarning(
                "FortiToken challenge {ChallengeId} exceeded max attempts for user {UserId}",
                ctx.ChallengeId, ctx.UserId);
            return new VerificationResult
            {
                Success = false,
                Error = "Maximum verification attempts exceeded",
                ShouldLockout = true
            };
        }

        // Increment attempts
        challenge.Attempts++;
        await _store.SetAsync(ChallengePrefix + ctx.ChallengeId, challenge, TimeSpan.FromMinutes(_settings.ChallengeExpiryMinutes + 1), ct);

        // Validate OTP against FortiAuthenticator API
        var authResult = await _fortiClient.AuthenticateAsync(
            challenge.Username, response.Trim(), ct);

        if (!authResult.Success)
        {
            var remainingAttempts = _settings.MaxAttempts - challenge.Attempts;
            _logger.LogWarning(
                "FortiToken OTP verification failed for challenge {ChallengeId}, " +
                "{Remaining} attempt(s) remaining",
                ctx.ChallengeId, remainingAttempts);

            return new VerificationResult
            {
                Success = false,
                Error = $"Invalid verification code. {remainingAttempts} attempt(s) remaining."
            };
        }

        // Success - remove the challenge
        challenge.Status = ChallengeStatus.Approved;
        await _store.RemoveAsync(ChallengePrefix + ctx.ChallengeId, ct);

        _logger.LogInformation(
            "FortiToken verification succeeded for challenge {ChallengeId}, user {UserId}",
            ctx.ChallengeId, ctx.UserId);

        return new VerificationResult
        {
            Success = true
        };
    }

    /// <summary>
    /// Checks the asynchronous status of a push-based challenge by polling
    /// the FortiAuthenticator push status API.
    /// </summary>
    public async Task<AsyncVerificationStatus> CheckAsyncStatusAsync(
        string challengeId, CancellationToken ct = default)
    {
        var challenge = await _store.GetAsync<PendingFortiChallenge>(ChallengePrefix + challengeId, ct);
        if (challenge == null)
        {
            return new AsyncVerificationStatus
            {
                Status = ChallengeStatus.Failed,
                Error = "Challenge not found"
            };
        }

        // Auto-expire if the deadline has passed.
        if (challenge.Status == ChallengeStatus.Issued &&
            DateTimeOffset.UtcNow > challenge.ExpiresAt)
        {
            challenge.Status = ChallengeStatus.Expired;
            await _store.RemoveAsync(ChallengePrefix + challengeId, ct);

            return new AsyncVerificationStatus
            {
                Status = ChallengeStatus.Expired,
                Error = "Challenge has expired"
            };
        }

        // If the challenge was already resolved locally (e.g. via VerifyAsync with OTP),
        // return that status directly.
        if (challenge.Status != ChallengeStatus.Issued)
        {
            // Clean up resolved challenges after reporting the final status.
            await _store.RemoveAsync(ChallengePrefix + challengeId, ct);

            return new AsyncVerificationStatus
            {
                Status = challenge.Status,
                Error = challenge.Status switch
                {
                    ChallengeStatus.Denied => "Authentication request was denied by user",
                    ChallengeStatus.Expired => "Challenge has expired",
                    ChallengeStatus.Failed => "Challenge failed",
                    _ => null
                }
            };
        }

        // For push challenges, poll FortiAuthenticator for the push session status.
        if (!string.IsNullOrEmpty(challenge.PushSessionId))
        {
            var pushStatus = await _fortiClient.CheckPushStatusAsync(
                challenge.PushSessionId, ct);

            if (pushStatus.Success)
            {
                switch (pushStatus.Status)
                {
                    case "approved":
                    case "allow":
                    case "accept":
                        challenge.Status = ChallengeStatus.Approved;
                        await _store.RemoveAsync(ChallengePrefix + challengeId, ct);
                        _logger.LogInformation(
                            "FortiToken push approved for challenge {ChallengeId}", challengeId);
                        return new AsyncVerificationStatus
                        {
                            Status = ChallengeStatus.Approved
                        };

                    case "denied":
                    case "deny":
                    case "reject":
                        challenge.Status = ChallengeStatus.Denied;
                        await _store.RemoveAsync(ChallengePrefix + challengeId, ct);
                        _logger.LogInformation(
                            "FortiToken push denied for challenge {ChallengeId}", challengeId);
                        return new AsyncVerificationStatus
                        {
                            Status = ChallengeStatus.Denied,
                            Error = "Authentication request was denied by user"
                        };

                    case "pending":
                    case "waiting":
                        // Still waiting for user response
                        return new AsyncVerificationStatus
                        {
                            Status = ChallengeStatus.Issued
                        };

                    default:
                        _logger.LogWarning(
                            "Unknown FortiAuth push status '{Status}' for challenge {ChallengeId}",
                            pushStatus.Status, challengeId);
                        return new AsyncVerificationStatus
                        {
                            Status = ChallengeStatus.Issued
                        };
                }
            }

            // Push status check failed - keep the challenge alive but report the issue
            _logger.LogWarning(
                "Failed to poll FortiAuth push status for challenge {ChallengeId}: {Error}",
                challengeId, pushStatus.Error);
        }

        // Default: still issued and waiting
        return new AsyncVerificationStatus
        {
            Status = ChallengeStatus.Issued
        };
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Issues a push-based challenge via FortiToken Mobile.
    /// </summary>
    private async Task<ChallengeResult> IssuePushChallengeAsync(
        ChallengeContext ctx,
        string username,
        string challengeId,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var pushResult = await _fortiClient.PushAuthenticateAsync(username, ct);

        if (!pushResult.Success)
        {
            _logger.LogError(
                "FortiToken push failed for user {UserId}: {Error}",
                ctx.UserId, pushResult.Error);
            return new ChallengeResult
            {
                Success = false,
                Error = $"Failed to send push notification: {pushResult.Error}",
                Status = ChallengeStatus.Failed
            };
        }

        var pending = new PendingFortiChallenge
        {
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiresAt,
            Username = username,
            PushSessionId = pushResult.SessionId,
            IsPush = true
        };
        await _store.SetAsync(ChallengePrefix + challengeId, pending, TimeSpan.FromMinutes(_settings.ChallengeExpiryMinutes + 1), ct);

        _logger.LogInformation(
            "FortiToken push challenge {ChallengeId} issued for user {UserId}, " +
            "push session {PushSessionId}, expires at {ExpiresAt}",
            challengeId, ctx.UserId, pushResult.SessionId, expiresAt);

        return new ChallengeResult
        {
            Success = true,
            ChallengeId = challengeId,
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiresAt,
            UserPrompt = "A push notification has been sent to your FortiToken Mobile app. " +
                         "Approve the request, or enter the OTP code from your token as a fallback."
        };
    }

    /// <summary>
    /// Issues an OTP-only challenge (no push notification).
    /// </summary>
    private async Task<ChallengeResult> IssueOtpChallengeAsync(
        ChallengeContext ctx,
        string username,
        string challengeId,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var pending = new PendingFortiChallenge
        {
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiresAt,
            Username = username,
            PushSessionId = null,
            IsPush = false
        };
        await _store.SetAsync(ChallengePrefix + challengeId, pending, TimeSpan.FromMinutes(_settings.ChallengeExpiryMinutes + 1), ct);

        _logger.LogInformation(
            "FortiToken OTP challenge {ChallengeId} issued for user {UserId}, expires at {ExpiresAt}",
            challengeId, ctx.UserId, expiresAt);

        return new ChallengeResult
        {
            Success = true,
            ChallengeId = challengeId,
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiresAt,
            UserPrompt = "Enter the one-time password displayed on your FortiToken device."
        };
    }

    /// <summary>
    /// Extracts the username from the encrypted enrollment secret stored in the
    /// challenge context. The secret format is "username:serialNumber" encoded as UTF-8.
    /// Falls back to UserId if decryption is not possible.
    /// </summary>
    private string ExtractUsernameFromContext(ChallengeContext ctx)
    {
        if (ctx.EncryptedSecret != null && ctx.EncryptedSecret.Length > 0)
        {
            try
            {
                var secretStr = Encoding.UTF8.GetString(ctx.EncryptedSecret);
                var colonIndex = secretStr.IndexOf(':');
                if (colonIndex > 0)
                {
                    return secretStr[..colonIndex];
                }

                // If no colon found, treat the whole string as the username
                return secretStr;
            }
            catch
            {
                // Fall through to default
            }
        }

        // Fallback: use UserId as the username
        _logger.LogWarning(
            "Could not extract username from enrollment secret for user {UserId}, " +
            "falling back to UserId",
            ctx.UserId);
        return ctx.UserId;
    }

    /// <summary>
    /// Performs a constant-time comparison of two strings to prevent timing attacks
    /// on OTP verification. Used as a local validation fallback.
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a == null || b == null)
            return false;

        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    // ── Internal models ─────────────────────────────────────────────────

    /// <summary>
    /// Represents a pending FortiToken challenge awaiting verification via
    /// either OTP entry or push notification approval.
    /// </summary>
    internal sealed class PendingFortiChallenge
    {
        public ChallengeStatus Status { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string Username { get; set; } = string.Empty;
        public string? PushSessionId { get; set; }
        public bool IsPush { get; set; }
        public int Attempts { get; set; }
    }
}
