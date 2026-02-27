using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;
using FluentAssertions;
using MfaSrv.Cryptography;

namespace MfaSrv.Tests.Unit.Cryptography;

public class CertificateHelperTests
{
    [Fact]
    public void GenerateSelfSignedCertificate_CreatesValidCert()
    {
        var cert = CertificateHelper.GenerateSelfSignedCertificate(
            "TestCert",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        cert.Should().NotBeNull();
        cert.Subject.Should().Contain("CN=TestCert");
        cert.HasPrivateKey.Should().BeTrue();
        cert.NotAfter.Should().BeAfter(DateTime.UtcNow);

        cert.Dispose();
    }

    [Fact]
    public void GenerateSelfSignedCertificate_WithSanNames_IncludesThem()
    {
        var cert = CertificateHelper.GenerateSelfSignedCertificate(
            "TestWithSAN",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            new[] { "agent1.example.com", "agent2.example.com" });

        cert.Should().NotBeNull();

        // Find SAN extension
        var sanExt = cert.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .FirstOrDefault();

        sanExt.Should().NotBeNull();
        var dnsNames = sanExt!.EnumerateDnsNames().ToList();
        dnsNames.Should().Contain("agent1.example.com");
        dnsNames.Should().Contain("agent2.example.com");

        cert.Dispose();
    }

    [Fact]
    public void GenerateCaCertificate_HasCaFlag()
    {
        var caCert = CertificateHelper.GenerateCaCertificate(
            "MfaSrv Test CA",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        caCert.Should().NotBeNull();
        caCert.Subject.Should().Contain("CN=MfaSrv Test CA");
        caCert.HasPrivateKey.Should().BeTrue();

        // Check BasicConstraints CA=true
        var basicConstraints = caCert.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();

        basicConstraints.Should().NotBeNull();
        basicConstraints!.CertificateAuthority.Should().BeTrue();

        caCert.Dispose();
    }

    [Fact]
    public void CreateSignedCertificate_IsSignedByCA()
    {
        using var caCert = CertificateHelper.GenerateCaCertificate(
            "Test CA",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        using var agentCert = CertificateHelper.CreateSignedCertificate(
            "Agent-DC01",
            caCert,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            new[] { "dc01.corp.local" });

        agentCert.Should().NotBeNull();
        agentCert.Subject.Should().Contain("CN=Agent-DC01");
        agentCert.HasPrivateKey.Should().BeTrue();

        // The issuer should be the CA subject
        agentCert.Issuer.Should().Contain("CN=Test CA");

        // Verify the chain
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        var valid = chain.Build(agentCert);
        valid.Should().BeTrue();
    }

    [Fact]
    public void CreateCsr_And_SignIt_ProducesValidCert()
    {
        using var caCert = CertificateHelper.GenerateCaCertificate(
            "Test CA for CSR",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        var (csrPem, privateKey) = CertificateHelper.CreateCertificateSigningRequest(
            "Agent-EP01",
            new[] { "ep01.corp.local" });

        csrPem.Should().Contain("-----BEGIN CERTIFICATE REQUEST-----");
        csrPem.Should().Contain("-----END CERTIFICATE REQUEST-----");

        var signedCert = CertificateHelper.SignCertificateRequest(
            csrPem,
            caCert,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        signedCert.Should().NotBeNull();
        signedCert.Issuer.Should().Contain("CN=Test CA for CSR");

        // Combine with private key
        var certWithKey = signedCert.CopyWithPrivateKey(privateKey);
        certWithKey.HasPrivateKey.Should().BeTrue();

        signedCert.Dispose();
        certWithKey.Dispose();
        privateKey.Dispose();
    }

    [Fact]
    public void ExportToPem_And_LoadFromPem_RoundTrip()
    {
        using var cert = CertificateHelper.GenerateSelfSignedCertificate(
            "PemRoundTrip",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var pem = CertificateHelper.ExportToPem(cert);

        pem.Should().Contain("-----BEGIN CERTIFICATE-----");
        pem.Should().Contain("-----END CERTIFICATE-----");

        using var loaded = CertificateHelper.LoadFromPem(pem);

        loaded.Subject.Should().Be(cert.Subject);
        loaded.Thumbprint.Should().Be(cert.Thumbprint);
    }

    [Fact]
    public void CreateSignedCertificate_WithoutCaPrivateKey_Throws()
    {
        using var caCert = CertificateHelper.GenerateCaCertificate(
            "Test CA",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        // Export CA cert without private key
        var caCertPem = CertificateHelper.ExportToPem(caCert);
        using var caCertNoKey = CertificateHelper.LoadFromPem(caCertPem);

        var action = () => CertificateHelper.CreateSignedCertificate(
            "NoPrivateKey",
            caCertNoKey,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*private key*");
    }
}
