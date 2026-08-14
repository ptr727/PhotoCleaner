# PhotoCleaner <!-- omit from toc -->

Utility to prepare photos and videos for import into photo managers.

## Build and Distribution <!-- omit from toc -->

- **Source Code**: [GitHub][github-link] for source, issues, and the CI/CD pipelines.
- **Versioned Releases**: [GitHub Releases][releases-link] for pre-compiled executables for Windows, Linux, and macOS.
- **Docker Images**: [Docker Hub][docker-hub-link] for container images with all tools pre-installed.

### Build Status <!-- omit from toc -->

[![Release Status][release-status-shield]][actions-link]\
[![Docker Status][docker-status-shield]][actions-link]\
[![Last Commit][last-commit-shield]][commits-link]

### Releases <!-- omit from toc -->

[![GitHub Release][release-version-shield]][releases-link]\
[![GitHub Pre-Release][pre-release-version-shield]][releases-link]\
[![Docker Latest][docker-latest-version-shield]][docker-hub-link]\
[![Docker Develop][docker-develop-version-shield]][docker-hub-link]

### Release Notes <!-- omit from toc -->

**Version 1.1**:

**Summary**:

- Added `verify` command to detect possibly corrupt images, e.g. Immich fails to generate a preview image.
- Added `-validate` to exiftool to report metadata warnings and errors.

> **Breaking**: commands now exit `2` when they complete with per-file failures.

See [Release History][history] for complete release notes and older versions.

## Table of Contents <!-- omit from toc -->

- [Overview](#overview)
- [Usage](#usage)
  - [Command Line Syntax](#command-line-syntax)
  - [Exit Codes](#exit-codes)
  - [Examples](#examples)
- [Processing Flow](#processing-flow)
- [Undo Flow](#undo-flow)
- [Import Flow](#import-flow)
- [Trash Flow](#trash-flow)
- [Verify Flow](#verify-flow)
- [Supported File Types](#supported-file-types)
- [Docker](#docker)
- [Workflow Example](#workflow-example)
- [Questions or Issues](#questions-or-issues)
- [Development Environment Setup](#development-environment-setup)
  - [Install](#install)
  - [Update](#update)

## Overview

PhotoCleaner analyzes and transforms media files through a validation pipeline that:

- **Renames mismatched extensions**: Corrects file extensions that do not match the actual file
  content (MIME type), normalizes to the preferred extension (e.g. `.jpeg` -> `.jpg`), and strips
  compound extensions (e.g. `photo.heic.jpg` -> `photo.jpg`).
- **Renames mixed-case extensions**: Converts uppercase or mixed-case extensions to lowercase
  (e.g. `.JPG` -> `.jpg`).
- **Handles Live Photos**: Removes Apple Live Photo video components. Videos <= 1s are always
  removed. Videos <= 4s with a candidate companion image (same basename, or basename with `_hevc`
  suffix stripped) are removed when both files share the same `ContentIdentifier` EXIF tag.
  Longer videos with a matching image trigger a warning but are kept.
- **Converts video formats**: Remuxes MTS, M2TS, and MKV to MP4, re-encodes WMV, AVI, 3GP, and
  GIF to MP4 (H.264/AAC), and re-encodes PCM audio to AAC in MOV and MP4 files while preserving
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
- **Verifies files render in Immich** (via `verify` command): Answers whether a file will survive
  Immich's preview generation, which metadata checks cannot predict. Immich's own thumbnail
  pipeline is run inside the `immich-server` image, so the verdict is the one Immich will reach
  rather than an approximation of it. That decoder is the sole authority: PhotoCleaner does not
  parse container formats itself, because a format it did not recognize would be indistinguishable
  from a damaged one. Docker is required. Nothing is modified, and `verify` only ever reads.
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
  PhotoCleaner - Utility to prepare photos and videos for import into photo managers.

Usage:
  PhotoCleaner [command] [options]

Commands:
  process     Process media files
  undo        Undo media file processing
  import      Import media files into date-based subdirectories
  index       Index files into the database for deduplication tracking
  trash       Sync trashed asset hashes from Immich
  verify      Verify that media files can be rendered by Immich

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
  --path <path> (REQUIRED)      The media directory path
  --dryrun                      Perform a dry run without making changes
  --threads <threads>           Number of parallel threads [default: 4]
  --skipbackup                  Skip creating backup files (disables undo)
  --deleteempty                 Delete empty subdirectories under the target directory after the command completes
  --db <db>                     SQLite database file for file state tracking
  --rehash                      Force rehashing of all files, ignoring size/mtime cache
  --duration <duration>         Maximum duration in seconds below which a video is considered a short clip and deleted [default: 1]
  --reprocess                   Re-run every file even if the database marks it done
  --trashdb <trashdb>           SQLite database with Immich trash hashes (read-only)
```

```text
$> PhotoCleaner undo --help
Description:
  Undo media file processing

Options:
  --path <path> (REQUIRED)      The media directory path
  --dryrun                      Perform a dry run without making changes
```

```text
$> PhotoCleaner import --help
Description:
  Import media files into date-based subdirectories

Options:
  --path <path> (REQUIRED)       The media directory path
  --dryrun                       Perform a dry run without making changes
  --threads <threads>            Number of parallel threads [default: 4]
  --outpath <outpath> (REQUIRED) Output directory for organized files
  --format <format>              Date format for output subdirectory names; use '/' to create nested subdirectories (e.g. yyyy/MM/dd) [default: yyyy/MM/dd]
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
  --path <path> (REQUIRED)      The media directory path
  --threads <threads>           Number of parallel threads [default: 4]
  --db <db> (REQUIRED)          SQLite database file for file state tracking
  --rehash                      Force rehashing of all files, ignoring size/mtime cache
  --processed                   Mark newly inserted rows as already processed (use when seeding a Process.db from existing files)
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

```text
$> PhotoCleaner verify --help
Description:
  Verify that media files can be rendered by Immich

Options:
  --path <path> (REQUIRED)      The media directory path
  --threads <threads>           Number of parallel threads [default: 4]
  --db <db>                     SQLite database file for file state tracking
  --rehash                      Force rehashing of all files, ignoring size/mtime cache
  --reprocess                   Re-run every file even if the database marks it done
```

**Option notes:**

- `--path`: must point to an existing directory. Accepts exactly one directory per command
  invocation.
- `--threads`: defaults to `min(CPU count, 4)`. Must be `> 0` and `<= CPU count`.
- `--skipbackup`: opt-in (`process` only). Skips all `.bak` file creation. The `undo`
  command cannot reverse a run made with this flag.
- `--outpath`: required for `import`. Target directory (created on demand).
- `--format`: optional (`import` only). A C# date format string used to name date
  subdirectories (default `"yyyy/MM/dd"`). Must be date-only, so time components are rejected.
  Files with no EXIF date land in a `"0001/01/01"` fallback bucket.
- `--deleteempty`: optional (`import`, `process`). After the command completes, deletes
  empty child subdirectories from the target directory (deepest first). For `import` the
  target is `--outpath`, and for `process` it is `--path` (which is operated on in-place). The
  target root itself is never deleted. Useful for cleaning up directory trees left behind
  after `process` deletes files (live photos, originals when `--skipbackup`) or after
  pruning organized output.
- `--move`: optional (`import` only). Moves files instead of copying. Default behavior is
  to copy, which preserves the source files. Use `--move` when the source directory is
  temporary.
- `--tagpath`: optional (`import` only). Splits the source sub-directory path relative to
  `--path` into tokens and writes each token as an `XMP:Subject` tag on the destination file
  using exiftool. Files at the root of `--path` receive no tags. Tags are applied with a
  remove-then-add pattern (`-XMP:Subject-= / -XMP:Subject+=`) so existing tags are preserved
  and duplicates are not created. Only file types that support XMP writes are tagged.
- `--tags`: optional (`import` only). A comma-separated list of `XMP:Subject` tags applied to
  every organized file (e.g. `--tags "vacation,family trip,2018"`). Tags are applied using the
  same remove-then-add pattern as `--tagpath`. Can be combined with `--tagpath`, and both sets of
  tags are merged. Only file types that support XMP writes are tagged.
- `--datepath`: optional (`import` only). When a file has no embedded creation date, infers
  one from the filename or directory path structure (via `DateFromPath`) and writes it to the
  destination file before restoring mtime. Opt-in because writing to files is destructive and
  the source path context is only available during `import` (before files move to date-based
  directories).
- `--db <path>`: optional for `import` and `process`, **required** for `index`. Path to a
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
  hash diverges from the source hash, and `process` then re-hashes the dest and clobbers the row).
  This was a real bug, and the per-stage layout is the fix.
- `--trashdb <path>`: **required** for `trash`, optional for `import` and `process`. Path
  to a SQLite database with Immich trash hashes.
  - For `trash`: hashes are fetched from the Immich API and written to the database.
  - For `import`: files matching a trash hash are skipped (read-only). This is the durable
    "do not re-import" record beyond Immich's own ~30-day trash retention. Without it, a file
    the user trashed > 30 days ago can come back the next time the downloader re-fetches it, because
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
- `--skipdb <path>`: optional for `import`. Path to a SQLite database with indexed files
  to be skipped (read-only). Files matching a record in this DB are skipped without being
  recorded. Use to skip files already present in another collection.
- `--url <url>`: **required** for `trash`. The Immich server URL (e.g. `http://immich:2283`).
- `--apikey <key>` / `--apikey-file <path>`: the Immich API key for `trash`. Supply it via
  exactly one of these two mutually exclusive options (one is required). Create the key in Immich
  under Account Settings > API Keys. `--apikey` passes the key inline, while `--apikey-file` points to a
  file whose trimmed contents are the key, keeping the secret out of shell history and process
  arguments. The file must exist and be non-empty.
- `--rehash`: optional (`process`, `import`, `index`). Forces SHA-256 recomputation for
  every file, bypassing the size/mtime cache. SHA-1 is also recomputed when `--trashdb` is
  in use. Use when file content may have changed without the modification timestamp being
  updated.
- `--duration`: optional (`process` only). Overrides the short-video deletion threshold
  (default `1.0` seconds). Videos in a live-photo-compatible format whose duration is <= this
  value are always deleted. Must be `> 0`.
- `--reprocess`: optional (`process`, `verify`). When set, ignores the `is_processed` flag in
  the database and processes every file regardless of prior run history. Useful after changing
  pipeline settings (e.g. `--duration`) without wiping the database.
- `--processed`: optional (`index` only). Marks newly inserted rows with `is_processed = 1`.
  Use this when seeding a Process.db from existing files so a subsequent `process` run treats
  them as already processed and only touches new arrivals. The flag does not flip the bit on
  rows that already exist in the DB.

### Exit Codes

Every command uses the same three codes, so a pipeline can branch on the result without parsing
logs:

| Code | Meaning |
| ---- | ------- |
| `0` | Success. The command completed and every file succeeded. |
| `1` | Error. The command could not complete: unhandled exception, fatal configuration error, cancellation, or a failed `verify` preflight. |
| `2` | Completed with failures. The command ran to completion, but one or more files failed or failed verification, or `trash` synced only part of the server. |

The distinction between `1` and `2` matters most for `verify`. A `1` means the check itself could
not run at all, because Docker was unreachable or the Immich image could not be prepared, and it
says nothing about any file. A `2` means the run completed and one or more files were invalid **or could
not be verified**, the latter covering a file that could not be read or that no verdict came back
for. A script must never treat an infrastructure failure as a verdict on the collection, and should
read the `Invalid` and `Failed` counts to tell a bad file from a gap in coverage.

`trash` exits `2` when pagination stops early, which leaves the trash database holding fewer
hashes than the server. That database is used by `import --trashdb` and `process --trashdb` to skip
files, so a short one silently re-imports assets that were trashed, and a pipeline gating on the
exit code should not go on to upload against it.

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

# Preview what import would do without changing anything
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

# Verify that Immich can render every file, before uploading
PhotoCleaner verify --path /home/user/Intermediate --db /data/verify.db

# Full workflow with Immich trash integration
PhotoCleaner trash --url http://immich:2283 --apikey $IMMICH_KEY --trashdb /data/trash.db
PhotoCleaner import --path /home/user/iCloud --outpath /home/user/Intermediate --db /data/photos.db --trashdb /data/trash.db
PhotoCleaner process --path /home/user/Intermediate --db /data/process.db
PhotoCleaner verify --path /home/user/Intermediate --db /data/verify.db
```

## Processing Flow

1. **File enumeration**: Recursively scans all specified directories.
2. **Case conflict detection**: Identifies files with the same name but different casing that
   would collide on case-insensitive file systems, then renames them before processing.
3. **Per-file validation pipeline** (runs in parallel, stops on first action per file):
   0. Act on the exiftool `-validate` verdict, which rides along with the metadata read that
      `process` already performs and so costs nothing extra. A file exiftool reports **errors**
      on is marked invalid and no further step touches it. **Warnings are logged at debug level
      and nothing more**: measured across a real collection, roughly three quarters of perfectly
      healthy files carry at least one (odd IFD offsets, non-standard maker note tags, short
      IPTC fields), so failing on warnings would condemn most of a library. This is a cheap net
      for a rare case, not a substitute for the `verify` command.
   1. Rename to canonical MIME extension, correcting mismatches and stripping compound extensions.
   2. Rename mixed-case extension to lowercase.
   3. Delete short or Live Photo video clips. Videos <= 1s are always deleted, and videos <= 4s
      with a candidate companion image (direct name match or `_hevc`-suffix match) are deleted
      when both files share a matching `ContentIdentifier` tag.
   4. Convert legacy or incompatible video formats to MP4:
      - Remux: MTS, M2TS, MKV (stream copy, no quality loss)
      - Re-encode: WMV, AVI, 3GP, GIF (H.264 CRF 21 / AAC 128k)
      - Re-encode PCM audio: MOV, MP4 with PCM audio (AAC 128k, video stream copied)
      - After every conversion: all source metadata copied to output via `exiftool -TagsFromFile`
   5. Warn on DNG version > v1.4.
4. **Reprocess loop**: Any file that was renamed or converted is re-queued until stable.
5. **Results summary**: Reports counts of failed, invalid, modified, and successfully processed
   files, and lists any unrecognized file extensions. Exits `2` if anything failed or was invalid.

The `import` command applies the same exiftool validation, skipping any file that reports errors
rather than pulling it into the collection.

## Undo Flow

Every file modification or deletion made by `process` creates a `.bak` backup alongside the
original: the first backup is `X.bak`, and if that already exists (from a prior run) the next is
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
     `stem_1.mp4`). If no companion exists, falls back to deleting `stem.mp4` when present
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
     (counted as "skipped by reference"). This is a read-only check, so no records are written.
   - `--db` (Import.db): if the source SHA-256 is already present (from a previous import
     run), the file is skipped (counted as "skipped"). Otherwise, the file is copied/moved and
     a record is inserted **keyed by the SOURCE path** (not the dest path) with the source
     hash, size, and mtime. This is the load-bearing detail: rows in Import.db identify
     sources, not destinations, so subsequent runs of `process`/`index` against a separate
     Process.db cannot clobber the dedup key. The DB file is created automatically on first use.
3. **Date resolution**: reads EXIF metadata via `exiftool`. Uses `EXIF:DateTimeOriginal` or
   `QuickTime:CreateDate` (whichever is set). Falls back to `DateTime.MinValue` when no date
   is found. Those files land in a `"0001/01/01"` bucket (with the default `yyyy/MM/dd` format),
   making undated files easy to locate and handle manually.
4. **Subdirectory naming**: the date is formatted using `--format` (default `"yyyy/MM/dd"`).
   The format is validated at startup, and time components are rejected.
5. **Copy or move**: by default files are copied and the source is preserved. Pass `--move`
   to remove the source file after a successful copy.
6. **Tagging**: `--tagpath` splits the source sub-directory path relative to `--path` into
   tokens and writes each as an `XMP:Subject` tag. `--tags` applies explicit comma-separated
   tags to every file. Both can be combined, and tags are merged and deduplicated. Applied after
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

## Verify Flow

The `verify` command answers one question: will Immich be able to generate a preview for this
file? It exists because a file can be byte-complete, pass every other check, upload successfully,
and then fail thumbnail generation forever. Metadata inspection cannot see this, because the file
reports as a perfectly clean HEIC or DNG, so the only reliable answer comes from running the decoder
Immich runs.

`verify` is a standalone pipeline step rather than an option on `process`, so the calling script
chooses where to run it and whether a failure should stop the pipeline or merely be recorded.

1. **Partition**: non-media files are ignored. With `--db`, files already verified and unchanged
   since are skipped, so a repeat run over a large collection is cheap.

   **Give `verify` its own database file.** It records the verified state in the same
   `is_processed` column that `process` writes, so pointing `--db` at a `Process.db` makes
   `verify` skip every file as "already verified" when they were only processed. Nothing detects
   this, so use a separate `Verify.db`, as the examples below do.
2. **Decode pass** (requires Docker). Every file is handed to
   Immich's own `MediaRepository` running inside the `immich-server` image, using the same
   `generateThumbnail`, the same libvips build, the same libheif and libraw versions, and for RAW
   the same embedded-preview extraction. Paths are streamed in batches over stdin so container
   startup is paid once per batch rather than once per file. The media directory is mounted
   read-only at the fixed container path `/photocleaner`, and every path is translated onto it
   before being sent in, so a host path that is not a valid container path still works.

   The decoder is the only judge of a file's health. PhotoCleaner deliberately carries no
   container parser of its own, because such a parser condemns whatever it fails to understand,
   and an unfamiliar but valid format is indistinguishable from a damaged one from the inside.
   A file that cannot be read, or that vanishes mid-run, counts as failed rather than invalid,
   since neither is evidence of damage.
3. **Report**: logs each rejection with the decoder's own message, then a summary. Exits `2` if
   any file is invalid or any file failed.

Nothing is modified, moved, or deleted. `verify` only ever reads.

Because the decode pass calls Immich's own compiled code rather than reimplementing its pipeline,
it tracks Immich's behavior across releases automatically. It runs `docker` directly, so it must
be run somewhere Docker is available, and is not supported from inside PhotoCleaner's own
container. There is no offline mode, because the decoder is the whole check.

Before any file is judged, the command runs a preflight against the image. If Docker is
unreachable or the image cannot be prepared, it exits `1` without marking a single file invalid.

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
    photocleaner:latest import --path /source --outpath /organized

# Import with deduplication DB (mount a persistent directory for the DB file)
docker run --rm \
    -v /host/photos:/source \
    -v /host/organized:/organized \
    -v /host/db:/db \
    photocleaner:latest import --path /source --outpath /organized --db /db/photos.db

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
    photocleaner:latest import --path /source --outpath /organized --db /db/photos.db --trashdb /db/trash.db
```

## Workflow Example

**Run [kei][kei-link] to download photos from iCloud**:

kei keeps its settings in a TOML file rather than on the command line, so the same configuration
serves the one-shot and the long-running forms. A minimal `config.toml`:

```toml
[auth]
username = "your@icloud.email"

[download]
directory = "/photos"
folder_structure = "%Y/%m/%Y-%m-%d"

[filters]
media = ["photos", "videos", "live-photos"]

[photos]
raw_policy = "prefer-raw"

[watch]
interval = 86400
```

Authenticate once, interactively, so the session is stored in the data directory:

```shell
#!/bin/bash

set -Eeuo pipefail

docker run -it --rm --name kei \
    -v /data/appdata/kei/config:/config \
    -v /data/media/icloud:/photos \
    -e KEI_DATA_DIR=/config \
    ghcr.io/rhoopr/kei:latest \
    kei login
```

Then sync on demand:

```shell
#!/bin/bash

set -Eeuo pipefail

docker run -it --rm --name kei \
    -v /data/appdata/kei/config:/config \
    -v /data/media/icloud:/photos \
    -e KEI_DATA_DIR=/config \
    ghcr.io/rhoopr/kei:latest \
    kei sync \
        --config /config/config.toml \
        --recent 30d
        # --dry-run to preview without writing
```

Or run it as a service that keeps mirroring on the `[watch]` interval:

```yaml
services:
  kei:
    image: ghcr.io/rhoopr/kei:latest
    container_name: kei
    restart: unless-stopped
    environment:
      - TZ=America/Los_Angeles
      - KEI_DATA_DIR=/config
    volumes:
      - /data/media/icloud:/photos
      - /data/appdata/kei/config:/config
    secrets:
      - icloud_password
    command:
      - kei
      - service
      - run
      - --config
      - /config/config.toml
      - --password-file
      - /run/secrets/icloud_password
```

**Run PhotoCleaner to sync [Immich][immich-link] trash hashes** (optional, prevents re-importing trashed files):

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

**Run PhotoCleaner to import new photos**:

Copy only new files (not already in the DB and not trashed in Immich) from the download
directory to an intermediate directory, without touching the downloaded originals:

```shell
#!/bin/bash

set -Eeuo pipefail

docker run --rm \
    -v /data/media/icloud:/icloud \
    -v /data/media/intermediate:/intermediate \
    -v /data/media:/db \
    photocleaner:latest \
    import \
    --path /icloud \
    --outpath /intermediate \
    --db /db/photos.db \
    --trashdb /db/trash.db \
    --threads 4
```

**Run PhotoCleaner to process the intermediate photos**:

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

**Run PhotoCleaner to verify Immich can render the photos**:

Run this on the host rather than inside the PhotoCleaner container: the decode pass invokes
`docker` to run Immich's own decoder, and Docker-in-Docker is not supported. Capture the exit
code instead of letting `set -e` abort, so the upload can be skipped while the run still reports
cleanly:

```shell
#!/bin/bash

set -Eeuo pipefail

rc=0
PhotoCleaner verify \
    --path /data/media/intermediate \
    --db /data/media/verify.db \
    --threads 4 || rc=$?

case $rc in
    0) echo "All files verified" ;;
    2) echo "Invalid files found - skipping upload, review the log" >&2; exit 2 ;;
    *) echo "Verification could not run (exit $rc) - this says nothing about the files" >&2; exit "$rc" ;;
esac
```

**Run [Immich CLI][immich-cli-link] to import photos into Immich**:

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

**Run [immich-go][immich-go-link] to import photos into Immich**:

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

## Questions or Issues

Ask questions in the [Discussions][discussions-link] forum and report bugs in [GitHub Issues][issues-link].

## Development Environment Setup

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

#### Update Windows

```shell
winget upgrade --all --accept-package-agreements --include-unknown
```

#### Update Linux

```shell
apt update
apt upgrade
```

#### Update .NET Tools

```shell
dotnet tool restore
dotnet tool update --all
dotnet outdated --upgrade:prompt
```

## 3rd Party Tools

The third-party tools, libraries, and actions this project depends on.

| Tool | Role |
| --- | --- |
| [AwesomeAssertions][awesomeassertions-link] | Assertion library for .NET tests. |
| [BenchmarkDotNet][benchmarkdotnet-link] | Benchmarking library for .NET. |
| [CliWrap][cliwrap-link] | Process execution library for .NET. |
| [Codecov][codecov-link] | Code coverage reporting service. |
| [CSharpier][csharpier-link] | C# code formatter. |
| [cspell][cspell-link] | Spell checker. |
| [Docker Hub Description][docker-hub-description-link] | GitHub action that publishes a Docker Hub repository overview. |
| [dotnet-outdated][dotnet-outdated-link] | Outdated NuGet dependency reporter. |
| [editorconfig-checker][editorconfig-checker-link] | Line-ending and whitespace linter. |
| [ExifTool][exiftool-link] | Media metadata reader and writer. |
| [FFmpeg][ffmpeg-link] | Media transcoder. |
| [GH Release][gh-release-link] | GitHub action that creates a release. |
| [GitHub Actions][github-actions-link] | CI and automation runner. |
| [GitHub Dependabot][dependabot-link] | Dependency update bot. |
| [Husky.Net][husky-link] | Git hook manager for .NET. |
| [markdownlint-cli2][markdownlint-link] | Markdown linter. |
| [Nerdbank.GitVersioning][nbgv-link] | Version computation from git height. |
| [Serilog][serilog-link] | Structured logging library for .NET. |
| [SQLite][sqlite-link] | Embedded relational database engine. |
| [xUnit.Net][xunit-link] | Test framework for .NET. |

## License <!-- omit from toc -->

Licensed under the [MIT License][license]\
![GitHub License][license-shield]

<!-- Shields -->

[docker-develop-version-shield]: https://img.shields.io/docker/v/ptr727/photocleaner/develop?label=Docker%20Develop&logo=docker&color=orange
[docker-latest-version-shield]: https://img.shields.io/docker/v/ptr727/photocleaner/latest?label=Docker%20Latest&logo=docker
[docker-status-shield]: https://img.shields.io/github/actions/workflow/status/ptr727/PhotoCleaner/publish-release.yml?event=schedule&logo=github&label=Docker%20Build
[last-commit-shield]: https://img.shields.io/github/last-commit/ptr727/PhotoCleaner?logo=github&label=Last%20Commit
[license-shield]: https://img.shields.io/github/license/ptr727/PhotoCleaner?label=License
[pre-release-version-shield]: https://img.shields.io/github/v/release/ptr727/PhotoCleaner?include_prereleases&label=GitHub%20Pre-Release&logo=github
[release-status-shield]: https://img.shields.io/github/actions/workflow/status/ptr727/PhotoCleaner/publish-release.yml?event=schedule&logo=github&label=Releases%20Build
[release-version-shield]: https://img.shields.io/github/v/release/ptr727/PhotoCleaner?logo=github&label=GitHub%20Release

<!-- Repo -->

[history]: ./HISTORY.md
[license]: ./LICENSE

<!-- Distribution -->

[actions-link]: https://github.com/ptr727/PhotoCleaner/actions
[commits-link]: https://github.com/ptr727/PhotoCleaner/commits/main
[discussions-link]: https://github.com/ptr727/PhotoCleaner/discussions
[docker-hub-link]: https://hub.docker.com/r/ptr727/photocleaner
[github-link]: https://github.com/ptr727/PhotoCleaner
[issues-link]: https://github.com/ptr727/PhotoCleaner/issues
[releases-link]: https://github.com/ptr727/PhotoCleaner/releases

<!-- External -->

[awesomeassertions-link]: https://awesomeassertions.org/
[benchmarkdotnet-link]: https://benchmarkdotnet.org/
[cliwrap-link]: https://github.com/Tyrrrz/CliWrap
[codecov-link]: https://codecov.io/
[csharpier-link]: https://csharpier.com/
[cspell-link]: https://cspell.org
[dependabot-link]: https://github.com/dependabot
[docker-hub-description-link]: https://github.com/marketplace/actions/docker-hub-description
[dotnet-outdated-link]: https://github.com/dotnet-outdated/dotnet-outdated
[editorconfig-checker-link]: https://github.com/editorconfig-checker/editorconfig-checker
[exiftool-link]: https://exiftool.org/
[ffmpeg-link]: https://www.ffmpeg.org/
[gh-release-link]: https://github.com/marketplace/actions/gh-release
[github-actions-link]: https://github.com/actions
[husky-link]: https://alirezanet.github.io/Husky.Net/
[immich-cli-link]: https://docs.immich.app/features/command-line-interface
[immich-go-link]: https://github.com/simulot/immich-go
[immich-link]: https://immich.app
[kei-link]: https://github.com/rhoopr/kei
[markdownlint-link]: https://github.com/DavidAnson/markdownlint-cli2
[nbgv-link]: https://github.com/dotnet/Nerdbank.GitVersioning
[serilog-link]: https://serilog.net/
[sqlite-link]: https://www.sqlite.org/
[xunit-link]: https://xunit.net/
