# Architecture

PhotoCleaner is a .NET 10 console application that processes media files in preparation for import into photo management systems (Lightroom, Immich, PhotoPrism). It analyzes and transforms media files through validation, modification, and verification phases.

## Project Structure

- **Docker/**: Docker configuration
  - `Dockerfile`: Two-stage build (SDK Alpine build -> runtime Alpine final) that installs `exiftool` and `ffmpeg` in the final stage
- **PhotoCleaner/**: Main console application
  - `Program.cs`: Entry point with logger setup (Main only)
  - `CommandLine.cs`: System.CommandLine implementation for CLI parsing (`process`, `undo`, `import`, `index`, `trash`, and `verify` subcommands)
  - `MediaUtilities.cs`: Shared static utilities, `SupportedExtensions` (FrozenSet), `GetUniqueFileName`, `GetExifToolJsonAsync`, `TrySetCreateDateAsync`, `WriteExifToolAsync`, video/duration constants
  - `CommandRunner.cs`: Thin wrapper for command start/complete/error logging, taking `Func<Task<int>>` and returning the command's exit code
  - `ExitCode.cs`: The shared exit-code contract, being `Success` (0), `Error` (1, command could not run), and `Failed` (2, ran to completion with per-file failures)
  - `DatabaseScope.cs`: Generic async DB lifecycle helper (create, init, dispose)
  - `TrashDatabaseScope.cs`: Same lifecycle helper pattern for `TrashDatabase`
  - `FileEnumerator.cs`: Parallel file enumeration returning `(IReadOnlyList<string>, int)`
  - `DirectoryCleaner.cs`: Static helper that deletes empty subdirectories under a root (deepest-first, and the root itself is never deleted), used by `import` and `process` when `--deleteempty` is set
  - `ProcessCommand.cs`: Process command orchestration, case conflict resolution, reprocessing loop, result reporting
  - `ImportCommand.cs`: Import command orchestration (formerly `OrganizeCommand`)
  - `IndexCommand.cs`: Index command orchestration
  - `TrashCommand.cs`: Trash command orchestration, fetches trashed asset checksums from Immich API, stores SHA-1 hashes in a `TrashDatabase`. With `--processdb` it also stores each asset's image data hash, looked up by SHA-1 in a read-only Process.db
  - `UndoCommand.cs`: Undo command orchestration
  - `VerifyCommand.cs`: Verify command orchestration, which enumerates, runs `VerifyTask`, and reports counts
  - `ProcessTask.cs`: Core file processing pipeline (validation, conversion, metadata)
  - `UndoTask.cs`: Undo logic, reverses recorded conversions and restores `.bak` files
  - `ImportTask.cs`: Import logic, copies (default) or moves supported media files from source into date-based subdirectories under `--outpath`. Inserts a row keyed by SOURCE path into Import.db. Optional SQLite deduplication via `Database`. The opt-in `--imagehash` adds image data hash matching against Import.db and Trash.db. (Formerly `OrganizeTask`.)
  - `VerifyTask.cs`: Verification logic, a decode pass that runs Immich's own `MediaRepository` inside `ghcr.io/immich-app/immich-server:release` via `docker run`, batching paths over stdin. Preflights the image before judging any file, so an infrastructure failure exits `Error` rather than marking files invalid
  - `ImmichVerifyScript.cs`: The Node script run inside the Immich image, as const strings. Calls Immich's own compiled `MediaRepository`, `defaults`, and `ThumbnailConfig` rather than reimplementing the preview pipeline, so behavior tracks Immich across releases
  - `VerifyResult.cs`: The AOT-compatible `ImmichVerifyLine` JSON model and its `ImmichVerifyJsonContext` source-generated context, together forming the container's output protocol
  - `IndexTask.cs`: Common DB upsert logic used by `process` and `index` commands. `IndexFileAsync` (single-file) returns `(IndexStatus, sha256, sha1, wasProcessed)`. `ExecuteAsync` (batch parallel) returns `(inserted, updated, unchanged, ignored, failed)`. When `options.MarkProcessed` is true, newly inserted rows are marked `is_processed=1` (used by `index --processed` to seed Process.db).
  - `Database.cs`: SQLite wrapper with a single `files` table (`path` PRIMARY KEY, `sha256`, `sha1`, `file_size`, `mtime_ticks`, `is_processed`, `content_identifier`, `image_hash`, `metadata_read`). It indexes the hash, `content_identifier` and `image_hash` columns. Size/mtime caching via `ResolveHashesAsync` skips rehashing unchanged files. Every write computes both sha256 and sha1 in a single read pass. Both columns are non-null. A database written before the catalog columns existed is upgraded in place when a writing command opens it.
  - `TrashDatabase.cs`: Simple SQLite wrapper for Immich trash hashes with a single `trash_hashes` table (`sha1` PRIMARY KEY, `original_file_name`, `image_hash`), used by `trash`, `import`, and `process` commands. It indexes `image_hash`. A database written before that column existed gains it in place when `trash` opens it. A read-only open of such a database reports `HasImageHash = false` and matches by SHA-1 only
  - `ImmichApiModels.cs`: AOT-compatible JSON models for Immich API (`ImmichSearchRequest`, `ImmichSearchResponse`, `ImmichAssetDto`) with `ImmichJsonContext` source generation
  - `DateFromPath.cs`: Static utility class for date inference from filenames/paths
  - `ExifToolJson.cs`: JSON model for ExifTool metadata, including the `ExifTool:Validate` verdict and `ParseValidate` which splits it into error and warning counts
  - `SkippedExtensionTracker.cs`: Thread-safe tracker for unknown file extensions skipped during processing; used by all commands that filter by `MediaUtilities.SupportedExtensions` (`process`, `import`, `index`)
  - `LoggerFactory.cs`: Serilog logger factory, reusing an already-configured `Log.Logger` when one exists rather than always creating a new one
  - `Extensions.cs`: Extension methods for logging and error handling
- **PhotoCleanerTests/**: Comprehensive test project (`TempDirectoryFixture.cs` is the shared fixture, not a test class)
  - `DateInferenceTests.cs`: Core date inference functionality tests
  - `CommandLineTests.cs`: Command line parsing and validation tests
  - `ProcessTaskTests.cs`: Process task tests
  - `UndoTaskTests.cs`: Undo task tests
  - `ExifToolJsonTests.cs`: ExifToolJson unit tests (includes GetDate, IsDngVersionNewer)
  - `ImportTaskTests.cs`: Import task tests
  - `DatabaseTests.cs`: Database tests
  - `IndexTaskTests.cs`: IndexTask tests
  - `TrashDatabaseTests.cs`: TrashDatabase tests
  - `TrashCommandTests.cs`: TrashCommand tests with mock HTTP handler
  - `DirectoryCleanerTests.cs`: DirectoryCleaner static helper tests
  - `VerifyTaskTests.cs`: Verify protocol parsing and script-contract tests
  - `LivePhotoCatalogTests.cs`: Live Photo pairing through the catalog and the catalog columns, needs exiftool and ffmpeg
  - `CatalogSchemaTests.cs`: In-place schema upgrade and metadata mapping tests, need no external tools
  - `ArchitectureDiagramTests.cs`: Checks that each `Type.Method()` a Command Logic diagram names exists, and that the suspect marks match the Suspect Branches list

## Core Processing Pipeline

The application uses a sequential validation pipeline where each method returns `bool` -
`false` stops processing the current file:

```csharp
if (!RenameMismatchedMimeExtensions()
    || !RenameMixedCaseExtensions()
    || !await DeleteLivePhotosAsync()
    || !await ConvertVideoAsync()
    || !WarnDngVersion())
```

Before that chain runs, `CheckExifToolValidation` acts on the `ExifTool:Validate` verdict that
rides along with the metadata read. Only an error count fails the file (`ProcessResult.Invalid`).
Warnings are logged at debug level, because roughly three quarters of healthy files in a real
collection carry at least one.

### Two-Phase `process`

When `--db` is given, `ProcessCommand` first catalogs every media file under `--path` in parallel
through `IndexTask`, then runs the per-file pipeline above. The second phase hits the size and
mtime cache, so catalog metadata is read once. Hashes are computed once without `--rehash`. A video is therefore never judged before its still has
been seen. Without `--db` there is no catalog phase and the pipeline runs as before.
A new file costs two exiftool runs, one in the catalog and one in the pipeline, because parsed metadata is not carried across phases.

### Catalog Metadata Columns

`IndexTask.IndexFileAsync` fills `content_identifier` and `image_hash` when it inserts a row or sees new content.
It uses one exiftool run that returns the JSON metadata and the image data hash (`-ImageDataHash -api ImageHashType=SHA256`). `index` and `process` populate
the columns through this path, and `import` fills them from its own metadata read. `verify` indexes without reading metadata, so it needs no exiftool and leaves its rows unread. Other metadata reads leave the hash flag out, since hashing the
image data costs extra I/O.

- `metadata_read` separates "not read yet" from "read, tag absent". Rows from an older database
  start at 0. The next catalog reads them if the file still exists, then sets the flag.
  A row whose file is gone stays unread.
- A null `content_identifier` or `image_hash` with `metadata_read = 1` means the tag is absent,
  which is normal for a format with no image hash.
- ExifTool 12.63 or newer is required, the release that introduced `ImageDataHash`. An older
  version fails the run with a message instead of cataloguing nothing.
- Import dedup and the trash match use the image hash only with the opt-in `--imagehash`. Default runs match by whole-file hash.
- An empty image hash is stored as null and never matches, so a format ExifTool cannot hash keeps whole-file matching.
- A read-only open cannot upgrade a database in the old shape. Only the hash lookups work there.

## Command Logic

The diagrams below trace each command from the command line to the end state of every file. They describe what the code does today, including branches that look wrong, so a reader can compare code and docs branch by branch.

Conventions used in every diagram:

- A label that starts with `Type.Name()` names the method that runs that node. `ArchitectureDiagramTests` checks that each named method exists, so a rename breaks the test until the diagram follows.
- Any other label is prose, and an option name in it refers to a command-line flag.
- A rounded node is an end state: an exit code, or the result a file is counted as. A rounded node that names another diagram continues there.
- A step drawn without a fail edge still throws to the handler the prose under its diagram names.
- A node styled with a dashed red border is a suspect branch. Its `S` number points into [Suspect Branches](#suspect-branches).
- Change a diagram in the same change that moves a branch it draws.

### Run Flow

Program.Main parses the arguments before anything else. A parse error, `--help`, or `--version` skips logger setup and returns the System.CommandLine exit code.

```mermaid
flowchart TD
  main["Program.Main(): parse the arguments with System.CommandLine"]
  bypass{"CommandLine.BypassStartup(): parse error, --help or --version?"}
  scl["System.CommandLine prints help, version, or the parse errors"]
  sclEnd(["Exit 0 for help or version, exit 1 for a parse error"])
  logger["LoggerFactory.Create(): build the logger from --loglevel and --logfile"]
  dispatch{"Which subcommand?"}
  process["ProcessCommand.ExecuteAsync(): process"]
  undo["UndoCommand.ExecuteAsync(): undo"]
  import["ImportCommand.ExecuteAsync(): import"]
  index["IndexCommand.ExecuteAsync(): index"]
  trash["TrashCommand.ExecuteAsync(): trash"]
  verify["VerifyCommand.ExecuteAsync(): verify"]
  runner["CommandRunner.RunAsync(): log start and run the command work"]
  how{"How did the work end?"}
  cancelled(["ExitCode.Error 1, logged as cancelled"])
  thrown(["ExitCode.Error 1, exception logged"])
  code{"Returned code?"}
  ok(["ExitCode.Success 0, logged complete"])
  failed(["ExitCode.Failed 2, logged complete with failures"])
  other(["Other code returned as is, logged as not complete"])
  mainCatch(["Exit 1 from the catch in Program.Main, not logged since Log.Logger is not set yet"])
  signal["Program.OnTerminationSignal(): the first SIGINT or SIGTERM at any point cancels the token"]
  killed(["Process ends at once by the signal's default action, no cleanup"])

  main --> bypass
  bypass -->|"yes"| scl --> sclEnd
  bypass -->|"no"| logger
  logger -->|"exception"| mainCatch
  logger --> dispatch
  dispatch -->|"process"| process
  dispatch -->|"undo"| undo
  dispatch -->|"import"| import
  dispatch -->|"index"| index
  dispatch -->|"trash"| trash
  dispatch -->|"verify"| verify
  process --> runner
  undo --> runner
  import --> runner
  index --> runner
  trash --> runner
  verify --> runner
  runner --> how
  how -->|"OperationCanceledException"| cancelled
  how -->|"any other exception"| thrown
  how -->|"returned a code"| code
  code -->|"0"| ok
  code -->|"2"| failed
  code -->|"anything else"| other
  signal -->|"the work throws OperationCanceledException"| how
  signal -->|"a second signal, unless the process is PID 1"| killed
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

An exception outside CommandRunner.RunAsync, such as one while building the options, reaches the catch in Program.Main and exits 1. Program.Main registers its own SIGINT and SIGTERM handler and sets ProcessTerminationTimeout to null. That keeps System.CommandLine's own handler off. With a timeout set, that handler returns 130 or 143 once it expires, even while the cancel path still runs. The first signal cancels the token the command runs with, so CliWrap kills the child tool and the command's cleanup runs before the exit. A second signal takes the default action and ends the process at once, for a cancel path that hangs. Run as PID 1, as the container image's exec-form entrypoint runs it, the kernel drops that default action. There only a SIGKILL ends a hung cancel path, such as the one `docker kill` sends or the one `docker stop` sends after its timeout.

### Process

#### Process Run

The trash database opens read-only and the process database opens read-write. Both stay open for the whole run. Any exception that escapes this diagram reaches CommandRunner.RunAsync and exits 1.

```mermaid
flowchart TD
  run["ProcessCommand.ExecuteAsync(): wrapped by CommandRunner.RunAsync"]
  tscope["TrashDatabaseScope.RunAsync(): open --trashdb read-only when given"]
  dscope["DatabaseScope.RunAsync(): open --db read-write, also under --dryrun, S8"]:::suspect
  hashver["MediaUtilities.EnsureImageHashSupportAsync(): with --db, need exiftool 12.63 or newer"]
  warn["TrashDatabase.WarnIfImageHashMissing(): under --imagehash only"]
  enum["FileEnumerator.Enumerate(): list every file under --path"]
  fixcase{"ProcessCommand.FixCaseConflicts(): two full paths equal ignoring case?"}
  dry{"--dryrun?"}
  dryloop["ProcessCommand.RenamedMixedCaseFiles(): log each conflict once, nothing moves"]
  move["ProcessCommand.RenamedMixedCaseFiles(): rename every variant to name_N"]
  hasdb{"--db given?"}
  catalog["ProcessCommand.CatalogAsync(): IndexTask.ExecuteAsync on every media file"]
  more{"File list empty?"}
  round["ProcessCommand.ExecuteProcessAsync(): one round, see Process Dispatch"]
  totals["Log the totals and SkippedExtensionTracker.LogWarnings"]
  delempty{"--deleteempty?"}
  clean["DirectoryCleaner.DeleteEmptyDirectories(): deepest first, count only under --dryrun"]
  exit{"Failed or Invalid count above zero?"}
  e2(["ExitCode.Failed 2"])
  e0(["ExitCode.Success 0"])
  err(["Exception to CommandRunner.RunAsync, ExitCode.Error 1"])

  run --> tscope --> dscope
  dscope -->|"--db given"| hashver
  dscope -->|"no --db"| warn
  hashver -->|"too old or unreadable"| err
  hashver --> warn
  warn --> enum
  enum -->|"exception"| err
  enum --> fixcase
  fixcase -->|"yes"| dry
  dry -->|"yes"| dryloop -->|"keep the names as found"| hasdb
  dry -->|"no"| move
  move -->|"File.Move fails"| err
  move -->|"enumerate again"| enum
  fixcase -->|"no"| hasdb
  hasdb -->|"yes"| catalog --> more
  hasdb -->|"no"| more
  more -->|"no"| round --> more
  more -->|"yes"| totals --> delempty
  delempty -->|"yes"| clean --> exit
  delempty -->|"no"| exit
  exit -->|"yes"| e2
  exit -->|"no"| e0
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

The catalog phase logs a per-file index failure and does not count it. The same file normally fails again in the per-file pipeline, where it is counted. Invalid files fail the run as well as Failed files.

#### Process Dispatch

Each round keeps one media file per directory and stem, so files sharing a stem never run in parallel. Non-media files are never held back.

```mermaid
flowchart TD
  start["ProcessCommand.ExecuteProcessAsync(): start a round"]
  split["ProcessCommand.FixExtensionConflicts(): keep one media file per directory and stem"]
  defer["Hold the other same-stem media files for the next round"]
  par["Parallel.ForEachAsync over the kept files, --threads at a time"]
  task["ProcessTask.ExecuteAsync(): run the per-file pipeline"]
  result{"ProcessResult?"}
  cFailed(["Counted Failed"])
  cInvalid(["Counted Invalid"])
  cDeleted(["Counted Deleted"])
  cModified(["Counted Modified"])
  cSkipped(["Counted Skipped"])
  cNone(["Not counted"])
  gone["Log at Information that the file no longer exists"]
  goneEnd(["Not counted, the run is not failed"])
  cancel(["Rethrown, the run ends ExitCode.Error 1"])
  logFail["Log the exception with the path"]
  merge["Add the held files to the queue that ReProcess filled"]
  empty{"Queue empty?"}
  done(["List cleared, the loop in Process Run ends"])
  next(["Queue becomes the list for the next round"])

  start --> split --> defer --> par
  par -->|"each kept file"| task --> result
  result -->|"Failure"| cFailed
  result -->|"Invalid"| cInvalid
  result -->|"Deleted"| cDeleted
  result -->|"Modified or Reprocess"| cModified
  result -->|"Skipped"| cSkipped
  result -->|"Success or UnknownExtension"| cNone
  task -->|"FileNotFoundException or DirectoryNotFoundException"| gone --> goneEnd
  task -->|"OperationCanceledException"| cancel
  task -->|"any other exception"| logFail --> cFailed
  par -->|"all kept files done"| merge --> empty
  empty -->|"yes"| done
  empty -->|"no"| next
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

Modified counts each step that queued a new name, not each file. A file renamed and then converted counts twice. Outside a dry run no path returns Modified without also queuing, so Modified results come from Reprocess. A dry run queues nothing, so it counts each file it would change once, by the first step that would change it.

#### Process Per-File Pipeline

The steps run in the order drawn, and the first step returning false stops the chain. Every step throws to the per-file catch in ProcessCommand.ExecuteProcessAsync.

```mermaid
flowchart TD
  start["ProcessTask.ExecuteAsync(): one file"]
  exists{"File exists?"}
  goneOk(["Success, not counted"])
  ext{"Supported media extension?"}
  track["SkippedExtensionTracker.Track(): record the extension"]
  unknown(["UnknownExtension, not counted"])
  hasdb{"--db given?"}
  index["IndexTask.IndexFileAsync(): hash, insert or update the row, also under --dryrun, S8"]:::suspect
  hastrash{"--trashdb given?"}
  trashed{"ProcessTask.TryDeleteIfTrashedAsync(): trashed?"}
  del(["Deleted, counted Deleted"])
  trashFail(["Failure, counted Failed, the next run retries the delete"])
  skip{"Row unchanged and processed, and no --reprocess?"}
  skipped(["Skipped, counted Skipped"])
  exif["MediaUtilities.GetExifToolJsonAsync(): read the metadata and validation"]
  valid{"ProcessTask.CheckExifToolValidation(): no validation errors?"}
  invalid(["Invalid, counted Invalid"])
  s1{"ProcessTask.RenameMismatchedMimeExtensions(): passes?"}
  s2{"ProcessTask.RenameMixedCaseExtensions(): passes?"}
  s3{"ProcessTask.DeleteLivePhotosAsync(): passes?"}
  s4{"ProcessTask.ConvertVideoAsync(): passes?"}
  s5["ProcessTask.WarnDngVersion(): warn on a DNG newer than 1.4, always passes"]
  flag{"Which flag did the stopping step set?"}
  reproc(["Reprocess, counted Modified"])
  delStep(["Deleted, counted Deleted"])
  dryMod(["Modified, counted Modified, nothing changed, this run does not mark the row"])
  failStep(["Failure, counted Failed"])
  setdb{"--db given?"}
  mark["Database.SetProcessedAsync(): mark the row processed, also under --dryrun, S8"]:::suspect
  mod{"Modified flag set?"}
  modEnd(["Modified, counted Modified"])
  okEnd(["Success, not counted"])

  start --> exists
  exists -->|"no"| goneOk
  exists -->|"yes"| ext
  ext -->|"no"| track --> unknown
  ext -->|"yes"| hasdb
  hasdb -->|"no"| exif
  hasdb -->|"yes"| index --> hastrash
  hastrash -->|"yes"| trashed
  hastrash -->|"no"| skip
  trashed -->|"Deleted"| del
  trashed -->|"Failure"| trashFail
  trashed -->|"not trashed"| skip
  skip -->|"yes"| skipped
  skip -->|"no"| exif
  exif --> valid
  valid -->|"no"| invalid
  valid -->|"yes"| s1
  s1 -->|"true"| s2
  s2 -->|"true"| s3
  s3 -->|"true"| s4
  s4 -->|"true"| s5
  s1 -->|"false"| flag
  s2 -->|"false"| flag
  s3 -->|"false"| flag
  s4 -->|"false"| flag
  flag -->|"Reprocess flag"| reproc
  flag -->|"Deleted flag only"| delStep
  flag -->|"would-modify flag, set only under --dryrun"| dryMod
  flag -->|"no flag"| failStep
  s5 --> setdb
  setdb -->|"yes"| mark --> mod
  setdb -->|"no"| mod
  mod -->|"yes"| modEnd
  mod -->|"no"| okEnd
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

The trash check runs only with `--db`, before the already-processed skip. A file is marked processed only when every step passes. So this run does not mark a deleted or renamed file, a trashed file whose delete failed, or a file a dry run would change.

#### Process Trash Check

The image hash comes from the cataloged row, and only under `--imagehash`. Without it the image hash lookup always answers no.

```mermaid
flowchart TD
  start["ProcessTask.TryDeleteIfTrashedAsync(): check the SHA-1 and image hash"]
  sha{"TrashDatabase.Sha1ExistsAsync(): SHA-1 in the trash DB?"}
  img{"TrashDatabase.ImageHashExistsAsync(): image hash in the trash DB?"}
  keep(["Returns no result, pipeline continues"])
  dry{"--dryrun?"}
  dryDel(["Returns Deleted, counted Deleted, nothing removed"])
  del["MediaUtilities.TryDeleteFile(): delete with no backup, even without --skipbackup"]
  delOk(["Returns Deleted, row kept, counted Deleted"])
  delFail(["Log an error, return Failure, counted Failed, this run does not mark the row"])

  start --> sha
  sha -->|"yes"| dry
  sha -->|"no"| img
  img -->|"yes"| dry
  img -->|"no"| keep
  dry -->|"yes"| dryDel
  dry -->|"no"| del
  del -->|"deleted"| delOk
  del -->|"IOException or UnauthorizedAccessException"| delFail
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

#### Process Renames

RenameMismatchedMimeExtensions runs first, then RenameMixedCaseExtensions. Neither takes a backup, so undo cannot restore the old name. A failed File.Move throws to the per-file catch.

```mermaid
flowchart TD
  m0["ProcessTask.RenameMismatchedMimeExtensions(): compare the name to the content"]
  m1{"exiftool returned a FileTypeExtension?"}
  m2{"Expected extension is a supported one?"}
  m3["ProcessTask.GetFileMediaExtension(): split the base name and trailing media extensions"]
  m4{"Trailing media extensions equal the expected one?"}
  m5{"--dryrun?"}
  m6["ProcessTask.RenameToFreeName(): base name plus the expected extension"]
  c0["ProcessTask.RenameMixedCaseExtensions(): check the extension case"]
  c1{"ProcessTask.IsMixedCaseExtension(): upper and lower case mixed?"}
  c2{"--dryrun?"}
  c3["ProcessTask.RenameToFreeName(): same name with a lower-case extension"]
  pass(["Step passes, next step runs"])
  dryEnd(["Set the would-modify flag and return false, Modified, counted Modified, nothing renamed"])
  taken{"ProcessTask.IsNameTaken(): target held by another file?"}
  r1["ProcessTask.RenameFile(): move to the target name"]
  r2["MediaUtilities.GetUniqueFileName(): pick name_N, then move there"]
  queue["ProcessTask.ReProcess(): queue the new name and set the Reprocess flag"]
  reproc(["Reprocess, counted Modified"])

  m0 --> m1
  m1 -->|"no"| pass
  m1 -->|"yes"| m2
  m2 -->|"no"| pass
  m2 -->|"yes"| m3 --> m4
  m4 -->|"yes"| pass
  m4 -->|"no"| m5
  m5 -->|"yes"| dryEnd
  m5 -->|"no"| m6 --> taken
  c0 --> c1
  c1 -->|"no"| pass
  c1 -->|"yes"| c2
  c2 -->|"yes"| dryEnd
  c2 -->|"no"| c3 --> taken
  taken -->|"no"| r1 --> queue
  taken -->|"yes"| r2 --> queue
  queue --> reproc
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

The old path keeps its database row and the new name gets a fresh row in the next round.

#### Process Live Photos

Every file reaches this step, and a file that is not .mov or .mp4 passes it unchanged. A GetDurationAsync output that is not a number throws to the per-file catch.

```mermaid
flowchart TD
  l0["ProcessTask.DeleteLivePhotosAsync(): live photo and short clip check"]
  l1{"Extension .mov or .mp4?"}
  pass(["Step passes, next step runs"])
  l2["ProcessTask.GetDurationAsync(): ffprobe format duration"]
  l3{"Duration at or below --duration, default 1s?"}
  sdry{"--dryrun?"}
  dryEnd(["Set the Deleted flag and return false, counted Deleted, nothing removed"])
  sskip{"--skipbackup?"}
  sdel["File.Delete the clip"]
  smark{"ProcessTask.CullFile(): this run converted the clip?"}
  sout["File.Delete the clip and its marker, the original stays in its backup"]
  sbak["ProcessTask.BackupFile(): rename the clip to name.bak"]
  delEnd(["Deleted, counted Deleted"])
  hasdb{"--db given?"}
  k1{"Video has a ContentIdentifier?"}
  k2["Database.GetByPathAsync(): read the video's own row"]
  k3["Database.FindImagePathByContentIdentifierAsync(): a still row, not the video's bytes"]
  k4{"Still row found, even for a file now gone?"}
  long{"Duration 4s or longer?"}
  warnLong["Log a warning that a long clip has a matching still"]
  f1["ProcessTask.FindCompanionImagePath(): same stem still in the same folder"]
  f2{"Companion found?"}
  f3{"Duration 4s or longer?"}
  f4["MediaUtilities.GetExifToolJsonAsync(): read the companion ContentIdentifier"]
  f5{"Both ContentIdentifiers present and equal?"}
  dl["ProcessTask.DeleteLiveVideo(): remove the live video"]
  ddry{"--dryrun?"}
  dskip{"--skipbackup?"}
  ddel["File.Delete the video"]
  dmark{"ProcessTask.CullFile(): this run converted the video?"}
  dout["File.Delete the video and its marker, the original stays in its backup"]
  dbak["ProcessTask.BackupFile(): rename the video to name.bak"]

  l0 --> l1
  l1 -->|"no"| pass
  l1 -->|"yes"| l2 --> l3
  l3 -->|"yes"| sdry
  sdry -->|"yes"| dryEnd
  sdry -->|"no"| sskip
  sskip -->|"yes"| sdel --> delEnd
  sskip -->|"no"| smark
  smark -->|"yes"| sout --> delEnd
  smark -->|"no"| sbak --> delEnd
  l3 -->|"no"| hasdb
  hasdb -->|"yes"| k1
  k1 -->|"no"| pass
  k1 -->|"yes"| k2 --> k3 --> k4
  k4 -->|"no"| pass
  k4 -->|"yes"| long
  long -->|"yes"| warnLong --> pass
  long -->|"no"| dl
  hasdb -->|"no"| f1 --> f2
  f2 -->|"no"| pass
  f2 -->|"yes"| f3
  f3 -->|"yes"| warnLong
  f3 -->|"no"| f4 --> f5
  f5 -->|"no"| pass
  f5 -->|"yes"| dl
  dl --> ddry
  ddry -->|"yes"| dryEnd
  ddry -->|"no"| dskip
  dskip -->|"yes"| ddel --> delEnd
  dskip -->|"no"| dmark
  dmark -->|"yes"| dout --> delEnd
  dmark -->|"no"| dbak --> delEnd
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

The short clip rule needs no still and runs before any pairing. A deleted video keeps its database row.

#### Process Video Conversion

The temporary output is the source path with its extension changed to .temp. A finally block deletes that file whenever it still exists at the end. A single-frame or short GIF becomes a PNG rather than an MP4. As an MP4 it would meet the short clip rule in Process Live Photos in the next round. A GIF that ffprobe gives no duration, such as a GIF87a still, counts as a still too.

```mermaid
flowchart TD
  v0["ProcessTask.ConvertVideoAsync(): pick the ffmpeg job by extension"]
  v1{"Extension .m2t or .mkv?"}
  vg{"Extension .gif?"}
  gd["ProcessTask.IsStillOrShortGifAsync(): ffprobe frame count and format duration"]
  gq{"One frame, duration not a number, or at or below --duration, default 1s?"}
  still["Convert the first frame to a PNG still"]
  v2{"Extension .asf, .wmv, .avi or .3gp?"}
  v3{"Extension .mov or .mp4?"}
  v4{"ProcessTask.IsPcmAudioAsync(): first audio stream is PCM?"}
  pass(["Step passes, next step runs"])
  remux["Remux the video and audio into MP4"]
  reenc["Re-encode to H.264 and AAC in MP4"]
  aac["Re-encode the PCM audio to AAC and copy the video"]
  dry{"--dryrun?"}
  dryEnd(["Set the would-modify flag and return false, Modified, counted Modified, nothing converted"])
  tmp["Delete any existing stem.temp beside the file"]
  ff["ffmpeg writes stem.temp"]
  ffEnd(["Exception to the per-file catch, stem.temp removed, counted Failed"])
  bak["ProcessTask.BackupFile(): rename the original to name.bak"]
  outName["MediaUtilities.GetUniqueFileName(): stem.mp4 or stem.png, or stem_N with it"]
  sb{"--skipbackup?"}
  marker["Write name.bak.out holding the output name"]
  mv["ProcessTask.RenameFile(): move stem.temp to the output name"]
  left["ProcessTask.RollBackConversion(): move name.bak back, remove the marker, then rethrow"]
  leftEnd(["Exception to the per-file catch, counted Failed"])
  commit(["Output in place, continue in Process Video Commit"])

  v0 --> v1
  v1 -->|"yes"| remux --> dry
  v1 -->|"no"| vg
  vg -->|"yes"| gd --> gq
  gq -->|"yes"| still --> dry
  gq -->|"no"| reenc
  vg -->|"no"| v2
  v2 -->|"yes"| reenc --> dry
  v2 -->|"no"| v3
  v3 -->|"no"| pass
  v3 -->|"yes"| v4
  v4 -->|"no"| pass
  v4 -->|"yes"| aac --> dry
  dry -->|"yes"| dryEnd
  dry -->|"no"| tmp --> ff
  ff -->|"non-zero exit or exception"| ffEnd
  ff -->|"exit 0"| bak --> outName --> sb
  sb -->|"no"| marker --> mv
  sb -->|"yes"| mv
  marker -->|"exception"| left
  mv -->|"exception"| left
  left --> leftEnd
  mv --> commit
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

The original is renamed aside in both modes before the output takes a name. Under `--skipbackup` no marker records the pairing. From that rename until the metadata writes land, a failure puts the original back under its own name. An output that cannot be removed leaves both files in place.

#### Process Video Commit

The original is deleted under `--skipbackup` only after both metadata writes land. A failed write rolls the swap back and fails the file.

```mermaid
flowchart TD
  w0["ProcessTask.WriteConvertedMetadataAsync(): put the metadata on the output"]
  w1["MediaUtilities.WriteExifToolAsync(): copy all tags from name.bak"]
  w2{"Copy failed?"}
  w3{"Source has a created date?"}
  w4["MediaUtilities.TrySetCreateDateAsync(): write the created date tags"]
  w5{"Date write failed?"}
  wx["ProcessTask.RollBackConversion(): undo the swap, then rethrow"]
  wxEnd(["Exception to the per-file catch, counted Failed"])
  f0["Log an error naming the output and the original"]
  rb["ProcessTask.RollBackConversion(): undo the swap"]
  rb1{"MediaUtilities.TryDeleteFile(): output removed?"}
  rbKeep["Log an error, the output and name.bak both stay"]
  rb2["ProcessTask.RenameFile(): move name.bak back to the original name"]
  rb3["MediaUtilities.TryDeleteFile(): remove name.bak.out, warn on failure"]
  failEnd(["Failure, counted Failed"])
  sb{"--skipbackup?"}
  delOrig["File.Delete name.bak, the original"]
  rec["ProcessTask.RecordConvertedOutputAsync(): record the output; when this run converted the source, point the original's marker at it and delete the intermediate and its marker"]
  stamp["File.SetLastWriteTimeUtc on the output from the original time"]
  rq["ProcessTask.ReProcess(): queue the output for the next round"]
  rqEnd(["Reprocess, counted Modified"])

  w0 --> w1 --> w2
  w1 -->|"exception"| wx
  w2 -->|"yes"| f0
  w2 -->|"no"| w3
  w3 -->|"no"| sb
  w3 -->|"yes"| w4 --> w5
  w4 -->|"exception"| wx
  w5 -->|"yes"| f0
  w5 -->|"no"| sb
  wx --> wxEnd
  f0 --> rb --> rb1
  rb1 -->|"no"| rbKeep --> failEnd
  rb1 -->|"yes"| rb2 --> rb3 --> failEnd
  rb2 -->|"exception"| wxEnd
  sb -->|"yes"| delOrig --> stamp
  sb -->|"no"| rec --> stamp
  rec -->|"exception"| wxEnd
  delOrig -->|"exception"| wxEnd
  stamp -->|"exception"| wxEnd
  stamp --> rq --> rqEnd
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

The queued output runs the whole pipeline again in the next round. That includes the short clip rule in Process Live Photos.

### Import

#### Import Run

The command lists every file under `--path` before it opens any database. It then opens `--trashdb`, `--skipdb`, and `--db` in that order, nested, and runs the files in parallel.

```mermaid
flowchart TD
  start["ImportCommand.ExecuteAsync(): start the import"] --> runner["CommandRunner.RunAsync(): wrap the whole run"]
  runner --> enum["FileEnumerator.Enumerate(): list every file under --path"]
  enum -->|"exception, e.g. unreadable subdirectory"| exitError(["Exit 1 Error"])
  enum --> trashQ{"--trashdb given?"}
  trashQ -->|"yes"| trashOpen["TrashDatabase.InitializeAsync(): open read-only, validate the schema"]
  trashQ -->|"no"| skipQ{"--skipdb given?"}
  trashOpen --> skipQ
  skipQ -->|"yes"| skipOpen["Database.InitializeAsync(): open the skip database read-only"]
  skipQ -->|"no"| dbQ{"--db given?"}
  skipOpen --> dbQ
  dbQ -->|"yes"| dbOpen["Database.InitializeAsync(): create or upgrade the catalog, also on --dryrun, S8"]:::suspect
  dbOpen --> dbVer["MediaUtilities.EnsureImageHashSupportAsync(): require exiftool 12.63 or newer"]
  dbQ -->|"no"| ihQ{"--imagehash?"}
  dbVer --> ihQ
  ihQ -->|"yes"| warn["TrashDatabase.WarnIfImageHashMissing(): warn when trash db has no image_hash"]
  warn --> ihVer["MediaUtilities.EnsureImageHashSupportAsync(): require exiftool 12.63 or newer"]
  ihQ -->|"no"| exec["ImportTask.ExecuteAsync(): run files in parallel, up to --threads"]
  ihVer --> exec
  exec -->|"each file"| perFile["ImportTask.ImportFileAsync(): import one file, see the stages below"]
  perFile -->|"returns a result"| release["ImportTask.ReleaseClaims(): keep claims if Imported or a moved file was kept, else free them"]
  release --> counted(["Counted under the returned result"])
  perFile -->|"exception other than cancel"| perFileCatch["ImportTask.ReleaseClaims(): free claims unless a moved file was kept, then log the file"]
  perFileCatch --> countedFailed(["Counted Failed"])
  perFile -->|"cancel"| exitError
  exec -->|"all files done"| deQ{"--deleteempty?"}
  deQ -->|"no"| summary["ImportCommand.ExecuteAsync(): log every counter"]
  deQ -->|"yes"| outQ{"--outpath exists and is readable?"}
  outQ -->|"no"| outMissing["Throws after all files ran, counters never logged, S11"]:::suspect
  outMissing --> exitError
  outQ -->|"yes"| clean["DirectoryCleaner.DeleteEmptyDirectories(): prune --outpath only, never --path"]
  clean --> summary
  summary --> exitQ{"Failed or Invalid above 0?"}
  exitQ -->|"yes"| exitFailed(["Exit 2 Failed"])
  exitQ -->|"no"| exitSuccess(["Exit 0 Success"])
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

Any exception before `ImportTask.ExecuteAsync()` starts, such as a bad schema or an old exiftool, reaches `CommandRunner.RunAsync()` and exits 1. Ignored, Skipped, SkippedBySkipDb, and TrashedInImmich all leave the exit code at 0.

#### Import Per-File Checks

Each file runs the checks below in this order. The first check that matches decides the result. Exceptions from every step here go to the per-file catch in `ImportTask.ExecuteAsync()`, which counts the file Failed.

```mermaid
flowchart TD
  start["ImportTask.ImportFileAsync(): start one file"] --> extQ{"Extension in the supported media list?"}
  extQ -->|"no"| track["SkippedExtensionTracker.Track(): note the extension"]
  track --> ignored(["Counted Ignored"])
  extQ -->|"yes"| anyDbQ{"Any of --db, --skipdb, --trashdb given?"}
  anyDbQ -->|"no"| metaQ{"Metadata already read for --imagehash?"}
  anyDbQ -->|"yes"| cache["Database.GetByPathAsync(): read the --db row for the source path"]
  cache --> hash["Database.ResolveHashesAsync(): reuse cached hashes if size and mtime match without --rehash"]
  hash --> trashQ{"TrashDatabase.Sha1ExistsAsync(): --trashdb holds the SHA-1?"}
  trashQ -->|"yes"| trashed(["Counted TrashedInImmich"])
  trashQ -->|"no, or no --trashdb"| skipQ{"Database.Sha256ExistsAsync(): --skipdb holds the SHA-256?"}
  skipQ -->|"yes"| skipDb(["Counted SkippedBySkipDb"])
  skipQ -->|"no, or no --skipdb"| dbQ{"Database.Sha256ExistsAsync(): --db holds the SHA-256?"}
  dbQ -->|"yes"| skipped(["Counted Skipped"])
  dbQ -->|"no, or no --db"| ihQ{"--imagehash with --db or --trashdb?"}
  ihQ -->|"no"| claimDbQ{"--db given?"}
  ihQ -->|"yes"| ihMeta["ImportTask.GetFileMetaAsync(): catalog read with the image data hash"]
  ihMeta --> ihEmpty{"Image data hash present?"}
  ihEmpty -->|"no"| claimDbQ
  ihEmpty -->|"yes"| ihTrash{"TrashDatabase.ImageHashExistsAsync(): trash db holds the image hash?"}
  ihTrash -->|"yes"| trashed
  ihTrash -->|"no"| ihDb{"Database.FindPathByImageHashAsync(): a --db row has the image hash?"}
  ihDb -->|"yes"| skipped
  ihDb -->|"no"| claimDbQ
  claimDbQ -->|"no"| metaQ
  claimDbQ -->|"yes"| claim{"ImportTask.ClaimedByAsync(): SHA-256 or image hash held in this run?"}
  claim -->|"held, owner imported or kept its moved file"| skipped
  claim -->|"held, owner freed the claim"| claim
  claim -->|"this file takes the claim"| metaQ
  metaQ -->|"yes"| validate
  metaQ -->|"no"| meta["ImportTask.GetFileMetaAsync(): read metadata, an IOException gives none"]
  meta --> validate{"ExifToolJson.ParseValidate(): validate verdict counts errors?"}
  validate -->|"yes"| invalid(["Counted Invalid"])
  validate -->|"no, warnings ignored"| next(["Continues in Import Destination"])
```

A claim waiter blocks until the owner finishes, then loops if the owner freed the claim. An owner that kept its moved file in `--outpath` holds the claim, though it counts Failed. A file is Skipped by a claim only after it passed every database check.

#### Import Destination

The folder date comes from the inferred path date, else the parsed metadata date, else `DateTime.MinValue`. A dry run ends here, before any file or database write.

```mermaid
flowchart TD
  start["ExifToolJson.GetDate(): walk the date tags in priority order, Matroska and RIFF last"]
  start --> dpQ{"--datepath?"}
  dpQ -->|"no"| getDate
  dpQ -->|"yes"| setQ{"ExifToolJson.IsDateSet(): metadata has a date that parses?"}
  setQ -->|"yes"| getDate{"ExifToolJson.GetDate(): date string parses, with or without sub-seconds, a time zone offset, or a seconds part?"}
  setQ -->|"no"| infer{"DateFromPath.InferCreatedDate(): date in the file name or directories?"}
  infer -->|"yes"| inferred["Use the inferred date for the folder and the later date write"]
  infer -->|"no"| inferWarn["Log a warning, no inferred date"]
  inferWarn --> getDate
  getDate -->|"yes"| build
  getDate -->|"no"| minDate["Folder date is DateTime.MinValue, so 0001/01/01"]
  minDate --> build
  inferred --> build["ImportTask.BuildDestinationPath(): --outpath, date by --format, file name"]
  build --> unique["MediaUtilities.GetUniqueFileName(): add _1, _2 when the name exists now"]
  unique --> writeQ{"Detected file type in the exiftool write list?"}
  writeQ -->|"no"| dropped["ImportTask.WarnSkippedWrites(): warn naming the file and the tags or date it skips, S14"]:::suspect
  writeQ -->|"yes"| tags["ImportTask.ComputePathTags(): directory names under --path, with --tagpath"]
  tags --> addTags["Add the --tags values"]
  dropped --> dryQ{"--dryrun?"}
  addTags --> dryQ
  dryQ -->|"yes"| dryImported(["Counted Imported, nothing written"])
  dryQ -->|"no"| next(["Continues in Import Copy and Writes"])
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

`ExifToolJson.IsDateSet()` is true only when `GetDate()` parses the date, so `--datepath` is tried for a zero or malformed date too. A date with a time zone offset files under the clock time it records, the same reading a date without an offset gets. A `Z` date, as Matroska stores, is that UTC clock time. `GetDate()` takes the first date tag that parses, so a zero date in one tag does not hide a valid one in another. The Matroska date is read from `Matroska:DateTimeOriginal`, with `RIFF:DateTimeOriginal` after it for an AVI.

#### Import Copy and Writes

The created-date write runs before the tag write, and the mtime restore runs after both. The database row describes the source path and its hashes, not the destination.

```mermaid
flowchart TD
  start["ImportTask.ImportFileAsync(): read source size and mtime, create the date folder"]
  start --> mvQ{"--move?"}
  mvQ -->|"yes"| move["Move the source to the destination"]
  mvQ -->|"no"| copy["Copy the source to the destination"]
  move -->|"IOException, name now taken"| race["A parallel file took the unique name first, S13"]:::suspect
  copy -->|"IOException, name now taken"| race
  move -->|"other IOException or access denied"| ioCatch
  copy -->|"other IOException or access denied"| ioCatch
  race --> ioCatch["Log failed to copy or move"]
  ioCatch --> failed(["Counted Failed"])
  move --> dateQ{"Inferred date and writable type?"}
  copy --> dateQ
  dateQ -->|"yes"| dateW["MediaUtilities.TrySetCreateDateAsync(): write the created date"]
  dateW --> dateOk{"ImportTask.ReportWrite(): created-date write succeeded?"}
  dateQ -->|"no"| tagQ{"Any tags to apply?"}
  dateOk -->|"yes"| tagQ
  dateOk -->|"no"| writeMvQ{"--move?"}
  tagQ -->|"yes"| tagW["ImportTask.ApplyTagsAsync(): write XMP Subject tags"]
  tagW --> tagOk{"ImportTask.ReportWrite(): tag write succeeded?"}
  tagOk -->|"no"| writeMvQ
  writeMvQ -->|"no"| rollback["ImportTask.RemoveCopy(): undo the copy"]
  writeMvQ -->|"yes, keep the moved file"| mtime
  tagQ -->|"no"| mtime["ImportTask.RestoreSourceTime(): restore the source mtime on the destination"]
  tagOk -->|"yes"| mtime
  mtime -->|"IOException or access denied"| timeMvQ{"--move?"}
  timeMvQ -->|"no"| rollback
  timeMvQ -->|"yes, keep the moved file"| rowQ
  rollback --> delQ{"Copy removed?"}
  delQ -->|"yes"| failed
  delQ -->|"no"| copyLeft["Copy stays with a warning, next run copies it again as _1, S12"]:::suspect
  copyLeft --> failed
  mtime --> rowQ{"ImportTask.WriteRowAsync(): --db given?"}
  rowQ -->|"no"| keptQ
  rowQ -->|"yes"| cachedQ{"Cached row for the source path?"}
  cachedQ -->|"no"| insert["Database.InsertAsync(): insert or ignore the source row"]
  cachedQ -->|"yes"| update["Database.UpdateHashesAsync(): new hashes, metadata, reset is_processed"]
  insert --> keptQ{"Moved file kept after a failed write?"}
  update --> keptQ
  keptQ -->|"no"| imported(["Counted Imported"])
  keptQ -->|"yes"| keepMoved["Moved file kept in --outpath, row written under --db, writes missing, claims kept"]
  keepMoved --> failed
  insert -->|"other exception"| rowMvQ{"--move?"}
  update -->|"other exception"| rowMvQ
  rowMvQ -->|"no"| rollback
  rowMvQ -->|"yes"| rowFail["Escapes to the per-file catch, moved file kept, no row, claims kept, S16"]:::suspect
  rowFail --> failed
  insert -->|"cancel"| cancelQ
  update -->|"cancel"| cancelQ
  dateW -->|"cancel"| cancelQ{"--move?"}
  tagW -->|"cancel"| cancelQ
  cancelQ -->|"no"| cancelDel["ImportTask.RemoveCopy(): remove the copy"]
  cancelDel --> exitError(["Rethrown, run exits 1 Error"])
  cancelDel -->|"removal fails"| cancelLeft["Copy stays with a warning, next run copies it again as _1, S12"]:::suspect
  cancelLeft --> exitError
  cancelQ -->|"yes"| cancelMoved["Moved file stays, no row"]
  cancelMoved --> exitError
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

Inside this stage a cancel removes the copy in copy mode and rethrows, even when the copy completed. In copy mode any other failure after the copy also removes the copy, before the file's claims are freed. So a failed write, a failed mtime restore, or a `SqliteException` from the row write leaves the source to import cleanly on the next run. A `SqliteException` still reaches the per-file catch in `ImportTask.ExecuteAsync()`, which counts the file Failed. A copy that cannot be removed stays with a warning instead, S12.

A moved file has no source left to return to, so a failed write keeps it in `--outpath`. It gets the source mtime unless the mtime restore is the write that failed. Its row is still written, keyed by source path and hash as for an import, so a later copy is skipped. The file is counted Failed, and the error names the write that failed. Its claims stay held, so a matching file later in the run is skipped too. Any other exception after a move also keeps the claims, since the moved file is in the library without its row.

### Index

#### Index Run

The index command requires `--db` and opens it read-write. It has no `--dryrun`, and it never removes a row.

```mermaid
flowchart TD
  start["IndexCommand.ExecuteAsync(): start the index"] --> runner["CommandRunner.RunAsync(): wrap the whole run"]
  runner --> enum["FileEnumerator.Enumerate(): list every file under --path"]
  enum -->|"exception"| exitError(["Exit 1 Error"])
  enum --> open["DatabaseScope.RunAsync(): create the --db folder, open read-write"]
  open --> init["Database.InitializeAsync(): create the table, upgrade the schema"]
  init --> ver["MediaUtilities.EnsureImageHashSupportAsync(): require exiftool 12.63 or newer"]
  init -->|"exception"| exitError
  ver -->|"too old or not found"| exitError
  ver --> exec["IndexTask.ExecuteAsync(): run files in parallel, up to --threads"]
  exec -->|"each file"| extQ{"Extension in the supported media list?"}
  extQ -->|"no"| track["SkippedExtensionTracker.Track(): note the extension"]
  track --> ignored(["Counted Ignored"])
  extQ -->|"yes"| file["IndexTask.IndexFileAsync(): index one file, see Index Per-File"]
  file -->|"Inserted"| inserted(["Counted Inserted"])
  file -->|"Updated"| updated(["Counted Updated"])
  file -->|"Unchanged"| unchanged(["Counted Unchanged"])
  file -->|"exception other than cancel"| failed(["Counted Failed"])
  file -->|"cancel"| exitError
  exec -->|"all files done"| stale["Rows for files gone from --path stay in --db"]
  stale --> summary["IndexCommand.ExecuteAsync(): log every counter"]
  summary --> exitQ{"Failed above 0?"}
  exitQ -->|"yes"| exitFailed(["Exit 2 Failed"])
  exitQ -->|"no"| exitSuccess(["Exit 0 Success"])
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

#### Index Per-File

Metadata is read only for a new row, new content, or a row whose metadata was never read. Exceptions from every step here go to the per-file catch in `IndexTask.ExecuteAsync()`, which counts the file Failed. A cancel is rethrown instead, and the run exits 1.

```mermaid
flowchart TD
  start["IndexTask.IndexFileAsync(): index one file"] --> cache["Database.GetByPathAsync(): read the row for this path"]
  cache --> hash["Database.ResolveHashesAsync(): reuse hashes if size and mtime match without --rehash, else hash"]
  hash -->|"exception, e.g. file gone"| failed(["Counted Failed"])
  hash --> needQ{"No row, new SHA-256, or row metadata unread?"}
  needQ -->|"yes"| read["IndexTask.ReadMetadataAsync(): catalog read, an exiftool error leaves it unread"]
  needQ -->|"no"| rowQ{"Row exists?"}
  read --> rowQ
  rowQ -->|"no"| insert["Database.InsertAsync(): size and mtime read after hashing"]
  insert --> inserted(["Counted Inserted"])
  rowQ -->|"yes"| shaQ{"SHA-256 differs from the row?"}
  shaQ -->|"yes"| update["Database.UpdateHashesAsync(): new hashes and metadata, reset is_processed"]
  update --> updated(["Counted Updated"])
  shaQ -->|"no"| metaQ{"Metadata read in this run?"}
  metaQ -->|"yes"| backfill["Database.SetMetadataAsync(): backfill the metadata columns"]
  metaQ -->|"no"| sizeQ{"Size or mtime differs from the row?"}
  backfill --> sizeQ
  sizeQ -->|"no"| unchanged(["Counted Unchanged"])
  sizeQ -->|"yes"| stat["Database.UpdateMetadataAsync(): store SHA-1, size, and mtime"]
  stat --> procQ{"Row is_processed?"}
  procQ -->|"yes"| unchanged
  procQ -->|"no"| updated
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

`--processed` sets `is_processed` only on an inserted row. Counted Updated also covers an unprocessed row whose content is the same but whose size or mtime moved.

### Trash

#### Trash Setup and Exit

The trash command opens the trash database read-write and the process database read-only. The sync itself is drawn in the next diagram.

```mermaid
flowchart TD
  T1["CommandRunner.RunAsync(): run the trash work"] --> T2["TrashDatabaseScope.RunAsync(): open the trash database"]
  T2 --> T3{"--trashdb given?"}
  T3 -->|"no"| T4["TrashCommand.ExecuteAsync(): throw, trash database path is required"]
  T4 -->|"exception"| TE
  T3 -->|"yes"| T3a["Create the --trashdb folder"]
  T3a --> T5["TrashDatabase.InitializeAsync(): create table, add image_hash column and index"]
  T5 -->|"exception"| TE
  T5 --> T6{"--processdb given?"}
  T6 -->|"yes"| T7["Database.InitializeAsync(): open --processdb read-only, check the files table"]
  T7 -->|"exception"| TE
  T7 --> T8["TrashCommand.SyncAsync(): fetch and store hashes, see next diagram"]
  T6 -->|"no"| T8
  T8 -->|"exception"| TE
  T8 -->|"cancel or HTTP timeout"| T9["CommandRunner.RunAsync(): log Trash cancelled at Information, S15"]:::suspect
  T9 --> T10(["Exit 1, Error"])
  T8 --> T11{"partial flag set?"}
  T11 -->|"yes"| T12(["Exit 2, Failed"])
  T11 -->|"no"| T13(["Exit 0, Success"])
  TE["CommandRunner.RunAsync(): log the exception"] --> T14(["Exit 1, Error"])
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

Both databases are disposed in a finally block whichever way the work ends. A required --trashdb makes the missing-path branch unreachable from the command line.

#### Trash Fetch and Store

Each page is committed in its own transaction before the next page is fetched. A page that throws therefore leaves every earlier page stored and skips the image hash backfill.

```mermaid
flowchart TD
  S1["TrashCommand.SyncAsync(): warn if --processdb lacks image_hash, count rows"] --> S2["TrashCommand.CreateDefaultHttpClient(): needs --url and an API key"]
  S2 -->|"missing, throws"| SE
  S2 --> S3["TrashCommand.FetchTrashedAssetsAsync(): POST api/search/metadata, 1000 per page"]
  S3 -->|"HTTP error or bad JSON"| SE
  S3 --> S4{"page has items?"}
  S4 -->|"no"| S5["TrashCommand.SyncAsync(): stop paging without reading NextPage, S15"]:::suspect
  S4 -->|"yes"| S6{"for each asset: isTrashed?"}
  S6 -->|"no"| S7["Log warning, skip the asset"]
  S6 -->|"yes"| S8["TrashCommand.ConvertChecksum(): base64 checksum to hex SHA-1"]
  S8 -->|"empty, bad base64, or not 20 bytes"| S7
  S8 -->|"valid"| S9["Add SHA-1 and file name to the page batch"]
  S7 -->|"after the last asset"| S10{"batch has rows?"}
  S9 -->|"after the last asset"| S10
  S10 -->|"yes"| S11["TrashDatabase.InsertHashesAsync(): upsert the page, rows never removed"]
  S10 -->|"no"| S12
  S11 --> S12{"NextPage value?"}
  S12 -->|"integer above this page"| S3
  S12 -->|"null"| S14
  S12 -->|"any other value"| S13["Log error, set the partial flag, stop paging"]
  S13 --> S14
  S5 --> S14["TrashDatabase.GetCountAsync(): log fetched and newly inserted counts"]
  S14 --> S15{"--processdb given?"}
  S15 -->|"yes"| S16["TrashDatabase.GetSha1sWithoutImageHashAsync(): rows lacking an image hash"]
  S16 --> S17["Database.GetImageHashBySha1Async(): look up each SHA-1, null without the column"]
  S17 --> S18["TrashDatabase.InsertHashesAsync(): set image_hash where it is still null"]
  S18 --> S19
  S15 -->|"no"| S19{"partial flag set?"}
  S19 -->|"yes"| S20(["Exit 2, Failed"])
  S19 -->|"no"| S21(["Exit 0, Success"])
  SE["CommandRunner.RunAsync(): log the exception"] --> S22(["Exit 1, Error, earlier pages stay stored"])
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

Exceptions from every step, including the count and backfill steps, go to CommandRunner.RunAsync() and exit 1. The upsert keeps an existing file name and only fills an image hash that is still null.

### Undo

#### Undo Inputs

Undo has no database. It reverses only what process left on disk as backup files and .out markers. This diagram shows those states as ProcessTask writes them.

```mermaid
flowchart TD
  P1{"ProcessTask.ExecuteAsync(): which change?"}
  P1 -->|"short clip"| P2["ProcessTask.DeleteLivePhotosAsync(): video at or under --duration seconds"]
  P1 -->|"live photo video"| P3["ProcessTask.DeleteLiveVideo(): video paired with a still"]
  P2 --> P4{"--skipbackup?"}
  P3 --> P4
  P4 -->|"yes"| P5(["Deleted, nothing for undo"])
  P4 -->|"no"| P4a{"ProcessTask.CullFile(): this run converted this file?"}
  P4a -->|"yes"| P4b(["Output and its marker deleted, the original's backup stays for undo"])
  P4a -->|"no"| P6["ProcessTask.BackupFile(): rename to .bak, or .bakN when taken"]
  P6 --> P7(["On disk: backup only, no .out marker"])
  P1 -->|"convert video"| P8["ProcessTask.ConvertVideoAsync(): ffmpeg writes name.temp"]
  P8 -->|"ffmpeg fails"| P9(["Original untouched, temp removed, file fails"])
  P8 --> P10["ProcessTask.BackupFile(): rename the original to .bak or .bakN"]
  P10 --> P11{"--skipbackup?"}
  P11 -->|"no"| P12["ProcessTask.ConvertVideoAsync(): write backup.out holding the absolute output path"]
  P11 -->|"yes"| P13
  P12 --> P13["ProcessTask.ConvertVideoAsync(): rename temp to name.mp4, name.png for a GIF still, or name_N"]
  P13 --> P14["ProcessTask.WriteConvertedMetadataAsync(): copy tags, set created date"]
  P14 -->|"fails or throws"| P15["ProcessTask.RollBackConversion(): delete output, rename backup back, drop marker"]
  P15 -->|"output delete fails"| P16(["On disk: backup and output, plus the marker without --skipbackup, file fails"])
  P15 --> P17(["Original back under its name, file fails"])
  P14 -->|"ok"| P18{"--skipbackup?"}
  P18 -->|"yes"| P19(["Original deleted, output only, nothing for undo"])
  P18 -->|"no"| P18a{"this run converted the source too?"}
  P18a -->|"yes"| P18b["ProcessTask.RecordConvertedOutputAsync(): point the original's marker at the new output, delete the intermediate and its marker"]
  P18b --> P20
  P18a -->|"no"| P20(["On disk: backup, backup.out, output queued for reprocess"])
  P1 -->|"rename extension or case"| P21(["Renamed with no backup, undo cannot reverse it"])
  P1 -->|"trashed in Immich"| P22(["Deleted with no backup, undo cannot reverse it"])
```

A converted output is reprocessed in the same run, and the run records it. A short clip or live photo video output is then deleted with its marker, since its original already sits in a backup. A second conversion of it leaves one marker on the original. Only the run's own record decides this, never a marker found on disk, which can be stale. Deletion backups never get a marker. A later run, or an older release, backs up such an output instead, since the record lasts one run.

#### Undo Grouping

Undo acts only on what process recorded. It reads every backup of supported media and its .out marker before it changes anything. One backup's marker can name a file another backup holds.

```mermaid
flowchart TD
  U1["CommandRunner.RunAsync(): run the undo work"] --> U2["FileEnumerator.Enumerate(): list every file under --path"]
  U2 -->|"exception"| UE
  U2 --> U3["UndoTask.IsUndoableBackup(): keep names ending .bak or .bakN whose base is supported media"]
  U3 -->|"another type"| U4(["Skipped, left as it is"])
  U3 --> U5{"any backups?"}
  U5 -->|"no"| U6(["Exit 0, Success, nothing to undo"])
  U5 -->|"yes"| U7["UndoTask.GetBackupSortKey(): order by base name, then .bak, .bak1, .bak2"]
  U7 --> U8{"backup readable and not empty?"}
  U8 -->|"empty, vanished or unreadable"| U9(["Counted Failed, backup left, the run continues"])
  U8 -->|"yes"| U10{"marker named after this backup exists?"}
  U10 -->|"no"| U13
  U10 -->|"yes"| U11["UndoTask.ResolveMarkerOutput(): take the output's file name, beside the backup"]
  U11 -->|"marker unreadable"| U9
  U11 --> U13["UndoTask.Execute(): reverse each recorded output, then restore each original, see the next two diagrams"]
  U13 -->|"cancelled"| U14(["Exit 1, Error"])
  U13 --> U15{"failed count above zero?"}
  U15 -->|"yes"| U16(["Exit 2, Failed"])
  U15 -->|"no"| U17(["Exit 0, Success"])
  UE["CommandRunner.RunAsync(): log the exception, counters never logged"] --> U18(["Exit 1, Error"])
```

A file or marker error fails only its own backup. Under --dryrun, UndoTask.TryDelete() and UndoTask.TryRestore() change nothing on disk but record the result. A dry run so picks the same free names as a live run, where the volume's case rules match the OS default.

#### Undo Converted Outputs

Each output a marker names is reversed before any original is restored, so an original can take back a name its output held. A backup holding a gone output's name may be that output, culled by a later run or an older release. Undo restores it as an original, which leaves an extra file but never loses one.

```mermaid
flowchart TD
  C1{"output exists?"}
  C1 -->|"no"| C2(["Logged, nothing to delete, every backup holding its name is restored as an original"])
  C1 -->|"yes"| C3{"one marker names it, and no backup holds its name?"}
  C3 -->|"no"| C4(["Counted Failed, warned, the output, its markers and every backup in the group are left"])
  C3 -->|"yes"| C5["UndoTask.TryDelete(): delete the converted output"]
  C5 -->|"fails"| C6(["Counted Failed, the marker and its backup are left for a rerun"])
  C5 -->|"ok"| C7(["Counted Deleted"])
```

A backup can hold the name of an output that still exists. It may be the output culled by a later run or an older release, or an original backed up before the conversion took its name. Undo cannot tell them apart, so it leaves that group.

#### Undo Restore

Every backup in a group not left above is an original. Undo never deletes a backup, and never overwrites a current file.

```mermaid
flowchart TD
  R1{"its group was left above?"}
  R1 -->|"yes"| R2(["Warned, backup and marker left as they are"])
  R1 -->|"no"| R3["MediaUtilities.GetUniqueFileName(): the base name, or the first name_N no file and no other original's base holds"]
  R3 -->|"name_N"| R4["UndoTask.Execute(): warn, naming the taken file and the free name"]
  R3 --> R5
  R4 --> R5["UndoTask.TryRestore(): move the backup to that name"]
  R5 -->|"IO or access error"| R6(["Counted Failed, backup kept"])
  R5 -->|"ok"| R7{"backup has a marker?"}
  R7 -->|"no"| R8(["Counted Restored"])
  R7 -->|"yes"| R9["UndoTask.TryDelete(): delete the marker"]
  R9 -->|"fails"| R10(["Counted Restored, marker Counted Failed"])
  R9 -->|"ok"| R11(["Counted Restored, marker Counted Deleted"])
```

An output from a release before the .out marker has no record, so it stays beside its restored original.

### Verify

#### Verify Screening

Batches of 1024 files run in parallel, up to --threads at once. A per-file catch turns any screening exception into a Failed count, except cancellation.

```mermaid
flowchart TD
  V1["CommandRunner.RunAsync(): run the verify work"] --> V2["FileEnumerator.Enumerate(): list every file under --path"]
  V2 -->|"exception"| VE
  V2 --> V3["DatabaseScope.RunAsync(): open --db read-write when given, no exiftool check"]
  V3 -->|"exception"| VE
  V3 --> V4["VerifyTask.RunAsync(): chunk into batches of 1024, run --threads at once"]
  V4 --> V5["VerifyTask.ShouldVerifyAsync(): screen each file of the batch"]
  V5 --> V6{"supported media extension?"}
  V6 -->|"no"| V7(["Counted Ignored"])
  V6 -->|"yes"| V8{"file still exists?"}
  V8 -->|"no"| V9(["Counted Failed"])
  V8 -->|"yes"| V10{"--db given?"}
  V10 -->|"yes"| V11["IndexTask.IndexFileAsync(): hash or reuse cache, insert or update the row"]
  V11 -->|"exception"| V9
  V11 --> V12{"row unchanged, is_processed set, no --reprocess?"}
  V12 -->|"yes"| V13(["Counted Skipped, flag shared with process"])
  V12 -->|"no"| V14
  V10 -->|"no"| V14["MediaUtilities.EnsureReadable(): open the file once"]
  V14 -->|"exception"| V9
  V14 --> V15["Add the file to the batch candidates"]
  V15 --> V16{"batch has candidates?"}
  V16 -->|"no"| V20
  V16 -->|"yes"| V17["VerifyTask.PreflightAsync(): once per run, load Immich modules in Docker"]
  V17 -->|"no docker, exit non-zero, or no sentinel"| VE
  V17 --> V18["VerifyTask.DecodeBatchAsync(): decode the batch, see next diagram"]
  V18 -->|"exception"| VE
  V18 --> V20{"after all batches: Invalid or Failed above zero?"}
  V20 -->|"yes"| V21(["Exit 2, Failed"])
  V20 -->|"no"| V22(["Exit 0, Success"])
  VE["CommandRunner.RunAsync(): log the exception, other batches cancelled"] --> V23(["Exit 1, Error"])
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

The preflight runs only when some batch has a candidate. A tree of non-media or already verified files never needs Docker.

#### Verify Decode Batch

One container judges a whole batch, reading container paths on stdin. Each verdict is matched back to its host file by container path.

```mermaid
flowchart TD
  B1{"path has a line break?"}
  B1 -->|"yes"| B2(["Counted Failed"])
  B1 -->|"no"| B3{"VerifyTask.TryToContainerPath(): path stays under --path?"}
  B3 -->|"no"| B2
  B3 -->|"yes"| B4["VerifyTask.RunImmichAsync(): docker run Immich, read-only bind mount, paths on stdin"]
  B4 -->|"fails to start, not a cancel"| B5(["Every mapped file Counted Failed"])
  B4 --> B6{"Verify script: video extension?"}
  B6 -->|"yes"| B7["Verify script: probe, transcode one preview frame"]
  B6 -->|"no"| B8["Verify script: RAW preview extract best effort, decode a preview"]
  B7 --> B9["Verify script: print one JSON verdict per path"]
  B8 --> B9
  B9 --> B10{"container exit code non-zero?"}
  B10 -->|"yes"| B11["Log the exit code and stderr, keep going"]
  B10 -->|"no"| B12
  B11 --> B12["VerifyTask.ParseLine(): parse stdout lines, key verdicts by container path"]
  B12 --> B13{"verdict returned for this file?"}
  B13 -->|"no"| B2
  B13 -->|"yes"| B14{"verdict ok?"}
  B14 -->|"no"| B15{"host file still exists?"}
  B15 -->|"no"| B2
  B15 -->|"yes"| B16["Log Immich cannot render, also for a symlink leaving the mount"]
  B16 --> B17(["Counted Invalid"])
  B14 -->|"yes"| B18{"--db given?"}
  B18 -->|"yes"| B19["Database.SetProcessedAsync(): set is_processed to 1"]
  B19 -->|"exception"| B20(["Exit 1, Error, the run aborts"])
  B19 --> B21(["Counted Verified"])
  B18 -->|"no"| B21
  classDef suspect stroke:#c00,stroke-width:2px,stroke-dasharray:5 3
```

A verdict the container never printed is a tooling gap and counts Failed, never Invalid. Only Invalid and Failed make the exit code 2.

### Suspect Branches

Each entry describes a branch the diagrams mark, as the code behaves today, and links the issue that tracks its fix. Update the diagram and remove the entry when the fix lands.

- **S8.** A dry run with `--db` creates, upgrades and writes the database, #316.
- **S11.** `import --deleteempty` throws when `--outpath` does not exist, so the run exits 1, #319.
- **S12.** A copy that cannot be removed after a failed import stays with a warning, and the next run imports it again as `_1`, #338.
- **S13.** Two parallel imports with the same name and date can pick the same destination, #321.
- **S14.** For a detected type outside the exiftool write list, `import` skips `--tags`, `--tagpath` and the inferred date with only a warning, #334.
- **S15.** `trash` stops at an empty page without marking the sync partial, and logs a timeout as a cancel, #323.
- **S16.** A moved file whose row write fails stays in `--outpath` with no row, so the next run imports a copy of it again, #340.

## State Management Pattern

- **Primary Constructor Parameters**: Command and task classes use C# 12 primary constructors. All task classes take `CommandLine.Options options` as their first parameter, plus any non-option runtime params (e.g., `Database`, shared collections). Command classes take `(CommandLine.Options options, CancellationToken cancellationToken)` and pass `options` directly to task constructors.
- **Command/Task Separation**: Command classes (e.g., `ProcessCommand`) handle orchestration (file enumeration, DB lifecycle, result logging), while task classes (e.g., `ProcessTask`) handle per-file business logic
- **Composable Infrastructure**: `CommandRunner`, `DatabaseScope`, and `FileEnumerator` are static helpers freely composed by command classes, no inheritance hierarchy
- **Shared Collections**: `ConcurrentBag<string>` for file names, `ConcurrentDictionary<string, byte>` for unknown extensions with case-insensitive comparison
- **Parallel Processing**: Files processed using `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`
- **External Tool Integration**: Uses `CliWrap` for all external command execution (exiftool, ffmpeg, ffprobe)
- **FrozenSet Collections**: All static readonly extension collections use `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` for O(1) lookups

## Key Patterns & Conventions

### External Tool Execution Pattern

```csharp
BufferedCommandResult result = await Cli.Wrap("exiftool")
    .WithArguments(["-groupNames", "-json", "-validate", "-all", filePath])
    .ExecuteBufferedAsync();
```

- Always use array syntax for arguments: `["-arg1", "value"]`
- Use `BufferedCommandResult` for output capture, `CommandResult` for fire-and-forget
- JSON trimming pattern: `result.StandardOutput.AsSpan().Trim([' ', '\n', '\r', '[', ']'])`

### Media File Processing Conventions

- **FrozenSet Extensions**: Define supported extensions as `FrozenSet<string>` with `StringComparer.OrdinalIgnoreCase` (e.g., `s_remuxExtensions`, `s_jpegExtensions`)
- **Case-Insensitive Matching**: Use FrozenSet `.Contains()` directly without `.ToLower()`, comparer handles case-insensitivity
- **File Type Categorization**: Group operations by file type requirements (remux vs re-encode vs audio-only)
- **Single-Pass Optimizations**: Prefer single-loop iterations with early exit over multiple LINQ passes
- **Skipped Extension Tracking**: Commands that filter files by `MediaUtilities.SupportedExtensions` pass a shared `SkippedExtensionTracker` instance to their task classes. The tracker collects unknown extensions (thread-safe via `Track()`), and the command calls `LogWarnings()` after processing to log them sorted. Used by `process`, `import`, and `index` commands.

### EXIF/Metadata Handling

- Uses `ExifToolJson` class with `JsonPropertyName` attributes for precise metadata field mapping
- Date validation prioritizes `EXIF:DateTimeOriginal` over `QuickTime:CreateDate`
- Custom `IsDateSet()` and `GetDateString()` methods handle metadata extraction logic
- `ContentIdentifier` property maps both `QuickTime:ContentIdentifier` and `MakerNotes:ContentIdentifier`
  group names (both occur in the wild) returning whichever is set
- `ImageDataHash` maps `File:ImageDataHash`, which only a catalog read requests (see Catalog Metadata Columns)

### Date Inference System (DateFromPath.cs)

- **Static Internal Methods**: All methods are `internal static` for testability with `InternalsVisibleTo`
- **DateFromPath.InferCreatedDate()**: Main entry point, tries filename first, then path fallback
- **DateFromPath.ExtractDateFromFilename()**: Supports multiple filename patterns:
  - `YYYYMMDD_HHMMSS` format (e.g., `20210502_200152957_iOS-1747.jpg`)
  - `YYYYMMDD` format (e.g., `EX_20030219_3378.jpg`)
  - `YYYY-MM-DD-HH-MM-SS` format (e.g., `PHOTO-2024-06-22-07-56-41.jpg`)
  - `YYYY MM DD` format with spaces (e.g., `EV 2014 07 03_0003.tif`)
- **DateFromPath.ExtractDateFromPath()**: Extracts from directory structures and year-only fallback
- **DateFromPath.IsDateValid()**: Validates dates within 1900-current year range

### Command Line Interface (CommandLine.cs)

- **System.CommandLine Integration**: Uses modern .NET command line parsing
- **Six subcommands**: `process`, `undo`, `import`, `index`, `trash`, `verify`, each with their own option set
- **Required `--path` Parameter**: Single directory path using `Option<DirectoryInfo>`. Validated with `AcceptExistingOnly()`
- **Optional `--dryrun` Flag**: Non-destructive preview mode (process, undo, import, not index)
- **Optional `--threads` Parameter**: Controls parallel processing degree with `DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 4)`. Validated to be > 0 and <= Environment.ProcessorCount using `Validators.Add()` (process, import, index)
- **Optional `--skipbackup` Flag** (process only): Leaves no `.bak` file behind, so a converted original or a deleted video is removed rather than backed up. No file is deleted to free its name for a rename, which takes a free name instead (see "Rename Collisions" below). Logs a warning at startup. Disables undo.
- **Optional `--deleteempty` Flag** (process, import): After the command completes, deletes empty child subdirectories from the target directory (deepest first, while the target root is never deleted). For `process` the target is `--path` (operated on in-place), and for `import` it is `--outpath`. Implemented by `DirectoryCleaner.DeleteEmptyDirectories(root, dryRun)`.
- **`import` subcommand** (formerly `organize`): Copies (default) or moves supported media files from `--path` sources into `--outpath/date/filename` directory structure. Date comes from EXIF metadata (falls back to `DateTime.MinValue` -> `"0001/01/01"` bucket when absent). `--format` (default `"yyyy/MM/dd"`) controls subdirectory naming and is validated as a date-only format (no time components). Uses `GetUniqueFileName` for collision handling (`foo_1.jpg` etc.). Parallel via `--threads` (same as `process`). `--deleteempty` (default `false`) deletes empty child subdirectories from `--outpath` after all files are imported. `--move` (default `false`) moves files instead of copying. `--tagpath` (default `false`) splits the source sub-directory path into tokens and writes each token as an `XMP:Subject` tag on the destination file using exiftool. It applies only to `s_exiftoolWriteExtensions` (`.3gp`, `.arw`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.mov`, `.mp4`, `.nef`, `.orf`, `.png`, `.psd`, `.rw2`, `.tif`, `.tiff`) via `meta.FileTypeExtension`. A file of any other type imports with a warning naming the tags or date it skips. It uses `-XMP:Subject-= / -XMP:Subject+=` to prevent duplicates while preserving existing tags. `--tags <string>` (optional) applies explicit comma-separated `XMP:Subject` tags to every imported file. `--datepath` (default `false`) infers the creation date from the source file path when no date is already embedded. It applies that date to the destination file before restoring mtime. A destination write that exits non-zero or prints an `Error` line fails the file. A failed copy is removed with no `--db` row. A moved file stays at its destination, and under `--db` it still gets its row unless the row write itself fails. **`--db <sqlite-file>` (Import.db) is the source-side dedup DB**: rows are keyed by `path = source_path` (NOT dest path) and hold the source file's hash/size/mtime. On each source file, import calls `GetByPathAsync(source_path)` for source-side hash caching, then `Sha256ExistsAsync(source_hash)` to skip already-imported sources. New imports insert a row at the source path. **No command outside `import` writes to source-keyed rows**, so dedup cannot be clobbered by later runs of `process`/`index`. `--trashdb <sqlite-file>` skips files whose **source-file** SHA-1 is in Trash.db. When import rewrites the destination via `--tags`/`--tagpath`/`--datepath`, its SHA-1 differs from the source SHA-1. Immich stored the destination SHA-1 from a prior upload, so the trash match is missed here and caught later by `process --trashdb`. `--skipdb <sqlite-file>` skips files whose SHA-256 matches a reference DB (read-only). Cross-collection dedup is typically implemented by pointing `--skipdb` at another collection's Import.db. `--rehash` forces recomputation of all hashes ignoring the size/mtime cache.
- **`index` subcommand**: Iterates all files in `--path`, upserts each into the `files` DB table via `IndexTask.ExecuteAsync` (insert new, update if hash changed, skip unchanged). `--db <sqlite-file>` is **required**. No `--dryrun` (always writes to DB). Supports `--threads` and `--rehash`. `--processed` (optional) marks newly-INSERTED rows with `is_processed = 1`, which is useful when seeding a Process.db from existing files so `process` treats them as already-done. The flag does not flip the flag on existing rows. Reports `inserted`/`updated`/`unchanged`/`ignored`/`failed` counts.
- **`trash` subcommand**: Syncs trashed asset checksums from an Immich server into a local SQLite trash database. It takes `--url` (Immich server URL, required) and `--trashdb <sqlite-file>` (trash database, required). The API key comes from exactly one of `--apikey` (inline) or `--apikey-file`, a file whose trimmed contents are the key. The two API-key options are mutually exclusive, and exactly one must be provided. `--apikey-file` must reference an existing, non-empty, readable file. An option validator enforces existence, and a command-level validator enforces non-empty and readable. Read failures are translated to validation errors, never thrown. The key is resolved at parse time by `CommandLine.ResolveApiKey`/`ReadApiKeyFile` (file contents preferred and `.Trim()`-med) and flows into `Options.ImmichApiKey`. Uses `POST /api/search/metadata` with `trashedAfter` to fetch all trashed assets, converts Base64 SHA-1 checksums to hex, and inserts them, keeping an existing row. Full sync (idempotent, append-only). No `--dryrun`.
- **`--trashdb` Flag** (import, process): SQLite database file with Immich trash hashes (synced by `trash`). In `import`, files matching the trash DB are skipped, preventing re-import of photos the user trashed in Immich. The check is against the **source-file** SHA-1. Files whose destination SHA-1 `import` mutated (`--tags`/`--tagpath`/`--datepath`) do not match here, although Immich stored the mutated SHA-1. The `process --trashdb` command catches those on the next pass. In `process`, matching files are **deleted from disk** before the per-file pipeline runs. This cleans up files trashed in Immich after upload and catches the import source-vs-dest SHA-1 drift. Their Process.db rows stay, so a Live Photo video still pairs with a trashed still. The Trash.db check is the durable safety net beyond Immich's ~30-day trash retention.
- **Optional `--imagehash` Flag** (import, process): Opt-in matching by image data hash, stored in `CommandLine.Options.ImageHash`. It is not a `required` member, so an `Options` built without it matches by whole-file hash only. `ImportCommand` checks the ExifTool version once up front. In `import`, after the whole-file checks find nothing, `ImportTask` reads the source's catalog metadata once and reuses it for the rest of the file. A hash in Trash.db returns `TrashedInImmich`. A hash in Import.db returns `Skipped`, logging the matched row from `Database.FindPathByImageHashAsync`. With `--db`, an in-run claim set lets only the first of two matching files in one run import. It is keyed by SHA-256, and also by image hash under `--imagehash`. A file that meets a claim waits for its owner's outcome. It skips when the owner imported or kept its moved file, and tries again when the owner released the claim by not importing. The rest return `Skipped`, so the counts match a sequential run and a dry run. With more than one thread, which of the matching files imports depends on timing. In `process`, `ProcessTask` reads `image_hash` from the cataloged row and deletes the file when Trash.db holds it. Command validators require `--db` or `--trashdb` for `import`, and both for `process`.
- **Optional `--processdb` Flag** (trash only): A Process.db opened read-only through `DatabaseScope`. After the sync, `TrashCommand` reads every `trash_hashes` row with no `image_hash`, including assets the server has purged. It looks each up with `Database.GetImageHashBySha1Async` and stores what it finds. The insert is an upsert that fills a missing `image_hash` and keeps everything else. Without the flag `trash` stores SHA-1 hashes only.
- **Optional `--skipdb` Flag** (import only): SQLite database of files to skip (read-only SHA-256 check). Files whose SHA-256 matches a record in this DB are skipped without being recorded. Use this to skip files already present in another collection.
- **Optional `--rehash` Flag** (process, import, index): Forces SHA-256 recomputation for every file, ignoring the size/mtime cache. SHA-1 is also recomputed when `--trashdb` is in use. Useful after filesystem operations that preserve mtime but change content.
- **Optional `--duration` Flag** (process only): Overrides `ShortVideoDuration` (default `1.0`s). Videos in a live-photo-compatible format whose duration is <= this value are always deleted, and a GIF at or under it becomes a PNG. Must be `> 0`. Stored in `CommandLine.Options.ShortVideoDuration` and read by `DeleteLivePhotosAsync` and `IsStillOrShortGifAsync`.
- **Optional `--reprocess` Flag** (process only): When set, ignores `is_processed` in the DB and forces every file to be processed again. Stored in `CommandLine.Options.Reprocess`, it disables the `IndexStatus.Unchanged && wasProcessed` early-return in `ExecuteAsync`.
- **Command Construction**: `CommandLine.SetAction` handlers create the appropriate command class (e.g., `ProcessCommand`, `ImportCommand`) with `CommandLine.Options` and `CancellationToken`
- **Built-in Help System**: Automatic help generation and validation

## Development Workflow

See [`CODESTYLE.md`](./CODESTYLE.md) for build requirements, formatting commands, and tooling.

### Dependencies

- **CliWrap**: External process execution
- **System.CommandLine**: Modern CLI argument parsing and validation
- **System.Text.Json**: High-performance JSON with source generation
- **Microsoft.Data.Sqlite**: SQLite database access for source file deduplication
- **Serilog**: Structured logging with console output
- **Native AOT**: Project configured for `PublishAot=true` with `InvariantGlobalization=true`
- **xUnit**: Testing framework for PhotoCleanerTests project

### Test Architecture

- **PhotoCleanerTests Project**: comprehensive test coverage across all commands, see the file list under Project Structure above rather than a count here, which drifts every time a test is added
- **InternalsVisibleTo**: Enables direct testing of internal methods without reflection
- **Coverage Areas**: Date inference (filename patterns, path structures, validation), command line interface (parsing, validation, error handling, thread configuration and boundary validation), integration scenarios, process task execution, live photo detection (ContentIdentifier matching, `_hevc` suffix naming, mismatch/missing tag scenarios), metadata preservation through conversion

## Critical Implementation Details

### Video Conversion Logic

- **Three-tier approach**: Remux (.m2t, .mkv) -> Re-encode (.asf, .wmv, .avi, .3gp, .gif) -> Audio-only re-encode (.mov/.mp4 with PCM audio)
- **Still GIF**: A GIF with one frame, no duration at all, or one no longer than `--duration` skips the re-encode tier. Its first frame becomes a PNG. As an MP4 it would meet the short clip rule in the next round and be deleted, the original with it under `--skipbackup`.
- **Even Dimensions**: The re-encode tier pads with `pad=ceil(iw/2)*2:ceil(ih/2)*2`, because libx264 at `yuv420p` rejects an odd width or height. Nothing is scaled. A GIF keeps every pixel and gains one edge row or column. An odd-sized subsampled source, such as a `yuv420p` AVI, loses its last odd row or column to the pad color instead.
- **Rename Collisions**: The mixed-case and MIME extension renames go through `RenameToFreeName`. A file already holding the target name is never deleted or backed up. The renamed file takes the next free `_N` name from `MediaUtilities.GetUniqueFileName`, and a warning names both files. This holds with and without `--skipbackup`. A case-only rename on a case-insensitive filesystem is not a collision with itself. `ProcessCommand`'s extension-conflict deferral only keeps same-stem files out of one parallel pass, and it reserves no name on disk.
- **Backup Strategy**: Original files renamed to `.bak` extension after successful conversion, and `BackupFile()` returns the backup path. A `{backup}.out` companion file (e.g. `img.gif.bak.out`) beside the backup holds the full output path, which may carry a `GetUniqueFileName` counter suffix (e.g. `img_1.mp4`). It is the only record undo has of a converted output. An output that this run converted and then culls is deleted with its marker, with no backup of its own. A second conversion of it deletes the intermediate and points the original's marker at the new output. When `options.SkipBackup` is true, the original still moves to its `.bak` name first, so a same-name output such as a PCM `.mp4` keeps its name. No `.bak.out` is written, and the `.bak` is deleted once the metadata copy and the created-date write have both landed.
- **Metadata Preservation**: After every ffmpeg conversion, `exiftool -TagsFromFile <source> <output> -all:all -overwrite_original` copies all source metadata to the output file. The source is the `.bak` file in both modes. A failed copy or date write removes the output, returns the `.bak` to the original name, and fails the file. When the output cannot be removed, both stay and an error names them. So `--skipbackup` never deletes an original after a failed exiftool write. Tags the MP4 cannot hold are skipped with exit 0, which counts as landed. `ffmpeg -map_metadata` is not used, it is unreliable for Apple QuickTime-specific tags (e.g. `ContentIdentifier` in the `mdta`/`keys` atom). `TagsFromFile` does not reliably set the output's create date across every format pairing. So a separate `TrySetCreateDateAsync` call follows it, writing the date from the already-parsed `ExifToolJson` to the output.
- **Re-queue Pattern**: Converted files are added back to processing queue for validation

### Live Photo Detection

- **Short videos** (duration <= `options.ShortVideoDuration`, default `1.0s`, overridable via `--duration`): always deleted regardless of companion file
- **Pairing with `--db`**: the still is the catalog row of a HEIC/HEIF/JPG/JPEG file whose `content_identifier` equals the video's, whatever its file name or folder. This covers a still with a different stem, in another date folder, or no longer on disk. The video's own `ContentIdentifier` is read from its metadata. If it is missing or no image row matches, the video is kept. A row never counts as the still when its `sha256` or `image_hash` equals the video's own, since the same media data is the same video. A differently encoded video with an image extension and a shared ContentIdentifier can still pair. That residual risk is accepted, since the loss is one short clip.
- **Companion file search without `--db`** (`FindCompanionImagePath()`): looks for a HEIC/HEIF/JPG/JPEG file in the same folder by:
  1. Direct basename match (`IMG_1234.mov` -> `IMG_1234.heic`)
  2. Basename minus `_hevc` suffix (`IMG_1234_HEVC.mov` -> `IMG_1234.heic`), the newer iPhone naming
- **ContentIdentifier confirmation** (without `--db`): a candidate pair is only deleted when both files expose a `ContentIdentifier` tag that matches exactly. If either file lacks the tag, or the tags differ, the video is kept. There is no fallback to name-only deletion.
- **Long videos** (>= `LiveVideoDuration` = 4.0s): always kept even with a matching companion, and a warning is logged

### Undo Architecture (UndoTask.cs)

- **Backup naming**: `X.bak` (first), `X.bak1`, `X.bak2`, ... (subsequent runs of `process`)
- **`FileEnumerator.Enumerate()`** enumerates all files including `.bak*` files before calling `Execute()`
- **Records only** in `UndoTask.Execute()`:
  - *Read*: every `.bak` or `.bakN` whose base is supported media, in base then number order, and the `.out` marker named after each one. A marker's output is its file name resolved beside the backup, so a moved or copied tree still matches. An empty, vanished, or unreadable backup, or an unreadable marker, fails only itself. An empty marker records no output.
  - *Reverse outputs*: a marker's output is deleted when it exists, one marker names it, and no backup holds its name. When the output is gone, every backup holding its name is an original. Two markers, or a backup holding the name of an output that still exists, are ambiguous. Then the group is left as it is and counted Failed, so the record survives for a hand fix.
  - *Restore originals*: every other backup moves back to its base name, or to the next free `name_N` when a file holds it, with a warning. A free name never takes another original's base name. Its marker is deleted after the move. Undo never deletes a backup, and never overwrites a current file.
- **Internal static helpers** (testable via `InternalsVisibleTo`):
  - `IsBackupFile(path)`: matches `.bak\d*$`
  - `GetBackupBase(path)`: strips the `.bak\d*` suffix
  - `IsUndoableBackup(path)`: a backup whose base extension is supported media
  - `ResolveMarkerOutput(backup, text)`: the marker's output file name, beside the backup
- **Dry run**: logs all intended operations and changes no file. It tracks the names it would free and take. Its free names match a live run where the volume's case rules match the OS default.
- **Known limitation**: an extension rename creates no backup and cannot be undone. A rename whose target name was taken leaves the other file untouched, so undo has nothing of it to restore

### Error Handling Strategy

- Console output uses structured prefixes: `WARNING:`, `INFORMATION:`
- External command failures throw `CommandExecutionException`
- Methods return `false` to skip file processing rather than throwing exceptions

## File Processing Extensions

Supported: `.3gp`, `.arw`, `.asf`, `.avi`, `.cr2`, `.dng`, `.gif`, `.heic`, `.heif`, `.jpeg`, `.jpg`, `.m2t`, `.m2ts`, `.mkv`, `.mov`, `.mp4`, `.mts`, `.nef`, `.orf`, `.png`, `.psd`, `.rw2`, `.tif`, `.tiff`, `.wmv`

## JSON Source Generation

Uses `ExifToolJsonContext` for AOT-compatible JSON serialization of `ExifToolJson` metadata,
`ImmichJsonContext` for the Immich API models, and `ImmichVerifyJsonContext` for the
`ImmichVerifyLine` protocol the `verify` command reads from the containerized decoder.

## Testing Strategy

- **Direct Method Testing**: Uses `InternalsVisibleTo` for compile-time safe method calls
- **Comprehensive Coverage**: Tests all filename patterns, path structures, date validation, and CLI parsing
- **Integration Testing**: Validates end-to-end date inference and command line interface logic
- **No Reflection**: All tests use direct method calls for better performance and maintainability

### Command Line Testing Patterns

- **Direct construction, no helper**: `new CommandLine([args])` parses directly, and the test reads `cli.Result.Errors` / `cli.Result.GetValue(...)` (or the equivalent per-subcommand options) rather than going through a shared builder.
- **Real Directory Testing**: Uses `Directory.GetCurrentDirectory()` as a path that is guaranteed to exist, for both success cases and `AcceptExistingOnly()` validator cases.
- **Comprehensive Scenarios**: Per-option default values, explicit values, flag parsing, validation errors (an unknown option, a missing required option, an out-of-range numeric value), and subcommand registration.
