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
    private int _deletedCount;
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

    internal async Task<int> ProcessCommandAsync()
    {
        Log.Information("Processing started");
        try
        {
            // Rename duplicate mixed case files
            bool foundConflicts = true;
            while (foundConflicts)
            {
                GetFileList(commandLineOptions.Paths);
                foundConflicts = FixCaseConflicts();
            }

            // Process files
            while (!_fileNames.IsEmpty)
            {
                await ExecuteProcessAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Processing complete");

        if (!_unknownExtensions.IsEmpty)
        {
            List<string> unknownExtensionsList = [.. _unknownExtensions.Keys];
            unknownExtensionsList.Sort();
            foreach (string extension in unknownExtensionsList)
            {
                Log.Warning("Unknown file extension: '{Extension}'", extension);
            }
        }

        if (_modifiedCount > 0)
        {
            Log.Information("Modified files: {ModifiedCount}", _modifiedCount);
        }

        if (_deletedCount > 0)
        {
            Log.Information("Deleted files: {DeletedCount}", _deletedCount);
        }

        if (_failedCount > 0)
        {
            Log.Warning("Failed files: {FailedCount}", _failedCount);
        }

        return 0;
    }

    internal async Task<int> UndoCommandAsync()
    {
        Log.Information("Undo started");
        try
        {
            GetFileList(commandLineOptions.Paths);
            await ExecuteUndoAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Undo complete");

        if (_modifiedCount > 0)
        {
            Log.Information("Restored files: {RestoredCount}", _modifiedCount);
        }

        if (_deletedCount > 0)
        {
            Log.Information("Deleted files: {DeletedCount}", _deletedCount);
        }

        if (_failedCount > 0)
        {
            Log.Warning("Failed files: {FailedCount}", _failedCount);
        }

        return 0;
    }

    internal async Task<int> OrganizeCommandAsync()
    {
        Log.Information("Organize started");
        try
        {
            GetFileList(commandLineOptions.Paths);
            OrganizeTask task = new(
                commandLineOptions.DryRun,
                commandLineOptions.OutPath!,
                commandLineOptions.Format,
                commandLineOptions.Threads,
                commandLineOptions.DeleteEmpty
            );
            (int moved, int ignored, int failed, int deletedDirs) = await task.ExecuteOrganizeAsync(
                    [.. _fileNames],
                    commandLineOptions.Paths,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (moved > 0)
            {
                Log.Information("Moved {MovedCount} files", moved);
            }

            if (ignored > 0)
            {
                Log.Information("Ignored {IgnoredCount} non-media files", ignored);
            }

            if (deletedDirs > 0)
            {
                Log.Information("Deleted {DeletedCount} empty directories", deletedDirs);
            }

            if (failed > 0)
            {
                Log.Warning("Failed {FailedCount} files", failed);
            }
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Organize complete");
        return 0;
    }

    internal async Task<int> CleanupCommandAsync()
    {
        Log.Information("Cleanup started");
        try
        {
            GetFileList(commandLineOptions.Paths);
            (int deleted, int failed) = new CleanupTask(commandLineOptions.DryRun).ExecuteCleanup([
                .. _fileNames,
            ]);
            if (deleted > 0)
            {
                Log.Information("Deleted {DeletedCount} non-media files", deleted);
            }

            if (failed > 0)
            {
                Log.Warning("Failed to delete {FailedCount} files", failed);
            }
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Cleanup complete");
        return 0;
    }

    private async Task ExecuteProcessAsync()
    {
        // Process files in parallel
        ConcurrentBag<string> reProcessNames = [];
        Log.Information("Processing {FileCount} files", _fileNames.Count);
        await Parallel
            .ForEachAsync(
                _fileNames,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = commandLineOptions.Threads,
                    CancellationToken = cancellationToken,
                },
                async (fileName, ct) =>
                {
                    Log.Debug("Processing file: '{FileName}'", fileName);
                    switch (
                        await ProcessTask
                            .ExecuteAsync(
                                new ProcessTask.Context
                                {
                                    FileInfo = new FileInfo(fileName),
                                    DryRun = commandLineOptions.DryRun,
                                    DateFromPath = commandLineOptions.DateFromPath,
                                    SkipBackup = commandLineOptions.SkipBackup,
                                    UnknownExtensions = _unknownExtensions,
                                    ReProcessNames = reProcessNames,
                                }
                            )
                            .ConfigureAwait(false)
                    )
                    {
                        case ProcessTask.ProcessResult.Failure:
                            _ = Interlocked.Increment(ref _failedCount);
                            break;
                        case ProcessTask.ProcessResult.Deleted:
                            _ = Interlocked.Increment(ref _deletedCount);
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
            )
            .ConfigureAwait(false);

        // Reprocess files
        if (reProcessNames.IsEmpty)
        {
            _fileNames.Clear();
        }
        else
        {
            Log.Information("Adding {ReprocessCount} files for reprocessing", reProcessNames.Count);
            _fileNames = reProcessNames;
        }
    }

    private Task ExecuteUndoAsync()
    {
        UndoTask undoTask = new(commandLineOptions.DryRun);
        (int undoRestored, int undoDeleted, int undoFailed) = undoTask.ExecuteUndo([.. _fileNames]);
        _modifiedCount += undoRestored;
        _deletedCount += undoDeleted;
        _failedCount += undoFailed;
        return Task.CompletedTask;
    }

    private void GetFileList(List<DirectoryInfo> directoryList)
    {
        _fileNames = [];
        foreach (DirectoryInfo directoryInfo in directoryList)
        {
            Log.Information("Enumerating files in '{DirectoryPath}'", directoryInfo.FullName);

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
                "Found {FileCount} files in '{DirectoryPath}'",
                count,
                directoryInfo.FullName
            );
        }
    }

    private bool FixCaseConflicts()
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
        Log.Warning("Found {FileCount} case variants of file: '{FilePath}'", files.Count, files[0]);
        int counter = 1;
        foreach (string file in files)
        {
            string uniqueFileName = GetUniqueFileName(file, ref counter);
            Log.Warning("Renaming '{SourcePath}' to '{DestinationPath}'", file, uniqueFileName);
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
            Log.Verbose("Dry run enabled, skipping action in {Function}", function);
        }
        return commandLineOptions.DryRun;
    }
}
