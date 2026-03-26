using System.CommandLine;
using System.CommandLine.Parsing;

namespace PhotoCleaner;

internal sealed class CommandLine
{
    private readonly Option<LogEventLevel> _logLevelOption = CreateLogLevelOption();
    private readonly Option<FileInfo?> _logFileOption = CreateLogFileOption();
    private readonly Option<bool> _logFileClearOption = CreateLogFileClearOption();
    private readonly Option<DirectoryInfo> _pathOption = CreatePathOption();
    private readonly Option<bool> _dryRunOption = CreateDryRunOption();
    private readonly Option<int> _threadsOption = CreateThreadsOption();
    private readonly Option<bool> _datePathOption = CreateDatePathOption();
    private readonly Option<bool> _skipBackupOption = CreateSkipBackupOption();
    private readonly Option<DirectoryInfo> _outPathOption = CreateOutPathOption();
    private readonly Option<string> _formatOption = CreateFormatOption();
    private readonly Option<bool> _deleteEmptyOption = CreateDeleteEmptyOption();
    private readonly Option<bool> _moveOption = CreateMoveOption();
    private readonly Option<bool> _tagPathOption = CreateTagPathOption();
    private readonly Option<string?> _tagsOption = CreateTagsOption();
    private readonly Option<FileInfo?> _dbFileOption = CreateDbFileOption();
    private readonly Option<bool> _rehashOption = CreateRehashOption();
    private readonly Option<double> _durationOption = CreateDurationOption();
    private readonly Option<bool> _reprocessOption = CreateReprocessOption();

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
        command.Subcommands.Add(CreateDuplicatesCommand());
        command.Subcommands.Add(CreateIndexCommand());

        return command;
    }

    private Command CreateProcessCommand()
    {
        Command command = new("process", "Process media files")
        {
            _pathOption,
            _dryRunOption,
            _threadsOption,
            _skipBackupOption,
            _dbFileOption,
            _rehashOption,
            _durationOption,
            _reprocessOption,
        };
        command.SetAction(
            (parseResult, cancellationToken) =>
                new ProcessCommand(CreateOptions(parseResult), cancellationToken).ExecuteAsync()
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
                new CleanupCommand(CreateOptions(parseResult), cancellationToken).ExecuteAsync()
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
            _tagPathOption,
            _tagsOption,
            _datePathOption,
            _dbFileOption,
            _rehashOption,
        };
        command.SetAction(
            (parseResult, cancellationToken) =>
                new OrganizeCommand(CreateOptions(parseResult), cancellationToken).ExecuteAsync()
        );
        return command;
    }

    private Command CreateDuplicatesCommand()
    {
        Option<FileInfo?> requiredDbOption = CreateDbFileOption();
        requiredDbOption.Required = true;

        Command command = new(
            "duplicates",
            "Delete files in --outpath whose content (SHA-256) matches a file in --path"
        )
        {
            _pathOption,
            _dryRunOption,
            _threadsOption,
            _outPathOption,
            requiredDbOption,
            _rehashOption,
        };
        command.SetAction(
            (parseResult, cancellationToken) =>
                new DuplicatesCommand(
                    CreateOptions(parseResult, parseResult.GetValue(requiredDbOption)),
                    cancellationToken
                ).ExecuteAsync()
        );
        return command;
    }

    private Command CreateIndexCommand()
    {
        Option<FileInfo?> requiredDbOption = CreateDbFileOption();
        requiredDbOption.Required = true;

        Command command = new("index", "Index files into the database for deduplication tracking")
        {
            _pathOption,
            _threadsOption,
            requiredDbOption,
            _rehashOption,
        };
        command.SetAction(
            (parseResult, cancellationToken) =>
                new IndexCommand(
                    CreateOptions(parseResult, parseResult.GetValue(requiredDbOption)),
                    cancellationToken
                ).ExecuteAsync()
        );
        return command;
    }

    private Command CreateUndoCommand()
    {
        Command command = new("undo", "Undo media file processing") { _pathOption, _dryRunOption };
        command.SetAction(
            (parseResult, cancellationToken) =>
                new UndoCommand(CreateOptions(parseResult), cancellationToken).ExecuteAsync()
        );

        return command;
    }

    internal Options CreateOptions(ParseResult parseResult, FileInfo? dbPathOverride = null) =>
        new()
        {
            Path = parseResult.GetValue(_pathOption)!,
            Threads = parseResult.GetValue(_threadsOption) is int t and > 0
                ? t
                : Math.Min(Environment.ProcessorCount, 4),
            DryRun = parseResult.GetValue(_dryRunOption),
            DatePath = parseResult.GetValue(_datePathOption),
            SkipBackup = parseResult.GetValue(_skipBackupOption),
            OutPath = parseResult.GetValue(_outPathOption),
            Format = parseResult.GetValue(_formatOption) ?? "yyyy/MM",
            DeleteEmpty = parseResult.GetValue(_deleteEmptyOption),
            Move = parseResult.GetValue(_moveOption),
            TagPath = parseResult.GetValue(_tagPathOption),
            Tags = parseResult.GetValue(_tagsOption),
            DbFile = dbPathOverride ?? parseResult.GetValue(_dbFileOption),
            Rehash = parseResult.GetValue(_rehashOption),
            ShortVideoDuration = parseResult.GetValue(_durationOption),
            Reprocess = parseResult.GetValue(_reprocessOption),
            LogOptions = new LoggerFactory.Options
            {
                Level = parseResult.GetValue(_logLevelOption),
                File = parseResult.GetValue(_logFileOption),
                FileClear = parseResult.GetValue(_logFileClearOption),
            },
        };

    private static Option<DirectoryInfo> CreatePathOption() =>
        new Option<DirectoryInfo>("--path")
        {
            Description = "The directory path to process",
            Required = true,
        }.AcceptExistingOnly();

    private static Option<bool> CreateDryRunOption() =>
        new("--dryrun") { Description = "Perform a dry run without making changes" };

    private static Option<bool> CreateDatePathOption() =>
        new("--datepath") { Description = "Set missing EXIF creation date from file path" };

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

    private static Option<bool> CreateTagPathOption() =>
        new("--tagpath")
        {
            Description =
                "Apply path sub-directory components as XMP Subject tags to the organized file",
        };

    private static Option<string?> CreateTagsOption() =>
        new("--tags")
        {
            Description =
                "Comma-separated XMP Subject tags to apply to every organized file (e.g. \"vacation,family\")",
        };

    private static Option<FileInfo?> CreateDbFileOption() =>
        new("--db") { Description = "SQLite database file for deduplication tracking" };

    private static Option<bool> CreateRehashOption() =>
        new("--rehash") { Description = "Force rehashing of all files, ignoring size/mtime cache" };

    private static Option<bool> CreateReprocessOption() =>
        new("--reprocess")
        {
            Description = "Re-process files even if already marked as processed in the database",
        };

    private static Option<double> CreateDurationOption()
    {
        Option<double> option = new("--duration")
        {
            Description =
                "Maximum duration in seconds below which a video is considered a short clip and deleted",
            DefaultValueFactory = _ => MediaUtilities.ShortVideoDuration,
        };
        option.Validators.Add(result =>
        {
            double value = result.GetValue(option);
            if (value <= 0.0)
            {
                result.AddError("Duration must be greater than 0");
            }
        });
        return option;
    }

    private static Option<DirectoryInfo> CreateOutPathOption() =>
        new("--outpath") { Description = "Output directory for organized files", Required = true };

    private static Option<string> CreateFormatOption()
    {
        Option<string> option = new("--format")
        {
            Description =
                "Date format for output subdirectory names; use '/' to create nested subdirectories (e.g. yyyy/MM/dd)",
            DefaultValueFactory = _ => "yyyy/MM",
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

    private static Option<FileInfo?> CreateLogFileOption() =>
        new("--logfile") { Description = "Write logs to the specified file", Recursive = true };

    internal static bool BypassStartup(ParseResult parseResult) =>
        parseResult.Errors.Count > 0
        || parseResult.CommandResult.Children.Any(symbolResult =>
            symbolResult is OptionResult optionResult
            && s_cliBypassList.Contains(optionResult.Option.Name)
        );

    internal sealed class Options
    {
        public required DirectoryInfo Path { get; init; }
        public required int Threads { get; init; }
        public required bool DryRun { get; init; }
        public required bool DatePath { get; init; }
        public required bool SkipBackup { get; init; }
        public required DirectoryInfo? OutPath { get; init; }
        public required string Format { get; init; }
        public required bool DeleteEmpty { get; init; }
        public required bool Move { get; init; }
        public required bool TagPath { get; init; }
        public required string? Tags { get; init; }
        public required FileInfo? DbFile { get; init; }
        public required bool Rehash { get; init; }
        public required double ShortVideoDuration { get; init; }
        public required bool Reprocess { get; init; }
        internal required LoggerFactory.Options LogOptions { get; init; }
    }
}
