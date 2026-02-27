using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Core.ValueObjects;
using MfaSrv.Cryptography;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Controllers;

/// <summary>
/// Self-service enrollment API for end users to manage their own MFA methods.
/// In production, this would be secured with Windows Authentication or OIDC
/// so users can only manage their own enrollments.
/// </summary>
[ApiController]
[Route("api/self-enrollment")]
public class SelfEnrollmentController : ControllerBase
{
    private readonly MfaSrvDbContext _db;
    private readonly IEnumerable<IMfaProvider> _providers;
    private readonly IAuditLogger _auditLogger;
    private readonly byte[] _encryptionKey;

    public SelfEnrollmentController(
        MfaSrvDbContext db,
        IEnumerable<IMfaProvider> providers,
        IAuditLogger auditLogger,
        IConfiguration config)
    {
        _db = db;
        _providers = providers;
        _auditLogger = auditLogger;
        var keyBase64 = config["MfaSrv:EncryptionKey"] ?? Convert.ToBase64String(new byte[32]);
        _encryptionKey = Convert.FromBase64String(keyBase64);
    }

    /// <summary>
    /// Get available MFA methods and current enrollment status for a user.
    /// </summary>
    [HttpGet("{userId}/status")]
    public async Task<IActionResult> GetEnrollmentStatus(string userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found");

        var enrollments = await _db.MfaEnrollments
            .Where(e => e.UserId == userId)
            .Select(e => new
            {
                e.Id,
                Method = e.Method.ToString(),
                Status = e.Status.ToString(),
                e.FriendlyName,
                e.CreatedAt,
                e.ActivatedAt,
                e.LastUsedAt
            })
            .AsNoTracking()
            .ToListAsync();

        var availableMethods = _providers.Select(p => new
        {
            p.MethodId,
            p.DisplayName,
            p.RequiresEndpointAgent,
            SupportsSync = p.SupportsSynchronousVerification,
            SupportsAsync = p.SupportsAsynchronousVerification
        });

        return Ok(new
        {
            UserId = userId,
            UserName = user.UserPrincipalName,
            user.MfaEnabled,
            Enrollments = enrollments,
            AvailableMethods = availableMethods
        });
    }

    /// <summary>
    /// Begin enrolling a new MFA method.
    /// </summary>
    [HttpPost("{userId}/begin")]
    public async Task<IActionResult> BeginEnrollment(string userId, [FromBody] SelfEnrollmentBeginRequest request)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found");

        // Check if already enrolled with this method (active)
        var existing = await _db.MfaEnrollments
            .AnyAsync(e => e.UserId == userId && e.Method == request.Method && e.Status == EnrollmentStatus.Active);

        if (existing)
            return Conflict($"User already has an active {request.Method} enrollment. Revoke it first to re-enroll.");

        // Clean up any previous pending enrollments for this method
        var pendingEnrollments = await _db.MfaEnrollments
            .Where(e => e.UserId == userId && e.Method == request.Method && e.Status == EnrollmentStatus.Pending)
            .ToListAsync();
        _db.MfaEnrollments.RemoveRange(pendingEnrollments);

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

        if (!result.Success)
            return BadRequest(new { result.Error });

        byte[] encryptedSecret = Array.Empty<byte>();
        byte[] secretNonce = Array.Empty<byte>();

        if (result.Secret != null)
        {
            var (encrypted, nonce) = AesGcmEncryption.Encrypt(result.Secret, _encryptionKey);
            encryptedSecret = encrypted;
            secretNonce = nonce;
        }

        var enrollment = new MfaEnrollment
        {
            UserId = user.Id,
            Method = request.Method,
            Status = EnrollmentStatus.Pending,
            EncryptedSecret = encryptedSecret,
            SecretNonce = secretNonce,
            FriendlyName = request.FriendlyName ?? $"{request.Method} - {DateTimeOffset.UtcNow:yyyy-MM-dd}"
        };

        _db.MfaEnrollments.Add(enrollment);
        await _db.SaveChangesAsync();

        await _auditLogger.LogAsync(
            AuditEventType.UserEnrolled,
            userId, null, null,
            $"Enrollment started: {request.Method}, EnrollmentId={enrollment.Id}");

        return Ok(new
        {
            EnrollmentId = enrollment.Id,
            result.ProvisioningUri,
            result.QrCodeDataUri,
            result.Metadata,
            result.Success
        });
    }

    /// <summary>
    /// Complete enrollment by verifying the user has the MFA device/app configured.
    /// </summary>
    [HttpPost("{userId}/complete")]
    public async Task<IActionResult> CompleteEnrollment(string userId, [FromBody] SelfEnrollmentCompleteRequest request)
    {
        var enrollment = await _db.MfaEnrollments.FindAsync(request.EnrollmentId);
        if (enrollment == null) return NotFound("Enrollment not found");
        if (enrollment.UserId != userId) return Forbid();
        if (enrollment.Status != EnrollmentStatus.Pending)
            return BadRequest("Enrollment is not in pending state");

        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found");

        // Verify the code/response
        bool verified = false;

        if (enrollment.EncryptedSecret.Length > 0)
        {
            try
            {
                var secret = AesGcmEncryption.Decrypt(enrollment.EncryptedSecret, enrollment.SecretNonce, _encryptionKey);

                // For TOTP, directly verify
                if (enrollment.Method == MfaMethod.Totp)
                {
                    verified = TotpGenerator.Validate(secret, request.VerificationCode, DateTimeOffset.UtcNow);
                }
            }
            catch
            {
                // Decryption failed
            }
        }

        // Also try via the provider's CompleteEnrollment
        if (!verified)
        {
            var provider = _providers.FirstOrDefault(p => p.MethodId == enrollment.Method.ToString().ToUpperInvariant());
            if (provider != null)
            {
                var ctx = new EnrollmentContext
                {
                    UserId = user.Id,
                    UserName = user.UserPrincipalName,
                    Issuer = "MfaSrv"
                };
                var result = await provider.CompleteEnrollmentAsync(ctx, request.VerificationCode);
                verified = result.Success;
            }
        }

        if (verified)
        {
            enrollment.Status = EnrollmentStatus.Active;
            enrollment.ActivatedAt = DateTimeOffset.UtcNow;
            user.MfaEnabled = true;
            await _db.SaveChangesAsync();

            await _auditLogger.LogAsync(
                AuditEventType.UserEnrolled,
                userId, null, null,
                $"Enrollment completed: {enrollment.Method}, EnrollmentId={enrollment.Id}");

            return Ok(new { Success = true, Message = "MFA enrollment activated successfully" });
        }

        return BadRequest(new { Success = false, Error = "Verification failed. Please try again." });
    }

    /// <summary>
    /// Revoke (disable) an MFA enrollment.
    /// </summary>
    [HttpDelete("{userId}/enrollments/{enrollmentId}")]
    public async Task<IActionResult> RevokeEnrollment(string userId, string enrollmentId)
    {
        var enrollment = await _db.MfaEnrollments.FindAsync(enrollmentId);
        if (enrollment == null) return NotFound("Enrollment not found");
        if (enrollment.UserId != userId) return Forbid();

        enrollment.Status = EnrollmentStatus.Revoked;
        await _db.SaveChangesAsync();

        // Check if user has any active enrollments left
        var hasActive = await _db.MfaEnrollments
            .AnyAsync(e => e.UserId == userId && e.Status == EnrollmentStatus.Active && e.Id != enrollmentId);

        if (!hasActive)
        {
            var user = await _db.Users.FindAsync(userId);
            if (user != null)
            {
                user.MfaEnabled = false;
                await _db.SaveChangesAsync();
            }
        }

        await _auditLogger.LogAsync(
            AuditEventType.UserDisenrolled,
            userId, null, null,
            $"Enrollment revoked: {enrollment.Method}, EnrollmentId={enrollmentId}");

        return Ok(new { Success = true, Message = "Enrollment revoked" });
    }

    /// <summary>
    /// Test an enrolled MFA method by issuing a challenge.
    /// </summary>
    [HttpPost("{userId}/test")]
    public async Task<IActionResult> TestEnrollment(string userId, [FromBody] SelfEnrollmentTestRequest request)
    {
        var enrollment = await _db.MfaEnrollments
            .FirstOrDefaultAsync(e => e.Id == request.EnrollmentId && e.UserId == userId && e.Status == EnrollmentStatus.Active);

        if (enrollment == null)
            return NotFound("Active enrollment not found");

        var provider = _providers.FirstOrDefault(p => p.MethodId == enrollment.Method.ToString().ToUpperInvariant());
        if (provider == null) return BadRequest("Provider not found");

        var ctx = new ChallengeContext
        {
            UserId = userId,
            EnrollmentId = enrollment.Id,
            EncryptedSecret = enrollment.EncryptedSecret,
            SecretNonce = enrollment.SecretNonce
        };

        var challenge = await provider.IssueChallengeAsync(ctx);

        return Ok(new
        {
            challenge.Success,
            challenge.ChallengeId,
            challenge.UserPrompt,
            challenge.ExpiresAt,
            challenge.Error
        });
    }
}

public record SelfEnrollmentBeginRequest
{
    public MfaMethod Method { get; init; }
    public string? FriendlyName { get; init; }
}

public record SelfEnrollmentCompleteRequest
{
    public string EnrollmentId { get; init; } = string.Empty;
    public string VerificationCode { get; init; } = string.Empty;
}

public record SelfEnrollmentTestRequest
{
    public string EnrollmentId { get; init; } = string.Empty;
}
