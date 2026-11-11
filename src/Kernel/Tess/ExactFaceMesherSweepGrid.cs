using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

public static partial class ExactFaceMesher
{
    /// <summary>
    /// Untrimmed rectangular sweep patches only. Opposite boundaries must have matching
    /// stations. Reuse every shared edge point; never insert a private boundary vertex.
    /// Curved cell centres avoid the folded ear triangles of general UV triangulation.
    /// This is a display mesh, never an input to construction of the B-rep.
    /// </summary>
    private static (List<Vector3d> P, List<int> T, List<Vector3d> N)? FullSweepGrid(TFace face, bool allowWrapped = false)
    {
        if ((!allowWrapped && face.Surface is FacetedSurface) || face.Tag?.StartsWith("sweep:side[") != true
            || GeometryAudit.AuthoritativeSurface(face) is not SectionSurface { IsProcedural: true, Closed: false } s
            || face.Loops.Count != 1 || face.Loops[0].CoEdges.Count != 4) return null;
        double a = s.UMin, b = s.UMax, range = b - a;
        if (!(range > 0)) return null;
        var borders = new Dictionary<int, List<(double T, Vector3d P)>>();
        foreach (var ce in face.Loops[0].CoEdges)
        {
            var pts = EdgePoints(ce.Edge);
            if (pts.Count < 2) return null;
            var uv = pts.Select(s.ProjectToUV).Select(q => new Vector2d((q.X - a) / range, q.Y)).ToArray();
            int side = -1;
            if (uv.All(q => Math.Abs(q.X) < 1e-6)) side = 0;
            else if (uv.All(q => Math.Abs(q.X - 1) < 1e-6)) side = 1;
            else if (uv.All(q => Math.Abs(q.Y) < 1e-6)) side = 2;
            else if (uv.All(q => Math.Abs(q.Y - 1) < 1e-6)) side = 3;
            if (side < 0 || borders.ContainsKey(side)) return null;
            var edge = pts.Select((p, i) => (T: side < 2 ? uv[i].Y : uv[i].X, P: p)).OrderBy(q => q.T).ToList();
            if (Math.Abs(edge[0].T) > 1e-6 || Math.Abs(edge[^1].T - 1) > 1e-6) return null;
            for (int i = 0; i < edge.Count; i++)
            {
                double t = Math.Clamp(edge[i].T, 0, 1);
                var q = side switch
                {
                    0 => new Vector2d(a, t), 1 => new Vector2d(b, t),
                    2 => new Vector2d(a + range * t, 0), _ => new Vector2d(a + range * t, 1)
                };
                if ((s.PointAt(q) - edge[i].P).Length > 1e-6) return null;
                edge[i] = (t, edge[i].P);
                if (i > 0 && t - edge[i - 1].T < 1e-10) return null;
            }
            borders.Add(side, edge);
        }
        if (borders.Count != 4) return null;
        bool Matches(int x, int y) => borders[x].Count == borders[y].Count
            && borders[x].Zip(borders[y]).All(p => Math.Abs(p.First.T - p.Second.T) < 1e-7);
        if (!Matches(0, 1) || !Matches(2, 3)) return null;
        var us = borders[2].Select(p => a + range * p.T).ToArray();
        var vs = borders[0].Select(p => p.T).ToArray();
        int nu = us.Length, nv = vs.Length;
        if ((long)nu * nv > 100000) return null;
        var points = new List<Vector3d>(); var parameters = new List<Vector2d>(); var normals = new List<Vector3d>();
        var triangles = new List<int>();
        var middle = new Vector2d((a + b) / 2, 0.5);
        bool reversed = Vector3d.Dot(s.NormalAt(middle), face.NormalAt(s.PointAt(middle))) < 0;
        int Add(Vector2d uv, Vector3d? exact = null)
        {
            int id = points.Count;
            points.Add(exact ?? s.PointAt(uv)); parameters.Add(uv);
            normals.Add(s.NormalAt(uv) * (reversed ? -1 : 1));
            return id;
        }
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
                Add(new Vector2d(us[i], vs[j]), i == 0 ? borders[0][j].P : i == nu - 1 ? borders[1][j].P
                    : j == 0 ? borders[2][i].P : j == nv - 1 ? borders[3][i].P : null);
        bool Triangle(int x, int y, int z)
        {
            var cross = Vector3d.Cross(points[y] - points[x], points[z] - points[x]);
            var uv = (parameters[x] + parameters[y] + parameters[z]) / 3;
            if (Vector3d.Dot(cross, s.NormalAt(uv)) <= 1e-18) return false;
            if ((s.PointAt(uv) - (points[x] + points[y] + points[z]) / 3).Length > Tol.Chord) return false;
            foreach (var (p, q) in new[] { (x, y), (y, z), (z, x) })
                if ((s.PointAt((parameters[p] + parameters[q]) / 2) - (points[p] + points[q]) / 2).Length > Tol.Chord) return false;
            triangles.Add(x); triangles.Add(reversed ? z : y); triangles.Add(reversed ? y : z);
            return true;
        }
        for (int i = 0; i < nu - 1; i++)
            for (int j = 0; j < nv - 1; j++)
            {
                int c = Add(new Vector2d((us[i] + us[i + 1]) / 2, (vs[j] + vs[j + 1]) / 2));
                int p = i * nv + j, q = (i + 1) * nv + j;
                if (!Triangle(p, q, c) || !Triangle(q, q + 1, c)
                    || !Triangle(q + 1, p + 1, c) || !Triangle(p + 1, p, c)) return null;
            }
        return (points, triangles, normals);
    }

    /// <summary>Reconcile a copied sweep face with the shared seam's actual boundary samples.</summary>
    internal static bool RefreshSweepDisplay(TFace face)
    {
        if (face.Surface is not FacetedSurface fs) return false;
        var mesh = ThreadGrid(face) ?? FullSweepGrid(face, allowWrapped: true);
        if (mesh is not { } m) return false;
        var triangles = new List<(Vector3d, Vector3d, Vector3d)>();
        var normals = new List<(Vector3d, Vector3d, Vector3d)>();
        for (int i = 0; i < m.T.Count; i += 3)
        {
            int a = m.T[i], b = m.T[i + 1], c = m.T[i + 2];
            triangles.Add((m.P[a], m.P[b], m.P[c]));
            normals.Add((m.N[a], m.N[b], m.N[c]));
        }
        fs.SetDisplay(triangles, normals);
        return true;
    }
}
