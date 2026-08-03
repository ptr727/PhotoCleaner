using System.Text;
using System.Text.Json;
using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class TrashCommandTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"trashcmd_{Path.GetRandomFileName()}.db");

    private static CommandLine.Options CreateOptions(string dbPath) =>
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
            DbFile = null,
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
            await command.ExecuteAsync();

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
            count.Should().Be(1); // INSERT OR IGNORE deduplicates
        }
        finally
        {
            File.Delete(dbPath);
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
