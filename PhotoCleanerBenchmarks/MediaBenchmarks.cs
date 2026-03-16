namespace PhotoCleanerBenchmarks;

[MemoryDiagnoser]
public class MediaBenchmarks : IDisposable
{
    private string _tempDir = string.Empty;
    private string _jpegPath = string.Empty;
    private string _movPath = string.Empty;
    private Database _database = null!;
    private string _preInsertedHash = string.Empty;
    private long _jpegFileSize;
    private string _organizedTo = string.Empty;
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

        _jpegFileSize = new FileInfo(_jpegPath).Length;
        _organizedTo = Path.Combine(_tempDir, "organized", "sample.jpg");

        string dbPath = Path.Combine(_tempDir, "bench.db");
        _database = new Database(dbPath);
        await _database.InitializeAsync();

        _preInsertedHash = await Database.ComputeHashAsync(_jpegPath);
        MediaFileRecord seedRecord = new(
            _preInsertedHash,
            _jpegPath,
            "sample.jpg",
            null,
            null,
            _jpegFileSize,
            "image/jpeg",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            _organizedTo
        );
        await _database.InsertAsync(seedRecord);
    }

    public void Dispose()
    {
        _database?.Dispose();
        GC.SuppressFinalize(this);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _database.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
    }

    [Benchmark]
    public async Task<string> Sha256Hash() => await Database.ComputeHashAsync(_jpegPath);

    [Benchmark]
    public async Task<ExifToolJson?> ExifToolLoad() =>
        await ProcessTask.GetExifToolJsonAsync(_jpegPath);

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
        string hash = $"bench_{counter:D10}";
        MediaFileRecord record = new(
            hash,
            _jpegPath,
            "sample.jpg",
            null,
            null,
            _jpegFileSize,
            "image/jpeg",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            _organizedTo
        );
        await _database.InsertAsync(record);
    }

    [Benchmark]
    public async Task<string?> DbRead() => await _database.GetSourcePathAsync(_preInsertedHash);
}
