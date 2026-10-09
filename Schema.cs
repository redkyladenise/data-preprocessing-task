using System.Text.RegularExpressions;

namespace DataPreprocessingTask;
public static class Schema
{
    // --- Sheets ---
    public const string SheetMarilao    = "Marilao";
    public const string SheetMeycauayan = "Meycauayan";
    public const string SheetObando     = "Obando";
    public const string SheetIgnore     = "Table1 (3)";

    public static readonly string[] WaterBodySheets = { SheetMarilao, SheetMeycauayan, SheetObando };

    // --- Standard parameter names ---
    public static readonly string[] StandardParameterNames = {
        "Dissolved Oxygen, mg/L",
        "pH",
        "Temperature °C",
        "Biochemical Oxygen Demand, mg/L",
        "Total Suspended Solids, mg/L",
        "Color TCU",
        "Fecal Coliform, MPN/100mL",
        "Total Coliform, MPN/100mL",
        "Ammonia, mg/L",
        "Nitrates as Nitrogen, mg/L",
        "Phosphates as Phosphorous, mg/L",
        "Chlorides Cl, mg/L"
    };

    // --- Names used in output CSV ---
    public static readonly string[] SafeParameterNames = {
        "DissolvedOxygen_mgL",
        "pH",
        "Temperature_C",
        "BOD_mgL",
        "TSS_mgL",
        "Color_TCU",
        "FecalColiform_MPN",
        "TotalColiform_MPN",
        "Ammonia_mgL",
        "Nitrates_N_mgL",
        "Phosphates_P_mgL",
        "Chlorides_mgL"
    };

    // --- Non-parameter columns ---
    public const string ColStationRaw = "Name of Water Body";
    public const string ColLatitude   = "Latitude, North (degree)";
    public const string ColLongitude  = "Longitude, East (degree)";
    public const string ColDate       = "Date";
    public const string ColTime       = "Time";

    // --- Other columns ---
    public const string ColSourceSheet        = "SourceSheet";
    public const string ColSourceRow          = "SourceRow";
    public const string ColYear               = "Year";
    public const string ColMonth              = "Month";
    public const string ColYearMonth          = "YearMonth";
    public const string ColStationID          = "StationID";
    public const string ColIsOutlier          = "Is_Outlier";
    public const string ColOutlierParams      = "Outlier_Params";
    public const string ColSymbolValueParams  = "SymbolValue_Params";
    public const string ColImputedParams     = "Imputed_Params";
    public const string ColDateLabelMismatch  = "DateLabelMismatch";

    // --- Other params ---
    public const string ParamFecalColiform = "Fecal Coliform, MPN/100mL";
    public const string ParamTotalColiform = "Total Coliform, MPN/100mL";
    public const string ParamAmmonia       = "Ammonia, mg/L";
    public const string ParamTemperature   = "Temperature °C";
    public const string ParamColor         = "Color TCU";
    public const string ParamChlorides     = "Chlorides Cl, mg/L";

    // HEADER NORMALIZATION
    public static string NormalizeHeader(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();

        s = Regex.Replace(s, @"^\s*\d+\s*[\.\)\-]\s*", "");
        s = s.Replace("*", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(
                s,
                @"^Chlorides\s+Cl\s*-?\s*\(mg/L\)$",
                "Chlorides Cl, mg/L",
                RegexOptions.IgnoreCase);

        s = Regex.Replace(s, @"\s+", " ").Trim();

        return s;
    }

    public static string? MatchParameter(string? rawHeader)
    {
        var norm = NormalizeHeader(rawHeader);
        if (norm.Length == 0) return null;

        foreach (var std in StandardParameterNames)
        {
            if (string.Equals(norm, std, StringComparison.OrdinalIgnoreCase))
                return std;
        }

        foreach (var std in StandardParameterNames)
        {
            var key = std.Split(',')[0].Trim();
            if (norm.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return std;
        }

        return null;
    }
}

public class RawRecord
{
    public string SourceSheet     { get; set; } = "";
    public int    SourceRow       { get; set; }
    public string YearMonthLabel  { get; set; } = "";
    public string StationRaw      { get; set; } = "";
    public string Latitude        { get; set; } = "";
    public string Longitude       { get; set; } = "";
    public string DateText        { get; set; } = "";
    public string TimeText        { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public class CleanRecord
{
    public string    SourceSheet  { get; set; } = "";
    public int       SourceRow    { get; set; }
    public int       Year         { get; set; }
    public int       Month        { get; set; }
    public string    YearMonth    { get; set; } = ""; 
    public string    StationRaw   { get; set; } = "";
    public string    StationID    { get; set; } = "";
    public double?   Latitude     { get; set; }
    public double?   Longitude    { get; set; }
    public DateTime? SampleDate   { get; set; }
    public TimeSpan? SampleTime   { get; set; }
    public bool      DateLabelMismatch { get; set; }

    public Dictionary<string, double?> Parameters       { get; set; } = new();
    public HashSet<string> SymbolValueParams { get; set; } = new();
    public HashSet<string> OutlierParams     { get; set; } = new();
    public HashSet<string> ImputedParams    { get; set; } = new();
}