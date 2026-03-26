namespace PhotoCleanerTests;

public sealed class DateInferenceTests
{
    [Theory]
    [InlineData("Foo_2021_05_02_200152957_iOS-1747.jpg", "2021:05:02 20:01:52")]
    [InlineData("Foo_2021_05_02_iOS-1747.jpg", "2021:05:02 00:00:00")]
    [InlineData("Foo_2021-05-02_200152957_iOS-1747.jpg", "2021:05:02 20:01:52")]
    [InlineData("Foo_2021-05-02_iOS-1747.jpg", "2021:05:02 00:00:00")]
    [InlineData("20210502_200152957_iOS-1747.jpg", "2021:05:02 20:01:52")]
    [InlineData("20030219_123456.jpg", "2003:02:19 12:34:56")]
    [InlineData("20090709_081500.mov", "2009:07:09 08:15:00")]
    [InlineData("20090709.mov", "2009:07:09 00:00:00")]
    [InlineData("20090709_0815.mov", "2009:07:09 00:00:00")] // 4 digits - no time parsing
    [InlineData("EX_20030219_3378.jpg", "2003:02:19 00:00:00")]
    [InlineData("PV_20090709_0081.mp4", "2009:07:09 00:00:00")]
    [InlineData("PHOTO-2024-06-22-07-56-41.jpg", "2024:06:22 07:56:41")]
    [InlineData("WhatsApp Image 2024-06-30.jpg", "2024:06:30 00:00:00")]
    [InlineData("EV 2014 07 03_0003.tif", "2014:07:03 00:00:00")]
    [InlineData("IMG_20240101_123456.jpg", "2024:01:01 12:34:56")]
    [InlineData("VID-20231225-WA0001.mp4", "2023:12:25 00:00:00")]
    [InlineData("Screenshot_2024-03-15-10-30-45.png", "2024:03:15 10:30:45")]
    [InlineData("2024_01_15_photo.jpg", "2024:01:15 00:00:00")]
    [InlineData("Screenshot 2023-12-25 at 10.30.15 AM.png", "2023:12:25 10:30:15")]
    [InlineData("WhatsApp Image 2018-09-22 at 7.48.17 PM-253.jpg", "2018:09:22 19:48:17")]
    [InlineData("WhatsApp Image 2024-06-30 at 12.00.00 AM.jpg", "2024:06:30 00:00:00")]
    [InlineData("WhatsApp Image 2024-06-30 at 12.30.00 PM.jpg", "2024:06:30 12:30:00")]
    [InlineData("IMG_2023-01-01_New_Year.jpg", "2023:01:01 00:00:00")]
    [InlineData("Video_2022-06-15_12-30-45_HD.mp4", "2022:06:15 00:00:00")] // _12 is only 2 digits
    [InlineData("Backup_2021-03-20_evening.jpg", "2021:03:20 00:00:00")]
    [InlineData("20240229_120000.jpg", "2024:02:29 12:00:00")] // leap year
    [InlineData("20230228_120000.jpg", "2023:02:28 12:00:00")]
    [InlineData("20201231_235959.jpg", "2020:12:31 23:59:59")]
    [InlineData("20220101_000000.jpg", "2022:01:01 00:00:00")]
    public void ExtractDateFromFilename_ValidDateFormats_ReturnsCorrectDate(
        string filename,
        string expectedDateString
    )
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert
        result.Should().NotBeNull();
        string actualDateString = result.Value.ToString(
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture
        );
        actualDateString.Should().Be(expectedDateString);
    }

    [Theory]
    [InlineData("random_file.jpg")]
    [InlineData("no_date_here.mov")]
    [InlineData("image_file.png")]
    [InlineData("invalid_20301301_120000.jpg")] // Invalid month 13
    [InlineData("bad_format_2023-13-01.jpg")] // Invalid month
    [InlineData("PHOTO-2024-02-30.jpg")] // February 30th
    [InlineData("IMG-2023-13-01.jpg")] // Month 13
    [InlineData("VID-2022-06-32.jpg")] // Day 32
    [InlineData("20240230_120000.jpg")] // February 30th compact
    public void ExtractDateFromFilename_InvalidOrNoDate_ReturnsNull(string filename)
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("20991231_120000.jpg", "2099:12:31 12:00:00")] // Future date - extracted but fails validation
    [InlineData("18991231_120000.jpg", "1899:12:31 12:00:00")] // Too old - extracted but fails validation
    public void ExtractDateFromFilename_OutOfRangeDate_StillReturnsDate(
        string filename,
        string expectedDateString
    )
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert
        result.Should().NotBeNull();
        string actualDateString = result.Value.ToString(
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture
        );
        actualDateString.Should().Be(expectedDateString);
    }

    [Theory]
    [InlineData("photos/2021/05/02/vacation.jpg", "2021:05:02 00:00:00")]
    [InlineData("photos/2021/2021-05-02/vacation.jpg", "2021:05:02 00:00:00")]
    [InlineData("photos/2021/20210502/vacation.jpg", "2021:05:02 00:00:00")]
    [InlineData("photos/2021/2021_05_02/vacation.jpg", "2021:05:02 00:00:00")]
    [InlineData("data/media/Pictures/Lumia/2015-11-18/image.jpg", "2015:11:18 00:00:00")]
    [InlineData("archive/MP Navigator EX/2014_07_14/scan.tif", "2014:07:14 00:00:00")]
    [InlineData("backup/photos/2020/file.jpg", "2020:01:01 00:00:00")] // Year-only fallback
    [InlineData("Photos/2024/01/15/image.jpg", "2024:01:15 00:00:00")]
    [InlineData("backup/2023/01/photos/file.jpg", "2023:01:01 00:00:00")] // Year-only fallback
    [InlineData("media/2022/vacation.mp4", "2022:01:01 00:00:00")] // Year-only fallback
    [InlineData(
        "Volumes/External/Photos/2023/Christmas/2023-12-25/dinner.jpg",
        "2023:12:25 00:00:00"
    )]
    [InlineData("Users/Photos/Vacation/2022/2022-07-15/beach.png", "2022:07:15 00:00:00")]
    [InlineData(
        "storage/emulated/0/DCIM/Camera/2021/2021-01-01/new_year.mp4",
        "2021:01:01 00:00:00"
    )]
    public void ExtractDateFromPath_ValidPathFormats_ReturnsCorrectDate(
        string fullPath,
        string expectedDateString
    )
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromPath(fullPath);

        // Assert
        result.Should().NotBeNull();
        string actualDateString = result.Value.ToString(
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture
        );
        actualDateString.Should().Be(expectedDateString);
    }

    [Theory]
    [InlineData("random/path/file.jpg")]
    [InlineData("no/date/in/path.mov")]
    [InlineData("photos/not_a_date/image.png")]
    public void ExtractDateFromPath_InvalidOrNoDate_ReturnsNull(string fullPath)
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromPath(fullPath);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(2024, true)]
    [InlineData(2000, true)]
    [InlineData(1950, true)]
    [InlineData(1900, true)]
    [InlineData(1899, false)] // Too old
    [InlineData(2099, false)] // Future date
    public void IsDateValid_WithVariousDates_ReturnsExpectedResult(int year, bool expectedValid)
    {
        // Arrange
        DateTime testDate = new(year, 6, 15, 12, 30, 45);

        // Act
        bool result = PhotoCleaner.DateFromPath.IsDateValid(testDate);

        // Assert
        result.Should().Be(expectedValid);
    }

    [Fact]
    public void IsDateValid_NullDate_ReturnsFalse()
    {
        // Act
        bool result = PhotoCleaner.DateFromPath.IsDateValid(null);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsDateValid_CurrentYear_ReturnsTrue()
    {
        // Arrange
        DateTime currentDate = DateTime.Now;

        // Act
        bool result = PhotoCleaner.DateFromPath.IsDateValid(currentDate);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsDateValid_FutureYear_ReturnsFalse()
    {
        // Arrange
        DateTime futureDate = new(DateTime.Now.Year + 2, 1, 1);

        // Act
        bool result = PhotoCleaner.DateFromPath.IsDateValid(futureDate);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("photos/vacation/IMG_20210502_200152.jpg", "2021:05:02 20:01:52")] // Filename wins
    [InlineData("photos/2020/2020-12-25/random_file.jpg", "2020:12:25 00:00:00")] // Path fallback
    [InlineData("photos/2023/2023-01-01/IMG_20240315_143000.jpg", "2024:03:15 14:30:00")] // Filename overrides path
    public void InferCreatedDate_DateFound_ReturnsExpectedDate(string fullPath, string expectedDate)
    {
        // Act
        string createdDate = string.Empty;
        bool result = PhotoCleaner.DateFromPath.InferCreatedDate(fullPath, ref createdDate);

        // Assert
        result.Should().BeTrue();
        createdDate.Should().Be(expectedDate);
    }

    [Theory]
    [InlineData("random/path/no_date.jpg")]
    [InlineData("photos/unknown/IMG_12345.jpg")]
    [InlineData("random/path/no_date_file.mov")]
    [InlineData("temp/processing/temp_file.jpg")]
    public void InferCreatedDate_NoDateAvailable_ReturnsFalse(string fullPath)
    {
        // Act
        string createdDate = string.Empty;
        bool result = PhotoCleaner.DateFromPath.InferCreatedDate(fullPath, ref createdDate);

        // Assert
        result.Should().BeFalse();
        createdDate.Should().BeEmpty();
    }
}
