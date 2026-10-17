using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Features;

namespace Parametric3D.Model;

/// <summary>u13j: NURBS-поверхность как данные шага FreeForm (степени, сетка опорных точек, веса, узлы).</summary>
public sealed class NurbsDef
{
    public int DegU = 3, DegV = 3, Nu, Nv;
    public Vector3d[,] Cp = new Vector3d[0, 0];
    public double[,] W = new double[0, 0];
    public double[] Ku = Array.Empty<double>(), Kv = Array.Empty<double>();

    public NurbsSurface Surface => new(Cp, DegU, DegV, W, Ku, Kv);

    public static NurbsDef Of(NurbsSurface s)
    {
        var d = new NurbsDef { DegU = s.DegreeU, DegV = s.DegreeV, Nu = s.CountU, Nv = s.CountV, Ku = s.KnotsU.ToArray(), Kv = s.KnotsV.ToArray() };
        d.Cp = new Vector3d[d.Nu, d.Nv]; d.W = new double[d.Nu, d.Nv];
        for (int i = 0; i < d.Nu; i++) for (int j = 0; j < d.Nv; j++) { d.Cp[i, j] = s.ControlPoint(i, j); d.W[i, j] = s.Weight(i, j); }
        return d;
    }

    public NurbsDef Clone() => new() { DegU = DegU, DegV = DegV, Nu = Nu, Nv = Nv, Cp = (Vector3d[,])Cp.Clone(), W = (double[,])W.Clone(), Ku = (double[])Ku.Clone(), Kv = (double[])Kv.Clone() };

    /// <summary>Плоский список чисел для документа: степени, размеры, точки, веса, узлы.</summary>
    public List<double> Pack()
    {
        var r = new List<double> { DegU, DegV, Nu, Nv };
        for (int i = 0; i < Nu; i++) for (int j = 0; j < Nv; j++) { var p = Cp[i, j]; r.Add(p.X); r.Add(p.Y); r.Add(p.Z); }
        for (int i = 0; i < Nu; i++) for (int j = 0; j < Nv; j++) r.Add(W[i, j]);
        r.AddRange(Ku); r.AddRange(Kv);
        return r;
    }

    public static NurbsDef? Unpack(List<double>? a)
    {
        if (a == null || a.Count < 4) return null;
        var d = new NurbsDef { DegU = (int)a[0], DegV = (int)a[1], Nu = (int)a[2], Nv = (int)a[3] };
        int k = 4;
        if (a.Count < 4 + d.Nu * d.Nv * 4 + d.Nu + d.DegU + 1 + d.Nv + d.DegV + 1) return null;
        d.Cp = new Vector3d[d.Nu, d.Nv]; d.W = new double[d.Nu, d.Nv];
        for (int i = 0; i < d.Nu; i++) for (int j = 0; j < d.Nv; j++) { d.Cp[i, j] = new Vector3d(a[k], a[k + 1], a[k + 2]); k += 3; }
        for (int i = 0; i < d.Nu; i++) for (int j = 0; j < d.Nv; j++) d.W[i, j] = a[k++];
        d.Ku = a.Skip(k).Take(d.Nu + d.DegU + 1).ToArray(); k += d.Nu + d.DegU + 1;
        d.Kv = a.Skip(k).Take(d.Nv + d.DegV + 1).ToArray();
        return d;
    }
}

/// <summary>
/// u13j: ПОВЕРХНОСТИ FREEFORM — линейчатая, сетка кривых (Гордон), заплатка (Кунс), мостик между краями (G1),
/// продление, обращение, правка опорных точек, вес, узел — всё как NURBS-поверхность (интерполяция сетки точек, Пигл–
/// Тиллер A9.4); квилт из неё — грань с точной NURBS-поверхностью на основе «юбка + дно» (для отверждения, замены грани).
/// </summary>
public static class FFSurfaces
{
    // ------------------------------------------------------------------ интерполяция сетки

    static double[] Knots(double[] u, int p)
    {
        int n = u.Length - 1;
        var k = new double[n + p + 2];
        for (int i = 0; i <= p; i++) { k[i] = 0; k[^(i + 1)] = 1; }
        for (int j = 1; j <= n - p; j++) { double s = 0; for (int i = j; i < j + p; i++) s += u[i]; k[j + p] = s / p; }
        return k;
    }

    static Vector3d[] InterpWith(IReadOnlyList<Vector3d> q, int p, double[] u, double[] k)
    {
        int n = q.Count - 1;
        var a = new double[n + 1, n + 1];
        for (int r = 0; r <= n; r++) for (int c = 0; c <= n; c++) a[r, c] = FFCurves.Basis(c, p, u[r], k);
        return FFCurves.Solve(a, q.ToArray());
    }

    /// <summary>Интерполяция сетки с РАВНОМЕРНЫМИ параметрами: узлы сетки — точки (i/n, j/m) поверхности (общие края лоскутов).</summary>
    public static NurbsSurface InterpolateUniform(Vector3d[,] q, int pu = 3, int pv = 3) => Interpolate(q, pu, pv, uniform: true);

    /// <summary>Интерполяция сетки q[i, j] (i — вдоль u, j — вдоль v) NURBS-поверхностью степеней pu, pv.</summary>
    public static NurbsSurface Interpolate(Vector3d[,] q, int pu = 3, int pv = 3, bool uniform = false)
    {
        int nu = q.GetLength(0), nv = q.GetLength(1);
        if (nu < 2 || nv < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Surface: the point grid is smaller than 2×2.");
        pu = Math.Clamp(pu, 1, nu - 1); pv = Math.Clamp(pv, 1, nv - 1);
        var u = new double[nu]; var v = new double[nv];
        if (uniform)
        {
            for (int i = 0; i < nu; i++) u[i] = (double)i / (nu - 1);
            for (int j = 0; j < nv; j++) v[j] = (double)j / (nv - 1);
        }
        else
        {
            for (int j = 0; j < nv; j++) { var col = Enumerable.Range(0, nu).Select(i => q[i, j]).ToList(); var cu = FFCurves.ChordParams(col); for (int i = 0; i < nu; i++) u[i] += cu[i] / nv; }
            for (int i = 0; i < nu; i++) { var row = Enumerable.Range(0, nv).Select(j => q[i, j]).ToList(); var cv = FFCurves.ChordParams(row); for (int j = 0; j < nv; j++) v[j] += cv[j] / nu; }
        }
        u[0] = 0; u[^1] = 1; v[0] = 0; v[^1] = 1;
        var ku = Knots(u, pu); var kv = Knots(v, pv);
        var r = new Vector3d[nu, nv];
        for (int j = 0; j < nv; j++)
        {
            var c = InterpWith(Enumerable.Range(0, nu).Select(i => q[i, j]).ToList(), pu, u, ku);
            for (int i = 0; i < nu; i++) r[i, j] = c[i];
        }
        var cp = new Vector3d[nu, nv];
        for (int i = 0; i < nu; i++)
        {
            var c = InterpWith(Enumerable.Range(0, nv).Select(j => r[i, j]).ToList(), pv, v, kv);
            for (int j = 0; j < nv; j++) cp[i, j] = c[j];
        }
        return new NurbsSurface(cp, pu, pv, null, ku, kv);
    }

    /// <summary>Ломаная — n+1 точек равномерно по длине.</summary>
    public static List<Vector3d> Resample(IReadOnlyList<Vector3d> pts, int n)
    {
        var q = FFCurves.Dedup(pts);
        if ((pts[0] - pts[^1]).Length < 1e-9 && pts.Count > 2) q.Add(q[0]);
        var acc = new double[q.Count];
        for (int i = 1; i < q.Count; i++) acc[i] = acc[i - 1] + (q[i] - q[i - 1]).Length;
        double total = acc[^1];
        var res = new List<Vector3d>(n + 1);
        int k = 1;
        for (int i = 0; i <= n; i++)
        {
            double s = total * i / n;
            while (k < q.Count - 1 && acc[k] < s) k++;
            double l = acc[k] - acc[k - 1];
            double t = l < 1e-15 ? 0 : Math.Clamp((s - acc[k - 1]) / l, 0, 1);
            res.Add(q[k - 1] + (q[k] - q[k - 1]) * t);
        }
        return res;
    }

    /// <summary>Направить вторую кривую как первую (начало к началу).</summary>
    public static List<Vector3d> Align(IReadOnlyList<Vector3d> a, List<Vector3d> b)
    {
        if ((a[0] - b[0]).Length + (a[^1] - b[^1]).Length > (a[0] - b[^1]).Length + (a[^1] - b[0]).Length) b.Reverse();
        return b;
    }

    // ------------------------------------------------------------------ построения

    /// <summary>ЛИНЕЙЧАТАЯ поверхность между двумя кривыми (степень 1 поперёк).</summary>
    public static NurbsSurface Ruled(IReadOnlyList<Vector3d> a, IReadOnlyList<Vector3d> b, int n = 24)
    {
        var ra = Resample(a, n); var rb = Align(ra, Resample(b, n));
        var q = new Vector3d[n + 1, 2];
        for (int i = 0; i <= n; i++) { q[i, 0] = ra[i]; q[i, 1] = rb[i]; }
        return Interpolate(q, 3, 1);
    }

    /// <summary>Естественный кубический сплайн по узлам x (возрастающим) — значение в t (покоординатно).</summary>
    public static Vector3d Spline(double[] x, IReadOnlyList<Vector3d> y, double t)
    {
        int n = x.Length;
        if (n == 1) return y[0];
        if (n == 2) { double s = (t - x[0]) / (x[1] - x[0]); return y[0] + (y[1] - y[0]) * s; }
        var m = new Vector3d[n];            // вторые производные
        var c = new double[n]; var d = new Vector3d[n];
        for (int i = 1; i < n - 1; i++)
        {
            double h0 = x[i] - x[i - 1], h1 = x[i + 1] - x[i];
            double a = h0, b = 2 * (h0 + h1), cc = h1;
            var r = 6 * ((y[i + 1] - y[i]) / h1 - (y[i] - y[i - 1]) / h0);
            double den = b - a * c[i - 1];
            c[i] = cc / den; d[i] = (r - a * d[i - 1]) / den;
        }
        for (int i = n - 2; i >= 1; i--) m[i] = d[i] - c[i] * m[i + 1];
        int k = 0; while (k < n - 2 && t > x[k + 1]) k++;
        double hk = x[k + 1] - x[k];
        double A = (x[k + 1] - t) / hk, B = (t - x[k]) / hk;
        return A * y[k] + B * y[k + 1] + ((A * A * A - A) * m[k] + (B * B * B - B) * m[k + 1]) * (hk * hk) / 6;
    }

    static double[] Uniform(int n) => Enumerable.Range(0, n).Select(i => n == 1 ? 0 : (double)i / (n - 1)).ToArray();

    /// <summary>
    /// СЕТКА КРИВЫХ (Гордон): S = Lu + Lv − T; Lu — сплайн поперёк кривых U, Lv — поперёк кривых V, T — по точкам их
    /// пересечения (ближайшие точки). Кривые параметризуются по длине; поверхность проходит через кривые с отклонением,
    /// которое показывается.
    /// </summary>
    public static NurbsSurface Network(IReadOnlyList<IReadOnlyList<Vector3d>> uCurves, IReadOnlyList<IReadOnlyList<Vector3d>> vCurves, int n = 24)
    {
        if (uCurves.Count < 2 || vCurves.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Network: at least two curves in each direction are needed.");
        // направления: все U — как первая, все V — от первой U ко второй
        var U = uCurves.Select(c => Resample(c, n)).ToList();
        for (int i = 1; i < U.Count; i++) U[i] = Align(U[0], U[i]);
        var V = vCurves.Select(c => Resample(c, n)).ToList();
        for (int j = 0; j < V.Count; j++) if ((V[j][0] - U[0][n / 2]).Length > (V[j][^1] - U[0][n / 2]).Length && DistTo(U[0], V[j][^1]) < DistTo(U[0], V[j][0])) V[j].Reverse();
        // порядок: U — по удалению от начала первой V, V — по удалению вдоль первой U
        U = U.OrderBy(c => ParamOn(V[0], Nearest(V[0], c))).ToList();
        V = V.OrderBy(c => ParamOn(U[0], Nearest(U[0], c))).ToList();
        var su = Uniform(U.Count); var sv = Uniform(V.Count);
        // пересечения P[i, j] = ближайшие точки U_i и V_j
        var P = new Vector3d[U.Count, V.Count];
        for (int i = 0; i < U.Count; i++) for (int j = 0; j < V.Count; j++) P[i, j] = (Nearest(U[i], V[j]) + Nearest(V[j], U[i])) / 2;
        // параметры поперёк: средние по пересечениям
        var tU = new double[U.Count]; var tV = new double[V.Count];
        for (int i = 0; i < U.Count; i++) tU[i] = Enumerable.Range(0, V.Count).Average(j => ParamOn(V[j], P[i, j]));
        for (int j = 0; j < V.Count; j++) tV[j] = Enumerable.Range(0, U.Count).Average(i => ParamOn(U[i], P[i, j]));
        tU = Monotone(tU); tV = Monotone(tV);
        var q = new Vector3d[n + 1, n + 1];
        for (int a = 0; a <= n; a++)            // a — вдоль U-кривых (параметр s)
            for (int b = 0; b <= n; b++)        // b — поперёк (параметр t)
            {
                double s = (double)a / n, t = (double)b / n;
                var lu = Spline(tU, U.Select(c => At(c, s)).ToList(), t);
                var lv = Spline(tV, V.Select(c => At(c, t)).ToList(), s);
                var tp = Spline(tU, Enumerable.Range(0, U.Count).Select(i => Spline(tV, Enumerable.Range(0, V.Count).Select(j => P[i, j]).ToList(), s)).ToList(), t);
                q[a, b] = lu + lv - tp;
            }
        return Interpolate(q);
    }

    static double[] Monotone(double[] t)
    {
        var r = (double[])t.Clone();
        r[0] = 0; r[^1] = 1;
        for (int i = 1; i < r.Length - 1; i++) r[i] = Math.Clamp(r[i], r[i - 1] + 1e-3, 1 - 1e-3 * (r.Length - 1 - i));
        return r;
    }

    static double DistTo(IReadOnlyList<Vector3d> c, Vector3d p) => (Nearest(c, p) - p).Length;

    /// <summary>Точка ломаной на доле длины s.</summary>
    public static Vector3d At(IReadOnlyList<Vector3d> c, double s)
    {
        double f = Math.Clamp(s, 0, 1) * (c.Count - 1);
        int i = Math.Min((int)f, c.Count - 2);
        return c[i] + (c[i + 1] - c[i]) * (f - i);
    }

    /// <summary>Доля длины ломаной (равномерной), ближайшая к p.</summary>
    static double ParamOn(IReadOnlyList<Vector3d> c, Vector3d p)
    {
        double best = double.MaxValue, bs = 0;
        for (int i = 1; i < c.Count; i++)
        {
            var x = FFCurves.Closest(c[i - 1], c[i], p); double d = (x - p).Length;
            if (d < best) { best = d; double l = (c[i] - c[i - 1]).Length; bs = (i - 1 + (l < 1e-15 ? 0 : (x - c[i - 1]).Length / l)) / (c.Count - 1); }
        }
        return bs;
    }

    static Vector3d Nearest(IReadOnlyList<Vector3d> c, Vector3d p)
    {
        double best = double.MaxValue; Vector3d bx = c[0];
        for (int i = 1; i < c.Count; i++) { var x = FFCurves.Closest(c[i - 1], c[i], p); double d = (x - p).LengthSquared; if (d < best) { best = d; bx = x; } }
        return bx;
    }

    static Vector3d Nearest(IReadOnlyList<Vector3d> c, IReadOnlyList<Vector3d> other)
    {
        double best = double.MaxValue; Vector3d bx = c[0];
        foreach (var p in other) { var x = Nearest(c, p); double d = (x - p).LengthSquared; if (d < best) { best = d; bx = x; } }
        return bx;
    }

    /// <summary>
    /// ЗАПЛАТКА (Кунс) по замкнутому контуру: одна замкнутая кривая — на четыре стороны по длине; четыре кривые — стороны
    /// (сшиваются в контур); иначе — контур делится на четыре.
    /// </summary>
    public static NurbsSurface Patch(IReadOnlyList<IReadOnlyList<Vector3d>> curves, int n = 24)
    {
        List<Vector3d> loop;
        if (curves.Count == 1) loop = curves[0].ToList();
        else loop = FFCurves.Join(curves, 1e-3 * Math.Max(1, curves.SelectMany(c => c).Max(p => p.Length)));
        if ((loop[0] - loop[^1]).Length > 1e-6 * Math.Max(1, loop.Max(p => p.Length))) throw new RebuildException(OperationOutcome.InvalidInput, "Patch: the boundary is not closed.");
        List<List<Vector3d>> sides;
        if (curves.Count == 4) sides = SidesFromCurves(curves, loop);
        else
        {
            var r = Resample(loop, 4 * n);
            sides = Enumerable.Range(0, 4).Select(k => r.Skip(k * n).Take(n + 1).ToList()).ToList();
        }
        var c0 = Resample(sides[0], n); var d1 = Resample(sides[1], n);
        var c1 = Resample(sides[2], n); c1.Reverse(); var d0 = Resample(sides[3], n); d0.Reverse();
        var q = new Vector3d[n + 1, n + 1];
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                double u = (double)i / n, v = (double)j / n;
                q[i, j] = (1 - v) * c0[i] + v * c1[i] + (1 - u) * d0[j] + u * d1[j]
                        - ((1 - u) * (1 - v) * c0[0] + u * (1 - v) * c0[n] + (1 - u) * v * c1[0] + u * v * c1[n]);
            }
        return Interpolate(q);
    }

    static List<List<Vector3d>> SidesFromCurves(IReadOnlyList<IReadOnlyList<Vector3d>> curves, List<Vector3d> loop)
    {
        // стороны по порядку контура: каждая кривая — в направлении обхода
        var res = new List<List<Vector3d>>();
        var left = curves.Select(c => c.ToList()).ToList();
        var cur = left[0]; left.RemoveAt(0); res.Add(cur);
        while (left.Count > 0)
        {
            var end = res[^1][^1];
            int bi = -1; bool rev = false; double bd = double.MaxValue;
            for (int i = 0; i < left.Count; i++)
            {
                double d0 = (left[i][0] - end).Length, d1 = (left[i][^1] - end).Length;
                if (d0 < bd) { bd = d0; bi = i; rev = false; }
                if (d1 < bd) { bd = d1; bi = i; rev = true; }
            }
            var c = left[bi]; left.RemoveAt(bi); if (rev) c.Reverse(); res.Add(c);
        }
        return res;
    }

    /// <summary>
    /// МОСТИК между двумя краями (G1): кривые краёв и направления «от грани» (поперёк края, в касательной плоскости
    /// грани); поперёк — кубическая Эрмита, натяжение — доля расстояния.
    /// </summary>
    public static NurbsSurface Blend(IReadOnlyList<Vector3d> a, IReadOnlyList<Vector3d> ta, IReadOnlyList<Vector3d> b, IReadOnlyList<Vector3d> tb, double tension = 1, int n = 24, int m = 12)
    {
        var ra = Resample(a, n); var rb = Resample(b, n);
        var ta2 = ResampleDirs(a, ta, n); var tb2 = ResampleDirs(b, tb, n);
        if ((ra[0] - rb[0]).Length + (ra[^1] - rb[^1]).Length > (ra[0] - rb[^1]).Length + (ra[^1] - rb[0]).Length) { rb.Reverse(); tb2.Reverse(); }
        var q = new Vector3d[n + 1, m + 1];
        for (int i = 0; i <= n; i++)
        {
            double dist = (rb[i] - ra[i]).Length;
            var d0 = ta2[i] * (tension * dist); var d1 = -tb2[i] * (tension * dist);
            for (int j = 0; j <= m; j++)
            {
                double s = (double)j / m;
                double h00 = 2 * s * s * s - 3 * s * s + 1, h10 = s * s * s - 2 * s * s + s, h01 = -2 * s * s * s + 3 * s * s, h11 = s * s * s - s * s;
                q[i, j] = h00 * ra[i] + h10 * d0 + h01 * rb[i] + h11 * d1;
            }
        }
        return Interpolate(q);
    }

    static List<Vector3d> ResampleDirs(IReadOnlyList<Vector3d> pts, IReadOnlyList<Vector3d> dirs, int n)
    {
        var acc = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++) acc[i] = acc[i - 1] + (pts[i] - pts[i - 1]).Length;
        var res = new List<Vector3d>();
        int k = 1;
        for (int i = 0; i <= n; i++)
        {
            double s = acc[^1] * i / n;
            while (k < pts.Count - 1 && acc[k] < s) k++;
            double l = acc[k] - acc[k - 1]; double t = l < 1e-15 ? 0 : Math.Clamp((s - acc[k - 1]) / l, 0, 1);
            var d = dirs[k - 1] * (1 - t) + dirs[k] * t;
            res.Add(d.LengthSquared < 1e-24 ? dirs[k] : d.Normalized());
        }
        return res;
    }

    /// <summary>Сетка точек поверхности (n+1)×(n+1).</summary>
    public static Vector3d[,] Grid(NurbsSurface s, int n)
    {
        var g = new Vector3d[n + 1, n + 1];
        for (int i = 0; i <= n; i++) for (int j = 0; j <= n; j++) g[i, j] = s.PointAt(new Vector2d((double)i / n, (double)j / n));
        return g;
    }

    /// <summary>ПРОДЛЕНИЕ края на длину d (по касательной поперёк края): side 0 — u=0, 1 — u=1, 2 — v=0, 3 — v=1.</summary>
    public static NurbsSurface Extend(NurbsSurface s, int side, double d, int n = 24)
    {
        if (!(d > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Extend: the length must be positive.");
        var g = Grid(s, n);
        int extra = Math.Max(2, n / 4);
        bool alongU = side < 2;               // продление по u (новые столбцы i)
        int nu = n + 1 + (alongU ? extra : 0), nv = n + 1 + (alongU ? 0 : extra);
        var q = new Vector3d[nu, nv];
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                int ii = i + (side == 0 ? extra : 0), jj = j + (side == 2 ? extra : 0);
                q[ii, jj] = g[i, j];
            }
        for (int k = 0; k <= n; k++)
        {
            // край и касательная наружу
            Vector3d p, t;
            switch (side)
            {
                case 0: p = g[0, k]; t = (g[0, k] - g[1, k]).Normalized(); break;
                case 1: p = g[n, k]; t = (g[n, k] - g[n - 1, k]).Normalized(); break;
                case 2: p = g[k, 0]; t = (g[k, 0] - g[k, 1]).Normalized(); break;
                default: p = g[k, n]; t = (g[k, n] - g[k, n - 1]).Normalized(); break;
            }
            for (int e = 1; e <= extra; e++)
            {
                var x = p + t * (d * e / extra);
                switch (side)
                {
                    case 0: q[extra - e, k] = x; break;
                    case 1: q[n + e, k] = x; break;
                    case 2: q[k, extra - e] = x; break;
                    default: q[k, n + e] = x; break;
                }
            }
        }
        return Interpolate(q);
    }

    /// <summary>Обратить нормаль: та же поверхность с обратным u.</summary>
    public static NurbsDef Flip(NurbsDef d)
    {
        var r = d.Clone();
        for (int i = 0; i < d.Nu; i++) for (int j = 0; j < d.Nv; j++) { r.Cp[i, j] = d.Cp[d.Nu - 1 - i, j]; r.W[i, j] = d.W[d.Nu - 1 - i, j]; }
        r.Ku = d.Ku.Select(k => 1 - k).Reverse().ToArray();
        return r;
    }

    // ------------------------------------------------------------------ квилт из NURBS

    /// <summary>
    /// КВИЛТ ИЗ NURBS-ПОВЕРХНОСТИ: грань с точной поверхностью (разметка n×n) на основе: «юбка» — край, опущенный против
    /// средней нормали до плоскости ниже поверхности, и плоское дно. Основа замкнута — отверждение даёт тело под
    /// поверхностью, замена грани — столб, срезанный поверхностью.
    /// </summary>
    public static Quilt Quilt(NurbsSurface s, string key, string name, int n = 24)
    {
        var g = Grid(s, n);
        var nrm = Vector3d.Zero;
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) nrm += Vector3d.Cross(g[i + 1, j] - g[i, j], g[i, j + 1] - g[i, j]);
        if (nrm.LengthSquared < 1e-24) throw new RebuildException(OperationOutcome.InvalidInput, "Surface: the surface is degenerate.");
        nrm.Normalize();
        double size = 0; var bb = Bounds3.Empty; foreach (var p in g) bb.Add(p); size = Math.Max(1, (bb.Max - bb.Min).Length);
        double low = double.MaxValue; foreach (var p in g) low = Math.Min(low, Vector3d.Dot(p, nrm));
        double bottom = low - 0.25 * size;
        Vector3d Down(Vector3d p) => p - nrm * (Vector3d.Dot(p, nrm) - bottom);
        const uint TopTag = 1, SideTag = 2, BottomTag = 3;
        var polys = new List<CsgPolygon>();
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var a = g[i, j]; var b = g[i + 1, j]; var c = g[i + 1, j + 1]; var d = g[i, j + 1];
                if (Vector3d.Cross(b - a, c - a).LengthSquared > 1e-24) polys.Add(new CsgPolygon(new List<Vector3d> { a, b, c }, TopTag));
                if (Vector3d.Cross(c - a, d - a).LengthSquared > 1e-24) polys.Add(new CsgPolygon(new List<Vector3d> { a, c, d }, TopTag));
            }
        // край по обходу (u вдоль v=0, затем v вдоль u=1, …) — согласован с треугольниками сетки (нормаль nrm наружу)
        var rim = new List<Vector3d>();
        for (int i = 0; i < n; i++) rim.Add(g[i, 0]);
        for (int j = 0; j < n; j++) rim.Add(g[n, j]);
        for (int i = n; i > 0; i--) rim.Add(g[i, n]);
        for (int j = n; j > 0; j--) rim.Add(g[0, j]);
        uint side = SideTag;
        for (int k = 0; k < rim.Count; k++)
        {
            var a = rim[k]; var b = rim[(k + 1) % rim.Count];
            var quad = new List<Vector3d> { b, a, Down(a), Down(b) };
            if (Vector3d.Cross(quad[1] - quad[0], quad[2] - quad[0]).LengthSquared > 1e-24) polys.Add(new CsgPolygon(quad, side));
        }
        // дно: контур края на плоскости, треугольники наружу (−nrm)
        var fr = Frame3.FromNormal(nrm * bottom, nrm);
        var flat = rim.Select(p => fr.ToLocal2(Down(p))).ToList();
        var tris = Kernel.Tess.Triangulator.Triangulate(flat, Array.Empty<IReadOnlyList<Vector2d>>(), out var pts2);
        for (int k = 0; k + 2 < tris.Count; k += 3)
        {
            var a = fr.ToWorld(pts2[tris[k]]); var b = fr.ToWorld(pts2[tris[k + 1]]); var c = fr.ToWorld(pts2[tris[k + 2]]);
            polys.Add(new CsgPolygon(new List<Vector3d> { a, c, b }, BottomTag));
        }
        var sources = new Dictionary<uint, ISurface3?> { [TopTag] = s };
        var solid = BooleanOp.SolidFromPolygons(polys, sources, new Dictionary<uint, string> { [TopTag] = "nurbs", [BottomTag] = "bottom" });
        var faces = solid.Faces.Where(f => f.Surface is NurbsSurface || (f.Surface is FacetedSurface fs && fs.Source is NurbsSurface)).ToList();
        if (faces.Count == 0) throw new RebuildException(OperationOutcome.InternalFailure, "Surface: the NURBS face was not built.");
        var q = new Quilt(key, name, solid, faces) { Nurbs = s };
        return q;
    }

    /// <summary>Наибольшее отклонение точек от поверхности (по сетке 64×64 и ближайшим точкам).</summary>
    public static double Deviation(NurbsSurface s, IEnumerable<Vector3d> pts)
    {
        double worst = 0;
        foreach (var p in pts)
        {
            var uv = s.ProjectToUV(p);
            worst = Math.Max(worst, (s.PointAt(uv) - p).Length);
        }
        return worst;
    }
}

/// <summary>
/// ПОВЕРХНОСТЬ FREEFORM: готовая NURBS-поверхность шага (геометрия без параметров) — новый квилт или замена квилта
/// с ключом ReplacesKey (правки: продление, обращение, опорные точки, вес, узел).
/// </summary>
public sealed class FFNurbsQuiltFeature : Feature
{
    public NurbsDef Def = new();
    public string? ReplacesKey;
    public string QuiltName = "";
    public FFNurbsQuiltFeature(string name = "Surface") { Name = name; }
    public override string Summary => $"NURBS surface {Def.Nu}×{Def.Nv}{(ReplacesKey != null ? " (edit)" : "")}";

    public override void Execute(BuildContext ctx)
    {
        if (Def.Nu < 2 || Def.Nv < 2) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the surface is empty.");
        NurbsSurface s;
        try { s = Def.Surface; } catch (ArgumentException ex) { throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: {ex.Message}"); }
        if (ReplacesKey != null)
        {
            var old = ctx.QuiltByKey(ReplacesKey) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the edited surface is not found.");
            var nq = FFSurfaces.Quilt(s, old.Key, old.Name);
            ctx.Quilts[ctx.Quilts.IndexOf(old)] = nq;
            return;
        }
        ctx.Quilts.Add(FFSurfaces.Quilt(s, Key, QuiltName.Length > 0 ? QuiltName : Name));
    }
}

/// <summary>ПЛОСКАЯ ПОВЕРХНОСТЬ по замкнутой плоской кривой (точки контура): точная плоская грань.</summary>
public sealed class FFPlanarQuiltFeature : Feature
{
    public readonly List<Vector3d> Boundary = new();
    public FFPlanarQuiltFeature(string name = "Planar surface") { Name = name; }
    public override string Summary => $"Planar surface ({Boundary.Count} points)";

    public override void Execute(BuildContext ctx)
    {
        var q = FFCurves.Dedup(Boundary);
        if (q.Count < 3) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the boundary has fewer than three points.");
        var c = q.Aggregate(Vector3d.Zero, (a, b) => a + b) / q.Count;
        var n = Vector3d.Zero;
        for (int i = 0; i < q.Count; i++) n += Vector3d.Cross(q[i] - c, q[(i + 1) % q.Count] - c);
        if (n.LengthSquared < 1e-18) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the boundary encloses no area.");
        n.Normalize();
        if (q.Any(p => Math.Abs(Vector3d.Dot(p - c, n)) > 1e-6 * Math.Max(1, (p - c).Length) + 1e-6))
            throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the curve is not planar.");
        var fr = Frame3.FromNormal(c, n);
        var lines = new List<ICurve3>();
        for (int i = 0; i < q.Count; i++) lines.Add(new LineCurve3(fr.ToWorld(fr.ToLocal2(q[i])), fr.ToWorld(fr.ToLocal2(q[(i + 1) % q.Count]))));
        var bb = Bounds3.Empty; foreach (var p in q) bb.Add(p);
        double depth = Math.Max(1, 0.25 * (bb.Max - bb.Min).Length);
        var prism = ExtrudeOp.Build(ProfileBuilder.Build(fr, lines), -n * depth);
        var top = prism.Faces.Where(f => SketchFaceRef.PlaneOf(f) is { } pl && Math.Abs(Vector3d.Dot(pl.P - c, n)) < 1e-6 && Math.Abs(Math.Abs(Vector3d.Dot(pl.N.Normalized(), n)) - 1) < 1e-9).ToList();
        if (top.Count == 0) throw new RebuildException(OperationOutcome.InternalFailure, $"{Name}: the planar face was not built.");
        ctx.Quilts.Add(new Quilt(Key, Name, prism, top));
    }
}

/// <summary>СШИТЬ В ТЕЛО: грани квилтов, образующие замкнутую оболочку, — тело (новое тело FreeForm; квилты расходуются).</summary>
public sealed class FFStitchFeature : Feature
{
    public readonly List<string> Sources = new();
    public FFStitchFeature(string name = "Stitch") { Name = name; }
    public override string Summary => $"Stitch {Sources.Count} surfaces";

    public override void Execute(BuildContext ctx)
    {
        if (Sources.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no surfaces.");
        var qs = Sources.Select(k => QuiltUtil.Require(ctx, k, Name)).ToList();
        var polys = new List<CsgPolygon>();
        var sources = new Dictionary<uint, ISurface3?>();
        uint tag = 1;
        foreach (var q in qs)
            foreach (var part in QuiltUtil.Simple(q))
                foreach (var f in part.Faces)
                {
                    var mb = new Kernel.Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb);
                    var md = new Kernel.Tess.MeshData(); mb.FillInto(md);
                    bool any = false;
                    for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                    {
                        var a = mb.PositionsD[md.Indices[i]]; var b = mb.PositionsD[md.Indices[i + 1]]; var c = mb.PositionsD[md.Indices[i + 2]];
                        if (Vector3d.Cross(b - a, c - a).LengthSquared < 1e-24) continue;
                        polys.Add(new CsgPolygon(new List<Vector3d> { a, b, c }, tag)); any = true;
                    }
                    if (any) { sources[tag] = f.Surface is FacetedSurface fs ? fs.Source : f.Surface; tag++; }
                }
        var solid = BooleanOp.SolidFromPolygons(polys, sources);
        var open = Kernel.Topology.ShellComponents.Invariant(solid);
        int free = solid.Edges.Count(e => e.CoEdges.Count < 2);
        if (free > 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the surfaces do not close a volume ({free} open edges).");
        if (AnalyticVolume.Of(solid) is double v && v < 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the surfaces face inwards (flip their normals).");
        ctx.Bodies.Add(solid);
        foreach (var q in qs) ctx.Quilts.Remove(q);
    }
}

/// <summary>Края для мостика: точки ребра и направления «от грани» (поперёк ребра в касательной плоскости грани).</summary>
public static class FFEdges
{
    public static (List<Vector3d> Pts, List<Vector3d> Away) Away(TEdge e, TFace f)
    {
        var pts = new List<Vector3d>();
        var ce = e.CoEdges.FirstOrDefault(c => ReferenceEquals(c.Loop?.Face, f)) ?? e.CoEdges.FirstOrDefault();
        if (ce != null) ce.Tessellate(pts, true); else pts.AddRange(FFCurves.Sample(e.Curve, 48));
        pts = FFCurves.Dedup(pts);
        if (pts.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Blend: the edge is too short.");
        var s = GeometryAudit.AuthoritativeSurface(f) ?? throw new RebuildException(OperationOutcome.InvalidInput, "Blend: the face has no surface.");
        var inside = FaceRefs.Inside(f);
        var dirs = new List<Vector3d>();
        for (int i = 0; i < pts.Count; i++)
        {
            var t = (pts[Math.Min(i + 1, pts.Count - 1)] - pts[Math.Max(i - 1, 0)]).Normalized();
            var n = s.NormalAt(s.ProjectToUV(pts[i]));
            var c = Vector3d.Cross(t, n);
            if (c.LengthSquared < 1e-24) c = (pts[i] - inside).Normalized(); else c.Normalize();
            dirs.Add(c);
        }
        // наружу от грани: по большинству — от внутренней точки
        double sum = 0; for (int i = 0; i < pts.Count; i++) sum += Vector3d.Dot(dirs[i], pts[i] - inside);
        if (sum < 0) for (int i = 0; i < dirs.Count; i++) dirs[i] = -dirs[i];
        return (pts, dirs);
    }

    /// <summary>Грань ребра для мостика: грань квилта; у тела — та, от которой направление ближе к другому краю.</summary>
    public static TFace PickFace(TEdge e, Func<TFace, bool> isQuilt, Vector3d towards)
    {
        var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (faces.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Blend: the edge has no face.");
        if (faces.FirstOrDefault(isQuilt) is { } q) return q;
        TFace best = faces[0]; double bd = double.MinValue;
        var mid = (e.V0.P + e.V1.P) / 2;
        foreach (var f in faces)
        {
            try
            {
                var (pts, away) = Away(e, f);
                int k = pts.Count / 2;
                double d = Vector3d.Dot(away[k], (towards - pts[k]).Normalized());
                if (d > bd) { bd = d; best = f; }
            }
            catch (RebuildException) { }
        }
        return best;
    }
}
