using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_LOFTNURBS=0: лофт по отрезкам и дугам — прежним путём (ломаные, многогранно).</summary>
    internal static readonly bool LoftNurbsOff = Environment.GetEnvironmentVariable("P3D_LOFTNURBS") == "0";
    public static long NurbsLofts;

    /// <summary>
    /// KRN-07 (07.4): ТОЧНЫЙ ЛОФТ ПО ОТРЕЗКАМ И ДУГАМ. Каждое сечение — замкнутый контур из N отрезков и дуг (у всех сечений N
    /// одно, звенья сопоставлены по порядку). Звено j каждого сечения представляется одинаковым числом K_j рациональных
    /// квадратичных кусков Безье (дуга — точно, отрезок — делением), контур — одна NURBS-кривая степени 2 с двойными узлами
    /// на стыках кусков. Поперёк сечений — интерполяция однородных опорных точек: линейчатый лофт (степень 1, грань на
    /// каждый пролёт) или гладкий (степень до 3, глобальная интерполяция по параметру длины хода через центры сечений; одна
    /// грань на звено). Сечения — точные края поверхности (интерполяция в однородных координатах их воспроизводит);
    /// боковые рёбра — NURBS-кривые столбцов опорных точек; торцы — плоские грани по самим сечениям. Тело собирается и
    /// проверяется (годность, аналитический объём, объём по фасетам). null — отказ (причина в why).
    /// </summary>
    internal static TSolid? NurbsLoft(IReadOnlyList<(List<ICurve3> Loop, Vector3d Normal)> sections, bool smooth, ref string? why)
    {
        if (LoftNurbsOff) { why = "NURBS loft is off"; return null; }
        int S = sections.Count;
        if (S < 2) { why = "loft: fewer than two sections"; return null; }
        int N = sections[0].Loop.Count;
        if (N == 0 || sections.Any(s => s.Loop.Count != N)) { why = "loft: sections differ in the number of curves"; return null; }
        if (sections.Any(s => s.Loop.Any(c => c is not LineCurve3 && c is not ArcCurve3))) { why = "loft: a section curve is neither a line nor an arc"; return null; }

        static Vector3d Start(ICurve3 c) => c.PointAt(c.T0);
        static Vector3d End(ICurve3 c) => c.PointAt(c.T1);
        static ICurve3 Rev(ICurve3 c) => c switch
        {
            LineCurve3 l => new LineCurve3(l.B, l.A),
            ArcCurve3 a => new ArcCurve3(a.Frame, a.Radius, a.A1, a.A0),
            _ => c,
        };
        var cent = sections.Select(s => s.Loop.Aggregate(Vector3d.Zero, (acc, c) => acc + Start(c)) / N).ToList();
        var run = cent[^1] - cent[0];
        if (run.Length < 1e-9) { why = "loft: the sections lie on top of each other"; return null; }
        run.Normalize();

        // обход: у всех сечений против часовой стрелки, если смотреть по ходу (векторная площадь по ходу)
        var loops = new List<List<ICurve3>>();
        foreach (var (loop, _) in sections)
        {
            var area = Vector3d.Zero;
            foreach (var c in loop) for (int k = 0; k < 16; k++) { var a = c.PointAt(c.T0 + (c.T1 - c.T0) * k / 16.0); var b = c.PointAt(c.T0 + (c.T1 - c.T0) * (k + 1) / 16.0); area += Vector3d.Cross(a, b); }
            var l2 = loop.ToList();
            for (int i = 0; i < N; i++) if ((End(l2[i]) - Start(l2[(i + 1) % N])).Length > 1e-7) { why = "loft: a section loop is not chained"; return null; }
            if (Vector3d.Dot(area, run) < 0) { l2.Reverse(); l2 = l2.Select(Rev).ToList(); }
            loops.Add(l2);
        }
        // сопоставление звеньев: начало каждого следующего сечения — поворот, ближайший по вершинам относительно центров
        for (int k = 1; k < S; k++)
        {
            int best = 0; double bd = double.MaxValue;
            for (int r = 0; r < N; r++)
            {
                double d = 0;
                for (int i = 0; i < N; i++) d += ((Start(loops[k][(i + r) % N]) - cent[k]) - (Start(loops[k - 1][i]) - cent[k - 1])).Length;
                if (d < bd - 1e-9) { bd = d; best = r; }
            }
            loops[k] = Enumerable.Range(0, N).Select(i => loops[k][(i + best) % N]).ToList();
        }

        // куски звеньев
        var K = new int[N];
        for (int j = 0; j < N; j++)
            K[j] = loops.Max(l => l[j] is ArcCurve3 a ? Math.Max(1, (int)Math.Ceiling(Math.Abs(a.Sweep) / (Math.PI / 2) - 1e-9)) : 1);
        int M = K.Sum();
        int nu = 2 * M + 1;
        var colStart = new int[N + 1]; for (int j = 0; j < N; j++) colStart[j + 1] = colStart[j] + 2 * K[j];
        // однородные опорные точки сечений: (w·P, w)
        var H = new Vector4d[S, nu];
        for (int k = 0; k < S; k++)
        {
            int idx = 0;
            for (int j = 0; j < N; j++)
            {
                var c = loops[k][j];
                for (int p = 0; p < K[j]; p++)
                {
                    Vector3d P0, P1, P2; double w1;
                    if (c is LineCurve3 l)
                    {
                        P0 = l.A + (l.B - l.A) * ((double)p / K[j]); P2 = l.A + (l.B - l.A) * ((double)(p + 1) / K[j]);
                        P1 = (P0 + P2) / 2; w1 = 1;
                    }
                    else
                    {
                        var a = (ArcCurve3)c;
                        double t0 = a.A0 + a.Sweep * p / K[j], t1 = a.A0 + a.Sweep * (p + 1) / K[j], tm = (t0 + t1) / 2, half = Math.Abs(t1 - t0) / 2;
                        P0 = a.Frame.ToWorld(Math.Cos(t0) * a.Radius, Math.Sin(t0) * a.Radius);
                        P2 = a.Frame.ToWorld(Math.Cos(t1) * a.Radius, Math.Sin(t1) * a.Radius);
                        double rr = a.Radius / Math.Cos(half);
                        P1 = a.Frame.ToWorld(Math.Cos(tm) * rr, Math.Sin(tm) * rr); w1 = Math.Cos(half);
                    }
                    if (idx == 0) H[k, idx++] = new Vector4d(P0, 1);
                    H[k, idx++] = new Vector4d(P1 * w1, w1);
                    H[k, idx++] = new Vector4d(P2, 1);
                }
            }
            if ((H[k, nu - 1].Xyz - H[k, 0].Xyz).Length > 1e-7) { why = "loft: a section does not close"; return null; }
        }

        // поперёк сечений
        var vpar = new double[S];
        for (int k = 1; k < S; k++) vpar[k] = vpar[k - 1] + (cent[k] - cent[k - 1]).Length;
        for (int k = 1; k < S; k++) vpar[k] /= vpar[S - 1];
        int dv = smooth ? Math.Min(3, S - 1) : 1;
        double[] kv; Vector4d[,] Q;
        if (dv == 1)
        {
            kv = new double[S + 2]; kv[0] = 0; for (int k = 0; k < S; k++) kv[k + 1] = vpar[k]; kv[S + 1] = 1;
            Q = H;
        }
        else
        {
            kv = new double[S + dv + 1];
            for (int i = 0; i <= dv; i++) { kv[i] = 0; kv[S + i] = 1; }
            for (int j = 1; j < S - dv; j++) { double sum = 0; for (int i = j; i < j + dv; i++) sum += vpar[i]; kv[j + dv] = sum / dv; }
            // глобальная интерполяция: A·Q = H по каждому столбцу (4D)
            var A = new double[S, S];
            for (int k = 0; k < S; k++) for (int i = 0; i < S; i++) A[k, i] = Basis(kv, dv, i, vpar[k]);
            Q = new Vector4d[S, nu];
            var inv = Inverse(A);
            if (inv == null) { why = "loft: interpolation matrix is singular"; return null; }
            for (int c = 0; c < nu; c++)
                for (int i = 0; i < S; i++)
                {
                    var acc = Vector4d.Zero;
                    for (int k = 0; k < S; k++) acc += H[k, c] * inv[i, k];
                    Q[i, c] = acc;
                }
            for (int i = 0; i < S; i++) for (int c = 0; c < nu; c++) if (!(Q[i, c].W > 1e-6)) { why = "loft: a non-positive weight after interpolation"; return null; }
        }

        // поверхности звеньев (и пролётов у линейчатого)
        var faces = new List<FDesc>();
        int spans = dv == 1 ? S - 1 : 1;
        ICurve3 SideCurve(int col, int span)
        {
            if (dv == 1) return new LineCurve3(Q[span, col].Xyz / Q[span, col].W, Q[span + 1, col].Xyz / Q[span + 1, col].W);
            var pts = new List<Vector3d>(); var ws = new List<double>();
            for (int i = 0; i < S; i++) { pts.Add(Q[i, col].Xyz / Q[i, col].W); ws.Add(Q[i, col].W); }
            return new SplineCurve3(pts, dv, ws, kv);
        }
        var sideCurves = new Dictionary<(int, int), ICurve3>();
        ICurve3 Side(int j, int span) { if (!sideCurves.TryGetValue((j % N, span), out var c)) sideCurves[(j % N, span)] = c = SideCurve(colStart[j % N], span); return c; }
        for (int span = 0; span < spans; span++)
            for (int j = 0; j < N; j++)
            {
                int c0 = colStart[j], cn = 2 * K[j] + 1;
                int r0 = dv == 1 ? span : 0, rn = dv == 1 ? 2 : S;
                var cp = new Vector3d[cn, rn]; var w = new double[cn, rn];
                for (int a = 0; a < cn; a++) for (int b = 0; b < rn; b++) { var q = Q[r0 + b, c0 + a]; cp[a, b] = q.Xyz / q.W; w[a, b] = q.W; }
                var ku = new double[cn + 3];
                ku[0] = ku[1] = ku[2] = 0; for (int p = 1; p < K[j]; p++) { ku[1 + 2 * p] = ku[2 + 2 * p] = (double)p / K[j]; }
                ku[cn] = ku[cn + 1] = ku[cn + 2] = 1;
                var kvs = dv == 1 ? new double[] { 0, 0, 1, 1 } : kv;
                var ns = new NurbsSurface(cp, 2, dv, w, ku, kvs);
                var lo = dv == 1 ? loops[span][j] : loops[0][j]; var hi = dv == 1 ? loops[span + 1][j] : loops[S - 1][j];
                var A0 = Start(lo); var B0 = End(lo); var A1 = Start(hi); var B1 = End(hi);
                var loop = new List<Seg>
                {
                    new() { Curve = lo, A = A0, B = B0 },
                    new() { Curve = Side(j + 1, span), A = B0, B = B1 },
                    new() { Curve = hi, A = B1, B = A1 },
                    new() { Curve = Side(j, span), A = A1, B = A0 },
                };
                var fd = new FDesc { Surf = ns, Rev = false, Tag = $"loft:side[{j}]" + (spans > 1 ? $".{span}" : ""), Touched = true, Hint = ns.PointAt(new Vector2d(0.5, 0.5)) };
                fd.Loops.Add(loop);
                faces.Add(fd);
            }
        // торцы
        foreach (var (k, tag, sgn) in new[] { (0, "loft:start", -1.0), (S - 1, "loft:end", 1.0) })
        {
            var n = sections[k].Normal.Normalized(); if (Vector3d.Dot(n, run) * sgn < 0) n = -n;
            var loop = loops[k].Select(c => new Seg { Curve = c, A = Start(c), B = End(c) }).ToList();
            var fd = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(cent[k], n)), Rev = false, Tag = tag, Touched = true, N = n };
            fd.Loops.Add(loop);
            faces.Add(fd);
        }
        var solid = FinishFaces(faces, "loft", ref why);
        if (solid == null) return null;
        if (Prov)
        {
            var pv = new Provenance { Operation = smooth ? "loft.smooth" : "loft.ruled" };
            foreach (var fd in faces) if (fd.Made != null) pv.Faces.Add(new FaceProvenance(fd.Made, null, FaceOrigin.Generated, fd.Tag));
            solid.SetProvenance(pv);
        }
        Interlocked.Increment(ref NurbsLofts);
        return solid;
    }

    /// <summary>Базисная функция B-сплайна N_{i,p}(t) (Кокс — де Бур; правый конец — к последней функции).</summary>
    static double Basis(double[] kn, int p, int i, double t)
    {
        int n = kn.Length - p - 1;
        if (t >= kn[^1]) return i == n - 1 ? 1 : 0;
        var N = new double[p + 1];
        for (int j = 0; j <= p; j++) N[j] = (i + j < kn.Length - 1 && t >= kn[i + j] && t < kn[i + j + 1]) ? 1 : 0;
        for (int k = 1; k <= p; k++)
            for (int j = 0; j <= p - k; j++)
            {
                double a = kn[i + j + k] - kn[i + j], b = kn[i + j + k + 1] - kn[i + j + 1];
                double l = a > 0 ? (t - kn[i + j]) / a * N[j] : 0, r = b > 0 ? (kn[i + j + k + 1] - t) / b * N[j + 1] : 0;
                N[j] = l + r;
            }
        return N[0];
    }

    static double[,]? Inverse(double[,] A)
    {
        int n = A.GetLength(0); var m = new double[n, 2 * n];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = A[i, j]; m[i, n + i] = 1; }
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (Math.Abs(m[piv, c]) < 1e-12) return null;
            if (piv != c) for (int j = 0; j < 2 * n; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            double d = m[c, c]; for (int j = 0; j < 2 * n; j++) m[c, j] /= d;
            for (int r = 0; r < n; r++) if (r != c) { double f = m[r, c]; if (f != 0) for (int j = 0; j < 2 * n; j++) m[r, j] -= f * m[c, j]; }
        }
        var inv = new double[n, n]; for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }
}
