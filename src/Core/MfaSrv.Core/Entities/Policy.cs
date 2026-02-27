using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class Policy
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; }
    public FailoverMode FailoverMode { get; set; } = FailoverMode.FailOpen;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<PolicyRuleGroup> RuleGroups { get; set; } = new();
    public List<PolicyAction> Actions { get; set; } = new();
}
