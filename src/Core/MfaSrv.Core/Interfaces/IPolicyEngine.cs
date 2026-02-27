using MfaSrv.Core.ValueObjects;

namespace MfaSrv.Core.Interfaces;

public interface IPolicyEngine
{
    Task<PolicyEvaluationResult> EvaluateAsync(AuthenticationContext context, CancellationToken ct = default);
}
