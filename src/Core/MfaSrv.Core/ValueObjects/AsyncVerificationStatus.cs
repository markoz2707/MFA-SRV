using MfaSrv.Core.Enums;

namespace MfaSrv.Core.ValueObjects;

public record AsyncVerificationStatus
{
    public required ChallengeStatus Status { get; init; }
    public string? Error { get; init; }
}
