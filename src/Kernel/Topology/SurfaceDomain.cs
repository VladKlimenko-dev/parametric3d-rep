using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// KRN-03. ОБЛАСТЬ ПАРАМЕТРОВ ПОВЕРХНОСТИ — правила карт, общие для всех операций (не для разметчика).
///
/// Поверхность вращения с полным оборотом имеет ПОЛЮСЫ — точки образующей на оси (у сферы — оба конца меридиана, у конуса —
/// вершина). В полюсе параметр угла не определён: кривая, проходящая через полюс, в параметрах рвётся на половину периода,
/// и непрерывной p-кривой у неё нет (замер: сфера операнда с осью Z, грани бруска через центр — меридианы через полюс,
/// p-кривая Эрмита отходит от ребра на 6e-3…2e-2 мм при бюджете 1e-7).
///
/// КОНТРАКТ КАРТЫ ГРАНИ: полюс карты не лежит на ребре грани — ни одна точка внутри ребра не ближе MinFeature к полюсу.
/// Полюс допустим (а) вне грани, (б) внутри грани (шапка: полюс в её центре; область вокруг полюса строится отдельно),
/// (в) в ВЕРШИНЕ грани, стоящей точно в полюсе (законное вырождение: долька сферы, вершина конуса).
/// Сферическая грань, у которой полюс неизбежно лежит на ребре в данной карте, получает другую карту той же сферы
/// (<see cref="SphereChart"/>): «набор локальных карт» инструкции. Нарушение ловит проверка B-rep (pole-on-boundary).
/// </summary>
public static class SurfaceDomain
{
    /// <summary>Полюсы поверхности: у поверхности вращения с полным оборотом — концы образующей на оси.</summary>
    public static List<Vector3d> Poles(ISurface3? s)
    {
        var res = new List<Vector3d>();
        if (s is not RevolvedSurface rv || Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) > 1e-9) return res;
        var w = rv.Axis.W.Normalized();
        foreach (double t in new[] { rv.Base.T0, rv.Base.T1 })
        {
            var p = rv.Base.PointAt(t); var d = p - rv.Axis.Origin;
            if ((d - w * Vector3d.Dot(d, w)).Length < 1e-9 * Math.Max(1, p.Length)) res.Add(p);
        }
        return res;
    }

    /// <summary>
    /// Наименьшее расстояние от полюсов карты до ВНУТРЕННИХ точек рёбер грани (мм); +∞ — полюсов нет. Участки ребра у его
    /// конца, стоящего точно в полюсе (вершина в полюсе — законно), не учитываются: исключение радиуса 2·MinFeature.
    /// Минимум — выборкой по точной кривой ребра с уточнением золотым сечением.
    /// </summary>
    public static double PoleClearance(TFace f, ISurface3? s = null)
    {
        s ??= GeometryAudit.AuthoritativeSurface(f);
        var poles = Poles(s);
        if (poles.Count == 0) return double.PositiveInfinity;
        double mf = ToleranceContext.Current.MinFeature, acc = ToleranceContext.Current.ModelAccuracy;
        double best = double.PositiveInfinity;
        var edges = f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge).Distinct().ToList();
        foreach (var e in edges)
        {
            var c = e.Curve; if (c == null) continue;
            foreach (var pole in poles)
            {
                var a = c.PointAt(c.T0); var b = c.PointAt(c.T1);
                bool atA = (a - pole).Length <= Math.Max(10 * acc, 1e-9), atB = (b - pole).Length <= Math.Max(10 * acc, 1e-9);
                bool Excluded(Vector3d q) => (atA && (q - a).Length < 2 * mf) || (atB && (q - b).Length < 2 * mf);
                double D(double t) { var q = c.PointAt(t); return Excluded(q) ? double.PositiveInfinity : (q - pole).Length; }
                const int n = 128;
                int bi = -1; double bd = double.PositiveInfinity;
                for (int i = 0; i <= n; i++) { double d = D(c.T0 + (c.T1 - c.T0) * i / n); if (d < bd) { bd = d; bi = i; } }
                if (bi < 0) continue;
                double lo = c.T0 + (c.T1 - c.T0) * Math.Max(0, bi - 1) / n, hi = c.T0 + (c.T1 - c.T0) * Math.Min(n, bi + 1) / n;
                const double g = 0.6180339887498949;
                double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = D(x1), f2 = D(x2);
                for (int k = 0; k < 80 && hi - lo > 1e-15 * Math.Max(1, Math.Abs(hi)); k++)
                {
                    if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = D(x1); }
                    else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = D(x2); }
                }
                best = Math.Min(best, Math.Min(bd, Math.Min(f1, f2)));
            }
        }
        return best;
    }

    /// <summary>Карта сферы (центр C, радиус R) с осью <paramref name="axis"/>: меридиан от −axis к +axis, полный оборот.</summary>
    public static RevolvedSurface SphereChart(Vector3d C, double R, Vector3d axis)
    {
        var ax = axis.Normalized();
        var e = Vector3d.Cross(ax, Math.Abs(ax.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
        var baseArc = new ArcCurve3(new Frame3(C, e, ax), R, -Math.PI / 2, Math.PI / 2);
        return new RevolvedSurface(baseArc, new Frame3(C, e, Vector3d.Cross(ax, e)), 0, 2 * Math.PI);
    }

    /// <summary>
    /// Ось карты сферической грани по её области: шапка, ограниченная одной окружностью, — ось по нормали плоскости
    /// окружности в сторону шапки (полюс в центре шапки, край — линия широты; у шапки больше полусферы полюс внутри грани
    /// неизбежен при любой оси); прочие — <paramref name="away"/>: направление, наиболее удалённое от точек грани (полюс вне
    /// грани с запасом; замер корпуса — не меньше 84°).
    /// </summary>
    public static Vector3d SphereAxis(ArcCurve3? singleRim, Vector3d inside, Vector3d away)
    {
        if (singleRim != null)
        {
            var nrm = singleRim.Frame.W.Normalized();
            return Vector3d.Dot(inside - singleRim.Frame.Origin, nrm) > 0 ? nrm : -nrm;
        }
        return away.Normalized();
    }

    /// <summary>
    /// Полюс стереографической карты сферической грани ВНЕ грани — только по B-rep (точные кривые рёбер, обход контуров),
    /// без фасетов: направление, наиболее удалённое от рёбер, вокруг которого контуры грани не обходят (суммарное число
    /// оборотов в проекции из противоположной точки — 0; обход полурёбер — материал слева относительно нормали грани).
    /// null — у грани нет рёбер (полная сфера) или подходящего направления нет.
    /// </summary>
    /// <summary>
    /// g.05: СКОЛЬКО ПОЛЮСОВ КАРТЫ СФЕРИЧЕСКОЙ ГРАНИ ЛЕЖИТ ВНУТРИ НЕЁ (по B-rep: обход контуров, проекция из противоположной
    /// точки). Грань-диск с обоими полюсами внутри не размечается шапкой (кольца от края к одному полюсу) — разметчик брал
    /// дополнение, объём выходил отрицательным (шар со смещённым центром на стенке цилиндра: все 24 положения по кругу —
    /// отказ маршрута). −1 — не решено.
    /// </summary>
    public static int PolesInside(TFace f, Vector3d C)
    {
        var s = GeometryAudit.AuthoritativeSurface(f);
        var poles = Poles(s);
        if (poles.Count == 0) return 0;
        var (loops, side) = SphereLoops(f, C);
        if (loops.Count == 0) return -1;
        int n = 0;
        foreach (var p in poles)
        {
            var ins = InsideFace(loops, C, (p - C).Normalized(), side);
            if (ins == null) return -1;
            if (ins == true) n++;
        }
        return n;
    }

    static (List<List<Vector3d>> Loops, double Side) SphereLoops(TFace f, Vector3d C)
    {
        var loops = new List<List<Vector3d>>();
        var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
        foreach (var l in f.Loops)
        {
            var pts = new List<Vector3d>();
            foreach (var ce in l.CoEdges)
            {
                if (uses[ce.Edge] > 1) continue;
                for (int i = 0; i < PoleSamples; i++) pts.Add(PCurves.At(ce, i / (double)PoleSamples));
            }
            if (pts.Count >= 3) loops.Add(pts);
        }
        if (loops.Count == 0) return (loops, 1);
        var p0 = loops[0][0];
        return (loops, AnalyticVolume.SphereSideByHint(f, C, (loops[0][0] - C).Length) ?? (Vector3d.Dot(f.NormalAt(p0), p0 - C) >= 0 ? 1 : -1));
    }

    public static Vector3d? SpherePoleOutside(TFace f, Vector3d C, double R)
    {
        var loops = new List<List<Vector3d>>();
        var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
        foreach (var l in f.Loops)
        {
            var pts = new List<Vector3d>();
            foreach (var ce in l.CoEdges)
            {
                if (uses[ce.Edge] > 1) continue;                    // шов — не край
                for (int i = 0; i < PoleSamples; i++) pts.Add(PCurves.At(ce, i / (double)PoleSamples));
            }
            if (pts.Count >= 3) loops.Add(pts);
        }
        if (loops.Count == 0) return null;
        // сторона: нормаль грани наружу от центра — знак +1
        var p0 = loops[0][0];
        double side = AnalyticVolume.SphereSideByHint(f, C, R) ?? (Vector3d.Dot(f.NormalAt(p0), p0 - C) >= 0 ? 1 : -1);   // g.05: по точке грани (P3D_SPHHINTSIDE)
        var dirs = loops.SelectMany(l => l).Select(p => (p - C).Normalized()).ToList();
        Vector3d? best = null; double bestCos = 2;
        const int N = 600; double ga = Math.PI * (3 - Math.Sqrt(5));
        double maxStep = MaxStep(loops, C);
        if (PoleFastForTest ?? !PoleFastOff)
        {
            // KRN-10: кандидаты — по возрастанию близости к краю; первый, лежащий вне грани, и есть прежний выбор (наименьшая
            // близость, при равенстве — меньший номер). Прежде проверка «внутри грани» шла для каждого улучшения: у
            // сферического угла скругления — сотни проверок по 576 точкам (60–100 мс на грань). Мутация P3D_POLEFAST=0.
            var cand = new List<(double Near, int I, Vector3d D)>(N);
            for (int i = 0; i < N; i++)
            {
                double z = 1 - 2 * (i + 0.5) / N, r = Math.Sqrt(1 - z * z);
                var d = new Vector3d(r * Math.Cos(ga * i), r * Math.Sin(ga * i), z);
                double near = double.MinValue; foreach (var x in dirs) { double c = Vector3d.Dot(x, d); if (c > near) near = c; }
                cand.Add((near, i, d));
            }
            cand.Sort((a, b) => a.Near != b.Near ? a.Near.CompareTo(b.Near) : a.I.CompareTo(b.I));
            foreach (var (near, _, d) in cand)
            {
                PoleInsideTests++;
                if (InsideFace(loops, C, d, side, maxStep) == false) { best = d; break; }
            }
        }
        else
        for (int i = 0; i < N; i++)
        {
            double z = 1 - 2 * (i + 0.5) / N, r = Math.Sqrt(1 - z * z);
            var d = new Vector3d(r * Math.Cos(ga * i), r * Math.Sin(ga * i), z);
            double near = dirs.Max(x => Vector3d.Dot(x, d));        // косинус до ближайшей точки края
            if (near >= bestCos) continue;
            PoleInsideTests++;
            if (InsideFace(loops, C, d, side, maxStep) != false) continue;    // внутри грани или не решено
            bestCos = near; best = d;
        }
        if (Environment.GetEnvironmentVariable("P3D_POLEDBG") == "1")
            Console.Error.WriteLine($"POLEDBG face {f.Id} loops {loops.Count} pts {loops.Sum(l => l.Count)} side {side} best {best}");
        return best;
    }

    /// <summary>
    /// Лежит ли направление d внутри грани. Всё — в одной плоскости: стереографическая проекция из −d (d → начало координат,
    /// ориентация по наружной нормали в d сохраняется). Ломаная контуров (обход со стороной грани) обходит точки области с
    /// числом оборотов 1, если область ограничена (−d вне неё), и 0, если область содержит −d («бесконечность»). Точка
    /// области X — у середины первого звена ломаной, сдвинутая влево (материал слева), в той же плоскости: она по нужную
    /// сторону ИМЕННО ЭТОЙ ломаной. null — не решено (контур через −d).
    /// </summary>
    static readonly bool PoleClearOff = Environment.GetEnvironmentVariable("P3D_POLECLEAR") == "0";
    static int PoleSamples => PoleClearOff ? 48 : 192;
    static readonly bool PoleFastOff = Environment.GetEnvironmentVariable("P3D_POLEFAST") == "0";
    /// <summary>Сколько проверок «направление внутри грани» сделал выбор полюса карты (диагностика скорости).</summary>
    public static long PoleInsideTests;
    /// <summary>Проверка: сравнить быстрый выбор полюса с прежним перебором на тех же гранях.</summary>
    [ThreadStatic] public static bool? PoleFastForTest;

    /// <summary>Наибольший угловой шаг ломаной контуров (от d не зависит — считается один раз на грань).</summary>
    static double MaxStep(List<List<Vector3d>> loops, Vector3d C)
    {
        double maxStep = 0;
        foreach (var l in loops)
            for (int i = 0; i < l.Count; i++)
            {
                var u1 = (l[i] - C).Normalized(); var u2 = (l[(i + 1) % l.Count] - C).Normalized();
                maxStep = Math.Max(maxStep, Math.Acos(Math.Clamp(Vector3d.Dot(u1, u2), -1, 1)));
            }
        return maxStep;
    }

    static bool? InsideFace(List<List<Vector3d>> loops, Vector3d C, Vector3d d, double side, double? maxStepKnown = null)
    {
        var u0 = Math.Abs(d.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
        var e1 = (u0 - d * Vector3d.Dot(u0, d)).Normalized(); var e2 = Vector3d.Cross(d, e1);
        // g.05: центр проекции −d должен отстоять от ломаной контура на несколько её звеньев: у самого контура ломаная уходит к
        // «бесконечности» хордами, которые не повторяют окружность, — число оборотов ложное, и полюс карты выбирался ВНУТРИ
        // грани (шар − шар, серия 8, случай 158: классификатор результата расходился с CSG в 136 точках из 256).
        // Мутация P3D_POLECLEAR=0.
        if (!PoleClearOff)
        {
            double maxStep = maxStepKnown ?? MaxStep(loops, C), maxCos = -1;
            foreach (var l in loops)
                foreach (var p in l) maxCos = Math.Max(maxCos, -Vector3d.Dot((p - C).Normalized(), d));
            // minAng < 4·maxStep  ⇔  cos(minAng) > cos(4·maxStep) (при 4·maxStep < π)
            if (4 * maxStep >= Math.PI || maxCos > Math.Cos(4 * maxStep)) return null;
        }
        var proj = new List<List<Vector2d>>();
        foreach (var l in loops)
        {
            var pl = new List<Vector2d>();
            foreach (var p in l)
            {
                var u = (p - C).Normalized(); double den = 1 + Vector3d.Dot(u, d);
                if (den < 1e-12) return null;
                pl.Add(new Vector2d(Vector3d.Dot(u, e1) / den, Vector3d.Dot(u, e2) / den));
            }
            proj.Add(pl);
        }
        int W(Vector2d x)
        {
            double tot = 0;
            foreach (var pl in proj)
                for (int i = 0; i < pl.Count; i++)
                {
                    var a = pl[i] - x; var b = pl[(i + 1) % pl.Count] - x;
                    tot += Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);
                }
            return (int)Math.Round(side * tot / (2 * Math.PI));
        }
        var q0 = proj[0][0]; var q1 = proj[0][1]; var t = q1 - q0; double len = t.Length;
        if (len < 1e-15) return null;
        var leftDir = new Vector2d(-t.Y, t.X) * (side / len);           // поворот на +90° (при стороне −1 — на −90°)
        var X = (q0 + q1) / 2 + leftDir * (1e-6 * len);
        int wx = W(X), wd = W(Vector2d.Zero);
        return wx == 1 ? wd == 1 : wd == 0;
    }

}
