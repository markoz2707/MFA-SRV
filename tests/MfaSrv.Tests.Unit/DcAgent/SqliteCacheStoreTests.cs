using Xunit;
using FluentAssertions;
using MfaSrv.Core.Enums;
using MfaSrv.DcAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MfaSrv.Tests.Unit.DcAgent;

public class SqliteCacheStoreTests : IAsyncLifetime
{
    private SqliteCacheStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteCacheStore(":memory:", NullLogger<SqliteCacheStore>.Instance);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        _store.Dispose();
        return Task.CompletedTask;
    }

    // ── Policy Tests ──

    [Fact]
    public async Task SavePolicy_And_LoadAllPolicies_RoundTrip()
    {
        var policy = new CachedPolicy
        {
            PolicyId = "p1",
            Name = "Test Policy",
            PolicyJson = """{"ruleGroups":[]}""",
            FailoverMode = FailoverMode.FailClose,
            Priority = 10,
            IsEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _store.SavePolicyAsync(policy);

        var loaded = await _store.LoadAllPoliciesAsync();

        loaded.Should().HaveCount(1);
        loaded[0].PolicyId.Should().Be("p1");
        loaded[0].Name.Should().Be("Test Policy");
        loaded[0].FailoverMode.Should().Be(FailoverMode.FailClose);
        loaded[0].Priority.Should().Be(10);
        loaded[0].IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SavePolicy_Upsert_UpdatesExisting()
    {
        var policy1 = new CachedPolicy
        {
            PolicyId = "p1",
            Name = "V1",
            PolicyJson = "{}",
            FailoverMode = FailoverMode.FailOpen,
            Priority = 1,
            IsEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var policy2 = new CachedPolicy
        {
            PolicyId = "p1",
            Name = "V2",
            PolicyJson = """{"updated":true}""",
            FailoverMode = FailoverMode.FailClose,
            Priority = 2,
            IsEnabled = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _store.SavePolicyAsync(policy1);
        await _store.SavePolicyAsync(policy2);

        var loaded = await _store.LoadAllPoliciesAsync();

        loaded.Should().HaveCount(1);
        loaded[0].Name.Should().Be("V2");
        loaded[0].FailoverMode.Should().Be(FailoverMode.FailClose);
        loaded[0].IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task RemovePolicy_RemovesFromStore()
    {
        await _store.SavePolicyAsync(new CachedPolicy
        {
            PolicyId = "p-del",
            Name = "ToDelete",
            PolicyJson = "{}",
            FailoverMode = FailoverMode.FailOpen,
            Priority = 1,
            IsEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await _store.RemovePolicyAsync("p-del");

        var loaded = await _store.LoadAllPoliciesAsync();
        loaded.Should().BeEmpty();
    }

    // ── Session Tests ──

    [Fact]
    public async Task SaveSession_And_LoadAllSessions_RoundTrip()
    {
        var session = new CachedSession
        {
            SessionId = "s1",
            UserId = "u1",
            UserName = "testuser",
            SourceIp = "10.0.0.1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            VerifiedMethod = "TOTP",
            Revoked = false
        };

        await _store.SaveSessionAsync(session);

        var loaded = await _store.LoadAllSessionsAsync();

        loaded.Should().HaveCount(1);
        loaded[0].SessionId.Should().Be("s1");
        loaded[0].UserName.Should().Be("testuser");
        loaded[0].SourceIp.Should().Be("10.0.0.1");
        loaded[0].VerifiedMethod.Should().Be("TOTP");
    }

    [Fact]
    public async Task LoadAllSessions_ExcludesExpired()
    {
        await _store.SaveSessionAsync(new CachedSession
        {
            SessionId = "s-active",
            UserId = "u1",
            UserName = "active",
            SourceIp = "10.0.0.1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            VerifiedMethod = "TOTP",
            Revoked = false
        });

        await _store.SaveSessionAsync(new CachedSession
        {
            SessionId = "s-expired",
            UserId = "u2",
            UserName = "expired",
            SourceIp = "10.0.0.2",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            VerifiedMethod = "TOTP",
            Revoked = false
        });

        var loaded = await _store.LoadAllSessionsAsync();

        loaded.Should().HaveCount(1);
        loaded[0].SessionId.Should().Be("s-active");
    }

    [Fact]
    public async Task CleanupExpiredSessions_RemovesExpiredAndRevoked()
    {
        await _store.SaveSessionAsync(new CachedSession
        {
            SessionId = "s-keep",
            UserId = "u1",
            UserName = "keeper",
            SourceIp = "10.0.0.1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            VerifiedMethod = "TOTP",
            Revoked = false
        });

        await _store.SaveSessionAsync(new CachedSession
        {
            SessionId = "s-expired",
            UserId = "u2",
            UserName = "expired",
            SourceIp = "10.0.0.2",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            VerifiedMethod = "TOTP",
            Revoked = false
        });

        await _store.SaveSessionAsync(new CachedSession
        {
            SessionId = "s-revoked",
            UserId = "u3",
            UserName = "revoked",
            SourceIp = "10.0.0.3",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            VerifiedMethod = "TOTP",
            Revoked = true
        });

        var removed = await _store.CleanupExpiredSessionsAsync();

        removed.Should().Be(2);
    }

    [Fact]
    public async Task RemoveSession_RemovesFromStore()
    {
        await _store.SaveSessionAsync(new CachedSession
        {
            SessionId = "s-del",
            UserId = "u1",
            UserName = "todelete",
            SourceIp = "10.0.0.1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            VerifiedMethod = "TOTP",
            Revoked = false
        });

        await _store.RemoveSessionAsync("s-del");

        var loaded = await _store.LoadAllSessionsAsync();
        loaded.Should().BeEmpty();
    }

    // ── Metadata Tests ──

    [Fact]
    public async Task SetMetadata_And_GetMetadata_RoundTrip()
    {
        await _store.SetMetadataAsync("test_key", "test_value");

        var value = await _store.GetMetadataAsync("test_key");

        value.Should().Be("test_value");
    }

    [Fact]
    public async Task SetMetadata_Upsert_UpdatesExisting()
    {
        await _store.SetMetadataAsync("key1", "v1");
        await _store.SetMetadataAsync("key1", "v2");

        var value = await _store.GetMetadataAsync("key1");

        value.Should().Be("v2");
    }

    [Fact]
    public async Task GetMetadata_NonExistentKey_ReturnsNull()
    {
        var value = await _store.GetMetadataAsync("nonexistent");

        value.Should().BeNull();
    }
}
