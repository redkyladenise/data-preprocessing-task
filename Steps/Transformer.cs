using System.Globalization;
using CsvHelper;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Transformer
{
    public const string InputPath  = Imputer.OutputPath;
    public const string OutputPath = "Data/clean/mmors_water_quality_clean_2012_2018.csv";

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("=========== 06: Transformer (Final Dataset) ===========");
        
        if (!File.Exists(InputPath))
        {
            Console.WriteLine($"Input not found: {InputPath}. Run Step 5 first.");
            return;
        }

        var records = LoadData(InputPath);
        Console.WriteLine($"  Loaded {records.Count} records.");

        var finalRows = records.Select(BuildFinalRow).ToList();

        finalRows = finalRows
            .OrderBy(r => r["WaterBody"], StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => int.TryParse(r["Year"],  out var y) ? y : 0)
            .ThenBy(r => int.TryParse(r["Month"], out var m) ? m : 0)
            .ThenBy(r => r["StationID"], StringComparer.OrdinalIgnoreCase)
            .ToList();

        WriteFinal(finalRows);

        Console.WriteLine($"  Rows    : {finalRows.Count}");
        Console.WriteLine($"  Columns : {Schema.FinalColumnOrder.Length}");
        Console.WriteLine($"=== 06 Complete. Wrote {OutputPath}");
    }

    private static List<Dictionary<string, string>> LoadData(string path)
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
            foreach (var h in headers)
                row[h] = csv.GetField(h) ?? "";
            list.Add(row);
        }
        return list;
    }

    private static Dictionary<string, string> BuildFinalRow(Dictionary<string, string> rec)
    {
        var final = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        final["WaterBody"]   = rec.TryGetValue("SourceSheet", out var wb) ? wb : "";
        final["StationID"]   = rec.TryGetValue("StationID",   out var sid) ? sid : "";
        final["StationName"] = rec.TryGetValue("StationRaw",  out var sn) ? sn : "";

        final["Latitude"]   = RoundLatLon(rec.TryGetValue("Latitude",  out var lat) ? lat : "");
        final["Longitude"]  = RoundLatLon(rec.TryGetValue("Longitude", out var lon) ? lon : "");

        final["Year"]  = rec.TryGetValue("Year",  out var yr) ? yr : "";
        final["Month"] = rec.TryGetValue("Month", out var mo) ? mo : "";
        final["Date"]  = rec.TryGetValue("SampleDate", out var sd) ? sd : "";
        final["Time"]  = TruncateToHHmm(rec.TryGetValue("SampleTime", out var st) ? st : "");
        
        foreach (var stdName in Schema.StandardParameterNames)
        {
            var safeName = Schema.ParameterToSafeName[stdName];
            var rawValue = rec.TryGetValue(stdName, out var v) ? v : "";

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                final[safeName] = "";
                continue;
            }

            if (Schema.ImputedDecimalPlaces.TryGetValue(stdName, out var dp) &&
                double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                final[safeName] = FormatRounded(d, dp);
            }
            else
            {
                final[safeName] = rawValue;
            }
        }

        return final;
    }

    private static string RoundLatLon(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return raw;
        
        var rounded = Math.Round(d, Schema.LatLonDecimalPlaces, MidpointRounding.AwayFromZero);
        return rounded.ToString("0." + new string('#', Schema.LatLonDecimalPlaces), CultureInfo.InvariantCulture);
    }
    private static string TruncateToHHmm(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw.Length >= 5 ? raw.Substring(0, 5) : raw;
    }

    private static string FormatRounded(double value, int decimals)
    {
        if (decimals == 0)
            return Math.Round(value, 0, MidpointRounding.AwayFromZero)
                       .ToString("0", CultureInfo.InvariantCulture);

        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        var format  = "0." + new string('0', decimals); 
        
        return rounded.ToString(format, CultureInfo.InvariantCulture);
    }
    private static void WriteFinal(List<Dictionary<string, string>> rows)
    {
        var headers = Schema.FinalColumnOrder.ToList();

        var table = new List<IReadOnlyList<string>>();
        foreach (var r in rows)
        {
            var line = new List<string>(headers.Count);
            foreach (var h in headers)
                line.Add(r.TryGetValue(h, out var v) ? (v ?? "") : "");
            table.Add(line);
        }

        TableWriter.WriteTable(OutputPath, headers, table);
    }
}