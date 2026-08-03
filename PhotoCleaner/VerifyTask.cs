using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace PhotoCleaner;

// Only running Immich's own decoder can catch files that are intact but undecodable.
internal sealed class VerifyTask(
    CommandLine.Options options,
    Database? database,
    SkippedExtensionTracker skippedExtensions
)
{
    // Immich publishes no :latest tag, so :release is the rolling equivalent.
    internal const string ImmichImage = "ghcr.io/immich-app/immich-server:release";

    // Namespaced to this application and purpose so it cannot collide with any other container.
    internal const string ContainerLabel = "photocleaner-verify";

    // Stateless and shared across the parallel loop, rather than rebuilt for every file.
    private readonly IndexTask? _indexTask = database is null
        ? null
        : new IndexTask(options, database, skippedExtensions);

    // A host path is not a valid container path on every platform.
    // The tree is mounted here and paths are translated across the boundary.
    private const string ContainerMount = "/photocleaner";

    // Files per container invocation, amortizing a measured four seconds of fixed startup.
    // Most of that is Immich's own module graph loading, which no faithful invocation can avoid.
    // Per-file cost spans fiftyfold between a thumbnail and a raw frame.
    // The size is set for the cheap end, where it still outruns startup sevenfold.
    // Going higher risks fewer batches than threads on a small tree, which costs more than it saves.
    private const int BatchSize = 1024;

    internal sealed record Counts(int Verified, int Invalid, int Skipped, int Ignored, int Failed);

    private sealed class Tally
    {
        private int _verified;
        private int _invalid;
        private int _skipped;
        private int _ignored;
        private int _failed;

        internal void Verified() => Interlocked.Increment(ref _verified);

        internal void Invalid() => Interlocked.Increment(ref _invalid);

        internal void Skipped() => Interlocked.Increment(ref _skipped);

        internal void Ignored() => Interlocked.Increment(ref _ignored);

        internal void Failed() => Interlocked.Increment(ref _failed);

        internal Counts ToCounts() => new(_verified, _invalid, _skipped, _ignored, _failed);
    }

    internal async Task<Counts> ExecuteAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken = default
    )
    {
        Tally tally = new();
        await RunAsync(files, tally, cancellationToken).ConfigureAwait(false);
        return tally.ToCounts();
    }

    // Batched so one container serves many files, since startup dominates the per-file cost.
    // Batches run in parallel, each recording its own results, so state lands throughout the run.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Per-file catch-all logs the file path and continues verifying remaining files"
    )]
    private async Task RunAsync(
        IReadOnlyCollection<string> files,
        Tally tally,
        CancellationToken cancellationToken
    )
    {
        // Nothing to decode means nothing needs Docker.
        // The preflight therefore waits for a file that actually reaches the decoder.
        // A tree of non-media files needs none.
        // Neither does a database-backed run whose files are all already verified.
        // The first batch to find a candidate pays for it, bounding a Docker failure to one batch.
        Lazy<Task> preflight = new(
            () => PreflightAsync(cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication
        );

        string mount = options.Path.FullName;
        List<string[]> batches = [.. files.Chunk(BatchSize)];
        Log.Information(
            "Verifying {FileCount} files in {BatchCount} batches",
            files.Count,
            batches.Count
        );

        await Parallel
            .ForEachAsync(
                batches,
                CreateParallelOptions(cancellationToken),
                async (batch, token) =>
                {
                    List<string> candidates = new(batch.Length);
                    foreach (string file in batch)
                    {
                        try
                        {
                            if (!await ShouldVerifyAsync(file, tally, token).ConfigureAwait(false))
                            {
                                continue;
                            }
                            candidates.Add(file);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // One unreadable file must not abort the batch, nor the run.
                            Log.Error(ex, "Failed to verify '{FilePath}'", file);
                            tally.Failed();
                        }
                    }

                    if (candidates.Count > 0)
                    {
                        await preflight.Value.ConfigureAwait(false);
                        await DecodeBatchAsync(candidates, mount, tally, token)
                            .ConfigureAwait(false);
                    }
                }
            )
            .ConfigureAwait(false);
    }

    // Extension filter and database skip check, which is what hashes the file.
    private async Task<bool> ShouldVerifyAsync(
        string file,
        Tally tally,
        CancellationToken cancellationToken
    )
    {
        string extension = Path.GetExtension(file);
        if (!MediaUtilities.SupportedExtensions.Contains(extension))
        {
            Log.Debug("Skipping non-media file: '{FilePath}'", file);
            skippedExtensions.Track(extension);
            tally.Ignored();
            return false;
        }

        // Verify only reads, so a file gone since enumeration means the tree changed mid-run.
        // The run no longer covers what it was asked to, which is a failure rather than damage.
        // Import reaches the same verdict through the exception its file access throws.
        // Verify needs the check stated, because without a database it never opens the file.
        // The path would otherwise reach Immich and come back as unrenderable instead.
        if (!File.Exists(file))
        {
            Log.Error("Failed to verify '{FilePath}': the file no longer exists", file);
            tally.Failed();
            return false;
        }

        if (_indexTask is not null)
        {
            (IndexStatus status, _, _, bool wasVerified) = await _indexTask
                .IndexFileAsync(file, cancellationToken)
                .ConfigureAwait(false);
            if (status == IndexStatus.Unchanged && wasVerified && !options.Reprocess)
            {
                Log.Debug("Skipping already verified '{FilePath}'", file);
                tally.Skipped();
                return false;
            }
        }

        // Everything reaching here goes to the decoder, so this is where readability has to hold.
        // With no database nothing opens the file at all.
        // With one the size and mtime cache returns hashes without reading it.
        // The per-file guard turns the throw into a failed count.
        MediaUtilities.EnsureReadable(file);
        return true;
    }

    private async Task DecodeBatchAsync(
        List<string> batch,
        string mount,
        Tally tally,
        CancellationToken cancellationToken
    )
    {
        List<(string Host, string Container)> mapped = new(batch.Count);
        foreach (string file in batch)
        {
            // Paths reach the container one per line, so a line break cannot be expressed here.
            // Without this the name splits and the container decodes fragments of it.
            // The counts land the same either way, because a split path matches no returned verdict.
            // So this buys an accurate reason rather than a different tally.
            if (
                file.Contains('\n', StringComparison.Ordinal)
                || file.Contains('\r', StringComparison.Ordinal)
            )
            {
                Log.Error("Failed to verify '{FilePath}': the path contains a line break", file);
                tally.Failed();
                continue;
            }

            if (!TryToContainerPath(mount, file, out string container))
            {
                // The container path would leave the mount, so Immich would judge a different file.
                Log.Error(
                    "File '{FilePath}' is not under the verified directory '{MountRoot}'",
                    file,
                    mount
                );
                tally.Failed();
                continue;
            }
            mapped.Add((file, container));
        }

        if (mapped.Count == 0)
        {
            return;
        }

        BufferedCommandResult result;
        try
        {
            result = await RunImmichAsync(
                    ImmichVerifyScript.Verify,
                    mount,
                    string.Join('\n', mapped.Select(entry => entry.Container)),
                    $"decode of {mapped.Count} files",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The batch never ran, so none of its files were judged.
            // Counted over mapped rather than batch, since anything dropped in mapping already counted.
            Log.Error(ex, "Immich decode batch of {FileCount} files failed to run", mapped.Count);
            for (int i = 0; i < mapped.Count; i++)
            {
                tally.Failed();
            }
            return;
        }

        if (result.ExitCode != 0)
        {
            Log.Error(
                "Immich decode batch exited {ExitCode}: {Error}",
                result.ExitCode,
                result.StandardError.Trim()
            );
        }

        Dictionary<string, ImmichVerifyLine> results = new(StringComparer.Ordinal);
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            ImmichVerifyLine? parsed = ParseLine(line);
            if (parsed?.Path is not null)
            {
                results[parsed.Path] = parsed;
            }
        }

        foreach ((string host, string container) in mapped)
        {
            if (!results.TryGetValue(container, out ImmichVerifyLine? line))
            {
                // A missing verdict is a tooling gap, so it must never be reported as corruption.
                Log.Error("No verification result returned for '{FilePath}'", host);
                tally.Failed();
                continue;
            }

            if (!line.Ok)
            {
                // A file removed after the batch was assembled makes the decoder fail too.
                // Reporting that as unrenderable would call a vanished file damaged media.
                // Deliberately untested, because reproducing it needs a race.
                // The file has to vanish between assembling a batch and reading its results.
                if (!File.Exists(host))
                {
                    Log.Error("Failed to verify '{FilePath}': the file no longer exists", host);
                    tally.Failed();
                    continue;
                }

                Log.Error("Immich cannot render '{FilePath}': {Error}", host, line.Error);
                tally.Invalid();
                continue;
            }

            Log.Debug("Verified '{FilePath}' via {Via}", host, line.Via);
            tally.Verified();
            if (database is not null)
            {
                await database.SetProcessedAsync(host, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static ImmichVerifyLine? ParseLine(string line)
    {
        ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
        if (trimmed.IsEmpty || trimmed[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                trimmed,
                ImmichVerifyJsonContext.Default.ImmichVerifyLine
            );
        }
        catch (JsonException)
        {
            Log.Warning("Ignoring unparsable verification output: '{Line}'", line);
            return null;
        }
    }

    // Throws rather than returning, so an unreachable Docker exits Error instead of condemning every file.
    private static async Task PreflightAsync(CancellationToken cancellationToken)
    {
        Log.Information("Verifying Immich image '{Image}' is usable", ImmichImage);

        BufferedCommandResult result;
        try
        {
            result = await RunImmichAsync(
                    ImmichVerifyScript.Preflight,
                    mount: null,
                    standardInput: null,
                    "preflight",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Verification requires the 'docker' command and the Immich image, and has no "
                    + "offline mode, because Immich's decoder is the whole check. Run verify "
                    + "somewhere Docker is available.",
                ex
            );
        }

        if (
            result.ExitCode != 0
            || !result.StandardOutput.Contains(
                ImmichVerifyScript.PreflightSentinel,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidOperationException(
                $"Immich image '{ImmichImage}' could not be prepared for verification "
                    + $"(exit {result.ExitCode}): {result.StandardError.Trim()}"
            );
        }
    }

    private static async Task<BufferedCommandResult> RunImmichAsync(
        string script,
        string? mount,
        string? standardInput,
        string label,
        CancellationToken cancellationToken
    )
    {
        List<string> arguments =
        [
            "run",
            "--rm",
            "-i",
            // The image's healthcheck cannot pass when the entrypoint is node.
            // Without this every container reports unhealthy and alerts whatever watches Docker.
            "--no-healthcheck",
            // Marks the containers this tool starts so they can be found without matching on image.
            // Other containers may run the same image, so an image or ancestor filter is not unique.
            "--label",
            $"{ContainerLabel}=1",
            "--entrypoint",
            "node",
            "-w",
            ImmichVerifyScript.WorkingDirectory,
        ];
        if (mount is not null)
        {
            // Not -v, whose spec is colon delimited, so a colon in the host path is rejected.
            // --mount is comma delimited and would reject a comma instead.
            // A comma is the more common of the two in a photo directory name.
            // Quoting the whole source field as CSV covers both, with any quote doubled.
            string source = mount.Replace("\"", "\"\"", StringComparison.Ordinal);
            arguments.Add("--mount");
            arguments.Add($"type=bind,\"source={source}\",target={ContainerMount},readonly");
        }
        arguments.Add(ImmichImage);
        arguments.Add("-e");
        arguments.Add(script);

        // Paired start and stop lines, so the log timestamps carry the duration.
        Log.Debug("docker: Starting Immich {Label}", label);
        BufferedCommandResult result = await Cli.Wrap("docker")
            .WithArguments(arguments)
            .WithStandardInputPipe(
                standardInput is null ? PipeSource.Null : PipeSource.FromString(standardInput)
            )
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        Log.Debug("docker: Stopped Immich {Label}", label);
        return result;
    }

    // Fails rather than mapping a file outside the mount.
    // Such a path resolves to a container path that leaves the mount, naming a different file.
    internal static bool TryToContainerPath(string mountRoot, string file, out string containerPath)
    {
        string relative = Path.GetRelativePath(mountRoot, file);
        if (
            Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            containerPath = string.Empty;
            return false;
        }

        containerPath = ContainerMount + "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
        return true;
    }

    private ParallelOptions CreateParallelOptions(CancellationToken cancellationToken) =>
        new() { MaxDegreeOfParallelism = options.Threads, CancellationToken = cancellationToken };
}
