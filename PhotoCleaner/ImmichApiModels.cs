using System.Text.Json.Serialization;

namespace PhotoCleaner;

internal sealed class ImmichSearchRequest
{
    [JsonPropertyName("trashedAfter")]
    public string TrashedAfter { get; set; } = "1970-01-01T00:00:00.000Z";

    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("size")]
    public int Size { get; set; } = 1000;
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

    [JsonPropertyName("nextPage")]
    public string? NextPage { get; set; }
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
