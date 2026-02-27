using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Cryptography;
using MfaSrv.Protocol;

namespace MfaSrv.DcAgent.Services;

/// <summary>
/// Background service that manages the DC Agent's mTLS certificate.
/// Auto-enrolls on first start, auto-renews before expiry.
/// Other services read <see cref="CurrentCertificate"/> to create mTLS gRPC channels.
/// </summary>
public class CertificateService : BackgroundService
{
    private readonly DcAgentSettings _settings;
    private readonly ILogger<CertificateService> _logger;
    private readonly object _certLock = new();

    private static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromHours(12);
    private static readonly int RenewalThresholdDays = 30;

    private X509Certificate2? _currentCertificate;

    /// <summary>
    /// The current agent certificate for mTLS. Null if enrollment has not yet completed.
    /// </summary>
    public X509Certificate2? CurrentCertificate
    {
        get
        {
            lock (_certLock)
            {
                return _currentCertificate;
            }
        }
        private set
        {
            lock (_certLock)
            {
                _currentCertificate = value;
            }
        }
    }

    public CertificateService(
        IOptions<DcAgentSettings> settings,
        ILogger<CertificateService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Certificate service starting for agent {AgentId}", _settings.AgentId);

        // Initial certificate load or enrollment
        await EnsureCertificateAsync(stoppingToken);

        // Periodically check certificate expiry
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ExpiryCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var cert = CurrentCertificate;
                if (cert == null || IsCertificateExpiringSoon(cert))
                {
                    _logger.LogInformation(
                        "Certificate is missing or expiring soon, initiating renewal");
                    await RenewCertificateAsync(stoppingToken);
                }
                else
                {
                    _logger.LogDebug(
                        "Certificate is valid until {NotAfter}, next check in {Interval}",
                        cert.NotAfter, ExpiryCheckInterval);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during certificate expiry check");
            }
        }

        _logger.LogInformation("Certificate service shutting down");
    }

    /// <summary>
    /// Loads an existing certificate from disk, or performs initial enrollment via the Central Server.
    /// </summary>
    private async Task EnsureCertificateAsync(CancellationToken ct)
    {
        var certPath = _settings.CertificatePath;

        // Try to load an existing certificate
        if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
        {
            try
            {
                var cert = CertificateHelper.LoadFromPfx(certPath,
                    string.IsNullOrEmpty(_settings.CertificatePassword) ? null : _settings.CertificatePassword);

                if (!IsCertificateExpiringSoon(cert))
                {
                    CurrentCertificate = cert;
                    _logger.LogInformation(
                        "Loaded existing certificate: Thumbprint={Thumbprint}, ValidUntil={NotAfter}",
                        cert.Thumbprint, cert.NotAfter);
                    return;
                }

                _logger.LogWarning(
                    "Existing certificate is expiring soon (NotAfter={NotAfter}), requesting renewal",
                    cert.NotAfter);
                cert.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load certificate from {Path}", certPath);
            }
        }

        // No valid certificate found — enroll via the Central Server
        await EnrollViaCentralServerAsync(ct);
    }

    /// <summary>
    /// Generates a CSR, sends it to the Central Server for signing,
    /// and saves the resulting certificate to disk.
    /// </summary>
    private async Task EnrollViaCentralServerAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting certificate enrollment with Central Server at {Url}", _settings.CentralServerUrl);

        try
        {
            // Generate CSR
            var subjectName = $"MfaSrv-DcAgent-{_settings.AgentId}";
            var sanDnsNames = new[] { _settings.AgentId, Environment.MachineName };
            var (csrPem, privateKey) = CertificateHelper.CreateCertificateSigningRequest(subjectName, sanDnsNames);

            try
            {
                // Send CSR to the Central Server via gRPC
                using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(_settings.CentralServerUrl);
                var client = new MfaService.MfaServiceClient(channel);

                var response = await client.EnrollCertificateAsync(new EnrollCertificateRequest
                {
                    AgentId = _settings.AgentId,
                    AgentType = AgentTypeEnum.AgentTypeDc,
                    CsrPem = csrPem
                }, cancellationToken: ct);

                if (!response.Success)
                {
                    _logger.LogError("Certificate enrollment rejected by server: {Error}", response.ErrorMessage);
                    return;
                }

                // Combine signed cert with our private key
                var signedCert = CertificateHelper.LoadFromPem(response.SignedCertPem);
                var certWithKey = signedCert.CopyWithPrivateKey(privateKey);

                var result = new X509Certificate2(
                    certWithKey.Export(X509ContentType.Pfx),
                    (string?)null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

                signedCert.Dispose();
                certWithKey.Dispose();

                // Save to disk
                var certPath = _settings.CertificatePath;
                if (string.IsNullOrEmpty(certPath))
                    certPath = Path.Combine(AppContext.BaseDirectory, "certs", $"{_settings.AgentId}.pfx");

                var certDir = Path.GetDirectoryName(certPath);
                if (!string.IsNullOrEmpty(certDir) && !Directory.Exists(certDir))
                    Directory.CreateDirectory(certDir);

                CertificateHelper.ExportToPfx(result, certPath,
                    string.IsNullOrEmpty(_settings.CertificatePassword) ? null : _settings.CertificatePassword);

                CurrentCertificate = result;

                _logger.LogInformation(
                    "Certificate enrollment successful: Thumbprint={Thumbprint}, ValidUntil={NotAfter}, Saved={Path}",
                    result.Thumbprint, result.NotAfter, certPath);
            }
            finally
            {
                privateKey.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Certificate enrollment failed");
        }
    }

    /// <summary>
    /// Renews the current certificate by performing a new CSR enrollment.
    /// </summary>
    private async Task RenewCertificateAsync(CancellationToken ct)
    {
        _logger.LogInformation("Initiating certificate renewal for agent {AgentId}", _settings.AgentId);

        var oldCert = CurrentCertificate;
        await EnrollViaCentralServerAsync(ct);

        if (CurrentCertificate != null && CurrentCertificate != oldCert)
        {
            _logger.LogInformation(
                "Certificate renewed: OldThumbprint={OldThumbprint}, NewThumbprint={NewThumbprint}",
                oldCert?.Thumbprint ?? "none", CurrentCertificate.Thumbprint);

            oldCert?.Dispose();
        }
    }

    private static bool IsCertificateExpiringSoon(X509Certificate2 cert)
    {
        return cert.NotAfter <= DateTime.UtcNow.AddDays(RenewalThresholdDays);
    }

    public override void Dispose()
    {
        _currentCertificate?.Dispose();
        base.Dispose();
    }
}
