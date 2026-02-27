using Grpc.Core;
using Microsoft.Extensions.Logging;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Protocol;
using MfaSrv.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace MfaSrv.Server.GrpcServices;

public partial class MfaGrpcService : MfaService.MfaServiceBase
{
    private readonly IPolicyEngine _policyEngine;
    private readonly ISessionManager _sessionManager;
    private readonly IMfaChallengeOrchestrator _challengeOrchestrator;
    private readonly IAuditLogger _auditLogger;
    private readonly MfaSrvDbContext _db;
    private readonly ILogger<MfaGrpcService> _logger;
    private readonly Services.PolicySyncStreamService _policySyncStream;

    public MfaGrpcService(
        IPolicyEngine policyEngine,
        ISessionManager sessionManager,
        IMfaChallengeOrchestrator challengeOrchestrator,
        IAuditLogger auditLogger,
        MfaSrvDbContext db,
        ILogger<MfaGrpcService> logger,
        Services.PolicySyncStreamService policySyncStream)
    {
        _policyEngine = policyEngine;
        _sessionManager = sessionManager;
        _challengeOrchestrator = challengeOrchestrator;
        _auditLogger = auditLogger;
        _db = db;
        _logger = logger;
        _policySyncStream = policySyncStream;
    }

    public override async Task<AuthEvaluationResponse> EvaluateAuthentication(AuthEvaluationRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Auth evaluation for {User}@{Domain} from {Ip}", request.UserName, request.Domain, request.SourceIp);

        // Check for existing active session
        var user = await _db.Users.FirstOrDefaultAsync(u => u.SamAccountName == request.UserName, context.CancellationToken);
        if (user != null)
        {
            var existingSession = await _sessionManager.FindActiveSessionAsync(user.Id, request.SourceIp, context.CancellationToken);
            if (existingSession != null)
            {
                return new AuthEvaluationResponse
                {
                    Decision = AuthDecisionType.AuthDecisionAllow,
                    Reason = "Active MFA session found"
                };
            }
        }

        // Build auth context and evaluate policies
        var groups = user != null
            ? await _db.UserGroupMemberships
                .Where(m => m.UserId == user.Id)
                .Select(m => m.GroupName)
                .ToListAsync(context.CancellationToken)
            : new List<string>();

        var authContext = new AuthenticationContext
        {
            UserId = user?.Id ?? string.Empty,
            UserName = request.UserName,
            SourceIp = request.SourceIp,
            TargetResource = request.TargetResource,
            Protocol = MapProtocol(request.Protocol),
            UserGroups = groups
        };

        var result = await _policyEngine.EvaluateAsync(authContext, context.CancellationToken);

        await _auditLogger.LogAsync(
            AuditEventType.PolicyEvaluated,
            user?.Id ?? request.UserName,
            request.SourceIp,
            request.TargetResource,
            $"Decision: {result.Decision}, Policy: {result.MatchedPolicyName ?? "none"}",
            context.CancellationToken);

        return new AuthEvaluationResponse
        {
            Decision = MapDecision(result.Decision),
            MatchedPolicyId = result.MatchedPolicyId ?? string.Empty,
            Reason = result.Reason ?? string.Empty,
            RequiredMethod = result.RequiredMethod?.ToString() ?? string.Empty,
            TimeoutMs = 300000 // 5 minutes for MFA completion
        };
    }

    public override async Task<IssueChallengeResponse> IssueChallenge(IssueChallengeRequest request, ServerCallContext context)
    {
        if (!Enum.TryParse<MfaMethod>(request.Method, true, out var method))
        {
            return new IssueChallengeResponse { Success = false, Error = $"Unknown MFA method: {request.Method}" };
        }

        var challengeCtx = new ChallengeContext
        {
            UserId = request.UserId,
            EnrollmentId = string.Empty, // will be resolved by orchestrator
            SourceIp = request.SourceIp,
            TargetResource = request.TargetResource
        };

        var result = await _challengeOrchestrator.IssueChallengeAsync(request.UserId, method, challengeCtx, context.CancellationToken);

        var response = new IssueChallengeResponse
        {
            Success = result.Success,
            ChallengeId = result.ChallengeId ?? string.Empty,
            Error = result.Error ?? string.Empty,
            UserPrompt = result.UserPrompt ?? string.Empty
        };

        if (result.ExpiresAt.HasValue)
            response.ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(result.ExpiresAt.Value);

        return response;
    }

    public override async Task<VerifyChallengeResponse> VerifyChallenge(VerifyChallengeRequest request, ServerCallContext context)
    {
        var result = await _challengeOrchestrator.VerifyChallengeAsync(request.ChallengeId, request.Response, context.CancellationToken);

        var response = new VerifyChallengeResponse
        {
            Success = result.Success,
            Error = result.Error ?? string.Empty,
            ShouldLockout = result.ShouldLockout
        };

        // If verification succeeded, create a session
        if (result.Success)
        {
            var challenge = await _db.MfaChallenges.FindAsync(new object[] { request.ChallengeId }, context.CancellationToken);
            if (challenge != null)
            {
                var session = await _sessionManager.CreateSessionAsync(
                    challenge.UserId, challenge.SourceIp ?? "", challenge.TargetResource ?? "",
                    ct: context.CancellationToken);

                response.SessionToken = Convert.ToBase64String(session.TokenHash); // simplified - in production use actual token
            }
        }

        return response;
    }

    public override async Task<CheckChallengeStatusResponse> CheckChallengeStatus(CheckChallengeStatusRequest request, ServerCallContext context)
    {
        var status = await _challengeOrchestrator.CheckChallengeStatusAsync(request.ChallengeId, context.CancellationToken);

        return new CheckChallengeStatusResponse
        {
            Status = MapChallengeStatus(status.Status),
            Error = status.Error ?? string.Empty
        };
    }

    public override async Task<ValidateSessionResponse> ValidateSession(ValidateSessionRequest request, ServerCallContext context)
    {
        var session = await _sessionManager.ValidateSessionAsync(request.SessionToken, context.CancellationToken);

        var response = new ValidateSessionResponse
        {
            Valid = session != null
        };

        if (session != null)
        {
            response.UserId = session.UserId;
            response.ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(session.ExpiresAt);
        }

        return response;
    }

    public override async Task<RegisterAgentResponse> RegisterAgent(RegisterAgentRequest request, ServerCallContext context)
    {
        var registration = new Core.Entities.AgentRegistration
        {
            AgentType = request.AgentType == AgentTypeEnum.AgentTypeDc ? AgentType.DcAgent : AgentType.EndpointAgent,
            Hostname = request.Hostname,
            IpAddress = request.IpAddress,
            CertificateThumbprint = request.CertificateThumbprint,
            Version = request.Version,
            Status = Core.Enums.AgentStatus.Online,
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };

        _db.AgentRegistrations.Add(registration);
        await _db.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("Registered agent {Hostname} ({Type}) as {AgentId}", request.Hostname, registration.AgentType, registration.Id);

        return new RegisterAgentResponse
        {
            AgentId = registration.Id,
            Success = true
        };
    }

    public override async Task<HeartbeatResponse> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        var agent = await _db.AgentRegistrations.FindAsync(new object[] { request.AgentId }, context.CancellationToken);
        if (agent != null)
        {
            agent.Status = Core.Enums.AgentStatus.Online;
            agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(context.CancellationToken);
        }

        return new HeartbeatResponse { Acknowledged = true };
    }

    private static AuthProtocol MapProtocol(AuthProtocolType proto) => proto switch
    {
        AuthProtocolType.AuthProtocolKerberos => AuthProtocol.Kerberos,
        AuthProtocolType.AuthProtocolNtlm => AuthProtocol.Ntlm,
        AuthProtocolType.AuthProtocolLdap => AuthProtocol.Ldap,
        AuthProtocolType.AuthProtocolRadius => AuthProtocol.Radius,
        _ => AuthProtocol.Unknown
    };

    private static AuthDecisionType MapDecision(AuthDecision decision) => decision switch
    {
        AuthDecision.Allow => AuthDecisionType.AuthDecisionAllow,
        AuthDecision.RequireMfa => AuthDecisionType.AuthDecisionRequireMfa,
        AuthDecision.Deny => AuthDecisionType.AuthDecisionDeny,
        AuthDecision.Pending => AuthDecisionType.AuthDecisionPending,
        _ => AuthDecisionType.AuthDecisionAllow
    };

    private static ChallengeStatusType MapChallengeStatus(ChallengeStatus status) => status switch
    {
        ChallengeStatus.Issued => ChallengeStatusType.ChallengeStatusIssued,
        ChallengeStatus.Approved => ChallengeStatusType.ChallengeStatusApproved,
        ChallengeStatus.Denied => ChallengeStatusType.ChallengeStatusDenied,
        ChallengeStatus.Expired => ChallengeStatusType.ChallengeStatusExpired,
        ChallengeStatus.Failed => ChallengeStatusType.ChallengeStatusFailed,
        _ => ChallengeStatusType.ChallengeStatusFailed
    };
}
