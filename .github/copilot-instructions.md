# PhotoCleaner AI Coding Instructions

## Project Overview
PhotoCleaner is a .NET 10 console application that processes media files in preparation for import into photo management systems (Lightroom, Immich, PhotoPrims). It analyzes and transforms media files through validation, modification, and verification phases.

## Architecture & Data Flow

### Core Processing Pipeline
The application uses a sequential validation pipeline where each method returns `bool` - `false` stops processing the current file:
```csharp
if (!await DetectDoubleExtensions()
    || !await DetectMismatchedMimeExtension()
    || !await DeleteLivePhotos()
    || !await ConvertVideo()
    || !await DetectPcmAudio()
    || !await DetectMissingCreateDate())
```

### State Management Pattern
- **Shared Instance Variables**: `_fileInfoQueue`, `_fileInfo`, `_exifToolJson` maintain state across the processing pipeline
- **Queue-based Processing**: Files are processed from `Queue<FileInfo>` with new files added during processing (see `ConvertVideo()`)
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

## Development Workflow

### Build & Format
```bash
dotnet build                    # Standard build
dotnet format --verify-no-changes --verbosity=detailed  # Uses .editorconfig settings
dotnet csharpier format --log-level=debug .
```

### Dependencies
- **CliWrap**: External process execution
- **System.Text.Json**: High-performance JSON with source generation
- **Native AOT**: Project configured for `PublishAot=true` with `InvariantGlobalization=true`

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
Supported: `.jpg`, `.jpeg`, `.png`, `.heic`, `.mp4`, `.mov`, `.mts`, `.m2ts`, `.wmv`, `.avi`

## JSON Source Generation
Uses `SourceGenerationContext` for AOT-compatible JSON serialization of `ExifToolJson` metadata.
