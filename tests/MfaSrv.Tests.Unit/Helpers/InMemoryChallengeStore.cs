using System.Collections.Concurrent;
using System.Text.Json;
using MfaSrv.Core.Interfaces;

namespace MfaSrv.Tests.Unit.Helpers;

/// <summary>
/// Simple in-memory IChallengeStore implementation for unit tests.
/// </summary>
public class InMemoryChallengeStore : IChallengeStore
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        _store[key] = json;
        return Task.CompletedTask;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_store.TryGetValue(key, out var bytes))
            return Task.FromResult(JsonSerializer.Deserialize<T>(bytes));
        return Task.FromResult(default(T));
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
