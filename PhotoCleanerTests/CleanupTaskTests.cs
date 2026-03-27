using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class CleanupTaskTests
{
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
            OutDbFile = null,
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
        string dir = Path.Combine(
            Path.GetTempPath(),
            $"CleanupTaskTest_{Path.GetRandomFileName()}"
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string path) => File.WriteAllBytes(path, []);

    // -- No files --------------------------------------------------------------

    [Fact]
    public void Execute_NoFiles_ReturnsZeros()
    {
        (int deleted, int failed) = new CleanupTask(CreateOptions()).Execute([]);

        deleted.Should().Be(0);
        failed.Should().Be(0);
    }

    // -- Supported extensions kept ---------------------------------------------

    [Fact]
    public void Execute_SupportedExtension_NotDeleted()
    {
        string dir = TempDir();
        try
        {
            string jpg = Path.Combine(dir, "photo.jpg");
            string mp4 = Path.Combine(dir, "video.mp4");
            Touch(jpg);
            Touch(mp4);

            (int deleted, int failed) = new CleanupTask(CreateOptions()).Execute([jpg, mp4]);

            deleted.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(jpg).Should().BeTrue();
            File.Exists(mp4).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Unsupported extensions deleted ----------------------------------------

    [Fact]
    public void Execute_UnsupportedExtension_Deleted()
    {
        string dir = TempDir();
        try
        {
            string txt = Path.Combine(dir, "notes.txt");
            string ds = Path.Combine(dir, ".DS_Store");
            Touch(txt);
            Touch(ds);

            (int deleted, int failed) = new CleanupTask(CreateOptions()).Execute([txt, ds]);

            deleted.Should().Be(2);
            failed.Should().Be(0);
            File.Exists(txt).Should().BeFalse();
            File.Exists(ds).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Backup files deleted --------------------------------------------------

    [Fact]
    public void Execute_BackupFile_Deleted()
    {
        string dir = TempDir();
        try
        {
            string bak = Path.Combine(dir, "photo.jpg.bak");
            string bak1 = Path.Combine(dir, "photo.jpg.bak1");
            string out_ = Path.Combine(dir, "photo.jpg.bak.out");
            Touch(bak);
            Touch(bak1);
            Touch(out_);

            (int deleted, int failed) = new CleanupTask(CreateOptions()).Execute([bak, bak1, out_]);

            deleted.Should().Be(3);
            failed.Should().Be(0);
            File.Exists(bak).Should().BeFalse();
            File.Exists(bak1).Should().BeFalse();
            File.Exists(out_).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Mixed: supported files kept, unsupported deleted ---------------------

    [Fact]
    public void Execute_MixedFiles_DeletesOnlyUnsupported()
    {
        string dir = TempDir();
        try
        {
            string jpg = Path.Combine(dir, "photo.jpg");
            string bak = Path.Combine(dir, "photo.jpg.bak");
            string txt = Path.Combine(dir, "readme.txt");
            Touch(jpg);
            Touch(bak);
            Touch(txt);

            (int deleted, int failed) = new CleanupTask(CreateOptions()).Execute([jpg, bak, txt]);

            deleted.Should().Be(2);
            failed.Should().Be(0);
            File.Exists(jpg).Should().BeTrue();
            File.Exists(bak).Should().BeFalse();
            File.Exists(txt).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // -- Dry run ---------------------------------------------------------------

    [Fact]
    public void Execute_DryRun_ReportsCountButLeavesFilesIntact()
    {
        string dir = TempDir();
        try
        {
            string txt = Path.Combine(dir, "notes.txt");
            string jpg = Path.Combine(dir, "photo.jpg");
            Touch(txt);
            Touch(jpg);

            (int deleted, int failed) = new CleanupTask(CreateOptions(dryRun: true)).Execute([
                txt,
                jpg,
            ]);

            deleted.Should().Be(1); // only .txt
            failed.Should().Be(0);
            File.Exists(txt).Should().BeTrue(); // not deleted in dry-run
            File.Exists(jpg).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
