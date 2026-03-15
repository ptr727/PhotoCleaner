namespace PhotoCleaner;

internal sealed class CleanupTask(bool dryRun)
{
    internal (int deleted, int failed) ExecuteCleanup(IReadOnlyCollection<string> allFiles)
    {
        int deleted = 0;
        int failed = 0;

        foreach (string file in allFiles)
        {
            if (ProcessTask.SupportedExtensions.Contains(Path.GetExtension(file)))
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

            if (dryRun)
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
