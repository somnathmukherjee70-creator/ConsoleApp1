using System.Collections.Generic;
using System.Linq;

namespace RevitGeoExport.Core
{
    /// <summary>A point in a local East-North-Up frame, in metres.</summary>
    public readonly struct Vec3
    {
        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>East, metres.</summary>
        public double X { get; }

        /// <summary>North, metres.</summary>
        public double Y { get; }

        /// <summary>Up, metres.</summary>
        public double Z { get; }
    }

    /// <summary>A flat polygon: an outer ring and optional holes, each without a repeated closing point.</summary>
    public sealed class GeoPolygon
    {
        public GeoPolygon(List<Vec3> outer, List<List<Vec3>>? holes = null)
        {
            Outer = outer;
            Holes = holes ?? new List<List<Vec3>>();
        }

        public List<Vec3> Outer { get; }
        public List<List<Vec3>> Holes { get; }
    }

    /// <summary>A triangulated model element whose vertices are in ENU metres relative to the <see cref="GeoAnchor"/>.</summary>
    public sealed class GeoMesh
    {
        public GeoMesh(string elementId, string name, string category)
        {
            ElementId = elementId;
            Name = name;
            Category = category;
        }

        public string ElementId { get; }
        public string Name { get; }
        public string Category { get; }
        public List<Vec3> Vertices { get; } = new List<Vec3>();

        /// <summary>Vertex indices, three per triangle.</summary>
        public List<int> Indices { get; } = new List<int>();

        public int TriangleCount => Indices.Count / 3;

        /// <summary>Flat faces kept whole (with holes) instead of triangulated; far smaller in KML.</summary>
        public List<GeoPolygon> Polygons { get; } = new List<GeoPolygon>();

        public bool IsEmpty => Indices.Count == 0 && Polygons.Count == 0;

        /// <summary>How many coordinates this element writes to KML: the measure of its file size.</summary>
        public int KmlPointCount => TriangleCount * 4 + Polygons.Sum(p => p.Outer.Count + 1 + p.Holes.Sum(h => h.Count + 1));

        /// <summary>Adds a planar polygon. Its points also go into <see cref="Vertices"/> for extents and footprints.</summary>
        public void AddPolygon(GeoPolygon polygon)
        {
            Polygons.Add(polygon);
            Vertices.AddRange(polygon.Outer);
        }

        public void AddTriangle(Vec3 a, Vec3 b, Vec3 c)
        {
            int start = Vertices.Count;
            Vertices.Add(a);
            Vertices.Add(b);
            Vertices.Add(c);
            Indices.Add(start);
            Indices.Add(start + 1);
            Indices.Add(start + 2);
        }
    }
}
