namespace MfaSrv.Core.Enums;

public enum AuditEventType
{
    AuthenticationAttempt,
    MfaChallengeIssued,
    MfaChallengeVerified,
    MfaChallengeFailed,
    MfaChallengeExpired,
    PolicyEvaluated,
    SessionCreated,
    SessionExpired,
    SessionRevoked,
    UserEnrolled,
    UserDisenrolled,
    PolicyCreated,
    PolicyUpdated,
    PolicyDeleted,
    AgentRegistered,
    AgentHeartbeat,
    AgentDisconnected,
    FailoverActivated,
    FailoverDeactivated,
    ConfigurationChanged
}
