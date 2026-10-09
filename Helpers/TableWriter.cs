using System.Globalization;
using System.Text;
using CsvHelper;

namespace DataPreprocessingTask.Helpers;

public static class TableWriter
{
    public static void WriteTable(
        string path,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string>> rows)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        // header row
        foreach (var h in headers) csv.WriteField(h);
        csv.NextRecord();

        // data rows
        foreach (var r in rows)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                var value = (r != null && i < r.Count) ? r[i] : "";
                csv.WriteField(value ?? "");
            }
            csv.NextRecord();
        }
    }

    public static void WriteRows(
        string path,
        IEnumerable<IDictionary<string, string>> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            var dir0 = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir0))
                Directory.CreateDirectory(dir0);
            File.WriteAllText(path, "", new UTF8Encoding(true));
            return;
        }

        var headers = list[0].Keys.ToList();

        var table = new List<IReadOnlyList<string>>();
        foreach (var row in list)
        {
            var line = new List<string>(headers.Count);
            foreach (var h in headers)
                line.Add(row.TryGetValue(h, out var v) ? (v ?? "") : "");
            table.Add(line);
        }

        WriteTable(path, headers, table);
    }

    public static void WriteRaw(
        string path,
        IEnumerable<string> headers,
        IEnumerable<string[]> rows)
    {
        var headerList = headers.ToList();
        WriteTable(path, headerList, rows.Select(r => (IReadOnlyList<string>)r));
    }
}