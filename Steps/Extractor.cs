using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DataPreprocessingTask.Domain;
using DataPreprocessingTask.Helpers;

namespace DataPreprocessingTask.Steps;

public static class Extractor
{
    public const string OutputPath  = "Data/interim/extracted_raw_records.csv";
    public const string SkippedPath = "Data/interim/skipped_rows.csv";

    public static void Run()
    {
        Console.WriteLine("*******************************************************");
        Console.WriteLine("======== 02: Extractor (Read and Extract Rows) ========");

        if (!File.Exists(Inspector.RawWorkbookPath))
        {
            Console.WriteLine($"Workbook not found: {Inspector.RawWorkbookPath}");
            return;
        }

        using var wb = new XLWorkbook(Inspector.RawWorkbookPath);

        var allRecords  = new List<RawRecord>();
        var skippedRows = new List<IDictionary<string, string>>();

        foreach (var sheetName in Schema.WaterBodySheets)
        {
            if (!wb.Worksheets.TryGetWorksheet(sheetName, out var ws))
            {
                Console.WriteLine($"  (sheet not found: {sheetName})");
                continue;
            }

            var records = ExtractSheet(ws, sheetName, skippedRows);
            allRecords.AddRange(records);
            Console.WriteLine($"  {sheetName,-12}: {records.Count,4} records extracted");
        }

        Console.WriteLine();
        Console.WriteLine($"  Total records : {allRecords.Count}");
        Console.WriteLine($"  Skipped rows  : {skippedRows.Count}");

        WriteStaging(allRecords);
        TableWriter.WriteRows(SkippedPath, skippedRows);

        Console.WriteLine();
        Console.WriteLine($"=== 02 Complete. Wrote {OutputPath}");
        Console.WriteLine($"                       {SkippedPath}");
    }

    private static List<RawRecord> ExtractSheet(
        IXLWorksheet ws,
        string sheetName,
        List<IDictionary<string, string>> skippedRows)
    {
        var records = new List<RawRecord>();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;

        var paramColumnMap = BuildParameterColumnMap(ws);
        var mergeDateValues  = BuildMergeDateValues(ws); 

        string currentLabel    = "";
        int    currentLabelRow = 0;

        for (int r = 13; r <= lastRow; r++)
        {
            var aText = ExcelText.Read(ws.Cell(r, 1));
            var bText = ExcelText.Read(ws.Cell(r, 2));

            if (aText.StartsWith("Column", StringComparison.OrdinalIgnoreCase))
            {
                LogSkipped(skippedRows, sheetName, r, "PLACEHOLDER_ROW", aText);
                continue;
            }

            if (aText.StartsWith("*") || aText.StartsWith("0.048"))
            {
                LogSkipped(skippedRows, sheetName, r, "FOOTER_NOTE", aText);
                continue;
            }

            if (aText.StartsWith("CY", StringComparison.OrdinalIgnoreCase))
            {
                var (ok, _, _) = Parsers.TryParseMonthLabel(aText);
                if (ok)
                {
                    currentLabel    = aText;
                    currentLabelRow = r;
                }
                continue;
            }

            if (Regex.IsMatch(aText, @"^\d+$") && !string.IsNullOrWhiteSpace(bText))
            {
                var rec = new RawRecord
                {
                    SourceSheet       = sheetName,
                    SourceRow         = r,
                    YearMonthLabel    = currentLabel,
                    YearMonthLabelRow = currentLabelRow,
                    StationRaw        = bText,
                    Latitude          = ExcelText.Read(ws.Cell(r, 3)),
                    Longitude         = ExcelText.Read(ws.Cell(r, 4)),
                    DateText          = mergeDateValues.TryGetValue(r, out var mergedDate)
                                        ? mergedDate
                                        : ExcelText.Read(ws.Cell(r, 5)),
                    TimeText          = ExcelText.Read(ws.Cell(r, 6)),
                };

                foreach (var kv in paramColumnMap)
                    rec.Parameters[kv.Key] = ExcelText.Read(ws.Cell(r, kv.Value));

                records.Add(rec);
                continue;
            }

            if (IsBlankRow(ws, r))
                continue;


            LogSkipped(skippedRows, sheetName, r, "UNCLASSIFIED", aText);
        }

        return records;
    }

    private static Dictionary<string, int> BuildParameterColumnMap(IXLWorksheet ws)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;

        for (int c = 1; c <= lastCol; c++)
        {
            var headerRaw = ExcelText.Read(ws.Cell(9, c));
            var std = Schema.MatchParameter(headerRaw);
            if (std != null && !map.ContainsKey(std))
                map[std] = c;
        }
        return map;
    }

    private static Dictionary<int, string> BuildMergeDateValues(IXLWorksheet ws)
    {
        var map = new Dictionary<int, string>();

        foreach (var range in ws.MergedRanges)
        {
            if (range.FirstColumn().ColumnNumber() > 5) continue;
            if (range.LastColumn().ColumnNumber() < 5)  continue;

            var topLeftValue = ExcelText.Read(range.FirstCell());
            if (!Parsers.IsNoSamplingText(topLeftValue)) continue;

            int firstRow = range.FirstRow().RowNumber();
            int lastRow  = range.LastRow().RowNumber();

            for (int r = firstRow; r <= lastRow; r++)
                map[r] = topLeftValue;
        }

        return map;
    }

    private static bool IsBlankRow(IXLWorksheet ws, int r)
    {
        for (int c = 1; c <= 18; c++)
        {
            if (!string.IsNullOrWhiteSpace(ExcelText.Read(ws.Cell(r, c))))
                return false;
        }
        return true;
    }

    private static void LogSkipped(
        List<IDictionary<string, string>> skippedRows,
        string sheetName, int row, string reason, string preview)
    {
        skippedRows.Add(new Dictionary<string, string>
        {
            ["Sheet"]   = sheetName,
            ["Row"]     = row.ToString(CultureInfo.InvariantCulture),
            ["Reason"]  = reason,
            ["Preview"] = Truncate(preview, 120),
        });
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    private static void WriteStaging(List<RawRecord> records)
    {
        var headers = new List<string>
        {
            "SourceSheet", "SourceRow",
            "YearMonthLabel", "YearMonthLabelRow",
            "StationRaw", "Latitude", "Longitude",
            "DateText", "TimeText"
        };
        headers.AddRange(Schema.StandardParameterNames);

        var rows = new List<IReadOnlyList<string>>();

        foreach (var rec in records)
        {
            var row = new List<string>
            {
                rec.SourceSheet,
                rec.SourceRow.ToString(CultureInfo.InvariantCulture),
                rec.YearMonthLabel,
                rec.YearMonthLabelRow.ToString(CultureInfo.InvariantCulture),
                rec.StationRaw,
                rec.Latitude,
                rec.Longitude,
                rec.DateText,
                rec.TimeText,
            };

            foreach (var p in Schema.StandardParameterNames)
            {
                row.Add(rec.Parameters.TryGetValue(p, out var v) ? (v ?? "") : "");
            }
            rows.Add(row);
        }

        TableWriter.WriteTable(OutputPath, headers, rows);
    }
}