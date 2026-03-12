using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace PhotoCleaner;

internal sealed class Program(
    CommandLine.Options commandLineOptions,
    CancellationToken cancellationToken
)
{
    private ConcurrentBag<string> _fileNames = [];
    private readonly ConcurrentDictionary<string, byte> _unknownExtensions = new(
        StringComparer.OrdinalIgnoreCase
    );
    private int _failedCount;
    private int _modifiedCount;

    internal CommandLine.Options GetCommandLineOptions() => commandLineOptions;

    internal CancellationToken GetCancellationToken() => cancellationToken;

    internal static async Task<int> Main(string[] args)
    {
        try
        {
            // Parse commandline
            CommandLine commandLine = new(args);
            commandLine.Result.InvocationConfiguration.EnableDefaultExceptionHandler = false;
            commandLine.Result.InvocationConfiguration.ProcessTerminationTimeout = null;

            // Bypass startup for errors or help and version commands
            if (CommandLine.BypassStartup(commandLine.Result))
            {
                return await commandLine.Result.InvokeAsync().ConfigureAwait(false);
            }

            // Create logger
            Log.Logger = LoggerFactory.Create(
                commandLine.CreateOptions(commandLine.Result).LogOptions
            );

            // Invoke command
            Log.Logger.LogOverrideContext().Information("Starting: {Args}", args);
            return await commandLine.Result.InvokeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    internal async Task<int> ExecuteAsync()
    {
        Log.Information("Processing started...");
        try
        {
            bool foundConflicts = true;
            while (foundConflicts)
            {
                // Get list of files to process
                GetFileList(commandLineOptions.Paths);

                // Rename mixed case files
                foundConflicts = FindCaseConflicts();
            }

            // Process files as long as there are files to process
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
            .WithDegreeOfParallelism(commandLineOptions.Threads)
            .ForAll(fileName =>
            {
                Log.Debug("Processing file: '{FileName}'", fileName);
                switch (
                    ProcessTask.Execute(
                        new ProcessTask.Context
                        {
                            FileInfo = new FileInfo(fileName),
                            DryRun = commandLineOptions.DryRun,
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
                .WithDegreeOfParallelism(commandLineOptions.Threads)
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

    private bool FindCaseConflicts()
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
        if (commandLineOptions.DryRun)
        {
            Log.Verbose("Dry run enabled, skipping action in {Function}.", function);
        }
        return commandLineOptions.DryRun;
    }
}
