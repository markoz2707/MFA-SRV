using MfaSrv.Core.Enums;

namespace MfaSrv.Core.ValueObjects;

public record AuthenticationContext
{
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public string? SourceIp { get; init; }
    public string? TargetResource { get; init; }
    public AuthProtocol Protocol { get; init; }
    public IReadOnlyList<string> UserGroups { get; init; } = Array.Empty<string>();
    public string? UserOu { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
