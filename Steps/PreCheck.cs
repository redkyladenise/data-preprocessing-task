using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class PreCheck
{
    public static void Run()
    {
        Console.WriteLine("=== Pre-Check (Station/Coordinate Consistency) ===");

        if (!File.Exists(Inspector.RawWorkbookPath))
        {
            Console.WriteLine($"Workbook not found: {Inspector.RawWorkbookPath}");
            return;
        }

        using var wb = new XLWorkbook(Inspector.RawWorkbookPath);

        foreach (var sheetName in Schema.WaterBodySheets)
        {
            Console.WriteLine();
            Console.WriteLine($"----- {sheetName} -----");
            if (!wb.Worksheets.TryGetWorksheet(sheetName, out var ws))
            {
                Console.WriteLine("  (sheet not found)");
                continue;
            }

            InspectFirstRows(ws, sheetName);
            InspectLastRows(ws, sheetName);
            InspectStationNamesAndCoordinates(ws, sheetName);
            InspectNoSamplingRows(ws, sheetName);
        }

        Console.WriteLine();
        Console.WriteLine("--- Pre-check complete. See Output/tables/precheck/. ---");
    }

    private static void InspectFirstRows(IXLWorksheet ws, string sheetName)
    {
        Console.WriteLine();
        Console.WriteLine("--- First 5 rows from row 13 (A..H) ---");

        var rows = new List<IDictionary<string, string>>();

        for (int r = 13; r <= 17; r++)
        {
            var vals = new List<string>();
            for (int c = 1; c <= 8; c++)
                vals.Add(ExcelText.Read(ws.Cell(r, c)));

            Console.WriteLine($"  Row {r,4}: [" + string.Join(" | ", vals) + "]");

            var dict = new Dictionary<string, string>
            {
                ["Sheet"] = sheetName,
                ["Row"]   = r.ToString(CultureInfo.InvariantCulture)
            };
            for (int c = 1; c <= 8; c++)
                dict["Col" + c] = vals[c - 1];
            rows.Add(dict);
        }

        // TableWriter.WriteRows($"Output/tables/precheck/06_{sheetName}_first_rows.csv", rows);
        _ = rows;
    }

    private static void InspectLastRows(IXLWorksheet ws, string sheetName)
    {
        Console.WriteLine();
        Console.WriteLine("--- Last 15 rows (A..F) ---");

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var start = Math.Max(13, lastRow - 14);

        var rows = new List<IDictionary<string, string>>();

        for (int r = start; r <= lastRow; r++)
        {
            var vals = new List<string>();
            for (int c = 1; c <= 6; c++)
                vals.Add(ExcelText.Read(ws.Cell(r, c)));

            Console.WriteLine($"  Row {r,4}: [" + string.Join(" | ", vals) + "]");

            var dict = new Dictionary<string, string>
            {
                ["Sheet"] = sheetName,
                ["Row"]   = r.ToString(CultureInfo.InvariantCulture)
            };
            for (int c = 1; c <= 6; c++)
                dict["Col" + c] = vals[c - 1];
            rows.Add(dict);
        }

        // TableWriter.WriteRows($"Output/tables/precheck/07_{sheetName}_last_rows.csv", rows);
        _ = rows;
    }

    private static void InspectStationNamesAndCoordinates(IXLWorksheet ws, string sheetName)
    {
        Console.WriteLine();
        Console.WriteLine("--- Distinct station names and coordinates ---");

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;

        var stations = new Dictionary<string, List<(string lat, string lon)>>();

        for (int r = 13; r <= lastRow; r++)
        {
            var aText = ExcelText.Read(ws.Cell(r, 1));
            if (!Regex.IsMatch(aText, @"^\d+$")) continue;

            var bText = ExcelText.Read(ws.Cell(r, 2));
            if (string.IsNullOrWhiteSpace(bText)) continue;

            var clean = Regex.Replace(bText, @"\s+", " ").Trim();
            var lat = ExcelText.Read(ws.Cell(r, 3));
            var lon = ExcelText.Read(ws.Cell(r, 4));

            if (!stations.TryGetValue(clean, out var list))
            {
                list = new List<(string, string)>();
                stations[clean] = list;
            }
            list.Add((lat, lon));
        }

        Console.WriteLine($"  Total station rows: {stations.Values.Sum(l => l.Count)}");
        Console.WriteLine($"  Distinct names    : {stations.Count}");

        var reportRows = new List<IDictionary<string, string>>();

        foreach (var kv in stations.OrderByDescending(k => k.Value.Count))
        {
            var distinctCoords = kv.Value
                .Select(v => v.lat + "|" + v.lon)
                .Distinct()
                .ToList();

            Console.WriteLine($"    [{kv.Value.Count,4}] \"{kv.Key}\"  distinct coords: {distinctCoords.Count}");
            if (distinctCoords.Count > 1 && distinctCoords.Count <= 5)
            {
                foreach (var c in distinctCoords)
                    Console.WriteLine($"             coord: {c}");
            }

            reportRows.Add(new Dictionary<string, string>
            {
                ["Sheet"]          = sheetName,
                ["StationName"]    = kv.Key,
                ["RowCount"]       = kv.Value.Count.ToString(CultureInfo.InvariantCulture),
                ["DistinctCoords"] = distinctCoords.Count.ToString(CultureInfo.InvariantCulture),
                ["SampleCoords"]   = string.Join(" ; ", distinctCoords.Take(3))
            });
        }

        TableWriter.WriteRows($"Output/tables/precheck/06_{sheetName}_stations.csv", reportRows);
    }

    private static void InspectNoSamplingRows(IXLWorksheet ws, string sheetName)
    {
        Console.WriteLine();
        Console.WriteLine("--- Rows containing 'NO SAMPLING CONDUCTED' ---");

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var rows = new List<IDictionary<string, string>>();

        for (int r = 13; r <= lastRow; r++)
        {
            for (int c = 1; c <= 20; c++)
            {
                var text = ExcelText.Read(ws.Cell(r, c));
                if (Parsers.IsNoSamplingText(text))
                {
                    Console.WriteLine($"  Row {r,4}: found in column {c}: \"{text}\"");
                    rows.Add(new Dictionary<string, string>
                    {
                        ["Sheet"]  = sheetName,
                        ["Row"]    = r.ToString(CultureInfo.InvariantCulture),
                        ["Column"] = c.ToString(CultureInfo.InvariantCulture),
                        ["Text"]   = text
                    });
                    break;
                }
            }
        }

        if (rows.Count == 0)
            Console.WriteLine("  (none)");

        // TableWriter.WriteRows($"Output/tables/precheck/09_{sheetName}_no_sampling.csv", rows);
        _ = rows;
    }
}