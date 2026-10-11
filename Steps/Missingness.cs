using System.Globalization;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Missingness
{
    public const string InputPath  = Cleaner.StageDPath;
    public const string OutputDir  = "Output/tables/missingness";

    // Ammonia and Total Coliform are not included as primary params
    private static readonly HashSet<string> DaoPrimaryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dissolved Oxygen, mg/L",
        "pH",
        "Temperature °C",
        "Biochemical Oxygen Demand, mg/L",
        "Total Suspended Solids, mg/L",
        "Color TCU",
        "Chlorides Cl, mg/L",
        "Phosphates as Phosphorous, mg/L",
        "Nitrates as Nitrogen, mg/L",
        "Fecal Coliform, MPN/100mL",
    };

    // to drop
    private static readonly HashSet<string> DomainDrop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ammonia, mg/L",
        "Total Coliform, MPN/100mL",
    };

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("=============== 05: Missingness Analysis ==============");

        if (!File.Exists(InputPath))
        {
            Console.WriteLine($"Input not found: {InputPath}. Run Step 4D first.");
            return;
        }

        var records = Load(InputPath);
        Console.WriteLine($"  Loaded {records.Count} stage-D records.");

        WriteMissingByParameter(records);
        WriteMissingByParameterYear(records);
        WriteMonitoredYears(records);
        WriteMissingWithinMonitoredYears(records);
        var threshold = WriteGapAnalysis(records);
        WriteThresholdDecision(records, threshold);

        Console.WriteLine();
        Console.WriteLine($"Wrote 6 files to {OutputDir}/");
    }

    private static List<Dictionary<string, string>> Load(string path)
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

    // % missing
    private static void WriteMissingByParameter(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Missing % per parameter (overall) ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]).OrderBy(g => g.Key))
        {
            var sheet = sheetGroup.Key;
            var list  = sheetGroup.ToList();
            int total = list.Count;

            foreach (var p in Schema.StandardParameterNames)
            {
                int missing = list.Count(r =>
                    string.IsNullOrWhiteSpace(r.TryGetValue(p, out var v) ? v : ""));

                double pct = total > 0 ? missing * 100.0 / total : 0;

                rows.Add(new Dictionary<string, string>
                {
                    ["Sheet"]          = sheet,
                    ["Parameter"]      = p,
                    ["MissingCount"]   = missing.ToString(CultureInfo.InvariantCulture),
                    ["TotalCount"]     = total.ToString(CultureInfo.InvariantCulture),
                    ["MissingPercent"] = pct.ToString("F2", CultureInfo.InvariantCulture),
                });
            }
        }

        TableWriter.WriteRows($"{OutputDir}/01_missing_by_parameter.csv", rows);
    }

    private static void WriteMissingByParameterYear(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Missing % per parameter per year ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]).OrderBy(g => g.Key))
        {
            var sheet = sheetGroup.Key;

            foreach (var yearGroup in sheetGroup.GroupBy(r => r["Year"]).OrderBy(g => g.Key))
            {
                var year  = yearGroup.Key;
                var list  = yearGroup.ToList();
                int total = list.Count;
                if (total == 0) continue;

                foreach (var p in Schema.StandardParameterNames)
                {
                    int missing = list.Count(r =>
                        string.IsNullOrWhiteSpace(r.TryGetValue(p, out var v) ? v : ""));

                    double pct = missing * 100.0 / total;

                    rows.Add(new Dictionary<string, string>
                    {
                        ["Sheet"]          = sheet,
                        ["Year"]           = year,
                        ["Parameter"]      = p,
                        ["MissingCount"]   = missing.ToString(CultureInfo.InvariantCulture),
                        ["TotalCount"]     = total.ToString(CultureInfo.InvariantCulture),
                        ["MissingPercent"] = pct.ToString("F2", CultureInfo.InvariantCulture),
                    });
                }
            }
        }

        TableWriter.WriteRows($"{OutputDir}/02_missing_by_parameter_year.csv", rows);
    }

    private static void WriteMonitoredYears(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Monitored years per parameter ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]).OrderBy(g => g.Key))
        {
            var sheet = sheetGroup.Key;

            foreach (var p in Schema.StandardParameterNames)
            {
                var byYear = sheetGroup
                    .GroupBy(r => r["Year"])
                    .OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        int total = g.Count();
                        int missing = g.Count(r => string.IsNullOrWhiteSpace(r.TryGetValue(p, out var v) ? v : ""));
                        return new { Year = g.Key, Total = total, Missing = missing };
                    })
                    .Where(x => x.Total > 0)
                    .ToList();

                var monitored = byYear.Where(x => x.Missing < x.Total).Select(x => x.Year).ToList();

                string first = monitored.FirstOrDefault() ?? "";
                string last  = monitored.LastOrDefault()  ?? "";
                string list  = string.Join(",", monitored);

                rows.Add(new Dictionary<string, string>
                {
                    ["Sheet"]          = sheet,
                    ["Parameter"]      = p,
                    ["FirstYear"]      = first,
                    ["LastYear"]       = last,
                    ["MonitoredYears"] = list,
                    ["MonitoredCount"] = monitored.Count.ToString(CultureInfo.InvariantCulture),
                });
            }
        }

        TableWriter.WriteRows($"{OutputDir}/03_monitored_years.csv", rows);
    }

    // missing % within monitored years only
    private static void WriteMissingWithinMonitoredYears(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Missing % within monitored years ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]).OrderBy(g => g.Key))
        {
            var sheet = sheetGroup.Key;

            foreach (var p in Schema.StandardParameterNames)
            {
                var monitoredYears = sheetGroup
                    .GroupBy(r => r["Year"])
                    .Where(g => g.Any(r =>
                        !string.IsNullOrWhiteSpace(r.TryGetValue(p, out var v) ? v : "")))
                    .Select(g => g.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var inWindow = sheetGroup
                    .Where(r => monitoredYears.Contains(r["Year"]))
                    .ToList();

                int total = inWindow.Count;
                int missing = inWindow.Count(r =>
                    string.IsNullOrWhiteSpace(r.TryGetValue(p, out var v) ? v : ""));
                double pct = total > 0 ? missing * 100.0 / total : 0;

                rows.Add(new Dictionary<string, string>
                {
                    ["Sheet"]                 = sheet,
                    ["Parameter"]             = p,
                    ["MonitoredYears"]        = string.Join(",", monitoredYears.OrderBy(y => y)),
                    ["RowsInWindow"]          = total.ToString(CultureInfo.InvariantCulture),
                    ["MissingWithinWindow"]   = missing.ToString(CultureInfo.InvariantCulture),
                    ["MissingPercentInWindow"]= pct.ToString("F2", CultureInfo.InvariantCulture),
                });
            }
        }

        TableWriter.WriteRows($"{OutputDir}/04_missing_within_monitored_years.csv", rows);
    }

    private static double WriteGapAnalysis(List<Dictionary<string, string>> records)
    {
        Console.WriteLine();
        Console.WriteLine("--- Gap analysis (sorted by average missing %) ---");

        // avg missing % per parameter across the three sheets
        var perParam = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]))
        {
            var list = sheetGroup.ToList();
            int total = list.Count;
            if (total == 0) continue;

            foreach (var p in Schema.StandardParameterNames)
            {
                int missing = list.Count(r => string.IsNullOrWhiteSpace(r.TryGetValue(p, out var v) ? v : ""));
                double pct = missing * 100.0 / total;

                if (!perParam.TryGetValue(p, out var l))
                {
                    l = new List<double>();
                    perParam[p] = l;
                }
                l.Add(pct);
            }
        }

        var sorted = perParam
            .Select(kv => new
            {
                Parameter = kv.Key,
                AvgMissing = kv.Value.Average(),
                InDao      = DaoPrimaryParameters.Contains(kv.Key),
                DomainDrop = DomainDrop.Contains(kv.Key),
            })
            .OrderByDescending(x => x.AvgMissing)
            .ToList();

        var rows = new List<IDictionary<string, string>>();
        double largestGap = 0;
        int    gapIndex   = -1;

        for (int i = 0; i < sorted.Count; i++)
        {
            double gap = (i > 0) ? sorted[i - 1].AvgMissing - sorted[i].AvgMissing : 0;
            if (i > 0 && gap > largestGap)
            {
                largestGap = gap;
                gapIndex   = i;
            }

            rows.Add(new Dictionary<string, string>
            {
                ["Rank"]              = (i + 1).ToString(CultureInfo.InvariantCulture),
                ["Parameter"]         = sorted[i].Parameter,
                ["AvgMissingPercent"] = sorted[i].AvgMissing.ToString("F2", CultureInfo.InvariantCulture),
                ["GapFromPrev"]       = gap.ToString("F2", CultureInfo.InvariantCulture),
                ["InDaoPrimary"]      = sorted[i].InDao ? "YES" : "NO",
                ["DomainDrop"]        = sorted[i].DomainDrop ? "YES" : "NO",
            });
        }

        double threshold;
        if (gapIndex > 0)
        {
            double upperSide = sorted[gapIndex - 1].AvgMissing;
            double lowerSide = sorted[gapIndex].AvgMissing;
            threshold = (upperSide + lowerSide) / 2.0;

            Console.WriteLine($"  Largest gap: {sorted[gapIndex - 1].Parameter} " +
                              $"({upperSide:F2}%) -> {sorted[gapIndex].Parameter} " +
                              $"({lowerSide:F2}%) = {largestGap:F2} pp");
            Console.WriteLine($"  Proposed threshold: {threshold:F2}%");
        }
        else
        {
            threshold = 50.0;
            Console.WriteLine("  No clear gap detected; defaulting to 50%.");
        }

        TableWriter.WriteRows($"{OutputDir}/05_gap_analysis.csv", rows);
        return threshold;
    }

    // threshold
    private static void WriteThresholdDecision(
        List<Dictionary<string, string>> records,
        double threshold)
    {
        Console.WriteLine();
        Console.WriteLine("--- Threshold decision ---");
        Console.WriteLine($"  Threshold: {threshold:F2}%");

        var perParam = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]))
        {
            var list  = sheetGroup.ToList();
            int total = list.Count;
            if (total == 0) continue;

            foreach (var p in Schema.StandardParameterNames)
            {
                int missing = list.Count(r =>
                    string.IsNullOrWhiteSpace(r.TryGetValue(p, out var v) ? v : ""));
                double pct = missing * 100.0 / total;

                if (!perParam.TryGetValue(p, out var l))
                {
                    l = new List<double>();
                    perParam[p] = l;
                }
                l.Add(pct);
            }
        }

        var rows = new List<IDictionary<string, string>>();

        foreach (var p in Schema.StandardParameterNames)
        {
            perParam.TryGetValue(p, out var values);
            double miss = values != null && values.Count > 0 ? values.Average() : 0;

            bool over   = miss > threshold;
            bool inDao  = DaoPrimaryParameters.Contains(p);
            bool forced = DomainDrop.Contains(p);

            string decision;
            string reason;

            if (forced)
            {
                decision = "DROP";
                reason   = "Domain override: parameter is not on the DAO 2016-08 primary parameter list; excluded regardless of threshold.";
            }
            else if (!over)
            {
                decision = "KEEP";
                reason   = "Missing % is below the threshold.";
            }
            else if (inDao)
            {
                decision = "KEEP_PERIOD_LIMITED";
                reason   = "Above threshold but is a DAO primary parameter; retained with period-limited coverage.";
            }
            else
            {
                decision = "DROP";
                reason   = "Above threshold and not a DAO primary parameter.";
            }

            Console.WriteLine($"  {p,-40} | {miss,6:F2}% | {decision}");

            rows.Add(new Dictionary<string, string>
            {
                ["Parameter"]      = p,
                ["AvgMissing"]     = miss.ToString("F2", CultureInfo.InvariantCulture),
                ["AboveThreshold"] = over ? "YES" : "NO",
                ["InDaoPrimary"]   = inDao ? "YES" : "NO",
                ["Decision"]       = decision,
                ["Reason"]         = reason,
            });
        }

        TableWriter.WriteRows($"{OutputDir}/06_threshold_decision.csv", rows);
    }
}