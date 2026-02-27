using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class PolicyRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RuleGroupId { get; set; } = string.Empty;
    public PolicyRuleType RuleType { get; set; }
    public string Operator { get; set; } = "Equals";
    public string Value { get; set; } = string.Empty;
    public bool Negate { get; set; }

    public PolicyRuleGroup? RuleGroup { get; set; }
}
