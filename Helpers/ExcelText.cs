using System.Globalization;
using ClosedXML.Excel;

namespace DataPreprocessingTask.Helpers;

public static class ExcelText
{
    // to read any cell as text
    public static string Read(IXLCell? cell)
    {
        if (cell == null) return "";

        XLCellValue v;
        try { v = cell.Value; } catch { return ""; }

        // plain tedxt
        try { 
            if (v.IsText) return (v.GetText() ?? "").Trim(); 
        } catch { }

        // dates
        try {
            if (v.IsDateTime)
            {
                var dt = v.GetDateTime();
                if (dt.TimeOfDay == TimeSpan.Zero)
                    return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        } catch { }

        // numbers
        try {
            if (v.IsNumber)
            {
                var num = v.GetNumber();
                return num.ToString("0.############################", CultureInfo.InvariantCulture);
            }
        } catch { }

        // boolean
        try { if (v.IsBoolean) return v.GetBoolean() ? "TRUE" : "FALSE"; } catch { }

        try { return (v.ToString() ?? "").Trim(); } catch { return ""; }
    }
}