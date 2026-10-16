using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using static RevitCopilot.Tools.Json;
using static RevitCopilot.Tools.RevitHelpers;

namespace RevitCopilot.Tools;

/// <summary>Creating parameters and updating their values.</summary>
internal static class ParameterTools
{
    private static readonly Dictionary<string, ForgeTypeId> DataTypes = new()
    {
        ["text"] = SpecTypeId.String.Text,
        ["integer"] = SpecTypeId.Int.Integer,
        ["number"] = SpecTypeId.Number,
        ["length"] = SpecTypeId.Length,
        ["area"] = SpecTypeId.Area,
        ["volume"] = SpecTypeId.Volume,
        ["angle"] = SpecTypeId.Angle,
        ["yes_no"] = SpecTypeId.Boolean.YesNo,
    };

    private static readonly Dictionary<string, ForgeTypeId> Groups = new()
    {
        ["data"] = GroupTypeId.Data,
        ["identity_data"] = GroupTypeId.IdentityData,
        ["text"] = GroupTypeId.Text,
        ["dimensions"] = GroupTypeId.Geometry,
        ["constraints"] = GroupTypeId.Constraints,
        ["structural"] = GroupTypeId.Structural,
        ["construction"] = GroupTypeId.Construction,
    };

    private const string SharedGroupName = "Revit Copilot";

    public static RevitTool[] All =>
    [
        new("create_parameter",
            "Create a new project parameter and add it to one or more categories (e.g. a 'Concrete Grade' text parameter on Structural Columns and Structural Framing). If a parameter with this name was already created by the copilot, the categories are added to it. It is stored as a shared parameter in the copilot's own shared parameter file, so it can also be used in schedules, tags and filters.",
            new
            {
                name = S("Parameter name."),
                categories = new { type = "array", items = new { type = "string" }, description = "Category names to add it to, e.g. ['Structural Columns', 'Structural Framing']." },
                data_type = new { type = "string", @enum = DataTypes.Keys.ToArray(), description = "Default 'text'." },
                binding = new { type = "string", @enum = new[] { "instance", "type" }, description = "Instance (each element its own value, default) or type (shared by all elements of a type)." },
                group = new { type = "string", @enum = Groups.Keys.ToArray(), description = "Properties-palette group to show it under. Default 'data'." },
                description = S("Tooltip text. Optional."),
            },
            ["name", "categories"], true, CreateParameter),

        new("set_parameter_values",
            "Set a parameter to a DIFFERENT value on each element, e.g. number columns C1, C2, C3 or copy one parameter into another. For the same value on many elements use set_parameter instead. Values with units are read in project display units unless units are written in the value.",
            new
            {
                parameter_name = S("Exact parameter name."),
                updates = new
                {
                    type = "array",
                    items = new { type = "object", properties = new { element_id = I("Element id"), value = S("New value as text") }, required = new[] { "element_id", "value" } },
                },
                target = new { type = "string", @enum = new[] { "instance", "type" }, description = "Default 'instance'." },
            },
            ["parameter_name", "updates"], true, (app, input) =>
            {
                Document doc = Doc(app);
                string pName = input.Str("parameter_name");
                bool onType = input.OptStr("target") == "type";
                var outcomes = new List<object>();
                int updated = 0;
                string warnings = InTransaction(doc, $"Update {pName}", () =>
                {
                    foreach (JsonElement u in input.GetProperty("updates").EnumerateArray())
                    {
                        long id = u.GetProperty("element_id").GetInt64();
                        string value = u.Str("value");
                        try
                        {
                            Element e = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException("element not found");
                            Element target = onType ? doc.GetElement(e.GetTypeId()) ?? throw new ArgumentException("element has no type") : e;
                            Parameter p = target.LookupParameter(pName) ?? throw new ArgumentException($"no {(onType ? "type" : "instance")} parameter '{pName}'");
                            SetParam(p, value);
                            outcomes.Add(new { id, ok = true, value = ParamValue(p) });
                            updated++;
                        }
                        catch (Exception ex)
                        {
                            outcomes.Add(new { id, ok = false, error = ex.Message });
                        }
                    }
                });
                return Result(new { updated, results = outcomes, warnings });
            }),
    ];

    private static string CreateParameter(UIApplication app, JsonElement input)
    {
        Document doc = Doc(app);
        if (doc.IsFamilyDocument) throw new InvalidOperationException("Open a project, not a family, to add project parameters.");
        string name = input.Str("name");
        string typeKey = input.OptStr("data_type") ?? "text";
        ForgeTypeId spec = DataTypes.GetValueOrDefault(typeKey) ?? throw new ArgumentException($"Unknown data_type '{typeKey}'.");
        ForgeTypeId group = Groups.GetValueOrDefault(input.OptStr("group") ?? "data") ?? GroupTypeId.Data;
        bool typeBinding = input.OptStr("binding") == "type";

        Autodesk.Revit.ApplicationServices.Application rvt = app.Application;
        CategorySet cats = rvt.Create.NewCategorySet();
        var skipped = new List<string>();
        foreach (Category c in Categories(doc, input))
        {
            if (c.AllowsBoundParameters) cats.Insert(c);
            else skipped.Add(c.Name);
        }
        if (cats.IsEmpty) throw new ArgumentException("None of the categories can take parameters.");

        ExternalDefinition definition = GetOrCreateSharedDefinition(rvt, name, spec, input.OptStr("description"));

        // Refuse to shadow a different parameter the project already has under the same name.
        DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            if (!it.Key.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            bool isOurs = it.Key is InternalDefinition d && doc.GetElement(d.Id) is SharedParameterElement sp && sp.GuidValue == definition.GUID;
            if (!isOurs)
                throw new InvalidOperationException($"The project already has a parameter named '{it.Key.Name}'. Choose another name, or use set_parameter to fill it in.");
        }

        string warnings = InTransaction(doc, $"Create parameter {name}", () =>
        {
            Binding binding = typeBinding ? rvt.Create.NewTypeBinding(cats) : rvt.Create.NewInstanceBinding(cats);
            if (!doc.ParameterBindings.Insert(definition, binding, group))
            {
                // Already bound: add the new categories to the existing ones.
                if (doc.ParameterBindings.get_Item(definition) is ElementBinding existing)
                    foreach (Category c in existing.Categories) cats.Insert(c);
                Binding merged = typeBinding ? rvt.Create.NewTypeBinding(cats) : rvt.Create.NewInstanceBinding(cats);
                if (!doc.ParameterBindings.ReInsert(definition, merged, group))
                    throw new InvalidOperationException("Revit refused to bind the parameter.");
            }
        });

        return Result(new
        {
            created = name,
            data_type = typeKey,
            binding = typeBinding ? "type" : "instance",
            categories = cats.Cast<Category>().Select(c => c.Name),
            skipped_categories = skipped,
            warnings,
        });
    }

    /// <summary>
    /// Revit can only add parameters through a shared parameter file. Use the copilot's own file
    /// so the user's file is never edited, and switch the setting back afterwards.
    /// </summary>
    private static ExternalDefinition GetOrCreateSharedDefinition(
        Autodesk.Revit.ApplicationServices.Application rvt, string name, ForgeTypeId spec, string? description)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitCopilot");
        string path = Path.Combine(folder, "SharedParameters.txt");
        Directory.CreateDirectory(folder);
        if (!File.Exists(path)) File.WriteAllText(path, "");

        string original = rvt.SharedParametersFilename;
        try
        {
            rvt.SharedParametersFilename = path;
            DefinitionFile file = rvt.OpenSharedParameterFile() ?? throw new InvalidOperationException($"Could not open {path}.");
            DefinitionGroup group = file.Groups.get_Item(SharedGroupName) ?? file.Groups.Create(SharedGroupName);
            if (group.Definitions.get_Item(name) is ExternalDefinition existing)
            {
                if (existing.GetDataType() != spec)
                    throw new InvalidOperationException($"'{name}' already exists in the copilot's shared parameters with a different data type. Choose another name.");
                return existing;
            }
            var options = new ExternalDefinitionCreationOptions(name, spec) { Description = description ?? "", UserModifiable = true };
            return (ExternalDefinition)group.Definitions.Create(options);
        }
        finally
        {
            rvt.SharedParametersFilename = original;
        }
    }
}
