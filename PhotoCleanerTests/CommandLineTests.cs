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

    // -- MarkProcessed (--processed on index) ---------------------------------

    [Fact]
    public void MarkProcessedOption_DefaultFalse()
    {
        CommandLine cli = new(["index", "--path", ExistingDir, "--db", "/tmp/test.db"]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.MarkProcessed.Should().BeFalse();
    }

    [Fact]
    public void MarkProcessedOption_Flag_ParsedTrue()
    {
        CommandLine cli = new([
            "index",
            "--path",
            ExistingDir,
            "--db",
            "/tmp/test.db",
            "--processed",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.MarkProcessed.Should().BeTrue();
    }

    // -- Process --trashdb ----------------------------------------------------

    [Fact]
    public void ProcessTrashDbOption_ParsedCorrectly()
    {
        // The validator requires the file to exist; use a real file.
        string trashDbFile = Path.Combine(Path.GetTempPath(), $"trash_{Guid.NewGuid()}.db");
        File.WriteAllBytes(trashDbFile, []);
        try
        {
            CommandLine cli = new(["process", "--path", ExistingDir, "--trashdb", trashDbFile]);

            CommandLine.Options options = cli.CreateOptions(cli.Result);

            cli.Result.Errors.Should().BeEmpty();
            options.TrashDbFile.Should().NotBeNull();
            options.TrashDbFile.FullName.Should().Be(new FileInfo(trashDbFile).FullName);
        }
        finally
        {
            File.Delete(trashDbFile);
        }
    }

    // -- Organize command -----------------------------------------------------

    [Fact]
    public void ImportCommand_IsRegistered()
    {
        CommandLine cli = new(["import", "--path", ExistingDir, "--outpath", ExistingDir]);

        cli.Result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ImportCommand_DryRunFlag_ParsedTrue()
    {
        CommandLine cli = new([
            "import",
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
        CommandLine cli = new(["import", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DeleteEmpty.Should().BeFalse();
    }

    [Fact]
    public void DeleteEmptyOption_Flag_ParsedTrue()
    {
        CommandLine cli = new([
            "import",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--deleteempty",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DeleteEmpty.Should().BeTrue();
    }

    [Fact]
    public void DeleteEmptyOption_Process_DefaultFalse()
    {
        CommandLine cli = new(["process", "--path", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.DeleteEmpty.Should().BeFalse();
    }

    [Fact]
    public void DeleteEmptyOption_Process_Flag_ParsedTrue()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "--deleteempty"]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.DeleteEmpty.Should().BeTrue();
    }

    // -- --format option ------------------------------------------------------

    [Fact]
    public void FormatOption_DefaultYearMonth()
    {
        CommandLine cli = new(["import", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.Format.Should().Be("yyyy/MM/dd");
    }

    [Fact]
    public void FormatOption_ValidDateFormat_ParsedOk()
    {
        CommandLine cli = new([
            "import",
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
            "import",
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
    public void ImportCommand_ThreadsOption_ParsedCorrectly()
    {
        CommandLine cli = new([
            "import",
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
            "import",
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
        CommandLine cli = new(["import", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.Move.Should().BeFalse();
    }

    [Fact]
    public void MoveOption_Flag_ParsedTrue()
    {
        CommandLine cli = new([
            "import",
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
        CommandLine cli = new(["import", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        options.DbFile.Should().BeNull();
    }

    [Fact]
    public void DbFileOption_Path_ParsedCorrectly()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "photos.db");
        CommandLine cli = new([
            "import",
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

    // -- trash command --------------------------------------------------------

    [Fact]
    public void TrashCommand_ValidArgs_Parses()
    {
        string dbPath = Path.GetTempFileName();
        try
        {
            CommandLine cli = new([
                "trash",
                "--url",
                "http://immich:2283",
                "--apikey",
                "test-key",
                "--trashdb",
                dbPath,
            ]);

            cli.Result.Errors.Should().BeEmpty();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void TrashCommand_MissingUrl_ValidationError()
    {
        string dbPath = Path.GetTempFileName();
        try
        {
            CommandLine cli = new(["trash", "--apikey", "test-key", "--trashdb", dbPath]);

            cli.Result.Errors.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void TrashCommand_InvalidUrl_ValidationError()
    {
        string dbPath = Path.GetTempFileName();
        try
        {
            CommandLine cli = new([
                "trash",
                "--url",
                "not-a-url",
                "--apikey",
                "test-key",
                "--trashdb",
                dbPath,
            ]);

            cli.Result.Errors.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void TrashCommand_MissingApiKey_ValidationError()
    {
        string dbPath = Path.GetTempFileName();
        try
        {
            CommandLine cli = new(["trash", "--url", "http://immich:2283", "--trashdb", dbPath]);

            cli.Result.Errors.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void TrashCommand_MissingTrashDb_ValidationError()
    {
        CommandLine cli = new(["trash", "--url", "http://immich:2283", "--apikey", "test-key"]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    // -- --trashdb / --skipdb file validation ----------------------------------

    [Fact]
    public void TrashDbOption_NonExistentFile_ValidationError()
    {
        CommandLine cli = new([
            "import",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--trashdb",
            "/nonexistent/path/trash.db",
        ]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void SkipDbOption_NonExistentFile_ValidationError()
    {
        CommandLine cli = new([
            "import",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--skipdb",
            "/nonexistent/path/skip.db",
        ]);

        cli.Result.Errors.Should().NotBeEmpty();
    }
}
