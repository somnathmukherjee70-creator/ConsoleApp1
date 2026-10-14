using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace RevitGeoExport.Core
{
    /// <summary>
    /// Writes a self-contained HTML page that shows the model in 3D on a live satellite / street map.
    /// Double-click the file to open it in any browser; no server or API key is needed.
    /// </summary>
    public static class WebMapWriter
    {
        private const string Placeholder = "/*__MODEL_GEOJSON__*/null";

        public static string ToHtml(GeoAnchor anchor, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options)
        {
            string template = LoadTemplate();
            return template
                .Replace("__MODEL_TITLE__", System.Net.WebUtility.HtmlEncode(options.DocumentName))
                .Replace(Placeholder, GeoJsonWriter.ToGeoJson(anchor, meshes, options));
        }

        public static void Write(string path, GeoAnchor anchor, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options) =>
            File.WriteAllText(path, ToHtml(anchor, meshes, options), new UTF8Encoding(false));

        internal static string LoadTemplate()
        {
            var assembly = typeof(WebMapWriter).GetTypeInfo().Assembly;
            using (var stream = assembly.GetManifestResourceStream("RevitGeoExport.Core.map-viewer.html"))
            using (var reader = new StreamReader(stream!))
                return reader.ReadToEnd();
        }
    }
}
