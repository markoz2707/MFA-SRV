using MfaSrv.Core.Enums;

namespace MfaSrv.Core.Entities;

public class MfaChallenge
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string EnrollmentId { get; set; } = string.Empty;
    public MfaMethod Method { get; set; }
    public ChallengeStatus Status { get; set; } = ChallengeStatus.Issued;
    public string? SourceIp { get; set; }
    public string? TargetResource { get; set; }
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
}
