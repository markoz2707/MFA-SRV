using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MfaSrv.Core.Enums;

namespace MfaSrv.DcAgent.Services;

/// <summary>
/// SQLite-backed persistent storage for policy and session caches.
/// Data survives service restarts. Uses Microsoft.Data.Sqlite directly (no EF Core).
///
/// Thread safety: uses a single persistent connection with WAL journal mode and
/// a SemaphoreSlim to serialize write operations. WAL allows concurrent reads
/// while a write is in progress.
/// </summary>
public class SqliteCacheStore : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<SqliteCacheStore> _logger;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public SqliteCacheStore(string dbPath, ILogger<SqliteCacheStore> logger)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Opens the SQLite connection and creates tables if they don't exist.
    /// Must be called once before any other operations.
    /// </summary>
    public async Task InitializeAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync();

        // Enable WAL journal mode for concurrent read/write
        await ExecuteNonQueryAsync("PRAGMA journal_mode=WAL;");
        // Synchronous=NORMAL gives good durability with WAL without full fsync overhead
        await ExecuteNonQueryAsync("PRAGMA synchronous=NORMAL;");

        await CreateTablesAsync();

        _logger.LogInformation("SQLite cache store initialized at {DbPath}", _dbPath);
    }

    private async Task CreateTablesAsync()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS cached_policies (
                policy_id   TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                policy_json TEXT NOT NULL,
                failover_mode INT NOT NULL,
                priority    INT NOT NULL,
                is_enabled  INT NOT NULL,
                updated_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS cached_sessions (
                session_id      TEXT PRIMARY KEY,
                user_id         TEXT NOT NULL,
                user_name       TEXT NOT NULL,
                source_ip       TEXT NOT NULL,
                expires_at      TEXT NOT NULL,
                verified_method TEXT NOT NULL,
                revoked         INT NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS cache_metadata (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            """;

        await ExecuteNonQueryAsync(sql);
    }

    // ─── Policy Operations ───────────────────────────────────────────────

    public async Task SavePolicyAsync(CachedPolicy policy)
    {
        const string sql = """
            INSERT INTO cached_policies (policy_id, name, policy_json, failover_mode, priority, is_enabled, updated_at)
            VALUES (@PolicyId, @Name, @PolicyJson, @FailoverMode, @Priority, @IsEnabled, @UpdatedAt)
            ON CONFLICT(policy_id) DO UPDATE SET
                name          = excluded.name,
                policy_json   = excluded.policy_json,
                failover_mode = excluded.failover_mode,
                priority      = excluded.priority,
                is_enabled    = excluded.is_enabled,
                updated_at    = excluded.updated_at;
            """;

        await _writeLock.WaitAsync();
        try
        {
            using var cmd = CreateCommand(sql);
            cmd.Parameters.AddWithValue("@PolicyId", policy.PolicyId);
            cmd.Parameters.AddWithValue("@Name", policy.Name);
            cmd.Parameters.AddWithValue("@PolicyJson", policy.PolicyJson);
            cmd.Parameters.AddWithValue("@FailoverMode", (int)policy.FailoverMode);
            cmd.Parameters.AddWithValue("@Priority", policy.Priority);
            cmd.Parameters.AddWithValue("@IsEnabled", policy.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@UpdatedAt", policy.UpdatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemovePolicyAsync(string policyId)
    {
        const string sql = "DELETE FROM cached_policies WHERE policy_id = @PolicyId;";

        await _writeLock.WaitAsync();
        try
        {
            using var cmd = CreateCommand(sql);
            cmd.Parameters.AddWithValue("@PolicyId", policyId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<CachedPolicy>> LoadAllPoliciesAsync()
    {
        const string sql = "SELECT policy_id, name, policy_json, failover_mode, priority, is_enabled, updated_at FROM cached_policies;";

        var policies = new List<CachedPolicy>();

        using var cmd = CreateCommand(sql);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            policies.Add(new CachedPolicy
            {
                PolicyId = reader.GetString(0),
                Name = reader.GetString(1),
                PolicyJson = reader.GetString(2),
                FailoverMode = (FailoverMode)reader.GetInt32(3),
                Priority = reader.GetInt32(4),
                IsEnabled = reader.GetInt32(5) != 0,
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
            });
        }

        _logger.LogDebug("Loaded {Count} policies from SQLite cache", policies.Count);
        return policies;
    }

    // ─── Session Operations ──────────────────────────────────────────────

    public async Task SaveSessionAsync(CachedSession session)
    {
        const string sql = """
            INSERT INTO cached_sessions (session_id, user_id, user_name, source_ip, expires_at, verified_method, revoked)
            VALUES (@SessionId, @UserId, @UserName, @SourceIp, @ExpiresAt, @VerifiedMethod, @Revoked)
            ON CONFLICT(session_id) DO UPDATE SET
                user_id         = excluded.user_id,
                user_name       = excluded.user_name,
                source_ip       = excluded.source_ip,
                expires_at      = excluded.expires_at,
                verified_method = excluded.verified_method,
                revoked         = excluded.revoked;
            """;

        await _writeLock.WaitAsync();
        try
        {
            using var cmd = CreateCommand(sql);
            cmd.Parameters.AddWithValue("@SessionId", session.SessionId);
            cmd.Parameters.AddWithValue("@UserId", session.UserId);
            cmd.Parameters.AddWithValue("@UserName", session.UserName);
            cmd.Parameters.AddWithValue("@SourceIp", session.SourceIp);
            cmd.Parameters.AddWithValue("@ExpiresAt", session.ExpiresAt.ToString("O"));
            cmd.Parameters.AddWithValue("@VerifiedMethod", session.VerifiedMethod);
            cmd.Parameters.AddWithValue("@Revoked", session.Revoked ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemoveSessionAsync(string sessionId)
    {
        const string sql = "DELETE FROM cached_sessions WHERE session_id = @SessionId;";

        await _writeLock.WaitAsync();
        try
        {
            using var cmd = CreateCommand(sql);
            cmd.Parameters.AddWithValue("@SessionId", sessionId);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Loads only non-expired, non-revoked sessions from the database.
    /// </summary>
    public async Task<List<CachedSession>> LoadAllSessionsAsync()
    {
        const string sql = """
            SELECT session_id, user_id, user_name, source_ip, expires_at, verified_method, revoked
            FROM cached_sessions
            WHERE expires_at > @Now AND revoked = 0;
            """;

        var sessions = new List<CachedSession>();

        using var cmd = CreateCommand(sql);
        cmd.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("O"));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sessions.Add(new CachedSession
            {
                SessionId = reader.GetString(0),
                UserId = reader.GetString(1),
                UserName = reader.GetString(2),
                SourceIp = reader.GetString(3),
                ExpiresAt = DateTimeOffset.Parse(reader.GetString(4)),
                VerifiedMethod = reader.GetString(5),
                Revoked = reader.GetInt32(6) != 0
            });
        }

        _logger.LogDebug("Loaded {Count} active sessions from SQLite cache", sessions.Count);
        return sessions;
    }

    /// <summary>
    /// Removes expired and revoked sessions from the database.
    /// </summary>
    public async Task<int> CleanupExpiredSessionsAsync()
    {
        const string sql = "DELETE FROM cached_sessions WHERE expires_at <= @Now OR revoked = 1;";

        await _writeLock.WaitAsync();
        try
        {
            using var cmd = CreateCommand(sql);
            cmd.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("O"));
            var count = await cmd.ExecuteNonQueryAsync();
            if (count > 0)
                _logger.LogDebug("Cleaned up {Count} expired/revoked sessions from SQLite cache", count);
            return count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ─── Metadata Operations ─────────────────────────────────────────────

    public async Task<string?> GetMetadataAsync(string key)
    {
        const string sql = "SELECT value FROM cache_metadata WHERE key = @Key;";

        using var cmd = CreateCommand(sql);
        cmd.Parameters.AddWithValue("@Key", key);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public async Task SetMetadataAsync(string key, string value)
    {
        const string sql = """
            INSERT INTO cache_metadata (key, value)
            VALUES (@Key, @Value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;

        await _writeLock.WaitAsync();
        try
        {
            using var cmd = CreateCommand(sql);
            cmd.Parameters.AddWithValue("@Key", key);
            cmd.Parameters.AddWithValue("@Value", value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private SqliteCommand CreateCommand(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is null)
            throw new InvalidOperationException("SqliteCacheStore has not been initialized. Call InitializeAsync first.");

        return new SqliteCommand(sql, _connection);
    }

    private async Task ExecuteNonQueryAsync(string sql)
    {
        using var cmd = CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _connection?.Close();
        _connection?.Dispose();
        _writeLock.Dispose();

        _logger.LogDebug("SQLite cache store disposed");
    }
}
