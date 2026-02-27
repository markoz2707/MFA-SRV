using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MfaSrv.Server.Services;

/// <summary>
/// Provides on-demand database export, import, and backup inventory operations.
/// Uses the SQLite VACUUM INTO command for consistent, lock-free snapshots and
/// the Online Backup API for restoring from a backup file.
/// </summary>
public class DatabaseExportService
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<BackupSettings> _settings;
    private readonly ILogger<DatabaseExportService> _logger;

    public DatabaseExportService(
        IConfiguration configuration,
        IOptions<BackupSettings> settings,
        ILogger<DatabaseExportService> logger)
    {
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Exports the full database to the specified output path using VACUUM INTO.
    /// The export is a consistent snapshot that does not block concurrent writes.
    /// </summary>
    /// <param name="outputPath">Absolute or relative path for the exported database file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExportAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var absolutePath = GetAbsolutePath(outputPath);
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _logger.LogInformation("Exporting database to {OutputPath}", absolutePath);

        var connectionString = GetConnectionString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO @outputPath";
        command.Parameters.AddWithValue("@outputPath", absolutePath);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var fileInfo = new FileInfo(absolutePath);
        _logger.LogInformation(
            "Database export completed: {OutputPath} ({SizeKB} KB)",
            absolutePath, fileInfo.Length / 1024);
    }

    /// <summary>
    /// Restores the live database from a backup file using the SQLite Online Backup API.
    /// This operation replaces ALL data in the current database and should be used with extreme caution.
    /// </summary>
    /// <param name="inputPath">Path to the backup database file to restore from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ImportAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);

        var absolutePath = GetAbsolutePath(inputPath);

        if (!File.Exists(absolutePath))
            throw new FileNotFoundException("Backup file not found.", absolutePath);

        // Validate that the source file is a valid SQLite database before proceeding.
        await ValidateSqliteFileAsync(absolutePath, cancellationToken);

        _logger.LogWarning("Starting database restore from {InputPath}. All current data will be replaced.", absolutePath);

        var connectionString = GetConnectionString();

        // Open the source (backup) and destination (live) connections.
        await using var sourceConnection = new SqliteConnection($"Data Source={absolutePath};Mode=ReadOnly");
        await sourceConnection.OpenAsync(cancellationToken);

        await using var destinationConnection = new SqliteConnection(connectionString);
        await destinationConnection.OpenAsync(cancellationToken);

        // Use the raw SQLite backup API exposed by Microsoft.Data.Sqlite.
        // This copies pages from source -> destination atomically.
        var sourceHandle = sourceConnection.Handle;
        var destinationHandle = destinationConnection.Handle;

        var rc = SQLitePCL.raw.sqlite3_backup_init(destinationHandle, "main", sourceHandle, "main");
        if (rc == null)
        {
            var errorMsg = SQLitePCL.raw.sqlite3_errmsg(destinationHandle).utf8_to_string();
            throw new InvalidOperationException($"Failed to initialize SQLite backup: {errorMsg}");
        }

        try
        {
            // Copy all pages in one step (-1 means copy everything).
            var stepResult = SQLitePCL.raw.sqlite3_backup_step(rc, -1);
            if (stepResult != SQLitePCL.raw.SQLITE_DONE)
            {
                var errorMsg = SQLitePCL.raw.sqlite3_errmsg(destinationHandle).utf8_to_string();
                throw new InvalidOperationException($"SQLite backup step failed with code {stepResult}: {errorMsg}");
            }
        }
        finally
        {
            SQLitePCL.raw.sqlite3_backup_finish(rc);
        }

        _logger.LogWarning("Database restore completed successfully from {InputPath}", absolutePath);
    }

    /// <summary>
    /// Returns a list of all available backup files with metadata (filename, size, timestamp).
    /// Only files matching the naming convention mfasrv_backup_*.db are included.
    /// </summary>
    public Task<List<BackupFileInfo>> GetBackupsAsync()
    {
        var backupDir = GetAbsoluteBackupDirectory();
        var result = new List<BackupFileInfo>();

        if (!Directory.Exists(backupDir))
            return Task.FromResult(result);

        var files = Directory.GetFiles(backupDir, "mfasrv_backup_*.db");
        foreach (var filePath in files.OrderByDescending(f => f))
        {
            var fi = new FileInfo(filePath);
            result.Add(new BackupFileInfo
            {
                FileName = fi.Name,
                SizeBytes = fi.Length,
                CreatedUtc = fi.CreationTimeUtc,
                LastModifiedUtc = fi.LastWriteTimeUtc
            });
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns the absolute path to a specific backup file, or null if it does not exist.
    /// The filename is validated to prevent directory traversal attacks.
    /// </summary>
    public string? GetBackupFilePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        // Prevent directory traversal by stripping any path components.
        var safeName = Path.GetFileName(fileName);
        if (safeName != fileName)
            return null;

        // Only allow files matching the expected naming pattern.
        if (!safeName.StartsWith("mfasrv_backup_", StringComparison.OrdinalIgnoreCase)
            || !safeName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            return null;

        var backupDir = GetAbsoluteBackupDirectory();
        var fullPath = Path.Combine(backupDir, safeName);

        return File.Exists(fullPath) ? fullPath : null;
    }

    /// <summary>
    /// Validates that a file is a genuine SQLite database by attempting to open it and querying PRAGMA integrity_check.
    /// </summary>
    private static async Task ValidateSqliteFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            var result = await command.ExecuteScalarAsync(cancellationToken) as string;

            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SQLite integrity check failed: {result}");
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"The file is not a valid SQLite database: {ex.Message}", ex);
        }
    }

    private string GetConnectionString()
    {
        return _configuration.GetConnectionString("DefaultConnection") ?? "Data Source=mfasrv.db";
    }

    private string GetAbsoluteBackupDirectory()
    {
        var dir = _settings.Value.BackupDirectory;
        if (Path.IsPathRooted(dir))
            return dir;
        return Path.Combine(AppContext.BaseDirectory, dir);
    }

    private static string GetAbsolutePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;
        return Path.Combine(AppContext.BaseDirectory, path);
    }
}

/// <summary>
/// Metadata about a single backup file.
/// </summary>
public class BackupFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastModifiedUtc { get; set; }
}
