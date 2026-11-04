using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Drawing;

/// <summary>Направление взгляда вида и оси листа (ортогональная проекция).</summary>
public readonly record struct ViewFrame(Vector3d Right, Vector3d Up)
{
    /// <summary>К наблюдателю (Right × Up).</summary>
    public Vector3d Toward => Vector3d.Cross(Right, Up).Normalized();
    public Vector2d Project(Vector3d p) => new(Vector3d.Dot(p, Right), Vector3d.Dot(p, Up));
    public double Depth(Vector3d p) => Vector3d.Dot(p, Toward);

    /// <summary>
    /// Основные виды ГОСТ 2.305 (метод первого угла; модель: Z вверх, вид спереди — со стороны −Y): спереди (главный), сверху,
    /// слева, справа, снизу, сзади; изометрия ГОСТ 2.317.
    /// </summary>
    public static ViewFrame Of(string name) => name switch
    {
        "front" => new(Vector3d.UnitX, Vector3d.UnitZ),
        "top" => new(Vector3d.UnitX, Vector3d.UnitY),
        "left" => new(-Vector3d.UnitY, Vector3d.UnitZ),     // вид слева: смотрим с −X… взгляд вдоль +X
        "right" => new(Vector3d.UnitY, Vector3d.UnitZ),
        "bottom" => new(Vector3d.UnitX, -Vector3d.UnitY),
        "back" => new(-Vector3d.UnitX, Vector3d.UnitZ),
        "iso" => Iso(),
        _ => throw new ArgumentException("unknown view " + name),
    };

    static ViewFrame Iso()
    {
        var toward = new Vector3d(1, -1, 1).Normalized();
        var right = Vector3d.Cross(Vector3d.UnitZ, toward).Normalized();
        var up = Vector3d.Cross(toward, right).Normalized();
        return new(right, up);
    }
}

/// <summary>Линия вида: ломаная на плоскости вида (мм модели), видимость, вид линии и ребро-источник.</summary>
public sealed record ViewLine(List<Vector2d> Points, bool Visible, ViewLineKind Kind, uint SourceId);
public enum ViewLineKind { Edge, Silhouette, Tangent }
/// <summary>Ось тела вращения на виде: Cross — ось вдоль взгляда (перекрестие центра окружности радиуса Radius), иначе — отрезок
/// оси от Lo до Hi вдоль Dir через Center.</summary>
public sealed record ViewAxis(Vector2d Center, Vector2d Dir, double Lo, double Hi, bool Cross, double Radius);

/// <summary>
/// k155 (D2, начало): ПРОЕКЦИЯ ТЕЛА НА ВИД СО СКРЫТИЕМ ЛИНИЙ. Рёбра тела (точные кривые) и очерки кривых граней (линии, где
/// нормаль грани перпендикулярна взгляду) проецируются ортогонально; видимость точки — нет ли перед ней (ближе к наблюдателю)
/// треугольника сетки тела, накрывающего её проекцию (сетка — ускоритель: ячейки 2D). Граница видимых и невидимых участков
/// уточняется делением пополам. Рёбра гладкого стыка граней — линии перехода (Tangent; на чертеже ГОСТ — не показываются).
/// </summary>
public static class ViewProjection
{
    static readonly bool FullTurnOff = Environment.GetEnvironmentVariable("P3D_AXISFULLTURN") == "0";
    static readonly bool DedupOff = Environment.GetEnvironmentVariable("P3D_HLRDEDUP") == "0";
    /// <summary>Разбор: точек, проверенных на видимость.</summary>
    public static long VisibilityTests;

    sealed class Tri { public Vector3d A, B, C; public uint Face; }

    sealed class Scene
    {
        public readonly List<Tri> Tris = new();
        public double MinX, MinY, Cell; public int Nx, Ny;
        public List<int>[] Grid = Array.Empty<List<int>>();
        public double Eps;

        public void Index()
        {
            double maxX = double.MinValue, maxY = double.MinValue; MinX = MinY = double.MaxValue;
            foreach (var t in Tris) foreach (var p in new[] { t.A, t.B, t.C }) { MinX = Math.Min(MinX, p.X); MinY = Math.Min(MinY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            double size = Math.Max(maxX - MinX, maxY - MinY);
            if (!(size > 0)) size = 1;
            Nx = Ny = Math.Clamp((int)Math.Sqrt(Tris.Count / 2.0), 1, 256);
            Cell = size / Nx * 1.000001;
            Nx = Math.Max(1, (int)Math.Ceiling((maxX - MinX) / Cell) + 1); Ny = Math.Max(1, (int)Math.Ceiling((maxY - MinY) / Cell) + 1);
            Grid = new List<int>[Nx * Ny];
            for (int i = 0; i < Tris.Count; i++)
            {
                var t = Tris[i];
                int x0 = CellX(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X))), x1 = CellX(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)));
                int y0 = CellY(Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y))), y1 = CellY(Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) (Grid[y * Nx + x] ??= new()).Add(i);
            }
            Eps = Math.Max(1e-6, size * 2e-4);
        }
        int CellX(double x) => Math.Clamp((int)((x - MinX) / Cell), 0, Nx - 1);
        int CellY(double y) => Math.Clamp((int)((y - MinY) / Cell), 0, Ny - 1);

        /// <summary>Точка (координаты вида, Z — к наблюдателю) закрыта треугольником, кроме граней skip.</summary>
        public bool Hidden(Vector3d p, uint skipA, uint skipB)
        {
            Interlocked.Increment(ref VisibilityTests);
            var cell = Grid[CellY(p.Y) * Nx + CellX(p.X)];
            if (cell == null) return false;
            foreach (int i in cell)
            {
                var t = Tris[i];
                if (t.Face == skipA || t.Face == skipB) continue;
                if (!Inside(t, p.X, p.Y, out double z)) continue;
                if (z > p.Z + Eps) return true;
            }
            return false;
        }

        static bool Inside(Tri t, double x, double y, out double z)
        {
            z = 0;
            double d = (t.B.Y - t.C.Y) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Y - t.C.Y);
            if (Math.Abs(d) < 1e-18) return false;
            double l1 = ((t.B.Y - t.C.Y) * (x - t.C.X) + (t.C.X - t.B.X) * (y - t.C.Y)) / d;
            double l2 = ((t.C.Y - t.A.Y) * (x - t.C.X) + (t.A.X - t.C.X) * (y - t.C.Y)) / d;
            double l3 = 1 - l1 - l2;
            const double e = 1e-9;
            if (l1 < -e || l2 < -e || l3 < -e) return false;
            z = l1 * t.A.Z + l2 * t.B.Z + l3 * t.C.Z;
            return true;
        }
    }

    static Vector3d ToView(ViewFrame v, Vector3d p) => new(Vector3d.Dot(p, v.Right), Vector3d.Dot(p, v.Up), v.Depth(p));

    /// <summary>Линии вида тела. chord — наибольший отход ломаной от кривой (мм модели).</summary>
    public static List<ViewLine> Project(TSolid body, ViewFrame view, double chord = 0.02, bool silhouettes = true)
    {
        var mesh = Tessellator.Tessellate(body);
        var sc = new Scene();
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            var a = mesh.Vertices[mesh.Indices[t]]; var b = mesh.Vertices[mesh.Indices[t + 1]]; var c = mesh.Vertices[mesh.Indices[t + 2]];
            sc.Tris.Add(new Tri { A = ToView(view, (Vector3d)a.P), B = ToView(view, (Vector3d)b.P), C = ToView(view, (Vector3d)c.P), Face = a.FaceId });
        }
        sc.Index();
        var res = new List<ViewLine>();
        foreach (var e in body.Edges)
        {
            if (e.Curve == null) continue;
            var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            uint fa = faces.Count > 0 ? faces[0].Id : uint.MaxValue, fb = faces.Count > 1 ? faces[1].Id : uint.MaxValue;
            var kind = faces.Count == 2 && Smooth(e, faces[0], faces[1]) ? ViewLineKind.Tangent : ViewLineKind.Edge;
            var pts = Sample(e, chord);
            Split(sc, view, pts, fa, fb, kind, e.Id, res);
        }
        if (silhouettes) Silhouettes(mesh, view, sc, res);
        return DropCovered(res, sc.Eps * 5);
    }

    /// <summary>
    /// Оси граней вращения (цилиндр, поверхность вращения): грани с общей осью (та же прямая с точностью 1e-6 габарита)
    /// объединяются; ось вдоль взгляда — перекрестие центра, поперёк взгляда — отрезок по длине граней; наклонные оси не
    /// рисуются (на них — вспомогательные виды, D5).
    /// </summary>
    public static List<ViewAxis> Axes(TSolid body, ViewFrame view)
    {
        var w = view.Toward;
        var groups = new List<(Vector3d O, Vector3d W, List<Vector3d> Pts)>();
        foreach (var f in body.Faces)
        {
            var srf = GeometryAudit.AuthoritativeSurface(f);
            (Vector3d Origin, Vector3d W)? ax = srf switch
            {
                Kernel.Surfaces.CylinderSurface cs => (cs.Axis.Origin, cs.Axis.W),
                Kernel.Surfaces.RevolvedSurface rs => (rs.Axis.Origin, rs.Axis.W),
                // выдавленная дуга, направление — вдоль оси дуги: цилиндр
                Kernel.Surfaces.ExtrudedSurface es when es.Base is Kernel.Curves.ArcCurve3 arc && Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), es.Dir.Normalized())) - 1) < 1e-9 => (arc.Frame.Origin, es.Dir),
                _ => null,
            };
            if (ax is not { } a) continue;
            var pts = new List<Vector3d>();
            foreach (var e in f.Edges) { if (e.Curve == null) continue; for (int k = 0; k <= 72; k++) pts.Add(e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 72)); }
            if (pts.Count == 0) continue;
            var W = a.W.Normalized();
            int gi = groups.FindIndex(g => Math.Abs(Math.Abs(Vector3d.Dot(g.W, W)) - 1) < 1e-9 && Vector3d.Cross(a.Origin - g.O, g.W).Length < 1e-6 * Math.Max(1, (a.Origin - g.O).Length));
            if (gi < 0) groups.Add((a.Origin, W, pts)); else groups[gi].Pts.AddRange(pts);
        }
        var res = new List<ViewAxis>();
        foreach (var (o, W, pts) in groups)
        {
            double along = Math.Abs(Vector3d.Dot(W, w));
            double r = pts.Max(p => ((p - o) - W * Vector3d.Dot(p - o, W)).Length);
            if (r < 1e-9) continue;
            // ось — только у полного оборота (отверстие, вал, бобышка); у скругления угла (четверть оборота) оси нет
            if (!FullTurnOff)
            {
                var u = Math.Abs(W.X) < 0.9 ? Vector3d.Cross(W, Vector3d.UnitX).Normalized() : Vector3d.Cross(W, Vector3d.UnitY).Normalized();
                var vv = Vector3d.Cross(W, u);
                var bins = new bool[36];
                foreach (var p in pts)
                {
                    var q = p - o - W * Vector3d.Dot(p - o, W);
                    if (q.Length < 1e-9) continue;
                    double ang = Math.Atan2(Vector3d.Dot(q, vv), Vector3d.Dot(q, u));
                    bins[Math.Clamp((int)((ang + Math.PI) / (2 * Math.PI) * 36), 0, 35)] = true;
                }
                if (bins.Count(b => b) < 30) continue;
            }
            if (along > 1 - 1e-9) { res.Add(new ViewAxis(view.Project(o), Vector2d.UnitX, -r, r, true, r)); continue; }
            if (along > 1e-9) continue;
            var d2 = view.Project(o + W) - view.Project(o);
            if (d2.Length < 1e-12) continue;
            d2.Normalize();
            var c = view.Project(o);
            var t = pts.Select(p => Vector2d.Dot(view.Project(p) - c, d2)).ToList();
            res.Add(new ViewAxis(c, d2, t.Min(), t.Max(), false, r));
        }
        return res;
    }

    /// <summary>Ребро гладкого стыка: нормали граней у трёх точек ребра совпадают (угол меньше ~1°).</summary>
    static bool Smooth(TEdge e, TFace a, TFace b)
    {
        for (int k = 1; k <= 3; k++)
        {
            var p = e.Curve!.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 4.0);
            Vector3d na, nb;
            try { na = a.NormalAt(p); nb = b.NormalAt(p); } catch { return false; }
            if (na.LengthSquared < 1e-24 || nb.LengthSquared < 1e-24) return false;
            if (Vector3d.Dot(na.Normalized(), nb.Normalized()) < 0.99985) return false;
        }
        return true;
    }

    /// <summary>Точки ребра: прямая — концы; кривая — деление пополам, пока отход середины хорды больше chord.</summary>
    static List<Vector3d> Sample(TEdge e, double chord)
    {
        var c = e.Curve!;
        var pts = new List<Vector3d> { c.PointAt(c.T0) };
        void Rec(double t0, Vector3d p0, double t1, Vector3d p1, int depth)
        {
            double tm = (t0 + t1) / 2; var pm = c.PointAt(tm);
            var mid = (p0 + p1) / 2;
            if (depth < 12 && ((pm - mid).Length > chord || depth < 2 && c is not Kernel.Curves.LineCurve3))
            { Rec(t0, p0, tm, pm, depth + 1); Rec(tm, pm, t1, p1, depth + 1); }
            else pts.Add(p1);
        }
        Rec(c.T0, pts[0], c.T1, c.PointAt(c.T1), 0);
        return pts;
    }

    /// <summary>Ломаная → видимые и невидимые участки: пробы через шаг не больше 1/64 габарита, граница — делением пополам.</summary>
    static void Split(Scene sc, ViewFrame view, List<Vector3d> pts3, uint fa, uint fb, ViewLineKind kind, uint id, List<ViewLine> res)
    {
        var pv = pts3.Select(p => ToView(view, p)).ToList();
        double step = Math.Max(sc.Cell / 2, 1e-3);
        var dense = new List<Vector3d> { pv[0] };
        for (int i = 1; i < pv.Count; i++)
        {
            double len = new Vector2d(pv[i].X - pv[i - 1].X, pv[i].Y - pv[i - 1].Y).Length;
            int n = Math.Clamp((int)Math.Ceiling(len / step), 1, 512);
            for (int k = 1; k <= n; k++) dense.Add(pv[i - 1] + (pv[i] - pv[i - 1]) * (k / (double)n));
        }
        bool Hid(Vector3d p) => sc.Hidden(p, fa, fb);
        var cur = new List<Vector2d> { dense[0].Xy };
        // видимость участка — по его середине (у концов ребро касается соседних граней)
        bool vis = !Hid((dense[0] + dense[Math.Min(1, dense.Count - 1)]) / 2);
        for (int i = 1; i < dense.Count; i++)
        {
            bool v = !Hid((dense[i - 1] + dense[i]) / 2);
            if (v != vis)
            {
                // граница внутри [i−1, i]: деление пополам по видимости
                Vector3d a = dense[i - 1], b = dense[i];
                for (int it = 0; it < 20; it++) { var m = (a + b) / 2; if (!Hid(m) == vis) a = m; else b = m; }
                var cut = ((a + b) / 2).Xy;
                cur.Add(cut);
                if (Length(cur) > 1e-6) res.Add(new ViewLine(cur, vis, kind, id));
                cur = new List<Vector2d> { cut };
                vis = v;
            }
            cur.Add(dense[i].Xy);
        }
        if (Length(cur) > 1e-6) res.Add(new ViewLine(Simplify(cur), vis, kind, id));
    }

    /// <summary>
    /// Невидимая линия, лежащая на видимой (заднее ребро за передним, нижняя окружность под верхней), на чертеже не рисуется;
    /// линии перехода и очерки, совпадающие с рёбрами, — тоже. Покрытие — все пробы линии ближе eps к видимому ребру.
    /// </summary>
    static List<ViewLine> DropCovered(List<ViewLine> lines, double eps)
    {
        // видимые рёбра — от длинных к коротким; ребро, целиком лежащее на уже принятом видимом (нижняя кромка сквозного
        // отверстия под верхней), — повтор
        var vis = new List<ViewLine>();
        bool Covered(ViewLine l)
        {
            for (int i = 0; i + 1 < l.Points.Count; i++)
                for (int k = 0; k <= 4; k++)
                {
                    var q = l.Points[i] + (l.Points[i + 1] - l.Points[i]) * (k / 4.0);
                    if (!vis.Any(v => DistTo(v.Points, q) < eps)) return false;
                }
            return true;
        }
        foreach (var l in lines.Where(l => l.Visible && l.Kind == ViewLineKind.Edge).OrderByDescending(l => Length(l.Points)))
            if (DedupOff || !Covered(l)) vis.Add(l);
        var res = new List<ViewLine>(vis);
        foreach (var l in lines)
        {
            if (l.Visible && l.Kind == ViewLineKind.Edge) continue;
            if (!Covered(l)) res.Add(l);
        }
        return res;
    }

    static double DistTo(List<Vector2d> pl, Vector2d q)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < pl.Count; i++)
        {
            var a = pl[i]; var ab = pl[i + 1] - a;
            double t = ab.LengthSquared < 1e-24 ? 0 : Math.Clamp(Vector2d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
            best = Math.Min(best, (a + ab * t - q).Length);
        }
        if (pl.Count == 1) best = (pl[0] - q).Length;
        return best;
    }

    static double Length(List<Vector2d> p) { double s = 0; for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length; return s; }

    /// <summary>Убрать промежуточные точки на прямых участках.</summary>
    static List<Vector2d> Simplify(List<Vector2d> p)
    {
        if (p.Count < 3) return p;
        var r = new List<Vector2d> { p[0] };
        for (int i = 1; i < p.Count - 1; i++)
        {
            var a = r[^1]; var b = p[i]; var c = p[i + 1];
            var ab = b - a; var ac = c - a;
            double cross = Math.Abs(ab.X * ac.Y - ab.Y * ac.X);
            if (cross > 1e-9 * Math.Max(1, ac.LengthSquared) || Vector2d.Dot(ab, ac) < 0) r.Add(b);
        }
        r.Add(p[^1]);
        return r;
    }

    /// <summary>
    /// Очерки кривых граней — линия нуля функции g = n·w (n — нормаль поверхности в вершинах сетки, она точная, w — к
    /// наблюдателю): в каждом треугольнике грани, где g меняет знак, — отрезок между точками нуля на его сторонах (линейная
    /// интерполяция). У цилиндра и конуса, чья образующая — сторона сетки, очерк точный. Отрезки собираются в цепочки по
    /// общим концам; плоские грани (нормаль постоянна) очерка не дают.
    /// </summary>
    static void Silhouettes(MeshData mesh, ViewFrame view, Scene sc, List<ViewLine> res)
    {
        var w = view.Toward;
        var byFace = new Dictionary<uint, List<(Vector3d A, Vector3d B)>>();
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            var v0 = mesh.Vertices[mesh.Indices[t]]; var v1 = mesh.Vertices[mesh.Indices[t + 1]]; var v2 = mesh.Vertices[mesh.Indices[t + 2]];
            var n0 = (Vector3d)v0.N; var n1 = (Vector3d)v1.N; var n2 = (Vector3d)v2.N;
            if ((n0 - n1).LengthSquared < 1e-12 && (n0 - n2).LengthSquared < 1e-12) continue;   // плоский участок
            // участок, видимый «с ребра» (нормаль поперёк взгляда во всех вершинах: стенка отверстия вдоль взгляда), — его
            // проекция вырождена в линию, совпадающую с рёбрами; очерка нет
            if (Math.Max(Math.Abs(Vector3d.Dot(n0, w)), Math.Max(Math.Abs(Vector3d.Dot(n1, w)), Math.Abs(Vector3d.Dot(n2, w)))) < 1e-6) continue;
            double g0 = G(n0), g1 = G(n1), g2 = G(n2);
            double G(Vector3d n) { double g = Vector3d.Dot(n, w); return Math.Abs(g) < 1e-12 ? 1e-12 : g; }
            var P = new[] { (Vector3d)v0.P, (Vector3d)v1.P, (Vector3d)v2.P }; var g = new[] { g0, g1, g2 };
            var cross = new List<Vector3d>(2);
            for (int k = 0; k < 3; k++)
            {
                int m = (k + 1) % 3;
                if (g[k] * g[m] < 0) { double u = g[k] / (g[k] - g[m]); cross.Add(P[k] + (P[m] - P[k]) * u); }
            }
            if (cross.Count != 2 || (cross[0] - cross[1]).LengthSquared < 1e-18) continue;
            (byFace.TryGetValue(v0.FaceId, out var l) ? l : byFace[v0.FaceId] = new()).Add((cross[0], cross[1]));
        }
        static (long, long, long) Key(Vector3d p) => ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));
        foreach (var (face, segs) in byFace)
        {
            var at = new Dictionary<(long, long, long), List<int>>();
            for (int i = 0; i < segs.Count; i++)
                foreach (var q in new[] { segs[i].A, segs[i].B }) { var k = Key(q); (at.TryGetValue(k, out var l) ? l : at[k] = new()).Add(i); }
            var used = new bool[segs.Count];
            for (int s0 = 0; s0 < segs.Count; s0++)
            {
                if (used[s0]) continue;
                used[s0] = true;
                var chain = new LinkedList<Vector3d>(); chain.AddLast(segs[s0].A); chain.AddLast(segs[s0].B);
                foreach (bool fwd in new[] { true, false })
                    while (true)
                    {
                        var end = fwd ? chain.Last!.Value : chain.First!.Value;
                        int next = -1;
                        if (at.TryGetValue(Key(end), out var cand)) foreach (int c in cand) if (!used[c]) { next = c; break; }
                        if (next < 0) break;
                        used[next] = true;
                        var other = Key(segs[next].A) == Key(end) ? segs[next].B : segs[next].A;
                        if (fwd) chain.AddLast(other); else chain.AddFirst(other);
                    }
                Split(sc, view, chain.ToList(), face, uint.MaxValue, ViewLineKind.Silhouette, face, res);
            }
        }
    }
}
