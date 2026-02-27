using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Protocol;

namespace MfaSrv.DcAgent.Services;

public class CentralServerClient : BackgroundService
{
    private readonly FailoverManager _failoverManager;
    private readonly PolicyCacheService _policyCache;
    private readonly DcAgentSettings _settings;
    private readonly ILogger<CentralServerClient> _logger;

    public CentralServerClient(
        FailoverManager failoverManager,
        PolicyCacheService policyCache,
        IOptions<DcAgentSettings> settings,
        ILogger<CentralServerClient> logger)
    {
        _failoverManager = failoverManager;
        _policyCache = policyCache;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Central server client starting, connecting to {Url}", _settings.CentralServerUrl);

        // Register agent on startup
        await RegisterAgentAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat cycle");
                _failoverManager.MarkServerUnavailable();
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.HeartbeatIntervalSeconds), stoppingToken);
        }
    }

    private async Task RegisterAgentAsync(CancellationToken ct)
    {
        try
        {
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(_settings.CentralServerUrl);
            var client = new MfaService.MfaServiceClient(channel);

            var response = await client.RegisterAgentAsync(new RegisterAgentRequest
            {
                Hostname = Environment.MachineName,
                AgentType = AgentTypeEnum.AgentTypeDc,
                IpAddress = GetLocalIpAddress(),
                Version = typeof(CentralServerClient).Assembly.GetName().Version?.ToString() ?? "1.0.0"
            }, cancellationToken: ct);

            if (response.Success)
            {
                _logger.LogInformation("Registered as agent {AgentId}", response.AgentId);
                _failoverManager.MarkServerAvailable();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register with central server");
            _failoverManager.MarkServerUnavailable();
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(_settings.CentralServerUrl);
        var client = new MfaService.MfaServiceClient(channel);

        var response = await client.HeartbeatAsync(new HeartbeatRequest
        {
            AgentId = _settings.AgentId
        }, cancellationToken: ct);

        _failoverManager.MarkServerAvailable();

        if (response.ForcePolicySync)
        {
            _logger.LogInformation("Server requested policy sync");
            // Policy sync via streaming would be triggered here
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
