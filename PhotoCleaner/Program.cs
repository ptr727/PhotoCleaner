using System.Collections.Concurrent;

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
    private int _skippedCount;
    private int _totalCount;

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
            // CA2000 false positive: database is disposed in the finally block below
#pragma warning disable CA2000
            Database? database = commandLineOptions.DbPath is not null
                ? new Database(commandLineOptions.DbPath.FullName)
                : null;
#pragma warning restore CA2000
            try
            {
                if (database is not null)
                {
                    await database.InitializeAsync().ConfigureAwait(false);
                    Log.Warning(
                        "Skipping already processed files using database '{DbPath}'",
                        commandLineOptions.DbPath!.FullName
                    );
                }

                // Rename duplicate mixed case files
                bool foundConflicts = true;
                while (foundConflicts)
                {
                    GetFileList(commandLineOptions.Path);
                    foundConflicts = FixCaseConflicts();
                }

                // Process files
                while (!_fileNames.IsEmpty)
                {
                    await ExecuteProcessAsync(database).ConfigureAwait(false);
                }
            }
            finally
            {
                if (database is not null)
                {
                    await database.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Processing complete");
        Log.Information("Total: {TotalCount} files", _totalCount);

        if (!_unknownExtensions.IsEmpty)
        {
            List<string> unknownExtensionsList = [.. _unknownExtensions.Keys];
            unknownExtensionsList.Sort();
            foreach (string extension in unknownExtensionsList)
            {
                Log.Warning("Unknown file extension: '{Extension}'", extension);
            }
        }

        if (_skippedCount > 0)
        {
            Log.Information("Skipped {SkippedCount} already processed files", _skippedCount);
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
            GetFileList(commandLineOptions.Path);
            await ExecuteUndoAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Undo complete");
        Log.Information("Total: {TotalCount} files", _totalCount);

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
            GetFileList(commandLineOptions.Path);

            // CA2000 false positive: database is disposed in the finally block below
#pragma warning disable CA2000
            Database? database = commandLineOptions.DbPath is not null
                ? new Database(commandLineOptions.DbPath.FullName)
                : null;
#pragma warning restore CA2000
            try
            {
                if (database is not null)
                {
                    await database.InitializeAsync().ConfigureAwait(false);
                }

                OrganizeTask task = new(
                    commandLineOptions.DryRun,
                    commandLineOptions.OutPath!,
                    commandLineOptions.Format,
                    commandLineOptions.Threads,
                    commandLineOptions.DeleteEmpty,
                    commandLineOptions.Move,
                    commandLineOptions.Rehash,
                    database
                );
                (int organized, int ignored, int skipped, int failed, int deletedDirs) =
                    await task.ExecuteOrganizeAsync(
                            [.. _fileNames],
                            commandLineOptions.Path,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                Log.Information("Total: {TotalCount} files", _totalCount);
                if (organized > 0)
                {
                    Log.Information("Organized {OrganizedCount} files", organized);
                }

                if (ignored > 0)
                {
                    Log.Information("Ignored {IgnoredCount} non-media files", ignored);
                }

                if (skipped > 0)
                {
                    Log.Information("Skipped {SkippedCount} files already in collection", skipped);
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
            finally
            {
                if (database is not null)
                {
                    await database.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }

        Log.Information("Organize complete");
        return 0;
    }

    internal async Task<int> DuplicatesCommandAsync()
    {
        Log.Information("Duplicates started");
        try
        {
            GetFileList(commandLineOptions.Path);
            IReadOnlyList<string> sourceFiles = [.. _fileNames];

            GetFileList(commandLineOptions.OutPath!);
            IReadOnlyList<string> outFiles = [.. _fileNames];

            // CA2000 false positive: database is disposed in the finally block below
#pragma warning disable CA2000
            Database database = new(commandLineOptions.DbPath!.FullName);
#pragma warning restore CA2000
            try
            {
                await database.InitializeAsync().ConfigureAwait(false);
                DuplicatesTask task = new(
                    commandLineOptions.DryRun,
                    commandLineOptions.Threads,
                    commandLineOptions.Rehash,
                    database
                );
                (int indexed, int ignored, int deleted, int kept, int failed) =
                    await task.ExecuteDuplicatesAsync(sourceFiles, outFiles, cancellationToken)
                        .ConfigureAwait(false);

                Log.Information("Total: {TotalCount} files", _totalCount);
                Log.Information("Indexed {IndexedCount} source files", indexed);
                if (ignored > 0)
                {
                    Log.Information("Ignored {IgnoredCount} non-media source files", ignored);
                }

                if (deleted > 0)
                {
                    Log.Information("Deleted {DeletedCount} duplicate files", deleted);
                }

                if (kept > 0)
                {
                    Log.Information("Kept {KeptCount} unique files", kept);
                }

                if (failed > 0)
                {
                    Log.Warning("Failed {FailedCount} files", failed);
                }
            }
            finally
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }

        Log.Information("Duplicates complete");
        return 0;
    }

    internal async Task<int> CleanupCommandAsync()
    {
        Log.Information("Cleanup started");
        int deleted;
        int failed;
        try
        {
            GetFileList(commandLineOptions.Path);
            (deleted, failed) = new CleanupTask(commandLineOptions.DryRun).ExecuteCleanup([
                .. _fileNames,
            ]);
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("Cleanup complete");
        int kept = _totalCount - deleted - failed;
        Log.Information("Total: {TotalCount} files", _totalCount);
        if (kept > 0)
        {
            Log.Information("Kept {KeptCount} media files", kept);
        }

        if (deleted > 0)
        {
            Log.Information("Deleted {DeletedCount} non-media files", deleted);
        }

        if (failed > 0)
        {
            Log.Warning("Failed to delete {FailedCount} files", failed);
        }

        return 0;
    }

    internal async Task<int> IndexCommandAsync()
    {
        Log.Information("Index started");
        try
        {
            GetFileList(commandLineOptions.Path);

            // CA2000 false positive: database is disposed in the finally block below
#pragma warning disable CA2000
            Database database = new(commandLineOptions.DbPath!.FullName);
#pragma warning restore CA2000
            try
            {
                await database.InitializeAsync().ConfigureAwait(false);
                IndexTask task = new(commandLineOptions.Rehash, database);
                (int inserted, int updated, int unchanged, int ignored, int failed) =
                    await task.ExecuteIndexAsync(
                            [.. _fileNames],
                            commandLineOptions.Threads,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                Log.Information("Total: {TotalCount} files", _totalCount);
                if (inserted > 0)
                {
                    Log.Information("Inserted {InsertedCount} new files", inserted);
                }

                if (updated > 0)
                {
                    Log.Information("Updated {UpdatedCount} changed files", updated);
                }

                if (unchanged > 0)
                {
                    Log.Information("Unchanged {UnchangedCount} files", unchanged);
                }

                if (ignored > 0)
                {
                    Log.Information("Ignored {IgnoredCount} non-media files", ignored);
                }

                if (failed > 0)
                {
                    Log.Warning("Failed {FailedCount} files", failed);
                }
            }
            finally
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }

        Log.Information("Index complete");
        return 0;
    }

    private async Task ExecuteProcessAsync(Database? database)
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
                                    Rehash = commandLineOptions.Rehash,
                                    ShortVideoDuration = commandLineOptions.ShortVideoDuration,
                                    Reprocess = commandLineOptions.Reprocess,
                                    UnknownExtensions = _unknownExtensions,
                                    ReProcessNames = reProcessNames,
                                    Database = database,
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
                        case ProcessTask.ProcessResult.Skipped:
                            _ = Interlocked.Increment(ref _skippedCount);
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

    private void GetFileList(DirectoryInfo directoryInfo)
    {
        _fileNames = [];
        _totalCount = 0;

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
        _totalCount = count;
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
            if (!commandLineOptions.DryRun)
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
}
