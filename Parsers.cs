using System.Globalization;
using System.Text.RegularExpressions;

namespace DataPreprocessingTask;

public static class Parsers
{
    private static readonly string[] Placeholders =
    {
        "-", "_", "", "n/a", "na", "null", "none", "nd", "no data"
    };

    public static bool IsPlaceholder(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;

        var t = s.Trim();
        foreach (var p in Placeholders)
        {
            if (string.Equals(t, p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool IsNoSamplingText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;

        var t = s.Trim().ToUpperInvariant();

        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");

        return t == "NO SAMPLING CONDUCTED"
            || t == "NO SAMPLING"
            || t == "NO SAMPLING DONE"
            || t == "NO SAMPLE"
            || t == "NO SAMPLE COLLECTED";
    }

    // numbers
    public static (bool ok, double value, bool hadLimit) TryParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, 0, false);

        var s = raw.Trim();
        if (IsPlaceholder(s)) return (false, 0, false);

        bool hadLimit = false;

        if (s.StartsWith("<") || s.StartsWith(">"))
        {
            hadLimit = true;
            s = s.Substring(1).Trim();
        }

        if (s.EndsWith("*"))
        {
            hadLimit = true;
            s = s.Substring(0, s.Length - 1).Trim();
        }

        if (s.Contains('*'))
        {
            hadLimit = true;
            s = s.Replace("*", "").Trim();
        }

        s = s.Replace(",", "");

        s = Regex.Replace(s, @"(\d)\.\s+(\d)", "$1.$2");

        if (double.TryParse(
                s,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var d))
        {
            return (true, d, hadLimit);
        }

        return (false, 0, hadLimit);
    }

    // dates
    public static (bool ok, DateTime value) TryParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, default);

        var s = raw.Trim();
        if (IsPlaceholder(s)) return (false, default);

        while (s.Contains("//"))
            s = s.Replace("//", "/");

        string[] formats =
        {
            "M/d/yyyy", "MM/dd/yyyy",
            "M/d/yy",   "MM/dd/yy",
            "yyyy-MM-dd",
            "yyyy/MM/dd",
            "d-MMM-yyyy", "MMM d, yyyy",
            "d/M/yyyy", "dd/MM/yyyy"
        };

        if (DateTime.TryParseExact(
                s,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dt))
        {
            return (true, dt);
        }

        if (DateTime.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out dt))
        {
            return (true, dt);
        }

        return (false, default);
    }

    // time
    public static (bool ok, TimeSpan value) TryParseTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, default);

        var s = raw.Trim();
        if (IsPlaceholder(s)) return (false, default);

        s = Regex.Replace(
                s,
                @"(\d)\s*(AM|PM|am|pm)\b",
                "$1 $2",
                RegexOptions.IgnoreCase);

        string[] formats =
        {
            "h:mm tt",  "hh:mm tt",
            "h:mm:ss tt", "hh:mm:ss tt",
            "H:mm",     "HH:mm",
            "H:mm:ss",  "HH:mm:ss"
        };

        if (DateTime.TryParseExact(
                s,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dt))
        {
            return (true, dt.TimeOfDay);
        }

        // Fallback: plain TimeSpan parse (e.g. "14:30:00").
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts))
        {
            return (true, ts);
        }

        return (false, default);
    }

    // month 
    private static readonly string[] MonthNames =
    {
        "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE",
        "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER"
    };

    public static (bool ok, int year, int month) TryParseMonthLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, 0, 0);

        var s = raw.Trim().ToUpperInvariant();

        if (s.StartsWith("CY"))
            s = s.Substring(2).Trim();

        var mYear = Regex.Match(s, @"\b(20\d{2})\b");
        if (!mYear.Success) return (false, 0, 0);

        int year = int.Parse(mYear.Groups[1].Value, CultureInfo.InvariantCulture);

        for (int i = 0; i < MonthNames.Length; i++)
        {
            if (s.Contains(MonthNames[i]))
                return (true, year, i + 1);
        }

        return (false, year, 0);
    }
}