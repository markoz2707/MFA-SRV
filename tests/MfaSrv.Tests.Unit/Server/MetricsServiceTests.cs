using FluentAssertions;
using MfaSrv.Server.Services;
using Xunit;

namespace MfaSrv.Tests.Unit.Server;

public class MetricsServiceTests
{
    [Fact]
    public void AuthEvaluationsTotal_IsConfiguredWithDecisionLabel()
    {
        // Verify the counter exists and can be incremented with labels
        var labeled = MetricsService.AuthEvaluationsTotal.WithLabels("allow");
        labeled.Should().NotBeNull();
    }

    [Fact]
    public void MfaChallengesIssuedTotal_IsConfiguredWithMethodLabel()
    {
        var labeled = MetricsService.MfaChallengesIssuedTotal.WithLabels("TOTP");
        labeled.Should().NotBeNull();
    }

    [Fact]
    public void MfaVerificationsTotal_IsConfiguredWithMethodAndResultLabels()
    {
        var labeled = MetricsService.MfaVerificationsTotal.WithLabels("TOTP", "success");
        labeled.Should().NotBeNull();
    }

    [Fact]
    public void RegisteredAgentsCount_IsConfiguredWithTypeLabel()
    {
        var labeled = MetricsService.RegisteredAgentsCount.WithLabels("dc");
        labeled.Should().NotBeNull();
    }

    [Fact]
    public void GrpcCallsTotal_IsConfiguredWithMethodAndStatusLabels()
    {
        var labeled = MetricsService.GrpcCallsTotal.WithLabels("Evaluate", "OK");
        labeled.Should().NotBeNull();
    }

    [Fact]
    public void DbBackupsTotal_IsConfiguredWithResultLabel()
    {
        var labeled = MetricsService.DbBackupsTotal.WithLabels("success");
        labeled.Should().NotBeNull();
    }

    [Fact]
    public void EnrollmentsTotal_IsConfiguredWithMethodAndResultLabels()
    {
        var labeled = MetricsService.EnrollmentsTotal.WithLabels("TOTP", "success");
        labeled.Should().NotBeNull();
    }

    [Fact]
    public void ActiveSessionsCount_CanSetValue()
    {
        MetricsService.ActiveSessionsCount.Set(42);
        MetricsService.ActiveSessionsCount.Value.Should().Be(42);
    }

    [Fact]
    public void IsLeader_CanToggle()
    {
        MetricsService.IsLeader.Set(1);
        MetricsService.IsLeader.Value.Should().Be(1);

        MetricsService.IsLeader.Set(0);
        MetricsService.IsLeader.Value.Should().Be(0);
    }

    [Fact]
    public void DbSizeBytes_CanSetValue()
    {
        MetricsService.DbSizeBytes.Set(1024 * 1024);
        MetricsService.DbSizeBytes.Value.Should().Be(1024 * 1024);
    }

    [Fact]
    public void AuthEvaluationDuration_CanObserve()
    {
        // Should not throw
        MetricsService.AuthEvaluationDuration.Observe(0.05);
    }

    [Fact]
    public void PolicyEvaluationsTotal_IsConfiguredWithActionLabel()
    {
        var labeled = MetricsService.PolicyEvaluationsTotal.WithLabels("require_mfa");
        labeled.Should().NotBeNull();
    }
}
