# Architecture

PhotoCleaner is a .NET 10 console application that processes media files in preparation for import into photo management systems (Lightroom, Immich, PhotoPrims). It analyzes and transforms media files through validation, modification, and verification phases.

## Project Structure

- **Docker/**: Docker configuration
  - `Dockerfile`: Two-stage build (SDK Alpine build -> runtime Alpine final) that installs `exiftool` and `ffmpeg` in the final stage
- **PhotoCleaner/**: Main console application
  - `Program.cs`: Entry point with logger setup (Main only)
  - `CommandLine.cs`: System.CommandLine implementation for CLI parsing (`process`, `undo`, `import`, `index`, `trash`, and `verify` subcommands)
  - `MediaUtilities.cs`: Shared static utilities, `SupportedExtensions` (FrozenSet), `GetUniqueFileName`, `GetExifToolJsonAsync`, `SetCreateDateAsync`, video/duration constants
  - `CommandRunner.cs`: Thin wrapper for command start/complete/error logging, taking `Func<Task<int>>` and returning the command's exit code
  - `ExitCode.cs`: The shared exit-code contract, being `Success` (0), `Error` (1, command could not run), and `Failed` (2, ran to completion with per-file failures)
  - `DatabaseScope.cs`: Generic async DB lifecycle helper (create, init, dispose)
  - `TrashDatabaseScope.cs`: Same lifecycle helper pattern for `TrashDatabase`
  - `FileEnumerator.cs`: Parallel file enumeration returning `(IReadOnlyList<string>, int)`
  - `DirectoryCleaner.cs`: Static helper that deletes empty subdirectories under a root (deepest-first, and the root itself is never deleted), used by `import` and `process` when `--deleteempty` is set
  - `ProcessCommand.cs`: Process command orchestration, case conflict resolution, reprocessing loop, result reporting
  - `ImportCommand.cs`: Import command orchestration (formerly `OrganizeCommand`)
  - `IndexCommand.cs`: Index command orchestration
  - `TrashCommand.cs`: Trash command orchestration, fetches trashed asset checksums from Immich API, stores SHA-1 hashes in a `TrashDatabase`
  - `UndoCommand.cs`: Undo command orchestration
  - `VerifyCommand.cs`: Verify command orchestration, which enumerates, runs `VerifyTask`, and reports counts
  - `ProcessTask.cs`: Core file processing pipeline (validation, conversion, metadata)
  - `UndoTask.cs`: Undo logic, two-pass algorithm that restores `.bak` files
  - `ImportTask.cs`: Import logic, copies (default) or moves supported media files from source into date-based subdirectories under `--outpath`. Inserts a row keyed by SOURCE path into Import.db. Optional SQLite deduplication via `Database`. (Formerly `OrganizeTask`.)
  - `VerifyTask.cs`: Verification logic, a decode pass that runs Immich's own `MediaRepository` inside `ghcr.io/immich-app/immich-server:release` via `docker run`, batching paths over stdin. Preflights the image before judging any file, so an infrastructure failure exits `Error` rather than marking files invalid
  - `ImmichVerifyScript.cs`: The Node script run inside the Immich image, as const strings. Calls Immich's own compiled `MediaRepository`, `defaults`, and `ThumbnailConfig` rather than reimplementing the preview pipeline, so behavior tracks Immich across releases
  - `VerifyResult.cs`: The AOT-compatible `ImmichVerifyLine` JSON model and its `ImmichVerifyJsonContext` source-generated context, together forming the container's output protocol
  - `IndexTask.cs`: Common DB upsert logic used by `process` and `index` commands. `IndexFileAsync` (single-file) returns `(IndexStatus, sha256, sha1, wasProcessed)`. `ExecuteAsync` (batch parallel) returns `(inserted, updated, unchanged, ignored, failed)`. When `options.MarkProcessed` is true, newly inserted rows are marked `is_processed=1` (used by `index --processed` to seed Process.db).
  - `Database.cs`: SQLite wrapper with a single `files` table (`path` PRIMARY KEY, `sha256`, `sha1`, `file_size`, `mtime_ticks`, `is_processed`), indexes on both hash columns, and size/mtime caching via `ResolveHashesAsync` to skip rehashing unchanged files. Every write computes both sha256 and sha1 in a single read pass. Both columns are non-null.
  - `TrashDatabase.cs`: Simple SQLite wrapper for Immich trash hashes with a single `trash_hashes` table (`sha1` PRIMARY KEY), used by `trash`, `import`, and `process` commands
  - `ImmichApiModels.cs`: AOT-compatible JSON models for Immich API (`ImmichSearchRequest`, `ImmichSearchResponse`, `ImmichAssetDto`) with `ImmichJsonContext` source generation
  - `DateFromPath.cs`: Static utility class for date inference from filenames/paths
  - `ExifToolJson.cs`: JSON model for ExifTool metadata, including the `ExifTool:Validate` verdict and `ParseValidate` which splits it into error and warning counts
  - `SkippedExtensionTracker.cs`: Thread-safe tracker for unknown file extensions skipped during processing; used by all commands that filter by `MediaUtilities.SupportedExtensions` (`process`, `import`, `index`)
  - `HttpClientFactory.cs`: Polly resilience pipeline (retry, circuit breaker) and `SocketsHttpHandler` connection pooling
  - `AssemblyInfo.cs`: Assembly metadata (app name, version) used by `HttpClientFactory` for User-Agent header
  - `Extensions.cs`: Extension methods for logging and error handling
- **PhotoCleanerTests/**: Comprehensive test project
  - `DateInferenceTests.cs`: Core date inference functionality tests (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Edge cases and comprehensive scenarios (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation tests (15 tests)
  - `ProcessTaskTests.cs`: Process task tests (61 tests)
  - `UndoTaskTests.cs`: Undo task tests (13 tests)
  - `ExifToolJsonTests.cs`: ExifToolJson unit tests (includes GetDate, IsDngVersionNewer) (33 tests)
  - `ImportTaskTests.cs`: Import task tests (24 tests)
  - `DatabaseTests.cs`: Database tests (15 tests)
  - `IndexTaskTests.cs`: IndexTask tests (7 tests)
  - `TrashDatabaseTests.cs`: TrashDatabase tests (8 tests)
  - `TrashCommandTests.cs`: TrashCommand tests with mock HTTP handler (6 tests)
  - `DirectoryCleanerTests.cs`: DirectoryCleaner static helper tests (6 tests)
  - `VerifyTaskTests.cs`: Verify protocol parsing and script-contract tests (10 tests)

## Core Processing Pipeline

The application uses a sequential validation pipeline where each method returns `bool` -
`false` stops processing the current file:

```csharp
if (!RenameMismatchedMimeExtensions()
    || !RenameMixedCaseExtensions()
    || !await DeleteLivePhotosAsync()
    || !await ConvertVideoAsync()
    || !WarnDngVersion())
```

Before that chain runs, `CheckExifToolValidation` acts on the `ExifTool:Validate` verdict that
rides along with the metadata read. Only an error count fails the file (`ProcessResult.Invalid`).
Warnings are logged at debug level, because roughly three quarters of healthy files in a real
collection carry at least one.

## State Management Pattern

- **Primary Constructor Parameters**: Command and task classes use C# 12 primary constructors. All task classes take `CommandLine.Options options` as their first parameter, plus any non-option runtime params (e.g., `Database`, shared collections). Command classes take `(CommandLine.Options options, CancellationToken cancellationToken)` and pass `options` directly to task constructors.
- **Command/Task Separation**: Command classes (e.g., `ProcessCommand`) handle orchestration (file enumeration, DB lifecycle, result logging), while task classes (e.g., `ProcessTask`) handle per-file business logic
- **Composable Infrastructure**: `CommandRunner`, `DatabaseScope`, and `FileEnumerator` are static helpers freely composed by command classes, no inheritance hierarchy
- **Shared Collections**: `ConcurrentBag<string>` for file names, `ConcurrentDictionary<string, byte>` for unknown extensions with case-insensitive comparison
- **Parallel Processing**: Files processed using `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`
- **External Tool Integration**: Uses `CliWrap` for all external command execution (exiftool, ffmpeg, ffprobe)
- **FrozenSet Collections**: All static readonly extension collections use `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` for O(1) lookups

## Key Patterns & Conventions

### External Tool Execution Pattern

```csharp
BufferedCommandResult result = await Cli.Wrap("exiftool")
    .WithArguments(["-groupNames", "-json", "-validate", "-all", _fileInfo.FullName])
    .ExecuteBufferedAsync();
```

- Always use array syntax for arguments: `["-arg1", "value"]`
- Use `BufferedCommandResult` for output capture, `CommandResult` for fire-and-forget
- JSON trimming pattern: `result.StandardOutput.Trim(' ', '\n', '\r', ' ', '[', ']')`

### Media File Processing Conventions

- **FrozenSet Extensions**: Define supported extensions as `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` (e.g., `s_remuxExtensions`, `s_jpegExtensions`)
- **Case-Insensitive Matching**: Use FrozenSet `.Contains()` directly without `.ToLower()`, comparer handles case-insensitivity
- **File Type Categorization**: Group operations by file type requirements (remux vs re-encode vs audio-only)
- **Single-Pass Optimizations**: Prefer single-loop iterations with early exit over multiple LINQ passes
- **Skipped Extension Tracking**: Commands that filter files by `MediaUtilities.SupportedExtensions` pass a shared `SkippedExtensionTracker` instance to their task classes. The tracker collects unknown extensions (thread-safe via `Track()`), and the command calls `LogWarnings()` after processing to log them sorted. Used by `process`, `import`, and `index` commands.

### EXIF/Metadata Handling

- Uses `ExifToolJson` class with `JsonPropertyName` attributes for precise metadata field mapping
- Date validation prioritizes `EXIF:DateTimeOriginal` over `QuickTime:CreateDate`
- Custom `IsDateSet()` and `GetDateString()` methods handle metadata extraction logic
- `ContentIdentifier` property maps both `QuickTime:ContentIdentifier` and `Keys:ContentIdentifier`
  group names (both occur in the wild for ISOBMFF files) returning whichever is set

### Date Inference System (DateFromPath.cs)

- **Static Internal Methods**: All methods are `internal static` for testability with `InternalsVisibleTo`
- **DateFromPath.InferCreatedDate()**: Main entry point, tries filename first, then path fallback
- **DateFromPath.ExtractDateFromFilename()**: Supports multiple filename patterns:
  - `YYYYMMDD_HHMMSS` format (e.g., `20210502_200152957_iOS-1747.jpg`)
  - `YYYYMMDD` format (e.g., `EX_20030219_3378.jpg`)
  - `YYYY-MM-DD-HH-MM-SS` format (e.g., `PHOTO-2024-06-22-07-56-41.jpg`)
  - `YYYY MM DD` format with spaces (e.g., `EV 2014 07 03_0003.tif`)
- **DateFromPath.ExtractDateFromPath()**: Extracts from directory structures and year-only fallback
- **DateFromPath.IsDateValid()**: Validates dates within 1900-current year range

### Command Line Interface (CommandLine.cs)

- **System.CommandLine Integration**: Uses modern .NET command line parsing
- **Six subcommands**: `process`, `undo`, `import`, `index`, `trash`, `verify`, each with their own option set
- **Required `--path` Parameter**: Single directory path using `Option<DirectoryInfo>`. Validated with `AcceptExistingOnly()`
- **Optional `--dryrun` Flag**: Non-destructive preview mode (process, undo, import, not index)
- **Optional `--threads` Parameter**: Controls parallel processing degree with `DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 4)`. Validated to be > 0 and <= Environment.ProcessorCount using `Validators.Add()` (process, import, index)
- **Optional `--skipbackup` Flag** (process only): Skips all `.bak` file creation, originals are deleted/overwritten in-place. Logs a warning at startup. Disables undo.
- **Optional `--deleteempty` Flag** (process, import): After the command completes, deletes empty child subdirectories from the target directory (deepest first, while the target root is never deleted). For `process` the target is `--path` (operated on in-place), and for `import` it is `--outpath`. Implemented by `DirectoryCleaner.DeleteEmptyDirectories(root, dryRun)`.
- **`import` subcommand** (formerly `organize`): Copies (default) or moves supported media files from `--path` sources into `--outpath/date/filename` directory structure. Date comes from EXIF metadata (falls back to `DateTime.MinValue` -> `"0001/01/01"` bucket when absent). `--format` (default `"yyyy/MM/dd"`) controls subdirectory naming and is validated as a date-only format (no time components). Uses `GetUniqueFileName` for collision handling (`foo_1.jpg` etc.). Parallel via `--threads` (same as `process`). `--deleteempty` (default `false`) deletes empty child subdirectories from `--outpath` after all files are imported. `--move` (default `false`) moves files instead of copying. `--tagpath` (default `false`) splits the source sub-directory path into tokens and writes each token as an `XMP:Subject` tag on the destination file using exiftool. It is filtered by `s_exiftoolWriteExtensions` (`.3gp`, `.arw`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.mov`, `.mp4`, `.nef`, `.orf`, `.png`, `.psd`, `.rw2`, `.tif`, `.tiff`) checked via `meta.FileTypeExtension`, and uses `-XMP:Subject-= / -XMP:Subject+=` to prevent duplicates while preserving existing tags. `--tags <string>` (optional) applies explicit comma-separated `XMP:Subject` tags to every imported file. `--datepath` (default `false`) infers the EXIF creation date from the source file path when no date is already embedded, then applies the date to the destination file before restoring mtime. **`--db <sqlite-file>` (Import.db) is the source-side dedup DB**: rows are keyed by `path = source_path` (NOT dest path) and hold the source file's hash/size/mtime. On each source file, import calls `GetByPathAsync(source_path)` for source-side hash caching, then `Sha256ExistsAsync(source_hash)` to skip already-imported sources. New imports insert a row at the source path. **No command outside `import` writes to source-keyed rows**, so dedup cannot be clobbered by later runs of `process`/`index`. `--trashdb <sqlite-file>` skips files whose **source-file** SHA-1 is in Trash.db. When import rewrites the destination via `--tags`/`--tagpath`/`--datepath`, its SHA-1 differs from the source SHA-1. Immich stored the destination SHA-1 from a prior upload, so the trash match is missed here and caught later by `process --trashdb`. `--skipdb <sqlite-file>` skips files whose SHA-256 matches a reference DB (read-only). Cross-collection dedup is typically implemented by pointing `--skipdb` at another collection's Import.db. `--rehash` forces recomputation of all hashes ignoring the size/mtime cache.
- **`index` subcommand**: Iterates all files in `--path`, upserts each into the `files` DB table via `IndexTask.ExecuteAsync` (insert new, update if hash changed, skip unchanged). `--db <sqlite-file>` is **required**. No `--dryrun` (always writes to DB). Supports `--threads` and `--rehash`. `--processed` (optional) marks newly-INSERTED rows with `is_processed = 1`, which is useful when seeding a Process.db from existing files so `process` treats them as already-done. The flag does not flip the flag on existing rows. Reports `inserted`/`updated`/`unchanged`/`ignored`/`failed` counts.
- **`trash` subcommand**: Syncs trashed asset checksums from an Immich server into a local SQLite trash database. `--url` (Immich server URL, required), `--trashdb <sqlite-file>` (trash database, required), and the API key supplied by exactly one of `--apikey` (inline) or `--apikey-file` (path to a file whose trimmed contents are the key). The two API-key options are mutually exclusive and exactly one must be provided; `--apikey-file` must reference an existing, non-empty, readable file (existence enforced by an option validator, non-empty/readable by a command-level validator; read failures are translated to validation errors, never thrown). The key is resolved at parse time by `CommandLine.ResolveApiKey`/`ReadApiKeyFile` (file contents preferred and `.Trim()`-med) and flows into `Options.ImmichApiKey`. Uses `POST /api/search/metadata` with `trashedAfter` to fetch all trashed assets, converts Base64 SHA-1 checksums to hex, and inserts them via `INSERT OR IGNORE`. Full sync (idempotent, append-only). No `--dryrun`.
- **`--trashdb` Flag** (import, process): SQLite database file with Immich trash hashes (synced by `trash`). In `import`, files matching the trash DB are skipped, preventing re-import of photos the user trashed in Immich. The check is against the **source-file** SHA-1, so files whose destination SHA-1 was mutated by `import` itself (`--tags`/`--tagpath`/`--datepath`) will not match here even though Immich stored the mutated SHA-1. The `process --trashdb` command catches those on the next pass. In `process`, matching files are **deleted from disk and from Process.db** before the per-file processing pipeline runs (cleanup of files trashed in Immich after upload, and the safety net for the import source-vs-dest SHA-1 drift). The Trash.db check is the durable safety net beyond Immich's ~30-day trash retention.
- **Optional `--skipdb` Flag** (import only): SQLite database of files to skip (read-only SHA-256 check). Files whose SHA-256 matches a record in this DB are skipped without being recorded. Use this to skip files already present in another collection.
- **Optional `--rehash` Flag** (process, import, index): Forces SHA-256 recomputation for every file, ignoring the size/mtime cache. SHA-1 is also recomputed when `--trashdb` is in use. Useful after filesystem operations that preserve mtime but change content.
- **Optional `--duration` Flag** (process only): Overrides `ShortVideoDuration` (default `1.0`s). Videos in a live-photo-compatible format whose duration is <= this value are always deleted. Must be `> 0`. Stored in `CommandLine.Options.ShortVideoDuration` and read by `DeleteLivePhotosAsync`.
- **Optional `--reprocess` Flag** (process only): When set, ignores `is_processed` in the DB and forces every file to be processed again. Stored in `CommandLine.Options.Reprocess`, it disables the `IndexStatus.Unchanged && wasProcessed` early-return in `ExecuteAsync`.
- **Command Construction**: `CommandLine.SetAction` handlers create the appropriate command class (e.g., `ProcessCommand`, `ImportCommand`) with `CommandLine.Options` and `CancellationToken`
- **Built-in Help System**: Automatic help generation and validation

## Development Workflow

See [`CODESTYLE.md`](../CODESTYLE.md) for build requirements, formatting commands, and tooling.

### Dependencies

- **CliWrap**: External process execution
- **System.CommandLine**: Modern CLI argument parsing and validation
- **System.Text.Json**: High-performance JSON with source generation
- **Microsoft.Data.Sqlite**: SQLite database access for source file deduplication
- **Serilog**: Structured logging with console output
- **Native AOT**: Project configured for `PublishAot=true` with `InvariantGlobalization=true`
- **xUnit**: Testing framework for PhotoCleanerTests project

### Test Architecture

- **PhotoCleanerTests Project**: 300 comprehensive tests covering all functionality
- **InternalsVisibleTo**: Enables direct testing of internal methods without reflection
- **Test Categories**:
  - `DateInferenceTests.cs`: Core date inference functionality (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Date inference edge cases and integration (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation (24 tests)
  - `ProcessTaskTests.cs`: Process task tests (61 tests)
- **Coverage Areas**: Date inference (filename patterns, path structures, validation), command line interface (parsing, validation, error handling, multiple paths, thread configuration and boundary validation), integration scenarios, process task execution, live photo detection (ContentIdentifier matching, `_hevc` suffix naming, mismatch/missing tag scenarios), metadata preservation through conversion

## Critical Implementation Details

### Video Conversion Logic

- **Three-tier approach**: Remux (.mts, .m2ts, .mkv) -> Re-encode (.wmv, .avi, .3gp, .gif) -> Audio-only (.mov/.mp4 with PCM)
- **Backup Strategy**: Original files renamed to `.bak` extension after successful conversion, and `BackupFile()` returns the backup path. A `{backup}.out` companion file (e.g. `img.gif.bak.out`) is written alongside the backup containing the full output path, this is needed when `GetUniqueFileName` appended a counter suffix (e.g. `img_1.mp4`) because the canonical name was already taken. When `options.SkipBackup` is true, no `.bak` or `.bak.out` files are created, and the original is deleted after conversion.
- **Metadata Preservation**: After every ffmpeg conversion, `exiftool -TagsFromFile <source.bak> <output> -all:all -overwrite_original` copies all source metadata to the output file. `ffmpeg -map_metadata` is not used, it is unreliable for Apple QuickTime-specific tags (e.g. `ContentIdentifier` in the `mdta`/`keys` atom). `TagsFromFile` handles cross-format date mapping, so no separate date-setting step is needed after conversion.
- **Re-queue Pattern**: Converted files are added back to processing queue for validation

### Live Photo Detection

- **Short videos** (duration <= `options.ShortVideoDuration`, default `1.0s`, overridable via `--duration`): always deleted regardless of companion file
- **Companion file search** (`FindCompanionImagePath()`): looks for a HEIC/JPG/JPEG file by:
  1. Direct basename match (`IMG_1234.mov` -> `IMG_1234.heic`)
  2. Basename minus `_hevc` suffix (`IMG_1234_HEVC.mov` -> `IMG_1234.heic`), the newer iPhone naming
- **ContentIdentifier confirmation**: a candidate pair is only deleted when both files expose a `ContentIdentifier` tag that matches exactly. If either file lacks the tag, or the tags differ, the video is kept. There is no fallback to name-only deletion.
- **Long videos** (>= `LiveVideoDuration` = 4.0s): always kept even with a matching companion, and a warning is logged

### Undo Architecture (UndoTask.cs)

- **Backup naming**: `X.bak` (first), `X.bak1`, `X.bak2`, ... (subsequent runs of `process`)
- **`FileEnumerator.Enumerate()`** enumerates all files including `.bak*` files before calling `Execute()`
- **Two-pass algorithm** in `UndoTask.Execute()`:
  - *Pass 1 - Identify derived bases*:
    - **Rule 1**: any numbered backup (`.bak1`, `.bak2`, ...) present -> base is derived
    - **Rule 2**: `.mp4` base with same-stem non-`.mp4` primary backup in same dir -> base is derived
  - *Pass 2 - Act*:
    - Derived base: delete current file + all its backups
    - Non-derived base: delete current file if present, restore `X.bak` -> `X`; then locate the derived conversion output: if `X.bak.out` companion exists read the explicit output path from it and delete that file (handles uniquified names like `img_1.mp4`); otherwise fall back to checking whether `stem.mp4` exists and has no backup (legacy single-run heuristic)
- **Internal static helpers** (testable via `InternalsVisibleTo`):
  - `IsBackupFile(path)`: matches `.bak\d*$`
  - `IsNumberedBackup(path)`: matches `.bak\d+$`
  - `GetBackupBase(path)`: strips the `.bak\d*` suffix
- **Dry run**: logs all intended operations but performs no file I/O
- **Known limitation**: extension renames to a previously non-existent filename create no backup and cannot be undone

### Error Handling Strategy

- Console output uses structured prefixes: `WARNING:`, `INFORMATION:`
- External command failures throw `CommandExecutionException`
- Methods return `false` to skip file processing rather than throwing exceptions

## File Processing Extensions

Supported: `.3gp`, `.arw`, `.avi`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.m2ts`, `.mkv`, `.mov`, `.mp4`, `.mts`, `.nef`, `.orf`, `.png`, `.rw2`, `.tif`, `.tiff`, `.wmv`

## JSON Source Generation

Uses `SourceGenerationContext` for AOT-compatible JSON serialization of `ExifToolJson` metadata.
Uses `ImmichJsonContext` for AOT-compatible JSON serialization of Immich API models.

## Testing Strategy

- **Direct Method Testing**: Uses `InternalsVisibleTo` for compile-time safe method calls
- **Comprehensive Coverage**: Tests all filename patterns, path structures, date validation, and CLI parsing
- **Integration Testing**: Validates end-to-end date inference and command line interface logic
- **No Reflection**: All tests use direct method calls for better performance and maintainability

### Command Line Testing Patterns

- **CreateTestCommand() Helper**: Uses `CommandLine.CreateRootCommand()` directly for single source of truth
- **Type-based Option Extraction**: Identifies options by type (`Option<List<DirectoryInfo>>`, `Option<bool>`, `Option<int>`) using 4-tuple destructuring
- **Real Directory Testing**: Uses `Directory.GetCurrentDirectory()` for path validation tests
- **Parse Result Validation**: Tests both success/error states and extracted argument values, including list counts for multiple paths and thread values
- **Comprehensive Scenarios**: Single path, multiple paths, thread configuration, option properties, argument parsing, validation errors, edge cases, default values
- **Multiple Path Testing**: Validates 2-path and 3-path scenarios, mixed valid/invalid paths, and proper list indexing
- **Thread Option Testing**: Validates thread count parsing, default value calculation, short option, and combined option scenarios
