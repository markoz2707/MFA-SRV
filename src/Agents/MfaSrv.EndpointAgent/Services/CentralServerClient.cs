using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Protocol;

namespace MfaSrv.EndpointAgent.Services;

public class CentralServerClient
{
    private readonly EndpointAgentSettings _settings;
    private readonly EndpointFailoverManager _failoverManager;
    private readonly ILogger<CentralServerClient> _logger;

    public CentralServerClient(
        IOptions<EndpointAgentSettings> settings,
        EndpointFailoverManager failoverManager,
        ILogger<CentralServerClient> logger)
    {
        _settings = settings.Value;
        _failoverManager = failoverManager;
        _logger = logger;
    }

    /// <summary>
    /// Calls EvaluateAuthentication on the Central Server to determine if MFA is required.
    /// Returns the auth evaluation response, or null if the server is unreachable.
    /// </summary>
    public async Task<AuthEvaluationResponse?> PreAuthenticateAsync(
        string userName, string domain, string workstation, CancellationToken ct = default)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(_settings.CentralServerUrl);
            var client = new MfaService.MfaServiceClient(channel);

            var request = new AuthEvaluationRequest
            {
                UserName = userName,
                Domain = domain,
                SourceIp = workstation,
                AgentId = _settings.AgentId
            };

            var response = await client.EvaluateAuthenticationAsync(request, cancellationToken: ct);

            _failoverManager.MarkServerAvailable();
            _logger.LogDebug("PreAuth for {User}@{Domain}: decision={Decision}",
                userName, domain, response.Decision);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pre-authenticate {User}@{Domain} via central server",
                userName, domain);
            _failoverManager.MarkServerUnavailable();
            return null;
        }
    }

    /// <summary>
    /// Calls VerifyChallenge on the Central Server to submit an MFA response.
    /// </summary>
    public async Task<VerifyChallengeResponse?> SubmitMfaAsync(
        string challengeId, string response, CancellationToken ct = default)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(_settings.CentralServerUrl);
            var client = new MfaService.MfaServiceClient(channel);

            var request = new VerifyChallengeRequest
            {
                ChallengeId = challengeId,
                Response = response
            };

            var result = await client.VerifyChallengeAsync(request, cancellationToken: ct);

            _failoverManager.MarkServerAvailable();
            _logger.LogDebug("SubmitMfa for challenge {ChallengeId}: success={Success}",
                challengeId, result.Success);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit MFA for challenge {ChallengeId}", challengeId);
            _failoverManager.MarkServerUnavailable();
            return null;
        }
    }

    /// <summary>
    /// Calls CheckChallengeStatus on the Central Server to poll the current MFA challenge status.
    /// </summary>
    public async Task<CheckChallengeStatusResponse?> CheckStatusAsync(
        string challengeId, CancellationToken ct = default)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(_settings.CentralServerUrl);
            var client = new MfaService.MfaServiceClient(channel);

            var request = new CheckChallengeStatusRequest
            {
                ChallengeId = challengeId
            };

            var result = await client.CheckChallengeStatusAsync(request, cancellationToken: ct);

            _failoverManager.MarkServerAvailable();
            _logger.LogDebug("CheckStatus for challenge {ChallengeId}: status={Status}",
                challengeId, result.Status);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check status for challenge {ChallengeId}", challengeId);
            _failoverManager.MarkServerUnavailable();
            return null;
        }
    }

    /// <summary>
    /// Registers this Endpoint Agent with the Central Server.
    /// </summary>
    public async Task<RegisterAgentResponse?> RegisterAsync(CancellationToken ct = default)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(_settings.CentralServerUrl);
            var client = new MfaService.MfaServiceClient(channel);

            var request = new RegisterAgentRequest
            {
                Hostname = _settings.Hostname,
                AgentType = AgentTypeEnum.AgentTypeEndpoint,
                IpAddress = GetLocalIpAddress(),
                Version = typeof(CentralServerClient).Assembly.GetName().Version?.ToString() ?? "1.0.0"
            };

            var response = await client.RegisterAgentAsync(request, cancellationToken: ct);

            if (response.Success)
            {
                _failoverManager.MarkServerAvailable();
                _logger.LogInformation("Registered as agent {AgentId}", response.AgentId);
            }
            else
            {
                _logger.LogWarning("Agent registration failed: {Error}", response.Error);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register with central server");
            _failoverManager.MarkServerUnavailable();
            return null;
        }
    }

    /// <summary>
    /// Sends a heartbeat to the Central Server with agent status information.
    /// </summary>
    public async Task<HeartbeatResponse?> HeartbeatAsync(
        int activeSessions, CancellationToken ct = default)
    {
        try
        {
            using var channel = GrpcChannel.ForAddress(_settings.CentralServerUrl);
            var client = new MfaService.MfaServiceClient(channel);

            var request = new HeartbeatRequest
            {
                AgentId = _settings.AgentId,
                ActiveSessions = activeSessions
            };

            var response = await client.HeartbeatAsync(request, cancellationToken: ct);

            _failoverManager.MarkServerAvailable();

            if (response.ForcePolicySync)
            {
                _logger.LogInformation("Central server requested policy sync");
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Heartbeat to central server failed");
            _failoverManager.MarkServerUnavailable();
            return null;
        }
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }
}
