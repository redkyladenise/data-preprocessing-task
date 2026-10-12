using System.Globalization;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Figures
{
    public const string OutputDir = "Output/figures";

    private const string FinalPath     = Transformer.OutputPath;
    private const string RawPath       = Extractor.OutputPath;
    private const string ExcludedPath  = Cleaner.ExcludedPath;
    private const string OutlierPath   = Cleaner.OutlierSummaryPath;

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("====== 10: Figures (Charts for the Report) ============");

        Directory.CreateDirectory(OutputDir);

        var final    = LoadCsv(FinalPath);
        var raw      = LoadCsv(RawPath);
        var excluded = LoadCsv(ExcludedPath);
        var outliers = LoadCsv(OutlierPath);

        DrawMissingBeforeAfter(raw, final);
        DrawCoverageTimeline(final);
        DrawRowCount(excluded);
        DrawOutliersPerParameter(outliers);

        Console.WriteLine("====== Figures complete ======");
    }

    // 1. Missing % per parameter
    private static void DrawMissingBeforeAfter(
        List<Dictionary<string, string>> raw,
        List<Dictionary<string, string>> final)
    {
        var stdOrder = Schema.StandardParameterNames;
        
        var shortLabels = stdOrder.Select(std =>
        {
            string name = ShortName(std);

            string wrapped = name switch
            {
                "Biochemical Oxygen Demand" => "Biochemical\nOxygen Demand",
                "Total Suspended Solids"    => "Total Suspended\nSolids",
                "Fecal Coliform"            => "Fecal\nColiform",
                "Total Coliform"            => "Total\nColiform",
                _                           => name
            };

            if (Schema.DroppedParameters.Contains(std))
                wrapped += "\n(Dropped)";

            return wrapped;
        }).ToArray();

        var before = new double[stdOrder.Length];
        for (int i = 0; i < stdOrder.Length; i++)
        {
            int missing = raw.Count(r => string.IsNullOrWhiteSpace(r.TryGetValue(stdOrder[i], out var v) ? v : ""));
            before[i] = raw.Count > 0 ? missing * 100.0 / raw.Count : 0;
        }

        var after   = new double[stdOrder.Length];
        var dropped = new bool[stdOrder.Length];
        for (int i = 0; i < stdOrder.Length; i++)
        {
            var std = stdOrder[i];
            if (Schema.DroppedParameters.Contains(std)) { dropped[i] = true; continue; }

            var safe = Schema.ParameterToSafeName[std];
            int missing = final.Count(r => string.IsNullOrWhiteSpace(r.TryGetValue(safe, out var v) ? v : ""));
            after[i] = final.Count > 0 ? missing * 100.0 / final.Count : 0;
        }

        var plt = new ScottPlot.Plot();
        plt.Title("Missing % per parameter: raw vs. final");
        plt.XLabel("Parameter");
        plt.YLabel("Missing %");

        double barWidth = 0.38;

        var beforeBars = new List<ScottPlot.Bar>();
        var afterBars  = new List<ScottPlot.Bar>();

        for (int i = 0; i < stdOrder.Length; i++)
        {
            double beforePos = dropped[i] ? i : i - barWidth / 2.0;
            double afterPos  = i + barWidth / 2.0;

            beforeBars.Add(new ScottPlot.Bar
            {
                Position  = beforePos,
                Value     = before[i],
                Size      = barWidth,
                FillColor = ScottPlot.Colors.SteelBlue,
            });

            if (!dropped[i])
            {
                afterBars.Add(new ScottPlot.Bar
                {
                    Position  = afterPos,
                    Value     = after[i],
                    Size      = barWidth,
                    FillColor = ScottPlot.Colors.Orange,
                });
            }
        }

        var barPlotBefore = plt.Add.Bars(beforeBars.ToArray());
        barPlotBefore.LegendText = "Before (raw)";

        var barPlotAfter = plt.Add.Bars(afterBars.ToArray());
        barPlotAfter.LegendText = "After (final)";

        barPlotBefore.ValueLabelStyle.FontSize = 11;
        barPlotBefore.ValueLabelStyle.Bold = true;
        barPlotBefore.ValueLabelStyle.ForeColor = ScottPlot.Colors.SteelBlue;
        foreach (var bar in barPlotBefore.Bars)
        {
            bar.Label = bar.Value > 0 ? $"{bar.Value:F1}%" : string.Empty;
        }

        barPlotAfter.ValueLabelStyle.FontSize = 11;
        barPlotAfter.ValueLabelStyle.Bold = true;
        barPlotAfter.ValueLabelStyle.ForeColor = ScottPlot.Colors.Orange;
        foreach (var bar in barPlotAfter.Bars)
        {
            bar.Label = bar.Value > 0 ? $"{bar.Value:F1}%" : string.Empty;
        }

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

    // 2. Data Coverage Timeline (horizontal)
    private static void DrawCoverageTimeline(List<Dictionary<string, string>> final)
    {
        var stdOrder = Schema.StandardParameterNames
            .Where(std => !Schema.DroppedParameters.Contains(std))
            .ToArray();

        int firstYear = 2012;
        int lastYear = 2018;
        int yearsCount = lastYear - firstYear + 1;

        var plt = new ScottPlot.Plot();
        plt.Title("Final Dataset Coverage Timeline by Parameter (2012–2018)");
        plt.XLabel("Year");
        plt.YLabel("Parameter");

        for (int p = 0; p < stdOrder.Length; p++)
        {
            var std = stdOrder[p];
            var safe = Schema.ParameterToSafeName[std];

            for (int y = 0; y < yearsCount; y++)
            {
                int year = firstYear + y;
                var rowsThisYear = final
                    .Where(r => r.TryGetValue("Year", out var yrVal) && yrVal == year.ToString())
                    .ToList();

                double pct = 0;
                if (rowsThisYear.Count > 0)
                {
                    int present = rowsThisYear.Count(r =>
                        r.TryGetValue(safe, out var val) && !string.IsNullOrWhiteSpace(val));
                    pct = present * 100.0 / rowsThisYear.Count;
                }

                double margin = 0.05;
                double x1 = year + margin;
                double x2 = year + 1.0 - margin;
                double yCenter = p;
                double barHeight = 0.5;
                double y1 = yCenter - barHeight / 2.0;
                double y2 = yCenter + barHeight / 2.0;

                var rect = plt.Add.Rectangle(new ScottPlot.CoordinateRect(x1, x2, y1, y2));
                
                if (pct <= 0)
                {
                    rect.FillColor = ScottPlot.Colors.SteelBlue.WithAlpha(0.1);
                    rect.LineColor = ScottPlot.Colors.SteelBlue.WithAlpha(0.3);
                }
                else if (pct < 80.0)
                {
                    rect.FillColor = ScottPlot.Colors.SteelBlue.WithAlpha(0.35); 
                    rect.LineColor = ScottPlot.Colors.SteelBlue.WithAlpha(0.6);
                }
                else
                {
                    rect.FillColor = ScottPlot.Colors.SteelBlue;
                    rect.LineColor = ScottPlot.Colors.SteelBlue;
                }

                if (pct > 0)
                {
                    var txt = plt.Add.Text($"{pct:F0}%", x1 + 0.45, yCenter);
                    txt.LabelStyle.FontSize = 11;
                    txt.LabelStyle.Bold = true;
                    txt.LabelStyle.ForeColor = pct >= 80.0 ? ScottPlot.Colors.White : ScottPlot.Colors.Black;
                    txt.LabelStyle.Alignment = ScottPlot.Alignment.MiddleCenter;
                }
            }
        }

        var yLabels = stdOrder.Select(std => ShortName(std)).ToArray();
        var yPositions = Enumerable.Range(0, stdOrder.Length).Select(i => (double)i).ToArray();
        
        plt.Axes.Left.SetTicks(yPositions, yLabels);
        plt.Axes.InvertY();

        var xPositions = Enumerable.Range(0, yearsCount + 1).Select(i => (double)(firstYear + i)).ToArray();
        var xLabels = Enumerable.Range(0, yearsCount + 1).Select(i => (firstYear + i).ToString()).ToArray();
        plt.Axes.Bottom.SetTicks(xPositions, xLabels);

        plt.Axes.SetLimitsX(firstYear - 0.5, lastYear + 1.5);
        plt.Axes.SetLimitsY(-1.0, stdOrder.Length);

        plt.SavePng($"{OutputDir}/02_coverage_timeline.png", 1300, 700);
        Console.WriteLine("  Wrote 02_coverage_timeline.png");
    }

    // 3. row Counts
    private static void DrawRowCount(List<Dictionary<string, string>> excluded)
    {
        
        var reasons = excluded
            .GroupBy(e => e.TryGetValue("Reason", out var r) ? r : "UNKNOWN")
            .OrderByDescending(g => g.Count())
            .ToList();

        var plt = new ScottPlot.Plot();
        plt.Title("Row Count Comparison: Raw, Final, and Removed Rows");
        plt.XLabel("Category");
        plt.YLabel("Number of Rows");

        int total = 2 + reasons.Count;
        var positions = new double[total];
        var labels = new string[total];

        int rawCount   = LoadCsv(RawPath).Count;
        int finalCount = LoadCsv(FinalPath).Count;

        var rawBars = new List<ScottPlot.Bar>
        {
            new ScottPlot.Bar { Position = 0, Value = rawCount, FillColor = ScottPlot.Colors.SteelBlue }
        };

        var finalBars = new List<ScottPlot.Bar>
        {
            new ScottPlot.Bar { Position = 1, Value = finalCount, FillColor = ScottPlot.Colors.MidnightBlue }
        };

        var exclusionBars = new List<ScottPlot.Bar>();

        positions[0] = 0; labels[0] = "Raw Dataset";
        positions[1] = 1; labels[1] = "Final Dataset";

        for (int i = 0; i < reasons.Count; i++)
        {
            int idx = i + 2;
            
            var barColor = i == 0 ? ScottPlot.Colors.DarkOrange : 
                           i == 1 ? ScottPlot.Colors.Orange : ScottPlot.Colors.SandyBrown;

            exclusionBars.Add(new ScottPlot.Bar
            {
                Position = idx,
                Value = reasons[i].Count(),
                FillColor = barColor,
            });
            positions[idx] = idx;
            
            string rawKey = reasons[i].Key;
            string cleanReason = rawKey switch
            {
                "EMPTY_PLACEHOLDER" => "Empty Placeholder",
                "NO_SAMPLING_TEXT" => "No Sampling Conducted Text",
                "NO_RESULTS" => "No Results",
                _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(rawKey.Replace("_", " ").ToLower())
            };

            labels[idx] = cleanReason;
        }

        var barPlotRaw = plt.Add.Bars(rawBars.ToArray());
        var barPlotFinal = plt.Add.Bars(finalBars.ToArray());
        var barPlotExclusions = plt.Add.Bars(exclusionBars.ToArray());

        foreach (var bp in new[] { barPlotRaw, barPlotFinal, barPlotExclusions })
        {
            bp.ValueLabelStyle.FontSize = 11;
            bp.ValueLabelStyle.Bold = true;
            bp.ValueLabelStyle.ForeColor = ScottPlot.Colors.DarkSlateGray;
            foreach (var bar in bp.Bars)
            {
                bar.Label = bar.Value > 0 ? bar.Value.ToString("N0") : string.Empty;
            }
        }

        plt.Axes.Bottom.SetTicks(positions, labels);
        plt.Axes.Bottom.TickLabelStyle.Rotation = 0;
        plt.Axes.Bottom.TickLabelStyle.FontSize = 11;

        plt.Axes.SetLimitsY(0, 1300);
        plt.Axes.SetLimitsX(-0.8, total - 0.2);

        plt.SavePng($"{OutputDir}/03_row_counts.png", 1300, 750);
        Console.WriteLine("  Wrote 03_row_counts.png");
    }

    // 4. Outliers (all 12 params)
    private static void DrawOutliersPerParameter(List<Dictionary<string, string>> outliers)
    {
        var allParameters = Schema.StandardParameterNames
            .Select(std => ShortName(std))
            .Distinct()
            .ToList();

        var paramOutlierSums = allParameters.ToDictionary(
            param => param,
            param => outliers
                .Where(o => ShortName(o["Parameter"]) == param &&
                            int.TryParse(o["OutlierCount"], out _))
                .Sum(o => int.Parse(o["OutlierCount"]))
        );

        var sortedParams = Schema.StandardParameterNames
            .Select(std => ShortName(std))
            .Distinct()
            .ToList();

        var droppedShortNames = Schema.DroppedParameters
            .Select(std => ShortName(std))
            .ToHashSet();

        var sheets = new[] { "Marilao", "Meycauayan", "Obando" };
        var colors = new[]
        {
            ScottPlot.Colors.SteelBlue,
            ScottPlot.Colors.Orange,
            ScottPlot.Colors.MidnightBlue,
        };

        var plt = new ScottPlot.Plot();
        plt.Title("Outliers per parameter and water body");
        plt.XLabel("Parameter");
        plt.YLabel("Number of flagged outlier cells");

        int n = sortedParams.Count;
        double width = 0.25;

        for (int s = 0; s < sheets.Length; s++)
        {
            var bars = new List<ScottPlot.Bar>();
            for (int i = 0; i < n; i++)
            {
                string paramName = sortedParams[i];
                bool dropped = droppedShortNames.Contains(paramName);

                int count = outliers
                    .Where(o => ShortName(o["Parameter"]) == paramName &&
                                o["Sheet"] == sheets[s] &&
                                int.TryParse(o["OutlierCount"], out _))
                    .Sum(o => int.Parse(o["OutlierCount"]));

                bars.Add(new ScottPlot.Bar
                {
                    Position  = i + (s - 1) * width,
                    Value     = count,
                    Size      = width,
                    FillColor = colors[s], 
                });
            }

            var barPlot = plt.Add.Bars(bars.ToArray());
            barPlot.LegendText = sheets[s];

            barPlot.ValueLabelStyle.FontSize  = 10;
            barPlot.ValueLabelStyle.Bold      = true;
            barPlot.ValueLabelStyle.ForeColor = ScottPlot.Colors.DarkSlateGray;

            foreach (var bar in barPlot.Bars)
                bar.Label = bar.Value > 0 ? bar.Value.ToString("0") : string.Empty;
        }

        var positions = Enumerable.Range(0, n).Select(i => (double)i).ToArray();

        var labels = sortedParams.Select(name =>
        {
            string wrapped = name switch
            {
                "Biochemical Oxygen Demand" => "Biochemical\nOxygen Demand",
                "Total Suspended Solids"    => "Total Suspended\nSolids",
                "Fecal Coliform"            => "Fecal\nColiform",
                "Total Coliform"            => "Total\nColiform",
                "Dissolved Oxygen"          => "Dissolved\nOxygen",
                _                           => name
            };

            if (droppedShortNames.Contains(name))
                wrapped += "\n(dropped)";

            return wrapped;
        }).ToArray();

        plt.Axes.Bottom.SetTicks(positions, labels);
        plt.Axes.Bottom.TickLabelStyle.Rotation = 0;
        plt.Axes.Bottom.TickLabelStyle.FontSize = 10;

        plt.ShowLegend(ScottPlot.Alignment.UpperRight);

        int maxSingleBar = outliers
            .Select(o => int.TryParse(o["OutlierCount"], out var c) ? c : 0)
            .DefaultIfEmpty(0)
            .Max();
        plt.Axes.SetLimitsY(0, maxSingleBar + 5);
        plt.Axes.SetLimitsX(-0.8, n - 0.2);

        plt.SavePng($"{OutputDir}/04_outliers_per_parameter.png", 1400, 780);
        Console.WriteLine("  Wrote 04_outliers_per_parameter.png");
    }

    private static string ShortName(string parameter) => parameter switch
    {
        "Dissolved Oxygen, mg/L"            => "Dissolved Oxygen",
        "Biochemical Oxygen Demand, mg/L"  => "Biochemical Oxygen Demand",
        "Total Suspended Solids, mg/L"     => "Total Suspended Solids",
        "Temperature °C"                  => "Temperature",
        "Color TCU"                        => "Color",
        "Fecal Coliform, MPN/100mL"        => "Fecal Coliform",
        "Total Coliform, MPN/100mL"        => "Total Coliform",
        "Ammonia, mg/L"                    => "Ammonia",
        "Nitrates as Nitrogen, mg/L"       => "Nitrates",
        "Phosphates as Phosphorous, mg/L"  => "Phosphates",
        "Chlorides Cl, mg/L"               => "Chlorides",
        _ => parameter
    };

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