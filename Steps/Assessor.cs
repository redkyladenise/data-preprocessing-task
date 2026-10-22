using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Assessor
{
    public const string InputPath = Extractor.OutputPath;
    public const string OutputDir = "Output/tables/assessment";

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("====== 03: Assessor (Quality Assessment, BEFORE) ======");

        if (!File.Exists(InputPath))
        {
            Console.WriteLine($"Input not found: {InputPath}. Run Step 2 first.");
            return;
        }

        var records = LoadRecords(InputPath);
        Console.WriteLine($"  Loaded {records.Count} raw records.");

        AssessRowCounts(records);
        AssessMissingValues(records);
        AssessNonNumericValues(records);
        AssessDateTimeIssues(records);
        AssessEmptyOrNoSampling(records);
        AssessDuplicates(records);
        AssessMissingDateTimeWithValues(records);

        Console.WriteLine();
        Console.WriteLine($"=== 03 Complete. Wrote {OutputDir}/");
    }

    private static List<Dictionary<string, string>> LoadRecords(string path)
    {
        var records = new List<Dictionary<string, string>>();

        var content = File.ReadAllText(path).TrimStart('\uFEFF');
        using var reader = new StringReader(content);

        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();

        while (csv.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
                row[h] = csv.GetField(h) ?? "";
            records.Add(row);
        }
        return records;
    }
    private static void AssessRowCounts(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Row and month-block counts ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var g in records.GroupBy(r => r["SourceSheet"]).OrderBy(g => g.Key))
        {
            var monthBlocks = g.Select(r => r["YearMonthLabel"])
                               .Where(s => !string.IsNullOrWhiteSpace(s))
                               .Distinct()
                               .Count();

            Console.WriteLine($"  {g.Key,-12}: {g.Count(),4} rows, {monthBlocks,4} month blocks");

            rows.Add(new Dictionary<string, string>
            {
                ["Sheet"]       = g.Key,
                ["RowCount"]    = g.Count().ToString(CultureInfo.InvariantCulture),
                ["MonthBlocks"] = monthBlocks.ToString(CultureInfo.InvariantCulture),
            });
        }

        TableWriter.WriteRows($"{OutputDir}/01_rows_and_month_blocks.csv", rows);
    }

    private static void AssessMissingValues(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Missing values per parameter per sheet ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]).OrderBy(g => g.Key))
        {
            var sheet = sheetGroup.Key;
            var list  = sheetGroup.ToList();
            int total = list.Count;

            foreach (var param in Schema.StandardParameterNames)
            {
                int missing = list.Count(r =>
                    Parsers.IsPlaceholder(r.TryGetValue(param, out var v) ? v : ""));

                double pct = total > 0 ? missing * 100.0 / total : 0;

                Console.WriteLine(
                    $"  {sheet,-12} | {param,-40} | {missing,4}/{total,4} ({pct,5:F1}%)");

                rows.Add(new Dictionary<string, string>
                {
                    ["Sheet"]          = sheet,
                    ["Parameter"]      = param,
                    ["MissingCount"]   = missing.ToString(CultureInfo.InvariantCulture),
                    ["TotalCount"]     = total.ToString(CultureInfo.InvariantCulture),
                    ["MissingPercent"] = pct.ToString("F2", CultureInfo.InvariantCulture),
                });
            }
        }

        TableWriter.WriteRows($"{OutputDir}/02_missing_values_by_parameter.csv", rows);
    }

    private static void AssessMissingDateTimeWithValues(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Rows with measurements but no Date ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var rec in records)
        {
            var d = rec.TryGetValue("DateText", out var dv) ? dv : "";
            var t = rec.TryGetValue("TimeText", out var tv) ? tv : "";

            bool hasDate =
                !string.IsNullOrWhiteSpace(d) &&
                !Parsers.IsPlaceholder(d) &&
                !Parsers.IsNoSamplingText(d);

            if (hasDate) continue;

            var presentParams = new List<string>();
            foreach (var param in Schema.StandardParameterNames)
            {
                var v = rec.TryGetValue(param, out var raw) ? raw : "";
                if (!Parsers.IsPlaceholder(v) && !Parsers.IsNoSamplingText(v))
                    presentParams.Add(param);
            }

            if (presentParams.Count == 0) continue;

            rows.Add(new Dictionary<string, string>
            {
                ["Sheet"]          = rec["SourceSheet"],
                ["Row"]            = rec["SourceRow"],
                ["Label"]          = rec["YearMonthLabel"],
                ["DateText"]       = d,
                ["TimeText"]       = t,
                ["NonEmptyParams"] = presentParams.Count.ToString(CultureInfo.InvariantCulture),
                ["SampleParams"]   = string.Join("; ", presentParams),
            });
        }

        Console.WriteLine($"  Rows with values but no date: {rows.Count}");

        TableWriter.WriteRows($"{OutputDir}/03_missing_datetime_with_values.csv", rows);
    }

    private static void AssessNonNumericValues(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Non-numeric values in parameter columns ---");

        var counts = new Dictionary<string, (string param, string value, string type, int count)>();

        foreach (var rec in records)
        {
            foreach (var param in Schema.StandardParameterNames)
            {
                var raw = rec.TryGetValue(param, out var v) ? v : "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var type = ClassifyNonNumeric(raw);
                if (type == "NUMERIC") continue;

                var key = param + "\u0001" + raw;
                if (counts.TryGetValue(key, out var existing))
                    counts[key] = (existing.param, existing.value, existing.type, existing.count + 1);
                else
                    counts[key] = (param, raw, type, 1);
            }
        }

        var rows = new List<IDictionary<string, string>>();

        foreach (var kv in counts.OrderByDescending(k => k.Value.count)
                                 .ThenBy(k => k.Value.type))
        {
            var (param, value, type, count) = kv.Value;
            Console.WriteLine($"  {type,-18} | {param,-40} | \"{value}\" x{count}");

            rows.Add(new Dictionary<string, string>
            {
                ["Parameter"] = param,
                ["Value"]     = value,
                ["Type"]      = type,
                ["Count"]     = count.ToString(CultureInfo.InvariantCulture),
            });
        }

        Console.WriteLine($"  Total distinct (parameter,value) pairs: {counts.Count}");

        TableWriter.WriteRows($"{OutputDir}/04_nonnumeric_values_by_type.csv", rows);
    }

    private static string ClassifyNonNumeric(string value)
    {
        if (string.IsNullOrWhiteSpace(value))                 return "BLANK";
        if (Parsers.IsNoSamplingText(value))                  return "NO_SAMPLING_TEXT";
        if (Parsers.IsPlaceholder(value))                     return "PLACEHOLDER";
        if (value.StartsWith("<") || value.StartsWith(">"))   return "LIMIT_SYMBOL";
        if (value.Contains('*'))                              return "ASTERISK";
        if (Regex.IsMatch(value, @"[eE][+-]?\d+"))            return "SCIENTIFIC";
        if (value.Contains(','))                              return "THOUSANDS_SEP";
        if (Regex.IsMatch(value, @"\d\.\s+\d"))               return "SPACE_IN_DECIMAL";
        if (Regex.IsMatch(value, @"[A-Za-z]"))                return "CONTAINS_LETTERS";

        var (ok, _, _) = Parsers.TryParseNumber(value);
        return ok ? "NUMERIC" : "OTHER";
    }

    // unparsable datetime values
    private static void AssessDateTimeIssues(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Date and Time parsing issues ---");

        var rows = new List<IDictionary<string, string>>();
        int dateFail = 0, timeFail = 0;

        foreach (var rec in records)
        {
            var dt = rec.TryGetValue("DateText", out var d) ? d : "";
            var tt = rec.TryGetValue("TimeText", out var t) ? t : "";

            if (Parsers.IsNoSamplingText(dt)) dt = "";
            if (Parsers.IsNoSamplingText(tt)) tt = "";

            if (!string.IsNullOrWhiteSpace(dt) && !Parsers.IsPlaceholder(dt))
            {
                var (ok, _) = Parsers.TryParseDate(dt);
                if (!ok)
                {
                    dateFail++;
                    rows.Add(new Dictionary<string, string>
                    {
                        ["Sheet"]  = rec["SourceSheet"],
                        ["Row"]    = rec["SourceRow"],
                        ["Field"]  = "Date",
                        ["Text"]   = dt,
                        ["Reason"] = "UNPARSEABLE_DATE",
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(tt) && !Parsers.IsPlaceholder(tt))
            {
                var (ok, _) = Parsers.TryParseTime(tt);
                if (!ok)
                {
                    timeFail++;
                    rows.Add(new Dictionary<string, string>
                    {
                        ["Sheet"]  = rec["SourceSheet"],
                        ["Row"]    = rec["SourceRow"],
                        ["Field"]  = "Time",
                        ["Text"]   = tt,
                        ["Reason"] = "UNPARSEABLE_TIME",
                    });
                }
            }
        }

        Console.WriteLine($"  Unparseable dates: {dateFail}");
        Console.WriteLine($"  Unparseable times: {timeFail}");

        TableWriter.WriteRows($"{OutputDir}/05_unparseable_dates_and_times.csv", rows);
    }

    private static void AssessEmptyOrNoSampling(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Empty or no-sampling rows ---");

        var rows = new List<IDictionary<string, string>>();
        int noSamplingCount = 0;
        int emptyCount = 0;

        foreach (var rec in records)
        {
            bool hasAnyParam = false;
            foreach (var param in Schema.StandardParameterNames)
            {
                var v = rec.TryGetValue(param, out var raw) ? raw : "";
                if (!Parsers.IsPlaceholder(v) && !Parsers.IsNoSamplingText(v))
                {
                    hasAnyParam = true;
                    break;
                }
            }
            if (hasAnyParam) continue;

            var d = rec.TryGetValue("DateText", out var dv) ? dv : "";
            var t = rec.TryGetValue("TimeText", out var tv) ? tv : "";
            var b = rec.TryGetValue("StationRaw", out var bv) ? bv : "";

            string reason;
            string preview;

            if (Parsers.IsNoSamplingText(d) ||
                Parsers.IsNoSamplingText(t) ||
                Parsers.IsNoSamplingText(b))
            {
                reason = "NO_SAMPLING_TEXT";
                preview = Parsers.IsNoSamplingText(d) ? d
                        : Parsers.IsNoSamplingText(t) ? t
                        : b;
                noSamplingCount++;
            }
            else
            {
                reason = "EMPTY_PLACEHOLDER";
                preview = "";
                emptyCount++;
            }

            rows.Add(new Dictionary<string, string>
            {
                ["Sheet"]   = rec["SourceSheet"],
                ["Row"]     = rec["SourceRow"],
                ["Label"]   = rec["YearMonthLabel"],
                ["Reason"]  = reason,
                ["Preview"] = preview,
            });
        }

        Console.WriteLine($"  Empty rows (blank, no measurements): {emptyCount}");
        Console.WriteLine($"  No-sampling rows (text marker):      {noSamplingCount}");
        Console.WriteLine($"  Total:                               {emptyCount + noSamplingCount}");

        TableWriter.WriteRows($"{OutputDir}/06_rows_without_measurements.csv", rows);
    }

    // duplicate groups (sheet-label-station = 1 group)
    private static void AssessDuplicates(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Duplicate checks ---");

        var rows = new List<IDictionary<string, string>>();

        var byKey = records
            .GroupBy(r => $"{r["SourceSheet"]}|{r["YearMonthLabel"]}|{r["StationRaw"]}")
            .Where(g => g.Count() > 1)
            .ToList();

        Console.WriteLine($"  Duplicate (sheet+label+station) groups: {byKey.Count}");

        foreach (var g in byKey)
        {
            rows.Add(new Dictionary<string, string>
            {
                ["Type"]       = "SHEET_LABEL_STATION",
                ["Key"]        = g.Key,
                ["Count"]      = g.Count().ToString(CultureInfo.InvariantCulture),
                ["SourceRows"] = string.Join(" ; ", g.Select(r => r["SourceRow"]).Take(5)),
            });
        }

        var byExact = records
            .Where(HasAnyData)
            .GroupBy(r => string.Join("\u0001", r.Values))
            .Where(g => g.Count() > 1)
            .ToList();

        Console.WriteLine($"  Exact duplicate data rows: {byExact.Count} groups");

        foreach (var g in byExact)
        {
            var first = g.First();
            rows.Add(new Dictionary<string, string>
            {
                ["Type"]       = "EXACT_ROW",
                ["Key"]        = $"{first["SourceSheet"]} row {first["SourceRow"]}",
                ["Count"]      = g.Count().ToString(CultureInfo.InvariantCulture),
                ["SourceRows"] = string.Join(" ; ", g.Select(r => r["SourceRow"]).Take(5)),
            });
        }

        TableWriter.WriteRows($"{OutputDir}/07_duplicate_groups.csv", rows);
    }

    private static bool HasAnyData(Dictionary<string, string> rec)
    {
        foreach (var param in Schema.StandardParameterNames)
        {
            var v = rec.TryGetValue(param, out var raw) ? raw : "";
            if (!Parsers.IsPlaceholder(v)) return true;
        }
        return false;
    }

}