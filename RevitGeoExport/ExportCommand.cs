using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitGeoExport.Core;
using DialogResult = System.Windows.Forms.DialogResult;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RevitGeoExport
{
    /// <summary>Exports the model at its real-world position for Google Earth and live web maps.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                message = "Open a project (not a family) to place it on a map.";
                return Result.Failed;
            }

            var location = RevitLocation.Read(doc);
            var title = string.IsNullOrEmpty(doc.Title) ? "Model" : Path.GetFileNameWithoutExtension(doc.Title);
            var folder = string.IsNullOrEmpty(doc.PathName)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(doc.PathName);

            ExportSettings settings;
            using (var dialog = new ExportDialog(location, Path.Combine(folder, title)))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                    return Result.Cancelled;
                settings = dialog.Settings;
            }

            if (settings.SaveLocationToProject)
                SaveLocation(doc, settings);

            // Respect the active 3D view's visibility (hidden categories, section box, phases) when there is one.
            var view = doc.ActiveView as View3D;
            if (view != null && view.IsTemplate)
                view = null;

            var frame = settings.Crs != null ? location.ProjectedFrame() : location.LocalFrame(settings.Anchor);
            var meshes = RevitModelReader.Read(doc, view, frame, settings.ExteriorOnly);
            if (meshes.Count == 0)
            {
                message = "No 3D model geometry was found to export.";
                return Result.Failed;
            }

            IGeoPlacement placement = settings.Crs != null
                ? new ProjectedPlacement(settings.Crs, settings.SiteElevationMeters)
                : new GeoConverter(new GeoAnchor(settings.Latitude, settings.Longitude, settings.SiteElevationMeters, settings.RotationDegrees));
            var options = new ExportOptions
            {
                DocumentName = title,
                AltitudeMode = settings.AltitudeMode,
                GroundOffsetMeters = settings.OffsetMeters,
            };

            var written = new List<string>();
            GlbResult earthModel = null;
            GlbResult glb = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputBasePath)));
                if (settings.WriteGlb)
                {
                    string glbPath = settings.OutputBasePath + GlbSuffix;
                    written.Add(Write(glbPath, p => glb = GlbWriter.Write(p, placement, meshes, options)));
                    // Earth Web asks where to put the model; keep the answers next to it.
                    written.Add(Write(settings.OutputBasePath + "_GoogleEarthWeb_3D_placement.txt",
                        p => File.WriteAllText(p, glb.Instructions(Path.GetFileName(glbPath)))));
                }

                if (settings.WriteKmz)
                {
                    // The real 3D model (COLLADA) with its location: Google Earth Pro opens it in place.
                    written.Add(Write(settings.OutputBasePath + ProKmzSuffix,
                        p => earthModel = ColladaKmzWriter.Write(p, placement, meshes, options)));
                }
                if (settings.WriteGeoJson)
                    written.Add(Write(settings.OutputBasePath + ".geojson", p => GeoJsonWriter.Write(p, placement, meshes, options)));
                if (settings.WriteWebMap)
                    written.Add(Write(settings.OutputBasePath + "_map.html", p => WebMapWriter.Write(p, placement, meshes, options)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                message = "Could not write the export: " + ex.Message;
                return Result.Failed;
            }

            ShowResult(meshes, view, placement, earthModel ?? glb, glb, written);
            return Result.Succeeded;
        }

        private const string GlbSuffix = "_GoogleEarthWeb_3D.glb";
        private const string ProKmzSuffix = "_GoogleEarthPro_3D.kmz";

        private static string Write(string path, Action<string> write)
        {
            write(path);
            return path;
        }

        private static void SaveLocation(Document doc, ExportSettings settings)
        {
            using (var transaction = new Transaction(doc, "Set project location"))
            {
                transaction.Start();
                doc.SiteLocation.Latitude = settings.Latitude * Math.PI / 180;
                doc.SiteLocation.Longitude = settings.Longitude * Math.PI / 180;
                transaction.Commit();
            }
        }

        private static void ShowResult(List<GeoMesh> meshes, View3D view, IGeoPlacement placement, GlbResult earthModel, GlbResult glb, List<string> written)
        {
            string trimmed = earthModel != null && earthModel.Omitted > 0
                ? $"The 3D model leaves out the {earthModel.Omitted:N0} smallest elements to stay a manageable size.\n"
                : "";
            string model = glb == null ? "" : glb.Instructions(Path.GetFileName(written.First(p => p.EndsWith(GlbSuffix, StringComparison.OrdinalIgnoreCase)))) + "\n\n";
            var dialog = new TaskDialog("Place model on map")
            {
                MainInstruction = $"Exported {meshes.Count:N0} elements",
                MainContent = (view != null ? $"Source: 3D view \"{view.Name}\".\n" : "Source: whole model (open a 3D view to export only what it shows).\n")
                    + placement.Description + ".\n\n"
                    + model
                    + trimmed
                    + string.Join("\n", written),
            };

            var kmz = written.FirstOrDefault(p => p.EndsWith(ProKmzSuffix, StringComparison.OrdinalIgnoreCase));
            var html = written.FirstOrDefault(p => p.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
            if (kmz != null)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open in Google Earth Pro (places itself)",
                    "The full 3D architecture, already in position. For earth.google.com use the .glb instead.");
            if (html != null)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the 3D map (places itself)",
                    "The real 3D model on satellite imagery in your browser; nothing to set up.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Open the export folder");
            if (glb != null)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Copy the 3D model's coordinates",
                    "Paste into Google Earth Web's 3D model position fields after importing the .glb.");

            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1: Open(kmz); break;
                case TaskDialogResult.CommandLink2: Open(html); break;
                case TaskDialogResult.CommandLink3: Open(Path.GetDirectoryName(written[0])); break;
                case TaskDialogResult.CommandLink4:
                    System.Windows.Forms.Clipboard.SetText(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0:0.0000000}, {1:0.0000000}", glb.Latitude, glb.Longitude));
                    TaskDialog.Show("Place model on map", "Copied. In Google Earth Web, import the .glb, then paste into its latitude/longitude fields.");
                    break;
            }
        }

        private static void Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Place model on map", $"Could not open {path}: {ex.Message}");
            }
        }
    }
}
