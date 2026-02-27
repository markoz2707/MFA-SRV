using MfaSrv.Core.Enums;

namespace MfaSrv.Core.ValueObjects;

public record PolicyEvaluationResult
{
    public required AuthDecision Decision { get; init; }
    public string? MatchedPolicyId { get; init; }
    public string? MatchedPolicyName { get; init; }
    public MfaMethod? RequiredMethod { get; init; }
    public FailoverMode FailoverMode { get; init; } = FailoverMode.FailOpen;
    public string? Reason { get; init; }
}
