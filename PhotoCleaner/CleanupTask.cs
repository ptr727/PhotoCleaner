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
                Log.Warning("Deleting backup artefact: '{File}'", file);
            }
            else
            {
                Log.Warning("Deleting non-media file: '{File}'", file);
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
            catch (IOException ex)
            {
                Log.Error(ex, "Failed to delete '{File}'", file);
                failed++;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Error(ex, "Failed to delete '{File}'", file);
                failed++;
            }
        }

        return (deleted, failed);
    }
}
