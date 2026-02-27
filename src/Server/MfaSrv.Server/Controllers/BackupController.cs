using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MfaSrv.Server.Services;

namespace MfaSrv.Server.Controllers;

[ApiController]
[Route("api/backups")]
[Authorize]
public class BackupController : ControllerBase
{
    private readonly DatabaseExportService _exportService;
    private readonly DatabaseBackupService _backupService;
    private readonly ILogger<BackupController> _logger;

    /// <summary>
    /// Tokens that have been issued for confirming a restore operation.
    /// Stored in memory with expiration to prevent replay attacks.
    /// In a multi-instance deployment this should be replaced with a shared store.
    /// </summary>
    private static readonly Dictionary<string, DateTime> _confirmationTokens = new();
    private static readonly object _tokenLock = new();

    public BackupController(
        DatabaseExportService exportService,
        DatabaseBackupService backupService,
        ILogger<BackupController> logger)
    {
        _exportService = exportService;
        _backupService = backupService;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/backups - Lists all available backup files with metadata.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListBackups()
    {
        var backups = await _exportService.GetBackupsAsync();
        return Ok(new { total = backups.Count, data = backups });
    }

    /// <summary>
    /// POST /api/backups - Triggers a manual database backup.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateBackup(CancellationToken cancellationToken)
    {
        try
        {
            var backupPath = await _backupService.PerformBackupAsync(cancellationToken);
            var fileName = Path.GetFileName(backupPath);
            _logger.LogInformation("Manual backup triggered by {User}: {FileName}",
                User.Identity?.Name ?? "unknown", fileName);

            return Ok(new { message = "Backup created successfully.", fileName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual backup request failed");
            return StatusCode(500, new { error = "Backup failed. Check server logs for details." });
        }
    }

    /// <summary>
    /// POST /api/backups/restore - Restores the database from a specified backup file.
    /// Requires a confirmation token to prevent accidental restores.
    ///
    /// Flow:
    /// 1. Call with { "fileName": "..." } and no confirmationToken to receive a token.
    /// 2. Call again with { "fileName": "...", "confirmationToken": "..." } to execute the restore.
    /// </summary>
    [HttpPost("restore")]
    public async Task<IActionResult> RestoreBackup(
        [FromBody] RestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
            return BadRequest(new { error = "fileName is required." });

        var backupFilePath = _exportService.GetBackupFilePath(request.FileName);
        if (backupFilePath == null)
            return NotFound(new { error = "Backup file not found or invalid filename." });

        // If no confirmation token is provided, generate one and return it.
        if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
        {
            var token = GenerateConfirmationToken(request.FileName);
            return Ok(new
            {
                message = "Restore requires confirmation. Re-submit with the provided confirmationToken to proceed. The token expires in 5 minutes.",
                fileName = request.FileName,
                confirmationToken = token
            });
        }

        // Validate the confirmation token.
        if (!ValidateConfirmationToken(request.FileName, request.ConfirmationToken))
        {
            return BadRequest(new { error = "Invalid or expired confirmation token." });
        }

        try
        {
            _logger.LogWarning("Database restore initiated by {User} from backup {FileName}",
                User.Identity?.Name ?? "unknown", request.FileName);

            await _exportService.ImportAsync(backupFilePath, cancellationToken);

            return Ok(new { message = "Database restored successfully.", fileName = request.FileName });
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "Backup file not found." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Database restore failed for {FileName}", request.FileName);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during database restore from {FileName}", request.FileName);
            return StatusCode(500, new { error = "Restore failed. Check server logs for details." });
        }
    }

    /// <summary>
    /// GET /api/backups/{filename}/download - Downloads a specific backup file.
    /// </summary>
    [HttpGet("{filename}/download")]
    public IActionResult DownloadBackup(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return BadRequest(new { error = "filename is required." });

        var backupFilePath = _exportService.GetBackupFilePath(filename);
        if (backupFilePath == null)
            return NotFound(new { error = "Backup file not found or invalid filename." });

        _logger.LogInformation("Backup download requested by {User}: {FileName}",
            User.Identity?.Name ?? "unknown", filename);

        var stream = new FileStream(backupFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "application/x-sqlite3", filename);
    }

    /// <summary>
    /// Generates a time-limited confirmation token for a restore operation.
    /// </summary>
    private static string GenerateConfirmationToken(string fileName)
    {
        var token = Guid.NewGuid().ToString("N");
        var key = $"{fileName}:{token}";

        lock (_tokenLock)
        {
            // Clean up expired tokens while we hold the lock.
            var expired = _confirmationTokens
                .Where(kv => kv.Value < DateTime.UtcNow)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in expired)
                _confirmationTokens.Remove(k);

            _confirmationTokens[key] = DateTime.UtcNow.AddMinutes(5);
        }

        return token;
    }

    /// <summary>
    /// Validates and consumes a confirmation token. Each token can only be used once.
    /// </summary>
    private static bool ValidateConfirmationToken(string fileName, string token)
    {
        var key = $"{fileName}:{token}";

        lock (_tokenLock)
        {
            if (_confirmationTokens.TryGetValue(key, out var expiry))
            {
                _confirmationTokens.Remove(key); // Single use.
                return expiry >= DateTime.UtcNow;
            }
        }

        return false;
    }
}

public class RestoreRequest
{
    public string FileName { get; set; } = string.Empty;
    public string? ConfirmationToken { get; set; }
}
