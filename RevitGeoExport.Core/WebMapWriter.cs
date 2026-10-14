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
        private const string GlbPlaceholder = "/*__MODEL_GLB__*/null";
        private const string OriginPlaceholder = "/*__MODEL_ORIGIN__*/null";

        /// <param name="includeModel">Embed the full 3D model (GLB) so the page shows the real architecture, not just blocks.</param>
        public static string ToHtml(IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options, bool includeModel = true)
        {
            string glb = "null", origin = "null";
            if (includeModel)
            {
                using (var stream = new MemoryStream())
                {
                    var result = GlbWriter.Write(stream, placement, meshes, options);
                    glb = "\"" + System.Convert.ToBase64String(stream.ToArray()) + "\"";
                    origin = "[" + KmlWriter.Format(result.Longitude) + "," + KmlWriter.Format(result.Latitude) + "]";
                }
            }

            return LoadTemplate()
                .Replace("__MODEL_TITLE__", System.Net.WebUtility.HtmlEncode(options.DocumentName))
                .Replace(GlbPlaceholder, glb)
                .Replace(OriginPlaceholder, origin)
                .Replace(Placeholder, GeoJsonWriter.ToGeoJson(placement, meshes, options));
        }

        public static void Write(string path, IGeoPlacement placement, IReadOnlyCollection<GeoMesh> meshes, ExportOptions options) =>
            File.WriteAllText(path, ToHtml(placement, meshes, options), new UTF8Encoding(false));

        internal static string LoadTemplate()
        {
            var assembly = typeof(WebMapWriter).GetTypeInfo().Assembly;
            using (var stream = assembly.GetManifestResourceStream("RevitGeoExport.Core.map-viewer.html"))
            using (var reader = new StreamReader(stream!))
                return reader.ReadToEnd();
        }
    }
}
