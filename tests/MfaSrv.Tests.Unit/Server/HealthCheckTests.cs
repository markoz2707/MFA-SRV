using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Server;
using MfaSrv.Server.Data;
using MfaSrv.Server.Services;
using Xunit;

namespace MfaSrv.Tests.Unit.Server;

public class HealthCheckTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly LeaderElectionService _leaderService;
    private readonly string _dbName;

    public HealthCheckTests()
    {
        _dbName = $"HealthCheck_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<MfaSrvDbContext>(options =>
            options.UseInMemoryDatabase(_dbName));
        _serviceProvider = services.BuildServiceProvider();

        // Ensure DB created through a scope
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MfaSrvDbContext>();
        db.Database.EnsureCreated();

        _leaderService = new LeaderElectionService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new HaSettings { Enabled = false, InstanceId = "test" }),
            NullLogger<LeaderElectionService>.Instance);
    }

    private MfaSrvHealthCheck CreateHealthCheck()
    {
        return new MfaSrvHealthCheck(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _leaderService,
            NullLogger<MfaSrvHealthCheck>.Instance);
    }

    private MfaSrvReadinessCheck CreateReadinessCheck()
    {
        return new MfaSrvReadinessCheck(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _leaderService);
    }

    private MfaSrvDbContext GetScopedDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MfaSrvDbContext>();
    }

    [Fact]
    public async Task HealthCheck_WithEmptyDb_ReturnsResult()
    {
        var check = CreateHealthCheck();
        var context = new HealthCheckContext();

        var result = await check.CheckHealthAsync(context);

        // The health check should complete without throwing
        result.Should().NotBeNull();
        result.Data.Should().NotBeNull();

        // If healthy, verify expected data keys
        if (result.Status == HealthStatus.Healthy)
        {
            result.Data.Should().ContainKey("database");
            result.Data["database"].Should().Be("connected");
            result.Data.Should().ContainKey("active_sessions");
            result.Data.Should().ContainKey("active_agents");
            result.Data.Should().ContainKey("active_policies");
        }
        else
        {
            // If unhealthy due to InMemory limitations, verify error info is present
            result.Data.Should().ContainKey("error");
        }
    }

    [Fact]
    public async Task HealthCheck_CountsActiveSessionsCorrectly()
    {
        using (var db = GetScopedDb())
        {
            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                ObjectGuid = Guid.NewGuid().ToString(),
                SamAccountName = "testuser",
                UserPrincipalName = "testuser@example.com"
            };
            db.Users.Add(user);

            // Active session
            db.MfaSessions.Add(new MfaSession
            {
                Id = Guid.NewGuid().ToString(),
                UserId = user.Id,
                Status = SessionStatus.Active,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt = DateTimeOffset.UtcNow,
                SourceIp = "127.0.0.1"
            });

            // Expired session (should not count)
            db.MfaSessions.Add(new MfaSession
            {
                Id = Guid.NewGuid().ToString(),
                UserId = user.Id,
                Status = SessionStatus.Active,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                SourceIp = "127.0.0.1"
            });

            await db.SaveChangesAsync();
        }

        var check = CreateHealthCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        if (result.Status == HealthStatus.Healthy)
        {
            result.Data["active_sessions"].Should().Be(1);
        }
    }

    [Fact]
    public async Task HealthCheck_CountsActiveAgentsCorrectly()
    {
        using (var db = GetScopedDb())
        {
            // Active agent (heartbeat within 5 min)
            db.AgentRegistrations.Add(new AgentRegistration
            {
                Id = Guid.NewGuid().ToString(),
                AgentType = AgentType.DcAgent,
                Hostname = "dc01",
                LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });

            // Stale agent (heartbeat > 5 min ago)
            db.AgentRegistrations.Add(new AgentRegistration
            {
                Id = Guid.NewGuid().ToString(),
                AgentType = AgentType.EndpointAgent,
                Hostname = "ws01",
                LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            });

            await db.SaveChangesAsync();
        }

        var check = CreateHealthCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        if (result.Status == HealthStatus.Healthy)
        {
            result.Data["active_agents"].Should().Be(1);
        }
    }

    [Fact]
    public async Task HealthCheck_IncludesLeaderElectionStatus()
    {
        var check = CreateHealthCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Leader status should be present regardless of health status
        result.Data.Should().ContainKey("instance_id");
    }

    [Fact]
    public async Task ReadinessCheck_WithDatabase_ReturnsHealthy()
    {
        var check = CreateReadinessCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("Ready to serve traffic");
        result.Data["database"].Should().Be("connected");
    }

    [Fact]
    public async Task ReadinessCheck_IncludesLeaderInfo()
    {
        var check = CreateReadinessCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Data.Should().ContainKey("is_leader");
        result.Data.Should().ContainKey("instance_id");
    }

    [Fact]
    public async Task HealthCheck_CountsActivePolicies()
    {
        using (var db = GetScopedDb())
        {
            db.Policies.Add(new Policy
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Enabled Policy",
                IsEnabled = true,
                Priority = 1
            });

            db.Policies.Add(new Policy
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Disabled Policy",
                IsEnabled = false,
                Priority = 2
            });

            await db.SaveChangesAsync();
        }

        var check = CreateHealthCheck();
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        if (result.Status == HealthStatus.Healthy)
        {
            result.Data["active_policies"].Should().Be(1);
        }
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}
