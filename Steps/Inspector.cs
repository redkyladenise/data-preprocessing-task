using System.Globalization;
using ClosedXML.Excel;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;
using System.Text.RegularExpressions;


namespace DataPreprocessingTask.Steps;
public static class Inspector
{
    public const string RawWorkbookPath =
        "Data/raw/MMORS_water_quality_results_2012-2018_orig.xlsx";

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("========== 01: Inspector (Load and Inspect) ===========");

        if (!File.Exists(RawWorkbookPath))
        {
            Console.WriteLine($"Workbook not found: {RawWorkbookPath}");
            return;
        }

        using var wb = new XLWorkbook(RawWorkbookPath);

        InspectSheets(wb);
        InspectIgnoredSheet(wb);
        DumpHeaderRows(wb);
        InspectDateAndTimeTypes(wb);
        VerifyMarilaoSideTable(wb);
        InspectStations(wb);

        Console.WriteLine();
        Console.WriteLine("==== 01 Inspection complete. See Output/tables/inspection/. ====");
    }

    private static void InspectSheets(XLWorkbook wb)
    {
        Console.WriteLine();
        Console.WriteLine("--- Sheets and structure ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();
            string usedAddr = used?.RangeAddress.ToString() ?? "(empty)";
            string firstRow = used?.FirstRow().RowNumber().ToString() ?? "";
            string lastRow  = used?.LastRow().RowNumber().ToString()  ?? "";
            string firstCol = used?.FirstColumn().ColumnLetter()      ?? "";
            string lastCol  = used?.LastColumn().ColumnLetter()       ?? "";

            int mergedCount = ws.MergedRanges.Count();
            int tableCount  = ws.Tables.Count();

            Console.WriteLine($"Sheet: \"{ws.Name}\"");
            Console.WriteLine($"  Used range : {usedAddr}");
            Console.WriteLine($"  First/Last row : {firstRow} / {lastRow}");
            Console.WriteLine($"  First/Last col : {firstCol} / {lastCol}");
            Console.WriteLine($"  Merged ranges  : {mergedCount}");
            Console.WriteLine($"  Excel tables   : {tableCount}");

            rows.Add(new Dictionary<string, string>
            {
                ["Sheet"]        = ws.Name,
                ["UsedRange"]    = usedAddr,
                ["FirstRow"]     = firstRow,
                ["LastRow"]      = lastRow,
                ["FirstCol"]     = firstCol,
                ["LastCol"]      = lastCol,
                ["MergedRanges"] = mergedCount.ToString(CultureInfo.InvariantCulture),
                ["ExcelTables"]  = tableCount.ToString(CultureInfo.InvariantCulture),
            });
        }

        TableWriter.WriteRows("Output/tables/inspection/01_sheet_structure.csv", rows);
    }

    private static void InspectIgnoredSheet(XLWorkbook wb)
    {
        Console.WriteLine();
        Console.WriteLine($"--- Contents of \"{Schema.SheetIgnore}\" ---");

        if (!wb.Worksheets.TryGetWorksheet(Schema.SheetIgnore, out var ws))
        {
            Console.WriteLine("  (sheet not found)");
            return;
        }

        var used = ws.RangeUsed();
        if (used == null)
        {
            Console.WriteLine("  (empty)");
            return;
        }

        var rows = new List<IDictionary<string, string>>();

        foreach (var row in used.RowsUsed())
        {
            foreach (var cell in row.CellsUsed())
            {
                var text = ExcelText.Read(cell);
                Console.WriteLine($"  {cell.Address}: \"{text}\"");
                rows.Add(new Dictionary<string, string>
                {
                    ["Cell"] = cell.Address.ToString() ?? "",
                    ["Text"] = text,
                });
            }
        }

        TableWriter.WriteRows("Output/tables/inspection/02_sheet_table1_3_contents.csv", rows);
    }

    private static void DumpHeaderRows(XLWorkbook wb)
    {
        Console.WriteLine();
        Console.WriteLine("--- Header row (rows 8, 9, 10, columns A..J) ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var sheetName in Schema.WaterBodySheets)
        {
            if (!wb.Worksheets.TryGetWorksheet(sheetName, out var ws))
                continue;

            Console.WriteLine($"Sheet: {sheetName}");
            for (int r = 8; r <= 12; r++)
{
                var values = new List<string>();
                for (int c = 1; c <= 18; c++)
                {
                    values.Add(ExcelText.Read(ws.Cell(r, c)));
                }
                Console.WriteLine($"  Row {r}: [" + string.Join(" | ", values) + "]");

                var dict = new Dictionary<string, string>
                {
                    ["Sheet"] = sheetName,
                    ["Row"]   = r.ToString(CultureInfo.InvariantCulture),
                };
                for (int c = 1; c <= 18; c++)
                    dict["Col" + c] = values[c - 1];
                rows.Add(dict);
            }
        }

        TableWriter.WriteRows("Output/tables/inspection/03_header_rows.csv", rows);
    }

    private static void InspectDateAndTimeTypes(XLWorkbook wb)
    {
        Console.WriteLine();
        Console.WriteLine("--- Date/Time cell types (all rows, all sheets) ---");

        var summaryRows = new List<IDictionary<string, string>>();

        foreach (var sheetName in Schema.WaterBodySheets)
        {
            if (!wb.Worksheets.TryGetWorksheet(sheetName, out var ws))
                continue;

            int dateCol = FindHeaderColumn(ws, 5, 13, Schema.ColDate);
            int timeCol = FindHeaderColumn(ws, 5, 13, Schema.ColTime);

            int dateText = 0, dateNum = 0, dateDt = 0, dateOther = 0;
            int timeText = 0, timeNum = 0, timeDt = 0, timeOther = 0;

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;

            for (int r = 13; r <= lastRow; r++)
            {
                if (dateCol > 0)
                {
                    var cell = ws.Cell(r, dateCol);
                    var text = ExcelText.Read(cell);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        switch (GetCellType(cell))
                        {
                            case "text":     dateText++;  break;
                            case "number":   dateNum++;   break;
                            case "datetime": dateDt++;    break;
                            default:         dateOther++; break;
                        }
                    }
                }

                if (timeCol > 0)
                {
                    var cell = ws.Cell(r, timeCol);
                    var text = ExcelText.Read(cell);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        switch (GetCellType(cell))
                        {
                            case "text":     timeText++;  break;
                            case "number":   timeNum++;   break;
                            case "datetime": timeDt++;    break;
                            default:         timeOther++; break;
                        }
                    }
                }
            }

            Console.WriteLine(
                $"  {sheetName,-12} Date: text={dateText,4} num={dateNum,4} dt={dateDt,4} other={dateOther,4}");
            Console.WriteLine(
                $"  {sheetName,-12} Time: text={timeText,4} num={timeNum,4} dt={timeDt,4} other={timeOther,4}");

            summaryRows.Add(new Dictionary<string, string>
            {
                ["Sheet"]      = sheetName,
                ["Date_Text"]  = dateText.ToString(CultureInfo.InvariantCulture),
                ["Date_Num"]   = dateNum.ToString(CultureInfo.InvariantCulture),
                ["Date_DT"]    = dateDt.ToString(CultureInfo.InvariantCulture),
                ["Date_Other"] = dateOther.ToString(CultureInfo.InvariantCulture),
                ["Time_Text"]  = timeText.ToString(CultureInfo.InvariantCulture),
                ["Time_Num"]   = timeNum.ToString(CultureInfo.InvariantCulture),
                ["Time_DT"]    = timeDt.ToString(CultureInfo.InvariantCulture),
                ["Time_Other"] = timeOther.ToString(CultureInfo.InvariantCulture),
            });
        }

        TableWriter.WriteRows("Output/tables/inspection/04_date_time_format.csv", summaryRows);
    }

    private static int FindHeaderColumn(IXLWorksheet ws, int startRow, int endRow, string target)
    {
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;

        for (int r = startRow; r <= endRow; r++)
        {
            for (int c = 1; c <= lastCol; c++)
            {
                var raw = ExcelText.Read(ws.Cell(r, c));
                var norm = Schema.NormalizeHeader(raw);
                if (string.Equals(norm, target, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
        }
        return -1;
    }

    private static string GetCellType(IXLCell cell)
    {
        try
        {
            var v = cell.Value;
            if (v.IsText)     return "text";
            if (v.IsNumber)   return "number";
            if (v.IsDateTime) return "datetime";
            if (v.IsBoolean)  return "boolean";
            return "other";
        }
        catch
        {
            return "error";
        }
    }

    
    private static void VerifyMarilaoSideTable(XLWorkbook wb)
    {
        Console.WriteLine();
        Console.WriteLine("--- Marilao side table automatic comparison ---");

        if (!wb.Worksheets.TryGetWorksheet(Schema.SheetMarilao, out var ws))
        {
            Console.WriteLine("  (Marilao sheet not found)");
            return;
        }

        int labelRow = FindLabelRow(ws, "2017", "NOVEMBER");

        if (labelRow < 0)
        {
            Console.WriteLine("  November 2017 label not found.");
            return;
        }

        int firstDataRow = labelRow + 1;
        int lastDataRow = labelRow + 5;

        var sideRange = ws.Range("V435:Z440");
        var rows = new List<IDictionary<string, string>>();

        int sideRow = 435;

        foreach (var sideCells in sideRange.Rows())
        {
            var sideValues = new List<string>();

            foreach (var cell in sideCells.Cells())
                sideValues.Add(ExcelText.Read(cell));

            if (sideValues.All(string.IsNullOrWhiteSpace))
            {
                sideRow++;
                continue;
            }

            bool foundMatch = false;

            for (int c = 1; c <= 22; c++)
            {
                var mainValues = new List<string>();

                for (int r = firstDataRow; r <= lastDataRow; r++)
                    mainValues.Add(ExcelText.Read(ws.Cell(r, c)));

                bool exactMatch = sideValues.SequenceEqual(
                    mainValues,
                    StringComparer.Ordinal
                );

                if (!exactMatch)
                    continue;

                string columnLetter = ws.Column(c).ColumnLetter();

                Console.WriteLine(
                    $"Side row {sideRow} (V{sideRow}:Z{sideRow}) " +
                    $"matches main-table column {columnLetter}."
                );

                rows.Add(new Dictionary<string, string>
                {
                    ["SideTableRange"] = $"V{sideRow}:Z{sideRow}",
                    ["MainTableColumn"] = columnLetter,
                    ["SideTableValues"] = string.Join(", ", sideValues),
                    ["MainTableValues"] = string.Join(", ", mainValues),
                    ["Match?"] = "Yes"
                });

                foundMatch = true;
            }

            if (!foundMatch)
            {
                Console.WriteLine(
                    $"Side row {sideRow} (V{sideRow}:Z{sideRow}): " +
                    "No exact match found."
                );

                rows.Add(new Dictionary<string, string>
                {
                    ["SideTableRange"] = $"V{sideRow}:Z{sideRow}",
                    ["MainTableColumn"] = "",
                    ["SideTableValues"] = string.Join(" | ", sideValues),
                    ["MainTableValues"] = "",
                    ["Match?"] = "No"
                });
            }

            sideRow++;
        }

        TableWriter.WriteRows(
            "Output/tables/inspection/05_marilao_side_table.csv",
            rows
        );
    }

    private static int FindLabelRow(IXLWorksheet ws, string year, string monthUpper)
    {
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (int r = 1; r <= lastRow; r++)
        {
            var raw = ExcelText.Read(ws.Cell(r, 1)).ToUpperInvariant();
            if (raw.Contains(year) && raw.Contains(monthUpper))
                return r;
        }
        return -1;
    }
    private static void InspectStations(XLWorkbook wb)
    {
        Console.WriteLine();
        Console.WriteLine("--- Distinct station names and coordinates ---");

        var allStationRows = new List<IDictionary<string, string>>();

        foreach (var sheetName in Schema.WaterBodySheets)
        {
            if (!wb.Worksheets.TryGetWorksheet(sheetName, out var ws))
                continue;

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

            Console.WriteLine($"  {sheetName,-12} total station rows: {stations.Values.Sum(l => l.Count)}");
            Console.WriteLine($"  {sheetName,-12} distinct names    : {stations.Count}");

            foreach (var kv in stations.OrderByDescending(k => k.Value.Count))
            {
                var distinctCoords = kv.Value
                    .Select(v => v.lat + "," + v.lon)
                    .Distinct()
                    .ToList();

                Console.WriteLine($"    [{kv.Value.Count,4}] \"{kv.Key}\"  distinct coords: {distinctCoords.Count}");

                allStationRows.Add(new Dictionary<string, string>
                {
                    ["Sheet"]          = sheetName,
                    ["StationName"]    = kv.Key,
                    ["RowCount"]       = kv.Value.Count.ToString(CultureInfo.InvariantCulture),
                    ["DistinctCoords"] = distinctCoords.Count.ToString(CultureInfo.InvariantCulture),
                    ["SampleCoords"]   = string.Join(" ; ", distinctCoords.Take(3))
                });
            }
        }

        TableWriter.WriteRows("Output/tables/inspection/06_distinct_stations.csv", allStationRows);
    }

}