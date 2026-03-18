# PhotoCleaner AI Coding Instructions

> For coding style, formatting rules, and conventions, see [`CODESTYLE.md`](../CODESTYLE.md).

## Project Overview
PhotoCleaner is a .NET 10 console application that processes media files in preparation for import into photo management systems (Lightroom, Immich, PhotoPrims). It analyzes and transforms media files through validation, modification, and verification phases.

## Architecture & Data Flow

### Project Structure
- **Docker/**: Docker configuration
  - `Dockerfile`: Two-stage build (SDK Alpine build -> runtime Alpine final); installs `exiftool` and `ffmpeg` in the final stage
- **PhotoCleaner/**: Main console application
  - `Program.cs`: Entry point with logger setup
  - `CommandLine.cs`: System.CommandLine implementation for CLI parsing (`process`, `undo`, `cleanup`, `organize`, `duplicates`, and `index` subcommands)
  - `ProcessTask.cs`: Core file processing pipeline
  - `UndoTask.cs`: Undo logic - two-pass algorithm that restores `.bak` files
  - `CleanupTask.cs`: Cleanup logic - deletes files whose extensions are not in the supported list
  - `OrganizeTask.cs`: Organize logic - copies (default) or moves supported media files into date-based subdirectories; optional SQLite deduplication via `Database`
  - `DuplicatesTask.cs`: Duplicates logic - two-phase: indexes source files into DB via `IndexTask`, then deletes matching files from the target directory
  - `IndexTask.cs`: Common DB upsert logic used by `process`, `duplicates`, and `index` commands; `IndexFileAsync` (single-file) returns `(IndexStatus, hash, wasProcessed)`; `ExecuteIndexAsync` (batch parallel) returns `(inserted, updated, unchanged, ignored, failed)`
  - `Database.cs`: SQLite wrapper with a single `files` table (`path` PRIMARY KEY, `hash`, `file_size`, `mtime_ticks`, `is_processed`); non-unique hash index for dedup lookups; size/mtime caching via `ResolveHashAsync` to skip rehashing unchanged files
  - `DateFromPath.cs`: Static utility class for date inference from filenames/paths
  - `ExifToolJson.cs`: JSON model for ExifTool metadata
  - `Extensions.cs`: Extension methods for logging and error handling
- **PhotoCleanerTests/**: Comprehensive test project
  - `DateInferenceTests.cs`: Core date inference functionality tests (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Edge cases and comprehensive scenarios (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation tests (15 tests)
  - `ProcessTaskTests.cs`: Process task tests (65 tests)
  - `UndoTaskTests.cs`: Undo task tests (13 tests)
  - `CleanupTaskTests.cs`: Cleanup task tests (6 tests)
  - `ExifToolJsonTests.cs`: ExifToolJson unit tests (includes GetDate, IsDngVersionNewer) (33 tests)
  - `OrganizeTaskTests.cs`: Organize task tests (12 tests)
  - `DatabaseTests.cs`: Database tests (15 tests)
  - `DuplicatesTaskTests.cs`: Duplicates task tests (6 tests)
  - `IndexTaskTests.cs`: IndexTask tests (7 tests)

### Core Processing Pipeline

The application uses a sequential validation pipeline where each method returns `bool` -
`false` stops processing the current file:

```csharp
if (!RenameMismatchedMimeExtensions()
    || !RenameMixedCaseExtensions()
    || !await DeleteLivePhotosAsync()
    || !await ConvertVideoAsync()
    || !await SetMissingCreateDateAsync()   // skipped unless --datefrompath is set
    || !WarnDngVersion())
```

### State Management Pattern
- **Primary Constructor Parameters**: Both `Program` and `ProcessTask` use C# 12 primary constructors for immutable configuration (e.g., `Program(int degreeOfParallelism, bool dryRun)`)
- **Shared Collections**: `ConcurrentBag<string>` for file names, `ConcurrentDictionary<string, byte>` for unknown extensions with case-insensitive comparison
- **Parallel Processing**: Files processed using `AsParallel().WithDegreeOfParallelism(degreeOfParallelism)`
- **External Tool Integration**: Uses `CliWrap` for all external command execution (exiftool, ffmpeg, ffprobe)
- **FrozenSet Collections**: All static readonly extension collections use `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` for O(1) lookups

## Key Patterns & Conventions

### External Tool Execution Pattern
```csharp
BufferedCommandResult result = await Cli.Wrap("exiftool")
    .WithArguments(["-groupNames", "-json", _fileInfo.FullName])
    .ExecuteBufferedAsync();
```
- Always use array syntax for arguments: `["-arg1", "value"]`
- Use `BufferedCommandResult` for output capture, `CommandResult` for fire-and-forget
- JSON trimming pattern: `result.StandardOutput.Trim(' ', '\n', '\r', ' ', '[', ']')`

### Media File Processing Conventions
- **FrozenSet Extensions**: Define supported extensions as `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` (e.g., `s_remuxExtensions`, `s_jpegExtensions`)
- **Case-Insensitive Matching**: Use FrozenSet `.Contains()` directly without `.ToLower()` - comparer handles case-insensitivity
- **File Type Categorization**: Group operations by file type requirements (remux vs re-encode vs audio-only)
- **Single-Pass Optimizations**: Prefer single-loop iterations with early exit over multiple LINQ passes

### EXIF/Metadata Handling
- Uses `ExifToolJson` class with `JsonPropertyName` attributes for precise metadata field mapping
- Date validation prioritizes `EXIF:DateTimeOriginal` over `QuickTime:CreateDate`
- Custom `IsDateSet()` and `GetDateString()` methods handle metadata extraction logic
- `ContentIdentifier` property maps both `QuickTime:ContentIdentifier` and `Keys:ContentIdentifier`
  group names (both occur in the wild for ISOBMFF files) returning whichever is set

### Date Inference System (DateFromPath.cs)
- **Static Internal Methods**: All methods are `internal static` for testability with `InternalsVisibleTo`
- **DateFromPath.InferCreatedDate()**: Main entry point - tries filename first, then path fallback
- **DateFromPath.ExtractDateFromFilename()**: Supports multiple filename patterns:
  - `YYYYMMDD_HHMMSS` format (e.g., `20210502_200152957_iOS-1747.jpg`)
  - `YYYYMMDD` format (e.g., `EX_20030219_3378.jpg`)
  - `YYYY-MM-DD-HH-MM-SS` format (e.g., `PHOTO-2024-06-22-07-56-41.jpg`)
  - `YYYY MM DD` format with spaces (e.g., `EV 2014 07 03_0003.tif`)
- **DateFromPath.ExtractDateFromPath()**: Extracts from directory structures and year-only fallback
- **DateFromPath.IsDateValid()**: Validates dates within 1900-current year range

### Command Line Interface (CommandLine.cs)
- **System.CommandLine Integration**: Uses modern .NET command line parsing
- **Six subcommands**: `process`, `undo`, `cleanup`, `organize`, `duplicates`, `index` - each with their own option set
- **Required `--path` Parameter**: Single directory path using `Option<DirectoryInfo>`. Validated with `AcceptExistingOnly()`
- **Optional `--dryrun` Flag**: Non-destructive preview mode (process, undo, cleanup, organize, duplicates - not index)
- **Optional `--threads` Parameter**: Controls parallel processing degree with `DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 4)`. Validated to be > 0 and <= Environment.ProcessorCount using `Validators.Add()` (process, organize, duplicates, index)
- **Optional `--datefrompath` Flag** (process only): Opt-in; when absent, `SetMissingCreateDateAsync` is skipped entirely - date inference from paths is a destructive write that cannot be undone
- **Optional `--skipbackup` Flag** (process only): Skips all `.bak` file creation - originals are deleted/overwritten in-place. Logs a warning at startup. Disables undo.
- **`cleanup` subcommand**: Deletes every file whose extension is not in `ProcessTask.SupportedExtensions`. Logs a warning for `.bak*` artefacts before deleting them. Supports `--dryrun` only (no `--threads` - pure I/O, no benefit).
- **`organize` subcommand**: Copies (default) or moves supported media files from `--path` sources into `--outpath/date/filename` directory structure. Date comes from EXIF metadata (falls back to `DateTime.MinValue` -> `"0001/01"` bucket when absent). `--format` (default `"yyyy/MM"`) controls subdirectory naming and is validated as a date-only format (no time components). Uses `GetUniqueFileName` for collision handling (`foo_1.jpg` etc.). Parallel via `--threads` (same as `process`). `--deleteempty` (default `false`) deletes empty child subdirectories from source paths after all files are organized (deepest first; source roots are never deleted). `--move` (default `false`) moves files instead of copying. `--db <sqlite-file>` (optional) enables SHA-256 deduplication: files whose hash is already in the DB are skipped; new files are copied/moved and recorded in the `files` table (dest path as PK). `--rehash` forces recomputation of all hashes ignoring the size/mtime cache.
- **`duplicates` subcommand**: Deletes files from `--outpath` whose SHA-256 hash matches any file in `--path`. Two-phase: (1) hash all supported files in `--path` via `IndexTask.ExecuteIndexAsync` (idempotent upsert via path PK; size/mtime cache avoids rehashing); (2) hash all supported files in `--outpath` and delete those found in the DB via `HashExistsAsync`. `--db <sqlite-file>` is **required**. Source files are never touched. Supports `--dryrun`, `--threads`, and `--rehash`.
- **`index` subcommand**: Iterates all files in `--path`, upserts each into the `files` DB table via `IndexTask.ExecuteIndexAsync` (insert new, update if hash changed, skip unchanged). `--db <sqlite-file>` is **required**. No `--dryrun` (always writes to DB). Supports `--threads` and `--rehash`. Reports `inserted`/`updated`/`unchanged`/`ignored`/`failed` counts.
- **Optional `--rehash` Flag** (process, organize, duplicates, index): Forces recomputation of SHA-256 for every file, ignoring the size/mtime cache. Useful after filesystem operations that preserve mtime but change content.
- **Program Construction**: Creates `Program` instance with primary constructor parameters passed via `CommandLine.Options`
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
- **PhotoCleanerTests Project**: 210 comprehensive tests covering all functionality
- **InternalsVisibleTo**: Enables direct testing of internal methods without reflection
- **Test Categories**:
  - `DateInferenceTests.cs`: Core date inference functionality (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Date inference edge cases and integration (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation (18 tests)
  - `ProcessTaskTests.cs`: Process task tests (65 tests)
- **Coverage Areas**: Date inference (filename patterns, path structures, validation), command line interface (parsing, validation, error handling, multiple paths, thread configuration and boundary validation), integration scenarios, process task execution, live photo detection (ContentIdentifier matching, `_hevc` suffix naming, mismatch/missing tag scenarios), metadata preservation through conversion

## Critical Implementation Details

### Video Conversion Logic
- **Three-tier approach**: Remux (.mts, .m2ts, .mkv) -> Re-encode (.wmv, .avi, .3gp, .gif) -> Audio-only (.mov/.mp4 with PCM)
- **Backup Strategy**: Original files renamed to `.bak` extension after successful conversion; `BackupFile()` returns the backup path. A `{backup}.out` companion file (e.g. `img.gif.bak.out`) is written alongside the backup containing the full output path - this is needed when `GetUniqueFileName` appended a counter suffix (e.g. `img_1.mp4`) because the canonical name was already taken. When `processContext.SkipBackup` is true, no `.bak` or `.bak.out` files are created - the original is deleted after conversion.
- **Metadata Preservation**: After every ffmpeg conversion, `exiftool -TagsFromFile <source.bak> <output> -all:all -overwrite_original` copies all source metadata to the output file. `ffmpeg -map_metadata` is not used - it is unreliable for Apple QuickTime-specific tags (e.g. `ContentIdentifier` in the `mdta`/`keys` atom). `TagsFromFile` handles cross-format date mapping, so no separate date-setting step is needed after conversion.
- **Re-queue Pattern**: Converted files are added back to processing queue for validation

### Live Photo Detection
- **Short videos** (<= `ShortVideoDuration` = 1.0s): always deleted regardless of companion file
- **Companion file search** (`FindCompanionImagePath()`): looks for a HEIC/JPG/JPEG file by:
  1. Direct basename match (`IMG_1234.mov` -> `IMG_1234.heic`)
  2. Basename minus `_hevc` suffix (`IMG_1234_HEVC.mov` -> `IMG_1234.heic`) - new iPhone naming
- **ContentIdentifier confirmation**: a candidate pair is only deleted when both files expose a `ContentIdentifier` tag that matches exactly. If either file lacks the tag, or the tags differ, the video is kept. There is no fallback to name-only deletion.
- **Long videos** (>= `LiveVideoDuration` = 4.0s): always kept even with a matching companion; a warning is logged

### Undo Architecture (UndoTask.cs)
- **Backup naming**: `X.bak` (first), `X.bak1`, `X.bak2`, ... (subsequent runs of `process`)
- **`GetFileList()`** in `Program.cs` enumerates all files including `.bak*` files before calling `ExecuteUndoAsync()`
- **Two-pass algorithm** in `UndoTask.ExecuteUndo()`:
  - *Pass 1 - Identify derived bases*:
    - **Rule 1**: any numbered backup (`.bak1`, `.bak2`, ...) present -> base is derived
    - **Rule 2**: `.mp4` base with same-stem non-`.mp4` primary backup in same dir -> base is derived
  - *Pass 2 - Act*:
    - Derived base: delete current file + all its backups
    - Non-derived base: delete current file if present, restore `X.bak` -> `X`; then locate the derived conversion output: if `X.bak.out` companion exists read the explicit output path from it and delete that file (handles uniquified names like `img_1.mp4`); otherwise fall back to checking whether `stem.mp4` exists and has no backup (legacy single-run heuristic)
- **Internal static helpers** (testable via `InternalsVisibleTo`):
  - `IsBackupFile(path)` - matches `.bak\d*$`
  - `IsNumberedBackup(path)` - matches `.bak\d+$`
  - `GetBackupBase(path)` - strips the `.bak\d*` suffix
- **Dry run**: logs all intended operations but performs no file I/O
- **Known limitation**: extension renames to a previously non-existent filename create no backup and cannot be undone

### Error Handling Strategy
- Console output uses structured prefixes: `WARNING:`, `INFORMATION:`
- External command failures throw `CommandExecutionException`
- Methods return `false` to skip file processing rather than throwing exceptions

## File Processing Extensions
Supported: `.3gp`, `.arw`, `.avi`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.m2ts`, `.mkv`, `.mov`, `.mp4`, `.mts`, `.nef`, `.orf`, `.png`, `.rw2`, `.tif`, `.tiff`, `.wmv`

## Command Line Usage
```bash
# Basic usage
PhotoCleaner process --path /photos

# Dry run mode
PhotoCleaner process --path /photos --dryrun

# Custom thread count
PhotoCleaner process --path /photos --threads 8

# Skip backup files (no .bak created, undo not possible)
PhotoCleaner process --path /photos --skipbackup

# Undo last process run
PhotoCleaner undo --path /photos
PhotoCleaner undo --path /photos --dryrun

# Cleanup: delete files not in the supported media list (junk, .bak artefacts, etc.)
PhotoCleaner cleanup --path /photos
PhotoCleaner cleanup --path /photos --dryrun

# Organize: copy media files into date-based subdirectories (default: copy)
PhotoCleaner organize --path /photos --outpath /organized
PhotoCleaner organize --path /photos --outpath /organized --format "yyyy/MM/dd"
PhotoCleaner organize --path /photos --outpath /organized --dryrun

# Organize with move (removes source files)
PhotoCleaner organize --path /photos --outpath /organized --move

# Organize with deduplication DB (skip files already organized)
PhotoCleaner organize --path /icloud/originals --outpath /intermediate --db /data/photos.db

# Full workflow: only copy new files from icloudpd directory, skip already-imported
PhotoCleaner organize --path /icloud/originals --outpath /intermediate --db /data/photos.db
PhotoCleaner process --path /intermediate
# import /intermediate to Immich
# subsequent runs: only new files from icloudpd are copied

# Index: build/update the source file hash index for use with duplicates
PhotoCleaner index --path /icloud/originals --db /data/photos.db
PhotoCleaner index --path /icloud/originals --db /data/photos.db --rehash

# Duplicates: delete files in /target whose content matches any file in /source
PhotoCleaner duplicates --path /source --outpath /target --db /data/photos.db
PhotoCleaner duplicates --path /source --outpath /target --db /data/photos.db --dryrun

# Incremental deduplication: index source once, then check multiple targets over time
PhotoCleaner index --path /icloud/originals --db /data/photos.db
PhotoCleaner duplicates --path /icloud/originals --outpath /import1 --db /data/photos.db
PhotoCleaner duplicates --path /icloud/originals --outpath /import2 --db /data/photos.db

# Help
PhotoCleaner --help
PhotoCleaner process --help
PhotoCleaner cleanup --help
PhotoCleaner index --help
PhotoCleaner duplicates --help
```

## JSON Source Generation
Uses `SourceGenerationContext` for AOT-compatible JSON serialization of `ExifToolJson` metadata.

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
