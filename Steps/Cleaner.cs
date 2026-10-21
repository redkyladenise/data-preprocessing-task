using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Cleaner
{
    public const string InputPath             = Extractor.OutputPath;
    public const string ManualCorrectionsPath = "Data/inputs/manual_corrections.csv";
    public const string FinalCleanedPath      = "Data/interim/data_before_imputation.csv";
    public const string ImpossibilityReportPath  = "Output/tables/cleaning/impossible_values.csv";
    public const string OutlierSummaryPath     = "Output/tables/cleaning/outlier_summary.csv";

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("=============== 04: Cleaner (All Phases) ==============");

        if (!File.Exists(InputPath))
        {
            Console.WriteLine($"Input not found: {InputPath}. Run Step 2 first.");
            return;
        }

        // 1. Load Raw Data
        var records = LoadRawRecords(InputPath);
        Console.WriteLine($"  Loaded {records.Count} raw records.");

        if (!File.Exists(ManualCorrectionsPath))
        {
            Console.WriteLine($"Manual corrections file not found: {ManualCorrectionsPath}");
            return;
        }

        var corrections = LoadManualCorrections(ManualCorrectionsPath);
        ApplyCorrections(records, corrections);

        // 2. Run Phases in Memory
        var stageA = RunPhaseA(records);
        var stageB = RunPhaseB(stageA);
        var stageC = RunPhaseC(stageB);
        var stageD = RunPhaseD(stageC);

        // 3. Write the single final cleaned dataset
        WriteFinalDataset(stageD);

        Console.WriteLine();
        Console.WriteLine($"  Wrote {FinalCleanedPath}");
    }

    private static List<Dictionary<string, string>> RunPhaseA(List<RawRecord> records)
    {
        Console.WriteLine();
        Console.WriteLine("========= 4A: Standardization and Corrections =========");
        var rows = new List<Dictionary<string, string>>();
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
            var stationId    = stationMap.TryGetValue((rec.SourceSheet, stationClean), out var sid) ? sid : "";

            var paramValues  = new Dictionary<string, string>();
            var symbolParams = new List<string>();

            foreach (var p in Schema.StandardParameterNames)
            {
                var rawText = rec.Parameters.TryGetValue(p, out var v) ? v : "";

                if (Parsers.IsNoSamplingText(rawText) || Parsers.IsPlaceholder(rawText))
                {
                    paramValues[p] = "";
                    continue;
                }

                var (ok, value, hadLimit) = Parsers.TryParseNumber(rawText);
                if (ok)
                {
                    paramValues[p] = value.ToString("R", CultureInfo.InvariantCulture);
                    if (hadLimit) { symbolParams.Add(p); symbolCells++; }
                }
                else
                {
                    paramValues[p] = "";
                }
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SourceSheet"] = rec.SourceSheet,
                ["SourceRow"] = rec.SourceRow.ToString(CultureInfo.InvariantCulture),
                ["Year"] = year.ToString(CultureInfo.InvariantCulture),
                ["Month"] = month.ToString(CultureInfo.InvariantCulture),
                ["YearMonth"] = ym,
                ["StationRaw"] = stationClean,
                ["StationID"] = stationId,
                ["Latitude"] = latOk ? latVal.ToString("R", CultureInfo.InvariantCulture) : "",
                ["Longitude"] = lonOk ? lonVal.ToString("R", CultureInfo.InvariantCulture) : "",
                ["SampleDate"] = dateOk ? dateVal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                ["SampleTime"] = timeOk ? timeVal.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "",
                ["DateLabelMismatch"] = mismatch ? "TRUE" : "",
                ["SymbolValue_Params"] = string.Join("; ", symbolParams)
            };

            foreach (var p in Schema.StandardParameterNames)
                row[p] = paramValues[p];

            rows.Add(row);
        }

        Console.WriteLine($"  Cells with symbol removed : {symbolCells}");
        Console.WriteLine($"  Date-label mismatches     : {mismatchCount}");
        return rows;
    }

    private static List<Dictionary<string, string>> RunPhaseB(List<Dictionary<string, string>> stageA)
    {
        Console.WriteLine();
        Console.WriteLine("========= 4B: Placeholders and Missing Dates ==========");

        int synthesizedDates = 0;

        foreach (var row in stageA)
        {
            var dateVal = row.TryGetValue("SampleDate", out var d) ? d : "";
            
            if (string.IsNullOrWhiteSpace(dateVal))
            {
                var year = row.TryGetValue("Year", out var y) ? y : "";
                var month = row.TryGetValue("Month", out var m) ? m : "";
                
                if (!string.IsNullOrWhiteSpace(year) && !string.IsNullOrWhiteSpace(month) && year != "0" && month != "0")
                {
                    row["SampleDate"] = $"{year}-{int.Parse(month):D2}-01";
                    synthesizedDates++;
                }
            }
        }

        Console.WriteLine($"  Synthesized missing Dates : {synthesizedDates}");
        Console.WriteLine($"  Retained rows             : {stageA.Count} (No rows excluded)");
        return stageA;
    }

    private static List<Dictionary<string, string>> RunPhaseC(List<Dictionary<string, string>> stageB)
    {
        Console.WriteLine();
        Console.WriteLine("=========== 4C: Checking Impossible Values ============");

        var violations = new List<Dictionary<string, string>>();
        int rowsWithViolations = 0;

        foreach (var row in stageB)
        {
            bool rowHadViolation = false;

            // pH
            if (row.TryGetValue("pH", out var phRaw) && !string.IsNullOrWhiteSpace(phRaw) &&
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
            if (row.TryGetValue("Dissolved Oxygen, mg/L", out var doRaw) && !string.IsNullOrWhiteSpace(doRaw) &&
                double.TryParse(doRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doVal))
            {
                if (doVal < 0 || doVal > 20)
                {
                    LogViolation(violations, row, "Dissolved Oxygen, mg/L", doRaw, "DO must be between 0 and 20 mg/L");
                    row["Dissolved Oxygen, mg/L"] = "";
                    rowHadViolation = true;
                }
            }

            // Temperature (0-45 C)
            if (row.TryGetValue("Temperature °C", out var tempRaw) && !string.IsNullOrWhiteSpace(tempRaw) &&
                double.TryParse(tempRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var temp))
            {
                if (temp < 0 || temp > 45)
                {
                    LogViolation(violations, row, "Temperature °C", tempRaw, "Temperature must be between 0 and 45 °C");
                    row["Temperature °C"] = "";
                    rowHadViolation = true;
                }
            }

            // all other parameters for negatives
            foreach (var p in Schema.StandardParameterNames)
            {
                if (p == "pH" || p == "Dissolved Oxygen, mg/L" || p == "Temperature °C") continue;
                if (!row.TryGetValue(p, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v < 0)
                {
                    LogViolation(violations, row, p, raw, "Concentration cannot be negative");
                    row[p] = "";
                    rowHadViolation = true;
                }
            }

            if (rowHadViolation) rowsWithViolations++;
        }

        Console.WriteLine($"  Rows with violations : {rowsWithViolations}");
        TableWriter.WriteRows(ImpossibilityReportPath, violations);
        return stageB;
    }

    private static List<Dictionary<string, string>> RunPhaseD(List<Dictionary<string, string>> stageC)
    {
        Console.WriteLine();
        Console.WriteLine("============= 4D: Outlier Flagging (IQR) ==============");

        var fences = ComputeFences(stageC);
        int rowsWithOutlier = 0;
        int outlierCells    = 0;

        foreach (var row in stageC)
        {
            var sheet = row["SourceSheet"];
            var paramsFlagged = new List<string>();

            foreach (var p in Schema.StandardParameterNames)
            {
                if (!row.TryGetValue(p, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;

                var key = $"{sheet}\u0001{p}";
                if (!fences.TryGetValue(key, out var fence) || fence == null) continue;

                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue;

                if (value < fence.Value.lower || value > fence.Value.upper)
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

        WriteOutlierSummary(stageC, fences);
        return stageC;
    }

    // --- helpers
    private static void WriteFinalDataset(List<Dictionary<string, string>> rows)
    {
        var headers = new List<string>
        {
            "SourceSheet", "SourceRow", "Year", "Month", "YearMonth",
            "StationRaw", "StationID", "Latitude", "Longitude",
            "SampleDate", "SampleTime", "DateLabelMismatch"
        };
        headers.AddRange(Schema.StandardParameterNames);
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

        TableWriter.WriteTable(FinalCleanedPath, headers, table);
    }

    private static void WriteOutlierSummary(List<Dictionary<string, string>> rows, Dictionary<string, Fence?> fences)
    {
        var summaryRows = new List<IDictionary<string, string>>();

        foreach (var sheetGroup in rows.GroupBy(r => r["SourceSheet"]))
        {
            var sheet = sheetGroup.Key;
            var list  = sheetGroup.ToList();

            foreach (var p in Schema.StandardParameterNames)
            {
                var key = $"{sheet}\u0001{p}";
                fences.TryGetValue(key, out var fence);

                int nonNull = list.Count(r => !string.IsNullOrWhiteSpace(r.TryGetValue(p, out var raw) ? raw : ""));
                int flagged = list.Count(r => r.TryGetValue("Outlier_Params", out var op) && !string.IsNullOrWhiteSpace(op) && op.Contains(p));

                summaryRows.Add(new Dictionary<string, string>
                {
                    ["Sheet"]          = sheet,
                    ["Parameter"]      = p,
                    ["NonEmptyCount"]  = nonNull.ToString(CultureInfo.InvariantCulture),
                    ["FenceComputed"]  = fence.HasValue ? "YES" : "NO",
                    ["LowerFence"]     = fence.HasValue ? fence.Value.lower.ToString("R", CultureInfo.InvariantCulture) : "",
                    ["UpperFence"]     = fence.HasValue ? fence.Value.upper.ToString("R", CultureInfo.InvariantCulture) : "",
                    ["OutlierCount"]   = flagged.ToString(CultureInfo.InvariantCulture),
                    ["Scale"]          = "raw"
                });
            }
        }
        TableWriter.WriteRows(OutlierSummaryPath, summaryRows);
    }

    private record struct Fence(double lower, double upper);

    private static Dictionary<string, Fence?> ComputeFences(List<Dictionary<string, string>> rows)
    {
        var fences = new Dictionary<string, Fence?>();
        foreach (var sheetGroup in rows.GroupBy(r => r["SourceSheet"]))
        {
            var sheet = sheetGroup.Key;
            var list  = sheetGroup.ToList();

            foreach (var p in Schema.StandardParameterNames)
            {
                var values = new List<double>();
                foreach (var row in list)
                {
                    if (!row.TryGetValue(p, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
                    if (row.TryGetValue("SymbolValue_Params", out var sp) && !string.IsNullOrWhiteSpace(sp) && sp.Contains(p)) continue;

                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        values.Add(v);
                }

                var key = $"{sheet}\u0001{p}";
                if (values.Count < 8) { fences[key] = null; continue; }

                values.Sort();
                double q1 = Quantile(values, 0.25);
                double q3 = Quantile(values, 0.75);
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

    private static void LogViolation(List<Dictionary<string, string>> violations, Dictionary<string, string> row, string parameter, string value, string reason)
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

    private static List<RawRecord> LoadRawRecords(string path)
    {
        var list = new List<RawRecord>();
        var content = File.ReadAllText(path).TrimStart('\uFEFF');
        using var reader = new StringReader(content);
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

    private static List<Dictionary<string, string>> LoadManualCorrections(string path)
    {
        var content = File.ReadAllText(path).TrimStart('\uFEFF');
        var list = new List<Dictionary<string, string>>();
        using var reader = new StringReader(content);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();

        while (csv.Read())
        {
            list.Add(new Dictionary<string, string>
            {
                ["Sheet"]     = csv.GetField("Sheet")     ?? "",
                ["SourceRow"] = csv.GetField("SourceRow") ?? "",
                ["Field"]     = csv.GetField("Field")     ?? "",
                ["OldValue"]  = csv.GetField("OldValue")  ?? "",
                ["NewValue"]  = csv.GetField("NewValue")  ?? "",
                ["Reason"]    = csv.GetField("Reason")    ?? "",
            });
        }
        return list;
    }

    private static void ApplyCorrections(List<RawRecord> records, List<Dictionary<string, string>> corrections)
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
                var targets = records.Where(r => r.SourceSheet.Equals(c["Sheet"], StringComparison.OrdinalIgnoreCase) && r.YearMonthLabelRow == rowNumber).ToList();
                foreach (var t in targets) t.YearMonthLabel = newValue;
                if (targets.Count > 0) correctionsApplied++;
                rowsAffected += targets.Count;
            }
            else if (field == "DateText" || field == "TimeText")
            {
                var target = records.FirstOrDefault(r => r.SourceSheet.Equals(c["Sheet"], StringComparison.OrdinalIgnoreCase) && r.SourceRow == rowNumber);
                if (target != null)
                {
                    if (field == "DateText") target.DateText = newValue;
                    else                     target.TimeText = newValue;
                    correctionsApplied++;
                    rowsAffected++;
                }
            }
        }
        Console.WriteLine($"  Manual corrections applied: {correctionsApplied} ({rowsAffected} rows affected)");
    }

    private static Dictionary<(string, string), string> BuildStationNumberMap(List<RawRecord> records)
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
                map[(sheet, distinctStations[i])] = $"{prefix}-{i + 1}";
        }
        return map;
    }
}