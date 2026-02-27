using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.EndpointAgent.Services;

/// <summary>
/// Manages local FIDO2/WebAuthn assertion flows for YubiKey authentication.
/// Coordinates between the C++ Credential Provider (which handles the
/// platform CTAP2/WebAuthn API interaction) and the Central Server
/// (which holds the stored credential and performs final verification).
///
/// Flow:
/// 1. Central Server issues FIDO2 challenge with assertion options
/// 2. This service parses the options and forwards them to the Credential Provider
/// 3. Credential Provider triggers Windows WebAuthn API → user taps YubiKey
/// 4. Credential Provider returns assertion response via named pipe
/// 5. This service forwards the assertion to Central Server for verification
/// </summary>
public class YubiKeyService
{
    private readonly CentralServerClient _centralServerClient;
    private readonly EndpointSessionCache _sessionCache;
    private readonly EndpointAgentSettings _settings;
    private readonly ILogger<YubiKeyService> _logger;

    /// <summary>
    /// Active FIDO2 challenges awaiting user interaction with the security key.
    /// Key: challengeId, Value: the assertion context from the Central Server.
    /// </summary>
    private readonly ConcurrentDictionary<string, Fido2ChallengeContext> _activeChallenges = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public YubiKeyService(
        CentralServerClient centralServerClient,
        EndpointSessionCache sessionCache,
        IOptions<EndpointAgentSettings> settings,
        ILogger<YubiKeyService> logger)
    {
        _centralServerClient = centralServerClient;
        _sessionCache = sessionCache;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Begins a local FIDO2 assertion flow. Requests assertion options from the
    /// Central Server and returns them formatted for the Credential Provider to
    /// invoke the platform WebAuthn API.
    /// </summary>
    public async Task<Fido2BeginResult> BeginAssertionAsync(
        string userName, string domain, string challengeId, CancellationToken ct = default)
    {
        try
        {
            // If we already have a challenge context from the PreAuth response,
            // use it directly (the Central Server already issued the challenge).
            if (_activeChallenges.TryGetValue(challengeId, out var existing))
            {
                _logger.LogDebug(
                    "Using existing FIDO2 challenge {ChallengeId} for {User}",
                    challengeId, userName);

                return new Fido2BeginResult
                {
                    Success = true,
                    ChallengeId = challengeId,
                    AssertionOptionsJson = existing.AssertionOptionsJson,
                    TimeoutMs = existing.TimeoutMs
                };
            }

            // Otherwise, request a new FIDO2 challenge from the Central Server.
            var response = await _centralServerClient.PreAuthenticateAsync(userName, domain, _settings.Hostname, ct);

            if (response == null)
            {
                return new Fido2BeginResult
                {
                    Success = false,
                    Error = "Central server unavailable for FIDO2 challenge"
                };
            }

            if (string.IsNullOrEmpty(response.ChallengeId))
            {
                return new Fido2BeginResult
                {
                    Success = false,
                    Error = "Server did not issue a FIDO2 challenge"
                };
            }

            // Parse assertion options from the server response.
            // The server includes them in the challenge metadata.
            var assertionOptions = response.ChallengeMetadata;
            if (string.IsNullOrEmpty(assertionOptions))
            {
                // Build minimal assertion options from what we know
                assertionOptions = BuildMinimalAssertionOptions(response);
            }

            var context = new Fido2ChallengeContext
            {
                ChallengeId = response.ChallengeId,
                UserName = userName,
                Domain = domain,
                AssertionOptionsJson = assertionOptions,
                TimeoutMs = response.TimeoutMs > 0 ? response.TimeoutMs : 60000,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(
                    response.TimeoutMs > 0 ? response.TimeoutMs : 60000)
            };

            _activeChallenges[response.ChallengeId] = context;

            _logger.LogInformation(
                "FIDO2 assertion started for {User}@{Domain}, challenge {ChallengeId}",
                userName, domain, response.ChallengeId);

            return new Fido2BeginResult
            {
                Success = true,
                ChallengeId = response.ChallengeId,
                AssertionOptionsJson = assertionOptions,
                TimeoutMs = context.TimeoutMs
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error beginning FIDO2 assertion for {User}@{Domain}", userName, domain);
            return new Fido2BeginResult
            {
                Success = false,
                Error = $"Internal error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Registers a FIDO2 challenge context received from the Central Server
    /// during pre-authentication. Called when the PreAuth response indicates
    /// FIDO2 method is required and includes assertion options.
    /// </summary>
    public void RegisterChallenge(string challengeId, string assertionOptionsJson, int timeoutMs, string userName, string domain)
    {
        var context = new Fido2ChallengeContext
        {
            ChallengeId = challengeId,
            UserName = userName,
            Domain = domain,
            AssertionOptionsJson = assertionOptionsJson,
            TimeoutMs = timeoutMs > 0 ? timeoutMs : 60000,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs > 0 ? timeoutMs : 60000)
        };

        _activeChallenges[challengeId] = context;

        _logger.LogDebug(
            "Registered FIDO2 challenge {ChallengeId} for {User}@{Domain}",
            challengeId, userName, domain);
    }

    /// <summary>
    /// Completes the FIDO2 assertion by forwarding the authenticator response
    /// to the Central Server for cryptographic verification.
    /// </summary>
    public async Task<Fido2CompleteResult> CompleteAssertionAsync(
        string challengeId, string assertionResponseJson, CancellationToken ct = default)
    {
        try
        {
            if (!_activeChallenges.TryRemove(challengeId, out var context))
            {
                return new Fido2CompleteResult
                {
                    Success = false,
                    Error = "Unknown or expired FIDO2 challenge"
                };
            }

            if (DateTimeOffset.UtcNow > context.ExpiresAt)
            {
                _logger.LogWarning(
                    "FIDO2 challenge {ChallengeId} expired for {User}",
                    challengeId, context.UserName);

                return new Fido2CompleteResult
                {
                    Success = false,
                    Error = "FIDO2 challenge has expired"
                };
            }

            // Perform basic local validation of the assertion response format.
            var localValidation = ValidateAssertionResponseFormat(assertionResponseJson);
            if (!localValidation.IsValid)
            {
                _logger.LogWarning(
                    "FIDO2 assertion response format invalid for {ChallengeId}: {Error}",
                    challengeId, localValidation.Error);

                return new Fido2CompleteResult
                {
                    Success = false,
                    Error = localValidation.Error
                };
            }

            // Forward the assertion to the Central Server for full verification.
            var result = await _centralServerClient.SubmitMfaAsync(challengeId, assertionResponseJson, ct);

            if (result == null)
            {
                return new Fido2CompleteResult
                {
                    Success = false,
                    Error = "Central server unavailable for FIDO2 verification"
                };
            }

            if (result.Success && !string.IsNullOrEmpty(result.SessionToken))
            {
                // Cache the session for future authentications
                _sessionCache.AddOrUpdateSession(new CachedEndpointSession
                {
                    SessionId = result.SessionToken,
                    UserName = context.UserName,
                    Domain = context.Domain,
                    Workstation = _settings.Hostname,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.SessionTtlMinutes),
                    VerifiedMethod = "FIDO2"
                });

                _logger.LogInformation(
                    "FIDO2 assertion verified for {User}@{Domain}, session {SessionId}",
                    context.UserName, context.Domain, result.SessionToken);
            }
            else
            {
                _logger.LogWarning(
                    "FIDO2 assertion failed for {User}@{Domain}: {Error}",
                    context.UserName, context.Domain, result.Error ?? "unknown");
            }

            return new Fido2CompleteResult
            {
                Success = result.Success,
                SessionToken = result.SessionToken,
                Error = result.Error
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing FIDO2 assertion for challenge {ChallengeId}", challengeId);
            return new Fido2CompleteResult
            {
                Success = false,
                Error = $"Internal error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Cleans up expired FIDO2 challenges from memory.
    /// </summary>
    public void CleanupExpiredChallenges()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _activeChallenges
            .Where(kv => kv.Value.ExpiresAt < now)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
            _activeChallenges.TryRemove(key, out _);

        if (expired.Count > 0)
            _logger.LogDebug("Cleaned up {Count} expired FIDO2 challenges", expired.Count);
    }

    public int ActiveChallengeCount => _activeChallenges.Count;

    /// <summary>
    /// Validates the format of an assertion response JSON before forwarding
    /// to the server. Checks that required fields are present.
    /// </summary>
    private static AssertionValidationResult ValidateAssertionResponseFormat(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Required WebAuthn assertion response fields
            if (!root.TryGetProperty("credentialId", out _))
                return new AssertionValidationResult(false, "Missing 'credentialId' field");

            if (!root.TryGetProperty("authenticatorData", out _))
                return new AssertionValidationResult(false, "Missing 'authenticatorData' field");

            if (!root.TryGetProperty("clientDataJSON", out _))
                return new AssertionValidationResult(false, "Missing 'clientDataJSON' field");

            if (!root.TryGetProperty("signature", out _))
                return new AssertionValidationResult(false, "Missing 'signature' field");

            return new AssertionValidationResult(true, null);
        }
        catch (JsonException)
        {
            return new AssertionValidationResult(false, "Invalid JSON in assertion response");
        }
    }

    /// <summary>
    /// Builds minimal PublicKeyCredentialRequestOptions when the server
    /// doesn't include full assertion options in the challenge metadata.
    /// </summary>
    private static string BuildMinimalAssertionOptions(MfaSrv.Protocol.AuthEvaluationResponse response)
    {
        var options = new
        {
            challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            timeout = (long)(response.TimeoutMs > 0 ? response.TimeoutMs : 60000),
            rpId = "mfasrv",
            userVerification = "preferred"
        };

        return JsonSerializer.Serialize(options, JsonOptions);
    }

    private sealed record AssertionValidationResult(bool IsValid, string? Error);
}

/// <summary>
/// Tracks an active FIDO2 assertion challenge between the Central Server
/// and the Credential Provider.
/// </summary>
public class Fido2ChallengeContext
{
    public required string ChallengeId { get; init; }
    public required string UserName { get; init; }
    public required string Domain { get; init; }
    public required string AssertionOptionsJson { get; init; }
    public int TimeoutMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public class Fido2BeginResult
{
    public bool Success { get; init; }
    public string? ChallengeId { get; init; }
    public string? AssertionOptionsJson { get; init; }
    public int TimeoutMs { get; init; }
    public string? Error { get; init; }
}

public class Fido2CompleteResult
{
    public bool Success { get; init; }
    public string? SessionToken { get; init; }
    public string? Error { get; init; }
}
