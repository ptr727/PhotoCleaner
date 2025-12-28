using System.Collections.Concurrent;
using System.Globalization;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace PhotoCleaner;

internal class Program
{
    private readonly ConcurrentBag<string> _fileNameBag = [];
    private readonly List<string> _unknownExtensionsList = [];
    private readonly Lock _unknownExtensionsLock = new();

    private readonly int _degreeOfParallelism = int.Max(Environment.ProcessorCount, 4);

    public static async Task<int> Main(string[] args)
    {
        CreateLogger();

        return await CommandLine.Invoke(args);
    }

    public int Execute(List<DirectoryInfo> directoryPath, bool dryRun)
    {
        int failedCount = 0;
        int modifiedCount = 0;
        try
        {
            // Get list of files to process
            GetFileList(directoryPath);

            // Log a warning for files with similar names but different cases
            FindCaseConflicts();

            // Process files in parallel
            Log.Information("Processing {FileCount} files ...", _fileNameBag.Count);
            _fileNameBag
                .AsParallel()
                .WithDegreeOfParallelism(_degreeOfParallelism)
                .ForAll(fileName =>
                {
                    switch (
                        ProcessTask.Execute(
                            _fileNameBag,
                            _unknownExtensionsList,
                            _unknownExtensionsLock,
                            new FileInfo(fileName),
                            dryRun
                        )
                    )
                    {
                        case ProcessTask.ProcessResult.Failure:
                        case ProcessTask.ProcessResult.DoubleExtensions:
                            _ = Interlocked.Increment(ref failedCount);
                            break;
                        case ProcessTask.ProcessResult.Modified:
                        case ProcessTask.ProcessResult.Reprocess:
                            _ = Interlocked.Increment(ref modifiedCount);
                            break;
                        case ProcessTask.ProcessResult.UnknownExtension:
                        case ProcessTask.ProcessResult.Success:
                        default:
                            break;
                    }
                });
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Processing complete.");

        if (_unknownExtensionsList.Count > 0)
        {
            _unknownExtensionsList.Sort();
            foreach (string extension in _unknownExtensionsList)
            {
                Log.Warning("Unknown file extension: '{Extension}'", extension);
            }
        }

        if (failedCount > 0)
        {
            Log.Warning("Failed files: {FailedCount}", failedCount);
        }

        if (modifiedCount > 0)
        {
            Log.Information("Modified files: {ModifiedCount}", modifiedCount);
        }

        return 0;
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
            // Get all files in root directory
            Log.Information("Enumerating files in '{DirectoryPath}' ...", directoryInfo.FullName);
            foreach (FileInfo file in directoryInfo.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                _fileNameBag.Add(file.FullName);
            }

            // Get all top level directories
            int count = 0;
            DirectoryInfo[] topLevelDirs = directoryInfo.GetDirectories();
            topLevelDirs
                .AsParallel()
                .WithDegreeOfParallelism(_degreeOfParallelism)
                .ForAll(dir =>
                {
                    // Get all files in each directory
                    foreach (FileInfo file in dir.GetFiles("*", SearchOption.AllDirectories))
                    {
                        _fileNameBag.Add(file.FullName);
                        count++;
                    }
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
        // Case insensitive dictionary of file names
        Dictionary<string, List<string>> fileNameMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (string fileName in _fileNameBag)
        {
            // Look for existing path ignoring case
            if (!fileNameMap.TryGetValue(fileName, out List<string>? value))
            {
                // Not found, create new entry
                value = [];
                fileNameMap[fileName] = value;
            }

            // Add the file name to the list
            value.Add(fileName);
        }

        // Find all conflicts
        foreach (KeyValuePair<string, List<string>> entry in fileNameMap)
        {
            if (entry.Value.Count > 1)
            {
                foreach (string file in entry.Value)
                {
                    FileInfo fileInfo = new(file);
                    Log.Warning(
                        "File name case conflict: '{FileName}' {Size}",
                        file,
                        fileInfo.Length
                    );
                }
            }
        }
    }
}
