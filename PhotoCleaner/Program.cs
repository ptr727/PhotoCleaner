using System.Collections.Concurrent;
using System.CommandLine;
using System.Globalization;
using System.Runtime.CompilerServices;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace PhotoCleaner;

internal class Program(CommandLine.Context commandLineContext)
{
    private ConcurrentBag<string> _fileNames = [];
    private readonly ConcurrentDictionary<string, byte> _unknownExtensions = new(
        StringComparer.OrdinalIgnoreCase
    );
    private int _failedCount;
    private int _modifiedCount;

    public static async Task<int> Main(string[] args)
    {
        // Parse commandline
        (CommandLine commandLine, RootCommand rootCommand) =
            CommandLine.CreateRootCommandWithCommandLine();
        ParseResult parseResult = rootCommand.Parse(args);

        // Bypass startup for help and version commands
        if (CommandLine.BypassStartup(parseResult))
        {
            return await parseResult.InvokeAsync();
        }

        // Create logger
        CreateLogger(commandLine.CreateContext(parseResult));
        Log.Logger.LogOverrideContext().Information("Starting PhotoCleaner: {Args}", args);

        // Invoke command
        return await parseResult.InvokeAsync();
    }

    public async Task<int> ExecuteAsync()
    {
        try
        {
            bool foundConflicts = true;
            while (foundConflicts)
            {
                // Get list of files to process
                GetFileList(commandLineContext.Paths);

                // Rename mixed case files
                foundConflicts = FindAndFixCaseConflicts();
            }

            // Process files as long as there are files to process
            while (!_fileNames.IsEmpty)
            {
                await ExecuteProcess();
            }
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Processing complete.");

        if (!_unknownExtensions.IsEmpty)
        {
            List<string> unknownExtensionsList = [.. _unknownExtensions.Keys];
            unknownExtensionsList.Sort();
            foreach (string extension in unknownExtensionsList)
            {
                Log.Warning("Unknown file extension: '{Extension}'", extension);
            }
        }

        if (_failedCount > 0)
        {
            Log.Warning("Failed files: {FailedCount}", _failedCount);
        }

        if (_modifiedCount > 0)
        {
            Log.Information("Modified files: {ModifiedCount}", _modifiedCount);
        }

        return 0;
    }

    public async Task ExecuteProcess()
    {
        // Process files in parallel
        ConcurrentBag<string> reProcessNames = [];
        Log.Information("Processing {FileCount} files ...", _fileNames.Count);
        await Parallel.ForEachAsync(
            _fileNames,
            new ParallelOptions()
            {
                MaxDegreeOfParallelism = commandLineContext.Threads,
                CancellationToken = commandLineContext.CancellationToken,
            },
            async (fileName, cancellationToken) =>
            {
                Log.Debug("Processing file: '{FileName}'", fileName);
                ProcessTask processTask = new(
                    new ProcessTask.Context
                    {
                        FileInfo = new FileInfo(fileName),
                        DryRun = commandLineContext.DryRun,
                        UnknownExtensions = _unknownExtensions,
                        ReProcessNames = reProcessNames,
                    }
                );
                switch (await processTask.ExecuteAsync())
                {
                    case ProcessTask.ProcessResult.Failure:
                    case ProcessTask.ProcessResult.DoubleExtensions:
                        _ = Interlocked.Increment(ref _failedCount);
                        break;
                    case ProcessTask.ProcessResult.Modified:
                    case ProcessTask.ProcessResult.Reprocess:
                        _ = Interlocked.Increment(ref _modifiedCount);
                        break;
                    case ProcessTask.ProcessResult.UnknownExtension:
                    case ProcessTask.ProcessResult.Success:
                    default:
                        break;
                }
            }
        );

        // Reprocess files
        if (reProcessNames.IsEmpty)
        {
            _fileNames.Clear();
        }
        else
        {
            Log.Information(
                "Adding {ReprocessCount} files for reprocessing.",
                reProcessNames.Count
            );
            _fileNames = reProcessNames;
        }
    }

    private static void CreateLogger(CommandLine.Context context)
    {
        // Enable Serilog debug output to the console
        SelfLog.Enable(Console.Error);
        LoggerConfiguration loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(context.LogLevel)
            .MinimumLevel.Override(typeof(Extensions.LogOverride).FullName!, LogEventLevel.Verbose)
            .Enrich.WithThreadId()
            .WriteTo.Console(
                theme: AnsiConsoleTheme.Code,
                // Remove lj from default to quote strings
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] <{ThreadId}> {Message}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture
            );

        // Add file sink if logFile is specified
        if (!string.IsNullOrEmpty(context.LogFile))
        {
            _ = loggerConfiguration.WriteTo.File(
                context.LogFile,
                // Remove lj from default to quote strings
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] <{ThreadId}> {Message}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture
            );
        }

        Log.Logger = loggerConfiguration.CreateLogger();
    }

    private void GetFileList(List<DirectoryInfo> directoryList)
    {
        _fileNames = [];
        foreach (DirectoryInfo directoryInfo in directoryList)
        {
            Log.Information("Enumerating files in '{DirectoryPath}' ...", directoryInfo.FullName);

            int count = 0;
            directoryInfo
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .AsParallel()
                .WithDegreeOfParallelism(commandLineContext.Threads)
                .ForAll(file =>
                {
                    _fileNames.Add(file.FullName);
                    _ = Interlocked.Increment(ref count);
                });

            Log.Information(
                "Found {FileCount} files in '{DirectoryPath}'.",
                count,
                directoryInfo.FullName
            );
        }
    }

    private bool FindAndFixCaseConflicts()
    {
        // Create case insensitive map of file names to list of case sensitive file names
        Dictionary<string, List<string>> fileNameMap = new(
            _fileNames.Count,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (string fileName in _fileNames)
        {
            if (!fileNameMap.TryGetValue(fileName, out List<string>? value))
            {
                fileNameMap[fileName] = [fileName];
            }
            else
            {
                value.Add(fileName);
            }
        }

        // Find all files with multiple cased versions of the same file name
        bool foundConflicts = false;
        foreach (List<string> files in fileNameMap.Values.Where(values => values.Count > 1))
        {
            // Rename the files to make them unique
            foundConflicts = true;
            RenamedMixedCaseFiles(files);
        }

        // The renamed files could create new variants of mixed case names
        return foundConflicts;
    }

    private void RenamedMixedCaseFiles(List<string> files)
    {
        Log.Warning("Found {FileCount} case variants of file: '{FileName}'", files.Count, files[0]);
        int counter = 1;
        foreach (string file in files)
        {
            string uniqueFileName = GetUniqueFileName(file, ref counter);
            Log.Warning("Renaming '{OldFileName}' to '{NewFileName}'", file, uniqueFileName);
            if (!IsDryRun())
            {
                File.Move(file, uniqueFileName, false);
            }
        }
    }

    internal static string GetUniqueFileName(string fileName, ref int counter)
    {
        string directory = Path.GetDirectoryName(fileName) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        // Generate a unique file name by appending a counter
        string uniqueFileName = CreateFileName(directory, name, counter, extension);
        counter++;
        while (File.Exists(uniqueFileName))
        {
            counter++;
            uniqueFileName = CreateFileName(directory, name, counter, extension);
        }
        return uniqueFileName;
    }

    private static string CreateFileName(
        string directory,
        string name,
        int counter,
        string extension
    ) => Path.Combine(directory, $"{name}_{counter}{extension}");

    private bool IsDryRun([CallerMemberName] string function = "unknown")
    {
        if (commandLineContext.DryRun)
        {
            Log.Verbose("Dry run enabled, skipping action in {Function}.", function);
        }
        return commandLineContext.DryRun;
    }
}
