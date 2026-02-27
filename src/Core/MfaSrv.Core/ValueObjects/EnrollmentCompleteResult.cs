namespace MfaSrv.Core.ValueObjects;

public record EnrollmentCompleteResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
}
