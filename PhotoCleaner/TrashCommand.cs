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
            .RunAsync(
                "Trash",
                async () =>
                {
                    await TrashDatabaseScope
                        .RunAsync(
                            options.TrashDbFile,
                            async trashDatabase =>
                            {
                                if (trashDatabase is null)
                                {
                                    Log.Error("Trash database path is required");
                                    return;
                                }

                                int totalFetched = 0;
                                int totalInserted = 0;
                                long countBefore = await trashDatabase
                                    .GetCountAsync(cancellationToken)
                                    .ConfigureAwait(false);

                                HttpClient client = httpClient ?? CreateDefaultHttpClient();
                                int page = 1;
                                bool hasMore = true;

                                while (hasMore)
                                {
                                    Log.Information("Fetching trashed assets page {Page}", page);
                                    ImmichSearchRequest request = new() { Page = page };
                                    List<ImmichAssetDto> assets = await FetchTrashedAssetsAsync(
                                            client,
                                            request
                                        )
                                        .ConfigureAwait(false);

                                    if (assets.Count == 0)
                                    {
                                        hasMore = false;
                                        continue;
                                    }

                                    totalFetched += assets.Count;

                                    foreach (ImmichAssetDto asset in assets)
                                    {
                                        string sha1Hex = ConvertChecksum(asset.Checksum);
                                        await trashDatabase
                                            .InsertHashAsync(sha1Hex, cancellationToken)
                                            .ConfigureAwait(false);
                                    }

                                    Log.Information(
                                        "Fetched {Count} trashed assets on page {Page}",
                                        assets.Count,
                                        page
                                    );
                                    page++;
                                }

                                long countAfter = await trashDatabase
                                    .GetCountAsync(cancellationToken)
                                    .ConfigureAwait(false);
                                totalInserted = (int)(countAfter - countBefore);

                                Log.Information(
                                    "Fetched {TotalFetched} trashed assets, inserted {TotalInserted} new hashes (total {TotalCount})",
                                    totalFetched,
                                    totalInserted,
                                    countAfter
                                );
                            },
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            )
            .ConfigureAwait(false);

    private HttpClient CreateDefaultHttpClient()
    {
        HttpClient client = HttpClientFactory.GetHttpClient();
        client.BaseAddress = new Uri(options.ImmichUrl!);
        _ = client.DefaultRequestHeaders.Remove("x-api-key");
        client.DefaultRequestHeaders.Add("x-api-key", options.ImmichApiKey);
        return client;
    }

    private async Task<List<ImmichAssetDto>> FetchTrashedAssetsAsync(
        HttpClient client,
        ImmichSearchRequest request
    )
    {
        HttpResponseMessage response = await client
            .PostAsJsonAsync(
                "/api/search/metadata",
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

        return searchResponse?.Assets.Items ?? [];
    }

    internal static string ConvertChecksum(string base64Checksum) =>
        Convert.ToHexStringLower(Convert.FromBase64String(base64Checksum));
}
