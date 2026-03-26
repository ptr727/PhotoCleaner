using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class UndoTaskTests
{
    // -- Helper utilities -----------------------------------------------------

    private static CommandLine.Options CreateOptions(bool dryRun = false) =>
        new()
        {
            Path = new DirectoryInfo(Path.GetTempPath()),
            Threads = 1,
            DryRun = dryRun,
            DatePath = false,
            SkipBackup = false,
            OutPath = null,
            Format = "yyyy/MM",
            DeleteEmpty = false,
            Move = false,
            TagPath = false,
            Tags = null,
            DbFile = null,
            Rehash = false,
            ShortVideoDuration = MediaUtilities.ShortVideoDuration,
            Reprocess = false,
            LogOptions = new LoggerFactory.Options
            {
                Level = LogEventLevel.Information,
                File = null,
                FileClear = false,
            },
        };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"UndoTaskTest_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string path) => File.WriteAllBytes(path, []);

    private static void WriteContent(string path) => File.WriteAllBytes(path, [1, 2, 3]);

    // -- IsBackupFile ---------------------------------------------------------

    [Theory]
    [InlineData("photo.jpg.bak", true)]
    [InlineData("photo.jpg.bak1", true)]
    [InlineData("photo.jpg.bak99", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("photo.bak.jpg", false)]
    [InlineData("", false)]
    public void IsBackupFile_VariousPaths_ReturnsExpected(string path, bool expected) =>
        UndoTask.IsBackupFile(path).Should().Be(expected);

    // -- IsNumberedBackup -----------------------------------------------------

    [Theory]
    [InlineData("photo.jpg.bak", false)]
    [InlineData("photo.jpg.bak1", true)]
    [InlineData("photo.jpg.bak99", true)]
    [InlineData("photo.jpg", false)]
    public void IsNumberedBackup_VariousPaths_ReturnsExpected(string path, bool expected) =>
        UndoTask.IsNumberedBackup(path).Should().Be(expected);

    // -- GetBackupBase ---------------------------------------------------------

    [Theory]
    [InlineData("photo.jpg.bak", "photo.jpg")]
    [InlineData("photo.jpg.bak1", "photo.jpg")]
    [InlineData("photo.mov.bak99", "photo.mov")]
    public void GetBackupBase_VariousPaths_ReturnsBase(string path, string expected) =>
        UndoTask.GetBackupBase(path).Should().Be(expected);

    // -- Execute: no backups -----------------------------------------------

    [Fact]
    public void Execute_NoBackupFiles_ReturnsZeros()
    {
        string dir = TempDir();
        try
        {
            Touch(Path.Combine(dir, "photo.jpg"));
            string[] files = Directory.GetFiles(dir);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute(files);

            restored.Should().Be(0);
            deleted.Should().Be(0);
            failed.Should().Be(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Scenario 1: deleted live photo / short video (only .bak exists) ------

    [Fact]
    public void Execute_SingleBackupNoCurrentFile_RestoresOriginal()
    {
        string dir = TempDir();
        try
        {
            // img.mp4 was deleted; img.mp4.bak is the only artefact
            string bakPath = Path.Combine(dir, "img.mp4.bak");
            WriteContent(bakPath);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                bakPath,
            ]);

            restored.Should().Be(1);
            deleted.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(Path.Combine(dir, "img.mp4")).Should().BeTrue();
            File.Exists(bakPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Scenario 2: date-from-path (current file AND .bak both exist) --------

    [Fact]
    public void Execute_BackupAndCurrentFileExist_DeletesCurrentAndRestoresBackup()
    {
        string dir = TempDir();
        try
        {
            string filePath = Path.Combine(dir, "photo.jpg");
            string bakPath = filePath + ".bak";
            Touch(filePath);
            WriteContent(bakPath);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                bakPath,
            ]);

            restored.Should().Be(1);
            deleted.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(filePath).Should().BeTrue(); // restored from .bak
            File.Exists(bakPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Scenario 3: single-run video conversion (img.mov.bak + img.mp4) ------

    [Fact]
    public void Execute_SingleRunConversion_RestoresMovAndDeletesMp4()
    {
        string dir = TempDir();
        try
        {
            string mp4Path = Path.Combine(dir, "img.mp4");
            string movBak = Path.Combine(dir, "img.mov.bak");
            Touch(mp4Path);
            WriteContent(movBak);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                movBak,
            ]);

            restored.Should().Be(1);
            deleted.Should().Be(1); // img.mp4 deleted
            failed.Should().Be(0);
            File.Exists(Path.Combine(dir, "img.mov")).Should().BeTrue();
            File.Exists(movBak).Should().BeFalse();
            File.Exists(mp4Path).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Scenario 4: two-run conversion (Rule 2 - .mp4 derived) --------------
    // Files: img.mov.bak  img.mp4.bak  img.mp4

    [Fact]
    public void Execute_TwoRunConversion_DeletesDerivedMp4AndRestoresMov()
    {
        string dir = TempDir();
        try
        {
            string mp4Path = Path.Combine(dir, "img.mp4");
            string mp4Bak = mp4Path + ".bak";
            string movBak = Path.Combine(dir, "img.mov.bak");
            Touch(mp4Path);
            WriteContent(mp4Bak);
            WriteContent(movBak);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                mp4Bak,
                movBak,
            ]);

            restored.Should().Be(1); // img.mov restored
            deleted.Should().BeGreaterThanOrEqualTo(2); // img.mp4 + img.mp4.bak deleted
            failed.Should().Be(0);
            File.Exists(Path.Combine(dir, "img.mov")).Should().BeTrue();
            File.Exists(mp4Path).Should().BeFalse();
            File.Exists(mp4Bak).Should().BeFalse();
            File.Exists(movBak).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Scenario 5: three-run conversion (Rule 1 - numbered backup) ----------
    // Files: img.mov.bak  img.mp4.bak  img.mp4.bak1  img.mp4

    [Fact]
    public void Execute_ThreeRunConversion_DeletesAllDerivedAndRestoresMov()
    {
        string dir = TempDir();
        try
        {
            string mp4Path = Path.Combine(dir, "img.mp4");
            string mp4Bak = mp4Path + ".bak";
            string mp4Bak1 = mp4Path + ".bak1";
            string movBak = Path.Combine(dir, "img.mov.bak");
            Touch(mp4Path);
            WriteContent(mp4Bak);
            WriteContent(mp4Bak1);
            WriteContent(movBak);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                mp4Bak,
                mp4Bak1,
                movBak,
            ]);

            restored.Should().Be(1);
            deleted.Should().BeGreaterThanOrEqualTo(3); // img.mp4 + img.mp4.bak + img.mp4.bak1
            failed.Should().Be(0);
            File.Exists(Path.Combine(dir, "img.mov")).Should().BeTrue();
            File.Exists(mp4Path).Should().BeFalse();
            File.Exists(mp4Bak).Should().BeFalse();
            File.Exists(mp4Bak1).Should().BeFalse();
            File.Exists(movBak).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Scenario 6: dry run - no file changes --------------------------------

    [Fact]
    public void Execute_DryRun_ReturnsCountsButDoesNotChangeFiles()
    {
        string dir = TempDir();
        try
        {
            string mp4Path = Path.Combine(dir, "img.mp4");
            string movBak = Path.Combine(dir, "img.mov.bak");
            Touch(mp4Path);
            WriteContent(movBak);

            (int restored, int deleted, int failed) = new UndoTask(
                CreateOptions(dryRun: true)
            ).Execute([movBak]);

            // Counts are still reported
            restored.Should().Be(1);
            deleted.Should().Be(1);
            failed.Should().Be(0);

            // But files are untouched
            File.Exists(mp4Path).Should().BeTrue();
            File.Exists(movBak).Should().BeTrue();
            File.Exists(Path.Combine(dir, "img.mov")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Scenario: in-place double modification (regression: numbered backup != derived) ----

    [Fact]
    public void Execute_InPlaceDoubleModification_RestoresOriginalAndDeletesAllBackups()
    {
        string dir = TempDir();
        try
        {
            // img.jpg was modified in-place twice (e.g. date-from-path run twice):
            //   original -> img.jpg.bak, modified -> img.jpg.bak1, img.jpg updated again
            string filePath = Path.Combine(dir, "img.jpg");
            string bakPath = filePath + ".bak";
            string bak1Path = filePath + ".bak1";
            Touch(filePath);
            WriteContent(bakPath);
            WriteContent(bak1Path);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                bakPath,
                bak1Path,
            ]);

            restored.Should().Be(1);
            failed.Should().Be(0);
            File.Exists(filePath).Should().BeTrue(); // restored from .bak
            File.Exists(bakPath).Should().BeFalse(); // superseded backup removed
            File.Exists(bak1Path).Should().BeFalse(); // superseded backup removed
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Companion image not touched -------------------------------------------

    [Fact]
    public void Execute_CompanionImageWithNoBackup_IsNotTouched()
    {
        string dir = TempDir();
        try
        {
            // heic companion has no backup - undo must leave it alone
            string heicPath = Path.Combine(dir, "img.heic");
            string movBak = Path.Combine(dir, "img.mov.bak");
            Touch(heicPath);
            WriteContent(movBak);

            _ = new UndoTask(CreateOptions()).Execute([movBak]);

            File.Exists(heicPath).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Scenario: uniquified output via .bak.out companion -------------------
    // img.mp4 pre-existed; process wrote img_1.mp4 + img.gif.bak + img.gif.bak.out

    [Fact]
    public void Execute_CompanionOutFile_DeletesUniquifiedOutputAndLeavesPreExistingMp4()
    {
        string dir = TempDir();
        try
        {
            string preExistingMp4 = Path.Combine(dir, "img.mp4");
            string uniquifiedMp4 = Path.Combine(dir, "img_1.mp4");
            string gifBak = Path.Combine(dir, "img.gif.bak");
            string companionPath = gifBak + ".out";
            Touch(preExistingMp4);
            Touch(uniquifiedMp4);
            WriteContent(gifBak);
            File.WriteAllText(companionPath, uniquifiedMp4);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                gifBak,
            ]);

            restored.Should().Be(1);
            deleted.Should().Be(2); // img_1.mp4 + img.gif.bak.out
            failed.Should().Be(0);
            File.Exists(Path.Combine(dir, "img.gif")).Should().BeTrue(); // restored
            File.Exists(gifBak).Should().BeFalse();
            File.Exists(companionPath).Should().BeFalse();
            File.Exists(uniquifiedMp4).Should().BeFalse(); // derived output deleted
            File.Exists(preExistingMp4).Should().BeTrue(); // pre-existing untouched
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_CompanionOutFile_DryRun_ReportsCountsButLeavesFilesIntact()
    {
        string dir = TempDir();
        try
        {
            string preExistingMp4 = Path.Combine(dir, "img.mp4");
            string uniquifiedMp4 = Path.Combine(dir, "img_1.mp4");
            string gifBak = Path.Combine(dir, "img.gif.bak");
            string companionPath = gifBak + ".out";
            Touch(preExistingMp4);
            Touch(uniquifiedMp4);
            WriteContent(gifBak);
            File.WriteAllText(companionPath, uniquifiedMp4);

            (int restored, int deleted, int failed) = new UndoTask(
                CreateOptions(dryRun: true)
            ).Execute([gifBak]);

            restored.Should().Be(1);
            deleted.Should().Be(2);
            failed.Should().Be(0);
            File.Exists(gifBak).Should().BeTrue(); // not moved in dry-run
            File.Exists(companionPath).Should().BeTrue(); // not deleted in dry-run
            File.Exists(uniquifiedMp4).Should().BeTrue(); // not deleted in dry-run
            File.Exists(preExistingMp4).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- 0-byte backup handling -----------------------------------------------

    [Fact]
    public void Execute_ZeroByteBackup_FallsBackToNextBackup()
    {
        string dir = TempDir();
        try
        {
            string filePath = Path.Combine(dir, "foo.jpg");
            string bakPath = filePath + ".bak";
            string bak1Path = filePath + ".bak1";
            Touch(filePath);
            Touch(bakPath); // 0 bytes - corrupted
            WriteContent(bak1Path); // valid content

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                bakPath,
                bak1Path,
            ]);

            restored.Should().Be(1);
            failed.Should().Be(0);
            File.Exists(filePath).Should().BeTrue(); // restored from bak1
            File.Exists(bakPath).Should().BeFalse(); // deleted as superseded
            File.Exists(bak1Path).Should().BeFalse(); // moved to foo.jpg
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_ZeroByteBackupNoAlternative_SkipsRestoreAndIncreasesFailed()
    {
        string dir = TempDir();
        try
        {
            string filePath = Path.Combine(dir, "foo.jpg");
            string bakPath = filePath + ".bak";
            Touch(filePath); // original still intact
            Touch(bakPath); // 0 bytes - corrupted, no alternative

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                bakPath,
            ]);

            restored.Should().Be(0);
            failed.Should().Be(1);
            File.Exists(filePath).Should().BeTrue(); // original left untouched
            File.Exists(bakPath).Should().BeTrue(); // 0-byte backup left for inspection
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_AllBackupsZeroBytes_SkipsRestoreAndIncreasesFailed()
    {
        string dir = TempDir();
        try
        {
            string filePath = Path.Combine(dir, "foo.jpg");
            string bakPath = filePath + ".bak";
            string bak1Path = filePath + ".bak1";
            Touch(filePath); // original still intact
            Touch(bakPath); // 0 bytes
            Touch(bak1Path); // 0 bytes

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute([
                bakPath,
                bak1Path,
            ]);

            restored.Should().Be(0);
            failed.Should().Be(1);
            File.Exists(filePath).Should().BeTrue(); // original left untouched
            File.Exists(bakPath).Should().BeTrue(); // backups left for inspection
            File.Exists(bak1Path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
