using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MfaSrv.Core.Enums;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Services;

/// <summary>
/// Custom health check that verifies:
/// 1. Database connectivity (EF Core can query)
/// 2. Leader election status
/// 3. Agent connectivity (at least one DC agent registered and heartbeating)
/// </summary>
public class MfaSrvHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LeaderElectionService _leaderElection;
    private readonly ILogger<MfaSrvHealthCheck> _logger;

    public MfaSrvHealthCheck(
        IServiceScopeFactory scopeFactory,
        LeaderElectionService leaderElection,
        ILogger<MfaSrvHealthCheck> logger)
    {
        _scopeFactory = scopeFactory;
        _leaderElection = leaderElection;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MfaSrvDbContext>();

            // 1. Database check
            var canConnect = await db.Database.CanConnectAsync(ct);
            data["database"] = canConnect ? "connected" : "disconnected";

            if (!canConnect)
            {
                return HealthCheckResult.Unhealthy("Database unreachable", data: data);
            }

            // 2. Leader election status
            data["is_leader"] = _leaderElection.IsLeader;
            data["instance_id"] = _leaderElection.InstanceId;

            // 3. Basic counts for monitoring
            var activeSessions = await db.MfaSessions
                .CountAsync(s => s.ExpiresAt > DateTimeOffset.UtcNow && s.Status == Core.Enums.SessionStatus.Active, ct);
            var activeAgents = await db.AgentRegistrations
                .CountAsync(a => a.LastHeartbeatAt > DateTimeOffset.UtcNow.AddMinutes(-5), ct);
            var activePolicies = await db.Policies.CountAsync(p => p.IsEnabled, ct);

            data["active_sessions"] = activeSessions;
            data["active_agents"] = activeAgents;
            data["active_policies"] = activePolicies;

            // Update Prometheus gauges
            MetricsService.ActiveSessionsCount.Set(activeSessions);
            MetricsService.RegisteredAgentsCount.WithLabels("dc").Set(
                await db.AgentRegistrations.CountAsync(
                    a => a.AgentType == AgentType.DcAgent && a.LastHeartbeatAt > DateTimeOffset.UtcNow.AddMinutes(-5), ct));
            MetricsService.RegisteredAgentsCount.WithLabels("endpoint").Set(
                await db.AgentRegistrations.CountAsync(
                    a => a.AgentType == AgentType.EndpointAgent && a.LastHeartbeatAt > DateTimeOffset.UtcNow.AddMinutes(-5), ct));
            MetricsService.ActivePoliciesCount.Set(activePolicies);

            // Update DB size metric
            var connString = db.Database.GetConnectionString();
            if (connString != null)
            {
                var dbPath = ExtractDbPath(connString);
                if (dbPath != null && File.Exists(dbPath))
                {
                    MetricsService.DbSizeBytes.Set(new FileInfo(dbPath).Length);
                }
            }

            return HealthCheckResult.Healthy("All systems operational", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            data["error"] = ex.Message;
            return HealthCheckResult.Unhealthy("Health check failed", ex, data);
        }
    }

    private static string? ExtractDbPath(string connectionString)
    {
        // Parse "Data Source=path" from SQLite connection string
        foreach (var part in connectionString.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Data Source=".Length..].Trim();
            }
        }
        return null;
    }
}

/// <summary>
/// Readiness check - returns healthy only when this instance is ready to serve traffic.
/// For the active instance: database connected and leader lease held.
/// For standby: always ready (can serve read-only health/metrics endpoints).
/// </summary>
public class MfaSrvReadinessCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LeaderElectionService _leaderElection;

    public MfaSrvReadinessCheck(
        IServiceScopeFactory scopeFactory,
        LeaderElectionService leaderElection)
    {
        _scopeFactory = scopeFactory;
        _leaderElection = leaderElection;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>
        {
            ["is_leader"] = _leaderElection.IsLeader,
            ["instance_id"] = _leaderElection.InstanceId
        };

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MfaSrvDbContext>();

            var canConnect = await db.Database.CanConnectAsync(ct);
            data["database"] = canConnect ? "connected" : "disconnected";

            if (!canConnect)
            {
                return HealthCheckResult.Unhealthy("Database unavailable", data: data);
            }

            return HealthCheckResult.Healthy("Ready to serve traffic", data);
        }
        catch (Exception ex)
        {
            data["error"] = ex.Message;
            return HealthCheckResult.Unhealthy("Readiness check failed", ex, data);
        }
    }
}
