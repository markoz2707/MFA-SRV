using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Protocol;

namespace MfaSrv.DcAgent.Services;

public class FailoverManager
{
    private readonly DcAgentSettings _settings;
    private readonly ILogger<FailoverManager> _logger;
    private volatile bool _isCentralServerAvailable;
    private DateTimeOffset _lastSuccessfulContact = DateTimeOffset.MinValue;

    public FailoverManager(IOptions<DcAgentSettings> settings, ILogger<FailoverManager> logger)
    {
        _settings = settings.Value;
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

    public async Task<AuthResponseMessage?> EvaluateViaCentralServerAsync(AuthQueryMessage query, CancellationToken ct)
    {
        try
        {
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(_settings.CentralServerUrl);
            var client = new MfaService.MfaServiceClient(channel);

            var request = new AuthEvaluationRequest
            {
                UserName = query.UserName,
                Domain = query.Domain,
                SourceIp = query.SourceIp ?? string.Empty,
                Protocol = MapProtocol(query.Protocol),
                AgentId = _settings.AgentId
            };

            var response = await client.EvaluateAuthenticationAsync(request, cancellationToken: ct);

            MarkServerAvailable();

            return new AuthResponseMessage
            {
                Decision = MapDecision(response.Decision),
                SessionToken = response.SessionToken,
                ChallengeId = response.ChallengeId,
                Reason = response.Reason,
                TimeoutMs = response.TimeoutMs
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to contact central server for auth evaluation");
            MarkServerUnavailable();
            return null;
        }
    }

    private static AuthProtocolType MapProtocol(AuthProtocol protocol) => protocol switch
    {
        AuthProtocol.Kerberos => AuthProtocolType.AuthProtocolKerberos,
        AuthProtocol.Ntlm => AuthProtocolType.AuthProtocolNtlm,
        AuthProtocol.Ldap => AuthProtocolType.AuthProtocolLdap,
        AuthProtocol.Radius => AuthProtocolType.AuthProtocolRadius,
        _ => AuthProtocolType.AuthProtocolUnknown
    };

    private static AuthDecision MapDecision(AuthDecisionType decision) => decision switch
    {
        AuthDecisionType.AuthDecisionAllow => AuthDecision.Allow,
        AuthDecisionType.AuthDecisionRequireMfa => AuthDecision.RequireMfa,
        AuthDecisionType.AuthDecisionDeny => AuthDecision.Deny,
        AuthDecisionType.AuthDecisionPending => AuthDecision.Pending,
        _ => AuthDecision.Allow
    };
}
