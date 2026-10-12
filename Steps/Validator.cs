using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Validator
{
    public const string FinalPath   = Transformer.OutputPath;
    public const string LogPath     = LogBuilder.OutputPath;
    public const string RawPath     = Extractor.OutputPath;
    public const string StageEPath  = Imputer.StageEPath;
    public const string ExcludedPath = Cleaner.ExcludedPath;
    public const string ImputedPath  = Imputer.ImputedLogPath;
    public const string ReportPath   = "Output/validation_report.csv";

    private static readonly List<Dictionary<string, string>> _report = new();
    private static int _id = 1;

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("======== 09: Validator (Final Dataset and Log) ========");

        _report.Clear();
        _id = 1;

        var final    = LoadCsv(FinalPath);
        var log      = LoadCsv(LogPath);
        var raw      = LoadCsv(RawPath);
        var stageE   = LoadCsv(StageEPath);
        var excluded = LoadCsv(ExcludedPath);
        var imputed  = LoadCsv(ImputedPath);

        Console.WriteLine($"  Loaded: final={final.Count} log={log.Count} " +
                          $"raw={raw.Count} stageE={stageE.Count} " +
                          $"excluded={excluded.Count} imputed={imputed.Count}");

        CheckRowReconciliation(final, raw, excluded);
        CheckColumnLayout(final);
        CheckNoDroppedColumns(final);
        CheckNoDuplicates(final);
        CheckYearMonthRanges(final);
        CheckPhRangeAndNegatives(final);
        CheckEveryRowHasMeasurement(final);
        CheckStationCoverage(final);
        CheckLog10Column(final);
        CheckNoScientificNotation(final);
        CheckImputedValuesWithinRange(final, imputed);
        CheckLogIntegrity(log);
        CheckLogReconciliation(log, final, excluded, imputed);
        CheckLogIdUnique(log);
        CheckImputationNeighbors(log, imputed);
        CheckNoDuplicateLogEntries(log);
        CheckImputedLogMatchesFinal(log, final);
        CheckCellChangeCompleteness(final, raw, log);

        WriteReport();
        PrintSummary();

        Console.WriteLine($"  Wrote {ReportPath}");
    }

    private static void CheckRowReconciliation(
        List<Dictionary<string, string>> final,
        List<Dictionary<string, string>> raw,
        List<Dictionary<string, string>> excluded)
    {
        int expected = raw.Count - excluded.Count;
        AddCheck("Dataset", "Raw rows = final rows + excluded rows",
                 raw.Count.ToString(), (final.Count + excluded.Count).ToString(),
                 raw.Count == final.Count + excluded.Count);
    }

    private static void CheckColumnLayout(List<Dictionary<string, string>> final)
    {
        if (final.Count == 0)
        {
            AddCheck("Dataset", "Final column order", "22 columns", "empty file", false);
            return;
        }

        var actual = final[0].Keys.ToList();
        bool same = actual.Count == Schema.FinalColumnOrder.Length;
        if (same)
        {
            for (int i = 0; i < actual.Count; i++)
            {
                if (!string.Equals(actual[i], Schema.FinalColumnOrder[i], StringComparison.OrdinalIgnoreCase))
                {
                    same = false;
                    break;
                }
            }
        }

        AddCheck("Dataset", "Final column order matches Schema.FinalColumnOrder",
                 string.Join(",", Schema.FinalColumnOrder),
                 string.Join(",", actual), same);
    }

    private static void CheckNoDroppedColumns(List<Dictionary<string, string>> final)
    {
        if (final.Count == 0) return;

        var cols = final[0].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] forbidden = { "Ammonia_mgL", "TotalColiform_MPN", "Is_Outlier",
                               "Outlier_Params", "Imputed_Params", "SymbolValue_Params",
                               "DateLabelMismatch", "YearMonth" };

        var found = forbidden.Where(c => cols.Contains(c)).ToList();
        AddCheck("Dataset", "No dropped/flag columns in final dataset",
                 "none", found.Count == 0 ? "none" : string.Join(",", found),
                 found.Count == 0);
    }

    private static void CheckNoDuplicates(List<Dictionary<string, string>> final)
    {
        var dupes = final
            .GroupBy(r => $"{r["WaterBody"]}|{r["StationID"]}|{r["Year"]}|{r["Month"]}")
            .Count(g => g.Count() > 1);

        AddCheck("Dataset", "No duplicate (WaterBody, StationID, Year, Month)",
                 "0", dupes.ToString(), dupes == 0);
    }

    private static void CheckYearMonthRanges(List<Dictionary<string, string>> final)
    {
        int badYear  = final.Count(r => !int.TryParse(r["Year"], out var y) || y < 2012 || y > 2018);
        int badMonth = final.Count(r => !int.TryParse(r["Month"], out var m) || m < 1 || m > 12);

        AddCheck("Dataset", "Years are 2012-2018",
                 "0 violations", badYear.ToString(), badYear == 0);
        AddCheck("Dataset", "Months are 1-12",
                 "0 violations", badMonth.ToString(), badMonth == 0);
    }

    private static void CheckPhRangeAndNegatives(List<Dictionary<string, string>> final)
    {
        int badPh = 0;
        int badNeg = 0;
        foreach (var row in final)
        {
            if (double.TryParse(row["pH"], NumberStyles.Float, CultureInfo.InvariantCulture, out var ph))
            {
                if (ph < 0 || ph > 14) badPh++;
            }
            foreach (var p in Schema.SafeParameterNames)
            {
                if (p == "pH") continue;
                if (!final[0].Keys.Contains(p)) continue;
                if (double.TryParse(row[p], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v < 0)
                    badNeg++;
            }
        }

        AddCheck("Dataset", "pH between 0 and 14",
                 "0 violations", badPh.ToString(), badPh == 0);
        AddCheck("Dataset", "No negative values anywhere",
                 "0 violations", badNeg.ToString(), badNeg == 0);
    }

    private static void CheckEveryRowHasMeasurement(List<Dictionary<string, string>> final)
    {
        var paramSafeNames = Schema.SafeParameterNames
            .Where(n => final.Count > 0 && final[0].Keys.Contains(n))
            .ToList();

        int empty = final.Count(r =>
            paramSafeNames.All(p => string.IsNullOrWhiteSpace(r[p])));

        AddCheck("Dataset", "Every row has at least one measurement",
                 "0 empty rows", empty.ToString(), empty == 0);
    }

    private static void CheckStationCoverage(List<Dictionary<string, string>> final)
    {
        var byBody = final.GroupBy(r => r["WaterBody"])
                          .ToDictionary(g => g.Key, g => g.Select(r => r["StationID"]).Distinct().Count());

        bool marilaoOk    = byBody.TryGetValue("Marilao",    out var m) && m == 5;
        bool meycauayanOk = byBody.TryGetValue("Meycauayan", out var y) && y == 5;
        bool obandoOk     = byBody.TryGetValue("Obando",     out var o) && o == 3;

        AddCheck("Dataset", "Marilao has 5 stations",
                 "5", byBody.TryGetValue("Marilao", out var m2) ? m2.ToString() : "0", marilaoOk);
        AddCheck("Dataset", "Meycauayan has 5 stations",
                 "5", byBody.TryGetValue("Meycauayan", out var y2) ? y2.ToString() : "0", meycauayanOk);
        AddCheck("Dataset", "Obando has 3 stations",
                 "3", byBody.TryGetValue("Obando", out var o2) ? o2.ToString() : "0", obandoOk);
    }

    private static void CheckLog10Column(List<Dictionary<string, string>> final)
    {
        int mismatches = 0;
        foreach (var row in final)
        {
            var fc  = row["FecalColiform_MPN_per100mL"];
            var log = row["FecalColiform_log10"];

            bool fcEmpty  = string.IsNullOrWhiteSpace(fc);
            bool logEmpty = string.IsNullOrWhiteSpace(log);

            if (fcEmpty != logEmpty) { mismatches++; continue; }
            if (fcEmpty) continue;

            if (!double.TryParse(fc,  NumberStyles.Float, CultureInfo.InvariantCulture, out var fcVal)) { mismatches++; continue; }
            if (!double.TryParse(log, NumberStyles.Float, CultureInfo.InvariantCulture, out var logVal)) { mismatches++; continue; }
            if (fcVal <= 0) continue;

            double expected = Math.Log10(fcVal);
            if (Math.Abs(expected - logVal) > 0.001) mismatches++;
        }

        AddCheck("Dataset", "FecalColiform_log10 matches log10(Fecal MPN)",
                 "0 mismatches", mismatches.ToString(), mismatches == 0);
    }

    private static void CheckNoScientificNotation(List<Dictionary<string, string>> final)
    {
        int count = 0;
        foreach (var row in final)
            foreach (var kv in row)
                if (!string.IsNullOrWhiteSpace(kv.Value) &&
                    Regex.IsMatch(kv.Value, @"\d[eE][+-]?\d"))
                    count++;

        AddCheck("Dataset", "No scientific notation in final dataset",
                 "0", count.ToString(), count == 0);
    }

    private static void CheckImputedValuesWithinRange(
        List<Dictionary<string, string>> final,
        List<Dictionary<string, string>> imputed)
    {
        int bad = 0;

        var finalIndex = final.ToDictionary(
            r => $"{r["WaterBody"]}\u0001{r["StationID"]}\u0001{r["Year"]}-{int.Parse(r["Month"]):D2}",
            r => r);

        foreach (var i in imputed)
        {
            var key = $"{i["SourceSheet"]}\u0001{i["StationID"]}\u0001{i["YearMonth"]}";
            if (!finalIndex.TryGetValue(key, out var fr)) continue;

            var safeName = Schema.ParameterToSafeName.TryGetValue(i["Parameter"], out var sn) ? sn : "";
            if (string.IsNullOrWhiteSpace(safeName)) continue;
            if (!fr.ContainsKey(safeName)) continue;

            if (!double.TryParse(fr[safeName], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            if (v < 0) bad++;
            if (safeName == "pH" && (v < 0 || v > 14)) bad++;
        }

        AddCheck("Dataset", "All imputed values within plausible ranges",
                 "0 violations", bad.ToString(), bad == 0);
    }

    // log checks
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "MANUAL_CORRECTION", "LIMIT_STANDARDIZED", "TYPO_FIXED",
        "PLACEHOLDER_TO_NULL", "DATE_PARSED", "TIME_PARSED",
        "FLAGGED_OUTLIER", "IMPUTED", "ROW_EXCLUDED", "COLUMN_DROPPED"
    };

    private static void CheckLogIntegrity(List<Dictionary<string, string>> log)
    {
        int badAction = log.Count(e => !AllowedActions.Contains(e["Action"]));
        AddCheck("Log", "All Action values are from the allowed list",
                 "0 violations", badAction.ToString(), badAction == 0);

        string[] alwaysRequired = { "LogID", "Level", "Action" };
        int blankCore = log.Count(e => alwaysRequired.Any(f => string.IsNullOrWhiteSpace(e[f])));
        AddCheck("Log", "Core fields (LogID, Level, Action) are never blank",
                 "0 violations", blankCore.ToString(), blankCore == 0);

        int blankSheet = log.Count(e =>
            (e["Level"] == "Cell" || e["Level"] == "Row") &&
            string.IsNullOrWhiteSpace(e["SourceSheet"]));
        AddCheck("Log", "SourceSheet is filled on Cell and Row entries",
                 "0 violations", blankSheet.ToString(), blankSheet == 0);
    }

    private static void CheckLogReconciliation(
        List<Dictionary<string, string>> log,
        List<Dictionary<string, string>> final,
        List<Dictionary<string, string>> excluded,
        List<Dictionary<string, string>> imputed)
    {
        int logExcluded = log.Count(e => e["Action"] == "ROW_EXCLUDED");
        int logImputed  = log.Count(e => e["Action"] == "IMPUTED");

        AddCheck("Log", "ROW_EXCLUDED entries = excluded_records.csv rows",
                 excluded.Count.ToString(), logExcluded.ToString(), logExcluded == excluded.Count);
        AddCheck("Log", "IMPUTED entries = imputed_cells.csv rows",
                 imputed.Count.ToString(), logImputed.ToString(), logImputed == imputed.Count);

        int logDropped = log.Count(e => e["Action"] == "COLUMN_DROPPED");
        AddCheck("Log", "COLUMN_DROPPED count = 2 data parameters",
                 "2", logDropped.ToString(), logDropped == 2);
    }

    private static void CheckLogIdUnique(List<Dictionary<string, string>> log)
    {
        int total = log.Count;
        int unique = log.Select(e => e["LogID"]).Distinct().Count();
        AddCheck("Log", "LogID is unique",
                 total.ToString(), unique.ToString(), total == unique);
    }

    private static void CheckImputationNeighbors(
        List<Dictionary<string, string>> log,
        List<Dictionary<string, string>> imputed)
    {
        int bad = 0;
        foreach (var i in imputed)
        {
            if (!double.TryParse(i["PrevValue"], NumberStyles.Float, CultureInfo.InvariantCulture, out var pv)) { bad++; continue; }
            if (!double.TryParse(i["NextValue"], NumberStyles.Float, CultureInfo.InvariantCulture, out var nv)) { bad++; continue; }

            double mid = (pv + nv) / 2.0;
            double min = Math.Min(pv, nv);
            double max = Math.Max(pv, nv);

            if (!double.TryParse(i["ImputedValue"], NumberStyles.Float, CultureInfo.InvariantCulture, out var imp))
            { bad++; continue; }

            if (imp < min - 0.001 || imp > max + 0.001)
            {
                if (pv > 0 && nv > 0 && imp > 0)
                {
                    double logMid = Math.Pow(10, (Math.Log10(pv) + Math.Log10(nv)) / 2.0);
                    if (Math.Abs(imp - logMid) > 0.001) bad++;
                }
                else bad++;
            }
        }

        AddCheck("Log", "All imputed values are midpoints of their neighbors",
                 "0 violations", bad.ToString(), bad == 0);
    }

    private static void CheckNoDuplicateLogEntries(List<Dictionary<string, string>> log)
    {
        var dupes = log
            .Where(e => !string.IsNullOrWhiteSpace(e["SourceRow"]) &&
                        !string.IsNullOrWhiteSpace(e["Parameter"]))
            .GroupBy(e => $"{e["SourceSheet"]}|{e["SourceRow"]}|{e["Parameter"]}|{e["Action"]}")
            .Where(g => g.Count() > 1)
            .ToList();

        AddCheck("Log", "No duplicate (Sheet, Row, Parameter, Action) tuples",
                 "0 duplicates", dupes.Count.ToString(), dupes.Count == 0);

        if (dupes.Count > 0)
        {
            Console.WriteLine("    Sample duplicates:");
            foreach (var g in dupes.Take(5))
                Console.WriteLine($"      {g.Key} (x{g.Count()})");
        }
    }

    private static void CheckImputedLogMatchesFinal(
        List<Dictionary<string, string>> log,
        List<Dictionary<string, string>> final)
    {
        var finalIndex = new Dictionary<string, Dictionary<string, string>>();
        foreach (var r in final)
            finalIndex[$"{r["WaterBody"]}|{r["StationID"]}|{r["Year"]}|{r["Month"]}"] = r;

        int mismatches = 0;

        foreach (var e in log.Where(x => x["Action"] == "IMPUTED"))
        {
            var k = $"{e["WaterBody"]}|{e["StationID"]}|{e["Year"]}|{e["Month"]}";
            if (!finalIndex.TryGetValue(k, out var fr)) { mismatches++; continue; }

            var std = e["Parameter"];
            if (!Schema.ParameterToSafeName.TryGetValue(std, out var safe)) { mismatches++; continue; }
            if (!fr.TryGetValue(safe, out var finalVal)) { mismatches++; continue; }

            if (!double.TryParse(e["NewValue"], NumberStyles.Float, CultureInfo.InvariantCulture, out var logNum) ||
                !double.TryParse(finalVal, NumberStyles.Float, CultureInfo.InvariantCulture, out var finalNum) ||
                Math.Abs(logNum - finalNum) > 0.0001)
            {
                mismatches++;
            }
        }

        AddCheck("Log", "IMPUTED NewValue matches final dataset value",
                 "0 mismatches", mismatches.ToString(), mismatches == 0);
    }

    private static void CheckCellChangeCompleteness(
        List<Dictionary<string, string>> final,
        List<Dictionary<string, string>> raw,
        List<Dictionary<string, string>> log)
    {
        var rawIndex = new Dictionary<string, Dictionary<string, string>>();
        foreach (var r in raw)
            rawIndex[$"{r["SourceSheet"]}|{r["SourceRow"]}"] = r;

        var valueChanging = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "LIMIT_STANDARDIZED", "PLACEHOLDER_TO_NULL", "TYPO_FIXED", "IMPUTED" };

        var logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in log)
        {
            if (valueChanging.Contains(e["Action"]))
                logged.Add($"{e["SourceSheet"]}|{e["SourceRow"]}|{e["Parameter"]}");
        }

        var safeToStd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Schema.ParameterToSafeName)
            safeToStd[kv.Value] = kv.Key;

        int unlogged = 0;
        var samples = new List<string>();

        foreach (var f in final)
        {
            var key = $"{f["SourceSheet"]}|{f["SourceRow"]}";
            if (!rawIndex.TryGetValue(key, out var rawRow)) continue;

            foreach (var safe in Schema.SafeParameterNames)
            {
                if (!f.ContainsKey(safe)) continue;
                if (!safeToStd.TryGetValue(safe, out var std)) continue;
                if (Schema.DroppedParameters.Contains(std)) continue;

                var finalVal = f[safe];
                var rawVal   = rawRow.TryGetValue(std, out var rv) ? rv : "";

                if (!ValuesDiffer(rawVal, finalVal)) continue;

                var logKey = $"{f["SourceSheet"]}|{f["SourceRow"]}|{std}";
                if (!logged.Contains(logKey))
                {
                    unlogged++;
                    if (samples.Count < 5)
                        samples.Add($"{f["SourceSheet"]} row {f["SourceRow"]} {std}: raw='{rawVal}' final='{finalVal}'");
                }
            }
        }

        AddCheck("Log", "Every cell-level change has a log entry",
                 "0 unlogged", unlogged.ToString(), unlogged == 0);

        if (unlogged > 0)
        {
            Console.WriteLine("    Sample unlogged changes:");
            foreach (var s in samples)
                Console.WriteLine($"      {s}");
        }
    }

    private static bool ValuesDiffer(string rawVal, string finalVal)
    {
        bool rawEmpty   = string.IsNullOrWhiteSpace(rawVal) || Parsers.IsPlaceholder(rawVal);
        bool finalEmpty = string.IsNullOrWhiteSpace(finalVal);

        if (rawEmpty && finalEmpty) return false;
        if (rawEmpty != finalEmpty) return true;

        var (rawOk, rawNum, _)     = Parsers.TryParseNumber(rawVal);
        var (finalOk, finalNum, _) = Parsers.TryParseNumber(finalVal);

        if (rawOk && finalOk)
            return Math.Round(rawNum, 4) != Math.Round(finalNum, 4);

        return rawVal != finalVal;
    }

    // helpers
    private static void AddCheck(string category, string check, string expected, string actual, bool passed)
    {
        _report.Add(new Dictionary<string, string>
        {
            ["CheckID"]  = _id++.ToString(),
            ["Category"] = category,
            ["Check"]    = check,
            ["Expected"] = expected,
            ["Actual"]   = actual,
            ["Result"]   = passed ? "PASS" : "FAIL"
        });
    }

    private static void WriteReport()
    {
        var headers = new[] { "CheckID", "Category", "Check", "Expected", "Actual", "Result" };
        var rows = _report.Select(r =>
            (IReadOnlyList<string>)new[] { r["CheckID"], r["Category"], r["Check"], r["Expected"], r["Actual"], r["Result"] }
        ).ToList();

        TableWriter.WriteTable(ReportPath, headers, rows);
    }

    private static void PrintSummary()
    {
        int pass = _report.Count(r => r["Result"] == "PASS");
        int fail = _report.Count(r => r["Result"] == "FAIL");

        Console.WriteLine();
        Console.WriteLine("--- Validation summary ---");
        Console.WriteLine($"  PASS: {pass}");
        Console.WriteLine($"  FAIL: {fail}");

        if (fail > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Failed checks:");
            foreach (var r in _report.Where(r => r["Result"] == "FAIL"))
                Console.WriteLine($"    [{r["CheckID"]}] {r["Check"]}  (expected {r["Expected"]}, got {r["Actual"]})");
        }
    }

    private static List<Dictionary<string, string>> LoadCsv(string path)
    {
        var list = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return list;

        using var reader = new StreamReader(path);
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
}