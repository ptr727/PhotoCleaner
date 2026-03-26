namespace PhotoCleaner;

internal sealed class CleanupTask(CommandLine.Options options)
{
    internal (int deleted, int failed) Execute(
        IReadOnlyCollection<string> allFiles,
        CancellationToken cancellationToken = default
    )
    {
        int deleted = 0;
        int failed = 0;

        foreach (string file in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (MediaUtilities.SupportedExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            if (UndoTask.IsBackupFile(file))
            {
                Log.Warning("Deleting backup artefact: '{FilePath}'", file);
            }
            else
            {
                Log.Information("Deleting non-media file: '{FilePath}'", file);
            }

            if (options.DryRun)
            {
                deleted++;
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Failed to delete '{FilePath}'", file);
                failed++;
            }
        }

        return (deleted, failed);
    }
}
