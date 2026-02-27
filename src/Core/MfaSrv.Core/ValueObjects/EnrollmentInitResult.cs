namespace MfaSrv.Core.ValueObjects;

public record EnrollmentInitResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public byte[]? Secret { get; init; }
    public string? ProvisioningUri { get; init; }
    public string? QrCodeDataUri { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}
