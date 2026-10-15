using System.Text.Json;
using Autodesk.Revit.UI;

namespace RevitCopilot.Tools;

/// <summary>
/// One action the copilot can take. <see cref="Properties"/> is the JSON-schema "properties"
/// object Claude sees; <see cref="Run"/> executes on Revit's main thread.
/// Tools that change the model must set <see cref="ModifiesModel"/> so the user is asked first,
/// and must do their work inside a Transaction so Ctrl+Z undoes them.
/// </summary>
public sealed record RevitTool(
    string Name,
    string Description,
    object Properties,
    string[] Required,
    bool ModifiesModel,
    Func<UIApplication, JsonElement, string> Run);
