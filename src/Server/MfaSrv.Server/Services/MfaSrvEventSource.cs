using System.Diagnostics.Tracing;

namespace MfaSrv.Server.Services;

/// <summary>
/// ETW (Event Tracing for Windows) event source for MfaSrv audit logging.
/// Enables real-time monitoring and integration with Windows Event Viewer,
/// PerfView, and other ETW consumers.
///
/// Provider name: MfaSrv-Server
///
/// To collect events:
///   logman create trace MfaSrvTrace -p "MfaSrv-Server" -o mfasrv.etl
///   logman start MfaSrvTrace
///   logman stop MfaSrvTrace
///
/// Or with dotnet-trace:
///   dotnet-trace collect --providers MfaSrv-Server
/// </summary>
[EventSource(Name = "MfaSrv-Server")]
public sealed class MfaSrvEventSource : EventSource
{
    public static readonly MfaSrvEventSource Instance = new();

    private MfaSrvEventSource() : base(EventSourceSettings.EtwSelfDescribingEventFormat) { }

    // ── Authentication Events ──────────────────────────────────────────

    [Event(1, Level = EventLevel.Informational, Channel = EventChannel.Operational,
        Message = "Authentication evaluated: User={0}, Decision={1}, Policy={2}")]
    public void AuthenticationEvaluated(string userName, string decision, string policy)
    {
        if (IsEnabled()) WriteEvent(1, userName, decision, policy);
    }

    [Event(7, Level = EventLevel.Warning, Channel = EventChannel.Operational,
        Message = "Authentication denied: User={0}, Reason={1}")]
    public void AuthenticationDenied(string userName, string reason)
    {
        if (IsEnabled()) WriteEvent(7, userName, reason);
    }

    // ── MFA Challenge Events ───────────────────────────────────────────

    [Event(2, Level = EventLevel.Informational, Channel = EventChannel.Operational,
        Message = "MFA challenge issued: User={0}, Method={1}, ChallengeId={2}")]
    public void MfaChallengeIssued(string userName, string method, string challengeId)
    {
        if (IsEnabled()) WriteEvent(2, userName, method, challengeId);
    }

    [Event(3, Level = EventLevel.Informational, Channel = EventChannel.Operational,
        Message = "MFA challenge verified: User={0}, ChallengeId={1}, Success={2}")]
    public void MfaChallengeVerified(string userName, string challengeId, string success)
    {
        if (IsEnabled()) WriteEvent(3, userName, challengeId, success);
    }

    // ── Session Events ─────────────────────────────────────────────────

    [Event(4, Level = EventLevel.Informational, Channel = EventChannel.Operational,
        Message = "Session created: User={0}, SessionId={1}, ExpiresAt={2}")]
    public void SessionCreated(string userName, string sessionId, string expiresAt)
    {
        if (IsEnabled()) WriteEvent(4, userName, sessionId, expiresAt);
    }

    [Event(5, Level = EventLevel.Warning, Channel = EventChannel.Operational,
        Message = "Session revoked: SessionId={0}")]
    public void SessionRevoked(string sessionId)
    {
        if (IsEnabled()) WriteEvent(5, sessionId);
    }

    // ── Agent Events ───────────────────────────────────────────────────

    [Event(6, Level = EventLevel.Informational, Channel = EventChannel.Operational,
        Message = "Agent registered: AgentId={0}, Hostname={1}, Type={2}")]
    public void AgentRegistered(string agentId, string hostname, string agentType)
    {
        if (IsEnabled()) WriteEvent(6, agentId, hostname, agentType);
    }

    [Event(8, Level = EventLevel.Warning, Channel = EventChannel.Operational,
        Message = "Failover activated: AgentId={0}, Mode={1}")]
    public void FailoverActivated(string agentId, string mode)
    {
        if (IsEnabled()) WriteEvent(8, agentId, mode);
    }

    // ── Policy Events ──────────────────────────────────────────────────

    [Event(9, Level = EventLevel.Informational, Channel = EventChannel.Operational,
        Message = "Policy changed: PolicyId={0}, Action={1}")]
    public void PolicyChanged(string policyId, string action)
    {
        if (IsEnabled()) WriteEvent(9, policyId, action);
    }

    // ── Enrollment Events ──────────────────────────────────────────────

    [Event(10, Level = EventLevel.Informational, Channel = EventChannel.Operational,
        Message = "User enrollment: UserId={0}, Method={1}, Action={2}")]
    public void UserEnrollment(string userId, string method, string action)
    {
        if (IsEnabled()) WriteEvent(10, userId, method, action);
    }
}
