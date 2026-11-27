using System.Text;
using System.Text.Json;
using PhotoCleaner;
using Serilog;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class TrashCommandTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"trashcmd_{Path.GetRandomFileName()}.db");

    internal static CommandLine.Options CreateOptions(
        string dbPath,
        string? processDbPath = null
    ) =>
        new()
        {
            Path = new DirectoryInfo(Path.GetTempPath()),
            Threads = 1,
            DryRun = false,
            DatePath = false,
            SkipBackup = false,
            OutPath = null,
            Format = "yyyy/MM/dd",
            DeleteEmpty = false,
            Move = false,
            TagPath = false,
            Tags = null,
            DbFile = processDbPath is null ? null : new FileInfo(processDbPath),
            Rehash = false,
            ShortVideoDuration = MediaUtilities.ShortVideoDuration,
            Reprocess = false,
            MarkProcessed = false,
            ImmichUrl = "http://localhost:9999",
            ImmichApiKey = "test-api-key",
            TrashDbFile = new FileInfo(dbPath),
            SkipDbFile = null,
            LogOptions = new LoggerFactory.Options
            {
                Level = LogEventLevel.Information,
                File = null,
                FileClear = false,
            },
        };

    internal static ImmichSearchResponse MakeResponse(
        string[] base64Checksums,
        string? nextCursor = null
    )
    {
        List<ImmichAssetDto> items = [];
        int index = 0;
        foreach (string checksum in base64Checksums)
        {
            items.Add(
                new ImmichAssetDto
                {
                    Id = $"asset-{index}",
                    Checksum = checksum,
                    OriginalFileName = $"photo_{index}.jpg",
                    IsTrashed = true,
                }
            );
            index++;
        }
        return new ImmichSearchResponse
        {
            Assets = new ImmichSearchAssets { Items = items, NextCursor = nextCursor },
        };
    }

    private static ImmichSearchResponse EmptyResponse() =>
        new()
        {
            Assets = new ImmichSearchAssets { Items = [], NextCursor = null },
        };

    // "J76BZtEWfrTV8jm2GjwQRuuO8mY=" is a real Immich-style Base64-encoded SHA-1 checksum (20 bytes -> 40 hex chars)

    [Fact]
    public void ConvertChecksum_Base64ToHex_ReturnsLowercaseHex()
    {
        // 20 bytes of 0xAB -> base64
        byte[] bytes = new byte[20];
        Array.Fill(bytes, (byte)0xAB);
        string base64 = Convert.ToBase64String(bytes);

        string? hex = TrashCommand.ConvertChecksum(base64);

        hex.Should().NotBeNull();
        hex.Should().Be("abababababababababababababababababababab");
        hex.Should().HaveLength(40); // SHA-1 = 20 bytes = 40 hex chars
    }

    [Fact]
    public void ConvertChecksum_RealImmichChecksum_Converts()
    {
        // A realistic Immich checksum (base64-encoded SHA-1)
        string base64 = "J76BZtEWfrTV8jm2GjwQRuuO8mY=";

        string? hex = TrashCommand.ConvertChecksum(base64);

        hex.Should().NotBeNull();
        hex.Should().HaveLength(40);
        hex.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void ConvertChecksum_EmptyString_ReturnsNull() =>
        TrashCommand.ConvertChecksum("").Should().BeNull();

    [Fact]
    public void ConvertChecksum_InvalidBase64_ReturnsNull() =>
        TrashCommand.ConvertChecksum("not-valid-base64!!!").Should().BeNull();

    internal static async Task<(int ExitCode, long Count)> RunCommandAsync(
        HttpMessageHandler handler,
        CancellationToken cancellationToken,
        Func<TrashDatabase, Task>? inspect = null
    )
    {
        string dbPath = TempDb();
        try
        {
            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999/"),
            };
            int exitCode = await new TrashCommand(
                CreateOptions(dbPath),
                cancellationToken,
                client
            ).ExecuteAsync();

            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            if (inspect is not null)
            {
                await inspect(db);
            }
            return (exitCode, count);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    private static async Task<(int ExitCode, long Count, int Requests)> RunSyncAsync(
        MockImmichHandler handler,
        Func<TrashDatabase, Task>? inspect = null
    )
    {
        (int exitCode, long count) = await RunCommandAsync(
            handler,
            TestContext.Current.CancellationToken,
            inspect
        );
        return (exitCode, count, handler.RequestCount);
    }

    private static string Sha1Base64(byte first)
    {
        byte[] sha1 = new byte[20];
        sha1[0] = first;
        return Convert.ToBase64String(sha1);
    }

    // Pagination stopping early leaves the database short of the server.
    // Callers use it to skip files, so a short one silently re-imports trashed assets.
    [Fact]
    public async Task ExecuteAsync_RepeatedNextCursor_ExitsFailed()
    {
        using MockImmichHandler handler = new();
        handler.EnqueueResponse(MakeResponse([Sha1Base64(0x01)], nextCursor: "c1"));
        handler.EnqueueResponse(MakeResponse([Sha1Base64(0x02)], nextCursor: "c1"));

        (int exitCode, long count, int requests) = await RunSyncAsync(handler);

        exitCode.Should().Be(2);
        requests.Should().Be(2);
        count.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyPageNamingCursor_ExitsFailed()
    {
        using MockImmichHandler handler = new();
        handler.EnqueueResponse(MakeResponse([Sha1Base64(0x01)], nextCursor: "c1"));
        handler.EnqueueResponse(MakeResponse([], nextCursor: "c2"));

        (int exitCode, long count, int requests) = await RunSyncAsync(handler);

        exitCode.Should().Be(2);
        requests.Should().Be(2);
        count.Should().Be(1);
    }

    internal const string AssetJson =
        """{"id":"asset-0","checksum":"AQAAAAAAAAAAAAAAAAAAAAAAAAA=","originalFileName":"photo_0.jpg","isTrashed":true}""";

    internal const string LiveAssetJson =
        """{"id":"asset-1","checksum":"AgAAAAAAAAAAAAAAAAAAAAAAAAA=","originalFileName":"photo_1.jpg","isTrashed":false}""";

    // An Immich server older than v3.2.0 drops the filter and cursor it does not know.
    // It answers a page-based search of live assets with no nextCursor key.
    [Theory]
    [InlineData("""{"assets":{"items":[LIVE,LIVE],"nextPage":"2"}}""", 0)]
    [InlineData("""{"assets":{"items":[LIVE],"nextPage":null}}""", 0)]
    [InlineData("""{"assets":{"items":[ASSET],"nextPage":"2"}}""", 1)]
    [InlineData("""{"assets":{"items":[ASSET],"nextPage":null}}""", 1)]
    [InlineData("""{"assets":{"items":[ASSET]}}""", 1)]
    [InlineData("""{"assets":{"items":[],"nextPage":null}}""", 0)]
    [InlineData("""{"assets":{"items":[]}}""", 0)]
    [InlineData("""{}""", 0)]
    [InlineData("""{"assets":null}""", 0)]
    [InlineData("""null""", 0)]
    public async Task ExecuteAsync_NoNextCursorKey_ExitsFailed(
        string answerJson,
        long expectedCount
    )
    {
        using MockImmichHandler handler = new();
        handler.EnqueueJson(
            answerJson
                .Replace("ASSET", AssetJson, StringComparison.Ordinal)
                .Replace("LIVE", LiveAssetJson, StringComparison.Ordinal)
        );

        (int exitCode, long count, int requests) = await RunSyncAsync(handler);

        exitCode.Should().Be(2);
        requests.Should().Be(1);
        count.Should().Be(expectedCount);
    }

    [Theory]
    [InlineData("""{"assets":{"items":[ASSET],"nextPage":null,"nextCursor":null}}""", 1)]
    [InlineData("""{"assets":{"items":[ASSET],"nextCursor":null}}""", 1)]
    [InlineData("""{"assets":{"items":[ASSET],"nextPage":"2","nextCursor":null}}""", 1)]
    [InlineData("""{"assets":{"items":[],"nextPage":null,"nextCursor":null}}""", 0)]
    public async Task ExecuteAsync_ExplicitNullNextCursor_ExitsSuccess(
        string answerJson,
        long expectedCount
    )
    {
        using MockImmichHandler handler = new();
        handler.EnqueueJson(answerJson.Replace("ASSET", AssetJson, StringComparison.Ordinal));

        (int exitCode, long count, int requests) = await RunSyncAsync(handler);

        exitCode.Should().Be(0);
        requests.Should().Be(1);
        count.Should().Be(expectedCount);
    }

    // Immich rejects a request that mixes the structured shape with any deprecated flat field.
    [Fact]
    public async Task ExecuteAsync_Requests_UseStructuredFilterAndCursor()
    {
        using MockImmichHandler handler = new();
        handler.EnqueueResponse(MakeResponse([Sha1Base64(0x01)], nextCursor: "c1"));
        handler.EnqueueResponse(MakeResponse([Sha1Base64(0x02)], nextCursor: "c2"));
        handler.EnqueueResponse(MakeResponse([Sha1Base64(0x03)]));

        (int exitCode, _, _) = await RunSyncAsync(handler);

        exitCode.Should().Be(0);
        handler.RequestBodies.Should().HaveCount(3);
        using JsonDocument first = JsonDocument.Parse(handler.RequestBodies[0]);
        using JsonDocument second = JsonDocument.Parse(handler.RequestBodies[1]);
        using JsonDocument third = JsonDocument.Parse(handler.RequestBodies[2]);
        foreach (JsonDocument body in new[] { first, second, third })
        {
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Should()
                .BeSubsetOf(["filter", "size", "cursor"]);
            JsonElement trashedAt = body.RootElement.GetProperty("filter").GetProperty("trashedAt");
            trashedAt.EnumerateObject().Select(property => property.Name).Should().Equal("ne");
            trashedAt.GetProperty("ne").ValueKind.Should().Be(JsonValueKind.Null);
            body.RootElement.GetProperty("size").GetInt32().Should().Be(1000);
        }
        first.RootElement.TryGetProperty("cursor", out _).Should().BeFalse();
        second.RootElement.GetProperty("cursor").GetString().Should().Be("c1");
        third.RootElement.GetProperty("cursor").GetString().Should().Be("c2");
    }

    [Fact]
    public async Task ExecuteAsync_SinglePage_InsertsHashes()
    {
        string sha1a = Sha1Base64(0x01);
        string sha1b = Sha1Base64(0x02);
        using MockImmichHandler handler = new();
        handler.EnqueueResponse(MakeResponse([sha1a, sha1b], nextCursor: "c1"));
        handler.EnqueueResponse(EmptyResponse()); // page 2 empty, no cursor -> stop

        (int exitCode, long count, _) = await RunSyncAsync(handler, async db =>
        {
            foreach (string base64 in new[] { sha1a, sha1b })
            {
                (
                    await db.Sha1ExistsAsync(
                        TrashCommand.ConvertChecksum(base64)!,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                )
                    .Should()
                    .BeTrue();
            }
        });

        exitCode.Should().Be(0);
        count.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePages_PaginatesUntilEmpty()
    {
        using MockImmichHandler handler = new();
        handler.EnqueueResponse(MakeResponse([Sha1Base64(0x10)], nextCursor: "c1")); // page 1
        handler.EnqueueResponse(MakeResponse([Sha1Base64(0x20)], nextCursor: "c2")); // page 2
        handler.EnqueueResponse(EmptyResponse()); // page 3 empty, no cursor -> stop

        (int exitCode, long count, int requests) = await RunSyncAsync(handler);

        exitCode.Should().Be(0);
        count.Should().Be(2);
        requests.Should().Be(3); // 3 requests: page 1, 2, 3 (empty)
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTrash_InsertsNothing()
    {
        using MockImmichHandler handler = new();
        handler.EnqueueResponse(EmptyResponse()); // first page empty

        (int exitCode, long count, _) = await RunSyncAsync(handler);

        exitCode.Should().Be(0); // an empty page naming no cursor is not a short sync
        count.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateHashes_Idempotent()
    {
        string base64 = Sha1Base64(0x42);
        using MockImmichHandler handler = new();
        // Same hash appears twice in the same page
        handler.EnqueueResponse(MakeResponse([base64, base64], nextCursor: "c1"));
        handler.EnqueueResponse(EmptyResponse());

        (int exitCode, long count, _) = await RunSyncAsync(handler);

        exitCode.Should().Be(0);
        count.Should().Be(1); // The upsert on sha1 keeps one row per checksum
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_ProcessDb_RecordsImageHashOnlyWhenGiven(bool withProcessDb)
    {
        string dbPath = TempDb();
        string processDbPath = TempDb();
        try
        {
            byte[] sha1a = new byte[20];
            sha1a[0] = 0x01;
            byte[] sha1b = new byte[20];
            sha1b[0] = 0x02;

            // A row synced earlier for an asset the server has since purged
            byte[] sha1c = new byte[20];
            sha1c[0] = 0x03;
            await using (TrashDatabase seed = new(dbPath))
            {
                await seed.InitializeAsync(TestContext.Current.CancellationToken);
                await seed.InsertHashAsync(
                    Convert.ToHexStringLower(sha1c),
                    "IMG_5678.HEIC",
                    cancellationToken: TestContext.Current.CancellationToken
                );
            }

            // The process database knows the first asset and the purged one, not the second
            await using (Database processDb = new(processDbPath))
            {
                await processDb.InitializeAsync(TestContext.Current.CancellationToken);
                await processDb.InsertAsync(
                    new FileRecord(
                        "/processed/IMG_5678.HEIC",
                        "sha256-c",
                        Convert.ToHexStringLower(sha1c),
                        10L,
                        0L,
                        true,
                        ImageHash: "imagehash-c",
                        MetadataRead: true
                    ),
                    TestContext.Current.CancellationToken
                );
                await processDb.InsertAsync(
                    new FileRecord(
                        "/processed/IMG_1234.HEIC",
                        "sha256-a",
                        Convert.ToHexStringLower(sha1a),
                        10L,
                        0L,
                        true,
                        ImageHash: "imagehash-a",
                        MetadataRead: true
                    ),
                    TestContext.Current.CancellationToken
                );
            }

            using MockImmichHandler handler = new();
            handler.EnqueueResponse(
                MakeResponse([Convert.ToBase64String(sha1a), Convert.ToBase64String(sha1b)])
            );

            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999"),
            };
            TrashCommand command = new(
                CreateOptions(dbPath, withProcessDb ? processDbPath : null),
                TestContext.Current.CancellationToken,
                client
            );
            int exitCode = await command.ExecuteAsync();

            exitCode.Should().Be(ExitCode.Success);
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            (await db.GetCountAsync(TestContext.Current.CancellationToken)).Should().Be(3);
            (await db.ImageHashExistsAsync("imagehash-a", TestContext.Current.CancellationToken))
                .Should()
                .Be(withProcessDb);
            (await db.ImageHashExistsAsync("imagehash-c", TestContext.Current.CancellationToken))
                .Should()
                .Be(withProcessDb);
            (await db.GetSha1sWithoutImageHashAsync(TestContext.Current.CancellationToken))
                .Should()
                .HaveCount(withProcessDb ? 1 : 3);
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(processDbPath);
        }
    }

    internal sealed class MockImmichHandler : DelegatingHandler
    {
        private readonly Queue<string> _responses = new();

        internal int RequestCount { get; private set; }

        internal List<string> RequestBodies { get; } = [];

        internal void EnqueueResponse(ImmichSearchResponse response) =>
            _responses.Enqueue(
                JsonSerializer.Serialize(response, ImmichJsonContext.Default.ImmichSearchResponse)
            );

        internal void EnqueueJson(string json) => _responses.Enqueue(json);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            RequestBodies.Add(
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)
            );
            string json = _responses.Dequeue();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}

[Collection(GlobalLoggerDefinition.Name)]
public sealed class TrashCommandLogTests
{
    private static async Task<(int ExitCode, CapturingSink Sink)> RunAsync(
        HttpMessageHandler handler,
        CancellationToken token
    )
    {
        CapturingSink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Sink(sink)
                .CreateLogger();
            (int exitCode, _) = await TrashCommandTests.RunCommandAsync(handler, token);
            return (exitCode, sink);
        }
        finally
        {
            Log.Logger = savedLogger;
        }
    }

    [Theory]
    [InlineData("""{"assets":{"items":[],"nextCursor":"c2"}}""", "empty page 2 names cursor")]
    [InlineData("""{"assets":{"items":[ASSET],"nextCursor":"c1"}}""", "repeated NextCursor")]
    [InlineData("""{"assets":{"items":[ASSET],"nextPage":"2"}}""", "has no nextCursor field")]
    public async Task ExecuteAsync_ShortSync_LogsMatchingError(string secondAnswer, string expected)
    {
        using TrashCommandTests.MockImmichHandler handler = new();
        handler.EnqueueResponse(
            TrashCommandTests.MakeResponse([Convert.ToBase64String(new byte[20])], nextCursor: "c1")
        );
        handler.EnqueueJson(
            secondAnswer.Replace("ASSET", TrashCommandTests.AssetJson, StringComparison.Ordinal)
        );

        (int exitCode, CapturingSink sink) = await RunAsync(
            handler,
            TestContext.Current.CancellationToken
        );

        exitCode.Should().Be(2);
        sink.Events.Should()
            .ContainSingle(e =>
                e.Level == LogEventLevel.Error
                && e.RenderMessage().Contains(expected, StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task ExecuteAsync_RequestTimesOut_LogsErrorNamingRequest()
    {
        using ThrowingHandler handler = new(() =>
            new TaskCanceledException("timed out", new TimeoutException())
        );

        (int exitCode, CapturingSink sink) = await RunAsync(
            handler,
            TestContext.Current.CancellationToken
        );

        exitCode.Should().Be(1);
        sink.Events.Should()
            .Contain(e =>
                e.Level == LogEventLevel.Error
                && e.Exception != null
                && e.Exception.Message.Contains("api/search/metadata", StringComparison.Ordinal)
                && e.Exception.Message.Contains("timed out", StringComparison.Ordinal)
            );
        sink.Events.Should()
            .NotContain(e => e.RenderMessage().Contains("cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_UserCancels_LogsCancelNotError()
    {
        using CancellationTokenSource cts = new();
        using ThrowingHandler handler = new(() =>
        {
            cts.Cancel();
            return new TaskCanceledException("canceled", null, cts.Token);
        });

        (int exitCode, CapturingSink sink) = await RunAsync(handler, cts.Token);

        exitCode.Should().Be(1);
        sink.Events.Should()
            .Contain(e =>
                e.Level == LogEventLevel.Information
                && e.RenderMessage().Contains("cancelled", StringComparison.Ordinal)
            );
        sink.Events.Should().NotContain(e => e.Level == LogEventLevel.Error);
    }

    private sealed class ThrowingHandler(Func<Exception> create) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromException<HttpResponseMessage>(create());
    }
}
