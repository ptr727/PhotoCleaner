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

Leave `--trashdb` off the import into Originals, so the archive keeps every file the mirror delivered, including one the owner later trashed. Passing `--trashdb` on that first import is a valid variant. It matches only files whose untagged bytes the server held, so it keeps little out of the archive in this chain. Pass `--trashdb` to the second import and to `process`, where a trash database exists. See "Keeping Removed Photos Out" for what each check can match. The [Workflow Example][readme-workflow] lists the commands for the whole chain with rclone as the source.

## Adopting a New Source for a Library the Server Already Holds

Moving to a new source for a library that the photo server already holds risks a second copy of every photo. The import database helps only for bytes it has seen. A new source that delivers bytes identical to an earlier import is already recorded and is skipped. Files whose bytes differ look new to it. Find what the server already holds before importing, using the strongest evidence first.

1. **Byte-identical.** Run `index` on the new source and on the server's stored originals, each into its own database. Files with the same SHA-256 are the same file.
1. **Same `image_hash`.** The catalog reads `image_hash` from the image data. It stays equal across metadata-only rewrites, such as new tags or a changed date. It differs for a real re-encode.
1. **Visual comparison.** For re-encoded or resized copies, compare the pictures themselves, as the next section describes.
1. **Everything left is imported.**

Compare the catalogs with SQLite, using the `files` table of each database. Review the matches before you act on them.

`import --imagehash` applies the second key against the import database during the import itself. It skips a source whose `image_hash` a row already holds, and logs the row it matched. It is opt-in, because two files with equal image data and different metadata then count as one.

An import database records that bytes were imported once. It does not record that an upload succeeded or that the server still holds them. A file that was imported once and is absent from the server now is normally one the owner removed. Confirm the upload first. Evidence is that the photo server holds the asset in its library or its trash, found by checksum or by the visual comparison below. The upload tool's own log showing the upload is evidence too, and so is a `trash_hashes` row for the uploaded file. Import history alone is not evidence. Then skip the file rather than re-importing it.

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

Skipping a file wrongly loses a photo without any sign. Importing a file wrongly either creates a duplicate or brings back a photo the owner had removed. The owner can remove either again in the photo server, after which the trash sync records it. Import anything ambiguous.

### Show the Remainder to the Owner Before Uploading

Before you upload the remainder, show it to the library's owner. Contact sheets of small thumbnails, built from the staged files, work well.

What a library lacks after byte, image-hash, and visual matching is often what the owner removed earlier. That includes shots deleted on the phone before any import ran, which no import history can show.

In the same migration, the owner reviewed the whole remainder and rejected all of it, so nothing was imported. The matching work still made the remainder small enough to review.

## Recording a Source as Already Imported

Use this to tell an import database that a whole source is known, and then un-mark the files that should still import.

1. Run `index` over the whole source into the database of the import into the processing directory, not the one for Originals. Those rows mean known, not copied. For example `index --path /mnt/photos/newsource --db /mnt/photos/db/import.db`.
1. Delete the rows of the files that should import. The table is `files` and its key is `path`, so a SQLite statement such as `DELETE FROM files WHERE path = '/mnt/photos/newsource/IMG_0001.HEIC'` removes one. Import skips a file whose SHA-256 is in any row, so also delete the rows of every byte-identical twin, such as a `(1)` copy.
1. Where an Originals archive stage exists, copy the new source into Originals first, with the import that has its own database.
1. Run the import that reads Originals with `--dryrun`, and read its count there. Without an archive stage, run `import` on the source itself.
1. Run that `import` for real.
1. Run `import` again and confirm that it imports nothing.

The database matches by SHA-256, so this seeding works only while the archive copy is byte-identical to the source. An option that rewrites the file on import breaks that identity for the stage that uses it. Those options are `--tags`, `--tagpath`, and `--datepath`. Keep them off the Originals copy, so that Originals stays identical to the source.

Add `--tags` with a tag of its own, for example `--tags newsource`, to the import that reads Originals. The owner can then review that batch in the photo server afterwards.

## Keeping Removed Photos Out

The `trash` command syncs the hashes of the assets that are in the photo server's trash into a trash database. `import` and `process` both take `--trashdb`. `import` skips a source file whose hash is in it, and `process` deletes a matching processed file. Both compare the whole-file SHA-1.

The server stores and trashes the tagged copy that was uploaded, so its hash differs from the untagged source that `import` reads. `process` reads the tagged files, so it is the check that matches what the server holds. Pass `--trashdb` to `process` as well as to `import`, and give `process` a `--db` too. It checks the trash database only when it has a database.

To match by image data as well, run `trash` with `--processdb` naming the process database. It then records the `image_hash` of each trashed processed file. Pass `--imagehash` to `import` and `process` to treat a file with a recorded `image_hash` as trashed. This catches a copy whose metadata differs, such as the untagged source of a tagged upload. A format ExifTool cannot hash has no `image_hash` and is still matched by SHA-1 alone.

The match covers every copy with that image data, including one the owner kept. Where the server held two copies that differ only in metadata and the owner trashed one, `process --imagehash` deletes the kept copy's processed file. `import --imagehash` skips its source. Leave `--imagehash` off for a library whose duplicates were culled this way.

Run `trash` after you cull and before the server's trash is emptied or purged by retention. A purged asset's hash can no longer be read, so the removal is forgotten if the sync runs too late.

The trash database is append-only. A restored asset stays in it. Delete that one row from the `trash_hashes` table. Rebuilding the whole database drops the hashes of purged assets, which `trash` cannot fetch again. The `sha1` column holds lowercase hex. Run `sha1sum` on the restored original downloaded from the server, which prints that form. A file name can repeat, so do not select the row by name. With `--imagehash`, another trashed asset with the same image data still matches the restored file. Leave `--imagehash` off while that asset stays trashed.

## Live Photos

`process` removes a Live Photo's motion video when three things hold. The video is a `.mov` or `.mp4` under the Live Photo length bound of 4 seconds. The process database knows a HEIC, HEIF, JPG, or JPEG still with the same Apple `ContentIdentifier`. File names and date folders do not matter when `process` runs with `--db`. Without `--db`, it pairs only files in the same folder with the same base name, or the base name plus `_HEVC`. It confirms each pair by `ContentIdentifier`. See "Live Photo Detection" in [ARCHITECTURE][architecture-livephoto] for the full rules.

A cloud export may deliver the motion parts as short videos with no sidecar of their own. A `.mov` or `.mp4` video at or under the `--duration` threshold is deleted in any case.

## What to Check in a Cloud Export Before Importing

- **JSON sidecars are not read.** The tool uses embedded metadata only.
- **A file with no embedded date lands in the no-date folder** unless `--datepath` infers a date from its path. With the format `yyyy/MM/yyyy-MM-dd` that folder is `0001/01/0001-01-01`. File times are not used as a fallback. Review that folder by hand.
- **`-edited` renditions are separate files.** So are copies with a `(1)`-style counter. The tool keeps each one. Import with a database skips a file identical to one already recorded, including an identical file earlier in the same run.
- **`process` renames a file whose extension disagrees with its content.** When another file already holds the corrected name, the renamed file takes the next free name, such as `photo_1.jpg`, and the run logs a warning. It also converts every GIF to an MP4 video, animated or not. The new MP4 is then subject to the short-video rule, so a short or static GIF can vanish. Keep backups, and check short GIFs before you run with `--skipbackup`.
- **Short videos are deleted.** A `.mov` or `.mp4` video at or under the `--duration` threshold is removed, one second by default.

<!-- Repo -->

[architecture]: ./ARCHITECTURE.md
[architecture-livephoto]: ./ARCHITECTURE.md#live-photo-detection
[readme]: ./README.md
[readme-workflow]: ./README.md#workflow-example

<!-- External -->

[rclone-link]: https://rclone.org
