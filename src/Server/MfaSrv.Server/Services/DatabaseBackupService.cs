using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Server.Services;

/// <summary>
/// Background service that performs periodic SQLite database backups.
/// Uses VACUUM INTO for hot backups that do not require exclusive locks on the source database.
/// Manages backup rotation by deleting the oldest files when the retention count is exceeded.
/// </summary>
public class DatabaseBackupService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<BackupSettings> _settingsMonitor;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    public DatabaseBackupService(
        IConfiguration configuration,
        IOptionsMonitor<BackupSettings> settingsMonitor,
        ILogger<DatabaseBackupService> logger)
    {
        _configuration = configuration;
        _settingsMonitor = settingsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _settingsMonitor.CurrentValue;

        if (!settings.Enabled)
        {
            _logger.LogInformation("Database backup service is disabled via configuration");
            return;
        }

        _logger.LogInformation(
            "Database backup service started. Interval: {IntervalHours}h, Retention: {RetentionCount}, Directory: {BackupDir}",
            settings.BackupIntervalHours, settings.RetentionCount, settings.BackupDirectory);

        // Perform an initial backup shortly after startup to ensure a recent backup exists.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown; do not log as an error.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during scheduled database backup");
            }

            var currentSettings = _settingsMonitor.CurrentValue;
            var interval = TimeSpan.FromHours(Math.Max(currentSettings.BackupIntervalHours, 1));
            await Task.Delay(interval, stoppingToken);
        }

        _logger.LogInformation("Database backup service is stopping");
    }

    /// <summary>
    /// Performs a single backup cycle: creates the backup directory if needed,
    /// runs VACUUM INTO to produce a consistent snapshot, then rotates old backups.
    /// </summary>
    public async Task<string> PerformBackupAsync(CancellationToken cancellationToken = default)
    {
        await _backupLock.WaitAsync(cancellationToken);
        try
        {
            var settings = _settingsMonitor.CurrentValue;
            var backupDir = GetAbsoluteBackupDirectory(settings.BackupDirectory);
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"mfasrv_backup_{timestamp}.db";
            var backupPath = Path.Combine(backupDir, backupFileName);

            _logger.LogInformation("Starting database backup to {BackupPath}", backupPath);

            var connectionString = _configuration.GetConnectionString("DefaultConnection")
                                   ?? "Data Source=mfasrv.db";

            await using var sourceConnection = new SqliteConnection(connectionString);
            await sourceConnection.OpenAsync(cancellationToken);

            // VACUUM INTO creates a fully self-contained copy of the database without
            // holding a write lock on the source. It is safe to call on a live database.
            await using var command = sourceConnection.CreateCommand();
            command.CommandText = "VACUUM INTO @backupPath";
            command.Parameters.AddWithValue("@backupPath", backupPath);
            await command.ExecuteNonQueryAsync(cancellationToken);

            var fileInfo = new FileInfo(backupPath);
            _logger.LogInformation(
                "Database backup completed successfully: {BackupFile} ({SizeKB} KB)",
                backupFileName, fileInfo.Length / 1024);

            RotateBackups(backupDir, settings.RetentionCount);

            return backupPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database backup failed");
            throw;
        }
        finally
        {
            _backupLock.Release();
        }
    }

    /// <summary>
    /// Deletes the oldest backup files when the total count exceeds the retention limit.
    /// Only files matching the expected backup naming pattern are considered.
    /// </summary>
    private void RotateBackups(string backupDir, int retentionCount)
    {
        try
        {
            var backupFiles = Directory.GetFiles(backupDir, "mfasrv_backup_*.db")
                .OrderByDescending(f => f)
                .ToList();

            if (backupFiles.Count <= retentionCount)
                return;

            var filesToDelete = backupFiles.Skip(retentionCount).ToList();
            foreach (var file in filesToDelete)
            {
                try
                {
                    File.Delete(file);
                    _logger.LogInformation("Rotated old backup: {FileName}", Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old backup file: {FileName}", Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during backup rotation in {BackupDir}", backupDir);
        }
    }

    /// <summary>
    /// Resolves a potentially relative backup directory path to an absolute path
    /// using the application's current directory as the base.
    /// </summary>
    private static string GetAbsoluteBackupDirectory(string backupDirectory)
    {
        if (Path.IsPathRooted(backupDirectory))
            return backupDirectory;

        return Path.Combine(AppContext.BaseDirectory, backupDirectory);
    }

    public override void Dispose()
    {
        _backupLock.Dispose();
        base.Dispose();
    }
}
