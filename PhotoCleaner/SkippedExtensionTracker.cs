using System.Collections.Concurrent;

namespace PhotoCleaner;

internal sealed class SkippedExtensionTracker
{
    private readonly ConcurrentDictionary<string, byte> _extensions = new(
        StringComparer.OrdinalIgnoreCase
    );

    internal ICollection<string> Extensions => _extensions.Keys;

    internal void Track(string extension) => _extensions.TryAdd(extension, 0);

    internal void LogWarnings()
    {
        if (_extensions.IsEmpty)
        {
            return;
        }

        List<string> sorted = [.. _extensions.Keys];
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string extension in sorted)
        {
            // A file with no extension tracks as an empty string, which reads as a quoted nothing.
            // Naming the case keeps the count honest without printing a blank.
            if (extension.Length == 0)
            {
                Log.Warning("Skipped files carrying no extension");
                continue;
            }

            Log.Warning("Unknown file extension: '{Extension}'", extension);
        }
    }
}
