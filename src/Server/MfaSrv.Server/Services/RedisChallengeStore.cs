using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using MfaSrv.Core.Interfaces;

namespace MfaSrv.Server.Services;

/// <summary>
/// Distributed challenge store backed by IDistributedCache (Redis or in-memory fallback).
/// </summary>
public class RedisChallengeStore : IChallengeStore
{
    private readonly IDistributedCache _cache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RedisChallengeStore(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiry
        };
        await _cache.SetAsync(key, json, options, ct);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var bytes = await _cache.GetAsync(key, ct);
        if (bytes == null)
            return default;

        return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _cache.RemoveAsync(key, ct);
    }
}
