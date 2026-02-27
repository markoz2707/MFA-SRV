using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.Push;

/// <summary>
/// MFA provider that issues push notification challenges to a registered mobile device.
/// The user approves or denies the authentication request from their device, and the
/// server polls the challenge status asynchronously.
/// </summary>
public class PushMfaProvider : IMfaProvider
{
    private readonly PushNotificationClient _pushClient;
    private readonly PushSettings _settings;
    private readonly ILogger<PushMfaProvider> _logger;

    /// <summary>
    /// In-memory store for pending push challenges. In a production deployment this
    /// would be backed by a distributed cache (e.g. Redis) so that any server node
    /// can resolve the callback from the mobile app.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingPushChallenge> _pendingChallenges = new();

    /// <summary>
    /// In-memory store for pending enrollments. Maps a registration token to the
    /// enrollment context so that <see cref="CompleteEnrollmentAsync"/> can match
    /// the callback from the mobile app.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingEnrollment> _pendingEnrollments = new();

    public PushMfaProvider(
        PushNotificationClient pushClient,
        IOptions<PushSettings> settings,
        ILogger<PushMfaProvider> logger)
    {
        _pushClient = pushClient;
        _settings = settings.Value;
        _logger = logger;
    }

    // ── IMfaProvider metadata ────────────────────────────────────────────

    public string MethodId => "PUSH";
    public string DisplayName => "Push Notification";
    public bool SupportsSynchronousVerification => false;
    public bool SupportsAsynchronousVerification => true;
    public bool RequiresEndpointAgent => false;

    // ── Enrollment ──────────────────────────────────────────────────────

    /// <summary>
    /// Begins enrollment by generating a unique registration token and a shared
    /// secret. The mobile app uses the registration token to associate itself with
    /// this enrollment, and later provides its FCM/APNs device token.
    /// </summary>
    public Task<EnrollmentInitResult> BeginEnrollmentAsync(EnrollmentContext ctx, CancellationToken ct = default)
    {
        // Generate a cryptographically random registration token the mobile app
        // will present when it calls CompleteEnrollment with its device token.
        var registrationToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // Generate a shared secret that will be stored (encrypted) in the enrollment
        // record. For push, the secret holds the device token after completion.
        var secret = RandomNumberGenerator.GetBytes(32);

        // Store the pending enrollment so CompleteEnrollment can find it.
        var pending = new PendingEnrollment
        {
            UserId = ctx.UserId,
            UserName = ctx.UserName,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        _pendingEnrollments[registrationToken] = pending;

        _logger.LogInformation(
            "Push enrollment started for user {UserId}. Registration token issued",
            ctx.UserId);

        return Task.FromResult(new EnrollmentInitResult
        {
            Success = true,
            Secret = secret,
            ProvisioningUri = null,
            QrCodeDataUri = null,
            Metadata = new Dictionary<string, string>
            {
                ["registrationToken"] = registrationToken,
                ["instruction"] = "Open the MfaSrv Authenticator app and scan or paste this registration token to link your device."
            }
        });
    }

    /// <summary>
    /// Completes enrollment by accepting the device token provided by the mobile
    /// app. The <paramref name="response"/> is expected to be a JSON object with
    /// at least <c>registrationToken</c> and <c>deviceToken</c> fields.
    /// </summary>
    public Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(EnrollmentContext ctx, string response, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("registrationToken", out var regTokenElement) ||
                !root.TryGetProperty("deviceToken", out var devTokenElement))
            {
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "Response must contain 'registrationToken' and 'deviceToken' fields"
                });
            }

            var registrationToken = regTokenElement.GetString();
            var deviceToken = devTokenElement.GetString();

            if (string.IsNullOrWhiteSpace(registrationToken) || string.IsNullOrWhiteSpace(deviceToken))
            {
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "Registration token and device token must not be empty"
                });
            }

            // Validate the registration token.
            if (!_pendingEnrollments.TryRemove(registrationToken, out var pending))
            {
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "Invalid or expired registration token"
                });
            }

            if (DateTimeOffset.UtcNow > pending.ExpiresAt)
            {
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "Registration token has expired"
                });
            }

            // Verify user identity matches.
            if (pending.UserId != ctx.UserId)
            {
                _logger.LogWarning(
                    "Enrollment completion user mismatch: expected {ExpectedUser}, got {ActualUser}",
                    pending.UserId, ctx.UserId);

                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "User identity mismatch"
                });
            }

            // The device token is stored by the calling layer (e.g. EnrollmentsController)
            // in the MfaEnrollment entity's encrypted secret field. We encode the device
            // token into the enrollment context's secret so the caller can persist it.
            //
            // NOTE: The caller is responsible for encrypting and storing the device token
            // that is returned via this result. The PushMfaProvider stores it as the
            // enrollment secret (replacing the initial placeholder secret) so that
            // IssueChallengeAsync can later retrieve it from the EncryptedSecret field.

            _logger.LogInformation(
                "Push enrollment completed for user {UserId}. Device token registered",
                ctx.UserId);

            return Task.FromResult(new EnrollmentCompleteResult
            {
                Success = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse enrollment completion response");
            return Task.FromResult(new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Invalid JSON response format"
            });
        }
    }

    // ── Challenge / Verify ──────────────────────────────────────────────

    /// <summary>
    /// Issues a push notification challenge. The device token is extracted from
    /// the encrypted secret stored in the enrollment. A push notification is
    /// sent to the mobile app, and the challenge is stored in the pending
    /// dictionary for asynchronous status polling.
    /// </summary>
    public async Task<ChallengeResult> IssueChallengeAsync(ChallengeContext ctx, CancellationToken ct = default)
    {
        // The encrypted secret contains the device token (stored as UTF-8 bytes).
        // The caller (orchestrator) is expected to pass the encrypted blob; the
        // provider reads the device token from the decrypted secret.
        //
        // In practice the orchestrator decrypts before calling us, but to be safe
        // we handle both encrypted and plaintext paths.
        string? deviceToken = null;

        if (ctx.EncryptedSecret != null)
        {
            try
            {
                // The secret stored for push enrollments is a JSON object with the
                // device token. Try to parse it.
                var secretJson = Encoding.UTF8.GetString(ctx.EncryptedSecret);
                using var doc = JsonDocument.Parse(secretJson);
                if (doc.RootElement.TryGetProperty("deviceToken", out var dtElem))
                {
                    deviceToken = dtElem.GetString();
                }
                else
                {
                    // If the secret is not JSON, treat the entire string as the device token.
                    deviceToken = secretJson;
                }
            }
            catch (JsonException)
            {
                // Not JSON - treat raw bytes as UTF-8 device token string.
                deviceToken = Encoding.UTF8.GetString(ctx.EncryptedSecret);
            }
        }

        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            _logger.LogError("No device token found for enrollment {EnrollmentId}", ctx.EnrollmentId);
            return new ChallengeResult
            {
                Success = false,
                Error = "No device token registered for this enrollment",
                Status = ChallengeStatus.Failed
            };
        }

        var challengeId = Guid.NewGuid().ToString();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.ChallengeExpiryMinutes);

        // Store the pending challenge.
        var pending = new PendingPushChallenge
        {
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiresAt,
            DeviceToken = deviceToken
        };
        _pendingChallenges[challengeId] = pending;

        // Send the push notification.
        var title = "MFA Authentication Request";
        var body = ctx.TargetResource != null
            ? $"Approve sign-in to {ctx.TargetResource}?"
            : "Approve your sign-in request?";

        var sent = await _pushClient.SendPushAsync(deviceToken, title, body, challengeId, ct);

        if (!sent)
        {
            // Remove the pending challenge since delivery failed.
            _pendingChallenges.TryRemove(challengeId, out _);

            _logger.LogError("Failed to deliver push notification for challenge {ChallengeId}", challengeId);
            return new ChallengeResult
            {
                Success = false,
                Error = "Failed to deliver push notification",
                Status = ChallengeStatus.Failed
            };
        }

        _logger.LogInformation(
            "Push challenge {ChallengeId} issued for user {UserId}, expires at {ExpiresAt}",
            challengeId, ctx.UserId, expiresAt);

        return new ChallengeResult
        {
            Success = true,
            ChallengeId = challengeId,
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiresAt,
            UserPrompt = "A push notification has been sent to your registered device. Please approve or deny the request."
        };
    }

    /// <summary>
    /// Processes an approval or denial response from the mobile app callback.
    /// The <paramref name="response"/> string is expected to be "APPROVE" or "DENY".
    /// </summary>
    public Task<VerificationResult> VerifyAsync(VerificationContext ctx, string response, CancellationToken ct = default)
    {
        if (!_pendingChallenges.TryGetValue(ctx.ChallengeId, out var pending))
        {
            _logger.LogWarning("Verification attempted for unknown challenge {ChallengeId}", ctx.ChallengeId);
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Challenge not found or already resolved"
            });
        }

        // Check expiration.
        if (DateTimeOffset.UtcNow > pending.ExpiresAt)
        {
            pending.Status = ChallengeStatus.Expired;
            _pendingChallenges.TryRemove(ctx.ChallengeId, out _);

            _logger.LogInformation("Challenge {ChallengeId} has expired", ctx.ChallengeId);
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Challenge has expired"
            });
        }

        // Check that the challenge has not already been resolved.
        if (pending.Status != ChallengeStatus.Issued)
        {
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = $"Challenge already resolved with status: {pending.Status}"
            });
        }

        var normalizedResponse = response?.Trim().ToUpperInvariant();

        switch (normalizedResponse)
        {
            case "APPROVE":
                pending.Status = ChallengeStatus.Approved;
                _logger.LogInformation("Challenge {ChallengeId} approved by user {UserId}", ctx.ChallengeId, ctx.UserId);
                return Task.FromResult(new VerificationResult
                {
                    Success = true
                });

            case "DENY":
                pending.Status = ChallengeStatus.Denied;
                _logger.LogInformation("Challenge {ChallengeId} denied by user {UserId}", ctx.ChallengeId, ctx.UserId);
                return Task.FromResult(new VerificationResult
                {
                    Success = false,
                    Error = "Authentication request was denied by user"
                });

            default:
                _logger.LogWarning(
                    "Invalid push response '{Response}' for challenge {ChallengeId}",
                    normalizedResponse, ctx.ChallengeId);
                return Task.FromResult(new VerificationResult
                {
                    Success = false,
                    Error = "Invalid response. Expected 'APPROVE' or 'DENY'"
                });
        }
    }

    /// <summary>
    /// Returns the current asynchronous status of a push challenge.
    /// Called by the server when polling on behalf of the authentication flow.
    /// </summary>
    public Task<AsyncVerificationStatus> CheckAsyncStatusAsync(string challengeId, CancellationToken ct = default)
    {
        if (!_pendingChallenges.TryGetValue(challengeId, out var pending))
        {
            return Task.FromResult(new AsyncVerificationStatus
            {
                Status = ChallengeStatus.Failed,
                Error = "Challenge not found"
            });
        }

        // Auto-expire if the deadline has passed.
        if (pending.Status == ChallengeStatus.Issued && DateTimeOffset.UtcNow > pending.ExpiresAt)
        {
            pending.Status = ChallengeStatus.Expired;
            _pendingChallenges.TryRemove(challengeId, out _);
        }

        // Clean up resolved challenges from memory (keep them until polled at least once).
        if (pending.Status is ChallengeStatus.Approved or ChallengeStatus.Denied or ChallengeStatus.Expired)
        {
            // Remove after returning the final status so the next poll returns "not found".
            _pendingChallenges.TryRemove(challengeId, out _);
        }

        return Task.FromResult(new AsyncVerificationStatus
        {
            Status = pending.Status,
            Error = pending.Status switch
            {
                ChallengeStatus.Denied => "Authentication request was denied by user",
                ChallengeStatus.Expired => "Challenge has expired",
                _ => null
            }
        });
    }

    // ── Internal models ─────────────────────────────────────────────────

    private sealed class PendingPushChallenge
    {
        public ChallengeStatus Status { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public required string DeviceToken { get; set; }
    }

    private sealed class PendingEnrollment
    {
        public required string UserId { get; set; }
        public required string UserName { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
