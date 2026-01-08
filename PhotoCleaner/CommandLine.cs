using System.CommandLine;
using Serilog.Events;

namespace PhotoCleaner;

internal class CommandLine
{
    public class Context
    {
        public required List<DirectoryInfo> Paths { get; init; }
        public required int Threads { get; init; }
        public required bool DryRun { get; init; }
        public required LogEventLevel LogLevel { get; init; }
        public string? LogFile { get; init; }
    }

    internal static (
        CommandLine commandLine,
        RootCommand rootCommand
    ) CreateRootCommandWithCommandLine()
    {
        CommandLine commandLine = new();
        RootCommand rootCommand = new(
            "PhotoCleaner - Pre-process media files for photo management systems."
        )
        {
            commandLine._pathOption,
            commandLine._dryRunOption,
            commandLine._threadsOption,
            commandLine._logLevelOption,
            commandLine._logFileOption,
        };
        rootCommand.SetAction(parseResult =>
        {
            Program program = new(commandLine.CreateContext(parseResult));
            return program.Execute();
        });

        return (commandLine, rootCommand);
    }

    internal Context CreateContext(ParseResult parseResult) =>
        new()
        {
            Paths = parseResult.GetValue(_pathOption) ?? [],
            Threads = parseResult.GetValue(_threadsOption),
            DryRun = parseResult.GetValue(_dryRunOption),
            LogLevel = parseResult.GetValue(_logLevelOption),
            LogFile = parseResult.GetValue(_logFileOption),
        };

    private readonly Option<List<DirectoryInfo>> _pathOption = CreatePathOption();
    private readonly Option<bool> _dryRunOption = CreateDryRunOption();
    private readonly Option<int> _threadsOption = CreateThreadsOption();
    private readonly Option<LogEventLevel> _logLevelOption = CreateLogLevelOption();
    private readonly Option<string?> _logFileOption = CreateLogFileOption();

    private static Option<List<DirectoryInfo>> CreatePathOption() =>
        new Option<List<DirectoryInfo>>("--path", "-p")
        {
            Description = "The directory path to process.",
            Required = true,
        }.AcceptExistingOnly();

    private static Option<bool> CreateDryRunOption() =>
        new("--dryrun", "-d")
        {
            Description = "Perform a dry run without making changes (default: false).",
        };

    private static Option<int> CreateThreadsOption()
    {
        Option<int> option = new("--threads", "-t")
        {
            Description = "Number of parallel threads (default: Max(ProcessorCount, 4)).",
            DefaultValueFactory = _ => Math.Max(Environment.ProcessorCount, 4),
        };

        option.Validators.Add(result =>
        {
            int value = result.GetValue(option);
            if (value <= 0)
            {
                result.AddError("Thread count must be greater than 0.");
            }
            else if (value > Environment.ProcessorCount)
            {
                result.AddError(
                    $"Thread count must be less than or equal to {Environment.ProcessorCount}."
                );
            }
        });

        return option;
    }

    private static Option<LogEventLevel> CreateLogLevelOption() =>
        new("--loglevel", "-l")
        {
            Description = "Set the log level (default: Information).",
            DefaultValueFactory = _ => LogEventLevel.Information,
        };

    private static Option<string?> CreateLogFileOption() =>
        new("--logfile", "-f") { Description = "Write logs to the specified file (optional)." };
}
