using FluentAssertions;
using MfaSrv.Server;
using Xunit;

namespace MfaSrv.Tests.Unit.Server;

public class BackupSettingsTests
{
    [Fact]
    public void BackupSettings_HasCorrectDefaults()
    {
        var settings = new BackupSettings();
        settings.BackupDirectory.Should().Be("./backups");
        settings.BackupIntervalHours.Should().Be(6);
        settings.RetentionCount.Should().Be(10);
        settings.Enabled.Should().BeTrue();
    }

    [Fact]
    public void BackupSettings_CanSetCustomValues()
    {
        var settings = new BackupSettings
        {
            BackupDirectory = "/custom/path",
            BackupIntervalHours = 12,
            RetentionCount = 20,
            Enabled = false
        };

        settings.BackupDirectory.Should().Be("/custom/path");
        settings.BackupIntervalHours.Should().Be(12);
        settings.RetentionCount.Should().Be(20);
        settings.Enabled.Should().BeFalse();
    }
}
