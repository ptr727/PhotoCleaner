namespace PhotoCleaner;

internal static class DirectoryCleaner
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Per-directory catch-all logs the path and continues with the remaining directories"
    )]
    internal static int DeleteEmptyDirectories(DirectoryInfo root, bool dryRun)
    {
        int count = 0;

        // Order deepest-first so children are removed before their parents
        List<string> subdirs =
        [
            .. Directory
                .EnumerateDirectories(root.FullName, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)),
        ];

        foreach (string dir in subdirs)
        {
            if (Directory.EnumerateFileSystemEntries(dir).Any())
            {
                continue;
            }

            Log.Information("Deleting empty directory: '{DirectoryPath}'", dir);
            if (dryRun)
            {
                count++;
                continue;
            }

            try
            {
                Directory.Delete(dir);
                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Failed to delete empty directory: '{DirectoryPath}'", dir);
            }
        }

        return count;
    }
}
