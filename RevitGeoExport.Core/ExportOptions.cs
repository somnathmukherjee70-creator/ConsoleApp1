using System.Collections.Generic;
using System.Linq;

namespace RevitGeoExport.Core
{
    public enum AltitudeMode
    {
        /// <summary>The lowest point of the model sits on the map's terrain (plus <see cref="ExportOptions.GroundOffsetMeters"/>).</summary>
        OnGround,

        /// <summary>Heights are the placement's elevation plus the model's own Z, above sea level.</summary>
        AboveSeaLevel,
    }

    public enum KmlMode
    {
        /// <summary>Every face in 3D. Best in Google Earth Pro (desktop).</summary>
        Detailed3D,

        /// <summary>Each element's outline extruded to its height: simple shapes every Google Earth accepts, including Earth Web.</summary>
        Massing,
    }

    public sealed class ExportOptions
    {
        public string DocumentName { get; set; } = "Revit model";
        public AltitudeMode AltitudeMode { get; set; } = AltitudeMode.OnGround;
        public double GroundOffsetMeters { get; set; }

        /// <summary>
        /// Most coordinates written to a KMZ. Beyond this Google Earth (especially Earth Web, in the
        /// browser) runs out of memory, so the smallest elements are left out. 0 means no limit.
        /// </summary>
        public int MaxKmlPoints { get; set; } = 300_000;

        public KmlMode KmlMode { get; set; } = KmlMode.Detailed3D;

        /// <summary>Most triangles written to a GLB model; the smallest elements go first. 0 means no limit.</summary>
        public int MaxModelTriangles { get; set; } = 1_500_000;
    }

    /// <summary>Resolves a mesh vertex height to the altitude written to the map.</summary>
    internal sealed class AltitudeResolver
    {
        private readonly ExportOptions _options;
        private readonly double _anchorElevation;
        private readonly double _modelMinZ;

        public AltitudeResolver(ExportOptions options, IGeoPlacement placement, IEnumerable<GeoMesh> meshes)
        {
            _options = options;
            _anchorElevation = placement.ElevationMeters;
            var zs = meshes.SelectMany(m => m.Vertices).Select(v => v.Z).ToList();
            _modelMinZ = zs.Count == 0 ? 0 : zs.Min();
        }

        /// <summary>True when the altitude is measured from the terrain rather than sea level.</summary>
        public bool IsRelativeToGround => _options.AltitudeMode == AltitudeMode.OnGround;

        public double Resolve(double z) => IsRelativeToGround
            ? z - _modelMinZ + _options.GroundOffsetMeters
            : _anchorElevation + z + _options.GroundOffsetMeters;
    }

    /// <summary>Fixed colours per Revit category, so the same model always looks the same.</summary>
    internal static class CategoryColors
    {
        private static readonly Dictionary<string, (byte R, byte G, byte B, byte A)> Known = new Dictionary<string, (byte, byte, byte, byte)>
        {
            ["Walls"] = (225, 222, 214, 255),
            ["Floors"] = (190, 190, 190, 255),
            ["Roofs"] = (165, 82, 60, 255),
            ["Ceilings"] = (240, 240, 240, 255),
            ["Windows"] = (110, 170, 220, 170),
            ["Curtain Panels"] = (110, 170, 220, 170),
            ["Curtain Wall Mullions"] = (90, 90, 95, 255),
            ["Doors"] = (140, 95, 60, 255),
            ["Columns"] = (200, 200, 200, 255),
            ["Structural Columns"] = (170, 170, 175, 255),
            ["Structural Framing"] = (150, 150, 155, 255),
            ["Structural Foundations"] = (130, 130, 130, 255),
            ["Stairs"] = (205, 195, 175, 255),
            ["Railings"] = (80, 80, 85, 255),
            ["Planting"] = (80, 150, 70, 255),
        };

        private static readonly (byte R, byte G, byte B, byte A)[] Fallback =
        {
            (230, 160, 80, 255), (120, 180, 140, 255), (170, 140, 200, 255),
            (220, 120, 130, 255), (120, 160, 200, 255), (200, 190, 110, 255),
        };

        public static (byte R, byte G, byte B, byte A) For(string category)
        {
            if (Known.TryGetValue(category, out var color))
                return color;

            // Stable across runs (string.GetHashCode is randomised on .NET Core).
            int hash = 0;
            foreach (char c in category)
                hash = unchecked(hash * 31 + c);
            return Fallback[(hash & int.MaxValue) % Fallback.Length];
        }

        /// <summary>KML uses aabbggrr hex.</summary>
        public static string ToKml(string category)
        {
            var c = For(category);
            return $"{c.A:x2}{c.B:x2}{c.G:x2}{c.R:x2}";
        }

        public static string ToCss(string category)
        {
            var c = For(category);
            return $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        }
    }
}
