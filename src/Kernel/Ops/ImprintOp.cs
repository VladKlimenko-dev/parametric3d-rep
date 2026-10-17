using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// u13i (FreeForm): ОТПЕЧАТОК КРИВОЙ НА ПЛОСКОЙ ГРАНИ — грань делится по кривой, форма тела не меняется.
/// Замкнутая кривая внутри грани — новая грань (область кривой) и отверстие-контур в прежней. Незамкнутая — от края до
/// края грани: рёбра границы в точках выхода делятся (отрезки и дуги), грань — на две по цепочке кривой; отверстия грани
/// уходят в ту часть, где лежат. Рёбра кривой — отрезки (ломаная) или один сплайн (гладкая кривая). Булевой так не
/// сделать: объединение сливает соседние грани одной плоскости обратно.
/// </summary>
public static class ImprintOp
{
    /// <param name="pts">Точки кривой (проецируются на плоскость грани); замкнутая — первая совпадает с последней.</param>
    /// <param name="smooth">Гладкая кривая (сплайн) — одним ребром; null — рёбра-отрезки по точкам.</param>
    public static TSolid? Apply(TSolid body, TFace face, IReadOnlyList<Vector3d> pts, SplineCurve3? smooth, ref string? why)
    {
        if (body.Exact != null && body.ExactAuthoritative) { why = "the body shape is defined by its exact polyhedral form"; return null; }
        var on = PlanarTweak.OutwardNormal(face);
        var src = GeometryAudit.AuthoritativeSurface(face) as PlaneSurface;
        if (on is not { } n || src == null) { why = "the face is not planar"; return null; }
        var fr = Frame3.FromNormal(src.F.Origin, n);
        Vector3d Proj(Vector3d p) => p - n * Vector3d.Dot(p - fr.Origin, n);
        var q = new List<Vector3d>();
        foreach (var p in pts) { var pp = Proj(p); if (q.Count == 0 || (pp - q[^1]).Length > Tol.Weld) q.Add(pp); }
        bool closed = q.Count > 3 && (q[0] - q[^1]).Length < 10 * Tol.Weld;
        if (closed) q.RemoveAt(q.Count - 1);
        if (q.Count < 2 || (closed && q.Count < 3)) { why = "the curve is too short"; return null; }

        var outerSrc = face.Outer?.Ordered();
        if (outerSrc == null) { why = "the face boundary is not closed"; return null; }
        var outer2 = Poly(face.Outer!).Select(fr.ToLocal2).ToList();
        var holes2 = face.Inners.Select(l => Poly(l).Select(fr.ToLocal2).ToList()).ToList();
        var q2 = q.Select(fr.ToLocal2).ToList();
        double size = Math.Max(1, Extent(outer2));
        double tol = Math.Max(Tol.Weld * 10, 1e-7 * size);

        var dst = TopologyClone.Deep(body);
        int fi = body.Faces.ToList().IndexOf(face);
        var df = dst.Faces.ElementAt(fi);
        dst.BeginEdit("Imprint", EditKind.Layout);
        var plane = new PlaneSurface(src.F);
        if (df.Surface is FacetedSurface) df.Surface = plane;        // разметка грани — заново по контурам
        df.InteriorHint = null;

        if (closed)
        {
            // вся кривая — внутри грани, мимо отверстий, без самопересечений с границей
            if (!q2.All(p => Inside(outer2, p) && !holes2.Any(h => Inside(h, p)))) { why = "the closed curve must lie inside the face"; return null; }
            if (Crosses(q2, true, outer2) || holes2.Any(h => Crosses(q2, true, h))) { why = "the closed curve crosses the face boundary"; return null; }
            var chain = Chain(q, true, smooth, null, null);
            // ориентация: новая грань обходится как прежний внешний контур; отверстие — наоборот
            bool same = Math.Sign(Area(q2)) == Math.Sign(Area(outer2));
            var hole = df.AddLoop(outer: false);
            AddChain(hole, chain.Edges, forward: !same);
            var nf = NewFace(df, plane);
            AddChain(nf.AddLoop(outer: true), chain.Edges, forward: same);
            // отверстия прежней грани внутри новой области — уходят в неё
            foreach (var hl in df.Inners.Where(l => !ReferenceEquals(l, hole)).ToList())
                if (Inside(q2, fr.ToLocal2(Poly(hl)[0]))) MoveLoop(hl, df, nf);
            return Finish(dst);
        }

        // незамкнутая: концы продлеваются до границы по касательной; кусок кривой между выходами на контур вокруг её середины
        var cut = OpenPath(outer2, q2, size);
        if (cut == null) { why = "the open curve must cross the face from edge to edge"; return null; }
        var (p0, inner2, p1) = cut.Value;
        if (holes2.Any(h => Crosses(new[] { p0 }.Concat(inner2).Append(p1).ToList(), false, h))) { why = "the curve crosses a hole of the face"; return null; }
        var dOuter = df.Outer!.Ordered()!;
        var x0 = SplitAt(dst, dOuter, fr.ToWorld(p0), tol, ref why); if (x0 == null) return null;
        dOuter = df.Outer!.Ordered()!;
        var x1 = SplitAt(dst, dOuter, fr.ToWorld(p1), tol, ref why); if (x1 == null) return null;
        if (ReferenceEquals(x0, x1)) { why = "the curve enters and leaves the face at the same point"; return null; }
        var path = new List<Vector3d> { x0.P };
        path.AddRange(inner2.Select(fr.ToWorld).Where(p => (p - x0.P).Length > tol && (p - x1.P).Length > tol));
        path.Add(x1.P);
        bool smoothFits = smooth != null && (smooth.PointAt(0) - x0.P).Length < tol && (smooth.PointAt(1) - x1.P).Length < tol;
        var ch = Chain(path, false, smoothFits ? smooth : null, x0, x1);
        // контур грани: от x1 до x0 — первая часть, от x0 до x1 — вторая
        var loop = df.Outer!.Ordered()!;
        int i0 = loop.FindIndex(c => ReferenceEquals(c.Start, x0)), i1 = loop.FindIndex(c => ReferenceEquals(c.Start, x1));
        if (i0 < 0 || i1 < 0) { why = "the exit points are not on the face boundary"; return null; }
        List<(TEdge E, bool R)> Arc(int from, int to)
        {
            var res = new List<(TEdge, bool)>();
            for (int k = from; k != to; k = (k + 1) % loop.Count) res.Add((loop[k].Edge, loop[k].Reversed));
            return res;
        }
        var partA = Arc(i1, i0);            // x1 → x0 по контуру; замыкается кривой x0 → x1
        var partB = Arc(i0, i1);            // x0 → x1 по контуру; замыкается кривой x1 → x0
        var outerLoop = df.Outer!;
        foreach (var ce in outerLoop.CoEdges.ToList()) { ce.Edge.CoEdges.Remove(ce); outerLoop.CoEdges.Remove(ce); }
        AddChain(outerLoop, ch.Edges, forward: true);
        foreach (var (e, r) in partA) outerLoop.Add(e, r);
        var nf2 = NewFace(df, plane);
        var nl = nf2.AddLoop(outer: true);
        foreach (var (e, r) in partB) nl.Add(e, r);
        AddChain(nl, ch.Edges, forward: false);
        // отверстия: в ту часть, где лежат
        var polyB = Poly(nl).Select(fr.ToLocal2).ToList();
        foreach (var hl in df.Inners.ToList())
            if (Inside(polyB, fr.ToLocal2(Poly(hl)[0]))) MoveLoop(hl, df, nf2);
        return Finish(dst);
    }

    /// <summary>Соглашение K05 заново (обход контуров — против часовой стрелки вокруг наружной нормали), сетка — заново.</summary>
    static TSolid Finish(TSolid dst)
    {
        dst.TopologyConvention = 0;
        TopologyConventions.Normalize(dst);
        dst.DisplayMeshKey = null;
        return dst;
    }

    static TFace NewFace(TFace like, ISurface3 surface)
    {
        var nf = new TFace { Surface = surface, Reversed = like.Reversed, MaterialSlot = like.MaterialSlot, SourceTag = like.SourceTag };
        nf.Tag = like.Tag.Length > 0 ? like.Tag + ":imprint" : "imprint";
        like.Shell!.Add(nf);
        return nf;
    }

    static void MoveLoop(TLoop l, TFace from, TFace to)
    {
        var items = l.CoEdges.Select(c => (c.Edge, c.Reversed)).ToList();
        foreach (var ce in l.CoEdges.ToList()) { ce.Edge.CoEdges.Remove(ce); l.CoEdges.Remove(ce); }
        from.Loops.Remove(l);
        var nl = to.AddLoop(outer: false);
        foreach (var (e, r) in items) nl.Add(e, r);
    }

    sealed class ChainResult { public List<TEdge> Edges = new(); }

    /// <summary>Рёбра кривой по точкам: отрезки, или один сплайн; концы — данные вершины (у замкнутой — одна).</summary>
    static ChainResult Chain(List<Vector3d> q, bool closed, SplineCurve3? smooth, TVertex? first, TVertex? last)
    {
        var res = new ChainResult();
        if (smooth != null)
        {
            var a = first ?? new TVertex(smooth.PointAt(0));
            var b = closed ? a : last ?? new TVertex(smooth.PointAt(1));
            res.Edges.Add(Link(a, b, smooth));
            return res;
        }
        var vs = new List<TVertex>();
        for (int i = 0; i < q.Count; i++)
            vs.Add(i == 0 && first != null ? first : i == q.Count - 1 && last != null && !closed ? last : new TVertex(q[i]));
        int segs = closed ? q.Count : q.Count - 1;
        for (int i = 0; i < segs; i++)
        {
            var a = vs[i]; var b = vs[(i + 1) % vs.Count];
            res.Edges.Add(Link(a, b, new LineCurve3(a.P, b.P)));
        }
        return res;
    }

    static TEdge Link(TVertex a, TVertex b, ICurve3 c)
    {
        var e = new TEdge { V0 = a, V1 = b, Curve = c };
        a.Edges.Add(e); if (!ReferenceEquals(a, b)) b.Edges.Add(e);
        return e;
    }

    static void AddChain(TLoop l, List<TEdge> edges, bool forward)
    {
        if (forward) foreach (var e in edges) l.Add(e, false);
        else for (int i = edges.Count - 1; i >= 0; i--) l.Add(edges[i], true);
    }

    /// <summary>Точка контура (вершина рядом — она же; иначе ребро делится: отрезок или дуга).</summary>
    static TVertex? SplitAt(TSolid dst, List<TCoEdge> loop, Vector3d x, double tol, ref string? why)
    {
        foreach (var ce in loop) if ((ce.Start.P - x).Length <= tol) return ce.Start;
        TCoEdge? best = null; double bd = double.MaxValue, bt = 0;
        foreach (var ce in loop)
        {
            double t = Analytic.FaceSplit.ClosestParam(ce.Edge.Curve, x);
            double d = (ce.Edge.Curve.PointAt(t) - x).Length;
            if (d < bd) { bd = d; best = ce; bt = t; }
        }
        if (best == null || bd > 1e3 * tol) { why = "the exit point is not on the face boundary"; return null; }
        var e = best.Edge;
        ICurve3 c1, c2;
        switch (e.Curve)
        {
            case LineCurve3 ln: c1 = new LineCurve3(ln.A, x); c2 = new LineCurve3(x, ln.B); break;
            case ArcCurve3 arc:
            {
                double am = arc.A0 + arc.Sweep * bt;
                c1 = new ArcCurve3(arc.Frame, arc.Radius, arc.A0, am); c2 = new ArcCurve3(arc.Frame, arc.Radius, am, arc.A1);
                x = c1.PointAt(1);
                break;
            }
            default: why = $"the boundary edge is a {e.Curve.GetType().Name} (only lines and arcs can be split)"; return null;
        }
        var v = new TVertex(x);
        var e1 = Link(e.V0, v, c1); var e2 = Link(v, e.V1, c2);
        foreach (var ce in e.CoEdges.ToList())
        {
            var l = ce.Loop!; bool r = ce.Reversed;
            e.CoEdges.Remove(ce); l.CoEdges.Remove(ce);
            if (!r) { l.Add(e1, false); l.Add(e2, false); } else { l.Add(e2, true); l.Add(e1, true); }
        }
        e.V0.Edges.Remove(e); if (!ReferenceEquals(e.V0, e.V1)) e.V1.Edges.Remove(e);
        return v;
    }

    /// <summary>
    /// Кусок незамкнутой кривой внутри контура: кривая продлевается лучами с концов; выходы на контур — ближайшие к её
    /// середине с обеих сторон. (точка входа, точки между, точка выхода) или null.
    /// </summary>
    static (Vector2d P0, List<Vector2d> Inner, Vector2d P1)? OpenPath(List<Vector2d> poly, List<Vector2d> q, double size)
    {
        double big = 10 * size + 1;
        var ext = new List<Vector2d> { q[0] + (q[0] - q[1]).Normalized() * big };
        ext.AddRange(q);
        ext.Add(q[^1] + (q[^1] - q[^2]).Normalized() * big);
        var hits = new List<(double T, Vector2d P)>();
        for (int i = 0; i + 1 < ext.Count; i++)
        {
            var a = ext[i]; var r = ext[i + 1] - a;
            for (int k = 0; k < poly.Count; k++)
            {
                var c = poly[k]; var sv = poly[(k + 1) % poly.Count] - c;
                double den = r.X * sv.Y - r.Y * sv.X;
                if (Math.Abs(den) < 1e-18) continue;
                var ac = c - a;
                double t = (ac.X * sv.Y - ac.Y * sv.X) / den, u = (ac.X * r.Y - ac.Y * r.X) / den;
                if (t < -1e-12 || t > 1 + 1e-12 || u < -1e-12 || u > 1 + 1e-12) continue;
                hits.Add((i + t, a + r * t));
            }
        }
        hits.Sort((x, y) => x.T.CompareTo(y.T));
        double mid = 1 + (q.Count - 1) / 2.0;
        var before = hits.Where(h => h.T < mid).ToList(); var after = hits.Where(h => h.T > mid).ToList();
        if (before.Count == 0 || after.Count == 0) return null;
        var h0 = before[^1]; var h1 = after[0];
        var inner = new List<Vector2d>();
        for (int i = 1; i < ext.Count - 1; i++) if (i > h0.T && i < h1.T) inner.Add(ext[i]);
        return (h0.P, inner, h1.P);
    }

    static List<Vector3d> Poly(TLoop l)
    {
        var res = new List<Vector3d>();
        var ord = l.Ordered() ?? l.CoEdges.ToList();
        for (int i = 0; i < ord.Count; i++) ord[i].Tessellate(res, i == 0);
        if (res.Count > 1 && (res[0] - res[^1]).Length < 1e-9) res.RemoveAt(res.Count - 1);
        return res;
    }

    static double Area(IReadOnlyList<Vector2d> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++) { var u = p[i]; var v = p[(i + 1) % p.Count]; a += u.X * v.Y - v.X * u.Y; }
        return a / 2;
    }

    static double Extent(IReadOnlyList<Vector2d> p) =>
        p.Count == 0 ? 0 : Math.Max(p.Max(v => v.X) - p.Min(v => v.X), p.Max(v => v.Y) - p.Min(v => v.Y));

    static bool Inside(IReadOnlyList<Vector2d> poly, Vector2d p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    static bool SegX(Vector2d a, Vector2d b, Vector2d c, Vector2d d)
    {
        double D(Vector2d p, Vector2d q, Vector2d r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        double d1 = D(c, d, a), d2 = D(c, d, b), d3 = D(a, b, c), d4 = D(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>Кривая пересекает контур (у незамкнутой — без концевых отрезков, которые и должны выходить на край).</summary>
    static bool Crosses(List<Vector2d> c, bool closed, List<Vector2d> poly, bool skipEnds = false)
    {
        int segs = closed ? c.Count : c.Count - 1;
        for (int i = 0; i < segs; i++)
        {
            if (skipEnds && (i == 0 || i == segs - 1)) continue;
            var a = c[i]; var b = c[(i + 1) % c.Count];
            for (int k = 0; k < poly.Count; k++) if (SegX(a, b, poly[k], poly[(k + 1) % poly.Count])) return true;
        }
        return false;
    }

    /// <summary>Выход конца кривой на контур: конец уже на контуре — он; иначе первое пересечение луча по касательной.</summary>
    static Vector2d? Exit(List<Vector2d> poly, Vector2d from, Vector2d dir, double tol)
    {
        for (int k = 0; k < poly.Count; k++)
        {
            var a = poly[k]; var b = poly[(k + 1) % poly.Count];
            var ab = b - a; double l2 = ab.LengthSquared; if (l2 < 1e-30) continue;
            double t = Math.Clamp(Vector2d.Dot(from - a, ab) / l2, 0, 1);
            if ((a + ab * t - from).Length <= tol) return a + ab * t;
        }
        // луч назад — тоже: конец мог выйти за контур (тогда точка выхода — позади конца)
        double best = double.MaxValue; Vector2d? res = null;
        for (int k = 0; k < poly.Count; k++)
        {
            var a = poly[k]; var b = poly[(k + 1) % poly.Count];
            var r = dir; var s = b - a;
            double den = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(den) < 1e-18) continue;
            var ac = a - from;
            double t = (ac.X * s.Y - ac.Y * s.X) / den, u = (ac.X * r.Y - ac.Y * r.X) / den;
            if (u < -1e-12 || u > 1 + 1e-12) continue;
            if (Math.Abs(t) < Math.Abs(best)) { best = t; res = from + r * t; }
        }
        return res;
    }
}
