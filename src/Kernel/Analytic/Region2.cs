using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Analytic;

// ПЕРЕХОД НА АНАЛИТИЧЕСКОЕ ЯДРО, ЭТАП 1 (фундамент). Плоская область, ограниченная отрезками и дугами окружностей, и её
// булевы операции. Сечение «2.5-мерного» тела (боковые грани — плоскости и цилиндры вдоль общего направления, торцы —
// поперёк) плоскостью поперёк направления — такая область; булева тел сводится к булевым сечений по слоям.
//
// Геометрия точная (отрезки и окружности — сами по себе, без гранения); решения о совпадении точек — с допуском Eps,
// как в промышленных ядрах. Ориентация: наружный контур — против часовой стрелки, отверстие — по часовой; область — слева.

/// <summary>Кусок границы: отрезок или дуга окружности (центр, радиус, начальный угол, угол обхода со знаком).</summary>
public readonly record struct Seg2(Vector2d A, Vector2d B, bool IsArc, Vector2d C, double R, double T0, double Sweep)
{
    public static Seg2 Line(Vector2d a, Vector2d b) => new(a, b, false, default, 0, 0, 0);

    /// <summary>Дуга с центром c, радиусом r, от угла t0 на sweep (sweep &gt; 0 — против часовой).</summary>
    public static Seg2 Arc(Vector2d c, double r, double t0, double sweep) =>
        new(c + r * new Vector2d(Math.Cos(t0), Math.Sin(t0)), c + r * new Vector2d(Math.Cos(t0 + sweep), Math.Sin(t0 + sweep)), true, c, r, t0, sweep);

    public Vector2d At(double u) => IsArc
        ? C + R * new Vector2d(Math.Cos(T0 + u * Sweep), Math.Sin(T0 + u * Sweep))
        : A + (B - A) * u;

    /// <summary>Касательная (не нормированная) в параметре u.</summary>
    public Vector2d Tangent(double u)
    {
        if (!IsArc) return B - A;
        double t = T0 + u * Sweep;
        return new Vector2d(-Math.Sin(t), Math.Cos(t)) * (R * Sweep);
    }

    public Seg2 Reversed() => IsArc ? new Seg2(B, A, true, C, R, T0 + Sweep, -Sweep) : Line(B, A);

    /// <summary>Часть от u0 до u1 (концы — точно в заданных точках, если переданы).</summary>
    public Seg2 Part(double u0, double u1, Vector2d? p0 = null, Vector2d? p1 = null) => IsArc
        ? new Seg2(p0 ?? At(u0), p1 ?? At(u1), true, C, R, T0 + u0 * Sweep, (u1 - u0) * Sweep)
        : Line(p0 ?? At(u0), p1 ?? At(u1));

    /// <summary>½∮(x dy − y dx) вдоль куска — вклад в ориентированную площадь (точно для дуги).</summary>
    public double AreaTerm()
    {
        if (!IsArc) return (A.X * B.Y - A.Y * B.X) / 2;
        double t1 = T0 + Sweep;
        return (R * R * Sweep + R * (C.X * (Math.Sin(t1) - Math.Sin(T0)) - C.Y * (Math.Cos(t1) - Math.Cos(T0)))) / 2;
    }

    /// <summary>
    /// Тот же вклад относительно начала o: у щепки вдали от начала координат вклады x·dy порядка |p|² гасят друг друга,
    /// и шум округления (~|p|²·ε) больше её настоящей площади; разность синусов — через полусумму (малый угол без потерь).
    /// </summary>
    public double AreaTerm(Vector2d o)
    {
        if (!IsArc) { var a = A - o; var b = B - o; return (a.X * b.Y - a.Y * b.X) / 2; }
        var c = C - o; double h = Sweep / 2, m = T0 + h, sh = Math.Sin(h);
        double ds = 2 * Math.Cos(m) * sh, dc = -2 * Math.Sin(m) * sh;
        return (R * R * Sweep + R * (c.X * ds - c.Y * dc)) / 2;
    }

    public double Length => IsArc ? Math.Abs(Sweep) * R : (B - A).Length;
}

/// <summary>Область: набор замкнутых контуров (наружные — против часовой, отверстия — по часовой).</summary>
public sealed class Region2
{
    public readonly List<List<Seg2>> Loops = new();

    public double Area => Loops.Sum(l => l.Sum(s => s.AreaTerm()));

    public static Region2 Rect(double x0, double y0, double x1, double y1)
    {
        var r = new Region2();
        r.Loops.Add(new List<Seg2> { Seg2.Line(new(x0, y0), new(x1, y0)), Seg2.Line(new(x1, y0), new(x1, y1)),
                                     Seg2.Line(new(x1, y1), new(x0, y1)), Seg2.Line(new(x0, y1), new(x0, y0)) });
        return r;
    }

    /// <summary>Круг из двух полуокружностей (против часовой стрелки).</summary>
    public static Region2 Circle(Vector2d c, double r)
    {
        var g = new Region2();
        g.Loops.Add(new List<Seg2> { Seg2.Arc(c, r, 0, Math.PI), Seg2.Arc(c, r, Math.PI, Math.PI) });
        return g;
    }

    public Region2 Clone() { var g = new Region2(); foreach (var l in Loops) g.Loops.Add(new List<Seg2>(l)); return g; }

    /// <summary>
    /// Точка внутри (по чётности пересечений луча; на границе — не определено). Луч — НЕ ПО ОСИ, под «общим» углом:
    /// детали строятся вдоль осей, и луч +X попадал ровно в вершины (стык двух полуокружностей при угле 0), где решение
    /// зависело от ошибки округления sin(−2π) = 2.4e−16 — точка внутри круга считалась внешней. Под общим углом вершина
    /// на луче — лишь случайное совпадение.
    /// </summary>
    public bool Contains(Vector2d p)
    {
        var dir = new Vector2d(Math.Cos(RayAngle), Math.Sin(RayAngle));
        int crossings = 0;
        foreach (var loop in Loops)
            foreach (var s in loop)
            {
                if (!s.IsArc)
                {
                    // p + t·dir = A + u·(B − A), t > 0, 0 ≤ u < 1
                    var e = s.B - s.A; double den = dir.X * e.Y - dir.Y * e.X;
                    if (Math.Abs(den) < 1e-300) continue;
                    var q = s.A - p;
                    double t = (q.X * e.Y - q.Y * e.X) / den, u = (q.X * dir.Y - q.Y * dir.X) / den;
                    if (t > 0 && u >= 0 && u < 1) crossings++;
                    continue;
                }
                // |p + t·dir − C|² = R²
                var f = p - s.C; double bq = Vector2d.Dot(f, dir), cq = f.LengthSquared - s.R * s.R, disc = bq * bq - cq;
                if (disc <= 0) continue;
                double sq = Math.Sqrt(disc);
                foreach (double t in new[] { -bq - sq, -bq + sq })
                {
                    if (t <= 0) continue;
                    var x = p + dir * t;
                    if (!Region2Ops.OnArc(s, Math.Atan2(x.Y - s.C.Y, x.X - s.C.X), out double uu)) continue;
                    if (uu >= 0 && uu < 1) crossings++;
                }
            }
        return (crossings & 1) == 1;
    }

    /// <summary>Угол луча проверки «внутри»: общий (не кратный долям π), чтобы вершины на луч не попадали.</summary>
    const double RayAngle = 0.3709824417;
}

public enum Bool2 { Union, Intersect, Subtract }

public static class Region2Ops
{
    /// <summary>
    /// Допуск совпадения точек, мм. Соизмерим с сеткой точного ядра (1e-5 мм): вершины тел после булевой лежат в её
    /// узлах, а дуги восстановлены подгонкой — концы дуги и вершины соседних отрезков расходятся до ~1e-5 мм.
    /// </summary>
    public const double LegacyEps = 2e-5;

    /// <summary>
    /// KRN-02: ДОПУСК ОПЕРАЦИИ — max(модельная точность, измеренный допуск операндов) на время булевой этапа 1 (BooleanOp);
    /// вне операции — прежние 2e-5 мм (LegacyEps).
    /// </summary>
    [ThreadStatic] internal static double? EpsOverride;
    /// <summary>Разбор: задать допуск операции снаружи (null — по умолчанию).</summary>
    public static double? EpsOverrideForTests { get => EpsOverride; set => EpsOverride = value; }
    public static double Eps => EpsOverride ?? LegacyEps;

    /// <summary>Лежит ли угол t на дуге s; u — параметр (0…1).</summary>
    public static bool OnArc(Seg2 s, double t, out double u)
    {
        double d = t - s.T0;
        // привести d к знаку обхода и диапазону [0, 2π)
        double twoPi = 2 * Math.PI;
        if (s.Sweep >= 0) { d %= twoPi; if (d < 0) d += twoPi; u = d / s.Sweep; }
        else { d = -d; d %= twoPi; if (d < 0) d += twoPi; u = d / -s.Sweep; }
        if (u > 1 && u * Math.Abs(s.Sweep) > twoPi - 1e-12) u -= twoPi / Math.Abs(s.Sweep);   // у начала с другой стороны
        return u >= -1e-12 && u <= 1 + 1e-12;
    }

    /// <summary>Параметр точки p на куске s (p лежит на s).</summary>
    public static double ParamOf(Seg2 s, Vector2d p)
    {
        if (!s.IsArc)
        {
            var d = s.B - s.A; double l2 = d.LengthSquared;
            return l2 == 0 ? 0 : Vector2d.Dot(p - s.A, d) / l2;
        }
        OnArc(s, Math.Atan2(p.Y - s.C.Y, p.X - s.C.X), out double u);
        return u;
    }

    /// <summary>
    /// KRN-02: численная граница различимости двойного корня: у касания полухорда — шум порядка √(64ε)·S (S — характерный
    /// размер: радиусы, расстояние центров); меньше неё две точки пересечения в double неразличимы.
    /// </summary>
    internal static double TangencyBound(double scale) => Math.Sqrt(64 * 2.220446049250313e-16) * scale;

    /// <summary>Точки пересечения двух кусков (в т. ч. концы совпадающих участков).</summary>
    public static List<Vector2d> Intersections(Seg2 a, Seg2 b)
    {
        var res = new List<Vector2d>();
        void Add(Vector2d p)
        {
            if (a.IsArc ? !OnArcPoint(a, p) : !OnLinePoint(a, p)) return;
            if (b.IsArc ? !OnArcPoint(b, p) : !OnLinePoint(b, p)) return;
            if (res.All(q => (q - p).Length > Eps)) res.Add(p);
        }
        // KRN-02: КАСАНИЕ — положение точки касания вдоль касательной не определено точнее √(2·R·τ) (вдоль касательной
        // расстояние до окружности растёт квадратично): вершина-конец куска в этих пределах — та же точка касания в пределах
        // модельной точности, и берётся она (вершины авторитетны), а не вычисленная (резец скругления: конец дуги и
        // основание перпендикуляра расходились на 1.6e-7 мм вдоль стороны, и при допуске 1e-7 контур сечения рвался).
        void AddTangency(Vector2d p, double r)
        {
            double along = Math.Sqrt(2 * Math.Max(r, 0) * Eps);
            if (res.Any(q => (q - p).Length <= Math.Max(Eps, along))) return;
            Add(p);
        }
        // концы одного на другом (совпадающие участки, касания в вершине)
        foreach (var p in new[] { a.A, a.B }) Add(p);
        foreach (var p in new[] { b.A, b.B }) Add(p);

        if (!a.IsArc && !b.IsArc)
        {
            var r = a.B - a.A; var s = b.B - b.A;
            double den = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(den) > 1e-15 * r.Length * s.Length)
            {
                var q = b.A - a.A;
                double t = (q.X * s.Y - q.Y * s.X) / den;
                Add(a.A + r * t);
            }
            return res;
        }
        if (a.IsArc && b.IsArc)
        {
            var d = b.C - a.C; double dist = d.Length;
            if (dist < Eps) return res;                                          // концентрические: только концы
            if (dist > a.R + b.R + Eps || dist < Math.Abs(a.R - b.R) - Eps) return res;
            double x = (dist * dist + a.R * a.R - b.R * b.R) / (2 * dist);
            double h2 = a.R * a.R - x * x; double h = h2 > 0 ? Math.Sqrt(h2) : 0;
            var e = d / dist; var n = new Vector2d(-e.Y, e.X);
            // KRN-02: касание окружностей — одна точка (две шумовые точки двойного корня — щепка)
            if (h <= Math.Max(Eps, TangencyBound(Math.Max(dist, Math.Max(a.R, b.R))))) { AddTangency(a.C + e * x, Math.Min(a.R, b.R)); return res; }
            Add(a.C + e * x + n * h);
            if (h > Eps) Add(a.C + e * x - n * h);
            return res;
        }
        // отрезок и дуга
        var line = a.IsArc ? b : a; var arc = a.IsArc ? a : b;
        var dl = line.B - line.A; double L2 = dl.LengthSquared;
        var f = line.A - arc.C;
        double A2 = L2, B2 = 2 * Vector2d.Dot(f, dl), C2 = f.LengthSquared - arc.R * arc.R;
        double disc = B2 * B2 - 4 * A2 * C2;
        if (disc < -1e-12 * A2 * arc.R * arc.R) return res;
        double sq = disc > 0 ? Math.Sqrt(disc) : 0;
        // KRN-02: КАСАНИЕ прямой и окружности — по полухорде (не по дискриминанту: двойной корень даёт две шумовые точки на
        // ~1e-6 мм); точка касания — точной формулой: основание перпендикуляра из центра, спроецированное на окружность
        {
            double tf = -Vector2d.Dot(f, dl) / A2; var foot = line.A + dl * tf; var fc = foot - arc.C;
            double dperp = fc.Length, halfChord = Math.Sqrt(Math.Max(0, arc.R * arc.R - dperp * dperp));
            bool tangent = halfChord <= Math.Max(Eps, TangencyBound(Math.Max(arc.R, f.Length)));
            if (tangent)
            {
                if (Math.Abs(dperp - arc.R) > Eps) return res;
                var p = dperp > 0 ? arc.C + fc * (arc.R / dperp) : foot;
                if (tangent) AddTangency(p, arc.R); else Add(p);
                return res;
            }
        }
        foreach (double t in new[] { (-B2 - sq) / (2 * A2), (-B2 + sq) / (2 * A2) })
        {
            // точка на окружности точно по радиусу (проекция от центра) — ошибка квадратного уравнения не накапливается
            var p = line.A + dl * t; var v = p - arc.C; double vl = v.Length;
            if (vl > 0) p = arc.C + v * (arc.R / vl);
            Add(p);
        }
        return res;
    }

    static bool OnLinePoint(Seg2 s, Vector2d p)
    {
        var d = s.B - s.A; double l = d.Length; if (l == 0) return (p - s.A).Length <= Eps;
        double u = Vector2d.Dot(p - s.A, d) / (l * l);
        if (u < -Eps / l || u > 1 + Eps / l) return false;
        var foot = s.A + d * u;
        return (p - foot).Length <= Eps;
    }

    static bool OnArcPoint(Seg2 s, Vector2d p)
    {
        if (Math.Abs((p - s.C).Length - s.R) > Eps) return false;
        if ((p - s.A).Length <= Eps || (p - s.B).Length <= Eps) return true;
        return OnArc(s, Math.Atan2(p.Y - s.C.Y, p.X - s.C.X), out double u) && u > 0 && u < 1;
    }

    /// <summary>Разбить кусок в точках (по параметру), концы частей — точно в этих точках.</summary>
    static IEnumerable<Seg2> SplitAt(Seg2 s, List<Vector2d> pts)
    {
        var cuts = pts.Where(p => (p - s.A).Length > Eps && (p - s.B).Length > Eps)
                      .Select(p => (U: ParamOf(s, p), P: p)).Where(c => c.U > 0 && c.U < 1)
                      .OrderBy(c => c.U).ToList();
        double u0 = 0; Vector2d p0 = s.A;
        foreach (var (u, p) in cuts)
        {
            if ((p - p0).Length <= Eps) continue;
            yield return s.Part(u0, u, p0, p);
            u0 = u; p0 = p;
        }
        if ((s.B - p0).Length > Eps) yield return s.Part(u0, 1, p0, s.B);
    }

    /// <summary>Совпадают ли два куска как множества точек (с ориентацией: +1 — в одну сторону, −1 — навстречу, 0 — нет).</summary>
    static int Coincide(Seg2 a, Seg2 b)
    {
        if (a.IsArc != b.IsArc) return 0;
        if (a.IsArc && ((a.C - b.C).Length > Eps || Math.Abs(a.R - b.R) > Eps)) return 0;
        var ma = a.At(0.5);
        bool same = (a.A - b.A).Length <= Eps && (a.B - b.B).Length <= Eps;
        bool opp = (a.A - b.B).Length <= Eps && (a.B - b.A).Length <= Eps;
        if (!same && !opp) return 0;
        // середины совпадают (у дуг с общими концами — та же или дополнительная дуга)
        if ((ma - b.At(0.5)).Length > 1e-7) return 0;
        return same ? 1 : -1;
    }

    /// <summary>Булева двух областей.</summary>
    public static Region2 Boolean(Region2 a, Region2 b, Bool2 op)
    {
        var segA = a.Loops.SelectMany(l => l).ToList();
        var segB = b.Loops.SelectMany(l => l).ToList();
        // 1. Точки разбиения каждого куска — пересечения со всеми кусками другой области.
        var cutA = segA.Select(_ => new List<Vector2d>()).ToList();
        var cutB = segB.Select(_ => new List<Vector2d>()).ToList();
        for (int i = 0; i < segA.Count; i++)
            for (int j = 0; j < segB.Count; j++)
                foreach (var p in Intersections(segA[i], segB[j])) { cutA[i].Add(p); cutB[j].Add(p); }
        var pa = segA.SelectMany((s, i) => SplitAt(s, cutA[i])).ToList();
        var pb = segB.SelectMany((s, j) => SplitAt(s, cutB[j])).ToList();

        // 2. Совпадающие куски и классификация остальных по середине.
        var keep = new List<Seg2>();
        var usedB = new bool[pb.Count];
        foreach (var s in pa)
        {
            int match = -1, dir = 0;
            for (int j = 0; j < pb.Count && match < 0; j++)
                if (!usedB[j] && (dir = Coincide(s, pb[j])) != 0) match = j;
            if (match >= 0)
            {
                usedB[match] = true;
                bool keepIt = op switch
                {
                    Bool2.Union => dir > 0,
                    Bool2.Intersect => dir > 0,
                    _ => dir < 0,                       // разность: общий участок, обходимый навстречу, — граница результата
                };
                if (keepIt) keep.Add(s);
                continue;
            }
            bool inB = b.Contains(s.At(0.5));
            if (op == Bool2.Union ? !inB : op == Bool2.Intersect ? inB : !inB) keep.Add(s);
        }
        for (int j = 0; j < pb.Count; j++)
        {
            if (usedB[j]) continue;
            var s = pb[j];
            bool inA = a.Contains(s.At(0.5));
            if (op == Bool2.Union && !inA) keep.Add(s);
            else if (op == Bool2.Intersect && inA) keep.Add(s);
            else if (op == Bool2.Subtract && inA) keep.Add(s.Reversed());
        }
        var outR = Chain(keep);
        if (DbgAt is Vector2d dp)
        {
            bool Near(Seg2 q) => (q.A - dp).Length < 1e-3 || (q.B - dp).Length < 1e-3;
            string S(Seg2 q) => q.IsArc ? $"arc C{q.C:0.#########} R{q.R:G9} T0 {q.T0:G12} Sw {q.Sweep:G12} {q.A:0.#########}->{q.B:0.#########}" : $"line {q.A:0.#########}->{q.B:0.#########}";
            if (segA.Any(Near) || segB.Any(Near))
            {
                Console.Error.WriteLine($"R2 {op} Eps {Eps:G3}");
                foreach (var q in segA.Where(Near)) Console.Error.WriteLine("  A " + S(q));
                foreach (var q in segB.Where(Near)) Console.Error.WriteLine("  B " + S(q));
                foreach (var q in keep.Where(Near)) Console.Error.WriteLine("  K " + S(q));
                foreach (var l in outR.Loops) if (l.Any(Near) && l.Count < 4) foreach (var q in l) Console.Error.WriteLine($"  RK {q.A.X:R} {q.A.Y:R} {q.B.X:R} {q.B.Y:R} {q.IsArc} {q.C.X:R} {q.C.Y:R} {q.R:R} {q.T0:R} {q.Sweep:R}");
                foreach (var l in outR.Loops) if (l.Any(Near)) Console.Error.WriteLine($"  loop n{l.Count} area {l.Sum(q => q.AreaTerm()):G4} per {l.Sum(q => q.Length):G6}");
            }
        }
        return outR;
    }
    [ThreadStatic] public static Vector2d? DbgAt;
    internal static readonly bool LocalAreaOff = Environment.GetEnvironmentVariable("P3D_R2LOCALAREA") == "0";

    /// <summary>
    /// Собрать куски в замкнутые контуры. Область слева от входящего куска; обход вокруг вершины по часовой стрелке от
    /// направления назад первым встречает самое ЛЕВОЕ продолжение — его и берём (касающиеся в вершине области — врозь).
    /// </summary>
    public static Region2 Chain(List<Seg2> segs)
    {
        var res = new Region2();
        var used = new bool[segs.Count];
        for (int start = 0; start < segs.Count; start++)
        {
            if (used[start]) continue;
            var loop = new List<Seg2>();
            int cur = start;
            var origin = segs[start].A;
            for (int guard = 0; guard < segs.Count + 1; guard++)
            {
                used[cur] = true; loop.Add(segs[cur]);
                var end = segs[cur].B;
                if ((end - origin).Length <= Eps) break;
                var inDir = segs[cur].Tangent(1).Normalized();
                int next = -1; double best = double.MaxValue;
                for (int k = 0; k < segs.Count; k++)
                {
                    if (used[k] || (segs[k].A - end).Length > Eps) continue;
                    var outDir = segs[k].Tangent(0).Normalized();
                    // поворот от входящего к исходящему (−π…π, влево — положительный); берём наибольший левый
                    double turn = Math.Atan2(inDir.X * outDir.Y - inDir.Y * outDir.X, Vector2d.Dot(inDir, outDir));
                    double key = -turn;
                    if (key < best) { best = key; next = k; }
                }
                if (next < 0) { loop = null!; break; }   // разомкнуто — кусок отбрасывается (вырожденный остаток)
                cur = next;
            }
            // KRN-02: вырожденный контур — средняя ширина не больше допуска: |площадь| ≤ Eps × периметр (а не |площадь| ≤ Eps —
            // сравнение мм² с мм, зависевшее от размера детали)
            // площадь — относительно своей вершины (P3D_R2LOCALAREA=0 — от начала координат: щепка 3e-7 мм у (76, 50) давала
            // шумовую площадь 7e-14 мм² > Eps × периметр и оставалась гранью, которую нельзя разбить на треугольники)
            if (loop != null && loop.Count > 0)
            {
                var o = LocalAreaOff ? Vector2d.Zero : loop[0].A;
                if (Math.Abs(loop.Sum(s => s.AreaTerm(o))) > Eps * loop.Sum(s => s.Length)) res.Loops.Add(loop);
            }
        }
        return res;
    }
}
