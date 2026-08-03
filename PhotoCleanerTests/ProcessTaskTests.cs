using System.Collections.Concurrent;
using CliWrap;
using PhotoCleaner;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class ProcessTaskTests(TempDirectoryFixture fixture)
    : IClassFixture<TempDirectoryFixture>,
        IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        if (!fixture.IsAvailable)
        {
            Assert.Skip(fixture.SkipReason ?? "Required tools not available");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class InMemorySink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    // -- Extension / MIME handling --------------------------------------------

    // A file exiftool cannot open looks like one it cannot parse: an error plus valid JSON.
    // The shared metadata call separates them by opening the file first.
    // A permission problem is therefore never recorded as damaged media, in any command.
    [Fact]
    public async Task GetExifToolJsonAsync_UnreadableFile_Throws()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("File mode permissions are only enforced on Linux");
            return;
        }

        string workDir = TempDirectoryFixture.CreateWorkDir();
        string locked = Path.Combine(workDir, "locked.jpg");
        try
        {
            // Arrange
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), locked);
            File.SetUnixFileMode(locked, UnixFileMode.None);
            using (FileStream? readable = TryOpen(locked))
            {
                if (readable is not null)
                {
                    Assert.Skip("Running with permission to read anything");
                    return;
                }
            }

            // Act
            Func<Task> act = async () =>
                await MediaUtilities.GetExifToolJsonAsync(
                    locked,
                    TestContext.Current.CancellationToken
                );

            // Assert
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
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

    private static FileStream? TryOpen(string path)
    {
        try
        {
            return File.OpenRead(path);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnknownExtension_ReturnsUnknownExtension()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "file.xyz");
            File.WriteAllBytes(filePath, []);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.UnknownExtension);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MixedCaseExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // On a case-insensitive filesystem the two names are the same file.
            // The rename is then meaningless and File.Exists cannot distinguish them.
            if (!TempDirectoryFixture.IsFileSystemCaseSensitive(workDir))
            {
                Assert.Skip(
                    "Filesystem is case-insensitive; mixed-case extension rename test only runs on case-sensitive filesystems"
                );
            }

            string filePath = Path.Combine(workDir, "photo.Jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(Path.Combine(workDir, "photo.jpg")).Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedMimeExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes saved with .png extension - MIME mismatch
            string filePath = Path.Combine(workDir, "photo.png");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(Path.Combine(workDir, "photo.jpg")).Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MultipleExtensions_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes with compound extension .heic.jpg - GetFileMediaExtension strips it
            string filePath = Path.Combine(workDir, "photo.heic.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(Path.Combine(workDir, "photo.jpg")).Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonPreferredExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes with .jpeg extension - .jpg is the preferred extension
            string filePath = Path.Combine(workDir, "photo.jpeg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(Path.Combine(workDir, "photo.jpg")).Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Live photos / short videos -------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ShortVideo_DeletesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.ShortVideoFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.ShortVideoFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Deleted);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LivePhotoVideo_DeletesVideoWithMatchingContentIdentifier()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "livephoto.mp4");
            string imagePath = Path.Combine(workDir, "livephoto.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            // Use a copy of the video file as companion (ISOBMFF container supports ContentIdentifier)
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "test-uuid-livephoto");
            await SetContentIdentifierAsync(imagePath, "test-uuid-livephoto");

            // Act
            ProcessTask.ProcessResult result = await CreateContext(videoPath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Deleted);
            File.Exists(videoPath + ".bak").Should().BeTrue();
            File.Exists(videoPath).Should().BeFalse();
            File.Exists(imagePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LivePhotoHevcSuffix_DeletesVideoWithMatchingContentIdentifier()
    {
        // Arrange - new iPhone naming: IMG_1234_HEVC.mov pairs with IMG_1234.heic
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "img_1234_hevc.mp4");
            string imagePath = Path.Combine(workDir, "img_1234.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "test-uuid-hevc");
            await SetContentIdentifierAsync(imagePath, "test-uuid-hevc");

            // Act
            ProcessTask.ProcessResult result = await CreateContext(videoPath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Deleted);
            File.Exists(videoPath + ".bak").Should().BeTrue();
            File.Exists(videoPath).Should().BeFalse();
            File.Exists(imagePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LivePhotoContentIdentifierMismatch_KeepsVideo()
    {
        // Arrange - same name, different ContentIdentifier: not a live photo pair
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "photo.mp4");
            string imagePath = Path.Combine(workDir, "photo.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "uuid-video-AAA");
            await SetContentIdentifierAsync(imagePath, "uuid-image-BBB");

            // Act
            ProcessTask.ProcessResult result = await CreateContext(videoPath);

            // Assert - mismatch means not a live pair, video kept
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
            File.Exists(videoPath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LivePhotoNoContentIdentifier_KeepsVideo()
    {
        // Arrange - companion image found by name but no ContentIdentifier on either file
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "photo.mp4");
            string imagePath = Path.Combine(workDir, "photo.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), imagePath);

            // Act - no ContentIdentifier set on either file
            ProcessTask.ProcessResult result = await CreateContext(videoPath);

            // Assert - cannot confirm pair without ContentIdentifier, video kept
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
            File.Exists(videoPath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LongVideoWithMatchingImage_KeepsVideo()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "longvideo.mp4");
            string imagePath = Path.Combine(workDir, "longvideo.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LongVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "test-uuid-long");
            await SetContentIdentifierAsync(imagePath, "test-uuid-long");

            // Act
            ProcessTask.ProcessResult result = await CreateContext(videoPath);

            // Assert - long video is always kept regardless of ContentIdentifier match
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LiveDurationVideoNoMatchingImage_KeepsVideo()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "solo.mp4");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(videoPath);

            // Assert - no matching image candidate, so video is kept
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Video conversion - remux ---------------------------------------------

    [Fact]
    public async Task ExecuteAsync_MtsFile_RemuxesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.MtsFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.MtsFile), filePath);

            // For MPEG-TS, exiftool returns "m2t", so the first pass renames and re-queues.
            // The remux to .mp4 happens on the second pass.
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath).Should().BeFalse();
            File.Exists(Path.ChangeExtension(filePath, ".m2t")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_M2tsFile_RemuxesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.M2tsFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.M2tsFile), filePath);

            // For MPEG-TS, exiftool returns "m2t", so the first pass renames and re-queues.
            // The remux to .mp4 happens on the second pass.
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath).Should().BeFalse();
            File.Exists(Path.ChangeExtension(filePath, ".m2t")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MkvFile_RemuxesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.MkvFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.MkvFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Video conversion - reencode ------------------------------------------

    [Fact]
    public async Task ExecuteAsync_AviFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AviFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AviFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WmvFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.WmvFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.WmvFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_3gpFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.ThreeGpFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.ThreeGpFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_GifFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.GifFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.GifFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_GifFileWhenMp4AlreadyExists_WritesCompanionWithUniquifiedOutputPath()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string gifPath = Path.Combine(workDir, TempDirectoryFixture.GifFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.GifFile), gifPath);

            // Pre-create a same-stem .mp4 so GetUniqueFileName appends _1
            string canonicalMp4 = Path.ChangeExtension(gifPath, ".mp4");
            File.WriteAllBytes(canonicalMp4, []);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(gifPath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            string bakPath = gifPath + ".bak";
            string companionPath = bakPath + ".out";
            File.Exists(bakPath).Should().BeTrue();
            File.Exists(companionPath).Should().BeTrue();

            string trackedOutput = (
                await File.ReadAllTextAsync(
                    companionPath,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            ).Trim();
            string expectedUnique = Path.Combine(
                workDir,
                Path.GetFileNameWithoutExtension(TempDirectoryFixture.GifFile) + "_1.mp4"
            );
            trackedOutput.Should().Be(expectedUnique);
            File.Exists(trackedOutput).Should().BeTrue();
            File.Exists(canonicalMp4).Should().BeTrue(); // pre-existing mp4 untouched
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Video conversion - skipbackup ----------------------------------------

    [Fact]
    public async Task ExecuteAsync_GifFile_SkipBackup_NoBackupFilesCreated()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.GifFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.GifFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath, skipBackup: true);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeFalse();
            File.Exists(filePath + ".bak.out").Should().BeFalse();
            File.Exists(filePath).Should().BeFalse(); // original deleted after conversion
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Video conversion - PCM audio -----------------------------------------

    [Fact]
    public async Task ExecuteAsync_MovWithPcmAudio_ReencodesAudio()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.PcmMovFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.PcmMovFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // For QuickTime, exiftool returns "mov", which matches the extension, so no rename occurs.
            // PCM audio is detected and re-encoded directly in the first pass.
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath).Should().BeFalse();
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MovWithAacAudio_NoConversion()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AacMovFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AacMovFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // For QuickTime, exiftool returns "mov", which matches the extension, so no rename occurs.
            // AAC audio needs no conversion, so the file is left as-is.
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(filePath).Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeFalse();
            File.Exists(filePath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Mp4WithPcmAudio_ReencodesAudio()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.PcmMp4File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.PcmMp4File), filePath);

            // Act - second pass: .mp4 with PCM audio triggers audio reencode
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert - original backed up, re-encoded output has same path
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(filePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Mp4WithAacAudio_ReturnsSuccess()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AacMp4File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AacMp4File), filePath);

            // Act - second pass: .mp4 with AAC audio requires no conversion
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(filePath).Should().BeTrue();
            File.Exists(filePath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PcmMp4WithContentIdentifier_PreservesContentIdentifierAfterConversion()
    {
        // The first pass re-encodes the audio, since no companion image exists yet.
        // The companion is then added and the second pass deletes the video as a live photo.
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "livephoto.mp4");
            string imagePath = Path.Combine(workDir, "livephoto.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.PcmMp4File), videoPath);
            await SetContentIdentifierAsync(videoPath, "test-uuid-pcm-preserved");

            // Act - first pass: no companion yet -> PCM audio re-encoded, .mp4 queued for reprocess
            ProcessTask.ProcessResult firstResult = await CreateContext(videoPath);

            // Assert - first pass queues for reprocess; original backed up
            firstResult.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(videoPath).Should().BeTrue();
            File.Exists(videoPath + ".bak").Should().BeTrue();

            // Add companion image so live photo detection fires on second pass
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(imagePath, "test-uuid-pcm-preserved");

            // Act - second pass: live photo detection on the re-encoded .mp4
            ProcessTask.ProcessResult secondResult = await CreateContext(videoPath);

            // Assert - ContentIdentifier was preserved through PCM re-encoding, video is deleted
            secondResult.Should().Be(ProcessTask.ProcessResult.Deleted);
            File.Exists(videoPath + ".bak").Should().BeTrue();
            File.Exists(videoPath).Should().BeFalse();
            File.Exists(imagePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- exiftool validation ---------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_FileWithValidationWarnings_IsNotTreatedAsInvalid()
    {
        // A truncated JPEG makes exiftool report "1 Warning: JPEG format error".
        // Warnings must stay advisory, since roughly three quarters of healthy files carry one.
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange
            string filePath = Path.Combine(workDir, "truncated.jpg");
            byte[] source = await File.ReadAllBytesAsync(
                fixture.SourceFile(TempDirectoryFixture.SmallJpegFile),
                TestContext.Current.CancellationToken
            );
            await File.WriteAllBytesAsync(
                filePath,
                source[..(source.Length / 2)],
                TestContext.Current.CancellationToken
            );

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().NotBe(ProcessTask.ProcessResult.Invalid);
            File.Exists(filePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FileExifToolErrorsOn_ReturnsInvalid()
    {
        // On exactly these files, exiftool exits non-zero while still emitting the JSON verdict.
        // Without validation disabled on that call this path throws and is counted as a failure.
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange
            string filePath = Path.Combine(workDir, "garbage.jpg");
            await File.WriteAllBytesAsync(
                filePath,
                [.. Enumerable.Range(0, 500).Select(i => (byte)(i % 251))],
                TestContext.Current.CancellationToken
            );

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Invalid);
            File.Exists(filePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HealthyFile_ReturnsSuccessWithValidationEnabled()
    {
        // Guards the always-on -validate addition: it must not perturb the existing pipeline.
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange
            string filePath = Path.Combine(workDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Success);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- DNG version warning ---------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_DngV1_4_NoWarning()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        InMemorySink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(sink)
                .CreateLogger();

            string filePath = Path.Combine(workDir, TempDirectoryFixture.DngV14File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.DngV14File), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Success);
            sink.Events.Where(e => e.Level == LogEventLevel.Warning)
                .Should()
                .NotContain(e =>
                    e.RenderMessage().Contains("DNG version", StringComparison.Ordinal)
                );
        }
        finally
        {
            Log.Logger = savedLogger;
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DngV1_5_LogsWarning()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        InMemorySink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(sink)
                .CreateLogger();

            string filePath = Path.Combine(workDir, TempDirectoryFixture.DngV15File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.DngV15File), filePath);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Success);
            sink.Events.Should()
                .Contain(e =>
                    e.Level == LogEventLevel.Warning
                    && e.RenderMessage().Contains("DNG version", StringComparison.Ordinal)
                    && e.RenderMessage()
                        .Contains(TempDirectoryFixture.DngVersion15, StringComparison.Ordinal)
                );
        }
        finally
        {
            Log.Logger = savedLogger;
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Timestamp preservation -----------------------------------------------

    [Fact]
    public async Task ExecuteAsync_MkvFile_ConversionPreservesLastWriteTime()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.MkvFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.MkvFile), filePath);
            DateTime originalMtime = new(2019, 7, 22, 14, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, originalMtime);

            // Act
            ProcessTask.ProcessResult result = await CreateContext(filePath);

            // Assert - ffmpeg and exiftool both reset mtime; original must be restored on output
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            string outputFile = Path.ChangeExtension(filePath, ".mp4");
            File.Exists(outputFile).Should().BeTrue();
            File.GetLastWriteTimeUtc(outputFile)
                .Should()
                .BeCloseTo(originalMtime, TimeSpan.FromSeconds(1));
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- GetUniqueFileName -----------------------------------------------------

    [Fact]
    public void GetUniqueFileName_FileDoesNotExist_ReturnsSamePath()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "output.mp4");

            string result = MediaUtilities.GetUniqueFileName(filePath);

            result.Should().Be(filePath);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void GetUniqueFileName_FileExists_AppendsUnderscore1()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "output.mp4");
            File.WriteAllBytes(filePath, []);

            string result = MediaUtilities.GetUniqueFileName(filePath);

            result.Should().Be(Path.Combine(workDir, "output_1.mp4"));
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void GetUniqueFileName_FileAndUnderscore1Exist_AppendsUnderscore2()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "output.mp4");
            File.WriteAllBytes(filePath, []);
            File.WriteAllBytes(Path.Combine(workDir, "output_1.mp4"), []);

            string result = MediaUtilities.GetUniqueFileName(filePath);

            result.Should().Be(Path.Combine(workDir, "output_2.mp4"));
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- SplitExtensionConflicts -----------------------------------------------

    [Fact]
    public void SplitExtensionConflicts_NoConflicts_KeepsAllFiles()
    {
        ConcurrentBag<string> files =
        [
            "/photos/IMG_0001.jpg",
            "/photos/IMG_0002.jpg",
            "/photos/IMG_0003.png",
        ];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(3);
        deferred.Should().BeEmpty();
    }

    [Fact]
    public void SplitExtensionConflicts_SameStemDifferentExtensions_DefersExtra()
    {
        ConcurrentBag<string> files = ["/photos/IMG_2859.DNG", "/photos/IMG_2859.jpg"];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(1);
        deferred.Should().HaveCount(1);
        kept.Concat(deferred)
            .Should()
            .BeEquivalentTo(["/photos/IMG_2859.DNG", "/photos/IMG_2859.jpg"]);
    }

    [Fact]
    public void SplitExtensionConflicts_ThreeExtensionVariants_DefersTwoKeepsOne()
    {
        ConcurrentBag<string> files =
        [
            "/photos/photo.jpeg",
            "/photos/photo.jpg",
            "/photos/photo.png",
        ];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(1);
        deferred.Should().HaveCount(2);
        kept.Concat(deferred)
            .Should()
            .BeEquivalentTo(["/photos/photo.jpeg", "/photos/photo.jpg", "/photos/photo.png"]);
    }

    [Fact]
    public void SplitExtensionConflicts_CaseInsensitiveStem_DefersExtra()
    {
        ConcurrentBag<string> files = ["/photos/IMG_0001.DNG", "/photos/img_0001.jpg"];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(1);
        deferred.Should().HaveCount(1);
    }

    [Fact]
    public void SplitExtensionConflicts_DifferentDirectories_NoConflict()
    {
        ConcurrentBag<string> files = ["/photos/2019/IMG_0001.DNG", "/photos/2020/IMG_0001.jpg"];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(2);
        deferred.Should().BeEmpty();
    }

    [Fact]
    public void SplitExtensionConflicts_EmptyBag_ReturnsEmpty()
    {
        ConcurrentBag<string> files = [];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().BeEmpty();
        deferred.Should().BeEmpty();
    }

    [Fact]
    public void SplitExtensionConflicts_MixedConflictsAndUnique_PartitionsCorrectly()
    {
        ConcurrentBag<string> files =
        [
            "/photos/IMG_0001.DNG",
            "/photos/IMG_0001.jpg",
            "/photos/IMG_0002.jpg",
            "/photos/IMG_0003.jpeg",
            "/photos/IMG_0003.jpg",
        ];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(3);
        deferred.Should().HaveCount(2);
        kept.Concat(deferred).Should().HaveCount(5);
    }

    [Fact]
    public void SplitExtensionConflicts_NonMediaSameStem_NoConflict()
    {
        ConcurrentBag<string> files =
        [
            "/photos/PhotoCleaner.Process.db",
            "/photos/PhotoCleaner.Process.log",
        ];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(2);
        deferred.Should().BeEmpty();
        kept.Should()
            .BeEquivalentTo([
                "/photos/PhotoCleaner.Process.db",
                "/photos/PhotoCleaner.Process.log",
            ]);
    }

    [Fact]
    public void SplitExtensionConflicts_MediaWithMultipleDots_GroupsByLastExtension()
    {
        ConcurrentBag<string> files = ["/photos/IMG.2024.dng", "/photos/IMG.2024.jpg"];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(1);
        deferred.Should().HaveCount(1);
        kept.Concat(deferred)
            .Should()
            .BeEquivalentTo(["/photos/IMG.2024.dng", "/photos/IMG.2024.jpg"]);
    }

    [Fact]
    public void SplitExtensionConflicts_NoExtensionFiles_NoConflict()
    {
        ConcurrentBag<string> files = ["/photos/README", "/photos/LICENSE"];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(2);
        deferred.Should().BeEmpty();
        kept.Should().BeEquivalentTo(["/photos/README", "/photos/LICENSE"]);
    }

    [Fact]
    public void SplitExtensionConflicts_MediaAndNonMediaSameStem_NoConflict()
    {
        ConcurrentBag<string> files = ["/photos/IMG_0001.jpg", "/photos/IMG_0001.txt"];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(2);
        deferred.Should().BeEmpty();
        kept.Should().BeEquivalentTo(["/photos/IMG_0001.jpg", "/photos/IMG_0001.txt"]);
    }

    [Fact]
    public void SplitExtensionConflicts_HiddenDotFile_NotMisclassified()
    {
        ConcurrentBag<string> files = ["/photos/.gitignore"];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(1);
        deferred.Should().BeEmpty();
        kept.Should().BeEquivalentTo(["/photos/.gitignore"]);
    }

    [Fact]
    public void SplitExtensionConflicts_MixedMediaAndNonMediaMultiDot_OnlyMediaConflict()
    {
        ConcurrentBag<string> files =
        [
            "/photos/IMG.jpg",
            "/photos/IMG.dng",
            "/photos/PhotoCleaner.Process.db",
            "/photos/PhotoCleaner.Process.log",
        ];

        (ConcurrentBag<string> kept, ConcurrentBag<string> deferred) =
            ProcessCommand.SplitExtensionConflicts(files);

        kept.Should().HaveCount(3);
        deferred.Should().HaveCount(1);
        kept.Concat(deferred)
            .Should()
            .BeEquivalentTo([
                "/photos/IMG.jpg",
                "/photos/IMG.dng",
                "/photos/PhotoCleaner.Process.db",
                "/photos/PhotoCleaner.Process.log",
            ]);
        deferred.First().Should().BeOneOf("/photos/IMG.jpg", "/photos/IMG.dng");
    }

    // -- TrashDb: file matching trash hash is deleted from disk and DB --------

    [Fact]
    public async Task ExecuteAsync_TrashDb_MatchingFile_DeletedFromDiskAndDb()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        string dbPath = Path.Combine(workDir, "Process.db");
        string trashDbPath = Path.Combine(workDir, "Trash.db");
        try
        {
            string filePath = Path.Combine(workDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);
            (string _, string sha1) = await Database.ComputeHashesAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync(TestContext.Current.CancellationToken);
            await trashDb.InsertHashAsync(
                sha1,
                cancellationToken: TestContext.Current.CancellationToken
            );

            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateOptions(),
                new FileInfo(filePath),
                db,
                trashDb,
                [],
                new SkippedExtensionTracker(),
                TestContext.Current.CancellationToken
            );

            result.Should().Be(ProcessTask.ProcessResult.Deleted);
            File.Exists(filePath).Should().BeFalse();
            FileRecord? row = await db.GetByPathAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );
            row.Should().BeNull();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TrashDb_NonMatchingFile_NotDeleted()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        string dbPath = Path.Combine(workDir, "Process.db");
        string trashDbPath = Path.Combine(workDir, "Trash.db");
        try
        {
            string filePath = Path.Combine(workDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync(TestContext.Current.CancellationToken);
            await trashDb.InsertHashAsync(
                "0000000000000000000000000000000000000000",
                cancellationToken: TestContext.Current.CancellationToken
            );

            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateOptions(),
                new FileInfo(filePath),
                db,
                trashDb,
                [],
                new SkippedExtensionTracker(),
                TestContext.Current.CancellationToken
            );

            result.Should().NotBe(ProcessTask.ProcessResult.Deleted);
            File.Exists(filePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Helpers ---------------------------------------------------------------

    private static async Task SetContentIdentifierAsync(string filePath, string contentId) =>
        // exiftool may exit with code 1 for warnings; suppress validation
        await Cli.Wrap("exiftool")
            .WithArguments([
                $"-Keys:ContentIdentifier={contentId}",
                "-overwrite_original",
                filePath,
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

    private static CommandLine.Options CreateOptions(
        bool dryRun = false,
        bool skipBackup = false,
        bool rehash = false
    ) =>
        new()
        {
            Path = new DirectoryInfo(Path.GetTempPath()),
            Threads = 1,
            DryRun = dryRun,
            DatePath = false,
            SkipBackup = skipBackup,
            OutPath = null,
            Format = "yyyy/MM/dd",
            DeleteEmpty = false,
            Move = false,
            TagPath = false,
            Tags = null,
            DbFile = null,
            Rehash = rehash,
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

    private static Task<ProcessTask.ProcessResult> CreateContext(
        string filePath,
        bool dryRun = false,
        bool skipBackup = false,
        bool rehash = false
    ) =>
        ProcessTask.ExecuteAsync(
            CreateOptions(dryRun: dryRun, skipBackup: skipBackup, rehash: rehash),
            new FileInfo(filePath),
            null,
            null,
            [],
            new SkippedExtensionTracker(),
            default
        );
}
