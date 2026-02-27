namespace MfaSrv.Provider.Sms;

public class SmsSettings
{
    public string GatewayUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;
    public string MessageTemplate { get; set; } = "Your MfaSrv verification code is: {code}. Valid for 5 minutes.";
    public int CodeLength { get; set; } = 6;
    public int CodeExpiryMinutes { get; set; } = 5;
    public int MaxAttempts { get; set; } = 3;
}
