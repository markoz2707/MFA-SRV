using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MfaSrv.Server;
using MfaSrv.Server.Services;
using Xunit;

namespace MfaSrv.Tests.Unit.Server;

public class DatabaseExportServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseExportService _service;

    public DatabaseExportServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"mfasrv_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={Path.Combine(_testDir, "test.db")}"
            })
            .Build();

        var settings = Options.Create(new BackupSettings
        {
            BackupDirectory = Path.Combine(_testDir, "backups"),
            RetentionCount = 5
        });

        _service = new DatabaseExportService(
            config,
            settings,
            NullLogger<DatabaseExportService>.Instance);
    }

    [Fact]
    public void GetBackupFilePath_WithValidName_ReturnsPathWhenFileExists()
    {
        var backupDir = Path.Combine(_testDir, "backups");
        Directory.CreateDirectory(backupDir);
        var filePath = Path.Combine(backupDir, "mfasrv_backup_20240101_120000.db");
        File.WriteAllBytes(filePath, new byte[] { 0 });

        var result = _service.GetBackupFilePath("mfasrv_backup_20240101_120000.db");
        result.Should().NotBeNull();
        result.Should().EndWith("mfasrv_backup_20240101_120000.db");
    }

    [Fact]
    public void GetBackupFilePath_WithTraversal_ReturnsNull()
    {
        var result = _service.GetBackupFilePath("../../../etc/passwd");
        result.Should().BeNull();
    }

    [Fact]
    public void GetBackupFilePath_WithInvalidPrefix_ReturnsNull()
    {
        var result = _service.GetBackupFilePath("not_a_backup.db");
        result.Should().BeNull();
    }

    [Fact]
    public void GetBackupFilePath_WithInvalidExtension_ReturnsNull()
    {
        var result = _service.GetBackupFilePath("mfasrv_backup_20240101_120000.exe");
        result.Should().BeNull();
    }

    [Fact]
    public void GetBackupFilePath_WithNonExistentFile_ReturnsNull()
    {
        var result = _service.GetBackupFilePath("mfasrv_backup_99991231_235959.db");
        result.Should().BeNull();
    }

    [Fact]
    public void GetBackupFilePath_WithNull_ReturnsNull()
    {
        var result = _service.GetBackupFilePath("");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBackupsAsync_WithNoBackupDir_ReturnsEmptyList()
    {
        // Backup dir doesn't exist yet
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Data Source=test.db"
            })
            .Build();

        var settings = Options.Create(new BackupSettings
        {
            BackupDirectory = Path.Combine(_testDir, "nonexistent_backups")
        });

        var svc = new DatabaseExportService(config, settings, NullLogger<DatabaseExportService>.Instance);

        var result = await svc.GetBackupsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBackupsAsync_WithMultipleBackups_ReturnsOrderedList()
    {
        var backupDir = Path.Combine(_testDir, "backups");
        Directory.CreateDirectory(backupDir);

        File.WriteAllBytes(Path.Combine(backupDir, "mfasrv_backup_20240101_100000.db"), new byte[100]);
        File.WriteAllBytes(Path.Combine(backupDir, "mfasrv_backup_20240102_100000.db"), new byte[200]);
        File.WriteAllBytes(Path.Combine(backupDir, "mfasrv_backup_20240103_100000.db"), new byte[300]);
        // Non-matching file should be excluded
        File.WriteAllBytes(Path.Combine(backupDir, "other_file.db"), new byte[50]);

        var result = await _service.GetBackupsAsync();
        result.Should().HaveCount(3);
        result[0].FileName.Should().Contain("20240103"); // Most recent first
        result[2].FileName.Should().Contain("20240101"); // Oldest last
    }

    [Fact]
    public async Task ExportAsync_WithNullPath_ThrowsArgumentException()
    {
        var act = () => _service.ExportAsync("", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ImportAsync_WithNonExistentFile_ThrowsFileNotFoundException()
    {
        var act = () => _service.ImportAsync("/nonexistent/backup.db", CancellationToken.None);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); }
        catch { /* best effort cleanup */ }
    }
}
