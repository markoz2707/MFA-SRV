namespace MfaSrv.Provider.Fido2;

public class Fido2Settings
{
    public string ServerDomain { get; set; } = "localhost";
    public string ServerName { get; set; } = "MfaSrv";
    public string Origin { get; set; } = "https://localhost:5080";
    public int ChallengeSize { get; set; } = 32;
    public int ChallengeExpiryMinutes { get; set; } = 5;
}
