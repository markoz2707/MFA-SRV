namespace MfaSrv.Server;

/// <summary>
/// Configuration settings for the automated database backup system.
/// Bound from the "Backup" section of appsettings.json.
/// </summary>
public class BackupSettings
{
    /// <summary>
    /// Directory where backup files are stored. Relative paths are resolved from the application root.
    /// </summary>
    public string BackupDirectory { get; set; } = "./backups";

    /// <summary>
    /// Interval in hours between automatic backups.
    /// </summary>
    public int BackupIntervalHours { get; set; } = 6;

    /// <summary>
    /// Maximum number of backup files to retain. Oldest backups are deleted when this count is exceeded.
    /// </summary>
    public int RetentionCount { get; set; } = 10;

    /// <summary>
    /// Whether the automatic backup service is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
