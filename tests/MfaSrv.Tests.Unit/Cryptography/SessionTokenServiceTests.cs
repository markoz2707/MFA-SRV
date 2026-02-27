using FluentAssertions;
using MfaSrv.Cryptography;
using Xunit;

namespace MfaSrv.Tests.Unit.Cryptography;

public class SessionTokenServiceTests
{
    private readonly SessionTokenService _service;

    public SessionTokenServiceTests()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        _service = new SessionTokenService(key);
    }

    [Fact]
    public void GenerateAndValidate_RoundTrip()
    {
        var sessionId = Guid.NewGuid().ToString();
        var userId = "user-123";
        var expiry = DateTimeOffset.UtcNow.AddHours(8);

        var token = _service.GenerateSessionToken(sessionId, userId, expiry);
        var payload = _service.ValidateSessionToken(token);

        payload.Should().NotBeNull();
        payload!.SessionId.Should().Be(sessionId);
        payload.UserId.Should().Be(userId);
    }

    [Fact]
    public void GenerateToken_ProducesCompactToken()
    {
        var token = _service.GenerateSessionToken("sess-1", "user-1", DateTimeOffset.UtcNow.AddHours(1));
        // Should be around 60-120 bytes depending on IDs
        token.Length.Should().BeLessThan(200);
    }

    [Fact]
    public void ValidateToken_ExpiredToken_ReturnsNull()
    {
        var token = _service.GenerateSessionToken("sess-1", "user-1", DateTimeOffset.UtcNow.AddSeconds(-1));
        var payload = _service.ValidateSessionToken(token);
        payload.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_TamperedToken_ReturnsNull()
    {
        var token = _service.GenerateSessionToken("sess-1", "user-1", DateTimeOffset.UtcNow.AddHours(1));
        token[5] ^= 0xFF; // Tamper
        var payload = _service.ValidateSessionToken(token);
        payload.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_DifferentKey_ReturnsNull()
    {
        var token = _service.GenerateSessionToken("sess-1", "user-1", DateTimeOffset.UtcNow.AddHours(1));

        var otherKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(otherKey);
        var otherService = new SessionTokenService(otherKey);

        var payload = otherService.ValidateSessionToken(token);
        payload.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_EmptyArray_ReturnsNull()
    {
        var payload = _service.ValidateSessionToken(Array.Empty<byte>());
        payload.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_TooShort_ReturnsNull()
    {
        var payload = _service.ValidateSessionToken(new byte[] { 1, 2, 3 });
        payload.Should().BeNull();
    }

    [Fact]
    public void Constructor_ShortKey_ThrowsArgumentException()
    {
        var act = () => new SessionTokenService(new byte[16]);
        act.Should().Throw<ArgumentException>();
    }
}
