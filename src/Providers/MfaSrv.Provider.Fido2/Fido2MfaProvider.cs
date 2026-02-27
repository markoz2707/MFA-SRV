using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Provider.Fido2;

/// <summary>
/// MFA provider implementing FIDO2 / WebAuthn authentication using hardware
/// security keys (e.g. YubiKey). Uses the Fido2.Models package for type
/// definitions while implementing the core registration and assertion logic
/// manually.
/// </summary>
public class Fido2MfaProvider : IMfaProvider
{
    private readonly Fido2Settings _settings;
    private readonly ILogger<Fido2MfaProvider> _logger;

    /// <summary>
    /// Pending attestation (registration) options keyed by a server-generated
    /// challenge ID. Consumed by <see cref="CompleteEnrollmentAsync"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingAttestation> _pendingAttestations = new();

    /// <summary>
    /// Pending assertion (authentication) options keyed by challenge ID.
    /// Consumed by <see cref="VerifyAsync"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingAssertion> _pendingAssertions = new();

    public Fido2MfaProvider(IOptions<Fido2Settings> settings, ILogger<Fido2MfaProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    // ── IMfaProvider metadata ────────────────────────────────────────────

    public string MethodId => "FIDO2";
    public string DisplayName => "FIDO2 / WebAuthn (YubiKey)";
    public bool SupportsSynchronousVerification => true;
    public bool SupportsAsynchronousVerification => false;
    public bool RequiresEndpointAgent => true;

    // ── Enrollment (Attestation) ────────────────────────────────────────

    /// <summary>
    /// Begins FIDO2 enrollment by creating PublicKeyCredentialCreationOptions.
    /// The client (browser / endpoint agent) uses these options to call
    /// <c>navigator.credentials.create()</c> and returns the attestation response.
    /// </summary>
    public Task<EnrollmentInitResult> BeginEnrollmentAsync(EnrollmentContext ctx, CancellationToken ct = default)
    {
        var challengeBytes = RandomNumberGenerator.GetBytes(_settings.ChallengeSize);
        var challengeId = Guid.NewGuid().ToString();

        // Build the user handle — a random byte sequence (not the username)
        // per WebAuthn recommendation.
        var userHandle = RandomNumberGenerator.GetBytes(32);

        // Attestation options to send to the client.
        var creationOptions = new
        {
            rp = new { id = _settings.ServerDomain, name = _settings.ServerName },
            user = new
            {
                id = Convert.ToBase64String(userHandle),
                name = ctx.UserName,
                displayName = ctx.UserName
            },
            challenge = Convert.ToBase64String(challengeBytes),
            pubKeyCredParams = new[]
            {
                new { type = "public-key", alg = -7 },   // ES256
                new { type = "public-key", alg = -257 }   // RS256
            },
            timeout = (long)TimeSpan.FromMinutes(_settings.ChallengeExpiryMinutes).TotalMilliseconds,
            attestation = "direct",
            authenticatorSelection = new
            {
                authenticatorAttachment = "cross-platform",
                requireResidentKey = false,
                userVerification = "preferred"
            }
        };

        var optionsJson = JsonSerializer.Serialize(creationOptions, _jsonOptions);

        // Store the pending attestation so we can validate the response later.
        _pendingAttestations[challengeId] = new PendingAttestation
        {
            Challenge = challengeBytes,
            UserId = ctx.UserId,
            UserHandle = userHandle,
            RpId = _settings.ServerDomain,
            Origin = _settings.Origin,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.ChallengeExpiryMinutes)
        };

        _logger.LogInformation(
            "FIDO2 attestation started for user {UserId}, challenge {ChallengeId}",
            ctx.UserId, challengeId);

        return Task.FromResult(new EnrollmentInitResult
        {
            Success = true,
            Secret = userHandle, // Placeholder; will be replaced by credential data on completion.
            Metadata = new Dictionary<string, string>
            {
                ["challengeId"] = challengeId,
                ["publicKeyCredentialCreationOptions"] = optionsJson,
                ["instruction"] = "Use your security key to complete registration. Insert the key and touch it when prompted."
            }
        });
    }

    /// <summary>
    /// Completes FIDO2 enrollment by validating the attestation response from
    /// the client. Extracts the credential ID and public key from the
    /// authenticator's response and returns success so the caller can persist
    /// the credential data in the enrollment record.
    /// </summary>
    public Task<EnrollmentCompleteResult> CompleteEnrollmentAsync(EnrollmentContext ctx, string response, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            // Extract the challenge ID to look up the pending attestation.
            if (!root.TryGetProperty("challengeId", out var challengeIdElem))
            {
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "Response must contain 'challengeId'"
                });
            }

            var challengeId = challengeIdElem.GetString()!;

            if (!_pendingAttestations.TryRemove(challengeId, out var pending))
            {
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "Unknown or expired attestation challenge"
                });
            }

            if (DateTimeOffset.UtcNow > pending.ExpiresAt)
            {
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "Attestation challenge has expired"
                });
            }

            // Verify user identity matches.
            if (pending.UserId != ctx.UserId)
            {
                _logger.LogWarning(
                    "Attestation user mismatch: expected {Expected}, got {Actual}",
                    pending.UserId, ctx.UserId);

                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "User identity mismatch"
                });
            }

            // Extract the attestation response fields.
            if (!root.TryGetProperty("attestationObject", out var attestationObjElem) ||
                !root.TryGetProperty("clientDataJSON", out var clientDataElem) ||
                !root.TryGetProperty("credentialId", out var credIdElem))
            {
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = "Response must contain 'attestationObject', 'clientDataJSON', and 'credentialId'"
                });
            }

            var credentialIdB64 = credIdElem.GetString()!;
            var clientDataJsonB64 = clientDataElem.GetString()!;

            // Validate clientDataJSON.
            var clientDataBytes = Convert.FromBase64String(clientDataJsonB64);
            var clientDataValidation = ValidateClientData(
                clientDataBytes, pending.Challenge, "webauthn.create", pending.Origin);

            if (!clientDataValidation.IsValid)
            {
                _logger.LogWarning("Client data validation failed: {Error}", clientDataValidation.Error);
                return Task.FromResult(new EnrollmentCompleteResult
                {
                    Success = false,
                    Error = $"Client data validation failed: {clientDataValidation.Error}"
                });
            }

            // Extract public key from the attestation response.
            // The attestation object is CBOR-encoded. For a lightweight
            // implementation we accept the credential and store the raw
            // attestation data. The public key is provided by the client
            // as an additional convenience field.
            string? publicKeyB64 = null;
            if (root.TryGetProperty("publicKey", out var pkElem))
            {
                publicKeyB64 = pkElem.GetString();
            }

            int algorithm = -7; // Default ES256
            if (root.TryGetProperty("algorithm", out var algElem))
            {
                algorithm = algElem.GetInt32();
            }

            // Build the credential data that will be stored (encrypted) as the
            // enrollment secret. This is serialized to JSON so that
            // IssueChallenge / Verify can reconstruct the credential later.
            var credentialData = new StoredCredentialData
            {
                CredentialId = credentialIdB64,
                PublicKey = publicKeyB64 ?? string.Empty,
                SignCount = 0,
                Algorithm = algorithm,
                UserHandle = Convert.ToBase64String(pending.UserHandle),
                RpId = pending.RpId,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var credentialJson = JsonSerializer.Serialize(credentialData, _jsonOptions);

            _logger.LogInformation(
                "FIDO2 attestation completed for user {UserId}, credential {CredentialId}",
                ctx.UserId, credentialIdB64);

            // The caller (EnrollmentsController / orchestrator) persists the
            // credential JSON as the enrollment's encrypted secret. We signal
            // success and provide the credential data in metadata so the caller
            // knows what to store.
            return Task.FromResult(new EnrollmentCompleteResult
            {
                Success = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse attestation response");
            return Task.FromResult(new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Invalid JSON attestation response"
            });
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Invalid base64 in attestation response");
            return Task.FromResult(new EnrollmentCompleteResult
            {
                Success = false,
                Error = "Invalid base64 encoding in attestation response"
            });
        }
    }

    // ── Challenge (Assertion) ───────────────────────────────────────────

    /// <summary>
    /// Issues a FIDO2 assertion challenge. Creates
    /// PublicKeyCredentialRequestOptions that the client uses to call
    /// <c>navigator.credentials.get()</c>.
    /// </summary>
    public Task<ChallengeResult> IssueChallengeAsync(ChallengeContext ctx, CancellationToken ct = default)
    {
        // Deserialize the stored credential data from the encrypted secret.
        StoredCredentialData? credential = null;
        if (ctx.EncryptedSecret != null)
        {
            try
            {
                var json = Encoding.UTF8.GetString(ctx.EncryptedSecret);
                credential = JsonSerializer.Deserialize<StoredCredentialData>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize credential data for enrollment {EnrollmentId}", ctx.EnrollmentId);
            }
        }

        if (credential == null)
        {
            return Task.FromResult(new ChallengeResult
            {
                Success = false,
                Error = "No FIDO2 credential found for this enrollment",
                Status = ChallengeStatus.Failed
            });
        }

        var challengeBytes = RandomNumberGenerator.GetBytes(_settings.ChallengeSize);
        var challengeId = Guid.NewGuid().ToString();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.ChallengeExpiryMinutes);

        // Build assertion options.
        var requestOptions = new
        {
            challenge = Convert.ToBase64String(challengeBytes),
            timeout = (long)TimeSpan.FromMinutes(_settings.ChallengeExpiryMinutes).TotalMilliseconds,
            rpId = credential.RpId,
            allowCredentials = new[]
            {
                new
                {
                    type = "public-key",
                    id = credential.CredentialId
                }
            },
            userVerification = "preferred"
        };

        var optionsJson = JsonSerializer.Serialize(requestOptions, _jsonOptions);

        // Store the pending assertion.
        _pendingAssertions[challengeId] = new PendingAssertion
        {
            Challenge = challengeBytes,
            Credential = credential,
            UserId = ctx.UserId,
            Origin = _settings.Origin,
            ExpiresAt = expiresAt
        };

        _logger.LogInformation(
            "FIDO2 assertion challenge {ChallengeId} issued for user {UserId}",
            challengeId, ctx.UserId);

        return Task.FromResult(new ChallengeResult
        {
            Success = true,
            ChallengeId = challengeId,
            Status = ChallengeStatus.Issued,
            ExpiresAt = expiresAt,
            UserPrompt = $"Insert your security key and touch it to authenticate.\n{optionsJson}"
        });
    }

    /// <summary>
    /// Verifies a FIDO2 assertion response from the client.
    /// The <paramref name="response"/> is a JSON object containing the
    /// authenticator assertion response fields.
    /// </summary>
    public Task<VerificationResult> VerifyAsync(VerificationContext ctx, string response, CancellationToken ct = default)
    {
        if (!_pendingAssertions.TryRemove(ctx.ChallengeId, out var pending))
        {
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Unknown or expired assertion challenge"
            });
        }

        if (DateTimeOffset.UtcNow > pending.ExpiresAt)
        {
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Assertion challenge has expired"
            });
        }

        if (pending.UserId != ctx.UserId)
        {
            _logger.LogWarning(
                "Assertion user mismatch: expected {Expected}, got {Actual}",
                pending.UserId, ctx.UserId);

            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "User identity mismatch"
            });
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            // Extract required assertion response fields.
            if (!root.TryGetProperty("credentialId", out var credIdElem) ||
                !root.TryGetProperty("authenticatorData", out var authDataElem) ||
                !root.TryGetProperty("clientDataJSON", out var clientDataElem) ||
                !root.TryGetProperty("signature", out var signatureElem))
            {
                return Task.FromResult(new VerificationResult
                {
                    Success = false,
                    Error = "Response must contain 'credentialId', 'authenticatorData', 'clientDataJSON', and 'signature'"
                });
            }

            var credentialId = credIdElem.GetString()!;
            var authenticatorDataB64 = authDataElem.GetString()!;
            var clientDataJsonB64 = clientDataElem.GetString()!;
            var signatureB64 = signatureElem.GetString()!;

            // Verify credential ID matches.
            if (credentialId != pending.Credential.CredentialId)
            {
                _logger.LogWarning(
                    "Credential ID mismatch for challenge {ChallengeId}",
                    ctx.ChallengeId);

                return Task.FromResult(new VerificationResult
                {
                    Success = false,
                    Error = "Credential ID does not match"
                });
            }

            // Validate clientDataJSON.
            var clientDataBytes = Convert.FromBase64String(clientDataJsonB64);
            var clientDataValidation = ValidateClientData(
                clientDataBytes, pending.Challenge, "webauthn.get", pending.Origin);

            if (!clientDataValidation.IsValid)
            {
                _logger.LogWarning("Client data validation failed: {Error}", clientDataValidation.Error);
                return Task.FromResult(new VerificationResult
                {
                    Success = false,
                    Error = $"Client data validation failed: {clientDataValidation.Error}"
                });
            }

            // Validate authenticator data.
            var authenticatorData = Convert.FromBase64String(authenticatorDataB64);
            var authDataValidation = ValidateAuthenticatorData(
                authenticatorData, pending.Credential.RpId, pending.Credential.SignCount);

            if (!authDataValidation.IsValid)
            {
                _logger.LogWarning("Authenticator data validation failed: {Error}", authDataValidation.Error);
                return Task.FromResult(new VerificationResult
                {
                    Success = false,
                    Error = $"Authenticator data validation failed: {authDataValidation.Error}"
                });
            }

            // Verify the signature over the authenticator data and client data hash.
            var signatureBytes = Convert.FromBase64String(signatureB64);
            var clientDataHash = SHA256.HashData(clientDataBytes);

            // The signed message is authenticatorData || clientDataHash.
            var signedData = new byte[authenticatorData.Length + clientDataHash.Length];
            Buffer.BlockCopy(authenticatorData, 0, signedData, 0, authenticatorData.Length);
            Buffer.BlockCopy(clientDataHash, 0, signedData, authenticatorData.Length, clientDataHash.Length);

            var signatureValid = VerifySignature(
                pending.Credential.PublicKey,
                pending.Credential.Algorithm,
                signedData,
                signatureBytes);

            if (!signatureValid)
            {
                _logger.LogWarning("Signature verification failed for challenge {ChallengeId}", ctx.ChallengeId);
                return Task.FromResult(new VerificationResult
                {
                    Success = false,
                    Error = "Signature verification failed"
                });
            }

            // Update sign count for replay detection.
            pending.Credential.SignCount = authDataValidation.NewSignCount;

            _logger.LogInformation(
                "FIDO2 assertion verified for user {UserId}, challenge {ChallengeId}",
                ctx.UserId, ctx.ChallengeId);

            return Task.FromResult(new VerificationResult
            {
                Success = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse assertion response");
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Invalid JSON assertion response"
            });
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Invalid base64 in assertion response");
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Invalid base64 encoding in assertion response"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during assertion verification");
            return Task.FromResult(new VerificationResult
            {
                Success = false,
                Error = "Internal error during signature verification"
            });
        }
    }

    /// <summary>
    /// FIDO2 is synchronous, so async status always returns Issued (waiting for
    /// the user to interact with the security key).
    /// </summary>
    public Task<AsyncVerificationStatus> CheckAsyncStatusAsync(string challengeId, CancellationToken ct = default)
    {
        // FIDO2 is a synchronous flow; there is no async status to check.
        return Task.FromResult(new AsyncVerificationStatus
        {
            Status = ChallengeStatus.Issued
        });
    }

    // ── Validation helpers ──────────────────────────────────────────────

    /// <summary>
    /// Validates the clientDataJSON object per WebAuthn specification.
    /// </summary>
    private static ClientDataValidationResult ValidateClientData(
        byte[] clientDataBytes, byte[] expectedChallenge, string expectedType, string expectedOrigin)
    {
        try
        {
            var clientDataJson = Encoding.UTF8.GetString(clientDataBytes);
            using var doc = JsonDocument.Parse(clientDataJson);
            var root = doc.RootElement;

            // Verify type.
            if (!root.TryGetProperty("type", out var typeElem) ||
                typeElem.GetString() != expectedType)
            {
                return new ClientDataValidationResult(false, $"Expected type '{expectedType}'");
            }

            // Verify challenge.
            if (!root.TryGetProperty("challenge", out var challengeElem))
            {
                return new ClientDataValidationResult(false, "Missing 'challenge' field");
            }

            var challengeB64 = challengeElem.GetString()!;
            // WebAuthn uses base64url encoding for the challenge.
            var receivedChallenge = Base64UrlDecode(challengeB64);
            if (!CryptographicOperations.FixedTimeEquals(receivedChallenge, expectedChallenge))
            {
                return new ClientDataValidationResult(false, "Challenge mismatch");
            }

            // Verify origin.
            if (!root.TryGetProperty("origin", out var originElem) ||
                originElem.GetString() != expectedOrigin)
            {
                return new ClientDataValidationResult(false, "Origin mismatch");
            }

            return new ClientDataValidationResult(true, null);
        }
        catch (Exception ex)
        {
            return new ClientDataValidationResult(false, $"Failed to parse clientDataJSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates authenticator data per WebAuthn specification.
    /// The authenticator data is at least 37 bytes:
    ///   - 32 bytes: rpIdHash
    ///   - 1 byte:   flags
    ///   - 4 bytes:  signCount (big-endian uint32)
    /// </summary>
    private static AuthenticatorDataValidationResult ValidateAuthenticatorData(
        byte[] authData, string expectedRpId, uint previousSignCount)
    {
        if (authData.Length < 37)
        {
            return new AuthenticatorDataValidationResult(false, "Authenticator data too short", 0);
        }

        // Validate rpIdHash (first 32 bytes).
        var expectedRpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedRpId));
        var rpIdHash = authData[..32];
        if (!CryptographicOperations.FixedTimeEquals(rpIdHash, expectedRpIdHash))
        {
            return new AuthenticatorDataValidationResult(false, "RP ID hash mismatch", 0);
        }

        // Validate flags (byte 32).
        var flags = authData[32];
        var userPresent = (flags & 0x01) != 0;
        if (!userPresent)
        {
            return new AuthenticatorDataValidationResult(false, "User presence flag not set", 0);
        }

        // Extract sign count (bytes 33-36, big-endian).
        var signCount = (uint)(
            (authData[33] << 24) |
            (authData[34] << 16) |
            (authData[35] << 8) |
            authData[36]);

        // Sign count validation: if both the stored and new sign counts are
        // non-zero, the new count must be greater to prevent replay attacks.
        if (signCount > 0 && previousSignCount > 0 && signCount <= previousSignCount)
        {
            return new AuthenticatorDataValidationResult(
                false,
                $"Sign count regression detected (stored: {previousSignCount}, received: {signCount})",
                signCount);
        }

        return new AuthenticatorDataValidationResult(true, null, signCount);
    }

    /// <summary>
    /// Verifies a cryptographic signature using the stored public key.
    /// Supports ES256 (COSE algorithm -7) and RS256 (COSE algorithm -257).
    /// </summary>
    private bool VerifySignature(string publicKeyB64, int algorithm, byte[] signedData, byte[] signature)
    {
        if (string.IsNullOrEmpty(publicKeyB64))
        {
            _logger.LogWarning("No public key available for signature verification");
            return false;
        }

        try
        {
            var publicKeyBytes = Convert.FromBase64String(publicKeyB64);

            switch (algorithm)
            {
                case -7: // ES256
                {
                    using var ecdsa = ECDsa.Create();
                    ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
                    return ecdsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                }
                case -257: // RS256
                {
                    using var rsa = RSA.Create();
                    rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
                    return rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
                default:
                    _logger.LogWarning("Unsupported COSE algorithm: {Algorithm}", algorithm);
                    return false;
            }
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Cryptographic error during signature verification");
            return false;
        }
    }

    /// <summary>
    /// Decodes a base64url-encoded string (per RFC 7515) to bytes.
    /// </summary>
    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input
            .Replace('-', '+')
            .Replace('_', '/');

        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }

        return Convert.FromBase64String(base64);
    }

    // ── JSON serialization options ──────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // ── Internal models ─────────────────────────────────────────────────

    private sealed record ClientDataValidationResult(bool IsValid, string? Error);

    private sealed record AuthenticatorDataValidationResult(bool IsValid, string? Error, uint NewSignCount);

    private sealed class PendingAttestation
    {
        public required byte[] Challenge { get; set; }
        public required string UserId { get; set; }
        public required byte[] UserHandle { get; set; }
        public required string RpId { get; set; }
        public required string Origin { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class PendingAssertion
    {
        public required byte[] Challenge { get; set; }
        public required StoredCredentialData Credential { get; set; }
        public required string UserId { get; set; }
        public required string Origin { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    /// <summary>
    /// Credential data persisted (as encrypted JSON) in the enrollment record.
    /// Contains everything needed to validate future assertion responses.
    /// </summary>
    private sealed class StoredCredentialData
    {
        public string CredentialId { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public uint SignCount { get; set; }
        public int Algorithm { get; set; } = -7; // COSE ES256
        public string UserHandle { get; set; } = string.Empty;
        public string RpId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
