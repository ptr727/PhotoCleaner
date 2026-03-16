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
    private readonly Option<bool> _dateFromPathOption = CreateDateFromPathOption();
    private readonly Option<bool> _skipBackupOption = CreateSkipBackupOption();
    private readonly Option<DirectoryInfo> _outPathOption = CreateOutPathOption();
    private readonly Option<string> _formatOption = CreateFormatOption();
    private readonly Option<bool> _deleteEmptyOption = CreateDeleteEmptyOption();
    private readonly Option<bool> _moveOption = CreateMoveOption();
    private readonly Option<FileInfo?> _dbPathOption = CreateDbPathOption();

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
        RootCommand command = new(
            "PhotoCleaner - Pre-process media files for photo management systems."
        )
        {
            _logLevelOption,
            _logFileOption,
            _logFileClearOption,
        };
        command.Subcommands.Add(CreateProcessCommand());
        command.Subcommands.Add(CreateUndoCommand());
        command.Subcommands.Add(CreateCleanupCommand());
        command.Subcommands.Add(CreateOrganizeCommand());

        return command;
    }

    private Command CreateProcessCommand()
    {
        Command command = new("process", "Process media files")
        {
            _pathOption,
            _dryRunOption,
            _threadsOption,
            _dateFromPathOption,
            _skipBackupOption,
            _dbPathOption,
        };
        command.SetAction(
            (parseResult, cancellationToken) =>
            {
                Program program = new(CreateOptions(parseResult), cancellationToken);
                return program.ProcessCommandAsync();
            }
        );

        return command;
    }

    private Command CreateCleanupCommand()
    {
        Command command = new("cleanup", "Delete files not in the supported media list")
        {
            _pathOption,
            _dryRunOption,
        };
        command.SetAction(
            (parseResult, cancellationToken) =>
            {
                Program program = new(CreateOptions(parseResult), cancellationToken);
                return program.CleanupCommandAsync();
            }
        );

        return command;
    }

    private Command CreateOrganizeCommand()
    {
        Command command = new("organize", "Organize media files into date-based subdirectories")
        {
            _pathOption,
            _dryRunOption,
            _threadsOption,
            _outPathOption,
            _formatOption,
            _deleteEmptyOption,
            _moveOption,
            _dbPathOption,
        };
        command.SetAction(
            (parseResult, cancellationToken) =>
            {
                Program program = new(CreateOptions(parseResult), cancellationToken);
                return program.OrganizeCommandAsync();
            }
        );
        return command;
    }

    private Command CreateUndoCommand()
    {
        Command command = new("undo", "Undo media file processing") { _pathOption, _dryRunOption };
        command.SetAction(
            (parseResult, cancellationToken) =>
            {
                Program program = new(CreateOptions(parseResult), cancellationToken);
                return program.UndoCommandAsync();
            }
        );

        return command;
    }

    internal Options CreateOptions(ParseResult parseResult) =>
        new()
        {
            Paths = parseResult.GetValue(_pathOption) ?? [],
            Threads = parseResult.GetValue(_threadsOption) is int t and > 0
                ? t
                : Math.Min(Environment.ProcessorCount, 4),
            DryRun = parseResult.GetValue(_dryRunOption),
            DateFromPath = parseResult.GetValue(_dateFromPathOption),
            SkipBackup = parseResult.GetValue(_skipBackupOption),
            OutPath = parseResult.GetValue(_outPathOption),
            Format = parseResult.GetValue(_formatOption) ?? "yyyy-MM",
            DeleteEmpty = parseResult.GetValue(_deleteEmptyOption),
            Move = parseResult.GetValue(_moveOption),
            DbPath = parseResult.GetValue(_dbPathOption),
            LogOptions = new LoggerFactory.Options
            {
                Level = parseResult.GetValue(_logLevelOption),
                File = parseResult.GetValue(_logFileOption) ?? string.Empty,
                FileClear = parseResult.GetValue(_logFileClearOption),
            },
        };

    private static Option<List<DirectoryInfo>> CreatePathOption() =>
        new Option<List<DirectoryInfo>>("--path")
        {
            Description = "The directory path to process",
            Required = true,
        }.AcceptExistingOnly();

    private static Option<bool> CreateDryRunOption() =>
        new("--dryrun") { Description = "Perform a dry run without making changes" };

    private static Option<bool> CreateDateFromPathOption() =>
        new("--datefrompath") { Description = "Set missing EXIF creation date from file path" };

    private static Option<bool> CreateSkipBackupOption() =>
        new("--skipbackup") { Description = "Skip creating backup files (disables undo)" };

    private static Option<int> CreateThreadsOption()
    {
        Option<int> option = new("--threads")
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

    private static Option<bool> CreateDeleteEmptyOption() =>
        new("--deleteempty")
        {
            Description = "Delete empty source subdirectories after organizing",
        };

    private static Option<bool> CreateMoveOption() =>
        new("--move") { Description = "Move files instead of copying (default: copy)" };

    private static Option<FileInfo?> CreateDbPathOption() =>
        new("--db") { Description = "SQLite database file for deduplication tracking" };

    private static Option<DirectoryInfo> CreateOutPathOption() =>
        new("--outpath") { Description = "Output directory for organized files", Required = true };

    private static Option<string> CreateFormatOption()
    {
        Option<string> option = new("--format")
        {
            Description =
                "Date format for output subdirectory names; use '/' to create nested subdirectories (e.g. yyyy/MM/dd)",
            DefaultValueFactory = _ => "yyyy-MM",
        };
        option.Validators.Add(result =>
        {
            string? format = result.GetValue(option);
            if (string.IsNullOrEmpty(format))
            {
                result.AddError("Format cannot be empty");
                return;
            }
            try
            {
                DateTime morning = new(2024, 6, 15, 8, 0, 0);
                DateTime evening = new(2024, 6, 15, 20, 30, 45);
                if (
                    morning.ToString(format, CultureInfo.InvariantCulture)
                    != evening.ToString(format, CultureInfo.InvariantCulture)
                )
                {
                    result.AddError(
                        $"Format '{format}' contains time components; only date-based formats are allowed"
                    );
                }
            }
            catch (FormatException)
            {
                result.AddError($"'{format}' is not a valid date format specifier");
            }
        });
        return option;
    }

    private static Option<bool> CreateLogFileClearOption() =>
        new("--logclear") { Description = "Clear the log file before writing", Recursive = true };

    private static Option<LogEventLevel> CreateLogLevelOption() =>
        new("--loglevel")
        {
            Description = "Set the log level",
            DefaultValueFactory = _ => LogEventLevel.Information,
            Recursive = true,
        };

    private static Option<string> CreateLogFileOption()
    {
        Option<string> option = new("--logfile")
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
        public required bool DateFromPath { get; init; }
        public required bool SkipBackup { get; init; }
        public required DirectoryInfo? OutPath { get; init; }
        public required string Format { get; init; }
        public required bool DeleteEmpty { get; init; }
        public required bool Move { get; init; }
        public required FileInfo? DbPath { get; init; }
        internal required LoggerFactory.Options LogOptions { get; init; }
    }
}
