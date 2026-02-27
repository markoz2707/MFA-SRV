namespace MfaSrv.Server;

/// <summary>
/// Configuration for High Availability (active-passive) mode.
/// When enabled, multiple server instances coordinate via database-backed
/// leader election. Only the active leader processes background tasks
/// (session cleanup, policy sync, backups).
/// </summary>
public class HaSettings
{
    /// <summary>
    /// Enable HA mode. When false, the instance runs as a standalone server.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Unique identifier for this server instance.
    /// Defaults to "{MachineName}-{ProcessId}" if not specified.
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// How long the leader lease is valid (in seconds).
    /// If the leader doesn't renew within this period, another instance takes over.
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 30;

    /// <summary>
    /// How often the leader renews the lease (in seconds).
    /// Should be significantly less than LeaseDurationSeconds.
    /// </summary>
    public int LeaseRenewIntervalSeconds { get; set; } = 10;
}
