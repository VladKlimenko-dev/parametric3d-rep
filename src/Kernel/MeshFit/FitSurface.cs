using OpenTK.Mathematics;

namespace Parametric3D.Kernel.MeshFit;

public enum FitKind { Plane, Cylinder, Cone, Sphere, Torus }

/// <summary>
/// ПОВЕРХНОСТЬ, НАЙДЕННАЯ НА СЕТКЕ (k119). Плоскость: точка P и нормаль A. Цилиндр: точка оси P, ось A, радиус R.
/// Конус: вершина P, ось A (от вершины внутрь конуса), полуугол R (рад). Сфера: центр P, радиус R. Тор: центр P, ось A,
/// большой радиус R, малый Rm. Расстояние — со знаком: «наружу» по каноничной нормали (от оси, от центра, от средней
/// окружности тора; у плоскости — по A).
/// </summary>
public sealed class FitSurface
{
    public FitKind Kind;
    public Vector3d P, A;
    public double R, Rm;

    public FitSurface Clone() => (FitSurface)MemberwiseClone();
    public override string ToString() => Kind switch
    {
        FitKind.Plane => $"plane n{Fmt(A)}",
        FitKind.Cylinder => $"cylinder R{R:0.#####} axis{Fmt(A)}",
        FitKind.Cone => $"cone {R * 180 / Math.PI:0.###}° axis{Fmt(A)}",
        FitKind.Sphere => $"sphere R{R:0.#####}",
        _ => $"torus R{R:0.#####} r{Rm:0.#####}",
    };
    static string Fmt(Vector3d v) => $"({v.X:0.###},{v.Y:0.###},{v.Z:0.###})";

    public bool Revolves => Kind is FitKind.Cylinder or FitKind.Cone or FitKind.Torus;

    public double Distance(Vector3d p)
    {
        switch (Kind)
        {
            case FitKind.Plane: return Vector3d.Dot(p - P, A);
            case FitKind.Sphere: return (p - P).Length - R;
            case FitKind.Cylinder:
            {
                var d = p - P; var r = d - A * Vector3d.Dot(d, A);
                return r.Length - R;
            }
            case FitKind.Cone:
            {
                var d = p - P; double z = Vector3d.Dot(d, A); double rho = (d - A * z).Length;
                return rho * Math.Cos(R) - z * Math.Sin(R);
            }
            default:
            {
                var d = p - P; double z = Vector3d.Dot(d, A); double rho = (d - A * z).Length;
                return Math.Sqrt((rho - R) * (rho - R) + z * z) - Rm;
            }
        }
    }

    /// <summary>Каноничная нормаль в ближайшей точке (единичная); NaN у оси или центра.</summary>
    public Vector3d Normal(Vector3d p)
    {
        switch (Kind)
        {
            case FitKind.Plane: return A;
            case FitKind.Sphere: return Unit(p - P);
            case FitKind.Cylinder:
            {
                var d = p - P; return Unit(d - A * Vector3d.Dot(d, A));
            }
            case FitKind.Cone:
            {
                var d = p - P; double z = Vector3d.Dot(d, A); var radial = Unit(d - A * z);
                return Unit(radial * Math.Cos(R) - A * Math.Sin(R));
            }
            default:
            {
                var d = p - P; double z = Vector3d.Dot(d, A); var radial = Unit(d - A * z);
                var c = P + radial * R;
                return Unit(p - c);
            }
        }
    }

    public Vector3d Project(Vector3d p)
    {
        for (int i = 0; i < 3; i++)
        {
            var n = Normal(p);
            if (double.IsNaN(n.X)) return p;
            p -= n * Distance(p);
        }
        return p;
    }

    static Vector3d Unit(Vector3d v) { double l = v.Length; return l > 1e-300 ? v / l : new Vector3d(double.NaN); }

    // ------------------------------------------------------------------ параметры для уточнения

    /// <summary>Параметры уточнения: сдвиги в рамке вокруг текущей формы (малые поправки).</summary>
    public int ParamCount => Kind switch { FitKind.Plane => 3, FitKind.Sphere => 4, FitKind.Cylinder => 5, FitKind.Cone => 6, _ => 7 };

    /// <summary>Поверхность с поправками q (в рамке u, w вокруг оси).</summary>
    public FitSurface Moved(double[] q)
    {
        var s = Clone();
        var (u, w) = Perp(A);
        switch (Kind)
        {
            case FitKind.Plane:
                s.A = (A + u * q[0] + w * q[1]).Normalized(); s.P = P + A * q[2]; break;
            case FitKind.Sphere:
                s.P = P + new Vector3d(q[0], q[1], q[2]); s.R = R + q[3]; break;
            case FitKind.Cylinder:
                s.A = (A + u * q[0] + w * q[1]).Normalized(); s.P = P + u * q[2] + w * q[3]; s.R = R + q[4]; break;
            case FitKind.Cone:
                s.A = (A + u * q[0] + w * q[1]).Normalized(); s.P = P + new Vector3d(q[2], q[3], q[4]); s.R = R + q[5]; break;
            default:
                s.A = (A + u * q[0] + w * q[1]).Normalized(); s.P = P + new Vector3d(q[2], q[3], q[4]); s.R = R + q[5]; s.Rm = Rm + q[6]; break;
        }
        return s;
    }

    public static (Vector3d U, Vector3d W) Perp(Vector3d a)
    {
        var t = Math.Abs(a.X) < 0.6 ? Vector3d.UnitX : Math.Abs(a.Y) < 0.6 ? Vector3d.UnitY : Vector3d.UnitZ;
        var u = Vector3d.Cross(a, t).Normalized();
        return (u, Vector3d.Cross(a, u).Normalized());
    }

    /// <summary>Каноничная форма: точка оси цилиндра — ближайшая к началу координат; ось — в «положительную» сторону.</summary>
    public void Canonical()
    {
        if (Kind == FitKind.Cylinder)
        {
            if (A.X + A.Y * 1e-3 + A.Z * 1e-6 < 0 && Math.Abs(A.X) > 1e-12 || (Math.Abs(A.X) <= 1e-12 && (A.Y < 0 || (Math.Abs(A.Y) <= 1e-12 && A.Z < 0)))) A = -A;
            P -= A * Vector3d.Dot(P, A);
        }
        if (Kind == FitKind.Plane) P = A * Vector3d.Dot(P, A);
    }
}

/// <summary>Подгонка поверхностей к точкам (k119): начальные приближения и уточнение по Левенбергу — Марквардту.</summary>
public static class Fits
{
    /// <summary>Плоскость по точкам (главные оси разброса).</summary>
    public static FitSurface? Plane(IReadOnlyList<Vector3d> pts, Vector3d hint)
    {
        if (pts.Count < 3) return null;
        var c = Mean(pts);
        var m = new double[3, 3];
        foreach (var p in pts) { var d = p - c; Add(m, d, d, 1); }
        var (vals, vecs) = Eigen3(m);
        var n = vecs[0];
        if (Vector3d.Dot(n, hint) < 0) n = -n;
        return new FitSurface { Kind = FitKind.Plane, P = c, A = n };
    }

    /// <summary>Сфера: алгебраическая подгонка (линейна по центру и r² − |c|²).</summary>
    public static FitSurface? Sphere(IReadOnlyList<Vector3d> pts)
    {
        if (pts.Count < 4) return null;
        var c0 = Mean(pts);
        var ata = new double[4, 4]; var atb = new double[4];
        foreach (var p0 in pts)
        {
            var p = p0 - c0;
            double[] row = { 2 * p.X, 2 * p.Y, 2 * p.Z, 1 };
            double rhs = p.LengthSquared;
            for (int i = 0; i < 4; i++) { atb[i] += row[i] * rhs; for (int j = 0; j < 4; j++) ata[i, j] += row[i] * row[j]; }
        }
        var x = Solve(ata, atb);
        if (x == null) return null;
        var cen = new Vector3d(x[0], x[1], x[2]);
        double r2 = x[3] + cen.LengthSquared;
        if (!(r2 > 0)) return null;
        return new FitSurface { Kind = FitKind.Sphere, P = cen + c0, R = Math.Sqrt(r2) };
    }

    /// <summary>Цилиндр с заданной осью: окружность по точкам, спроектированным поперёк оси (алгебраически).</summary>
    public static FitSurface? CylinderAbout(IReadOnlyList<Vector3d> pts, Vector3d axis)
    {
        if (pts.Count < 3) return null;
        axis = axis.Normalized();
        var (u, w) = FitSurface.Perp(axis);
        var c0 = Mean(pts);
        var ata = new double[3, 3]; var atb = new double[3];
        foreach (var p0 in pts)
        {
            var d = p0 - c0; double x = Vector3d.Dot(d, u), y = Vector3d.Dot(d, w);
            double[] row = { 2 * x, 2 * y, 1 }; double rhs = x * x + y * y;
            for (int i = 0; i < 3; i++) { atb[i] += row[i] * rhs; for (int j = 0; j < 3; j++) ata[i, j] += row[i] * row[j]; }
        }
        var s = Solve(ata, atb);
        if (s == null) return null;
        double r2 = s[2] + s[0] * s[0] + s[1] * s[1];
        if (!(r2 > 0)) return null;
        return new FitSurface { Kind = FitKind.Cylinder, P = c0 + u * s[0] + w * s[1], A = axis, R = Math.Sqrt(r2) };
    }

    /// <summary>Ось тела вращения по нормалям: прямая, которую пересекают все прямые нормалей (Плюккер, 6×6).</summary>
    public static (Vector3d Point, Vector3d Dir)? RevolutionAxis(IReadOnlyList<(Vector3d P, Vector3d N)> pn)
    {
        if (pn.Count < 6) return null;
        var c0 = Mean(pn.Select(x => x.P).ToList());
        var m = new double[6, 6];
        foreach (var (p0, n) in pn)
        {
            var p = p0 - c0;
            var pxn = Vector3d.Cross(p, n);
            double[] v = { pxn.X, pxn.Y, pxn.Z, n.X, n.Y, n.Z };
            for (int i = 0; i < 6; i++) for (int j = 0; j < 6; j++) m[i, j] += v[i] * v[j];
        }
        var (vals, vecs) = EigenSym(m);
        var e = vecs[0];
        var a = new Vector3d(e[0], e[1], e[2]); var M = new Vector3d(e[3], e[4], e[5]);
        double al = a.Length;
        if (al < 1e-9) return null;
        // ось (a, M), M = c×a: прямые пересекаются ⇔ a·(p×n) + M·n = 0; точка оси c = a×M/|a|²
        var c = Vector3d.Cross(a, M) / (al * al);
        return (c + c0, a / al);
    }

    /// <summary>
    /// КОНУС ПО НОРМАЛЯМ ГРАНЕЙ: у конуса все нормали составляют с осью один угол (n·a = −sin θ), а нормаль треугольника
    /// полосы конуса — точная нормаль на средней образующей. Поэтому направление оси — наименьший собственный вектор
    /// ЦЕНТРИРОВАННОГО разброса нормалей (у цилиндра — нецентрированного). Положение оси и угол — линейно по точкам:
    /// x² + y² = 2cₓx + 2c_y y + k²z² + 2kb z + (b² − c²). Прежде (Плюккер по прямым нормалей) ось на полосе в один ряд
    /// треугольников уходила на десятки градусов: прямые через центры треугольников проходят мимо оси.
    /// </summary>
    [ThreadStatic] public static string? Why;
    public static FitSurface? ConeFromNormals(IReadOnlyList<Vector3d> pts, IReadOnlyList<(Vector3d N, double W)> normals)
    {
        if (pts.Count < 5 || normals.Count < 3) { Why = "L229"; return null; }
        double ws = normals.Sum(x => x.W);
        var mean = Vector3d.Zero; foreach (var (n, w) in normals) mean += n * w; mean /= ws;
        var cov = new double[3, 3];
        foreach (var (n0, w) in normals) { var n = n0 - mean; Add(cov, n, n, w / ws); }
        var (vals, vecs) = Eigen3(cov);
        var a = vecs[0];
        if (vals[1] < 1e-12) { Why = "L236"; return null; }                                  // все нормали одинаковы — плоскость
        double c = Vector3d.Dot(mean, a);
        if (Math.Abs(c) < Math.Sin(0.5 * Math.PI / 180)) { Why = "L238"; return null; }       // цилиндр
        // n·a не постоянна — не конус (разброс вдоль a больше 2°)
        if (Math.Sqrt(Math.Max(vals[0], 0)) > Math.Sin(2 * Math.PI / 180)) { Why = "L240"; return null; }
        // положение оси: окружность по проекциям точек поперёк оси (смещена разными радиусами — уточнится), затем
        // прямая «радиус от высоты»; всё — уточнение по точкам
        // x² + y² = 2cₓx + 2c_y y + Q(z), Q — квадратичная (у полосы в один ряд — две высоты: линейная)
        var (uu, ww) = FitSurface.Perp(a);
        var c0 = Mean(pts);
        double[]? sol = null;
        foreach (int nq in new[] { 5, 4 })
        {
            var ata = new double[nq, nq]; var atb = new double[nq];
            foreach (var p0 in pts)
            {
                var d = p0 - c0; double x = Vector3d.Dot(d, uu), y = Vector3d.Dot(d, ww), z = Vector3d.Dot(d, a);
                double[] row = nq == 5 ? new[] { 2 * x, 2 * y, 1, z, z * z } : new[] { 2 * x, 2 * y, 1, z };
                double rhs = x * x + y * y;
                for (int i = 0; i < nq; i++) { atb[i] += row[i] * rhs; for (int j = 0; j < nq; j++) ata[i, j] += row[i] * row[j]; }
            }
            sol = Solve(ata, atb);
            if (sol != null) break;
        }
        if (sol == null) { Why = "L243"; return null; }
        var on = c0 + uu * sol[0] + ww * sol[1];
        var hr = pts.Select(p => HeightRadius(p, on, a)).ToList();
        double mz = hr.Average(x => x.Z), mr = hr.Average(x => x.R);
        double szz = 0, szr = 0;
        foreach (var (z, r) in hr) { szz += (z - mz) * (z - mz); szr += (z - mz) * (r - mr); }
        if (szz < 1e-24) { Why = "L249"; return null; }
        double k = szr / szz, b = mr - k * mz;
        if (Math.Abs(k) < 1e-6) { Why = "L251"; return null; }
        double zApex = -b / k;
        var apex = on + a * zApex;
        var dir = k > 0 ? a : -a;
        double half = Math.Atan(Math.Abs(k));
        if (half < 1e-3 || half > Math.PI / 2 - 1e-3) { Why = "L256"; return null; }
        return Refine(new FitSurface { Kind = FitKind.Cone, P = apex, A = dir, R = half }, pts);
    }

    /// <summary>Конус или тор по оси: (высота, расстояние) точек — прямая (конус) или окружность (тор).</summary>
    public static FitSurface? ConeAbout(IReadOnlyList<Vector3d> pts, Vector3d on, Vector3d axis)
    {
        var hr = pts.Select(p => HeightRadius(p, on, axis)).ToList();
        // прямая rho = k z + b по наименьшим квадратам (полная: главная ось разброса)
        double mz = hr.Average(x => x.Z), mr = hr.Average(x => x.R);
        double szz = 0, srr = 0, szr = 0;
        foreach (var (z, r) in hr) { szz += (z - mz) * (z - mz); srr += (r - mr) * (r - mr); szr += (z - mz) * (r - mr); }
        double ang = 0.5 * Math.Atan2(2 * szr, szz - srr);   // направление прямой в (z, rho)
        double dz = Math.Cos(ang), dr = Math.Sin(ang);
        if (Math.Abs(dz) < 1e-6 || Math.Abs(dr) < 1e-6) return null;   // плоскость или цилиндр
        // вершина: rho = 0
        double t = -mr / dr;
        double zApex = mz + dz * t;
        var apex = on + axis * zApex;
        // ось — от вершины в сторону точек
        var A = (mz - zApex) > 0 ? axis : -axis;
        double half = Math.Atan(Math.Abs(dr / dz));
        if (half < 1e-3 || half > Math.PI / 2 - 1e-3) return null;
        return new FitSurface { Kind = FitKind.Cone, P = apex, A = A, R = half };
    }

    public static FitSurface? TorusAbout(IReadOnlyList<Vector3d> pts, Vector3d on, Vector3d axis)
    {
        var hr = pts.Select(p => HeightRadius(p, on, axis)).ToList();
        var ata = new double[3, 3]; var atb = new double[3];
        double mz = hr.Average(x => x.Z), mr = hr.Average(x => x.R);
        foreach (var (z0, r0) in hr)
        {
            double x = r0 - mr, y = z0 - mz;
            double[] row = { 2 * x, 2 * y, 1 }; double rhs = x * x + y * y;
            for (int i = 0; i < 3; i++) { atb[i] += row[i] * rhs; for (int j = 0; j < 3; j++) ata[i, j] += row[i] * row[j]; }
        }
        var s = Solve(ata, atb);
        if (s == null) return null;
        double rr = s[2] + s[0] * s[0] + s[1] * s[1];
        if (!(rr > 0)) return null;
        double major = s[0] + mr, zc = s[1] + mz, minor = Math.Sqrt(rr);
        if (major <= minor * 0.05) return null;   // «яблоко» и шар — не тор
        return new FitSurface { Kind = FitKind.Torus, P = on + axis * zc, A = axis, R = major, Rm = minor };
    }

    public static (double Z, double R) HeightRadius(Vector3d p, Vector3d on, Vector3d axis)
    {
        var d = p - on; double z = Vector3d.Dot(d, axis);
        return (z, (d - axis * z).Length);
    }

    /// <summary>Уточнение по точкам (Гаусс — Ньютон с затуханием Марквардта, производные — малыми шагами).</summary>
    public static FitSurface Refine(FitSurface s, IReadOnlyList<Vector3d> pts0, int maxPts = 240, int steps = 30) => Refine(s, pts0, null, maxPts, steps);

    /// <summary>Уточнение с закреплёнными параметрами (fixedParams[k] — параметр k не меняется: например, наклон оси).</summary>
    public static FitSurface Refine(FitSurface s, IReadOnlyList<Vector3d> pts0, bool[]? fixedParams, int maxPts = 240, int steps = 30)
    {
        var pts = Pick(pts0, maxPts);
        int n = s.ParamCount;
        double lambda = 1e-3;
        double Rms(FitSurface f) { double a = 0; foreach (var p in pts) { double d = f.Distance(p); a += d * d; } return Math.Sqrt(a / pts.Count); }
        double cur = Rms(s);
        double scale = Math.Max(1e-6, Span(pts));
        for (int it = 0; it < steps && cur > 1e-13 * scale; it++)
        {
            var jt = new double[n, n]; var g = new double[n];
            double h = 1e-7 * scale;
            var r0 = pts.Select(p => s.Distance(p)).ToArray();
            var J = new double[pts.Count, n];
            for (int k = 0; k < n; k++)
            {
                var q = new double[n]; q[k] = (k < 2 && s.Kind != FitKind.Sphere) || (s.Kind == FitKind.Cone && k == 5) ? 1e-7 : h;
                var sp = s.Moved(q);
                var qm = new double[n]; qm[k] = -q[k];
                var sm = s.Moved(qm);
                for (int i = 0; i < pts.Count; i++) J[i, k] = fixedParams != null && fixedParams[k] ? 0 : (sp.Distance(pts[i]) - sm.Distance(pts[i])) / (2 * q[k]);
            }
            for (int i = 0; i < pts.Count; i++)
                for (int a = 0; a < n; a++)
                {
                    g[a] += J[i, a] * r0[i];
                    for (int b = 0; b < n; b++) jt[a, b] += J[i, a] * J[i, b];
                }
            bool improved = false;
            for (int tries = 0; tries < 8; tries++)
            {
                var m = (double[,])jt.Clone();
                for (int a = 0; a < n; a++) m[a, a] += fixedParams != null && fixedParams[a] ? 1 : lambda * Math.Max(jt[a, a], 1e-30);
                var step = Solve(m, g.Select(x => -x).ToArray());
                if (step == null) { lambda *= 10; continue; }
                var cand = s.Moved(step);
                if (cand.Kind == FitKind.Cone && (cand.R <= 1e-4 || cand.R >= Math.PI / 2 - 1e-4)) { lambda *= 10; continue; }
                if (cand.R < 0 || cand.Kind == FitKind.Torus && cand.Rm <= 0) { lambda *= 10; continue; }
                double nr = Rms(cand);
                if (nr < cur) { s = cand; cur = nr; lambda = Math.Max(lambda * 0.3, 1e-9); improved = true; break; }
                lambda *= 10;
            }
            if (!improved) break;
        }
        return s;
    }

    public static IReadOnlyList<Vector3d> Pick(IReadOnlyList<Vector3d> pts, int max)
    {
        if (pts.Count <= max) return pts;
        var r = new List<Vector3d>(max);
        double step = pts.Count / (double)max;
        for (int i = 0; i < max; i++) r.Add(pts[(int)(i * step)]);
        return r;
    }

    public static double Span(IReadOnlyList<Vector3d> pts)
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
        foreach (var p in pts) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }
        return pts.Count == 0 ? 0 : (hi - lo).Length;
    }

    public static Vector3d Mean(IReadOnlyList<Vector3d> pts)
    {
        var s = Vector3d.Zero; foreach (var p in pts) s += p; return pts.Count > 0 ? s / pts.Count : s;
    }

    static void Add(double[,] m, Vector3d a, Vector3d b, double w)
    {
        m[0, 0] += a.X * b.X * w; m[0, 1] += a.X * b.Y * w; m[0, 2] += a.X * b.Z * w;
        m[1, 0] += a.Y * b.X * w; m[1, 1] += a.Y * b.Y * w; m[1, 2] += a.Y * b.Z * w;
        m[2, 0] += a.Z * b.X * w; m[2, 1] += a.Z * b.Y * w; m[2, 2] += a.Z * b.Z * w;
    }

    /// <summary>Собственные векторы симметричной 3×3 (по возрастанию собственных значений).</summary>
    public static (double[] Vals, Vector3d[] Vecs) Eigen3(double[,] m)
    {
        var (vals, vecs) = EigenSym(m);
        return (vals, vecs.Select(v => new Vector3d(v[0], v[1], v[2])).ToArray());
    }

    /// <summary>Якоби для симметричной матрицы n×n: собственные значения по возрастанию и векторы.</summary>
    public static (double[] Vals, double[][] Vecs) EigenSym(double[,] a0)
    {
        int n = a0.GetLength(0);
        var a = (double[,])a0.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j];
            if (off < 1e-30) break;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var idx = Enumerable.Range(0, n).OrderBy(i => a[i, i]).ToArray();
        return (idx.Select(i => a[i, i]).ToArray(), idx.Select(i => Enumerable.Range(0, n).Select(k => v[k, i]).ToArray()).ToArray());
    }

    /// <summary>Решение системы методом Гаусса с выбором ведущего; null — вырождена.</summary>
    public static double[]? Solve(double[,] a0, double[] b0)
    {
        int n = b0.Length;
        var a = (double[,])a0.Clone(); var b = (double[])b0.Clone();
        double scale = 0; for (int i = 0; i < n; i++) scale = Math.Max(scale, Math.Abs(a[i, i]));
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) <= 1e-14 * Math.Max(scale, 1e-300)) return null;
            if (piv != c) { for (int k = 0; k < n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]); (b[c], b[piv]) = (b[piv], b[c]); }
            for (int r = c + 1; r < n; r++)
            {
                double f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) a[r, k] -= f * a[c, k];
                b[r] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k];
            x[r] = s / a[r, r];
        }
        return x;
    }
}
