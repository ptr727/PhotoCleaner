using System.Net.Http.Json;

namespace PhotoCleaner;

internal sealed class TrashCommand(
    CommandLine.Options options,
    CancellationToken cancellationToken,
    HttpClient? httpClient = null
)
{
    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync("Trash", async () =>
            {
                bool partialSync = await TrashDatabaseScope
                    .RunAsync(
                        options.TrashDbFile,
                        async trashDatabase =>
                        {
                            if (trashDatabase is null)
                            {
                                throw new InvalidOperationException(
                                    "Trash database path is required (--trashdb)"
                                );
                            }

                            return await DatabaseScope
                                .RunAsync(
                                    options.DbFile,
                                    processDatabase => SyncAsync(trashDatabase, processDatabase),
                                    readOnly: true,
                                    label: "process database",
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                        },
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);

                // Pagination stopping early leaves the database short of the server.
                // Callers use it to skip files, so a short one re-imports trashed assets.
                // That is a completed run carrying a failure rather than a success.
                // A page that throws leaves it short as well, keeping the pages before it.
                // That exits Error from the handler above rather than reaching here.
                return partialSync ? ExitCode.Failed : ExitCode.Success;
            })
            .ConfigureAwait(false);

    private async Task<bool> SyncAsync(TrashDatabase trashDatabase, Database? processDatabase)
    {
        if (processDatabase is not null && !processDatabase.HasImageHash)
        {
            Log.Warning(
                "Process database '{DbFile}' has no image_hash column, so no image data hashes are recorded",
                options.DbFile!.FullName
            );
        }

        bool partialSync = false;
        long totalFetched = 0;
        long totalImageHashes = 0;
        long countBefore = await trashDatabase
            .GetCountAsync(cancellationToken)
            .ConfigureAwait(false);

        using HttpClient? ownedClient = httpClient is null ? CreateDefaultHttpClient() : null;
        HttpClient client = httpClient ?? ownedClient!;
        int page = 1;
        bool hasMore = true;

        while (hasMore)
        {
            Log.Information("Fetching trashed assets page {Page}", page);
            ImmichSearchRequest request = new() { Page = page };
            ImmichSearchAssets result = await FetchTrashedAssetsAsync(client, request)
                .ConfigureAwait(false);
            List<ImmichAssetDto> assets = result.Items;

            if (assets.Count == 0)
            {
                hasMore = false;
                continue;
            }

            totalFetched += assets.Count;

            List<(string Sha1Hex, string? OriginalFileName, string? ImageHash)> batch = [];
            foreach (ImmichAssetDto asset in assets)
            {
                if (!asset.IsTrashed)
                {
                    Log.Warning(
                        "Skipping non-trashed asset '{FileName}' (id: {AssetId})",
                        asset.OriginalFileName,
                        asset.Id
                    );
                    continue;
                }

                string? sha1Hex = ConvertChecksum(asset.Checksum);
                if (sha1Hex is null)
                {
                    Log.Warning(
                        "Skipping asset '{FileName}' (id: {AssetId}) with invalid checksum '{Checksum}'",
                        asset.OriginalFileName,
                        asset.Id,
                        asset.Checksum
                    );
                    continue;
                }

                string? imageHash = processDatabase is null
                    ? null
                    : await processDatabase
                        .GetImageHashBySha1Async(sha1Hex, cancellationToken)
                        .ConfigureAwait(false);
                if (imageHash is not null)
                {
                    totalImageHashes++;
                }

                Log.Debug(
                    "Trashed asset '{FileName}' with SHA-1 '{Sha1}' and image data hash '{ImageHash}'",
                    asset.OriginalFileName,
                    sha1Hex,
                    imageHash
                );
                batch.Add((sha1Hex, asset.OriginalFileName, imageHash));
            }

            if (batch.Count > 0)
            {
                await trashDatabase
                    .InsertHashesAsync(batch, cancellationToken)
                    .ConfigureAwait(false);
            }

            Log.Information("Fetched {Count} trashed assets on page {Page}", assets.Count, page);

            if (result.NextPage is null)
            {
                hasMore = false;
            }
            else if (int.TryParse(result.NextPage, out int nextPage) && nextPage > page)
            {
                page = nextPage;
            }
            else
            {
                Log.Error(
                    "Stopping pagination: invalid NextPage value '{NextPage}' after page {Page}",
                    result.NextPage,
                    page
                );
                partialSync = true;
                hasMore = false;
            }
        }

        long countAfter = await trashDatabase
            .GetCountAsync(cancellationToken)
            .ConfigureAwait(false);
        long totalInserted = Math.Max(0, countAfter - countBefore);

        Log.Information(
            "Fetched {TotalFetched} trashed assets, inserted {TotalInserted} new hashes (total {TotalCount})",
            totalFetched,
            totalInserted,
            countAfter
        );
        if (processDatabase is not null)
        {
            Log.Information(
                "Found image data hashes for {ImageHashCount} trashed assets in the process database",
                totalImageHashes
            );
        }

        return partialSync;
    }

    private HttpClient CreateDefaultHttpClient()
    {
        if (string.IsNullOrWhiteSpace(options.ImmichUrl))
        {
            throw new InvalidOperationException("Immich URL is required (--url)");
        }

        if (string.IsNullOrWhiteSpace(options.ImmichApiKey))
        {
            throw new InvalidOperationException(
                "Immich API key is required (--apikey or --apikey-file)"
            );
        }

        HttpClient client = ptr727.Utilities.HttpClientFactory.CreateClient(
            new ptr727.Utilities.HttpClientOptions
            {
                BaseAddress = new Uri(options.ImmichUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(120),
            }
        );
        client.DefaultRequestHeaders.Add("x-api-key", options.ImmichApiKey);
        return client;
    }

    private async Task<ImmichSearchAssets> FetchTrashedAssetsAsync(
        HttpClient client,
        ImmichSearchRequest request
    )
    {
        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                "api/search/metadata",
                request,
                ImmichJsonContext.Default.ImmichSearchRequest,
                cancellationToken
            )
            .ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();

        ImmichSearchResponse? searchResponse = await response
            .Content.ReadFromJsonAsync(
                ImmichJsonContext.Default.ImmichSearchResponse,
                cancellationToken
            )
            .ConfigureAwait(false);

        return searchResponse?.Assets ?? new ImmichSearchAssets();
    }

    internal static string? ConvertChecksum(string base64Checksum)
    {
        if (string.IsNullOrWhiteSpace(base64Checksum))
        {
            return null;
        }

        string trimmed = base64Checksum.Trim();
        byte[] buffer = new byte[(trimmed.Length + 3) / 4 * 3];
        return
            !Convert.TryFromBase64String(trimmed, buffer, out int bytesWritten)
            || bytesWritten != 20
            ? null
            : Convert.ToHexStringLower(buffer.AsSpan(0, bytesWritten));
    }
}
