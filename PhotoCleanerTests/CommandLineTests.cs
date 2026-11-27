using System.Net.Sockets;
using System.Runtime.InteropServices;
using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class CommandLineTests
{
    private static string ExistingDir => Directory.GetCurrentDirectory();

    // -- Verify command -------------------------------------------------------

    [Fact]
    public void VerifyCommand_PathOnly_ParsesWithoutErrors()
    {
        CommandLine cli = new(["verify", "--path", ExistingDir]);

        cli.Result.Errors.Should().BeEmpty();
    }

    // An option the command does not define is an error, never something silently ignored.
    [Fact]
    public void VerifyCommand_UnknownOption_IsRejected()
    {
        CommandLine cli = new(["verify", "--path", ExistingDir, "--nosuchoption"]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void VerifyCommand_WithDbAndThreads_ParsesOptions()
    {
        CommandLine cli = new([
            "verify",
            "--path",
            ExistingDir,
            "--db",
            "Verify.db",
            "--threads",
            "2",
            "--reprocess",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.DbFile!.Name.Should().Be("Verify.db");
        options.Threads.Should().Be(2);
        options.Reprocess.Should().BeTrue();
    }

    [Fact]
    public void VerifyCommand_MissingPath_ReportsError()
    {
        CommandLine cli = new(["verify"]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void VerifyCommand_IsRegisteredAsSubcommand()
    {
        CommandLine cli = new(["--help"]);

        cli.Root.Subcommands.Select(command => command.Name).Should().Contain("verify");
    }

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

    [Fact]
    public void TrashCommand_ApiKeyFile_Parses()
    {
        string dbPath = Path.GetTempFileName();
        string keyFile = Path.GetTempFileName();
        File.WriteAllText(keyFile, "file-key\n");
        try
        {
            CommandLine cli = new([
                "trash",
                "--url",
                "http://immich:2283",
                "--apikey-file",
                keyFile,
                "--trashdb",
                dbPath,
            ]);

            cli.Result.Errors.Should().BeEmpty();
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void TrashCommand_ApiKeyFileNonExistent_ValidationError()
    {
        string dbPath = Path.GetTempFileName();
        try
        {
            CommandLine cli = new([
                "trash",
                "--url",
                "http://immich:2283",
                "--apikey-file",
                "/nonexistent/path/immich_api_key.txt",
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
    public void TrashCommand_BothApiKeyAndFile_ValidationError()
    {
        string dbPath = Path.GetTempFileName();
        string keyFile = Path.GetTempFileName();
        File.WriteAllText(keyFile, "file-key");
        try
        {
            CommandLine cli = new([
                "trash",
                "--url",
                "http://immich:2283",
                "--apikey",
                "inline-key",
                "--apikey-file",
                keyFile,
                "--trashdb",
                dbPath,
            ]);

            cli.Result.Errors.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void TrashCommand_EmptyApiKeyFile_ValidationError()
    {
        string dbPath = Path.GetTempFileName();
        string keyFile = Path.GetTempFileName();
        File.WriteAllText(keyFile, "   \n");
        try
        {
            CommandLine cli = new([
                "trash",
                "--url",
                "http://immich:2283",
                "--apikey-file",
                keyFile,
                "--trashdb",
                dbPath,
            ]);

            cli.Result.Errors.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(keyFile);
        }
    }

    // -- API key resolution ---------------------------------------------------

    [Fact]
    public void ResolveApiKey_InlineKey_ReturnsKey() =>
        CommandLine.ResolveApiKey("inline-key", null).Should().Be("inline-key");

    [Fact]
    public void ResolveApiKey_FileTrimmed_ReturnsContents()
    {
        string keyFile = Path.GetTempFileName();
        File.WriteAllText(keyFile, "  file-key\n");
        try
        {
            CommandLine.ResolveApiKey(null, new FileInfo(keyFile)).Should().Be("file-key");
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public void ReadApiKeyFile_UnreadableFile_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess)
        {
            string keyFile = Path.GetTempFileName();
            File.WriteAllText(keyFile, "file-key");
            File.SetUnixFileMode(keyFile, UnixFileMode.None);
            try
            {
                CommandLine.ReadApiKeyFile(new FileInfo(keyFile)).Should().BeNull();
            }
            finally
            {
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(keyFile);
            }
        }
        else
        {
            Assert.Skip("Requires Unix file modes and a non-privileged process");
        }
    }

    [Fact]
    public void ResolveApiKey_FilePreferredOverInline_ReturnsFileContents()
    {
        string keyFile = Path.GetTempFileName();
        File.WriteAllText(keyFile, "file-key");
        try
        {
            CommandLine.ResolveApiKey("inline-key", new FileInfo(keyFile)).Should().Be("file-key");
        }
        finally
        {
            File.Delete(keyFile);
        }
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

    // -- --failunwritable -------------------------------------------------------

    [Fact]
    public void FailUnwritableOption_DefaultFalse()
    {
        CommandLine cli = new(["import", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.FailUnwritable.Should().BeFalse();
    }

    [Fact]
    public void FailUnwritableOption_Import_ParsedTrue()
    {
        CommandLine cli = new([
            "import",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--failunwritable",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.FailUnwritable.Should().BeTrue();
    }

    [Fact]
    public void FailUnwritableOption_Process_ParseError()
    {
        CommandLine cli = new(["process", "--path", ExistingDir, "--failunwritable"]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    // -- --imagehash and trash --processdb -------------------------------------

    [Fact]
    public void ImageHashOption_DefaultFalse()
    {
        CommandLine cli = new(["import", "--path", ExistingDir, "--outpath", ExistingDir]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.ImageHash.Should().BeFalse();
    }

    [Fact]
    public void ImageHashOption_ImportWithDb_ParsedTrue()
    {
        CommandLine cli = new([
            "import",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--db",
            Path.Combine(Path.GetTempPath(), "Import.db"),
            "--imagehash",
        ]);

        CommandLine.Options options = cli.CreateOptions(cli.Result);

        cli.Result.Errors.Should().BeEmpty();
        options.ImageHash.Should().BeTrue();
    }

    [Fact]
    public void ImageHashOption_ImportWithoutDbOrTrashDb_ValidationError()
    {
        CommandLine cli = new([
            "import",
            "--path",
            ExistingDir,
            "--outpath",
            ExistingDir,
            "--imagehash",
        ]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void ImageHashOption_ProcessWithDbAndTrashDb_ParsedTrue()
    {
        string trashDbFile = Path.Combine(Path.GetTempPath(), $"trash_{Guid.NewGuid()}.db");
        File.WriteAllBytes(trashDbFile, []);
        try
        {
            CommandLine cli = new([
                "process",
                "--path",
                ExistingDir,
                "--db",
                Path.Combine(Path.GetTempPath(), "Process.db"),
                "--trashdb",
                trashDbFile,
                "--imagehash",
            ]);

            CommandLine.Options options = cli.CreateOptions(cli.Result);

            cli.Result.Errors.Should().BeEmpty();
            options.ImageHash.Should().BeTrue();
        }
        finally
        {
            File.Delete(trashDbFile);
        }
    }

    [Fact]
    public void ImageHashOption_ProcessWithoutTrashDb_ValidationError()
    {
        CommandLine cli = new([
            "process",
            "--path",
            ExistingDir,
            "--db",
            Path.Combine(Path.GetTempPath(), "Process.db"),
            "--imagehash",
        ]);

        cli.Result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void TrashCommand_ProcessDb_Parses()
    {
        string dbPath = Path.GetTempFileName();
        string processDbPath = Path.GetTempFileName();
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
                "--processdb",
                processDbPath,
            ]);

            CommandLine.Options options = cli.CreateOptions(cli.Result);

            cli.Result.Errors.Should().BeEmpty();
            options.DbFile.Should().NotBeNull();
            options.DbFile.FullName.Should().Be(new FileInfo(processDbPath).FullName);
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(processDbPath);
        }
    }

    [Fact]
    public void TrashCommand_ProcessDbNonExistent_ValidationError()
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
                "--processdb",
                "/nonexistent/path/process.db",
            ]);

            cli.Result.Errors.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Theory]
    [InlineData(PosixSignal.SIGINT)]
    [InlineData(PosixSignal.SIGTERM)]
    public void OnTerminationSignal_FirstSignal_CancelsAndKeepsProcess(PosixSignal signal)
    {
        using CancellationTokenSource terminationSource = new();
        PosixSignalContext context = new(signal);
        PosixSignal? recorded = null;

        Program.OnTerminationSignal(context, terminationSource, received => recorded = received);

        context.Cancel.Should().BeTrue();
        terminationSource.IsCancellationRequested.Should().BeTrue();
        recorded.Should().Be(signal);
    }

    [Fact]
    public void OnTerminationSignal_SecondSignal_TakesDefaultAction()
    {
        using CancellationTokenSource terminationSource = new();
        List<PosixSignal> recorded = [];
        Program.OnTerminationSignal(
            new PosixSignalContext(PosixSignal.SIGINT),
            terminationSource,
            recorded.Add
        );
        PosixSignalContext second = new(PosixSignal.SIGTERM);

        Program.OnTerminationSignal(second, terminationSource, recorded.Add);

        second.Cancel.Should().BeFalse();
        recorded.Should().Equal(PosixSignal.SIGINT);
    }

    [Theory]
    [InlineData(PosixSignal.SIGINT, 2, ExitCode.Interrupted)]
    [InlineData(PosixSignal.SIGTERM, 15, ExitCode.Terminated)]
    public void SignalExit_MapsSignalToNumberAndExitCode(
        PosixSignal signal,
        int number,
        int exitCode
    )
    {
        Program.SignalExit(signal).Should().Be((number, exitCode));
        exitCode.Should().Be(128 + number);
    }

    [Fact]
    public async Task Main_SigInt_DiesBySignalAfterCleanupSoCallingLoopStops()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Requires setsid, env --default-signal, and bash");
        }

        DirectoryInfo directory = Directory.CreateTempSubdirectory("PhotoCleanerTests_");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using System.Diagnostics.Process loop = new();
        loop.StartInfo = new System.Diagnostics.ProcessStartInfo("setsid")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory.FullName,
        };
        foreach (
            string argument in (string[])
                [
                    "env",
                    "--default-signal=INT",
                    "bash",
                    "-c",
                    "\"$@\"; echo \"loop continued\"",
                    "bash",
                    "dotnet",
                    Path.Combine(AppContext.BaseDirectory, "PhotoCleaner.dll"),
                    "trash",
                    "--url",
                    $"http://127.0.0.1:{port}",
                    "--apikey",
                    "test-key",
                    "--trashdb",
                    Path.Combine(directory.FullName, "trash.db"),
                ]
        )
        {
            loop.StartInfo.ArgumentList.Add(argument);
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        bool started = false;
        try
        {
            started = loop.Start();
            Task<string> output = loop.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = loop.StandardError.ReadToEndAsync(timeout.Token);
            using TcpClient request = await listener.AcceptTcpClientAsync(timeout.Token);

            using System.Diagnostics.Process kill = System.Diagnostics.Process.Start(
                "kill",
                ["-s", "INT", "--", $"-{loop.Id}"]
            );
            await kill.WaitForExitAsync(timeout.Token);
            await loop.WaitForExitAsync(timeout.Token);

            kill.ExitCode.Should().Be(0);
            string log = await output + await error;
            log.Should().Contain("Trash cancelled");
            log.Should().NotContain("loop continued");
            loop.ExitCode.Should().Be(ExitCode.Interrupted);
        }
        finally
        {
            if (started && !loop.HasExited)
            {
                loop.Kill(entireProcessTree: true);
            }
            directory.Delete(true);
        }
    }

    [Fact]
    public void ConfigureInvocation_DisablesBuiltInTerminationHandler()
    {
        CommandLine cli = new(["process", "--path", ExistingDir]);

        Program.ConfigureInvocation(cli.Result);

        cli.Result.InvocationConfiguration.ProcessTerminationTimeout.Should().BeNull();
        cli.Result.InvocationConfiguration.EnableDefaultExceptionHandler.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessCommand_CancelledToken_ExitsAsCancelled()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("PhotoCleanerTests_");
        try
        {
            CommandLine cli = new(["process", "--path", directory.FullName]);
            Program.ConfigureInvocation(cli.Result);
            using CancellationTokenSource terminationSource = new();
            await terminationSource.CancelAsync();

            int exitCode = await cli.Result.InvokeAsync(cancellationToken: terminationSource.Token);

            exitCode.Should().Be(ExitCode.Error);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
