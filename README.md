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
PhotoCleaner --path <directory> [--dryrun]
```

### Options

- `--path, -p <directory>` - **Required**. The directory path containing media files to process
- `--dryrun, -d` - **Optional**. Perform a dry run without making any actual changes
- `--help, -h` - Display help information

### Examples

```bash
# Process all media files in the Photos directory
PhotoCleaner --path /home/user/Photos

# Preview what changes would be made without actually modifying files
PhotoCleaner --path /home/user/Photos --dryrun

# Using short options
PhotoCleaner -p /home/user/Photos -d
```

## Supported File Types

- **Images**: JPG, JPEG, PNG, HEIC, HEIF, TIFF, TIF, CR2, NEF, ARW, ORF, RW2, DNG
- **Videos**: MP4, MOV, MTS, M2TS, MKV, AVI, WMV, 3GP
- **Other**: GIF

## Development Tooling

### Fresh Install

```shell
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.VisualStudioCode
winget install nektos.act
winget install Gyan.FFmpeg
winget install OliverBetz.ExifTool
```

```shell
dotnet new tool-manifest
dotnet tool install csharpier
dotnet tool install husky
dotnet tool install dotnet-outdated-tool
dotnet husky install
dotnet husky add pre-commit -c "dotnet husky run"
```

### Update Dependencies

```shell
winget upgrade Microsoft.DotNet.SDK.10
winget upgrade Microsoft.VisualStudioCode
winget upgrade nektos.act
winget upgrade Gyan.FFmpeg
winget upgrade OliverBetz.ExifTool
```

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
