using Xunit;

namespace PhotoCleanerTests;

public class DateInferenceTests
{
    [Theory]
    [InlineData("Foo_2021_05_02_200152957_iOS-1747.jpg", "2021:05:02 20:01:52")]
    [InlineData("Foo_2021_05_02_iOS-1747.jpg", "2021:05:02 00:00:00")]
    [InlineData("Foo_2021-05-02_200152957_iOS-1747.jpg", "2021:05:02 20:01:52")]
    [InlineData("Foo_2021-05-02_iOS-1747.jpg", "2021:05:02 00:00:00")]
    [InlineData("20210502_200152957_iOS-1747.jpg", "2021:05:02 20:01:52")]
    [InlineData("20030219_123456.jpg", "2003:02:19 12:34:56")]
    [InlineData("20090709_081500.mov", "2009:07:09 08:15:00")] // 6 digits needed for time
    [InlineData("20090709.mov", "2009:07:09 00:00:00")]
    [InlineData("20090709_0815.mov", "2009:07:09 00:00:00")] // 4 digits - no time parsing
    [InlineData("EX_20030219_3378.jpg", "2003:02:19 00:00:00")]
    [InlineData("PV_20090709_0081.mp4", "2009:07:09 00:00:00")]
    [InlineData("PHOTO-2024-06-22-07-56-41.jpg", "2024:06:22 07:56:41")]
    [InlineData("WhatsApp Image 2024-06-30.jpg", "2024:06:30 00:00:00")]
    [InlineData("EV 2014 07 03_0003.tif", "2014:07:03 00:00:00")]
    public void ExtractDateFromFilename_ValidDateFormats_ReturnsCorrectDate(
        string filename,
        string expectedDateString
    )
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert
        Assert.NotNull(result);
        string actualDateString = result!.Value.ToString("yyyy:MM:dd HH:mm:ss");
        Assert.Equal(expectedDateString, actualDateString);
    }

    [Theory]
    [InlineData("random_file.jpg")]
    [InlineData("no_date_here.mov")]
    [InlineData("image_file.png")]
    [InlineData("invalid_20301301_120000.jpg")] // Invalid date (month 13)
    [InlineData("bad_format_2023-13-01.jpg")] // Invalid month
    public void ExtractDateFromFilename_InvalidOrNoDate_ReturnsNull(string filename)
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("/photos/2021/2021-05-02/vacation.jpg", "2021:05:02 00:00:00")]
    [InlineData("/photos/2021/20210502/vacation.jpg", "2021:05:02 00:00:00")]
    [InlineData("/photos/2021/2021_05_02/vacation.jpg", "2021:05:02 00:00:00")]
    [InlineData("/data/media/Pictures/Lumia/2015-11-18/image.jpg", "2015:11:18 00:00:00")]
    [InlineData("/archive/MP Navigator EX/2014_07_14/scan.tif", "2014:07:14 00:00:00")]
    [InlineData("/backup/photos/2020/file.jpg", "2020:01:01 00:00:00")] // Year only fallback
    public void ExtractDateFromPath_ValidPathFormats_ReturnsCorrectDate(
        string fullPath,
        string expectedDateString
    )
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromPath(fullPath);

        // Assert
        Assert.NotNull(result);
        string actualDateString = result!.Value.ToString("yyyy:MM:dd HH:mm:ss");
        Assert.Equal(expectedDateString, actualDateString);
    }

    [Theory]
    [InlineData("/random/path/file.jpg")]
    [InlineData("/no/date/in/path.mov")]
    [InlineData("/photos/not_a_date/image.png")]
    public void ExtractDateFromPath_InvalidOrNoDate_ReturnsNull(string fullPath)
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromPath(fullPath);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("20991231_120000.jpg", "2099:12:31 12:00:00")] // Future date - extracted but will fail validation
    [InlineData("18991231_120000.jpg", "1899:12:31 12:00:00")] // Too old date - extracted but will fail validation
    public void ExtractDateFromFilename_ExtractsDateEvenIfInvalid_ReturnsDate(
        string filename,
        string expectedDateString
    )
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert
        Assert.NotNull(result);
        string actualDateString = result!.Value.ToString("yyyy:MM:dd HH:mm:ss");
        Assert.Equal(expectedDateString, actualDateString);
    }

    [Theory]
    [InlineData(2024, true)]
    [InlineData(2000, true)]
    [InlineData(1950, true)]
    [InlineData(1900, true)]
    [InlineData(1899, false)] // Too old
    [InlineData(2099, false)] // Future date
    public void IsDateValid_VariousDates_ReturnsExpectedValidation(int year, bool expectedValid)
    {
        // Arrange
        DateTime testDate = new DateTime(year, 6, 15, 12, 30, 45);

        // Act
        bool result = PhotoCleaner.DateFromPath.IsDateValid(testDate);

        // Assert
        Assert.Equal(expectedValid, result);
    }

    [Fact]
    public void IsDateValid_NullDate_ReturnsFalse()
    {
        // Act
        bool result = PhotoCleaner.DateFromPath.IsDateValid(null);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("IMG_20210502_200152.jpg", "/photos/vacation/", "2021:05:02 20:01:52")] // Filename takes priority
    [InlineData("random_file.jpg", "/photos/2020/2020-12-25/", "2020:12:25 00:00:00")] // Path fallback
    [InlineData("no_date.jpg", "/random/path/", null)] // No date available
    public void InferCreatedDate_Integration_ReturnsExpectedResult(
        string filename,
        string directoryPath,
        string? expectedDateString
    )
    {
        // Arrange - Create a temporary file path
        string tempFilePath = Path.Combine(directoryPath, filename);

        // Act
        string createdDate = "";
        bool result = PhotoCleaner.DateFromPath.InferCreatedDate(tempFilePath, ref createdDate);

        // Assert
        if (expectedDateString != null)
        {
            Assert.True(result);
            Assert.Equal(expectedDateString, createdDate);
        }
        else
        {
            Assert.False(result);
        }
    }
}
