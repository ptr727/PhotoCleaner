using System.CommandLine;
using PhotoCleaner;
using Serilog.Events;

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
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        _ = Assert.Single(context.Paths);
        Assert.Equal(existingPath, context.Paths[0].FullName);
        Assert.True(context.DryRun);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsValidPathWithShortOptionsParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["-p", existingPath, "-d"];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        _ = Assert.Single(context.Paths);
        Assert.Equal(existingPath, context.Paths[0].FullName);
        Assert.True(context.DryRun);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsValidPathOnlyParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        _ = Assert.Single(context.Paths);
        Assert.Equal(existingPath, context.Paths[0].FullName);
        Assert.False(context.DryRun);
        Assert.Equal(Math.Max(Environment.ProcessorCount, 4), context.Threads);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsMissingPathReturnsError()
    {
        // Arrange
        string[] args = ["--dryrun"];

        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        _ = Assert.Single(context.Paths);
        Assert.Equal(existingPath, context.Paths[0].FullName);
    }

    [Fact]
    public void ParseArgumentsInvalidOptionReturnsError()
    {
        // Arrange
        string[] args = ["--path", "/test/path", "--invalid-option"];

        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();

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
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
        Option<List<DirectoryInfo>> pathOption =
            (Option<List<DirectoryInfo>>)
                rootCommand.Options.First(o => o is Option<List<DirectoryInfo>>);

        // Assert
        Assert.True(pathOption.Required);
        Assert.Equal("The directory path to process.", pathOption.Description);
        Assert.Contains("-p", pathOption.Aliases);
    }

    [Fact]
    public void DryRunOptionHasCorrectProperties()
    {
        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
        Option<bool> dryRunOption = rootCommand.Options.OfType<Option<bool>>().First();

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
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(2, context.Paths.Count);
        Assert.Equal(path1, context.Paths[0].FullName);
        Assert.Equal(path2, context.Paths[1].FullName);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsMultiplePathsWithShortOptionsParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string path1 = Directory.GetCurrentDirectory();
        string path2 = Directory.GetCurrentDirectory();
        string[] args = ["-p", path1, "-p", path2, "-d"];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(2, context.Paths.Count);
        Assert.Equal(path1, context.Paths[0].FullName);
        Assert.Equal(path2, context.Paths[1].FullName);
        Assert.True(context.DryRun);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsMultiplePathsMixedValidInvalidReturnsError()
    {
        // Arrange - Use one existing and one non-existing path
        string existingPath = Directory.GetCurrentDirectory();
        const string nonExistentPath = "/this/path/should/not/exist/123456";
        string[] args = ["--path", existingPath, "--path", nonExistentPath];

        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(3, context.Paths.Count);
        Assert.Equal(path1, context.Paths[0].FullName);
        Assert.Equal(path2, context.Paths[1].FullName);
        Assert.Equal(path3, context.Paths[2].FullName);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ThreadsOptionHasCorrectProperties()
    {
        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
        Option<int> threadsOption = (Option<int>)rootCommand.Options.First(o => o is Option<int>);

        // Assert
        Assert.False(threadsOption.Required);
        Assert.Equal(
            "Number of parallel threads (default: Max(ProcessorCount, 4)).",
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
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(8, context.Threads);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsThreadsOptionWithShortOptionParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["-p", existingPath, "-t", "16"];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(16, context.Threads);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsThreadsOptionDefaultValueIsCorrect()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];
        int expectedDefault = Math.Max(Environment.ProcessorCount, 4);

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(expectedDefault, context.Threads);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsAllOptionsTogetherParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--dryrun", "--threads", "12"];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        _ = Assert.Single(context.Paths);
        Assert.Equal(existingPath, context.Paths[0].FullName);
        Assert.True(context.DryRun);
        Assert.Equal(12, context.Threads);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsThreadsOptionZeroReturnsError()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--threads", "0"];

        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
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
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(processorCount, context.Threads);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsThreadsOptionExceedsProcessorCountReturnsError()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        int exceedsCount = Environment.ProcessorCount + 1;
        string[] args = ["--path", existingPath, "--threads", exceedsCount.ToString()];

        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(
            parseResult.Errors,
            e => e.Message.Contains($"less than or equal to {Environment.ProcessorCount}")
        );
    }

    [Fact]
    public void ParseArgumentsThreadsWithValidValue8_ParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--threads", "8"];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Only test if machine has at least 8 processors
        if (Environment.ProcessorCount >= 8)
        {
            CommandLine.Context context = commandLine.CreateContext(parseResult);

            // Assert
            Assert.False(parseResult.Errors.Any());
            Assert.Equal(8, context.Threads);
            Assert.Equal(LogEventLevel.Information, context.LogLevel);
        }
        else
        {
            // Should fail on machines with fewer processors
            Assert.True(parseResult.Errors.Any());
        }
    }

    [Fact]
    public void ParseArgumentsMultiplePathsWithDifferentFormats_ParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args =
        [
            "--path",
            existingPath,
            "-p",
            existingPath,
            "--path",
            existingPath,
            "--dryrun",
        ];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(3, context.Paths.Count);
        Assert.True(context.DryRun);
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsCombinedShortAndLongOptions_ParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["-p", existingPath, "--dryrun", "-t", "4"];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Only test if machine has at least 4 processors
        if (Environment.ProcessorCount >= 4)
        {
            CommandLine.Context context = commandLine.CreateContext(parseResult);

            // Assert
            Assert.False(parseResult.Errors.Any());
            _ = Assert.Single(context.Paths);
            Assert.True(context.DryRun);
            Assert.Equal(4, context.Threads);
            Assert.Equal(LogEventLevel.Information, context.LogLevel);
        }
    }

    [Fact]
    public void PathOption_HasCorrectDescription()
    {
        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
        Option<List<DirectoryInfo>>? pathOption = rootCommand
            .Options.OfType<Option<List<DirectoryInfo>>>()
            .FirstOrDefault();

        // Assert
        Assert.NotNull(pathOption);
        Assert.Contains("directory", pathOption.Description!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DryRunOption_HasCorrectDescription()
    {
        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
        Option<bool>? dryRunOption = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault();

        // Assert
        Assert.NotNull(dryRunOption);
        Assert.Contains("dry", dryRunOption.Description!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThreadsOption_HasCorrectDescription()
    {
        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
        Option<int>? threadsOption = rootCommand.Options.OfType<Option<int>>().FirstOrDefault();

        // Assert
        Assert.NotNull(threadsOption);
        Assert.Contains("parallel", threadsOption.Description!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogLevelOption_HasCorrectProperties()
    {
        // Act
        (CommandLine _, RootCommand rootCommand) = CreateTestCommand();
        Option<LogEventLevel>? logLevelOption = rootCommand
            .Options.OfType<Option<LogEventLevel>>()
            .FirstOrDefault();

        // Assert
        Assert.NotNull(logLevelOption);
        Assert.False(logLevelOption.Required);
        Assert.Equal("Set the log level (default: Information).", logLevelOption.Description);
        Assert.Contains("-l", logLevelOption.Aliases);
    }

    [Fact]
    public void ParseArgumentsLogLevelWithValueParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--loglevel", "Debug"];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(LogEventLevel.Debug, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsLogLevelWithShortOptionParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["-p", existingPath, "-l", "Warning"];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(LogEventLevel.Warning, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsLogLevelDefaultValueIsCorrect()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(LogEventLevel.Information, context.LogLevel);
    }

    [Fact]
    public void ParseArgumentsLogFileOptionParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string logFilePath = "/tmp/photocleaner.log";
        string[] args = ["--path", existingPath, "--logfile", logFilePath];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(logFilePath, context.LogFile);
    }

    [Fact]
    public void ParseArgumentsLogFileOptionWithShortOptionParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string logFilePath = "/var/log/app.log";
        string[] args = ["-p", existingPath, "-f", logFilePath];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(logFilePath, context.LogFile);
    }

    [Fact]
    public void ParseArgumentsLogFileOptionNotSpecifiedIsNull()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Null(context.LogFile);
    }

    [Fact]
    public void ParseArgumentsAllOptionsIncludingLogFileParsesCorrectly()
    {
        // Arrange
        string existingPath = Directory.GetCurrentDirectory();
        string logFilePath = "./test.log";
        string[] args =
        [
            "--path",
            existingPath,
            "--dryrun",
            "--threads",
            "8",
            "--loglevel",
            "Debug",
            "--logfile",
            logFilePath,
        ];

        // Act
        (CommandLine commandLine, RootCommand rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        CommandLine.Context context = commandLine.CreateContext(parseResult);

        // Assert
        Assert.False(parseResult.Errors.Any());
        _ = Assert.Single(context.Paths);
        Assert.Equal(existingPath, context.Paths[0].FullName);
        Assert.True(context.DryRun);
        Assert.Equal(8, context.Threads);
        Assert.Equal(LogEventLevel.Debug, context.LogLevel);
        Assert.Equal(logFilePath, context.LogFile);
    }

    /// <summary>
    /// Helper method to create test command structure using the actual CommandLine.CreateRootCommandWithCommandLine method
    /// </summary>
    private static (CommandLine commandLine, RootCommand rootCommand) CreateTestCommand() =>
        CommandLine.CreateRootCommandWithCommandLine();
}
