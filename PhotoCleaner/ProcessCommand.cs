using System.Collections.Concurrent;

namespace PhotoCleaner;

internal sealed class ProcessCommand(
    CommandLine.Options options,
    CancellationToken cancellationToken
)
{
    private ConcurrentBag<string> _fileNames = [];
    private readonly SkippedExtensionTracker _skippedExtensions = new();
    private int _failedCount;
    private int _deletedCount;
    private int _modifiedCount;
    private int _skippedCount;
    private int _totalCount;

    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync(
                "Processing",
                async () =>
                {
                    await DatabaseScope
                        .RunAsync(
                            options.DbFile,
                            async database =>
                            {
                                // Rename duplicate mixed case files
                                bool foundConflicts = true;
                                while (foundConflicts)
                                {
                                    (IReadOnlyList<string> files, _totalCount) =
                                        FileEnumerator.Enumerate(
                                            options.Path,
                                            options.Threads,
                                            cancellationToken
                                        );
                                    _fileNames = [.. files];
                                    foundConflicts = FixCaseConflicts();
                                }

                                // Process files
                                while (!_fileNames.IsEmpty)
                                {
                                    await ExecuteProcessAsync(database).ConfigureAwait(false);
                                }
                            },
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    Log.Information("Total {TotalCount} files", _totalCount);

                    _skippedExtensions.LogWarnings();

                    if (_skippedCount > 0)
                    {
                        Log.Information(
                            "Skipped {SkippedCount} already processed files",
                            _skippedCount
                        );
                    }

                    if (_modifiedCount > 0)
                    {
                        Log.Information("Modified {ModifiedCount} files", _modifiedCount);
                    }

                    if (_deletedCount > 0)
                    {
                        Log.Information("Deleted {DeletedCount} files", _deletedCount);
                    }

                    if (_failedCount > 0)
                    {
                        Log.Warning("Failed {FailedCount} files", _failedCount);
                    }
                }
            )
            .ConfigureAwait(false);

    private async Task ExecuteProcessAsync(Database? database)
    {
        ConcurrentBag<string> reProcessNames = [];
        Log.Information("Processing {FileCount} files", _fileNames.Count);
        await Parallel
            .ForEachAsync(
                _fileNames,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.Threads,
                    CancellationToken = cancellationToken,
                },
                async (fileName, ct) =>
                {
                    Log.Debug("Processing '{FilePath}'", fileName);
                    switch (
                        await ProcessTask
                            .ExecuteAsync(
                                options,
                                new FileInfo(fileName),
                                database,
                                reProcessNames,
                                _skippedExtensions,
                                ct
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

    private bool FixCaseConflicts()
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

        bool foundConflicts = false;
        foreach (List<string> files in fileNameMap.Values.Where(values => values.Count > 1))
        {
            foundConflicts = true;
            RenamedMixedCaseFiles(files);
        }

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
            if (!options.DryRun)
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

        string uniqueFileName = Path.Combine(directory, $"{name}_{counter}{extension}");
        counter++;
        while (File.Exists(uniqueFileName))
        {
            counter++;
            uniqueFileName = Path.Combine(directory, $"{name}_{counter}{extension}");
        }
        return uniqueFileName;
    }
}
