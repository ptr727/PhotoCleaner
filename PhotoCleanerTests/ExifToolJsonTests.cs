using PhotoCleaner;

namespace PhotoCleanerTests;

public class ExifToolJsonTests
{
    [Fact]
    public void IsDateSet_WhenAllDatesNull_ReturnsFalse()
    {
        // Arrange
        ExifToolJson json = new();

        // Act
        bool result = json.IsDateSet();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsDateSet_WhenEXIFDateTimeOriginalSet_ReturnsTrue()
    {
        // Arrange
        ExifToolJson json = new() { EXIFDateTimeOriginal = "2024:01:15 12:30:45" };

        // Act
        bool result = json.IsDateSet();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDateSet_WhenEXIFCreateDateSet_ReturnsTrue()
    {
        // Arrange
        ExifToolJson json = new() { EXIFCreateDate = "2024:01:15 12:30:45" };

        // Act
        bool result = json.IsDateSet();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDateSet_WhenXMPCreateDateSet_ReturnsTrue()
    {
        // Arrange
        ExifToolJson json = new() { XMPCreateDate = "2024:01:15 12:30:45" };

        // Act
        bool result = json.IsDateSet();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDateSet_WhenQuickTimeCreateDateSet_ReturnsTrue()
    {
        // Arrange
        ExifToolJson json = new() { QuickTimeCreateDate = "2024:01:15 12:30:45" };

        // Act
        bool result = json.IsDateSet();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsDateSet_WhenQuickTimeCreateDateIsZero_ReturnsFalse()
    {
        // Arrange
        ExifToolJson json = new() { QuickTimeCreateDate = "0000:00:00 00:00:00" };

        // Act
        bool result = json.IsDateSet();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsDateSet_WhenQuickTimeCreateDateIsEmpty_ReturnsFalse()
    {
        // Arrange
        ExifToolJson json = new() { QuickTimeCreateDate = "" };

        // Act
        bool result = json.IsDateSet();

        // Assert
        Assert.False(result);
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
        Assert.True(result);
    }

    [Fact]
    public void IsDateSet_WhenOnlyH264DateTimeOriginalSet_ReturnsFalse()
    {
        // Arrange
        ExifToolJson json = new() { H264DateTimeOriginal = "2024:01:15 12:30:45" };

        // Act
        bool result = json.IsDateSet();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetDateString_WhenAllDatesNull_ReturnsNull()
    {
        // Arrange
        ExifToolJson json = new();

        // Act
        string? result = json.GetDateString();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetDateString_WhenEXIFDateTimeOriginalSet_ReturnsThatDate()
    {
        // Arrange
        ExifToolJson json = new() { EXIFDateTimeOriginal = "2024:01:15 12:30:45" };

        // Act
        string? result = json.GetDateString();

        // Assert
        Assert.Equal("2024:01:15 12:30:45", result);
    }

    [Fact]
    public void GetDateString_WhenEXIFCreateDateSet_ReturnsThatDate()
    {
        // Arrange
        ExifToolJson json = new() { EXIFCreateDate = "2024:01:16 13:45:00" };

        // Act
        string? result = json.GetDateString();

        // Assert
        Assert.Equal("2024:01:16 13:45:00", result);
    }

    [Fact]
    public void GetDateString_WhenXMPCreateDateSet_ReturnsThatDate()
    {
        // Arrange
        ExifToolJson json = new() { XMPCreateDate = "2024:01:17 14:00:00" };

        // Act
        string? result = json.GetDateString();

        // Assert
        Assert.Equal("2024:01:17 14:00:00", result);
    }

    [Fact]
    public void GetDateString_WhenQuickTimeCreateDateSet_ReturnsThatDate()
    {
        // Arrange
        ExifToolJson json = new() { QuickTimeCreateDate = "2024:01:18 15:15:00" };

        // Act
        string? result = json.GetDateString();

        // Assert
        Assert.Equal("2024:01:18 15:15:00", result);
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
        Assert.Equal("2024:01:19 16:30:00", result);
    }

    [Fact]
    public void GetDateString_WhenH264DateTimeOriginalSet_ReturnsThatDate()
    {
        // Arrange
        ExifToolJson json = new() { H264DateTimeOriginal = "2024:01:20 17:45:00" };

        // Act
        string? result = json.GetDateString();

        // Assert
        Assert.Equal("2024:01:20 17:45:00", result);
    }

    [Fact]
    public void GetDateString_WhenASFCreationDateSet_ReturnsThatDate()
    {
        // Arrange
        ExifToolJson json = new() { ASFCreationDate = "2024:01:21 18:00:00" };

        // Act
        string? result = json.GetDateString();

        // Assert
        Assert.Equal("2024:01:21 18:00:00", result);
    }

    [Fact]
    public void GetDateString_WhenRIFFDateTimeOriginalSet_ReturnsThatDate()
    {
        // Arrange
        ExifToolJson json = new() { RIFFDateTimeOriginal = "2024:01:22 19:15:00" };

        // Act
        string? result = json.GetDateString();

        // Assert
        Assert.Equal("2024:01:22 19:15:00", result);
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
        Assert.Equal("2024:01:15 12:30:45", result);
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
        Assert.Equal("2024:01:16 13:45:00", result);
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
        Assert.Equal("2024:01:17 14:00:00", result);
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
        Assert.Equal("2024:01:18 15:15:00", result);
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
        Assert.Equal("2024:01:19 16:30:00", result);
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
        Assert.Equal("2024:01:20 17:45:00", result);
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
        Assert.Null(result);
    }
}
