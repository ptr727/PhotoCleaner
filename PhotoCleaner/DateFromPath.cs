using System.Text.RegularExpressions;

namespace PhotoCleaner;

internal static partial class DateFromPath
{
    internal static bool InferCreatedDate(string fullPath, ref string createdDate)
    {
        // Try to extract date from filename
        string fileName = Path.GetFileName(fullPath);
        DateTime? dateFromPath = ExtractDateFromFilename(fileName);
        if (IsDateValid(dateFromPath))
        {
            createdDate = dateFromPath!.Value.ToString(
                "yyyy:MM:dd HH:mm:ss",
                CultureInfo.InvariantCulture
            );
            return true;
        }

        // Try to extract date from directory path
        dateFromPath = ExtractDateFromPath(fullPath);
        if (!IsDateValid(dateFromPath))
        {
            return false;
        }

        createdDate = dateFromPath!.Value.ToString(
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture
        );
        return true;
    }

    internal static bool IsDateValid(DateTime? date) =>
        date != null
        && date.HasValue
        && date.Value.Year >= 1900
        && date.Value.Year <= DateTime.Now.Year;

    internal static DateTime? ExtractDateFromFilename(string fileName)
    {
        // Pattern 1: YYYYMMDD format with optional _HHMMSS time
        // Handles: 20210502_200152957_iOS-1747.jpg, EX_20030219_3378.jpg, 20090709.mov
        Match compactMatch = FilenameCompactDateTimeRegex().Match(fileName);
        if (compactMatch.Success)
        {
            if (
                DateTime.TryParseExact(
                    compactMatch.Groups[1].Value,
                    "yyyyMMdd",
                    null,
                    DateTimeStyles.None,
                    out DateTime compactDate
                )
            )
            {
                if (compactMatch.Groups[2].Success)
                {
                    string timeStr = compactMatch.Groups[2].Value.PadRight(6, '0')[..6];
                    return DateTime.TryParseExact(
                        timeStr,
                        "HHmmss",
                        null,
                        DateTimeStyles.None,
                        out DateTime compactTime
                    )
                        ? compactDate.Date.Add(compactTime.TimeOfDay)
                        : compactDate;
                }

                return compactDate;
            }
        }

        // Pattern 2: YYYY-MM-DD format with optional _HHMMSS or -HH-MM-SS time
        // Handles: PHOTO-2024-06-22-07-56-41.jpg, Foo_2021-05-02_200152957_iOS.jpg, WhatsApp Image 2024-06-30.jpg
        Match hyphenMatch = FilenameHyphenDateRegex().Match(fileName);
        if (hyphenMatch.Success)
        {
            if (
                DateTime.TryParseExact(
                    $"{hyphenMatch.Groups[1].Value}-{hyphenMatch.Groups[2].Value}-{hyphenMatch.Groups[3].Value}",
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime hyphenDate
                )
            )
            {
                if (hyphenMatch.Groups[4].Success)
                {
                    // Compact time: _HHMMSS
                    string timeStr = hyphenMatch.Groups[4].Value.PadRight(6, '0')[..6];
                    return DateTime.TryParseExact(
                        timeStr,
                        "HHmmss",
                        null,
                        DateTimeStyles.None,
                        out DateTime hyphenCompactTime
                    )
                        ? hyphenDate.Date.Add(hyphenCompactTime.TimeOfDay)
                        : hyphenDate;
                }

                if (hyphenMatch.Groups[5].Success)
                {
                    // Hyphen-separated time: -HH-MM-SS
                    return
                        int.TryParse(hyphenMatch.Groups[5].Value, out int hours)
                        && int.TryParse(hyphenMatch.Groups[6].Value, out int minutes)
                        && int.TryParse(hyphenMatch.Groups[7].Value, out int seconds)
                        && hours <= 23
                        && minutes <= 59
                        && seconds <= 59
                        ? hyphenDate.Date.Add(new TimeSpan(hours, minutes, seconds))
                        : hyphenDate;
                }

                if (hyphenMatch.Groups[8].Success)
                {
                    // Dotted 12-hour time: " at H.MM.SS AM/PM"
                    if (
                        int.TryParse(hyphenMatch.Groups[8].Value, out int hours12)
                        && int.TryParse(hyphenMatch.Groups[9].Value, out int mins)
                        && int.TryParse(hyphenMatch.Groups[10].Value, out int secs)
                        && hours12 >= 1
                        && hours12 <= 12
                        && mins <= 59
                        && secs <= 59
                    )
                    {
                        bool isPm = hyphenMatch
                            .Groups[11]
                            .Value.Equals("PM", StringComparison.OrdinalIgnoreCase);
                        int hours24 =
                            hours12 == 12 ? (isPm ? 12 : 0) : (isPm ? hours12 + 12 : hours12);
                        return hyphenDate.Date.Add(new TimeSpan(hours24, mins, secs));
                    }

                    return hyphenDate;
                }

                return hyphenDate;
            }
        }

        // Pattern 3: YYYY_MM_DD format with optional _HHMMSS time
        // Handles: Foo_2021_05_02_200152957_iOS-1747.jpg, Photo_2021_05_02.jpg
        Match underscoreMatch = FilenameUnderscoreDateRegex().Match(fileName);
        if (underscoreMatch.Success)
        {
            if (
                DateTime.TryParseExact(
                    $"{underscoreMatch.Groups[1].Value}-{underscoreMatch.Groups[2].Value}-{underscoreMatch.Groups[3].Value}",
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime underscoreDate
                )
            )
            {
                if (underscoreMatch.Groups[4].Success)
                {
                    string timeStr = underscoreMatch.Groups[4].Value.PadRight(6, '0')[..6];
                    return DateTime.TryParseExact(
                        timeStr,
                        "HHmmss",
                        null,
                        DateTimeStyles.None,
                        out DateTime underscoreTime
                    )
                        ? underscoreDate.Date.Add(underscoreTime.TimeOfDay)
                        : underscoreDate;
                }

                return underscoreDate;
            }
        }

        // Pattern 4: YYYY MM DD format with spaces (e.g., EV 2014 07 03_0003.tif)
        Match spaceMatch = FilenameSpaceDateRegex().Match(fileName);
        return !spaceMatch.Success ? null
            : DateTime.TryParseExact(
                $"{spaceMatch.Groups[1].Value}-{spaceMatch.Groups[2].Value}-{spaceMatch.Groups[3].Value}",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime spaceDate
            )
                ? spaceDate
            : null;
    }

    internal static DateTime? ExtractDateFromPath(string fullPath)
    {
        // Extract YYYY-MM-DD format anywhere in the path (e.g., /Lumia/2015-11-18/, /2021/2021-05-02/)
        Match pathHyphenMatch = PathHyphenDateRegex().Match(fullPath);
        if (pathHyphenMatch.Success)
        {
            string dateString =
                $"{pathHyphenMatch.Groups[1].Value}-{pathHyphenMatch.Groups[2].Value}-{pathHyphenMatch.Groups[3].Value}";
            if (
                DateTime.TryParseExact(
                    dateString,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime pathHyphenDate
                )
            )
            {
                return pathHyphenDate;
            }
        }

        // Extract YYYY_MM_DD format anywhere in the path (e.g., /MP Navigator EX/2010_01_21/)
        Match pathUnderscoreMatch = PathUnderscoreDateRegex().Match(fullPath);
        if (pathUnderscoreMatch.Success)
        {
            string dateString =
                $"{pathUnderscoreMatch.Groups[1].Value}-{pathUnderscoreMatch.Groups[2].Value}-{pathUnderscoreMatch.Groups[3].Value}";
            if (
                DateTime.TryParseExact(
                    dateString,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime pathUnderscoreDate
                )
            )
            {
                return pathUnderscoreDate;
            }
        }

        // Extract YYYYMMDD format anywhere in the path (e.g., /photos/20210502/)
        Match pathCompactMatch = PathCompactDateRegex().Match(fullPath);
        if (pathCompactMatch.Success)
        {
            if (
                DateTime.TryParseExact(
                    pathCompactMatch.Groups[1].Value,
                    "yyyyMMdd",
                    null,
                    DateTimeStyles.None,
                    out DateTime pathCompactDate
                )
            )
            {
                return pathCompactDate;
            }
        }

        // Extract YYYY/MM/DD format anywhere in the path (e.g., /photos/2021/05/02/file.heic)
        Match pathYearMonthDayMatch = PathYearMonthDayRegex().Match(fullPath);
        if (pathYearMonthDayMatch.Success)
        {
            string dateString =
                $"{pathYearMonthDayMatch.Groups[1].Value}-{pathYearMonthDayMatch.Groups[2].Value}-{pathYearMonthDayMatch.Groups[3].Value}";
            if (
                DateTime.TryParseExact(
                    dateString,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime pathYearMonthDayDate
                )
            )
            {
                return pathYearMonthDayDate;
            }
        }

        // Fallback: Try to extract just year from path
        Match pathYearMatch = PathYearOnlyRegex().Match(fullPath);
        return !pathYearMatch.Success ? null
            : int.TryParse(pathYearMatch.Groups[1].Value, out int year)
            && year >= 1900
            && year <= DateTime.Now.Year
                ? new DateTime(year, 1, 1)
            : null;
    }

    // Filename: YYYYMMDD with optional _HHMMSS (6-9 digits)
    [GeneratedRegex(@"(\d{8})(?:_(\d{6,9}))?")]
    private static partial Regex FilenameCompactDateTimeRegex();

    // Filename: YYYY-MM-DD with optional _HHMMSS, -HH-MM-SS, or " at H.MM.SS AM/PM"
    [GeneratedRegex(
        @"(\d{4})-(\d{2})-(\d{2})(?:_(\d{6,9})|-(\d{2})-(\d{2})-(\d{2})|\s+at\s+(\d{1,2})\.(\d{2})\.(\d{2})\s*(AM|PM))?",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex FilenameHyphenDateRegex();

    // Filename: YYYY_MM_DD with optional _HHMMSS (6-9 digits)
    [GeneratedRegex(@"(\d{4})_(\d{2})_(\d{2})(?:_(\d{6,9}))?")]
    private static partial Regex FilenameUnderscoreDateRegex();

    // Filename: YYYY MM DD with whitespace separators
    [GeneratedRegex(@"(\d{4})\s+(\d{2})\s+(\d{2})")]
    private static partial Regex FilenameSpaceDateRegex();

    // Path: /YYYY-MM-DD/ segment
    [GeneratedRegex(@"[/\\](\d{4})-(\d{2})-(\d{2})[/\\]")]
    private static partial Regex PathHyphenDateRegex();

    // Path: /YYYY_MM_DD/ segment
    [GeneratedRegex(@"[/\\](\d{4})_(\d{2})_(\d{2})[/\\]")]
    private static partial Regex PathUnderscoreDateRegex();

    // Path: /YYYYMMDD/ segment
    [GeneratedRegex(@"[/\\](\d{8})[/\\]")]
    private static partial Regex PathCompactDateRegex();

    // Path: /YYYY/MM/DD/ segments
    [GeneratedRegex(@"[/\\](\d{4})[/\\](\d{2})[/\\](\d{2})[/\\]")]
    private static partial Regex PathYearMonthDayRegex();

    // Path: /YYYY/ segment (year-only fallback)
    [GeneratedRegex(@"[/\\](\d{4})[/\\]")]
    private static partial Regex PathYearOnlyRegex();
}
