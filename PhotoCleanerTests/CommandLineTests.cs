using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class CommandLineTests
{
    private static string ExistingDir => Directory.GetCurrentDirectory();

    // ── SkipBackup option ────────────────────────────────────────────────────

    [Fact]
    public void SkipBackupOption_DefaultFalse()
    {
        CommandLine cli = new(["process", "--path", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.SkipBackup.Should().BeFalse();
    }

    [Fact]
    public void SkipBackupOption_LongFlag_ParsedTrue()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "--skipbackup"]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.SkipBackup.Should().BeTrue();
    }

    [Fact]
    public void SkipBackupOption_ShortFlag_ParsedTrue()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "-s"]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.SkipBackup.Should().BeTrue();
    }

    // ── Cleanup command ──────────────────────────────────────────────────────

    [Fact]
    public void CleanupCommand_IsRegistered()
    {
        CommandLine cli = new(["cleanup", "--path", ExistingDir]);

        // No parse errors means the command is recognized
        cli.Result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void CleanupCommand_DryRunFlag_ParsedTrue()
    {
        CommandLine cli = new(["cleanup", "--path", ExistingDir, "--dryrun"]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DryRun.Should().BeTrue();
    }

    [Fact]
    public void CleanupCommand_SkipBackupNotAvailable_DefaultFalse()
    {
        // --skipbackup is only on the process command; cleanup should default to false
        CommandLine cli = new(["cleanup", "--path", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.SkipBackup.Should().BeFalse();
    }
}
