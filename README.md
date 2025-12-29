# PhotoCleaner

A .NET console application that processes and prepares media files for import into photo management systems such as Lightroom, Immich, and PhotoPrims.

## Overview

PhotoCleaner analyzes and transforms media files through a comprehensive validation pipeline that:

- **Detects and reports file issues**: Double extensions, mixed case extensions, mismatched MIME types
- **Handles Live Photos**: Identifies and manages Apple Live Photo video components
- **Converts video formats**: Transforms legacy video formats (MTS, M2TS, WMV, AVI) to MP4
- **Processes audio**: Detects PCM audio in video files that may need conversion
- **Manages metadata**: Infers creation dates from filenames and directory structures when EXIF data is missing
- **Validates media integrity**: Ensures files are properly formatted for photo management systems

The application processes files in parallel and provides detailed logging of all operations, making it easy to track what changes were made during processing.

## Usage

### Command Line Syntax

```bash
PhotoCleaner --path <directory> [--path <directory> ...] [--dryrun] [--threads <count>]
```

### Options

- `--path, -p <directory>` - **Required**. One or more directory paths containing media files to process. Can be specified multiple times to process multiple directories in a single run.
- `--dryrun, -d` - **Optional**. Perform a dry run without making any actual changes
- `--threads, -t <count>` - **Optional**. Number of parallel threads to use for processing (default: Max(ProcessorCount, 4), must be 1-ProcessorCount)
- `--help, -h` - Display help information

### Examples

```bash
# Process all media files in a single directory
PhotoCleaner --path /home/user/Photos

# Process multiple directories in one run
PhotoCleaner --path /home/user/Photos --path /mnt/backup/Photos

# Process multiple directories using short options
PhotoCleaner -p /home/user/Photos -p /mnt/backup/Photos -p /media/Archive

# Preview what changes would be made without actually modifying files
PhotoCleaner --path /home/user/Photos --dryrun

# Using short options with dry run
PhotoCleaner -p /home/user/Photos -d

# Process with custom thread count
PhotoCleaner --path /home/user/Photos --threads 8

# All options combined
PhotoCleaner -p /home/user/Photos -p /mnt/backup -d -t 12
```

## Processing Flow

1. **File Enumeration**: Recursively scans all specified directories to build a list of files to process
2. **Case Conflict Detection**: Identifies files with similar names but different cases that may cause issues
3. **Parallel Processing**: Processes files in parallel (based on CPU core count) through the validation pipeline:
   - Detects double file extensions (e.g., `.jpg.jpg`)
   - Detects mixed case extensions (e.g., `.JpG`)
   - Detects MIME type mismatches between file content and extension
   - Identifies and handles Apple Live Photo components
   - Converts legacy video formats (MTS, M2TS) via remuxing to MP4
   - Converts incompatible video formats (WMV, AVI) via re-encoding to MP4
   - Detects and reports PCM audio in MOV files
   - Validates EXIF creation dates, inferring from filenames/paths when missing
4. **Results Summary**: Reports counts of failed, modified, and successfully processed files

## Supported File Types

- **Images**: JPG, JPEG, PNG, HEIC, HEIF, TIFF, TIF, CR2, NEF, ARW, ORF, RW2, DNG
- **Videos**: MP4, MOV, MTS, M2TS, MKV, AVI, WMV, 3GP
- **Other**: GIF

## Development Tooling

### Install

#### Windows

```shell
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.VisualStudioCode
winget install nektos.act
winget install Gyan.FFmpeg
winget install OliverBetz.ExifTool
```

#### Linux

```shell
apt install dotnet-sdk-10.0
apt install ffmpeg
apt install exiftool
```

#### .NET Tools

```shell
dotnet new tool-manifest
dotnet tool install csharpier
dotnet tool install husky
dotnet tool install dotnet-outdated-tool
dotnet husky install
dotnet husky add pre-commit -c "dotnet husky run"
```

### Update

#### Windows

```shell
winget upgrade --all --accept-package-agreements --include-unknown
```

#### Linux

```shell
apt update
apt upgrade
```

#### .NET Tools

```shell
dotnet tool restore
dotnet tool update --all
dotnet outdated --upgrade:prompt
```

## License

Licensed under the [MIT License][license-link]\
![GitHub License][license-shield]

[license-link]: ./LICENSE
[license-shield]: https://img.shields.io/github/license/ptr727/PhotoCleaner?label=License
