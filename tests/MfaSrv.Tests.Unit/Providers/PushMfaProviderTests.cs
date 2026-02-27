using System.Text;
using System.Text.Json;
using Xunit;
using FluentAssertions;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Provider.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MfaSrv.Tests.Unit.Providers;

public class PushMfaProviderTests
{
    private static PushMfaProvider CreateProvider(PushSettings? settings = null)
    {
        var pushSettings = settings ?? new PushSettings
        {
            ChallengeExpiryMinutes = 5,
            FcmServerKey = "",  // Dev mode: logs instead of sending
            FcmSendUrl = "https://fcm.googleapis.com/fcm/send"
        };

        var pushClient = new PushNotificationClient(
            new HttpClient(),
            Options.Create(pushSettings),
            NullLogger<PushNotificationClient>.Instance);

        return new PushMfaProvider(
            pushClient,
            Options.Create(pushSettings),
            NullLogger<PushMfaProvider>.Instance);
    }

    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var provider = CreateProvider();

        provider.MethodId.Should().Be("PUSH");
        provider.DisplayName.Should().Be("Push Notification");
        provider.SupportsSynchronousVerification.Should().BeFalse();
        provider.SupportsAsynchronousVerification.Should().BeTrue();
        provider.RequiresEndpointAgent.Should().BeFalse();
    }

    [Fact]
    public async Task BeginEnrollment_ReturnsRegistrationToken()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        result.Success.Should().BeTrue();
        result.Secret.Should().NotBeNull();
        result.Secret!.Length.Should().Be(32);
        result.Metadata.Should().ContainKey("registrationToken");
        result.Metadata!["registrationToken"].Should().NotBeNullOrEmpty();
        result.Metadata.Should().ContainKey("instruction");
    }

    [Fact]
    public async Task CompleteEnrollment_InvalidJson_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var result = await provider.CompleteEnrollmentAsync(ctx, "not-valid-json");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid JSON");
    }

    [Fact]
    public async Task CompleteEnrollment_MissingFields_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var json = JsonSerializer.Serialize(new { registrationToken = "abc" });
        var result = await provider.CompleteEnrollmentAsync(ctx, json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("must contain");
    }

    [Fact]
    public async Task CompleteEnrollment_InvalidRegistrationToken_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var json = JsonSerializer.Serialize(new
        {
            registrationToken = "invalid-token",
            deviceToken = "device-xyz"
        });

        var result = await provider.CompleteEnrollmentAsync(ctx, json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid or expired");
    }

    [Fact]
    public async Task CompleteEnrollment_ValidFlow_ReturnsSuccess()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        // Start enrollment to get a registration token
        var beginResult = await provider.BeginEnrollmentAsync(ctx);
        beginResult.Success.Should().BeTrue();

        var registrationToken = beginResult.Metadata!["registrationToken"];

        var json = JsonSerializer.Serialize(new
        {
            registrationToken,
            deviceToken = "fcm-device-token-123"
        });

        var result = await provider.CompleteEnrollmentAsync(ctx, json);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteEnrollment_UserMismatch_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var beginResult = await provider.BeginEnrollmentAsync(ctx);
        var registrationToken = beginResult.Metadata!["registrationToken"];

        var differentUserCtx = new EnrollmentContext
        {
            UserId = "user2",
            UserName = "otheruser"
        };

        var json = JsonSerializer.Serialize(new
        {
            registrationToken,
            deviceToken = "fcm-device-token-123"
        });

        var result = await provider.CompleteEnrollmentAsync(differentUserCtx, json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("User identity mismatch");
    }

    [Fact]
    public async Task IssueChallengeAsync_WithDeviceToken_ReturnsChallenge()
    {
        var provider = CreateProvider();

        var deviceTokenJson = JsonSerializer.Serialize(new { deviceToken = "test-device-token" });
        var ctx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = Encoding.UTF8.GetBytes(deviceTokenJson),
            TargetResource = "RDP Server"
        };

        var result = await provider.IssueChallengeAsync(ctx);

        result.Success.Should().BeTrue();
        result.ChallengeId.Should().NotBeNullOrEmpty();
        result.Status.Should().Be(ChallengeStatus.Issued);
        result.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        result.UserPrompt.Should().Contain("push notification");
    }

    [Fact]
    public async Task IssueChallengeAsync_NoDeviceToken_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = null
        };

        var result = await provider.IssueChallengeAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No device token");
    }

    [Fact]
    public async Task VerifyAsync_Approve_ReturnsSuccess()
    {
        var provider = CreateProvider();

        // Issue a challenge first
        var deviceTokenJson = JsonSerializer.Serialize(new { deviceToken = "test-device" });
        var challengeCtx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = Encoding.UTF8.GetBytes(deviceTokenJson)
        };

        var challenge = await provider.IssueChallengeAsync(challengeCtx);
        challenge.Success.Should().BeTrue();

        // Verify with APPROVE
        var verifyCtx = new VerificationContext
        {
            ChallengeId = challenge.ChallengeId!,
            UserId = "user1"
        };

        var result = await provider.VerifyAsync(verifyCtx, "APPROVE");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_Deny_ReturnsFalse()
    {
        var provider = CreateProvider();

        var deviceTokenJson = JsonSerializer.Serialize(new { deviceToken = "test-device" });
        var challengeCtx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = Encoding.UTF8.GetBytes(deviceTokenJson)
        };

        var challenge = await provider.IssueChallengeAsync(challengeCtx);

        var verifyCtx = new VerificationContext
        {
            ChallengeId = challenge.ChallengeId!,
            UserId = "user1"
        };

        var result = await provider.VerifyAsync(verifyCtx, "DENY");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("denied");
    }

    [Fact]
    public async Task VerifyAsync_InvalidResponse_ReturnsFalse()
    {
        var provider = CreateProvider();

        var deviceTokenJson = JsonSerializer.Serialize(new { deviceToken = "test-device" });
        var challengeCtx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = Encoding.UTF8.GetBytes(deviceTokenJson)
        };

        var challenge = await provider.IssueChallengeAsync(challengeCtx);

        var verifyCtx = new VerificationContext
        {
            ChallengeId = challenge.ChallengeId!,
            UserId = "user1"
        };

        var result = await provider.VerifyAsync(verifyCtx, "MAYBE");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Expected 'APPROVE' or 'DENY'");
    }

    [Fact]
    public async Task VerifyAsync_UnknownChallenge_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new VerificationContext
        {
            ChallengeId = "nonexistent",
            UserId = "user1"
        };

        var result = await provider.VerifyAsync(ctx, "APPROVE");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task CheckAsyncStatus_IssuedChallenge_ReturnsIssued()
    {
        var provider = CreateProvider();

        var deviceTokenJson = JsonSerializer.Serialize(new { deviceToken = "test-device" });
        var challengeCtx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = Encoding.UTF8.GetBytes(deviceTokenJson)
        };

        var challenge = await provider.IssueChallengeAsync(challengeCtx);

        var status = await provider.CheckAsyncStatusAsync(challenge.ChallengeId!);

        status.Status.Should().Be(ChallengeStatus.Issued);
    }

    [Fact]
    public async Task CheckAsyncStatus_ApprovedChallenge_ReturnsApproved()
    {
        var provider = CreateProvider();

        var deviceTokenJson = JsonSerializer.Serialize(new { deviceToken = "test-device" });
        var challengeCtx = new ChallengeContext
        {
            UserId = "user1",
            EnrollmentId = "enroll1",
            EncryptedSecret = Encoding.UTF8.GetBytes(deviceTokenJson)
        };

        var challenge = await provider.IssueChallengeAsync(challengeCtx);

        // Approve the challenge
        var verifyCtx = new VerificationContext
        {
            ChallengeId = challenge.ChallengeId!,
            UserId = "user1"
        };
        await provider.VerifyAsync(verifyCtx, "APPROVE");

        var status = await provider.CheckAsyncStatusAsync(challenge.ChallengeId!);

        status.Status.Should().Be(ChallengeStatus.Approved);
    }

    [Fact]
    public async Task CheckAsyncStatus_UnknownChallenge_ReturnsFailed()
    {
        var provider = CreateProvider();

        var status = await provider.CheckAsyncStatusAsync("unknown-id");

        status.Status.Should().Be(ChallengeStatus.Failed);
        status.Error.Should().Contain("not found");
    }
}
