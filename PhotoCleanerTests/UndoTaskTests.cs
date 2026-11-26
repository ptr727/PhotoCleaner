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

    [Theory]
    [InlineData("clip.mov.bak", true)]
    [InlineData("photo.jpg.bak2", true)]
    [InlineData("notes.txt.bak", false)]
    [InlineData("archive.bak", false)]
    [InlineData("clip.mov", false)]
    public void IsUndoableBackup_VariousPaths_ReturnsExpected(string path, bool expected) =>
        UndoTask.IsUndoableBackup(path).Should().Be(expected);

    [Fact]
    public void ResolveMarkerOutput_AbsolutePathFromAnotherTree_ResolvesBesideBackup()
    {
        string backup = Path.Combine("now", "here", "img.mov.bak");
        string text = Path.Combine(Path.GetTempPath(), "old", "tree", "img_1.mp4") + "\n";

        UndoTask
            .ResolveMarkerOutput(backup, text)
            .Should()
            .Be(Path.Combine("now", "here", "img_1.mp4"));
    }

    [Fact]
    public void ResolveMarkerOutput_MarkerWrittenOnWindows_ResolvesBesideBackup() =>
        UndoTask
            .ResolveMarkerOutput(Path.Combine("here", "img.mov.bak"), @"C:\Photos\old\img_1.mp4")
            .Should()
            .Be(Path.Combine("here", "img_1.mp4"));

    // -- Execute -------------------------------------------------------------

    private static (int restored, int deleted, int failed) Run(string dir, bool dryRun = false) =>
        new UndoTask(CreateOptions(dryRun)).Execute(
            Directory.GetFiles(dir),
            cancellationToken: TestContext.Current.CancellationToken
        );

    private static string[] Names(string dir) =>
        [.. Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [Fact]
    public void Execute_NoBackupFiles_ReturnsZeros()
    {
        string dir = TempDir();
        try
        {
            Touch(Path.Combine(dir, "photo.jpg"));

            Run(dir).Should().Be((0, 0, 0));
            Names(dir).Should().Equal("photo.jpg");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_DeletionBackup_RestoresOriginal()
    {
        string dir = TempDir();
        try
        {
            WriteContent(Path.Combine(dir, "img.mp4.bak"));

            Run(dir).Should().Be((1, 0, 0));
            Names(dir).Should().Equal("img.mp4");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_NameTakenByUnrecordedFile_RestoresToFreeNameAndKeepsCurrent()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "photo.jpg"), [200]);
            WriteContent(Path.Combine(dir, "photo.jpg.bak"));

            Run(dir).Should().Be((1, 0, 0));
            Names(dir).Should().Equal("photo.jpg", "photo_1.jpg");
            File.ReadAllBytes(Path.Combine(dir, "photo.jpg")).Should().Equal(200);
            File.ReadAllBytes(Path.Combine(dir, "photo_1.jpg")).Should().Equal(1, 2, 3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_Conversion_RestoresOriginalAndDeletesOutputAndMarker()
    {
        string dir = TempDir();
        try
        {
            string mp4 = Path.Combine(dir, "img.mp4");
            string movBak = Path.Combine(dir, "img.mov.bak");
            Touch(mp4);
            WriteContent(movBak);
            File.WriteAllText(movBak + ".out", mp4);

            Run(dir).Should().Be((1, 2, 0));
            Names(dir).Should().Equal("img.mov");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_DeletionBackupBesideUnrelatedMp4_KeepsTheMp4()
    {
        string dir = TempDir();
        try
        {
            WriteContent(Path.Combine(dir, "clip.mp4"));
            WriteContent(Path.Combine(dir, "clip.mov.bak"));

            Run(dir).Should().Be((1, 0, 0));
            Names(dir).Should().Equal("clip.mov", "clip.mp4");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_SameStemGroupsWithoutMarkers_RestoresBoth()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "img.mp4"), [200]);
            WriteContent(Path.Combine(dir, "img.mp4.bak"));
            WriteContent(Path.Combine(dir, "img.mov.bak"));

            Run(dir).Should().Be((2, 0, 0));
            Names(dir).Should().Equal("img.mov", "img.mp4", "img_1.mp4");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_TwoOriginalsSameName_RestoresBoth()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "clip.mov.bak"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "clip.mov.bak1"), [2]);

            Run(dir).Should().Be((2, 0, 0));
            Names(dir).Should().Equal("clip.mov", "clip_1.mov");
            File.ReadAllBytes(Path.Combine(dir, "clip.mov")).Should().Equal(1);
            File.ReadAllBytes(Path.Combine(dir, "clip_1.mov")).Should().Equal(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_BackupsDifferingOnlyInCase_RestoresBoth()
    {
        string dir = TempDir();
        try
        {
            WriteContent(Path.Combine(dir, "clip.mov.bak"));
            Assert.SkipWhen(
                File.Exists(Path.Combine(dir, "CLIP.mov.bak")),
                "Requires a case-sensitive filesystem"
            );
            WriteContent(Path.Combine(dir, "CLIP.mov.bak"));

            Run(dir).Should().Be((2, 0, 0));
            Names(dir).Should().Equal("CLIP.mov", "clip.mov");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_ConvertedOutputBackedUpAsShortClip_RestoresOnlyTheOriginal()
    {
        string dir = TempDir();
        try
        {
            string gpBak = Path.Combine(dir, "clip.3gp.bak");
            WriteContent(gpBak);
            File.WriteAllText(gpBak + ".out", Path.Combine(dir, "clip.mp4"));
            WriteContent(Path.Combine(dir, "clip.mp4.bak"));

            Run(dir).Should().Be((1, 2, 0));
            Names(dir).Should().Equal("clip.3gp");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_MarkerFromMovedTree_DeletesTheOutputBesideTheBackup()
    {
        string dir = TempDir();
        try
        {
            string movBak = Path.Combine(dir, "img.mov.bak");
            WriteContent(movBak);
            File.WriteAllText(
                movBak + ".out",
                Path.Combine(Path.GetTempPath(), "moved-away", "img.mp4")
            );
            Touch(Path.Combine(dir, "img.mp4"));

            Run(dir).Should().Be((1, 2, 0));
            Names(dir).Should().Equal("img.mov");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_NumberedBackupMarker_DeletesItsOutput()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "img.mov.bak"), [1]);
            string bak1 = Path.Combine(dir, "img.mov.bak1");
            File.WriteAllBytes(bak1, [2]);
            File.WriteAllText(bak1 + ".out", Path.Combine(dir, "img_1.mp4"));
            Touch(Path.Combine(dir, "img_1.mp4"));

            Run(dir).Should().Be((2, 2, 0));
            Names(dir).Should().Equal("img.mov", "img_1.mov");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_ConversionToSameName_DeletesOutputThenRestoresOriginal()
    {
        string dir = TempDir();
        try
        {
            string mp4 = Path.Combine(dir, "img.mp4");
            File.WriteAllBytes(mp4, [200]);
            File.WriteAllBytes(mp4 + ".bak", [1]);
            File.WriteAllText(mp4 + ".bak.out", mp4);

            Run(dir).Should().Be((1, 2, 0));
            Names(dir).Should().Equal("img.mp4");
            File.ReadAllBytes(mp4).Should().Equal(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_AmbiguousOutputBackups_RestoresEveryBackupAndDeletesNothingElse()
    {
        string dir = TempDir();
        try
        {
            WriteContent(Path.Combine(dir, "clip.mp4.bak"));
            string gpBak = Path.Combine(dir, "clip.3gp.bak");
            WriteContent(gpBak);
            File.WriteAllText(gpBak + ".out", Path.Combine(dir, "clip.mp4"));
            WriteContent(Path.Combine(dir, "clip.mp4.bak1"));

            Run(dir).Should().Be((3, 1, 0));
            Names(dir).Should().Equal("clip.3gp", "clip.mp4", "clip_1.mp4");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_VanishedBackup_FailsItAndRestoresTheRest()
    {
        string dir = TempDir();
        try
        {
            string present = Path.Combine(dir, "a.mov.bak");
            WriteContent(present);

            (int restored, int deleted, int failed) = new UndoTask(CreateOptions()).Execute(
                [Path.Combine(dir, "gone.mov.bak"), present],
                cancellationToken: TestContext.Current.CancellationToken
            );

            (restored, deleted, failed).Should().Be((1, 0, 1));
            Names(dir).Should().Equal("a.mov");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_EmptyMarker_FailsItsBackupAndLeavesIt()
    {
        string dir = TempDir();
        try
        {
            string movBak = Path.Combine(dir, "img.mov.bak");
            WriteContent(movBak);
            File.WriteAllText(movBak + ".out", " \n");
            Touch(Path.Combine(dir, "img.mp4"));

            Run(dir).Should().Be((0, 0, 1));
            Names(dir).Should().Equal("img.mov.bak", "img.mov.bak.out", "img.mp4");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_BackupOfUnsupportedType_IsLeftAlone()
    {
        string dir = TempDir();
        try
        {
            WriteContent(Path.Combine(dir, "notes.txt.bak"));

            Run(dir).Should().Be((0, 0, 0));
            Names(dir).Should().Equal("notes.txt.bak");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_CompanionImageWithNoBackup_IsNotTouched()
    {
        string dir = TempDir();
        try
        {
            Touch(Path.Combine(dir, "img.heic"));
            WriteContent(Path.Combine(dir, "img.mov.bak"));

            Run(dir).Should().Be((1, 0, 0));
            Names(dir).Should().Equal("img.heic", "img.mov");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The img.mp4 file existed before process wrote img_1.mp4, img.gif.bak, and img.gif.bak.out.
    [Fact]
    public void Execute_UniquifiedOutput_DeletesItAndLeavesPreExistingMp4()
    {
        string dir = TempDir();
        try
        {
            Touch(Path.Combine(dir, "img.mp4"));
            Touch(Path.Combine(dir, "img_1.mp4"));
            string gifBak = Path.Combine(dir, "img.gif.bak");
            WriteContent(gifBak);
            File.WriteAllText(gifBak + ".out", Path.Combine(dir, "img_1.mp4"));

            Run(dir).Should().Be((1, 2, 0));
            Names(dir).Should().Equal("img.gif", "img.mp4");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_DryRun_ReportsCountsAndChangesNothing()
    {
        string dir = TempDir();
        try
        {
            string gpBak = Path.Combine(dir, "clip.3gp.bak");
            WriteContent(gpBak);
            File.WriteAllText(gpBak + ".out", Path.Combine(dir, "clip.mp4"));
            WriteContent(Path.Combine(dir, "clip.mp4.bak"));
            WriteContent(Path.Combine(dir, "img.mov.bak"));
            WriteContent(Path.Combine(dir, "img.mov.bak1"));
            string[] before = Names(dir);

            Run(dir, dryRun: true).Should().Be((3, 2, 0));
            Names(dir).Should().Equal(before);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_EmptyBackup_FailsItAndRestoresTheNext()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "foo.jpg"), [200]);
            Touch(Path.Combine(dir, "foo.jpg.bak"));
            WriteContent(Path.Combine(dir, "foo.jpg.bak1"));

            Run(dir).Should().Be((1, 0, 1));
            Names(dir).Should().Equal("foo.jpg", "foo.jpg.bak", "foo_1.jpg");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_AllBackupsEmpty_FailsEachAndLeavesThem()
    {
        string dir = TempDir();
        try
        {
            Touch(Path.Combine(dir, "foo.jpg"));
            Touch(Path.Combine(dir, "foo.jpg.bak"));
            Touch(Path.Combine(dir, "foo.jpg.bak1"));

            Run(dir).Should().Be((0, 0, 2));
            Names(dir).Should().Equal("foo.jpg", "foo.jpg.bak", "foo.jpg.bak1");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_ShortClipBackupBesideLaterConversionOutput_RestoresTheShortClip()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "clip.mp4.bak"), [1]);
            string aviBak = Path.Combine(dir, "clip.avi.bak");
            File.WriteAllBytes(aviBak, [2]);
            File.WriteAllText(aviBak + ".out", Path.Combine(dir, "clip.mp4"));
            File.WriteAllBytes(Path.Combine(dir, "clip.mp4"), [3]);

            Run(dir).Should().Be((2, 1, 0));
            Names(dir).Should().Equal("clip.avi", "clip.mp4", "clip_1.mp4");
            File.ReadAllBytes(Path.Combine(dir, "clip_1.mp4")).Should().Equal(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Execute_FreeNameIsAnotherOriginalsBase_LeavesItForThatOriginal()
    {
        string dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "clip.mov.bak"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "clip.mov.bak1"), [2]);
            File.WriteAllBytes(Path.Combine(dir, "clip_1.mov.bak"), [3]);

            Run(dir).Should().Be((3, 0, 0));
            Names(dir).Should().Equal("clip.mov", "clip_1.mov", "clip_2.mov");
            File.ReadAllBytes(Path.Combine(dir, "clip_1.mov")).Should().Equal(3);
            File.ReadAllBytes(Path.Combine(dir, "clip_2.mov")).Should().Equal(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
