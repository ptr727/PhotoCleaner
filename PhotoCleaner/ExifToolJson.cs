using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoCleaner;

// Handles exiftool outputting XMP:Subject as a bare string, bare number, or mixed array
internal sealed class StringOrStringArrayConverter : JsonConverter<string[]?>
{
    public override string[]? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString()!];
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return [ReadNumberAsString(ref reader)];
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            List<string> list = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                list.Add(
                    reader.TokenType == JsonTokenType.String
                        ? reader.GetString()!
                        : ReadNumberAsString(ref reader)
                );
            }

            return [.. list];
        }

        throw new JsonException($"Unexpected token {reader.TokenType} for XMP:Subject");
    }

    private static string ReadNumberAsString(ref Utf8JsonReader reader) =>
        reader.TryGetInt64(out long l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : reader.GetDouble().ToString(CultureInfo.InvariantCulture);

    public override void Write(
        Utf8JsonWriter writer,
        string[]? value,
        JsonSerializerOptions options
    )
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (string s in value)
        {
            writer.WriteStringValue(s);
        }

        writer.WriteEndArray();
    }
}

public sealed class ExifToolJson
{
    [JsonPropertyName("File:FileModifyDate")]
    public string? FileModifyDate { get; set; }

    [JsonPropertyName("File:FileType")]
    public string? FileType { get; set; }

    [JsonPropertyName("File:FileTypeExtension")]
    public string? FileTypeExtension { get; set; }

    [JsonPropertyName("File:MIMEType")]
    public string? MIMEType { get; set; }

    [JsonPropertyName("H264:DateTimeOriginal")]
    public string? H264DateTimeOriginal { get; set; }

    [JsonPropertyName("QuickTime:CreateDate")]
    public string? QuickTimeCreateDate { get; set; }

    [JsonPropertyName("QuickTime:ModifyDate")]
    public string? QuickTimeModifyDate { get; set; }

    [JsonPropertyName("ASF:CreationDate")]
    public string? ASFCreationDate { get; set; }

    [JsonPropertyName("RIFF:DateTimeOriginal")]
    public string? RIFFDateTimeOriginal { get; set; }

    [JsonPropertyName("EXIF:ModifyDate")]
    public string? EXIFModifyDate { get; set; }

    [JsonPropertyName("EXIF:DateTimeOriginal")]
    public string? EXIFDateTimeOriginal { get; set; }

    [JsonPropertyName("EXIF:CreateDate")]
    public string? EXIFCreateDate { get; set; }

    [JsonPropertyName("EXIF:DNGVersion")]
    public string? EXIFDNGVersion { get; set; }

    [JsonPropertyName("XMP:ModifyDate")]
    public string? XMPModifyDate { get; set; }

    [JsonPropertyName("XMP:CreateDate")]
    public string? XMPCreateDate { get; set; }

    [SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "Required for JSON deserialization"
    )]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    [JsonPropertyName("XMP:Subject")]
    public string[]? XMPSubject { get; set; }

    [JsonPropertyName("XMP:DateCreated")]
    public string? XMPDateCreated { get; set; }

    [JsonPropertyName("IPTC:DigitalCreationTime")]
    public string? IPTCDigitalCreationTime { get; set; }

    [JsonPropertyName("IPTC:DigitalCreationDate")]
    public string? IPTCDigitalCreationDate { get; set; }

    [JsonPropertyName("IPTC:DateCreated")]
    public string? IPTCDateCreated { get; set; }

    [JsonPropertyName("IPTC:TimeCreated")]
    public string? IPTCTimeCreated { get; set; }

    [JsonPropertyName("Composite:DateTimeCreated")]
    public string? CompositeDateTimeCreated { get; set; }

    [JsonPropertyName("Composite:DigitalCreationDateTime")]
    public string? CompositeDigitalCreationDateTime { get; set; }

    [JsonPropertyName("Matroska:DateTimeOriginal")]
    public string? MatroskaDateTimeOriginal { get; set; }

    [JsonPropertyName("QuickTime:ContentIdentifier")]
    public string? QuickTimeContentIdentifier { get; set; }

    [JsonPropertyName("MakerNotes:ContentIdentifier")]
    public string? MakerNotesContentIdentifier { get; set; }

    public string? ContentIdentifier => QuickTimeContentIdentifier ?? MakerNotesContentIdentifier;

    public string FileDetails =>
        $"type='{FileType}', extension='{FileTypeExtension}', MIME='{MIMEType}'";

    public bool IsDateSet() => GetDateString() is not null;

    private const string ExifDateFormat = "yyyy:MM:dd HH:mm:ss";

    public DateTime? GetDate()
    {
        string? dateStr = GetDateString();
        return
            !string.IsNullOrEmpty(dateStr)
            && DateTime.TryParseExact(
                dateStr,
                ExifDateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime date
            )
            ? date
            : null;
    }

    public string? GetDateString()
    {
        // EXIF:DateTimeOriginal
        if (!string.IsNullOrEmpty(EXIFDateTimeOriginal))
        {
            return EXIFDateTimeOriginal;
        }

        // EXIF:CreateDate
        if (!string.IsNullOrEmpty(EXIFCreateDate))
        {
            return EXIFCreateDate;
        }

        // XMP:CreateDate
        if (!string.IsNullOrEmpty(XMPCreateDate))
        {
            return XMPCreateDate;
        }

        // QuickTime:CreateDate
        if (
            !string.IsNullOrEmpty(QuickTimeCreateDate)
            && QuickTimeCreateDate != "0000:00:00 00:00:00"
        )
        {
            return QuickTimeCreateDate;
        }

        // H264:DateTimeOriginal
        if (!string.IsNullOrEmpty(H264DateTimeOriginal))
        {
            return H264DateTimeOriginal;
        }

        // ASF:CreationDate
        if (
            !string.IsNullOrEmpty(ASFCreationDate)
            && !ASFCreationDate.StartsWith("0000:", StringComparison.Ordinal)
        )
        {
            return ASFCreationDate;
        }

        // Matroska:DateTimeOriginal
        return !string.IsNullOrEmpty(RIFFDateTimeOriginal) ? RIFFDateTimeOriginal : null;
    }

    internal static bool IsDngVersionNewer(string? versionString)
    {
        if (string.IsNullOrEmpty(versionString))
        {
            return false;
        }

        ReadOnlySpan<char> span = versionString.AsSpan();
        int dotIndex = span.IndexOf('.');
        if (dotIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> afterMajor = span[(dotIndex + 1)..];
        int secondDot = afterMajor.IndexOf('.');
        ReadOnlySpan<char> minorSpan = secondDot >= 0 ? afterMajor[..secondDot] : afterMajor;

        return int.TryParse(
                span[..dotIndex],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int major
            )
            && int.TryParse(
                minorSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int minor
            )
            && (major > 1 || (major == 1 && minor > 4));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExifToolJson))]
internal partial class SourceGenerationContext : JsonSerializerContext;
