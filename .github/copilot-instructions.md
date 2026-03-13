# PhotoCleaner AI Coding Instructions

> For coding style, formatting rules, and conventions, see [`CODESTYLE.md`](../CODESTYLE.md).

## Project Overview
PhotoCleaner is a .NET 10 console application that processes media files in preparation for import into photo management systems (Lightroom, Immich, PhotoPrims). It analyzes and transforms media files through validation, modification, and verification phases.

## Architecture & Data Flow

### Project Structure
- **PhotoCleaner/**: Main console application
  - `Program.cs`: Entry point with logger setup
  - `CommandLine.cs`: System.CommandLine implementation for CLI parsing
  - `ProcessTask.cs`: Core file processing pipeline
  - `DateFromPath.cs`: Static utility class for date inference from filenames/paths
  - `ExifToolJson.cs`: JSON model for ExifTool metadata
  - `Extensions.cs`: Extension methods for logging and error handling
- **PhotoCleanerTests/**: Comprehensive test project with 143 tests
  - `DateInferenceTests.cs`: Core date inference functionality tests (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Edge cases and comprehensive scenarios (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation tests (30 tests including thread option)
  - `ProcessTaskTests.cs`: Process task tests (61 tests)

### Core Processing Pipeline

The application uses a sequential validation pipeline where each method returns `bool` —
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
- **Required `--path/-p` Parameter**: Accepts multiple directory paths using `Option<List<DirectoryInfo>>`. Each path is validated with `AcceptExistingOnly()`
- **Multiple Path Support**: Can be specified multiple times (e.g., `--path /dir1 --path /dir2`) to process multiple directories in a single run
- **Optional `--dryrun/-d` Flag**: Non-destructive preview mode
- **Optional `--threads/-t` Parameter**: Controls parallel processing degree with `DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 4)`. Validated to be > 0 and <= Environment.ProcessorCount using `Validators.Add()`
- **Optional `--datefrompath/-a` Flag**: Opt-in; when absent, `SetMissingCreateDateAsync` is skipped entirely — date inference from paths is a destructive write that cannot be undone
- **Program Construction**: Creates `Program` instance with primary constructor parameters passed via `CommandLine.Options`
- **Built-in Help System**: Automatic help generation and validation

## Development Workflow

See [`CODESTYLE.md`](../CODESTYLE.md) for build requirements, formatting commands, and tooling.

### Dependencies
- **CliWrap**: External process execution
- **System.CommandLine**: Modern CLI argument parsing and validation
- **System.Text.Json**: High-performance JSON with source generation
- **Serilog**: Structured logging with console output
- **Native AOT**: Project configured for `PublishAot=true` with `InvariantGlobalization=true`
- **xUnit**: Testing framework for PhotoCleanerTests project

### Test Architecture
- **PhotoCleanerTests Project**: 143 comprehensive tests covering all functionality
- **InternalsVisibleTo**: Enables direct testing of internal methods without reflection
- **Test Categories**:
  - `DateInferenceTests.cs`: Core date inference functionality (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Date inference edge cases and integration (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation (30 tests including multiple path and thread validation scenarios)
  - `ProcessTaskTests.cs`: Process task tests (61 tests)
- **Coverage Areas**: Date inference (filename patterns, path structures, validation), command line interface (parsing, validation, error handling, multiple paths, thread configuration and boundary validation), integration scenarios, process task execution, live photo detection (ContentIdentifier matching, `_hevc` suffix naming, mismatch/missing tag scenarios), metadata preservation through conversion

## Critical Implementation Details

### Video Conversion Logic
- **Three-tier approach**: Remux (.mts, .m2ts, .mkv) → Re-encode (.wmv, .avi, .3gp, .gif) → Audio-only (.mov/.mp4 with PCM)
- **Backup Strategy**: Original files renamed to `.bak` extension after successful conversion; `BackupFile()` returns the backup path
- **Metadata Preservation**: After every ffmpeg conversion, `exiftool -TagsFromFile <source.bak> <output> -all:all -overwrite_original` copies all source metadata to the output file. `ffmpeg -map_metadata` is not used — it is unreliable for Apple QuickTime-specific tags (e.g. `ContentIdentifier` in the `mdta`/`keys` atom). `TagsFromFile` handles cross-format date mapping, so no separate date-setting step is needed after conversion.
- **Re-queue Pattern**: Converted files are added back to processing queue for validation

### Live Photo Detection
- **Short videos** (≤ `ShortVideoDuration` = 1.0s): always deleted regardless of companion file
- **Companion file search** (`FindCompanionImagePath()`): looks for a HEIC/JPG/JPEG file by:
  1. Direct basename match (`IMG_1234.mov` → `IMG_1234.heic`)
  2. Basename minus `_hevc` suffix (`IMG_1234_HEVC.mov` → `IMG_1234.heic`) — new iPhone naming
- **ContentIdentifier confirmation**: a candidate pair is only deleted when both files expose a `ContentIdentifier` tag that matches exactly. If either file lacks the tag, or the tags differ, the video is kept. There is no fallback to name-only deletion.
- **Long videos** (≥ `LiveVideoDuration` = 4.0s): always kept even with a matching companion; a warning is logged

### Error Handling Strategy
- Console output uses structured prefixes: `WARNING:`, `INFORMATION:`
- External command failures throw `CommandExecutionException`
- Methods return `false` to skip file processing rather than throwing exceptions

## File Processing Extensions
Supported: `.3gp`, `.arw`, `.avi`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.m2ts`, `.mkv`, `.mov`, `.mp4`, `.mts`, `.nef`, `.orf`, `.png`, `.rw2`, `.tif`, `.tiff`, `.wmv`

## Command Line Usage
```bash
# Basic usage - single directory
PhotoCleaner --path /photos

# Multiple directories
PhotoCleaner --path /photos --path /backup/photos

# Multiple directories with short options
PhotoCleaner -p /photos -p /backup/photos -p /archive

# Dry run mode
PhotoCleaner --path /photos --dryrun

# Custom thread count
PhotoCleaner --path /photos --threads 8

# Short options
PhotoCleaner -p /photos -d

# All options combined
PhotoCleaner -p /photos -p /backup -d -t 12

# Help
PhotoCleaner --help
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
