using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.EndpointAgent.Services;

public class EndpointFailoverManager
{
    private readonly EndpointAgentSettings _settings;
    private readonly EndpointSessionCache _sessionCache;
    private readonly ILogger<EndpointFailoverManager> _logger;
    private volatile bool _isCentralServerAvailable;
    private DateTimeOffset _lastSuccessfulContact = DateTimeOffset.MinValue;

    public EndpointFailoverManager(
        IOptions<EndpointAgentSettings> settings,
        EndpointSessionCache sessionCache,
        ILogger<EndpointFailoverManager> logger)
    {
        _settings = settings.Value;
        _sessionCache = sessionCache;
        _logger = logger;
    }

    public bool IsCentralServerAvailable => _isCentralServerAvailable;
    public DateTimeOffset LastSuccessfulContact => _lastSuccessfulContact;

    public void MarkServerAvailable()
    {
        if (!_isCentralServerAvailable)
        {
            _logger.LogInformation("Central server connection restored");
            _isCentralServerAvailable = true;
        }
        _lastSuccessfulContact = DateTimeOffset.UtcNow;
    }

    public void MarkServerUnavailable()
    {
        if (_isCentralServerAvailable)
        {
            _logger.LogWarning("Central server connection lost - entering degraded mode");
            _isCentralServerAvailable = false;
        }
    }

    /// <summary>
    /// When the Central Server is unavailable, determines whether to allow or deny
    /// the authentication based on the configured failover mode and local session cache.
    /// </summary>
    public FailoverDecision GetFailoverDecision(string userName)
    {
        var mode = Enum.TryParse<FailoverModeType>(_settings.FailoverMode, ignoreCase: true, out var parsed)
            ? parsed
            : FailoverModeType.FailOpen;

        switch (mode)
        {
            case FailoverModeType.FailOpen:
                _logger.LogWarning("Fail-open: allowing {UserName} while central server is unavailable", userName);
                return new FailoverDecision { Allow = true, Reason = "Fail-open: central server unavailable" };

            case FailoverModeType.FailClose:
                _logger.LogWarning("Fail-close: denying {UserName} while central server is unavailable", userName);
                return new FailoverDecision { Allow = false, Reason = "Fail-close: central server unavailable" };

            case FailoverModeType.CachedOnly:
                var session = _sessionCache.FindSession(userName);
                if (session != null)
                {
                    _logger.LogDebug("Cached session found for {UserName} in cached-only mode", userName);
                    return new FailoverDecision { Allow = true, Reason = "Cached session valid (cached-only mode)" };
                }
                _logger.LogWarning("No cached session for {UserName} in cached-only mode", userName);
                return new FailoverDecision { Allow = false, Reason = "No cached session (cached-only mode)" };

            default:
                return new FailoverDecision { Allow = true, Reason = "Default fail-open" };
        }
    }
}

public class FailoverDecision
{
    public bool Allow { get; init; }
    public required string Reason { get; init; }
}

public enum FailoverModeType
{
    FailOpen,
    FailClose,
    CachedOnly
}
