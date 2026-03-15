# PhotoCleaner

A .NET console application that processes and prepares media files for import into photo management systems such as Lightroom, Immich, and PhotoPrism.

## Overview

PhotoCleaner analyzes and transforms media files through a validation pipeline that:

- **Renames mismatched extensions**: Corrects file extensions that do not match the actual file
  content (MIME type), normalizes to the preferred extension (e.g. `.jpeg` -> `.jpg`), and strips
  compound extensions (e.g. `photo.heic.jpg` -> `photo.jpg`).
- **Renames mixed-case extensions**: Converts uppercase or mixed-case extensions to lowercase
  (e.g. `.JPG` -> `.jpg`).
- **Handles Live Photos**: Removes Apple Live Photo video components - videos <= 1s are always
  removed; videos <= 4s with a candidate companion image (same basename, or basename with `_hevc`
  suffix stripped) are removed when both files share the same `ContentIdentifier` EXIF tag;
  longer videos with a matching image trigger a warning but are kept.
- **Converts video formats**: Remuxes MTS, M2TS, and MKV to MP4; re-encodes WMV, AVI, 3GP, and
  GIF to MP4 (H.264/AAC); re-encodes PCM audio to AAC in MOV and MP4 files while preserving
  the video stream. All source metadata (including `ContentIdentifier` and other QuickTime tags)
  is copied to the converted file using `exiftool -TagsFromFile`.
- **Sets missing creation dates** (opt-in via `--datefrompath`): Infers and writes
  EXIF/QuickTime creation dates from filenames or directory path structures when metadata is
  absent.
- **Organizes into date folders** (via `organize` command): Moves supported media files from
  source directories into `outpath/date/filename` using EXIF date metadata. Falls back to a
  deterministic `0001-01` bucket when no date is found. Supports a custom `--format` string
  (default `yyyy-MM`) validated as a date-only pattern.
- **Warns on DNG version**: Flags DNG files with a format version newer than v1.4 that may not
  render correctly in older applications.

Files that are renamed or converted are re-queued through the pipeline so every transformation
is validated. Original files are preserved with a `.bak` extension before any modification
(unless `--skipbackup` is used). The application processes files in parallel and provides
detailed logging of all operations.

## Usage

### Command Line Syntax

```text
$> PhotoCleaner --help
Description:
  PhotoCleaner - Pre-process media files for photo management systems.

Usage:
  PhotoCleaner [command] [options]

Commands:
  process   Process media files
  undo      Undo media file processing
  cleanup   Delete files not in the supported media list
  organize  Move media files into date-based subdirectories

Options:
  --loglevel <Debug|Error|Fatal|Information|Verbose|Warning>  Set the log level [default: Information]
  --logfile <logfile>                                         Write logs to the specified file
  --logclear                                                  Clear the log file before writing
  -?, -h, --help                                              Show help and usage information
  --version                                                   Show version information
```

```text
$> PhotoCleaner process --help
Description:
  Process media files

Options:
  --path <path> (REQUIRED)      The directory path to process (repeatable)
  --dryrun                      Perform a dry run without making changes
  --threads <threads>           Number of parallel threads [default: 4]
  --datefrompath                Set missing EXIF creation date from file path
  --skipbackup                  Skip creating backup files (disables undo)
```

```text
$> PhotoCleaner undo --help
Description:
  Undo media file processing

Options:
  --path <path> (REQUIRED)      The directory path to process (repeatable)
  --dryrun                      Perform a dry run without making changes
```

```text
$> PhotoCleaner cleanup --help
Description:
  Delete files not in the supported media list

Options:
  --path <path> (REQUIRED)      The directory path to process (repeatable)
  --dryrun                      Perform a dry run without making changes
```

```text
$> PhotoCleaner organize --help
Description:
  Move media files into date-based subdirectories

Options:
  --path <path> (REQUIRED)      The directory path to process (repeatable)
  --dryrun                      Perform a dry run without making changes
  --threads <threads>           Number of parallel threads [default: 4]
  --outpath <outpath> (REQUIRED) Output directory for organized files
  --format <format>             Date format for output subdirectory names [default: yyyy-MM]
  --deleteempty                 Delete empty source subdirectories after organizing
```

**Option notes:**

- `--path` - can be specified multiple times to process several directories in one run;
  must point to an existing directory.
- `--threads` - defaults to `min(CPU count, 4)`; must be `> 0` and `<= CPU count`.
- `--datefrompath` - opt-in; when absent, EXIF date inference from the file path is
  skipped entirely.
- `--skipbackup` - opt-in (`process` only); skips all `.bak` file creation. The `undo`
  command cannot reverse a run made with this flag.
- `--outpath` - required for `organize`; target directory (created on demand).
- `--format` - optional (`organize` only); a C# date format string used to name date
  subdirectories (default `"yyyy-MM"`). Must be date-only - time components are rejected.
  Files with no EXIF date land in a `"0001-01"` fallback bucket.
- `--deleteempty` - optional (`organize` only); after all files are moved, deletes empty
  child subdirectories from each source `--path` (deepest first). The source root itself is
  never deleted. Useful for cleaning up directory trees left behind after organizing.

### Examples

```bash
# Process multiple directories in one run
PhotoCleaner process --path /home/user/Photos --path /mnt/backup/Photos

# Preview what changes would be made without modifying files
PhotoCleaner process --path /home/user/Photos --dryrun

# Process with 8 parallel threads, log to file, infer missing created date from the path
PhotoCleaner process --path /home/user/Photos --threads 8 --logfile /tmp/photocleaner.log --datefrompath

# Process without creating backup files (faster, but undo is not possible)
PhotoCleaner process --path /home/user/Photos --skipbackup

# Undo all processing changes in a directory (restores .bak files)
PhotoCleaner undo --path /home/user/Photos

# Preview what undo would do without modifying files
PhotoCleaner undo --path /home/user/Photos --dryrun

# Remove all non-media files (.bak artefacts, .DS_Store, Thumbs.db, etc.)
PhotoCleaner cleanup --path /home/user/Photos

# Preview what cleanup would remove without deleting anything
PhotoCleaner cleanup --path /home/user/Photos --dryrun

# Organize media into date-based subdirectories (YYYY-MM by default)
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized

# Organize with a custom date format (creates e.g. 2024/06/15/ subdirectories)
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --format "yyyy/MM/dd"

# Preview what organize would move without changing anything
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --dryrun

# Organize and remove empty source subdirectories afterward
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --deleteempty
```

## Processing Flow

1. **File enumeration**: Recursively scans all specified directories.
2. **Case conflict detection**: Identifies files with the same name but different casing that
   would collide on case-insensitive file systems; renames conflicting files before processing.
3. **Per-file validation pipeline** (runs in parallel, stops on first action per file):
   1. Rename to canonical MIME extension - corrects mismatches and strips compound extensions.
   2. Rename mixed-case extension to lowercase.
   3. Delete short or Live Photo video clips: videos <= 1s are always deleted; videos <= 4s
      with a candidate companion image (direct name match or `_hevc`-suffix match) are deleted
      when both files share a matching `ContentIdentifier` tag.
   4. Convert legacy or incompatible video formats to MP4:
      - Remux: MTS, M2TS, MKV (stream copy, no quality loss)
      - Re-encode: WMV, AVI, 3GP, GIF (H.264 CRF 21 / AAC 128k)
      - Re-encode PCM audio: MOV, MP4 with PCM audio (AAC 128k, video stream copied)
      - After every conversion: all source metadata copied to output via `exiftool -TagsFromFile`
   5. Set missing EXIF/QuickTime creation date inferred from filename or directory path
      (only when `--datefrompath` is specified).
   6. Warn on DNG version > v1.4.
4. **Reprocess loop**: Any file that was renamed or converted is re-queued until stable.
5. **Results summary**: Reports counts of failed, modified, and successfully processed files;
   lists any unrecognized file extensions.

## Undo Flow

Every file modification or deletion made by `process` creates a `.bak` backup alongside the
original: the first backup is `X.bak`; if that already exists (from a prior run) the next is
`X.bak1`, then `X.bak2`, etc. The `undo` command reverses all processing by scanning the given
directories for backup files and applying a two-pass algorithm:

1. **Identify derived files** - an output file is "derived" (not original) when either:
   - a numbered backup (`.bak1`, `.bak2`, ...) exists for it (processed more than once), or
   - it is a `.mp4` file whose stem has a non-`.mp4` primary backup in the same directory
     (it was the conversion output).
2. **Restore or delete**:
   - *Derived* base: delete the current file and all its backup files.
   - *Non-derived* base: delete the current file if it exists (overwritten in-place), rename
     `X.bak` -> `X` to restore the original. The converted output is located via the
     `X.bak.out` companion file written at conversion time (handles uniquified names like
     `stem_1.mp4`); if no companion exists, falls back to deleting `stem.mp4` when present
     and untracked (legacy single-run heuristic).

**Known limitation**: extension renames that target a filename that did not previously exist
(e.g. `photo.JPEG` -> `photo.jpg` when `photo.jpg` was absent) create no backup and cannot be
undone by this command.

## Cleanup Flow

The `cleanup` command deletes every file in the target directories whose extension is **not** in
the supported media list. This removes processing artefacts (`.bak`, `.bak1`, `.bak.out`),
system junk (`.DS_Store`, `Thumbs.db`), and any other non-media files. Backup artefacts are
logged as warnings before deletion; other files are logged as informational.

Run `cleanup` after verifying `process` results, or use `process --skipbackup` followed by
`cleanup` for a no-artefact workflow.

## Organize Flow

The `organize` command moves every supported media file in the source directories to
`outpath/date/filename`:

1. **Date resolution**: reads EXIF metadata via `exiftool`. Uses `EXIF:DateTimeOriginal` or
   `QuickTime:CreateDate` (whichever is set). Falls back to `DateTime.MinValue` when no date
   is found - those files land in a `"0001-01"` bucket (with the default `yyyy-MM` format),
   making undated files easy to locate and handle manually.
2. **Subdirectory naming**: the date is formatted using `--format` (default `"yyyy-MM"`).
   The format is validated at startup - time components are rejected.
3. **Collision handling**: if a file with the same name already exists in the destination,
   `_1`, `_2`, ... suffixes are appended (e.g. `photo_1.jpg`). A warning is logged.
4. **Unsupported files**: non-media files are counted as ignored and left in place.
5. **Empty directory cleanup** (opt-in via `--deleteempty`): after all files are moved,
   iterates each source directory and deletes empty child subdirectories deepest-first.
   The source root itself is never deleted.

The `organize` command is non-destructive in the sense that it only *moves* files. Run with
`--dryrun` to preview the planned moves without touching the file system.

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

## Workflow Example

**Run [icloudpd](https://icloud-photos-downloader.github.io) to download photos from iCloud**:

```shell
#!/bin/bash

set -Eeuo pipefail

docker run -it --rm --name icloudpd \
    -v $(pwd)/.icloudpd:/cookies \
    -v /data/media/Test:/data \
    -e TZ=America/Los_Angeles \
    docker.io/icloudpd/icloudpd:latest \
    icloudpd \
        --cookie-directory /cookies \
        --username your@icloud.email \
        --directory /data \
        --set-exif-datetime \
        --folder-structure "{:%Y/%m}" \
        --recent 1000
        # --skip-created-before 2025-01-01
```

**Run [PhotoCleaner](https://github.com/ptr727/PhotoCleaner) to cleanup photos**:

```shell
#!/bin/bash

set -Eeuo pipefail

dotnet run --project ./PhotoCleaner/PhotoCleaner/PhotoCleaner.csproj -- \
    process \
    --path /data/media/Test \
    --threads 4
```

**Run [Immich CLI](https://docs.immich.app/features/command-line-interface/) to import photos into Immich**:"

```shell
#!/bin/bash

set -Eeuo pipefail

docker run -it --rm --name immichcli \
    -v /data/media/Test:/upload:ro \
    -e IMMICH_INSTANCE_URL=https://your.immich.server/api \
    -e IMMICH_API_KEY=yourapikey \
    -e TZ=America/Los_Angeles \
    ghcr.io/immich-app/immich-cli:latest \
    upload \
        --recursive \
        --concurrency 4 \
        --ignore "**/*.bak*" \
        --ignore "**/*.xmp" \
        --ignore "**/*.tmp" \
        --ignore "**/.DS_Store" \
        --ignore "**/Thumbs.db" \
        --ignore "**/@eaDir/**" \
        --ignore "**/._*" \
        /upload
```

**Run [immich-go](https://github.com/simulot/immich-go) to import photos into Immich**:"

```shell
#!/bin/bash

set -Eeuo pipefail

immich-go upload from-folder --server=https://your.immich.server \
    --api-key=yourapikey \
    --manage-raw-jpeg=StackCoverJPG \
    --manage-heic-jpeg=StackCoverJPG \
    --manage-burst=NoStack \
    --recursive \
    --concurrent-tasks=4 \
    --client-timeout=60m \
    --on-errors=continue \
    --include-extensions=.mp4,.mov,.tif,.jpg,.png,.dng,.heif,.heic \
    /data/media/Test
```

## License

Licensed under the [MIT License][license-link]\
![GitHub License][license-shield]

[license-link]: ./LICENSE
[license-shield]: https://img.shields.io/github/license/ptr727/PhotoCleaner?label=License
