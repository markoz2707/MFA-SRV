using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Cryptography;
using MfaSrv.Server.Data;
using MfaSrv.Server.Services;
using Xunit;

namespace MfaSrv.Tests.Unit.Server;

public class SessionManagerTests : IDisposable
{
    private readonly MfaSrvDbContext _db;
    private readonly SessionManager _manager;
    private readonly SessionTokenService _tokenService;

    public SessionManagerTests()
    {
        var options = new DbContextOptionsBuilder<MfaSrvDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new MfaSrvDbContext(options);

        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        _tokenService = new SessionTokenService(key);

        var logger = Mock.Of<ILogger<SessionManager>>();
        _manager = new SessionManager(_db, _tokenService, logger);
    }

    [Fact]
    public async Task CreateSession_CreatesAndReturnsSession()
    {
        var session = await _manager.CreateSessionAsync("user-1", "10.0.0.5", "\\\\fileserver");

        session.Should().NotBeNull();
        session.UserId.Should().Be("user-1");
        session.SourceIp.Should().Be("10.0.0.5");
        session.TargetResource.Should().Be("\\\\fileserver");
        session.Status.Should().Be(SessionStatus.Active);
        session.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        session.TokenHash.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateSession_CustomTtl_SetsExpiry()
    {
        var session = await _manager.CreateSessionAsync("user-1", "10.0.0.5", "", TimeSpan.FromMinutes(30));

        session.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FindActiveSession_FindsMatchingSession()
    {
        await _manager.CreateSessionAsync("user-1", "10.0.0.5", "");

        var found = await _manager.FindActiveSessionAsync("user-1", "10.0.0.5");

        found.Should().NotBeNull();
        found!.UserId.Should().Be("user-1");
    }

    [Fact]
    public async Task FindActiveSession_NoMatch_ReturnsNull()
    {
        await _manager.CreateSessionAsync("user-1", "10.0.0.5", "");

        var found = await _manager.FindActiveSessionAsync("user-2", "10.0.0.5");
        found.Should().BeNull();
    }

    [Fact]
    public async Task RevokeSession_MarksAsRevoked()
    {
        var session = await _manager.CreateSessionAsync("user-1", "10.0.0.5", "");

        await _manager.RevokeSessionAsync(session.Id);

        var dbSession = await _db.MfaSessions.FindAsync(session.Id);
        dbSession!.Status.Should().Be(SessionStatus.Revoked);
    }

    [Fact]
    public async Task FindActiveSession_RevokedSession_ReturnsNull()
    {
        var session = await _manager.CreateSessionAsync("user-1", "10.0.0.5", "");
        await _manager.RevokeSessionAsync(session.Id);

        var found = await _manager.FindActiveSessionAsync("user-1", "10.0.0.5");
        found.Should().BeNull();
    }

    [Fact]
    public async Task CleanupExpiredSessions_MarksExpiredAsExpired()
    {
        // Create an already-expired session
        var session = await _manager.CreateSessionAsync("user-1", "10.0.0.5", "", TimeSpan.FromSeconds(-1));

        // Force the expiry to be in the past
        var dbSession = await _db.MfaSessions.FindAsync(session.Id);
        dbSession!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _db.SaveChangesAsync();

        await _manager.CleanupExpiredSessionsAsync();

        dbSession = await _db.MfaSessions.FindAsync(session.Id);
        dbSession!.Status.Should().Be(SessionStatus.Expired);
    }

    [Fact]
    public async Task CreateSession_SessionIsPersisted()
    {
        var session = await _manager.CreateSessionAsync("user-1", "10.0.0.5", "");

        var count = await _db.MfaSessions.CountAsync();
        count.Should().Be(1);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }
}
