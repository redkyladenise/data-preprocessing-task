using System.Globalization;
using ClosedXML.Excel;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;
public static class Inspector
{
    public const string RawWorkbookPath =
        "Data/raw/MMORS_water_quality_results_2012-2018_orig.xlsx";

    public static void Run()
    {
        Console.WriteLine("====== STEP 1: Inspector (Load and Inspect) ======");

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

        Console.WriteLine();
        Console.WriteLine("==== Inspection complete. See Output/tables/inspector/. ====");
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

        TableWriter.WriteRows("Output/tables/inspector/01_sheet_structure.csv", rows);
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

        TableWriter.WriteRows("Output/tables/inspector/02_table1_3_contents.csv", rows);
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
            for (int r = 8; r <= 10; r++)
            {
                var values = new List<string>();
                for (int c = 1; c <= 10; c++)
                {
                    values.Add(ExcelText.Read(ws.Cell(r, c)));
                }
                Console.WriteLine($"  Row {r}: [" + string.Join(" | ", values) + "]");

                rows.Add(new Dictionary<string, string>
                {
                    ["Sheet"] = sheetName,
                    ["Row"]   = r.ToString(CultureInfo.InvariantCulture),
                    ["A"]     = values[0],
                    ["B"]     = values[1],
                    ["C"]     = values[2],
                    ["D"]     = values[3],
                    ["E"]     = values[4],
                    ["F"]     = values[5],
                    ["G"]     = values[6],
                    ["H"]     = values[7],
                    ["I"]     = values[8],
                    ["J"]     = values[9],
                });
            }
        }

        TableWriter.WriteRows("Output/tables/inspector/05_header_row_dump.csv", rows);
    }

    // -----------------------------------------------------------------
    private static void InspectDateAndTimeTypes(XLWorkbook wb)
    {
        Console.WriteLine();
        Console.WriteLine("--- Date/Time cell types (first 40 rows per sheet) ---");

        var rows = new List<IDictionary<string, string>>();

        foreach (var sheetName in Schema.WaterBodySheets)
        {
            if (!wb.Worksheets.TryGetWorksheet(sheetName, out var ws))
                continue;

            // NEW: scan rows 5..13 for the header (not just row 9).
            int dateCol = FindHeaderColumn(ws, 5, 13, Schema.ColDate);
            int timeCol = FindHeaderColumn(ws, 5, 13, Schema.ColTime);

            Console.WriteLine($"Sheet: {sheetName}  DateCol={dateCol}  TimeCol={timeCol}");

            var lastRow = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, 40);

            for (int r = 13; r <= lastRow; r++)
            {
                var dCell = dateCol > 0 ? ws.Cell(r, dateCol) : null;
                var tCell = timeCol > 0 ? ws.Cell(r, timeCol) : null;

                string dText = ExcelText.Read(dCell);
                string tText = ExcelText.Read(tCell);
                string dType = dCell != null ? GetCellType(dCell) : "";
                string tType = tCell != null ? GetCellType(tCell) : "";

                if (dText.Length == 0 && tText.Length == 0) continue;

                Console.WriteLine(
                    $"  Row {r,4}: Date=\"{dText}\" [{dType}]  Time=\"{tText}\" [{tType}]");

                rows.Add(new Dictionary<string, string>
                {
                    ["Sheet"]    = sheetName,
                    ["Row"]      = r.ToString(CultureInfo.InvariantCulture),
                    ["DateText"] = dText,
                    ["DateType"] = dType,
                    ["TimeText"] = tText,
                    ["TimeType"] = tType,
                });
            }
        }

        TableWriter.WriteRows("Output/tables/inspector/03_date_time_types.csv", rows);
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
        Console.WriteLine("--- Marilao side table V435:Z440 vs November 2017 block ---");

        if (!wb.Worksheets.TryGetWorksheet(Schema.SheetMarilao, out var ws))
        {
            Console.WriteLine("  (Marilao sheet not found)");
            return;
        }

        var sideRange = ws.Range("V435:Z440");
        var rows = new List<IDictionary<string, string>>();

        Console.WriteLine("Side table V435:Z440:");
        int sideRowIndex = 0;
        foreach (var row in sideRange.Rows())
        {
            sideRowIndex++;
            var values = new List<string>();
            foreach (var cell in row.Cells())
            {
                values.Add(ExcelText.Read(cell));
            }
            Console.WriteLine("  [" + string.Join(" | ", values) + "]");

            rows.Add(new Dictionary<string, string>
            {
                ["Block"]    = "SideTable_V435_Z440",
                ["RowIndex"] = sideRowIndex.ToString(CultureInfo.InvariantCulture),
                ["C1"]       = values.Count > 0 ? values[0] : "",
                ["C2"]       = values.Count > 1 ? values[1] : "",
                ["C3"]       = values.Count > 2 ? values[2] : "",
                ["C4"]       = values.Count > 3 ? values[3] : "",
                ["C5"]       = values.Count > 4 ? values[4] : "",
            });
        }

        int labelRow = FindLabelRow(ws, "2017", "NOVEMBER");
        Console.WriteLine();
        Console.WriteLine($"Nearest 'CY 2017 NOVEMBER' label found at row {labelRow}.");

        if (labelRow > 0)
        {
            Console.WriteLine("Next 6 rows after that label:");
            int blockIndex = 0;
            for (int r = labelRow + 1;
                 r <= labelRow + 6 && r <= (ws.LastRowUsed()?.RowNumber() ?? labelRow);
                 r++)
            {
                blockIndex++;
                var values = new List<string>();
                for (int c = 1; c <= 22; c++)
                {
                    values.Add(ExcelText.Read(ws.Cell(r, c)));
                }
                Console.WriteLine($"  Row {r}: [" + string.Join(" | ", values) + "]");

                rows.Add(new Dictionary<string, string>
                {
                    ["Block"]    = "MainBlock_Nov2017",
                    ["RowIndex"] = blockIndex.ToString(CultureInfo.InvariantCulture),
                    ["C1"]       = values.Count > 0 ? values[0] : "",
                    ["C2"]       = values.Count > 1 ? values[1] : "",
                    ["C3"]       = values.Count > 2 ? values[2] : "",
                    ["C4"]       = values.Count > 3 ? values[3] : "",
                    ["C5"]       = values.Count > 4 ? values[4] : "",
                });
            }
        }

        TableWriter.WriteRows("Output/tables/inspector/04_marilao_side_table.csv", rows);
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
}