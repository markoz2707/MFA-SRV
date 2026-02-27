namespace MfaSrv.Core.Interfaces;

public interface IUserSyncService
{
    Task SyncUsersAsync(CancellationToken ct = default);
    Task SyncGroupsAsync(CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}
