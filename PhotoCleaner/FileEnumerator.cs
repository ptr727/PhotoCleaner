using System.Collections.Concurrent;

namespace PhotoCleaner;

internal static class FileEnumerator
{
    internal static (IReadOnlyList<string> files, int total) Enumerate(
        DirectoryInfo directoryInfo,
        int threads,
        CancellationToken cancellationToken = default
    )
    {
        ConcurrentBag<string> fileNames = [];
        int count = 0;

        Log.Information("Enumerating files in '{DirectoryPath}'", directoryInfo.FullName);

        directoryInfo
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .AsParallel()
            .WithDegreeOfParallelism(threads)
            .WithCancellation(cancellationToken)
            .ForAll(file =>
            {
                fileNames.Add(file.FullName);
                _ = Interlocked.Increment(ref count);
            });

        Log.Information(
            "Found {FileCount} files in '{DirectoryPath}'",
            count,
            directoryInfo.FullName
        );

        return ([.. fileNames], count);
    }
}
