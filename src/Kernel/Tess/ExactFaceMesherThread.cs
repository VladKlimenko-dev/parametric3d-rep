using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

public static partial class ExactFaceMesher
{
    /// <summary>
    /// Ruled thread patches, including a collapsed end. Zipper the two shared rail polylines;
    /// no new boundary samples, duplicated tip triangles, or ear clipping across a pinched UV end.
    /// </summary>
    private static (List<Vector3d> P, List<int> T, List<Vector3d> N)? ThreadGrid(TFace face)
    {
        if (face.Tag?.StartsWith("sweep:side[thread.") != true
            || GeometryAudit.AuthoritativeSurface(face) is not SectionSurface { Closed: false } s
            || face.Loops.Count != 1) return null;
        var rails = new Dictionary<int, List<(double U, Vector3d P)>>();
        foreach (var edge in face.Edges)
        {
            if (edge.Curve is LineCurve3)
            {
                if (EdgePoints(edge).Count != 2) return null;
                continue;
            }
            double mid = (edge.Curve.T0 + edge.Curve.T1) / 2;
            var uv = s.ProjectToUV(edge.Curve.PointAt(mid));
            int side = uv.Y < 0.5 ? 0 : 1;
            if (Math.Abs(uv.Y - side) > 1e-6 || rails.ContainsKey(side)) return null;
            var points = EdgePoints(edge).Select(p => (U: s.ProjectToUV(p).X, P: p)).OrderBy(p => p.U).ToList();
            if (points.Count < 2 || Math.Abs(points[0].U - s.UMin) > 1e-7
                || Math.Abs(points[^1].U - s.UMax) > 1e-7) return null;
            if (points.Any(p => (s.PointAt(new Vector2d(p.U, side)) - p.P).Length > 1e-7)) return null;
            rails.Add(side, points);
        }
        if (rails.Count != 2) return null;
        var middle = new Vector2d((s.UMin + s.UMax) / 2, 0.5);
        bool reversed = Vector3d.Dot(s.NormalAt(middle), face.NormalAt(s.PointAt(middle))) < 0;
        var positions = new List<Vector3d>(); var parameters = new List<Vector2d>();
        var normals = new List<Vector3d>(); var triangles = new List<int>();
        int Add(Vector2d uv, Vector3d? point = null)
        {
            int id = positions.Count;
            positions.Add(point ?? s.PointAt(uv)); parameters.Add(uv);
            normals.Add(s.NormalAt(uv) * (reversed ? -1 : 1));
            return id;
        }
        var lo = rails[0].Select(p => Add(new Vector2d(p.U, 0), p.P)).ToArray();
        var hi = rails[1].Select(p => Add(new Vector2d(p.U, 1), p.P)).ToArray();
        bool Fan(List<int> ring)
        {
            // Remove the duplicated geometric vertex at either pinched end.
            for (int k = ring.Count - 1; k >= 0; k--)
                if ((positions[ring[k]] - positions[ring[(k + 1) % ring.Count]]).Length < 1e-10)
                    ring.RemoveAt(k);
            if (ring.Count < 3) return true;
            var centre = ring.Aggregate(Vector2d.Zero, (sum, id) => sum + parameters[id]) / ring.Count;
            int c = Add(centre);
            for (int k = 0; k < ring.Count; k++)
            {
                int a = ring[k], b = ring[(k + 1) % ring.Count];
                var uv = (parameters[a] + parameters[b] + centre) / 3;
                var cross = Vector3d.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (Vector3d.Dot(cross, s.NormalAt(uv)) <= 1e-20) return false;
                if ((s.PointAt(uv) - (positions[a] + positions[b] + positions[c]) / 3).Length > Tol.Chord) return false;
                foreach (int v in new[] { a, b })
                    if ((s.PointAt((parameters[v] + centre) / 2) - (positions[v] + positions[c]) / 2).Length > Tol.Chord)
                        return false;
                triangles.Add(a); triangles.Add(reversed ? c : b); triangles.Add(reversed ? b : c);
            }
            return true;
        }
        int i = 0, j = 0;
        while (i + 1 < lo.Length || j + 1 < hi.Length)
        {
            double u = i + 1 < lo.Length ? rails[0][i + 1].U : double.PositiveInfinity;
            double v = j + 1 < hi.Length ? rails[1][j + 1].U : double.PositiveInfinity;
            var ring = new List<int> { lo[i] };
            bool both = Math.Abs(u - v) < 1e-9 * (s.UMax - s.UMin);
            if (u < v || both) ring.Add(lo[++i]);
            if (v < u || both) ring.Add(hi[j + 1]);
            ring.Add(hi[j]);
            if (v < u || both) j++;
            if (!Fan(ring)) return null;
        }
        return (positions, triangles, normals);
    }
}
