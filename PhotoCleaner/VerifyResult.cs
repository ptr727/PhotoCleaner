using System.Text.Json.Serialization;

namespace PhotoCleaner;

// One line of the JSON protocol emitted by the in-container verify script.
internal sealed class ImmichVerifyLine
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("via")]
    public string? Via { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

[JsonSerializable(typeof(ImmichVerifyLine))]
internal partial class ImmichVerifyJsonContext : JsonSerializerContext;
