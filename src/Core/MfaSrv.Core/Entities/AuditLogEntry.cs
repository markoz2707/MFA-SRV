using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class AuditLogEntry
{
    public long Id { get; set; }
    public AuditEventType EventType { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? SourceIp { get; set; }
    public string? TargetResource { get; set; }
    public string? Details { get; set; }
    public bool Success { get; set; }
    public string? AgentId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
