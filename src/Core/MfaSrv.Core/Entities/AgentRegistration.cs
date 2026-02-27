using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class AgentRegistration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public AgentType AgentType { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Offline;
    public string? CertificateThumbprint { get; set; }
    public string? Version { get; set; }
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastHeartbeatAt { get; set; }
}
