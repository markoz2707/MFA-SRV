using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ObjectGuid { get; set; } = string.Empty;
    public string SamAccountName { get; set; } = string.Empty;
    public string UserPrincipalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string DistinguishedName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool MfaEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? LastAuthAt { get; set; }

    public List<MfaEnrollment> Enrollments { get; set; } = new();
    public List<UserGroupMembership> GroupMemberships { get; set; } = new();
}
