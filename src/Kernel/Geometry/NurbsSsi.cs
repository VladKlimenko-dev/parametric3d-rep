using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Geometry;

/// <summary>Точки найденных ломаных в корзинах куба со стороной s: близость и кандидаты без перебора всех точек (у почти
/// касания точек тысячи, ячеек — сотни тысяч).</summary>
sealed class TracedGrid
{
    readonly double _s; readonly Dictionary<(long, long, long), List<int>> _b = new();
    public readonly List<Vector3d> P = new(); public readonly List<(int C, int I)> Id = new();
    public TracedGrid(double s) { _s = Math.Max(s, 1e-9); }
    (long, long, long) Key(Vector3d p) => ((long)Math.Floor(p.X / _s), (long)Math.Floor(p.Y / _s), (long)Math.Floor(p.Z / _s));
    public void Add(Vector3d p, int c, int i)
    {
        var k = Key(p); if (!_b.TryGetValue(k, out var l)) _b[k] = l = new List<int>();
        l.Add(P.Count); P.Add(p); Id.Add((c, i));
    }
    public IEnumerable<int> Within(Vector3d x, double r)
    {
        double r2 = r * r;
        long nx = (long)Math.Floor((x.X + r) / _s) - (long)Math.Floor((x.X - r) / _s) + 1;
        if (nx * nx * nx > P.Count) { for (int i = 0; i < P.Count; i++) if ((P[i] - x).LengthSquared <= r2) yield return i; yield break; }
        var lo = Key(x - new Vector3d(r)); var hi = Key(x + new Vector3d(r));
        for (long a = lo.Item1; a <= hi.Item1; a++)
            for (long b = lo.Item2; b <= hi.Item2; b++)
                for (long c = lo.Item3; c <= hi.Item3; c++)
                    if (_b.TryGetValue((a, b, c), out var l))
                        foreach (int i in l) if ((P[i] - x).LengthSquared <= r2) yield return i;
    }
    public bool Any(Vector3d x, double r) => Within(x, r).Any();
}

/// <summary>
/// KRN-07 (07.8), KRN-04 (04.9), шаг N1b: ПЕРЕСЕЧЕНИЕ ГРАНИ NURBS С ПЛОСКОСТЬЮ ИЛИ КВАДРИКОЙ — нулевое множество функции
/// G(u, v) = F(S(u, v)) в прямоугольнике параметров грани, где F — неявная форма другой поверхности (расстояние со знаком,
/// константа Липшица 1). Точки кривой лежат на сплайне точно (вычисляются по S), F уточняется до 1e-12.
///   затравки — смены знака G на сетке 64×64 прямоугольника (с уточнением);
///   шаг — вдоль касательной к G = 0 в параметрах, нормированной на 1 мм пути, поправка — Ньютон по градиенту G;
///   выход за прямоугольник — точка на его стороне (деление пополам);
///   ПОЛНОТА — куски Безье (строгий габарит по выпуклой оболочке): кусок исключён, если |F(центр габарита)| больше половины
///   его диагонали; иначе — пояснён найденной кривой или делится; малый непояснённый кусок — новая затравка (Ньютон из
///   центра) или отказ (Uncertified).
/// </summary>
public static class NurbsSsi
{
    /// <summary>Мутация P3D_NURBSSIDE=0: сторона грани NURBS на другой поверхности — прежний общий путь (отказ удостоверения).</summary>
    static readonly bool SideOff = Environment.GetEnvironmentVariable("P3D_NURBSSIDE") == "0";
    public static long CoincidentSides;

    /// <summary>
    /// 06.12/g.08: СТОРОНА ПРЯМОУГОЛЬНИКА ГРАНИ ЦЕЛИКОМ НА ДРУГОЙ ПОВЕРХНОСТИ (лофт на плите: нижний край боковой грани лежит
    /// в плоскости верха плиты). Прослеживание вдоль самой стороны не удостоверялось («шаг не доказан однозначным»), и
    /// объединение уходило в многогранный путь. Теперь: G = 0 на стороне (65 проб), а в полосе у стороны G строго монотонна
    /// поперёк неё (оценка производной по кускам Безье с поправкой на кривизну другой поверхности) — в полосе нулей нет, кроме
    /// самой стороны. Пересечение = сторона (ломаная в параметрах) + обычный поиск в прямоугольнике без полосы.
    /// </summary>
    public static Ssi.Result Intersect(NurbsSurface S, double ua, double ub, double va, double vb, Ssi.Shape other, double scale)
    {
        if (!SideOff)
        {
            double G0(Vector2d q) => Ssi.Implicit(other, S.PointAt(q)).F;
            double tolF = 1e-9 * Math.Max(1, scale);
            foreach (int side in new[] { 0, 1, 2, 3 })        // 0: v = va, 1: v = vb, 2: u = ua, 3: u = ub
            {
                bool vSide = side < 2; double val = side switch { 0 => va, 1 => vb, 2 => ua, _ => ub };
                Vector2d At(double f) => vSide ? new Vector2d(ua + (ub - ua) * f, val) : new Vector2d(val, va + (vb - va) * f);
                bool on = true; for (int k = 0; k <= 64 && on; k++) if (Math.Abs(G0(At(k / 64.0))) > tolF) on = false;
                if (!on) continue;
                // полоса у стороны: производная G поперёк стороны одного знака
                double range = vSide ? vb - va : ub - ua, w = range / 16;
                var bezS = NurbsTools.Beziers(S); bool proved = false;
                for (int it = 0; it < 24 && !proved; it++, w /= 2)
                {
                    double s0 = side switch { 0 => va, 1 => vb - w, 2 => ua, _ => ub - w }, s1 = s0 + w;
                    int sg = 0; bool ok = true;
                    foreach (var bp in bezS)
                    {
                        double cu0 = vSide ? Math.Max(ua, bp.U0) : Math.Max(s0, bp.U0), cu1 = vSide ? Math.Min(ub, bp.U1) : Math.Min(s1, bp.U1);
                        double cv0 = vSide ? Math.Max(s0, bp.V0) : Math.Max(va, bp.V0), cv1 = vSide ? Math.Min(s1, bp.V1) : Math.Min(vb, bp.V1);
                        if (cu0 >= cu1 || cv0 >= cv1) continue;
                        var sub = bp.Sub(cu0, cu1, cv0, cv1);
                        if (sub.DerivBounds() is not { } db) { ok = false; break; }
                        var c3 = (db.Lo + db.Hi) / 2; double rb = (db.Hi - db.Lo).Length / 2;
                        if (Ssi.NormalLip(other, c3, rb) is not double kap) { ok = false; break; }
                        var g0 = Ssi.Implicit(other, c3).G; double del = Math.Min(2, kap * rb);
                        var dir = vSide ? new Vector2d(0, 1) : new Vector2d(1, 0);
                        if (sub.DirBound(g0, dir) is not { } d2) { ok = false; break; }
                        Vector3d dl = vSide ? db.SvLo : db.SuLo, dh = vSide ? db.SvHi : db.SuHi;
                        double a2 = 0; for (int m = 0; m < 3; m++) { double am = Math.Max(Math.Abs(dl[m]), Math.Abs(dh[m])); a2 += am * am; }
                        double mag = del * Math.Sqrt(a2);
                        int sgi = d2.Lo - mag > 0 ? 1 : d2.Hi + mag < 0 ? -1 : 0;
                        if (sgi == 0 || (sg != 0 && sgi != sg)) { ok = false; break; }
                        sg = sgi;
                    }
                    if (ok && sg != 0) proved = true; else if (w < 1e-7 * range) break;
                    if (proved)
                    {
                        // поиск в прямоугольнике без полосы, затем сторона — отдельная кривая
                        var rest = side switch
                        {
                            0 => Intersect(S, ua, ub, va + w, vb, other, scale),
                            1 => Intersect(S, ua, ub, va, vb - w, other, scale),
                            2 => Intersect(S, ua + w, ub, va, vb, other, scale),
                            _ => Intersect(S, ua, ub - w, va, vb, other, scale),
                        };
                        var uv = Enumerable.Range(0, 65).Select(k => At(k / 64.0)).ToList();
                        var sideC = new NurbsImplicitCurve3(S, other, uv);
                        // у периодической поверхности противоположная сторона — та же кривая в пространстве (тор: v = 0 и v = 1):
                        // вторая — не новая ветвь
                        bool dup = Environment.GetEnvironmentVariable("P3D_SIDEDEDUP") != "0" && rest.Curves.Any(cv =>
                        {
                            if (!(Math.Abs(cv.Length - sideC.Length) < 1e-6 * Math.Max(1, sideC.Length))) return false;
                            return Enumerable.Range(0, 5).All(k => { var q = sideC.PointAt((k + 0.5) / 5); var x = cv.PointAt(Analytic.FaceSplit.ClosestParam(cv, q)); return (x - q).Length < 1e-7 * Math.Max(1, scale); })
                                   && Math.Abs(cv.Length - sideC.Length) < 1e-6 * Math.Max(1, sideC.Length);
                        });
                        if (Environment.GetEnvironmentVariable("P3D_SIDEDBG") == "1") Console.WriteLine($"   [side] {side} [{ua},{ub}]x[{va},{vb}] w {w} rest {rest.Curves.Count} dup {dup} L {sideC.Length} " + string.Join(",", rest.Curves.Select(c => $"{c.GetType().Name}:{c.Length:F4}:{c.PointAt(c.T0)}")));
                        if (!dup) rest.Curves.Add(sideC);
                        rest.Kind = Ssi.Kind.Curves;
                        Interlocked.Increment(ref CoincidentSides);
                        return rest;
                    }
                }
            }
        }
        return IntersectCore(S, ua, ub, va, vb, other, scale);
    }

    static Ssi.Result IntersectCore(NurbsSurface S, double ua, double ub, double va, double vb, Ssi.Shape other, double scale)
    {
        var r = new Ssi.Result();
        double G(Vector2d q) => Ssi.Implicit(other, S.PointAt(q)).F;
        (double Gv, Vector2d Grad, Vector3d Su, Vector3d Sv) GD(Vector2d q)
        {
            const double h = 1e-7;
            double u = q.X, v = q.Y;
            double u0 = Math.Max(0, u - h), u1 = Math.Min(1, u + h), v0 = Math.Max(0, v - h), v1 = Math.Min(1, v + h);
            var X = S.PointAt(q);
            var su = (S.PointAt(new Vector2d(u1, v)) - S.PointAt(new Vector2d(u0, v))) / (u1 - u0);
            var sv = (S.PointAt(new Vector2d(u, v1)) - S.PointAt(new Vector2d(u, v0))) / (v1 - v0);
            var (f, g) = Ssi.Implicit(other, X);
            return (f, new Vector2d(Vector3d.Dot(g, su), Vector3d.Dot(g, sv)), su, sv);
        }
        bool InRect(Vector2d q) => q.X >= ua - 1e-12 && q.X <= ub + 1e-12 && q.Y >= va - 1e-12 && q.Y <= vb + 1e-12;
        Vector2d? Correct(Vector2d q)
        {
            for (int it = 0; it < 30; it++)
            {
                var (gv, gr, _, _) = GD(q);
                if (Math.Abs(gv) < 1e-12) return q;
                double n2 = gr.LengthSquared; if (n2 < 1e-30) return null;
                var nq = q - gr * (gv / n2);
                nq = new Vector2d(Math.Clamp(nq.X, 0, 1), Math.Clamp(nq.Y, 0, 1));
                if ((nq - q).Length < 1e-16) return Math.Abs(G(nq)) < 1e-9 ? nq : null;
                q = nq;
            }
            return Math.Abs(G(q)) < 1e-9 ? q : null;
        }
        // затравки
        int N = 64; var seeds = new List<Vector2d>();
        var gridG = new double[N + 1, N + 1];
        Vector2d UV(int i, int j) => new(ua + (ub - ua) * i / N, va + (vb - va) * j / N);
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) gridG[i, j] = G(UV(i, j));
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
                foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
                {
                    if (i + di > N || j + dj > N) continue;
                    double f0 = gridG[i, j], f1 = gridG[i + di, j + dj];
                    if (!(f0 == 0 || f0 * f1 < 0)) continue;
                    var a = UV(i, j); var b = UV(i + di, j + dj); double lo = 0, hi = 1;
                    for (int it = 0; it < 60; it++) { double m = (lo + hi) / 2; if (Math.Sign(G(a + (b - a) * m)) == Math.Sign(f0)) lo = m; else hi = m; }
                    if (Correct(a + (b - a) * lo) is { } q && InRect(q)) seeds.Add(q);
                }
        double h = Math.Max(scale / 400, 1e-4);              // шаг, мм
        var traced = new List<List<Vector2d>>();
        var tracedPts = new List<Vector3d>(); var grid = new TracedGrid(2 * h);
        bool Near(Vector2d q) { var x = S.PointAt(q); return grid.Any(x, 2 * h * (1 - 1e-12)); }
        bool NearR(Vector2d q, double rr) { var x = S.PointAt(q); return grid.Any(x, rr * (1 - 1e-12)); }
        Vector2d ToRect(Vector2d inside, Vector2d outside)
        {
            // точка кривой на стороне прямоугольника: деление пополам по отрезку с поправкой на G = 0
            Vector2d a = inside, b = outside;
            for (int it = 0; it < 60; it++)
            {
                var m = (a + b) / 2; var c = Correct(m) ?? m;
                if (InRect(c)) a = c; else b = c;
            }
            // на сторону — точно (параметр, ближайший к стороне, равен её значению), затем поправка вдоль стороны
            var q = a;
            double dua = Math.Abs(q.X - ua), dub = Math.Abs(q.X - ub), dva = Math.Abs(q.Y - va), dvb = Math.Abs(q.Y - vb);
            double mn = Math.Min(Math.Min(dua, dub), Math.Min(dva, dvb));
            bool fixU = mn == dua || mn == dub; double fixed_ = mn == dua ? ua : mn == dub ? ub : mn == dva ? va : vb;
            for (int it = 0; it < 40; it++)
            {
                var p = fixU ? new Vector2d(fixed_, q.Y) : new Vector2d(q.X, fixed_);
                var (gv, gr, _, _) = GD(p);
                double d = fixU ? gr.Y : gr.X; if (Math.Abs(d) < 1e-30) { q = p; break; }
                var np = fixU ? new Vector2d(fixed_, Math.Clamp(p.Y - gv / d, va, vb)) : new Vector2d(Math.Clamp(p.X - gv / d, ua, ub), fixed_);
                q = np; if (Math.Abs(gv) < 1e-13) break;
            }
            return q;
        }
        void TraceFrom(Vector2d s0)
        {
            if (UniqOff ? Near(s0) : NearR(s0, 0.75 * h)) return;
            List<Vector2d> Walk(double dir, out bool closed)
            {
                closed = false;
                var pts = new List<Vector2d> { s0 }; var q = s0; var start3 = S.PointAt(s0); double travelled = 0;
                for (int step = 0; step < (UniqOff ? 50000 : 400000); step++)
                {
                    var (_, gr, su, sv) = GD(q);
                    var t = new Vector2d(-gr.Y, gr.X) * dir;
                    var t3 = su * t.X + sv * t.Y; double sp = t3.Length;
                    if (sp < 1e-14) break;                                     // касание (градиент G ≈ 0)
                    // шаг — по углу пересечения: не больше 0.04·угол / (кривизна сплайна + кривизна другой поверхности); у почти
                    // касания точки чаще, и круги единственности у них (Disc) больше шага
                    double hs = h;
                    if (!UniqOff)
                    {
                        var n0 = Vector3d.Cross(su, sv); double nl = n0.Length;
                        if (nl > 1e-300)
                        {
                            n0 /= nl; var x0 = S.PointAt(q);
                            double kS = 0;
                            foreach (var dq in new[] { new Vector2d(0.5 * h / Math.Max(su.Length, 1e-12), 0), new Vector2d(0, 0.5 * h / Math.Max(sv.Length, 1e-12)) })
                            {
                                var q1 = q + dq; if (q1.X > 1 || q1.Y > 1) q1 = q - dq;
                                var (_, _, su1, sv1) = GD(q1); var n1 = Vector3d.Cross(su1, sv1);
                                double l1 = n1.Length, dx = (S.PointAt(q1) - x0).Length;
                                if (l1 > 1e-300 && dx > 1e-300) kS = Math.Max(kS, (n1 / l1 - n0).Length / dx);
                            }
                            var go = Ssi.Implicit(other, x0).G;
                            double sinA = Vector3d.Cross(n0, go).Length;
                            double kO = Ssi.NormalLip(other, x0, 0) ?? 0;
                            if (kS + kO > 0) hs = Math.Clamp(0.04 * sinA / (kS + kO), h / 64, h);
                        }
                    }
                    travelled += hs;
                    var pred = q + t * (hs / sp);
                    var nq = Correct(new Vector2d(Math.Clamp(pred.X, 0, 1), Math.Clamp(pred.Y, 0, 1)));
                    if (nq == null) { if (!InRect(pred) || pred.X < 0 || pred.X > 1 || pred.Y < 0 || pred.Y > 1) pts.Add(ToRect(q, pred)); break; }
                    if (!InRect(nq.Value) || pred.X < 0 || pred.X > 1 || pred.Y < 0 || pred.Y > 1) { pts.Add(ToRect(q, nq.Value)); break; }
                    q = nq.Value;
                    // замыкание — и в пространстве, и в параметрах (у шва u = 0 / u = 1 точки совпадают в пространстве, а кривая
                    // ещё не дошла до стороны прямоугольника; прежде такая кривая обрывалась за шаг до стороны)
                    if (step > 4 && (S.PointAt(q) - start3).Length < 0.8 * (UniqOff ? h : hs) && (UniqOff || (travelled > 3 * h && (q - s0).Length < 2 * (q - pts[^1]).Length + 1e-12))) { closed = true; break; }
                    pts.Add(q);
                }
                return pts;
            }
            var fwd = Walk(1, out bool cl);
            List<Vector2d> curve;
            if (cl) { curve = fwd; curve.Add(fwd[0]); }
            else { var back = Walk(-1, out _); back.Reverse(); back.RemoveAt(back.Count - 1); curve = back; curve.AddRange(fwd); }
            if (curve.Count < 2) return;
            traced.Add(curve); for (int k = 0; k < curve.Count; k++) { var x = S.PointAt(curve[k]); tracedPts.Add(x); grid.Add(x, traced.Count - 1, k); }
        }
        foreach (var s0 in seeds) TraceFrom(s0);
        if (TestExtraPolyline != null) traced.Add(TestExtraPolyline);
        var bez = NurbsTools.Beziers(S);
        double kU0 = S.KnotsU[0], kU1 = S.KnotsU[^1], kV0 = S.KnotsV[0], kV1 = S.KnotsV[^1];
        // ЕДИНСТВЕННОСТЬ ВЕТВИ у найденной кривой (04.7). Ближайшая найденная точка q* (в параметрах), направление d — градиент
        // G в q*, e ⟂ d. В квадрате Q (полуширина 1.12·R, R — от q* до дальнего угла ячейки внутри прямоугольника грани) строго
        // (годографы кусков Безье, продолжение многочлена за край области) G_d ≥ a > 0 и |G_e| ≤ a/2. Тогда нули G в круге R —
        // график s = φ(t) с |φ'| ≤ ½ через q* (на каждой прямой вдоль d нуль один): одна дуга. Стороны прямоугольника, задевающие
        // Q, круче конуса — дуга внутри прямоугольника связна. Найденная ломаная от q* в обе стороны доходит до |t| > R (или до
        // стороны прямоугольника, или замыкается), не выходя из Q, — значит, дуга пройдена вся, и ячейка пояснена.
        // круги единственности у точек ломаной: наибольший доказанный радиус (от четверти прямоугольника вниз делением пополам)
        // Круги единственности у точек ломаной — в МЕТРИЧЕСКИХ параметрах (u·|Su|, v·|Sv| в точке): круг в параметрах сплайна —
        // эллипс, вытянутый как поверхность. Радиус — наибольший доказанный (от четверти прямоугольника вниз делением пополам).
        var discs = new Dictionary<(int, int), (double R, double Lu, double Lv)>();
        (double R, double Lu, double Lv) DiscOf(int bi, int bj)
        {
            if (discs.TryGetValue((bi, bj), out var dc)) return dc;
            var qs = traced[bi][bj]; var (_, _, su, sv) = GD(qs);
            double Lu = Math.Max(su.Length, 1e-9), Lv = Math.Max(sv.Length, 1e-9);
            Vector2d M(Vector2d q) => new(q.X * Lu, q.Y * Lv);
            var tc = traced[bi]; double step0 = 0;
            for (int j = Math.Max(1, bj - 1); j < Math.Min(tc.Count, bj + 2); j++) step0 = Math.Max(step0, (M(tc[j]) - M(tc[j - 1])).Length);
            // наибольший шаг ломаной среди звеньев, начинающихся в круге 1.12·R у q* (по ходу в обе стороны)
            double StepWithin(double R)
            {
                double mx = 0; var q0 = M(qs); int n = tc.Count;
                bool closedC = n > 2 && (tc[0] - tc[^1]).Length < 1e-12;
                foreach (int dir in new[] { 1, -1 })
                {
                    int j = bj;
                    for (int it = 0; it < n; it++)
                    {
                        if ((M(tc[j]) - q0).Length > 1.12 * R) break;
                        int jn = j + dir;
                        if (jn < 0 || jn >= n) { if (!closedC) break; jn = dir > 0 ? 1 : n - 2; }     // замкнутая: точка 0 = точка n − 1
                        mx = Math.Max(mx, (M(tc[jn]) - M(tc[j])).Length); j = jn;
                    }
                }
                return mx;
            }
            double R = Math.Min(0.25 * Math.Max((ub - ua) * Lu, (vb - va) * Lv), 64 * Math.Max(step0, 1e-9)), res = 0;
            // тёплый старт: у соседней точки круг уже известен — начать с удвоенного
            foreach (int nb in new[] { bj - 1, bj + 1 })
                if (discs.TryGetValue((bi, nb), out var nd) && nd.R > 0) { R = Math.Min(R, 2 * nd.R); break; }
            for (int it = 0; it < 14 && R > 1e-12; it++, R /= 2)
            {
                double stepMax = Math.Max(StepWithin(R), step0);
                bool ok = Disc(bi, bj, R, stepMax, Lu, Lv, out var why);
                if (Dbg && !ok && (it == 13 || bj == 0)) Console.Error.WriteLine($"disc fail {bi}:{bj} at {qs} R {R:G3} step {stepMax:G3}: {why}");
                if (ok) { res = R; break; }
            }
            return discs[(bi, bj)] = (res, Lu, Lv);
        }
        bool rhoDbg = false;
        bool UniqueNear(BezierPatch cell, Vector3d c3, double crad)
        {
            double cu0 = Math.Max(cell.U0, ua), cu1 = Math.Min(cell.U1, ub), cv0 = Math.Max(cell.V0, va), cv1 = Math.Min(cell.V1, vb);
            if (cu0 > cu1 || cv0 > cv1) return true;                                    // ячейка вне грани
            // кандидаты — ближайшая точка каждой ломаной в метрических параметрах ячейки (не по пространству: у шва u = 0 / u = 1
            // точки совпадают в пространстве, а в параметрах далеки)
            var ccen = new Vector2d((cu0 + cu1) / 2, (cv0 + cv1) / 2); var (_, _, csu, csv) = GD(ccen);
            double cLu = Math.Max(csu.Length, 1e-9), cLv = Math.Max(csv.Length, 1e-9);
            // точки — из корзин в шаре близости ячейки (тот же шар, что у проверки «близко»)
            var bestJ = new Dictionary<int, (int J, double D)>();
            foreach (int gi in grid.Within(c3, crad + 2 * h))
            {
                var (ci, cj) = grid.Id[gi]; var dq = traced[ci][cj] - ccen; double dd = dq.X * dq.X * cLu * cLu + dq.Y * dq.Y * cLv * cLv;
                if (!bestJ.TryGetValue(ci, out var bb) || dd < bb.D) bestJ[ci] = (cj, dd);
            }
            foreach (var (i, (bj, _)) in bestJ)
            {
                var qs = traced[i][bj]; var (R, Lu, Lv) = DiscOf(i, bj); if (R <= 0) continue;
                bool inside = true;
                foreach (var cn in new[] { new Vector2d(cu0, cv0), new Vector2d(cu1, cv0), new Vector2d(cu0, cv1), new Vector2d(cu1, cv1) })
                    if (new Vector2d((cn.X - qs.X) * Lu, (cn.Y - qs.Y) * Lv).Length > R) { inside = false; break; }
                if (inside) return true;
                if (Dbg && rhoDbg) Console.Error.WriteLine($"cell [{cu0:G7},{cu1:G7}]x[{cv0:G7},{cv1:G7}] cand {i}:{bj}/{traced[i].Count} {qs} R {R:G3} Lu {Lu:G3} Lv {Lv:G3}");
            }
            return false;
        }
        bool Disc(int bi, int bj, double R, double stepMax, double Lu, double Lv, out string why, bool reach = true)
        {
            why = "";
            var qs = traced[bi][bj];
            Vector2d M(Vector2d q) => new((q.X - qs.X) * Lu, (q.Y - qs.Y) * Lv);       // метрические координаты от q*
            var (_, gr0, _, _) = GD(qs);
            var grM = new Vector2d(gr0.X / Lu, gr0.Y / Lv);
            if (grM.Length < 1e-14) { why = "gradient"; return false; }
            var d = grM.Normalized(); var e = new Vector2d(-d.Y, d.X);
            var dUV = new Vector2d(d.X / Lu, d.Y / Lv); var eUV = new Vector2d(e.X / Lu, e.Y / Lv);   // те же направления в u, v
            // квадрат Q — с запасом в шаг ломаной, чтобы первая точка за |t| = R ещё лежала в нём (на дуге)
            double k = 0.5, Hs = Math.Sqrt(1 + k * k) * (R + stepMax) * 1.001, T = Hs / Math.Sqrt(1 + k * k);
            double qu0 = qs.X - Hs / Lu, qu1 = qs.X + Hs / Lu, qv0 = qs.Y - Hs / Lv, qv1 = qs.Y + Hs / Lv;
            double aMin = double.MaxValue, cMax = 0;
            foreach (var bp in bez)
            {
                double eu0 = bp.U0 <= kU0 ? double.NegativeInfinity : bp.U0, eu1 = bp.U1 >= kU1 ? double.PositiveInfinity : bp.U1;
                double ev0 = bp.V0 <= kV0 ? double.NegativeInfinity : bp.V0, ev1 = bp.V1 >= kV1 ? double.PositiveInfinity : bp.V1;
                double su0 = Math.Max(qu0, eu0), su1 = Math.Min(qu1, eu1), sv0 = Math.Max(qv0, ev0), sv1 = Math.Min(qv1, ev1);
                if (su0 >= su1 || sv0 >= sv1) continue;
                var subP = bp.Sub(su0, su1, sv0, sv1);
                if (subP.DerivBounds() is not { } db) { why = "weights"; return false; }
                var c3 = (db.Lo + db.Hi) / 2; double rb = (db.Hi - db.Lo).Length / 2;
                if (Ssi.NormalLip(other, c3, rb) is not double kap) { why = "curvature"; return false; }
                var g0 = Ssi.Implicit(other, c3).G; double del = Math.Min(2, kap * rb);
                (double Lo, double Hi, double Abs) Dir(Vector2d w)
                {
                    double lo = 0, hi = 0, a2 = 0;
                    for (int m = 0; m < 3; m++)
                    {
                        double x1 = db.SuLo[m] * w.X, x2 = db.SuHi[m] * w.X, y1 = db.SvLo[m] * w.Y, y2 = db.SvHi[m] * w.Y;
                        double tl = Math.Min(x1, x2) + Math.Min(y1, y2), th = Math.Max(x1, x2) + Math.Max(y1, y2);
                        lo += Math.Min(g0[m] * tl, g0[m] * th); hi += Math.Max(g0[m] * tl, g0[m] * th);
                        double am = Math.Max(Math.Abs(tl), Math.Abs(th)); a2 += am * am;
                    }
                    return (lo, hi, Math.Sqrt(a2));
                }
                var (dl, _, da) = Dir(dUV); var (el, eh, ea) = Dir(eUV);
                var sub = subP;
                if (sub.DirBound(g0, dUV) is { } dd2) dl = Math.Max(dl, dd2.Lo);          // скалярная оценка точнее покомпонентной
                if (sub.DirBound(g0, eUV) is { } ee2) { el = Math.Max(el, ee2.Lo); eh = Math.Min(eh, ee2.Hi); }
                aMin = Math.Min(aMin, dl - del * da);
                cMax = Math.Max(cMax, Math.Max(Math.Abs(el), Math.Abs(eh)) + del * ea);
                if (!(aMin > 0) || cMax > k * aMin) { why = $"a {aMin:G3} c {cMax:G3} Hs {Hs:G3}"; return false; }
            }
            if (!(aMin > 0) || aMin == double.MaxValue) { why = "no patch"; return false; }
            // стороны прямоугольника, задевающие Q: дуга пересекает каждую не больше раза — тогда дуга внутри прямоугольника
            // связна. Достаточно: сторона круче конуса, или G вдоль стороны (на её отрезке в Q) строго монотонна.
            bool SideOk(bool uSide, double val)
            {
                if (uSide ? Math.Abs(d.Y) > k * Math.Abs(d.X) * (1 + 1e-9) : Math.Abs(d.X) > k * Math.Abs(d.Y) * (1 + 1e-9)) return true;
                var dir = uSide ? new Vector2d(0, 1) : new Vector2d(1, 0);
                int sgn = 0;
                foreach (var bp in bez)
                {
                    double eu0 = bp.U0 <= kU0 ? double.NegativeInfinity : bp.U0, eu1 = bp.U1 >= kU1 ? double.PositiveInfinity : bp.U1;
                    double ev0 = bp.V0 <= kV0 ? double.NegativeInfinity : bp.V0, ev1 = bp.V1 >= kV1 ? double.PositiveInfinity : bp.V1;
                    double su0, su1, sv0, sv1;
                    if (uSide) { if (val < eu0 || val > eu1) continue; su0 = su1 = val; sv0 = Math.Max(qv0, ev0); sv1 = Math.Min(qv1, ev1); if (sv0 >= sv1) continue; }
                    else { if (val < ev0 || val > ev1) continue; sv0 = sv1 = val; su0 = Math.Max(qu0, eu0); su1 = Math.Min(qu1, eu1); if (su0 >= su1) continue; }
                    // отрезок стороны делится на 8 кусков: оценка на каждом своя
                    for (int pc = 0; pc < 8; pc++)
                    {
                        double a0 = uSide ? sv0 + (sv1 - sv0) * pc / 8 : su0 + (su1 - su0) * pc / 8, a1 = uSide ? sv0 + (sv1 - sv0) * (pc + 1) / 8 : su0 + (su1 - su0) * (pc + 1) / 8;
                        var piece = uSide ? bp.Sub(val, val, a0, a1) : bp.Sub(a0, a1, val, val);
                        if (piece.DerivBounds() is not { } pb) return false;
                        var c3 = (pb.Lo + pb.Hi) / 2; double rb = (pb.Hi - pb.Lo).Length / 2;
                        if (Ssi.NormalLip(other, c3, rb) is not double kap) return false;
                        var g0 = Ssi.Implicit(other, c3).G; double del = Math.Min(2, kap * rb);
                        if (piece.DirBound(g0, dir) is not { } db2) return false;
                        var sl = uSide ? pb.SvLo : pb.SuLo; var sh = uSide ? pb.SvHi : pb.SuHi;
                        double sAbs = Math.Sqrt(Enumerable.Range(0, 3).Sum(m => Math.Pow(Math.Max(Math.Abs(sl[m]), Math.Abs(sh[m])), 2)));
                        double lo = db2.Lo - del * sAbs, hi = db2.Hi + del * sAbs;
                        int sg = lo > 0 ? 1 : hi < 0 ? -1 : 0;
                        if (sg == 0 || (sgn != 0 && sg != sgn)) return false;
                        sgn = sg;
                    }
                }
                return true;
            }
            if (Math.Abs(qs.X - ua) * Lu < Hs && !SideOk(true, ua)) { why = "side u0"; return false; }
            if (Math.Abs(qs.X - ub) * Lu < Hs && !SideOk(true, ub)) { why = "side u1"; return false; }
            if (Math.Abs(qs.Y - va) * Lv < Hs && !SideOk(false, va)) { why = "side v0"; return false; }
            if (Math.Abs(qs.Y - vb) * Lv < Hs && !SideOk(false, vb)) { why = "side v1"; return false; }
            if (!reach) return true;
            // найденная ломаная проходит дугу в круге целиком: от q* в обе стороны до |t| > R, не выходя из Q (иначе — скачок на
            // другую ветвь), или до стороны прямоугольника, или замыкается
            var cur = traced[bi]; int n = cur.Count;
            bool closedCur = n > 2 && (cur[0] - cur[^1]).Length < 1e-12;
            bool OnSide(Vector2d p) => Math.Min(Math.Min(Math.Abs(p.X - ua), Math.Abs(p.X - ub)), Math.Min(Math.Abs(p.Y - va), Math.Abs(p.Y - vb))) < 1e-9;
            int Reach(int dir)
            {
                int j = bj;
                for (int it = 0; it < n + 2; it++)
                {
                    j += dir;
                    if (j < 0 || j >= n)
                    {
                        if (!closedCur) return OnSide(cur[j < 0 ? 0 : n - 1]) ? 2 : 0;
                        j = j < 0 ? n - 2 : 1;                                             // замкнутая: первая точка = последняя
                    }
                    var p = M(cur[j]);
                    if (Math.Abs(p.X) > Hs || Math.Abs(p.Y) > Hs) return 0;
                    double t = Vector2d.Dot(p, e);
                    if (Math.Abs(t) > T) return 0;
                    if (Math.Abs(t) > R) return t > 0 ? 1 : -1;
                }
                return 0;
            }
            int f = Reach(1), b = Reach(-1);
            if (f == 0 || b == 0) { why = $"trace f {f} b {b}"; return false; }
            if (f != 2 && b != 2 && f == b) { why = "trace one side"; return false; }
            return true;
        }
        // ИСКЛЮЧЕНИЕ ПО ТЕОРЕМЕ О СРЕДНЕМ: на ячейке (в пределах прямоугольника грани) |G − G(центр)| ≤ max|G_u|·Δu/2 + max|G_v|·Δv/2;
        // G_u = ∇F·Su, оценки Su, Sv — строгие (годографы), ∇F — в центре габарита с поворотом по NormalLip. У почти касания
        // (полоса, где поверхности ближе ε) убывает квадратично, а не линейно, как проверка по габариту.
        bool MeanValueExcluded(BezierPatch bp)
        {
            double cu0 = Math.Max(bp.U0, ua), cu1 = Math.Min(bp.U1, ub), cv0 = Math.Max(bp.V0, va), cv1 = Math.Min(bp.V1, vb);
            if (cu0 >= cu1 || cv0 >= cv1) return true;
            var sub = cu0 > bp.U0 || cu1 < bp.U1 || cv0 > bp.V0 || cv1 < bp.V1 ? bp.Sub(cu0, cu1, cv0, cv1) : bp;
            if (sub.DerivBounds() is not { } db) return false;
            var c3 = (db.Lo + db.Hi) / 2; double rb = (db.Hi - db.Lo).Length / 2;
            if (Ssi.NormalLip(other, c3, rb) is not double kap) return false;
            var g0 = Ssi.Implicit(other, c3).G; double del = Math.Min(2, kap * rb);
            double Bound(Vector3d lo, Vector3d hi)
            {
                double l = 0, hh = 0, a2 = 0;
                for (int m = 0; m < 3; m++)
                {
                    l += Math.Min(g0[m] * lo[m], g0[m] * hi[m]); hh += Math.Max(g0[m] * lo[m], g0[m] * hi[m]);
                    double am = Math.Max(Math.Abs(lo[m]), Math.Abs(hi[m])); a2 += am * am;
                }
                return Math.Max(Math.Abs(l), Math.Abs(hh)) + del * Math.Sqrt(a2);
            }
            double gu = Bound(db.SuLo, db.SuHi), gv = Bound(db.SvLo, db.SvHi);
            double gc = Math.Abs(G(new Vector2d((cu0 + cu1) / 2, (cv0 + cv1) / 2)));
            return gc > (gu * (cu1 - cu0) / 2 + gv * (cv1 - cv0) / 2) * (1 + 1e-9) + 1e-13;
        }
        // полнота — по кускам Безье
        // ЗВЕНЬЯ ЛОМАНЫХ БЕЗ СКАЧКОВ: конец звена — в круге единственности его начала (радиус — длина звена): обе точки на одной
        // дуге, ломаная идёт по одной связной ветви. Мутация P3D_NURBSSTEPCERT=0.
        var segOk = new HashSet<(int, int)>();
        string? Segments()
        {
            if (UniqOff || StepCertOff) return null;
            for (int i = 0; i < traced.Count; i++)
                for (int j = 0; j + 1 < traced[i].Count; j++)
                {
                    if (segOk.Contains((i, j))) continue;
                    var p0 = traced[i][j]; var (_, _, su, sv) = GD(p0);
                    double Lu = Math.Max(su.Length, 1e-9), Lv = Math.Max(sv.Length, 1e-9);
                    var dq = traced[i][j + 1] - p0; double len = Math.Sqrt(dq.X * dq.X * Lu * Lu + dq.Y * dq.Y * Lv * Lv);
                    if (!Disc(i, j, Math.Max(len * 1.01, 1e-6 * Math.Max(1, scale)), 0, Lu, Lv, out var why, reach: false)) return $"NURBS certificate: trace step at {S.PointAt(p0):0.######} not proven single-branch ({why})";
                    segOk.Add((i, j));
                }
            return null;
        }
        string? Certify(List<Vector2d>? extra)
        {
            if (Segments() is { } segWhy) return segWhy;
            double rhoMin = 1e-4 * Math.Max(1, scale);
            var stack = new Stack<BezierPatch>();
            foreach (var bp in bez) if (bp.U1 > ua && bp.U0 < ub && bp.V1 > va && bp.V0 < vb) stack.Push(bp);
            int visited = 0; string? fail = null;
            while (stack.Count > 0)
            {
                if (++visited > 200000) return "NURBS certificate: too many cells";
                var bp = stack.Pop();
                var (lo, hi) = bp.Box(); var c = (lo + hi) / 2; double rad = (hi - lo).Length / 2;
                double f = Math.Abs(Ssi.Implicit(other, c).F);
                if (f > rad * (1 + 1e-12) + 1e-12) continue;                     // исключён: поверхность другой формы не задевает габарит
                if (!UniqOff && MeanValueExcluded(bp)) continue;                  // исключён по теореме о среднем (у почти касания)
                if (grid.Any(c, rad + 2 * h))
                {
                    if (UniqOff) continue;                                                // прежде: «близко — значит пояснён»
                    if (UniqueNear(bp, c, rad)) continue;                                         // доказано: ветвь в ячейке — найденная
                    if (rad > rhoMin) { var (A1, B1) = bp.Split(bp.U1 - bp.U0 >= bp.V1 - bp.V0); stack.Push(A1); stack.Push(B1); continue; }
                    var cq = new Vector2d(Math.Clamp((bp.U0 + bp.U1) / 2, ua, ub), Math.Clamp((bp.V0 + bp.V1) / 2, va, vb));
                    if (Correct(cq) is { } q2 && InRect(q2) && !NearR(q2, 0.75 * h))
                    {
                        if (extra == null) return $"NURBS certificate: an intersection point near {S.PointAt(q2):0.######} is on no traced curve";
                        extra.Add(q2); fail ??= "new seeds"; continue;
                    }
                    if (Dbg) { rhoDbg = true; UniqueNear(bp, c, rad); rhoDbg = false; }
                    return $"NURBS certificate: branch uniqueness not proven near {c:0.######} (cell radius {rad:G3})";
                }
                if (rad > rhoMin)
                {
                    var (A, B) = bp.Split(bp.U1 - bp.U0 >= bp.V1 - bp.V0 ? true : false);
                    // от длинной стороны в мм: делить по тому направлению, где габарит длиннее (по параметру — как приближение)
                    stack.Push(A); stack.Push(B); continue;
                }
                var cuv = new Vector2d(Math.Clamp((bp.U0 + bp.U1) / 2, ua, ub), Math.Clamp((bp.V0 + bp.V1) / 2, va, vb));
                if (Correct(cuv) is { } q)
                {
                    if (UniqOff && !InRect(q)) continue;                           // ветвь вне грани
                    if (UniqOff && Near(q)) continue;
                    if (!InRect(q) || NearR(q, 0.75 * h))
                        return $"NURBS certificate: unexplained near-contact cell at {c:0.######} (|F| {f:G3}, radius {rad:G3})";
                    if (extra == null) return $"NURBS certificate: an intersection point near {S.PointAt(q):0.######} is on no traced curve";
                    extra.Add(q); fail ??= "new seeds"; continue;
                }
                return $"NURBS certificate: unexplained near-contact cell at {c:0.######} (|F| {f:G3}, radius {rad:G3})";
            }
            return fail;
        }
        if (!CertOff)
            for (int round = 0; round < 3; round++)
            {
                var extra = new List<Vector2d>();
                var why = Certify(extra);
                if (why == null) break;
                int before = traced.Count;
                foreach (var q in extra) TraceFrom(q);
                if (traced.Count == before || round == 2) { if (Certify(null) is { } w2) r.Uncertified = w2; break; }
            }
        if (Dbg && r.Uncertified != null) Console.Error.WriteLine($"NURBS×{other.GetType().Name} uncertified: {r.Uncertified}");
        foreach (var c in traced)
            if (c.Count >= 2) r.Curves.Add(new NurbsImplicitCurve3(S, other, c));
        r.Kind = r.Curves.Count > 0 ? Ssi.Kind.Curves : Ssi.Kind.Empty;
        return r;
    }

    /// <summary>Мутация: P3D_NURBSCERT=0 — без удостоверения полноты (ветвь, пропущенная сеткой затравок, теряется).</summary>
    static readonly bool CertOff = Environment.GetEnvironmentVariable("P3D_NURBSCERT") == "0";
    /// <summary>Мутация: P3D_NURBSUNIQ=0 — ячейка у найденной кривой поясняется близостью (без доказательства единственности
    /// ветви), замыкание — только по близости в пространстве.</summary>
    static readonly bool Dbg = Environment.GetEnvironmentVariable("P3D_NURBSDBG") == "1";
    internal static readonly bool UniqOff = Environment.GetEnvironmentVariable("P3D_NURBSUNIQ") == "0";
    /// <summary>Мутация: P3D_NURBSSTEPCERT=0 — без проверки звеньев ломаных (скачок между ветвями не ловится).</summary>
    internal static readonly bool StepCertOff = Environment.GetEnvironmentVariable("P3D_NURBSSTEPCERT") == "0";
    /// <summary>Для проверки: ломаная (в параметрах), добавляемая к найденным перед удостоверением.</summary>
    [ThreadStatic] internal static List<Vector2d>? TestExtraPolyline;
}

/// <summary>
/// Кривая пересечения грани NURBS и неявной поверхности: ломаная в параметрах сплайна (затравка), параметр — доля длины
/// пути в пространстве; точка — по S в интерполированных параметрах с поправкой на G = 0 вдоль градиента (лежит на сплайне
/// точно и на другой поверхности до 1e-12).
/// </summary>
public sealed class NurbsImplicitCurve3 : ICurve3
{
    public readonly NurbsSurface S; public readonly Ssi.Shape Other;
    readonly Vector2d[] _uv; readonly double[] _s;
    public IReadOnlyList<Vector2d> SeedUV => _uv;
    public NurbsImplicitCurve3(NurbsSurface s, Ssi.Shape other, IReadOnlyList<Vector2d> uv)
    {
        S = s; Other = other; _uv = uv.ToArray(); _s = new double[_uv.Length];
        for (int i = 1; i < _uv.Length; i++) _s[i] = _s[i - 1] + (S.PointAt(_uv[i]) - S.PointAt(_uv[i - 1])).Length;
    }
    public double T0 => 0; public double T1 => 1;
    /// <summary>Изломы параметризации (доли t точек затравки): квадратура — по кускам между ними.</summary>
    public IEnumerable<double> Breaks { get { double tot = _s[^1]; if (tot <= 0) yield break; for (int i = 1; i < _s.Length - 1; i++) yield return _s[i] / tot; } }
    public bool IsClosed => _uv.Length > 2 && (_uv[0] - _uv[^1]).Length < 1e-12;
    public double Length => _s[^1];

    public Vector2d UVAt(double t)
    {
        double tot = _s[^1]; if (tot <= 0) return _uv[0];
        double L = Math.Clamp(t, 0, 1) * tot;
        int lo = 0, hi = _s.Length - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (_s[m] <= L) lo = m; else hi = m; }
        double seg = _s[hi] - _s[lo], a = seg > 0 ? (L - _s[lo]) / seg : 0;
        var q = _uv[lo] + (_uv[hi] - _uv[lo]) * a;
        if (a <= 0 || a >= 1) return q;                               // точки затравки — уже на кривой
        // поправка вдоль градиента G
        const double h = 1e-7;
        for (int it = 0; it < 8; it++)
        {
            var X = S.PointAt(q); var (f, g) = Ssi.Implicit(Other, X);
            if (Math.Abs(f) < 1e-13) break;
            double u0 = Math.Max(0, q.X - h), u1 = Math.Min(1, q.X + h), v0 = Math.Max(0, q.Y - h), v1 = Math.Min(1, q.Y + h);
            var su = (S.PointAt(new Vector2d(u1, q.Y)) - S.PointAt(new Vector2d(u0, q.Y))) / (u1 - u0);
            var sv = (S.PointAt(new Vector2d(q.X, v1)) - S.PointAt(new Vector2d(q.X, v0))) / (v1 - v0);
            var gr = new Vector2d(Vector3d.Dot(g, su), Vector3d.Dot(g, sv)); double n2 = gr.LengthSquared; if (n2 < 1e-30) break;
            q -= gr * (f / n2);
        }
        return q;
    }
    public Vector3d PointAt(double t) => S.PointAt(UVAt(t));
    public Vector3d TangentAt(double t)
    {
        double h = 1e-6; var a = PointAt(Math.Max(0, t - h)); var b = PointAt(Math.Min(1, t + h));
        var d = b - a; return d.LengthSquared < 1e-30 ? Vector3d.UnitX : d.Normalized();
    }
    public Bounds3 Bounds
    {
        get
        {
            var b = Bounds3.Empty; foreach (var q in _uv) b.Add(S.PointAt(q));
            // между точками затравки кривая отходит от хорды не больше шага — запас
            double step = 0; for (int i = 1; i < _s.Length; i++) step = Math.Max(step, _s[i] - _s[i - 1]);
            b.Add(b.Min - new Vector3d(step)); b.Add(b.Max + new Vector3d(step));
            return b;
        }
    }
    public ICurve3 Reversed() => new NurbsImplicitCurve3(S, Other, _uv.Reverse().ToList());
    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint)
    {
        for (int i = includeFirstPoint ? 0 : 1; i < _uv.Length; i++) dst.Add(S.PointAt(_uv[i]));
    }
}

/// <summary>
/// KRN-04 (04.9), шаг N2: ПЕРЕСЕЧЕНИЕ ДВУХ ГРАНЕЙ NURBS. Неизвестные (u₁, v₁, u₂, v₂), уравнения S₁ = S₂ (три) и плоскость шага.
///   затравки — смены знака расстояния до другой поверхности (по нормали в проекции) на сетке 32×32 каждой грани;
///   шаг — вдоль n₁ × n₂, поправка Гаусса — Ньютона (4×4 с плоскостью шага); выход за прямоугольник грани — делением пополам
///   и точно на сторону (параметр стороны закреплён, три уравнения на три неизвестные);
///   ПОЛНОТА — пары кусков Безье: габариты врозь — пара исключена; иначе пара поясняется найденной кривой (точка ближе
///   радиуса куска + 2 шага) или делится; малая непояснённая пара — затравка (Ньютон из центров) или отказ.
/// </summary>
public static class NurbsNurbsSsi
{
    [ThreadStatic] public static Action<string>? Debug;
    /// <summary>Для проверки: ломаная в (u₁, v₁, u₂, v₂), добавляемая к найденным перед удостоверением.</summary>
    [ThreadStatic] internal static List<Vector4d>? TestExtraPolyline;
    public readonly record struct Rect(double U0, double U1, double V0, double V1)
    {
        public bool In(double u, double v) => u >= U0 - 1e-12 && u <= U1 + 1e-12 && v >= V0 - 1e-12 && v <= V1 + 1e-12;
    }

    static bool Solve(double[,] m, double[] b, int n)
    {
        for (int c = 0; c < n; c++)
        {
            int p = c; for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) < 1e-300) return false;
            if (p != c) { for (int k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]); (b[c], b[p]) = (b[p], b[c]); }
            for (int r = c + 1; r < n; r++) { double f = m[r, c] / m[c, c]; for (int k = c; k < n; k++) m[r, k] -= f * m[c, k]; b[r] -= f * b[c]; }
        }
        for (int r = n - 1; r >= 0; r--) { double s = b[r]; for (int k = r + 1; k < n; k++) s -= m[r, k] * b[k]; b[r] = s / m[r, r]; }
        return true;
    }

    /// <summary>Поправка Гаусса — Ньютона к S₁(q.xy) = S₂(q.zw); с плоскостью (x0, t) — 4×4, иначе — наименьшая норма шага.</summary>
    internal static Vector4d? Correct(NurbsSurface A, NurbsSurface B, Vector4d q, Vector3d? x0, Vector3d? t, int fixIdx = -1)
    {
        for (int it = 0; it < 40; it++)
        {
            A.Derivs(new Vector2d(q.X, q.Y), out var Pa, out var Au, out var Av);
            B.Derivs(new Vector2d(q.Z, q.W), out var Pb, out var Bu, out var Bv);
            var r = Pa - Pb; double pr = x0 is { } xx && t is { } tt ? Vector3d.Dot(tt, Pa - xx) : 0;
            if (r.Length < 1e-13 && Math.Abs(pr) < 1e-13) return q;
            var cols = new[] { Au, Av, -Bu, -Bv };
            double[] d;
            if (fixIdx >= 0)
            {
                // один параметр закреплён (сторона прямоугольника): 3×3
                var idx = Enumerable.Range(0, 4).Where(k => k != fixIdx).ToArray();
                var m = new double[3, 3]; var b = new[] { -r.X, -r.Y, -r.Z };
                for (int row = 0; row < 3; row++) for (int c = 0; c < 3; c++) m[row, c] = row == 0 ? cols[idx[c]].X : row == 1 ? cols[idx[c]].Y : cols[idx[c]].Z;
                if (!Solve(m, b, 3)) return null;
                d = new double[4]; for (int c = 0; c < 3; c++) d[idx[c]] = b[c];
            }
            else if (x0 != null && t is { } tv)
            {
                var m = new double[4, 4]; var b = new[] { -r.X, -r.Y, -r.Z, -pr };
                for (int c = 0; c < 4; c++) { m[0, c] = cols[c].X; m[1, c] = cols[c].Y; m[2, c] = cols[c].Z; }
                m[3, 0] = Vector3d.Dot(tv, Au); m[3, 1] = Vector3d.Dot(tv, Av); m[3, 2] = 0; m[3, 3] = 0;
                if (!Solve(m, b, 4)) return null;
                d = b;
            }
            else
            {
                // наименьшая норма: Δ = Jᵀ (J Jᵀ)⁻¹ (−r)
                var JJ = new double[3, 3];
                for (int a = 0; a < 3; a++) for (int c = 0; c < 3; c++) { double s2 = 0; for (int k = 0; k < 4; k++) s2 += Comp(cols[k], a) * Comp(cols[k], c); JJ[a, c] = s2; }
                var y = new[] { -r.X, -r.Y, -r.Z };
                if (!Solve(JJ, y, 3)) return null;
                d = new double[4]; for (int k = 0; k < 4; k++) d[k] = Comp(cols[k], 0) * y[0] + Comp(cols[k], 1) * y[1] + Comp(cols[k], 2) * y[2];
            }
            var nq = new Vector4d(Math.Clamp(q.X + d[0], 0, 1), Math.Clamp(q.Y + d[1], 0, 1), Math.Clamp(q.Z + d[2], 0, 1), Math.Clamp(q.W + d[3], 0, 1));
            if ((nq - q).Length < 1e-16) { q = nq; break; }
            q = nq;
        }
        A.Derivs(new Vector2d(q.X, q.Y), out var Pa2, out _, out _); B.Derivs(new Vector2d(q.Z, q.W), out var Pb2, out _, out _);
        return (Pa2 - Pb2).Length < 1e-10 ? q : null;
    }
    static double Comp(Vector3d v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
    /// <summary>Оценка нормальной кривизны (поворот нормали на мм) по двум смещениям в полшага — для шага прослеживания.</summary>
    static double Curv(NurbsSurface S, Vector2d q, Vector3d su, Vector3d sv)
    {
        var n0 = Vector3d.Cross(su, sv); double l0 = n0.Length; if (l0 < 1e-300) return 0; n0 /= l0;
        var x0 = S.PointAt(q); double k = 0;
        foreach (var dq in new[] { new Vector2d(1e-3 / Math.Max(su.Length, 1e-12), 0), new Vector2d(0, 1e-3 / Math.Max(sv.Length, 1e-12)) })
        {
            var q1 = q + dq; if (q1.X > 1 || q1.Y > 1) q1 = q - dq;
            S.Derivs(q1, out var x1, out var a, out var b); var n1 = Vector3d.Cross(a, b); double l1 = n1.Length, dx = (x1 - x0).Length;
            if (l1 > 1e-300 && dx > 1e-300) k = Math.Max(k, (n1 / l1 - n0).Length / dx);
        }
        return k;
    }

    public static Ssi.Result Intersect(NurbsSurface A, Rect ra, NurbsSurface B, Rect rb, double scale)
    {
        var res = new Ssi.Result();
        bool In(Vector4d q) => ra.In(q.X, q.Y) && rb.In(q.Z, q.W);
        double h = Math.Max(scale / 400, 1e-4);
        var seeds = new List<Vector4d>();
        // затравки: расстояние до другой поверхности на сетке каждой грани
        foreach (bool swap in new[] { false, true })
        {
            var P = swap ? B : A; var Q = swap ? A : B; var rp = swap ? rb : ra; var rq = swap ? ra : rb;
            int N = 32; var dv = new double[N + 1, N + 1]; var uvq = new Vector2d[N + 1, N + 1]; var ok = new bool[N + 1, N + 1];
            Vector2d hint = Q.ProjectToUV(P.PointAt(new Vector2d(rp.U0, rp.V0)));
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                {
                    var uv = new Vector2d(rp.U0 + (rp.U1 - rp.U0) * i / N, rp.V0 + (rp.V1 - rp.V0) * j / N);
                    var x = P.PointAt(uv); var w = Q.ProjectToUV(x, hint); hint = w;
                    uvq[i, j] = w; ok[i, j] = w.X > rq.U0 + 1e-9 && w.X < rq.U1 - 1e-9 && w.Y > rq.V0 + 1e-9 && w.Y < rq.V1 - 1e-9;
                    dv[i, j] = Vector3d.Dot(x - Q.PointAt(w), Q.NormalAt(w));
                }
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                    foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
                    {
                        if (i + di > N || j + dj > N || !ok[i, j] || !ok[i + di, j + dj]) continue;
                        if (!(dv[i, j] * dv[i + di, j + dj] < 0 || dv[i, j] == 0)) continue;
                        var u0 = new Vector2d(rp.U0 + (rp.U1 - rp.U0) * i / N, rp.V0 + (rp.V1 - rp.V0) * j / N);
                        var u1 = new Vector2d(rp.U0 + (rp.U1 - rp.U0) * (i + di) / N, rp.V0 + (rp.V1 - rp.V0) * (j + dj) / N);
                        var m = (u0 + u1) / 2; var w = (uvq[i, j] + uvq[i + di, j + dj]) / 2;
                        var q0 = swap ? new Vector4d(w.X, w.Y, m.X, m.Y) : new Vector4d(m.X, m.Y, w.X, w.Y);
                        if (Correct(A, B, q0, null, null) is { } q && In(q)) seeds.Add(q);
                    }
        }
        var traced = new List<List<Vector4d>>(); var tracedPts = new List<Vector3d>(); var grid = new TracedGrid(2 * h);
        bool Near(Vector3d x, double rr) => grid.Any(x, rr);
        Vector4d ToBoundary(Vector4d inside, Vector4d outside)
        {
            Vector4d a = inside, b = outside;
            for (int it = 0; it < 50; it++) { var mm = (a + b) / 2; var c = Correct(A, B, mm, null, null) ?? mm; if (In(c)) a = c; else b = c; }
            // какой параметр у стороны и её значение
            var cand = new (int Idx, double Val, double D)[]
            {
                (0, ra.U0, Math.Abs(a.X - ra.U0)), (0, ra.U1, Math.Abs(a.X - ra.U1)), (1, ra.V0, Math.Abs(a.Y - ra.V0)), (1, ra.V1, Math.Abs(a.Y - ra.V1)),
                (2, rb.U0, Math.Abs(a.Z - rb.U0)), (2, rb.U1, Math.Abs(a.Z - rb.U1)), (3, rb.V0, Math.Abs(a.W - rb.V0)), (3, rb.V1, Math.Abs(a.W - rb.V1)),
            };
            var best = cand.OrderBy(c => c.D).First();
            var q = best.Idx switch { 0 => new Vector4d(best.Val, a.Y, a.Z, a.W), 1 => new Vector4d(a.X, best.Val, a.Z, a.W), 2 => new Vector4d(a.X, a.Y, best.Val, a.W), _ => new Vector4d(a.X, a.Y, a.Z, best.Val) };
            return Correct(A, B, q, null, null, best.Idx) ?? a;
        }
        void TraceFrom(Vector4d s0)
        {
            if (Near(A.PointAt(new Vector2d(s0.X, s0.Y)), NurbsSsi.UniqOff ? 2 * h : 0.75 * h)) return;
            List<Vector4d> Walk(double dir, out bool closed)
            {
                closed = false; var pts = new List<Vector4d> { s0 }; var q = s0; var start3 = A.PointAt(new Vector2d(s0.X, s0.Y)); double travelled = 0;
                for (int step = 0; step < (NurbsSsi.UniqOff ? 50000 : 400000); step++)
                {
                    A.Derivs(new Vector2d(q.X, q.Y), out var Pa, out var Au, out var Av);
                    B.Derivs(new Vector2d(q.Z, q.W), out _, out var Bu, out var Bv);
                    var t = Vector3d.Cross(Vector3d.Cross(Au, Av), Vector3d.Cross(Bu, Bv));
                    if (t.Length < 1e-12 * Math.Max(1, (Au.Length * Av.Length * Bu.Length * Bv.Length))) { Debug?.Invoke($"walk stop: tangent at {Pa:0.####}"); break; }   // касание
                    // шаг — по углу пересечения (как в NurbsSsi): 0.04·sin угла / (кривизна A + кривизна B)
                    double hs = h;
                    if (!NurbsSsi.UniqOff)
                    {
                        double sinA = t.Length / (Vector3d.Cross(Au, Av).Length * Vector3d.Cross(Bu, Bv).Length);
                        double kap = Curv(A, new Vector2d(q.X, q.Y), Au, Av) + Curv(B, new Vector2d(q.Z, q.W), Bu, Bv);
                        if (kap > 0) hs = Math.Clamp(0.04 * sinA / kap, h / 64, h);
                    }
                    travelled += hs;
                    t = t.Normalized() * dir;
                    // предсказание в параметрах обеих поверхностей: S_u du + S_v dv = t·h (наименьшие квадраты)
                    Vector2d Pred(Vector3d su, Vector3d sv)
                    {
                        double a11 = Vector3d.Dot(su, su), a12 = Vector3d.Dot(su, sv), a22 = Vector3d.Dot(sv, sv), det = a11 * a22 - a12 * a12;
                        double b1 = Vector3d.Dot(su, t * hs), b2 = Vector3d.Dot(sv, t * hs);
                        return Math.Abs(det) < 1e-300 ? Vector2d.Zero : new Vector2d((b1 * a22 - b2 * a12) / det, (a11 * b2 - a12 * b1) / det);
                    }
                    var da = Pred(Au, Av); var db = Pred(Bu, Bv);
                    var qp = new Vector4d(q.X + da.X, q.Y + da.Y, q.Z + db.X, q.W + db.Y);
                    bool outside = qp.X < 0 || qp.X > 1 || qp.Y < 0 || qp.Y > 1 || qp.Z < 0 || qp.Z > 1 || qp.W < 0 || qp.W > 1;
                    var nq = Correct(A, B, new Vector4d(Math.Clamp(qp.X, 0, 1), Math.Clamp(qp.Y, 0, 1), Math.Clamp(qp.Z, 0, 1), Math.Clamp(qp.W, 0, 1)), Pa + t * hs, t);
                    if (nq == null)
                    {
                        // поправка не сошлась: шаг вышел за область параметров (зажат на краю) — точка кривой на стороне грани
                        if (outside || !In(qp)) { pts.Add(ToBoundary(q, qp)); Debug?.Invoke($"walk stop: boundary (after a failed correction) at {A.PointAt(new Vector2d(pts[^1].X, pts[^1].Y)):0.####}"); break; }
                        Debug?.Invoke($"walk stop: correction failed at {Pa:0.####} step {step}"); break;
                    }
                    if (!In(nq.Value) || outside) { pts.Add(ToBoundary(q, nq.Value)); Debug?.Invoke($"walk stop: boundary at {A.PointAt(new Vector2d(pts[^1].X, pts[^1].Y)):0.####}"); break; }
                    q = nq.Value;
                    if (step > 4 && (A.PointAt(new Vector2d(q.X, q.Y)) - start3).Length < 0.8 * (NurbsSsi.UniqOff ? h : hs)
                        && (NurbsSsi.UniqOff || (travelled > 3 * h && (q - s0).Length < 2 * (q - pts[^1]).Length + 1e-12))) { closed = true; break; }
                    pts.Add(q);
                }
                return pts;
            }
            var fwd = Walk(1, out bool cl);
            List<Vector4d> curve;
            if (cl) { curve = fwd; curve.Add(fwd[0]); }
            else { var back = Walk(-1, out _); back.Reverse(); back.RemoveAt(back.Count - 1); curve = back; curve.AddRange(fwd); }
            if (curve.Count < 2) return;
            traced.Add(curve); for (int k = 0; k < curve.Count; k++) { var x = A.PointAt(new Vector2d(curve[k].X, curve[k].Y)); tracedPts.Add(x); grid.Add(x, traced.Count - 1, k); }
        }
        Debug?.Invoke($"seeds {seeds.Count}");
        var swT = System.Diagnostics.Stopwatch.StartNew();
        foreach (var s in seeds) TraceFrom(s);
        if (TestExtraPolyline != null) traced.Add(TestExtraPolyline);
        Debug?.Invoke($"trace ms {swT.ElapsedMilliseconds}");
        Debug?.Invoke($"traced {traced.Count}: " + string.Join(" | ", traced.Select(c => $"{c.Count} pts {A.PointAt(new Vector2d(c[0].X, c[0].Y)):0.###} → {A.PointAt(new Vector2d(c[^1].X, c[^1].Y)):0.###}")));
        // ЕДИНСТВЕННОСТЬ ВЕТВИ (04.7) в (u₁, v₁, u₂, v₂): F(q) = A(u₁, v₁) − B(u₂, v₂). В точке ломаной q* — метрические координаты
        // (параметр × |S_u| в q*), J — якобиан F (3×4), e — его ядро (касательная), W — дополнение (4×3), K = (J W)⁻¹. В области
        // Ω = {|t| ≤ T, |y|∞ ≤ Y} (q = q* + t e + W y) строго — по интервалам производных кусков Безье — ‖K ΔJ W‖∞ ≤ ½ и
        // ‖K ΔJ e‖∞ ≤ ¼ (ΔJ = J(x) − J(q*)). Тогда на каждом срезе t нуль один, нули в Ω — график y = φ(t), |φ'|∞ ≤ ½, через q*.
        // Стороны прямоугольников граней, задевающие Ω: параметр вдоль графика монотонен. Ломаная от q* доходит до |t| > R в обе
        // стороны, не выходя из Ω (или до стороны, или замыкается). Ячейка-пара в Ω (углы: |t| ≤ R, |y|∞ ≤ Y) — пояснена.
        var bezA = NurbsTools.Beziers(A); var bezB = NurbsTools.Beziers(B);
        var lo4 = new[] { ra.U0, ra.V0, rb.U0, rb.V0 }; var hi4 = new[] { ra.U1, ra.V1, rb.U1, rb.V1 };
        var discs = new Dictionary<(int, int), (double R, double Y, double[] L, double[] E, double[,] W, double T)>(); double discMs = 0;
        double[] Lof(Vector4d q)
        {
            A.Derivs(new Vector2d(q.X, q.Y), out _, out var au, out var av); B.Derivs(new Vector2d(q.Z, q.W), out _, out var bu, out var bv);
            return new[] { Math.Max(au.Length, 1e-9), Math.Max(av.Length, 1e-9), Math.Max(bu.Length, 1e-9), Math.Max(bv.Length, 1e-9) };
        }
        double[] Z(Vector4d p, Vector4d q0, double[] L) => new[] { (p.X - q0.X) * L[0], (p.Y - q0.Y) * L[1], (p.Z - q0.Z) * L[2], (p.W - q0.W) * L[3] };
        static double Nrm(double[] z) => Math.Sqrt(z.Sum(x => x * x));
        (double R, double Y, double[] L, double[] E, double[,] W, double T) DiscOf(int bi, int bj)
        {
            if (discs.TryGetValue((bi, bj), out var dc)) return dc;
            var swD = System.Diagnostics.Stopwatch.StartNew(); try {
            var tc = traced[bi]; var qs = tc[bj]; var L = Lof(qs); int n = tc.Count;
            bool closedC = n > 2 && (tc[0] - tc[^1]).Length < 1e-12;
            double step0 = 0;
            for (int j = Math.Max(1, bj - 1); j < Math.Min(n, bj + 2); j++) step0 = Math.Max(step0, Nrm(Z(tc[j], tc[j - 1], L)));
            double StepWithin(double R)
            {
                double mx = 0;
                foreach (int dir in new[] { 1, -1 })
                {
                    int j = bj;
                    for (int it = 0; it < n; it++)
                    {
                        if (Nrm(Z(tc[j], qs, L)) > 1.5 * R) break;
                        int jn = j + dir;
                        if (jn < 0 || jn >= n) { if (!closedC) break; jn = dir > 0 ? 1 : n - 2; }
                        mx = Math.Max(mx, Nrm(Z(tc[jn], tc[j], L))); j = jn;
                    }
                }
                return mx;
            }
            double size = 0; for (int i = 0; i < 4; i++) size = Math.Max(size, (hi4[i] - lo4[i]) * L[i]);
            double R = Math.Min(0.25 * size, 64 * Math.Max(step0, 1e-9));
            foreach (int nb in new[] { bj - 1, bj + 1 })
                if (discs.TryGetValue((bi, nb), out var nd) && nd.R > 0) { R = Math.Min(R, 2 * nd.R); break; }
            (double, double, double[], double[], double[,], double) res = (0, 0, L, new double[4], new double[4, 3], 0);
            for (int it = 0; it < 14 && R > 1e-12; it++, R /= 2)
            {
                double stp = Math.Max(StepWithin(R), step0);
                if (DiscNN(bi, bj, R, stp, L, out var Y, out var e, out var W, out var T, out var why)) { res = (R, Y, L, e, W, T); break; }
                if (Debug != null && it == 13) Debug($"NN disc fail {bi}:{bj} R {R:G3} step {stp:G3}: {why}");
            }
            return discs[(bi, bj)] = res;
            } finally { discMs += swD.Elapsed.TotalMilliseconds; }
        }
        bool DiscNN(int bi, int bj, double R, double step, double[] L, out double Y, out double[] eOut, out double[,] W, out double T, out string why, bool reach = true)
        {
            why = ""; Y = 0; var e = new double[4]; eOut = e; W = new double[4, 3]; T = 0;
            var tc = traced[bi]; var qs = tc[bj]; int n = tc.Count;
            A.Derivs(new Vector2d(qs.X, qs.Y), out _, out var au, out var av); B.Derivs(new Vector2d(qs.Z, qs.W), out _, out var bu, out var bv);
            var cols = new[] { au / L[0], av / L[1], -bu / L[2], -bv / L[3] };
            // ядро: e_k = (−1)^k det(J без столбца k)
            double Det3(Vector3d a, Vector3d b, Vector3d c) => Vector3d.Dot(a, Vector3d.Cross(b, c));
            for (int k = 0; k < 4; k++) { var o = Enumerable.Range(0, 4).Where(x => x != k).Select(x => cols[x]).ToArray(); e[k] = (k % 2 == 0 ? 1 : -1) * Det3(o[0], o[1], o[2]); }
            double en = Nrm(e); if (en < 1e-14) { why = "tangent"; return false; }
            for (int k = 0; k < 4; k++) e[k] /= en;
            // дополнение: Грам — Шмидт из базисных векторов
            var basis = new List<double[]>();
            foreach (int k in Enumerable.Range(0, 4).OrderBy(k => Math.Abs(e[k])))
            {
                if (basis.Count == 3) break;
                var v = new double[4]; v[k] = 1;
                foreach (var b in basis.Prepend(e)) { double dp = 0; for (int i = 0; i < 4; i++) dp += v[i] * b[i]; for (int i = 0; i < 4; i++) v[i] -= dp * b[i]; }
                double vn = Nrm(v); if (vn < 1e-6) continue; for (int i = 0; i < 4; i++) v[i] /= vn; basis.Add(v);
            }
            if (basis.Count < 3) { why = "basis"; return false; }
            for (int i = 0; i < 4; i++) for (int j = 0; j < 3; j++) W[i, j] = basis[j][i];
            // M = J W (3×3), K = M⁻¹
            var M = new double[3, 3];
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) { double sm = 0; for (int k = 0; k < 4; k++) sm += cols[k][r] * W[k, c]; M[r, c] = sm; }
            double det = M[0, 0] * (M[1, 1] * M[2, 2] - M[1, 2] * M[2, 1]) - M[0, 1] * (M[1, 0] * M[2, 2] - M[1, 2] * M[2, 0]) + M[0, 2] * (M[1, 0] * M[2, 1] - M[1, 1] * M[2, 0]);
            if (Math.Abs(det) < 1e-300) { why = "singular"; return false; }
            var K = new double[3, 3];
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++)
            {
                int r1 = (c + 1) % 3, r2 = (c + 2) % 3, c1 = (r + 1) % 3, c2 = (r + 2) % 3;
                K[r, c] = (M[r1, c1] * M[r2, c2] - M[r1, c2] * M[r2, c1]) / det;
            }
            T = R + step; Y = Math.Max(R, 0.5 * T) * 1.001;
            var hw = new double[4];
            for (int i = 0; i < 4; i++) { double sw = 0; for (int j = 0; j < 3; j++) sw += Math.Abs(W[i, j]); hw[i] = (T * Math.Abs(e[i]) + Y * sw) / L[i]; }
            var dA = NurbsTools.DerivRange(bezA, A.KnotsU[0], A.KnotsU[^1], A.KnotsV[0], A.KnotsV[^1], qs.X - hw[0], qs.X + hw[0], qs.Y - hw[1], qs.Y + hw[1]);
            var dB = NurbsTools.DerivRange(bezB, B.KnotsU[0], B.KnotsU[^1], B.KnotsV[0], B.KnotsV[^1], qs.Z - hw[2], qs.Z + hw[2], qs.W - hw[3], qs.W + hw[3]);
            if (dA is not { } ra2 || dB is not { } rb2) { why = "weights"; return false; }
            static Vector3d Dev(Vector3d lo, Vector3d hi, Vector3d v) => new(Math.Max(Math.Abs(lo.X - v.X), Math.Abs(hi.X - v.X)), Math.Max(Math.Abs(lo.Y - v.Y), Math.Abs(hi.Y - v.Y)), Math.Max(Math.Abs(lo.Z - v.Z), Math.Abs(hi.Z - v.Z)));
            var dJ = new[] { Dev(ra2.SuLo, ra2.SuHi, au) / L[0], Dev(ra2.SvLo, ra2.SvHi, av) / L[1], Dev(rb2.SuLo, rb2.SuHi, bu) / L[2], Dev(rb2.SvLo, rb2.SvHi, bv) / L[3] };
            // A1 = |K| |ΔJ| (3×4)
            var A1 = new double[3, 4];
            for (int r = 0; r < 3; r++) for (int k = 0; k < 4; k++) { double sm = 0; for (int m = 0; m < 3; m++) sm += Math.Abs(K[r, m]) * dJ[k][m]; A1[r, k] = sm; }
            double eMax = 0, sMax = 0;
            for (int r = 0; r < 3; r++)
            {
                double row = 0; for (int c = 0; c < 3; c++) { double sm = 0; for (int k = 0; k < 4; k++) sm += A1[r, k] * Math.Abs(W[k, c]); row += sm; }
                double sv = 0; for (int k = 0; k < 4; k++) sv += A1[r, k] * Math.Abs(e[k]);
                eMax = Math.Max(eMax, row); sMax = Math.Max(sMax, sv);
            }
            if (eMax > 0.5 || sMax > 0.25) { why = $"E {eMax:G3} s {sMax:G3} T {T:G3}"; return false; }
            // стороны прямоугольников, задевающие Ω: координата вдоль графика монотонна
            var q4 = new[] { qs.X, qs.Y, qs.Z, qs.W };
            for (int i = 0; i < 4; i++)
            {
                double sw = 0; for (int j = 0; j < 3; j++) sw += Math.Abs(W[i, j]);
                bool touches = Math.Abs(q4[i] - lo4[i]) < hw[i] || Math.Abs(q4[i] - hi4[i]) < hw[i];
                if (touches && !(Math.Abs(e[i]) > 0.5 * sw * (1 + 1e-9))) { why = $"side {i}"; return false; }
            }
            if (!reach) return true;
            // ломаная проходит дугу в Ω целиком
            bool closedC = n > 2 && (tc[0] - tc[^1]).Length < 1e-12;
            bool OnSide(Vector4d p) { var a = new[] { p.X, p.Y, p.Z, p.W }; for (int i = 0; i < 4; i++) if (Math.Abs(a[i] - lo4[i]) < 1e-9 || Math.Abs(a[i] - hi4[i]) < 1e-9) return true; return false; }
            var eL = e; var WL = W; double YL = Y, TL = T;
            int Reach(int dir)
            {
                int j = bj;
                for (int it = 0; it < n + 2; it++)
                {
                    j += dir;
                    if (j < 0 || j >= n)
                    {
                        if (!closedC) return OnSide(tc[j < 0 ? 0 : n - 1]) ? 2 : 0;
                        j = j < 0 ? n - 2 : 1;
                    }
                    var z = Z(tc[j], qs, L);
                    double t = 0; for (int i = 0; i < 4; i++) t += z[i] * eL[i];
                    for (int c = 0; c < 3; c++) { double y = 0; for (int i = 0; i < 4; i++) y += z[i] * WL[i, c]; if (Math.Abs(y) > YL) return 0; }
                    if (Math.Abs(t) > TL) return 0;
                    if (Math.Abs(t) > R) return t > 0 ? 1 : -1;
                }
                return 0;
            }
            int f = Reach(1), b2 = Reach(-1);
            if (f == 0 || b2 == 0) { why = $"trace f {f} b {b2}"; return false; }
            if (f != 2 && b2 != 2 && f == b2) { why = "trace one side"; return false; }
            return true;
        }
        int nUniq = 0, nSep = 0;
        bool UniqueNN(BezierPatch P, BezierPatch Q, Vector3d c3, double crad)
        {
            nUniq++;
            double a0 = Math.Max(P.U0, ra.U0), a1 = Math.Min(P.U1, ra.U1), b0 = Math.Max(P.V0, ra.V0), b1 = Math.Min(P.V1, ra.V1);
            double c0 = Math.Max(Q.U0, rb.U0), c1 = Math.Min(Q.U1, rb.U1), d0 = Math.Max(Q.V0, rb.V0), d1 = Math.Min(Q.V1, rb.V1);
            if (a0 > a1 || b0 > b1 || c0 > c1 || d0 > d1) return true;                 // пара вне граней
            var cen = new Vector4d((a0 + a1) / 2, (b0 + b1) / 2, (c0 + c1) / 2, (d0 + d1) / 2); var Lc = Lof(cen);
            var bestJ = new Dictionary<int, (int J, double D)>();
            foreach (int gi in grid.Within(c3, crad + 2 * h))
            {
                var (ci, cj) = grid.Id[gi]; double dd = Nrm(Z(traced[ci][cj], cen, Lc));
                if (!bestJ.TryGetValue(ci, out var bb) || dd < bb.D) bestJ[ci] = (cj, dd);
            }
            foreach (var (i, (bj, _)) in bestJ)
            {
                var (R, Y, L, e, W, _) = DiscOf(i, bj); if (R <= 0) continue;
                var qs = traced[i][bj]; bool inside = true;
                for (int m = 0; m < 16 && inside; m++)
                {
                    var cn = new Vector4d((m & 1) == 0 ? a0 : a1, (m & 2) == 0 ? b0 : b1, (m & 4) == 0 ? c0 : c1, (m & 8) == 0 ? d0 : d1);
                    var z = Z(cn, qs, L);
                    double t = 0; for (int k = 0; k < 4; k++) t += z[k] * e[k];
                    if (Math.Abs(t) > R) inside = false;
                    for (int c = 0; c < 3 && inside; c++) { double y = 0; for (int k = 0; k < 4; k++) y += z[k] * W[k, c]; if (Math.Abs(y) > Y) inside = false; }
                }
                if (inside) return true;
            }
            return false;
        }
        // РАЗДЕЛЯЮЩАЯ ОСЬ: выпуклые оболочки опорных точек кусков (строгие для положительных весов), спроектированные на нормаль
        // A или B в центре куска, не пересекаются — пары нет. У почти касания размах проекции плоского куска на его нормаль
        // убывает квадратично, а габариты по осям — линейно.
        bool Separated(BezierPatch P, BezierPatch Q)
        {
            foreach (var n in new[] { A.NormalAt(new Vector2d((P.U0 + P.U1) / 2, (P.V0 + P.V1) / 2)), B.NormalAt(new Vector2d((Q.U0 + Q.U1) / 2, (Q.V0 + Q.V1) / 2)) })
            {
                if (!(n.LengthSquared > 0)) continue;
                double pl = double.MaxValue, ph = double.MinValue, ql = double.MaxValue, qh = double.MinValue;
                foreach (var c in P.Pw) { double v = (n.X * c.X + n.Y * c.Y + n.Z * c.Z) / c.W; pl = Math.Min(pl, v); ph = Math.Max(ph, v); }
                foreach (var c in Q.Pw) { double v = (n.X * c.X + n.Y * c.Y + n.Z * c.Z) / c.W; ql = Math.Min(ql, v); qh = Math.Max(qh, v); }
                if (ph < ql - 1e-12 || qh < pl - 1e-12) return true;
            }
            return false;
        }
        var segOk = new HashSet<(int, int)>();
        string? Segments()
        {
            if (NurbsSsi.UniqOff || NurbsSsi.StepCertOff) return null;
            for (int i = 0; i < traced.Count; i++)
                for (int j = 0; j + 1 < traced[i].Count; j++)
                {
                    if (segOk.Contains((i, j))) continue;
                    var L = Lof(traced[i][j]); double len = Nrm(Z(traced[i][j + 1], traced[i][j], L));
                    {
                        // конец звена — в Ω начала: |t| ≤ R и |y|∞ ≤ Y при R = длине звена
                        if (!DiscNN(i, j, Math.Max(len * 1.01, 1e-6 * Math.Max(1, scale)), 0, L, out var Y, out var e, out var W, out _, out var why, reach: false))
                            return $"NURBS×NURBS certificate: trace step at {A.PointAt(new Vector2d(traced[i][j].X, traced[i][j].Y)):0.######} not proven single-branch ({why})";
                    }
                    segOk.Add((i, j));
                }
            return null;
        }
        string? Certify(List<Vector4d>? extra)
        {
            if (Segments() is { } segWhy) return segWhy;
            double rhoMin = 1e-4 * Math.Max(1, scale);
            var pa = bezA.Where(p => p.U1 > ra.U0 && p.U0 < ra.U1 && p.V1 > ra.V0 && p.V0 < ra.V1).ToList();
            var pb = bezB.Where(p => p.U1 > rb.U0 && p.U0 < rb.U1 && p.V1 > rb.V0 && p.V0 < rb.V1).ToList();
            var stack = new Stack<(BezierPatch P, BezierPatch Q)>();
            foreach (var x in pa) foreach (var y in pb) stack.Push((x, y));
            int visited = 0; string? fail = null;
            while (stack.Count > 0)
            {
                if (++visited > 400000) return "NURBS×NURBS certificate: too many cell pairs";
                var (P, Q) = stack.Pop();
                var (la, ha) = P.Box(); var (lb, hb) = Q.Box();
                if (la.X > hb.X || lb.X > ha.X || la.Y > hb.Y || lb.Y > ha.Y || la.Z > hb.Z || lb.Z > ha.Z) continue;   // врозь
                if (!NurbsSsi.UniqOff && Separated(P, Q)) { nSep++; continue; }                         // врозь по оси нормали (у почти касания)
                var ca = (la + ha) / 2; var cb = (lb + hb) / 2; double rA = (ha - la).Length / 2, rB = (hb - lb).Length / 2;
                if (Near(ca, rA + 2 * h) && Near(cb, rB + 2 * h))
                {
                    if (NurbsSsi.UniqOff) continue;                                        // прежде: «близко — значит пояснена»
                    if (UniqueNN(P, Q, ca, rA)) continue;                                          // доказано: ветвь в паре — найденная
                    if (Math.Max(rA, rB) > rhoMin)
                    {
                        if (rA >= rB) { var (x1, y1) = P.Split(P.U1 - P.U0 >= P.V1 - P.V0); stack.Push((x1, Q)); stack.Push((y1, Q)); }
                        else { var (x1, y1) = Q.Split(Q.U1 - Q.U0 >= Q.V1 - Q.V0); stack.Push((P, x1)); stack.Push((P, y1)); }
                        continue;
                    }
                    var qm = new Vector4d(Math.Clamp((P.U0 + P.U1) / 2, ra.U0, ra.U1), Math.Clamp((P.V0 + P.V1) / 2, ra.V0, ra.V1), Math.Clamp((Q.U0 + Q.U1) / 2, rb.U0, rb.U1), Math.Clamp((Q.V0 + Q.V1) / 2, rb.V0, rb.V1));
                    if (Correct(A, B, qm, null, null) is { } q2 && In(q2) && !Near(A.PointAt(new Vector2d(q2.X, q2.Y)), 0.75 * h))
                    {
                        if (extra == null) return $"NURBS×NURBS certificate: an intersection point near {A.PointAt(new Vector2d(q2.X, q2.Y)):0.######} is on no traced curve";
                        extra.Add(q2); fail ??= "new seeds"; continue;
                    }
                    return $"NURBS×NURBS certificate: branch uniqueness not proven near {ca:0.######}";
                }
                if (Math.Max(rA, rB) > rhoMin)
                {
                    if (rA >= rB) { var (x, y) = P.Split(P.U1 - P.U0 >= P.V1 - P.V0); stack.Push((x, Q)); stack.Push((y, Q)); }
                    else { var (x, y) = Q.Split(Q.U1 - Q.U0 >= Q.V1 - Q.V0); stack.Push((P, x)); stack.Push((P, y)); }
                    continue;
                }
                var q0 = new Vector4d(Math.Clamp((P.U0 + P.U1) / 2, ra.U0, ra.U1), Math.Clamp((P.V0 + P.V1) / 2, ra.V0, ra.V1), Math.Clamp((Q.U0 + Q.U1) / 2, rb.U0, rb.U1), Math.Clamp((Q.V0 + Q.V1) / 2, rb.V0, rb.V1));
                if (Correct(A, B, q0, null, null) is { } qq)
                {
                    if (NurbsSsi.UniqOff && !In(qq)) continue;
                    var x3 = A.PointAt(new Vector2d(qq.X, qq.Y));
                    if (NurbsSsi.UniqOff && Near(x3, 2 * h)) continue;
                    if (!In(qq) || Near(x3, 0.75 * h)) return $"NURBS×NURBS certificate: unexplained near-contact cell pair at {ca:0.######}";
                    if (extra == null) return $"NURBS×NURBS certificate: an intersection point near {x3:0.######} is on no traced curve";
                    extra.Add(qq); fail ??= "new seeds"; continue;
                }
                return $"NURBS×NURBS certificate: unexplained near-contact cell pair at {ca:0.######}";
            }
            Debug?.Invoke($"certify: visited {visited}, separated {nSep}, uniqueness checks {nUniq}, discs {discs.Count} ({discMs:F0} ms), points {traced.Sum(c => c.Count)}");
            return fail;
        }
        var swC = System.Diagnostics.Stopwatch.StartNew();
        for (int round = 0; round < 3; round++)
        {
            Debug?.Invoke($"certify round {round} at {swC.ElapsedMilliseconds} ms");
            var extra = new List<Vector4d>();
            var why = Certify(extra);
            if (why == null) break;
            int before = traced.Count;
            foreach (var q in extra) TraceFrom(q);
            if (traced.Count == before || round == 2) { if (Certify(null) is { } w2) res.Uncertified = w2; break; }
        }
        foreach (var c in traced) if (c.Count >= 2) res.Curves.Add(new NurbsNurbsCurve3(A, B, c));
        res.Kind = res.Curves.Count > 0 ? Ssi.Kind.Curves : Ssi.Kind.Empty;
        return res;
    }
}

/// <summary>Кривая пересечения двух граней NURBS: ломаная в (u₁, v₁, u₂, v₂), точка — на первой поверхности после поправки
/// (S₁ = S₂ до 1e-12) в плоскости, поперечной хорде; параметр — доля длины пути.</summary>
public sealed class NurbsNurbsCurve3 : ICurve3
{
    public readonly NurbsSurface A, B;
    readonly Vector4d[] _q; readonly double[] _s; readonly Vector3d[] _p;
    public IReadOnlyList<Vector4d> Seed => _q;
    public NurbsNurbsCurve3(NurbsSurface a, NurbsSurface b, IReadOnlyList<Vector4d> q)
    {
        A = a; B = b; _q = q.ToArray(); _s = new double[_q.Length]; _p = _q.Select(x => A.PointAt(new Vector2d(x.X, x.Y))).ToArray();
        for (int i = 1; i < _q.Length; i++) _s[i] = _s[i - 1] + (_p[i] - _p[i - 1]).Length;
    }
    public double T0 => 0; public double T1 => 1;
    public bool IsClosed => _q.Length > 2 && (_p[0] - _p[^1]).Length < 1e-10;
    public double Length => _s[^1];
    public IEnumerable<double> Breaks { get { double tot = _s[^1]; if (tot <= 0) yield break; for (int i = 1; i < _s.Length - 1; i++) yield return _s[i] / tot; } }
    public Vector4d QAt(double t)
    {
        double tot = _s[^1]; if (tot <= 0) return _q[0];
        double L = Math.Clamp(t, 0, 1) * tot; int lo = 0, hi = _s.Length - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (_s[m] <= L) lo = m; else hi = m; }
        double seg = _s[hi] - _s[lo], a = seg > 0 ? (L - _s[lo]) / seg : 0;
        if (a <= 0) return _q[lo]; if (a >= 1) return _q[hi];
        var q = _q[lo] + (_q[hi] - _q[lo]) * a; var x0 = _p[lo] + (_p[hi] - _p[lo]) * a; var tv = (_p[hi] - _p[lo]).Normalized();
        return NurbsNurbsSsi.Correct(A, B, q, x0, tv) ?? q;
    }
    public Vector3d PointAt(double t) { var q = QAt(t); return A.PointAt(new Vector2d(q.X, q.Y)); }
    public Vector3d TangentAt(double t) { double h = 1e-6; var d = PointAt(Math.Min(1, t + h)) - PointAt(Math.Max(0, t - h)); return d.LengthSquared < 1e-30 ? Vector3d.UnitX : d.Normalized(); }
    public Bounds3 Bounds
    {
        get
        {
            var b = Bounds3.Empty; foreach (var p in _p) b.Add(p);
            double step = 0; for (int i = 1; i < _s.Length; i++) step = Math.Max(step, _s[i] - _s[i - 1]);
            b.Add(b.Min - new Vector3d(step)); b.Add(b.Max + new Vector3d(step)); return b;
        }
    }
    public ICurve3 Reversed() => new NurbsNurbsCurve3(A, B, _q.Reverse().ToList());
    public void Tessellate(List<Vector3d> dst, bool includeFirstPoint) { for (int i = includeFirstPoint ? 0 : 1; i < _p.Length; i++) dst.Add(_p[i]); }
}
