using System.CommandLine;
using PhotoCleaner;
using Xunit;

namespace PhotoCleanerTests;

public class CommandLineTests
{
    [Fact]
    public void ParseArguments_ValidPathAndDryRun_ParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath, "--dryrun"];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)?.FullName);
        Assert.True(parseResult.GetValue(dryRunOption));
    }

    [Fact]
    public void ParseArguments_ValidPathWithShortOptions_ParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["-p", existingPath, "-d"];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)?.FullName);
        Assert.True(parseResult.GetValue(dryRunOption));
    }

    [Fact]
    public void ParseArguments_ValidPathOnly_ParsesCorrectly()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)?.FullName);
        Assert.False(parseResult.GetValue(dryRunOption));
    }

    [Fact]
    public void ParseArguments_MissingPath_ReturnsError()
    {
        // Arrange
        string[] args = ["--dryrun"];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("--path"));
    }

    [Fact]
    public void ParseArguments_EmptyArgs_ReturnsError()
    {
        // Arrange
        string[] args = [];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("--path"));
    }

    [Fact]
    public void ParseArguments_NonExistentDirectory_ReturnsValidationError()
    {
        // Arrange
        string nonExistentPath = "/this/path/should/not/exist/123456";
        string[] args = ["--path", nonExistentPath];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("does not exist"));
    }

    [Fact]
    public void ParseArguments_ExistingDirectory_NoValidationErrors()
    {
        // Arrange - Use current directory which should exist
        string existingPath = Directory.GetCurrentDirectory();
        string[] args = ["--path", existingPath];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.False(parseResult.Errors.Any());
        Assert.Equal(existingPath, parseResult.GetValue(pathOption)?.FullName);
    }

    [Fact]
    public void ParseArguments_InvalidOption_ReturnsError()
    {
        // Arrange
        string[] args = ["--path", "/test/path", "--invalid-option"];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
        Assert.Contains(parseResult.Errors, e => e.Message.Contains("invalid-option"));
    }

    [Fact]
    public void ParseArguments_PathWithoutValue_ReturnsError()
    {
        // Arrange
        string[] args = ["--path"];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert
        Assert.True(parseResult.Errors.Any());
    }

    [Fact]
    public void ParseArguments_HelpOption_ParsesCorrectly()
    {
        // Arrange
        string[] args = ["--help"];

        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
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
    public void ParseArguments_VariousValidInputs_ParsesWithoutErrors(params string[] args)
    {
        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();
        ParseResult parseResult = rootCommand.Parse(args);

        // Assert - Focus on parsing, not directory validation for this test
        // We expect parsing to succeed even if directory doesn't exist (validation errors are different)
        Assert.True(
            !parseResult.Errors.Any()
                || parseResult.Errors.All(e => e.Message.Contains("does not exist"))
        );
    }

    [Fact]
    public void RootCommand_HasCorrectDescription()
    {
        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();

        // Assert
        Assert.Equal(
            "PhotoCleaner - Pre-process media files for photo management systems.",
            rootCommand.Description
        );
    }

    [Fact]
    public void PathOption_HasCorrectProperties()
    {
        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();

        // Assert
        Assert.True(pathOption.Required);
        Assert.Equal("The directory path to process.", pathOption.Description);
        Assert.Contains("-p", pathOption.Aliases);
    }

    [Fact]
    public void DryRunOption_HasCorrectProperties()
    {
        // Act
        var (pathOption, dryRunOption, rootCommand) = CreateTestCommand();

        // Assert
        Assert.False(dryRunOption.Required);
        Assert.Equal("Perform a dry run without making changes.", dryRunOption.Description);
        Assert.Contains("-d", dryRunOption.Aliases);
    }

    /// <summary>
    /// Helper method to create test command structure using the actual CommandLine.CreateRootCommand method
    /// </summary>
    private static (
        Option<DirectoryInfo> pathOption,
        Option<bool> dryRunOption,
        RootCommand rootCommand
    ) CreateTestCommand()
    {
        RootCommand rootCommand = CommandLine.CreateRootCommand();

        // Extract the options from the root command by type since there are only two options
        Option<DirectoryInfo> pathOption =
            (Option<DirectoryInfo>)rootCommand.Options.First(o => o is Option<DirectoryInfo>);
        Option<bool> dryRunOption = (Option<bool>)rootCommand.Options.First(o => o is Option<bool>);

        return (pathOption, dryRunOption, rootCommand);
    }
}
