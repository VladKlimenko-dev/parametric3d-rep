using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Geometry;

/// <summary>
/// G4. РЁБРА — ТОЧНЫМИ ПЕРЕСЕЧЕНИЯМИ ПОВЕРХНОСТЕЙ ИХ ГРАНЕЙ.
///
/// Кривая ребра, восстановленная по точному многограннику (подгонка окружности по точкам),
/// лежит на своих гранях с точностью его сетки — единицы микрон (G3). Здесь для ребра между
/// двумя гранями с поверхностями «плоскость / цилиндр / шар» берётся их точное пересечение
/// (Ssi), выбирается ветвь, проходящая через ребро, и обрезается по проекциям вершин.
/// Вершины, где сходятся три такие поверхности, уточняются методом Ньютона на пересечении
/// всех трёх; вершины кромки (две поверхности) — проекцией на точную кривую.
/// Замена принимается, только если прежние точки ребра лежат на новой кривой ближе 1e-4 мм.
/// </summary>
public static class ExactEdges
{
    /// <summary>
    /// Применять после каждой булевой (из AnalyticEdges.Repair). ВЫКЛЮЧЕНО: следующие операции
    /// (скругление строит резцы по вершинам и кривым тела) расходятся на микроны с точным
    /// многогранником тела, и их результат отвергается проверкой (скругление всех рёбер призмы).
    /// Пока точные рёбра применяются к КОПИИ тела при записи в обменные форматы.
    /// </summary>
    private static bool _gEnabled = false;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.ExactEdgesEnabled), если он задан; иначе общее по умолчанию.</summary>
    public static bool Enabled
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ExactEdgesEnabled : _gEnabled;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gEnabled, value, "ExactEdges.Enabled");
    }
    private static bool _gSnapVertices = true;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.ExactEdgesSnapVertices), если он задан; иначе общее по умолчанию.</summary>
    public static bool SnapVertices
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ExactEdgesSnapVertices : _gSnapVertices;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gSnapVertices, value, "ExactEdges.SnapVertices");
    }
    private static bool _gReplaceCurves = true;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.ExactEdgesReplaceCurves), если он задан; иначе общее по умолчанию.</summary>
    public static bool ReplaceCurves
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ExactEdgesReplaceCurves : _gReplaceCurves;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gReplaceCurves, value, "ExactEdges.ReplaceCurves");
    }

    public sealed class Report
    {
        public int Edges, Replaced, Skipped, VerticesSnapped;
        public double MaxShift;
        public override string ToString() => $"edges {Edges}: exact {Replaced}, kept {Skipped}; vertices snapped {VerticesSnapped}; max point shift {MaxShift:E1} mm";
    }

    public static Ssi.Shape? ShapeOf(ISurface3? s)
    {
        switch (s)
        {
            case PlaneSurface p: return new Ssi.Plane(p.F.Origin, p.F.W);
            case CylinderSurface c: return new Ssi.Cylinder(c.Axis.Origin, c.Axis.W, c.Radius);
            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc && Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), ex.Dir.Normalized())) - 1) < 1e-12:
                return new Ssi.Cylinder(arc.Frame.Origin, ex.Dir, arc.Radius);
            case ExtrudedSurface ex when ex.Base is LineCurve3 ln:
            {
                var n = Vector3d.Cross(ln.B - ln.A, ex.Dir);
                return n.Length < 1e-12 ? null : new Ssi.Plane(ln.A, n.Normalized());
            }
            case RevolvedSurface rv when rv.Base is ArcCurve3 ga:
            {
                var w = rv.Axis.W.Normalized(); var d = ga.Frame.Origin - rv.Axis.Origin;
                var radial = d - w * Vector3d.Dot(d, w);
                if (radial.Length < 1e-12) return new Ssi.Sphere(ga.Frame.Origin, ga.Radius);
                // Окружность-образующая в плоскости через ось — тор.
                if (Math.Abs(Vector3d.Dot(ga.Frame.W.Normalized(), Vector3d.Cross(w, radial.Normalized()).Normalized())) > 1 - 1e-9)
                    return new Ssi.Torus(rv.Axis.Origin + w * Vector3d.Dot(d, w), w, radial.Length, ga.Radius);
                return null;
            }
            case RevolvedSurface rv when rv.Base is LineCurve3 gl:
            {
                // Отрезок-образующая: параллельно оси — цилиндр, наклонно — конус (вершина на оси).
                var w = rv.Axis.W.Normalized(); var dir = (gl.B - gl.A).Normalized();
                double c = Vector3d.Dot(dir, w);
                var dA = gl.A - rv.Axis.Origin; double zA = Vector3d.Dot(dA, w); double rA = (dA - w * zA).Length;
                var dB0 = gl.B - rv.Axis.Origin; double zB0 = Vector3d.Dot(dB0, w); double rB0 = (dB0 - w * zB0).Length;
                // KRN-02: ВЫРОЖДЕНИЯ — ПО РАССТОЯНИЮ, А НЕ ПО КОСИНУСУ. Концы образующей несут ошибку округления координат
                // (~4e-12 мм у 3e4 мм): у короткой образующей (0.7 мм) косинус угла с осью уходил за абсолютный порог 1e-12, и
                // плоский диск распознавался «конусом» с полууглом 90° − 5e-12 (классификатор по нему не считал попаданий луча,
                // грань детали отбрасывалась). Плоскость — высоты концов совпадают в пределах модельной точности; цилиндр —
                // совпадают радиусы.
                double acc = ToleranceContext.Current.ModelAccuracy;
                if (Math.Abs(rB0 - rA) <= acc && Math.Abs(zB0 - zA) > acc) return new Ssi.Cylinder(rv.Axis.Origin, w, (rA + rB0) / 2);
                if (Math.Abs(zB0 - zA) <= acc && Math.Abs(rB0 - rA) > acc) return new Ssi.Plane(gl.A + w * ((zB0 - zA) / 2), w);
                if (Math.Abs(Math.Abs(c) - 1) < 1e-12) return new Ssi.Cylinder(rv.Axis.Origin, w, rA);
                // Образующая ⟂ оси — плоскость ⟂ оси: кольцо (дно цекования, собранного вращением) или диск (торец тела
                // вращения, образующая доходит до оси). Поставка 4: прежде кольцо давало null, а диск — «конус» с
                // половинным углом 90° и осью NaN.
                if (Math.Abs(c) < 1e-12) return new Ssi.Plane(gl.A, w);
                var radialDir = (dir - w * c);
                if (radialDir.Length < 1e-12) return null;
                // KRN-02: вершина — где ρ = 0 на прямой образующей; ρ концов — по модулю (отрезок не пересекает ось), раскрытие —
                // к концу с бо́льшим ρ. Прежде знак скорости ρ брался из направления от оси к концу A: у конца НА оси (зенковка
                // вращением треугольника) это вектор длины ~1e-15 мм в случайном направлении — после поворота детали ось
                // конуса переворачивалась (другая полость), плоскость «не пересекала» конус, булева уходила в многогранный путь.
                var dB = gl.B - rv.Axis.Origin; double zB = Vector3d.Dot(dB, w); double rB = (dB - w * zB).Length;
                if (Math.Abs(rA - rB) < 1e-12 * Math.Max(1, Math.Max(rA, rB))) return null;
                var apex = gl.A + (gl.B - gl.A) * (rA / (rA - rB));
                var opening = rB > rA ? dir : -dir;
                double half = Math.Acos(Math.Clamp(Math.Abs(c), 0, 1));
                var axisDir = w * Math.Sign(Vector3d.Dot(opening, w));
                return new Ssi.Cone(apex, axisDir, half);
            }
        }
        return null;
    }

    /// <summary>Расстояние со знаком до поверхности и её нормаль (для уточнения вершин).</summary>
    static (double F, Vector3d G) Implicit(Ssi.Shape s, Vector3d x) => s switch
    {
        Ssi.Plane p => (Vector3d.Dot(p.N.Normalized(), x - p.P), p.N.Normalized()),
        Ssi.Sphere sp => ((x - sp.C).Length - sp.R, (x - sp.C).Normalized()),
        Ssi.Cylinder c => CylImplicit(c, x),
        Ssi.Torus t => TorImplicit(t, x),
        Ssi.Cone k => ConeImplicit(k, x),
        _ => (0, Vector3d.Zero)
    };

    static (double, Vector3d) TorImplicit(Ssi.Torus t, Vector3d x)
    {
        var w = t.Axis.Normalized(); var d = x - t.C; double h = Vector3d.Dot(d, w); var rad = d - w * h;
        var ringPt = t.C + (rad.LengthSquared > 0 ? rad.Normalized() : Vector3d.UnitX) * t.R;
        var v = x - ringPt; double L = v.Length;
        return (L - t.r, L > 0 ? v / L : Vector3d.Zero);
    }

    static (double, Vector3d) ConeImplicit(Ssi.Cone k, Vector3d x)
    {
        var w = k.Axis.Normalized(); var d = x - k.Apex; double h = Vector3d.Dot(d, w); var rad = d - w * h; double rho = rad.Length;
        double f = rho * Math.Cos(k.HalfAngle) - h * Math.Sin(k.HalfAngle);
        var g = (rho > 0 ? rad / rho : Vector3d.Zero) * Math.Cos(k.HalfAngle) - w * Math.Sin(k.HalfAngle);
        return (f, g);
    }

    static (double, Vector3d) CylImplicit(Ssi.Cylinder c, Vector3d x)
    {
        var w = c.Axis.Normalized(); var d = x - c.P; var r = d - w * Vector3d.Dot(d, w);
        return (r.Length - c.R, r.LengthSquared > 0 ? r.Normalized() : Vector3d.Zero);
    }

    public static Report Apply(TSolid solid)
    {
        solid.BeginEdit("ExactEdges.Apply", EditKind.Derive);
        var rep = new Report();
        var shapeOf = new Dictionary<TFace, Ssi.Shape?>(ReferenceEqualityComparer.Instance);
        Ssi.Shape? SOf(TFace f) { if (!shapeOf.TryGetValue(f, out var s)) shapeOf[f] = s = ShapeOf(GeometryAudit.AuthoritativeSurface(f)); return s; }

        // 1. Вершины трёх (и более) аналитических поверхностей — на их общую точку.
        var faceOfVertex = new Dictionary<TVertex, HashSet<TFace>>(ReferenceEqualityComparer.Instance);
        foreach (var e in solid.Edges)
            foreach (var v in new[] { e.V0, e.V1 }.Distinct())
            {
                if (!faceOfVertex.TryGetValue(v, out var set)) faceOfVertex[v] = set = new HashSet<TFace>(ReferenceEqualityComparer.Instance);
                foreach (var ce in e.CoEdges) if (ce.Loop?.Face is { } f) set.Add(f);
            }
        foreach (var (v, faces) in faceOfVertex)
        {
            if (!SnapVertices) break;
            var shapes = faces.Select(SOf).ToList();
            if (shapes.Count < 2 || shapes.Any(x => x == null)) continue;
            var x = v.P;
            if (shapes.Count == 2)
            {
                // Вершина кромки (две поверхности): наименьший сдвиг на их пересечение,
                // x ← x − Jᵀ(JJᵀ)⁻¹f.
                for (int it = 0; it < 20; it++)
                {
                    var (f1, g1) = Implicit(shapes[0]!, x); var (f2, g2) = Implicit(shapes[1]!, x);
                    if (Math.Max(Math.Abs(f1), Math.Abs(f2)) < 1e-13) break;
                    double a11 = Vector3d.Dot(g1, g1), a12 = Vector3d.Dot(g1, g2), a22 = Vector3d.Dot(g2, g2);
                    double det2 = a11 * a22 - a12 * a12;
                    if (Math.Abs(det2) < 1e-14) break;
                    double l1 = (a22 * f1 - a12 * f2) / det2, l2 = (-a12 * f1 + a11 * f2) / det2;
                    x -= g1 * l1 + g2 * l2;
                }
                double sh2 = (x - v.P).Length;
                if (sh2 > 1e-12 && sh2 < 1e-3 && shapes.All(sh => Math.Abs(Implicit(sh!, x).F) < 1e-10))
                { v.P = x; rep.VerticesSnapped++; rep.MaxShift = Math.Max(rep.MaxShift, sh2); }
                continue;
            }
            for (int it = 0; it < 20; it++)
            {
                // Наименьшие квадраты по всем поверхностям: Σ gᵢgᵢᵀ dx = −Σ gᵢfᵢ.
                var M = new Matrix3d(); var rhs = Vector3d.Zero;
                double worst = 0;
                foreach (var sh in shapes)
                {
                    var (f, g) = Implicit(sh!, x);
                    worst = Math.Max(worst, Math.Abs(f));
                    M = Matrix3d.Add(M, new Matrix3d(g.X * g.X, g.X * g.Y, g.X * g.Z, g.Y * g.X, g.Y * g.Y, g.Y * g.Z, g.Z * g.X, g.Z * g.Y, g.Z * g.Z));
                    rhs -= g * f;
                }
                if (worst < 1e-13) break;
                double det = M.Determinant;
                if (Math.Abs(det) < 1e-12) break;
                var dx = Vector3d.TransformRow(rhs, Matrix3d.Invert(M));
                x += dx;
            }
            double shift = (x - v.P).Length;
            if (shift > 1e-12 && shift < 1e-3 && shapes.All(sh => Math.Abs(Implicit(sh!, x).F) < 1e-10))
            {
                v.P = x; rep.VerticesSnapped++; rep.MaxShift = Math.Max(rep.MaxShift, shift);
            }
        }

        // 2. Кривые рёбер.
        foreach (var e in solid.Edges)
        {
            if (!ReplaceCurves) break;
            rep.Edges++;
            var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (faces.Count != 2 || SOf(faces[0]) is not { } a || SOf(faces[1]) is not { } b) { rep.Skipped++; continue; }
            if (e.Curve is IntersectionCurve3) { rep.Skipped++; continue; }    // уже уточняется на обеих поверхностях
            var old = e.Curve;
            var mid = old.PointAt((old.T0 + old.T1) / 2);
            Ssi.Result r;
            try { r = Ssi.Intersect(a, b, mid, Math.Max(10, (e.V0.P - e.V1.P).Length * 2 + 10)); }
            catch (NotSupportedException) { rep.Skipped++; continue; }
            if (r.Kind != Ssi.Kind.Curves) { rep.Skipped++; continue; }
            var made = r.Curves.Select(c => Trim(c, e, old)).Where(c => c != null).Select(c => c!)
                        .OrderBy(c => Deviation(old, c)).FirstOrDefault();
            // В обе стороны: точки прежней кривой на новой И точки новой на прежней, концы — у
            // вершин. Только первая проверка пропускала дугу длиннее прежней (полный круг вместо
            // половины): контур грани размыкался на 8 мм (скругление всех рёбер призмы).
            if (made == null || Deviation(old, made) > 1e-4 || Deviation(made, old) > 1e-4) { rep.Skipped++; continue; }
            {
                var ma = made.PointAt(made.T0); var mb = made.PointAt(made.T1);
                double endGap = ReferenceEquals(e.V0, e.V1) ? Math.Max((ma - e.V0.P).Length, (mb - e.V0.P).Length)
                    : Math.Min(Math.Max((ma - e.V0.P).Length, (mb - e.V1.P).Length), Math.Max((ma - e.V1.P).Length, (mb - e.V0.P).Length));
                if (endGap > 1e-5) { rep.Skipped++; continue; }
            }
            rep.MaxShift = Math.Max(rep.MaxShift, Deviation(old, made));
            e.Curve = made;
            e.PolylineCache = null;
            var ca = made.PointAt(made.T0); var cb = made.PointAt(made.T1);
            e.Tolerance = Math.Max(e.Tolerance, Math.Max(Math.Min((ca - e.V0.P).Length, (ca - e.V1.P).Length), Math.Min((cb - e.V0.P).Length, (cb - e.V1.P).Length)));
            rep.Replaced++;
        }
        // Рёбра, оставшиеся прежними, но с перенесёнными вершинами: их допуск — не меньше нового
        // расхождения концов кривой с вершинами (иначе проверка B-rep отвергала тело — скругление
        // всех рёбер призмы, где у тора-перехода кривые остались прежними).
        if (rep.VerticesSnapped > 0)
            foreach (var e in solid.Edges)
            {
                var c = e.Curve; Vector3d ca = c.PointAt(c.T0), cb = c.PointAt(c.T1);
                double gap = ReferenceEquals(e.V0, e.V1) ? Math.Max((ca - e.V0.P).Length, (cb - e.V0.P).Length)
                    : Math.Min(Math.Max((ca - e.V0.P).Length, (cb - e.V1.P).Length), Math.Max((ca - e.V1.P).Length, (cb - e.V0.P).Length));
                e.Tolerance = Math.Max(e.Tolerance, gap);
                e.V0.Tolerance = Math.Max(e.V0.Tolerance, gap); e.V1.Tolerance = Math.Max(e.V1.Tolerance, gap);
                e.PolylineCache = null;
            }
        if (rep.Replaced > 0 || rep.VerticesSnapped > 0)
        {
            foreach (var f in solid.Faces) if (f.Surface is FacetedSurface fs) { fs.SetDisplay(null, null); }
            solid.DisplayMeshKey = null;
        }
        return rep;
    }

    static double Deviation(ICurve3 old, ICurve3 made)
    {
        double worst = 0;
        for (int i = 0; i <= 16; i++)
        {
            var p = old.PointAt(old.T0 + (old.T1 - old.T0) * i / 16);
            worst = Math.Max(worst, Distance(made, p));
        }
        return worst;
    }

    static double Distance(ICurve3 c, Vector3d p)
    {
        switch (c)
        {
            case LineCurve3 l: { var ab = l.B - l.A; double t = Math.Clamp(Vector3d.Dot(p - l.A, ab) / ab.LengthSquared, 0, 1); return (l.A + ab * t - p).Length; }
            default:
            {
                double best = double.MaxValue;
                for (int i = 0; i <= 256; i++) best = Math.Min(best, (c.PointAt(c.T0 + (c.T1 - c.T0) * i / 256) - p).Length);
                return best;
            }
        }
    }

    /// <summary>Кусок точной кривой от проекции V0 до проекции V1 в направлении прежней кривой.</summary>
    static ICurve3? Trim(ICurve3 full, TEdge e, ICurve3 old)
    {
        bool closed = ReferenceEquals(e.V0, e.V1);
        switch (full)
        {
            case LineCurve3 l:
            {
                var d = (l.B - l.A).Normalized();
                Vector3d Proj(Vector3d q) => l.A + d * Vector3d.Dot(q - l.A, d);
                return closed ? null : new LineCurve3(Proj(e.V0.P), Proj(e.V1.P));
            }
            case ArcCurve3 a: return TrimConic(e, old, a.Frame, a.Radius, a.Radius, (f, ru, rv, t0, t1) => new ArcCurve3(f, ru, t0, t1));
            case EllipseCurve3 el: return TrimConic(e, old, el.Frame, el.RU, el.RV, (f, ru, rv, t0, t1) => new EllipseCurve3(f, ru, rv, t0, t1));
        }
        return null;
    }

    static ICurve3? TrimConic(TEdge e, ICurve3 old, Frame3 f, double ru, double rv, Func<Frame3, double, double, double, double, ICurve3> make)
    {
        var u = f.U.Normalized(); var v = f.V.Normalized();
        double Ang(Vector3d q) { var d = q - f.Origin; return Math.Atan2(Vector3d.Dot(d, v) / rv, Vector3d.Dot(d, u) / ru); }
        // Направление обхода — как у прежней кривой: по её точке на четверти пути.
        bool forward = (old.PointAt(old.T0) - e.V0.P).LengthSquared <= (old.PointAt(old.T1) - e.V0.P).LengthSquared || ReferenceEquals(e.V0, e.V1);
        var start = forward ? e.V0.P : e.V1.P; var end = forward ? e.V1.P : e.V0.P;
        double a0 = Ang(start), aq = Ang(old.PointAt(old.T0 + (old.T1 - old.T0) * 0.25));
        double a1 = Ang(end);
        double dq = aq - a0; while (dq > Math.PI) dq -= 2 * Math.PI; while (dq < -Math.PI) dq += 2 * Math.PI;
        double sweep = a1 - a0;
        if (ReferenceEquals(e.V0, e.V1)) sweep = Math.Sign(dq) * 2 * Math.PI;
        else
        {
            if (dq > 0) { while (sweep <= 0) sweep += 2 * Math.PI; while (sweep > 2 * Math.PI) sweep -= 2 * Math.PI; }
            else { while (sweep >= 0) sweep -= 2 * Math.PI; while (sweep < -2 * Math.PI) sweep += 2 * Math.PI; }
        }
        return make(f, ru, rv, a0, a0 + sweep);
    }
}
