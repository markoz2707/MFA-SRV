namespace MfaSrv.Core.ValueObjects;

public record EnrollmentContext
{
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public string? UserEmail { get; init; }
    public string? UserPhone { get; init; }
    public string? Issuer { get; init; }
}
