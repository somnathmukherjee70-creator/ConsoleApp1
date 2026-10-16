using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = Autodesk.Revit.DB.Color;

namespace RevitCopilot.Tools;

/// <summary>Shared Revit lookups and the transaction wrapper used by every tool.</summary>
internal static class RevitHelpers
{

    internal static Document Doc(UIApplication app) =>
        app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No Revit model is open.");

    internal static IEnumerable<Element> Instances(Document doc, Category cat) =>
        new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType().ToElements();

    /// <summary>Everyday words people use for categories whose Revit name differs.</summary>
    private static readonly Dictionary<string, string> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["beam"] = "Structural Framing", ["beams"] = "Structural Framing", ["framing"] = "Structural Framing",
        ["structural column"] = "Structural Columns", ["column"] = "Columns",
        ["foundation"] = "Structural Foundations", ["foundations"] = "Structural Foundations",
        ["slab"] = "Floors", ["slabs"] = "Floors", ["pipe"] = "Pipes", ["duct"] = "Ducts",
    };

    internal static Category FindCategory(Document doc, string name)
    {
        string wanted = CategoryAliases.GetValueOrDefault(name.Trim(), name.Trim());
        foreach (Category c in doc.Settings.Categories)
            if (c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(wanted + "s", StringComparison.OrdinalIgnoreCase))
                return c;
        throw new ArgumentException($"No category named '{name}'. Call count_elements without a category to see category names.");
    }

    /// <summary>Reads "categories" (array) and/or "category" (string) from a tool input.</summary>
    internal static List<Category> Categories(Document doc, System.Text.Json.JsonElement input)
    {
        var names = new List<string>();
        if (input.TryGetProperty("categories", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            names.AddRange(arr.EnumerateArray().Select(e => e.GetString()!).Where(n => !string.IsNullOrWhiteSpace(n)));
        if (input.TryGetProperty("category", out var one) && one.ValueKind == System.Text.Json.JsonValueKind.String)
            names.Add(one.GetString()!);
        return names.Select(n => FindCategory(doc, n)).DistinctBy(c => c.Id).ToList();
    }

    /// <summary>Instances of the categories, optionally only those visible in a view.</summary>
    internal static List<Element> Instances(Document doc, IEnumerable<Category> cats, View? inView)
    {
        var ids = cats.Select(c => c.Id).ToList();
        if (ids.Count == 0) return [];
        var collector = inView is null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, inView.Id);
        return collector.WherePasses(new ElementMulticategoryFilter(ids)).WhereElementIsNotElementType().ToElements().ToList();
    }

    /// <summary>Instance parameter by name, falling back to the element's type.</summary>
    internal static Parameter? FindParam(Document doc, Element e, string name) =>
        e.LookupParameter(name) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(name);

    // ---------- parameter comparison (used by find_elements and view filters) ----------

    internal static readonly string[] Operators =
        ["equals", "not_equals", "contains", "not_contains", "begins_with", "greater", "greater_or_equal", "less", "less_or_equal", "has_value", "has_no_value"];

    /// <summary>Converts user text (e.g. "450", "450 mm", "yes") to Revit's internal value for comparison.</summary>
    internal static double ParseNumber(Document doc, ForgeTypeId? spec, string value)
    {
        string v = value.Trim().ToLowerInvariant();
        if (v is "yes" or "true") return 1;
        if (v is "no" or "false") return 0;
        if (spec is not null && UnitUtils.IsMeasurableSpec(spec) && UnitFormatUtils.TryParse(doc.GetUnits(), spec, value, out double parsed))
            return parsed;
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
            ? d
            : throw new ArgumentException($"'{value}' is not a number.");
    }

    internal static bool Matches(Document doc, Parameter? p, string op, string value)
    {
        bool hasValue = p is not null && p.HasValue && !string.IsNullOrEmpty(ParamValue(p));
        if (op == "has_value") return hasValue;
        if (op == "has_no_value") return !hasValue;
        if (p is null) return op is "not_equals" or "not_contains";

        if (p.StorageType is StorageType.Double or StorageType.Integer && op is not ("contains" or "not_contains" or "begins_with"))
        {
            double actual = p.StorageType == StorageType.Double ? p.AsDouble() : p.AsInteger();
            double target = ParseNumber(doc, p.Definition.GetDataType(), value);
            const double eps = 1e-6;
            return op switch
            {
                "equals" => Math.Abs(actual - target) < eps,
                "not_equals" => Math.Abs(actual - target) >= eps,
                "greater" => actual > target + eps,
                "greater_or_equal" => actual > target - eps,
                "less" => actual < target - eps,
                "less_or_equal" => actual < target + eps,
                _ => throw new ArgumentException($"Unknown operator '{op}'."),
            };
        }

        string text = ParamValue(p) ?? "";
        return op switch
        {
            "equals" => text.Equals(value, StringComparison.OrdinalIgnoreCase),
            "not_equals" => !text.Equals(value, StringComparison.OrdinalIgnoreCase),
            "contains" => text.Contains(value, StringComparison.OrdinalIgnoreCase),
            "not_contains" => !text.Contains(value, StringComparison.OrdinalIgnoreCase),
            "begins_with" => text.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentException($"Operator '{op}' needs a numeric parameter."),
        };
    }

    // ---------- colours ----------

    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new(230, 57, 70), ["green"] = new(46, 160, 67), ["blue"] = new(37, 99, 235),
        ["yellow"] = new(250, 204, 21), ["orange"] = new(249, 115, 22), ["purple"] = new(147, 51, 234),
        ["pink"] = new(236, 72, 153), ["cyan"] = new(6, 182, 212), ["magenta"] = new(217, 70, 239),
        ["brown"] = new(146, 64, 14), ["grey"] = new(128, 128, 128), ["gray"] = new(128, 128, 128),
        ["black"] = new(0, 0, 0), ["white"] = new(255, 255, 255),
    };

    /// <summary>Distinct colours for "colour by value", in order.</summary>
    internal static readonly Color[] Palette =
    [
        new(230, 57, 70), new(37, 99, 235), new(46, 160, 67), new(249, 115, 22), new(147, 51, 234), new(6, 182, 212),
        new(250, 204, 21), new(236, 72, 153), new(146, 64, 14), new(20, 184, 166), new(132, 204, 22), new(100, 116, 139),
    ];

    internal static Color ParseColor(string text)
    {
        string t = text.Trim();
        if (NamedColors.TryGetValue(t, out Color? named)) return named;
        if (t.StartsWith('#') && t.Length == 7 && int.TryParse(t[1..], System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        string[] parts = t.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts.All(x => byte.TryParse(x, out _)))
            return new Color(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
        throw new ArgumentException($"Unknown colour '{text}'. Use a name like 'red', a hex code like '#FF8800', or 'R,G,B'.");
    }

    internal static string Hex(Color c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>Solid fill + coloured lines, for cut and projection, so the colour shows in plans, sections and 3D.</summary>
    internal static OverrideGraphicSettings ColorOverride(Document doc, Color color, int transparency = 0)
    {
        ElementId solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .First(f => f.GetFillPattern().IsSolidFill).Id;
        return new OverrideGraphicSettings()
            .SetSurfaceForegroundPatternId(solid).SetSurfaceForegroundPatternColor(color)
            .SetCutForegroundPatternId(solid).SetCutForegroundPatternColor(color)
            .SetProjectionLineColor(color).SetCutLineColor(color)
            .SetSurfaceTransparency(Math.Clamp(transparency, 0, 100));
    }

    internal static View ActiveGraphicalView(UIApplication app)
    {
        View view = Doc(app).ActiveView;
        if (view is ViewSchedule || view.ViewType is ViewType.DrawingSheet or ViewType.ProjectBrowser or ViewType.SystemBrowser)
            throw new InvalidOperationException($"The active view '{view.Name}' is not a model view. Open a plan, section, elevation or 3D view first.");
        return view;
    }

    internal static Level FindLevel(Document doc, string name) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No level named '{name}'. Call list_levels to see level names.");

    internal static T FindType<T>(Document doc, string? name, ElementTypeGroup fallback) where T : ElementType
    {
        if (name is null) return (T)doc.GetElement(doc.GetDefaultElementTypeId(fallback));
        return new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                   .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                   .FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No {typeof(T).Name} matching '{name}'. Call list_types to see options.");
    }

    internal static object Summary(Document doc, Element e) => new
    {
        id = e.Id.Value,
        category = e.Category?.Name,
        name = e.Name,
        type = doc.GetElement(e.GetTypeId())?.Name,
        level = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null,
    };

    internal static string? ParamValue(Parameter p) => p.StorageType switch
    {
        StorageType.String => p.AsString(),
        StorageType.ElementId => p.AsValueString() ?? p.AsElementId().Value.ToString(),
        _ => p.AsValueString() ?? (p.StorageType == StorageType.Integer ? p.AsInteger().ToString() : p.AsDouble().ToString()),
    };

    internal static void SetParam(Parameter p, string value)
    {
        if (p.IsReadOnly) throw new InvalidOperationException($"'{p.Definition.Name}' is read-only.");
        bool ok = p.StorageType switch
        {
            StorageType.String => p.Set(value),
            StorageType.Double => p.SetValueString(value) || (double.TryParse(value, out double d) && p.Set(d)),
            StorageType.Integer => value.Trim().ToLowerInvariant() switch
            {
                "yes" or "true" => p.Set(1),
                "no" or "false" => p.Set(0),
                _ => int.TryParse(value, out int i) ? p.Set(i) : p.SetValueString(value),
            },
            StorageType.ElementId => long.TryParse(value, out long id) && p.Set(new ElementId(id)),
            _ => false,
        };
        if (!ok) throw new ArgumentException($"Revit rejected value '{value}' for '{p.Definition.Name}'.");
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one named transaction (one Ctrl+Z step). Revit warnings are
    /// collected and returned instead of popping dialogs that would block the copilot.
    /// </summary>
    internal static string InTransaction(Document doc, string name, Action work)
    {
        using var t = new Transaction(doc, "Copilot: " + name);
        var collector = new WarningCollector();
        FailureHandlingOptions options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(collector);
        t.SetFailureHandlingOptions(options);
        t.Start();
        try
        {
            work();
            if (t.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit could not commit the change: " + string.Join("; ", collector.Messages));
        }
        catch
        {
            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
            throw;
        }
        return string.Join("; ", collector.Messages);
    }

    internal sealed class WarningCollector : IFailuresPreprocessor
    {
        public List<string> Messages { get; } = [];

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
            {
                Messages.Add(f.GetDescriptionText());
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
            }
            return FailureProcessingResult.Continue;
        }
    }
}
