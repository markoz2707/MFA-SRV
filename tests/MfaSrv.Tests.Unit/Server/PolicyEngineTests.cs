using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Server.Data;
using MfaSrv.Server.Services;
using Xunit;

namespace MfaSrv.Tests.Unit.Server;

public class PolicyEngineTests : IDisposable
{
    private readonly MfaSrvDbContext _db;
    private readonly PolicyEngine _engine;

    public PolicyEngineTests()
    {
        var options = new DbContextOptionsBuilder<MfaSrvDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new MfaSrvDbContext(options);
        var logger = Mock.Of<ILogger<PolicyEngine>>();
        _engine = new PolicyEngine(_db, logger);
    }

    [Fact]
    public async Task Evaluate_NoPolicies_ReturnsAllow()
    {
        var context = CreateAuthContext("admin", groups: new[] { "Domain Admins" });

        var result = await _engine.EvaluateAsync(context);

        result.Decision.Should().Be(AuthDecision.Allow);
        result.MatchedPolicyId.Should().BeNull();
        result.Reason.Should().Contain("No matching policy");
    }

    [Fact]
    public async Task Evaluate_GroupBasedPolicy_MatchesRequireMfa()
    {
        // Arrange: Policy requires MFA for "Domain Admins" group
        var policy = CreatePolicy("Admin MFA", priority: 1,
            ruleType: PolicyRuleType.SourceGroup, ruleValue: "Domain Admins",
            action: PolicyActionType.RequireMfa);

        _db.Policies.Add(policy);
        await _db.SaveChangesAsync();

        var context = CreateAuthContext("admin", groups: new[] { "Domain Admins" });

        // Act
        var result = await _engine.EvaluateAsync(context);

        // Assert
        result.Decision.Should().Be(AuthDecision.RequireMfa);
        result.MatchedPolicyId.Should().Be(policy.Id);
        result.MatchedPolicyName.Should().Be("Admin MFA");
    }

    [Fact]
    public async Task Evaluate_GroupPolicy_NoMatch_ReturnsAllow()
    {
        var policy = CreatePolicy("Admin MFA", priority: 1,
            ruleType: PolicyRuleType.SourceGroup, ruleValue: "Domain Admins",
            action: PolicyActionType.RequireMfa);

        _db.Policies.Add(policy);
        await _db.SaveChangesAsync();

        var context = CreateAuthContext("regular-user", groups: new[] { "Domain Users" });

        var result = await _engine.EvaluateAsync(context);

        result.Decision.Should().Be(AuthDecision.Allow);
        result.MatchedPolicyId.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_DisabledPolicy_IsSkipped()
    {
        var policy = CreatePolicy("Disabled Policy", priority: 1,
            ruleType: PolicyRuleType.SourceGroup, ruleValue: "Domain Admins",
            action: PolicyActionType.Deny);
        policy.IsEnabled = false;

        _db.Policies.Add(policy);
        await _db.SaveChangesAsync();

        var context = CreateAuthContext("admin", groups: new[] { "Domain Admins" });

        var result = await _engine.EvaluateAsync(context);
        result.Decision.Should().Be(AuthDecision.Allow);
    }

    [Fact]
    public async Task Evaluate_PriorityOrdering_HigherPriorityWins()
    {
        var policyAllow = CreatePolicy("Allow Policy", priority: 10,
            ruleType: PolicyRuleType.SourceUser, ruleValue: "admin",
            action: PolicyActionType.Allow);

        var policyDeny = CreatePolicy("Deny Policy", priority: 1,
            ruleType: PolicyRuleType.SourceUser, ruleValue: "admin",
            action: PolicyActionType.Deny);

        _db.Policies.AddRange(policyAllow, policyDeny);
        await _db.SaveChangesAsync();

        var context = CreateAuthContext("admin");

        var result = await _engine.EvaluateAsync(context);

        // Priority 1 evaluated first
        result.Decision.Should().Be(AuthDecision.Deny);
        result.MatchedPolicyName.Should().Be("Deny Policy");
    }

    [Fact]
    public async Task Evaluate_NegatedRule_InvertsMatch()
    {
        var policy = new Policy
        {
            Name = "Non-Admin MFA",
            Priority = 1,
            IsEnabled = true,
            FailoverMode = FailoverMode.FailOpen
        };

        var group = new PolicyRuleGroup { PolicyId = policy.Id, Order = 0 };
        group.Rules.Add(new PolicyRule
        {
            RuleGroupId = group.Id,
            RuleType = PolicyRuleType.SourceGroup,
            Operator = "Equals",
            Value = "Domain Admins",
            Negate = true // NOT in Domain Admins
        });
        policy.RuleGroups.Add(group);
        policy.Actions.Add(new PolicyAction
        {
            PolicyId = policy.Id,
            ActionType = PolicyActionType.RequireMfa
        });

        _db.Policies.Add(policy);
        await _db.SaveChangesAsync();

        // User NOT in Domain Admins should match the negated rule
        var context = CreateAuthContext("user1", groups: new[] { "Domain Users" });
        var result = await _engine.EvaluateAsync(context);
        result.Decision.Should().Be(AuthDecision.RequireMfa);

        // User IN Domain Admins should NOT match the negated rule
        var context2 = CreateAuthContext("admin", groups: new[] { "Domain Admins" });
        var result2 = await _engine.EvaluateAsync(context2);
        result2.Decision.Should().Be(AuthDecision.Allow);
    }

    [Fact]
    public async Task Evaluate_IpBasedRule_Matches()
    {
        var policy = CreatePolicy("Block External", priority: 1,
            ruleType: PolicyRuleType.SourceIp, ruleValue: "192.168.",
            action: PolicyActionType.RequireMfa, ruleOperator: "StartsWith");

        _db.Policies.Add(policy);
        await _db.SaveChangesAsync();

        var context = CreateAuthContext("user", sourceIp: "192.168.1.100");
        var result = await _engine.EvaluateAsync(context);
        result.Decision.Should().Be(AuthDecision.RequireMfa);

        var context2 = CreateAuthContext("user", sourceIp: "10.0.0.5");
        var result2 = await _engine.EvaluateAsync(context2);
        result2.Decision.Should().Be(AuthDecision.Allow);
    }

    [Fact]
    public async Task Evaluate_MultipleRulesInGroup_AllMustMatch()
    {
        // AND logic within a rule group
        var policy = new Policy
        {
            Name = "Admin + Internal",
            Priority = 1,
            IsEnabled = true
        };

        var group = new PolicyRuleGroup { PolicyId = policy.Id, Order = 0 };
        group.Rules.Add(new PolicyRule
        {
            RuleGroupId = group.Id,
            RuleType = PolicyRuleType.SourceGroup,
            Value = "Domain Admins"
        });
        group.Rules.Add(new PolicyRule
        {
            RuleGroupId = group.Id,
            RuleType = PolicyRuleType.SourceIp,
            Operator = "StartsWith",
            Value = "10.0."
        });
        policy.RuleGroups.Add(group);
        policy.Actions.Add(new PolicyAction
        {
            PolicyId = policy.Id,
            ActionType = PolicyActionType.RequireMfa
        });

        _db.Policies.Add(policy);
        await _db.SaveChangesAsync();

        // Both rules match
        var ctx1 = CreateAuthContext("admin", groups: new[] { "Domain Admins" }, sourceIp: "10.0.0.5");
        (await _engine.EvaluateAsync(ctx1)).Decision.Should().Be(AuthDecision.RequireMfa);

        // Only group matches
        var ctx2 = CreateAuthContext("admin", groups: new[] { "Domain Admins" }, sourceIp: "192.168.1.1");
        (await _engine.EvaluateAsync(ctx2)).Decision.Should().Be(AuthDecision.Allow);
    }

    [Fact]
    public async Task Evaluate_FailoverModeSetInResult()
    {
        var policy = CreatePolicy("Secure Policy", priority: 1,
            ruleType: PolicyRuleType.SourceGroup, ruleValue: "VPN Users",
            action: PolicyActionType.RequireMfa);
        policy.FailoverMode = FailoverMode.FailClose;

        _db.Policies.Add(policy);
        await _db.SaveChangesAsync();

        var context = CreateAuthContext("vpnuser", groups: new[] { "VPN Users" });
        var result = await _engine.EvaluateAsync(context);

        result.FailoverMode.Should().Be(FailoverMode.FailClose);
    }

    private static AuthenticationContext CreateAuthContext(
        string userName,
        string[]? groups = null,
        string? sourceIp = null,
        string? targetResource = null)
    {
        return new AuthenticationContext
        {
            UserId = Guid.NewGuid().ToString(),
            UserName = userName,
            UserGroups = groups ?? Array.Empty<string>(),
            SourceIp = sourceIp,
            TargetResource = targetResource,
            Protocol = AuthProtocol.Kerberos
        };
    }

    private static Policy CreatePolicy(
        string name,
        int priority,
        PolicyRuleType ruleType,
        string ruleValue,
        PolicyActionType action,
        string ruleOperator = "Equals")
    {
        var policy = new Policy
        {
            Name = name,
            Priority = priority,
            IsEnabled = true,
            FailoverMode = FailoverMode.FailOpen
        };

        var group = new PolicyRuleGroup { PolicyId = policy.Id, Order = 0 };
        group.Rules.Add(new PolicyRule
        {
            RuleGroupId = group.Id,
            RuleType = ruleType,
            Operator = ruleOperator,
            Value = ruleValue
        });
        policy.RuleGroups.Add(group);
        policy.Actions.Add(new PolicyAction
        {
            PolicyId = policy.Id,
            ActionType = action
        });

        return policy;
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }
}
