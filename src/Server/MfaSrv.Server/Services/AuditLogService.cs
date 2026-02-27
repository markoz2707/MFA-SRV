using Microsoft.Extensions.Logging;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Services;

public class AuditLogService : IAuditLogger
{
    private readonly MfaSrvDbContext _db;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(MfaSrvDbContext db, ILogger<AuditLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(AuditEventType eventType, string userId, string? sourceIp, string? targetResource, string? details, CancellationToken ct = default)
    {
        var isSuccess = eventType is AuditEventType.MfaChallengeVerified
            or AuditEventType.SessionCreated
            or AuditEventType.UserEnrolled
            or AuditEventType.AgentRegistered;

        var entry = new AuditLogEntry
        {
            EventType = eventType,
            UserId = userId,
            SourceIp = sourceIp,
            TargetResource = targetResource,
            Details = details,
            Success = isSuccess,
            Timestamp = DateTimeOffset.UtcNow
        };

        _db.AuditLog.Add(entry);
        await _db.SaveChangesAsync(ct);

        // Fire ETW event for real-time Windows event tracing
        EmitEtwEvent(eventType, userId, sourceIp, targetResource, details, isSuccess);

        _logger.LogDebug("Audit: {EventType} for user {UserId} from {SourceIp}", eventType, userId, sourceIp);
    }

    /// <summary>
    /// Emits the corresponding ETW event based on the audit event type.
    /// ETW events are fire-and-forget and never throw; failures are silently ignored
    /// to avoid disrupting the main audit pipeline.
    /// </summary>
    private static void EmitEtwEvent(AuditEventType eventType, string userId, string? sourceIp, string? targetResource, string? details, bool success)
    {
        try
        {
            var etw = MfaSrvEventSource.Instance;

            switch (eventType)
            {
                case AuditEventType.AuthenticationAttempt:
                case AuditEventType.PolicyEvaluated:
                    etw.AuthenticationEvaluated(
                        userId,
                        success ? "Allow" : "Evaluated",
                        details ?? string.Empty);
                    break;

                case AuditEventType.MfaChallengeIssued:
                    etw.MfaChallengeIssued(
                        userId,
                        targetResource ?? string.Empty,
                        details ?? string.Empty);
                    break;

                case AuditEventType.MfaChallengeVerified:
                    etw.MfaChallengeVerified(
                        userId,
                        details ?? string.Empty,
                        "true");
                    break;

                case AuditEventType.MfaChallengeFailed:
                    etw.MfaChallengeVerified(
                        userId,
                        details ?? string.Empty,
                        "false");
                    break;

                case AuditEventType.SessionCreated:
                    etw.SessionCreated(
                        userId,
                        details ?? string.Empty,
                        targetResource ?? string.Empty);
                    break;

                case AuditEventType.SessionRevoked:
                    etw.SessionRevoked(details ?? string.Empty);
                    break;

                case AuditEventType.AgentRegistered:
                    etw.AgentRegistered(
                        userId,
                        sourceIp ?? string.Empty,
                        details ?? string.Empty);
                    break;

                case AuditEventType.FailoverActivated:
                    etw.FailoverActivated(
                        userId,
                        details ?? string.Empty);
                    break;

                case AuditEventType.PolicyCreated:
                    etw.PolicyChanged(userId, "Created");
                    break;

                case AuditEventType.PolicyUpdated:
                    etw.PolicyChanged(userId, "Updated");
                    break;

                case AuditEventType.PolicyDeleted:
                    etw.PolicyChanged(userId, "Deleted");
                    break;

                case AuditEventType.UserEnrolled:
                    etw.UserEnrollment(
                        userId,
                        details ?? string.Empty,
                        "Enrolled");
                    break;

                case AuditEventType.UserDisenrolled:
                    etw.UserEnrollment(
                        userId,
                        details ?? string.Empty,
                        "Disenrolled");
                    break;

                // Events without a dedicated ETW event are covered by the structured log above
                default:
                    break;
            }
        }
        catch
        {
            // ETW emission must never fail the main audit path.
            // Any errors here (e.g., ETW session full) are silently absorbed.
        }
    }
}
