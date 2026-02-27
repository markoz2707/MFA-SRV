using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Core.Enums;
using MfaSrv.Cryptography;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Services;

/// <summary>
/// Handles agent certificate enrollment requests.
/// Agents send a CSR, server signs it with the CA key and returns the signed cert.
/// Maintains a revocation list for compromised or decommissioned agent certificates.
/// </summary>
public class CertificateEnrollmentService
{
    private readonly CertificateSettings _settings;
    private readonly MfaSrvDbContext _db;
    private readonly ILogger<CertificateEnrollmentService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedSerials = new();
    private X509Certificate2? _caCert;

    public CertificateEnrollmentService(
        IOptions<CertificateSettings> settings,
        MfaSrvDbContext db,
        ILogger<CertificateEnrollmentService> logger)
    {
        _settings = settings.Value;
        _db = db;
        _logger = logger;

        LoadRevocationList();
    }

    /// <summary>
    /// Enrolls an agent by signing its CSR with the CA certificate.
    /// Returns the signed certificate as a PEM-encoded string.
    /// </summary>
    public async Task<string> EnrollAgentAsync(
        string agentId,
        AgentType agentType,
        string csrPem,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(csrPem);

        // Validate that the agent is registered
        var agent = await _db.AgentRegistrations
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        if (agent == null)
        {
            _logger.LogWarning("Certificate enrollment rejected: agent {AgentId} is not registered", agentId);
            throw new InvalidOperationException($"Agent '{agentId}' is not registered. Register the agent before requesting a certificate.");
        }

        if (agent.AgentType != agentType)
        {
            _logger.LogWarning(
                "Certificate enrollment rejected: agent {AgentId} type mismatch (registered={Registered}, requested={Requested})",
                agentId, agent.AgentType, agentType);
            throw new InvalidOperationException(
                $"Agent type mismatch: agent '{agentId}' is registered as {agent.AgentType}, not {agentType}.");
        }

        // Load the CA certificate
        var caCert = GetCaCertificate();

        // Sign the CSR
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(_settings.AgentCertValidityDays);

        var signedCert = CertificateHelper.SignCertificateRequest(csrPem, caCert, notBefore, notAfter);

        // Update the agent's certificate thumbprint
        agent.CertificateThumbprint = signedCert.Thumbprint;
        await _db.SaveChangesAsync(ct);

        var certPem = CertificateHelper.ExportToPem(signedCert);

        _logger.LogInformation(
            "Enrolled agent {AgentId} (type={AgentType}): SerialNumber={SerialNumber}, Thumbprint={Thumbprint}, ValidUntil={NotAfter}",
            agentId, agentType, signedCert.SerialNumber, signedCert.Thumbprint, signedCert.NotAfter);

        signedCert.Dispose();

        return certPem;
    }

    /// <summary>
    /// Revokes a certificate by its serial number. The serial is added to the in-memory CRL
    /// and persisted to disk.
    /// </summary>
    public async Task RevokeCertificateAsync(string serialNumber, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);

        var revokedAt = DateTimeOffset.UtcNow;
        _revokedSerials[serialNumber] = revokedAt;

        _logger.LogWarning("Certificate revoked: SerialNumber={SerialNumber}", serialNumber);

        // Persist the revocation list
        await SaveRevocationListAsync(ct);
    }

    /// <summary>
    /// Returns the current Certificate Revocation List as a dictionary of
    /// serial numbers and their revocation timestamps.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset> GetCertificateRevocationList()
    {
        return _revokedSerials;
    }

    /// <summary>
    /// Checks whether a certificate serial number has been revoked.
    /// </summary>
    public bool IsCertificateRevoked(string serialNumber)
    {
        return _revokedSerials.ContainsKey(serialNumber);
    }

    private X509Certificate2 GetCaCertificate()
    {
        if (_caCert != null)
            return _caCert;

        if (!File.Exists(_settings.CaCertificatePath))
        {
            _logger.LogWarning(
                "CA certificate not found at {Path}, generating new CA certificate",
                _settings.CaCertificatePath);

            var certDir = Path.GetDirectoryName(_settings.CaCertificatePath);
            if (!string.IsNullOrEmpty(certDir) && !Directory.Exists(certDir))
                Directory.CreateDirectory(certDir);

            var caCert = CertificateHelper.GenerateCaCertificate(
                "MfaSrv Certificate Authority",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddYears(10));

            CertificateHelper.ExportToPfx(
                caCert,
                _settings.CaCertificatePath,
                string.IsNullOrEmpty(_settings.CaCertificatePassword) ? null : _settings.CaCertificatePassword);

            _caCert = caCert;

            _logger.LogInformation(
                "Generated new CA certificate: Thumbprint={Thumbprint}, ValidUntil={NotAfter}",
                caCert.Thumbprint, caCert.NotAfter);

            return _caCert;
        }

        _caCert = CertificateHelper.LoadFromPfx(
            _settings.CaCertificatePath,
            string.IsNullOrEmpty(_settings.CaCertificatePassword) ? null : _settings.CaCertificatePassword);

        _logger.LogInformation(
            "Loaded CA certificate: Thumbprint={Thumbprint}, ValidUntil={NotAfter}",
            _caCert.Thumbprint, _caCert.NotAfter);

        return _caCert;
    }

    private void LoadRevocationList()
    {
        if (!File.Exists(_settings.CrlPath))
        {
            _logger.LogDebug("No existing CRL found at {Path}", _settings.CrlPath);
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_settings.CrlPath);
            foreach (var line in lines)
            {
                var parts = line.Split('|', 2);
                if (parts.Length == 2 && DateTimeOffset.TryParse(parts[1], out var revokedAt))
                {
                    _revokedSerials[parts[0]] = revokedAt;
                }
            }

            _logger.LogInformation("Loaded CRL with {Count} revoked certificates from {Path}",
                _revokedSerials.Count, _settings.CrlPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load CRL from {Path}", _settings.CrlPath);
        }
    }

    private async Task SaveRevocationListAsync(CancellationToken ct)
    {
        try
        {
            var crlDir = Path.GetDirectoryName(_settings.CrlPath);
            if (!string.IsNullOrEmpty(crlDir) && !Directory.Exists(crlDir))
                Directory.CreateDirectory(crlDir);

            var lines = _revokedSerials.Select(kv => $"{kv.Key}|{kv.Value:O}");
            await File.WriteAllLinesAsync(_settings.CrlPath, lines, ct);

            _logger.LogDebug("Saved CRL with {Count} entries to {Path}",
                _revokedSerials.Count, _settings.CrlPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save CRL to {Path}", _settings.CrlPath);
        }
    }
}
