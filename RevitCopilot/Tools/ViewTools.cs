using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using static RevitCopilot.Tools.Json;
using static RevitCopilot.Tools.RevitHelpers;
using Color = Autodesk.Revit.DB.Color;

namespace RevitCopilot.Tools;

/// <summary>Colouring, filtering and isolating elements in the active view.</summary>
internal static class ViewTools
{
    private static readonly object CategoriesSchema =
        new { type = "array", items = new { type = "string" }, description = "Category names, e.g. ['Structural Columns', 'Structural Framing']." };

    private static readonly object OperatorSchema =
        new { type = "string", @enum = Operators, description = "How to compare. Numeric operators work on number/length parameters; values may include units, e.g. '450 mm'." };

    public static RevitTool[] All =>
    [
        new("color_elements",
            "Colour elements in the ACTIVE VIEW only (graphic overrides; the elements themselves are unchanged). Either give one colour for everything, or give color_by_parameter to give each distinct value its own colour (e.g. colour columns by Type or by Base Level) - the result includes the legend. Choose elements by categories and/or element ids.",
            new
            {
                categories = CategoriesSchema,
                element_ids = IdList("Specific elements to colour. Optional."),
                color = S("One colour for all: a name ('red'), hex ('#FF8800') or 'R,G,B'. Ignored when color_by_parameter is set."),
                color_by_parameter = S("Parameter whose values decide the colour, e.g. 'Type', 'Base Level', 'Mark'. Optional."),
                transparency = I("Surface transparency 0-100. Default 0."),
            },
            [], true, ColorElements),

        new("clear_colors",
            "Remove the colour/graphic overrides from elements in the active view. Give categories and/or element ids; with neither, clears overrides on every element visible in the view.",
            new { categories = CategoriesSchema, element_ids = IdList("Elements to reset. Optional.") },
            [], true, (app, input) =>
            {
                Document doc = Doc(app);
                View view = ActiveGraphicalView(app);
                List<ElementId> ids = Targets(doc, view, input, allIfEmpty: true);
                string warnings = InTransaction(doc, "Clear colours", () =>
                {
                    var none = new OverrideGraphicSettings();
                    foreach (ElementId id in ids) view.SetElementOverrides(id, none);
                });
                return Result(new { view = view.Name, cleared = ids.Count, warnings });
            }),

        new("create_view_filter",
            "Create (or update) a Revit view filter - a rule-based filter saved in the model that you can see in Visibility/Graphics > Filters - and apply it to the active view, optionally colouring or hiding what it matches. Example: categories ['Structural Columns','Structural Framing'], parameter 'Structural Material', operator 'contains', value 'Concrete', color 'orange'. With no rule it matches every element of the categories.",
            new
            {
                name = S("Filter name, e.g. 'Concrete Columns and Beams'."),
                categories = CategoriesSchema,
                parameter_name = S("Parameter for the rule. Optional."),
                @operator = OperatorSchema,
                value = S("Value to compare with. Not needed for has_value / has_no_value."),
                color = S("Colour for matching elements. Optional."),
                visible = new { type = "boolean", description = "false hides matching elements in the view. Default true." },
                apply_to_active_view = new { type = "boolean", description = "Default true." },
            },
            ["name", "categories"], true, CreateViewFilter),

        new("isolate_in_view",
            "Temporarily isolate or hide elements in the active view (like the sunglasses 'Temporary Hide/Isolate' button), or reset it. This is a temporary view state, not a model change.",
            new
            {
                mode = new { type = "string", @enum = new[] { "isolate", "hide", "reset" }, description = "isolate = show only these; hide = hide these; reset = show everything again." },
                categories = CategoriesSchema,
                element_ids = IdList("Elements. Optional."),
            },
            ["mode"], false, (app, input) =>
            {
                Document doc = Doc(app);
                View view = ActiveGraphicalView(app);
                string mode = input.Str("mode");
                int count = 0;
                InTransaction(doc, "Temporary hide/isolate", () =>
                {
                    if (mode == "reset")
                    {
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        return;
                    }
                    List<ElementId> ids = Targets(doc, view, input, allIfEmpty: false);
                    if (ids.Count == 0) throw new ArgumentException("No matching elements in this view.");
                    count = ids.Count;
                    if (mode == "isolate") view.IsolateElementsTemporary(ids);
                    else view.HideElementsTemporary(ids);
                });
                return Result(new { view = view.Name, mode, elements = count });
            }),
    ];

    /// <summary>Elements chosen by "element_ids" and/or "categories", limited to what the view shows.</summary>
    private static List<ElementId> Targets(Document doc, View view, JsonElement input, bool allIfEmpty)
    {
        var ids = new List<ElementId>();
        if (input.TryGetProperty("element_ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
            ids.AddRange(input.Ids("element_ids"));
        List<Category> cats = Categories(doc, input);
        ids.AddRange(Instances(doc, cats, view).Select(e => e.Id));
        if (ids.Count == 0 && allIfEmpty && cats.Count == 0)
            ids.AddRange(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds());
        return ids.Distinct().ToList();
    }

    private static string ColorElements(UIApplication app, JsonElement input)
    {
        Document doc = Doc(app);
        View view = ActiveGraphicalView(app);
        if (!view.AreGraphicsOverridesAllowed())
            throw new InvalidOperationException($"View '{view.Name}' does not allow graphic overrides.");
        List<ElementId> ids = Targets(doc, view, input, allIfEmpty: false);
        if (ids.Count == 0) throw new ArgumentException("No matching elements are visible in the active view.");

        int transparency = input.OptInt("transparency", 0);
        string? byParam = input.OptStr("color_by_parameter");
        var legend = new List<object>();

        string warnings = InTransaction(doc, "Colour elements", () =>
        {
            if (byParam is null)
            {
                Color color = ParseColor(input.OptStr("color") ?? "red");
                OverrideGraphicSettings ogs = ColorOverride(doc, color, transparency);
                foreach (ElementId id in ids) view.SetElementOverrides(id, ogs);
                legend.Add(new { value = "(all)", color = Hex(color), count = ids.Count });
                return;
            }

            // "Type" means the element's type name; anything else is looked up as a parameter.
            string ValueOf(Element e) => byParam.Equals("Type", StringComparison.OrdinalIgnoreCase)
                ? doc.GetElement(e.GetTypeId())?.Name ?? "(none)"
                : (FindParam(doc, e, byParam) is { } p ? ParamValue(p) : null) is { Length: > 0 } v ? v : "(empty)";

            var groups = ids.Select(doc.GetElement).Where(e => e is not null)
                .GroupBy(ValueOf).OrderBy(g => g.Key).ToList();
            for (int i = 0; i < groups.Count; i++)
            {
                Color color = Palette[i % Palette.Length];
                OverrideGraphicSettings ogs = ColorOverride(doc, color, transparency);
                foreach (Element e in groups[i]) view.SetElementOverrides(e.Id, ogs);
                legend.Add(new { value = groups[i].Key, color = Hex(color), count = groups[i].Count() });
            }
        });
        return Result(new { view = view.Name, colored = ids.Count, legend, warnings });
    }

    private static string CreateViewFilter(UIApplication app, JsonElement input)
    {
        Document doc = Doc(app);
        string name = input.Str("name");
        List<ElementId> catIds = Categories(doc, input).Select(c => c.Id).ToList();
        if (catIds.Count == 0) throw new ArgumentException("Give at least one category.");

        ICollection<ElementId> filterable = ParameterFilterUtilities.GetAllFilterableCategories();
        List<ElementId> notFilterable = catIds.Where(id => !filterable.Contains(id)).ToList();
        if (notFilterable.Count > 0)
            throw new ArgumentException("These categories can't be used in view filters: " +
                string.Join(", ", notFilterable.Select(id => Category.GetCategory(doc, id)?.Name)));

        ElementFilter? rule = BuildRule(doc, catIds, input);
        bool apply = !input.TryGetProperty("apply_to_active_view", out var a) || a.ValueKind != JsonValueKind.False;
        bool visible = !input.TryGetProperty("visible", out var v) || v.ValueKind != JsonValueKind.False;
        string? colorText = input.OptStr("color");
        View? view = apply ? ActiveGraphicalView(app) : null;
        if (view is not null && view.ViewTemplateId != ElementId.InvalidElementId)
            throw new InvalidOperationException($"View '{view.Name}' is controlled by a view template, so filters must be added to the template. Remove the template from the view or set apply_to_active_view=false.");

        bool updated = false;
        long filterId = 0;
        string warnings = InTransaction(doc, $"View filter {name}", () =>
        {
            ParameterFilterElement? filter = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (filter is null)
            {
                filter = rule is null
                    ? ParameterFilterElement.Create(doc, name, catIds)
                    : ParameterFilterElement.Create(doc, name, catIds, rule);
            }
            else
            {
                updated = true;
                filter.SetCategories(catIds);
                if (rule is not null) filter.SetElementFilter(rule);
                else filter.ClearRules();
            }
            filterId = filter.Id.Value;

            if (view is not null)
            {
                if (!view.GetFilters().Contains(filter.Id)) view.AddFilter(filter.Id);
                view.SetFilterVisibility(filter.Id, visible);
                view.SetFilterOverrides(filter.Id, colorText is null ? new OverrideGraphicSettings() : ColorOverride(doc, ParseColor(colorText)));
            }
        });

        // Count what it catches, so the user gets immediate feedback.
        int matches = Instances(doc, catIds.Select(id => Category.GetCategory(doc, id)), view)
            .Count(e => rule is null || rule.PassesFilter(e));
        return Result(new { filter = name, id = filterId, updated, applied_to_view = view?.Name, visible, color = colorText, matching_elements = matches, warnings });
    }

    /// <summary>Turns parameter_name / operator / value into a Revit filter rule.</summary>
    private static ElementFilter? BuildRule(Document doc, List<ElementId> catIds, JsonElement input)
    {
        string? pName = input.OptStr("parameter_name");
        if (pName is null) return null;
        string op = input.OptStr("operator") ?? "equals";
        string value = input.OptStr("value") ?? "";

        // Only parameters every chosen category shares can be used in the rule.
        ElementId? paramId = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, catIds)
            .FirstOrDefault(id => FilterParamName(doc, id).Equals(pName, StringComparison.OrdinalIgnoreCase));
        if (paramId is null)
        {
            var names = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, catIds)
                .Select(id => FilterParamName(doc, id)).Where(n => n.Length > 0).OrderBy(n => n).Take(60);
            throw new ArgumentException($"'{pName}' can't be used to filter these categories together. Usable parameters include: {string.Join(", ", names)}");
        }

        (StorageType storage, ForgeTypeId? spec) = ParamKind(doc, paramId, catIds, pName);

        FilterRule filterRule = op switch
        {
            "has_value" => ParameterFilterRuleFactory.CreateHasValueParameterRule(paramId),
            "has_no_value" => ParameterFilterRuleFactory.CreateHasNoValueParameterRule(paramId),
            _ when storage == StorageType.String || op is "contains" or "not_contains" or "begins_with" => op switch
            {
                "equals" => ParameterFilterRuleFactory.CreateEqualsRule(paramId, value),
                "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, value),
                "contains" => ParameterFilterRuleFactory.CreateContainsRule(paramId, value),
                "not_contains" => ParameterFilterRuleFactory.CreateNotContainsRule(paramId, value),
                "begins_with" => ParameterFilterRuleFactory.CreateBeginsWithRule(paramId, value),
                "greater" => ParameterFilterRuleFactory.CreateGreaterRule(paramId, value),
                "greater_or_equal" => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(paramId, value),
                "less" => ParameterFilterRuleFactory.CreateLessRule(paramId, value),
                "less_or_equal" => ParameterFilterRuleFactory.CreateLessOrEqualRule(paramId, value),
                _ => throw new ArgumentException($"Unknown operator '{op}'."),
            },
            _ when storage == StorageType.Integer => IntRule(op, paramId, (int)Math.Round(ParseNumber(doc, spec, value))),
            _ when storage == StorageType.Double => DoubleRule(op, paramId, ParseNumber(doc, spec, value)),
            _ => throw new ArgumentException($"'{pName}' refers to other elements (e.g. a level or material id); filter on a text parameter instead, or use operator 'has_value'."),
        };
        return new ElementParameterFilter(filterRule);
    }

    private static FilterRule IntRule(string op, ElementId id, int v) => op switch
    {
        "equals" => ParameterFilterRuleFactory.CreateEqualsRule(id, v),
        "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(id, v),
        "greater" => ParameterFilterRuleFactory.CreateGreaterRule(id, v),
        "greater_or_equal" => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, v),
        "less" => ParameterFilterRuleFactory.CreateLessRule(id, v),
        "less_or_equal" => ParameterFilterRuleFactory.CreateLessOrEqualRule(id, v),
        _ => throw new ArgumentException($"Unknown operator '{op}'."),
    };

    private static FilterRule DoubleRule(string op, ElementId id, double v)
    {
        const double eps = 1e-6;
        return op switch
        {
            "equals" => ParameterFilterRuleFactory.CreateEqualsRule(id, v, eps),
            "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(id, v, eps),
            "greater" => ParameterFilterRuleFactory.CreateGreaterRule(id, v, eps),
            "greater_or_equal" => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, v, eps),
            "less" => ParameterFilterRuleFactory.CreateLessRule(id, v, eps),
            "less_or_equal" => ParameterFilterRuleFactory.CreateLessOrEqualRule(id, v, eps),
            _ => throw new ArgumentException($"Unknown operator '{op}'."),
        };
    }

    private static string FilterParamName(Document doc, ElementId id)
    {
        if (id.Value < 0)
        {
            try { return LabelUtils.GetLabelFor((BuiltInParameter)id.Value); }
            catch { return ""; }
        }
        return doc.GetElement(id)?.Name ?? "";
    }

    /// <summary>Storage type and unit spec of a filter parameter, read from a real element when possible.</summary>
    private static (StorageType, ForgeTypeId?) ParamKind(Document doc, ElementId paramId, List<ElementId> catIds, string name)
    {
        foreach (Element e in Instances(doc, catIds.Select(id => Category.GetCategory(doc, id)), null).Take(200))
        {
            Parameter? p = FindParam(doc, e, name);
            if (p is not null && p.Id == paramId) return (p.StorageType, p.Definition.GetDataType());
        }
        if (paramId.Value < 0) return (doc.get_TypeOfStorage((BuiltInParameter)paramId.Value), null);
        ForgeTypeId spec = (doc.GetElement(paramId) as ParameterElement)?.GetDefinition().GetDataType() ?? SpecTypeId.String.Text;
        if (spec == SpecTypeId.String.Text) return (StorageType.String, spec);
        if (spec == SpecTypeId.Int.Integer || spec == SpecTypeId.Boolean.YesNo) return (StorageType.Integer, spec);
        return (StorageType.Double, spec);
    }
}
