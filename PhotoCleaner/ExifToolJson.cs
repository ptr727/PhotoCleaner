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

    public bool IsDateSet()
    {
        // EXIF:DateTimeOriginal
        // EXIF:CreateDate
        // QuickTime:CreateDate
        return !string.IsNullOrEmpty(EXIFDateTimeOriginal)
            || !string.IsNullOrEmpty(EXIFCreateDate)
            || (
                !string.IsNullOrEmpty(QuickTimeCreateDate)
                && QuickTimeCreateDate != "0000:00:00 00:00:00"
            );
    }

    public string? GetDateString()
    {
        if (!string.IsNullOrEmpty(EXIFDateTimeOriginal))
        {
            return EXIFDateTimeOriginal;
        }

        if (!string.IsNullOrEmpty(EXIFCreateDate))
        {
            return EXIFCreateDate;
        }

        if (
            !string.IsNullOrEmpty(QuickTimeCreateDate)
            && QuickTimeCreateDate != "0000:00:00 00:00:00"
        )
        {
            return QuickTimeCreateDate;
        }

        if (!string.IsNullOrEmpty(H264DateTimeOriginal))
        {
            return H264DateTimeOriginal;
        }

        if (!string.IsNullOrEmpty(ASFCreationDate))
        {
            return ASFCreationDate;
        }

        return !string.IsNullOrEmpty(RIFFDateTimeOriginal) ? RIFFDateTimeOriginal : null;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExifToolJson))]
internal partial class SourceGenerationContext : JsonSerializerContext;
