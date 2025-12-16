# PhotoCleaner AI Coding Instructions

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
- **PhotoCleanerTests/**: Comprehensive test project with 69 tests
  - `DateInferenceTests.cs`: Core date inference functionality tests (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Edge cases and comprehensive scenarios (19 tests)
  - `CommandLineTests.cs`: Command line parsing and validation tests (17 tests)

### Core Processing Pipeline
The application uses a sequential validation pipeline where each method returns `bool` - `false` stops processing the current file:
```csharp
if (!await DetectDoubleExtensions()
    || !await DetectMixedCaseExtensions()
    || !await DetectMismatchedMimeExtension()
    || !await DeleteLivePhotos()
    || !await ConvertVideo()
    || !await DetectPcmAudio()
    || !await DetectMissingCreateDate())
```

### State Management Pattern
- **Shared Instance Variables**: `_fileNameBag`, `_unknownExtensionBag`, `_fileInfo`, `_exifToolJson` maintain state across the processing pipeline
- **Parallel Processing**: Files processed using `AsParallel().WithDegreeOfParallelism(_threadCount)`
- **External Tool Integration**: Uses `CliWrap` for all external command execution (exiftool, ffmpeg, ffprobe)

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
- **Extension Arrays**: Define supported extensions as `string[]` arrays (e.g., `remuxExtensions`, `jpegExtensions`)
- **Case-Insensitive Matching**: Always use `.ToLower()` for extension comparisons
- **File Type Categorization**: Group operations by file type requirements (remux vs re-encode vs audio-only)

### EXIF/Metadata Handling
- Uses `ExifToolJson` class with `JsonPropertyName` attributes for precise metadata field mapping
- Date validation prioritizes `EXIF:DateTimeOriginal` over `QuickTime:CreateDate`
- Custom `IsDateSet()` and `GetDateString()` methods handle metadata extraction logic

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
- **Required `--path/-p` Parameter**: Directory path validation with `DirectoryInfo`
- **Optional `--dryrun/-d` Flag**: Non-destructive preview mode
- **Built-in Help System**: Automatic help generation and validation

## Development Workflow

### Build & Format
```bash
dotnet build                    # Standard build
dotnet format --verify-no-changes --verbosity=detailed  # Uses .editorconfig settings
dotnet csharpier format --log-level=debug .
```

### Dependencies
- **CliWrap**: External process execution
- **System.CommandLine**: Modern CLI argument parsing and validation
- **System.Text.Json**: High-performance JSON with source generation
- **Serilog**: Structured logging with console output
- **Native AOT**: Project configured for `PublishAot=true` with `InvariantGlobalization=true`
- **xUnit**: Testing framework for PhotoCleanerTests project

### Test Architecture
- **PhotoCleanerTests Project**: 69 comprehensive tests covering all functionality
- **InternalsVisibleTo**: Enables direct testing of internal methods without reflection
- **Test Categories**:
  - `DateInferenceTests.cs`: Core date inference functionality (33 tests)
  - `DateInferenceEdgeCasesTests.cs`: Date inference edge cases and integration (19 tests) 
  - `CommandLineTests.cs`: Command line parsing and validation (17 tests)
- **Coverage Areas**: Date inference (filename patterns, path structures, validation), command line interface (parsing, validation, error handling), integration scenarios

## Critical Implementation Details

### Video Conversion Logic
- **Three-tier approach**: Remux (.mts, .m2ts) → Re-encode (.wmv, .avi) → Audio-only (.mov with PCM)
- **Backup Strategy**: Original files renamed to `.bak` extension after successful conversion
- **Metadata Preservation**: ExifTool sets QuickTime create/modify dates on converted files
- **Re-queue Pattern**: Converted files are added back to processing queue for validation

### Live Photo Detection
- **Duration-based**: Videos ≤0.5s are always flagged for deletion
- **HEIC Association**: Videos ≤3.0s with matching `.heic`/`.HEIC` files are flagged
- **Commented Deletions**: Actual `_fileInfo.Delete()` calls are commented for safety

### Error Handling Strategy
- Console output uses structured prefixes: `WARNING:`, `INFORMATION:`
- External command failures throw `CommandExecutionException`
- Methods return `false` to skip file processing rather than throwing exceptions

## File Processing Extensions
Supported: `.3gp`, `.arw`, `.avi`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.m2ts`, `.mkv`, `.mov`, `.mp4`, `.mts`, `.nef`, `.orf`, `.png`, `.rw2`, `.tif`, `.tiff`, `.wmv`

## Command Line Usage
```bash
# Basic usage
PhotoCleaner --path /photos

# Dry run mode
PhotoCleaner --path /photos --dryrun

# Short options
PhotoCleaner -p /photos -d

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
- **Type-based Option Extraction**: Identifies options by type (`Option<DirectoryInfo>`, `Option<bool>`) for reliability
- **Real Directory Testing**: Uses `Directory.GetCurrentDirectory()` and `Guid.NewGuid()` for path validation tests
- **Parse Result Validation**: Tests both success/error states and extracted argument values
- **Comprehensive Scenarios**: Option properties, argument parsing, validation errors, edge cases
