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
                            cancellationToken: cancellationToken
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Per-file catch-all logs the file path and continues processing remaining files"
    )]
    private async Task ExecuteProcessAsync(Database? database)
    {
        // Separate files that share a stem (different extensions) so they
        // are never processed in parallel - prevents one thread from
        // deleting/renaming a file that another thread is reading.
        ConcurrentBag<string> deferred = FixExtensionConflicts();

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
                    Log.Information("Processing '{FilePath}'", fileName);
                    try
                    {
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
                    catch (Exception ex)
                        when (ex is FileNotFoundException || !File.Exists(fileName))
                    {
                        Log.Information(
                            "File no longer exists during processing (concurrent rename): '{FilePath}'",
                            fileName
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to process '{FilePath}'", fileName);
                        _ = Interlocked.Increment(ref _failedCount);
                    }
                }
            )
            .ConfigureAwait(false);

        // Merge deferred extension-conflict files for the next iteration
        foreach (string deferredFile in deferred)
        {
            reProcessNames.Add(deferredFile);
        }

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

    private ConcurrentBag<string> FixExtensionConflicts()
    {
        (ConcurrentBag<string> filtered, ConcurrentBag<string> deferred) = SplitExtensionConflicts(
            _fileNames
        );
        _fileNames = filtered;
        return deferred;
    }

    internal static (
        ConcurrentBag<string> kept,
        ConcurrentBag<string> deferred
    ) SplitExtensionConflicts(ConcurrentBag<string> fileNames)
    {
        // Group files by directory + stem (filename without extension).
        // When multiple files share the same stem (e.g. IMG.DNG + IMG.jpg),
        // keep only one per group and defer the rest so they are never
        // processed in parallel.
        ConcurrentBag<string> deferred = [];
        Dictionary<string, List<string>> stemMap = new(
            fileNames.Count,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (string fileName in fileNames)
        {
            string stem = Path.Combine(
                Path.GetDirectoryName(fileName) ?? string.Empty,
                Path.GetFileNameWithoutExtension(fileName)
            );
            if (!stemMap.TryGetValue(stem, out List<string>? group))
            {
                stemMap[stem] = [fileName];
            }
            else
            {
                group.Add(fileName);
            }
        }

        ConcurrentBag<string> filtered = [];
        foreach (KeyValuePair<string, List<string>> entry in stemMap)
        {
            filtered.Add(entry.Value[0]);
            if (entry.Value.Count > 1)
            {
                Log.Information(
                    "Deferring {DeferCount} extension conflict(s) for stem '{Stem}'",
                    entry.Value.Count - 1,
                    entry.Key
                );
                for (int i = 1; i < entry.Value.Count; i++)
                {
                    deferred.Add(entry.Value[i]);
                }
            }
        }

        return (filtered, deferred);
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
