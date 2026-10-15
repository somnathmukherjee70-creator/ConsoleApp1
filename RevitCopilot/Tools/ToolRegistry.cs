using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using static RevitCopilot.Tools.Json;

namespace RevitCopilot.Tools;

/// <summary>
/// The set of actions the copilot may take. To teach the copilot something new,
/// add one <see cref="RevitTool"/> here - Claude decides when to call it from its description.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, RevitTool> _tools;

    public ToolRegistry(IEnumerable<RevitTool> tools) => _tools = tools.ToDictionary(t => t.Name);

    public IReadOnlyCollection<RevitTool> All => _tools.Values;

    public RevitTool? Find(string name) => _tools.GetValueOrDefault(name);

    public static ToolRegistry CreateDefault() => new(
    [
        // ---------- Read-only: look at the model ----------
        new("get_model_info",
            "Overview of the open Revit model: title, file path, active view, project length unit, and counts of levels, views, sheets, rooms and walls. Call this first when you need context.",
            new { }, [], false, (app, _) =>
            {
                Document doc = Doc(app);
                int Count(BuiltInCategory c) => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType().GetElementCount();
                return Result(new
                {
                    title = doc.Title,
                    path = doc.PathName,
                    active_view = doc.ActiveView?.Name,
                    active_view_type = doc.ActiveView?.ViewType.ToString(),
                    length_unit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId,
                    levels = Count(BuiltInCategory.OST_Levels),
                    views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate),
                    sheets = Count(BuiltInCategory.OST_Sheets),
                    rooms = Count(BuiltInCategory.OST_Rooms),
                    walls = Count(BuiltInCategory.OST_Walls),
                });
            }),

        new("get_selection",
            "List the elements the user currently has selected in Revit (id, category, name, type, level).",
            new { }, [], false, (app, _) =>
            {
                Document doc = Doc(app);
                var ids = app.ActiveUIDocument.Selection.GetElementIds();
                return Result(new { count = ids.Count, elements = ids.Take(200).Select(id => Summary(doc, doc.GetElement(id))) });
            }),

        new("list_levels",
            "List all levels with their elevations in metres.",
            new { }, [], false, (app, _) =>
            {
                var levels = new FilteredElementCollector(Doc(app)).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => new { id = l.Id.Value, name = l.Name, elevation_m = Units.ToMetres(l.Elevation) });
                return Result(levels);
            }),

        new("count_elements",
            "Count element instances. With a category name (e.g. 'Walls', 'Doors', 'Windows', 'Furniture') returns the count for that category grouped by type; without one returns counts for every category that has elements.",
            new { category = S("Revit category name, e.g. 'Doors'. Optional.") }, [], false, (app, input) =>
            {
                Document doc = Doc(app);
                string? name = input.OptStr("category");
                if (name is not null)
                {
                    var byType = Instances(doc, FindCategory(doc, name))
                        .GroupBy(e => doc.GetElement(e.GetTypeId())?.Name ?? "(no type)")
                        .OrderByDescending(g => g.Count())
                        .Select(g => new { type = g.Key, count = g.Count() }).ToList();
                    return Result(new { category = name, total = byType.Sum(t => t.count), by_type = byType });
                }
                var all = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .Where(e => e.Category is { CategoryType: CategoryType.Model or CategoryType.Annotation })
                    .GroupBy(e => e.Category.Name)
                    .OrderByDescending(g => g.Count())
                    .Select(g => new { category = g.Key, count = g.Count() });
                return Result(all);
            }),

        new("find_elements",
            "Find element instances of a category, optionally filtered by level name and/or a parameter whose value contains some text. Returns id, name, type and level so the ids can be used with other tools.",
            new
            {
                category = S("Revit category name, e.g. 'Walls'."),
                level = S("Only elements on this level (exact level name). Optional."),
                parameter_name = S("Filter by this parameter. Optional."),
                parameter_value = S("Text the parameter value must contain (case-insensitive). Optional."),
                limit = I("Maximum results, default 100."),
            },
            ["category"], false, (app, input) =>
            {
                Document doc = Doc(app);
                IEnumerable<Element> found = Instances(doc, FindCategory(doc, input.Str("category")));
                if (input.OptStr("level") is { } levelName)
                {
                    ElementId levelId = FindLevel(doc, levelName).Id;
                    found = found.Where(e => e.LevelId == levelId);
                }
                if (input.OptStr("parameter_name") is { } pName)
                {
                    string want = input.OptStr("parameter_value") ?? "";
                    found = found.Where(e => e.LookupParameter(pName) is { } p &&
                        (ParamValue(p) ?? "").Contains(want, StringComparison.OrdinalIgnoreCase));
                }
                var list = found.ToList();
                int limit = input.OptInt("limit", 100);
                return Result(new { total = list.Count, truncated = list.Count > limit, elements = list.Take(limit).Select(e => Summary(doc, e)) });
            }),

        new("get_element_parameters",
            "Read all parameters of one element (instance parameters, plus its type's parameters) with their current values and whether they can be edited.",
            new { element_id = I("Element id.") }, ["element_id"], false, (app, input) =>
            {
                Document doc = Doc(app);
                Element e = doc.GetElement(new ElementId((long)input.Num("element_id")))
                    ?? throw new ArgumentException("No element with that id.");
                static object Dump(Element el) => el.Parameters.Cast<Parameter>()
                    .Where(p => p.Definition is not null)
                    .OrderBy(p => p.Definition.Name)
                    .Select(p => new { name = p.Definition.Name, value = ParamValue(p), read_only = p.IsReadOnly, storage = p.StorageType.ToString() });
                Element? type = doc.GetElement(e.GetTypeId());
                return Result(new { element = Summary(doc, e), instance_parameters = Dump(e), type_parameters = type is null ? null : Dump(type) });
            }),

        new("list_rooms",
            "List rooms with number, name, level and area in square metres. Unplaced or unenclosed rooms have area 0.",
            new { level = S("Only rooms on this level. Optional.") }, [], false, (app, input) =>
            {
                Document doc = Doc(app);
                var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>();
                if (input.OptStr("level") is { } levelName)
                    rooms = rooms.Where(r => string.Equals(r.Level?.Name, levelName, StringComparison.OrdinalIgnoreCase));
                return Result(rooms.OrderBy(r => r.Number).Select(r => new
                {
                    id = r.Id.Value, number = r.Number, name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                    level = r.Level?.Name, area_m2 = Units.ToSquareMetres(r.Area), placed = r.Location is not null,
                }));
            }),

        new("list_views",
            "List views (not templates) with id, name and view type. Optionally filter by type, e.g. 'FloorPlan', 'CeilingPlan', 'Section', 'Elevation', 'ThreeD', 'DrawingSheet', 'Schedule'.",
            new { view_type = S("Revit ViewType name. Optional.") }, [], false, (app, input) =>
            {
                string? kind = input.OptStr("view_type");
                var views = new FilteredElementCollector(Doc(app)).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && (kind is null || v.ViewType.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name)
                    .Select(v => new { id = v.Id.Value, name = v.Name, view_type = v.ViewType.ToString() }).ToList();
                return Result(new { total = views.Count, views = views.Take(300) });
            }),

        new("list_sheets",
            "List sheets with id, number, name and how many views are placed on each.",
            new { }, [], false, (app, _) =>
            {
                var sheets = new FilteredElementCollector(Doc(app)).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsPlaceholder)
                    .OrderBy(s => s.SheetNumber)
                    .Select(s => new { id = s.Id.Value, number = s.SheetNumber, name = s.Name, placed_views = s.GetAllPlacedViews().Count });
                return Result(sheets);
            }),

        new("list_types",
            "List the available types (family types) for a category, e.g. wall types, door types, title blocks. Use before creating elements to pick a valid type name.",
            new { category = S("Revit category name, e.g. 'Walls', 'Doors', 'Title Blocks'.") }, ["category"], false, (app, input) =>
            {
                Document doc = Doc(app);
                Category cat = FindCategory(doc, input.Str("category"));
                var types = new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsElementType().Cast<ElementType>()
                    .Select(t => new { id = t.Id.Value, family = t.FamilyName, name = t.Name });
                return Result(types);
            }),

        new("select_elements",
            "Select elements in the Revit UI and zoom to them, so the user can see what you are talking about. Does not change the model.",
            new { element_ids = IdList("Element ids to select.") }, ["element_ids"], false, (app, input) =>
            {
                var ids = input.Ids("element_ids");
                UIDocument uidoc = app.ActiveUIDocument;
                uidoc.Selection.SetElementIds(ids);
                if (ids.Count > 0) uidoc.ShowElements(ids);
                return Result(new { selected = ids.Count });
            }),

        // ---------- Model changes: each runs in one undoable transaction ----------
        new("set_parameter",
            "Set a parameter to the same value on one or more elements. Values for numbers with units are interpreted in the project's display units (e.g. '2400' in a millimetre project) unless units are written in the value. Yes/No parameters accept 'yes'/'no'. Use target='type' to change the type parameter (affects every instance of that type).",
            new
            {
                element_ids = IdList("Elements to change."),
                parameter_name = S("Exact parameter name, e.g. 'Comments', 'Mark', 'Fire Rating'."),
                value = S("New value as text."),
                target = new { type = "string", @enum = new[] { "instance", "type" }, description = "Default 'instance'." },
            },
            ["element_ids", "parameter_name", "value"], true, (app, input) =>
            {
                Document doc = Doc(app);
                string pName = input.Str("parameter_name"), value = input.Str("value");
                bool onType = input.OptStr("target") == "type";
                var outcomes = new List<object>();
                string warnings = InTransaction(doc, $"Set {pName}", () =>
                {
                    foreach (ElementId id in input.Ids("element_ids").Distinct())
                    {
                        Element? e = doc.GetElement(id);
                        Element? target = onType && e is not null ? doc.GetElement(e.GetTypeId()) : e;
                        try
                        {
                            Parameter p = target?.LookupParameter(pName)
                                ?? throw new ArgumentException(e is null ? "element not found" : $"no {(onType ? "type" : "instance")} parameter '{pName}'");
                            SetParam(p, value);
                            outcomes.Add(new { id = id.Value, ok = true, value = ParamValue(p) });
                        }
                        catch (Exception ex)
                        {
                            outcomes.Add(new { id = id.Value, ok = false, error = ex.Message });
                        }
                    }
                });
                return Result(new { results = outcomes, warnings });
            }),

        new("create_walls",
            "Create a chain of straight walls through the given points (metres, in plan). Set closed=true to also join the last point back to the first, e.g. four corners make a rectangular room.",
            new
            {
                points = new { type = "array", items = PointSchema("[x, y] in metres"), minItems = 2, description = "Wall path points." },
                closed = new { type = "boolean", description = "Close the loop. Default false." },
                level = S("Base level name."),
                height_m = N("Unconnected height in metres. Default 3."),
                wall_type = S("Wall type name (or part of it). Optional; default wall type if omitted."),
            },
            ["points", "level"], true, (app, input) =>
            {
                Document doc = Doc(app);
                Level level = FindLevel(doc, input.Str("level"));
                var pts = input.GetProperty("points").EnumerateArray()
                    .Select(p => new XYZ(Units.FromMetres(p[0].GetDouble()), Units.FromMetres(p[1].GetDouble()), level.Elevation)).ToList();
                if (input.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True) pts.Add(pts[0]);
                WallType type = FindType<WallType>(doc, input.OptStr("wall_type"), ElementTypeGroup.WallType);
                double height = Units.FromMetres(input.OptNum("height_m") ?? 3);
                var created = new List<long>();
                string warnings = InTransaction(doc, "Create walls", () =>
                {
                    for (int i = 0; i < pts.Count - 1; i++)
                        created.Add(Wall.Create(doc, Line.CreateBound(pts[i], pts[i + 1]), type.Id, level.Id, height, 0, false, false).Id.Value);
                });
                return Result(new { created_wall_ids = created, wall_type = type.Name, warnings });
            }),

        new("create_level",
            "Create a new level at an elevation (metres), with a matching floor plan view.",
            new { name = S("Level name."), elevation_m = N("Elevation in metres.") },
            ["name", "elevation_m"], true, (app, input) =>
            {
                Document doc = Doc(app);
                long levelId = 0, viewId = 0;
                string warnings = InTransaction(doc, "Create level", () =>
                {
                    Level level = Level.Create(doc, Units.FromMetres(input.Num("elevation_m")));
                    level.Name = input.Str("name");
                    levelId = level.Id.Value;
                    ViewFamilyType? planType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                        .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
                    if (planType is not null) viewId = ViewPlan.Create(doc, planType.Id, level.Id).Id.Value;
                });
                return Result(new { level_id = levelId, floor_plan_id = viewId, warnings });
            }),

        new("create_sheets",
            "Create one or more sheets with a title block.",
            new
            {
                sheets = new
                {
                    type = "array",
                    items = new { type = "object", properties = new { number = S("Sheet number"), name = S("Sheet name") }, required = new[] { "number", "name" } },
                },
                title_block = S("Title block type name (or part of it). Optional; first title block if omitted."),
            },
            ["sheets"], true, (app, input) =>
            {
                Document doc = Doc(app);
                string? wanted = input.OptStr("title_block");
                ElementId titleBlock = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType()
                    .FirstOrDefault(t => wanted is null || t.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))?.Id
                    ?? throw new InvalidOperationException("No matching title block is loaded in this model.");
                var created = new List<object>();
                string warnings = InTransaction(doc, "Create sheets", () =>
                {
                    foreach (JsonElement s in input.GetProperty("sheets").EnumerateArray())
                    {
                        ViewSheet sheet = ViewSheet.Create(doc, titleBlock);
                        sheet.SheetNumber = s.Str("number");
                        sheet.Name = s.Str("name");
                        created.Add(new { id = sheet.Id.Value, number = sheet.SheetNumber, name = sheet.Name });
                    }
                });
                return Result(new { created, warnings });
            }),

        new("rename_views",
            "Find-and-replace text in view names (sheets excluded). Case-sensitive. Returns old and new names.",
            new { find = S("Text to find."), replace = S("Replacement text (can be empty).") },
            ["find", "replace"], true, (app, input) =>
            {
                Document doc = Doc(app);
                string find = input.Str("find"), replace = input.Str("replace");
                if (find.Length == 0) throw new ArgumentException("'find' must not be empty.");
                var renamed = new List<object>();
                string warnings = InTransaction(doc, "Rename views", () =>
                {
                    foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                                 .Where(v => !v.IsTemplate && v is not ViewSheet && v.Name.Contains(find)).ToList())
                    {
                        string old = v.Name;
                        try { v.Name = old.Replace(find, replace); renamed.Add(new { id = v.Id.Value, old, @new = v.Name }); }
                        catch (Exception ex) { renamed.Add(new { id = v.Id.Value, old, error = ex.Message }); }
                    }
                });
                return Result(new { renamed, warnings });
            }),

        new("delete_elements",
            "Delete elements by id. Dependent elements (e.g. hosted doors in a deleted wall) are deleted too; the result lists everything removed.",
            new { element_ids = IdList("Element ids to delete.") }, ["element_ids"], true, (app, input) =>
            {
                Document doc = Doc(app);
                ICollection<ElementId> deleted = [];
                string warnings = InTransaction(doc, "Delete elements", () => deleted = doc.Delete(input.Ids("element_ids")));
                return Result(new { deleted_count = deleted.Count, deleted_ids = deleted.Select(i => i.Value), warnings });
            }),
    ]);

    // ---------- helpers ----------

    private static Document Doc(UIApplication app) =>
        app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No Revit model is open.");

    private static IEnumerable<Element> Instances(Document doc, Category cat) =>
        new FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType().ToElements();

    private static Category FindCategory(Document doc, string name)
    {
        foreach (Category c in doc.Settings.Categories)
            if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(name + "s", StringComparison.OrdinalIgnoreCase))
                return c;
        throw new ArgumentException($"No category named '{name}'. Call count_elements without a category to see category names.");
    }

    private static Level FindLevel(Document doc, string name) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No level named '{name}'. Call list_levels to see level names.");

    private static T FindType<T>(Document doc, string? name, ElementTypeGroup fallback) where T : ElementType
    {
        if (name is null) return (T)doc.GetElement(doc.GetDefaultElementTypeId(fallback));
        return new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                   .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                   .FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No {typeof(T).Name} matching '{name}'. Call list_types to see options.");
    }

    private static object Summary(Document doc, Element e) => new
    {
        id = e.Id.Value,
        category = e.Category?.Name,
        name = e.Name,
        type = doc.GetElement(e.GetTypeId())?.Name,
        level = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null,
    };

    private static string? ParamValue(Parameter p) => p.StorageType switch
    {
        StorageType.String => p.AsString(),
        StorageType.ElementId => p.AsValueString() ?? p.AsElementId().Value.ToString(),
        _ => p.AsValueString() ?? (p.StorageType == StorageType.Integer ? p.AsInteger().ToString() : p.AsDouble().ToString()),
    };

    private static void SetParam(Parameter p, string value)
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
    private static string InTransaction(Document doc, string name, Action work)
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

    private sealed class WarningCollector : IFailuresPreprocessor
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
