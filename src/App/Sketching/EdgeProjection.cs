using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Sketching;

/// <summary>
/// ПРОЕКЦИЯ РЁБЕР ТЕЛА НА ПЛОСКОСТЬ ЭСКИЗА: отрезок — отрезком, окружность и дуга в параллельной плоскости — окружностью
/// и дугой, остальное — ломаной. Точки концов общие (через фабрику), чтобы контур замыкался по общим точкам.
/// </summary>
public static class EdgeProjection
{
    public static Vector2d On(Sketch sk, Vector3d p) => sk.Plane.ToLocal2(p);

    /// <summary>Общие точки по координате (сшивка концов соседних рёбер).</summary>
    public sealed class PointPool
    {
        private readonly List<SkPoint> _pts = new();
        private readonly SkRole _role;
        private readonly string _source;
        public PointPool(SkRole role, string source) { _role = role; _source = source; }
        public SkPoint Get(Vector2d p)
        {
            foreach (var q in _pts) if ((q.P - p).Length < 1e-6) return q;
            var n = new SkPoint(p) { Role = _role, ReferenceSource = _source };
            _pts.Add(n);
            return n;
        }
    }

    public static List<SkEntity> Project(Sketch sk, ICurve3 c, PointPool pool, SkRole role, string source)
    {
        var res = new List<SkEntity>();
        T Mark<T>(T e) where T : SkEntity { e.Role = role; e.ReferenceSource = source; res.Add(e); return e; }
        var n = sk.Plane.W;
        switch (c)
        {
            case LineCurve3 l:
            {
                var a = On(sk, l.A); var b = On(sk, l.B);
                if ((a - b).Length < 1e-7) Mark(pool.Get(a));
                else Mark(new SkLine(pool.Get(a), pool.Get(b)));
                return res;
            }
            case ArcCurve3 arc when Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), n)) - 1) < 1e-9:
            {
                var ctr = On(sk, arc.Frame.Origin);
                if (arc.IsClosed) { Mark(new SkCircle(pool.Get(ctr), arc.Radius)); return res; }
                var s = On(sk, arc.PointAt(arc.T0)); var e = On(sk, arc.PointAt(arc.T1)); var m = On(sk, arc.PointAt(0.5 * (arc.T0 + arc.T1)));
                // центр и направление обхода — по трём точкам дуги
                var sa = SkArc.FromThreePoints(s, m, e);
                if (sa != null) { Mark(new SkArc(pool.Get(sa.Center.P), pool.Get(s), pool.Get(e), sa.CCW)); return res; }
                break;
            }
        }
        // ТОЧНАЯ ОКРУЖНОСТЬ ИЛИ ПРЯМАЯ ПОД ДРУГИМ ИМЕНЕМ (кривая пересечения шара или цилиндра с плоскостью грани,
        // часть кривой после деления ребра): в плоскости эскиза и на одной окружности (прямой) до 1e-7 мм — дуга (отрезок),
        // а не ломаная. Ломаная из хорд давала контур, на 1e-4…1e-3 мм расходящийся с гранью: выдавливание грани ручкой
        // оставляло щели-полоски, общая булева отказывала («a face cycle runs into a used piece»). Мутация P3D_PROJFIT=0.
        if (!FitOff && Recognize(sk, c) is { } rec)
        {
            if (rec.C is not { } ctr) { Mark(new SkLine(pool.Get(rec.A), pool.Get(rec.B))); return res; }
            if (rec.Closed) { Mark(new SkCircle(pool.Get(ctr), rec.R)); return res; }
            Mark(new SkArc(pool.Get(ctr), pool.Get(rec.A), pool.Get(rec.B), rec.Ccw)); return res;
        }
        // общий случай — ломаная
        var pts = new List<Vector3d>();
        c.Tessellate(pts, true);
        var p2 = pts.Select(p => On(sk, p)).ToList();
        for (int i = 1; i < p2.Count; i++)
            if ((p2[i] - p2[i - 1]).Length > 1e-7) Mark(new SkLine(pool.Get(p2[i - 1]), pool.Get(p2[i])));
        return res;
    }

    static readonly bool FitOff = Environment.GetEnvironmentVariable("P3D_PROJFIT") == "0";
    static readonly bool PerpLineOff = Environment.GetEnvironmentVariable("P3D_PROJPERP") == "0";
    /// <summary>Разбор: сколько кривых общего вида узнано дугой или отрезком.</summary>
    public static int Recognized;

    /// <summary>
    /// Кривая в плоскости эскиза — отрезок или дуга (окружность) с точностью 1e-7 мм по 65 точкам; иначе null.
    /// Концы — точные концы кривой (вершины ребра).
    /// </summary>
    public static (Vector2d A, Vector2d B, Vector2d? C, double R, bool Ccw, bool Closed)? Recognize(Sketch sk, ICurve3 c)
    {
        const int N = 64; const double tol = 1e-7;
        var n = sk.Plane.W.Normalized(); var o = sk.Plane.Origin;
        var p3 = new Vector3d[N + 1];
        for (int i = 0; i <= N; i++) p3[i] = c.PointAt(c.T0 + (c.T1 - c.T0) * i / N);
        var p = p3.Select(q => On(sk, q)).ToArray();
        // k96 (п. 13): ПРОЕКЦИЯ — ПРЯМАЯ, хотя кривая и не в плоскости эскиза (ребро на грани, перпендикулярной эскизу:
        // окружность торца цилиндра, кромка плоскости). Прежде такая кривая шла ломаной — с точкой на каждом шаге
        // разбиения («зёрна» вдоль линии). Теперь — отрезок между КРАЙНИМИ точками проекции (у окружности — не концы).
        if (!PerpLineOff)
        {
            int far = 0; for (int i = 1; i <= N; i++) if ((p[i] - p[0]).Length > (p[far] - p[0]).Length) far = i;
            var d0 = p[far] - p[0]; double L0 = d0.Length;
            if (L0 > 1e-6)
            {
                var u = d0 / L0;
                if (p.All(q => Math.Abs((q.X - p[0].X) * u.Y - (q.Y - p[0].Y) * u.X) < tol))
                {
                    double tmin = 0, tmax = 0;
                    for (int i = 0; i <= N; i++) { double t = Vector2d.Dot(p[i] - p[0], u); tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t); }
                    Recognized++;
                    return (p[0] + u * tmin, p[0] + u * tmax, null, 0, true, false);
                }
            }
        }
        // остальное (дуга, окружность) — кривая в плоскости эскиза или в параллельной ей
        double h0 = Vector3d.Dot(p3[0] - o, n);
        if (p3.Any(q => Math.Abs(Vector3d.Dot(q - o, n) - h0) > tol)) return null;
        if (Math.Abs(h0) > tol && FitOff) return null;
        bool closed = (p[0] - p[N]).Length < tol;
        double span = 0; for (int i = 1; i <= N; i++) span += (p[i] - p[i - 1]).Length;
        if (span < 1e-6) return null;
        if (!closed)
        {
            var d = (p[N] - p[0]); double L = d.Length;
            if (L > 1e-9)
            {
                var u = d / L;
                if (p.All(q => Math.Abs((q.X - p[0].X) * u.Y - (q.Y - p[0].Y) * u.X) < tol)) { Recognized++; return (p[0], p[N], null, 0, true, false); }
            }
        }
        var sa = closed ? SkArc.FromThreePoints(p[0], p[N / 3], p[2 * N / 3]) : SkArc.FromThreePoints(p[0], p[N / 2], p[N]);
        if (sa == null) return null;
        var ctr = sa.Center.P; double r = (p[0] - ctr).Length;
        if (r > 1e6 || p.Any(q => Math.Abs((q - ctr).Length - r) > tol)) return null;
        // направление обхода — по знаку поворота (проверка монотонности угла: дуга не обходит окружность дважды)
        double turn = 0;
        for (int i = 1; i <= N; i++)
        {
            double a0 = Math.Atan2(p[i - 1].Y - ctr.Y, p[i - 1].X - ctr.X), a1 = Math.Atan2(p[i].Y - ctr.Y, p[i].X - ctr.X);
            double da = a1 - a0; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
            if (i > 1 && Math.Sign(da) != Math.Sign(turn)) return null;
            turn += da;
        }
        if (Math.Abs(turn) > 2 * Math.PI + 1e-6) return null;
        Recognized++;
        return (p[0], p[N], ctr, r, turn > 0, closed);
    }
}

/// <summary>ССЫЛКИ ЭСКИЗА (как «References» в Creo): рёбра и вершины тела, спроецированные в эскиз как ссылочная геометрия.</summary>
public static class SketchReferences
{
    public static string SourceOf(PickRef r) => r.Kind == PickKind.Vertex ? $"Вершина #{r.Id}" : $"Ребро #{r.Id}";

    /// <summary>Добавить ссылку; null — добавлена, иначе — почему нет.</summary>
    public static string? Add(Sketch sk, Part part, PickRef r)
    {
        string src = SourceOf(r);
        if (sk.Entities.Any(e => e.Role == SkRole.Reference && e.ReferenceSource == src)) return Loc.L("Уже в ссылках");
        var pool = new EdgeProjection.PointPool(SkRole.Reference, src);
        if (r.Kind == PickKind.Vertex && part.FindVertex(r.Id) is { } v)
            sk.AddEntity(pool.Get(EdgeProjection.On(sk, v.P)));
        else if (r.Kind == PickKind.Edge && part.FindEdge(r.Id) is { Curve: { } c })
            foreach (var e in EdgeProjection.Project(sk, c, pool, SkRole.Reference, src)) sk.AddEntity(e);
        else return Loc.L("Ссылка не найдена");
        if (!sk.ReferenceIds.Contains(r.Id)) sk.ReferenceIds.Add(r.Id);
        sk.BumpStructure();
        return null;
    }

    /// <summary>
    /// k96 (п. 14): ПРОЕКЦИЯ ГРАНИ — ссылочные линии: цилиндр с осью вдоль плоскости эскиза — две образующие-очерка (в такой
    /// проекции цилиндр — прямоугольник), с осью поперёк — окружность; плоская грань поперёк эскиза — отрезок. Прочее — пусто.
    /// Отрезки — (A, B, null); окружность — (центр, радиус в A.X, признак).
    /// </summary>
    public static List<(Vector2d A, Vector2d B, double R, bool Circle)> FaceProjection(Sketch sk, TFace f)
    {
        var res = new List<(Vector2d, Vector2d, double, bool)>();
        var n = sk.Plane.W.Normalized();
        var shape = Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
        var bnd = new List<Vector3d>();
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) { try { ce.Tessellate(bnd, true); } catch { } }
        if (bnd.Count == 0) return res;
        switch (shape)
        {
            case Parametric3D.Kernel.Geometry.Ssi.Cylinder cy:
            {
                var ax = cy.Axis.Normalized();
                double along = Math.Abs(Vector3d.Dot(ax, n));
                if (along > 1 - 1e-9)
                {
                    res.Add((EdgeProjection.On(sk, cy.P), Vector2d.Zero, cy.R, true));
                    return res;
                }
                if (along > 1e-9) return res;
                // образующие очерка: ось ± R поперёк оси в плоскости эскиза; длина — по размаху грани вдоль оси
                var a2 = EdgeProjection.On(sk, cy.P + ax) - EdgeProjection.On(sk, cy.P);
                if (a2.Length < 1e-12) return res;
                a2 = a2.Normalized();
                var side = new Vector2d(-a2.Y, a2.X);
                var c0 = EdgeProjection.On(sk, cy.P);
                double tmin = double.MaxValue, tmax = double.MinValue, smin = double.MaxValue, smax = double.MinValue;
                foreach (var q in bnd)
                {
                    var q2 = EdgeProjection.On(sk, q) - c0;
                    double t = Vector2d.Dot(q2, a2), sd = Vector2d.Dot(q2, side);
                    tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t); smin = Math.Min(smin, sd); smax = Math.Max(smax, sd);
                }
                if (tmax - tmin < 1e-9) return res;
                // только очерки, лежащие на грани (часть цилиндра может не доходить до очерка)
                foreach (double s in new[] { -cy.R, cy.R })
                    if (s >= smin - 1e-6 * Math.Max(1, cy.R) && s <= smax + 1e-6 * Math.Max(1, cy.R))
                        res.Add((c0 + a2 * tmin + side * s, c0 + a2 * tmax + side * s, 0, false));
                return res;
            }
            case Parametric3D.Kernel.Geometry.Ssi.Plane pl when Math.Abs(Vector3d.Dot(pl.N.Normalized(), n)) < 1e-9:
            {
                var p2 = bnd.Select(q => EdgeProjection.On(sk, q)).ToList();
                int far = 0; for (int i = 1; i < p2.Count; i++) if ((p2[i] - p2[0]).Length > (p2[far] - p2[0]).Length) far = i;
                var d = p2[far] - p2[0]; if (d.Length < 1e-9) return res;
                var u = d.Normalized();
                double tmin = p2.Min(q => Vector2d.Dot(q - p2[0], u)), tmax = p2.Max(q => Vector2d.Dot(q - p2[0], u));
                res.Add((p2[0] + u * tmin, p2[0] + u * tmax, 0, false));
                return res;
            }
        }
        return res;
    }

    public static string FaceSource(uint id) => $"Грань #{id}";

    /// <summary>Добавить проекцию грани ссылкой; null — добавлена, иначе — почему нет.</summary>
    public static string? AddFace(Sketch sk, Part part, uint faceId)
    {
        string src = FaceSource(faceId);
        if (sk.Entities.Any(e => e.Role == SkRole.Reference && e.ReferenceSource == src)) return Loc.L("Уже в ссылках");
        if (part.FindFace(faceId) is not { } f) return Loc.L("Ссылка не найдена");
        var pr = FaceProjection(sk, f);
        if (pr.Count == 0) return Loc.L("Грань не даёт ссылки в плоскости эскиза");
        var pool = new EdgeProjection.PointPool(SkRole.Reference, src);
        foreach (var (a, b, r, circle) in pr)
        {
            if (circle) sk.AddEntity(new SkCircle(pool.Get(a), r) { Role = SkRole.Reference, ReferenceSource = src });
            else sk.AddEntity(new SkLine(pool.Get(a), pool.Get(b)) { Role = SkRole.Reference, ReferenceSource = src });
        }
        if (!sk.ReferenceIds.Contains(faceId)) sk.ReferenceIds.Add(faceId);
        sk.BumpStructure();
        return null;
    }

    public static IEnumerable<string> Sources(Sketch sk) =>
        sk.Entities.Where(e => e.Role == SkRole.Reference).Select(e => e.ReferenceSource).Where(s => s.Length > 0).Distinct();

    /// <summary>Убрать ссылку и связи, наложенные на неё.</summary>
    public static void Remove(Sketch sk, string source)
    {
        var gone = sk.Entities.Where(e => e.Role == SkRole.Reference && e.ReferenceSource == source).ToList();
        var pts = new HashSet<SkEntity>(gone.SelectMany(e => e.Handles).Concat(gone));
        sk.Constraints.RemoveAll(c => c.Items.Any(pts.Contains));
        sk.Dimensions.RemoveAll(d => d.Items.Any(pts.Contains));
        foreach (var e in gone) sk.Entities.Remove(e);
        if (source.Split('#') is [_, var id] && uint.TryParse(id, out var n)) sk.ReferenceIds.Remove(n);
        sk.BumpStructure();
    }
}
