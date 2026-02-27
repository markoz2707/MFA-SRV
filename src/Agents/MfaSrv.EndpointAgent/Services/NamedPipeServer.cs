using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Protocol;

namespace MfaSrv.EndpointAgent.Services;

public class NamedPipeServer : BackgroundService
{
    private readonly CentralServerClient _centralServerClient;
    private readonly EndpointFailoverManager _failoverManager;
    private readonly EndpointSessionCache _sessionCache;
    private readonly YubiKeyService _yubiKeyService;
    private readonly EndpointAgentSettings _settings;
    private readonly ILogger<NamedPipeServer> _logger;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public NamedPipeServer(
        CentralServerClient centralServerClient,
        EndpointFailoverManager failoverManager,
        EndpointSessionCache sessionCache,
        YubiKeyService yubiKeyService,
        IOptions<EndpointAgentSettings> settings,
        ILogger<NamedPipeServer> logger)
    {
        _centralServerClient = centralServerClient;
        _failoverManager = failoverManager;
        _sessionCache = sessionCache;
        _yubiKeyService = yubiKeyService;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Named pipe server starting on \\\\.\\pipe\\{PipeName}", _settings.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                var pipeServer = NamedPipeServerStreamAcl.Create(
                    _settings.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous,
                    4096, 4096,
                    pipeSecurity);

                await pipeServer.WaitForConnectionAsync(stoppingToken);

                // Handle each connection in a separate task
                _ = HandleConnectionAsync(pipeServer, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in named pipe server loop");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("Named pipe server stopped");
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_settings.PipeTimeoutMs);

                // Read the incoming message
                var buffer = new byte[4096];
                var bytesRead = await pipe.ReadAsync(buffer, cts.Token);

                if (bytesRead == 0) return;

                var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var message = JsonSerializer.Deserialize<PipeMessage>(json, ReadOptions);

                if (message == null)
                {
                    _logger.LogWarning("Received invalid message from named pipe");
                    return;
                }

                _logger.LogDebug("Pipe message received: type={Type}", message.Type);

                // Route to appropriate handler
                var responseJson = message.Type?.ToLowerInvariant() switch
                {
                    "preauth" => await HandlePreAuthAsync(message, cts.Token),
                    "submit_mfa" => await HandleSubmitMfaAsync(message, cts.Token),
                    "check_status" => await HandleCheckStatusAsync(message, cts.Token),
                    "fido2_begin" => await HandleFido2BeginAsync(message, cts.Token),
                    "fido2_complete" => await HandleFido2CompleteAsync(message, cts.Token),
                    _ => JsonSerializer.Serialize(new PipeResponse
                    {
                        Success = false,
                        Error = $"Unknown message type: {message.Type}"
                    }, WriteOptions)
                };

                // Send response
                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                await pipe.WriteAsync(responseBytes, cts.Token);
                await pipe.FlushAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Named pipe connection timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling named pipe connection - fail-open");
        }
    }

    private async Task<string> HandlePreAuthAsync(PipeMessage message, CancellationToken ct)
    {
        try
        {
            var userName = message.UserName ?? string.Empty;
            var domain = message.Domain ?? string.Empty;
            var workstation = message.Workstation ?? _settings.Hostname;

            // Check local session cache first
            var cachedSession = _sessionCache.FindSession(userName, workstation);
            if (cachedSession != null)
            {
                _logger.LogDebug("Found cached session for {User}, MFA not required", userName);
                return JsonSerializer.Serialize(new PreAuthPipeResponse
                {
                    Success = true,
                    MfaRequired = false,
                    Reason = "Cached MFA session valid"
                }, WriteOptions);
            }

            // If Central Server is unavailable, use failover logic
            if (!_failoverManager.IsCentralServerAvailable)
            {
                var failover = _failoverManager.GetFailoverDecision(userName);
                return JsonSerializer.Serialize(new PreAuthPipeResponse
                {
                    Success = true,
                    MfaRequired = !failover.Allow,
                    Reason = failover.Reason
                }, WriteOptions);
            }

            // Call Central Server
            var response = await _centralServerClient.PreAuthenticateAsync(userName, domain, workstation, ct);

            if (response == null)
            {
                // Server unreachable - apply failover
                var failover = _failoverManager.GetFailoverDecision(userName);
                return JsonSerializer.Serialize(new PreAuthPipeResponse
                {
                    Success = true,
                    MfaRequired = !failover.Allow,
                    Reason = failover.Reason
                }, WriteOptions);
            }

            var mfaRequired = response.Decision == AuthDecisionType.AuthDecisionRequireMfa;

            // If allowed without MFA, cache the session
            if (response.Decision == AuthDecisionType.AuthDecisionAllow && !string.IsNullOrEmpty(response.SessionToken))
            {
                _sessionCache.AddOrUpdateSession(new CachedEndpointSession
                {
                    SessionId = response.SessionToken,
                    UserName = userName,
                    Domain = domain,
                    Workstation = workstation,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.SessionTtlMinutes),
                    VerifiedMethod = response.RequiredMethod ?? "none"
                });
            }

            // If FIDO2 method is required, pre-register the challenge so the
            // Credential Provider can begin the assertion flow immediately.
            if (mfaRequired && string.Equals(response.RequiredMethod, "FIDO2", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(response.ChallengeId))
            {
                _yubiKeyService.RegisterChallenge(
                    response.ChallengeId,
                    response.ChallengeMetadata ?? "{}",
                    response.TimeoutMs,
                    userName,
                    domain);
            }

            return JsonSerializer.Serialize(new PreAuthPipeResponse
            {
                Success = true,
                MfaRequired = mfaRequired,
                ChallengeId = mfaRequired ? response.ChallengeId : null,
                Method = mfaRequired ? response.RequiredMethod : null,
                Reason = response.Reason,
                TimeoutMs = response.TimeoutMs
            }, WriteOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PreAuth handler - fail-open");
            return JsonSerializer.Serialize(new PreAuthPipeResponse
            {
                Success = true,
                MfaRequired = false,
                Reason = "Error during pre-authentication - fail-open"
            }, WriteOptions);
        }
    }

    private async Task<string> HandleSubmitMfaAsync(PipeMessage message, CancellationToken ct)
    {
        try
        {
            var challengeId = message.ChallengeId ?? string.Empty;
            var mfaResponse = message.Response ?? string.Empty;

            if (string.IsNullOrEmpty(challengeId))
            {
                return JsonSerializer.Serialize(new PipeResponse
                {
                    Success = false,
                    Error = "Missing challengeId"
                }, WriteOptions);
            }

            var result = await _centralServerClient.SubmitMfaAsync(challengeId, mfaResponse, ct);

            if (result == null)
            {
                return JsonSerializer.Serialize(new PipeResponse
                {
                    Success = false,
                    Error = "Central server unavailable"
                }, WriteOptions);
            }

            // If MFA was successful and we got a session token, cache it
            if (result.Success && !string.IsNullOrEmpty(result.SessionToken))
            {
                _sessionCache.AddOrUpdateSession(new CachedEndpointSession
                {
                    SessionId = result.SessionToken,
                    UserName = message.UserName ?? string.Empty,
                    Domain = message.Domain ?? string.Empty,
                    Workstation = message.Workstation ?? _settings.Hostname,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.SessionTtlMinutes),
                    VerifiedMethod = "mfa"
                });
            }

            return JsonSerializer.Serialize(new PipeResponse
            {
                Success = result.Success,
                Error = result.Error
            }, WriteOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SubmitMfa handler");
            return JsonSerializer.Serialize(new PipeResponse
            {
                Success = false,
                Error = "Internal error submitting MFA"
            }, WriteOptions);
        }
    }

    private async Task<string> HandleCheckStatusAsync(PipeMessage message, CancellationToken ct)
    {
        try
        {
            var challengeId = message.ChallengeId ?? string.Empty;

            if (string.IsNullOrEmpty(challengeId))
            {
                return JsonSerializer.Serialize(new CheckStatusPipeResponse
                {
                    Success = false,
                    Error = "Missing challengeId"
                }, WriteOptions);
            }

            var result = await _centralServerClient.CheckStatusAsync(challengeId, ct);

            if (result == null)
            {
                return JsonSerializer.Serialize(new CheckStatusPipeResponse
                {
                    Success = false,
                    Error = "Central server unavailable"
                }, WriteOptions);
            }

            var completed = result.Status == ChallengeStatusType.ChallengeStatusApproved
                         || result.Status == ChallengeStatusType.ChallengeStatusDenied
                         || result.Status == ChallengeStatusType.ChallengeStatusExpired
                         || result.Status == ChallengeStatusType.ChallengeStatusFailed;

            var approved = result.Status == ChallengeStatusType.ChallengeStatusApproved;

            return JsonSerializer.Serialize(new CheckStatusPipeResponse
            {
                Success = true,
                Status = result.Status.ToString(),
                Completed = completed,
                Approved = approved,
                Error = result.Error
            }, WriteOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CheckStatus handler");
            return JsonSerializer.Serialize(new CheckStatusPipeResponse
            {
                Success = false,
                Error = "Internal error checking status"
            }, WriteOptions);
        }
    }
    /// <summary>
    /// Handles "fido2_begin" messages from the Credential Provider.
    /// Starts a local FIDO2 assertion flow by requesting challenge options
    /// from the Central Server (or using a pre-registered challenge).
    /// </summary>
    private async Task<string> HandleFido2BeginAsync(PipeMessage message, CancellationToken ct)
    {
        try
        {
            var userName = message.UserName ?? string.Empty;
            var domain = message.Domain ?? string.Empty;
            var challengeId = message.ChallengeId ?? string.Empty;

            var result = await _yubiKeyService.BeginAssertionAsync(userName, domain, challengeId, ct);

            return JsonSerializer.Serialize(new Fido2BeginPipeResponse
            {
                Success = result.Success,
                ChallengeId = result.ChallengeId,
                AssertionOptionsJson = result.AssertionOptionsJson,
                TimeoutMs = result.TimeoutMs,
                Error = result.Error
            }, WriteOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Fido2Begin handler");
            return JsonSerializer.Serialize(new Fido2BeginPipeResponse
            {
                Success = false,
                Error = "Internal error starting FIDO2 assertion"
            }, WriteOptions);
        }
    }

    /// <summary>
    /// Handles "fido2_complete" messages from the Credential Provider.
    /// Forwards the authenticator assertion response to the Central Server
    /// for cryptographic verification.
    /// </summary>
    private async Task<string> HandleFido2CompleteAsync(PipeMessage message, CancellationToken ct)
    {
        try
        {
            var challengeId = message.ChallengeId ?? string.Empty;
            var assertionResponse = message.Response ?? string.Empty;

            if (string.IsNullOrEmpty(challengeId))
            {
                return JsonSerializer.Serialize(new PipeResponse
                {
                    Success = false,
                    Error = "Missing challengeId"
                }, WriteOptions);
            }

            if (string.IsNullOrEmpty(assertionResponse))
            {
                return JsonSerializer.Serialize(new PipeResponse
                {
                    Success = false,
                    Error = "Missing assertion response"
                }, WriteOptions);
            }

            var result = await _yubiKeyService.CompleteAssertionAsync(challengeId, assertionResponse, ct);

            return JsonSerializer.Serialize(new Fido2CompletePipeResponse
            {
                Success = result.Success,
                SessionToken = result.SessionToken,
                Error = result.Error
            }, WriteOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Fido2Complete handler");
            return JsonSerializer.Serialize(new Fido2CompletePipeResponse
            {
                Success = false,
                Error = "Internal error completing FIDO2 assertion"
            }, WriteOptions);
        }
    }
}

// --- Named pipe message DTOs ---

internal class PipeMessage
{
    public string? Type { get; set; }
    public string? UserName { get; set; }
    public string? Domain { get; set; }
    public string? Workstation { get; set; }
    public string? ChallengeId { get; set; }
    public string? Response { get; set; }
}

internal class PipeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

internal class PreAuthPipeResponse
{
    public bool Success { get; set; }
    public bool MfaRequired { get; set; }
    public string? ChallengeId { get; set; }
    public string? Method { get; set; }
    public string? Reason { get; set; }
    public int TimeoutMs { get; set; }
    public string? Error { get; set; }
}

internal class CheckStatusPipeResponse
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public bool Completed { get; set; }
    public bool Approved { get; set; }
    public string? Error { get; set; }
}

internal class Fido2BeginPipeResponse
{
    public bool Success { get; set; }
    public string? ChallengeId { get; set; }
    public string? AssertionOptionsJson { get; set; }
    public int TimeoutMs { get; set; }
    public string? Error { get; set; }
}

internal class Fido2CompletePipeResponse
{
    public bool Success { get; set; }
    public string? SessionToken { get; set; }
    public string? Error { get; set; }
}
