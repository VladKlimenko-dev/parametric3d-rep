using OpenTK.Mathematics;
using Parametric3D.App.Sketching;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Operations;

/// <summary>Топология для выбора и ручек: касательные цепочки рёбер, нормали, контур грани в эскиз.</summary>
public static class Topo
{
    /// <summary>Касание по углу: рёбра в общей вершине идут одним направлением (до 1°).</summary>
    private const double TangentCos = 0.99985;

    /// <summary>Направление ребра в его вершине (единичное), наружу от вершины не важно — сравнивается по модулю.</summary>
    private static Vector3d? DirAt(TEdge e, TVertex v)
    {
        var c = e.Curve;
        if (c == null) return null;
        double t = (c.PointAt(c.T0) - v.P).LengthSquared <= (c.PointAt(c.T1) - v.P).LengthSquared ? c.T0 : c.T1;
        var d = c.TangentAt(t);
        return d.LengthSquared > 1e-24 ? d.Normalized() : null;
    }

    /// <summary>П. 4: ребро и все рёбра, касательно продолжающие его через вершины (цепочка G1).</summary>
    public static List<TEdge> TangentChain(TEdge start)
    {
        var res = new List<TEdge> { start };
        var seen = new HashSet<TEdge> { start };
        var queue = new Queue<TEdge>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (v == null || DirAt(e, v) is not { } d) continue;
                foreach (var o in v.Edges)
                {
                    if (seen.Contains(o) || o.V0 == null || o.V1 == null) continue;
                    if (DirAt(o, v) is not { } d2) continue;
                    if (Math.Abs(Vector3d.Dot(d, d2)) < TangentCos) continue;
                    seen.Add(o); res.Add(o); queue.Enqueue(o);
                }
            }
        }
        return res;
    }

    public static List<PickRef> ChainRefs(Part part, PickRef r)
    {
        if (part.FindEdge(r.Id) is not { } e) return new() { r };
        var chain = TangentChain(e);
        var list = new List<PickRef> { r };
        list.AddRange(chain.Where(x => x != e).Select(x => new PickRef(x.Id, PickKind.Edge, part, null)));
        return list;
    }

    /// <summary>Грани ребра.</summary>
    public static IEnumerable<TFace> FacesOf(Part p, TEdge e) =>
        p.Bodies.SelectMany(b => b.Faces).Where(f => f.Edges.Contains(e));

    /// <summary>Внешняя нормаль грани около точки (по сетке, если грань не задана точно).</summary>
    public static Vector3d FaceNormal(Part p, TFace f, Vector3d near)
    {
        var n = f.NormalAt(near);
        if (n.LengthSquared > 1e-18) return n.Normalized();
        return Refs.FacePlane(p, f.Id)?.N ?? Vector3d.UnitZ;
    }

    /// <summary>Середина ребра и направление «наружу» — биссектриса нормалей смежных граней.</summary>
    public static (Vector3d At, Vector3d Out)? EdgeHandle(Part p, uint edgeId)
    {
        if (p.FindEdge(edgeId) is not { Curve: { } c } e) return null;
        var mid = c.PointAt(0.5 * (c.T0 + c.T1));
        var n = Vector3d.Zero;
        foreach (var f in FacesOf(p, e)) n += FaceNormal(p, f, mid);
        if (n.LengthSquared < 1e-12) return null;
        return (mid, n.Normalized());
    }

    /// <summary>Вершина и направление «наружу» — сумма нормалей граней вершины.</summary>
    public static (Vector3d At, Vector3d Out)? VertexHandle(Part p, uint vertexId)
    {
        if (p.FindVertex(vertexId) is not { } v) return null;
        var faces = v.Edges.SelectMany(e => FacesOf(p, e)).Distinct().ToList();
        var n = Vector3d.Zero;
        foreach (var f in faces) n += FaceNormal(p, f, v.P);
        if (n.LengthSquared < 1e-12) return null;
        return (v.P, n.Normalized());
    }

    /// <summary>Точка грани и её нормаль; плоская ли.</summary>
    public static (Vector3d At, Vector3d Out, bool Planar)? FaceHandle(Part p, uint faceId)
    {
        if (Refs.FacePlane(p, faceId) is { } pl) return (pl.P, pl.N, true);
        var c = Refs.FacePoint(p, faceId);
        if (c == null || p.FindFace(faceId) is not { } f) return null;
        return (c.Value, FaceNormal(p, f, c.Value), false);
    }

    // ================================================================ быстрый предпросмотр ручки (без расчёта ядром)

    /// <summary>Отрезки предпросмотра для величины ручки (мм).</summary>
    public delegate List<(Vector3d A, Vector3d B)> Ghost(double value);

    /// <summary>Направление «в грань» от ребра в его точке: перпендикуляр к ребру в плоскости касания грани.</summary>
    private static Vector3d InFace(Part p, TFace f, Vector3d at, Vector3d t, double sign)
    {
        var n = FaceNormal(p, f, at);
        var d = Vector3d.Cross(n, t);
        return d.LengthSquared > 1e-18 ? d.Normalized() * sign : Vector3d.Zero;
    }

    /// <summary>Знак «в грань»: по ближайшему к середине ребра треугольнику сетки этой грани.</summary>
    private static double InFaceSign(Part p, TFace f, Vector3d mid, Vector3d t)
    {
        var d = Vector3d.Cross(FaceNormal(p, f, mid), t);
        var md = p.Mesh;
        if (md == null || d.LengthSquared < 1e-18) return 1;
        double best = double.MaxValue; Vector3d c = mid;
        var v = md.Vertices; var ix = md.Indices;
        for (int i = 0; i + 2 < ix.Length; i += 3)
        {
            var a = v[ix[i]];
            if (a.FaceId != f.Id) continue;
            var m = (a.P + v[ix[i + 1]].P + v[ix[i + 2]].P) / 3f;
            var mc = new Vector3d(m.X, m.Y, m.Z);
            // точка треугольника — не на самом ребре
            var off = mc - mid; off -= t * Vector3d.Dot(off, t);
            if (off.LengthSquared < 1e-12) continue;
            double dd = (mc - mid).LengthSquared;
            if (dd < best) { best = dd; c = mc; }
        }
        return Vector3d.Dot(c - mid, d) >= 0 ? 1 : -1;
    }

    private static List<double> Samples(ICurve3 c)
    {
        int n = c is LineCurve3 ? 1 : 24;
        return Enumerable.Range(0, n + 1).Select(i => c.T0 + (c.T1 - c.T0) * i / n).ToList();
    }

    /// <summary>
    /// Предпросмотр фаски (round=false) или скругления рёбер: границы на гранях и сечения — мгновенно, по геометрии рёбер;
    /// само тело считается один раз, когда ручку отпустили.
    /// </summary>
    public static Ghost? EdgeGhost(Part p, IEnumerable<uint> edgeIds, bool round)
    {
        var rows = new List<List<(Vector3d P, Vector3d D1, Vector3d D2)>>();
        foreach (var id in edgeIds.Distinct())
        {
            if (p.FindEdge(id) is not { Curve: { } c } e) continue;
            var faces = FacesOf(p, e).Take(3).ToList();
            if (faces.Count < 2) continue;
            double tm = 0.5 * (c.T0 + c.T1);
            var mid = c.PointAt(tm);
            var tmid = c.TangentAt(tm);
            if (tmid.LengthSquared < 1e-24) continue;
            tmid.Normalize();
            double s1 = InFaceSign(p, faces[0], mid, tmid), s2 = InFaceSign(p, faces[1], mid, tmid);
            var row = new List<(Vector3d, Vector3d, Vector3d)>();
            foreach (var t in Samples(c))
            {
                var at = c.PointAt(t);
                var tg = c.TangentAt(t);
                if (tg.LengthSquared < 1e-24) continue;
                tg.Normalize();
                var d1 = InFace(p, faces[0], at, tg, s1); var d2 = InFace(p, faces[1], at, tg, s2);
                if (d1 == Vector3d.Zero || d2 == Vector3d.Zero) continue;
                row.Add((at, d1, d2));
            }
            if (row.Count >= 2) rows.Add(row);
        }
        if (rows.Count == 0) return null;
        return v =>
        {
            var res = new List<(Vector3d, Vector3d)>();
            foreach (var row in rows)
            {
                List<Vector3d>? prev = null;
                for (int i = 0; i < row.Count; i++)
                {
                    var (P, d1, d2) = row[i];
                    var sec = Section(P, d1, d2, v, round);
                    if (sec.Count < 2) continue;
                    // сечение — у концов и через одно; границы и середина — вдоль ребра
                    if (i == 0 || i == row.Count - 1 || i % 3 == 0 || row.Count <= 3)
                        for (int k = 1; k < sec.Count; k++) res.Add((sec[k - 1], sec[k]));
                    if (prev != null && prev.Count == sec.Count)
                    {
                        res.Add((prev[0], sec[0]));
                        res.Add((prev[^1], sec[^1]));
                        if (round) res.Add((prev[sec.Count / 2], sec[sec.Count / 2]));
                    }
                    prev = sec;
                }
            }
            return res;
        };
    }

    /// <summary>Сечение фаски (отрезок) или скругления (дуга, касательная к обеим граням) в точке ребра.</summary>
    private static List<Vector3d> Section(Vector3d P, Vector3d d1, Vector3d d2, double v, bool round)
    {
        if (!round) return new() { P + d1 * v, P + d2 * v };
        double phi = Math.Acos(Math.Clamp(Vector3d.Dot(d1, d2), -1, 1));
        if (phi < 1e-3 || phi > Math.PI - 1e-3) return new();
        double t = v / Math.Tan(phi / 2);
        var bis = (d1 + d2).Normalized();
        var c = P + bis * (v / Math.Sin(phi / 2));
        var a = P + d1 * t - c; var b = P + d2 * t - c;
        double ang = Math.Acos(Math.Clamp(Vector3d.Dot(a.Normalized(), b.Normalized()), -1, 1));
        var res = new List<Vector3d>();
        const int n = 8;
        for (int i = 0; i <= n; i++)
        {
            double s = (double)i / n;
            // сферическая интерполяция векторов одной длины
            double sa = Math.Sin(ang);
            var q = sa < 1e-9 ? a : a * (Math.Sin((1 - s) * ang) / sa) + b * (Math.Sin(s * ang) / sa);
            res.Add(c + q);
        }
        return res;
    }

    /// <summary>Предпросмотр фаски угла (многоугольник срезa) или скругления вершины (дуги между рёбрами).</summary>
    public static Ghost? VertexGhost(Part p, uint vertexId, bool round)
    {
        if (p.FindVertex(vertexId) is not { } vx || VertexHandle(p, vertexId) is not { } h) return null;
        var dirs = new List<Vector3d>();
        foreach (var e in vx.Edges)
            if (DirAt(e, vx) is { } d)
            {
                var c = e.Curve!;
                double t = (c.PointAt(c.T0) - vx.P).LengthSquared <= (c.PointAt(c.T1) - vx.P).LengthSquared ? c.T0 : c.T1;
                var other = c.PointAt(t == c.T0 ? c.T1 : c.T0);
                dirs.Add(Vector3d.Dot(d, other - vx.P) >= 0 ? d : -d);
            }
        if (dirs.Count < 2) return null;
        // порядок обхода — по углу вокруг направления ручки
        var n = h.Out;
        var u = Vector3d.Cross(n, Math.Abs(n.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
        var w = Vector3d.Cross(n, u);
        dirs = dirs.OrderBy(d => Math.Atan2(Vector3d.Dot(d, w), Vector3d.Dot(d, u))).ToList();
        var V = vx.P;
        return v =>
        {
            var res = new List<(Vector3d, Vector3d)>();
            var q = dirs.Select(d => V + d * v).ToList();
            for (int i = 0; i < q.Count; i++)
            {
                var a = q[i]; var b = q[(i + 1) % q.Count];
                if (!round) { res.Add((a, b)); continue; }
                Vector3d prev = a;
                for (int k = 1; k <= 8; k++)
                {
                    double s = k / 8.0;
                    var pt = a * ((1 - s) * (1 - s)) + V * (2 * s * (1 - s)) + b * (s * s);
                    res.Add((prev, pt)); prev = pt;
                }
            }
            return res;
        };
    }

    /// <summary>Предпросмотр выдавливания грани (sign=+1), выреза (−1) или оболочки (контур, сдвинутый внутрь по нормалям).</summary>
    public static Ghost? FaceGhost(Part p, uint faceId, int sign)
    {
        if (p.FindFace(faceId) is not { } f) return null;
        var loops = new List<List<(Vector3d P, Vector3d N)>>();
        foreach (var e in f.Edges)
        {
            if (e.Curve == null) continue;
            var pts = new List<Vector3d>();
            e.Curve.Tessellate(pts, true);
            if (pts.Count < 2) continue;
            loops.Add(pts.Select(q => (q, FaceNormal(p, f, q))).ToList());
        }
        if (loops.Count == 0) return null;
        return v =>
        {
            var res = new List<(Vector3d, Vector3d)>();
            foreach (var l in loops)
            {
                for (int i = 1; i < l.Count; i++)
                    res.Add((l[i - 1].P + l[i - 1].N * (sign * v), l[i].P + l[i].N * (sign * v)));
                // боковые рёбра — у концов рёбер грани
                res.Add((l[0].P, l[0].P + l[0].N * (sign * v)));
            }
            return res;
        };
    }

    /// <summary>
    /// Эскиз с контуром плоской грани (внешний и внутренние контуры — отрезками, дугами и окружностями, общие точки
    /// сшиты): основа выдавливания грани ручкой. null — грань не плоская или контур не получился.
    /// </summary>
    public static Sketch? FaceOutlineSketch(Part p, uint faceId, string name)
    {
        if (p.FindFace(faceId) is not { } f || Refs.FacePlane(p, faceId) is not { } pl) return null;
        var frame = Frame3.FromNormal(pl.P, pl.N);
        var sk = new Sketch(name, frame);
        sk.SetBasePlane(frame, faceId, Loc.L("Грань #{0}", faceId));
        var pool = new EdgeProjection.PointPool(SkRole.Object, "");
        foreach (var e in f.Edges)
        {
            if (e.Curve == null) continue;
            foreach (var ent in EdgeProjection.Project(sk, e.Curve, pool, SkRole.Object, ""))
                if (ent is not SkPoint) sk.AddEntity(ent);
        }
        if (sk.Entities.Count == 0) return null;
        // контур — зафиксирован (это граница грани, а не свободный чертёж)
        foreach (var ent in sk.Entities) ent.Fixed = true;
        return sk;
    }
}
