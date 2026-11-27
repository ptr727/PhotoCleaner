using System.Text.Json.Serialization;

namespace PhotoCleaner;

internal sealed class ImmichSearchRequest
{
    [JsonPropertyName("filter")]
    public ImmichSearchFilter Filter { get; set; } = new();

    [JsonPropertyName("size")]
    public int Size { get; set; } = 1000;

    [JsonPropertyName("cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cursor { get; set; }
}

internal sealed class ImmichSearchFilter
{
    [JsonPropertyName("trashedAt")]
    public ImmichNullableDateFilter TrashedAt { get; set; } = new();
}

internal sealed class ImmichNullableDateFilter
{
    [JsonPropertyName("ne")]
    public string? Ne { get; set; }
}

internal sealed class ImmichSearchResponse
{
    [JsonPropertyName("assets")]
    public ImmichSearchAssets Assets { get; set; } = new();
}

internal sealed class ImmichSearchAssets
{
    [JsonPropertyName("items")]
    public List<ImmichAssetDto> Items { get; set; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor
    {
        get;
        set
        {
            field = value;
            HasNextCursorKey = true;
        }
    }

    [JsonIgnore]
    public bool HasNextCursorKey { get; private set; }
}

internal sealed class ImmichAssetDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("checksum")]
    public string Checksum { get; set; } = string.Empty;

    [JsonPropertyName("originalFileName")]
    public string OriginalFileName { get; set; } = string.Empty;

    [JsonPropertyName("isTrashed")]
    public bool IsTrashed { get; set; }
}

[JsonSerializable(typeof(ImmichSearchRequest))]
[JsonSerializable(typeof(ImmichSearchResponse))]
internal partial class ImmichJsonContext : JsonSerializerContext;
