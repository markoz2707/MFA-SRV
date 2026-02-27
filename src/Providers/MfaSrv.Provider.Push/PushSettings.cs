namespace MfaSrv.Provider.Push;

public class PushSettings
{
    public string FcmServerKey { get; set; } = string.Empty;
    public string FcmSendUrl { get; set; } = "https://fcm.googleapis.com/fcm/send";
    public string ApnsKeyId { get; set; } = string.Empty;
    public string ApnsTeamId { get; set; } = string.Empty;
    public string ApnsBundleId { get; set; } = string.Empty;
    public int ChallengeExpiryMinutes { get; set; } = 5;
    public int PollIntervalSeconds { get; set; } = 2;
}
