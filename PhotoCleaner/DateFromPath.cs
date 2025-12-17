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
        if (!IsDateValid(dateFromPath))
        {
            return false;
        }

        createdDate = dateFromPath!.Value.ToString("yyyy:MM:dd HH:mm:ss");
        return true;
    }

    internal static bool IsDateValid(DateTime? date) =>
        date != null
        && date.HasValue
        && date.Value.Year >= 1900
        && date.Value.Year <= DateTime.Now.Year;

    internal static DateTime? ExtractDateFromFilename(string fileName)
    {
        // Pattern 1: YYYYMMDD_HHMMSS format (e.g., 20210502_200152957_iOS-1747.jpg)
        Regex pattern1 = new(@"(\d{8})_(\d{6,9})");
        Match match1 = pattern1.Match(fileName);
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
                string timeStr = match1.Groups[2].Value.PadRight(6, '0')[..6]; // Take first 6 digits for HHMMSS
                return DateTime.TryParseExact(
                    timeStr,
                    "HHmmss",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime time1
                )
                    ? date1.Date.Add(time1.TimeOfDay)
                    : date1;
            }
        }

        // Pattern 2: YYYYMMDD format without time (e.g., EX_20030219_3378.jpg, PV_20090709_0081.mp4)
        Regex pattern2 = new(@"(\d{8})");
        Match match2 = pattern2.Match(fileName);
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

        // Pattern 3: YYYY-MM-DD with underscore time separator (e.g., Foo_2021-05-02_200152957_iOS.jpg)
        Regex pattern3 = new(@"(\d{4})-(\d{2})-(\d{2})_(\d{6,9})");
        Match match3 = pattern3.Match(fileName);
        if (match3.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match3.Groups[1].Value}-{match3.Groups[2].Value}-{match3.Groups[3].Value}",
                    out DateTime date3
                )
            )
            {
                string timeStr = match3.Groups[4].Value.PadRight(6, '0')[..6]; // Take first 6 digits for HHMMSS
                return DateTime.TryParseExact(
                    timeStr,
                    "HHmmss",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime time3
                )
                    ? date3.Date.Add(time3.TimeOfDay)
                    : date3;
            }
        }

        // Pattern 3b: YYYY-MM-DD format with HH-MM-SS time (e.g., PHOTO-2024-06-22-07-56-41)
        Regex pattern3b = new(@"(\d{4})-(\d{2})-(\d{2})-(\d{2})-(\d{2})-(\d{2})");
        Match match3b = pattern3b.Match(fileName);
        if (match3b.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match3b.Groups[1].Value}-{match3b.Groups[2].Value}-{match3b.Groups[3].Value}",
                    out DateTime date3b
                )
            )
            {
                if (
                    int.TryParse(match3b.Groups[4].Value, out int hours)
                    && int.TryParse(match3b.Groups[5].Value, out int minutes)
                    && int.TryParse(match3b.Groups[6].Value, out int seconds)
                    && hours <= 23
                    && minutes <= 59
                    && seconds <= 59
                )
                {
                    return date3b.Date.Add(new TimeSpan(hours, minutes, seconds));
                }
                return date3b;
            }
        }

        // Pattern 3c: YYYY-MM-DD format without time (e.g., WhatsApp Image 2024-06-30)
        Regex pattern3c = new(@"(\d{4})-(\d{2})-(\d{2})");
        Match match3c = pattern3c.Match(fileName);
        if (match3c.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match3c.Groups[1].Value}-{match3c.Groups[2].Value}-{match3c.Groups[3].Value}",
                    out DateTime date3c
                )
            )
            {
                return date3c;
            }
        }

        // Pattern 4: YYYY_MM_DD format with optional time (e.g., Foo_2021_05_02_200152957_iOS-1747.jpg)
        Regex pattern4 = new(@"(\d{4})_(\d{2})_(\d{2})_(\d{6,9})");
        Match match4 = pattern4.Match(fileName);
        if (match4.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match4.Groups[1].Value}-{match4.Groups[2].Value}-{match4.Groups[3].Value}",
                    out DateTime date4
                )
            )
            {
                string timeStr = match4.Groups[4].Value.PadRight(6, '0')[..6]; // Take first 6 digits for HHMMSS
                return DateTime.TryParseExact(
                    timeStr,
                    "HHmmss",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime time4
                )
                    ? date4.Date.Add(time4.TimeOfDay)
                    : date4;
            }
        }

        // Pattern 5: YYYY_MM_DD format without time (e.g., Photo_2021_05_02.jpg)
        Regex pattern5 = new(@"(\d{4})_(\d{2})_(\d{2})");
        Match match5 = pattern5.Match(fileName);
        if (match5.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match5.Groups[1].Value}-{match5.Groups[2].Value}-{match5.Groups[3].Value}",
                    out DateTime date5
                )
            )
            {
                return date5;
            }
        }

        // Pattern 6: YYYY MM DD format with spaces (e.g., EV 2014 07 03_0003.tif)
        Regex pattern6 = new(@"(\d{4})\s+(\d{2})\s+(\d{2})");
        Match match6 = pattern6.Match(fileName);
        if (!match6.Success)
        {
            return null;
        }

        if (
            DateTime.TryParse(
                $"{match6.Groups[1].Value}-{match6.Groups[2].Value}-{match6.Groups[3].Value}",
                out DateTime date6
            )
        )
        {
            return date6;
        }

        return null;
    }

    internal static DateTime? ExtractDateFromPath(string fullPath)
    {
        // Extract date from directory structure (e.g., /2021/2021-05-02/)
        Regex pathPattern = new(@"[/\\](\d{4})[/\\](\d{4})-(\d{2})-(\d{2})[/\\]");
        Match pathMatch = pathPattern.Match(fullPath);
        if (pathMatch.Success)
        {
            string fullDate =
                $"{pathMatch.Groups[2].Value}-{pathMatch.Groups[3].Value}-{pathMatch.Groups[4].Value}";

            if (DateTime.TryParse(fullDate, out DateTime pathDate))
            {
                return pathDate;
            }
        }

        // Extract YYYY-MM-DD format anywhere in the path (e.g., /Lumia/2015-11-18/)
        Regex dateAnywherePattern = new(@"[/\\](\d{4})-(\d{2})-(\d{2})[/\\]");
        Match dateAnywhereMatch = dateAnywherePattern.Match(fullPath);
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
        Regex dateUnderscorePattern = new(@"[/\\](\d{4})_(\d{2})_(\d{2})[/\\]");
        Match dateUnderscoreMatch = dateUnderscorePattern.Match(fullPath);
        if (dateUnderscoreMatch.Success)
        {
            string dateString =
                $"{dateUnderscoreMatch.Groups[1].Value}-{dateUnderscoreMatch.Groups[2].Value}-{dateUnderscoreMatch.Groups[3].Value}";
            if (DateTime.TryParse(dateString, out DateTime dateUnderscore))
            {
                return dateUnderscore;
            }
        }

        // Extract YYYYMMDD format anywhere in the path (e.g., /photos/20210502/)
        Regex dateCompactPattern = new(@"[/\\](\d{8})[/\\]");
        Match dateCompactMatch = dateCompactPattern.Match(fullPath);
        if (dateCompactMatch.Success)
        {
            if (
                DateTime.TryParseExact(
                    dateCompactMatch.Groups[1].Value,
                    "yyyyMMdd",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime dateCompact
                )
            )
            {
                return dateCompact;
            }
        }

        // Fallback: Try to extract just year from path
        Regex yearPattern = new(@"[/\\](\d{4})[/\\]");
        Match yearMatch = yearPattern.Match(fullPath);
        if (!yearMatch.Success)
        {
            return null;
        }

        if (
            int.TryParse(yearMatch.Groups[1].Value, out int year)
            && year >= 1900
            && year <= DateTime.Now.Year
        )
        {
            return new DateTime(year, 1, 1);
        }

        return null;
    }
}
