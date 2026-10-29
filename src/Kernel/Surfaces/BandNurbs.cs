using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// KRN-06/обмен: КУСОК ЛЕНТЫ ПО ФОРМУЛЕ СЕЧЕНИЯ (дуга окружности в каждом сечении) — РАЦИОНАЛЬНЫМ B-СПЛАЙНОМ для записи в
/// STEP. По v — дуга точно (рациональная квадратичная: P0, P1 на пересечении касательных, P2; веса 1, cos(φ/2), 1); по u —
/// кубическая интерполяция однородных координат (w·P, w) трёх рядов через N + 1 сечений с равным шагом (глобальная
/// интерполяция, усреднённые узлы). В сечениях-узлах поверхность точна, между ними — приближение; отход измеряется в
/// серединах промежутков (5 значений v) по самой формуле, N удваивается до бюджета. null — бюджет не достигнут или дуга
/// не годится для одного рационального сегмента (угол ≥ π).
/// </summary>
public static class BandNurbs
{
    public static (NurbsSurface S, double Deviation)? Fit(SectionSurface ss, double ua, double ub, double budget, int maxN = 1024, int minN = 16)
    {
        if (!ss.IsProcedural) return null;
        // KRN-06: стыки формулы внутри промежутка (узлы закона радиуса: сечение — характеристическая окружность, она зависит
        // от r', а r'' у сплайна закона на узле скачет — сечения там лишь непрерывны) — узлы кратности 3 на стыках, интерполяция в точках Гревиля; без них кубика через стык сходилась медленно и бюджет не
        // достигался (точный STEP ленты с законом — отказ). Мутация P3D_BANDNURBSBREAKS=0.
        var brk = ss.Breaks.Where(x => x > ua + 1e-9 * (ub - ua) && x < ub - 1e-9 * (ub - ua)).OrderBy(x => x).ToList();
        if (brk.Count > 0 && !BreaksOff) return FitBroken(ss, ua, ub, brk, budget, maxN);
        for (int n = Math.Max(16, minN); n <= maxN; n *= 2)
        {
            var secs = new SweepSection[n + 1];
            // однородные опорные точки сечений: R рядов × (n + 1) сечений (дуга — рациональная квадратичная, отрезок — та же
            // степень 2 с серединой; KRN-07: грани точной протяжки со звеньями-отрезками; m.s1: звено-кривая Безье — её опорные
            // точки, степень по v — её степень)
            int NR = RowCount(ss.ExactSectionAt(ua));
            var H = new Vector4d[NR, n + 1];
            for (int i = 0; i <= n; i++)
            {
                secs[i] = ss.ExactSectionAt(ua + (ub - ua) * i / n);
                if (Rows(secs[i]) is not { } ar || ar.Length != NR) return null;
                for (int r = 0; r < NR; r++) H[r, i] = ar[r];
            }
            // глобальная кубическая интерполяция по равномерным параметрам t_i = i/n
            int deg = 3, m = n + 1;
            var t = Enumerable.Range(0, m).Select(i => (double)i / n).ToArray();
            var knots = new double[m + deg + 1];
            for (int i = 0; i <= deg; i++) { knots[i] = 0; knots[^(i + 1)] = 1; }
            for (int j = 1; j < m - deg; j++) knots[j + deg] = t.Skip(j).Take(deg).Average();
            var A = new double[m, m];
            for (int i = 0; i < m; i++) { var nb = Basis(knots, deg, t[i], m); for (int j = 0; j < m; j++) A[i, j] = nb[j]; }
            var D = new Vector4d[NR, m];
            for (int row = 0; row < NR; row++)
            {
                var rhs = new Vector4d[m]; for (int i = 0; i < m; i++) rhs[i] = H[row, i];
                var sol = Solve(A, rhs, m); if (sol == null) return null;
                for (int j = 0; j < m; j++) D[row, j] = sol[j];
            }
            var cp = new Vector3d[m, NR]; var w = new double[m, NR];
            for (int j = 0; j < m; j++)
                for (int row = 0; row < NR; row++)
                {
                    var d = D[row, j]; if (!(d.W > 1e-9)) return null;
                    cp[j, row] = d.Xyz / d.W; w[j, row] = d.W;
                }
            var nurbs = new NurbsSurface(cp, deg, NR - 1, w, knots, VKnots(NR));
            double dev = 0;
            for (int i = 0; i < n; i++)
            {
                double tm = (i + 0.5) / n; var ex = ss.ExactSectionAt(ua + (ub - ua) * tm);
                foreach (double v in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
                {
                    dev = Math.Max(dev, OffSection(nurbs.PointAt(new Vector2d(tm, v)), ex));
                }
            }
            if (FitDbg) Console.WriteLine($"BANDFIT n {n} dev {dev:E2} budget {budget:E1}");
            if (FitDbg) Console.WriteLine($"BANDFIT broken {brk.Count} n {n} dev {dev:E2} budget {budget:E1}");
            if (dev <= budget) return (nurbs, dev);
        }
        return null;
    }
    static readonly bool FitDbg = Environment.GetEnvironmentVariable("P3D_BANDFITDBG") == "1";

    /// <summary>Расстояние точки до сечения: до дуги (её плоскость и окружность) или до прямой отрезка.</summary>
    static double OffSection(Vector3d q, SweepSection ex)
    {
        if (ex.IsBezier)
        {
            // ближайшая точка кривой Безье: пробы, затем Ньютон по (P(v) − q)·P'(v) = 0
            double bv = 0, bd = double.MaxValue;
            for (int k = 0; k <= 64; k++) { double v = k / 64.0; double d = (ex.Point(v) - q).LengthSquared; if (d < bd) { bd = d; bv = v; } }
            for (int it = 0; it < 30; it++)
            {
                const double h = 1e-5;
                double g(double v) => Vector3d.Dot(ex.Point(v) - q, ex.Deriv(v));
                double g0 = g(bv), gd = (g(bv + h) - g(bv - h)) / (2 * h);
                if (!(Math.Abs(gd) > 1e-300)) break;
                double nv = Math.Clamp(bv - g0 / gd, 0, 1); if (Math.Abs(nv - bv) < 1e-15) { bv = nv; break; } bv = nv;
            }
            return Math.Min(Math.Sqrt(bd), (ex.Point(bv) - q).Length);
        }
        if (!ex.IsArc) { var d = ex.B - ex.A; double l2 = d.LengthSquared; var w = q - ex.A; return l2 < 1e-300 ? w.Length : (w - d * (Vector3d.Dot(w, d) / l2)).Length; }
        // точка рационального сплайна при параметре v — не точка сечения при той же доле угла: мерить расстояние до дуги
        var nrm = Vector3d.Cross(ex.E1, ex.E2).Normalized(); var dq = q - ex.C; double off = Vector3d.Dot(dq, nrm);
        var inPlane = dq - nrm * off; double rad = inPlane.Length - ex.R;
        return Math.Sqrt(off * off + rad * rad);
    }

    static readonly bool PinchOff = Environment.GetEnvironmentVariable("P3D_BANDPINCH") == "0";
    static readonly bool BreaksOff = Environment.GetEnvironmentVariable("P3D_BANDNURBSBREAKS") == "0";

    static int RowCount(SweepSection s) => s.IsBezier ? s.Q!.Length : 3;
    static double[] VKnots(int nr) { var k = new double[2 * nr]; for (int i = nr; i < 2 * nr; i++) k[i] = 1; return k; }

    /// <summary>Однородные опорные точки сечения по v (дуга, отрезок — 3; кривая Безье — свои, веса 1).</summary>
    static Vector4d[]? Rows(SweepSection s)
    {
        if (s.IsBezier) return s.Q!.Select(p => new Vector4d(p, 1)).ToArray();
        return ArcRows(s) is { } r ? new[] { r.P0, r.P1, r.P2 } : null;
    }

    static (Vector4d P0, Vector4d P1, Vector4d P2)? ArcRows(SweepSection s)
    {
        if (!s.IsArc) return (new Vector4d(s.A, 1), new Vector4d((s.A + s.B) / 2, 1), new Vector4d(s.B, 1));
        // 06.12: сечение, стянутое в точку (лента-«линза» тройника равных труб в сёдлах, φ = 0) — три совпавшие точки с весами 1:
        // формула та же, ряд непрерывен. Мутация P3D_BANDPINCH=0 — отказ, как прежде.
        if (!((PinchOff ? s.Phi > 1e-9 : s.Phi >= 0) && s.Phi < Math.PI - 1e-6)) return null;
        double h = s.Phi / 2, wm = Math.Cos(h);
        var p0 = s.C + s.E1 * s.R; var p2 = s.C + (s.E1 * Math.Cos(s.Phi) + s.E2 * Math.Sin(s.Phi)) * s.R;
        var p1 = s.C + (s.E1 + s.E2 * Math.Tan(h)) * s.R;
        return (new Vector4d(p0, 1), new Vector4d(p1 * wm, wm), new Vector4d(p2, 1));
    }

    static (NurbsSurface S, double Deviation)? FitBroken(SectionSurface ss, double ua, double ub, List<double> brk, double budget, int maxN)
    {
        // доли промежутка: куски между стыками
        var cuts = new List<double> { 0 }; cuts.AddRange(brk.Select(x => (x - ua) / (ub - ua))); cuts.Add(1);
        for (int n = 16; n <= maxN; n *= 2)
        {
            var kn = new List<double> { 0, 0, 0, 0 };
            for (int p = 0; p + 1 < cuts.Count; p++)
            {
                int k = Math.Max(2, (int)Math.Ceiling(n * (cuts[p + 1] - cuts[p])));
                for (int s = 1; s < k; s++) kn.Add(cuts[p] + (cuts[p + 1] - cuts[p]) * s / k);
                if (p + 2 < cuts.Count) { kn.Add(cuts[p + 1]); kn.Add(cuts[p + 1]); kn.Add(cuts[p + 1]); }
            }
            kn.Add(1); kn.Add(1); kn.Add(1); kn.Add(1);
            var U = kn.ToArray(); int m = U.Length - 4;
            var g = new double[m]; for (int i = 0; i < m; i++) g[i] = (U[i + 1] + U[i + 2] + U[i + 3]) / 3;
            var A = new double[m, m]; for (int i = 0; i < m; i++) { var nb = Basis(U, 3, g[i], m); for (int j = 0; j < m; j++) A[i, j] = nb[j]; }
            int NR = RowCount(ss.ExactSectionAt(ua));
            var rows = new Vector4d[NR][]; for (int r = 0; r < NR; r++) rows[r] = new Vector4d[m];
            for (int i = 0; i < m; i++)
            {
                // на стыке — сечение со своей стороны не нужно: формула непрерывна, точка Гревиля на стыке — само сечение
                if (Rows(ss.ExactSectionAt(ua + (ub - ua) * g[i])) is not { } ar || ar.Length != NR) return null;
                for (int r = 0; r < NR; r++) rows[r][i] = ar[r];
            }
            var cp = new Vector3d[m, NR]; var w = new double[m, NR];
            for (int r = 0; r < NR; r++)
            {
                var sol = Solve(A, rows[r], m); if (sol == null) return null;
                for (int j = 0; j < m; j++) { if (!(sol[j].W > 1e-9)) return null; cp[j, r] = sol[j].Xyz / sol[j].W; w[j, r] = sol[j].W; }
            }
            var nurbs = new NurbsSurface(cp, 3, NR - 1, w, U, VKnots(NR));
            double dev = 0;
            for (int i = 0; i + 1 < m && dev <= budget; i++)
            {
                double tm = (g[i] + g[i + 1]) / 2; var ex = ss.ExactSectionAt(ua + (ub - ua) * tm);
                foreach (double v in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }) dev = Math.Max(dev, OffSection(nurbs.PointAt(new Vector2d(tm, v)), ex));
            }
            if (dev <= budget) return (nurbs, dev);
        }
        return null;
    }

    internal static double[] Basis(double[] U, int p, double u, int n)
    {
        var N = new double[n];
        if (u >= U[n]) { N[n - 1] = 1; return N; }
        int span = p; while (span < n - 1 && u >= U[span + 1]) span++;
        var left = new double[p + 1]; var right = new double[p + 1]; var Nd = new double[p + 1]; Nd[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - U[span + 1 - j]; right[j] = U[span + j] - u; double saved = 0;
            for (int r = 0; r < j; r++) { double tmp = Nd[r] / (right[r + 1] + left[j - r]); Nd[r] = saved + right[r + 1] * tmp; saved = left[j - r] * tmp; }
            Nd[j] = saved;
        }
        for (int j = 0; j <= p; j++) N[span - p + j] = Nd[j];
        return N;
    }

    internal static Vector4d[]? Solve(double[,] A0, Vector4d[] b0, int n)
    {
        var A = (double[,])A0.Clone(); var b = (Vector4d[])b0.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r;
            if (Math.Abs(A[piv, c]) < 1e-14) return null;
            if (piv != c) { for (int k = 0; k < n; k++) (A[c, k], A[piv, k]) = (A[piv, k], A[c, k]); (b[c], b[piv]) = (b[piv], b[c]); }
            for (int r = c + 1; r < n; r++)
            {
                double f = A[r, c] / A[c, c]; if (f == 0) continue;
                for (int k = c; k < n; k++) A[r, k] -= f * A[c, k];
                b[r] -= b[c] * f;
            }
        }
        var x = new Vector4d[n];
        for (int r = n - 1; r >= 0; r--) { var s = b[r]; for (int k = r + 1; k < n; k++) s -= x[k] * A[r, k]; x[r] = s / A[r, r]; }
        return x;
    }
}

/// <summary>
/// KRN-06/обмен: УГЛОВОЙ ЛОСКУТ (CornerPatchSurface) — КУБИЧЕСКИМ B-СПЛАЙНОМ для записи в STEP. Квадрат (ξ, η) отображается
/// на многоугольник области лоскута по Кунсу от его края: 2n сторон делятся на четыре стороны квадрата поровну у
/// противоположных (a, b, a, b), каждая сторона многоугольника — равная доля стороны квадрата. Внутри каждой клетки
/// между изломами края (ξ = i/a, η = j/b) отображение билинейно — составная поверхность гладкая; на изломах — узлы
/// кратности 3 (C0). Интерполяция в точках Гревиля (тензорно), K пролётов на клетку удваиваются до бюджета; отход
/// мерится в серединах между точками Гревиля по самой формуле лоскута (при том же (ξ, η) — строже расстояния до
/// поверхности). Край квадрата ложится на край лоскута, рёбра грани — на сплайн в пределах отхода. null — бюджет не
/// достигнут или отображение складывается.
/// </summary>
public static class CornerPatchNurbs
{
    public static (NurbsSurface S, double Deviation)? Fit(CornerPatchSurface cp, double budget, int maxK = 32)
    {
        int m = cp.SideCount; if (m % 2 != 0) return null;
        int n2 = m / 2, a = (n2 + 1) / 2, b = n2 / 2; if (b < 1) return null;
        Vector2d V(int k) => cp.Vertex(k);
        Vector2d Seg(Vector2d p, Vector2d q, double t) => p + (q - p) * t;
        // края квадрата
        Vector2d B0(double x) { double y = Math.Clamp(x, 0, 1) * a; int i = Math.Min((int)y, a - 1); return Seg(V(i), V(i + 1), y - i); }
        Vector2d B1(double x) { double y = Math.Clamp(x, 0, 1) * b; int j = Math.Min((int)y, b - 1); return Seg(V(a + j), V(a + j + 1), y - j); }
        Vector2d B2(double x) { double y = Math.Clamp(x, 0, 1) * a; int i = Math.Min((int)y, a - 1); return Seg(V(2 * a + b - i), V(2 * a + b - i - 1), y - i); }
        Vector2d B3(double x) { double y = Math.Clamp(x, 0, 1) * b; int j = Math.Min((int)y, b - 1); return Seg(V(m - j), V(m - j - 1), y - j); }
        var P00 = V(0); var P10 = V(a); var P11 = V(a + b); var P01 = V(2 * a + b);
        Vector2d Coons(double x, double y) =>
            B0(x) * (1 - y) + B2(x) * y + B3(y) * (1 - x) + B1(y) * x - (P00 * ((1 - x) * (1 - y)) + P10 * (x * (1 - y)) + P01 * ((1 - x) * y) + P11 * (x * y));
        // отображение не складывается: якобиан одного знака на сетке
        {
            double sgn = 0;
            for (int i = 0; i <= 40; i++)
                for (int j = 0; j <= 40; j++)
                {
                    double x = (i + 0.5) / 41, y = (j + 0.5) / 41, h = 1e-6;
                    var dx = (Coons(x + h, y) - Coons(x - h, y)) / (2 * h); var dy = (Coons(x, y + h) - Coons(x, y - h)) / (2 * h);
                    double J = dx.X * dy.Y - dx.Y * dy.X;
                    if (sgn == 0) sgn = Math.Sign(J); else if (Math.Sign(J) != sgn || Math.Abs(J) < 1e-9) { if (Dbg) Console.WriteLine($"CORNERFIT fold at {x:G3},{y:G3} J {J:E2}"); return null; }
                }
        }
        // узлы клетки: K равных пролётов, крайние — ещё сгущены к концам делением пополам (Lg раз): в вершинах многоугольника
        // формула лоскута лишь C1 (смесь лент направленно зависит от подхода), и при равных пролётах сходимость была второго
        // порядка (отход 1e-4 при 69×69); сгущение к вершинам — сходимость как у гладкой
        static double[] Knots(int pieces, int K, int Lg)
        {
            var loc = new SortedSet<double>();
            double h = 1.0 / K;
            for (int s = 1; s < K; s++) loc.Add(s * h);
            for (int l = 1; l <= Lg; l++) { loc.Add(h / (1 << l)); loc.Add(1 - h / (1 << l)); }
            var kn = new List<double> { 0, 0, 0, 0 };
            for (int p = 0; p < pieces; p++)
            {
                foreach (double x in loc) kn.Add((p + x) / pieces);
                if (p < pieces - 1) { double br = (double)(p + 1) / pieces; kn.Add(br); kn.Add(br); kn.Add(br); }
            }
            kn.Add(1); kn.Add(1); kn.Add(1); kn.Add(1);
            return kn.ToArray();
        }
        static double[] Greville(double[] U) { int n = U.Length - 4; var g = new double[n]; for (int i = 0; i < n; i++) g[i] = (U[i + 1] + U[i + 2] + U[i + 3]) / 3; return g; }
        for (int K = 4; K <= maxK; K *= 2)
        {
            int Lg = GradeLayers;
            var Uu = Knots(a, K, Lg); var Uv = Knots(b, K, Lg);
            var gu = Greville(Uu); var gv = Greville(Uv); int nu = gu.Length, nv = gv.Length;
            var Au = new double[nu, nu]; for (int i = 0; i < nu; i++) { var nb = BandNurbs.Basis(Uu, 3, gu[i], nu); for (int k = 0; k < nu; k++) Au[i, k] = nb[k]; }
            var Av = new double[nv, nv]; for (int j = 0; j < nv; j++) { var nb = BandNurbs.Basis(Uv, 3, gv[j], nv); for (int k = 0; k < nv; k++) Av[j, k] = nb[k]; }
            var D = new Vector4d[nu, nv];
            for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) D[i, j] = new Vector4d(cp.PointAt(Coons(gu[i], gv[j])), 0);
            // по u для каждого столбца, затем по v для каждой строки
            var X = new Vector4d[nu, nv];
            for (int j = 0; j < nv; j++)
            {
                var col = new Vector4d[nu]; for (int i = 0; i < nu; i++) col[i] = D[i, j];
                var sol = BandNurbs.Solve(Au, col, nu); if (sol == null) return null;
                for (int i = 0; i < nu; i++) X[i, j] = sol[i];
            }
            var ctrl = new Vector3d[nu, nv];
            for (int i = 0; i < nu; i++)
            {
                var row = new Vector4d[nv]; for (int j = 0; j < nv; j++) row[j] = X[i, j];
                var sol = BandNurbs.Solve(Av, row, nv); if (sol == null) return null;
                for (int j = 0; j < nv; j++) ctrl[i, j] = sol[j].Xyz;
            }
            var ns = new NurbsSurface(ctrl, 3, 3, null, Uu, Uv);
            double dev = 0; double wx = 0, wy = 0;
            for (int i = 0; i + 1 < nu && (dev <= budget || Dbg); i++)
                for (int j = 0; j + 1 < nv; j++)
                {
                    double x = (gu[i] + gu[i + 1]) / 2, y = (gv[j] + gv[j + 1]) / 2;
                    foreach (var (xx, yy) in new[] { (x, y), (gu[i], y), (x, gv[j]) })
                    {
                        double dd = (ns.PointAt(new Vector2d(xx, yy)) - cp.PointAt(Coons(xx, yy))).Length;
                        if (dd > dev) { dev = dd; wx = xx; wy = yy; }
                    }
                }
            if (Dbg) Console.WriteLine($"CORNERFIT K {K} poles {nu}x{nv} dev {dev:E2} at ({wx:G4}, {wy:G4}) uv {Coons(wx, wy):G4} budget {budget:E1}");
            if (dev <= budget) return (ns, dev);
        }
        return null;
    }
    static readonly bool Dbg = Environment.GetEnvironmentVariable("P3D_CORNERFITDBG") == "1";
    static readonly int GradeLayers = int.TryParse(Environment.GetEnvironmentVariable("P3D_CORNERGRADE"), out var gl) ? gl : 4;
}
