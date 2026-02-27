using FluentAssertions;
using MfaSrv.Cryptography;
using Xunit;

namespace MfaSrv.Tests.Unit.Cryptography;

public class Base32Tests
{
    [Fact]
    public void Encode_EmptyArray_ReturnsEmptyString()
    {
        Base32.Encode(Array.Empty<byte>()).Should().BeEmpty();
    }

    [Fact]
    public void Decode_EmptyString_ReturnsEmptyArray()
    {
        Base32.Decode("").Should().BeEmpty();
    }

    [Fact]
    public void RoundTrip_PreservesData()
    {
        var original = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var encoded = Base32.Encode(original);
        var decoded = Base32.Decode(encoded);

        decoded.Should().Equal(original);
    }

    [Fact]
    public void RoundTrip_RandomData_PreservesData()
    {
        var original = new byte[20];
        System.Security.Cryptography.RandomNumberGenerator.Fill(original);

        var encoded = Base32.Encode(original);
        var decoded = Base32.Decode(encoded);

        decoded.Should().Equal(original);
    }

    [Theory]
    [InlineData("JBSWY3DPEHPK3PXP", new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0xDE, 0xAD, 0xBE, 0xEF })]
    public void Decode_KnownValues(string encoded, byte[] expected)
    {
        var decoded = Base32.Decode(encoded);
        decoded.Should().Equal(expected);
    }

    [Fact]
    public void Encode_OnlyContainsBase32Characters()
    {
        var data = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);

        var encoded = Base32.Encode(data);
        encoded.Should().MatchRegex(@"^[A-Z2-7]*$");
    }

    [Fact]
    public void Decode_InvalidCharacter_ThrowsFormatException()
    {
        var act = () => Base32.Decode("INVALID1");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Decode_CaseInsensitive()
    {
        var data = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var encoded = Base32.Encode(data);

        var decoded = Base32.Decode(encoded.ToLowerInvariant());
        decoded.Should().Equal(data);
    }
}
