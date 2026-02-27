using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Interfaces;

public interface IAuditLogger
{
    Task LogAsync(AuditEventType eventType, string userId, string? sourceIp, string? targetResource, string? details, CancellationToken ct = default);
}
