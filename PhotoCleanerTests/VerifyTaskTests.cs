using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

// A misread line becomes a false accusation against a real photo, so non-verdict output is ignored.
public sealed class VerifyTaskTests(TempDirectoryFixture fixture)
    : IClassFixture<TempDirectoryFixture>
{
    [Fact]
    public void ParseLine_SuccessfulVerdict_ParsesPathAndVia()
    {
        // Act
        ImmichVerifyLine? line = VerifyTask.ParseLine(
            """{"path":"/photos/IMG_0969.HEIC","ok":true,"via":"decode"}"""
        );

        // Assert
        line.Should().NotBeNull();
        line!.Path.Should().Be("/photos/IMG_0969.HEIC");
        line.Ok.Should().BeTrue();
        line.Via.Should().Be("decode");
    }

    [Fact]
    public void ParseLine_FailureVerdict_ParsesError()
    {
        // Arrange: the error Immich actually emits for the reported defect
        const string json = """
            {"path":"/photos/bad.heic","ok":false,"via":"","error":"Input file has corrupt header: bad seek to 104941"}
            """;

        // Act
        ImmichVerifyLine? line = VerifyTask.ParseLine(json);

        // Assert
        line.Should().NotBeNull();
        line!.Ok.Should().BeFalse();
        line.Error.Should().Contain("corrupt header");
    }

    [Fact]
    public void ParseLine_RawExtractVerdict_ParsesVia()
    {
        // Act
        ImmichVerifyLine? line = VerifyTask.ParseLine(
            """{"path":"/photos/IMG_1603.dng","ok":true,"via":"decode+extract"}"""
        );

        // Assert
        line!.Via.Should().Be("decode+extract");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(node:1) Warning: some deprecation notice")]
    [InlineData("npm notice")]
    public void ParseLine_NonJsonOutput_ReturnsNull(string line)
    {
        // Act
        ImmichVerifyLine? result = VerifyTask.ParseLine(line);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParseLine_TruncatedJson_ReturnsNullWithoutThrowing()
    {
        // Arrange: what a killed container leaves behind mid-write
        const string partial = """{"path":"/photos/IMG_0001.HEIC","ok":tr""";

        // Act
        Func<ImmichVerifyLine?> act = () => VerifyTask.ParseLine(partial);

        // Assert
        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void ParseLine_UnknownFields_AreIgnored()
    {
        // Arrange: a future script revision adding a field must not break an older binary
        const string json = """
            {"path":"/photos/a.jpg","ok":true,"via":"decode","durationMs":42,"newField":{"x":1}}
            """;

        // Act
        ImmichVerifyLine? line = VerifyTask.ParseLine(json);

        // Assert
        line.Should().NotBeNull();
        line!.Path.Should().Be("/photos/a.jpg");
        line.Ok.Should().BeTrue();
    }

    // A host path is not a valid container path on Windows, so paths are translated at the boundary.

    [Fact]
    public void TryToContainerPath_MapsUnderTheFixedMount()
    {
        string mount = Path.Combine(Path.DirectorySeparatorChar.ToString(), "photos");
        string file = Path.Combine(mount, "2024", "IMG_0001.HEIC");

        VerifyTask.TryToContainerPath(mount, file, out string container).Should().BeTrue();
        container.Should().Be("/photocleaner/2024/IMG_0001.HEIC");
    }

    [Fact]
    public void TryToContainerPath_FileAtMountRoot_HasNoExtraSeparator()
    {
        string mount = Path.Combine(Path.DirectorySeparatorChar.ToString(), "photos");
        string file = Path.Combine(mount, "IMG_0001.HEIC");

        VerifyTask.TryToContainerPath(mount, file, out string container).Should().BeTrue();
        container.Should().Be("/photocleaner/IMG_0001.HEIC");
    }

    [Fact]
    public void TryToContainerPath_AlwaysUsesForwardSlashes()
    {
        string mount = Path.Combine(Path.DirectorySeparatorChar.ToString(), "photos");
        string file = Path.Combine(mount, "a", "b", "c.jpg");

        VerifyTask.TryToContainerPath(mount, file, out string container).Should().BeTrue();

        container.Should().StartWith("/photocleaner/");
        container.Should().NotContain("\\");
    }

    // A path outside the mount maps to a container path that leaves it, naming a different file.
    [Theory]
    [InlineData("elsewhere", "secret.jpg")]
    [InlineData("photos-other", "IMG_0001.HEIC")]
    public void TryToContainerPath_FileOutsideTheMount_Fails(string sibling, string name)
    {
        string root = Path.DirectorySeparatorChar.ToString();
        string mount = Path.Combine(root, "photos");
        string file = Path.Combine(root, sibling, name);

        VerifyTask.TryToContainerPath(mount, file, out string container).Should().BeFalse();
        container.Should().BeEmpty();
    }

    // A name merely starting with two dots is an ordinary file, not an escape.
    [Fact]
    public void TryToContainerPath_NameStartingWithDots_IsMapped()
    {
        string mount = Path.Combine(Path.DirectorySeparatorChar.ToString(), "photos");
        string file = Path.Combine(mount, "..hidden.jpg");

        VerifyTask.TryToContainerPath(mount, file, out string container).Should().BeTrue();
        container.Should().Be("/photocleaner/..hidden.jpg");
    }

    // A file that cannot be read is a tooling gap, never damaged media.
    // The verdict must not depend on whether a database happens to be configured.
    [Fact]
    public async Task ExecuteAsync_UnreadableFileWithoutDatabase_CountsFailedNotInvalid()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("File mode permissions are only enforced on Linux");
            return;
        }

        if (!ImmichImageAvailable())
        {
            Assert.Skip(ImageSkipReason);
            return;
        }

        string workDir = TempDirectoryFixture.CreateWorkDir();
        string locked = Path.Combine(workDir, "unreadable.jpg");
        try
        {
            // Arrange - a decodable file the process cannot open
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), locked);
            File.SetUnixFileMode(locked, UnixFileMode.None);
            if (CanRead(locked))
            {
                Assert.Skip("Running with permission to read anything, so the file stays readable");
                return;
            }

            VerifyTask task = new(CreateOptions(workDir), null, new SkippedExtensionTracker());

            // Act
            VerifyTask.Counts counts = await task.ExecuteAsync(
                [locked],
                TestContext.Current.CancellationToken
            );

            // Assert
            counts.Failed.Should().Be(1);
            counts.Invalid.Should().Be(0);
            counts.Verified.Should().Be(0);
        }
        finally
        {
            if (File.Exists(locked))
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // Nothing to decode means nothing needs Docker.
    // Deliberately ungated on the image, since a host without one is the case this protects.
    // An unconditional preflight throws here instead of counting.
    [Fact]
    public async Task ExecuteAsync_OnlyNonMediaFiles_NeedsNoDocker()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - a tree holding nothing this command can verify
            string notes = Path.Combine(workDir, "notes.txt");
            string readme = Path.Combine(workDir, "readme.md");
            await File.WriteAllTextAsync(notes, "text", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(readme, "text", TestContext.Current.CancellationToken);

            VerifyTask task = new(CreateOptions(workDir), null, new SkippedExtensionTracker());

            // Act
            VerifyTask.Counts counts = await task.ExecuteAsync(
                [notes, readme],
                TestContext.Current.CancellationToken
            );

            // Assert
            counts.Ignored.Should().Be(2);
            counts.Verified.Should().Be(0);
            counts.Invalid.Should().Be(0);
            counts.Failed.Should().Be(0);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // A database row matching on size and mtime returns cached hashes without opening the file.
    // The hash read therefore cannot be relied on to notice that the file is unreadable.
    [Fact]
    public async Task ExecuteAsync_UnreadableFileWithCachedHashes_CountsFailedNotInvalid()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("File mode permissions are only enforced on Linux");
            return;
        }

        if (!ImmichImageAvailable())
        {
            Assert.Skip(ImageSkipReason);
            return;
        }

        string workDir = TempDirectoryFixture.CreateWorkDir();
        string target = Path.Combine(workDir, "cached.jpg");
        string dbPath = Path.Combine(workDir, "Verify.db");
        try
        {
            // Arrange - index the file while it is readable, so the row caches its hashes
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), target);
            await using Database database = new(dbPath);
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask index = new(CreateOptions(workDir), database, new SkippedExtensionTracker());
            _ = await index.IndexFileAsync(target, TestContext.Current.CancellationToken);

            // The row is present and unverified, so the run reaches the decoder without rehashing
            File.SetUnixFileMode(target, UnixFileMode.None);
            if (CanRead(target))
            {
                Assert.Skip("Running with permission to read anything, so the file stays readable");
                return;
            }

            VerifyTask task = new(CreateOptions(workDir), database, new SkippedExtensionTracker());

            // Act
            VerifyTask.Counts counts = await task.ExecuteAsync(
                [target],
                TestContext.Current.CancellationToken
            );

            // Assert
            counts.Failed.Should().Be(1);
            counts.Invalid.Should().Be(0);
            counts.Verified.Should().Be(0);
        }
        finally
        {
            if (File.Exists(target))
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnreadableDirectory_CountsFailedRatherThanSkipping()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("File mode permissions are only enforced on Linux");
            return;
        }

        if (!ImmichImageAvailable())
        {
            Assert.Skip(ImageSkipReason);
            return;
        }

        string workDir = TempDirectoryFixture.CreateWorkDir();
        string lockedDir = Path.Combine(workDir, "locked");
        string filePath = Path.Combine(lockedDir, "photo.heic");
        try
        {
            // Arrange
            Directory.CreateDirectory(lockedDir);
            await File.WriteAllBytesAsync(
                filePath,
                [.. Enumerable.Range(0, 64).Select(i => (byte)i)],
                TestContext.Current.CancellationToken
            );
            File.SetUnixFileMode(lockedDir, UnixFileMode.None);
            if (File.Exists(filePath))
            {
                Assert.Skip("Running with permission to read anything, so the path stays visible");
                return;
            }

            VerifyTask task = new(CreateOptions(workDir), null, new SkippedExtensionTracker());

            // Act
            VerifyTask.Counts counts = await task.ExecuteAsync(
                [filePath],
                TestContext.Current.CancellationToken
            );

            // Assert
            counts.Failed.Should().Be(1);
            counts.Verified.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(lockedDir))
            {
                File.SetUnixFileMode(
                    lockedDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // Verify only reads, so a file that vanished mid-run means the tree changed underneath it.
    [Fact]
    public async Task ExecuteAsync_FileMissingSinceIndexing_CountsFailed()
    {
        if (!ImmichImageAvailable())
        {
            Assert.Skip(ImageSkipReason);
            return;
        }

        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange: a path that was enumerated but is gone by the time it is verified
            string missing = Path.Combine(workDir, "gone.heic");

            VerifyTask task = new(CreateOptions(workDir), null, new SkippedExtensionTracker());

            // Act
            VerifyTask.Counts counts = await task.ExecuteAsync(
                [missing],
                TestContext.Current.CancellationToken
            );

            // Assert
            counts.Failed.Should().Be(1);
            counts.Verified.Should().Be(0);
            counts.Invalid.Should().Be(0);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // Hashing runs before the structural check when a database is in use.
    // An exception there must not abort a run that may span a whole library.
    [Fact]
    public async Task ExecuteAsync_UnreadableFileWithDatabase_DoesNotAbortTheRun()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("File mode permissions are only enforced on Linux");
            return;
        }

        if (!ImmichImageAvailable())
        {
            Assert.Skip(ImageSkipReason);
            return;
        }

        string workDir = TempDirectoryFixture.CreateWorkDir();
        string locked = Path.Combine(workDir, "unreadable.jpg");
        string dbPath = Path.Combine(workDir, "Verify.db");
        try
        {
            // Arrange - two decodable files either side of one that cannot be opened
            string source = fixture.SourceFile(TempDirectoryFixture.SmallJpegFile);
            string first = Path.Combine(workDir, "a.jpg");
            string last = Path.Combine(workDir, "b.jpg");
            File.Copy(source, first);
            File.Copy(source, last);
            File.Copy(source, locked);
            File.SetUnixFileMode(locked, UnixFileMode.None);
            if (CanRead(locked))
            {
                Assert.Skip("Running with permission to read anything, so the file stays readable");
                return;
            }

            await using Database database = new(dbPath);
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            VerifyTask task = new(CreateOptions(workDir), database, new SkippedExtensionTracker());

            // Act
            VerifyTask.Counts counts = await task.ExecuteAsync(
                [first, locked, last],
                TestContext.Current.CancellationToken
            );

            // Assert - the unreadable file is counted, and the others are still verified
            counts.Failed.Should().Be(1);
            counts.Verified.Should().Be(2);
        }
        finally
        {
            if (File.Exists(locked))
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    private static bool CanRead(string filePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const string ImageSkipReason =
        "Docker or the Immich image is unavailable, and verify has no offline mode";

    // Verify runs Immich's decoder for every file, so these cases cannot run without the image.
    private static bool ImmichImageAvailable()
    {
        try
        {
            using System.Diagnostics.Process? probe = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("docker")
                {
                    // Only the exit code is wanted, so render nothing rather than the manifest.
                    ArgumentList = { "image", "inspect", "--format", "", VerifyTask.ImmichImage },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            );
            if (probe is null)
            {
                return false;
            }

            // Redirected pipes are drained before waiting.
            // A child that fills one blocks on the write while the wait blocks on the child.
            Task<string> output = probe.StandardOutput.ReadToEndAsync();
            Task<string> error = probe.StandardError.ReadToEndAsync();
            if (!probe.WaitForExit(60_000))
            {
                // Disposing the probe frees its handles without ending the child.
                // A wedged docker would otherwise outlive the test that started it.
                try
                {
                    probe.Kill(entireProcessTree: true);

                    // Kill only signals, so the child is still alive when it returns.
                    probe.WaitForExit(5_000);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the timeout expiring and the kill.
                }

                // The kill closes the pipes, so the drains finish rather than being left unobserved.
                try
                {
                    Task.WaitAll([output, error], 5_000);
                }
                catch (AggregateException)
                {
                    // A drain that faulted on the closing pipe is observed here and discarded.
                }

                return false;
            }

            Task.WaitAll(output, error);
            return probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static CommandLine.Options CreateOptions(string path) =>
        new()
        {
            Path = new DirectoryInfo(path),
            Threads = 1,
            DryRun = false,
            DatePath = false,
            SkipBackup = false,
            OutPath = null,
            Format = "yyyy/MM/dd",
            DeleteEmpty = false,
            Move = false,
            TagPath = false,
            Tags = null,
            DbFile = null,
            Rehash = false,
            ShortVideoDuration = MediaUtilities.ShortVideoDuration,
            Reprocess = false,
            MarkProcessed = false,
            ImmichUrl = null,
            ImmichApiKey = null,
            TrashDbFile = null,
            SkipDbFile = null,
            LogOptions = new LoggerFactory.Options
            {
                Level = LogEventLevel.Information,
                File = null,
                FileClear = false,
            },
        };

    [Fact]
    public void ImmichImage_UsesReleaseTag()
    {
        // Immich publishes no :latest tag, so a wrong tag here fails preflight on every run.
        VerifyTask.ImmichImage.Should().Be("ghcr.io/immich-app/immich-server:release");
    }

    [Fact]
    public void VerifyScript_ReferencesImmichOwnModules()
    {
        // Replacing these requires with a reimplementation would silently lose the fidelity.
        ImmichVerifyScript.Verify.Should().Contain("media.repository.js");
        ImmichVerifyScript.Verify.Should().Contain("generateThumbnail");
        ImmichVerifyScript.Verify.Should().Contain("defaults.image.preview");
        ImmichVerifyScript.Preflight.Should().Contain(ImmichVerifyScript.PreflightSentinel);
    }
}
