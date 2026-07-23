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
- **Imports into date folders** (via `import` command): Copies (default) or moves supported
  media files from source directories into `outpath/date/filename` using EXIF date metadata.
  Falls back to a deterministic `0001/01` bucket when no date is found. Supports a custom
  `--format` string (default `yyyy/MM/dd`) validated as a date-only pattern. SQLite
  deduplication via `--db` (Import.db) tracks source file hashes keyed by source path so
  re-runs skip already-imported files. `--tagpath` writes the source sub-directory path
  components as `XMP:Subject` tags on the destination file. `--tags` applies explicit
  comma-separated `XMP:Subject` tags to every imported file. `--datepath` infers and writes
  EXIF/QuickTime creation dates from filenames or directory path structures when metadata is
  absent (opt-in, applied before the file is moved to a date-based directory so the source
  path is still available).
- **Syncs Immich trash hashes** (via `trash` command): Connects to an Immich server via its
  REST API, fetches all trashed asset checksums (SHA-1), and stores them in a local SQLite
  database. This trash DB can then be used with `import --trashdb` to skip files that were
  already imported and trashed in Immich, and with `process --trashdb` to delete files trashed
  in Immich after upload, preventing re-import of known duplicates.
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
  process     Process media files
  undo        Undo media file processing
  import      Import media files into date-based subdirectories
  index       Index files into the database for deduplication tracking
  trash       Sync trashed asset hashes from Immich

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
  --path <path> (REQUIRED)      The directory path to process
  --dryrun                      Perform a dry run without making changes
  --threads <threads>           Number of parallel threads [default: 4]
  --skipbackup                  Skip creating backup files (disables undo)
  --deleteempty                 Delete empty subdirectories under the target directory after the command completes
  --db <db>                     SQLite database file for file state tracking
  --rehash                      Force rehashing of all files, ignoring size/mtime cache
  --duration <duration>         Maximum duration in seconds below which a video is considered a short clip and deleted [default: 1]
  --reprocess                   Re-process files even if already marked as processed in the database
  --trashdb <trashdb>           SQLite database with Immich trash hashes (read-only). Files matching are deleted from disk and the DB
```

```text
$> PhotoCleaner undo --help
Description:
  Undo media file processing

Options:
  --path <path> (REQUIRED)      The directory path to process
  --dryrun                      Perform a dry run without making changes
```

```text
$> PhotoCleaner import --help
Description:
  Import media files into date-based subdirectories

Options:
  --path <path> (REQUIRED)       The directory path to process
  --dryrun                       Perform a dry run without making changes
  --threads <threads>            Number of parallel threads [default: 4]
  --outpath <outpath> (REQUIRED) Output directory for organized files
  --format <format>              Date format for output subdirectory names [default: yyyy/MM/dd]
  --deleteempty                  Delete empty subdirectories under the target directory after the command completes
  --move                         Move files instead of copying (default: copy)
  --tagpath                      Apply path sub-directory components as XMP Subject tags to the organized file
  --tags <tags>                  Comma-separated XMP Subject tags to apply to every organized file (e.g. "vacation,family")
  --datepath                     Set missing EXIF creation date from file path
  --db <db>                      SQLite database file for file state tracking
  --rehash                       Force rehashing of all files, ignoring size/mtime cache
  --trashdb <trashdb>            SQLite database with Immich trash hashes (read-only)
  --skipdb <skipdb>              SQLite database with indexed files to be skipped (read-only)
```

```text
$> PhotoCleaner index --help
Description:
  Index files into the database for deduplication tracking

Options:
  --path <path> (REQUIRED)      The directory path to index
  --threads <threads>           Number of parallel threads [default: 4]
  --db <db> (REQUIRED)          SQLite database file for file state tracking
  --rehash                      Force rehashing of all files, ignoring size/mtime cache
  --processed                   Mark newly inserted rows as already processed (use when seeding a Process.db)
```

```text
$> PhotoCleaner trash --help
Description:
  Sync trashed asset hashes from Immich

Options:
  --url <url> (REQUIRED)         Immich server URL (e.g. http://immich:2283)
  --apikey <apikey>              Immich API key (mutually exclusive with --apikey-file)
  --apikey-file <apikey-file>    File containing the Immich API key (mutually exclusive with --apikey)
  --trashdb <trashdb> (REQUIRED) SQLite database to store Immich trash hashes
```

**Option notes:**

- `--path` - must point to an existing directory; accepts exactly one directory per command
  invocation.
- `--threads` - defaults to `min(CPU count, 4)`; must be `> 0` and `<= CPU count`.
- `--skipbackup` - opt-in (`process` only); skips all `.bak` file creation. The `undo`
  command cannot reverse a run made with this flag.
- `--outpath` - required for `import`; target directory (created on demand).
- `--format` - optional (`import` only); a C# date format string used to name date
  subdirectories (default `"yyyy/MM/dd"`). Must be date-only - time components are rejected.
  Files with no EXIF date land in a `"0001/01/01"` fallback bucket.
- `--deleteempty` - optional (`import`, `process`); after the command completes, deletes
  empty child subdirectories from the target directory (deepest first). For `import` the
  target is `--outpath`; for `process` it is `--path` (which is operated on in-place). The
  target root itself is never deleted. Useful for cleaning up directory trees left behind
  after `process` deletes files (live photos, originals when `--skipbackup`) or after
  pruning organized output.
- `--move` - optional (`import` only); moves files instead of copying. Default behavior is
  to copy, which preserves the source files. Use `--move` when the source directory is
  temporary.
- `--tagpath` - optional (`import` only); splits the source sub-directory path relative to
  `--path` into tokens and writes each token as an `XMP:Subject` tag on the destination file
  using exiftool. Files at the root of `--path` receive no tags. Tags are applied with a
  remove-then-add pattern (`-XMP:Subject-= / -XMP:Subject+=`) so existing tags are preserved
  and duplicates are not created. Only file types that support XMP writes are tagged.
- `--tags` - optional (`import` only); a comma-separated list of `XMP:Subject` tags applied to
  every organized file (e.g. `--tags "vacation,family trip,2018"`). Tags are applied using the
  same remove-then-add pattern as `--tagpath`. Can be combined with `--tagpath` - both sets of
  tags are merged. Only file types that support XMP writes are tagged.
- `--datepath` - optional (`import` only); when a file has no embedded creation date, infers
  one from the filename or directory path structure (via `DateFromPath`) and writes it to the
  destination file before restoring mtime. Opt-in because writing to files is destructive and
  the source path context is only available during `import` (before files move to date-based
  directories).
- `--db <path>` - optional for `import` and `process`, **required** for `index`; path to a
  SQLite database file. Uses a single `files` table (`path` PRIMARY KEY, `sha256`, `sha1`,
  `file_size`, `mtime_ticks`, `is_processed`). The schema is the same for every command, but
  the **meaning of the `path` column depends on which command writes the row**, so each
  pipeline stage gets its own DB file:
  - **Import.db** (`import --db Import.db`): rows are keyed by SOURCE path. `sha256` is the
    source content hash. Dedup query: skip a source whose `sha256` is already in this DB.
  - **Process.db** (`process --db Process.db`): rows are keyed by DEST path. `sha256` is the
    current dest content hash. `is_processed = 1` means the dest file's pipeline ran to completion.
    `--reprocess` ignores the flag.

  The DB file is created automatically on first use. **The two stages MUST use separate DB files**:
  if `import` and `process` shared one DB, `process` would overwrite the source-content hashes
  that `import` wrote (because `import` mutates the dest file via XMP tag injection, so the dest
  hash diverges from the source hash; `process` then re-hashes the dest and clobbers the row).
  This was a real bug; the per-stage layout is the fix.
- `--trashdb <path>` - **required** for `trash`, optional for `import` and `process`; path
  to a SQLite database with Immich trash hashes.
  - For `trash`: hashes are fetched from the Immich API and written to the database.
  - For `import`: files matching a trash hash are skipped (read-only). This is the durable
    "do not re-import" record beyond Immich's own ~30-day trash retention. Without it, a file
    the user trashed > 30 days ago can come back the next time icloudpd re-downloads it, because
    Immich no longer has the hash to deduplicate against on re-upload.
    Limitation: `import` compares the **source-file** SHA-1 against the trash DB. When
    `import` rewrites the destination via `--tags`, `--tagpath`, or `--datepath`, the dest file's
    SHA-1 differs from the source SHA-1. The hash Immich stored on a prior upload (and therefore
    the hash that ends up in the trash DB) is the rewritten dest SHA-1, so the source-side trash
    check will not match. Those files are caught by `process --trashdb` instead, which hashes the
    dest file directly.
  - For `process`: files matching a trash hash are deleted from disk and from Process.db. This
    cleans up files trashed in Immich after upload, before the next `immich-cli` upload would
    re-upload them. Also acts as the safety net for files that `import --trashdb` could not
    match because of the source-vs-dest SHA-1 drift described above.
- `--skipdb <path>` - optional for `import`; path to a SQLite database with indexed files
  to be skipped (read-only). Files matching a record in this DB are skipped without being
  recorded. Use to skip files already present in another collection.
- `--url <url>` - **required** for `trash`; the Immich server URL (e.g. `http://immich:2283`).
- `--apikey <key>` / `--apikey-file <path>` - the Immich API key for `trash`; supply it via
  exactly one of these two mutually exclusive options (one is required). Create the key in Immich
  under Account Settings > API Keys. `--apikey` passes the key inline; `--apikey-file` points to a
  file whose trimmed contents are the key, keeping the secret out of shell history and process
  arguments. The file must exist and be non-empty.
- `--rehash` - optional (`process`, `import`, `index`); forces SHA-256 recomputation for
  every file, bypassing the size/mtime cache. SHA-1 is also recomputed when `--trashdb` is
  in use. Use when file content may have changed without the modification timestamp being
  updated.
- `--duration` - optional (`process` only); overrides the short-video deletion threshold
  (default `1.0` seconds). Videos in a live-photo-compatible format whose duration is <= this
  value are always deleted. Must be `> 0`.
- `--reprocess` - optional (`process` only); when set, ignores the `is_processed` flag in
  the database and processes every file regardless of prior run history. Useful after changing
  pipeline settings (e.g. `--duration`) without wiping the database.
- `--processed` - optional (`index` only); marks newly inserted rows with `is_processed = 1`.
  Use this when seeding a Process.db from existing files so a subsequent `process` run treats
  them as already processed and only touches new arrivals. The flag does not flip the bit on
  rows that already exist in the DB.

### Examples

```bash
# Preview what changes would be made without modifying files
PhotoCleaner process --path /home/user/Photos --dryrun

# Process with 8 parallel threads and log to file
PhotoCleaner process --path /home/user/Photos --threads 8 --logfile /tmp/photocleaner.log

# Process without creating backup files (faster, but undo is not possible)
PhotoCleaner process --path /home/user/Photos --skipbackup

# Undo all processing changes in a directory (restores .bak files)
PhotoCleaner undo --path /home/user/Photos

# Preview what undo would do without modifying files
PhotoCleaner undo --path /home/user/Photos --dryrun

# Import (copy) media into date-based subdirectories - source files are kept
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized

# Import with a custom date format (creates e.g. 2024/06/2024-06-15/ subdirectories)
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --format "yyyy/MM/yyyy-MM-dd"

# Preview what organize would do without changing anything
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --dryrun

# Move instead of copy (source files are removed)
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --move

# Import and remove empty subdirectories from the target afterward
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --deleteempty

# Import with path-based XMP:Subject tagging (sub-directory names become tags)
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --tagpath

# Import inferring missing EXIF dates from the source path
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --datepath

# Import with explicit XMP:Subject tags applied to every file
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --tags "vacation,family trip"

# Import with both path tagging, explicit tags, and date inference
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --tagpath --tags "2018" --datepath

# Import with deduplication: only copy files not already in the database
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Intermediate --db /data/photos.db

# Index a directory into the database (stand-alone, no other processing)
PhotoCleaner index --path /home/user/Source --db /data/dedup.db

# Re-index forcing hash recomputation (useful after file content changes without mtime update)
PhotoCleaner index --path /home/user/Source --db /data/dedup.db --rehash

# Sync Immich trash hashes into a local database
PhotoCleaner trash --url http://immich:2283 --apikey YOUR_API_KEY --trashdb /data/trash.db

# Sync using an API key read from a file (keeps the secret out of shell history/process args)
PhotoCleaner trash --url http://immich:2283 --apikey-file /secrets/immich_api_key.txt --trashdb /data/trash.db

# Import and skip files that were trashed in Immich (prevents re-import)
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --db /data/photos.db --trashdb /data/trash.db

# Import and skip files already in another collection (read-only reference)
PhotoCleaner import --path /home/user/Photos --outpath /home/user/Organized --skipdb /data/existing-collection.db

# Full workflow with Immich trash integration
PhotoCleaner trash --url http://immich:2283 --apikey $IMMICH_KEY --trashdb /data/trash.db
PhotoCleaner import --path /home/user/iCloud --outpath /home/user/Intermediate --db /data/photos.db --trashdb /data/trash.db
PhotoCleaner process --path /home/user/Intermediate --db /data/process.db
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
   5. Warn on DNG version > v1.4.
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

## Import Flow

The `import` command copies (default) or moves every supported media file in the source
directories to `outpath/date/filename`:

1. **Date inference** (opt-in via `--datepath`): when no embedded creation date is found,
   infers one from the source file path using `DateFromPath` (filename patterns like
   `20210502_200152.jpg` or directory structures like `2021/05/02/`). The inferred date is
   written to the destination file (for supported types) and used for subdirectory placement.
   Applied before the file is moved so the original path is still available.
2. **Skip checks** (opt-in): before any file operation, the source file is hashed (SHA-256;
   SHA-1 is also computed when `--trashdb` is provided) and checked against up to three
   databases in order:
   - `--trashdb`: if the file matches a hash in the Immich trash DB, the file is skipped
     (counted as "trashed in Immich").
   - `--skipdb`: if the file matches a record in the reference DB, the file is skipped
     (counted as "skipped by reference"). This is a read-only check - no records are written.
   - `--db` (Import.db): if the source SHA-256 is already present (from a previous import
     run), the file is skipped (counted as "skipped"). Otherwise, the file is copied/moved and
     a record is inserted **keyed by the SOURCE path** (not the dest path) with the source
     hash, size, and mtime. This is the load-bearing detail: rows in Import.db identify
     sources, not destinations, so subsequent runs of `process`/`index` against a separate
     Process.db cannot clobber the dedup key. The DB file is created automatically on first use.
3. **Date resolution**: reads EXIF metadata via `exiftool`. Uses `EXIF:DateTimeOriginal` or
   `QuickTime:CreateDate` (whichever is set). Falls back to `DateTime.MinValue` when no date
   is found - those files land in a `"0001/01/01"` bucket (with the default `yyyy/MM/dd` format),
   making undated files easy to locate and handle manually.
4. **Subdirectory naming**: the date is formatted using `--format` (default `"yyyy/MM/dd"`).
   The format is validated at startup - time components are rejected.
5. **Copy or move**: by default files are copied and the source is preserved. Pass `--move`
   to remove the source file after a successful copy.
6. **Tagging**: `--tagpath` splits the source sub-directory path relative to `--path` into
   tokens and writes each as an `XMP:Subject` tag. `--tags` applies explicit comma-separated
   tags to every file. Both can be combined - tags are merged and deduplicated. Applied after
   copy/move, before mtime restore. Files at the root of `--path` receive no path tags.
7. **Collision handling**: if a file with the same name already exists in the destination,
   `_1`, `_2`, ... suffixes are appended (e.g. `photo_1.jpg`). A warning is logged.
8. **Unsupported files**: non-media files are counted as ignored and left in place.
9. **Empty directory cleanup** (opt-in via `--deleteempty`): after all files are imported,
   iterates `--outpath` and deletes empty child subdirectories deepest-first. The target
   root itself is never deleted.

Run with `--dryrun` to preview the planned operations without touching the file system.

## Trash Flow

The `trash` command syncs trashed asset checksums from an Immich server into a local SQLite
database. This enables the `import` and `process` commands to skip or delete files that
were already imported and trashed in Immich.

1. **Connect**: authenticates to the Immich server using the `--url` and the API key from either
   `--apikey` or `--apikey-file`.
2. **Fetch**: paginates through `POST /api/search/metadata` with a `trashedAfter` filter to
   retrieve all trashed assets. Each page returns up to 1000 assets.
3. **Store**: converts each asset's Base64-encoded SHA-1 checksum to lowercase hex and inserts
   it into the `trash_hashes` table using `INSERT OR IGNORE`. The operation is idempotent -
   running `trash` again safely adds any newly trashed assets.
4. **Report**: logs the total number of fetched assets, newly inserted hashes, and total
   hash count in the database.

The trash database is append-only. If an asset is restored (un-trashed) in Immich, its hash
remains in the database. Delete the database file and re-run `trash` to rebuild from scratch.

## Supported File Types

- **Images**: ARW, CR2, DNG, HEIC, HEIF, JPEG, JPG, NEF, ORF, PNG, PSD, RW2, TIF, TIFF
- **Videos**: 3GP, AVI, GIF, M2TS, MKV, MOV, MP4, MTS, WMV

## Docker

Build the image from the project root:

```bash
docker build -f Docker/Dockerfile -t photocleaner:latest .
```

Mount host directories as volumes so the container can access media files.
All `--path`, `--outpath`, `--db`, `--trashdb`, and `--skipdb` arguments refer to paths inside the container:

```bash
# Show help (default when no arguments are passed)
docker run --rm photocleaner:latest

# Process media files
docker run --rm -v /host/photos:/data \
    photocleaner:latest process --path /data

# Dry run - preview without modifying files
docker run --rm -v /host/photos:/data \
    photocleaner:latest process --path /data --dryrun

# Undo processing
docker run --rm -v /host/photos:/data \
    photocleaner:latest undo --path /data

# Import into date-based subdirectories (copy, source preserved)
docker run --rm \
    -v /host/photos:/source \
    -v /host/organized:/organized \
    photocleaner:latest organize --path /source --outpath /organized

# Import with deduplication DB (mount a persistent directory for the DB file)
docker run --rm \
    -v /host/photos:/source \
    -v /host/organized:/organized \
    -v /host/db:/db \
    photocleaner:latest organize --path /source --outpath /organized --db /db/photos.db

# Index a directory into the database
docker run --rm \
    -v /host/source:/source \
    -v /host/db:/db \
    photocleaner:latest index --path /source --db /db/dedup.db

# Sync Immich trash hashes
docker run --rm \
    -v /host/db:/db \
    photocleaner:latest trash --url http://immich:2283 --apikey YOUR_API_KEY --trashdb /db/trash.db

# Import with trash skip (prevents re-importing files trashed in Immich)
docker run --rm \
    -v /host/photos:/source \
    -v /host/organized:/organized \
    -v /host/db:/db \
    photocleaner:latest organize --path /source --outpath /organized --db /db/photos.db --trashdb /db/trash.db
```

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

**Run [PhotoCleaner](https://github.com/ptr727/PhotoCleaner) to sync Immich trash hashes** (optional, prevents re-importing trashed files):

```shell
#!/bin/bash

set -Eeuo pipefail

docker run --rm \
    -v /data/media:/db \
    photocleaner:latest \
    trash \
    --url http://immich:2283 \
    --apikey $IMMICH_API_KEY \
    --trashdb /db/trash.db
```

**Run [PhotoCleaner](https://github.com/ptr727/PhotoCleaner) to organize new photos**:

Copy only new files (not already in the DB and not trashed in Immich) from the icloudpd
directory to an intermediate directory, without touching the icloudpd originals:

```shell
#!/bin/bash

set -Eeuo pipefail

docker run --rm \
    -v /data/media/icloud:/icloud \
    -v /data/media/intermediate:/intermediate \
    -v /data/media:/db \
    photocleaner:latest \
    organize \
    --path /icloud \
    --outpath /intermediate \
    --db /db/photos.db \
    --trashdb /db/trash.db \
    --threads 4
```

**Run [PhotoCleaner](https://github.com/ptr727/PhotoCleaner) to process the intermediate photos**:

```shell
#!/bin/bash

set -Eeuo pipefail

docker run --rm \
    -v /data/media/intermediate:/intermediate \
    photocleaner:latest \
    process \
    --path /intermediate \
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
