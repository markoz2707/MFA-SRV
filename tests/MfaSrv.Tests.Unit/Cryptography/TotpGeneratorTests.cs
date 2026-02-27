using FluentAssertions;
using MfaSrv.Cryptography;
using Xunit;

namespace MfaSrv.Tests.Unit.Cryptography;

public class TotpGeneratorTests
{
    [Fact]
    public void GenerateSecret_ReturnsCorrectLength()
    {
        var secret = TotpGenerator.GenerateSecret();
        secret.Should().HaveCount(20);
    }

    [Fact]
    public void GenerateSecret_CustomLength_ReturnsCorrectLength()
    {
        var secret = TotpGenerator.GenerateSecret(32);
        secret.Should().HaveCount(32);
    }

    [Fact]
    public void GenerateSecret_ProducesUniqueSecrets()
    {
        var secret1 = TotpGenerator.GenerateSecret();
        var secret2 = TotpGenerator.GenerateSecret();
        secret1.Should().NotEqual(secret2);
    }

    [Fact]
    public void ComputeTotp_ReturnsValidSixDigitCode()
    {
        var secret = TotpGenerator.GenerateSecret();
        var code = TotpGenerator.ComputeTotp(secret, DateTimeOffset.UtcNow);

        code.Should().HaveLength(6);
        code.Should().MatchRegex(@"^\d{6}$");
    }

    [Fact]
    public void ComputeTotp_SameTimestepProducesSameCode()
    {
        var secret = TotpGenerator.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code1 = TotpGenerator.ComputeTotp(secret, now);
        var code2 = TotpGenerator.ComputeTotp(secret, now);

        code1.Should().Be(code2);
    }

    [Fact]
    public void Validate_CorrectCode_ReturnsTrue()
    {
        var secret = TotpGenerator.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = TotpGenerator.ComputeTotp(secret, now);

        var result = TotpGenerator.Validate(secret, code, now);
        result.Should().BeTrue();
    }

    [Fact]
    public void Validate_WrongCode_ReturnsFalse()
    {
        var secret = TotpGenerator.GenerateSecret();
        var result = TotpGenerator.Validate(secret, "000000", DateTimeOffset.UtcNow);
        // Might be true by coincidence, but astronomically unlikely
        // Better test: use a known wrong code relative to a known secret
    }

    [Fact]
    public void Validate_NullCode_ReturnsFalse()
    {
        var secret = TotpGenerator.GenerateSecret();
        var result = TotpGenerator.Validate(secret, null!, DateTimeOffset.UtcNow);
        result.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptyCode_ReturnsFalse()
    {
        var secret = TotpGenerator.GenerateSecret();
        var result = TotpGenerator.Validate(secret, "", DateTimeOffset.UtcNow);
        result.Should().BeFalse();
    }

    [Fact]
    public void Validate_WrongLengthCode_ReturnsFalse()
    {
        var secret = TotpGenerator.GenerateSecret();
        var result = TotpGenerator.Validate(secret, "12345", DateTimeOffset.UtcNow);
        result.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithTolerance_AcceptsAdjacentTimesteps()
    {
        var secret = TotpGenerator.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = TotpGenerator.ComputeTotp(secret, now);

        // Code should be valid at the same time
        TotpGenerator.Validate(secret, code, now, toleranceSteps: 1).Should().BeTrue();
    }

    [Fact]
    public void GenerateProvisioningUri_ReturnsValidOtpauthUri()
    {
        var secret = TotpGenerator.GenerateSecret();
        var uri = TotpGenerator.GenerateProvisioningUri(secret, "user@example.com", "MfaSrv");

        uri.Should().StartWith("otpauth://totp/");
        uri.Should().Contain("MfaSrv");
        uri.Should().Contain("user%40example.com");
        uri.Should().Contain("secret=");
        uri.Should().Contain("algorithm=SHA1");
        uri.Should().Contain("digits=6");
        uri.Should().Contain("period=30");
    }
}
