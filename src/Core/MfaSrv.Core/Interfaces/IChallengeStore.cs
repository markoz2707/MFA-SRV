namespace MfaSrv.Core.Interfaces;

/// <summary>
/// Abstraction for distributed challenge state storage.
/// Backed by Redis in production, or in-memory for development.
/// </summary>
public interface IChallengeStore
{
    Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken ct = default);
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}
