using Xunit;
using FluentAssertions;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Provider.Sms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MfaSrv.Tests.Unit.Providers;

public class SmsMfaProviderTests
{
    private static SmsMfaProvider CreateProvider(SmsSettings? settings = null)
    {
        var smsSettings = settings ?? new SmsSettings
        {
            CodeLength = 6,
            CodeExpiryMinutes = 5,
            MaxAttempts = 3,
            MessageTemplate = "Code: {code}"
        };

        var smsClient = new SmsGatewayClient(
            new HttpClient(),
            Options.Create(smsSettings),
            NullLogger<SmsGatewayClient>.Instance);

        return new SmsMfaProvider(
            Options.Create(smsSettings),
            smsClient,
            NullLogger<SmsMfaProvider>.Instance);
    }

    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var provider = CreateProvider();

        provider.MethodId.Should().Be("SMS");
        provider.DisplayName.Should().Be("SMS One-Time Password");
        provider.SupportsSynchronousVerification.Should().BeTrue();
        provider.SupportsAsynchronousVerification.Should().BeFalse();
        provider.RequiresEndpointAgent.Should().BeFalse();
    }

    [Fact]
    public async Task BeginEnrollment_WithoutPhone_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserPhone = null
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Phone number is required");
    }

    [Fact]
    public async Task BeginEnrollment_WithPhone_ReturnsSuccess()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserPhone = "+15551234567"
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        result.Success.Should().BeTrue();
        result.Metadata.Should().ContainKey("phoneNumber");
        result.Metadata!["phoneNumber"].Should().Be("+15551234567");
    }

    [Fact]
    public async Task BeginEnrollment_WithEmptyPhone_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserPhone = "  "
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsyncStatus_ReturnsNotSupported()
    {
        var provider = CreateProvider();

        var result = await provider.CheckAsyncStatusAsync("any-challenge-id");

        result.Status.Should().Be(ChallengeStatus.Failed);
        result.Error.Should().Contain("does not support asynchronous");
    }

    [Fact]
    public async Task VerifyAsync_UnknownChallenge_ReturnsNotFound()
    {
        var provider = CreateProvider();
        var ctx = new VerificationContext
        {
            ChallengeId = "nonexistent-challenge",
            UserId = "user1"
        };

        var result = await provider.VerifyAsync(ctx, "123456");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }
}
