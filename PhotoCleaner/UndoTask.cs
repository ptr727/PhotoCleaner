using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PhotoCleaner;

internal sealed partial class UndoTask(CommandLine.Options options)
{
    [GeneratedRegex(@"\.bak\d*$")]
    private static partial Regex BackupPattern();

    [GeneratedRegex(@"\.bak\d+$")]
    private static partial Regex NumberedPattern();

    internal const string MarkerExtension = ".out";

    // Files removed and names taken so far, so a dry run picks the same free names a live run would.
    private readonly HashSet<string> _removed = new(MediaUtilities.PathComparer);
    private readonly HashSet<string> _taken = new(MediaUtilities.PathComparer);

    // Base names of originals still to restore, which a free name for another original never takes.
    private readonly HashSet<string> _pending = new(MediaUtilities.PathComparer);

    internal static bool IsBackupFile(string path) => BackupPattern().IsMatch(path);

    internal static string GetBackupBase(string path) =>
        BackupPattern().Replace(path, string.Empty);

    internal static int GetBackupSortKey(string path)
    {
        // .bak -> 0 (sort first), .bak1 -> 1, .bak2 -> 2, ...
        Match m = NumberedPattern().Match(path);
        return m.Success ? int.Parse(m.Value[4..], CultureInfo.InvariantCulture) : 0;
    }

    // Process backs up only media it supports, so any other .bak belongs to another tool.
    internal static bool IsUndoableBackup(string path) =>
        IsBackupFile(path)
        && MediaUtilities.SupportedExtensions.Contains(Path.GetExtension(GetBackupBase(path)));

    // The marker holds the absolute path at conversion time, and the output sits beside its backup.
    // A Windows path holds backslashes, while on Linux and macOS a backslash is a file name character.
    internal static string ResolveMarkerOutput(string backup, string markerText)
    {
        string text = markerText.Trim();
        int separator = text.StartsWith('/')
            ? text.LastIndexOf('/')
            : text.LastIndexOfAny(['/', '\\']);
        return Path.Combine(Path.GetDirectoryName(backup) ?? string.Empty, text[(separator + 1)..]);
    }

    private sealed record Backup(string Path, string Base, string? Marker, string? Output);

    internal (int restored, int deleted, int failed) Execute(
        IReadOnlyCollection<string> allFiles,
        CancellationToken cancellationToken = default
    )
    {
        List<string> backupPaths = [];
        foreach (string file in allFiles.Where(IsBackupFile))
        {
            if (IsUndoableBackup(file))
            {
                backupPaths.Add(file);
            }
            else
            {
                Log.Verbose("Skipping '{BackupPath}', not a backup of supported media", file);
            }
        }

        if (backupPaths.Count == 0)
        {
            Log.Information("No backup files found, nothing to undo");
            return (0, 0, 0);
        }

        int restored = 0;
        int deleted = 0;
        int failed = 0;

        // Read every marker first, since a marker on one backup names a file another backup may hold.
        List<Backup> backups = [];
        foreach (
            string path in backupPaths
                .OrderBy(GetBackupBase, StringComparer.Ordinal)
                .ThenBy(GetBackupSortKey)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(path).Length == 0)
                {
                    Log.Warning("Backup '{BackupPath}' is empty (0 bytes), leaving it", path);
                    failed++;
                    continue;
                }

                // An empty marker is a conversion that stopped before its output was placed.
                string? marker = path + MarkerExtension;
                string? output = null;
                if (!File.Exists(marker))
                {
                    marker = null;
                }
                else if (File.ReadAllText(marker) is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    output = ResolveMarkerOutput(path, text);
                }
                else
                {
                    Log.Warning("Marker '{FilePath}' is empty, no output to delete", marker);
                }

                backups.Add(new Backup(path, GetBackupBase(path), marker, output));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Failed to read '{BackupPath}' or its marker, leaving it", path);
                failed++;
            }
        }

        // A backup holding a gone output's name may be that output, culled by a later run or an older release.
        // Restoring it leaves an extra file, while deleting it could lose an original, so it is restored.
        HashSet<Backup> blocked = [];
        Log.Information("Undoing {BackupCount} backups", backups.Count);
        foreach (
            IGrouping<string, Backup> record in backups
                .Where(b => b.Output is not null)
                .GroupBy(b => b.Output!, StringComparer.Ordinal)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            string output = record.Key;
            List<Backup> holders = [.. record];
            List<Backup> outputBackups =
            [
                .. backups.Where(b =>
                    string.Equals(b.Base, output, StringComparison.Ordinal) && !holders.Contains(b)
                ),
            ];

            if (!Exists(output))
            {
                Log.Information(
                    "Converted output '{FilePath}' not found, nothing to delete",
                    output
                );
                continue;
            }

            if (holders.Count > 1 || outputBackups.Count > 0)
            {
                Log.Warning(
                    "Cannot tell which file is the output '{FilePath}' recorded, leaving it, its marker, and its backups",
                    output
                );
                failed++;
                blocked.UnionWith(holders);
                blocked.UnionWith(outputBackups);
                continue;
            }

            Log.Information("Deleting converted output: '{FilePath}'", output);
            if (TryDelete(output))
            {
                deleted++;
            }
            else
            {
                failed++;
                blocked.UnionWith(holders);
            }
        }

        // Every other backup is an original, and an original whose name is taken gets a free one.
        _pending.UnionWith(backups.Select(b => b.Base));
        foreach (Backup backup in backups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blocked.Contains(backup))
            {
                Log.Warning("Leaving '{BackupPath}' as it is", backup.Path);
                continue;
            }

            string target = Exists(backup.Base)
                ? MediaUtilities.GetUniqueFileName(backup.Base, candidate =>
                    Exists(candidate) || _pending.Contains(candidate)
                )
                : backup.Base;
            if (!string.Equals(target, backup.Base, StringComparison.Ordinal))
            {
                Log.Warning(
                    "'{FilePath}' is taken, restoring '{BackupPath}' to '{TargetPath}'",
                    backup.Base,
                    backup.Path,
                    target
                );
            }
            else
            {
                Log.Information("Restoring '{BackupPath}' to '{FilePath}'", backup.Path, target);
            }

            if (!TryRestore(backup.Path, target))
            {
                failed++;
                continue;
            }

            restored++;
            if (backup.Marker is not null)
            {
                Log.Information("Deleting conversion marker: '{FilePath}'", backup.Marker);
                if (TryDelete(backup.Marker))
                {
                    deleted++;
                }
                else
                {
                    failed++;
                }
            }
        }

        return (restored, deleted, failed);
    }

    private bool Exists(string path) =>
        _taken.Contains(path) || (!_removed.Contains(path) && File.Exists(path));

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
        if (!IsDryRun() && MediaUtilities.TryDeleteFile(path) is { } failure)
        {
            Log.Error(failure, "Failed to delete '{FilePath}'", path);
            return false;
        }

        _ = _removed.Add(path);
        return true;
    }

    private bool TryRestore(string backup, string target)
    {
        if (!IsDryRun())
        {
            try
            {
                File.Move(backup, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Failed to restore '{BackupPath}' to '{FilePath}'", backup, target);
                return false;
            }
        }

        _ = _removed.Add(backup);
        _ = _taken.Add(target);
        return true;
    }
}
