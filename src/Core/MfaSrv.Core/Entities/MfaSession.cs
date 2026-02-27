using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class MfaSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();
    public string SourceIp { get; set; } = string.Empty;
    public string? TargetResource { get; set; }
    public MfaMethod VerifiedMethod { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public string? DcAgentId { get; set; }

    public User? User { get; set; }
}
