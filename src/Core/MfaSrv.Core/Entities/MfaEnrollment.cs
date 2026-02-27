using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class MfaEnrollment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public MfaMethod Method { get; set; }
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Pending;
    public byte[] EncryptedSecret { get; set; } = Array.Empty<byte>();
    public byte[] SecretNonce { get; set; } = Array.Empty<byte>();
    public string? DeviceIdentifier { get; set; }
    public string? FriendlyName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public User? User { get; set; }
}
