using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                Image = LoadImage("logo16.png"),
                LargeImage = LoadImage("logo32.png"),
            };
            panel.AddItem(button);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        internal static Stream OpenResource(string name) =>
            typeof(App).Assembly.GetManifestResourceStream("RevitGeoExport." + name);

        private static ImageSource LoadImage(string name)
        {
            using (var stream = OpenResource(name))
            {
                // OnLoad copies the pixels so the stream can be closed.
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
    }
}
