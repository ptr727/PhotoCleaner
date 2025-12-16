using System.Text.RegularExpressions;

namespace PhotoCleaner;

internal static class DateFromPath
{
    internal static bool InferCreatedDate(string fullPath, ref string createdDate)
    {
        // Try to extract date from filename
        string fileName = Path.GetFileName(fullPath);
        DateTime? dateFromPath = ExtractDateFromFilename(fileName);
        if (IsDateValid(dateFromPath))
        {
            createdDate = dateFromPath!.Value.ToString("yyyy:MM:dd HH:mm:ss");
            return true;
        }

        // Try to extract date from directory path
        dateFromPath = ExtractDateFromPath(fullPath);
        if (IsDateValid(dateFromPath))
        {
            createdDate = dateFromPath!.Value.ToString("yyyy:MM:dd HH:mm:ss");
            return true;
        }

        return false;
    }

    internal static bool IsDateValid(DateTime? date)
    {
        return date != null
            && date.HasValue
            && date.Value.Year >= 1900
            && date.Value.Year <= DateTime.Now.Year;
    }

    internal static DateTime? ExtractDateFromFilename(string fileName)
    {
        // Pattern 1: YYYYMMDD_HHMMSS format (e.g., 20210502_200152957_iOS-1747.jpg)
        var pattern1 = new Regex(@"(\d{8})_(\d{6,9})");
        var match1 = pattern1.Match(fileName);
        if (match1.Success)
        {
            if (
                DateTime.TryParseExact(
                    match1.Groups[1].Value,
                    "yyyyMMdd",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime date1
                )
            )
            {
                string timeStr = match1.Groups[2].Value.PadRight(6, '0').Substring(0, 6); // Take first 6 digits for HHMMSS
                if (
                    DateTime.TryParseExact(
                        timeStr,
                        "HHmmss",
                        null,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime time1
                    )
                )
                {
                    return date1.Date.Add(time1.TimeOfDay);
                }
                return date1;
            }
        }

        // Pattern 2: YYYYMMDD format without time (e.g., EX_20030219_3378.jpg, PV_20090709_0081.mp4)
        var pattern2 = new Regex(@"(\d{8})");
        var match2 = pattern2.Match(fileName);
        if (match2.Success)
        {
            if (
                DateTime.TryParseExact(
                    match2.Groups[1].Value,
                    "yyyyMMdd",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime date2
                )
            )
            {
                return date2;
            }
        }

        // Pattern 3: YYYY-MM-DD format (e.g., PHOTO-2024-06-22-07-56-41, WhatsApp Image 2024-06-30)
        var pattern3 = new Regex(@"(\d{4})-(\d{2})-(\d{2})");
        var match3 = pattern3.Match(fileName);
        if (match3.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match3.Groups[1].Value}-{match3.Groups[2].Value}-{match3.Groups[3].Value}",
                    out DateTime date3
                )
            )
            {
                // Try to extract time if present (HH-MM-SS format)
                var timePattern = new Regex(
                    $@"{Regex.Escape(match3.Value)}-(\d{{2}})-(\d{{2}})-(\d{{2}})"
                );
                var timeMatch = timePattern.Match(fileName);
                if (timeMatch.Success)
                {
                    if (
                        int.TryParse(timeMatch.Groups[1].Value, out int hours)
                        && int.TryParse(timeMatch.Groups[2].Value, out int minutes)
                        && int.TryParse(timeMatch.Groups[3].Value, out int seconds)
                        && hours <= 23
                        && minutes <= 59
                        && seconds <= 59
                    )
                    {
                        return date3.Date.Add(new TimeSpan(hours, minutes, seconds));
                    }
                }
                return date3;
            }
        }

        // Pattern 4: YYYY MM DD format with spaces (e.g., EV 2014 07 03_0003.tif)
        var pattern4 = new Regex(@"(\d{4})\s+(\d{2})\s+(\d{2})");
        var match4 = pattern4.Match(fileName);
        if (match4.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match4.Groups[1].Value}-{match4.Groups[2].Value}-{match4.Groups[3].Value}",
                    out DateTime date4
                )
            )
            {
                return date4;
            }
        }

        return null;
    }

    internal static DateTime? ExtractDateFromPath(string fullPath)
    {
        // Extract date from directory structure (e.g., /2021/2021-05-02/)
        var pathPattern = new Regex(@"[/\\](\d{4})[/\\](\d{4})-(\d{2})-(\d{2})[/\\]");
        var pathMatch = pathPattern.Match(fullPath);
        if (pathMatch.Success)
        {
            string yearFromDir = pathMatch.Groups[1].Value;
            string fullDate =
                $"{pathMatch.Groups[2].Value}-{pathMatch.Groups[3].Value}-{pathMatch.Groups[4].Value}";

            if (DateTime.TryParse(fullDate, out DateTime pathDate))
            {
                return pathDate;
            }
        }

        // Extract YYYY-MM-DD format anywhere in the path (e.g., /Lumia/2015-11-18/)
        var dateAnywherePattern = new Regex(@"[/\\](\d{4})-(\d{2})-(\d{2})[/\\]");
        var dateAnywhereMatch = dateAnywherePattern.Match(fullPath);
        if (dateAnywhereMatch.Success)
        {
            string dateString =
                $"{dateAnywhereMatch.Groups[1].Value}-{dateAnywhereMatch.Groups[2].Value}-{dateAnywhereMatch.Groups[3].Value}";
            if (DateTime.TryParse(dateString, out DateTime dateAnywhere))
            {
                return dateAnywhere;
            }
        }

        // Extract YYYY_MM_DD format anywhere in the path (e.g., /MP Navigator EX/2010_01_21/)
        var dateUnderscorePattern = new Regex(@"[/\\](\d{4})_(\d{2})_(\d{2})[/\\]");
        var dateUnderscoreMatch = dateUnderscorePattern.Match(fullPath);
        if (dateUnderscoreMatch.Success)
        {
            string dateString =
                $"{dateUnderscoreMatch.Groups[1].Value}-{dateUnderscoreMatch.Groups[2].Value}-{dateUnderscoreMatch.Groups[3].Value}";
            if (DateTime.TryParse(dateString, out DateTime dateUnderscore))
            {
                return dateUnderscore;
            }
        }

        // Fallback: Try to extract just year from path
        var yearPattern = new Regex(@"[/\\](\d{4})[/\\]");
        var yearMatch = yearPattern.Match(fullPath);
        if (yearMatch.Success)
        {
            if (
                int.TryParse(yearMatch.Groups[1].Value, out int year)
                && year >= 1900
                && year <= DateTime.Now.Year
            )
            {
                return new DateTime(year, 1, 1);
            }
        }

        return null;
    }
}
