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

    // -- Duration option ------------------------------------------------------

    [Fact]
    public void DurationOption_DefaultIsShortVideoDuration()
    {
        CommandLine cli = new(["process", "--path", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.ShortVideoDuration.Should().Be(MediaUtilities.ShortVideoDuration);
    }

    [Fact]
    public void DurationOption_CustomValue_Parsed()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "--duration", "2.5"]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.ShortVideoDuration.Should().Be(2.5);
    }

    [Fact]
    public void DurationOption_ZeroValue_FailsValidation()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "--duration", "0"]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void DurationOption_NegativeValue_FailsValidation()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "--duration", "-1"]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    // -- Reprocess option -----------------------------------------------------

    [Fact]
    public void ReprocessOption_DefaultFalse()
    {
        CommandLine cli = new(["process", "--path", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.Reprocess.Should().BeFalse();
    }

    [Fact]
    public void ReprocessOption_Flag_ParsedTrue()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "--reprocess"]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.Reprocess.Should().BeTrue();
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

        options.Format.Should().Be("yyyy/MM/dd");
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
            "yyyy/MM",
        ]);

        cli.Result.Errors.Should().BeEmpty();
        CommandLine.Options options = cli.CreateOptions(cli.Result);
        options.Format.Should().Be("yyyy/MM");
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
    public void DbFileOption_DefaultNull()
    {
        CommandLine cli = new(["organize", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DbFile.Should().BeNull();
    }

    [Fact]
    public void DbFileOption_Path_ParsedCorrectly()
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

        options.DbFile!.FullName.Should().Be(dbPath);
    }

    // -- --outdb option -------------------------------------------------------

    [Fact]
    public void OutDbFileOption_DefaultNull()
    {
        CommandLine cli = new(["organize", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.OutDbFile.Should().BeNull();
    }

    [Fact]
    public void DuplicatesCommand_WithOutDb_Parses()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "source.db");
        string outDbPath = Path.Combine(Path.GetTempPath(), "target.db");
        CommandLine cli = new([
            "duplicates",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--db",
            dbPath,
            "--outdb",
            outDbPath,
        ]);

        cli.Result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void DuplicatesCommand_MissingOutDb_ValidationError()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "source.db");
        CommandLine cli = new([
            "duplicates",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--db",
            dbPath,
        ]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    // -- index command --------------------------------------------------------

    [Fact]
    public void IndexCommand_Parses()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "photos.db");
        CommandLine cli = new(["index", "--path", ExistingDir, "--db", dbPath]);

        cli.Result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void IndexCommand_MissingDb_ValidationError()
    {
        CommandLine cli = new(["index", "--path", ExistingDir]);

        cli.Result.Errors.Should().NotBeEmpty();
    }
}
