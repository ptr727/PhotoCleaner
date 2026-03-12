using System.Text.Json.Serialization;

namespace PhotoCleaner;

public class ExifToolJson
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

    public bool IsDateSet() =>
        // EXIF:DateTimeOriginal
        // EXIF:CreateDate
        // XMP:CreateDate
        // QuickTime:CreateDate
        !string.IsNullOrEmpty(EXIFDateTimeOriginal)
        || !string.IsNullOrEmpty(EXIFCreateDate)
        || !string.IsNullOrEmpty(XMPCreateDate)
        || (
            !string.IsNullOrEmpty(QuickTimeCreateDate)
            && QuickTimeCreateDate != "0000:00:00 00:00:00"
        );

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
