namespace MfaSrv.Provider.Email;

public class EmailSettings
{
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 25;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool UseSsl { get; set; }
    public string FromAddress { get; set; } = "mfasrv@example.com";
    public string FromName { get; set; } = "MfaSrv Authentication";
    public string SubjectTemplate { get; set; } = "Your MfaSrv Verification Code";
    public string BodyTemplate { get; set; } = "<html><body><h2>Your verification code is: <strong>{code}</strong></h2><p>This code is valid for 5 minutes.</p><p>If you did not request this code, please contact your administrator.</p></body></html>";
    public int CodeLength { get; set; } = 6;
    public int CodeExpiryMinutes { get; set; } = 5;
    public int MaxAttempts { get; set; } = 3;
}
