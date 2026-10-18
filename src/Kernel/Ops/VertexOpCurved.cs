using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// m.f3: ФАСКА И СКРУГЛЕНИЕ ВЕРШИНЫ С КРИВЫМИ РЁБРАМИ ИЛИ ГРАНЯМИ (угол цилиндра, конуса, шара, тора, лент — с плоскостями и
/// между собой). Три ребра угла; точки Qᵢ — на самих кривых рёбер: у фаски — на расстоянии dᵢ от вершины (по прямой), у
/// скругления — там, где сферу радиуса R встречает ребро (центр сферы — на оси угла по касательным рёбер в вершине, как у
/// прямого угла: сфера касается касательных лучей). Плоскость через три точки отсекает угол: резец — цилиндр по нормали
/// плоскости от неё за вершину, радиусом с запасом над следом угла (у скругления — без шара); вычитание у выпуклого угла,
/// объединение у вогнутого (материал добавляется в пустоту). Точная булева — грани угла точные. Седловая вершина (рёбра и
/// выпуклые, и вогнутые) — отказ, как у прямого угла. Мутация P3D_VERTEXCURVED=0 — отказ, как прежде.
/// </summary>
public static class VertexOpCurved
{
    static readonly bool Off = Environment.GetEnvironmentVariable("P3D_VERTEXCURVED") == "0";
    public static long Built;
    static readonly bool Dbg = Environment.GetEnvironmentVariable("P3D_VCURVEDDBG") == "1";

    /// <summary>Угол с кривыми: вершина, рёбра (от вершины), касательные, длины, вогнутость.</summary>
    public sealed record Corner(Vector3d P, List<(TEdge E, bool FromV0)> Edges, Vector3d[] Tangents, double[] Lengths, bool Concave, Vector3d[] Normals);

    /// <summary>У вершины есть кривое ребро или кривая грань (иначе — прямой угол, VertexOp).</summary>
    public static bool IsCurved(TSolid body, Vector3d point)
    {
        if (Off) return false;
        var v = body.Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().OrderBy(x => (x.P - point).LengthSquared).FirstOrDefault();
        if (v == null || (v.P - point).Length > 1e-3) return false;
        var edges = body.Edges.Where(e => ReferenceEquals(e.V0, v) ^ ReferenceEquals(e.V1, v)).ToList();
        if (edges.Any(e => e.Curve is not LineCurve3)) return true;
        var faces = edges.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct();
        return faces.Any(f => (f.Surface is FacetedSurface fs ? fs.Source : f.Surface) is { IsPlanar: false });
    }

    public static Corner Find(TSolid body, Vector3d point)
    {
        var v = body.Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().OrderBy(x => (x.P - point).LengthSquared).First();
        var edges = body.Edges.Where(e => ReferenceEquals(e.V0, v) ^ ReferenceEquals(e.V1, v)).Select(e => (E: e, FromV0: ReferenceEquals(e.V0, v))).ToList();
        if (body.Edges.Any(e => ReferenceEquals(e.V0, v) && ReferenceEquals(e.V1, v)))
            throw new RebuildException(OperationOutcome.Unsupported, "Vertex: a closed edge (seam) starts at the vertex; a vertex chamfer or round needs three edges meeting at a corner.");
        if (edges.Count != 3) throw new RebuildException(OperationOutcome.Unsupported, $"Vertex: {edges.Count} edges meet at a corner with curved edges or faces; three are supported.");
        var P = v.P;
        var tans = edges.Select(x => Tangent(x.E.Curve, x.FromV0)).ToArray();
        var lens = edges.Select(x => x.E.Curve.Length).ToArray();
        var faces = edges.SelectMany(x => x.E.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        var normals = faces.Select(f => f.NormalAt(P).Normalized()).ToArray();
        bool convex = normals.All(n => tans.All(d => Vector3d.Dot(n, d) <= 1e-6));
        bool concave = normals.All(n => tans.All(d => Vector3d.Dot(n, d) >= -1e-6));
        if (!convex && !concave)
            throw new RebuildException(OperationOutcome.InvalidInput, "Vertex: the corner is neither convex nor concave (a saddle vertex); a vertex chamfer or round is not defined for it.");
        return new Corner(P, edges, tans, lens, !convex, normals);
    }

    static Vector3d Tangent(ICurve3 c, bool fromStart)
    {
        double h = 1e-7 * (c.T1 - c.T0);
        var d = fromStart ? c.PointAt(c.T0 + h) - c.PointAt(c.T0) : c.PointAt(c.T1 - h) - c.PointAt(c.T1);
        return d.Normalized();
    }

    /// <summary>Первая от вершины точка ребра, где f(точка) меняет знак (f(вершина) &lt; 0).</summary>
    static Vector3d? Along(ICurve3 c, bool fromStart, Func<Vector3d, double> f)
    {
        double T(double s) => fromStart ? c.T0 + (c.T1 - c.T0) * s : c.T1 - (c.T1 - c.T0) * s;
        const int N = 400; double prev = f(c.PointAt(T(0)));
        for (int i = 1; i <= N; i++)
        {
            double s1 = (double)i / N, cur = f(c.PointAt(T(s1)));
            if (prev < 0 && cur >= 0)
            {
                double lo = (double)(i - 1) / N, hi = s1;
                for (int it = 0; it < 80; it++) { double m = (lo + hi) / 2; if (f(c.PointAt(T(m))) < 0) lo = m; else hi = m; }
                return c.PointAt(T((lo + hi) / 2));
            }
            prev = cur;
        }
        return null;
    }

    /// <summary>Точка ребра, ближайшая к центру (первый минимум расстояния от вершины).</summary>
    static Vector3d? Deepest(ICurve3 c, bool fromStart, Vector3d centre)
    {
        double T(double s) => fromStart ? c.T0 + (c.T1 - c.T0) * s : c.T1 - (c.T1 - c.T0) * s;
        double best = double.MaxValue, bs = 0;
        for (int i = 0; i <= 400; i++) { double s0 = i / 400.0, d = (c.PointAt(T(s0)) - centre).Length; if (d < best) { best = d; bs = s0; } else if (d > best * 1.5) break; }
        double lo = Math.Max(0, bs - 1 / 400.0), hi = Math.Min(1, bs + 1 / 400.0);
        for (int it = 0; it < 100; it++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if ((c.PointAt(T(m1)) - centre).Length < (c.PointAt(T(m2)) - centre).Length) hi = m2; else lo = m1;
        }
        return c.PointAt(T((lo + hi) / 2));
    }

    /// <summary>Точка встречи ребра со сферой: первое пересечение от вершины, а у касания (прямое ребро вдоль касательного
    /// луча) — точка наибольшего сближения, если там сфера достигает ребра до 1e-7 R.</summary>
    static Vector3d? MeetSphere(ICurve3 c, bool fromStart, Vector3d centre, double r)
    {
        if (Along(c, fromStart, x => r - (x - centre).Length) is { } hit) return hit;
        double T(double s) => fromStart ? c.T0 + (c.T1 - c.T0) * s : c.T1 - (c.T1 - c.T0) * s;
        double best = double.MaxValue, bs = 0;
        for (int i = 0; i <= 400; i++) { double s0 = i / 400.0, d = (c.PointAt(T(s0)) - centre).Length; if (d < best) { best = d; bs = s0; } }
        double lo = Math.Max(0, bs - 1 / 400.0), hi = Math.Min(1, bs + 1 / 400.0);
        for (int it = 0; it < 100; it++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if ((c.PointAt(T(m1)) - centre).Length < (c.PointAt(T(m2)) - centre).Length) hi = m2; else lo = m1;
        }
        var p = c.PointAt(T((lo + hi) / 2));
        return Math.Abs((p - centre).Length - r) <= 1e-7 * r ? p : null;
    }

    public static bool Chamfer(List<TSolid> bodies, int k, Vector3d point, double[] d, List<string> warnings)
    {
        var c = Find(bodies[k], point);
        if (d.Length == 1) d = Enumerable.Repeat(d[0], 3).ToArray();
        if (d.Length != 3) throw new RebuildException(OperationOutcome.InvalidInput, "Vertex chamfer: 3 distances are needed.");
        var q = new Vector3d[3];
        for (int i = 0; i < 3; i++)
        {
            if (!(d[i] > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Vertex chamfer: distances must be greater than zero.");
            double di = d[i];
            q[i] = Along(c.Edges[i].E.Curve, c.Edges[i].FromV0, x => (x - c.P).Length - di)
                   ?? throw new RebuildException(OperationOutcome.InvalidInput, $"Vertex chamfer: distance {di:0.###} does not fit on an edge (length {c.Lengths[i]:0.###}).");
        }
        Cut(bodies, k, c, q, null, warnings);
        warnings.Add($"Vertex chamfer ({(c.Concave ? "concave, material added" : "convex")}, curved corner): 3 edges, {string.Join(" × ", d.Select(x => x.ToString("0.###")))}.");
        return true;
    }

    public static bool Round(List<TSolid> bodies, int k, Vector3d point, double r, List<string> warnings)
    {
        var c = Find(bodies[k], point);
        // центр — по касательным лучам в вершине (как у прямого угла)
        var vc = new VertexOp.Corner(null!, c.P, c.Tangents, c.Lengths, c.Edges.Select(x => x.FromV0 ? x.E.V1.P : x.E.V0.P).ToArray(), c.Concave, c.Normals);
        var (centre0, _) = VertexOp.RoundGeometry(vc, r);
        // ребро, уходящее от касательного луча (выпуклая дуга отверстия), сферу на оси угла не встречает: центр — ближе к вершине
        // (наибольшее λ ∈ (R, λ₀], при котором сфера встречает все три ребра; у прямых рёбер — λ₀, как у прямого угла)
        var axis = (centre0 - c.P); double lam0 = axis.Length; axis /= lam0;
        Vector3d[]? Meet(double lam) { var cc = c.P + axis * lam; var qq = new Vector3d[3]; for (int i = 0; i < 3; i++) { if (MeetSphere(c.Edges[i].E.Curve, c.Edges[i].FromV0, cc, r) is not { } m) return null; qq[i] = m; } return qq; }
        // касание сферы и прямого ребра (λ₀) — вырожденный случай для булевой (кусок угла и шар касаются по точке рёбер):
        // центр ближе к вершине на 1e-3 λ₀ — сфера пересекает рёбра трансверсально (угол встречи ~ 2.6°)
        double lam = lam0 * (1 - 1e-3); var q = Meet(lam);
        if (q == null)
        {
            double lo = r * (1 + 1e-6), hi = lam;
            if (Meet(lo) == null) throw new RebuildException(OperationOutcome.InvalidInput, $"Vertex round: the sphere R {r:0.###} does not meet an edge of the corner (the edge is too short).");
            for (int it = 0; it < 60; it++) { double m = (lo + hi) / 2; if (Meet(m) != null) lo = m; else hi = m; }
            // на пределе дуга лишь касается сферы — центр ещё ближе к вершине (ребро входит в шар трансверсально)
            lam = Math.Max(r * (1 + 1e-3), lo - 5e-3 * (lo - r) - 1e-3 * r); q = Meet(lam) ?? throw new RebuildException(OperationOutcome.InvalidInput, "Vertex round: the sphere does not meet an edge of the corner.");
        }
        var centre = c.P + axis * lam;
        // плоскость резца — через точки рёбер НАИБОЛЬШЕГО сближения с центром (внутри шара, за точками встречи): сфера не
        // проходит через вершины куска угла (иначе булева «кусок − шар» вырождена); между плоскостью и сферой вне шара — только
        // кончик угла (шар выпукл)
        for (int i = 0; i < 3; i++) if (Deepest(c.Edges[i].E.Curve, c.Edges[i].FromV0, centre) is { } dp && (dp - centre).Length < r * (1 - 1e-4)) q[i] = dp;
        double margin = Math.Max(VertexOp.BallMargin, 0.5 * Math.Pow(1e-3 / r, 2));
        Cut(bodies, k, c, q, PrimitiveOp.Sphere(centre, r * (1 + margin)), warnings);
        warnings.Add($"Vertex round ({(c.Concave ? "concave, material added" : "convex")}, curved corner): 3 edges, R {r:0.###}.");
        return true;
    }

    static void Cut(List<TSolid> bodies, int k, Corner c, Vector3d[] q, TSolid? ball, List<string> warnings)
    {
        var n = Vector3d.Cross(q[1] - q[0], q[2] - q[0]);
        if (n.Length < 1e-12) throw new RebuildException(OperationOutcome.InvalidInput, "Vertex: the points on the edges are collinear.");
        n.Normalize(); if (Vector3d.Dot(c.P - q[0], n) < 0) n = -n;
        double h = Vector3d.Dot(c.P - q[0], n);
        if (!(h > 1e-6)) throw new RebuildException(OperationOutcome.InvalidInput, "Vertex: the cut plane passes through the vertex.");
        var ctr = (q[0] + q[1] + q[2]) / 3; var foot = c.P - n * h;
        double rad = new[] { q[0], q[1], q[2], foot }.Max(x => ((x - ctr) - n * Vector3d.Dot(x - ctr, n)).Length);
        // резец: цилиндр от плоскости (основание на ней) за вершину; радиус — с запасом над следом угла
        double R = 1.6 * rad + 1e-3, H = 1.5 * h + 1e-3;
        var cyl = PrimitiveOp.Cylinder(Vector3d.Zero, R, H);
        var fr = Frame3.FromNormal(ctr, n);
        var tool = TransformOp.Copy(cyl, new TransformOp.Xform(p => fr.Origin + fr.U * p.X + fr.V * p.Y + fr.W * p.Z, dd => fr.U * dd.X + fr.V * dd.Y + fr.W * dd.Z, false));
        // кусок угла: у выпуклого — тело ∩ резец (без шара у скругления); у вогнутого — пустота у вершины: резец − тело
        var piece = new List<TSolid> { TopologyClone.Deep(c.Concave ? tool : bodies[k]) };
        if (c.Concave) BooleanOp.Subtract(piece, bodies[k], warnings); else BooleanOp.Intersect(piece, tool, warnings);
        var prevDbg = BooleanOp.AnalyticRouteDebug; if (Dbg) BooleanOp.AnalyticRouteDebug = m => Console.Error.WriteLine("  route: " + m);
        try { if (ball != null && piece.Count > 0) BooleanOp.Subtract(piece, ball, warnings); } finally { BooleanOp.AnalyticRouteDebug = prevDbg; }
        if (piece.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Vertex: nothing to change at the corner.");
        if (Dbg) Console.Error.WriteLine($"VCURVED h {h:G4} rad {rad:G4} pieces {piece.Count} [{string.Join(",", piece.Select(x => $"{x.Guarantee}:{AnalyticVolume.Of(x)}"))}] tool {AnalyticVolume.Of(tool)}");
        var work = new List<TSolid> { bodies[k] };
        foreach (var pc in piece) { if (c.Concave) BooleanOp.Unite(work, pc, warnings); else BooleanOp.Subtract(work, pc, warnings); }
        if (work.Count != 1) throw new RebuildException(OperationOutcome.Unsupported, $"Vertex: the corner cut split the body ({work.Count} bodies).");
        bodies[k] = work[0];
        Interlocked.Increment(ref Built);
    }
}
