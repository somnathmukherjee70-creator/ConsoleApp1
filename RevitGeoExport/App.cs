using Autodesk.Revit.UI;

namespace RevitGeoExport
{
    /// <summary>Adds a "Geo Export" panel with one button to the Add-Ins tab.</summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            var panel = application.CreateRibbonPanel("Geo Export");
            var button = new PushButtonData(
                "ExportToMap",
                "Place on\nMap",
                typeof(App).Assembly.Location,
                typeof(ExportCommand).FullName)
            {
                ToolTip = "Export the model at its real-world position for Google Earth (KMZ) and a live web map.",
            };
            panel.AddItem(button);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
