using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.EndpointAgent.Services;

public class HeartbeatService : BackgroundService
{
    private readonly CentralServerClient _centralServerClient;
    private readonly EndpointSessionCache _sessionCache;
    private readonly EndpointAgentSettings _settings;
    private readonly ILogger<HeartbeatService> _logger;

    private bool _registered;
    private int _consecutiveFailures;
    private const int MaxBackoffSeconds = 300; // 5 minutes max

    public HeartbeatService(
        CentralServerClient centralServerClient,
        EndpointSessionCache sessionCache,
        IOptions<EndpointAgentSettings> settings,
        ILogger<HeartbeatService> logger)
    {
        _centralServerClient = centralServerClient;
        _sessionCache = sessionCache;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat service starting, interval={Interval}s",
            _settings.HeartbeatIntervalSeconds);

        // Register agent on first start
        await RegisterWithRetryAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Clean up expired sessions periodically alongside heartbeat
                _sessionCache.CleanupExpired();

                var activeSessions = _sessionCache.ActiveSessionCount;
                var response = await _centralServerClient.HeartbeatAsync(activeSessions, stoppingToken);

                if (response != null)
                {
                    _consecutiveFailures = 0;
                    _logger.LogDebug("Heartbeat acknowledged, activeSessions={Count}", activeSessions);
                }
                else
                {
                    _consecutiveFailures++;
                    _logger.LogWarning("Heartbeat failed, consecutiveFailures={Count}", _consecutiveFailures);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "Error in heartbeat cycle, consecutiveFailures={Count}", _consecutiveFailures);
            }

            var delay = CalculateBackoffDelay();
            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("Heartbeat service stopping");
    }

    private async Task RegisterWithRetryAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!_registered && !ct.IsCancellationRequested)
        {
            attempt++;
            _logger.LogInformation("Attempting agent registration (attempt {Attempt})", attempt);

            try
            {
                var response = await _centralServerClient.RegisterAsync(ct);
                if (response is { Success: true })
                {
                    _registered = true;
                    _logger.LogInformation("Agent registration successful, agentId={AgentId}", response.AgentId);
                    return;
                }

                _logger.LogWarning("Registration attempt {Attempt} failed: {Error}",
                    attempt, response?.Error ?? "no response");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration attempt {Attempt} threw an exception", attempt);
            }

            // Exponential backoff: 2, 4, 8, 16, 32... capped at MaxBackoffSeconds
            var backoffSeconds = Math.Min((int)Math.Pow(2, attempt), MaxBackoffSeconds);
            _logger.LogDebug("Retrying registration in {Seconds}s", backoffSeconds);
            await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
        }
    }

    private TimeSpan CalculateBackoffDelay()
    {
        if (_consecutiveFailures == 0)
            return TimeSpan.FromSeconds(_settings.HeartbeatIntervalSeconds);

        // Exponential backoff: base interval * 2^failures, capped at MaxBackoffSeconds
        var backoffSeconds = Math.Min(
            _settings.HeartbeatIntervalSeconds * (int)Math.Pow(2, _consecutiveFailures),
            MaxBackoffSeconds);

        return TimeSpan.FromSeconds(backoffSeconds);
    }
}
