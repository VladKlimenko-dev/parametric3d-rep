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

    /// <summary>
    /// u10: рёбра вершины. У тел после булевых операций список рёбер вершины не заполнен — тогда по рёбрам тела (та же вершина
    /// по ссылке или по месту). Прежде цепочка от половины кромки отверстия не продлевалась: выбиралась одна дуга, и ядро
    /// уходило в обходной путь резцом (≈1.2 с) вместо прямого построителя «целая окружность».
    /// </summary>
    public static IEnumerable<TEdge> EdgesAt(TVertex v, IReadOnlyList<TEdge>? all)
    {
        if (v.Edges.Any() || all == null) return v.Edges;
        return all.Where(e => ReferenceEquals(e.V0, v) || ReferenceEquals(e.V1, v)
                              || e.V0 != null && (e.V0.P - v.P).LengthSquared < 1e-14 || e.V1 != null && (e.V1.P - v.P).LengthSquared < 1e-14);
    }

    /// <summary>u12: кромка (граница касательных граней) — не ребро: скругление, фаска, массив по ней недоступны.</summary>
    public static bool IsSeam(PickRef r) => r.Kind == PickKind.Edge && r.Owner is Part p && p.FindEdge(r.Id) is { } e && Parametric3D.Kernel.Topology.EdgeClass.IsTangentSeam(e);

    /// <summary>
    /// u12 (замечание 13): плоскость цепочки рёбер (кромок) — если все выбранные рёбра лежат в одной плоскости и не на одной
    /// прямой: начало — центр точек, нормаль — по наибольшему разбросу. Иначе null.
    /// </summary>
    public static Frame3? CoplanarFrame(IReadOnlyList<PickRef> sel)
    {
        if (sel.Count == 0 || sel.Any(r => r.Kind != PickKind.Edge || r.Owner is not Part)) return null;
        var pts = new List<Vector3d>();
        foreach (var r in sel)
        {
            if (((Part)r.Owner!).FindEdge(r.Id) is not { Curve: { } c }) return null;
            for (int i = 0; i <= 8; i++) pts.Add(c.PointAt(c.T0 + (c.T1 - c.T0) * i / 8.0));
        }
        var o = pts.Aggregate(Vector3d.Zero, (a, b) => a + b) / pts.Count;
        double scale = pts.Max(q => (q - o).Length);
        if (!(scale > 1e-9)) return null;
        // нормаль: наибольшее векторное произведение пар направлений от центра
        Vector3d n = Vector3d.Zero; double best = 0;
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j += 3)
            {
                var c = Vector3d.Cross(pts[i] - o, pts[j] - o);
                if (c.Length > best) { best = c.Length; n = c; }
            }
        if (!(best > 1e-6 * scale * scale)) return null;          // на одной прямой
        n = n.Normalized();
        if (pts.Any(q => Math.Abs(Vector3d.Dot(q - o, n)) > 1e-6 * Math.Max(1, scale))) return null;
        // нормаль — от тела (по нормалям граней рёбер)
        var part = (Part)sel[0].Owner!;
        var e0 = part.FindEdge(sel[0].Id)!; var m = e0.Curve.PointAt(0.5 * (e0.Curve.T0 + e0.Curve.T1));
        var fn = FacesOf(part, e0).Select(f => FaceNormal(part, f, m)).Aggregate(Vector3d.Zero, (a, b) => a + b);
        if (Vector3d.Dot(fn, n) < 0) n = -n;
        return Frame3.FromNormal(n * Vector3d.Dot(o, n), n);
    }

    public static IReadOnlyList<TEdge> AllEdges(Part p) => p.Bodies.SelectMany(b => b.Edges).ToList();

    /// <summary>П. 4: ребро и все рёбра, касательно продолжающие его через вершины (цепочка G1).</summary>
    public static List<TEdge> TangentChain(TEdge start, IReadOnlyList<TEdge>? all = null)
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
                foreach (var o in EdgesAt(v, all))
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
        var chain = TangentChain(e, AllEdges(part));
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
        var fs = FacesOf(p, e).ToList();
        // u10: шов касательных граней (край скругления) — не ребро: ручки нет (скруглить/срезать там нечего)
        if (fs.Count == 2 && Vector3d.Dot(FaceNormal(p, fs[0], mid), FaceNormal(p, fs[1], mid)) > 0.9995) return null;
        if (Parametric3D.Kernel.Topology.EdgeClass.IsTangentSeam(e)) return null;
        foreach (var f in fs) n += FaceNormal(p, f, mid);
        if (n.LengthSquared < 1e-12) return null;
        return (mid, n.Normalized());
    }

    /// <summary>Вершина и направление «наружу» — сумма нормалей граней вершины.</summary>
    public static (Vector3d At, Vector3d Out)? VertexHandle(Part p, uint vertexId)
    {
        if (p.FindVertex(vertexId) is not { } v) return null;
        var faces = EdgesAt(v, AllEdges(p)).SelectMany(e => FacesOf(p, e)).Distinct().ToList();
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

    /// <summary>
    /// Знак «в грань»: пробные точки по обе стороны ребра (на малом шаге) — какая лежит на треугольниках этой грани.
    /// u10: прежде — по ближайшему центру треугольника; у кольцевой грани вокруг бобышки (длинные треугольники) центр
    /// оказывался по другую сторону, и граница полосы уходила внутрь бобышки (контур краснел у годного скругления).
    /// </summary>
    private static double InFaceSign(Part p, TFace f, Vector3d mid, Vector3d t)
    {
        var d = Vector3d.Cross(FaceNormal(p, f, mid), t);
        var md = p.Mesh;
        if (md == null || d.LengthSquared < 1e-18) return 1;
        d.Normalize();
        var tris = new List<(Vector3d A, Vector3d B, Vector3d C)>();
        var v = md.Vertices; var ix = md.Indices;
        static Vector3d D(OpenTK.Mathematics.Vector3 q) => new(q.X, q.Y, q.Z);
        for (int i = 0; i + 2 < ix.Length; i += 3)
            if (v[ix[i]].FaceId == f.Id) tris.Add((D(v[ix[i]].P), D(v[ix[i + 1]].P), D(v[ix[i + 2]].P)));
        if (tris.Count == 0) return 1;
        var lo = tris.Aggregate(new Vector3d(double.MaxValue), (m, q) => Vector3d.ComponentMin(m, q.A));
        var hi = tris.Aggregate(new Vector3d(double.MinValue), (m, q) => Vector3d.ComponentMax(m, q.A));
        double size = Math.Max((hi - lo).Length, 1e-6);
        foreach (double step in new[] { 2e-3, 1e-2, 4e-2 })
        {
            double h = step * size;
            double dp = tris.Min(q => DistToTriangle(mid + d * h, q.A, q.B, q.C));
            double dm = tris.Min(q => DistToTriangle(mid - d * h, q.A, q.B, q.C));
            if (Math.Abs(dp - dm) > 0.1 * h) return dp < dm ? 1 : -1;
        }
        return 1;
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
        // концы цепочки — точки, где кончается одно выбранное ребро и не начинается другое: там торцевое сечение
        var ends = rows.SelectMany(r => new[] { r[0].P, r[^1].P }).ToList();
        bool IsEnd(Vector3d q) => ends.Count(x => (x - q).LengthSquared < 1e-10) == 1;
        return v =>
        {
            // КОНТУР: две границы полосы на гранях (по всей цепочке) и торцевые сечения у концов цепочки — без внутренних
            // сечений и диагоналей
            var res = new List<(Vector3d, Vector3d)>();
            foreach (var row in rows)
            {
                List<Vector3d>? prev = null;
                for (int i = 0; i < row.Count; i++)
                {
                    var (P, d1, d2) = row[i];
                    var sec = Section(P, d1, d2, v, round);
                    if (sec.Count < 2) continue;
                    if ((i == 0 || i == row.Count - 1) && IsEnd(P))
                        for (int k = 1; k < sec.Count; k++) res.Add((sec[k - 1], sec[k]));
                    if (prev != null)
                    {
                        res.Add((prev[0], sec[0]));
                        res.Add((prev[^1], sec[^1]));
                    }
                    prev = sec;
                }
            }
            return res;
        };
    }

    // ---------------------------------------------------------------- u10: выйдет ли операция (красный контур)

    public static Action<string>? GhostDebug;

    /// <summary>Треугольники сетки детали (для проверки «точка на теле»).</summary>
    private static List<(Vector3d A, Vector3d B, Vector3d C)> Triangles(Part p)
    {
        var res = new List<(Vector3d, Vector3d, Vector3d)>();
        var md = p.Mesh;
        if (md == null) return res;
        var v = md.Vertices; var ix = md.Indices;
        static Vector3d D(OpenTK.Mathematics.Vector3 q) => new(q.X, q.Y, q.Z);
        for (int i = 0; i + 2 < ix.Length; i += 3) res.Add((D(v[ix[i]].P), D(v[ix[i + 1]].P), D(v[ix[i + 2]].P)));
        return res;
    }

    private static double DistToTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        // ближайшая точка треугольника (Эриксон, «Real-Time Collision Detection», 5.1.5)
        Vector3d ab = b - a, ac = c - a, ap = p - a;
        double d1 = Vector3d.Dot(ab, ap), d2 = Vector3d.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return (p - a).Length;
        Vector3d bp = p - b; double d3 = Vector3d.Dot(ab, bp), d4 = Vector3d.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return (p - b).Length;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + ab * (d1 / (d1 - d3)))).Length;
        Vector3d cp = p - c; double d5 = Vector3d.Dot(ab, cp), d6 = Vector3d.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return (p - c).Length;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + ac * (d2 / (d2 - d6)))).Length;
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return (p - (b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6)))).Length;
        double den = 1 / (va + vb + vc);
        return (p - (a + ab * (vb * den) + ac * (vc * den))).Length;
    }

    /// <summary>Проверка «все точки на поверхности тела» (допуск — доля размера детали).</summary>
    private static Func<IEnumerable<Vector3d>, bool> OnBodyTest(Part p)
    {
        var tris = Triangles(p);
        if (tris.Count == 0) return _ => true;
        var lo = tris.Aggregate(new Vector3d(double.MaxValue), (m, t) => Vector3d.ComponentMin(m, t.A));
        var hi = tris.Aggregate(new Vector3d(double.MinValue), (m, t) => Vector3d.ComponentMax(m, t.A));
        double tol = Math.Max(0.02, 2e-3 * (hi - lo).Length);
        return pts => pts.All(q => tris.Any(t => DistToTriangle(q, t.A, t.B, t.C) <= tol));
    }

    /// <summary>Точка на грани с той же нормалью (сама грань или касательно продолжающая её): треугольники сетки с нормалью n.</summary>
    private static Func<Vector3d, Vector3d, bool> OnFaceLikeTest(Part p)
    {
        var tris = Triangles(p).Select(t => (t.A, t.B, t.C, N: Vector3d.Cross(t.B - t.A, t.C - t.A))).Where(t => t.N.LengthSquared > 1e-24)
                               .Select(t => (t.A, t.B, t.C, N: t.N.Normalized())).ToList();
        if (tris.Count == 0) return (_, _) => true;
        var lo = tris.Aggregate(new Vector3d(double.MaxValue), (m, t) => Vector3d.ComponentMin(m, t.A));
        var hi = tris.Aggregate(new Vector3d(double.MinValue), (m, t) => Vector3d.ComponentMax(m, t.A));
        double tol = Math.Max(0.02, 2e-3 * (hi - lo).Length);
        return (q, n) => tris.Any(t => Math.Abs(Vector3d.Dot(t.N, n)) > 0.995 && DistToTriangle(q, t.A, t.B, t.C) <= tol);
    }

    /// <summary>
    /// Выйдет ли фаска (round=false) или скругление рёбер величиной v: граница полосы на каждой грани должна лежать на этой
    /// грани (или касательно продолжающей её). Скругление R150 у куба 100 уводит границу за край грани, фаска шире соседней
    /// грани — на другую грань: контур красный (u10).
    /// </summary>
    static readonly bool GhostArcOff = Environment.GetEnvironmentVariable("P3D_GHOSTARC") == "0";
    public static Func<double, bool>? EdgeGhostValid(Part p, IEnumerable<uint> edgeIds, bool round)
    {
        var onLike = OnFaceLikeTest(p);
        var onBody = OnBodyTest(p);
        var samples = new List<(Vector3d P, Vector3d D1, Vector3d D2, Vector3d N1, Vector3d N2)>();
        // u12b: ребро-дуга — полоса к центру дуги не шире её радиуса (скругление R4.2 на верхнем ребре бобышки R3.5 прежде
        // показывалось годным: точка касания за осью снова попадала на торец). Предел — по каждой стороне (P3D_GHOSTARC=0)
        // Только у полной окружности (все выбранные рёбра — дуги одного круга, вместе 2π): у дуги в цепочке с прямыми полоса за
        // центром обрезается соседями, и ядро такое строит (угол скруглённого контура, торец паза).
        var arcLimits = new List<(Vector3d P, Vector3d D, double Lim)>();
        var selArcs = edgeIds.Distinct().Select(i => p.FindEdge(i)?.Curve).ToList();
        bool fullCircle = selArcs.Count > 0 && selArcs.All(x => x is Parametric3D.Kernel.Curves.ArcCurve3) && selArcs.Cast<Parametric3D.Kernel.Curves.ArcCurve3>() is var arcs
            && arcs.All(a => (a.Frame.Origin - arcs.First().Frame.Origin).Length < 1e-6 && Math.Abs(a.Radius - arcs.First().Radius) < 1e-6)
            && arcs.Sum(a => Math.Abs(a.A1 - a.A0)) >= 2 * Math.PI - 1e-6;
        foreach (var id in edgeIds.Distinct())
        {
            if (p.FindEdge(id) is not { Curve: { } c } e) continue;
            var faces = FacesOf(p, e).Take(2).ToList();
            if (faces.Count < 2) continue;
            double tm = 0.5 * (c.T0 + c.T1);
            var mid = c.PointAt(tm); var tg0 = c.TangentAt(tm);
            if (tg0.LengthSquared < 1e-24) continue;
            tg0.Normalize();
            double s1 = InFaceSign(p, faces[0], mid, tg0), s2 = InFaceSign(p, faces[1], mid, tg0);
            foreach (double t in new[] { c.T0 + 0.02 * (c.T1 - c.T0), tm, c.T1 - 0.02 * (c.T1 - c.T0) })
            {
                var at = c.PointAt(t); var tg = c.TangentAt(t);
                if (tg.LengthSquared < 1e-24) continue;
                tg.Normalize();
                var d1 = InFace(p, faces[0], at, tg, s1); var d2 = InFace(p, faces[1], at, tg, s2);
                // кривая грань — нормаль Zero: точка проверяется только «на теле»
                Vector3d N(TFace f) => Refs.FacePlane(p, f.Id) is { } pl ? pl.N : Vector3d.Zero;
                if (d1 != Vector3d.Zero && d2 != Vector3d.Zero) samples.Add((at, d1, d2, N(faces[0]), N(faces[1])));
                if (!GhostArcOff && fullCircle && c is Parametric3D.Kernel.Curves.ArcCurve3 arc)
                    foreach (var d in new[] { d1, d2 })
                    {
                        if (d == Vector3d.Zero) continue;
                        var w = arc.Frame.W.Normalized(); var dp = d - w * Vector3d.Dot(d, w);   // составляющая в плоскости дуги
                        var toC = arc.Frame.Origin - at;
                        if (Vector3d.Dot(dp, toC) > 0 && dp.Length > 0.1) arcLimits.Add((at, d, arc.Radius / dp.Length));
                    }
            }
        }
        if (samples.Count == 0) return null;
        return v =>
        {
            foreach (var (P, d1, d2, n1, n2) in samples)
            {
                double dist = v;
                if (round)
                {
                    double phi = Math.Acos(Math.Clamp(Vector3d.Dot(d1, d2), -1, 1));
                    if (phi < 1e-3 || phi > Math.PI - 1e-3) return false;
                    dist = v / Math.Tan(phi / 2);
                }
                // плоская грань: точка на ней же (та же нормаль); кривая — на теле с нормалью грани в точке ребра не проверить
                // точно, тогда достаточно «на грани с близкой нормалью» по месту
                bool Ok(Vector3d q, Vector3d n) => n == Vector3d.Zero ? onBody(new[] { q }) : onLike(q, n);
                foreach (var (ap, ad, lim) in arcLimits)
                    if (ap == P && (ad == d1 || ad == d2) && dist >= lim * (1 - 1e-9)) { GhostDebug?.Invoke($"fail at {P}: band {dist} beyond the arc radius limit {lim}"); return false; }
                if (!Ok(P + d1 * dist, n1) || !Ok(P + d2 * dist, n2))
                {
                    GhostDebug?.Invoke($"fail at {P}: d1 {d1} n1 {n1} ok {Ok(P + d1 * dist, n1)}; d2 {d2} n2 {n2} ok {Ok(P + d2 * dist, n2)}; dist {dist}");
                    return false;
                }
            }
            return true;
        };
    }

    /// <summary>Выйдет ли фаска или скругление вершины величиной v: точки на рёбрах вершины — на теле (не дальше длины рёбер).</summary>
    public static Func<double, bool>? VertexGhostValid(Part p, uint vertexId)
    {
        if (p.FindVertex(vertexId) is not { } vx) return null;
        var on = OnBodyTest(p);
        var dirs = new List<Vector3d>();
        foreach (var e in EdgesAt(vx, AllEdges(p)))
            if (DirAt(e, vx) is { } d && e.Curve is { } c)
            {
                double t = (c.PointAt(c.T0) - vx.P).LengthSquared <= (c.PointAt(c.T1) - vx.P).LengthSquared ? c.T0 : c.T1;
                var other = c.PointAt(t == c.T0 ? c.T1 : c.T0);
                dirs.Add(Vector3d.Dot(d, other - vx.P) >= 0 ? d : -d);
            }
        if (dirs.Count < 2) return null;
        return v => on(dirs.Select(d => vx.P + d * v));
    }

    /// <summary>Выйдет ли оболочка толщиной t (внутрь): стенка тоньше половины наименьшего размера тела.</summary>
    public static Func<double, bool> ShellValid(Part p)
    {
        var b = p.Bounds;
        double min = Math.Min(b.Max.X - b.Min.X, Math.Min(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
        return t => t <= 0 || t < 0.5 * min;
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
        foreach (var e in EdgesAt(vx, AllEdges(p)))
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
