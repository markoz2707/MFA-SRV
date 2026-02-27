using MfaSrv.Core.Enums;

namespace MfaSrv.Core.ValueObjects;

public record ChallengeResult
{
    public required bool Success { get; init; }
    public string? ChallengeId { get; init; }
    public string? Error { get; init; }
    public ChallengeStatus Status { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? UserPrompt { get; init; }
}
