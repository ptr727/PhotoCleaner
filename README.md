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
- **Organizes into date folders** (via `organize` command): Copies (default) or moves supported
  media files from source directories into `outpath/date/filename` using EXIF date metadata.
  Falls back to a deterministic `0001/01` bucket when no date is found. Supports a custom
  `--format` string (default `yyyy/MM/dd`) validated as a date-only pattern. Optional SQLite
  deduplication via `--db` tracks source file hashes so re-runs skip already organized files.
  `--tagpath` writes the source sub-directory path components as `XMP:Subject` tags on the
  destination file. `--tags` applies explicit comma-separated `XMP:Subject` tags to every
  organized file. `--datepath` infers and writes EXIF/QuickTime creation dates from filenames
  or directory path structures when metadata is absent (opt-in, applied before the file is moved
  to a date-based directory so the source path is still available).
- **Deletes duplicates** (via `duplicates` command): Hashes all files in a source directory
  and registers them in a SQLite DB, then deletes any file from the target directory whose
  SHA-256 hash matches a source file, or whose SHA-1 matches a hash in an optional Immich
  trash database (`--trashdb`). Source files are never touched. The DB persists across runs,
  enabling incremental duplicate detection as the source collection grows.
- **Syncs Immich trash hashes** (via `trash` command): Connects to an Immich server via its
  REST API, fetches all trashed asset checksums (SHA-1), and stores them in a local SQLite
  database. This trash DB can then be used with `organize --trashdb` to skip files that were
  already imported and trashed in Immich, preventing re-import of known duplicates.
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
  cleanup     Delete files not in the supported media list
  organize    Organize media files into date-based subdirectories
  duplicates  Delete files in --outpath whose content (SHA-256) matches a file in --path
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
  --db <db>                     SQLite database file for deduplication tracking
  --rehash                      Force rehashing of all files, ignoring size/mtime cache
  --duration <duration>         Maximum duration in seconds below which a video is considered a short clip and deleted [default: 1]
  --reprocess                   Re-process files even if already marked as processed in the database
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
$> PhotoCleaner cleanup --help
Description:
  Delete files not in the supported media list

Options:
  --path <path> (REQUIRED)      The directory path to process
  --dryrun                      Perform a dry run without making changes
```

```text
$> PhotoCleaner organize --help
Description:
  Organize media files into date-based subdirectories

Options:
  --path <path> (REQUIRED)       The directory path to process
  --dryrun                       Perform a dry run without making changes
  --threads <threads>            Number of parallel threads [default: 4]
  --outpath <outpath> (REQUIRED) Output directory for organized files
  --format <format>              Date format for output subdirectory names [default: yyyy/MM/dd]
  --deleteempty                  Delete empty source subdirectories after organizing
  --move                         Move files instead of copying (default: copy)
  --tagpath                      Apply path sub-directory components as XMP Subject tags to the organized file
  --tags <tags>                  Comma-separated XMP Subject tags to apply to every organized file (e.g. "vacation,family")
  --datepath                     Set missing EXIF creation date from file path
  --db <db>                      SQLite database file for deduplication tracking
  --rehash                       Force rehashing of all files, ignoring size/mtime cache
  --trashdb <trashdb>            SQLite database with Immich trash hashes (skip matching files)
  --skipdb <skipdb>              SQLite database of files to skip (read-only SHA-256 check)
```

```text
$> PhotoCleaner duplicates --help
Description:
  Delete files in --outpath whose content (SHA-256) matches a file in --path

Options:
  --path <path> (REQUIRED)       The directory path to process
  --dryrun                       Perform a dry run without making changes
  --threads <threads>            Number of parallel threads [default: 4]
  --outpath <outpath> (REQUIRED) Target directory to scan for duplicates
  --db <db> (REQUIRED)           SQLite database file for source file index
  --outdb <outdb> (REQUIRED)     SQLite database file for target file hash caching
  --rehash                       Force rehashing of all files, ignoring size/mtime cache
  --trashdb <trashdb>            SQLite database with Immich trash hashes (delete matching files)
```

```text
$> PhotoCleaner index --help
Description:
  Index files into the database for deduplication tracking

Options:
  --path <path> (REQUIRED)      The directory path to index
  --threads <threads>           Number of parallel threads [default: 4]
  --db <db> (REQUIRED)          SQLite database file for deduplication tracking
  --rehash                      Force rehashing of all files, ignoring size/mtime cache
```

```text
$> PhotoCleaner trash --help
Description:
  Sync trashed asset hashes from Immich

Options:
  --url <url> (REQUIRED)         Immich server URL (e.g. http://immich:2283)
  --apikey <apikey> (REQUIRED)   Immich API key
  --db <db> (REQUIRED)           SQLite database file for trash hashes
```

**Option notes:**

- `--path` - must point to an existing directory; accepts exactly one directory per command
  invocation.
- `--threads` - defaults to `min(CPU count, 4)`; must be `> 0` and `<= CPU count`.
- `--skipbackup` - opt-in (`process` only); skips all `.bak` file creation. The `undo`
  command cannot reverse a run made with this flag.
- `--outpath` - required for `organize`; target directory (created on demand).
- `--format` - optional (`organize` only); a C# date format string used to name date
  subdirectories (default `"yyyy/MM/dd"`). Must be date-only - time components are rejected.
  Files with no EXIF date land in a `"0001/01/01"` fallback bucket.
- `--deleteempty` - optional (`organize` only); after all files are organized, deletes empty
  child subdirectories from each source `--path` (deepest first). The source root itself is
  never deleted. Useful for cleaning up directory trees left behind after organizing with
  `--move`.
- `--move` - optional (`organize` only); moves files instead of copying. Default behavior is
  to copy, which preserves the source files. Use `--move` when the source directory is
  temporary or when used alongside `--deleteempty` to clean up after organizing.
- `--tagpath` - optional (`organize` only); splits the source sub-directory path relative to
  `--path` into tokens and writes each token as an `XMP:Subject` tag on the destination file
  using exiftool. Files at the root of `--path` receive no tags. Tags are applied with a
  remove-then-add pattern (`-XMP:Subject-= / -XMP:Subject+=`) so existing tags are preserved
  and duplicates are not created. Only file types that support XMP writes are tagged.
- `--tags` - optional (`organize` only); a comma-separated list of `XMP:Subject` tags applied to
  every organized file (e.g. `--tags "vacation,family trip,2018"`). Tags are applied using the
  same remove-then-add pattern as `--tagpath`. Can be combined with `--tagpath` - both sets of
  tags are merged. Only file types that support XMP writes are tagged.
- `--datepath` - optional (`organize` only); when a file has no embedded creation date, infers
  one from the filename or directory path structure (via `DateFromPath`) and writes it to the
  destination file before restoring mtime. Opt-in because writing to files is destructive and
  the source path context is only available during `organize` (before files move to date-based
  directories).
- `--db <path>` - optional for `organize` and `process`, **required** for `duplicates` and
  `index`; path to a SQLite database file. Uses a single `files` table (`path` PRIMARY KEY,
  `sha256`, `sha1`, `file_size`, `mtime_ticks`, `is_processed`). For `organize`: files are hashed,
  checked against the DB by hash; new files are copied/moved and recorded by destination path.
  For `process`: files are looked up by path and skipped when already processed; processes and
  re-hashes when file content changes. For `duplicates` and `index`: source files are indexed;
  target files whose hash is found in the DB are deleted (duplicates only). The DB file is
  created automatically on first use. **Breaking change**: databases from earlier versions
  (with `organized_files`, `processed_files`, or `source_files` tables) are incompatible;
  delete the old file before first use.
- `--outdb <path>` - **required** for `duplicates`; path to a SQLite database file for
  caching target file hashes. Uses the same schema and size/mtime caching as `--db` so
  unchanged target files skip SHA-256 recomputation on re-runs. The outdb from a duplicates
  run can be reused as the `--db` for a subsequent `process` run on the same directory,
  avoiding redundant hashing across workflow steps. Created automatically on first use.
- `--trashdb <path>` - optional for `organize` and `duplicates`; path to a SQLite database
  containing Immich trash hashes (populated by the `trash` command). For `organize`: files
  whose SHA-1 matches a trash hash are skipped (not copied). For `duplicates`: matching files
  are deleted alongside SHA-256 duplicates.
- `--skipdb <path>` - optional for `organize`; path to a SQLite database of files to skip
  (read-only SHA-256 check). Files whose SHA-256 is in this DB are skipped without being
  recorded. Use to skip files already present in another collection.
- `--url <url>` - **required** for `trash`; the Immich server URL (e.g. `http://immich:2283`).
- `--apikey <key>` - **required** for `trash`; the Immich API key. Create one in Immich under
  Account Settings > API Keys.
- `--rehash` - optional (`process`, `organize`, `duplicates`, `index`); forces SHA-256 and
  SHA-1 recomputation for every file, bypassing the size/mtime cache. Use when file content
  may have changed without the modification timestamp being updated.
- `--duration` - optional (`process` only); overrides the short-video deletion threshold
  (default `1.0` seconds). Videos in a live-photo-compatible format whose duration is <= this
  value are always deleted. Must be `> 0`.
- `--reprocess` - optional (`process` only); when set, ignores the `is_processed` flag in
  the database and processes every file regardless of prior run history. Useful after changing
  pipeline settings (e.g. `--duration`) without wiping the database.

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

# Remove all non-media files (.bak artefacts, .DS_Store, Thumbs.db, etc.)
PhotoCleaner cleanup --path /home/user/Photos

# Preview what cleanup would remove without deleting anything
PhotoCleaner cleanup --path /home/user/Photos --dryrun

# Organize (copy) media into date-based subdirectories - source files are kept
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized

# Organize with a custom date format (creates e.g. 2024/06/2024-06-15/ subdirectories)
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --format "yyyy/MM/yyyy-MM-dd"

# Preview what organize would do without changing anything
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --dryrun

# Move instead of copy (source files are removed)
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --move

# Organize and remove empty source subdirectories afterward (use with --move)
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --move --deleteempty

# Organize with path-based XMP:Subject tagging (sub-directory names become tags)
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --tagpath

# Organize inferring missing EXIF dates from the source path
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --datepath

# Organize with explicit XMP:Subject tags applied to every file
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --tags "vacation,family trip"

# Organize with both path tagging, explicit tags, and date inference
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --tagpath --tags "2018" --datepath

# Organize with deduplication: only copy files not already in the database
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Intermediate --db /data/photos.db

# Delete files in /target that already exist (by content) in /source
PhotoCleaner duplicates --path /home/user/Source --outpath /home/user/Target --db /data/source.db --outdb /data/target.db

# Preview duplicate deletion without removing anything
PhotoCleaner duplicates --path /home/user/Source --outpath /home/user/Target --db /data/source.db --outdb /data/target.db --dryrun

# Incremental: index source once, then check multiple target directories over time
PhotoCleaner duplicates --path /home/user/Source --outpath /home/user/Import1 --db /data/source.db --outdb /data/import1.db
PhotoCleaner duplicates --path /home/user/Source --outpath /home/user/Import2 --db /data/source.db --outdb /data/import2.db

# Index a directory into the database (stand-alone, without running duplicates)
PhotoCleaner index --path /home/user/Source --db /data/dedup.db

# Re-index forcing hash recomputation (useful after file content changes without mtime update)
PhotoCleaner index --path /home/user/Source --db /data/dedup.db --rehash

# Sync Immich trash hashes into a local database
PhotoCleaner trash --url http://immich:2283 --apikey YOUR_API_KEY --db /data/trash.db

# Organize and skip files that were trashed in Immich (prevents re-import)
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --db /data/photos.db --trashdb /data/trash.db

# Organize and skip files already in another collection (read-only reference)
PhotoCleaner organize --path /home/user/Photos --outpath /home/user/Organized --skipdb /data/existing-collection.db

# Delete duplicates and files trashed in Immich from a target directory
PhotoCleaner duplicates --path /home/user/Source --outpath /home/user/Target --db /data/source.db --outdb /data/target.db --trashdb /data/trash.db

# Full workflow with Immich trash integration
PhotoCleaner trash --url http://immich:2283 --apikey $IMMICH_KEY --db /data/trash.db
PhotoCleaner organize --path /home/user/iCloud --outpath /home/user/Intermediate --db /data/photos.db --trashdb /data/trash.db
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

## Cleanup Flow

The `cleanup` command deletes every file in the target directories whose extension is **not** in
the supported media list. This removes processing artefacts (`.bak`, `.bak1`, `.bak.out`),
system junk (`.DS_Store`, `Thumbs.db`), and any other non-media files. Backup artefacts are
logged as warnings before deletion; other files are logged as informational.

Run `cleanup` after verifying `process` results, or use `process --skipbackup` followed by
`cleanup` for a no-artefact workflow.

## Organize Flow

The `organize` command copies (default) or moves every supported media file in the source
directories to `outpath/date/filename`:

1. **Date inference** (opt-in via `--datepath`): when no embedded creation date is found,
   infers one from the source file path using `DateFromPath` (filename patterns like
   `20210502_200152.jpg` or directory structures like `2021/05/02/`). The inferred date is
   written to the destination file (for supported types) and used for subdirectory placement.
   Applied before the file is moved so the original path is still available.
2. **Skip checks** (opt-in): before any file operation, the source file is hashed (SHA-256
   and SHA-1) and checked against up to three databases in order:
   - `--trashdb`: if the SHA-1 matches a hash in the Immich trash DB, the file is skipped
     (counted as "trashed in Immich").
   - `--skipdb`: if the SHA-256 matches a record in the reference DB, the file is skipped
     (counted as "skipped by reference"). This is a read-only check - no records are written.
   - `--db`: if the SHA-256 is already present (from a previous organize run), the file is
     skipped (counted as "skipped"). Otherwise, the file is copied/moved and a record is
     inserted keyed by the destination path. The DB file is created automatically on first use.
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
9. **Empty directory cleanup** (opt-in via `--deleteempty`): after all files are organized,
   iterates each source directory and deletes empty child subdirectories deepest-first.
   The source root itself is never deleted. Typically combined with `--move`.

Run with `--dryrun` to preview the planned operations without touching the file system.

## Duplicates Flow

The `duplicates` command removes files from a target directory whose content already exists in
a source directory (identified by SHA-256 hash) or whose SHA-1 matches an Immich trash hash
(via optional `--trashdb`). Source files are never modified.

1. **Phase 1 - Index sources**: hashes every supported media file in `--path` and inserts
   each record into the unified `files` table of the SQLite DB using `INSERT OR IGNORE` on
   the path primary key. Re-indexing the same source is idempotent; new source files added
   later are picked up on the next run. Size/mtime caching (bypassed by `--rehash`) avoids
   recomputing SHA-256 for unchanged files.
2. **Phase 2 - Scan target**: hashes every supported media file in `--outpath` using
   size/mtime caching from `--outdb` (unchanged files skip rehashing), upserts each record
   into the outdb, and checks each hash against the source DB via the hash index. If
   `--trashdb` is provided, files whose SHA-1 matches a trash hash are also deleted. Files
   with unique hashes (not in source DB or trash DB) are kept. The outdb can be reused as
   `--db` for a subsequent `process` run on the same directory.
3. **Unsupported files**: non-media files (by extension) are skipped in both phases and left
   untouched.

Run with `--dryrun` to report how many files would be deleted without removing any.

## Trash Flow

The `trash` command syncs trashed asset checksums from an Immich server into a local SQLite
database. This enables the `organize` and `duplicates` commands to skip or delete files that
were already imported and trashed in Immich.

1. **Connect**: authenticates to the Immich server using the `--url` and `--apikey` options.
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
All `--path`, `--outpath`, `--db`, `--outdb`, `--trashdb`, and `--skipdb` arguments refer to paths inside the container:

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

# Cleanup non-media files
docker run --rm -v /host/photos:/data \
    photocleaner:latest cleanup --path /data

# Organize into date-based subdirectories (copy, source preserved)
docker run --rm \
    -v /host/photos:/source \
    -v /host/organized:/organized \
    photocleaner:latest organize --path /source --outpath /organized

# Organize with deduplication DB (mount a persistent directory for the DB file)
docker run --rm \
    -v /host/photos:/source \
    -v /host/organized:/organized \
    -v /host/db:/db \
    photocleaner:latest organize --path /source --outpath /organized --db /db/photos.db

# Delete duplicates: remove files from /target that already exist in /source
docker run --rm \
    -v /host/source:/source \
    -v /host/target:/target \
    -v /host/db:/db \
    photocleaner:latest duplicates --path /source --outpath /target --db /db/source.db --outdb /db/target.db

# Index a directory into the database
docker run --rm \
    -v /host/source:/source \
    -v /host/db:/db \
    photocleaner:latest index --path /source --db /db/dedup.db

# Sync Immich trash hashes
docker run --rm \
    -v /host/db:/db \
    photocleaner:latest trash --url http://immich:2283 --apikey YOUR_API_KEY --db /db/trash.db

# Organize with trash skip (prevents re-importing files trashed in Immich)
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
    --db /db/trash.db
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
