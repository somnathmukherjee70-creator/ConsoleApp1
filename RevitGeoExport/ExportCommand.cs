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

            var meshes = RevitModelReader.Read(doc, view, location.LocalFrame(settings.Anchor));
            if (meshes.Count == 0)
            {
                message = "No 3D model geometry was found to export.";
                return Result.Failed;
            }

            var anchor = new GeoAnchor(settings.Latitude, settings.Longitude, settings.SiteElevationMeters, settings.RotationDegrees);
            var options = new ExportOptions
            {
                DocumentName = title,
                AltitudeMode = settings.AltitudeMode,
                GroundOffsetMeters = settings.OffsetMeters,
            };

            var written = new List<string>();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputBasePath)));
                if (settings.WriteKmz)
                    written.Add(Write(settings.OutputBasePath + ".kmz", p => KmlWriter.WriteKmz(p, anchor, meshes, options)));
                if (settings.WriteGeoJson)
                    written.Add(Write(settings.OutputBasePath + ".geojson", p => GeoJsonWriter.Write(p, anchor, meshes, options)));
                if (settings.WriteWebMap)
                    written.Add(Write(settings.OutputBasePath + "_map.html", p => WebMapWriter.Write(p, anchor, meshes, options)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                message = "Could not write the export: " + ex.Message;
                return Result.Failed;
            }

            ShowResult(meshes, view, written);
            return Result.Succeeded;
        }

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

        private static void ShowResult(List<GeoMesh> meshes, View3D view, List<string> written)
        {
            int triangles = meshes.Sum(m => m.TriangleCount);
            var dialog = new TaskDialog("Place model on map")
            {
                MainInstruction = $"Exported {meshes.Count:N0} elements ({triangles:N0} triangles)",
                MainContent = (view != null ? $"Source: 3D view \"{view.Name}\".\n" : "Source: whole model (open a 3D view to export only what it shows).\n")
                    + string.Join("\n", written),
            };

            var kmz = written.FirstOrDefault(p => p.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase));
            var html = written.FirstOrDefault(p => p.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
            if (kmz != null)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open in Google Earth Pro", "Or drag the .kmz into earth.google.com › Projects › Import.");
            if (html != null)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the live web map", "Satellite and street map in your browser.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Open the export folder");

            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1: Open(kmz); break;
                case TaskDialogResult.CommandLink2: Open(html); break;
                case TaskDialogResult.CommandLink3: Open(Path.GetDirectoryName(written[0])); break;
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
