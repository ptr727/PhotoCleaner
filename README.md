# PhotoCleaner

A .NET console application that processes and prepares media files for import into photo management systems such as Lightroom, Immich, and PhotoPrism.

## Overview

PhotoCleaner analyzes and transforms media files through a validation pipeline that:

- **Renames mismatched extensions**: Corrects file extensions that do not match the actual file
  content (MIME type), normalises to the preferred extension (e.g. `.jpeg` → `.jpg`), and strips
  compound extensions (e.g. `photo.heic.jpg` → `photo.jpg`).
- **Renames mixed-case extensions**: Converts uppercase or mixed-case extensions to lowercase
  (e.g. `.JPG` → `.jpg`).
- **Handles Live Photos**: Removes Apple Live Photo video components — videos ≤ 1s are always
  removed; videos ≤ 4s with a matching HEIC or JPEG are removed; longer videos with a matching
  image trigger a warning but are kept.
- **Converts video formats**: Remuxes MTS, M2TS, and MKV to MP4; re-encodes WMV, AVI, 3GP, and
  GIF to MP4 (H.264/AAC); re-encodes PCM audio to AAC in MOV and MP4 files while preserving
  the video stream.
- **Sets missing creation dates** (opt-in via `--datefrompath`): Infers and writes
  EXIF/QuickTime creation dates from filenames or directory path structures when metadata is
  absent.
- **Warns on DNG version**: Flags DNG files with a format version newer than v1.4 that may not
  render correctly in older applications.

Files that are renamed or converted are re-queued through the pipeline so every transformation
is validated. Original files are preserved with a `.bak` extension before any modification.
The application processes files in parallel and provides detailed logging of all operations.

## Usage

### Command Line Syntax

```text
$> PhotoCleaner --help
Description:
  PhotoCleaner - Pre-process media files for photo management systems.

Usage:
  PhotoCleaner [options]

Options:
  -p, --path <path> (REQUIRED)                                    The directory path to process (repeatable)
  -d, --dryrun                                                    Perform a dry run without making changes
  -t, --threads <threads>                                         Number of parallel threads [default: 4]
  -a, --datefrompath                                              Set missing EXIF creation date from file path
  -l, --loglevel <Debug|Error|Fatal|Information|Verbose|Warning>  Set the log level [default: Information]
  -f, --logfile <logfile>                                         Write logs to the specified file
  -c, --logclear                                                  Clear the log file before writing
  -?, -h, --help                                                  Show help and usage information
  --version                                                       Show version information
```

**Option notes:**

- `--path` / `-p` — can be specified multiple times to process several directories in one run;
  must point to an existing directory.
- `--threads` / `-t` — defaults to `min(CPU count, 4)`; must be `> 0` and `<= CPU count`.
- `--datefrompath` / `-a` — opt-in; when absent, EXIF date inference from the file path is
  skipped entirely.

### Examples

```bash
# Process multiple directories in one run
PhotoCleaner --path /home/user/Photos --path /mnt/backup/Photos

# Preview what changes would be made without modifying files
PhotoCleaner --path /home/user/Photos --dryrun

# Process with 8 parallel threads, log to file, infer missing created date from the path
PhotoCleaner --path /home/user/Photos --threads 8 --logfile /tmp/photocleaner.log --datefrompath
```

## Processing Flow

1. **File enumeration**: Recursively scans all specified directories.
2. **Case conflict detection**: Identifies files with the same name but different casing that
   would collide on case-insensitive file systems; renames conflicting files before processing.
3. **Per-file validation pipeline** (runs in parallel, stops on first action per file):
   1. Rename to canonical MIME extension — corrects mismatches and strips compound extensions.
   2. Rename mixed-case extension to lowercase.
   3. Delete short or Live Photo video clips (≤ 1s always; ≤ 4s with matching HEIC/JPEG).
   4. Convert legacy or incompatible video formats to MP4:
      - Remux: MTS, M2TS, MKV (stream copy, no quality loss)
      - Re-encode: WMV, AVI, 3GP, GIF (H.264 CRF 21 / AAC 128k)
      - Re-encode PCM audio: MOV, MP4 with PCM audio (AAC 128k, video stream copied)
   5. Set missing EXIF/QuickTime creation date inferred from filename or directory path
      (only when `--datefrompath` is specified).
   6. Warn on DNG version > v1.4.
4. **Reprocess loop**: Any file that was renamed or converted is re-queued until stable.
5. **Results summary**: Reports counts of failed, modified, and successfully processed files;
   lists any unrecognised file extensions.

## Supported File Types

- **Images**: ARW, CR2, DNG, HEIC, HEIF, JPEG, JPG, NEF, ORF, PNG, PSD, RW2, TIF, TIFF
- **Videos**: 3GP, AVI, GIF, M2TS, MKV, MOV, MP4, MTS, WMV

## Development Tooling

### Install

#### Windows

```shell
winget install Microsoft.DotNet.SDK.10
winget install Microsoft.VisualStudioCode
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

## Workflow

### iCloud Photos Downloader

Run [icloudpd](https://icloud-photos-downloader.github.io) from Docker:

```shell
#!/bin/bash

set -Eeuo pipefail

docker run -it --rm --name icloudpd \
    -v $(pwd)/.icloudpd:/cookies \
    -v /data/media/TestiCloud:/data \
    -e TZ=America/Los_Angeles \
    icloudpd/icloudpd:latest \
    icloudpd \
        --cookie-directory /cookies \
        --username your@icloud.email \
        --directory /data \
        --set-exif-datetime \
        --folder-structure "{:%Y/%m}" \
        --recent 1000
        # --skip-created-before 2025-01-01
```

Run PhotoCleaner from source:

```shell
#!/bin/bash

set -Eeuo pipefail

# https://github.com/ptr727/PhotoCleaner

dotnet run --project PhotoCleaner/PhotoCleaner.csproj -- \
    --path /data/media/TestiCloud \
    --threads 4
```

## License

Licensed under the [MIT License][license-link]\
![GitHub License][license-shield]

[license-link]: ./LICENSE
[license-shield]: https://img.shields.io/github/license/ptr727/PhotoCleaner?label=License
