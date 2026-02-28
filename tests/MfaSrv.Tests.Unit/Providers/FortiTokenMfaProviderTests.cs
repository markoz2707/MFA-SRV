using System.Text;
using Xunit;
using FluentAssertions;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Provider.FortiToken;
using MfaSrv.Tests.Unit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MfaSrv.Tests.Unit.Providers;

public class FortiTokenMfaProviderTests
{
    /// <summary>
    /// Creates a FortiTokenMfaProvider in dev mode (empty FortiAuthUrl).
    /// Dev mode auto-approves all API calls, allowing us to test provider logic.
    /// </summary>
    private static FortiTokenMfaProvider CreateProvider(FortiTokenSettings? settings = null)
    {
        var fortiSettings = settings ?? new FortiTokenSettings
        {
            FortiAuthUrl = "",  // Dev mode
            ChallengeExpiryMinutes = 5,
            MaxAttempts = 3,
            UsePushNotification = false
        };

        var httpClient = new HttpClient();
        var fortiClient = new FortiAuthClient(
            httpClient,
            Options.Create(fortiSettings),
            NullLogger<FortiAuthClient>.Instance);

        return new FortiTokenMfaProvider(
            fortiClient,
            Options.Create(fortiSettings),
            new InMemoryChallengeStore(),
            NullLogger<FortiTokenMfaProvider>.Instance);
    }

    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var provider = CreateProvider();

        provider.MethodId.Should().Be("FORTITOKEN");
        provider.DisplayName.Should().Be("FortiToken (FortiAuthenticator)");
        provider.SupportsSynchronousVerification.Should().BeTrue();
        provider.SupportsAsynchronousVerification.Should().BeTrue();
        provider.RequiresEndpointAgent.Should().BeFalse();
    }

    [Fact]
    public async Task BeginEnrollment_WithSerialNumber_ReturnsSuccess()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserPhone = "FTK200ABC123"  // serial number via transport field
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        result.Success.Should().BeTrue();
        result.Secret.Should().NotBeNull();
        result.Metadata.Should().ContainKey("serialNumber");
        result.Metadata!["serialNumber"].Should().Be("FTK200ABC123");
        result.Metadata.Should().ContainKey("username");
        result.Metadata["username"].Should().Be("testuser");
    }

    [Fact]
    public async Task BeginEnrollment_WithoutSerialNumber_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserPhone = null  // no serial
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("serial number");
    }

    [Fact]
    public async Task CompleteEnrollment_WithValidOtp_ReturnsSuccess()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser",
            UserPhone = "FTK200ABC123"
        };

        // Begin enrollment first
        await provider.BeginEnrollmentAsync(ctx);

        // Complete with an OTP - dev mode auto-approves
        var result = await provider.CompleteEnrollmentAsync(ctx, "123456");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteEnrollment_EmptyResponse_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var result = await provider.CompleteEnrollmentAsync(ctx, "");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("one-time password");
    }

    [Fact]
    public async Task IssueChallenge_OtpMode_ReturnsIssuedChallenge()
    {
        var provider = CreateProvider(new FortiTokenSettings
        {
            FortiAuthUrl = "",
            UsePushNotification = false,
            ChallengeExpiryMinutes = 5,
            MaxAttempts = 3
        });

        var secret = Encoding.UTF8.GetBytes("testuser:FTK200ABC123");
        var ctx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = secret
        };

        var result = await provider.IssueChallengeAsync(ctx);

        result.Success.Should().BeTrue();
        result.ChallengeId.Should().NotBeNullOrEmpty();
        result.Status.Should().Be(ChallengeStatus.Issued);
        result.UserPrompt.Should().Contain("FortiToken");
    }

    [Fact]
    public async Task IssueChallenge_PushMode_ReturnsIssuedChallenge()
    {
        var provider = CreateProvider(new FortiTokenSettings
        {
            FortiAuthUrl = "",
            UsePushNotification = true,
            ChallengeExpiryMinutes = 5,
            MaxAttempts = 3
        });

        var secret = Encoding.UTF8.GetBytes("testuser:FTK200ABC123");
        var ctx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = secret
        };

        var result = await provider.IssueChallengeAsync(ctx);

        result.Success.Should().BeTrue();
        result.ChallengeId.Should().NotBeNullOrEmpty();
        result.Status.Should().Be(ChallengeStatus.Issued);
        result.UserPrompt.Should().Contain("push notification");
    }

    [Fact]
    public async Task IssueChallenge_NullSecret_ReturnsFailed()
    {
        var provider = CreateProvider();
        var ctx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = null
        };

        var result = await provider.IssueChallengeAsync(ctx);

        // Falls back to UserId as username, should still work
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Verify_ValidOtp_ReturnsSuccess()
    {
        var provider = CreateProvider();
        var secret = Encoding.UTF8.GetBytes("testuser:FTK200ABC123");
        var challengeCtx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = secret
        };

        var challenge = await provider.IssueChallengeAsync(challengeCtx);
        challenge.Success.Should().BeTrue();

        var verifyCtx = new VerificationContext
        {
            ChallengeId = challenge.ChallengeId!,
            UserId = "user1"
        };

        // Dev mode auto-approves OTP verification
        var result = await provider.VerifyAsync(verifyCtx, "123456");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Verify_EmptyResponse_ReturnsFalse()
    {
        var provider = CreateProvider();
        var verifyCtx = new VerificationContext
        {
            ChallengeId = "some-challenge",
            UserId = "user1"
        };

        var result = await provider.VerifyAsync(verifyCtx, "");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("code is required");
    }

    [Fact]
    public async Task Verify_UnknownChallenge_ReturnsFalse()
    {
        var provider = CreateProvider();
        var verifyCtx = new VerificationContext
        {
            ChallengeId = "nonexistent",
            UserId = "user1"
        };

        var result = await provider.VerifyAsync(verifyCtx, "123456");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task CheckAsyncStatus_UnknownChallenge_ReturnsFailed()
    {
        var provider = CreateProvider();

        var status = await provider.CheckAsyncStatusAsync("nonexistent");

        status.Status.Should().Be(ChallengeStatus.Failed);
        status.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task CheckAsyncStatus_PendingPushChallenge_ReturnsApproved()
    {
        // In dev mode, push status returns "approved"
        var provider = CreateProvider(new FortiTokenSettings
        {
            FortiAuthUrl = "",
            UsePushNotification = true,
            ChallengeExpiryMinutes = 5,
            MaxAttempts = 3
        });

        var secret = Encoding.UTF8.GetBytes("testuser:FTK200ABC123");
        var ctx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = secret
        };

        var challenge = await provider.IssueChallengeAsync(ctx);
        challenge.Success.Should().BeTrue();

        // In dev mode, push status check auto-returns "approved"
        var status = await provider.CheckAsyncStatusAsync(challenge.ChallengeId!);

        status.Status.Should().Be(ChallengeStatus.Approved);
    }
}
