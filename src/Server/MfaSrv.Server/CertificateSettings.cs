namespace MfaSrv.Server;

/// <summary>
/// Configuration for the certificate enrollment and management subsystem.
/// Bind to the "Certificates" section in appsettings.json.
/// </summary>
public class CertificateSettings
{
    /// <summary>
    /// Path to the CA certificate PFX file used to sign agent certificates.
    /// </summary>
    public string CaCertificatePath { get; set; } = "certs/ca.pfx";

    /// <summary>
    /// Password for the CA certificate PFX file.
    /// </summary>
    public string CaCertificatePassword { get; set; } = string.Empty;

    /// <summary>
    /// Number of days an agent certificate is valid after signing.
    /// </summary>
    public int AgentCertValidityDays { get; set; } = 365;

    /// <summary>
    /// Number of days before expiry at which an agent should request renewal.
    /// </summary>
    public int RenewalThresholdDays { get; set; } = 30;

    /// <summary>
    /// Path where the Certificate Revocation List is stored.
    /// </summary>
    public string CrlPath { get; set; } = "certs/crl.pem";
}
