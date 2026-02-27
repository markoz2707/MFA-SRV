using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace MfaSrv.Cryptography;

/// <summary>
/// Manages agent certificates: generation, storage, renewal, and validation.
/// Used by DC Agent and Endpoint Agent for mTLS with Central Server.
/// </summary>
public class CertificateManager
{
    private readonly string _certStorePath;
    private readonly X509Certificate2 _caCert;
    private readonly ILogger<CertificateManager> _logger;

    public CertificateManager(
        string certStorePath,
        X509Certificate2 caCert,
        ILogger<CertificateManager> logger)
    {
        _certStorePath = certStorePath ?? throw new ArgumentNullException(nameof(certStorePath));
        _caCert = caCert ?? throw new ArgumentNullException(nameof(caCert));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Ensure the certificate store directory exists
        if (!Directory.Exists(_certStorePath))
        {
            Directory.CreateDirectory(_certStorePath);
            _logger.LogInformation("Created certificate store directory: {Path}", _certStorePath);
        }
    }

    /// <summary>
    /// Generates a new agent certificate signed by the CA.
    /// The agent ID is embedded as a SAN DNS name for identification during mTLS.
    /// </summary>
    public X509Certificate2 GenerateAgentCertificate(string agentId, string agentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentType);

        var subjectName = $"MfaSrv-{agentType}-{agentId}";
        var sanDnsNames = new[] { agentId, Environment.MachineName };

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5); // Small clock skew allowance
        var notAfter = DateTimeOffset.UtcNow.AddDays(365);

        _logger.LogInformation(
            "Generating agent certificate for {AgentId} (type={AgentType}), valid until {NotAfter}",
            agentId, agentType, notAfter);

        var cert = CertificateHelper.CreateSignedCertificate(
            subjectName,
            _caCert,
            notBefore,
            notAfter,
            sanDnsNames);

        _logger.LogInformation(
            "Generated agent certificate: Subject={Subject}, Thumbprint={Thumbprint}, SerialNumber={SerialNumber}",
            cert.Subject, cert.Thumbprint, cert.SerialNumber);

        return cert;
    }

    /// <summary>
    /// Loads an existing agent certificate from disk, or creates a new one if missing or expiring soon.
    /// If the certificate expires within 30 days, it is automatically renewed.
    /// </summary>
    public X509Certificate2 LoadOrCreateAgentCertificate(
        string agentId,
        string certPath,
        string? password = null,
        string agentType = "Agent")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(certPath);

        if (File.Exists(certPath))
        {
            try
            {
                var existingCert = CertificateHelper.LoadFromPfx(certPath, password);

                if (!IsCertificateExpiringSoon(existingCert))
                {
                    _logger.LogDebug(
                        "Loaded existing agent certificate: Thumbprint={Thumbprint}, ExpiresAt={NotAfter}",
                        existingCert.Thumbprint, existingCert.NotAfter);
                    return existingCert;
                }

                _logger.LogWarning(
                    "Agent certificate is expiring soon (NotAfter={NotAfter}), generating replacement",
                    existingCert.NotAfter);

                existingCert.Dispose();
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning(ex, "Failed to load existing certificate from {Path}, generating new one", certPath);
            }
        }
        else
        {
            _logger.LogInformation("No existing certificate found at {Path}, generating new one", certPath);
        }

        // Generate new certificate
        var cert = GenerateAgentCertificate(agentId, agentType);

        // Ensure parent directory exists
        var certDir = Path.GetDirectoryName(certPath);
        if (!string.IsNullOrEmpty(certDir) && !Directory.Exists(certDir))
            Directory.CreateDirectory(certDir);

        CertificateHelper.ExportToPfx(cert, certPath, password);
        _logger.LogInformation("Saved new agent certificate to {Path}", certPath);

        return cert;
    }

    /// <summary>
    /// Validates an agent certificate by checking expiry, issuer, and key usage.
    /// </summary>
    public bool ValidateAgentCertificate(X509Certificate2 cert)
    {
        if (cert == null)
        {
            _logger.LogWarning("Certificate validation failed: certificate is null");
            return false;
        }

        // Check expiry
        if (cert.NotAfter < DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Certificate validation failed: certificate expired on {NotAfter}",
                cert.NotAfter);
            return false;
        }

        if (cert.NotBefore > DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Certificate validation failed: certificate not yet valid (NotBefore={NotBefore})",
                cert.NotBefore);
            return false;
        }

        // Check issuer matches our CA
        if (cert.Issuer != _caCert.Subject)
        {
            _logger.LogWarning(
                "Certificate validation failed: issuer mismatch (expected={Expected}, actual={Actual})",
                _caCert.Subject, cert.Issuer);
            return false;
        }

        // Verify the certificate chain
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.Add(_caCert);
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_caCert);

        var isValid = chain.Build(cert);

        if (!isValid)
        {
            foreach (var status in chain.ChainStatus)
            {
                _logger.LogWarning(
                    "Certificate chain validation status: {Status} - {StatusInformation}",
                    status.Status, status.StatusInformation);
            }
        }

        // Check for client authentication EKU
        var hasClientAuth = false;
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509EnhancedKeyUsageExtension ekuExt)
            {
                foreach (var oid in ekuExt.EnhancedKeyUsages)
                {
                    if (oid.Value == "1.3.6.1.5.5.7.3.2") // Client Authentication
                    {
                        hasClientAuth = true;
                        break;
                    }
                }
            }
        }

        if (!hasClientAuth)
        {
            _logger.LogWarning("Certificate validation failed: missing Client Authentication EKU");
            return false;
        }

        return isValid;
    }

    /// <summary>
    /// Returns true if the certificate will expire within the given number of days.
    /// </summary>
    public bool IsCertificateExpiringSoon(X509Certificate2 cert, int daysThreshold = 30)
    {
        var expiryThreshold = DateTime.UtcNow.AddDays(daysThreshold);
        return cert.NotAfter <= expiryThreshold;
    }

    /// <summary>
    /// Generates a PKCS#10 Certificate Signing Request for the given agent.
    /// Returns the CSR as a PEM-encoded string. The private key is saved to disk
    /// so it can be paired with the signed certificate later.
    /// </summary>
    public (string CsrPem, string PrivateKeyPath) CreateCertificateSigningRequest(
        string agentId,
        string agentType = "Agent")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var subjectName = $"MfaSrv-{agentType}-{agentId}";
        var sanDnsNames = new[] { agentId, Environment.MachineName };

        var (csrPem, privateKey) = CertificateHelper.CreateCertificateSigningRequest(subjectName, sanDnsNames);

        // Save the private key so it can be combined with the signed cert later
        var keyPath = Path.Combine(_certStorePath, $"{agentId}.key.pem");
        var keyPem = privateKey.ExportRSAPrivateKeyPem();
        File.WriteAllText(keyPath, keyPem);

        _logger.LogInformation(
            "Generated CSR for agent {AgentId}, private key saved to {KeyPath}",
            agentId, keyPath);

        // Dispose the RSA key — we've saved it to disk
        privateKey.Dispose();

        return (csrPem, keyPath);
    }

    /// <summary>
    /// Signs a PEM-encoded CSR with the CA certificate.
    /// </summary>
    public X509Certificate2 SignCertificateRequest(string csrPem, int validDays = 365)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csrPem);

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(validDays);

        var signedCert = CertificateHelper.SignCertificateRequest(csrPem, _caCert, notBefore, notAfter);

        _logger.LogInformation(
            "Signed CSR: Subject={Subject}, SerialNumber={SerialNumber}, ValidUntil={NotAfter}",
            signedCert.Subject, signedCert.SerialNumber, signedCert.NotAfter);

        return signedCert;
    }

    /// <summary>
    /// Exports the agent certificate along with the CA certificate as a PFX chain file.
    /// </summary>
    public void ExportCertificateChain(
        X509Certificate2 cert,
        string filePath,
        string? password = null)
    {
        CertificateHelper.ExportCertificateChain(cert, _caCert, filePath, password);
        _logger.LogInformation("Exported certificate chain to {Path}", filePath);
    }

    /// <summary>
    /// Combines a signed certificate (PEM) with a previously saved private key to produce
    /// a full X509Certificate2 with private key.
    /// </summary>
    public X509Certificate2 CombineWithPrivateKey(string signedCertPem, string privateKeyPath)
    {
        var cert = CertificateHelper.LoadFromPem(signedCertPem);
        var keyPem = File.ReadAllText(privateKeyPath);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(keyPem);

        var certWithKey = cert.CopyWithPrivateKey(rsa);

        var result = new X509Certificate2(
            certWithKey.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        cert.Dispose();
        certWithKey.Dispose();

        _logger.LogInformation(
            "Combined signed certificate with private key: Thumbprint={Thumbprint}",
            result.Thumbprint);

        return result;
    }
}
