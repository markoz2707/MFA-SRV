using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MfaSrv.Core.Enums;

namespace MfaSrv.DcAgent.Services;

public class CachedPolicy
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public required string PolicyJson { get; init; }
    public FailoverMode FailoverMode { get; init; }
    public int Priority { get; init; }
    public bool IsEnabled { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public class PolicyCacheService
{
    private readonly ConcurrentDictionary<string, CachedPolicy> _policies = new();
    private readonly ILogger<PolicyCacheService> _logger;
    private readonly SqliteCacheStore _store;
    private FailoverMode _defaultFailoverMode = FailoverMode.FailOpen;

    public PolicyCacheService(ILogger<PolicyCacheService> logger, SqliteCacheStore store)
    {
        _logger = logger;
        _store = store;
    }

    /// <summary>
    /// Loads all persisted policies from SQLite into the in-memory cache.
    /// Call once at startup after SqliteCacheStore.InitializeAsync().
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var policies = await _store.LoadAllPoliciesAsync();
            foreach (var policy in policies)
            {
                _policies.TryAdd(policy.PolicyId, policy);
            }

            // Restore last sync time from metadata (set backing field directly to avoid re-persisting)
            var lastSync = await _store.GetMetadataAsync("policy_last_sync_time");
            if (lastSync is not null && DateTimeOffset.TryParse(lastSync, out var syncTime))
            {
                _lastSyncTime = syncTime;
            }

            _logger.LogInformation(
                "Policy cache initialized from SQLite: {Count} policies loaded, last sync {LastSync}",
                policies.Count, LastSyncTime?.ToString("O") ?? "never");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policies from SQLite cache; starting with empty cache");
        }
    }

    public void UpdatePolicy(CachedPolicy policy)
    {
        _policies.AddOrUpdate(policy.PolicyId, policy, (_, _) => policy);
        _logger.LogDebug("Updated cached policy {PolicyId}: {Name}", policy.PolicyId, policy.Name);

        // Fire-and-forget persistence to avoid blocking the hot path
        _ = PersistSavePolicyAsync(policy);
    }

    public void RemovePolicy(string policyId)
    {
        _policies.TryRemove(policyId, out _);
        _logger.LogDebug("Removed cached policy {PolicyId}", policyId);

        // Fire-and-forget persistence
        _ = PersistRemovePolicyAsync(policyId);
    }

    public IReadOnlyList<CachedPolicy> GetPolicies()
    {
        return _policies.Values
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Priority)
            .ToList();
    }

    public FailoverMode GetEffectiveFailoverMode(string? userName = null)
    {
        // Check if any policy explicitly sets a failover mode for this context
        var policies = GetPolicies();
        if (policies.Count > 0)
        {
            return policies.First().FailoverMode;
        }

        return _defaultFailoverMode;
    }

    public void SetDefaultFailoverMode(FailoverMode mode)
    {
        _defaultFailoverMode = mode;
    }

    public int PolicyCount => _policies.Count;

    private DateTimeOffset? _lastSyncTime;
    public DateTimeOffset? LastSyncTime
    {
        get => _lastSyncTime;
        set
        {
            _lastSyncTime = value;
            if (value.HasValue)
            {
                // Persist sync time to metadata
                _ = PersistMetadataAsync("policy_last_sync_time", value.Value.ToString("O"));
            }
        }
    }

    // ─── Private persistence helpers (fire-and-forget) ───────────────────

    private async Task PersistSavePolicyAsync(CachedPolicy policy)
    {
        try
        {
            await _store.SavePolicyAsync(policy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist policy {PolicyId} to SQLite", policy.PolicyId);
        }
    }

    private async Task PersistRemovePolicyAsync(string policyId)
    {
        try
        {
            await _store.RemovePolicyAsync(policyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove policy {PolicyId} from SQLite", policyId);
        }
    }

    private async Task PersistMetadataAsync(string key, string value)
    {
        try
        {
            await _store.SetMetadataAsync(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist metadata key {Key} to SQLite", key);
        }
    }
}
