namespace MfaSrv.Core.Entities;

public class PolicyRuleGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PolicyId { get; set; } = string.Empty;
    public int Order { get; set; }

    public Policy? Policy { get; set; }
    public List<PolicyRule> Rules { get; set; } = new();
}
