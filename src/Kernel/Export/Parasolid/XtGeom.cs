using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Export.Parasolid;

/// <summary>
/// ПОВЕРХНОСТИ PARASOLID XT ДЛЯ РАСЧЁТА: точка по параметрам и ближайшая точка (проекция) — для поверхностей скругления
/// (BLENDED_EDGE: касание шара с опорными поверхностями) и кривых на поверхности (SP_CURVE). Параметризация — по Parasolid XT
/// Format Reference (раздел «Surfaces»): плоскость P + uX + vY; цилиндр P + vA + r(X cos u + Y sin u); конус
/// P + vA + (X cos u + Y sin u)(r + v tg a) (в справочнике знак при vA напечатан минусом — данные ему не отвечают); шар C + r(cos v (X cos u + Y sin u) + A sin v); тор C + (R + r cos v)(X cos u +
/// Y sin u) + r sin v A; B-поверхность — NURBS (вершины по u — внешний индекс, однородные при весах).
/// </summary>
internal abstract class XtSurf
{
    public abstract Vector3d At(double u, double v);
    /// <summary>Ближайшая точка поверхности к q.</summary>
    public abstract Vector3d Project(Vector3d q);

    // поверхности по узлам файла — одна на узел: скругление строит ось (сгущение, уточнение) — дорого повторять для каждой
    // кривой пересечения и границы
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XtFile, Dictionary<int, XtSurf?>> cache = new();

    public static XtSurf? Of(XtFile f, XtNode n)
    {
        var d = cache.GetOrCreateValue(f);
        lock (d) if (d.TryGetValue(n.Id, out var hit)) return hit;
        var r = Make(f, n);
        lock (d) d[n.Id] = r;
        return r;
    }

    static XtSurf? Make(XtFile f, XtNode n)
    {
        switch (n.Name)
        {
            case "PLANE": return new Pl(n.V("pvec"), n.V("normal"), n.V("x_axis"));
            case "CYLINDER": return new Cyl(n.V("pvec"), n.V("axis"), n.V("x_axis"), n.D("radius"));
            case "CONE": return new Cone(n.V("pvec"), n.V("axis"), n.V("x_axis"), n.D("radius"), n.D("sin_half_angle"), n.D("cos_half_angle"));
            case "SPHERE": return new Sph(n.V("centre"), n.V("axis"), n.V("x_axis"), n.D("radius"));
            case "TORUS": return new Tor(n.V("centre"), n.V("axis"), n.V("x_axis"), n.D("major_radius"), n.D("minor_radius"));
            case "B_SURFACE": return f[n.P("nurbs")] is { } ns ? XtNurbsSurf.Of(f, ns) : null;
            case "BLENDED_EDGE": return XtBlendSurf.Of(f, n, 0);
            case "SPUN_SURF": return XtSpun.Of(f, n);
            // граница скругления (вспомогательная поверхность, пересекающая опору по краю скругления): проекция — точка края
            case "BLEND_BOUND": return f[n.P("blend")] is { } bn && Of(f, bn) is XtBlendSurf bb ? new BlendBound(bb, n.I("boundary")) : null;
            case "OFFSET_SURF": return f[n.P("surface")] is { } bs && Of(f, bs) is { } b ? new Off(b, n.D("offset") * (n.C("sense") == '-' ? -1 : 1) * (bs.C("sense") == '-' ? -1 : 1)) : null;
        }
        return null;
    }

    sealed class Pl : XtSurf
    {
        readonly Vector3d P, N, X, Y;
        public Pl(Vector3d p, Vector3d n, Vector3d x) { P = p; N = n.Normalized(); X = x.Normalized(); Y = Vector3d.Cross(N, X); }
        public override Vector3d At(double u, double v) => P + X * u + Y * v;
        public override Vector3d Project(Vector3d q) => q - N * Vector3d.Dot(q - P, N);
    }
    sealed class Cyl : XtSurf
    {
        readonly Vector3d P, A, X, Y; readonly double R;
        public Cyl(Vector3d p, Vector3d a, Vector3d x, double r) { P = p; A = a.Normalized(); X = x.Normalized(); Y = Vector3d.Cross(A, X); R = r; }
        public override Vector3d At(double u, double v) => P + A * v + (X * Math.Cos(u) + Y * Math.Sin(u)) * R;
        public override Vector3d Project(Vector3d q)
        {
            var d = q - P; double h = Vector3d.Dot(d, A); var rad = d - A * h;
            if (rad.LengthSquared < 1e-30) rad = X;
            return P + A * h + rad.Normalized() * R;
        }
    }
    sealed class Cone : XtSurf
    {
        readonly Vector3d P, A, X, Y; readonly double R, S, C;
        public Cone(Vector3d p, Vector3d a, Vector3d x, double r, double s, double c) { P = p; A = a.Normalized(); X = x.Normalized(); Y = Vector3d.Cross(A, X); R = r; S = s; C = c; }
        public override Vector3d At(double u, double v) => P + A * v + (X * Math.Cos(u) + Y * Math.Sin(u)) * (R + v * S / C);
        public override Vector3d Project(Vector3d q)
        {
            // в меридиане точки: образующая — прямая от (h = 0, ρ = R) с направлением (1, tg a) по (h вдоль A, ρ)
            var d = q - P; double h = Vector3d.Dot(d, A); var rad = d - A * h; double rho = rad.Length;
            var e = rho > 1e-30 ? rad / rho : X;
            var g = new Vector2d(C, S);                    // единичное направление образующей (h, ρ) — на единицу длины
            var w = new Vector2d(h, rho - R);
            double t = Vector2d.Dot(w, g);
            if (R + t * S < 0) t = -R / S;                   // не за вершину
            var m = new Vector2d(0, R) + g * t;
            return P + A * m.X + e * m.Y;
        }
    }
    sealed class Sph : XtSurf
    {
        readonly Vector3d Cn, A, X, Y; readonly double R;
        public Sph(Vector3d c, Vector3d a, Vector3d x, double r) { Cn = c; A = a.Normalized(); X = x.Normalized(); Y = Vector3d.Cross(A, X); R = r; }
        public override Vector3d At(double u, double v) => Cn + ((X * Math.Cos(u) + Y * Math.Sin(u)) * Math.Cos(v) + A * Math.Sin(v)) * R;
        public override Vector3d Project(Vector3d q) { var d = q - Cn; if (d.LengthSquared < 1e-30) d = X; return Cn + d.Normalized() * R; }
    }
    sealed class Tor : XtSurf
    {
        readonly Vector3d Cn, A, X, Y; readonly double Rm, r;
        public Tor(Vector3d c, Vector3d a, Vector3d x, double big, double small) { Cn = c; A = a.Normalized(); X = x.Normalized(); Y = Vector3d.Cross(A, X); Rm = big; r = small; }
        public override Vector3d At(double u, double v) => Cn + (X * Math.Cos(u) + Y * Math.Sin(u)) * (Rm + r * Math.Cos(v)) + A * (r * Math.Sin(v));
        public override Vector3d Project(Vector3d q)
        {
            var d = q - Cn; double h = Vector3d.Dot(d, A); var rad = d - A * h;
            var e = rad.LengthSquared > 1e-30 ? rad.Normalized() : X;
            var c = Cn + e * Rm; var t = q - c; if (t.LengthSquared < 1e-30) t = e;
            return c + t.Normalized() * r;
        }
    }
    internal sealed class BlendBound : XtSurf
    {
        readonly XtBlendSurf B; readonly int Idx;
        public BlendBound(XtBlendSurf b, int idx) { B = b; Idx = idx; }
        public override Vector3d At(double u, double v) => B.At(u, Idx == 0 ? 0 : 1);
        public override Vector3d Project(Vector3d q) { var c = B.CentreAt(q); return (Idx == 0 ? B.S0 : B.S1).Project(c); }
    }
    sealed class Off : XtSurf
    {
        readonly XtSurf B; readonly double D;
        public Off(XtSurf b, double d) { B = b; D = d; }
        public override Vector3d At(double u, double v)
        {
            var p = B.At(u, v); const double h = 1e-6;
            var n = Vector3d.Cross(B.At(u + h, v) - B.At(u - h, v), B.At(u, v + h) - B.At(u, v - h));
            return n.LengthSquared > 0 ? p + n.Normalized() * D : p;
        }
        public override Vector3d Project(Vector3d q)
        {
            var p = B.Project(q); var dn = q - p;
            if (dn.LengthSquared < 1e-30) return p;
            // нормаль базовой в p со стороны смещения: точка смещённой — p ± D·n
            var n = dn.Normalized();
            return p + n * Math.Abs(D);
        }
    }
}

/// <summary>
/// ПОВЕРХНОСТЬ ВРАЩЕНИЯ XT (SPUN_SURF): R(u, v) = Z(u) + (C(u) − Z(u)) cos v + A × (C(u) − Z(u)) sin v, C — образующая
/// (B-кривая, окружность, прямая), Z — её проекция на ось. Проекция точки — в меридиане: ближайшая точка образующей в
/// координатах (высота по оси, расстояние до оси), затем поворот в полуплоскость точки.
/// </summary>
internal sealed class XtSpun : XtSurf
{
    public Vector3d P, A; public Func<double, Vector3d> C = null!; public double T0, T1;
    List<(double T, Vector2d M)> mer = new();

    public static XtSpun? Of(XtFile f, XtNode n)
    {
        var pr = f[n.P("profile")]; if (pr == null) return null;
        var s = new XtSpun { P = n.V("base"), A = n.V("axis").Normalized() };
        switch (pr.Name)
        {
            case "B_CURVE":
                if (f[pr.P("nurbs")] is not { } nb || XtNurbsCurve.Of(f, nb) is not { Dim: 3 } nc) return null;
                s.C = nc.At3; s.T0 = nc.T0; s.T1 = nc.T1; break;
            case "CIRCLE":
            {
                var c = pr.V("centre"); var z = pr.V("normal").Normalized(); var x = pr.V("x_axis").Normalized(); var y = Vector3d.Cross(z, x); double r = pr.D("radius");
                s.C = t => c + (x * Math.Cos(t) + y * Math.Sin(t)) * r; s.T0 = 0; s.T1 = 2 * Math.PI; break;
            }
            case "LINE":
            {
                var o = pr.V("pvec"); var d = pr.V("direction").Normalized();
                s.C = t => o + d * t; s.T0 = -1; s.T1 = 1; break;
            }
            default: return null;
        }
        int m = 256;
        for (int i = 0; i <= m; i++) { double t = s.T0 + (s.T1 - s.T0) * i / m; s.mer.Add((t, s.Mer(s.C(t)))); }
        return s;
    }

    Vector2d Mer(Vector3d q) { var d = q - P; double h = Vector3d.Dot(d, A); return new Vector2d(h, (d - A * h).Length); }

    public override Vector3d At(double u, double v)
    {
        var c = C(u); var d = c - P; var z = P + A * Vector3d.Dot(d, A); var r = c - z;
        return z + r * Math.Cos(v) + Vector3d.Cross(A, r) * Math.Sin(v);
    }

    public override Vector3d Project(Vector3d q)
    {
        var mq = Mer(q);
        int bi = 0; double bd = double.MaxValue;
        for (int i = 0; i < mer.Count; i++) { double d = (mer[i].M - mq).LengthSquared; if (d < bd) { bd = d; bi = i; } }
        double t = mer[bi].T, h = (T1 - T0) * 1e-7;
        for (int it = 0; it < 30; it++)
        {
            var m0 = Mer(C(t)); var dm = (Mer(C(Math.Min(T1, t + h))) - Mer(C(Math.Max(T0, t - h)))) / (Math.Min(T1, t + h) - Math.Max(T0, t - h));
            double dd = Vector2d.Dot(dm, dm); if (dd < 1e-300) break;
            double step = Vector2d.Dot(m0 - mq, dm) / dd;
            double nt = Math.Clamp(t - step, T0, T1);
            bool done = Math.Abs(nt - t) < 1e-13 * Math.Max(1, T1 - T0); t = nt;
            if (done) break;
        }
        var mm = Mer(C(t));
        var dq = q - P; var rad = dq - A * Vector3d.Dot(dq, A);
        var e = rad.LengthSquared > 1e-30 ? rad.Normalized() : (Math.Abs(A.X) < 0.9 ? Vector3d.Cross(A, Vector3d.UnitX) : Vector3d.Cross(A, Vector3d.UnitY)).Normalized();
        return P + A * mm.X + e * mm.Y;
    }
}

/// <summary>NURBS-поверхность XT (однородные вершины при весах): точка и проекция Ньютоном от ближайшего узла сетки.</summary>
internal sealed class XtNurbsSurf : XtSurf
{
    public int Du, Dv, Nu, Nv; public double[] Ku = Array.Empty<double>(), Kv = Array.Empty<double>();
    public Vector4d[,] H = new Vector4d[0, 0];

    public static XtNurbsSurf? Of(XtFile f, XtNode n)
    {
        var s = new XtNurbsSurf { Du = n.I("u_degree"), Dv = n.I("v_degree"), Nu = n.I("n_u_vertices"), Nv = n.I("n_v_vertices") };
        int vd = n.I("vertex_dim"); bool rat = n.L("rational");
        var vs = f[n.P("bspline_vertices")]?.DA("vertices"); if (vs == null || vs.Length < s.Nu * s.Nv * vd) return null;
        s.Ku = Expand(f, n.P("u_knots"), n.P("u_knot_mult")); s.Kv = Expand(f, n.P("v_knots"), n.P("v_knot_mult"));
        if (s.Ku.Length != s.Nu + s.Du + 1 || s.Kv.Length != s.Nv + s.Dv + 1) return null;
        s.H = new Vector4d[s.Nu, s.Nv];
        for (int i = 0; i < s.Nu; i++)
            for (int j = 0; j < s.Nv; j++)
            {
                int o = (i * s.Nv + j) * vd;
                s.H[i, j] = rat ? new Vector4d(vs[o], vs[o + 1], vs[o + 2], vs[o + 3]) : new Vector4d(vs[o], vs[o + 1], vs[o + 2], 1);
            }
        return s;
    }

    public static double[] Expand(XtFile f, int knots, int mult)
    {
        var k = f[knots]?.DA("knots") ?? Array.Empty<double>();
        var m = f[mult]?.IA("mult") ?? Array.Empty<int>();
        var res = new List<double>();
        for (int i = 0; i < k.Length && i < m.Length; i++) for (int j = 0; j < m[i]; j++) res.Add(k[i]);
        return res.ToArray();
    }

    public (double U0, double U1, double V0, double V1) Range => (Ku[Du], Ku[Nu], Kv[Dv], Kv[Nv]);

    public override Vector3d At(double u, double v)
    {
        var (u0, u1, v0, v1) = Range;
        u = Math.Clamp(u, u0, u1); v = Math.Clamp(v, v0, v1);
        var bu = XtBasis.Eval(Ku, Du, Nu, u); var bv = XtBasis.Eval(Kv, Dv, Nv, v);
        var acc = Vector4d.Zero;
        for (int i = 0; i < Nu; i++) if (bu[i] != 0) for (int j = 0; j < Nv; j++) if (bv[j] != 0) acc += H[i, j] * (bu[i] * bv[j]);
        return acc.W != 0 ? acc.Xyz / acc.W : acc.Xyz;
    }

    // Сетка точек для начального приближения проекции — считается один раз (резьба: сотни вершин; прежде 625 вычислений
    // поверхности на каждую проекцию, а проекций при сгущении кривых — сотни тысяч).
    Vector3d[,]? grid; int gu, gv;
    void BuildGrid()
    {
        var (u0, u1, v0, v1) = Range;
        gu = Math.Clamp(3 * (Nu - Du), 24, 240); gv = Math.Clamp(3 * (Nv - Dv), 24, 240);
        var g = new Vector3d[gu + 1, gv + 1];
        for (int i = 0; i <= gu; i++) for (int j = 0; j <= gv; j++) g[i, j] = At(u0 + (u1 - u0) * i / gu, v0 + (v1 - v0) * j / gv);
        grid = g;
    }

    public override Vector3d Project(Vector3d q)
    {
        var (u0, u1, v0, v1) = Range;
        if (grid == null) BuildGrid();
        var gr = grid!;
        double bu = u0, bv = v0, best = double.MaxValue;
        for (int i = 0; i <= gu; i++) for (int j = 0; j <= gv; j++)
            {
                double d = (gr[i, j] - q).LengthSquared; if (d < best) { best = d; bu = u0 + (u1 - u0) * i / gu; bv = v0 + (v1 - v0) * j / gv; }
            }
        double hu = 1e-7 * Math.Max(1, u1 - u0), hv = 1e-7 * Math.Max(1, v1 - v0);
        for (int it = 0; it < 30; it++)
        {
            var p = At(bu, bv);
            var su = (At(bu + hu, bv) - At(bu - hu, bv)) / (2 * hu); var sv = (At(bu, bv + hv) - At(bu, bv - hv)) / (2 * hv);
            var r = p - q;
            double a = Vector3d.Dot(su, su), b = Vector3d.Dot(su, sv), c = Vector3d.Dot(sv, sv), det = a * c - b * b;
            if (Math.Abs(det) < 1e-300) break;
            double gu = Vector3d.Dot(r, su), gv = Vector3d.Dot(r, sv);
            double du = (c * gu - b * gv) / det, dv = (a * gv - b * gu) / det;
            bu = Math.Clamp(bu - du, u0, u1); bv = Math.Clamp(bv - dv, v0, v1);
            if (Math.Abs(du) < 1e-12 * Math.Max(1, u1 - u0) && Math.Abs(dv) < 1e-12 * Math.Max(1, v1 - v0)) break;
        }
        return At(bu, bv);
    }
}

/// <summary>Базисные функции B-сплайна (Кокс — де Бур) и глобальная интерполяция (NURBS Book, A9.1).</summary>
internal static class XtBasis
{
    public static double[] Eval(double[] k, int p, int n, double t)
    {
        var res = new double[n];
        int span = p;
        while (span < n - 1 && t >= k[span + 1]) span++;
        var N = new double[p + 1]; var left = new double[p + 1]; var right = new double[p + 1];
        N[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = t - k[span + 1 - j]; right[j] = k[span + j] - t;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                double den = right[r + 1] + left[j - r];
                double tmp = den != 0 ? N[r] / den : 0;
                N[r] = saved + right[r + 1] * tmp; saved = left[j - r] * tmp;
            }
            N[j] = saved;
        }
        for (int j = 0; j <= p; j++) if (span - p + j >= 0 && span - p + j < n) res[span - p + j] = N[j];
        return res;
    }

    /// <summary>Интерполяция точек кривой степени p: параметры по хордам, узлы усреднением. Возвращает (узлы, вершины).</summary>
    public static (double[] Knots, double[] Params) Setup(IReadOnlyList<double> prm, int p)
    {
        int n = prm.Count;
        var k = new double[n + p + 1];
        for (int i = 0; i <= p; i++) { k[i] = 0; k[n + p - i] = 1; }
        for (int j = 1; j < n - p; j++) { double s = 0; for (int i = j; i < j + p; i++) s += prm[i]; k[j + p] = s / p; }
        return (k, prm.ToArray());
    }

    /// <summary>Вершины интерполирующего B-сплайна. Матрица коллокации ленточная (в строке i ненулевые лишь столбцы i − p…i + p)
    /// и вполне положительная — исключение Гаусса без перестановок по ленте (де Бур), O(n·p²): тысячи точек плотной винтовой
    /// линии резьбы решаются мгновенно (прежде полный Гаусс O(n³)).</summary>
    public static Vector3d[] Solve(double[] knots, int p, double[] prm, IReadOnlyList<Vector3d> q)
    {
        int n = q.Count, w = 2 * p + 1;
        var M = new double[n, w];                 // M[i, j − i + p]
        for (int i = 0; i < n; i++)
        {
            double[] b;
            if (i == n - 1) { b = new double[n]; b[n - 1] = 1; } else b = Eval(knots, p, n, prm[i]);
            for (int j = Math.Max(0, i - p); j <= Math.Min(n - 1, i + p); j++) M[i, j - i + p] = b[j];
        }
        var B = q.ToArray();
        for (int c = 0; c < n; c++)
        {
            double d = M[c, p]; if (Math.Abs(d) < 1e-300) continue;
            for (int r = c + 1; r <= Math.Min(n - 1, c + p); r++)
            {
                double fct = M[r, c - r + p] / d; if (fct == 0) continue;
                for (int j = c; j <= Math.Min(n - 1, c + p); j++) M[r, j - r + p] -= fct * M[c, j - c + p];
                B[r] -= B[c] * fct;
            }
        }
        var X = new Vector3d[n];
        for (int r = n - 1; r >= 0; r--)
        {
            var s = B[r]; for (int j = r + 1; j <= Math.Min(n - 1, r + p); j++) s -= X[j] * M[r, j - r + p];
            X[r] = M[r, p] != 0 ? s / M[r, p] : s;
        }
        return X;
    }

    /// <summary>Параметры по хордам (0…1) для ряда точек.</summary>
    public static double[] Chord(IReadOnlyList<Vector3d> q)
    {
        int n = q.Count; var t = new double[n]; double tot = 0;
        for (int i = 1; i < n; i++) tot += (q[i] - q[i - 1]).Length;
        for (int i = 1; i < n; i++) t[i] = tot > 0 ? t[i - 1] + (q[i] - q[i - 1]).Length / tot : (double)i / (n - 1);
        t[n - 1] = 1;
        return t;
    }
}

/// <summary>NURBS-кривая XT (однородные вершины при весах; размерность 2 — кривая параметров поверхности).</summary>
internal sealed class XtNurbsCurve
{
    public int Deg, N, Dim; public double[] K = Array.Empty<double>(); public double[][] H = Array.Empty<double[]>(); public bool Rational;
    public double T0 => K[Deg]; public double T1 => K[N];

    public static XtNurbsCurve? Of(XtFile f, XtNode n)
    {
        var c = new XtNurbsCurve { Deg = n.I("degree"), N = n.I("n_vertices"), Rational = n.L("rational") };
        int vd = n.I("vertex_dim"); c.Dim = c.Rational ? vd - 1 : vd;
        var vs = f[n.P("bspline_vertices")]?.DA("vertices");
        c.K = XtNurbsSurf.Expand(f, n.P("knots"), n.P("knot_mult"));
        if (vs == null || vs.Length < c.N * vd || c.K.Length != c.N + c.Deg + 1 || c.Dim < 2) return null;
        c.H = new double[c.N][];
        for (int i = 0; i < c.N; i++) { c.H[i] = new double[c.Dim + 1]; for (int k = 0; k < c.Dim; k++) c.H[i][k] = vs[i * vd + k]; c.H[i][c.Dim] = c.Rational ? vs[i * vd + vd - 1] : 1; }
        return c;
    }

    public double[] At(double t)
    {
        t = Math.Clamp(t, T0, T1);
        var b = XtBasis.Eval(K, Deg, N, t);
        var acc = new double[Dim + 1];
        for (int i = 0; i < N; i++) if (b[i] != 0) for (int k = 0; k <= Dim; k++) acc[k] += b[i] * H[i][k];
        var r = new double[Dim];
        for (int k = 0; k < Dim; k++) r[k] = acc[Dim] != 0 ? acc[k] / acc[Dim] : acc[k];
        return r;
    }

    public Vector3d At3(double t) { var p = At(t); return new Vector3d(p[0], p[1], Dim > 2 ? p[2] : 0); }

    /// <summary>Точки по пролётам узлов (не реже per на пролёт).</summary>
    public List<double> Params(int per)
    {
        var res = new List<double>();
        for (int i = Deg; i < N; i++)
        {
            double a = K[i], b = K[i + 1];
            if (b - a <= 1e-15) continue;
            for (int j = 0; j < per; j++) res.Add(a + (b - a) * j / per);
        }
        res.Add(T1);
        return res;
    }
}

/// <summary>Ломаные кривых: плотная разбивка, уточнение пересечения и обрезка по вершинам ребра.</summary>
internal static class XtPoly
{
    static readonly bool BlendEdgeOff = Environment.GetEnvironmentVariable("P3D_XTBLENDEDGE") == "0";

    /// <summary>Сгущение ломаной кривой пересечения двух поверхностей: точки между узлами карты переносятся на обе поверхности
    /// попеременной проекцией (сходится к линии пересечения при пересечении под углом).</summary>
    public static List<Vector3d> Densify(IReadOnlyList<Vector3d> chart, XtSurf? a, XtSurf? b, int per)
    {
        var res = new List<Vector3d>();
        for (int i = 0; i + 1 < chart.Count; i++)
        {
            res.Add(chart[i]);
            for (int k = 1; k < per; k++)
            {
                var p = Vector3d.Lerp(chart[i], chart[i + 1], (double)k / per);
                // край скругления: опора касается скругления — попеременная проекция на касающиеся поверхности не сходится;
                // точка берётся касанием шара с опорой (центр — на обеих смещённых опорах)
                if (!BlendEdgeOff && (a as XtBlendSurf ?? b as XtBlendSurf) is { } bl && (a is XtBlendSurf ? b : a) is { } other)
                {
                    var q = bl.EdgeNear(p);
                    if ((other.Project(q) - q).Length < 1e-8) { res.Add(q); continue; }
                }
                // граница скругления: её проекция уже лежит на опоре (обычно это и есть вторая поверхность) — один шаг
                if (!BlendEdgeOff && (a as XtSurf.BlendBound ?? b as XtSurf.BlendBound) is { } bb && (a is XtSurf.BlendBound ? b : a) is { } oth)
                {
                    var q = bb.Project(p);
                    if ((oth.Project(q) - q).Length < 1e-8) { res.Add(q); continue; }
                }
                if (a != null && b != null)
                    for (int it = 0; it < 60; it++)
                    {
                        var q = b.Project(a.Project(p));
                        bool done = (q - p).Length < 1e-11;
                        p = q;
                        if (done) break;
                    }
                res.Add(p);
            }
        }
        if (chart.Count > 0) res.Add(chart[^1]);
        return res;
    }

    /// <summary>Ломаная кривой F на [t0, t1]: m равных шагов, затем дробление пополам, пока середина шага отходит от хорды больше
    /// 10 мкм (в метрах XT — 10⁻⁵; кубическая интерполяция по таким точкам отходит от кривой на сотые доли микрона) или шаг поворачивает больше чем на 10°; не глубже 16 делений, не больше 40 000 точек.</summary>
    public static List<Vector3d> Adaptive(Func<double, Vector3d> F, double t0, double t1, int m)
    {
        var res = new List<Vector3d> { F(t0) };
        void Rec(double a, Vector3d pa, double b, Vector3d pb, int depth)
        {
            double c = 0.5 * (a + b); var pc = F(c);
            var ab = pb - pa; double l2 = ab.LengthSquared;
            double t = l2 > 0 ? Math.Clamp(Vector3d.Dot(pc - pa, ab) / l2, 0, 1) : 0.5;
            double dev = (pa + ab * t - pc).Length;
            var d1 = pc - pa; var d2 = pb - pc;
            double cosA = d1.LengthSquared > 0 && d2.LengthSquared > 0 ? Vector3d.Dot(d1.Normalized(), d2.Normalized()) : 1;
            if (depth < 16 && res.Count < 40000 && (dev > 1e-5 || cosA < 0.985))
            {
                Rec(a, pa, c, pc, depth + 1); Rec(c, pc, b, pb, depth + 1);
            }
            else res.Add(pb);
        }
        var prev = res[0];
        for (int i = 1; i <= m; i++)
        {
            double a = t0 + (t1 - t0) * (i - 1) / m, b = t0 + (t1 - t0) * i / m;
            var pb = F(b);
            Rec(a, prev, b, pb, 0);
            prev = pb;
        }
        return res;
    }

    /// <summary>Участок ломаной между ближайшими к s и e точками (по ходу ломаной), концы — точно s и e.</summary>
    public static List<Vector3d> Trim(IReadOnlyList<Vector3d> pts, Vector3d s, Vector3d e)
    {
        (int I, double T, double D) Near(Vector3d q)
        {
            int bi = 0; double bt = 0, bd = double.MaxValue;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var ab = pts[i + 1] - pts[i]; double l2 = ab.LengthSquared;
                double t = l2 > 0 ? Math.Clamp(Vector3d.Dot(q - pts[i], ab) / l2, 0, 1) : 0;
                double d = (pts[i] + ab * t - q).LengthSquared;
                if (d < bd) { bd = d; bi = i; bt = t; }
            }
            return (bi, bt, bd);
        }
        if (pts.Count < 2) return pts.ToList();
        var ns = Near(s); var ne = Near(e);
        var res = new List<Vector3d>();
        bool fwd = (ns.I, ns.T).CompareTo((ne.I, ne.T)) <= 0;
        var (a, b) = fwd ? (ns, ne) : (ne, ns);
        res.Add(fwd ? s : e);
        for (int i = a.I + 1; i <= b.I; i++) res.Add(pts[i]);
        res.Add(fwd ? e : s);
        if (!fwd) res.Reverse();
        // убрать совпадающие точки у концов
        var clean = new List<Vector3d>();
        foreach (var p in res) if (clean.Count == 0 || (clean[^1] - p).Length > 1e-12) clean.Add(p);
        return clean;
    }
}


/// <summary>
/// СКРУГЛЕНИЕ PARASOLID (BLENDED_EDGE): ось шара — пересечение опорных поверхностей, смещённых на range (знак — сторона), сечение
/// — дуга с центром на оси от точки касания первой опоры до второй (v = 0 → 1; при обратной оси — наоборот). Радиус 0 — «край
/// обрыва» (cliff edge): поверхность вырождается в свою ось (кривую). Ось уточняется между точками карты попеременной проекцией
/// на смещённые опоры.
/// </summary>
internal sealed class XtBlendSurf : XtSurf
{
    public List<Vector3d> Spine = new();
    public XtSurf S0 = null!, S1 = null!;
    public double R0, R1;
    public bool Rev;

    public static XtBlendSurf? Of(XtFile f, XtNode n, int depth)
    {
        if (depth > 3) return null;
        var ss = n.PA("surface"); var sp = f[n.P("spine")];
        if (ss.Length < 2 || sp == null || f[ss[0]] is not { } a || f[ss[1]] is not { } b) return null;
        XtSurf? s0 = a.Name == "BLENDED_EDGE" ? Of(f, a, depth + 1) : XtSurf.Of(f, a);
        XtSurf? s1 = b.Name == "BLENDED_EDGE" ? Of(f, b, depth + 1) : XtSurf.Of(f, b);
        if (s0 == null || s1 == null) return null;
        var rg = n.DA("range");
        var bl = new XtBlendSurf { S0 = s0, S1 = s1, R0 = rg.Length > 0 ? Math.Abs(rg[0]) : 0, R1 = rg.Length > 1 ? Math.Abs(rg[1]) : 0, Rev = sp.C("sense") == '-' };
        List<Vector3d> raw;
        switch (sp.Name)
        {
            case "INTERSECTION": raw = f[sp.P("chart")]?.VA("hvec").ToList() ?? new(); break;
            case "CIRCLE":
            {
                var c = sp.V("centre"); var z = sp.V("normal").Normalized(); var x = sp.V("x_axis").Normalized(); var y = Vector3d.Cross(z, x); double r = sp.D("radius");
                raw = Enumerable.Range(0, 129).Select(i => c + (x * Math.Cos(2 * Math.PI * i / 128) + y * Math.Sin(2 * Math.PI * i / 128)) * r).ToList();
                break;
            }
            case "ELLIPSE":
            {
                var c = sp.V("centre"); var z = sp.V("normal").Normalized(); var x = sp.V("x_axis").Normalized(); var y = Vector3d.Cross(z, x);
                double ra = sp.D("major_radius"), rb = sp.D("minor_radius");
                raw = Enumerable.Range(0, 257).Select(i => c + x * (ra * Math.Cos(2 * Math.PI * i / 256)) + y * (rb * Math.Sin(2 * Math.PI * i / 256))).ToList();
                break;
            }
            case "LINE": raw = new() { sp.V("pvec") - sp.V("direction") * 0.1, sp.V("pvec") + sp.V("direction") * 0.1 }; break;
            case "B_CURVE":
                raw = f[sp.P("nurbs")] is { } nb && XtNurbsCurve.Of(f, nb) is { Dim: 3 } nc ? nc.Params(16).Select(nc.At3).ToList() : new();
                break;
            default: return null;
        }
        if (raw.Count < 2) return null;
        // уточнение оси: точки между узлами карты — на обе смещённые опоры
        if (sp.Name == "INTERSECTION")
        {
            var dense = new List<Vector3d>();
            for (int i = 0; i + 1 < raw.Count; i++)
            {
                dense.Add(raw[i]);
                for (int k = 1; k < 8; k++) dense.Add(bl.OnSpineIn(Vector3d.Lerp(raw[i], raw[i + 1], k / 8.0), raw[i + 1] - raw[i]));
            }
            dense.Add(raw[^1]);
            raw = dense;
        }
        // открытая ось — с запасом за концы (грань скругления доходит до торцов чуть дальше точек карты)
        if ((raw[0] - raw[^1]).Length > 1e-9)
        {
            double ext = Math.Max(2 * Math.Max(bl.R0, bl.R1), 1e-5);
            var d0 = (raw[0] - raw[1]); var d1 = (raw[^1] - raw[^2]);
            if (d0.LengthSquared > 0) raw.Insert(0, bl.OnSpineIn(raw[0] + d0.Normalized() * ext, d0));
            if (d1.LengthSquared > 0) raw.Add(bl.OnSpineIn(raw[^1] + d1.Normalized() * ext, d1));
        }
        bl.Spine = raw;
        return bl;
    }

    static Vector3d Offset(XtSurf s, Vector3d q, double d)
    {
        var p = s.Project(q); var t = q - p;
        return d <= 0 || t.LengthSquared < 1e-30 ? p : p + t.Normalized() * d;
    }

    internal Vector3d OnSpine(Vector3d p)
    {
        for (int it = 0; it < 80; it++)
        {
            var q = Offset(S1, Offset(S0, p, R0), R1);
            bool done = (q - p).Length < 1e-11; p = q;
            if (done) break;
        }
        return p;
    }

    /// <summary>Точки касания шара с центром c (по порядку v: от первой опоры, при обратной оси — от второй).</summary>
    public (Vector3d A, Vector3d B) Contacts(Vector3d c)
    {
        var a = S0.Project(c); var b = S1.Project(c);
        return Rev ? (b, a) : (a, b);
    }

    /// <summary>Точки дуги сечения с центром c.</summary>
    public Vector3d[] Arc(Vector3d c, int m)
    {
        var (a, b) = Contacts(c);
        var res = new Vector3d[m];
        var ea = a - c; var eb = b - c; double r = 0.5 * (ea.Length + eb.Length);
        if (r < 1e-12) { for (int j = 0; j < m; j++) res[j] = c; return res; }
        ea.Normalize(); eb.Normalize();
        double ang = Math.Acos(Math.Clamp(Vector3d.Dot(ea, eb), -1, 1));
        var nn = Vector3d.Cross(ea, eb);
        if (nn.LengthSquared < 1e-24) { for (int j = 0; j < m; j++) res[j] = Vector3d.Lerp(a, b, j / (double)(m - 1)); return res; }
        nn.Normalize(); var e2 = Vector3d.Cross(nn, ea);
        for (int j = 0; j < m; j++) { double t = ang * j / (m - 1); res[j] = c + (ea * Math.Cos(t) + e2 * Math.Sin(t)) * r; }
        return res;
    }

    public override Vector3d At(double u, double v)
    {
        double x = Math.Clamp(u, 0, 1) * (Spine.Count - 1); int i = Math.Min((int)x, Spine.Count - 2);
        var c = Vector3d.Lerp(Spine[i], Spine[i + 1], x - i);
        var arc = Arc(c, 33); double y = Math.Clamp(v, 0, 1) * 32; int j = Math.Min((int)y, 31);
        return Vector3d.Lerp(arc[j], arc[j + 1], y - j);
    }

    /// <summary>Ближайшая к q точка оси (ломаной центров шара), уточнённая на обе смещённые опоры.</summary>
    /// <summary>Точный центр шара в нормальной плоскости оси через ближайшую к q точку оси: попеременно — на обе смещённые
    /// опоры и обратно в плоскость. Без плоскости центр сползал вдоль оси, и точки сгущённой кривой шли вразнобой (зигзаг).</summary>
    public Vector3d CentreAt(Vector3d q)
    {
        int bi = 0; double bt = 0, bd = double.MaxValue;
        for (int i = 0; i + 1 < Spine.Count; i++)
        {
            var ab = Spine[i + 1] - Spine[i]; double l2 = ab.LengthSquared;
            double t = l2 > 0 ? Math.Clamp(Vector3d.Dot(q - Spine[i], ab) / l2, 0, 1) : 0;
            double d = (Spine[i] + ab * t - q).LengthSquared;
            if (d < bd) { bd = d; bi = i; bt = t; }
        }
        var c0 = Spine[bi] + (Spine[bi + 1] - Spine[bi]) * bt;
        return OnSpineIn(c0, Spine[bi + 1] - Spine[bi]);
    }

    /// <summary>Точка оси в плоскости через c0 с нормалью tg (см. CentreAt).</summary>
    internal Vector3d OnSpineIn(Vector3d c0, Vector3d tg)
    {
        if (tg.LengthSquared < 1e-30) return OnSpine(c0);
        tg.Normalize();
        var c = c0;
        for (int it = 0; it < 40; it++)
        {
            var c1 = Offset(S1, Offset(S0, c, R0), R1);
            c1 -= tg * Vector3d.Dot(c1 - c0, tg);
            bool done = (c1 - c).Length < 1e-11; c = c1;
            if (done) break;
        }
        return c;
    }

    public Vector3d SpineNear(Vector3d q)
    {
        int bi = 0; double bt = 0, bd = double.MaxValue;
        for (int i = 0; i + 1 < Spine.Count; i++)
        {
            var ab = Spine[i + 1] - Spine[i]; double l2 = ab.LengthSquared;
            double t = l2 > 0 ? Math.Clamp(Vector3d.Dot(q - Spine[i], ab) / l2, 0, 1) : 0;
            double d = (Spine[i] + ab * t - q).LengthSquared;
            if (d < bd) { bd = d; bi = i; bt = t; }
        }
        return Spine[bi] + (Spine[Math.Min(bi + 1, Spine.Count - 1)] - Spine[bi]) * bt;
    }

    /// <summary>Точка края скругления (касания шара с опорой) у q: центр шара — точный (на обеих смещённых опорах) в плоскости
    /// сечения около q, касание — ближайшее к q из двух.</summary>
    public Vector3d EdgeNear(Vector3d q)
    {
        var c = CentreAt(q);
        var (a, b) = Contacts(c);
        return (a - q).LengthSquared <= (b - q).LengthSquared ? a : b;
    }

    public override Vector3d Project(Vector3d q)
    {
        // ближайшая точка оси, затем ближайшая точка дуги сечения
        var c = SpineNear(q);
        if (R0 < 1e-12 && R1 < 1e-12) return c;
        var arc = Arc(c, 33);
        var best = arc[0]; double bdd = double.MaxValue;
        for (int j = 0; j + 1 < arc.Length; j++)
        {
            var ab = arc[j + 1] - arc[j]; double l2 = ab.LengthSquared;
            double t = l2 > 0 ? Math.Clamp(Vector3d.Dot(q - arc[j], ab) / l2, 0, 1) : 0;
            var pp = arc[j] + ab * t; double d = (pp - q).LengthSquared;
            if (d < bdd) { bdd = d; best = pp; }
        }
        return best;
    }
}
