using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitCopilot.Tools;

/// <summary>Small helpers for reading tool inputs and producing tool results.</summary>
internal static class Json
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Result(object value) => JsonSerializer.Serialize(value, Options);

    public static string Str(this JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ArgumentException($"Missing required string '{name}'.");

    public static string? OptStr(this JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double Num(this JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : throw new ArgumentException($"Missing required number '{name}'.");

    public static double? OptNum(this JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static int OptInt(this JsonElement input, string name, int fallback) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    public static List<ElementId> Ids(this JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"Missing required array '{name}'.");
        return v.EnumerateArray().Select(e => new ElementId(e.GetInt64())).ToList();
    }

    public static XYZ Point(this JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2)
            throw new ArgumentException($"'{name}' must be [x, y] in metres.");
        return new XYZ(Units.FromMetres(v[0].GetDouble()), Units.FromMetres(v[1].GetDouble()), 0);
    }

    // Schema snippets for tool definitions.
    public static object S(string description) => new { type = "string", description };
    public static object N(string description) => new { type = "number", description };
    public static object I(string description) => new { type = "integer", description };
    public static object IdList(string description) => new { type = "array", items = new { type = "integer" }, description };
    public static object PointSchema(string description) =>
        new { type = "array", items = new { type = "number" }, minItems = 2, maxItems = 2, description };
}

/// <summary>Revit stores lengths in decimal feet internally; the copilot talks in metres.</summary>
internal static class Units
{
    public static double FromMetres(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
    public static double ToMetres(double ft) => Math.Round(UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters), 3);
    public static double ToSquareMetres(double sqft) => Math.Round(UnitUtils.ConvertFromInternalUnits(sqft, UnitTypeId.SquareMeters), 2);
}
