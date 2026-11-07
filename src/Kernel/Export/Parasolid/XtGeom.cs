using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Export.Parasolid;

/// <summary>
/// ПОВЕРХНОСТИ PARASOLID XT ДЛЯ РАСЧЁТА: точка по параметрам и ближайшая точка (проекция) — для поверхностей скругления
/// (BLENDED_EDGE: касание шара с опорными поверхностями) и кривых на поверхности (SP_CURVE). Параметризация — по Parasolid XT
/// Format Reference (раздел «Surfaces»): плоскость P + uX + vY; цилиндр P + vA + r(X cos u + Y sin u); конус
/// P − vA + (X cos u + Y sin u)(r + v tg a); шар C + r(cos v (X cos u + Y sin u) + A sin v); тор C + (R + r cos v)(X cos u +
/// Y sin u) + r sin v A; B-поверхность — NURBS (вершины по u — внешний индекс, однородные при весах).
/// </summary>
internal abstract class XtSurf
{
    public abstract Vector3d At(double u, double v);
    /// <summary>Ближайшая точка поверхности к q.</summary>
    public abstract Vector3d Project(Vector3d q);

    public static XtSurf? Of(XtFile f, XtNode n)
    {
        switch (n.Name)
        {
            case "PLANE": return new Pl(n.V("pvec"), n.V("normal"), n.V("x_axis"));
            case "CYLINDER": return new Cyl(n.V("pvec"), n.V("axis"), n.V("x_axis"), n.D("radius"));
            case "CONE": return new Cone(n.V("pvec"), n.V("axis"), n.V("x_axis"), n.D("radius"), n.D("sin_half_angle"), n.D("cos_half_angle"));
            case "SPHERE": return new Sph(n.V("centre"), n.V("axis"), n.V("x_axis"), n.D("radius"));
            case "TORUS": return new Tor(n.V("centre"), n.V("axis"), n.V("x_axis"), n.D("major_radius"), n.D("minor_radius"));
            case "B_SURFACE": return f[n.P("nurbs")] is { } ns ? XtNurbsSurf.Of(f, ns) : null;
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
        public override Vector3d At(double u, double v) => P - A * v + (X * Math.Cos(u) + Y * Math.Sin(u)) * (R + v * S / C);
        public override Vector3d Project(Vector3d q)
        {
            // в меридиане точки: образующая — прямая от (h = 0, ρ = R) с направлением (−1, tg a) по (h вдоль A, ρ)
            var d = q - P; double h = Vector3d.Dot(d, A); var rad = d - A * h; double rho = rad.Length;
            var e = rho > 1e-30 ? rad / rho : X;
            var g = new Vector2d(-C, S);                    // единичное направление образующей (h, ρ) — на единицу длины
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

    public override Vector3d Project(Vector3d q)
    {
        var (u0, u1, v0, v1) = Range;
        double bu = u0, bv = v0, best = double.MaxValue;
        const int G = 24;
        for (int i = 0; i <= G; i++) for (int j = 0; j <= G; j++)
            {
                double u = u0 + (u1 - u0) * i / G, v = v0 + (v1 - v0) * j / G;
                double d = (At(u, v) - q).LengthSquared; if (d < best) { best = d; bu = u; bv = v; }
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

    public static Vector3d[] Solve(double[] knots, int p, double[] prm, IReadOnlyList<Vector3d> q)
    {
        int n = q.Count;
        var A = new double[n, n];
        for (int i = 0; i < n; i++) { var b = Eval(knots, p, n, prm[i]); if (i == n - 1) { b = new double[n]; b[n - 1] = 1; } for (int j = 0; j < n; j++) A[i, j] = b[j]; }
        var X = new Vector3d[n]; var B = q.ToArray();
        // Гаусс с выбором ведущего
        var M = (double[,])A.Clone();
        var idx = Enumerable.Range(0, n).ToArray();
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[piv, c])) piv = r;
            if (piv != c) { for (int j = 0; j < n; j++) (M[c, j], M[piv, j]) = (M[piv, j], M[c, j]); (B[c], B[piv]) = (B[piv], B[c]); }
            double d = M[c, c]; if (Math.Abs(d) < 1e-300) continue;
            for (int r = c + 1; r < n; r++)
            {
                double fct = M[r, c] / d; if (fct == 0) continue;
                for (int j = c; j < n; j++) M[r, j] -= fct * M[c, j];
                B[r] -= B[c] * fct;
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            var s = B[r]; for (int j = r + 1; j < n; j++) s -= X[j] * M[r, j];
            X[r] = M[r, r] != 0 ? s / M[r, r] : s;
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
