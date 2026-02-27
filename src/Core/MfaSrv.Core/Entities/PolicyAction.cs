using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class PolicyAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PolicyId { get; set; } = string.Empty;
    public PolicyActionType ActionType { get; set; }
    public MfaMethod? RequiredMethod { get; set; }

    public Policy? Policy { get; set; }
}
