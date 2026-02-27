using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Services;

/// <summary>
/// Simple file/database-based leader election for active-passive HA.
///
/// The leader periodically renews a lease in the database. If the lease expires
/// (leader crashed or became unreachable), another instance takes over.
///
/// This uses a "leader_lease" row in the cache_metadata-style table (or a
/// dedicated LeaderLease table) with optimistic concurrency control.
///
/// For production deployments with PostgreSQL, this can be replaced with
/// advisory locks or a distributed lock service (etcd, Consul).
/// </summary>
public class LeaderElectionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HaSettings _settings;
    private readonly ILogger<LeaderElectionService> _logger;

    private volatile bool _isLeader;
    private string _instanceId;

    public bool IsLeader => _isLeader;
    public string InstanceId => _instanceId;

    public LeaderElectionService(
        IServiceScopeFactory scopeFactory,
        IOptions<HaSettings> settings,
        ILogger<LeaderElectionService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
        _instanceId = _settings.InstanceId;

        if (string.IsNullOrEmpty(_instanceId))
        {
            _instanceId = $"{Environment.MachineName}-{Environment.ProcessId}";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            // Single-instance mode: always leader
            _isLeader = true;
            MetricsService.IsLeader.Set(1);
            _logger.LogInformation("HA disabled - running as single active instance ({InstanceId})", _instanceId);
            return;
        }

        _logger.LogInformation(
            "Leader election started: instance={InstanceId}, lease={LeaseDuration}s, renew={RenewInterval}s",
            _instanceId, _settings.LeaseDurationSeconds, _settings.LeaseRenewIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryAcquireOrRenewLeaseAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in leader election cycle");
                // If we can't reach the DB, relinquish leadership to be safe
                if (_isLeader)
                {
                    _isLeader = false;
                    MetricsService.IsLeader.Set(0);
                    _logger.LogWarning("Relinquishing leadership due to database error");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.LeaseRenewIntervalSeconds), stoppingToken);
        }

        // Release lease on shutdown
        if (_isLeader)
        {
            try
            {
                await ReleaseLeaseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release lease on shutdown");
            }
        }

        _isLeader = false;
        MetricsService.IsLeader.Set(0);
        _logger.LogInformation("Leader election stopped");
    }

    private async Task TryAcquireOrRenewLeaseAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MfaSrvDbContext>();

        var lease = await db.LeaderLeases.FirstOrDefaultAsync(l => l.LeaseKey == "primary", ct);

        var now = DateTimeOffset.UtcNow;
        var leaseExpiry = now.AddSeconds(_settings.LeaseDurationSeconds);

        if (lease == null)
        {
            // No lease exists - try to create one
            db.LeaderLeases.Add(new LeaderLease
            {
                LeaseKey = "primary",
                HolderId = _instanceId,
                AcquiredAt = now,
                ExpiresAt = leaseExpiry,
                RenewedAt = now
            });

            try
            {
                await db.SaveChangesAsync(ct);
                BecomeLeader();
            }
            catch (DbUpdateException)
            {
                // Another instance created it first - we remain standby
                BecomeStandby();
            }
        }
        else if (lease.HolderId == _instanceId)
        {
            // We hold the lease - renew it
            lease.ExpiresAt = leaseExpiry;
            lease.RenewedAt = now;
            await db.SaveChangesAsync(ct);

            if (!_isLeader)
                BecomeLeader();
        }
        else if (lease.ExpiresAt < now)
        {
            // Lease expired - try to take over
            var previousHolder = lease.HolderId;
            lease.HolderId = _instanceId;
            lease.AcquiredAt = now;
            lease.ExpiresAt = leaseExpiry;
            lease.RenewedAt = now;

            try
            {
                await db.SaveChangesAsync(ct);
                MetricsService.LeaderElectionsTotal.Inc();
                _logger.LogWarning(
                    "Took over leadership from expired lease (previous: {PreviousHolder})",
                    previousHolder);
                BecomeLeader();
            }
            catch (DbUpdateException)
            {
                // Another instance took it - remain standby
                BecomeStandby();
            }
        }
        else
        {
            // Another instance holds a valid lease
            BecomeStandby();
        }
    }

    private async Task ReleaseLeaseAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MfaSrvDbContext>();

        var lease = await db.LeaderLeases
            .FirstOrDefaultAsync(l => l.LeaseKey == "primary" && l.HolderId == _instanceId);

        if (lease != null)
        {
            lease.ExpiresAt = DateTimeOffset.UtcNow; // Expire immediately
            await db.SaveChangesAsync();
            _logger.LogInformation("Released leadership lease");
        }
    }

    private void BecomeLeader()
    {
        if (!_isLeader)
        {
            _isLeader = true;
            MetricsService.IsLeader.Set(1);
            _logger.LogInformation("This instance ({InstanceId}) is now the LEADER", _instanceId);
        }
    }

    private void BecomeStandby()
    {
        if (_isLeader)
        {
            _isLeader = false;
            MetricsService.IsLeader.Set(0);
            _logger.LogWarning("This instance ({InstanceId}) is now STANDBY", _instanceId);
        }
    }
}

/// <summary>
/// EF Core entity for leader lease tracking.
/// </summary>
public class LeaderLease
{
    public string LeaseKey { get; set; } = "primary";
    public string HolderId { get; set; } = string.Empty;
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset RenewedAt { get; set; }
}
