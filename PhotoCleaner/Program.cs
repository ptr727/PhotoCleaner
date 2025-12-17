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

    internal int Execute(string directoryPath, bool dryRun)
    {
        int failedCount = 0;
        try
        {
            // Get all files in root directory
            Log.Information("Enumerating files in '{DirectoryPath}' ...", directoryPath);
            DirectoryInfo rootDir = new(directoryPath);
            foreach (FileInfo file in rootDir.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                _fileNameBag.Add(file.FullName);
            }

            // Get all top level directories
            DirectoryInfo[] topLevelDirs = rootDir.GetDirectories();
            topLevelDirs
                .AsParallel()
                .WithDegreeOfParallelism(_degreeOfParallelism)
                .ForAll(dir =>
                {
                    // Get all files in each directory
                    foreach (FileInfo file in dir.GetFiles("*", SearchOption.AllDirectories))
                    {
                        _fileNameBag.Add(file.FullName);
                    }
                });

            // Process files in parallel
            Log.Information("Processing {FileCount} files ...", _fileNameBag.Count);
            _fileNameBag
                .AsParallel()
                .WithDegreeOfParallelism(_degreeOfParallelism)
                .ForAll(fileName =>
                {
                    if (
                        !ProcessTask.Execute(
                            _fileNameBag,
                            _unknownExtensionsList,
                            _unknownExtensionsLock,
                            new FileInfo(fileName),
                            dryRun
                        )
                    )
                    {
                        _ = Interlocked.Increment(ref failedCount);
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
            Log.Warning("Potential problem files: {FailedCount}", failedCount);
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
}
