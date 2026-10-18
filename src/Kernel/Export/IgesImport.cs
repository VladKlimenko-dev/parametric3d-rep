using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// ЧТЕНИЕ ТЕЛА ИЗ IGES. Обрезанные поверхности разбираются обратно в грани с контурами.
///
/// Ссылки между сущностями в IGES — это номера строк раздела описаний, и у разных систем они
/// считаются по-разному. Наши файлы ссылаются по порядку сущностей, и здесь принято то же
/// правило; чужой файл с иной нумерацией будет прочитан неверно, и лучше это знать, чем
/// молча получить перепутанные грани. Поэтому номера проверяются на разумность, а выходящие за
/// пределы называются.
/// </summary>
public static class IgesImport
{
    internal static readonly bool Iges122Old = Environment.GetEnvironmentVariable("P3D_IGES122") == "old";
    internal static readonly bool Iges120Old = Environment.GetEnvironmentVariable("P3D_IGES120") == "old";
    /// <summary>Прочитать тело. Возвращает null, если обрезанных поверхностей в файле нет.</summary>
    public static TSolid? Read(string text, List<string>? warnings = null)
    {
        var parsed = IgesParser.Parse(text);
        if (parsed.LengthUnitNote != null) warnings?.Add(parsed.LengthUnitNote);
        var all = parsed.Entities;

        IgesParser.Entity? At(int number) => parsed.At(number);

        var solid = new TSolid();
        var shell = new TShell { Solid = solid };
        solid.Shells.Add(shell);

        int built = 0, skipped = 0;

        // K10. СШИВКА. Прежде каждая грань получала свои вершины и рёбра (даже соседние стороны
        // одного контура не были связаны): прочитанный брусок давал 24 замечания B-rep, а
        // отверстия терялись (объём бруска с отверстием R8 — 40 180 вместо 43 979). Теперь
        // вершины и рёбра общие по положению (концы и середина кривой), как при чтении STEP.
        var verts = new Dictionary<(long, long, long), TVertex>();
        var edges = new Dictionary<string, TEdge>();
        // Вершина — общая для точек ближе 1e-4 мм: дуга IGES (100) задана центром, началом и
        // концом, и конец, пересчитанный по радиусу начала, отходит от записанного на 1e-5 мм у
        // дуг, подогнанных к точкам (кромка сферического выреза). Поиск по соседним ячейкам.
        var vgrid = new Dictionary<(long, long, long), List<TVertex>>();
        const double vtol = 1e-4;
        TVertex V(Vector3d p)
        {
            long cx = (long)Math.Floor(p.X / vtol), cy = (long)Math.Floor(p.Y / vtol), cz = (long)Math.Floor(p.Z / vtol);
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                if (vgrid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                    foreach (var q in l) if ((q.P - p).Length <= vtol) return q;
            var nv = new TVertex(p);
            if (!vgrid.TryGetValue((cx, cy, cz), out var own)) vgrid[(cx, cy, cz)] = own = new List<TVertex>();
            own.Add(nv);
            return nv;
        }
        void Sew(TFace face, List<ICurve3> curves, bool outer)
        {
            var loop = face.AddLoop(outer);
            // Кривые контура в файле не обязательно идут «конец к началу» (каждая — в своём
            // направлении): обход восстанавливается по стыкам концов, иначе площадь контура
            // выходила вдвое меньше и разметка грани отвергалась.
            double tol = Core.Tol.Weld * 10;
            var dirs = new bool[curves.Count];   // true — кривая проходится от T1 к T0
            if (curves.Count > 1)
            {
                Vector3d E(int i) => dirs[i] ? curves[i].PointAt(curves[i].T0) : curves[i].PointAt(curves[i].T1);
                var n0 = curves[1];
                var e0 = curves[0].PointAt(curves[0].T1);
                bool touches(Vector3d q) => (n0.PointAt(n0.T0) - q).Length < tol || (n0.PointAt(n0.T1) - q).Length < tol;
                dirs[0] = !touches(e0);
                for (int i = 1; i < curves.Count; i++)
                    dirs[i] = (curves[i].PointAt(curves[i].T0) - E(i - 1)).Length > tol && (curves[i].PointAt(curves[i].T1) - E(i - 1)).Length <= tol;
            }
            for (int ci = 0; ci < curves.Count; ci++)
            {
                var c = curves[ci];
                bool flip = dirs[ci];
                Vector3d a = flip ? c.PointAt(c.T1) : c.PointAt(c.T0), b = flip ? c.PointAt(c.T0) : c.PointAt(c.T1), mid = c.PointAt((c.T0 + c.T1) / 2);
                var va = V(a); var vb = V(b);
                var km = ((long)Math.Round(mid.X / vtol), (long)Math.Round(mid.Y / vtol), (long)Math.Round(mid.Z / vtol));
                string key = (va.GetHashCode() < vb.GetHashCode() ? $"{va.GetHashCode()}|{vb.GetHashCode()}" : $"{vb.GetHashCode()}|{va.GetHashCode()}") + "|" + km;
                // Вершины ребра — по началу и концу САМОЙ кривой (обход задаётся флагом rev).
                if (!edges.TryGetValue(key, out var edge))
                {
                    edges[key] = edge = new TEdge { V0 = V(c.PointAt(c.T0)), V1 = V(c.PointAt(c.T1)), Curve = c };
                    edge.Tolerance = Math.Max((c.PointAt(c.T0) - edge.V0.P).Length, (c.PointAt(c.T1) - edge.V1.P).Length);
                }
                bool rev = (a - b).Length <= Core.Tol.Weld
                    ? Vector3d.Dot(c.TangentAt(c.T0) * (flip ? -1 : 1), edge.Curve.TangentAt(edge.Curve.T0)) < 0
                    : !ReferenceEquals(edge.V0, va);
                loop.Add(edge, rev);
            }
        }

        foreach (var e in all)
        {
            var tr = IgesParser.Trimmed(e);
            if (tr == null) continue;

            var surfEntity = At(tr.Value.Surface);
            if (surfEntity == null)
            {
                warnings?.Add($"IGES: surface {tr.Value.Surface} is out of range.");
                skipped++;
                continue;
            }

            var surface = Surface(surfEntity.Value, At);
            if (surface == null) { skipped++; continue; }

            // Имя грани читается из файла: подпись пережила обмен и должна вернуться на место.
            var face = new TFace { Surface = surface, Tag = string.IsNullOrEmpty(e.Label) ? "iges" : e.Label };

            var outer = Loop(At(tr.Value.Outer), At);
            if (outer.Count > 0) Sew(face, outer, true);

            foreach (int h in tr.Value.Holes)
            {
                var hole = Loop(At(h), At);
                if (hole.Count > 0) Sew(face, hole, false);
            }

            // Отверстия плоской грани обходятся против внешнего контура: у замкнутой окружности
            // направление в файле не задано обходом (одна кривая), и обход по стыкам его не даёт.
            if (surface.IsPlanar && face.Loops.Count > 1)
            {
                Vector3d Newell(TLoop l)
                {
                    var pts = new List<Vector3d>();
                    foreach (var ce in l.CoEdges) ce.Tessellate(pts, pts.Count == 0);
                    var nw = Vector3d.Zero;
                    for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                    return nw;
                }
                var outerN = Newell(face.Loops[0]);
                foreach (var l in face.Loops.Skip(1))
                    if (Vector3d.Dot(Newell(l), outerN) > 0)
                    {
                        l.CoEdges.Reverse();
                        foreach (var ce in l.CoEdges) ce.Reversed = !ce.Reversed;
                    }
            }
            shell.Add(face);
            built++;
        }

        if (skipped > 0) warnings?.Add($"IGES: {skipped} face(s) skipped while reading.");
        warnings?.Add($"IGES: {built} face(s) read.");

        if (built == 0) return null;

        // K10. ЧАША ШАРА. В IGES шар — вращение полуокружности от полюса до полюса, а грань —
        // чаша, ограниченная кромкой. Какой полюс внутри грани, файл не говорит; берётся тот,
        // что лежит внутри габарита остальных граней тела (вторая половина шара торчала бы
        // наружу). Образующая заменяется точной дугой от этого полюса до широты кромки.
        var others = solid.Faces.SelectMany(f => f.Edges).SelectMany(e => new[] { e.V0.P, e.V1.P }).ToList();
        foreach (var f in solid.Faces)
        {
            if (f.Surface is not RevolvedSurface rv || f.Loops.Count == 0) continue;
            var w = rv.Axis.W.Normalized();
            Vector3d p0 = rv.Base.PointAt(rv.Base.T0), p1 = rv.Base.PointAt(rv.Base.T1);
            double Off(Vector3d q) { var d = q - rv.Axis.Origin; return (d - w * Vector3d.Dot(d, w)).Length; }
            if (Off(p0) > 1e-6 || Off(p1) > 1e-6) continue;
            var centre = (p0 + p1) / 2; double R = (p1 - p0).Length / 2;
            if (R < 1e-9) continue;
            var rest = f.Loops.SelectMany(l => l.CoEdges).SelectMany(ce => new[] { ce.Edge.V0.P, ce.Edge.V1.P }).ToList();
            var bmin = new Vector3d(double.MaxValue); var bmax = new Vector3d(double.MinValue);
            foreach (var q in solid.Faces.Where(x => x != f).SelectMany(x => x.Edges).SelectMany(e => new[] { e.V0.P, e.V1.P }))
            { bmin = Vector3d.ComponentMin(bmin, q); bmax = Vector3d.ComponentMax(bmax, q); }
            bool Inside(Vector3d q) => q.X >= bmin.X - 1e-6 && q.Y >= bmin.Y - 1e-6 && q.Z >= bmin.Z - 1e-6 && q.X <= bmax.X + 1e-6 && q.Y <= bmax.Y + 1e-6 && q.Z <= bmax.Z + 1e-6;
            Vector3d? pole = Inside(p0) && !Inside(p1) ? p0 : Inside(p1) && !Inside(p0) ? p1 : null;
            if (pole == null) continue;
            var pd = (pole.Value - centre).Normalized();
            double rim = rest.Max(q => Math.Acos(Math.Clamp(Vector3d.Dot((q - centre).Normalized(), pd), -1, 1)));
            var mid = rv.Base.PointAt((rv.Base.T0 + rv.Base.T1) / 2) - centre;
            var u = (mid - pd * Vector3d.Dot(mid, pd)); if (u.LengthSquared < 1e-18) continue; u = u.Normalized();
            var gen = new ArcCurve3(new Frame3(centre, pd, u), R, 0, rim);
            f.Surface = new RevolvedSurface(gen, Frame3.FromNormal(centre, w), 0, 2 * Math.PI);
        }

        // Разметка по контурам с общими точками рёбер и согласование стороны граней по телу.
        StepImport.MeshFacesFromLoops(solid, warnings, "IGES");
        StepImport.OrientShellPublic(solid);
        return solid;
    }

    private static void AddLoop(TFace face, List<ICurve3> curves, bool outer)
    {
        var loop = face.AddLoop(outer);

        foreach (var c in curves)
        {
            var v0 = new TVertex(c.PointAt(c.T0));
            var v1 = new TVertex(c.PointAt(c.T1));
            loop.Add(new TEdge { V0 = v0, V1 = v1, Curve = c }, false);
        }
    }

    /// <summary>Поверхность по её сущности.</summary>
    private static ISurface3? Surface(IgesParser.Entity e, Func<int, IgesParser.Entity?> at)
    {
        if (IgesParser.Plane(e) is { } pl)
        {
            Vector3d n = pl.Normal.LengthSquared < 1e-18 ? Vector3d.UnitZ : pl.Normal.Normalized();
            return new PlaneSurface(Frame3.FromNormal(n * pl.D, n));
        }

        if (IgesParser.Extruded(e) is { } ex)
        {
            var baseEntity = at(ex.BaseCurve);
            if (baseEntity == null) return null;

            var c = Curve(baseEntity.Value, at);
            // m.x2: по описанию IGES у 122 — КОНЕЦ образующей (точка), а не вектор: перенос = конец − начало направляющей.
            // Прежде вектор — так и писали, и OpenCascade читал наш цилиндр косым (площадь ≈ 0). P3D_IGES122=old — прежнее.
            return c == null ? null : new ExtrudedSurface(c, Iges122Old ? ex.Dir : ex.Dir - c.PointAt(c.T0));
        }

        if (IgesParser.SplineSurface(e) is { } ns) return ns;

        // K10: поверхность вращения (120): ось — отрезок, образующая — кривая, углы — от её
        // исходного положения.
        if (IgesParser.Revolved(e) is { } rv)
        {
            var axisEntity = at(rv.Axis); var genEntity = at(rv.Generatrix);
            if (axisEntity == null || genEntity == null) return null;
            if (Curve(axisEntity.Value, at) is not LineCurve3 axisLine) return null;
            var gen = Curve(genEntity.Value, at);
            if (gen == null) return null;
            // m.x2: положительный угол 120 — против часовой, если смотреть с начала оси к концу (так пишет и читает OpenCascade:
            // ось цилиндра вокруг +Z — от (0, 0, 1) к (0, 0, 0)); прежде — наоборот. P3D_IGES120=old — прежнее.
            var dir = Iges120Old ? (axisLine.B - axisLine.A) : (axisLine.A - axisLine.B);
            if (dir.LengthSquared < 1e-18) return null;
            return new RevolvedSurface(gen, Frame3.FromNormal(axisLine.A, dir.Normalized()), rv.A0, rv.A1);
        }

        return null;
    }

    /// <summary>Контур: одна кривая или составной набор.</summary>
    private static List<ICurve3> Loop(IgesParser.Entity? e, Func<int, IgesParser.Entity?> at)
    {
        var res = new List<ICurve3>();
        if (e == null) return res;
        // Кривая на поверхности (142): берётся её кривая в пространстве модели.
        if (e.Value.Type == IgesFile.TypeCurveOnSurface && e.Value.Data.Count >= 4)
            return Loop(at((int)e.Value.Data[3]), at);

        if (IgesParser.Composite(e.Value) is { } parts)
        {
            foreach (int p in parts)
            {
                var sub = at(p);
                if (sub == null) continue;
                var c = Curve(sub.Value, at);
                if (c != null) res.Add(c);
            }
            return res;
        }

        var one = Curve(e.Value, at);
        if (one != null) res.Add(one);
        return res;
    }

    private static ICurve3? Curve(IgesParser.Entity e, Func<int, IgesParser.Entity?> at)
    {
        ICurve3? c = null;
        if (IgesParser.Line(e) is { } ln) c = new LineCurve3(ln.A, ln.B);
        else if (IgesParser.Arc(e) is { } arc) c = arc;
        else if (IgesParser.Spline(e) is { } sp) c = sp;
        if (c == null || e.Matrix == 0) return c;
        // K10: матрица преобразования (124): дуга в наклонной плоскости задаётся в своей
        // плоскости XY и матрицей — так пишут почти все системы.
        var m = at(e.Matrix);
        if (m == null || m.Value.Type != 124 || m.Value.Data.Count < 12) return c;
        var d = m.Value.Data;
        Vector3d P(Vector3d q) => new(d[0] * q.X + d[1] * q.Y + d[2] * q.Z + d[3], d[4] * q.X + d[5] * q.Y + d[6] * q.Z + d[7], d[8] * q.X + d[9] * q.Y + d[10] * q.Z + d[11]);
        Vector3d R(Vector3d q) => P(q) - P(Vector3d.Zero);
        return c switch
        {
            LineCurve3 l => new LineCurve3(P(l.A), P(l.B)),
            ArcCurve3 a => new ArcCurve3(new Frame3(P(a.Frame.Origin), R(a.Frame.U), R(a.Frame.V)), a.Radius, a.A0, a.A1),
            SplineCurve3 s2 => new SplineCurve3(s2.ControlPoints.Select(P).ToList(), s2.Degree, s2.Weights.ToList(), s2.Knots.ToList()),
            _ => c
        };
    }
}
