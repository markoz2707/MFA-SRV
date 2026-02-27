using Grpc.Core;
using MfaSrv.Protocol;
using MfaSrv.Server.Services;
using Microsoft.EntityFrameworkCore;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;

namespace MfaSrv.Server.GrpcServices;

public partial class MfaGrpcService
{
    /// <summary>
    /// Server-streaming RPC that sends policy updates to DC Agents.
    /// First sends a full snapshot of all current enabled policies, then
    /// keeps the stream open to push real-time change notifications.
    /// </summary>
    public override async Task SyncPolicies(
        SyncPoliciesRequest request,
        IServerStreamWriter<PolicyUpdate> responseStream,
        ServerCallContext context)
    {
        var agentId = request.AgentId;
        _logger.LogInformation(
            "Agent {AgentId} starting policy sync stream (last_sync={LastSync})",
            agentId, request.LastSync?.ToDateTimeOffset());

        // Send initial snapshot of all current enabled policies
        var policies = await _db.Policies
            .Include(p => p.RuleGroups).ThenInclude(g => g.Rules)
            .Include(p => p.Actions)
            .Where(p => p.IsEnabled)
            .AsNoTracking()
            .ToListAsync(context.CancellationToken);

        _logger.LogInformation(
            "Sending {Count} initial policies to agent {AgentId}",
            policies.Count, agentId);

        foreach (var policy in policies)
        {
            var policyJson = JsonSerializer.Serialize(policy, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var update = new PolicyUpdate
            {
                PolicyId = policy.Id,
                PolicyJson = policyJson,
                Deleted = false,
                UpdatedAt = Timestamp.FromDateTimeOffset(policy.UpdatedAt)
            };

            await responseStream.WriteAsync(update);
        }

        // Subscribe to real-time policy changes
        var channel = _policySyncStream.Subscribe(agentId);

        try
        {
            await foreach (var notification in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                var update = new PolicyUpdate
                {
                    PolicyId = notification.PolicyId,
                    PolicyJson = notification.PolicyJson,
                    Deleted = notification.Deleted,
                    UpdatedAt = Timestamp.FromDateTimeOffset(notification.UpdatedAt)
                };

                await responseStream.WriteAsync(update);

                _logger.LogDebug(
                    "Streamed policy update {PolicyId} (deleted={Deleted}) to agent {AgentId}",
                    notification.PolicyId, notification.Deleted, agentId);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Policy sync stream cancelled for agent {AgentId}", agentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Policy sync stream error for agent {AgentId}", agentId);
            throw;
        }
        finally
        {
            _policySyncStream.Unsubscribe(agentId);
            _logger.LogInformation("Agent {AgentId} disconnected from policy sync stream", agentId);
        }
    }
}
