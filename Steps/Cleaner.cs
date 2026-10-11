using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Cleaner
{
    // -- A
    public const string InputPath             = Extractor.OutputPath;
    public const string ManualCorrectionsPath = "Data/interim/manual_corrections.csv";
    public const string StageAPath            = "Data/interim/clean_stage_a.csv";

    // -- B
    public const string StageBPath            = "Data/interim/clean_stage_b.csv";
    public const string ExcludedPath          = "Data/interim/excluded_records.csv";
    public const string DuplicateReportPath   = "Output/tables/cleaning/duplicates_after_exclusion.csv";

    // -- C
    public const string StageCPath               = "Data/interim/clean_stage_c.csv";
    public const string ImpossibilityReportPath  = "Output/tables/cleaning/impossible_values.csv";

    // -- D
    public const string StageDPath             = "Data/interim/clean_stage_d.csv";
    public const string OutlierSummaryPath     = "Output/tables/cleaning/outlier_summary.csv";
    public static void Run()
    {
        RunPhaseA();
        RunPhaseB();
        RunPhaseC();
        RunPhaseD();
    }

    // 4A - Corrections and standardization
    public static void RunPhaseA()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("==== 4A: Cleaner (Corrections and Standardization =====");

        if (!File.Exists(InputPath))
        {
            Console.WriteLine($"Input not found: {InputPath}. Run Step 2 first.");
            return;
        }

        var records = LoadRawRecords(InputPath);
        Console.WriteLine($"  Loaded {records.Count} raw records.");

        EnsureManualCorrectionsFile();
        var corrections = LoadManualCorrections(ManualCorrectionsPath);
        Console.WriteLine($"  Manual corrections loaded: {corrections.Count}");

        ApplyCorrections(records, corrections);

        var stats = WriteStageA(records);

        Console.WriteLine($"  Cells with symbol removed : {stats.SymbolCells}");
        Console.WriteLine($"  Date-label mismatches     : {stats.Mismatches}");
        Console.WriteLine($"  Wrote {StageAPath}");
    }

    private record StageAStats(int SymbolCells, int Mismatches);

    // 4B - Placeholders, empty rows, and exclusion log
    public static void RunPhaseB()
    {
        Console.WriteLine();
        Console.WriteLine("*******************************************************");
        Console.WriteLine("==== 4B: Cleaner (Placeholders and Empty Records) =====");

        if (!File.Exists(StageAPath))
        {
            Console.WriteLine($"Stage A file not found: {StageAPath}");
            return;
        }

        var stageA = LoadStageA(StageAPath);
        Console.WriteLine($"  Loaded {stageA.Count} stage-A records.");

        var rawIndex = BuildRawIndex(InputPath);

        var kept     = new List<Dictionary<string, string>>();
        var excluded = new List<Dictionary<string, string>>();

        foreach (var row in stageA)
        {
            var key = MakeKey(row["SourceSheet"], row["SourceRow"]);
            rawIndex.TryGetValue(key, out var raw);

            bool hasAnyValue = HasAnyParameterValue(row);

            if (hasAnyValue)
            {
                kept.Add(row);
            }
            else
            {
                var reason  = DetermineExclusionReason(row, raw);
                var preview = BuildPreview(row, raw);

                excluded.Add(new Dictionary<string, string>
                {
                    ["SourceSheet"] = row["SourceSheet"],
                    ["SourceRow"]   = row["SourceRow"],
                    ["YearMonth"]   = row["YearMonth"],
                    ["StationID"]   = row["StationID"],
                    ["Reason"]      = reason,
                    ["Preview"]     = preview,
                });
            }
        }

        Console.WriteLine($"  Kept rows     : {kept.Count}");
        Console.WriteLine($"  Excluded rows : {excluded.Count}");

        foreach (var g in excluded.GroupBy(e => e["Reason"]).OrderBy(g => g.Key))
            Console.WriteLine($"    {g.Key,-22}: {g.Count()}");

        WriteExcluded(excluded);
        WriteStageB(kept);
        WriteDuplicateReport(kept);

        Console.WriteLine();
        Console.WriteLine($"  Wrote {ExcludedPath}");
        Console.WriteLine($"  Wrote {StageBPath}");
        Console.WriteLine($"  Wrote {DuplicateReportPath}");
    }

    private static string MakeKey(string sheet, string row) => sheet + "|" + row;

    private static List<Dictionary<string, string>> LoadStageA(string path)
    {
        var list = new List<Dictionary<string, string>>();
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

    private record RawRecordMin(string Sheet, string Row, string DateText, string StationRaw);

    private static Dictionary<string, RawRecordMin> BuildRawIndex(string path)
    {
        var dict = new Dictionary<string, RawRecordMin>();
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();

        while (csv.Read())
        {
            var sheet = csv.GetField("SourceSheet") ?? "";
            var row   = csv.GetField("SourceRow")   ?? "";
            var dt    = csv.GetField("DateText")    ?? "";
            var st    = csv.GetField("StationRaw")  ?? "";
            dict[MakeKey(sheet, row)] = new RawRecordMin(sheet, row, dt, st);
        }
        return dict;
    }

    private static bool HasAnyParameterValue(Dictionary<string, string> row)
    {
        foreach (var p in Schema.StandardParameterNames)
        {
            if (row.TryGetValue(p, out var v) && !string.IsNullOrWhiteSpace(v))
                return true;
        }
        return false;
    }

    private static string DetermineExclusionReason(
        Dictionary<string, string> stageRow,
        RawRecordMin? raw)
    {
        if (string.IsNullOrWhiteSpace(stageRow["YearMonth"]))
            return "BAD_LABEL";

        if (raw != null && Parsers.IsNoSamplingText(raw.DateText))
            return "NO_SAMPLING_TEXT";

        bool hasDate = !string.IsNullOrWhiteSpace(stageRow["SampleDate"]);
        if (!hasDate)
            return "EMPTY_PLACEHOLDER";

        return "NO_RESULTS";
    }

    private static string BuildPreview(
        Dictionary<string, string> stageRow,
        RawRecordMin? raw)
    {
        if (raw != null && Parsers.IsNoSamplingText(raw.DateText))
            return raw.DateText;

        if (!string.IsNullOrWhiteSpace(stageRow["SampleDate"]))
            return "Date=" + stageRow["SampleDate"];

        return "";
    }

    private static void WriteExcluded(List<Dictionary<string, string>> excluded)
    {
        var headers = new[] { "SourceSheet", "SourceRow", "YearMonth", "StationID", "Reason", "Preview" };
        TableWriter.WriteRows(ExcludedPath, excluded.Select(e =>
            (IDictionary<string, string>)new Dictionary<string, string>
            {
                ["SourceSheet"] = e["SourceSheet"],
                ["SourceRow"]   = e["SourceRow"],
                ["YearMonth"]   = e["YearMonth"],
                ["StationID"]   = e["StationID"],
                ["Reason"]      = e["Reason"],
                ["Preview"]     = e["Preview"],
            }).ToList());
    }

    private static void WriteStageB(List<Dictionary<string, string>> kept)
    {
        var headers = new List<string>
        {
            "SourceSheet", "SourceRow",
            "Year", "Month", "YearMonth",
            "StationRaw", "StationID",
            "Latitude", "Longitude",
            "SampleDate", "SampleTime", "DateLabelMismatch",
        };
        headers.AddRange(Schema.StandardParameterNames);
        headers.Add("SymbolValue_Params");

        var rows = new List<IReadOnlyList<string>>();
        foreach (var k in kept)
        {
            var row = new List<string>();
            foreach (var h in headers)
                row.Add(k.TryGetValue(h, out var v) ? (v ?? "") : "");
            rows.Add(row);
        }

        TableWriter.WriteTable(StageBPath, headers, rows);
    }


    private static void WriteDuplicateReport(List<Dictionary<string, string>> kept)
    {
        var byKey = kept
            .GroupBy(r => $"{r["SourceSheet"]}|{r["YearMonth"]}|{r["StationID"]}")
            .Where(g => g.Count() > 1)
            .ToList();

        var rows = new List<IDictionary<string, string>>();

        foreach (var g in byKey)
        {
            rows.Add(new Dictionary<string, string>
            {
                ["Key"]        = g.Key,
                ["Count"]      = g.Count().ToString(CultureInfo.InvariantCulture),
                ["SourceRows"] = string.Join(" ; ",
                                    g.Select(r => r["SourceRow"]).Take(5)),
            });
        }

        TableWriter.WriteRows(DuplicateReportPath, rows);
        Console.WriteLine($"  Duplicate groups after exclusion: {byKey.Count}");
    }

    // 4C - Impossible Values checks
    public static void RunPhaseC()
    {
        Console.WriteLine();
        Console.WriteLine("*******************************************************");
        Console.WriteLine("======== 4C: Cleaner (Impossible Values Check) ========");

        if (!File.Exists(StageBPath))
        {
            Console.WriteLine($"Stage B file not found: {StageBPath}");
            return;
        }

        var stageB = LoadStageA(StageBPath); 
        Console.WriteLine($"  Loaded {stageB.Count} stage-B records.");

        var violations = new List<Dictionary<string, string>>();
        int rowsWithViolations = 0;

        foreach (var row in stageB)
        {
            bool rowHadViolation = false;

            // pH
            if (row.TryGetValue("pH", out var phRaw) &&
                !string.IsNullOrWhiteSpace(phRaw) &&
                double.TryParse(phRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ph))
            {
                if (ph < 0 || ph > 14)
                {
                    LogViolation(violations, row, "pH", phRaw, "pH must be between 0 and 14");
                    row["pH"] = "";
                    rowHadViolation = true;
                }
            }

            // Dissolved Oxygen (0-20 mg/L)
            if (row.TryGetValue("Dissolved Oxygen, mg/L", out var doRaw) &&
                !string.IsNullOrWhiteSpace(doRaw) &&
                double.TryParse(doRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doVal))
            {
                if (doVal < 0 || doVal > 20)
                {
                    LogViolation(violations, row, "Dissolved Oxygen, mg/L", doRaw,
                                 "DO must be between 0 and 20 mg/L");
                    row["Dissolved Oxygen, mg/L"] = "";
                    rowHadViolation = true;
                }
            }

            // temperature (0-45 C)
            if (row.TryGetValue("Temperature °C", out var tempRaw) &&
                !string.IsNullOrWhiteSpace(tempRaw) &&
                double.TryParse(tempRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var temp))
            {
                if (temp < 0 || temp > 45)
                {
                    LogViolation(violations, row, "Temperature °C", tempRaw,
                                 "Temperature must be between 0 and 45 °C");
                    row["Temperature °C"] = "";
                    rowHadViolation = true;
                }
            }

            // all other parameters for negatives
            foreach (var p in Schema.StandardParameterNames)
            {
                if (p == "pH" || p == "Dissolved Oxygen, mg/L" || p == "Temperature °C")
                    continue;

                if (!row.TryGetValue(p, out var raw) || string.IsNullOrWhiteSpace(raw))
                    continue;

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v < 0)
                {
                    LogViolation(violations, row, p, raw,
                                 "Concentration cannot be negative");
                    row[p] = "";
                    rowHadViolation = true;
                }
            }

            if (rowHadViolation) rowsWithViolations++;
        }

        Console.WriteLine($"  Rows with violations : {rowsWithViolations}");
        Console.WriteLine($"  Total violations     : {violations.Count}");

        if (violations.Count > 0)
        {
            foreach (var g in violations.GroupBy(v => v["Reason"]).OrderBy(g => g.Key))
                Console.WriteLine($"    {g.Key,-45}: {g.Count()}");
        }

        WriteStageC(stageB);
        TableWriter.WriteRows(ImpossibilityReportPath, violations);

        Console.WriteLine();
        Console.WriteLine($"  Wrote {StageCPath}");
        Console.WriteLine($"  Wrote {ImpossibilityReportPath}");
    }

    private static void LogViolation(
        List<Dictionary<string, string>> violations,
        Dictionary<string, string> row,
        string parameter,
        string value,
        string reason)
    {
        violations.Add(new Dictionary<string, string>
        {
            ["SourceSheet"] = row.TryGetValue("SourceSheet", out var s) ? s : "",
            ["SourceRow"]   = row.TryGetValue("SourceRow",   out var r) ? r : "",
            ["YearMonth"]   = row.TryGetValue("YearMonth",   out var y) ? y : "",
            ["StationID"]   = row.TryGetValue("StationID",   out var st) ? st : "",
            ["Parameter"]   = parameter,
            ["Value"]       = value,
            ["Reason"]      = reason,
        });
    }

    private static void WriteStageC(List<Dictionary<string, string>> rows)
    {
        var headers = new List<string>
        {
            "SourceSheet", "SourceRow",
            "Year", "Month", "YearMonth",
            "StationRaw", "StationID",
            "Latitude", "Longitude",
            "SampleDate", "SampleTime", "DateLabelMismatch",
        };
        headers.AddRange(Schema.StandardParameterNames);
        headers.Add("SymbolValue_Params");

        var table = new List<IReadOnlyList<string>>();
        foreach (var r in rows)
        {
            var line = new List<string>();
            foreach (var h in headers)
                line.Add(r.TryGetValue(h, out var v) ? (v ?? "") : "");
            table.Add(line);
        }

        TableWriter.WriteTable(StageCPath, headers, table);
    }

    public static void RunPhaseD()
    {
        Console.WriteLine();
        Console.WriteLine("*******************************************************");
        Console.WriteLine("========= 4D: Cleaner (IQR Outlier Flagging) ==========");
        if (!File.Exists(StageCPath))
        {
            Console.WriteLine($"Stage C file not found: {StageCPath}");
            return;
        }

        var records = LoadStageA(StageCPath);
        Console.WriteLine($"  Loaded {records.Count} stage-C records.");

        var fences = ComputeFences(records);    // n<8

        int totalFencesComputed = fences.Count(kv => kv.Value.HasValue);
        Console.WriteLine($"  Fence groups computed : {totalFencesComputed}");
        Console.WriteLine($"  Fence groups skipped  : {fences.Count - totalFencesComputed} (n < 8)");

        int rowsWithOutlier = 0;
        int outlierCells    = 0;

        foreach (var row in records)
        {
            var sheet = row["SourceSheet"];
            var paramsFlagged = new List<string>();

            foreach (var p in Schema.StandardParameterNames)
            {
                if (!row.TryGetValue(p, out var raw)) continue;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var key = $"{sheet}\u0001{p}";
                if (!fences.TryGetValue(key, out var fence) || fence == null) continue;

                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue;

                double compareValue = (p == Schema.ParamFecalColiform && value > 0)
                    ? Math.Log10(value)
                    : value;

                if (compareValue < fence.Value.lower || compareValue > fence.Value.upper)
                {
                    paramsFlagged.Add(p);
                    outlierCells++;
                }
            }

            row["Is_Outlier"]     = paramsFlagged.Count > 0 ? "TRUE" : "";
            row["Outlier_Params"] = string.Join("; ", paramsFlagged);
            if (paramsFlagged.Count > 0) rowsWithOutlier++;
        }

        Console.WriteLine($"  Rows with outliers    : {rowsWithOutlier}");
        Console.WriteLine($"  Outlier cells         : {outlierCells}");

        foreach (var row in records)
        {
            if (row.TryGetValue(Schema.ParamFecalColiform, out var fc) &&
                !string.IsNullOrWhiteSpace(fc) &&
                double.TryParse(fc, NumberStyles.Float, CultureInfo.InvariantCulture, out var fcVal) &&
                fcVal > 0)
            {
                row["FecalColiform_log10"] = Math.Log10(fcVal)
                    .ToString("F4", CultureInfo.InvariantCulture);
            }
            else
            {
                row["FecalColiform_log10"] = "";
            }
        }

        WriteStageD(records);
        WriteOutlierSummary(records);

        Console.WriteLine();
        Console.WriteLine($"  Wrote {StageDPath}");
        Console.WriteLine($"  Wrote {OutlierSummaryPath}");
    }

    // Fence
    private record struct Fence(double lower, double upper);

    private static Dictionary<string, Fence?> ComputeFences(
        List<Dictionary<string, string>> records)
    {
        var fences = new Dictionary<string, Fence?>();

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]))
        {
            var sheet = sheetGroup.Key;
            var list  = sheetGroup.ToList();

            foreach (var p in Schema.StandardParameterNames)
            {
                var values = new List<double>();

                foreach (var row in list)
                {
                    if (!row.TryGetValue(p, out var raw)) continue;
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    if (row.TryGetValue("SymbolValue_Params", out var sp) &&
                        !string.IsNullOrWhiteSpace(sp) &&
                        sp.Contains(p))
                        continue;

                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        values.Add(v);
                }

                var key = $"{sheet}\u0001{p}";

                if (values.Count < 8)
                {
                    fences[key] = null;
                    continue;
                }

                // log10 value of Fence for Fecal
                List<double> series = (p == Schema.ParamFecalColiform)
                    ? values.Where(v => v > 0).Select(Math.Log10).ToList()
                    : values;

                if (series.Count < 8)
                {
                    fences[key] = null;
                    continue;
                }

                series.Sort();
                double q1 = Quantile(series, 0.25);
                double q3 = Quantile(series, 0.75);
                double iqr = q3 - q1;

                fences[key] = new Fence(q1 - 1.5 * iqr, q3 + 1.5 * iqr);
            }
        }

        return fences;
    }

    private static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        double pos = (sorted.Count - 1) * q;
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];

        double frac = pos - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }

    private static void WriteStageD(List<Dictionary<string, string>> rows)
    {
        var headers = new List<string>
        {
            "SourceSheet", "SourceRow",
            "Year", "Month", "YearMonth",
            "StationRaw", "StationID",
            "Latitude", "Longitude",
            "SampleDate", "SampleTime", "DateLabelMismatch",
        };
        headers.AddRange(Schema.StandardParameterNames);
        headers.Add("FecalColiform_log10");
        headers.Add("SymbolValue_Params");
        headers.Add("Is_Outlier");
        headers.Add("Outlier_Params");

        var table = new List<IReadOnlyList<string>>();
        foreach (var r in rows)
        {
            var line = new List<string>();
            foreach (var h in headers)
                line.Add(r.TryGetValue(h, out var v) ? (v ?? "") : "");
            table.Add(line);
        }

        TableWriter.WriteTable(StageDPath, headers, table);
    }

    private static void WriteOutlierSummary(List<Dictionary<string, string>> records)
    {
        var rows = new List<IDictionary<string, string>>();

        var fences = ComputeFences(records); 

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]))
        {
            var sheet = sheetGroup.Key;
            var list  = sheetGroup.ToList();

            foreach (var p in Schema.StandardParameterNames)
            {
                var key = $"{sheet}\u0001{p}";
                fences.TryGetValue(key, out var fence);

                int nonNull = 0, flagged = 0;
                foreach (var row in list)
                {
                    if (!row.TryGetValue(p, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
                    nonNull++;

                    if (row.TryGetValue("Outlier_Params", out var op) &&
                        !string.IsNullOrWhiteSpace(op) &&
                        op.Contains(p))
                        flagged++;
                }

                rows.Add(new Dictionary<string, string>
                {
                    ["Sheet"]          = sheet,
                    ["Parameter"]      = p,
                    ["NonEmptyCount"]  = nonNull.ToString(CultureInfo.InvariantCulture),
                    ["FenceComputed"]  = fence.HasValue ? "YES" : "NO",
                    ["LowerFence"]     = fence.HasValue ? fence.Value.lower.ToString("R", CultureInfo.InvariantCulture) : "",
                    ["UpperFence"]     = fence.HasValue ? fence.Value.upper.ToString("R", CultureInfo.InvariantCulture) : "",
                    ["OutlierCount"]   = flagged.ToString(CultureInfo.InvariantCulture),
                    ["Scale"]          = p == Schema.ParamFecalColiform ? "log10" : "raw",
                });
            }
        }

        TableWriter.WriteRows(OutlierSummaryPath, rows);
    }

    // ---------- Shared helpers used by Phase A

    private static List<RawRecord> LoadRawRecords(string path)
    {
        var list = new List<RawRecord>();
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        csv.Read();
        csv.ReadHeader();

        while (csv.Read())
        {
            var rec = new RawRecord
            {
                SourceSheet       = csv.GetField("SourceSheet") ?? "",
                SourceRow         = int.TryParse(csv.GetField("SourceRow"), out var sr) ? sr : 0,
                YearMonthLabel    = csv.GetField("YearMonthLabel") ?? "",
                YearMonthLabelRow = int.TryParse(csv.GetField("YearMonthLabelRow"), out var lr) ? lr : 0,
                StationRaw        = csv.GetField("StationRaw") ?? "",
                Latitude          = csv.GetField("Latitude") ?? "",
                Longitude         = csv.GetField("Longitude") ?? "",
                DateText          = csv.GetField("DateText") ?? "",
                TimeText          = csv.GetField("TimeText") ?? "",
            };

            foreach (var p in Schema.StandardParameterNames)
                rec.Parameters[p] = csv.GetField(p) ?? "";

            list.Add(rec);
        }
        return list;
    }

    private static void EnsureManualCorrectionsFile()
    {
        if (File.Exists(ManualCorrectionsPath)) return;

        Console.WriteLine($"  Creating template: {ManualCorrectionsPath}");

        var headers = new[] { "Sheet", "SourceRow", "Field", "OldValue", "NewValue", "Reason" };
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "Marilao", "417", "YearMonthLabel", "CY 2016 AUGUST",    "CY 2017 AUGUST",    "Mislabeled year - station dates are Aug 2017" },
            new[] { "Marilao", "423", "YearMonthLabel", "CY 2016 SEPTEMBER", "CY 2017 SEPTEMBER", "Mislabeled year - station dates are Sep 2017" },
            new[] { "Marilao", "466", "YearMonthLabel", "CY 2017 MAY",       "CY 2018 MAY",       "Mislabeled year - station dates are May 2018" },
        };
        TableWriter.WriteTable(ManualCorrectionsPath, headers, rows);
    }

    private static List<Dictionary<string, string>> LoadManualCorrections(string path)
    {
        var list = new List<Dictionary<string, string>>();
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        while (csv.Read())
        {
            list.Add(new Dictionary<string, string>
            {
                ["Sheet"]     = csv.GetField("Sheet") ?? "",
                ["SourceRow"] = csv.GetField("SourceRow") ?? "",
                ["Field"]     = csv.GetField("Field") ?? "",
                ["OldValue"]  = csv.GetField("OldValue") ?? "",
                ["NewValue"]  = csv.GetField("NewValue") ?? "",
                ["Reason"]    = csv.GetField("Reason") ?? "",
            });
        }
        return list;
    }

    private static int ApplyCorrections(
        List<RawRecord> records,
        List<Dictionary<string, string>> corrections)
    {
        int correctionsApplied = 0;
        int rowsAffected       = 0;

        foreach (var c in corrections)
        {
            if (!int.TryParse(c["SourceRow"], out var rowNumber)) continue;

            var field    = c["Field"];
            var newValue = c["NewValue"];

            if (field == "YearMonthLabel")
            {
                var targets = records
                    .Where(r => r.SourceSheet.Equals(c["Sheet"], StringComparison.OrdinalIgnoreCase) &&
                                r.YearMonthLabelRow == rowNumber)
                    .ToList();

                foreach (var t in targets)
                    t.YearMonthLabel = newValue;

                if (targets.Count > 0) correctionsApplied++;
                rowsAffected += targets.Count;
            }
            else if (field == "DateText" || field == "TimeText")
            {
                var target = records.FirstOrDefault(r =>
                    r.SourceSheet.Equals(c["Sheet"], StringComparison.OrdinalIgnoreCase) &&
                    r.SourceRow == rowNumber);

                if (target != null)
                {
                    if (field == "DateText") target.DateText = newValue;
                    else                     target.TimeText = newValue;
                    correctionsApplied++;
                    rowsAffected++;
                }
            }
        }

        Console.WriteLine($"  Corrections applied       : {correctionsApplied} ({rowsAffected} rows affected)");
        return correctionsApplied;
    }

    private static StageAStats WriteStageA(List<RawRecord> records)
    {
        var headers = new List<string>
        {
            "SourceSheet", "SourceRow",
            "Year", "Month", "YearMonth",
            "StationRaw", "StationID",
            "Latitude", "Longitude",
            "SampleDate", "SampleTime", "DateLabelMismatch",
        };
        headers.AddRange(Schema.StandardParameterNames);
        headers.Add("SymbolValue_Params");

        var rows = new List<IReadOnlyList<string>>();
        int symbolCells  = 0;
        int mismatchCount = 0;
        var stationMap = BuildStationNumberMap(records);

        foreach (var rec in records)
        {
            var (labelOk, labelYear, labelMonth) = Parsers.TryParseMonthLabel(rec.YearMonthLabel);
            int year  = labelOk ? labelYear  : 0;
            int month = labelOk ? labelMonth : 0;
            string ym = (year > 0 && month > 0) ? $"{year:D4}-{month:D2}" : "";

            var (latOk, latVal, _) = Parsers.TryParseNumber(rec.Latitude);
            var (lonOk, lonVal, _) = Parsers.TryParseNumber(rec.Longitude);
            var (dateOk, dateVal)  = Parsers.TryParseDate(rec.DateText);
            var (timeOk, timeVal)  = Parsers.TryParseTime(rec.TimeText);

            bool mismatch = false;
            if (dateOk && year > 0 && month > 0)
                mismatch = (dateVal.Year != year) || (dateVal.Month != month);
            if (mismatch) mismatchCount++;

            var stationClean = Regex.Replace(rec.StationRaw, @"\s+", " ").Trim();
            var stationId    = stationMap.TryGetValue((rec.SourceSheet, stationClean), out var sid)
                                ? sid
                                : "";

            var paramValues  = new Dictionary<string, double?>();
            var symbolParams = new List<string>();

            foreach (var p in Schema.StandardParameterNames)
            {
                var rawText = rec.Parameters.TryGetValue(p, out var v) ? v : "";

                if (Parsers.IsNoSamplingText(rawText) || Parsers.IsPlaceholder(rawText))
                {
                    paramValues[p] = null;
                    continue;
                }

                var (ok, value, hadLimit) = Parsers.TryParseNumber(rawText);
                if (ok)
                {
                    paramValues[p] = value;
                    if (hadLimit)
                    {
                        symbolParams.Add(p);
                        symbolCells++;
                    }
                }
                else
                {
                    paramValues[p] = null;
                }
            }

            var row = new List<string>
            {
                rec.SourceSheet,
                rec.SourceRow.ToString(CultureInfo.InvariantCulture),
                year.ToString(CultureInfo.InvariantCulture),
                month.ToString(CultureInfo.InvariantCulture),
                ym,
                stationClean,
                stationId,
                latOk ? latVal.ToString("R", CultureInfo.InvariantCulture) : "",
                lonOk ? lonVal.ToString("R", CultureInfo.InvariantCulture) : "",
                dateOk ? dateVal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                timeOk ? timeVal.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "",
                mismatch ? "TRUE" : "",
            };

            foreach (var p in Schema.StandardParameterNames)
            {
                var val = paramValues[p];
                row.Add(val.HasValue
                    ? val.Value.ToString("R", CultureInfo.InvariantCulture)
                    : "");
            }

            row.Add(string.Join("; ", symbolParams));
            rows.Add(row);
        }

        TableWriter.WriteTable(StageAPath, headers, rows);
        return new StageAStats(symbolCells, mismatchCount);
    }

    private static Dictionary<(string, string), string> BuildStationNumberMap(
        List<RawRecord> records)
    {
        var map = new Dictionary<(string, string), string>();

        foreach (var sheetGroup in records.GroupBy(r => r.SourceSheet))
        {
            var sheet = sheetGroup.Key;

            var prefix = sheet switch
            {
                "Marilao"    => "MAR",
                "Meycauayan" => "MEY",
                "Obando"     => "OBA",
                _ => sheet.Substring(0, Math.Min(3, sheet.Length)).ToUpperInvariant()
            };

            var distinctStations = sheetGroup
                .Select(r => Regex.Replace(r.StationRaw, @"\s+", " ").Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < distinctStations.Count; i++)
            {
                map[(sheet, distinctStations[i])] = $"{prefix}-{i + 1}";
            }
        }

        return map;
    }
}