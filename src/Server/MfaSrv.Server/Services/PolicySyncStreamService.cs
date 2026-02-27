using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MfaSrv.Server.Services;

/// <summary>
/// Manages server-side policy sync streaming subscriptions.
/// When a DC Agent subscribes, it receives current policies as an initial snapshot
/// followed by real-time change notifications via a bounded channel.
/// </summary>
public class PolicySyncStreamService
{
    private readonly ConcurrentDictionary<string, Channel<PolicyChangeNotification>> _subscribers = new();
    private readonly ILogger<PolicySyncStreamService> _logger;

    public PolicySyncStreamService(ILogger<PolicySyncStreamService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Subscribes an agent to receive policy change notifications.
    /// If the agent was already subscribed, the old channel is completed and replaced.
    /// </summary>
    public Channel<PolicyChangeNotification> Subscribe(string agentId)
    {
        var channel = Channel.CreateBounded<PolicyChangeNotification>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers.AddOrUpdate(agentId, channel, (_, old) =>
        {
            old.Writer.TryComplete();
            return channel;
        });

        _logger.LogInformation("Agent {AgentId} subscribed to policy sync stream", agentId);
        return channel;
    }

    /// <summary>
    /// Unsubscribes an agent and completes its notification channel.
    /// </summary>
    public void Unsubscribe(string agentId)
    {
        if (_subscribers.TryRemove(agentId, out var channel))
        {
            channel.Writer.TryComplete();
            _logger.LogInformation("Agent {AgentId} unsubscribed from policy sync stream", agentId);
        }
    }

    /// <summary>
    /// Broadcasts a policy change notification to all connected agents.
    /// </summary>
    public async Task NotifyPolicyChangeAsync(string policyId, string policyJson, bool deleted, DateTimeOffset updatedAt)
    {
        var notification = new PolicyChangeNotification
        {
            PolicyId = policyId,
            PolicyJson = policyJson,
            Deleted = deleted,
            UpdatedAt = updatedAt
        };

        var failedAgents = new List<string>();

        foreach (var (agentId, channel) in _subscribers)
        {
            try
            {
                if (!channel.Writer.TryWrite(notification))
                {
                    _logger.LogWarning(
                        "Policy notification dropped for agent {AgentId} (queue full, oldest dropped)",
                        agentId);
                }
            }
            catch (ChannelClosedException)
            {
                _logger.LogDebug("Channel closed for agent {AgentId}, will be cleaned up", agentId);
                failedAgents.Add(agentId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify agent {AgentId} of policy change", agentId);
                failedAgents.Add(agentId);
            }
        }

        // Clean up agents whose channels have been closed
        foreach (var agentId in failedAgents)
        {
            Unsubscribe(agentId);
        }

        if (_subscribers.Count > 0)
        {
            _logger.LogDebug(
                "Policy change notification for {PolicyId} broadcast to {Count} agents",
                policyId, _subscribers.Count);
        }
    }

    /// <summary>
    /// Returns the current number of subscribed agents.
    /// </summary>
    public int SubscriberCount => _subscribers.Count;
}

/// <summary>
/// Represents a policy change event to be streamed to connected agents.
/// </summary>
public record PolicyChangeNotification
{
    public required string PolicyId { get; init; }
    public required string PolicyJson { get; init; }
    public bool Deleted { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
