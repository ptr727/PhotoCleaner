using System.CommandLine;
using PhotoCleaner;
using Xunit;

namespace PhotoCleanerTests;

public class CommandLineTests
{
    [Fact]
    public void ParseArgumentsValidPathAndDryRunParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--dryrun"];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Single(parseResult.GetValue(pathOption)!);
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)![0].FullName);
        Assert.True(parseResult.GetValue(dryRunOption));
    }

    [Fact]
    public void ParseArgumentsValidPathWithShortOptionsParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["-p", existingPath, "-d"];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Single(parseResult.GetValue(pathOption)!);
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)![0].FullName);
        Assert.True(parseResult.GetValue(dryRunOption));
    }

    [Fact]
    public void ParseArgumentsValidPathOnlyParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Single(parseResult.GetValue(pathOption)!);
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)![0].FullName);
        Assert.False(parseResult.GetValue(dryRunOption));
        Assert.Equal(Math.Max(Environment.ProcessorCount, 4), parseResult.GetValue(threadsOption));
    }

    [Fact]
    public void ParseArgumentsMissingPathReturnsError()
    {
        // Arrange
        string[] args = ["--dryrun"];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> _, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("--path"));
    }

    [Fact]
    public void ParseArgumentsEmptyArgsReturnsError()
    {
        // Arrange
        string[] args = [];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> _, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("--path"));
    }

    [Fact]
    public void ParseArgumentsNonExistentDirectoryReturnsValidationError()
    {
        // Arrange
        const string nonExistentPath = "/this/path/should/not/exist/123456";
        string[] args = ["--path", nonExistentPath];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> _, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("does not exist"));
    }

    [Fact]
    public void ParseArgumentsExistingDirectoryNoValidationErrors()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> _,
            Option<int> __,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Single(parseResult.GetValue(pathOption)!);
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)![0].FullName);
    }

    [Fact]
    public void ParseArgumentsInvalidOptionReturnsError()
    {
        // Arrange
        string[] args = ["--path", "/test/path", "--invalid-option"];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("invalid-option"));
    }

    [Fact]
    public void ParseArgumentsPathWithoutValueReturnsError()
    {
        // Arrange
        string[] args = ["--path"];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
    }

    [Fact]
    public void ParseArgumentsHelpOptionParsesCorrectly()
    {
        // Arrange
        string[] args = ["--help"];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        // Help should not produce errors and should be handled by the command line system
        Assert.False(parseResult.Errors.Any());
    }

    [Theory]
    [InlineData("--path", "/test", "--dryrun")]
    [InlineData("-p", "/test", "-d")]
    [InlineData("--path", "/test/with spaces")]
    [InlineData("-p", "/test/with-dashes")]
    public void ParseArgumentsVariousValidInputsParsesWithoutErrors(params string[] args)
    {
        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert - Focus on parsing, not directory validation for this test
        // We expect parsing to succeed even if directory doesn't exist (validation errors are different)
        Assert.True(
            !parseResult.Errors.Any()
                || parseResult.Errors.All(e => e.Message.Contains("does not exist"))
        );
    }

    [Fact]
    public void RootCommandHasCorrectDescription()
    {
        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();

        // Assert
        Assert.Equal(
            "PhotoCleaner - Pre-process media files for photo management systems.",
            rootCommand.Description
        );
    }

    [Fact]
    public void PathOptionHasCorrectProperties()
    {
        // Act
        (Option<List<DirectoryInfo>> pathOption, Option<bool> _, Option<int> __, RootCommand _) =
            CreateTestCommand();

        // Assert
        Assert.True(pathOption.Required);
        Assert.Equal("The directory path to process.", pathOption.Description);
        Assert.Contains("-p", pathOption.Aliases);
    }

    [Fact]
    public void DryRunOptionHasCorrectProperties()
    {
        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> dryRunOption, Option<int> __, RootCommand _) =
            CreateTestCommand();

        // Assert
        Assert.False(dryRunOption.Required);
        Assert.Equal(
            "Perform a dry run without making changes (default: false).",
            dryRunOption.Description
        );
        Assert.Contains("-d", dryRunOption.Aliases);
    }

    [Fact]
    public void ParseArgumentsMultiplePathsParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string path1 = Directory.GetCurrentDirectory();
        string path2 = Directory.GetCurrentDirectory();
        string[] args = ["--path", path1, "--path", path2];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> _,
            Option<int> __,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(2, parseResult.GetValue(pathOption)!.Count);
        Assert.Equal(path1, parseResult.GetValue(pathOption)![0].FullName);
        Assert.Equal(path2, parseResult.GetValue(pathOption)![1].FullName);
    }

    [Fact]
    public void ParseArgumentsMultiplePathsWithShortOptionsParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string path1 = Directory.GetCurrentDirectory();
        string path2 = Directory.GetCurrentDirectory();
        string[] args = ["-p", path1, "-p", path2, "-d"];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(2, parseResult.GetValue(pathOption)!.Count);
        Assert.Equal(path1, parseResult.GetValue(pathOption)![0].FullName);
        Assert.Equal(path2, parseResult.GetValue(pathOption)![1].FullName);
        Assert.True(parseResult.GetValue(dryRunOption));
    }

    [Fact]
    public void ParseArgumentsMultiplePathsMixedValidInvalidReturnsError()
    {
        // Arrange - Use one existing and one non-existing path
        string existingPath = Directory.GetCurrentDirectory();
        const string nonExistentPath = "/this/path/should/not/exist/123456";
        string[] args = ["--path", existingPath, "--path", nonExistentPath];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("does not exist"));
    }

    [Fact]
    public void ParseArgumentsThreePathsParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string path1 = Directory.GetCurrentDirectory();
        string path2 = Directory.GetCurrentDirectory();
        string path3 = Directory.GetCurrentDirectory();
        string[] args = ["--path", path1, "--path", path2, "--path", path3];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> _,
            Option<int> __,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(3, parseResult.GetValue(pathOption)!.Count);
        Assert.Equal(path1, parseResult.GetValue(pathOption)![0].FullName);
        Assert.Equal(path2, parseResult.GetValue(pathOption)![1].FullName);
        Assert.Equal(path3, parseResult.GetValue(pathOption)![2].FullName);
    }

    [Fact]
    public void ThreadsOptionHasCorrectProperties()
    {
        // Act
        (
            Option<List<DirectoryInfo>> _,
            Option<bool> __,
            Option<int> threadsOption,
            RootCommand _
        ) = CreateTestCommand();

        // Assert
        Assert.False(threadsOption.Required);
        Assert.Equal(
            "Number of parallel threads (default: max(4, core-count)).",
            threadsOption.Description
        );
        Assert.Contains("-t", threadsOption.Aliases);
    }

    [Fact]
    public void ParseArgumentsThreadsOptionWithValueParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--threads", "8"];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(8, parseResult.GetValue(threadsOption));
    }

    [Fact]
    public void ParseArgumentsThreadsOptionWithShortOptionParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["-p", existingPath, "-t", "16"];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(16, parseResult.GetValue(threadsOption));
    }

    [Fact]
    public void ParseArgumentsThreadsOptionDefaultValueIsCorrect()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];
        int expectedDefault = Math.Max(Environment.ProcessorCount, 4);

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(expectedDefault, parseResult.GetValue(threadsOption));
    }

    [Fact]
    public void ParseArgumentsAllOptionsTogetherParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--dryrun", "--threads", "12"];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Single(parseResult.GetValue(pathOption)!);
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)![0].FullName);
        Assert.True(parseResult.GetValue(dryRunOption));
        Assert.Equal(12, parseResult.GetValue(threadsOption));
    }

    [Fact]
    public void ParseArgumentsThreadsOptionZeroReturnsError()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--threads", "0"];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("greater than 0"));
    }

    [Fact]
    public void ParseArgumentsThreadsOptionNegativeReturnsError()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--threads", "-5"];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("greater than 0"));
    }

    [Fact]
    public void ParseArgumentsThreadsOptionAtProcessorCountParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        int processorCount = Environment.ProcessorCount;
        string[] args = ["--path", existingPath, "--threads", processorCount.ToString()];

        // Act
        (
            Option<List<DirectoryInfo>> pathOption,
            Option<bool> dryRunOption,
            Option<int> threadsOption,
            RootCommand rootCommand
        ) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(processorCount, parseResult.GetValue(threadsOption));
    }

    [Fact]
    public void ParseArgumentsThreadsOptionExceedsProcessorCountReturnsError()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        int exceedsCount = Environment.ProcessorCount + 1;
        string[] args = ["--path", existingPath, "--threads", exceedsCount.ToString()];

        // Act
        (Option<List<DirectoryInfo>> _, Option<bool> _, Option<int> __, RootCommand rootCommand) =
            CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(
            parseResult.Errors,
            e => e.Message.Contains($"less than or equal to {Environment.ProcessorCount}")
        );
    }

    /// <summary>
    /// Helper method to create test command structure using the actual CommandLine.CreateRootCommand method
    /// </summary>
    private static (
        Option<List<DirectoryInfo>> pathOption,
        Option<bool> dryRunOption,
        Option<int> threadsOption,
        RootCommand rootCommand
    ) CreateTestCommand()
    {
        RootCommand rootCommand = CommandLine.CreateRootCommand();

        // Extract the options from the root command by type
        Option<List<DirectoryInfo>> pathOption =
            (Option<List<DirectoryInfo>>)
                rootCommand.Options.First(o => o is Option<List<DirectoryInfo>>);
        Option<bool> dryRunOption = (Option<bool>)rootCommand.Options.First(o => o is Option<bool>);
        Option<int> threadsOption = (Option<int>)rootCommand.Options.First(o => o is Option<int>);

        return (pathOption, dryRunOption, threadsOption, rootCommand);
    }
}
