using MfaSrv.Core.Enums;

namespace MfaSrv.Core.ValueObjects;

public record AuthQueryMessage
{
    public required string UserName { get; init; }
    public required string Domain { get; init; }
    public string? SourceIp { get; init; }
    public string? Workstation { get; init; }
    public AuthProtocol Protocol { get; init; }
}

public record AuthResponseMessage
{
    public required AuthDecision Decision { get; init; }
    public string? SessionToken { get; init; }
    public string? ChallengeId { get; init; }
    public string? Reason { get; init; }
    public int TimeoutMs { get; init; }
}
