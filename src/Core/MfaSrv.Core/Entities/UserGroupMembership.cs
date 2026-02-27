namespace MfaSrv.Core.Entities;

public class UserGroupMembership
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string GroupSid { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string GroupDn { get; set; } = string.Empty;
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }
}
