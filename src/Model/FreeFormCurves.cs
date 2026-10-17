using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Features;

namespace Parametric3D.Model;

/// <summary>
/// u13i: КРИВЫЕ FREEFORM — построения над ломаными и NURBS (опорные кривые DatumCurve): NURBS по опорным точкам,
/// интерполяция и перестроение (наименьшие квадраты), спираль, эквидистанта, скругление углов, соединение, продление,
/// мостик G1/G2, изопараметрическая кривая грани, обрезка по пересечению. Результат — геометрия без параметров (шаг
/// FreeForm фиксируется сразу, как в Plasticity).
/// </summary>
public static class FFCurves
{
    // ------------------------------------------------------------------ NURBS: интерполяция и наименьшие квадраты

    /// <summary>Параметры точек по длине хорд (0…1).</summary>
    public static double[] ChordParams(IReadOnlyList<Vector3d> q)
    {
        var u = new double[q.Count];
        double total = 0;
        for (int i = 1; i < q.Count; i++) total += (q[i] - q[i - 1]).Length;
        for (int i = 1; i < q.Count; i++) u[i] = total < 1e-15 ? (double)i / (q.Count - 1) : u[i - 1] + (q[i] - q[i - 1]).Length / total;
        u[^1] = 1;
        return u;
    }

    /// <summary>Базисная функция B-сплайна N_{i,p}(t) (Кокс — де Бур).</summary>
    public static double Basis(int i, int p, double t, IReadOnlyList<double> k)
    {
        if (p == 0)
        {
            if (t >= k[i] && t < k[i + 1]) return 1;
            // правый конец: последний ненулевой интервал включает t = конец
            if (t >= k[^1] - 1e-14 && k[i] < k[i + 1] && Math.Abs(k[i + 1] - k[^1]) < 1e-14) return 1;
            return 0;
        }
        double a = 0, b = 0;
        double d1 = k[i + p] - k[i], d2 = k[i + p + 1] - k[i + 1];
        if (d1 > 1e-15) a = (t - k[i]) / d1 * Basis(i, p - 1, t, k);
        if (d2 > 1e-15) b = (k[i + p + 1] - t) / d2 * Basis(i + 1, p - 1, t, k);
        return a + b;
    }

    /// <summary>Решение плотной системы A·X = B (X, B — векторы-точки) методом Гаусса с выбором главного.</summary>
    public static Vector3d[] Solve(double[,] a, Vector3d[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone(); var r = (Vector3d[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int i = c + 1; i < n; i++) if (Math.Abs(m[i, c]) > Math.Abs(m[piv, c])) piv = i;
            if (Math.Abs(m[piv, c]) < 1e-14) throw new RebuildException(OperationOutcome.InvalidInput, "Curve fit: the points do not define the curve (coincident points).");
            if (piv != c) { for (int j = 0; j < n; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]); (r[c], r[piv]) = (r[piv], r[c]); }
            for (int i = c + 1; i < n; i++)
            {
                double f = m[i, c] / m[c, c]; if (f == 0) continue;
                for (int j = c; j < n; j++) m[i, j] -= f * m[c, j];
                r[i] -= f * r[c];
            }
        }
        var x = new Vector3d[n];
        for (int i = n - 1; i >= 0; i--)
        {
            var s = r[i]; for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return x;
    }

    /// <summary>ИНТЕРПОЛЯЦИЯ (Пигл–Тиллер A9.1): NURBS степени p точно через точки; параметры — по хордам, узлы — усреднением.</summary>
    public static SplineCurve3 Interpolate(IReadOnlyList<Vector3d> q, int p = 3)
    {
        int n = q.Count - 1;
        if (n < 1) throw new RebuildException(OperationOutcome.InvalidInput, "Curve: at least two points are needed.");
        p = Math.Clamp(p, 1, n);
        var u = ChordParams(q);
        var k = new double[n + p + 2];
        for (int i = 0; i <= p; i++) { k[i] = 0; k[^(i + 1)] = 1; }
        for (int j = 1; j <= n - p; j++) { double s = 0; for (int i = j; i < j + p; i++) s += u[i]; k[j + p] = s / p; }
        var a = new double[n + 1, n + 1];
        for (int r = 0; r <= n; r++) for (int c = 0; c <= n; c++) a[r, c] = Basis(c, p, u[r], k);
        var cp = Solve(a, q.ToArray());
        return new SplineCurve3(cp, p, null, k);
    }

    /// <summary>
    /// ПЕРЕСТРОЕНИЕ (наименьшие квадраты, Пигл–Тиллер A9.7): NURBS степени p с h+1 опорными точками; концы — точно.
    /// </summary>
    public static SplineCurve3 Fit(IReadOnlyList<Vector3d> q, int count, int p = 3)
    {
        int m = q.Count - 1, h = count - 1;
        if (h >= m) return Interpolate(q, p);
        p = Math.Clamp(p, 1, h);
        if (h < 1) throw new RebuildException(OperationOutcome.InvalidInput, "Rebuild: at least two control points are needed.");
        var u = ChordParams(q);
        var k = new double[h + p + 2];
        for (int i = 0; i <= p; i++) { k[i] = 0; k[^(i + 1)] = 1; }
        double d = (m + 1.0) / (h - p + 1);
        for (int j = 1; j <= h - p; j++) { int i = (int)(j * d); double al = j * d - i; k[p + j] = (1 - al) * u[i - 1] + al * u[i]; }
        var cp = new Vector3d[h + 1];
        cp[0] = q[0]; cp[h] = q[m];
        if (h > 1)
        {
            var rk = new Vector3d[m + 1];
            for (int i = 1; i < m; i++) rk[i] = q[i] - Basis(0, p, u[i], k) * q[0] - Basis(h, p, u[i], k) * q[m];
            var nn = new double[h - 1, h - 1];
            var rhs = new Vector3d[h - 1];
            for (int i = 1; i < m; i++)
            {
                var row = new double[h - 1];
                for (int j = 1; j < h; j++) row[j - 1] = Basis(j, p, u[i], k);
                for (int a = 0; a < h - 1; a++)
                {
                    if (row[a] == 0) continue;
                    rhs[a] += row[a] * rk[i];
                    for (int b = 0; b < h - 1; b++) nn[a, b] += row[a] * row[b];
                }
            }
            var inner = Solve(nn, rhs);
            for (int j = 1; j < h; j++) cp[j] = inner[j - 1];
        }
        return new SplineCurve3(cp, p, null, k);
    }

    /// <summary>Наибольшее отклонение точек от кривой (по ближайшей точке ломаной кривой).</summary>
    public static double Deviation(IReadOnlyList<Vector3d> q, IReadOnlyList<Vector3d> curve)
    {
        double worst = 0;
        foreach (var p in q)
        {
            double best = double.MaxValue;
            for (int i = 1; i < curve.Count; i++) best = Math.Min(best, (Closest(curve[i - 1], curve[i], p) - p).Length);
            worst = Math.Max(worst, best);
        }
        return worst;
    }

    public static Vector3d Closest(Vector3d a, Vector3d b, Vector3d p)
    {
        var d = b - a; double l2 = d.LengthSquared;
        if (l2 < 1e-30) return a;
        return a + d * Math.Clamp(Vector3d.Dot(p - a, d) / l2, 0, 1);
    }

    public static List<Vector3d> Sample(ICurve3 c, int n)
    {
        var res = new List<Vector3d>(n + 1);
        for (int i = 0; i <= n; i++) res.Add(c.PointAt(c.T0 + (c.T1 - c.T0) * i / n));
        return res;
    }

    // ------------------------------------------------------------------ построения

    /// <summary>Спираль (винтовая линия): радиус r0 → r1 за turns оборотов, шаг pitch вдоль нормали плоскости; pitch = 0 — плоская.</summary>
    public static List<Vector3d> Spiral(Frame3 fr, double r0, double r1, double pitch, double turns, bool leftHand = false, int perTurn = 72)
    {
        if (!(turns > 0)) throw new RebuildException(OperationOutcome.InvalidInput, "Spiral: the number of turns must be positive.");
        if (r0 < 0 || r1 < 0 || r0 + r1 < Tol.Weld) throw new RebuildException(OperationOutcome.InvalidInput, "Spiral: the radius must be positive.");
        int n = Math.Max(8, (int)Math.Ceiling(turns * perTurn));
        var res = new List<Vector3d>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double s = (double)i / n, th = 2 * Math.PI * turns * s * (leftHand ? -1 : 1);
            double r = r0 + (r1 - r0) * s;
            res.Add(fr.ToWorld(new Vector3d(r * Math.Cos(th), r * Math.Sin(th), pitch * turns * s)));
        }
        return res;
    }

    /// <summary>Эквидистанта в плоскости с нормалью n: влево по ходу (d &gt; 0) или вправо; углы — со срезом острия.</summary>
    public static List<Vector3d> Offset(IReadOnlyList<Vector3d> pts, Vector3d n, double d)
    {
        if (pts.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Offset: the curve is empty.");
        if (Math.Abs(d) < Tol.Weld) throw new RebuildException(OperationOutcome.InvalidInput, "Offset: the distance is zero.");
        n = n.Normalized();
        bool closed = (pts[0] - pts[^1]).Length < 1e-6 && pts.Count > 3;
        var q = Dedup(pts);
        int c = q.Count;
        Vector3d SegDir(int i) { var v = q[(i + 1) % c] - q[i % c]; v -= n * Vector3d.Dot(v, n); return v.LengthSquared < 1e-24 ? Vector3d.UnitX : v.Normalized(); }
        Vector3d Side(int i) => Vector3d.Cross(n, SegDir(i)).Normalized();
        int segs = closed ? c : c - 1;
        var res = new List<Vector3d>();
        for (int i = 0; i < c; i++)
        {
            Vector3d o;
            if (!closed && i == 0) o = Side(0) * d;
            else if (!closed && i == c - 1) o = Side(segs - 1) * d;
            else
            {
                var s1 = Side((i - 1 + segs) % segs); var s2 = Side(i % segs);
                var m = s1 + s2;
                if (m.LengthSquared < 1e-12) m = s1; else m.Normalize();
                double cos = Math.Max(0.2, Vector3d.Dot(m, s1));
                o = m * (d / cos);
            }
            res.Add(q[i] + o);
        }
        if (closed) res.Add(res[0]);
        return res;
    }

    /// <summary>Убрать совпадающие соседние точки (и повтор начала в конце замкнутой).</summary>
    public static List<Vector3d> Dedup(IReadOnlyList<Vector3d> pts)
    {
        var q = new List<Vector3d>();
        foreach (var p in pts) if (q.Count == 0 || (p - q[^1]).Length > 1e-9) q.Add(p);
        if (q.Count > 2 && (q[0] - q[^1]).Length < 1e-6) q.RemoveAt(q.Count - 1);
        return q;
    }

    /// <summary>Скругление углов ломаной радиусом r: угол (излом больше 1°) заменяется дугой.</summary>
    public static List<Vector3d> FilletCorners(IReadOnlyList<Vector3d> pts, double r, out int rounded, int arcSteps = 16)
    {
        if (!(r > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Curve fillet: the radius must be positive.");
        bool closed = (pts[0] - pts[^1]).Length < 1e-6 && pts.Count > 3;
        var q = Dedup(pts);
        int c = q.Count;
        rounded = 0;
        var res = new List<Vector3d>();
        for (int i = 0; i < c; i++)
        {
            bool end = !closed && (i == 0 || i == c - 1);
            if (end) { res.Add(q[i]); continue; }
            var v = q[i]; var prev = q[(i - 1 + c) % c]; var next = q[(i + 1) % c];
            var a = prev - v; var b = next - v;
            double la = a.Length, lb = b.Length;
            if (la < 1e-12 || lb < 1e-12) { res.Add(v); continue; }
            a /= la; b /= lb;
            double th = Math.Acos(Math.Clamp(Vector3d.Dot(a, b), -1, 1));       // внутренний угол
            if (Math.PI - th < Math.PI / 180) { res.Add(v); continue; }           // почти прямо
            double l = r / Math.Tan(th / 2);
            if (l > 0.5 * Math.Min(la, lb) + 1e-9) { res.Add(v); continue; }    // радиус не помещается
            var bis = (a + b).Normalized();
            var ctr = v + bis * (r / Math.Sin(th / 2));
            var p0 = v + a * l; var p1 = v + b * l;
            var e0 = (p0 - ctr).Normalized(); var e1 = (p1 - ctr).Normalized();
            double phi = Math.Acos(Math.Clamp(Vector3d.Dot(e0, e1), -1, 1));
            var ax = Vector3d.Cross(e0, e1); if (ax.LengthSquared < 1e-24) { res.Add(v); continue; }
            ax.Normalize();
            for (int k = 0; k <= arcSteps; k++)
            {
                double t = phi * k / arcSteps;
                var dir = e0 * Math.Cos(t) + Vector3d.Cross(ax, e0) * Math.Sin(t);
                res.Add(ctr + dir * r);
            }
            rounded++;
        }
        if (closed) res.Add(res[0]);
        return res;
    }

    /// <summary>Соединить кривые в одну по совпадающим концам (порядок и направление подбираются).</summary>
    public static List<Vector3d> Join(IReadOnlyList<IReadOnlyList<Vector3d>> curves, double tol)
    {
        if (curves.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Join: at least two curves are needed.");
        var left = curves.Select(c => c.ToList()).ToList();
        var chain = left[0]; left.RemoveAt(0);
        while (left.Count > 0)
        {
            bool found = false;
            for (int i = 0; i < left.Count && !found; i++)
            {
                var c = left[i];
                if ((c[0] - chain[^1]).Length <= tol) { chain.AddRange(c.Skip(1)); found = true; }
                else if ((c[^1] - chain[^1]).Length <= tol) { c.Reverse(); chain.AddRange(c.Skip(1)); found = true; }
                else if ((c[^1] - chain[0]).Length <= tol) { chain.InsertRange(0, c.Take(c.Count - 1)); found = true; }
                else if ((c[0] - chain[0]).Length <= tol) { c.Reverse(); chain.InsertRange(0, c.Take(c.Count - 1)); found = true; }
                if (found) left.RemoveAt(i);
            }
            if (!found) throw new RebuildException(OperationOutcome.InvalidInput, $"Join: {left.Count} curve(s) do not touch the chain (gap more than {tol:G3} mm).");
        }
        return chain;
    }

    /// <summary>Продление кривой на длину len: по касательной (прямой) или дугой по кривизне конца.</summary>
    public static List<Vector3d> Extend(IReadOnlyList<Vector3d> pts, double len, bool atStart, bool atEnd, bool arc, int steps = 24)
    {
        if (!(len > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Extend: the length must be positive.");
        var q = Dedup(pts);
        if (q.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Extend: the curve is empty.");
        List<Vector3d> Tail(Vector3d p2, Vector3d p1, Vector3d p0)
        {
            // p0 — конец, p1, p2 — перед ним
            var t = (p0 - p1).Normalized();
            var res = new List<Vector3d>();
            if (arc && CircleThrough(p2, p1, p0) is { } circ)
            {
                var (ctr, rad, nrm) = circ;
                double phi = len / rad;
                var e0 = (p0 - ctr).Normalized();
                var dirSign = Vector3d.Dot(Vector3d.Cross(nrm, e0), t) >= 0 ? 1 : -1;
                for (int k = 1; k <= steps; k++)
                {
                    double a = dirSign * phi * k / steps;
                    res.Add(ctr + (e0 * Math.Cos(a) + Vector3d.Cross(nrm, e0) * Math.Sin(a)) * rad);
                }
                return res;
            }
            res.Add(p0 + t * len);
            return res;
        }
        var outp = q.ToList();
        if (atEnd) outp.AddRange(Tail(q.Count > 2 ? q[^3] : q[^2], q[^2], q[^1]));
        if (atStart) { var head = Tail(q.Count > 2 ? q[2] : q[1], q[1], q[0]); head.Reverse(); outp.InsertRange(0, head); }
        return outp;
    }

    /// <summary>Окружность через три точки: центр, радиус, нормаль; почти на прямой — null.</summary>
    public static (Vector3d C, double R, Vector3d N)? CircleThrough(Vector3d a, Vector3d b, Vector3d c)
    {
        var ab = b - a; var ac = c - a;
        var n = Vector3d.Cross(ab, ac);
        double n2 = n.LengthSquared;
        if (n2 < 1e-18 * Math.Max(1, ab.LengthSquared * ac.LengthSquared)) return null;
        var ctr = a + (Vector3d.Cross(n, ab) * ac.LengthSquared + Vector3d.Cross(ac, n) * ab.LengthSquared) / (2 * n2);
        double r = (a - ctr).Length;
        if (r > 1e7) return null;
        return (ctr, r, n.Normalized());
    }

    /// <summary>Касательная и вектор кривизны в конце ломаной (наружу от кривой); кривизна — по окружности через три точки.</summary>
    public static (Vector3d P, Vector3d T, Vector3d K) EndFrame(IReadOnlyList<Vector3d> q, bool atEnd)
    {
        var a = atEnd ? q[^1] : q[0]; var b = atEnd ? q[^2] : q[1]; var c = q.Count > 2 ? (atEnd ? q[^3] : q[2]) : b;
        var t = (a - b).Normalized();
        var k = Vector3d.Zero;
        if (CircleThrough(c, b, a) is { } circ) k = (circ.C - a) / (circ.R * circ.R);
        return (a, t, k);
    }

    /// <summary>
    /// МОСТИК между концами двух кривых: G1 — кубическая кривая Эрмита (касательные концов), G2 — пятой степени (и
    /// кривизна концов). Концы — ближайшая пара; tension — длина касательных в долях расстояния.
    /// </summary>
    public static List<Vector3d> Bridge(IReadOnlyList<Vector3d> a, IReadOnlyList<Vector3d> b, bool g2, double tension = 1, int n = 64)
    {
        var qa = Dedup(a); var qb = Dedup(b);
        if (qa.Count < 2 || qb.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Bridge: the curves are empty.");
        (bool Ea, bool Eb) best = (true, false); double bd = double.MaxValue;
        foreach (var ea in new[] { true, false })
            foreach (var eb in new[] { true, false })
            {
                double d = ((ea ? qa[^1] : qa[0]) - (eb ? qb[^1] : qb[0])).Length;
                if (d < bd) { bd = d; best = (ea, eb); }
            }
        if (bd < Tol.Weld) throw new RebuildException(OperationOutcome.InvalidInput, "Bridge: the curve ends already touch.");
        var (p0, t0, k0) = EndFrame(qa, best.Ea);
        var (p1, t1o, k1) = EndFrame(qb, best.Eb);
        var t1 = -t1o;                       // в кривую b
        double m = tension * bd;
        var d0 = t0 * m; var d1 = t1 * m;
        var res = new List<Vector3d>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double s = (double)i / n;
            if (!g2)
            {
                double h00 = 2 * s * s * s - 3 * s * s + 1, h10 = s * s * s - 2 * s * s + s, h01 = -2 * s * s * s + 3 * s * s, h11 = s * s * s - s * s;
                res.Add(h00 * p0 + h10 * d0 + h01 * p1 + h11 * d1);
            }
            else
            {
                // пятой степени Эрмита: вторые производные = кривизна · |первая|²
                var a0 = k0 * (m * m); var a1 = k1 * (m * m);
                double s2 = s * s, s3 = s2 * s, s4 = s3 * s, s5 = s4 * s;
                double H0 = 1 - 10 * s3 + 15 * s4 - 6 * s5, H1 = s - 6 * s3 + 8 * s4 - 3 * s5, H2 = 0.5 * s2 - 1.5 * s3 + 1.5 * s4 - 0.5 * s5;
                double H3 = 0.5 * s3 - s4 + 0.5 * s5, H4 = -4 * s3 + 7 * s4 - 3 * s5, H5 = 10 * s3 - 15 * s4 + 6 * s5;
                res.Add(H0 * p0 + H1 * d0 + H2 * a0 + H3 * a1 + H4 * d1 + H5 * p1);
            }
        }
        return res;
    }

    /// <summary>Обрезка ломаной плоскостью: часть до первого пересечения (keepStart) или после.</summary>
    public static List<Vector3d> TrimByPlane(IReadOnlyList<Vector3d> pts, Vector3d at, Vector3d n, bool keepStart)
    {
        n = n.Normalized();
        for (int i = 1; i < pts.Count; i++)
        {
            double d0 = Vector3d.Dot(pts[i - 1] - at, n), d1 = Vector3d.Dot(pts[i] - at, n);
            if (d0 == 0 && i == 1) continue;
            if ((d0 <= 0 && d1 > 0) || (d0 >= 0 && d1 < 0))
            {
                var x = pts[i - 1] + (pts[i] - pts[i - 1]) * (d0 / (d0 - d1));
                return keepStart ? pts.Take(i).Append(x).ToList() : new[] { x }.Concat(pts.Skip(i)).ToList();
            }
        }
        throw new RebuildException(OperationOutcome.InvalidInput, "Trim: the curve does not cross the cutter.");
    }

    /// <summary>Обрезка ломаной другой кривой: точка наибольшего сближения отрезков (не дальше tol).</summary>
    public static List<Vector3d> TrimByCurve(IReadOnlyList<Vector3d> pts, IReadOnlyList<Vector3d> cutter, bool keepStart, double tol)
    {
        double best = double.MaxValue; int bi = -1; Vector3d bx = default;
        for (int i = 1; i < pts.Count; i++)
            for (int j = 1; j < cutter.Count; j++)
            {
                var (pa, pb) = SegSeg(pts[i - 1], pts[i], cutter[j - 1], cutter[j]);
                double d = (pa - pb).Length;
                if (d < best - 1e-12) { best = d; bi = i; bx = pa; }
            }
        if (bi < 0 || best > tol) throw new RebuildException(OperationOutcome.InvalidInput, $"Trim: the curves do not intersect (closest {best:G3} mm).");
        return keepStart ? pts.Take(bi).Append(bx).ToList() : new[] { bx }.Concat(pts.Skip(bi)).ToList();
    }

    /// <summary>Ближайшие точки двух отрезков.</summary>
    public static (Vector3d A, Vector3d B) SegSeg(Vector3d p1, Vector3d q1, Vector3d p2, Vector3d q2)
    {
        var d1 = q1 - p1; var d2 = q2 - p2; var r = p1 - p2;
        double a = Vector3d.Dot(d1, d1), e = Vector3d.Dot(d2, d2), f = Vector3d.Dot(d2, r);
        double s, t;
        if (a < 1e-30 && e < 1e-30) return (p1, p2);
        if (a < 1e-30) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = Vector3d.Dot(d1, r);
            if (e < 1e-30) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = Vector3d.Dot(d1, d2), den = a * e - b * b;
                s = den > 1e-30 ? Math.Clamp((b * f - c * e) / den, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (p1 + d1 * s, p2 + d2 * t);
    }

    /// <summary>
    /// ИЗОПАРАМЕТРИЧЕСКАЯ КРИВАЯ грани: линия u = const (или v = const) поверхности грани на доле t её пределов в параметрах
    /// (пределы — по границе грани); остаются точки на грани.
    /// </summary>
    public static List<Vector3d> Isoparm(TFace face, bool alongV, double t, int n = 96)
    {
        var s = GeometryAudit.AuthoritativeSurface(face) ?? throw new RebuildException(OperationOutcome.Unsupported, "Isoparm: the face has no exact surface.");
        var bnd = face.Loops.SelectMany(l => l.TessellatePolyline()).ToList();
        if (bnd.Count < 3) throw new RebuildException(OperationOutcome.InvalidInput, "Isoparm: the face has no boundary.");
        var uv = bnd.Select(s.ProjectToUV).ToList();
        double u0 = uv.Min(p => p.X), u1 = uv.Max(p => p.X), v0 = uv.Min(p => p.Y), v1 = uv.Max(p => p.Y);
        var bb = Bounds3.Empty; foreach (var bp in bnd) bb.Add(bp);
        double size = Math.Max(1, (bb.Max - bb.Min).Length);
        double tol = Math.Max(1e-4, 1e-3 * size);
        var res = new List<Vector3d>();
        for (int i = 0; i <= n; i++)
        {
            double a = (double)i / n;
            var p2 = alongV ? new Vector2d(u0 + (u1 - u0) * t, v0 + (v1 - v0) * a) : new Vector2d(u0 + (u1 - u0) * a, v0 + (v1 - v0) * t);
            var p = s.PointAt(p2);
            if (FaceRefs.Distance(face, p) <= tol) res.Add(p);
            else if (res.Count > 1) break;           // вышли с грани — первый связный кусок
            else res.Clear();
        }
        if (res.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Isoparm: the iso line does not cross the face.");
        return res;
    }
}
