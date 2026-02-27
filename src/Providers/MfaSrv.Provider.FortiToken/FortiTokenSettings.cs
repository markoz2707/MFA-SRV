namespace MfaSrv.Provider.FortiToken;

public class FortiTokenSettings
{
    public string FortiAuthUrl { get; set; } = string.Empty;  // e.g. "https://fortiauth.example.com"
    public string ApiKey { get; set; } = string.Empty;
    public string AdminUser { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public int ChallengeExpiryMinutes { get; set; } = 5;
    public int MaxAttempts { get; set; } = 3;
    public bool UsePushNotification { get; set; } = true;  // FortiToken Mobile push
}
