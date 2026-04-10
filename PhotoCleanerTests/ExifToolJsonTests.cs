using System.Text.Json;
using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class ExifToolJsonTests
{
    // -- StringOrStringArrayConverter -----------------------------------------

    private static ExifToolJson Deserialize(string json) =>
        JsonSerializer.Deserialize(json, ExifToolJsonContext.Default.ExifToolJson)!;

    [Fact]
    public void XMPSubject_StringArray_DeserializesCorrectly()
    {
        ExifToolJson result = Deserialize("""{"XMP:Subject": ["tag1", "tag2"]}""");

        result.XMPSubject.Should().BeEquivalentTo(["tag1", "tag2"]);
    }

    [Fact]
    public void XMPSubject_BareString_DeserializesAsSingleElementArray()
    {
        ExifToolJson result = Deserialize("""{"XMP:Subject": "solo"}""");

        result.XMPSubject.Should().BeEquivalentTo(["solo"]);
    }

    [Fact]
    public void XMPSubject_BareNumber_DeserializesAsStringArray()
    {
        ExifToolJson result = Deserialize("""{"XMP:Subject": 2009}""");

        result.XMPSubject.Should().BeEquivalentTo(["2009"]);
    }

    [Fact]
    public void XMPSubject_MixedArray_DeserializesAllAsStrings()
    {
        ExifToolJson result = Deserialize("""{"XMP:Subject": ["tag1", 2009, "tag2"]}""");

        result.XMPSubject.Should().BeEquivalentTo(["tag1", "2009", "tag2"]);
    }

    [Fact]
    public void XMPSubject_Null_DeserializesAsNull()
    {
        ExifToolJson result = Deserialize("""{"XMP:Subject": null}""");

        result.XMPSubject.Should().BeNull();
    }

    [Fact]
    public void XMPSubject_Missing_DeserializesAsNull()
    {
        ExifToolJson result = Deserialize("""{}""");

        result.XMPSubject.Should().BeNull();
    }

    public static TheoryData<ExifToolJson> IsDateSet_ValidDates =>
        [
            new ExifToolJson { EXIFDateTimeOriginal = "2024:01:15 12:30:45" },
            new ExifToolJson { EXIFCreateDate = "2024:01:15 12:30:45" },
            new ExifToolJson { XMPCreateDate = "2024:01:15 12:30:45" },
            new ExifToolJson { QuickTimeCreateDate = "2024:01:15 12:30:45" },
            new ExifToolJson { H264DateTimeOriginal = "2024:01:15 12:30:45" },
            new ExifToolJson { ASFCreationDate = "2024:01:15 12:30:45" },
            new ExifToolJson { RIFFDateTimeOriginal = "2024:01:15 12:30:45" },
        ];

    public static TheoryData<ExifToolJson, string> GetDateString_SingleFieldData =>
        new()
        {
            {
                new ExifToolJson { EXIFDateTimeOriginal = "2024:01:15 12:30:45" },
                "2024:01:15 12:30:45"
            },
            {
                new ExifToolJson { EXIFCreateDate = "2024:01:16 13:45:00" },
                "2024:01:16 13:45:00"
            },
            {
                new ExifToolJson { XMPCreateDate = "2024:01:17 14:00:00" },
                "2024:01:17 14:00:00"
            },
            {
                new ExifToolJson { QuickTimeCreateDate = "2024:01:18 15:15:00" },
                "2024:01:18 15:15:00"
            },
            {
                new ExifToolJson { H264DateTimeOriginal = "2024:01:19 16:30:00" },
                "2024:01:19 16:30:00"
            },
            {
                new ExifToolJson { ASFCreationDate = "2024:01:20 17:45:00" },
                "2024:01:20 17:45:00"
            },
            {
                new ExifToolJson { RIFFDateTimeOriginal = "2024:01:21 18:00:00" },
                "2024:01:21 18:00:00"
            },
        };

    [Fact]
    public void IsDateSet_WhenAllDatesNull_ReturnsFalse()
    {
        // Arrange
        ExifToolJson json = new();

        // Act
        bool result = json.IsDateSet();

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(IsDateSet_ValidDates))]
    public void IsDateSet_WhenDateFieldSet_ReturnsTrue(ExifToolJson json)
    {
        // Act
        bool result = json.IsDateSet();

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("")]
    public void IsDateSet_WhenQuickTimeCreateDateIsInvalid_ReturnsFalse(string value)
    {
        // Arrange
        ExifToolJson json = new() { QuickTimeCreateDate = value };

        // Act
        bool result = json.IsDateSet();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsDateSet_WhenMultipleDatesSet_ReturnsTrue()
    {
        // Arrange
        ExifToolJson json = new()
        {
            EXIFDateTimeOriginal = "2024:01:15 12:30:45",
            QuickTimeCreateDate = "2024:01:16 13:45:00",
        };

        // Act
        bool result = json.IsDateSet();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void GetDateString_WhenAllDatesNull_ReturnsNull()
    {
        // Arrange
        ExifToolJson json = new();

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(GetDateString_SingleFieldData))]
    public void GetDateString_WhenSingleFieldSet_ReturnsThatDate(ExifToolJson json, string expected)
    {
        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GetDateString_WhenQuickTimeCreateDateIsZero_SkipsToNextDate()
    {
        // Arrange
        ExifToolJson json = new()
        {
            QuickTimeCreateDate = "0000:00:00 00:00:00",
            H264DateTimeOriginal = "2024:01:19 16:30:00",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be("2024:01:19 16:30:00");
    }

    [Fact]
    public void GetDateString_WhenQuickTimeCreateDateIsEmpty_SkipsToNextDate()
    {
        // Arrange
        ExifToolJson json = new()
        {
            QuickTimeCreateDate = "",
            H264DateTimeOriginal = "2024:01:19 16:30:00",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be("2024:01:19 16:30:00");
    }

    [Fact]
    public void GetDateString_PrioritizesEXIFDateTimeOriginalOverOthers()
    {
        // Arrange
        ExifToolJson json = new()
        {
            EXIFDateTimeOriginal = "2024:01:15 12:30:45",
            EXIFCreateDate = "2024:01:16 13:45:00",
            XMPCreateDate = "2024:01:17 14:00:00",
            QuickTimeCreateDate = "2024:01:18 15:15:00",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be("2024:01:15 12:30:45");
    }

    [Fact]
    public void GetDateString_PrioritizesEXIFCreateDateOverXMP()
    {
        // Arrange
        ExifToolJson json = new()
        {
            EXIFCreateDate = "2024:01:16 13:45:00",
            XMPCreateDate = "2024:01:17 14:00:00",
            QuickTimeCreateDate = "2024:01:18 15:15:00",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be("2024:01:16 13:45:00");
    }

    [Fact]
    public void GetDateString_PrioritizesXMPCreateDateOverQuickTime()
    {
        // Arrange
        ExifToolJson json = new()
        {
            XMPCreateDate = "2024:01:17 14:00:00",
            QuickTimeCreateDate = "2024:01:18 15:15:00",
            H264DateTimeOriginal = "2024:01:19 16:30:00",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be("2024:01:17 14:00:00");
    }

    [Fact]
    public void GetDateString_PrioritizesQuickTimeCreateDateOverH264()
    {
        // Arrange
        ExifToolJson json = new()
        {
            QuickTimeCreateDate = "2024:01:18 15:15:00",
            H264DateTimeOriginal = "2024:01:19 16:30:00",
            ASFCreationDate = "2024:01:20 17:45:00",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be("2024:01:18 15:15:00");
    }

    [Fact]
    public void GetDateString_PrioritizesH264DateTimeOriginalOverASF()
    {
        // Arrange
        ExifToolJson json = new()
        {
            H264DateTimeOriginal = "2024:01:19 16:30:00",
            ASFCreationDate = "2024:01:20 17:45:00",
            RIFFDateTimeOriginal = "2024:01:21 18:00:00",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be("2024:01:19 16:30:00");
    }

    [Fact]
    public void GetDateString_PrioritizesASFCreationDateOverRIFF()
    {
        // Arrange
        ExifToolJson json = new()
        {
            ASFCreationDate = "2024:01:20 17:45:00",
            RIFFDateTimeOriginal = "2024:01:21 18:00:00",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().Be("2024:01:20 17:45:00");
    }

    [Fact]
    public void GetDateString_WhenOnlyEmptyStrings_ReturnsNull()
    {
        // Arrange
        ExifToolJson json = new()
        {
            EXIFDateTimeOriginal = "",
            EXIFCreateDate = "",
            XMPCreateDate = "",
            QuickTimeCreateDate = "",
        };

        // Act
        string? result = json.GetDateString();

        // Assert
        result.Should().BeNull();
    }

    // -- GetDate --------------------------------------------------------------

    [Fact]
    public void GetDate_WhenValidDateSet_ReturnsParsedDateTime()
    {
        ExifToolJson json = new() { EXIFDateTimeOriginal = "2024:06:15 10:30:00" };

        DateTime? result = json.GetDate();

        result.Should().Be(new DateTime(2024, 6, 15, 10, 30, 0));
    }

    [Fact]
    public void GetDate_WhenNoDatesSet_ReturnsNull()
    {
        ExifToolJson json = new();

        DateTime? result = json.GetDate();

        result.Should().BeNull();
    }

    [Fact]
    public void GetDate_ReturnsNullForZeroQuickTimeDate()
    {
        ExifToolJson json = new() { QuickTimeCreateDate = "0000:00:00 00:00:00" };

        DateTime? result = json.GetDate();

        result.Should().BeNull();
    }

    // -- IsDngVersionNewer ----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsDngVersionNewer_WhenNullOrEmpty_ReturnsFalse(string? version)
    {
        // Act
        bool result = ExifToolJson.IsDngVersionNewer(version);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("0.9.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.3.0.0")]
    [InlineData("1.4.0.0")]
    [InlineData("1.4")]
    public void IsDngVersionNewer_WhenVersionAtOrBelow1_4_ReturnsFalse(string version)
    {
        // Act
        bool result = ExifToolJson.IsDngVersionNewer(version);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("1.5.0.0")]
    [InlineData("1.6.0.0")]
    [InlineData("2.0.0.0")]
    [InlineData("1.5")]
    [InlineData("2.0")]
    public void IsDngVersionNewer_WhenVersionAbove1_4_ReturnsTrue(string version)
    {
        // Act
        bool result = ExifToolJson.IsDngVersionNewer(version);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("notaversion")]
    [InlineData("1")]
    [InlineData("abc.def")]
    [InlineData(".")]
    [InlineData("1.")]
    public void IsDngVersionNewer_WhenMalformed_ReturnsFalse(string version)
    {
        // Act
        bool result = ExifToolJson.IsDngVersionNewer(version);

        // Assert
        result.Should().BeFalse();
    }
}
