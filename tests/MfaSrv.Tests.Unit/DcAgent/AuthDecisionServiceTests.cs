using Xunit;
using FluentAssertions;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;
using MfaSrv.DcAgent;
using MfaSrv.DcAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MfaSrv.Tests.Unit.DcAgent;

public class AuthDecisionServiceTests
{
    /// <summary>
    /// Creates an AuthDecisionService with in-memory-only caches (no SQLite).
    /// The FailoverManager is configured as "server unavailable" by default
    /// so we can test failover mode logic.
    /// </summary>
    private static (AuthDecisionService Service, SessionCacheService SessionCache, PolicyCacheService PolicyCache, FailoverManager FailoverMgr)
        CreateServices(string failoverMode = "FailOpen")
    {
        var store = CreateInMemorySqliteStore().GetAwaiter().GetResult();

        var sessionCache = new SessionCacheService(
            NullLogger<SessionCacheService>.Instance, store);
        var policyCache = new PolicyCacheService(
            NullLogger<PolicyCacheService>.Instance, store);
        var settings = Options.Create(new DcAgentSettings
        {
            FailoverMode = failoverMode,
            SessionTtlMinutes = 480,
            CentralServerUrl = "https://localhost:5081"
        });

        var failoverMgr = new FailoverManager(
            settings,
            NullLogger<FailoverManager>.Instance);

        var service = new AuthDecisionService(
            sessionCache,
            policyCache,
            failoverMgr,
            settings,
            NullLogger<AuthDecisionService>.Instance);

        return (service, sessionCache, policyCache, failoverMgr);
    }

    private static async Task<SqliteCacheStore> CreateInMemorySqliteStore()
    {
        var store = new SqliteCacheStore(
            ":memory:",
            NullLogger<SqliteCacheStore>.Instance);
        await store.InitializeAsync();
        return store;
    }

    [Fact]
    public async Task EvaluateAsync_CachedSession_ReturnsAllow()
    {
        var (service, sessionCache, _, _) = CreateServices();

        // Add a cached session
        sessionCache.AddOrUpdateSession(new CachedSession
        {
            SessionId = "sess-1",
            UserId = "user1",
            UserName = "testuser",
            SourceIp = "10.0.0.1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            VerifiedMethod = "TOTP",
            Revoked = false
        });

        var query = new AuthQueryMessage
        {
            UserName = "testuser",
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        result.Decision.Should().Be(AuthDecision.Allow);
        result.SessionToken.Should().Be("sess-1");
        result.Reason.Should().Contain("Cached MFA session");
    }

    [Fact]
    public async Task EvaluateAsync_NoCache_ServerUnavailable_FailOpen_ReturnsAllow()
    {
        var (service, _, _, _) = CreateServices("FailOpen");

        var query = new AuthQueryMessage
        {
            UserName = "testuser",
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        result.Decision.Should().Be(AuthDecision.Allow);
        result.Reason.Should().Contain("Fail-open");
    }

    [Fact]
    public async Task EvaluateAsync_NoCache_ServerUnavailable_FailClose_ReturnsDeny()
    {
        var (service, _, _, _) = CreateServices("FailClose");

        var query = new AuthQueryMessage
        {
            UserName = "testuser",
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        result.Decision.Should().Be(AuthDecision.Deny);
        result.Reason.Should().Contain("Fail-close");
    }

    [Fact]
    public async Task EvaluateAsync_CachedOnly_WithSession_ReturnsAllow()
    {
        var (service, sessionCache, _, _) = CreateServices("CachedOnly");

        sessionCache.AddOrUpdateSession(new CachedSession
        {
            SessionId = "sess-cached",
            UserId = "user1",
            UserName = "testuser",
            SourceIp = "10.0.0.1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            VerifiedMethod = "TOTP",
            Revoked = false
        });

        var query = new AuthQueryMessage
        {
            UserName = "testuser",
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        result.Decision.Should().Be(AuthDecision.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_CachedOnly_WithoutSession_ReturnsDeny()
    {
        var (service, _, _, _) = CreateServices("CachedOnly");

        var query = new AuthQueryMessage
        {
            UserName = "unknownuser",
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        result.Decision.Should().Be(AuthDecision.Deny);
        result.Reason.Should().Contain("cached-only");
    }

    [Fact]
    public async Task EvaluateAsync_PolicyOverridesGlobalFailoverMode()
    {
        // Global mode is FailOpen, but a matching policy specifies FailClose
        var (service, _, policyCache, _) = CreateServices("FailOpen");

        policyCache.UpdatePolicy(new CachedPolicy
        {
            PolicyId = "policy-1",
            Name = "HighSecurity",
            PolicyJson = """
            {
                "ruleGroups": [
                    {
                        "rules": [
                            { "ruleType": "SOURCE_USER", "value": "admin", "negate": false }
                        ]
                    }
                ]
            }
            """,
            FailoverMode = FailoverMode.FailClose,
            Priority = 1,
            IsEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var query = new AuthQueryMessage
        {
            UserName = "admin",
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        // Policy says FailClose for admin user
        result.Decision.Should().Be(AuthDecision.Deny);
        result.Reason.Should().Contain("Fail-close");
    }

    [Fact]
    public async Task EvaluateAsync_PolicyDoesNotMatch_FallsToGlobal()
    {
        var (service, _, policyCache, _) = CreateServices("FailOpen");

        policyCache.UpdatePolicy(new CachedPolicy
        {
            PolicyId = "policy-1",
            Name = "AdminOnly",
            PolicyJson = """
            {
                "ruleGroups": [
                    {
                        "rules": [
                            { "ruleType": "SOURCE_USER", "value": "admin", "negate": false }
                        ]
                    }
                ]
            }
            """,
            FailoverMode = FailoverMode.FailClose,
            Priority = 1,
            IsEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var query = new AuthQueryMessage
        {
            UserName = "regularuser",  // does NOT match the admin policy
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        // Falls back to global FailOpen
        result.Decision.Should().Be(AuthDecision.Allow);
        result.Reason.Should().Contain("Fail-open");
    }

    [Fact]
    public async Task EvaluateAsync_NegatedRule_MatchesCorrectly()
    {
        var (service, _, policyCache, _) = CreateServices("FailOpen");

        policyCache.UpdatePolicy(new CachedPolicy
        {
            PolicyId = "policy-negate",
            Name = "NotAdmin",
            PolicyJson = """
            {
                "ruleGroups": [
                    {
                        "rules": [
                            { "ruleType": "SOURCE_USER", "value": "admin", "negate": true }
                        ]
                    }
                ]
            }
            """,
            FailoverMode = FailoverMode.FailClose,
            Priority = 1,
            IsEnabled = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // Non-admin user should match the negated rule
        var query = new AuthQueryMessage
        {
            UserName = "regularuser",
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        result.Decision.Should().Be(AuthDecision.Deny);
    }

    [Fact]
    public async Task EvaluateAsync_ExpiredSession_NotUsed()
    {
        var (service, sessionCache, _, _) = CreateServices("FailClose");

        sessionCache.AddOrUpdateSession(new CachedSession
        {
            SessionId = "sess-expired",
            UserId = "user1",
            UserName = "testuser",
            SourceIp = "10.0.0.1",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), // already expired
            VerifiedMethod = "TOTP",
            Revoked = false
        });

        var query = new AuthQueryMessage
        {
            UserName = "testuser",
            Domain = "CORP",
            SourceIp = "10.0.0.1",
            Protocol = AuthProtocol.Kerberos
        };

        var result = await service.EvaluateAsync(query);

        // Expired session should not count, falls to FailClose
        result.Decision.Should().Be(AuthDecision.Deny);
    }
}
