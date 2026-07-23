namespace PhotoCleanerBenchmarks;

[MemoryDiagnoser]
public class MediaBenchmarks : IAsyncDisposable
{
    private string _tempDir = string.Empty;
    private string _jpegPath = string.Empty;
    private string _movPath = string.Empty;
    private Database _database = null!;
    private string _preInsertedHash = string.Empty;
    private long _jpegFileSize;
    private long _jpegMtimeTicks;
    private int _insertCounter;

    [GlobalSetup]
    public async Task Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PhotoCleanerBenchmarks_{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(_tempDir);

        _jpegPath = Path.Combine(_tempDir, "sample.jpg");
        _ = await Cli.Wrap("ffmpeg")
            .WithArguments([
                "-f",
                "lavfi",
                "-i",
                "color=red:size=100x100:duration=1",
                "-frames:v",
                "1",
                _jpegPath,
            ])
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync();

        _movPath = Path.Combine(_tempDir, "sample.mov");
        _ = await Cli.Wrap("ffmpeg")
            .WithArguments([
                "-f",
                "lavfi",
                "-i",
                "color=blue:size=100x100:duration=3",
                "-c:v",
                "libx264",
                _movPath,
            ])
            .WithStandardErrorPipe(PipeTarget.Null)
            .ExecuteAsync();

        FileInfo jpegInfo = new(_jpegPath);
        _jpegFileSize = jpegInfo.Length;
        _jpegMtimeTicks = jpegInfo.LastWriteTimeUtc.Ticks;

        string dbPath = Path.Combine(_tempDir, "bench.db");
        _database = new Database(dbPath);
        await _database.InitializeAsync();

        (_preInsertedHash, string sha1) = await Database.ComputeHashesAsync(_jpegPath);
        FileRecord seedRecord = new(
            _jpegPath,
            _preInsertedHash,
            sha1,
            _jpegFileSize,
            _jpegMtimeTicks,
            false
        );
        await _database.InsertAsync(seedRecord);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await DisposeAsync();
        if (_tempDir.Length > 0 && Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }

        GC.SuppressFinalize(this);
    }

    [Benchmark]
    public async Task<(string, string)> ComputeHashes() =>
        await Database.ComputeHashesAsync(_jpegPath);

    [Benchmark]
    public async Task<ExifToolJson?> ExifToolLoad() =>
        await MediaUtilities.GetExifToolJsonAsync(_jpegPath);

    [Benchmark]
    public async Task<float> FfprobeGetDuration()
    {
        BufferedCommandResult result = await Cli.Wrap("ffprobe")
            .WithArguments([
                "-loglevel",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "format=duration",
                "-of",
                "default=nw=1:nk=1",
                _movPath,
            ])
            .ExecuteBufferedAsync();
        return float.Parse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture);
    }

    [Benchmark]
    public async Task DbInsert()
    {
        int counter = Interlocked.Increment(ref _insertCounter);
        string path = Path.Combine(_tempDir, $"bench_{counter:D10}.jpg");
        FileRecord record = new(
            path,
            $"bench_{counter:D10}",
            $"bench_sha1_{counter:D10}",
            _jpegFileSize,
            _jpegMtimeTicks,
            false
        );
        await _database.InsertAsync(record);
    }

    [Benchmark]
    public async Task<bool> DbRead() => await _database.Sha256ExistsAsync(_preInsertedHash);
}
