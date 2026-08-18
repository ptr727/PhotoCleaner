# PhotoCleaner

Utility to prepare photos and videos for import into photo managers.

## Release History

**Version: 1.1**:

- Added `verify` command to detect possibly corrupt media:
  - I discovered thousands of files in my Immich library that failed to create thumbnails, all these images were [corrupt](https://github.com/ptr727/PhotoCleaner/issues/25) but passed the entire pipeline undetected.
  - Runs the Immich decoder inside the Immich docker image to confirm that the file is usable, which makes that decoder the only judge of a file's health.
  - Carries no container parser of its own, deliberately, because such a parser condemns whatever it fails to understand, and an unfamiliar but valid format looks the same as a damaged one from the inside. Docker is therefore required.
- The exiftool metadata read now always passes `-validate`:
  - Files exiftool reports errors on are marked invalid by `process` and skipped by `import`.
  - Validation warnings are logged at debug level only, because many healthy files do produce warnings.
- **Breaking**:
  - Commands now exit `2` when they complete with per-file failures, where `process` previously exited `0`. `0` still means success and `1` still means the command could not run.
  - `trash` exits `2` when pagination stops early and the trash database ends up short of the server, where it previously reported success.

**Version: 1.0**:

- First published release, carrying the multi-arch Docker image and the GitHub release with the Linux and Windows executables attached.
