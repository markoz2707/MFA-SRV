using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MfaSrv.Cryptography;

/// <summary>
/// Helpers for generating self-signed certificates for mTLS between agents and server.
/// </summary>
public static class CertificateHelper
{
    public static X509Certificate2 GenerateSelfSignedCertificate(
        string subjectName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        string[]? dnsNames = null)
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={subjectName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new("1.3.6.1.5.5.7.3.1"), // Server Authentication
                    new("1.3.6.1.5.5.7.3.2")  // Client Authentication
                },
                false));

        if (dnsNames is { Length: > 0 })
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var dns in dnsNames)
                sanBuilder.AddDnsName(dns);
            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        var cert = request.CreateSelfSigned(notBefore, notAfter);

        // On Windows, export and re-import to make the private key persistent
        return new X509Certificate2(
            cert.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    public static void ExportToPfx(X509Certificate2 certificate, string filePath, string? password = null)
    {
        var pfxBytes = certificate.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(filePath, pfxBytes);
    }

    public static X509Certificate2 LoadFromPfx(string filePath, string? password = null)
    {
        return new X509Certificate2(filePath, password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>
    /// Generates a self-signed CA certificate with BasicConstraints CA=true.
    /// Used as the root of trust for signing agent certificates.
    /// </summary>
    public static X509Certificate2 GenerateCaCertificate(
        string subjectName,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(4096);

        var request = new CertificateRequest(
            $"CN={subjectName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, true, 1, true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
                true));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var cert = request.CreateSelfSigned(notBefore, notAfter);

        return new X509Certificate2(
            cert.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>
    /// Creates an agent certificate signed by the given CA certificate.
    /// The agent ID is embedded as a SAN DNS name and in the subject CN.
    /// </summary>
    public static X509Certificate2 CreateSignedCertificate(
        string subjectName,
        X509Certificate2 caCert,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        string[]? sanDnsNames = null)
    {
        if (!caCert.HasPrivateKey)
            throw new InvalidOperationException("CA certificate must have a private key to sign certificates.");

        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={subjectName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new("1.3.6.1.5.5.7.3.1"), // Server Authentication
                    new("1.3.6.1.5.5.7.3.2")  // Client Authentication
                },
                false));

        if (sanDnsNames is { Length: > 0 })
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var dns in sanDnsNames)
                sanBuilder.AddDnsName(dns);
            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        // Generate a random serial number
        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        serialNumber[0] &= 0x7F; // Ensure positive

        using var caPrivateKey = caCert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("CA certificate does not contain an RSA private key.");

        var signedCert = request.Create(
            caCert.SubjectName,
            X509SignatureGenerator.CreateForRSA(caPrivateKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            serialNumber);

        // Combine signed cert with the generated private key
        var certWithKey = signedCert.CopyWithPrivateKey(rsa);

        return new X509Certificate2(
            certWithKey.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>
    /// Creates a PKCS#10 Certificate Signing Request (CSR) for the given subject.
    /// Returns the CSR as a PEM-encoded string and the corresponding RSA key pair.
    /// </summary>
    public static (string CsrPem, RSA PrivateKey) CreateCertificateSigningRequest(
        string subjectName,
        string[]? sanDnsNames = null)
    {
        var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={subjectName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new("1.3.6.1.5.5.7.3.2") // Client Authentication
                },
                false));

        if (sanDnsNames is { Length: > 0 })
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var dns in sanDnsNames)
                sanBuilder.AddDnsName(dns);
            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        var csrDer = request.CreateSigningRequest();
        var csrPem = PemEncoding.Write("CERTIFICATE REQUEST", csrDer);

        return (new string(csrPem), rsa);
    }

    /// <summary>
    /// Signs a PEM-encoded CSR with the given CA certificate and returns the signed certificate.
    /// The caller must attach the private key separately if needed.
    /// </summary>
    public static X509Certificate2 SignCertificateRequest(
        string csrPem,
        X509Certificate2 caCert,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        if (!caCert.HasPrivateKey)
            throw new InvalidOperationException("CA certificate must have a private key to sign certificates.");

        // Parse the PEM CSR
        var pemFields = PemEncoding.Find(csrPem);
        var csrDer = Convert.FromBase64String(csrPem[pemFields.Base64Data]);

        var csrRequest = CertificateRequest.LoadSigningRequest(
            csrDer,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions,
            RSASignaturePadding.Pkcs1);

        // Generate a random serial number
        var serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        serialNumber[0] &= 0x7F; // Ensure positive

        using var caPrivateKey = caCert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("CA certificate does not contain an RSA private key.");

        var signedCert = csrRequest.Create(
            caCert.SubjectName,
            X509SignatureGenerator.CreateForRSA(caPrivateKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            serialNumber);

        return new X509Certificate2(
            signedCert.Export(X509ContentType.Cert));
    }

    /// <summary>
    /// Exports a certificate and its CA chain as a PFX file.
    /// </summary>
    public static void ExportCertificateChain(
        X509Certificate2 cert,
        X509Certificate2 caCert,
        string filePath,
        string? password = null)
    {
        var collection = new X509Certificate2Collection { cert, caCert };
        var pfxBytes = collection.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(filePath, pfxBytes!);
    }

    /// <summary>
    /// Exports a certificate as a PEM-encoded string.
    /// </summary>
    public static string ExportToPem(X509Certificate2 certificate)
    {
        var pem = PemEncoding.Write("CERTIFICATE", certificate.RawData);
        return new string(pem);
    }

    /// <summary>
    /// Loads a certificate from a PEM-encoded string.
    /// </summary>
    public static X509Certificate2 LoadFromPem(string pem)
    {
        var pemFields = PemEncoding.Find(pem);
        var certBytes = Convert.FromBase64String(pem[pemFields.Base64Data]);
        return new X509Certificate2(certBytes);
    }
}
