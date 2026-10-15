using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCopilot.Core;
using RevitCopilot.Tools;
using RevitCopilot.UI;

namespace RevitCopilot;

/// <summary>
/// Entry point Revit calls on startup (registered in RevitCopilot.addin).
/// Adds an "AI Copilot" ribbon tab and a dockable chat pane.
/// </summary>
public sealed class App : IExternalApplication
{
    public static readonly DockablePaneId PaneId = new(new Guid("B3A7F0C2-5E0B-4E0F-9C8E-1D2A3B4C5D6E"));

    internal static RevitBridge Bridge { get; private set; } = null!;

    public Result OnStartup(UIControlledApplication application)
    {
        // ExternalEvent.Create must run in a valid Revit API context, which OnStartup is.
        Bridge = new RevitBridge();

        var pane = new CopilotPane(new CopilotAgent(Bridge, ToolRegistry.CreateDefault()));
        application.RegisterDockablePane(PaneId, "Revit Copilot", new CopilotPaneProvider(pane));

        const string tab = "AI Copilot";
        application.CreateRibbonTab(tab);
        RibbonPanel panel = application.CreateRibbonPanel(tab, "Assistant");
        var button = new PushButtonData(
            "ShowCopilot", "Copilot",
            Assembly.GetExecutingAssembly().Location,
            typeof(ShowCopilotCommand).FullName)
        {
            ToolTip = "Open the Revit Copilot chat pane and give instructions in plain language.",
        };
        panel.AddItem(button);

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}

[Transaction(TransactionMode.Manual)]
public sealed class ShowCopilotCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        DockablePane pane = commandData.Application.GetDockablePane(App.PaneId);
        pane.Show();
        return Result.Succeeded;
    }
}

internal sealed class CopilotPaneProvider(CopilotPane pane) : IDockablePaneProvider
{
    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = pane;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right,
        };
    }
}
