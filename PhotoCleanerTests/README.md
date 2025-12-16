# PhotoCleaner Tests

This test project contains comprehensive unit tests for the PhotoCleaner application, specifically focusing on date inference functionality.

## Test Coverage

### DateInferenceTests.cs

Tests for the core date inference methods in `ProcessTask.cs`:

- **ExtractDateFromFilename_ValidDateFormats_ReturnsCorrectDate**: Tests various filename patterns:
  - `YYYYMMDD_HHMMSS` format (e.g., `20210502_200152957_iOS-1747.jpg`)
  - `YYYYMMDD` format (e.g., `EX_20030219_3378.jpg`)
  - `YYYY-MM-DD-HH-MM-SS` format (e.g., `PHOTO-2024-06-22-07-56-41.jpg`)
  - `YYYY MM DD` format with spaces (e.g., `EV 2014 07 03_0003.tif`)

- **ExtractDateFromFilename_InvalidOrNoDate_ReturnsNull**: Tests files with no extractable dates
- **ExtractDateFromFilename_ExtractsDateEvenIfInvalid_ReturnsDate**: Tests that dates are extracted even if they fail validation
- **ExtractDateFromPath_ValidPathFormats_ReturnsCorrectDate**: Tests date extraction from directory paths:
  - `/YYYY/YYYY-MM-DD/` format
  - `/YYYY-MM-DD/` anywhere in path
  - `/YYYY_MM_DD/` format
  - Year-only fallback

- **IsDateValid_VariousDates_ReturnsExpectedValidation**: Tests date validation logic
- **InferCreatedDate_Integration_ReturnsExpectedResult**: Integration tests combining filename and path logic

### DateInferenceEdgeCasesTests.cs

Additional edge case and comprehensive testing:

- **ExtractDateFromFilename_AdditionalFormats_ExtractsCorrectly**: Tests additional filename patterns
- **ExtractDateFromPath_WindowsAndLinuxPaths_ExtractsCorrectly**: Cross-platform path testing
- **ExtractDateFromFilename_SpecialDates_HandlesCorrectly**: Leap years, year boundaries
- **ExtractDateFromFilename_InvalidDates_ReturnsNull**: Invalid calendar dates
- **InferCreatedDate_FilenameOverridesPath_ReturnsFilenameDate**: Priority testing
- **InferCreatedDate_NoDateAvailable_ReturnsFalse**: Fallback scenarios

## Running Tests

```bash
# Run all tests
dotnet test PhotoCleanerTests/PhotoCleanerTests.csproj

# Run with detailed output
dotnet test PhotoCleanerTests/PhotoCleanerTests.csproj --verbosity normal

# Run specific test class
dotnet test PhotoCleanerTests/PhotoCleanerTests.csproj --filter "FullyQualifiedName~DateInferenceTests"
```

## Test Architecture

The tests directly call internal static methods from the `DateFromPath` class:

- `DateFromPath.ExtractDateFromFilename(string fileName)` - Static method for filename date extraction
- `DateFromPath.ExtractDateFromPath(string fullPath)` - Static method for path date extraction
- `DateFromPath.IsDateValid(DateTime? date)` - Static method for date validation
- `DateFromPath.InferCreatedDate(string fullPath, ref string createdDate)` - Static method for combined date inference

- **xUnit**: Primary testing framework
- **Reflection**: Used to access private methods for unit testing
- **.NET 10**: Target framework matching the main application

## Notes

- Tests directly call internal static methods using `InternalsVisibleTo` attribute
- No reflection is used - all method calls are compile-time safe
- Date validation follows the application's rules (years 1900-current year)
- Integration tests verify the priority order (filename dates override path dates)
- Edge case tests ensure robust handling of invalid inputs and special scenarios
- The `DateFromPath` class contains all date inference logic as internal static methods
