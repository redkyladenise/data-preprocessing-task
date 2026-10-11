using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Cleaner
{
    public const string InputPath             = Extractor.OutputPath;
    public const string ManualCorrectionsPath = "Data/interim/manual_corrections.csv";
    public const string StageAPath            = "Data/interim/clean_stage_a.csv";

    public static void Run()
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

    // container for two stats
    private record StageAStats(int SymbolCells, int Mismatches);

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
                // Label rows are not records — match on YearMonthLabelRow
                // and apply to every station row that inherited from it.
                var targets = records
                    .Where(r => r.SourceSheet.Equals(c["Sheet"], StringComparison.OrdinalIgnoreCase) &&
                                r.YearMonthLabelRow == rowNumber)
                    .ToList();

                foreach (var t in targets)
                    t.YearMonthLabel = newValue;

                if (targets.Count > 0) correctionsApplied++;
                rowsAffected += targets.Count;
            }
            else if (field == "DateText")
            {
                // Station rows are records — match on SourceRow.
                var target = records.FirstOrDefault(r =>
                    r.SourceSheet.Equals(c["Sheet"], StringComparison.OrdinalIgnoreCase) &&
                    r.SourceRow == rowNumber);

                if (target != null)
                {
                    target.DateText = newValue;
                    correctionsApplied++;
                    rowsAffected++;
                }
            }
            else if (field == "TimeText")
            {
                var target = records.FirstOrDefault(r =>
                    r.SourceSheet.Equals(c["Sheet"], StringComparison.OrdinalIgnoreCase) &&
                    r.SourceRow == rowNumber);

                if (target != null)
                {
                    target.TimeText = newValue;
                    correctionsApplied++;
                    rowsAffected++;
                }
            }
        }

        Console.WriteLine($"  Corrections applied       : {correctionsApplied} ({rowsAffected} rows affected)");
        return correctionsApplied;
    }

    // reads RawRecords, parses fields, writes stage_a.csv
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

        foreach (var rec in records)
        {
            var (labelOk, labelYear, labelMonth) = Parsers.TryParseMonthLabel(rec.YearMonthLabel);
            int year  = labelOk ? labelYear  : 0;
            int month = labelOk ? labelMonth : 0;
            string ym = (year > 0 && month > 0) ? $"{year:D4}-{month:D2}" : "";

            var (latOk, latVal, _) = Parsers.TryParseNumber(rec.Latitude);
            var (lonOk, lonVal, _) = Parsers.TryParseNumber(rec.Longitude);

            var (dateOk, dateVal) = Parsers.TryParseDate(rec.DateText);

            var (timeOk, timeVal) = Parsers.TryParseTime(rec.TimeText);

            bool mismatch = false;
            if (dateOk && year > 0 && month > 0)
                mismatch = (dateVal.Year != year) || (dateVal.Month != month);
            if (mismatch) mismatchCount++;

            var stationClean = Regex.Replace(rec.StationRaw, @"\s+", " ").Trim();
            var stationId    = BuildStationId(rec.SourceSheet, stationClean);

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

            // csv
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

    private static string BuildStationId(string sheet, string cleanStation)
    {
        string prefix = sheet switch
        {
            "Marilao"    => "MAR",
            "Meycauayan" => "MEY",
            "Obando"     => "OBA",
            _            => sheet.Substring(0, Math.Min(3, sheet.Length)).ToUpperInvariant(),
        };

        var words = cleanStation
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .Take(4)
            .Select(w => char.ToUpperInvariant(w[0]));

        return prefix + "-" + new string(words.ToArray());
    }
}