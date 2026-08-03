namespace PhotoCleaner;

// Calls Immich's own compiled modules rather than reimplementing the preview pipeline.
// Behavior therefore tracks Immich across releases.
// The coupling surface is three module paths and their exported names, which the preflight checks.
internal static class ImmichVerifyScript
{
    // Required for module resolution inside the image.
    internal const string WorkingDirectory = "/usr/src/app/server";

    // Loads the same modules the verify script needs, so a moved module fails before any file is judged.
    internal const string Preflight = """
        const ROOT = "/usr/src/app/server/dist";
        const { MediaRepository } = require(ROOT + "/repositories/media.repository.js");
        const { defaults } = require(ROOT + "/config.js");
        const { ThumbnailConfig } = require(ROOT + "/utils/media.js");
        if (!MediaRepository || !defaults || !defaults.image || !ThumbnailConfig) {
            throw new Error("Immich modules loaded but expected exports are missing");
        }
        process.stdout.write("PHOTOCLEANER_PREFLIGHT_OK\n");
        """;

    internal const string PreflightSentinel = "PHOTOCLEANER_PREFLIGHT_OK";

    // Reads absolute paths on stdin and writes one JSON object per line: { path, ok, via, error }.
    internal const string Verify = """
        const path = require("node:path");
        const readline = require("node:readline");
        const ROOT = "/usr/src/app/server/dist";
        const { MediaRepository } = require(ROOT + "/repositories/media.repository.js");
        const { defaults } = require(ROOT + "/config.js");
        const { ThumbnailConfig } = require(ROOT + "/utils/media.js");

        // MediaRepository calls whatever the logger interface exposes, and that set has grown before.
        // Swallowing every call through a Proxy keeps this working across Immich updates.
        const logger = new Proxy(
            {},
            { get: (_target, prop) => (prop === "isLevelEnabled" ? () => false : () => undefined) },
        );
        const repo = new MediaRepository(logger);

        const RAW = new Set([".arw", ".cr2", ".dng", ".nef", ".orf", ".rw2"]);
        const VIDEO = new Set([
            ".3gp", ".asf", ".avi", ".m2t", ".m2ts", ".mkv", ".mov", ".mp4", ".mts", ".wmv",
        ]);
        const OUT = "/tmp/photocleaner-verify.jpg";

        async function verifyVideo(file) {
            const info = await repo.probe(file);
            const videoStream = info.videoStreams[0];
            if (!videoStream) {
                throw new Error("No video stream found");
            }
            const config = ThumbnailConfig.create({
                ...defaults.ffmpeg,
                targetResolution: defaults.image.preview.size.toString(),
            });
            const options = config.getCommand(0, videoStream, undefined, info.format);
            await repo.transcode(file, OUT, options);
            return "transcode";
        }

        async function verifyImage(file, ext) {
            let via = "decode";
            if (RAW.has(ext)) {
                try {
                    if (await repo.extract(file)) {
                        via = "decode+extract";
                    }
                } catch {
                    // Immich treats extraction as best effort too; only the decode must succeed
                }
            }
            await repo.generateThumbnail(
                file,
                { ...defaults.image.preview, colorspace: "srgb", processInvalidImages: false },
                OUT,
            );
            return via;
        }

        const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
        (async () => {
            for await (const line of rl) {
                const file = line.trim();
                if (!file) {
                    continue;
                }
                const ext = path.extname(file).toLowerCase();
                try {
                    const via = VIDEO.has(ext)
                        ? await verifyVideo(file)
                        : await verifyImage(file, ext);
                    process.stdout.write(JSON.stringify({ path: file, ok: true, via }) + "\n");
                } catch (e) {
                    const error = String((e && e.message) || e)
                        .replaceAll(/\s+/g, " ")
                        .slice(0, 300);
                    process.stdout.write(
                        JSON.stringify({ path: file, ok: false, via: "", error }) + "\n",
                    );
                }
            }
        })().catch((e) => {
            process.stderr.write("fatal: " + String((e && e.message) || e) + "\n");
            process.exit(1);
        });
        """;
}
