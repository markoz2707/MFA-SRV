using System.Text.Json;
using Xunit;
using FluentAssertions;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Provider.Fido2;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MfaSrv.Tests.Unit.Providers;

public class Fido2MfaProviderTests
{
    private static Fido2MfaProvider CreateProvider(Fido2Settings? settings = null)
    {
        var fido2Settings = settings ?? new Fido2Settings
        {
            ServerDomain = "example.com",
            ServerName = "MfaSrv Test",
            Origin = "https://example.com",
            ChallengeSize = 32,
            ChallengeExpiryMinutes = 5
        };

        return new Fido2MfaProvider(
            Options.Create(fido2Settings),
            NullLogger<Fido2MfaProvider>.Instance);
    }

    [Fact]
    public void Metadata_HasCorrectValues()
    {
        var provider = CreateProvider();

        provider.MethodId.Should().Be("FIDO2");
        provider.DisplayName.Should().Be("FIDO2 / WebAuthn (YubiKey)");
        provider.SupportsSynchronousVerification.Should().BeTrue();
        provider.SupportsAsynchronousVerification.Should().BeFalse();
        provider.RequiresEndpointAgent.Should().BeTrue();
    }

    [Fact]
    public async Task BeginEnrollment_ReturnsValidCreationOptions()
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
        result.Secret!.Length.Should().Be(32); // user handle
        result.Metadata.Should().ContainKey("challengeId");
        result.Metadata.Should().ContainKey("publicKeyCredentialCreationOptions");
        result.Metadata.Should().ContainKey("instruction");
    }

    [Fact]
    public async Task BeginEnrollment_CreationOptionsContainExpectedFields()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var result = await provider.BeginEnrollmentAsync(ctx);
        var optionsJson = result.Metadata!["publicKeyCredentialCreationOptions"];
        using var doc = JsonDocument.Parse(optionsJson);
        var root = doc.RootElement;

        // Verify RP
        root.TryGetProperty("rp", out var rp).Should().BeTrue();
        rp.GetProperty("id").GetString().Should().Be("example.com");
        rp.GetProperty("name").GetString().Should().Be("MfaSrv Test");

        // Verify user
        root.TryGetProperty("user", out var user).Should().BeTrue();
        user.GetProperty("name").GetString().Should().Be("testuser");
        user.GetProperty("displayName").GetString().Should().Be("testuser");
        user.GetProperty("id").GetString().Should().NotBeNullOrEmpty();

        // Verify challenge
        root.TryGetProperty("challenge", out var challenge).Should().BeTrue();
        challenge.GetString().Should().NotBeNullOrEmpty();
        var challengeBytes = Convert.FromBase64String(challenge.GetString()!);
        challengeBytes.Length.Should().Be(32);

        // Verify pubKeyCredParams includes ES256 and RS256
        root.TryGetProperty("pubKeyCredParams", out var credParams).Should().BeTrue();
        credParams.GetArrayLength().Should().Be(2);
        credParams[0].GetProperty("alg").GetInt32().Should().Be(-7);   // ES256
        credParams[1].GetProperty("alg").GetInt32().Should().Be(-257); // RS256

        // Verify attestation
        root.GetProperty("attestation").GetString().Should().Be("direct");

        // Verify authenticator selection
        root.TryGetProperty("authenticatorSelection", out var authSel).Should().BeTrue();
        authSel.GetProperty("authenticatorAttachment").GetString().Should().Be("cross-platform");
        authSel.GetProperty("userVerification").GetString().Should().Be("preferred");
    }

    [Fact]
    public async Task BeginEnrollment_EachCallGeneratesUniqueChallenge()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var result1 = await provider.BeginEnrollmentAsync(ctx);
        var result2 = await provider.BeginEnrollmentAsync(ctx);

        result1.Metadata!["challengeId"].Should().NotBe(result2.Metadata!["challengeId"]);
    }

    [Fact]
    public async Task CompleteEnrollment_MissingChallengeId_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var json = JsonSerializer.Serialize(new
        {
            attestationObject = "abc",
            clientDataJSON = "def"
        });

        var result = await provider.CompleteEnrollmentAsync(ctx, json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("challengeId");
    }

    [Fact]
    public async Task CompleteEnrollment_UnknownChallengeId_ReturnsFalse()
    {
        var provider = CreateProvider();
        var ctx = new EnrollmentContext
        {
            UserId = "user1",
            UserName = "testuser"
        };

        var json = JsonSerializer.Serialize(new
        {
            challengeId = "nonexistent-id",
            attestationObject = "abc",
            clientDataJSON = "def",
            credentialId = "ghi"
        });

        var result = await provider.CompleteEnrollmentAsync(ctx, json);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unknown or expired");
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

        var result = await provider.CompleteEnrollmentAsync(ctx, "not-json");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid JSON");
    }

    [Fact]
    public async Task IssueChallengeAsync_NullSecret_ReturnsFalse()
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
        result.Error.Should().Contain("No FIDO2 credential");
        result.Status.Should().Be(ChallengeStatus.Failed);
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

        var result = await provider.VerifyAsync(ctx, "{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unknown or expired");
    }

    [Fact]
    public async Task CheckAsyncStatus_ReturnsIssued()
    {
        var provider = CreateProvider();

        var status = await provider.CheckAsyncStatusAsync("any-id");

        status.Status.Should().Be(ChallengeStatus.Issued);
    }
}
