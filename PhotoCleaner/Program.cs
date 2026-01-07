using System.Collections.Concurrent;
using System.Globalization;
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
        CreateLogger();

        return await CommandLine.Invoke(args);
    }

    public int Execute()
    {
        try
        {
            // Get list of files to process
            GetFileList(commandLineContext.Paths);

            // Log a warning for files with similar names but different cases
            FindCaseConflicts();

            // Process files
            while (!_fileNames.IsEmpty)
            {
                ExecuteProcess();
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

    public void ExecuteProcess()
    {
        // Process files in parallel
        ConcurrentBag<string> reProcessNames = [];
        Log.Information("Processing {FileCount} files ...", _fileNames.Count);
        _fileNames
            .AsParallel()
            .WithDegreeOfParallelism(commandLineContext.Threads)
            .ForAll(fileName =>
            {
                switch (
                    ProcessTask.Execute(
                        new ProcessTask.Context
                        {
                            FileInfo = new FileInfo(fileName),
                            DryRun = commandLineContext.DryRun,
                            UnknownExtensions = _unknownExtensions,
                            ReProcessNames = reProcessNames,
                        }
                    )
                )
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
            });

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

    private static void CreateLogger()
    {
        // Enable Serilog debug output to the console
        SelfLog.Enable(Console.Error);
        LoggerConfiguration loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(LogEventLevel.Information)
            .Enrich.WithThreadId()
            .WriteTo.Console(
                theme: AnsiConsoleTheme.Code,
                // Remove lj from default to quote strings
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] <{ThreadId}> {Message}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture
            );
        Log.Logger = loggerConfiguration.CreateLogger();
    }

    private void GetFileList(List<DirectoryInfo> directoryList)
    {
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

    private void FindCaseConflicts()
    {
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

        foreach (List<string> files in fileNameMap.Values.Where(values => values.Count > 1))
        {
            foreach (string file in files)
            {
                FileInfo fileInfo = new(file);
                Log.Warning(
                    "File name case conflict: '{FileName}' ({Size})",
                    file,
                    fileInfo.Length
                );
            }
        }
    }
}
