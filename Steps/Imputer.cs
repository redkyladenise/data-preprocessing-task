using System.Globalization;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Imputer
{
    public const string InputPath  = "Data/interim/data_before_imputation.csv";
    public const string OutputPath = "Data/interim/data_after_imputation.csv";
    public const string ImpactPath = "Output/tables/imputation/imputation_impact.csv";

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("========= 05: Imputer (Statistical Imputation) ========");

        if (!File.Exists(InputPath))
        {
            Console.WriteLine($"ERROR: Input not found: {InputPath}");
            return;
        }

        var records = LoadCsv(InputPath);
        Console.WriteLine($"  Loaded {records.Count} records.");

        // data before iputation
        var beforeStats = CalculateImpactStats(records);

        int imputedCount = 0;
        int outliersReplaced = 0;
        int timesImputed = 0;

        foreach (var sheetGroup in records.GroupBy(r => r["SourceSheet"]))
        {
            var list = sheetGroup.ToList();

            // stats per params (outlier excluded)
            var paramStats = new Dictionary<string, (double mean, double median, double mode)>();
            foreach (var p in Schema.StandardParameterNames)
            {
                var validValues = list.Where(r => !string.IsNullOrWhiteSpace(r[p]) && 
                                                  !r["Outlier_Params"].Contains(p))
                                      .Select(r => double.Parse(r[p], CultureInfo.InvariantCulture))
                                      .ToList();

                if (validValues.Count == 0) continue;

                double mean = validValues.Average();
                validValues.Sort();
                double median = GetMedian(validValues);
                double mode = GetMode(validValues);

                paramStats[p] = (mean, median, mode);
            }

            // mode for time
            var stationTimeModes = new Dictionary<string, string>();
            foreach (var stationGroup in list.GroupBy(r => r["StationID"]))
            {
                var times = stationGroup.Where(r => !string.IsNullOrWhiteSpace(r["SampleTime"]))
                                        .Select(r => r["SampleTime"]).ToList();
                if (times.Count > 0)
                {
                    stationTimeModes[stationGroup.Key] = times.GroupBy(t => t)
                                                              .OrderByDescending(g => g.Count())
                                                              .First().Key;
                }
            }

            // imputation (median for outliers)
            foreach (var row in list)
            {
                var imputedParamsForRow = new List<string>();
                var outlierParams = row["Outlier_Params"].Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var op in outlierParams)
                {
                    var p = op.Trim();
                    if (paramStats.TryGetValue(p, out var stats))
                    {
                        int decimals = Schema.ImputedDecimalPlaces.TryGetValue(p, out var d) ? d : 2;
                        row[p] = Math.Round(stats.median, decimals).ToString("R", CultureInfo.InvariantCulture);
                        
                        imputedParamsForRow.Add(p);
                        outliersReplaced++;
                    }
                }

                foreach (var p in Schema.StandardParameterNames)
                {
                    if (string.IsNullOrWhiteSpace(row[p]) && paramStats.TryGetValue(p, out var stats))
                    {
                        double fillValue;
                        if (p == "Color TCU") 
                            fillValue = stats.mode;
                        else 
                            fillValue = stats.median;

                        int decimals = Schema.ImputedDecimalPlaces.TryGetValue(p, out var d) ? d : 2;
                        row[p] = Math.Round(fillValue, decimals).ToString("R", CultureInfo.InvariantCulture);
                        
                        imputedParamsForRow.Add(p);
                        imputedCount++;
                    }
                }

                if (string.IsNullOrWhiteSpace(row["SampleTime"]) && stationTimeModes.TryGetValue(row["StationID"], out var modeTime))
                {
                    row["SampleTime"] = modeTime;
                    timesImputed++;
                }

                row["Imputed_Params"] = string.Join("; ", imputedParamsForRow.Distinct());
            }
        }

        Console.WriteLine($"  Missing parameters filled : {imputedCount}");
        Console.WriteLine($"  Outliers replaced         : {outliersReplaced}");
        Console.WriteLine($"  Missing times filled      : {timesImputed}");

        WriteDataset(records);
        WriteImpactReport(beforeStats, CalculateImpactStats(records));

        Console.WriteLine($"=== 05 Complete. Wrote {OutputPath}");
        Console.WriteLine($"                       {ImpactPath}");
    }

    // -- helpers
    private static double GetMedian(List<double> sorted)
    {
        int n = sorted.Count;
        return (n % 2 == 1) ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static double GetMode(List<double> values)
    {
        return values.GroupBy(v => v)
                     .OrderByDescending(g => g.Count())
                     .First().Key;
    }

    private static Dictionary<string, (double mean, double median)> CalculateImpactStats(List<Dictionary<string, string>> records)
    {
        var stats = new Dictionary<string, (double, double)>();
        foreach (var p in Schema.StandardParameterNames)
        {
            var vals = records.Where(r => !string.IsNullOrWhiteSpace(r[p]))
                              .Select(r => double.Parse(r[p], CultureInfo.InvariantCulture))
                              .ToList();
            if (vals.Count > 0)
            {
                double mean = vals.Average();
                vals.Sort();
                double median = GetMedian(vals);
                stats[p] = (mean, median);
            }
            else
            {
                stats[p] = (0, 0);
            }
        }
        return stats;
    }

    private static void WriteImpactReport(
        Dictionary<string, (double mean, double median)> before,
        Dictionary<string, (double mean, double median)> after)
    {
        var rows = new List<IDictionary<string, string>>();
        foreach (var p in Schema.StandardParameterNames)
        {
            rows.Add(new Dictionary<string, string>
            {
                ["Parameter"] = p,
                ["MeanBefore"] = before[p].mean.ToString("F4", CultureInfo.InvariantCulture),
                ["MeanAfter"]  = after[p].mean.ToString("F4", CultureInfo.InvariantCulture),
                ["MedianBefore"] = before[p].median.ToString("F4", CultureInfo.InvariantCulture),
                ["MedianAfter"]  = after[p].median.ToString("F4", CultureInfo.InvariantCulture)
            });
        }
        TableWriter.WriteRows(ImpactPath, rows);
    }

    private static List<Dictionary<string, string>> LoadCsv(string path)
    {
        var list = new List<Dictionary<string, string>>();
        var content = File.ReadAllText(path).TrimStart('\uFEFF');
        using var reader = new StringReader(content);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        while (csv.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers) row[h] = csv.GetField(h) ?? "";
            list.Add(row);
        }
        return list;
    }

    private static void WriteDataset(List<Dictionary<string, string>> rows)
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
        headers.Add("Imputed_Params");

        var table = new List<IReadOnlyList<string>>();
        foreach (var r in rows)
        {
            var line = new List<string>();
            foreach (var h in headers)
                line.Add(r.TryGetValue(h, out var v) ? (v ?? "") : "");
            table.Add(line);
        }

        TableWriter.WriteTable(OutputPath, headers, table);
    }
}