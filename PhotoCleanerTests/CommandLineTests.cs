using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class CommandLineTests
{
    private static string ExistingDir => Directory.GetCurrentDirectory();

    // -- SkipBackup option ----------------------------------------------------

    [Fact]
    public void SkipBackupOption_DefaultFalse()
    {
        CommandLine cli = new(["process", "--path", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.SkipBackup.Should().BeFalse();
    }

    [Fact]
    public void SkipBackupOption_Flag_ParsedTrue()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "--skipbackup"]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.SkipBackup.Should().BeTrue();
    }

    // -- Cleanup command ------------------------------------------------------

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

    // -- Organize command -----------------------------------------------------

    [Fact]
    public void OrganizeCommand_IsRegistered()
    {
        CommandLine cli = new(["organize", "--path", ExistingDir, "--outpath", ExistingDir]);

        cli.Result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void OrganizeCommand_DryRunFlag_ParsedTrue()
    {
        CommandLine cli = new([
            "organize",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--dryrun",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DryRun.Should().BeTrue();
    }

    // -- --deleteempty option -------------------------------------------------

    [Fact]
    public void DeleteEmptyOption_DefaultFalse()
    {
        CommandLine cli = new(["organize", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DeleteEmpty.Should().BeFalse();
    }

    [Fact]
    public void DeleteEmptyOption_Flag_ParsedTrue()
    {
        CommandLine cli = new([
            "organize",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--deleteempty",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DeleteEmpty.Should().BeTrue();
    }

    // -- --format option ------------------------------------------------------

    [Fact]
    public void FormatOption_DefaultYearMonth()
    {
        CommandLine cli = new(["organize", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.Format.Should().Be("yyyy-MM");
    }

    [Fact]
    public void FormatOption_ValidDateFormat_ParsedOk()
    {
        CommandLine cli = new([
            "organize",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--format",
            "yyyy/MM/dd",
        ]);

        cli.Result.Errors.Should().BeEmpty();
        CommandLine.Options options = cli.CreateOptions(cli.Result);
        options.Format.Should().Be("yyyy/MM/dd");
    }

    [Fact]
    public void FormatOption_WithTimeComponent_ValidationError()
    {
        CommandLine cli = new([
            "organize",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--format",
            "yyyy-MM-dd HH:mm",
        ]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void OrganizeCommand_ThreadsOption_ParsedCorrectly()
    {
        CommandLine cli = new([
            "organize",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--threads",
            "2",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.Threads.Should().Be(2);
    }

    [Fact]
    public void FormatOption_InvalidFormatSpecifier_ValidationError()
    {
        CommandLine cli = new([
            "organize",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--format",
            "%invalid%",
        ]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    // -- --move option ---------------------------------------------------------

    [Fact]
    public void MoveOption_DefaultFalse()
    {
        CommandLine cli = new(["organize", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.Move.Should().BeFalse();
    }

    [Fact]
    public void MoveOption_Flag_ParsedTrue()
    {
        CommandLine cli = new([
            "organize",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--move",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.Move.Should().BeTrue();
    }

    // -- --db option -----------------------------------------------------------

    [Fact]
    public void DbPathOption_DefaultNull()
    {
        CommandLine cli = new(["organize", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DbPath.Should().BeNull();
    }

    [Fact]
    public void DbPathOption_Path_ParsedCorrectly()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "photos.db");
        CommandLine cli = new([
            "organize",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--db",
            dbPath,
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DbPath!.FullName.Should().Be(dbPath);
    }
}
