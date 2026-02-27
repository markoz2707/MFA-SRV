using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Protocol;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;

namespace MfaSrv.DcAgent.Services;

/// <summary>
/// Background service that maintains a server-streaming gRPC connection
/// to the central server for real-time policy synchronization.
/// Automatically reconnects with exponential backoff on disconnection.
/// </summary>
public class PolicySyncClient : BackgroundService
{
    private readonly PolicyCacheService _policyCache;
    private readonly FailoverManager _failoverManager;
    private readonly DcAgentSettings _settings;
    private readonly ILogger<PolicySyncClient> _logger;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(2);

    public PolicySyncClient(
        PolicyCacheService policyCache,
        FailoverManager failoverManager,
        IOptions<DcAgentSettings> settings,
        ILogger<PolicySyncClient> logger)
    {
        _policyCache = policyCache;
        _failoverManager = failoverManager;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Policy sync client starting for agent {AgentId}", _settings.AgentId);

        var retryDelay = InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StreamPoliciesAsync(stoppingToken);
                // If the stream completed gracefully, reset the retry delay
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Policy sync client shutting down");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Policy sync stream disconnected, retrying in {RetryDelay}s",
                    retryDelay.TotalSeconds);

                _failoverManager.MarkServerUnavailable();

                try
                {
                    await Task.Delay(retryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Exponential backoff with cap
                retryDelay = TimeSpan.FromTicks(Math.Min(
                    retryDelay.Ticks * 2,
                    MaxRetryDelay.Ticks));
            }
        }
    }

    private async Task StreamPoliciesAsync(CancellationToken ct)
    {
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(_settings.CentralServerUrl);
        var client = new MfaService.MfaServiceClient(channel);

        var request = new SyncPoliciesRequest
        {
            AgentId = _settings.AgentId,
            LastSync = _policyCache.LastSyncTime.HasValue
                ? Timestamp.FromDateTimeOffset(_policyCache.LastSyncTime.Value)
                : Timestamp.FromDateTimeOffset(DateTimeOffset.MinValue)
        };

        _logger.LogInformation(
            "Opening policy sync stream to {ServerUrl} (last_sync={LastSync})",
            _settings.CentralServerUrl,
            _policyCache.LastSyncTime);

        using var stream = client.SyncPolicies(request, cancellationToken: ct);

        _failoverManager.MarkServerAvailable();
        _logger.LogInformation("Connected to policy sync stream");

        var updateCount = 0;

        await foreach (var update in stream.ResponseStream.ReadAllAsync(ct))
        {
            if (update.Deleted)
            {
                _policyCache.RemovePolicy(update.PolicyId);
                _logger.LogInformation("Policy {PolicyId} removed via sync stream", update.PolicyId);
            }
            else
            {
                var policyName = ExtractPolicyName(update.PolicyJson, update.PolicyId);

                _policyCache.UpdatePolicy(new CachedPolicy
                {
                    PolicyId = update.PolicyId,
                    Name = policyName,
                    PolicyJson = update.PolicyJson,
                    IsEnabled = true,
                    UpdatedAt = update.UpdatedAt.ToDateTimeOffset()
                });

                _logger.LogInformation(
                    "Policy {PolicyId} ({PolicyName}) updated via sync stream",
                    update.PolicyId, policyName);
            }

            _policyCache.LastSyncTime = DateTimeOffset.UtcNow;
            updateCount++;

            if (updateCount % 100 == 0)
            {
                _logger.LogDebug(
                    "Processed {Count} policy updates, cache has {CacheCount} policies",
                    updateCount, _policyCache.PolicyCount);
            }
        }

        _logger.LogInformation(
            "Policy sync stream completed after {Count} updates",
            updateCount);
    }

    /// <summary>
    /// Attempts to extract the policy name from the JSON payload.
    /// Falls back to the policy ID if parsing fails.
    /// </summary>
    private static string ExtractPolicyName(string policyJson, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(policyJson);
            if (doc.RootElement.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                return nameProp.GetString() ?? fallback;
            }

            // Also try PascalCase variant
            if (doc.RootElement.TryGetProperty("Name", out var nameCapProp) &&
                nameCapProp.ValueKind == JsonValueKind.String)
            {
                return nameCapProp.GetString() ?? fallback;
            }
        }
        catch (JsonException)
        {
            // Ignore parse errors, use fallback
        }

        return fallback;
    }
}
