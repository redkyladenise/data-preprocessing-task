using System.Globalization;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Figures
{
    public const string OutputDir = "Output/figures";

    private const string FinalPath = Transformer.OutputPath;
    private const string RawPath   = Extractor.OutputPath;

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("====== 10: Figures (Charts for the Report) ============");

        Directory.CreateDirectory(OutputDir);

        var final = LoadCsv(FinalPath);
        var raw   = LoadCsv(RawPath);

        DrawMissingBeforeAfter(raw, final);
        DrawExistingVsFinalData(raw, final);
        DrawDistributionHistograms(final);

        Console.WriteLine("====== Figures complete ======");
    }

    // =================================================================
    // FIGURE 1: Missing % (Raw vs. Final)
    // =================================================================
    private static void DrawMissingBeforeAfter(
        List<Dictionary<string, string>> raw,
        List<Dictionary<string, string>> final)
    {
        var stdOrder = Schema.StandardParameterNames;
        var shortLabels = stdOrder.Select(std => GetWrappedLabel(std)).ToArray();

        var before = new double[stdOrder.Length];
        for (int i = 0; i < stdOrder.Length; i++)
        {
            int missing = raw.Count(r => Parsers.IsPlaceholder(r.TryGetValue(stdOrder[i], out var v) ? v : ""));
            before[i] = raw.Count > 0 ? missing * 100.0 / raw.Count : 0;
        }

        var after = new double[stdOrder.Length];
        for (int i = 0; i < stdOrder.Length; i++)
        {
            var safe = Schema.ParameterToSafeName[stdOrder[i]];
            int missing = final.Count(r => string.IsNullOrWhiteSpace(r.TryGetValue(safe, out var v) ? v : ""));
            after[i] = final.Count > 0 ? missing * 100.0 / final.Count : 0;
        }

        var plt = new ScottPlot.Plot();
        plt.Title("Missing % per Parameter: Raw vs. Final");
        plt.XLabel("Parameter");
        plt.YLabel("Missing %");

        double barWidth = 0.38;
        var beforeBars = new List<ScottPlot.Bar>();
        var afterBars  = new List<ScottPlot.Bar>();

        for (int i = 0; i < stdOrder.Length; i++)
        {
            beforeBars.Add(new ScottPlot.Bar
            {
                Position  = i - barWidth / 2.0,
                Value     = before[i],
                Size      = barWidth,
                FillColor = ScottPlot.Colors.SteelBlue,
            });

            afterBars.Add(new ScottPlot.Bar
            {
                Position  = i + barWidth / 2.0,
                Value     = after[i],
                Size      = barWidth,
                FillColor = ScottPlot.Colors.Orange,
            });
        }

        var bpBefore = plt.Add.Bars(beforeBars.ToArray());
        bpBefore.LegendText = "Before (raw)";
        bpBefore.ValueLabelStyle.FontSize = 11;
        bpBefore.ValueLabelStyle.Bold = true;
        bpBefore.ValueLabelStyle.ForeColor = ScottPlot.Colors.SteelBlue;
        foreach (var bar in bpBefore.Bars) bar.Label = bar.Value > 0 ? $"{bar.Value:F1}%" : "";

        var bpAfter = plt.Add.Bars(afterBars.ToArray());
        bpAfter.LegendText = "After (final)";
        bpAfter.ValueLabelStyle.FontSize = 11;
        bpAfter.ValueLabelStyle.Bold = true;
        bpAfter.ValueLabelStyle.ForeColor = ScottPlot.Colors.Orange;
        foreach (var bar in bpAfter.Bars) bar.Label = bar.Value > 0 ? $"{bar.Value:F1}%" : "";

        var positions = Enumerable.Range(0, stdOrder.Length).Select(i => (double)i).ToArray();
        plt.Axes.Bottom.SetTicks(positions, shortLabels);
        plt.Axes.Bottom.TickLabelStyle.Rotation = 0;
        plt.Axes.Bottom.TickLabelStyle.FontSize = 10;
        plt.ShowLegend(ScottPlot.Alignment.UpperRight);

        plt.Axes.SetLimitsY(0, 110);
        plt.Axes.SetLimitsX(-0.8, stdOrder.Length);
        plt.SavePng($"{OutputDir}/01_missing_before_after.png", 1500, 850);
        Console.WriteLine("  Wrote 01_missing_before_after.png");
    }

    // =================================================================
    // FIGURE 2: Existing vs. Final Data Volume (Stacked Bar)
    // =================================================================
    private static void DrawExistingVsFinalData(
        List<Dictionary<string, string>> raw,
        List<Dictionary<string, string>> final)
    {
        var stdOrder = Schema.StandardParameterNames;
        var shortLabels = stdOrder.Select(std => GetWrappedLabel(std)).ToArray();

        var origCounts = new double[stdOrder.Length];
        var impCounts  = new double[stdOrder.Length];

        for (int i = 0; i < stdOrder.Length; i++)
        {
            var std  = stdOrder[i];
            var safe = Schema.ParameterToSafeName[std];

            // Count non-empty, non-placeholder in raw
            int orig = raw.Count(r => !Parsers.IsPlaceholder(r.TryGetValue(std, out var v) ? v : ""));
            // Count non-empty in final (should be the full 1147)
            int fin  = final.Count(r => !string.IsNullOrWhiteSpace(r.TryGetValue(safe, out var v) ? v : ""));

            origCounts[i] = orig;
            impCounts[i]  = fin - orig; // The difference is what we imputed
        }

        var plt = new ScottPlot.Plot();
        plt.Title("Data Completeness: Existing vs. Imputed per Parameter");
        plt.XLabel("Parameter");
        plt.YLabel("Number of Cells");

        var origBars = origCounts.Select((val, i) => new ScottPlot.Bar
        {
            Position = i,
            Value = val,
            FillColor = ScottPlot.Colors.SteelBlue,
        }).ToArray();
        
        var bpOrig = plt.Add.Bars(origBars);
        bpOrig.LegendText = "Original Data";

        var impBars = impCounts.Select((val, i) => new ScottPlot.Bar
        {
            Position = i,
            Value = val,
            ValueBase = origCounts[i], // Starts where original ends
            FillColor = ScottPlot.Colors.Orange,
        }).ToArray();
        
        var bpImp = plt.Add.Bars(impBars);
        bpImp.LegendText = "Imputed Data";

        var positions = Enumerable.Range(0, stdOrder.Length).Select(i => (double)i).ToArray();
        plt.Axes.Bottom.SetTicks(positions, shortLabels);
        plt.Axes.Bottom.TickLabelStyle.Rotation = 0;
        plt.Axes.Bottom.TickLabelStyle.FontSize = 10;
        plt.ShowLegend(ScottPlot.Alignment.UpperRight);

        plt.Axes.SetLimitsX(-0.8, stdOrder.Length);
        plt.SavePng($"{OutputDir}/02_existing_vs_final_data.png", 1500, 850);
        Console.WriteLine("  Wrote 02_existing_vs_final_data.png");
    }

    // =================================================================
    // FIGURE 3: Parameter Distribution Grid (12 separate histograms)
    // =================================================================
    private static void DrawDistributionHistograms(List<Dictionary<string, string>> final)
    {
        var stdOrder = Schema.StandardParameterNames;

        for (int i = 0; i < stdOrder.Length; i++)
        {
            var std = stdOrder[i];
            var safe = Schema.ParameterToSafeName[std];

            var values = new List<double>();
            foreach (var row in final)
            {
                if (double.TryParse(row[safe], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    values.Add(v);
            }

            if (values.Count == 0) continue;

            var plt = new ScottPlot.Plot();
            plt.Title($"{std} Distribution");
            plt.XLabel("Value");
            plt.YLabel("Frequency");

            // Manual binning to avoid version-specific Histogram API issues
            int binCount = 20;
            double min = values.Min();
            double max = values.Max();
            
            // Handle edge case where all values are identical
            if (Math.Abs(max - min) < 1e-9) max = min + 1; 

            double binWidth = (max - min) / binCount;
            var counts = new int[binCount];
            
            foreach (var v in values)
            {
                int idx = (int)Math.Floor((v - min) / binWidth);
                if (idx >= binCount) idx = binCount - 1;
                if (idx < 0) idx = 0;
                counts[idx]++;
            }

            var bars = new ScottPlot.Bar[binCount];
            for (int b = 0; b < binCount; b++)
            {
                double center = min + (b + 0.5) * binWidth;
                bars[b] = new ScottPlot.Bar
                {
                    Position = center,
                    Value = counts[b],
                    FillColor = ScottPlot.Colors.SteelBlue,
                    Size = binWidth * 0.9 // gap between bars
                };
            }

            plt.Add.Bars(bars);

            string fileNum = (i + 1).ToString("D2");
            string safeFileName = safe.Replace("_", "").Replace("-", "");
            plt.SavePng($"{OutputDir}/03_hist_{fileNum}_{safeFileName}.png", 600, 450);
        }
        Console.WriteLine("  Wrote 12 distribution histograms (03_hist_01... to 03_hist_12...)");
    }

    // helpers
    private static string GetWrappedLabel(string std)
    {
        string name = std.Split(',')[0].Trim();
        return name switch
        {
            "Biochemical Oxygen Demand" => "Biochemical\nOxygen Demand",
            "Total Suspended Solids"    => "Total Suspended\nSolids",
            "Fecal Coliform"            => "Fecal\nColiform",
            "Total Coliform"            => "Total\nColiform",
            "Dissolved Oxygen"          => "Dissolved\nOxygen",
            "Nitrates as Nitrogen"      => "Nitrates as\nNitrogen",
            "Phosphates as Phosphorous" => "Phosphates as\nPhosphorous",
            _                           => name
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
}