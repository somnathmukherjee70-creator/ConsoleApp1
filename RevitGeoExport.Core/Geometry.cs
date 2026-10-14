using System.Collections.Generic;

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
