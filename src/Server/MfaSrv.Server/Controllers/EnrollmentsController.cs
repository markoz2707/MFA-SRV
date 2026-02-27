using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Cryptography;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EnrollmentsController : ControllerBase
{
    private readonly MfaSrvDbContext _db;
    private readonly IEnumerable<IMfaProvider> _providers;
    private readonly byte[] _encryptionKey;

    public EnrollmentsController(MfaSrvDbContext db, IEnumerable<IMfaProvider> providers, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _db = db;
        _providers = providers;
        // In production, use a proper key management service
        var keyBase64 = config["MfaSrv:EncryptionKey"] ?? Convert.ToBase64String(new byte[32]);
        _encryptionKey = Convert.FromBase64String(keyBase64);
    }

    [HttpGet("user/{userId}")]
    public async Task<IActionResult> GetUserEnrollments(string userId)
    {
        var enrollments = await _db.MfaEnrollments
            .Where(e => e.UserId == userId)
            .Select(e => new
            {
                e.Id,
                e.Method,
                e.Status,
                e.FriendlyName,
                e.DeviceIdentifier,
                e.CreatedAt,
                e.ActivatedAt,
                e.LastUsedAt
            })
            .AsNoTracking()
            .ToListAsync();

        return Ok(enrollments);
    }

    [HttpPost("begin")]
    public async Task<IActionResult> BeginEnrollment([FromBody] BeginEnrollmentRequest request)
    {
        var user = await _db.Users.FindAsync(request.UserId);
        if (user == null) return NotFound("User not found");

        var provider = _providers.FirstOrDefault(p => p.MethodId == request.Method.ToString().ToUpperInvariant());
        if (provider == null) return BadRequest($"Unknown MFA method: {request.Method}");

        var ctx = new EnrollmentContext
        {
            UserId = user.Id,
            UserName = user.UserPrincipalName,
            UserEmail = user.Email,
            UserPhone = user.PhoneNumber,
            Issuer = "MfaSrv"
        };

        var result = await provider.BeginEnrollmentAsync(ctx);

        if (result.Success && result.Secret != null)
        {
            var (encrypted, nonce) = AesGcmEncryption.Encrypt(result.Secret, _encryptionKey);

            var enrollment = new MfaEnrollment
            {
                UserId = user.Id,
                Method = request.Method,
                Status = EnrollmentStatus.Pending,
                EncryptedSecret = encrypted,
                SecretNonce = nonce,
                FriendlyName = request.FriendlyName
            };

            _db.MfaEnrollments.Add(enrollment);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                EnrollmentId = enrollment.Id,
                result.ProvisioningUri,
                result.QrCodeDataUri,
                result.Success
            });
        }

        return BadRequest(new { result.Error });
    }

    [HttpPost("complete")]
    public async Task<IActionResult> CompleteEnrollment([FromBody] CompleteEnrollmentRequest request)
    {
        var enrollment = await _db.MfaEnrollments.FindAsync(request.EnrollmentId);
        if (enrollment == null) return NotFound("Enrollment not found");
        if (enrollment.Status != EnrollmentStatus.Pending)
            return BadRequest("Enrollment is not in pending state");

        var user = await _db.Users.FindAsync(enrollment.UserId);
        if (user == null) return NotFound("User not found");

        var provider = _providers.FirstOrDefault(p => p.MethodId == enrollment.Method.ToString().ToUpperInvariant());
        if (provider == null) return BadRequest("Provider not found");

        // Decrypt secret for verification
        var secret = AesGcmEncryption.Decrypt(enrollment.EncryptedSecret, enrollment.SecretNonce, _encryptionKey);

        var ctx = new EnrollmentContext
        {
            UserId = user.Id,
            UserName = user.UserPrincipalName,
            Issuer = "MfaSrv"
        };

        // For TOTP, the response is the OTP code the user entered to verify
        var result = await provider.CompleteEnrollmentAsync(ctx, request.VerificationCode);

        // Also verify the TOTP code directly
        if (result.Success || TotpGenerator.Validate(secret, request.VerificationCode, DateTimeOffset.UtcNow))
        {
            enrollment.Status = EnrollmentStatus.Active;
            enrollment.ActivatedAt = DateTimeOffset.UtcNow;
            user.MfaEnabled = true;
            await _db.SaveChangesAsync();

            return Ok(new { Success = true });
        }

        return BadRequest(new { Success = false, Error = "Verification failed" });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> RevokeEnrollment(string id)
    {
        var enrollment = await _db.MfaEnrollments.FindAsync(id);
        if (enrollment == null) return NotFound();

        enrollment.Status = EnrollmentStatus.Revoked;
        await _db.SaveChangesAsync();

        // Check if user has any active enrollments left
        var hasActive = await _db.MfaEnrollments
            .AnyAsync(e => e.UserId == enrollment.UserId && e.Status == EnrollmentStatus.Active && e.Id != id);

        if (!hasActive)
        {
            var user = await _db.Users.FindAsync(enrollment.UserId);
            if (user != null)
            {
                user.MfaEnabled = false;
                await _db.SaveChangesAsync();
            }
        }

        return NoContent();
    }
}

public record BeginEnrollmentRequest
{
    public string UserId { get; init; } = string.Empty;
    public MfaMethod Method { get; init; }
    public string? FriendlyName { get; init; }
}

public record CompleteEnrollmentRequest
{
    public string EnrollmentId { get; init; } = string.Empty;
    public string VerificationCode { get; init; } = string.Empty;
}
