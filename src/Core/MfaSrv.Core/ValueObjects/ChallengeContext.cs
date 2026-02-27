namespace MfaSrv.Core.ValueObjects;

public record ChallengeContext
{
    public required string UserId { get; init; }
    public required string EnrollmentId { get; init; }
    public string? SourceIp { get; init; }
    public string? TargetResource { get; init; }
    public byte[]? EncryptedSecret { get; init; }
    public byte[]? SecretNonce { get; init; }
}
