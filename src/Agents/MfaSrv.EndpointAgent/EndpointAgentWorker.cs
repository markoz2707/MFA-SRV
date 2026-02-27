using Microsoft.Extensions.Options;

namespace MfaSrv.EndpointAgent;

public class EndpointAgentWorker : BackgroundService
{
    private readonly EndpointAgentSettings _settings;
    private readonly ILogger<EndpointAgentWorker> _logger;

    public EndpointAgentWorker(
        IOptions<EndpointAgentSettings> settings,
        ILogger<EndpointAgentWorker> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MfaSrv Endpoint Agent started on {Hostname}, connecting to {ServerUrl}",
            _settings.Hostname,
            _settings.CentralServerUrl);

        _logger.LogInformation(
            "Configuration: pipe={PipeName}, heartbeat={HeartbeatInterval}s, sessionTtl={SessionTtl}m, failover={FailoverMode}",
            _settings.PipeName,
            _settings.HeartbeatIntervalSeconds,
            _settings.SessionTtlMinutes,
            _settings.FailoverMode);

        try
        {
            // The real work is performed by hosted services:
            // - NamedPipeServer: listens for Credential Provider connections
            // - HeartbeatService: sends heartbeats and registers with Central Server
            // This worker simply keeps the host alive and logs lifecycle events.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }

        _logger.LogInformation("MfaSrv Endpoint Agent stopping");
    }
}
