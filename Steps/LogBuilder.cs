using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class LogBuilder
{
    public const string OutputPath  = "Data/clean/mmors_consolidated_cleaning_log_2012_2018.csv";
    public const string SummaryPath = "Output/tables/cleaning/cleaning_log_summary.csv";

    private const string RawPath         = Extractor.OutputPath;
    private const string CleanedPath     = Cleaner.FinalCleanedPath;
    private const string ImputedPath     = Imputer.OutputPath;
    private const string CorrectionsPath = Cleaner.ManualCorrectionsPath;

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("====== 08: LogBuilder (Consolidated Cleaning Log) =====");

        var raw     = LoadCsv(RawPath);
        var cleaned = LoadCsv(CleanedPath);
        var imputed = LoadCsv(ImputedPath);

        Console.WriteLine($"  Loaded raw={raw.Count} cleaned={cleaned.Count} imputed={imputed.Count}");

        var log = new List<Dictionary<string, string>>();
        int id = 1;

        AddStandardization(log, ref id, raw, cleaned);
        AddManualCorrections(log, ref id);
        AddOutliers(log, ref id, cleaned);
        AddImputed(log, ref id, imputed);

        Console.WriteLine($"  Total log entries: {log.Count}");

        WriteLog(log);
        WriteSummary(log);

        Console.WriteLine($"  Wrote {OutputPath}");
        Console.WriteLine($"  Wrote {SummaryPath}");
    }

    // standardization (raw vs cleaned)
    private static void AddStandardization(
        List<Dictionary<string, string>> log, ref int id,
        List<Dictionary<string, string>> raw,
        List<Dictionary<string, string>> cleaned)
    {
        var index = new Dictionary<string, Dictionary<string, string>>();
        foreach (var r in cleaned)
            index[Key(r["SourceSheet"], r["SourceRow"])] = r;

        var dateCorrectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(CorrectionsPath))
        {
            var corrections = LoadCsv(CorrectionsPath);
            foreach (var c in corrections)
            {
                if (c["Field"].Equals("DateText", StringComparison.OrdinalIgnoreCase))
                    dateCorrectedKeys.Add(Key(c["Sheet"], c["SourceRow"]));
            }
        }

        int count = 0;

        foreach (var rawRec in raw)
        {
            var key = Key(rawRec["SourceSheet"], rawRec["SourceRow"]);
            if (!index.TryGetValue(key, out var clean)) continue;

            // params
            foreach (var p in Schema.StandardParameterNames)
            {
                var rawVal   = rawRec.TryGetValue(p, out var rv) ? rv : "";
                var cleanVal = clean.TryGetValue(p,  out var cv) ? cv : "";

                if (rawVal == cleanVal) continue;

                string? action = null;
                string reason  = "";

                if (Parsers.IsPlaceholder(rawVal) && string.IsNullOrWhiteSpace(cleanVal))
                {
                    action = "PLACEHOLDER_TO_NULL";
                    reason = $"Placeholder '{rawVal}' converted to null";
                }
                else if (rawVal.StartsWith("<") || rawVal.StartsWith(">") || rawVal.Contains('*'))
                {
                    action = "LIMIT_STANDARDIZED";
                    reason = $"Measurement-limit symbol removed; value set to {cleanVal}";
                }
                else if (Regex.IsMatch(rawVal, @"\d\.\s+\d"))
                {
                    action = "TYPO_FIXED";
                    reason = $"Internal space in decimal removed: '{rawVal}' -> '{cleanVal}'";
                }

                if (action == null) continue;

                log.Add(Row(id++, "Cell", action, clean, p, rawVal, cleanVal, reason, "data_before_imputation.csv"));
                count++;
            }

            if (!dateCorrectedKeys.Contains(key))
            {
                var rawDate   = rawRec.TryGetValue("DateText", out var rd) ? rd : "";
                var cleanDate = clean.TryGetValue("SampleDate", out var cd) ? cd : "";
                if (!string.IsNullOrWhiteSpace(rawDate) && !string.IsNullOrWhiteSpace(cleanDate) && rawDate != cleanDate)
                {
                    log.Add(Row(id++, "Cell", "DATE_PARSED", clean, "Date", rawDate, cleanDate, "Date parsed and standardized", "data_before_imputation.csv"));
                    count++;
                }
            }

            var rawTime   = rawRec.TryGetValue("TimeText", out var rt) ? rt : "";
            var cleanTime = clean.TryGetValue("SampleTime", out var ct) ? ct : "";
            if (!string.IsNullOrWhiteSpace(rawTime) && !string.IsNullOrWhiteSpace(cleanTime) && rawTime != cleanTime && Regex.IsMatch(rawTime, @"[A-Za-z]"))
            {
                log.Add(Row(id++, "Cell", "TIME_PARSED", clean, "Time", rawTime, cleanTime, "Text time parsed and standardized", "data_before_imputation.csv"));
                count++;
            }
        }

        Console.WriteLine($"  Standardization entries: {count}");
    }

    // manual corrections
    private static void AddManualCorrections(List<Dictionary<string, string>> log, ref int id)
    {
        if (!File.Exists(CorrectionsPath)) return;

        var corrections = LoadCsv(CorrectionsPath);
        int count = 0;

        foreach (var c in corrections)
        {
            int year = 0, month = 0;
            if (c["Field"] == "YearMonthLabel")
            {
                var (ok, y, m) = Parsers.TryParseMonthLabel(c["NewValue"]);
                if (ok) { year = y; month = m; }
            }

            log.Add(new Dictionary<string, string>
            {
                ["LogID"]        = id++.ToString(),
                ["Level"]        = "Cell",
                ["Action"]       = "MANUAL_CORRECTION",
                ["SourceSheet"]  = c["Sheet"],
                ["SourceRow"]    = c["SourceRow"],
                ["WaterBody"]    = c["Sheet"],
                ["StationID"]    = "",
                ["Year"]         = year > 0 ? year.ToString() : "",
                ["Month"]        = month > 0 ? month.ToString() : "",
                ["Parameter"]    = c["Field"],
                ["OldValue"]     = c["OldValue"],
                ["NewValue"]     = c["NewValue"],
                ["Reason"]       = c["Reason"],
                ["EvidenceFile"] = "manual_corrections.csv",
            });
            count++;
        }
        Console.WriteLine($"  Manual corrections: {count}");
    }

    // outliers
    private static void AddOutliers(
        List<Dictionary<string, string>> log, ref int id,
        List<Dictionary<string, string>> cleaned)
    {
        int count = 0;

        foreach (var r in cleaned)
        {
            var op = r.TryGetValue("Outlier_Params", out var v) ? v : "";
            if (string.IsNullOrWhiteSpace(op)) continue;

            foreach (var p in op.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pTrim = p.Trim();
                var value = r.TryGetValue(pTrim, out var val) ? val : "";

                log.Add(new Dictionary<string, string>
                {
                    ["LogID"]        = id++.ToString(),
                    ["Level"]        = "Cell",
                    ["Action"]       = "FLAGGED_OUTLIER",
                    ["SourceSheet"]  = r["SourceSheet"],
                    ["SourceRow"]    = r["SourceRow"],
                    ["WaterBody"]    = r["SourceSheet"],
                    ["StationID"]    = r["StationID"],
                    ["Year"]         = r["Year"],
                    ["Month"]        = r["Month"],
                    ["Parameter"]    = pTrim,
                    ["OldValue"]     = value,
                    ["NewValue"]     = value,
                    ["Reason"]       = "Outside IQR fence; flagged and replaced with Median",
                    ["EvidenceFile"] = "outlier_summary.csv",
                });
                count++;
            }
        }
        Console.WriteLine($"  Flagged outliers: {count}");
    }

    // imputed cells
    private static void AddImputed(
        List<Dictionary<string, string>> log, ref int id,
        List<Dictionary<string, string>> imputed)
    {
        int count = 0;

        foreach (var r in imputed)
        {
            var ip = r.TryGetValue("Imputed_Params", out var v) ? v : "";
            if (string.IsNullOrWhiteSpace(ip)) continue;

            foreach (var p in ip.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pTrim = p.Trim();
                var newValue = r.TryGetValue(pTrim, out var val) ? val : "";

                log.Add(new Dictionary<string, string>
                {
                    ["LogID"]        = id++.ToString(),
                    ["Level"]        = "Cell",
                    ["Action"]       = "IMPUTED",
                    ["SourceSheet"]  = r["SourceSheet"],
                    ["SourceRow"]    = r["SourceRow"],
                    ["WaterBody"]    = r["SourceSheet"],
                    ["StationID"]    = r["StationID"],
                    ["Year"]         = r["Year"],
                    ["Month"]        = r["Month"],
                    ["Parameter"]    = pTrim,
                    ["OldValue"]     = "",
                    ["NewValue"]     = newValue,
                    ["Reason"]       = "Statistical imputation applied (Mean/Median/Mode)",
                    ["EvidenceFile"] = "data_after_imputation.csv",
                });
                count++;
            }
        }
        Console.WriteLine($"  Imputed cells: {count}");
    }

    // helpers
    private static string Key(params string[] parts) => string.Join('\u0001', parts);

    private static Dictionary<string, string> Row(
        int id, string level, string action,
        Dictionary<string, string> rec, string parameter,
        string oldVal, string newVal, string reason, string evidence)
    {
        return new Dictionary<string, string>
        {
            ["LogID"]        = id.ToString(),
            ["Level"]        = level,
            ["Action"]       = action,
            ["SourceSheet"]  = rec.TryGetValue("SourceSheet", out var s) ? s : "",
            ["SourceRow"]    = rec.TryGetValue("SourceRow",   out var r) ? r : "",
            ["WaterBody"]    = rec.TryGetValue("SourceSheet", out var w) ? w : "",
            ["StationID"]    = rec.TryGetValue("StationID",   out var si) ? si : "",
            ["Year"]         = rec.TryGetValue("Year",        out var y) ? y : "",
            ["Month"]        = rec.TryGetValue("Month",       out var m) ? m : "",
            ["Parameter"]    = parameter,
            ["OldValue"]     = oldVal,
            ["NewValue"]     = newVal,
            ["Reason"]       = reason,
            ["EvidenceFile"] = evidence,
        };
    }

    private static List<Dictionary<string, string>> LoadCsv(string path)
    {
        var list = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return list;

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
            list.Add(row);
        }
        return list;
    }

    private static void WriteLog(List<Dictionary<string, string>> log)
    {
        var headers = new[]
        {
            "LogID", "Level", "Action", "SourceSheet", "SourceRow",
            "WaterBody", "StationID", "Year", "Month", "Parameter",
            "OldValue", "NewValue", "Reason", "EvidenceFile"
        };

        var rows = new List<IReadOnlyList<string>>();
        foreach (var e in log)
        {
            var line = new List<string>(headers.Length);
            foreach (var h in headers)
                line.Add(e.TryGetValue(h, out var v) ? (v ?? "") : "");
            rows.Add(line);
        }

        TableWriter.WriteTable(OutputPath, headers, rows);
    }

    private static void WriteSummary(List<Dictionary<string, string>> log)
    {
        var summary = log
            .GroupBy(e => e.TryGetValue("Action", out var a) ? a : "")
            .Select(g => (IDictionary<string, string>)new Dictionary<string, string>
            {
                ["Action"] = g.Key,
                ["Count"]  = g.Count().ToString(CultureInfo.InvariantCulture)
            })
            .OrderByDescending(x => int.Parse(x["Count"]))
            .ToList();

        Console.WriteLine();
        Console.WriteLine("--- Log summary ---");
        foreach (var s in summary)
            Console.WriteLine($"  {s["Action"],-25} {s["Count"]}");

        TableWriter.WriteRows(SummaryPath, summary);
    }
}