using System.Globalization;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Imputer
{
    public const string InputPath       = Cleaner.StageDPath;
    public const string StageEPath      = "Data/interim/clean_stage_e.csv";
    public const string ImputedLogPath  = "Output/tables/imputation/imputed_cells.csv";
    public const string ImpactPath      = "Output/tables/imputation/imputation_impact.csv";

    private static readonly HashSet<string> SkippedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ammonia, mg/L",
        "Total Coliform, MPN/100mL",
    };

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("===== 06: Imputer (Fill Isolated Missing Values) ======");


        if (!File.Exists(InputPath))
        {
            Console.WriteLine($"Input not found: {InputPath}. Run Step 4D first.");
            return;
        }

        var records = LoadStageD(InputPath);
        Console.WriteLine($"  Loaded {records.Count} stage-D records.");

        var byStation = records
            .GroupBy(r => $"{r["SourceSheet"]}\u0001{r["StationID"]}")
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r["YearMonth"]).ToList());

        var imputedCells = new List<IDictionary<string, string>>();
        var beforeStats = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var afterStats  = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Schema.StandardParameterNames)
        {
            if (SkippedParameters.Contains(p)) continue;
            beforeStats[p] = new List<double>();
            afterStats[p]  = new List<double>();
        }

        foreach (var rec in records)
        {
            foreach (var p in Schema.StandardParameterNames)
            {
                if (SkippedParameters.Contains(p)) continue;
                var v = rec.TryGetValue(p, out var raw) ? raw : "";
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    beforeStats[p].Add(d);
            }
        }

        int totalImputed = 0;

        foreach (var stationGroup in byStation.Values)
        {
            var byYM = stationGroup.ToDictionary(r => r["YearMonth"], r => r);

            foreach (var row in stationGroup)
            {
                var ym = row["YearMonth"];
                if (string.IsNullOrWhiteSpace(ym)) continue;

                string? prevYM = ShiftYearMonth(ym, -1);
                string? nextYM = ShiftYearMonth(ym, +1);

                var imputedParamsForRow = new List<string>();

                foreach (var p in Schema.StandardParameterNames)
                {
                    if (SkippedParameters.Contains(p)) continue;

                    // missing?
                    var cur = row.TryGetValue(p, out var cv) ? cv : "";
                    if (!string.IsNullOrWhiteSpace(cur)) continue;

                    // neighbords has values (same station)?
                    if (prevYM == null || nextYM == null) continue;
                    if (!byYM.TryGetValue(prevYM, out var prevRow)) continue;
                    if (!byYM.TryGetValue(nextYM, out var nextRow)) continue;

                    if (!TryGetValidNeighbor(prevRow, p, out var prevVal)) continue;
                    if (!TryGetValidNeighbor(nextRow, p, out var nextVal)) continue;

                    double imputed;
                    if (p == Schema.ParamFecalColiform)
                    {
                        if (prevVal <= 0 || nextVal <= 0) continue;
                        imputed = Math.Pow(10, (Math.Log10(prevVal) + Math.Log10(nextVal)) / 2.0);
                    }
                    else
                    {
                        imputed = (prevVal + nextVal) / 2.0;
                    }

                    // apply imputation
                    row[p] = imputed.ToString("R", CultureInfo.InvariantCulture);
                    imputedParamsForRow.Add(p);
                    totalImputed++;

                    imputedCells.Add(new Dictionary<string, string>
                    {
                        ["SourceSheet"]  = row["SourceSheet"],
                        ["StationID"]    = row["StationID"],
                        ["YearMonth"]    = ym,
                        ["Parameter"]    = p,
                        ["PrevYearMonth"]= prevYM,
                        ["PrevValue"]    = prevVal.ToString("R", CultureInfo.InvariantCulture),
                        ["NextYearMonth"]= nextYM,
                        ["NextValue"]    = nextVal.ToString("R", CultureInfo.InvariantCulture),
                        ["ImputedValue"] = imputed.ToString("R", CultureInfo.InvariantCulture),
                    });
                }

                var existing = row.TryGetValue("Imputed_Params", out var ip) ? ip : "";
                var merged = new List<string>();
                if (!string.IsNullOrWhiteSpace(existing))
                    merged.AddRange(existing.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                merged.AddRange(imputedParamsForRow);

                row["Imputed_Params"] = string.Join("; ", merged.Distinct());
            }
        }

        Console.WriteLine($"  Total cells imputed   : {totalImputed}");

        foreach (var rec in records)
        {
            foreach (var p in Schema.StandardParameterNames)
            {
                if (SkippedParameters.Contains(p)) continue;
                var v = rec.TryGetValue(p, out var raw) ? raw : "";
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    afterStats[p].Add(d);
            }
        }

        var imputedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in imputedCells)
        {
            var p = cell["Parameter"];
            imputedCounts[p] = imputedCounts.TryGetValue(p, out var c) ? c + 1 : 1;
        }

        WriteStageE(records);
        TableWriter.WriteRows(ImputedLogPath, imputedCells);
        WriteImpact(beforeStats, afterStats, imputedCounts);

        Console.WriteLine($"  Wrote {StageEPath}");
        Console.WriteLine($"  Wrote {ImputedLogPath}");
        Console.WriteLine($"  Wrote {ImpactPath}");
    }

    private static List<Dictionary<string, string>> LoadStageD(string path)
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

    private static bool TryGetValidNeighbor(
        Dictionary<string, string> neighbor,
        string parameter,
        out double value)
    {
        value = 0;

        var raw = neighbor.TryGetValue(parameter, out var v) ? v : "";
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        if (neighbor.TryGetValue("SymbolValue_Params", out var sp) &&
            !string.IsNullOrWhiteSpace(sp) &&
            sp.Contains(parameter))
            return false;

        if (neighbor.TryGetValue("Outlier_Params", out var op) &&
            !string.IsNullOrWhiteSpace(op) &&
            op.Contains(parameter))
            return false;

        return true;
    }
    private static string? ShiftYearMonth(string ym, int deltaMonths)
    {
        if (string.IsNullOrWhiteSpace(ym) || ym.Length != 7) return null;
        if (!int.TryParse(ym.Substring(0, 4), out var y)) return null;
        if (!int.TryParse(ym.Substring(5, 2), out var m)) return null;

        m += deltaMonths;
        while (m < 1)  { m += 12; y--; }
        while (m > 12) { m -= 12; y++; }

        if (y < 2012 || y > 2018) return null;

        return $"{y:D4}-{m:D2}";
    }

    private static void WriteStageE(List<Dictionary<string, string>> rows)
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
        headers.Add("Imputed_Params");

        var table = new List<IReadOnlyList<string>>();
        foreach (var r in rows)
        {
            var line = new List<string>();
            foreach (var h in headers)
                line.Add(r.TryGetValue(h, out var v) ? (v ?? "") : "");
            table.Add(line);
        }

        TableWriter.WriteTable(StageEPath, headers, table);
    }

    private static void WriteImpact(
        Dictionary<string, List<double>> before,
        Dictionary<string, List<double>> after,
        Dictionary<string, int> imputedCounts)
    {
        var rows = new List<IDictionary<string, string>>();

        foreach (var p in Schema.StandardParameterNames)
        {
            if (!before.TryGetValue(p, out var b) || !after.TryGetValue(p, out var a))
                continue;

            int imputed = imputedCounts.TryGetValue(p, out var c) ? c : 0;

            double bMean   = b.Count > 0 ? b.Average() : 0;
            double aMean   = a.Count > 0 ? a.Average() : 0;
            double bMedian = Median(b);
            double aMedian = Median(a);

            Console.WriteLine(
                $"  {p,-40} | " +
                $"imputed: {imputed,3} | " +
                $"n: {b.Count,4} -> {a.Count,4} | " +
                $"mean: {bMean,10:F4} -> {aMean,10:F4} | " +
                $"median: {bMedian,10:F4} -> {aMedian,10:F4}");

            rows.Add(new Dictionary<string, string>
            {
                ["Parameter"]     = p,
                ["Imputed"]       = imputed.ToString(CultureInfo.InvariantCulture),
                ["CountBefore"]   = b.Count.ToString(CultureInfo.InvariantCulture),
                ["CountAfter"]    = a.Count.ToString(CultureInfo.InvariantCulture),
                ["MeanBefore"]    = bMean.ToString("F4", CultureInfo.InvariantCulture),
                ["MeanAfter"]     = aMean.ToString("F4", CultureInfo.InvariantCulture),
                ["MedianBefore"]  = bMedian.ToString("F4", CultureInfo.InvariantCulture),
                ["MedianAfter"]   = aMedian.ToString("F4", CultureInfo.InvariantCulture),
            });
        }

        TableWriter.WriteRows(ImpactPath, rows);
    }
    

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return (n % 2 == 1)
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}