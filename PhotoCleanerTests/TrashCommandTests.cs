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

    private static ImmichSearchResponse MakeResponse(
        string[] base64Checksums,
        string? nextPage = null
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
            Assets = new ImmichSearchAssets { Items = items, NextPage = nextPage },
        };
    }

    private static ImmichSearchResponse EmptyResponse() =>
        new()
        {
            Assets = new ImmichSearchAssets { Items = [], NextPage = null },
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

    // Pagination stopping early leaves the database short of the server.
    // Callers use it to skip files, so a short one silently re-imports trashed assets.
    [Fact]
    public async Task ExecuteAsync_InvalidNextPage_ExitsFailed()
    {
        string dbPath = TempDb();
        try
        {
            // Arrange - a page that hands back a NextPage the loop cannot use
            byte[] sha1 = new byte[20];
            sha1[0] = 0x01;
            using MockImmichHandler handler = new();
            handler.EnqueueResponse(
                MakeResponse([Convert.ToBase64String(sha1)], nextPage: "not-a-page")
            );

            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999"),
            };
            TrashCommand command = new(
                CreateOptions(dbPath),
                TestContext.Current.CancellationToken,
                client
            );

            // Act
            int exitCode = await command.ExecuteAsync();

            // Assert - the page it did read is kept, and the run reports the shortfall
            exitCode.Should().Be(2);
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(1);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    // An empty page that names a later page is a short sync, the same as an unusable NextPage.
    [Fact]
    public async Task ExecuteAsync_EmptyPageNamingLaterPage_ExitsFailed()
    {
        string dbPath = TempDb();
        try
        {
            // Arrange - page 1 has an asset, page 2 is empty yet names page 3
            byte[] sha1 = new byte[20];
            sha1[0] = 0x01;
            using MockImmichHandler handler = new();
            handler.EnqueueResponse(MakeResponse([Convert.ToBase64String(sha1)], nextPage: "2"));
            handler.EnqueueResponse(MakeResponse([], nextPage: "3"));

            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999"),
            };
            TrashCommand command = new(
                CreateOptions(dbPath),
                TestContext.Current.CancellationToken,
                client
            );

            // Act
            int exitCode = await command.ExecuteAsync();

            // Assert - the page it did read is kept, and the run reports the shortfall
            exitCode.Should().Be(2);
            handler.RequestCount.Should().Be(2);
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(1);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    // An empty page with a non-null NextPage that is not a forward page is a short sync.
    [Theory]
    [InlineData("not-a-page")]
    [InlineData("2")]
    [InlineData("1")]
    public async Task ExecuteAsync_EmptyPageInvalidNextPage_ExitsFailed(string emptyNextPage)
    {
        string dbPath = TempDb();
        try
        {
            // Arrange - page 1 has an asset, page 2 is empty yet names a malformed, repeated, or earlier page
            byte[] sha1 = new byte[20];
            sha1[0] = 0x01;
            using MockImmichHandler handler = new();
            handler.EnqueueResponse(MakeResponse([Convert.ToBase64String(sha1)], nextPage: "2"));
            handler.EnqueueResponse(MakeResponse([], nextPage: emptyNextPage));

            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999"),
            };
            TrashCommand command = new(
                CreateOptions(dbPath),
                TestContext.Current.CancellationToken,
                client
            );

            // Act
            int exitCode = await command.ExecuteAsync();

            // Assert - the page it did read is kept, and the run reports the shortfall
            exitCode.Should().Be(2);
            handler.RequestCount.Should().Be(2);
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(1);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SinglePage_InsertsHashes()
    {
        string dbPath = TempDb();
        try
        {
            // Create two checksums (base64 of 20-byte SHA-1 values)
            byte[] sha1a = new byte[20];
            sha1a[0] = 0x01;
            byte[] sha1b = new byte[20];
            sha1b[0] = 0x02;

            using MockImmichHandler handler = new();
            handler.EnqueueResponse(
                MakeResponse(
                    [Convert.ToBase64String(sha1a), Convert.ToBase64String(sha1b)],
                    nextPage: "2"
                )
            );
            handler.EnqueueResponse(EmptyResponse()); // page 2 empty -> stop

            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999"),
            };
            TrashCommand command = new(
                CreateOptions(dbPath),
                TestContext.Current.CancellationToken,
                client
            );
            await command.ExecuteAsync();

            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(2);

            bool exists1 = await db.Sha1ExistsAsync(
                Convert.ToHexStringLower(sha1a),
                cancellationToken: TestContext.Current.CancellationToken
            );
            exists1.Should().BeTrue();
            bool exists2 = await db.Sha1ExistsAsync(
                Convert.ToHexStringLower(sha1b),
                cancellationToken: TestContext.Current.CancellationToken
            );
            exists2.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePages_PaginatesUntilEmpty()
    {
        string dbPath = TempDb();
        try
        {
            byte[] sha1a = new byte[20];
            sha1a[0] = 0x10;
            byte[] sha1b = new byte[20];
            sha1b[0] = 0x20;

            using MockImmichHandler handler = new();
            handler.EnqueueResponse(MakeResponse([Convert.ToBase64String(sha1a)], nextPage: "2")); // page 1
            handler.EnqueueResponse(MakeResponse([Convert.ToBase64String(sha1b)], nextPage: "3")); // page 2
            handler.EnqueueResponse(EmptyResponse()); // page 3 empty -> stop

            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999"),
            };
            TrashCommand command = new(
                CreateOptions(dbPath),
                TestContext.Current.CancellationToken,
                client
            );
            await command.ExecuteAsync();

            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(2);

            handler.RequestCount.Should().Be(3); // 3 requests: page 1, 2, 3 (empty)
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTrash_InsertsNothing()
    {
        string dbPath = TempDb();
        try
        {
            using MockImmichHandler handler = new();
            handler.EnqueueResponse(EmptyResponse()); // first page empty

            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999"),
            };
            TrashCommand command = new(
                CreateOptions(dbPath),
                TestContext.Current.CancellationToken,
                client
            );
            int exitCode = await command.ExecuteAsync();
            exitCode.Should().Be(0); // an empty page naming no later page is not a short sync

            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(0);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateHashes_Idempotent()
    {
        string dbPath = TempDb();
        try
        {
            byte[] sha1 = new byte[20];
            sha1[0] = 0x42;
            string base64 = Convert.ToBase64String(sha1);

            using MockImmichHandler handler = new();
            // Same hash appears twice in the same page
            handler.EnqueueResponse(MakeResponse([base64, base64], nextPage: "2"));
            handler.EnqueueResponse(EmptyResponse());

            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999"),
            };
            TrashCommand command = new(
                CreateOptions(dbPath),
                TestContext.Current.CancellationToken,
                client
            );
            await command.ExecuteAsync();

            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(1); // The upsert on sha1 keeps one row per checksum
        }
        finally
        {
            File.Delete(dbPath);
        }
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
        private readonly Queue<ImmichSearchResponse> _responses = new();

        internal int RequestCount { get; private set; }

        internal void EnqueueResponse(ImmichSearchResponse response) =>
            _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            ImmichSearchResponse response = _responses.Dequeue();
            string json = JsonSerializer.Serialize(
                response,
                ImmichJsonContext.Default.ImmichSearchResponse
            );
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
            );
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
        string dbPath = Path.Combine(Path.GetTempPath(), $"trashcmd_{Path.GetRandomFileName()}.db");
        CapturingSink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Sink(sink)
                .CreateLogger();
            using HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:9999/"),
            };
            int exitCode = await new TrashCommand(
                TrashCommandTests.CreateOptions(dbPath),
                token,
                client
            ).ExecuteAsync();
            return (exitCode, sink);
        }
        finally
        {
            Log.Logger = savedLogger;
            File.Delete(dbPath);
        }
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
