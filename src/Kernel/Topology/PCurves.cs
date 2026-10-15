using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// G1. КРИВЫЕ РЁБЕР В ПАРАМЕТРАХ ГРАНЕЙ.
///
/// Для каждого полуребра грани с точной поверхностью строится его кривая в параметрах (u, v)
/// этой поверхности: точная — отрезок (прямая на плоскости, изолиния цилиндра, конуса, шара,
/// тора: образующая и параллель) или дуга (окружность на плоскости); иначе — ломаная по точкам
/// ребра. Для каждой кривой измеряется отклонение S(pcurve(t)) от самого ребра. Периодические
/// параметры (угол цилиндра, тела вращения) разворачиваются, чтобы кривая через шов была
/// непрерывной. Это основа точного представления граней: обрезка в параметрах, разметка в (u, v),
/// запись кривых граней в STEP.
/// </summary>
public static class PCurves
{
    public sealed class Stats
    {
        public int Exact, Sampled, Skipped;
        public double MaxDeviationExact, MaxDeviationSampled;
        public override string ToString() =>
            $"pcurves exact {Exact}, sampled {Sampled}, skipped {Skipped}; max deviation exact {MaxDeviationExact:E1} mm, sampled {MaxDeviationSampled:E1} mm";
    }

    public static Stats Build(TSolid solid, int samples = 48)
    {
        var st = new Stats();
        foreach (var face in solid.Faces)
        {
            var s = GeometryAudit.AuthoritativeSurface(face);
            foreach (var ce in face.Loops.SelectMany(l => l.CoEdges))
            {
                ce.PCurve = null; ce.PCurveDeviation = 0;
                if (s == null) { st.Skipped++; continue; }
                // Выборка уплотняется, пока ломаная в параметрах не ляжет на ребро с допуском
                // 1e-5 мм (шаг сетки точного тела): у кривой пересечения цилиндров 48 точек
                // давали 4e-4 мм.
                // KRN-03: не точная — кубическая Эрмита, уплотняется до модельной точности (бюджет); прежде — ломаная до 1e-5 мм.
                double budget = ToleranceContext.Current.ModelAccuracy;
                var pc = Densify(ce, s, samples, budget, out double dev, out _);
                // KRN-06 (06.14): процедурная кривая на поверхности, где уплотнение Эрмита упёрлось в шум проекции (грань NURBS,
                // длинная линия касания: 2e-7 при бюджете 1e-7) — p-кривая проекцией самой кривой (точная по построению).
                // Мутация P3D_PCPROJPROC=0.
                if (pc != null && !pc.IsExact && dev > budget && ce.Edge.Curve is ProcCurve3 && ProjProc) pc = For(ce, s, samples, out dev, projected: true);
                if (pc == null) { st.Skipped++; continue; }
                ce.PCurve = pc; ce.PCurveDeviation = dev;
                if (pc.IsExact) { st.Exact++; st.MaxDeviationExact = Math.Max(st.MaxDeviationExact, dev); }
                else { st.Sampled++; st.MaxDeviationSampled = Math.Max(st.MaxDeviationSampled, dev); }
            }
        }
        return st;
    }

    /// <summary>
    /// KRN-03: p-кривая и её отклонение для каждого полуребра — БЕЗ записи в тело (проверка не правит опубликованное тело).
    /// Не точная — Эрмита, уплотнение до модельной точности.
    /// </summary>
    public static List<(TCoEdge Ce, ICurve2? PC, double Dev)> Measure(TSolid solid, int samples = 48)
    {
        var res = new List<(TCoEdge, ICurve2?, double)>();
        double budget = ToleranceContext.Current.ModelAccuracy;
        foreach (var face in solid.Faces)
        {
            var s = GeometryAudit.AuthoritativeSurface(face);
            foreach (var ce in face.Loops.SelectMany(l => l.CoEdges))
            {
                if (s == null) { res.Add((ce, null, 0)); continue; }
                var swT = System.Diagnostics.Stopwatch.StartNew(); int lastM = samples;
                var pc = Densify(ce, s, samples, budget, out double dev, out lastM);
                if (pc != null && !pc.IsExact && dev > budget && ce.Edge.Curve is ProcCurve3 && ProjProc) { pc = For(ce, s, samples, out dev, projected: true); lastM = -1; }
                if (TimeLog && swT.ElapsedMilliseconds > 500) Console.Error.WriteLine($"PCTIME {swT.ElapsedMilliseconds} ms edge {ce.Edge.Id} {ce.Edge.Curve.GetType().Name}{(ce.Edge.Curve is ProcCurve3 pq ? ":" + pq.Kind : "")} surf {s.GetType().Name} pc {pc?.GetType().Name} m {lastM} dev {dev:E2}");
                res.Add((ce, pc, dev));
            }
        }
        return res;
    }

    /// <summary>Точка полуребра по доле t ∈ [0,1] от Start к End.</summary>
    public static Vector3d At(TCoEdge ce, double t)
    {
        var e = ce.Edge; var c = e.Curve;
        bool closed = ReferenceEquals(e.V0, e.V1);
        bool forward = closed || (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared;
        double f = ce.Reversed ? 1 - t : t;
        if (!forward) f = 1 - f;
        return c.PointAt(c.T0 + (c.T1 - c.T0) * f);
    }

    /// <summary>
    /// Уплотнение Эрмита до бюджета. KRN-06: у процедурной кривой (точка — корень уравнения) уплотнение упирается в шум
    /// вычисления: отклонение перестаёт падать (Эрмита — h⁴, удвоение даёт ×1/16), а каждое удвоение вдвое дороже — до 4096
    /// узлов за минуты на ребро. Застой (удвоение уменьшило отклонение меньше чем втрое) у ProcCurve3 — выход: дальше
    /// p-кривая проекцией самой кривой. Мутация P3D_PCSTALL=0 — прежнее уплотнение до 4096.
    /// </summary>
    static ICurve2? Densify(TCoEdge ce, ISurface3 s, int samples, double budget, out double dev, out int lastM)
    {
        lastM = samples;
        var pc = For(ce, s, samples, out dev);
        bool proc = ce.Edge.Curve is ProcCurve3 && ProjProc && StallStop;
        for (int m = samples * 2; pc != null && !pc.IsExact && dev > budget && m <= 4096; m *= 2)
        {
            double prev = dev;
            pc = For(ce, s, m, out dev); lastM = m;
            if (proc && dev > budget && dev > prev / 3) break;
        }
        return pc;
    }
    static readonly bool StallStop = Environment.GetEnvironmentVariable("P3D_PCSTALL") != "0";
    static readonly bool TimeLog = Environment.GetEnvironmentVariable("P3D_PCTIME") == "1";
    static readonly bool ProjProc = Environment.GetEnvironmentVariable("P3D_PCPROJPROC") != "0";

    private static ICurve2? For(TCoEdge ce, ISurface3 s, int n, out double dev, bool projected = false)
    {
        dev = 0;
        var uv = new Vector2d[n + 1]; var xyz = new Vector3d[n + 1];
        var poles = SurfaceDomain.Poles(s);
        for (int i = 0; i <= n; i++) { xyz[i] = At(ce, (double)i / n); uv[i] = PoleSafeUV(s, poles, ce, (double)i / n); }
        // Развёртка периодических параметров (период 1 по u или v у цилиндра и тела вращения).
        var (perU, perV) = Periods(s);
        for (int i = 1; i <= n; i++)
        {
            if (perU > 0) uv[i].X -= perU * Math.Round((uv[i].X - uv[i - 1].X) / perU);
            if (perV > 0) uv[i].Y -= perV * Math.Round((uv[i].Y - uv[i - 1].Y) / perV);
        }
        double scale = Math.Max(1, uv.Max(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y))));
        ICurve2 cand;
        // Отрезок: все точки — на прямой A→B с параметром, пропорциональным t.
        var line = new Line2(uv[0], uv[n]);
        if (Enumerable.Range(0, n + 1).All(i => (line.PointAt((double)i / n) - uv[i]).Length < 1e-9 * scale)) cand = line;
        else if (s.IsPlanar && ConicOnPlane(ce, s, uv, scale) is { } cn) cand = cn;
        else if (s.IsPlanar && ArcThrough(uv) is { } arc) cand = arc;
        else if (projected || ce.Edge.Curve is IntersectionCurve3 or Geometry.NurbsImplicitCurve3 or Geometry.NurbsNurbsCurve3)
            cand = new ProjectedCurve2(t => PoleSafeUV(s, poles, ce, t), uv, perU, perV);
        else cand = HermiteThrough(ce, s, uv, perU, perV) ?? (ICurve2)new Polyline2(uv);
        // Отклонение: середины шагов и сами точки выборки.
        if (cand is Hermite2 hc)
        {
            for (int i = 0; i + 1 < hc.T.Length; i++)
                for (int q = 1; q <= 4; q++)
                {
                    double t = hc.T[i] + (hc.T[i + 1] - hc.T[i]) * (q - 0.5) / 4;
                    dev = Math.Max(dev, (s.PointAt(cand.PointAt(t)) - At(ce, t)).Length);
                }
        }
        else
            for (int i = 0; i < 2 * n; i++)
            {
                double t = (i + 0.5) / (2 * n);
                dev = Math.Max(dev, (s.PointAt(cand.PointAt(t)) - At(ce, t)).Length);
            }
        return cand;
    }

    /// <summary>
    /// KRN-03: Эрмита по узлам выборки; производная по t в узле — центральной разностью по проекции точек ребра (у концов —
    /// односторонней второго порядка), с развёрткой периода к значению узла. Сбой проекции (полюс) — null.
    /// </summary>
    private static Hermite2? HermiteThrough(TCoEdge ce, ISurface3 s, Vector2d[] uv, double perU, double perV)
    {
        int n = uv.Length - 1;
        // узлы: равномерные + изломы параметризации 3D-кривой (в t полуребра)
        var ts = new SortedSet<double>(Enumerable.Range(0, n + 1).Select(i => (double)i / n));
        var T = ts.ToArray();
        double minGap = Enumerable.Range(0, T.Length - 1).Min(i => T[i + 1] - T[i]);
        double dt = Math.Min(1e-3 / n, minGap / 4);
        if (dt <= 0) return null;
        var breakSet = new HashSet<double>();
        Vector2d Near(Vector2d q, Vector2d r)
        {
            if (perU > 0) q.X += perU * Math.Round((r.X - q.X) / perU);
            if (perV > 0) q.Y += perV * Math.Round((r.Y - q.Y) / perV);
            return q;
        }
        var poles = SurfaceDomain.Poles(s);
        Vector2d UV(double t, Vector2d near) => Near(PoleSafeUV(s, poles, ce, Math.Clamp(t, 0, 1)), near);
        var P = new Vector2d[T.Length]; var Dl = new Vector2d[T.Length]; var Dr = new Vector2d[T.Length];
        Vector2d prev = uv[0];
        for (int i = 0; i < T.Length; i++)
        {
            double t = T[i];
            P[i] = i == 0 ? uv[0] : UV(t, prev); prev = P[i];
            Vector2d Right() { var f1 = UV(t + dt, P[i]); var f2 = UV(t + 2 * dt, P[i]); return (-3 * P[i] + 4 * f1 - f2) / (2 * dt); }
            Vector2d Left() { var f1 = UV(t - dt, P[i]); var f2 = UV(t - 2 * dt, P[i]); return (3 * P[i] - 4 * f1 + f2) / (2 * dt); }
            if (i == 0) Dl[i] = Dr[i] = Right();
            else if (i == T.Length - 1) Dl[i] = Dr[i] = Left();
            else if (breakSet.Contains(t)) { Dl[i] = Left(); Dr[i] = Right(); }
            else Dl[i] = Dr[i] = (UV(t + dt, P[i]) - UV(t - dt, P[i])) / (2 * dt);
            foreach (var d in new[] { Dl[i], Dr[i] })
                if (double.IsNaN(d.X) || double.IsNaN(d.Y) || double.IsInfinity(d.X) || double.IsInfinity(d.Y)) return null;
        }
        return new Hermite2(T, P, Dl, Dr);
    }

    /// <summary>
    /// KRN-03: ПАРАМЕТРЫ ТОЧКИ ПОЛУРЕБРА С УЧЁТОМ ПОЛЮСА КАРТЫ. В полюсе угол не определён, и проекция давала произвольный —
    /// p-кривая ребра, кончающегося в полюсе (долька сферы, вершина в полюсе — законно по контракту SurfaceDomain), отходила
    /// от ребра на 6e-3 мм. В полюсе берётся предел вдоль ребра: угол — у точки, сдвинутой внутрь ребра (2⁻²⁰ доли, затем
    /// меньше), параметр образующей — самого полюса.
    /// </summary>
    internal static Vector2d PoleSafeUV(ISurface3 s, List<Vector3d> poles, TCoEdge ce, double t)
    {
        var x = At(ce, t); var q = s.ProjectToUV(x);
        if (!PoleLimit || poles.Count == 0) return q;
        double tol = Math.Max(1e-9, 10 * ToleranceContext.Current.ModelAccuracy);
        if (!poles.Any(p => (p - x).Length <= tol)) return q;
        double dir = t > 0.5 ? -1 : 1;
        for (double h = 1.0 / (1 << 20); h > 1e-15; h /= 16)
        {
            var y = At(ce, t + dir * h);
            if (poles.Any(p => (p - y).Length <= tol)) continue;
            var qy = s.ProjectToUV(y);
            return s is RevolvedSurface ? new Vector2d(q.X, qy.Y) : qy;
        }
        return q;
    }
    /// <summary>KRN-03: предел угла в полюсе (P3D_POLELIMIT=0 — прежняя проекция, для мутации).</summary>
    internal static readonly bool PoleLimit = Environment.GetEnvironmentVariable("P3D_POLELIMIT") != "0";

    /// <summary>Изломы параметризации 3D-кривой ребра в t полуребра (у кривой пересечения — точки затравки).</summary>
    private static IEnumerable<double> Breaks(TCoEdge ce)
    {
        if (ce.Edge.Curve is not IntersectionCurve3 ic) yield break;
        var e = ce.Edge; var c = e.Curve;
        bool closed = ReferenceEquals(e.V0, e.V1);
        bool forward = closed || (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared;
        foreach (double f0 in ic.Breaks)
        {
            double f = forward ? f0 : 1 - f0;          // доля кривой → доля полуребра (как в At)
            yield return ce.Reversed ? 1 - f : f;
        }
    }

    static readonly bool SectPerOff = Environment.GetEnvironmentVariable("P3D_PCSECTPER") == "0";
    static readonly bool PeriodsOld = Environment.GetEnvironmentVariable("P3D_PCPERIOD") == "0";

    /// <summary>
    /// Периоды параметров поверхности (0 — непериодичен). g.05: период — не обязательно 1: у выдавливания дуги параметр u — доля
    /// ДУГИ основания (A0…A1), и полный оборот — 2π/|размах|; прежде проверялся только сдвиг на 1, развёртка не делалась, и
    /// p-кривая дуги через «шов» параметра отходила от ребра на 22 мм (разностная серия 9, случай 20). Мутация P3D_PCPERIOD=0.
    /// </summary>
    private static (double U, double V) Periods(ISurface3 s)
    {
        bool Same(Vector2d a, Vector2d b) => (s.PointAt(a) - s.PointAt(b)).Length < 1e-9 * Math.Max(1, s.PointAt(a).Length);
        var p = new Vector2d(0.3137, 0.4271);
        try
        {
            if (s is PlaneSurface) return (0, 0);
            var cu = new List<double> { 1 }; var cv = new List<double> { 1 };
            if (!PeriodsOld)
            {
                if (s is ExtrudedSurface { Base: ArcCurve3 ba } && Math.Abs(ba.Sweep) > 1e-12) cu.Add(2 * Math.PI / Math.Abs(ba.Sweep));
                // KRN-06 (06.12): замкнутая поверхность по формуле сечения (лента катящегося шара) — период u = её промежуток
                if (s is SectionSurface { Closed: true } ssc) cu.Add(ssc.UMax - ssc.UMin);
                if (s is RevolvedSurface rv)
                {
                    if (Math.Abs(rv.Sweep) > 1e-12) cv.Add(2 * Math.PI / Math.Abs(rv.Sweep));
                    if (rv.Base is ArcCurve3 rb && Math.Abs(rb.Sweep) > 1e-12) cu.Add(2 * Math.PI / Math.Abs(rb.Sweep));
                }
            }
            // KRN-07 (07.3): у поверхности по формуле сечения u — миллиметры от начала траектории, промежуток грани может
            // начинаться не с нуля (треть замкнутой протяжки): проба (0.31, ·) и (1.31, ·) — обе вне промежутка, таблица
            // прижимает их к краю, и «период 1» находился ложно (p-кривая рельса отходила на 20–30 мм). Проба — внутри
            // промежутка, кандидат 1 — не для неё. Мутация P3D_PCSECTPER=0.
            if (!SectPerOff && s is SectionSurface sps)
            {
                p = new Vector2d(sps.UMin + 0.3137 * (sps.UMax - sps.UMin), 0.4271);
                cu.Remove(1);
            }
            double pu = cu.FirstOrDefault(c => Same(p, p + new Vector2d(c, 0)));
            double pv = cv.FirstOrDefault(c => Same(p, p + new Vector2d(0, c)));
            return (pu, pv);
        }
        catch { return (0, 0); }
    }

    static readonly bool PcConicOff = Environment.GetEnvironmentVariable("P3D_PCCONIC") == "0";

    /// <summary>
    /// g.05: P-КРИВАЯ ДУГИ ИЛИ ЭЛЛИПСА НА ПЛОСКОЙ ГРАНИ — ТОЧНО: параметры плоской поверхности — аффинный образ плоскости
    /// (плоскость; выдавливание отрезка — оси не единичны и не ортогональны), и дуга переходит в дугу эллипса
    /// C + cos a·P + sin a·Q с тем же углом a, что у 3D-кривой. Прежде — окружность через три точки выборки: на выдавленной
    /// грани окружность в параметрах — эллипс, и у короткой дуги (кривая пересечения поперёк ребра 0.01 мм) три точки
    /// «подтверждали» окружность, а между ними p-кривая отходила от ребра на 2e-6 мм при допуске 1e-7. Мутация P3D_PCCONIC=0.
    /// </summary>
    private static Conic2? ConicOnPlane(TCoEdge ce, ISurface3 s, Vector2d[] uv, double scale)
    {
        if (PcConicOff) return null;
        Frame3 F; double ru, rv, a0, sw;
        switch (ce.Edge.Curve)
        {
            case ArcCurve3 a: F = a.Frame; ru = rv = a.Radius; a0 = a.A0; sw = a.Sweep; break;
            case EllipseCurve3 el: F = el.Frame; ru = el.RU; rv = el.RV; a0 = el.A0; sw = el.Sweep; break;
            default: return null;
        }
        var c = s.ProjectToUV(F.Origin);
        var p = s.ProjectToUV(F.Origin + F.U.Normalized() * ru) - c;
        var q = s.ProjectToUV(F.Origin + F.V.Normalized() * rv) - c;
        // доли 3D-кривой у начала и конца полуребра (как в At)
        var e = ce.Edge; var cv = e.Curve;
        bool closed = ReferenceEquals(e.V0, e.V1);
        bool forward = closed || (cv.PointAt(cv.T0) - e.V0.P).LengthSquared <= (cv.PointAt(cv.T1) - e.V0.P).LengthSquared;
        double F0(double t) { double f = ce.Reversed ? 1 - t : t; return forward ? f : 1 - f; }
        var cn = new Conic2(c, p, q, a0 + sw * F0(0), a0 + sw * F0(1));
        int n = uv.Length - 1;
        for (int i = 0; i <= n; i++) if ((cn.PointAt((double)i / n) - uv[i]).Length > 1e-9 * scale) return null;   // не аффинно — не годится
        return cn;
    }

    /// <summary>Дуга окружности в параметрах плоскости через точки выборки (параметр — равномерно по углу).</summary>
    private static Arc2? ArcThrough(Vector2d[] uv)
    {
        int n = uv.Length - 1;
        var a = uv[0]; var b = uv[n / 2]; var c = uv[n];
        if ((a - c).Length < 1e-12) c = uv[n / 4];
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-15) return null;
        double ux = ((a.LengthSquared) * (b.Y - c.Y) + (b.LengthSquared) * (c.Y - a.Y) + (c.LengthSquared) * (a.Y - b.Y)) / d;
        double uy = ((a.LengthSquared) * (c.X - b.X) + (b.LengthSquared) * (a.X - c.X) + (c.LengthSquared) * (b.X - a.X)) / d;
        var centre = new Vector2d(ux, uy); double r = (a - centre).Length;
        double a0 = Math.Atan2(uv[0].Y - uy, uv[0].X - ux);
        // Развёрнутые углы выборки.
        var ang = new double[n + 1]; ang[0] = a0;
        for (int i = 1; i <= n; i++)
        {
            double ai = Math.Atan2(uv[i].Y - uy, uv[i].X - ux);
            while (ai - ang[i - 1] > Math.PI) ai -= 2 * Math.PI;
            while (ai - ang[i - 1] < -Math.PI) ai += 2 * Math.PI;
            ang[i] = ai;
        }
        var arc = new Arc2(centre, r, ang[0], ang[n]);
        double tol = 1e-9 * Math.Max(1, r);
        for (int i = 0; i <= n; i++) if ((arc.PointAt((double)i / n) - uv[i]).Length > tol) return null;
        return arc;
    }
}
