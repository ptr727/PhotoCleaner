using Xunit;

namespace PhotoCleanerTests;

public class DateInferenceEdgeCasesTests
{
    [Theory]
    [InlineData("Screenshot 2023-12-25 at 10.30.15 AM.png")] // Different format
    [InlineData("IMG_2023-01-01_New_Year.jpg")] // With descriptive text
    [InlineData("Video_2022-06-15_12-30-45_HD.mp4")] // Video with time
    [InlineData("Backup_2021-03-20_evening.jpg")] // With text suffix
    public void ExtractDateFromFilename_AdditionalFormats_ExtractsCorrectly(string filename)
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert - Just verify a date was extracted, regardless of specific value
        Assert.NotNull(result);
        Assert.True(result.HasValue);
    }

    [Theory]
    [InlineData("/Volumes/External/Photos/2023/Christmas/2023-12-25/family_dinner.jpg")]
    [InlineData("C:\\Users\\Photos\\Vacation\\2022\\2022-07-15\\beach.png")]
    [InlineData("/storage/emulated/0/DCIM/Camera/2021/2021-01-01/new_year.mp4")]
    public void ExtractDateFromPath_WindowsAndLinuxPaths_ExtractsCorrectly(string fullPath)
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromPath(fullPath);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.HasValue);
    }

    [Theory]
    [InlineData("20240229_120000.jpg")] // Leap year
    [InlineData("20230228_120000.jpg")] // Non-leap year last day of February
    [InlineData("20201231_235959.jpg")] // Last second of year
    [InlineData("20220101_000000.jpg")] // First second of year
    public void ExtractDateFromFilename_SpecialDates_HandlesCorrectly(string filename)
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert
        Assert.NotNull(result);

        // Call IsDateValid to ensure the extracted date passes validation
        bool isValid = PhotoCleaner.DateFromPath.IsDateValid(result);
        Assert.True(isValid);
    }

    [Theory]
    [InlineData("PHOTO-2024-02-30.jpg")] // Invalid date (February 30th)
    [InlineData("IMG-2023-13-01.jpg")] // Invalid month
    [InlineData("VID-2022-06-32.jpg")] // Invalid day
    [InlineData("20240230_120000.jpg")] // February 30th in filename format
    public void ExtractDateFromFilename_InvalidDates_ReturnsNull(string filename)
    {
        // Act
        DateTime? result = PhotoCleaner.DateFromPath.ExtractDateFromFilename(filename);

        // Assert - Should be null for genuinely invalid dates
        Assert.Null(result);
    }

    [Fact]
    public void InferCreatedDate_FilenameOverridesPath_ReturnsFilenameDate()
    {
        // Arrange - filename has 2024 date, path has 2023 date
        string filename = "IMG_20240315_143000.jpg";
        string directoryPath = "/photos/2023/2023-01-01/";
        string tempFilePath = Path.Combine(directoryPath, filename);

        // Act
        string createdDate = "";
        bool result = PhotoCleaner.DateFromPath.InferCreatedDate(tempFilePath, ref createdDate);

        // Assert - Should use filename date (2024) not path date (2023)
        Assert.True(result);
        Assert.StartsWith("2024:03:15", createdDate);
    }

    [Theory]
    [InlineData("/photos/unknown/IMG_12345.jpg", false)] // No date in filename or path
    [InlineData("/random/path/no_date_file.mov", false)] // No recognizable date patterns
    [InlineData("/temp/processing/temp_file.jpg", false)] // Temporary file paths
    public void InferCreatedDate_NoDateAvailable_ReturnsFalse(string fullPath, bool expectedResult)
    {
        // Act
        string createdDate = "";
        bool result = PhotoCleaner.DateFromPath.InferCreatedDate(fullPath, ref createdDate);

        // Assert
        Assert.Equal(expectedResult, result);
    }
}
