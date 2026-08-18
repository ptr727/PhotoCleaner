using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PhotoCleaner;

internal sealed partial class UndoTask(CommandLine.Options options)
{
    [GeneratedRegex(@"\.bak\d*$")]
    private static partial Regex BackupPattern();

    [GeneratedRegex(@"\.bak\d+$")]
    private static partial Regex NumberedPattern();

    internal static bool IsBackupFile(string path) => BackupPattern().IsMatch(path);

    internal static bool IsNumberedBackup(string path) => NumberedPattern().IsMatch(path);

    internal static string GetBackupBase(string path) =>
        BackupPattern().Replace(path, string.Empty);

    internal static int GetBackupSortKey(string path)
    {
        // .bak -> 0 (sort first), .bak1 -> 1, .bak2 -> 2, ...
        Match m = NumberedPattern().Match(path);
        return m.Success ? int.Parse(m.Value[4..], CultureInfo.InvariantCulture) : 0;
    }

    internal (int restored, int deleted, int failed) Execute(
        IReadOnlyCollection<string> allFiles,
        CancellationToken cancellationToken = default
    )
    {
        // Collect only backup files and group them by base path.
        // For example, img.mov.bak and img.mp4.bak1 are grouped by img.mov and img.mp4.
        Dictionary<string, List<string>> groups = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in allFiles)
        {
            if (!IsBackupFile(file))
            {
                continue;
            }

            string basePath = GetBackupBase(file);
            if (!groups.TryGetValue(basePath, out List<string>? backups))
            {
                backups = [];
                groups[basePath] = backups;
            }

            backups.Add(file);
        }

        if (groups.Count == 0)
        {
            Log.Information("No backup files found, nothing to undo");
            return (0, 0, 0);
        }

        // Pass 1 - identify derived base paths
        HashSet<string> derivedBases = new(StringComparer.OrdinalIgnoreCase);

        // Among same-stem groups, the one whose base file still exists is the conversion output.
        // The original was backed up then removed, so only the output remains on disk.
        foreach (KeyValuePair<string, List<string>> kvp in groups)
        {
            string basePath = kvp.Key;
            if (derivedBases.Contains(basePath))
            {
                continue;
            }

            string? directory = Path.GetDirectoryName(basePath);
            string stem = Path.GetFileNameWithoutExtension(basePath);
            string ext = Path.GetExtension(basePath);

            bool hasStemPeer = groups.Keys.Any(other =>
                !string.Equals(other, basePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetDirectoryName(other),
                    directory,
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    Path.GetFileNameWithoutExtension(other),
                    stem,
                    StringComparison.OrdinalIgnoreCase
                )
                && !string.Equals(Path.GetExtension(other), ext, StringComparison.OrdinalIgnoreCase)
            );

            if (hasStemPeer && File.Exists(basePath))
            {
                _ = derivedBases.Add(basePath);
            }
        }

        // Pass 2 - act on each group
        int restored = 0;
        int deleted = 0;
        int failed = 0;

        Log.Information("Undoing {GroupCount} backup groups", groups.Count);
        foreach (KeyValuePair<string, List<string>> kvp in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string basePath = kvp.Key;
            Log.Information("Undoing '{FilePath}'", basePath);
            List<string> backups = kvp.Value;

            if (derivedBases.Contains(basePath))
            {
                // Derived: delete the current (converted) file and all its backups
                if (File.Exists(basePath))
                {
                    Log.Information("Deleting derived file: '{FilePath}'", basePath);
                    if (!TryDelete(basePath))
                    {
                        failed++;
                        continue;
                    }

                    deleted++;
                }

                foreach (string backup in backups)
                {
                    Log.Information("Deleting derived backup: '{BackupPath}'", backup);
                    if (!TryDelete(backup))
                    {
                        failed++;
                        continue;
                    }

                    deleted++;
                }
            }
            else
            {
                // Non-derived: restore original from primary backup (.bak)
                // Select the first non-empty backup in natural order (.bak, .bak1, .bak2, ...)
                string? primaryBackup = null;
                foreach (string candidate in backups.OrderBy(GetBackupSortKey))
                {
                    if (new FileInfo(candidate).Length == 0)
                    {
                        Log.Warning(
                            "Backup '{BackupPath}' is empty (0 bytes), skipping",
                            candidate
                        );
                        continue;
                    }

                    primaryBackup = candidate;
                    break;
                }

                if (primaryBackup is null)
                {
                    Log.Warning(
                        "No usable backup found for '{BasePath}' - all backups are empty, leaving file unchanged",
                        basePath
                    );
                    failed++;
                    continue;
                }

                // Delete the current file if it exists (date-from-path wrote it in-place)
                if (File.Exists(basePath))
                {
                    Log.Information("Deleting current file before restore: '{FilePath}'", basePath);
                    if (!TryDelete(basePath))
                    {
                        failed++;
                        continue;
                    }
                }

                // Restore primary backup -> original name
                Log.Information(
                    "Restoring '{BackupPath}' to '{FilePath}'",
                    primaryBackup,
                    basePath
                );
                if (!TryRestore(primaryBackup, basePath))
                {
                    failed++;
                    continue;
                }

                restored++;

                // Once the original is restored the intermediate-state backups serve no purpose.
                // TryRestore already consumed primaryBackup, so it is skipped.
                foreach (
                    string backup in backups.Where(b =>
                        !string.Equals(b, primaryBackup, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    Log.Information("Deleting superseded backup: '{BackupPath}'", backup);
                    if (!TryDelete(backup))
                    {
                        failed++;
                    }
                    else
                    {
                        deleted++;
                    }
                }

                // The .bak.out companion stays reliable when the output name gained a counter suffix.
                // The stem-based heuristic is the fallback for backups predating that companion.
                string companionPath = primaryBackup + ".out";
                if (File.Exists(companionPath))
                {
                    string trackedOutput = File.ReadAllText(companionPath).Trim();
                    Log.Information("Deleting conversion tracker: '{FilePath}'", companionPath);
                    if (!TryDelete(companionPath))
                    {
                        failed++;
                    }
                    else
                    {
                        deleted++;
                    }

                    if (File.Exists(trackedOutput) && !groups.ContainsKey(trackedOutput))
                    {
                        Log.Information(
                            "Deleting tracked derived output: '{FilePath}'",
                            trackedOutput
                        );
                        if (!TryDelete(trackedOutput))
                        {
                            failed++;
                        }
                        else
                        {
                            deleted++;
                        }
                    }
                }
                else if (
                    !string.Equals(
                        Path.GetExtension(basePath),
                        MediaUtilities.VideoOutputExtension,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    // Legacy fallback looking for a same-stem .mp4 with no backup of its own.
                    // Only the known video output extension is targeted.
                    // A format-agnostic scan would falsely delete companion images.
                    string stem = Path.GetFileNameWithoutExtension(basePath);
                    string? dir = Path.GetDirectoryName(basePath);
                    string derivedOutput = Path.Combine(
                        dir ?? string.Empty,
                        stem + MediaUtilities.VideoOutputExtension
                    );

                    if (File.Exists(derivedOutput) && !groups.ContainsKey(derivedOutput))
                    {
                        Log.Information(
                            "Deleting single-run derived output: '{FilePath}'",
                            derivedOutput
                        );
                        if (!TryDelete(derivedOutput))
                        {
                            failed++;
                        }
                        else
                        {
                            deleted++;
                        }
                    }
                }
            }
        }

        return (restored, deleted, failed);
    }

    private bool IsDryRun([CallerMemberName] string function = "unknown")
    {
        if (options.DryRun)
        {
            Log.Verbose("Dry run enabled, skipping action in {Function}", function);
        }

        return options.DryRun;
    }

    private bool TryDelete(string path)
    {
        if (IsDryRun())
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Failed to delete '{FilePath}'", path);
            return false;
        }
    }

    private bool TryRestore(string backup, string target)
    {
        if (IsDryRun())
        {
            return true;
        }

        try
        {
            File.Move(backup, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Failed to restore '{BackupPath}' to '{FilePath}'", backup, target);
            return false;
        }
    }
}
