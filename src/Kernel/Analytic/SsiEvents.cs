using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// KRN-04 (g.05): СОБЫТИЯ ПЕРЕСЕЧЕНИЯ — где ребро границы грани пересекает поверхность другой грани. Кривая пересечения
/// поверхностей A и B выходит из грани A там, где пересекает ребро её границы; эта точка лежит на ребре (на A) и на B — значит,
/// это корень «ребро ∩ поверхность B». Для прямой, дуги и эллипса — АЛГЕБРАИЧЕСКИ: неявное уравнение поверхности (степень 1, 2
/// или 4) вдоль прямой — многочлен, вдоль дуги — тригонометрический многочлен (подстановкой половинного угла — многочлен
/// степени 2d на кусках дуги не больше π/2). Все вещественные корни многочлена отделяются рекурсивно по корням производной
/// (между соседними критическими точками функция монотонна): ни один корень не теряется от шага выборки. Кратный корень
/// (касание ребра и поверхности) — тоже событие. Ребро, целиком лежащее на поверхности, — «совпадающий интервал»: события —
/// его концы. Прочие кривые рёбер (кривая пересечения, сплайн) — выборкой с уточнением; их доля считается (SampledEdges).
/// </summary>
public static class SsiEvents
{
    /// <summary>Сколько рёбер дали события выборкой (нет алгебраического решения) — замер покрытия.</summary>
    public static long AlgebraicEdges, SampledEdges, CoincidentEdges, TangentEvents;

    /// <summary>Алгебраическое неявное уравнение поверхности (многочлен по координатам; нули — поверхность, у конуса — обе полости).</summary>
    public static double Q(Ssi.Shape s, Vector3d x)
    {
        switch (s)
        {
            case Ssi.Plane p: return Vector3d.Dot(p.N.Normalized(), x - p.P);
            case Ssi.Sphere sp: return (x - sp.C).LengthSquared - sp.R * sp.R;
            case Ssi.Cylinder c: { var w = c.Axis.Normalized(); var d = x - c.P; double h = Vector3d.Dot(d, w); return d.LengthSquared - h * h - c.R * c.R; }
            case Ssi.Cone k:
            {
                var w = k.Axis.Normalized(); var d = x - k.Apex; double h = Vector3d.Dot(d, w); double rho2 = d.LengthSquared - h * h;
                double ca = Math.Cos(k.HalfAngle), sa = Math.Sin(k.HalfAngle);
                return rho2 * ca * ca - h * h * sa * sa;
            }
            case Ssi.Torus t:
            {
                var w = t.Axis.Normalized(); var d = x - t.C; double h = Vector3d.Dot(d, w); double d2 = d.LengthSquared, rho2 = d2 - h * h;
                double m = d2 + t.R * t.R - t.r * t.r;
                return m * m - 4 * t.R * t.R * rho2;
            }
        }
        throw new NotSupportedException(s.GetType().Name);
    }

    /// <summary>
    /// Квадрика вдоль прямой p0 + s·g: Q = A s² + B s + C — коэффициенты в ЗАМКНУТОМ виде (относительно центра формы). Прежде —
    /// разностями Q в трёх точках: у детали ×1000 вдали от начала Q ~ 1e10, а A ~ 1 — потеря точности (кривая пересечения
    /// цилиндра с конусом уходила от поверхностей на 6e-4 мм). null — форма не квадрика (тор).
    /// </summary>
    public static (double A, double B, double C)? QLine(Ssi.Shape s, Vector3d p0, Vector3d g)
    {
        switch (s)
        {
            case Ssi.Sphere sp: { var y = p0 - sp.C; return (g.LengthSquared, 2 * Vector3d.Dot(g, y), y.LengthSquared - sp.R * sp.R); }
            case Ssi.Cylinder c:
            {
                var w = c.Axis.Normalized(); var y = p0 - c.P; double gw = Vector3d.Dot(g, w), yw = Vector3d.Dot(y, w);
                return (g.LengthSquared - gw * gw, 2 * (Vector3d.Dot(g, y) - gw * yw), y.LengthSquared - yw * yw - c.R * c.R);
            }
            case Ssi.Cone k:
            {
                var w = k.Axis.Normalized(); var y = p0 - k.Apex; double c2 = Math.Cos(k.HalfAngle); c2 *= c2;
                double gw = Vector3d.Dot(g, w), yw = Vector3d.Dot(y, w);
                return (g.LengthSquared * c2 - gw * gw, 2 * (Vector3d.Dot(g, y) * c2 - gw * yw), y.LengthSquared * c2 - yw * yw);
            }
            case Ssi.Plane pl: { var n = pl.N.Normalized(); return (0, Vector3d.Dot(n, g), Vector3d.Dot(n, p0 - pl.P)); }
        }
        return null;
    }

    static int Degree(Ssi.Shape s) => s switch { Ssi.Plane => 1, Ssi.Torus => 4, _ => 2 };

    /// <summary>
    /// Параметры кривой ребра, где оно пересекает или касается поверхности s. coincident — ребро лежит на поверхности целиком
    /// (тогда в ответе — концы). null не бывает: кривая без алгебраического решения — выборкой.
    /// </summary>
    public static List<double> CurveSurface(ICurve3 e, Ssi.Shape s, out bool coincident)
    {
        coincident = false; LastUncertain = false;
        // ребро на поверхности целиком: расстояние ~0 по всей длине
        bool allOn = true;
        for (int i = 0; i <= 16 && allOn; i++) if (Math.Abs(Ssi.Implicit(s, e.PointAt(e.T0 + (e.T1 - e.T0) * (i + 0.37 * (i < 16 ? 1 : 0)) / 16)).F) > 1e-9) allOn = false;
        if (allOn) { coincident = true; Interlocked.Increment(ref CoincidentEdges); return new List<double> { e.T0, e.T1 }; }

        var roots = new List<double>();
        int d = Degree(s);
        switch (e)
        {
            case LineCurve3 l:
            {
                Interlocked.Increment(ref AlgebraicEdges);
                foreach (var x in PolyRootsOn(u => Q(s, l.PointAt((u + 1) / 2)), d)) roots.Add((x + 1) / 2);
                break;
            }
            case ArcCurve3 or EllipseCurve3:
            {
                Interlocked.Increment(ref AlgebraicEdges);
                double a0 = e is ArcCurve3 ar ? ar.A0 : ((EllipseCurve3)e).A0, sw = e is ArcCurve3 ar2 ? ar2.Sweep : ((EllipseCurve3)e).Sweep;
                Vector3d At(double ang) => e.PointAt((ang - a0) / sw);
                int m = Math.Max(1, (int)Math.Ceiling(Math.Abs(sw) / (Math.PI / 2) - 1e-12));
                for (int k = 0; k < m; k++)
                {
                    double lo = a0 + sw * k / m, hi = a0 + sw * (k + 1) / m, ac = (lo + hi) / 2, th = Math.Tan((hi - lo) / 4);
                    // угол = ac + 2·atan(u·th), u ∈ [−1, 1]; Q·(1 + τ²)^d — многочлен по u степени 2d
                    double G(double u) { double tau = u * th; return Q(s, At(ac + 2 * Math.Atan(tau))) * Math.Pow(1 + tau * tau, d); }
                    foreach (var u in PolyRootsOn(G, 2 * d)) roots.Add((ac + 2 * Math.Atan(u * th) - a0) / sw);
                }
                break;
            }
            case IntersectionCurve3 ic when !TracedOff && TracedEvents(ic, s) is { } tr:
                Interlocked.Increment(ref TracedCertified);
                roots.AddRange(tr);
                break;
            case SplineCurve3 or BezierCurve3 or NurbsImplicitCurve3 or NurbsNurbsCurve3 when !RigorousEvents.Off && RigorousEvents.OnImplicit(e, s) is { } rg:
                if (RigorDbg)
                {
                    var old = Sampled(e, s).Select(t => Polish(e, s, t)).Where(t => Math.Abs(Ssi.Implicit(s, e.PointAt(t)).F) <= 1e-7).ToList();
                    Console.Error.WriteLine($"RIGOR {e.GetType().Name} × {s.GetType().Name}: rigorous [{string.Join(", ", rg.Select(t => t.ToString("R")))}] sampled [{string.Join(", ", old.Select(t => t.ToString("R")))}]");
                }
                roots.AddRange(rg.Select(t => OnSampleGrid(e, s, t)));
                break;
            case ProcCurve3 pc when !RigorousEvents.Off && !ContactExclOff && pc.NoEventsOn != null && pc.NoEventsOn(s, Math.Min(pc.T0, pc.T1), Math.Max(pc.T0, pc.T1)):
                Interlocked.Increment(ref RigorousEvents.Certified);   // доказано: кривая поверхность не задевает
                break;
            default:
                if (e is IntersectionCurve3 && !TracedOff) { Interlocked.Increment(ref TracedUncertain); LastUncertain = true; }
                else if (!RigorousEvents.Off) LastUncertain = true;          // выборка — события не доказаны
                Interlocked.Increment(ref SampledEdges);
                roots.AddRange(Sampled(e, s));
                break;
        }
        // уточнение по расстоянию со знаком (корень многочлена — до ~1e-10 относительной; здесь — до машинной)
        var outR = new List<double>();
        foreach (var t0 in roots)
        {
            double t = Math.Clamp(t0, e.T0, e.T1);
            t = Polish(e, s, t);
            if (Math.Abs(Ssi.Implicit(s, e.PointAt(t)).F) <= 1e-7) outR.Add(t);   // у конуса — корни второй полости отсеиваются
        }
        outR.Sort();
        // кратный корень (касание) находится дважды — по обе стороны критической точки: точки ближе 10 модельных точностей —
        // одно событие (с наименьшим |F|)
        var merged = new List<double>();
        foreach (var t in outR)
        {
            if (merged.Count > 0 && (e.PointAt(t) - e.PointAt(merged[^1])).Length <= 10 * ToleranceContext.Current.ModelAccuracy)
            {
                if (Math.Abs(Ssi.Implicit(s, e.PointAt(t)).F) < Math.Abs(Ssi.Implicit(s, e.PointAt(merged[^1])).F)) merged[^1] = t;
                continue;
            }
            merged.Add(t);
        }
        // 06.1: КАСАНИЕ У КОНЦА РЕБРА. Дуга торца ленты кончается на линии касания, и поверхность другого операнда (плоскость
        // грани, касательная к ленте) касается её именно в конце: кратный корень находится с точностью ~√ε (3e-7 по параметру
        // от конца) — вершина-двойник в 2e-7 мм и ребро такой длины (OCCT: незамкнутая оболочка). Конец на поверхности (|F| ≤
        // 1e-10) и корень ближе 1e-6·размер к нему — это конец. Мутация P3D_EVENDSNAP=0.
        if (!EvEndSnapOff && merged.Count > 0)
        {
            double len = (e.PointAt(e.T1) - e.PointAt(e.T0)).Length + (e.PointAt((e.T0 + e.T1) / 2) - e.PointAt(e.T0)).Length;
            double near = 1e-6 * Math.Max(1, len);
            foreach (var te in new[] { e.T0, e.T1 })
            {
                var pe = e.PointAt(te);
                if (Math.Abs(Ssi.Implicit(s, pe).F) > 1e-10) continue;
                for (int i = 0; i < merged.Count; i++)
                    if (merged[i] != te && (e.PointAt(merged[i]) - pe).Length <= near) { merged[i] = te; Interlocked.Increment(ref EndSnaps); }
            }
            merged = merged.Distinct().OrderBy(t => t).ToList();
        }
        return merged;
    }

    /// <summary>Замер: рёбра-кривые пересечения со строгими событиями и без них (тогда — выборка и отказ маршрута).</summary>
    public static long TracedCertified, TracedUncertain;
    /// <summary>Замер: корни у конца ребра, отнесённые к концу (касание у конца).</summary>
    public static long EndSnaps;
    static readonly bool EvEndSnapOff = Environment.GetEnvironmentVariable("P3D_EVENDSNAP") == "0";
    /// <summary>Замер: неотделимые звенья у конца кривой пересечения, отнесённые к вершине.</summary>
    public static long TracedEndExempt;
    /// <summary>Мутация P3D_TRACEDEND=0: неотделимое звено у конца кривой — отказ (прежнее).</summary>
    static readonly bool TracedEndDbg = Environment.GetEnvironmentVariable("P3D_TENDDBG") == "1";
    static readonly bool TracedEndOff = Environment.GetEnvironmentVariable("P3D_TRACEDEND") == "0";
    /// <summary>Мутация P3D_CONTACTEXCL=0: без доказательства «линия касания шара поверхность не задевает» (отказ маршрута).</summary>
    static readonly bool ContactExclOff = Environment.GetEnvironmentVariable("P3D_CONTACTEXCL") == "0";
    static readonly bool RigorDbg = Environment.GetEnvironmentVariable("P3D_RIGORDBG") == "1";
    /// <summary>Последний вызов CurveSurface (в этом потоке) дал события выборкой по кривой пересечения — не доказано.</summary>
    [ThreadStatic] public static bool LastUncertain;
    [ThreadStatic] public static string? TracedWhy;
    /// <summary>Мутация P3D_TRACEDEVENTS=0: события на кривых пересечения — выборкой (прежнее), без отказа.</summary>
    internal static readonly bool TracedOff = Environment.GetEnvironmentVariable("P3D_TRACEDEVENTS") == "0";
    /// <summary>Мутация P3D_TRACEDSPLIT=0: звено без шара единственности — отказ сразу (прежнее), без деления.</summary>
    static readonly bool TracedSplitOff = Environment.GetEnvironmentVariable("P3D_TRACEDSPLIT") == "0";

    /// <summary>
    /// 04.2: СТРОГИЕ СОБЫТИЯ НА КРИВОЙ ПЕРЕСЕЧЕНИЯ (ребро — прослеженная кривая A ∩ B) с поверхностью s. Звено между
    /// соседними уточнёнными точками лежит в шаре единственности ветви (Ssi.Transversal: дуга A ∩ B в шаре одна и связна) —
    /// значит, дуга между ними целиком в шаре звена. В шаре: |F_s(центр)| > радиуса — событий нет (F_s — расстояние со знаком,
    /// Липшиц 1); иначе F_s монотонна вдоль дуги, если тройное произведение ∇F_s·(∇F_A × ∇F_B) в центре больше его
    /// наибольшего изменения по шару (повороты нормалей — NormalLip) — тогда событие одно при смене знака на концах (деление
    /// пополам по параметру кривой) и ни одного без неё; иначе звено делится пополам (точка кривой обязана лежать в шаре).
    /// Касание (не отделить до 1e-9 мм) и шар без доказательства — null: события не доказаны.
    /// </summary>
    static List<double>? TracedEvents(IntersectionCurve3 ic, Ssi.Shape s)
    {
        if (ExactEdges.ShapeOf(ic.A) is not { } sa || ExactEdges.ShapeOf(ic.B) is not { } sb) { TracedWhy = "shapes/ends"; return null; }
        var (P, T) = ic.RefinedWithParams();
        if (P.Length < 2) { TracedWhy = "shapes/ends"; return null; }
        double ext = 0; foreach (var p in P) ext = Math.Max(ext, (p - P[0]).Length);
        double tiny = 1e-9 * Math.Max(1, ext);
        // концы — на обеих поверхностях
        foreach (var p in new[] { P[0], P[^1] })
            if (Math.Abs(Ssi.Implicit(sa, p).F) > 1e-9 || Math.Abs(Ssi.Implicit(sb, p).F) > 1e-9) return null;
        var res = new List<double>(); bool ok = true; int budget = 200000;
        double Fs(Vector3d x) => Ssi.Implicit(s, x).F;
        // ОКРЕСТНОСТЬ КОНЦА: шов двух лент, касающихся общей грани, кончается в точке касания лент (A и B касаются — шара
        // единственности нет) и касается там плоскости грани. Неотделимое звено (≤ tiny) у конца кривой: событие в нём — в
        // пределах 1e-8·размер от вершины ребра, т. е. сама вершина (|F_s| в конце ≤ 1e-9 — событие в конце; иначе событий нет)
        bool EndExempt(double t0, double t1, Vector3d p0, Vector3d p1)
        {
            if (TracedEndOff) return false;
            if (TracedEndDbg) Console.Error.WriteLine($"TEND t {t0:R}..{t1:R} curve {T[0]:R}..{T[^1]:R} n {T.Length} A {sa.GetType().Name} B {sb.GetType().Name} s {s.GetType().Name} p0 {P[0]} p1 {P[^1]} ic {ic.T0:R}..{ic.T1:R}");
            double near = 1e-8 * Math.Max(1, ext);
            bool atStart = Math.Max((p0 - P[0]).Length, (p1 - P[0]).Length) <= near, atEnd = Math.Max((p0 - P[^1]).Length, (p1 - P[^1]).Length) <= near;
            if (!atStart && !atEnd) return false;
            Interlocked.Increment(ref TracedEndExempt);
            double te = atStart ? T[0] : T[^1]; var pe = atStart ? P[0] : P[^1];
            if (Math.Abs(Fs(pe)) <= 1e-9) res.Add(te);
            return true;
        }
        void Seg(double t0, double t1, Vector3d p0, Vector3d p1, int depth)
        {
            if (!ok) return;
            if (--budget < 0) { ok = false; return; }
            var m = (p0 + p1) / 2; double r = (p1 - p0).Length / 2 * 1.01 + 1e-12;
            if (!Ssi.Transversal(sa, sb, m, r))
            {
                if (TracedSplitOff) { ok = false; TracedWhy = "segment not transversal (no split)"; return; }
                // звено длинно для доказательства (часть кривой перестроена реже): точка кривой посередине — два звена; цепочка
                // звеньев, каждое в своём шаре единственности, через общую точку — одна связная ветвь
                if ((2 * r < tiny || depth > 40) && EndExempt(t0, t1, p0, p1)) return;
                if (2 * r < tiny || depth > 40) { ok = false; TracedWhy = $"segment not transversal at {m:0.###} r {r:G3}"; return; }
                double tq = (t0 + t1) / 2; var pq = ic.PointAt(tq);
                if (Math.Abs(Ssi.Implicit(sa, pq).F) > 1e-9 || Math.Abs(Ssi.Implicit(sb, pq).F) > 1e-9) { ok = false; TracedWhy = "mid point off the curve"; return; }
                Seg(t0, tq, p0, pq, depth + 1); Seg(tq, t1, pq, p1, depth + 1); return;
            }
            double fm = Fs(m);
            if (Math.Abs(fm) > r * (1 + 1e-12) + 1e-13) return;                  // событий нет
            if (Ssi.NormalLip(s, m, r) is double ks && Ssi.NormalLip(sa, m, r) is double ka && Ssi.NormalLip(sb, m, r) is double kb)
            {
                var gs = Ssi.Implicit(s, m).G; var ga = Ssi.Implicit(sa, m).G; var gb = Ssi.Implicit(sb, m).G;
                double trip = Vector3d.Dot(gs, Vector3d.Cross(ga, gb));
                double da = Math.Min(2, ka * r), db = Math.Min(2, kb * r), dsv = Math.Min(2, ks * r);
                double err = dsv * (1 + da) * (1 + db) + da * (1 + db) + db + da * db;
                if (Math.Abs(trip) > err * (1 + 1e-9) + 1e-12)
                {
                    double f0 = Fs(p0), f1 = Fs(p1);
                    if (f0 == 0) { res.Add(t0); return; }
                    if (f1 == 0) { res.Add(t1); return; }
                    if (Math.Sign(f0) == Math.Sign(f1)) return;                     // монотонна, без смены знака — нет
                    double lo = t0, hi = t1, flo = f0;
                    for (int k = 0; k < 100 && hi - lo > 1e-16; k++)
                    {
                        double mid = (lo + hi) / 2, fmid = Fs(ic.PointAt(mid));
                        if (Math.Sign(fmid) == Math.Sign(flo)) { lo = mid; flo = fmid; } else hi = mid;
                    }
                    res.Add((lo + hi) / 2); return;
                }
            }
            if ((2 * r < tiny || depth > 60) && EndExempt(t0, t1, p0, p1)) return;
            if (2 * r < tiny || depth > 60) { ok = false; TracedWhy = $"tangency at {m:0.###}"; return; }                  // касание: не отделить
            double tm = (t0 + t1) / 2; var q = ic.PointAt(tm);
            if ((q - m).Length > r) { ok = false; TracedWhy = "point outside ball"; return; }                         // точка кривой вне шара звена
            Seg(t0, tm, p0, q, depth + 1); Seg(tm, t1, q, p1, depth + 1);
        }
        for (int i = 0; i + 1 < P.Length && ok; i++) Seg(T[i], T[i + 1], P[i], P[i + 1], 0);
        return ok ? res : null;
    }

    /// <summary>
    /// Строгий корень — тем же делением пополам на интервале сетки выборки, где он лежит (если там смена знака): то же значение
    /// до бита, что давала выборка (последующие построения сравнивают точки побитово — у плоскости z = 0 знак нуля менял
    /// разметку грани). Два корня в одном интервале (смены знака нет) — строгий корень как есть.
    /// </summary>
    static double OnSampleGrid(ICurve3 e, Ssi.Shape s, double t)
    {
        double len = 0; var prev = e.PointAt(e.T0); for (int i = 1; i <= 64; i++) { var q = e.PointAt(e.T0 + (e.T1 - e.T0) * i / 64); len += (q - prev).Length; prev = q; }
        int n = Math.Clamp((int)Math.Ceiling(len * 40), 512, 8192);
        double T(int i) => e.T0 + (e.T1 - e.T0) * i / n;
        int k = Math.Clamp((int)Math.Floor((t - e.T0) / (e.T1 - e.T0) * n), 0, n - 1);
        double f0 = Ssi.Implicit(s, e.PointAt(T(k))).F, f1 = Ssi.Implicit(s, e.PointAt(T(k + 1))).F;
        if (f0 == 0) return T(k);
        if (!(Math.Sign(f0) != Math.Sign(f1) && f1 != 0)) return t;
        double lo = T(k), hi = T(k + 1), flo = f0;
        for (int it = 0; it < 100 && hi - lo > 1e-16 * Math.Max(1, e.T1 - e.T0); it++)
        {
            double m = (lo + hi) / 2, fm = Ssi.Implicit(s, e.PointAt(m)).F;
            if (Math.Sign(fm) == Math.Sign(flo)) { lo = m; flo = fm; } else hi = m;
        }
        return (lo + hi) / 2;
    }

    static double Polish(ICurve3 e, Ssi.Shape s, double t)
    {
        double span = Math.Max(1e-300, e.T1 - e.T0), h = 1e-7 * span;
        double F(double u) => Ssi.Implicit(s, e.PointAt(Math.Clamp(u, e.T0, e.T1))).F;
        double f = F(t);
        for (int it = 0; it < 30 && Math.Abs(f) > 1e-14; it++)
        {
            double df = (F(t + h) - F(t - h)) / (2 * h);
            if (Math.Abs(df) < 1e-14) break;                    // касание — корень многочлена остаётся
            double t2 = Math.Clamp(t - f / df, e.T0, e.T1);
            if (Math.Abs(t2 - t) > 1e-3 * span) break;          // уход — не тот корень
            double f2 = F(t2);
            if (Math.Abs(f2) >= Math.Abs(f)) break;
            t = t2; f = f2;
        }
        return t;
    }

    /// <summary>Выборка для рёбер без алгебраического решения: смены знака (бисекция) и локальные минимумы |F| (касание).</summary>
    static IEnumerable<double> Sampled(ICurve3 e, Ssi.Shape s)
    {
        double len = 0; var prev = e.PointAt(e.T0); for (int i = 1; i <= 64; i++) { var q = e.PointAt(e.T0 + (e.T1 - e.T0) * i / 64); len += (q - prev).Length; prev = q; }
        // не реже 512 и 40 на мм, но не больше 8192 точек: деталь в масштабе ×1000 (метаморфная проверка KRN-02) давала по
        // 2.4 млн точек кривой пересечения на ребро — каждая с уточнением (прогон набора не завершался)
        int n = Math.Clamp((int)Math.Ceiling(len * 40), 512, 8192);
        var f = new double[n + 1];
        for (int i = 0; i <= n; i++) f[i] = Ssi.Implicit(s, e.PointAt(e.T0 + (e.T1 - e.T0) * i / n)).F;
        double T(int i) => e.T0 + (e.T1 - e.T0) * i / n;
        for (int i = 0; i < n; i++)
        {
            if (f[i] == 0) { yield return T(i); continue; }
            if (Math.Sign(f[i]) != Math.Sign(f[i + 1]) && f[i + 1] != 0)
            {
                double lo = T(i), hi = T(i + 1), flo = f[i];
                for (int k = 0; k < 100 && hi - lo > 1e-16 * Math.Max(1, e.T1 - e.T0); k++)
                {
                    double m = (lo + hi) / 2, fm = Ssi.Implicit(s, e.PointAt(m)).F;
                    if (Math.Sign(fm) == Math.Sign(flo)) { lo = m; flo = fm; } else hi = m;
                }
                yield return (lo + hi) / 2;
            }
            else if (i > 0 && Math.Abs(f[i]) < Math.Abs(f[i - 1]) && Math.Abs(f[i]) <= Math.Abs(f[i + 1]) && Math.Abs(f[i]) < 1e-3)
                yield return T(i);                                // близко к касанию: уточнение решит
        }
        if (f[n] == 0) yield return e.T1;
    }

    /// <summary>
    /// Все вещественные корни на [−1, 1] функции, которая ЯВЛЯЕТСЯ многочленом степени ≤ deg: коэффициенты — интерполяцией
    /// в узлах Чебышёва, корни — отделением по критическим точкам (рекурсивно по производным), кратные — по малости значения
    /// в критической точке.
    /// </summary>
    internal static List<double> PolyRootsOn(Func<double, double> g, int deg)
    {
        int n = deg + 1;
        var xs = new double[n]; var ys = new double[n];
        for (int i = 0; i < n; i++) { xs[i] = Math.Cos(Math.PI * (i + 0.5) / n); ys[i] = g(xs[i]); }
        var c = Interpolate(xs, ys);
        double scale = 0; foreach (var v in c) scale = Math.Max(scale, Math.Abs(v));
        if (scale == 0) return new List<double>();
        var roots = Roots(c, scale, 0);
        return roots;
    }

    /// <summary>Коэффициенты многочлена (по возрастанию степени) через узлы — Ньютон, затем перевод в мономы.</summary>
    static double[] Interpolate(double[] x, double[] y)
    {
        int n = x.Length; var a = (double[])y.Clone();
        for (int j = 1; j < n; j++) for (int i = n - 1; i >= j; i--) a[i] = (a[i] - a[i - 1]) / (x[i] - x[i - j]);
        var c = new double[n];
        for (int k = n - 1; k >= 0; k--)
        {
            // c = c·(X − x_k) + a_k
            for (int i = n - 1; i >= 1; i--) c[i] = c[i - 1] - x[k] * c[i];
            c[0] = -x[k] * c[0] + a[k];
        }
        return c;
    }

    static double Eval(double[] c, double x) { double r = 0; for (int i = c.Length - 1; i >= 0; i--) r = r * x + c[i]; return r; }

    static List<double> Roots(double[] c, double scale, int depth)
    {
        int deg = c.Length - 1;
        while (deg > 0 && Math.Abs(c[deg]) <= 1e-14 * scale) deg--;
        var res = new List<double>();
        if (deg <= 0) return res;
        if (deg == 1) { double r = -c[0] / c[1]; if (r >= -1 - 1e-12 && r <= 1 + 1e-12) res.Add(Math.Clamp(r, -1, 1)); return res; }
        var dc = new double[deg]; for (int i = 1; i <= deg; i++) dc[i - 1] = i * c[i];
        var cs = new double[deg + 1]; Array.Copy(c, cs, deg + 1);
        var crit = Roots(dc, scale * deg, depth + 1);
        var pts = new List<double> { -1 }; pts.AddRange(crit.Where(x => x > -1 && x < 1)); pts.Add(1);
        double tiny = 1e-11 * scale;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double lo = pts[i], hi = pts[i + 1], flo = Eval(cs, lo), fhi = Eval(cs, hi);
            if (Math.Abs(flo) <= tiny) { if (depth == 0) Interlocked.Increment(ref TangentEvents); res.Add(lo); continue; }
            if (Math.Sign(flo) == Math.Sign(fhi) || fhi == 0) continue;
            for (int k = 0; k < 200 && hi - lo > 1e-16; k++)
            {
                double m = (lo + hi) / 2, fm = Eval(cs, m);
                if (Math.Sign(fm) == Math.Sign(flo)) { lo = m; flo = fm; } else hi = m;
            }
            res.Add((lo + hi) / 2);
        }
        if (Math.Abs(Eval(cs, 1)) <= tiny) res.Add(1);
        return res;
    }
}
