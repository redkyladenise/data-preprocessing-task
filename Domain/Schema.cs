using System.Text.RegularExpressions;

namespace DataPreprocessingTask.Domain;

public static class Schema
{
    public const string SheetMarilao    = "Marilao";
    public const string SheetMeycauayan = "Meycauayan";
    public const string SheetObando     = "Obando";
    public const string SheetIgnore     = "Table1 (3)";

    public static readonly string[] WaterBodySheets = { SheetMarilao, SheetMeycauayan, SheetObando };
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

    // column names used in final CSV
    public static readonly string[] SafeParameterNames = {
        "DissolvedOxygen_mgL",
        "pH",
        "Temperature_C",
        "BiochemicalOxygenDemand_mgL",
        "TotalSuspendedSolids_mgL",
        "Color_TCU",
        "FecalColiform_MPN_per100mL",
        "TotalColiform_MPN_per100mL",
        "Ammonia_mgL",
        "NitratesAsNitrogen_mgL",
        "PhosphatesAsPhosphorous_mgL",
        "ChloridesCl_mgL"
    };

    public static readonly string[] FinalColumnOrder = {
        "WaterBody", "StationID", "StationName", "Latitude", "Longitude",
        "Year", "Month", "Date", "Time",
        "DissolvedOxygen_mgL", "pH", "Temperature_C", "BiochemicalOxygenDemand_mgL", "TotalSuspendedSolids_mgL",
        "Color_TCU", "FecalColiform_MPN_per100mL", "TotalColiform_MPN_per100mL", 
        "Ammonia_mgL", "NitratesAsNitrogen_mgL", "PhosphatesAsPhosphorous_mgL", "ChloridesCl_mgL"
    };

    public static readonly Dictionary<string, string> ParameterToSafeName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dissolved Oxygen, mg/L"]           = "DissolvedOxygen_mgL",
        ["pH"]                               = "pH",
        ["Temperature °C"]                   = "Temperature_C",
        ["Biochemical Oxygen Demand, mg/L"]  = "BiochemicalOxygenDemand_mgL",
        ["Total Suspended Solids, mg/L"]     = "TotalSuspendedSolids_mgL",
        ["Color TCU"]                        = "Color_TCU",
        ["Fecal Coliform, MPN/100mL"]        = "FecalColiform_MPN_per100mL",
        ["Total Coliform, MPN/100mL"]        = "TotalColiform_MPN_per100mL",
        ["Ammonia, mg/L"]                    = "Ammonia_mgL",
        ["Nitrates as Nitrogen, mg/L"]       = "NitratesAsNitrogen_mgL",
        ["Phosphates as Phosphorous, mg/L"]  = "PhosphatesAsPhosphorous_mgL",
        ["Chlorides Cl, mg/L"]               = "ChloridesCl_mgL"
    };

    public static readonly Dictionary<string, int> ImputedDecimalPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dissolved Oxygen, mg/L"]           = 2,
        ["pH"]                               = 2,
        ["Temperature °C"]                   = 1,
        ["Biochemical Oxygen Demand, mg/L"]  = 0,
        ["Total Suspended Solids, mg/L"]     = 0,
        ["Color TCU"]                        = 0,
        ["Fecal Coliform, MPN/100mL"]        = 0,
        ["Total Coliform, MPN/100mL"]        = 0,
        ["Ammonia, mg/L"]                    = 2,
        ["Nitrates as Nitrogen, mg/L"]       = 3,
        ["Phosphates as Phosphorous, mg/L"]  = 3,
        ["Chlorides Cl, mg/L"]               = 0
    };

    public const int LatLonDecimalPlaces = 6;

    public const string ColStationRaw = "Name of Water Body";
    public const string ColLatitude   = "Latitude, North (degree)";
    public const string ColLongitude  = "Longitude, East (degree)";
    public const string ColDate       = "Date";
    public const string ColTime       = "Time";

    public const string ColSourceSheet        = "SourceSheet";
    public const string ColSourceRow          = "SourceRow";
    public const string ColYear               = "Year";
    public const string ColMonth              = "Month";
    public const string ColYearMonth          = "YearMonth";
    public const string ColStationID          = "StationID";
    public const string ColIsOutlier          = "Is_Outlier";
    public const string ColOutlierParams      = "Outlier_Params";
    public const string ColSymbolValueParams  = "SymbolValue_Params";
    public const string ColImputedParams      = "Imputed_Params";
    public const string ColDateLabelMismatch  = "DateLabelMismatch";

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
    public int    YearMonthLabelRow  { get; set; }
    public string StationRaw      { get; set; } = "";
    public string Latitude        { get; set; } = "";
    public string Longitude       { get; set; } = "";
    public string DateText        { get; set; } = "";
    public string TimeText        { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new();
}