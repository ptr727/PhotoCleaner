using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public class ProgramTests
{
    private readonly string _testDirectory;

    public ProgramTests()
    {
        // Create a temporary test directory
        _testDirectory = Path.Combine(Path.GetTempPath(), $"PhotoCleanerTests_{Guid.NewGuid()}");
        _ = Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void Execute_WithEmptyDirectory_ReturnsZero()
    {
        // Arrange
        CommandLine.Context context = new()
        {
            Paths = [new DirectoryInfo(_testDirectory)],
            DryRun = true,
            Threads = 1,
            LogLevel = LogEventLevel.Information,
        };
        Program program = new(context);

        // Act
        int result = program.Execute();

        // Assert
        Assert.Equal(0, result);

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public void Execute_WithDryRunTrue_DoesNotModifyFiles()
    {
        // Arrange
        string testFile = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(testFile, "test content");
        DateTime originalModifiedTime = File.GetLastWriteTimeUtc(testFile);

        CommandLine.Context context = new()
        {
            Paths = [new DirectoryInfo(_testDirectory)],
            DryRun = true,
            Threads = 1,
            LogLevel = LogEventLevel.Information,
        };
        Program program = new(context);

        // Act
        _ = program.Execute();

        // Assert - file should still exist with same modification time
        Assert.True(File.Exists(testFile));
        Assert.Equal(originalModifiedTime, File.GetLastWriteTimeUtc(testFile));

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public void Execute_WithThreadsGreaterThanOne_ProcessesFiles()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            string testFile = Path.Combine(_testDirectory, $"file{i}.txt");
            File.WriteAllText(testFile, "test");
        }

        CommandLine.Context context = new()
        {
            Paths = [new DirectoryInfo(_testDirectory)],
            DryRun = true,
            Threads = 4,
            LogLevel = LogEventLevel.Information,
        };
        Program program = new(context);

        // Act - should complete without error
        _ = program.Execute();

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public void CommandLineContext_HasExpectedProperties()
    {
        // Arrange & Act
        CommandLine.Context context = new()
        {
            Paths = [new DirectoryInfo(_testDirectory)],
            DryRun = true,
            Threads = 4,
            LogLevel = LogEventLevel.Debug,
        };

        // Assert
        _ = Assert.Single(context.Paths);
        Assert.True(context.DryRun);
        Assert.Equal(4, context.Threads);
        Assert.Equal(LogEventLevel.Debug, context.LogLevel);

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public void GetUniqueFileName_GeneratesUniqueFileName()
    {
        // Arrange
        string testFile = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(testFile, "test");
        int counter = 1;

        // Act
        string uniqueFileName = Program.GetUniqueFileName(testFile, ref counter);

        // Assert - Should generate test_1.txt since test.txt exists
        Assert.NotEqual(testFile, uniqueFileName);
        Assert.Equal(Path.Combine(_testDirectory, "test_1.txt"), uniqueFileName);
        Assert.False(File.Exists(uniqueFileName));

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public void GetUniqueFileName_WithExistingCounter_IncrementsCounter()
    {
        // Arrange
        string testFile = Path.Combine(_testDirectory, "test.txt");
        File.WriteAllText(testFile, "test");
        string counter1File = Path.Combine(_testDirectory, "test1.txt");
        File.WriteAllText(counter1File, "test1");
        int counter = 1;

        // Act
        string uniqueFileName = Program.GetUniqueFileName(testFile, ref counter);

        // Assert - Should generate test_1.txt since test.txt exists (counter starts at 1)
        Assert.Equal(Path.Combine(_testDirectory, "test_1.txt"), uniqueFileName);
        Assert.Equal(2, counter); // Counter increments to 2
        Assert.False(File.Exists(uniqueFileName));

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public void GetUniqueFileName_WithMultipleCounters_FindsNextAvailable()
    {
        // Arrange
        string testFile = Path.Combine(_testDirectory, "photo.jpg");
        File.WriteAllText(testFile, "original");
        File.WriteAllText(Path.Combine(_testDirectory, "photo1.jpg"), "1");
        File.WriteAllText(Path.Combine(_testDirectory, "photo2.jpg"), "2");
        File.WriteAllText(Path.Combine(_testDirectory, "photo3.jpg"), "3");
        int counter = 1;

        // Act
        string uniqueFileName = Program.GetUniqueFileName(testFile, ref counter);

        // Assert - Should generate photo_1.jpg
        Assert.Equal(Path.Combine(_testDirectory, "photo_1.jpg"), uniqueFileName);
        Assert.Equal(2, counter); // Counter increments to 2
        Assert.False(File.Exists(uniqueFileName));

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public void GetUniqueFileName_WithNoExtension_AppendsCounter()
    {
        // Arrange
        string testFile = Path.Combine(_testDirectory, "README");
        File.WriteAllText(testFile, "content");
        int counter = 1;

        // Act
        string uniqueFileName = Program.GetUniqueFileName(testFile, ref counter);

        // Assert - Should generate README_1 (no extension)
        Assert.Equal(Path.Combine(_testDirectory, "README_1"), uniqueFileName);
        Assert.False(File.Exists(uniqueFileName));

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }

    [Fact]
    public void GetUniqueFileName_WithRefCounter_MaintainsStateAcrossCalls()
    {
        // Arrange - Simulate RenamedMixedCaseFiles behavior
        string baseFile = Path.Combine(_testDirectory, "photo.jpg");
        File.WriteAllText(baseFile, "original");
        int counter = 1;

        // Act - Call GetUniqueFileName which increments counter internally
        string unique1 = Program.GetUniqueFileName(baseFile, ref counter);
        string unique2 = Program.GetUniqueFileName(baseFile, ref counter);
        string unique3 = Program.GetUniqueFileName(baseFile, ref counter);

        // Assert - Each file gets sequential counter values, counter increments each time
        Assert.Equal(Path.Combine(_testDirectory, "photo_1.jpg"), unique1);
        Assert.Equal(Path.Combine(_testDirectory, "photo_2.jpg"), unique2);
        Assert.Equal(Path.Combine(_testDirectory, "photo_3.jpg"), unique3);
        Assert.Equal(4, counter); // Counter should be at 4 after three calls (1->2->3->4)

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }
}
