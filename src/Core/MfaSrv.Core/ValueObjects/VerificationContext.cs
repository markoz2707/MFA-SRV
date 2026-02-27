namespace MfaSrv.Core.ValueObjects;

public record VerificationContext
{
    public required string ChallengeId { get; init; }
    public required string UserId { get; init; }
    public byte[]? EncryptedSecret { get; init; }
    public byte[]? SecretNonce { get; init; }
}
