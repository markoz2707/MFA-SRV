namespace MfaSrv.Core.ValueObjects;

public record VerificationResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public bool ShouldLockout { get; init; }
}
