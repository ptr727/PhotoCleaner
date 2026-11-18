# PhotoCleaner Operations Guide

## Purpose

This guide covers the operations around PhotoCleaner rather than the commands themselves. It explains how to keep a pristine archive of originals and how to adopt a new photo source without creating duplicates. It also lists what to check in a cloud export before importing it.

The guide works with any sync tool and any photo server. [rclone][rclone-link] is the worked example of a source. Immich is named only where a PhotoCleaner command is Immich-specific, namely `trash` and `verify`.

The [README][readme] holds the command reference and the [Workflow Example][readme-workflow] listing. This guide links to that listing and does not repeat it. [ARCHITECTURE][architecture] explains the catalog columns and the Live Photo rules that this guide relies on.

All paths and file names here are constructed examples, such as `/mnt/photos/mirror` and `IMG_0001.HEIC`.

## Building a Pristine Originals Archive

A sync tool keeps a mirror current with a source. The mirror is not an archive, for these reasons:

- A sync removes, or moves aside, a file that was deleted at the source.
- A rename at the source changes the name the mirror holds.
- An incomplete listing from the source looks like a mass deletion to the sync.
- A retention window at the source eventually prunes what it still lists.

The pattern below keeps an archive that none of these can touch:

1. Treat the mirror as read-only. Nothing but the sync tool writes to it.
1. Run `import` as a copy with no tags, from the mirror into an append-only Originals directory.
1. Run a second `import`, with tags and its own database, from Originals into the processing directory.
1. Run `process` on the processing directory, and upload from there.

Never run `process`, or any other command that changes files, on the mirror or on Originals. Use a database of its own for each `import`, so the two copies keep separate records.

An `import` from the mirror skips every file whose whole-file SHA-256 is already in its database. A rename at the source therefore adds no second copy. Rerun the command after each sync to add what is new.

Leave `--trashdb` off the import into Originals, so the archive keeps every file the mirror delivered, including one the owner later trashed. Pass `--trashdb` to the second import and to `process`, where a trash database exists. See "Keeping Removed Photos Out" for what each check can match. The [Workflow Example][readme-workflow] lists the commands for the whole chain with rclone as the source.

## Adopting a New Source for a Library the Server Already Holds

Moving to a new source for a library that the photo server already holds risks a second copy of every photo. The import database does not help, because it has never seen these files. Find what the server already holds before importing, using the strongest evidence first.

1. **Byte-identical.** Run `index` on the new source and on the server's stored originals, each into its own database. Files with the same SHA-256 are the same file.
1. **Same `image_hash`.** The catalog reads `image_hash` from the image data. It stays equal across metadata-only rewrites, such as new tags or a changed date. It differs for a real re-encode.
1. **Visual comparison.** For re-encoded or resized copies, compare the pictures themselves, as the next section describes.
1. **Everything left is imported.**

Compare the catalogs with SQLite, using the `files` table of each database. Review the matches before you act on them.

An import database records that bytes were imported once. It does not record that the server still holds them. A file that was imported once and is absent from the server now is normally one the owner removed. Skip it rather than re-importing it.

## Re-encoded Copies and the Limits of Each Key

The measurements below come from one real migration of a cloud export. They are rates, and they describe that export. Treat them as a warning about each key, not as a guarantee for another source.

### What the Keys Did

- **A file extension is not a format.** Some exports re-encode images and keep the original extension. A file named `.heic` or `.dng` can hold a JPEG. `image_hash` then matches nothing, even for photos the server holds.
- **Capture time alone is weak.** Many assets share a capture second, such as bursts and scans. A match on capture second and camera model pointed at a clearly different picture in about two thirds of cases. It still did so in about one in six cases when the pixel dimensions also matched. A match on capture second and file name was clearly wrong in under 2 percent of cases.
- **A sidecar time is not a capture time.** When a file has no embedded date, the sidecar's taken-time is often an upload or file time. Do not use it as a matching key.
- **An embedded ImageUniqueID was not usable as a key.**

### A Visual Comparison That Worked

This comparison is a method you script yourself. It is not a PhotoCleaner command.

1. Decode each new still to a 32 by 32 grayscale grid. Squash the aspect ratio, for example with `ffmpeg -i IMG_0001.JPG -frames:v 1 -vf scale=32:32:flags=area,format=gray -f rawvideo -`.
1. Do the same for the server's own thumbnail of each asset.
1. Compare the new file's grid with each server grid, in all 8 rotations and mirrors.
1. Accept a match when the mean absolute difference is under 2 on a 0-255 scale and the correlation is at least 0.98.

In a self-test, every sampled original found its own asset first. Where an embedded capture time existed, a strong visual match agreed with it to the second in about 96 percent of cases.

HEIC needs a decoder that assembles its tiles, for example `heif-convert`. Choose the decoder by the file's content type rather than by its extension, because of the mislabeled files above.

### The Rule for Ambiguity

Skipping a file wrongly loses a photo without any sign. Importing a file wrongly only creates a duplicate, and the server's duplicate finder can catch that. Import anything ambiguous.

### Show the Remainder to the Owner Before Uploading

Before you upload the remainder, show it to the library's owner. Contact sheets of small thumbnails, built from the staged files, work well.

What a library lacks after byte, image-hash, and visual matching is often what the owner removed earlier. That includes shots deleted on the phone before any import ran, which no import history can show.

In the same migration, the owner reviewed the whole remainder and rejected all of it, so nothing was imported. The matching work still made the remainder small enough to review.

## Recording a Source as Already Imported

Use this to tell an import database that a whole source is known, and then un-mark the files that should still import.

1. Run `index` over the whole source into the database of the import into the processing directory, not the one for Originals. For example `index --path /mnt/photos/newsource --db /mnt/photos/db/import.db`.
1. Delete the rows of the files that should import. The table is `files` and its key is `path`, so a SQLite statement such as `DELETE FROM files WHERE path = '/mnt/photos/newsource/IMG_0001.HEIC'` removes one. Import skips a file whose SHA-256 is in any row, so also delete the rows of every byte-identical twin, such as a `(1)` copy.
1. Run `import` with `--dryrun` and check that its count matches the number of rows you deleted.
1. Run `import` for real.
1. Run `import` again and confirm that it imports nothing.

Add `--tags` with a tag of its own, for example `--tags newsource`, to the real import. The owner can then review that batch in the photo server afterwards.

## Keeping Removed Photos Out

The `trash` command syncs the hashes of the assets that are in the photo server's trash into a trash database. `import` and `process` both take `--trashdb`. `import` skips a source file whose hash is in it, and `process` deletes a matching processed file. Both compare the whole-file SHA-1.

The server stores and trashes the tagged copy that was uploaded, so its hash differs from the untagged source that `import` reads. `process` reads the tagged files, so it is the check that matches what the server holds. Pass `--trashdb` to `process` as well as to `import`, and give `process` a `--db` too. It checks the trash database only when it has a database.

Run `trash` after you cull and before the server's trash is emptied or purged by retention. A purged asset's hash can no longer be read, so the removal is forgotten if the sync runs too late.

The trash database is append-only. A restored asset stays in it until you delete the database and run `trash` again.

## Live Photos

`process` removes a Live Photo's motion video when three things hold. The video is a `.mov` or `.mp4` under the Live Photo length bound of 4 seconds. The process database knows a HEIC, JPG, or JPEG still with the same Apple `ContentIdentifier`. File names and date folders do not matter when `process` runs with `--db`. Without `--db`, it pairs only files in the same folder with the same base name, and confirms them by `ContentIdentifier`. See "Live Photo Detection" in [ARCHITECTURE][architecture-livephoto] for the full rules.

A cloud export may deliver the motion parts as short videos with no sidecar of their own. A `.mov` or `.mp4` video at or under the `--duration` threshold is deleted in any case.

## What to Check in a Cloud Export Before Importing

- **JSON sidecars are not read.** The tool uses embedded metadata only.
- **A file with no embedded date lands in the no-date folder** unless `--datepath` infers a date from its path. With the format `yyyy/MM/yyyy-MM-dd` that folder is `0001/01/0001-01-01`. File times are not used as a fallback. Review that folder by hand.
- **`-edited` renditions are separate files.** So are copies with a `(1)`-style counter. The tool keeps each one, except that `import` with a database skips a copy whose bytes are identical to a file already imported.
- **`process` renames a file whose extension disagrees with its content.** It also converts every GIF to an MP4 video, animated or not. The new MP4 is then subject to the short-video rule, so check a short or static GIF after the run.
- **Short videos are deleted.** A `.mov` or `.mp4` video at or under the `--duration` threshold is removed, one second by default.

## Known Issues

- [Issue 280][issue-280-link]: a rename collision with `--skipbackup` deletes the file that holds the target name. Check for same-stem files first, or keep the backups.
- [Issue 281][issue-281-link]: a GIF with an odd width or height fails conversion, animated or not.

<!-- Repo -->

[architecture]: ./ARCHITECTURE.md
[architecture-livephoto]: ./ARCHITECTURE.md#live-photo-detection
[readme]: ./README.md
[readme-workflow]: ./README.md#workflow-example

<!-- Distribution -->

[issue-280-link]: https://github.com/ptr727/PhotoCleaner/issues/280
[issue-281-link]: https://github.com/ptr727/PhotoCleaner/issues/281

<!-- External -->

[rclone-link]: https://rclone.org
