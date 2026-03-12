using System.CommandLine;
using System.CommandLine.Parsing;

namespace PhotoCleaner;

internal sealed class CommandLine
{
    private readonly Option<LogEventLevel> _logLevelOption = CreateLogLevelOption();
    private readonly Option<string> _logFileOption = CreateLogFileOption();
    private readonly Option<bool> _logFileClearOption = CreateLogFileClearOption();
    private readonly Option<List<DirectoryInfo>> _pathOption = CreatePathOption();
    private readonly Option<bool> _dryRunOption = CreateDryRunOption();
    private readonly Option<int> _threadsOption = CreateThreadsOption();

    private static readonly FrozenSet<string> s_cliBypassList = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "--help",
        "--version"
    );

    internal CommandLine(string[] args)
    {
        Root = CreateRootCommand();
        Result = Root.Parse(args);
    }

    internal RootCommand Root { get; }
    internal ParseResult Result { get; }

    internal RootCommand CreateRootCommand()
    {
        // Default root command
        RootCommand rootCommand = new(
            "PhotoCleaner - Pre-process media files for photo management systems."
        )
        {
            _pathOption,
            _dryRunOption,
            _threadsOption,
            _logLevelOption,
            _logFileOption,
            _logFileClearOption,
        };
        rootCommand.SetAction(
            (parseResult, cancellationToken) =>
            {
                Program program = new(CreateOptions(parseResult), cancellationToken);
                return program.ExecuteAsync();
            }
        );

        return rootCommand;
    }

    internal Options CreateOptions(ParseResult parseResult) =>
        new()
        {
            Paths = parseResult.GetValue(_pathOption) ?? [],
            Threads = parseResult.GetValue(_threadsOption),
            DryRun = parseResult.GetValue(_dryRunOption),
            LogOptions = new LoggerFactory.Options
            {
                Level = parseResult.GetValue(_logLevelOption),
                File = parseResult.GetValue(_logFileOption) ?? string.Empty,
                FileClear = parseResult.GetValue(_logFileClearOption),
            },
        };

    private static Option<List<DirectoryInfo>> CreatePathOption() =>
        new Option<List<DirectoryInfo>>("--path", "-p")
        {
            Description = "The directory path to process",
            Required = true,
        }.AcceptExistingOnly();

    private static Option<bool> CreateDryRunOption() =>
        new("--dryrun", "-d") { Description = "Perform a dry run without making changes" };

    private static Option<int> CreateThreadsOption()
    {
        Option<int> option = new("--threads", "-t")
        {
            Description = "Number of parallel threads",
            DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 4),
        };

        option.Validators.Add(result =>
        {
            int value = result.GetValue(option);
            if (value <= 0)
            {
                result.AddError("Thread count must be greater than 0");
            }
            else if (value > Environment.ProcessorCount)
            {
                result.AddError(
                    $"Thread count must be less than or equal to {Environment.ProcessorCount}"
                );
            }
        });

        return option;
    }

    private static Option<bool> CreateLogFileClearOption() =>
        new("--logclear", "-c")
        {
            Description = "Clear the log file before writing",
            Recursive = true,
        };

    private static Option<LogEventLevel> CreateLogLevelOption() =>
        new("--loglevel", "-l")
        {
            Description = "Set the log level",
            DefaultValueFactory = _ => LogEventLevel.Information,
            Recursive = true,
        };

    private static Option<string> CreateLogFileOption()
    {
        Option<string> option = new("--logfile", "-f")
        {
            Description = "Write logs to the specified file",
            Recursive = true,
        };
        return option.AcceptLegalFileNamesOnly();
    }

    internal static bool BypassStartup(ParseResult parseResult) =>
        parseResult.Errors.Count > 0
        || parseResult.CommandResult.Children.Any(symbolResult =>
            symbolResult is OptionResult optionResult
            && s_cliBypassList.Contains(optionResult.Option.Name)
        );

    internal sealed class Options
    {
        public required List<DirectoryInfo> Paths { get; init; }
        public required int Threads { get; init; }
        public required bool DryRun { get; init; }
        internal required LoggerFactory.Options LogOptions { get; init; }
    }
}
