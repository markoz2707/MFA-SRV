using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Services;

public class PolicyEngine : IPolicyEngine
{
    private readonly MfaSrvDbContext _db;
    private readonly ILogger<PolicyEngine> _logger;

    public PolicyEngine(MfaSrvDbContext db, ILogger<PolicyEngine> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PolicyEvaluationResult> EvaluateAsync(AuthenticationContext context, CancellationToken ct = default)
    {
        var policies = await _db.Policies
            .Where(p => p.IsEnabled)
            .Include(p => p.RuleGroups).ThenInclude(g => g.Rules)
            .Include(p => p.Actions)
            .OrderBy(p => p.Priority)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var policy in policies)
        {
            if (EvaluatePolicy(policy, context))
            {
                var action = policy.Actions.FirstOrDefault();
                if (action == null) continue;

                var decision = action.ActionType switch
                {
                    PolicyActionType.RequireMfa => AuthDecision.RequireMfa,
                    PolicyActionType.Deny => AuthDecision.Deny,
                    PolicyActionType.Allow => AuthDecision.Allow,
                    PolicyActionType.AlertOnly => AuthDecision.Allow,
                    _ => AuthDecision.Allow
                };

                _logger.LogInformation("Policy {PolicyName} matched for user {User}: {Decision}",
                    policy.Name, context.UserName, decision);

                return new PolicyEvaluationResult
                {
                    Decision = decision,
                    MatchedPolicyId = policy.Id,
                    MatchedPolicyName = policy.Name,
                    RequiredMethod = action.RequiredMethod,
                    FailoverMode = policy.FailoverMode,
                    Reason = $"Matched policy: {policy.Name}"
                };
            }
        }

        // Default: allow without MFA if no policy matches
        return new PolicyEvaluationResult
        {
            Decision = AuthDecision.Allow,
            Reason = "No matching policy"
        };
    }

    private bool EvaluatePolicy(Policy policy, AuthenticationContext context)
    {
        if (policy.RuleGroups.Count == 0)
            return false;

        // Rule groups are OR'd together, rules within a group are AND'd
        return policy.RuleGroups.Any(group => EvaluateRuleGroup(group, context));
    }

    private bool EvaluateRuleGroup(PolicyRuleGroup group, AuthenticationContext context)
    {
        if (group.Rules.Count == 0)
            return false;

        return group.Rules.All(rule => EvaluateRule(rule, context));
    }

    private bool EvaluateRule(PolicyRule rule, AuthenticationContext context)
    {
        var result = rule.RuleType switch
        {
            PolicyRuleType.SourceUser => MatchString(context.UserName, rule.Operator, rule.Value),
            PolicyRuleType.SourceGroup => context.UserGroups.Any(g => MatchString(g, rule.Operator, rule.Value)),
            PolicyRuleType.SourceIp => MatchString(context.SourceIp ?? "", rule.Operator, rule.Value),
            PolicyRuleType.SourceOu => MatchString(context.UserOu ?? "", rule.Operator, rule.Value),
            PolicyRuleType.TargetResource => MatchString(context.TargetResource ?? "", rule.Operator, rule.Value),
            PolicyRuleType.AuthProtocol => MatchString(context.Protocol.ToString(), rule.Operator, rule.Value),
            PolicyRuleType.TimeWindow => EvaluateTimeWindow(rule.Value),
            PolicyRuleType.RiskScore => false, // not implemented yet
            _ => false
        };

        return rule.Negate ? !result : result;
    }

    private static bool MatchString(string actual, string op, string expected)
    {
        return op.ToLowerInvariant() switch
        {
            "equals" => actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "startswith" => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "endswith" => actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            "regex" => System.Text.RegularExpressions.Regex.IsMatch(actual, expected, System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            _ => actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool EvaluateTimeWindow(string value)
    {
        // Format: "HH:mm-HH:mm" (e.g., "09:00-17:00" for business hours)
        var parts = value.Split('-');
        if (parts.Length != 2) return false;

        if (!TimeOnly.TryParse(parts[0].Trim(), out var start) ||
            !TimeOnly.TryParse(parts[1].Trim(), out var end))
            return false;

        var now = TimeOnly.FromDateTime(DateTime.Now);
        return start <= end
            ? now >= start && now <= end
            : now >= start || now <= end; // handles overnight ranges
    }
}
