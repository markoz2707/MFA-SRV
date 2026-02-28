using Xunit;
using FluentAssertions;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Provider.Email;
using MfaSrv.Tests.Unit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MfaSrv.Tests.Unit.Providers;

public class EmailMfaProviderTests
{
    private static EmailMfaProvider CreateProvider(EmailSettings? settings = null)
    {
        var emailSettings = settings ?? new EmailSettings
        {
            CodeLength = 6,
            CodeExpiryMinutes = 5,
            MaxAttempts = 3,
            SubjectTemplate = "Your Code",
            BodyTemplate = "Code: {code}"
        };

        var emailSender = new EmailSender(
            Options.Create(emailSettings),
            NullLogger<EmailSender>.Instance);

        return new EmailMfaProvider(
            Options.Create(emailSettings),
            emailSender,
            new InMemoryChallengeStore(),
            NullLogger<EmailMfaProvider>.Instance);
    }

    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var provider = CreateProvider();

        provider.MethodId.Should().Be("EMAIL");
        provider.DisplayName.Should().Be("Email One-Time Password");
        provider.SupportsSynchronousVerification.Should().BeTrue();
        provider.SupportsAsynchronousVerification.Should().BeFalse();
        provider.RequiresEndpointAgent.Should().BeFalse();
    }

    [Fact]
    public async Task BeginEnrollment_WithoutEmail_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserEmail = null
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Email address is required");
    }

    [Fact]
    public async Task BeginEnrollment_WithEmail_ReturnsSuccess()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserEmail = "user@example.com"
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        result.Success.Should().BeTrue();
        result.Metadata.Should().ContainKey("emailAddress");
        result.Metadata!["emailAddress"].Should().Be("user@example.com");
    }

    [Fact]
    public async Task BeginEnrollment_WithEmptyEmail_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserEmail = "  "
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
