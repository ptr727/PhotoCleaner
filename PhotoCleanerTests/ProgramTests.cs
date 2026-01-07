using PhotoCleaner;

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
        };

        // Assert
        _ = Assert.Single(context.Paths);
        Assert.True(context.DryRun);
        Assert.Equal(4, context.Threads);

        // Cleanup
        Directory.Delete(_testDirectory, true);
    }
}
