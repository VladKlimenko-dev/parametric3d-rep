using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Geometry;

/// <summary>
/// G4. ТОЧНОЕ ПЕРЕСЕЧЕНИЕ ПОВЕРХНОСТЕЙ (плоскость, цилиндр, шар).
///
/// Основа точной булевой: ребро нового тела — кривая пересечения двух поверхностей, а не
/// цепочка точек многогранника. Здесь — замкнутые формулы:
///   плоскость × плоскость → прямая;  плоскость × шар → окружность;  шар × шар → окружность;
///   плоскость × цилиндр → окружность (перпендикулярно оси), эллипс (наклонно), 0–2 прямые
///   (параллельно оси);  цилиндр × цилиндр с параллельными осями → 0–2 прямые;
///   цилиндр × шар с центром на оси → 0–2 окружности.
/// Общий случай цилиндр × цилиндр (поперечное отверстие) — прослеживание: затравка по
/// параметру одного цилиндра из квадратного уравнения для другого, затем IntersectionCurve3
/// уточняет точки на обеих поверхностях.
/// Неограниченные результаты (прямые) обрезаются по заданному шару «области интереса».
/// </summary>
public static class Ssi
{
    public abstract record Shape;
    public sealed record Plane(Vector3d P, Vector3d N) : Shape;
    public sealed record Cylinder(Vector3d P, Vector3d Axis, double R) : Shape;
    public sealed record Sphere(Vector3d C, double R) : Shape;
    /// <summary>Конус: вершина, ось (в сторону раскрытия), половина угла раствора.</summary>
    public sealed record Cone(Vector3d Apex, Vector3d Axis, double HalfAngle) : Shape;
    /// <summary>Тор: центр, ось, радиус окружности центров R и радиус трубки r.</summary>
    public sealed record Torus(Vector3d C, Vector3d Axis, double R, double r) : Shape;
    /// <summary>
    /// KRN-06 (06.14): поверхность общего вида (NURBS) как «неявная форма» для построителя сопряжений: знаковое расстояние —
    /// по основанию перпендикуляра (проекция Ньютоном с подсказкой своего потока, продолжение за край области — многочлены
    /// крайних промежутков), градиент — нормаль в основании. Верно в трубке, где проекция единственна (радиус меньше радиуса
    /// кривизны). Оценок Липшица нет (NormalLip — null): строгие сертификаты событий на таких формах не выдаются.
    /// </summary>
    public sealed record ParamSurf(Surfaces.ISurface3 S) : Shape;

    [ThreadStatic] static Dictionary<Surfaces.ISurface3, Vector2d>? _footHints;
    [ThreadStatic] public static Action<string>? FootDebug;
    /// <summary>Разбор: вызовы основания на поверхности общего вида и из них — с общей проекцией (без годной подсказки).</summary>
    public static long ParamFootCalls, ParamFootGlobal;

    static void ParamDerivs(Surfaces.ISurface3 s, Vector2d uv, out Vector3d S, out Vector3d Su, out Vector3d Sv)
    {
        if (s is Surfaces.NurbsSurface ns) { ns.DerivsExtended(uv, out S, out Su, out Sv); return; }
        const double h = 1e-7;
        S = s.PointAt(uv);
        Su = (s.PointAt(new Vector2d(uv.X + h, uv.Y)) - s.PointAt(new Vector2d(uv.X - h, uv.Y))) / (2 * h);
        Sv = (s.PointAt(new Vector2d(uv.X, uv.Y + h)) - s.PointAt(new Vector2d(uv.X, uv.Y - h))) / (2 * h);
    }

    static bool FootNewton(Surfaces.ISurface3 s, Vector3d x, ref Vector2d uv)
    {
        for (int it = 0; it < 40; it++)
        {
            ParamDerivs(s, uv, out var S, out var Su, out var Sv);
            var r = S - x;
            double a11 = Vector3d.Dot(Su, Su), a12 = Vector3d.Dot(Su, Sv), a22 = Vector3d.Dot(Sv, Sv);
            double b1 = -Vector3d.Dot(Su, r), b2 = -Vector3d.Dot(Sv, r);
            double det = a11 * a22 - a12 * a12; if (!(Math.Abs(det) > 1e-300)) return false;
            double du = (b1 * a22 - b2 * a12) / det, dv = (a11 * b2 - a12 * b1) / det;
            uv += new Vector2d(du, dv);
            if (!double.IsFinite(uv.X + uv.Y)) return false;
            if (Math.Abs(du) + Math.Abs(dv) < 1e-15) break;
        }
        ParamDerivs(s, uv, out var S1, out var Su1, out var Sv1);
        var w = x - S1; double L = w.Length;
        // ортогональность — относительно max(L, 1e-5 мм): у точки на самой поверхности остаток w — шум округления, и его угол
        // с касательной ничего не значит (касательный сдвиг основания там ~1e-14 мм)
        double Lr = Math.Max(L, 1e-3);
        return Math.Abs(Vector3d.Dot(w, Su1)) <= 1e-9 * Lr * Su1.Length && Math.Abs(Vector3d.Dot(w, Sv1)) <= 1e-9 * Lr * Sv1.Length;
    }

    /// <summary>Основание перпендикуляра и единичная нормаль поверхности общего вида (нормаль — в знаке s.NormalAt).</summary>
    public static (Vector3d P, Vector3d N) ParamFoot(Surfaces.ISurface3 s, Vector3d x)
    {
        _footHints ??= new Dictionary<Surfaces.ISurface3, Vector2d>(ReferenceEqualityComparer.Instance);
        Interlocked.Increment(ref ParamFootCalls);
        // подсказка — только рядом: из далёкой (прежний вызов в другом месте) Ньютон сходится и к дальней стационарной точке
        // (у продолжения рационального куска — противоположная сторона окружности), где условие ортогональности тоже верно
        Vector2d uv = default;
        bool ok = false;
        if (_footHints.TryGetValue(s, out var hint))
        {
            // ЗАЩИТА ОТ ДАЛЬНЕЙ СТАЦИОНАРНОЙ ТОЧКИ: найденное основание не дальше от x, чем точка подсказки (дальняя стационарная
            // точка — максимум расстояния — всегда дальше), и сдвиг по параметрам умеренный
            uv = hint; ok = FootNewton(s, x, ref uv) && (uv - hint).Length < 0.5;
            if (ok) { ParamDerivs(s, uv, out var Pf, out _, out _); ParamDerivs(s, hint, out var Ph, out _, out _); ok = (x - Pf).Length <= (x - Ph).Length * (1 + 1e-12) + 1e-12; }
        }
        if (!ok)
        {
            Interlocked.Increment(ref ParamFootGlobal);
            uv = s.ProjectToUV(x); var g0 = uv;
            ok = FootNewton(s, x, ref uv) && (uv - g0).Length < 0.25;
        }
        if (!ok)
        {
            if (FootDebug != null)
            {
                var g = s.ProjectToUV(x); var u2 = g; bool nok = FootNewton(s, x, ref u2);
                ParamDerivs(s, u2, out var S2, out var Su2, out var Sv2); var w2 = x - S2;
                FootDebug($"ParamFoot failed at {x:0.######}: global uv {g:0.######} newton {nok} uv {u2:0.######} L {w2.Length:E3} ortho {Vector3d.Dot(w2, Su2) / Su2.Length:E2} {Vector3d.Dot(w2, Sv2) / Sv2.Length:E2}");
            }
            return (new Vector3d(double.NaN), new Vector3d(double.NaN));
        }
        _footHints[s] = uv;
        ParamDerivs(s, uv, out var P, out var Su, out var Sv);
        var n = Vector3d.Cross(Su, Sv);
        if (n.LengthSquared < 1e-30) return (P, new Vector3d(double.NaN));
        n.Normalize();
        var nRef = s.NormalAt(new Vector2d(Math.Clamp(uv.X, 0, 1), Math.Clamp(uv.Y, 0, 1)));
        if (Vector3d.Dot(n, nRef) < 0) n = -n;
        return (P, n);
    }

    public enum Kind { Curves, Empty, Coincident, Tangent }

    public sealed class Result
    {
        public Kind Kind;
        public readonly List<ICurve3> Curves = new();
        public readonly List<Vector3d> Points = new();     // касание в точке
        /// <summary>m.b3: все кривые — касание (кратный корень профилей соосных тел: окружность касания), а не пересечение.</summary>
        public bool AllTangent;
        /// <summary>g.05: прослеживание не удостоверено (непояснённая ячейка исключения по Липшицу) — причина.</summary>
        public string? Uncertified;
        public override string ToString() => $"{Kind}: {Curves.Count} curve(s) [{string.Join(", ", Curves.Select(c => c.GetType().Name))}], {Points.Count} point(s)";
    }

    const double Eps = 1e-12;

    /// <summary>Пересечение; box — центр и радиус области, по которой обрезаются прямые.</summary>
    public static Result Intersect(Shape a, Shape b, Vector3d boxCentre, double boxRadius)
    {
        // Соосные тела вращения (плоскость поперёк оси, цилиндр, шар с центром на оси, конус, тор):
        // пересечение — окружности по точкам пересечения их профилей в полуплоскости (ρ, z).
        if (Coaxial(a, b) is { } co) return co;
        switch (a, b)
        {
            case (Plane p, Cone k): return PlaneCone(p, k, boxCentre, boxRadius);
            case (Cone k, Plane p): return PlaneCone(p, k, boxCentre, boxRadius);
            case (Plane p, Torus t): return PlaneTorus(p, t);
            case (Torus t, Plane p): return PlaneTorus(p, t);
            case (Plane p, Plane q): return PlanePlane(p, q, boxCentre, boxRadius);
            case (Plane p, Sphere s): return PlaneSphere(p, s);
            case (Sphere s, Plane p): return PlaneSphere(p, s);
            case (Sphere s, Sphere t): return SphereSphere(s, t);
            case (Plane p, Cylinder c): return PlaneCylinder(p, c, boxCentre, boxRadius);
            case (Cylinder c, Plane p): return PlaneCylinder(p, c, boxCentre, boxRadius);
            case (Cylinder c, Cylinder d): return CylinderCylinder(c, d, boxCentre, boxRadius);
            case (Cylinder c, Sphere s): return CylinderSphere(c, s);
            case (Sphere s, Cylinder c): return CylinderSphere(c, s);
            case (Cylinder c, Cone k) when !CylIntervalsOff && !CylConeOff && GeneratorsAgainst(c, k) is { } gk: return gk;
            case (Cone k, Cylinder c) when !CylIntervalsOff && !CylConeOff && GeneratorsAgainst(c, k) is { } gk2: return gk2;
            case (Cone k, Sphere sp) when !ConeGenOff && ConeGeneratorsAgainst(k, sp) is { } g3: return g3;
            case (Sphere sp, Cone k) when !ConeGenOff && ConeGeneratorsAgainst(k, sp) is { } g4: return g4;
            case (Cone k, Cone k2) when !ConeGenOff && ConeGeneratorsAgainst(k, k2) is { } g5: return g5;
        }
        return Trace(a, b, boxCentre, boxRadius);
    }

    static Result PlanePlane(Plane p, Plane q, Vector3d bc, double br)
    {
        var n1 = p.N.Normalized(); var n2 = q.N.Normalized();
        var dir = Vector3d.Cross(n1, n2);
        double d1 = Vector3d.Dot(n1, p.P), d2 = Vector3d.Dot(n2, q.P);
        if (dir.Length < 1e-12)
            return new Result { Kind = Math.Abs(Vector3d.Dot(n1, q.P) - d1) < 1e-9 ? Kind.Coincident : Kind.Empty };
        // Точка прямой: решение n1·x = d1, n2·x = d2 ближайшее к началу координат.
        var x0 = (Vector3d.Cross(n2, dir) * d1 + Vector3d.Cross(dir, n1) * d2) / dir.LengthSquared;
        dir = dir.Normalized();
        return Line(x0, dir, bc, br);
    }

    static Result Line(Vector3d x0, Vector3d dir, Vector3d bc, double br)
    {
        // Обрезка по шару области: |x0 + t·dir − bc| ≤ br.
        double tc = Vector3d.Dot(bc - x0, dir);
        double d2 = (x0 + dir * tc - bc).LengthSquared;
        var r = new Result();
        if (d2 > br * br) { r.Kind = Kind.Empty; return r; }
        double h = Math.Sqrt(br * br - d2);
        r.Kind = Kind.Curves; r.Curves.Add(new LineCurve3(x0 + dir * (tc - h), x0 + dir * (tc + h)));
        return r;
    }

    static Result PlaneSphere(Plane p, Sphere s)
    {
        var n = p.N.Normalized(); double d = Vector3d.Dot(n, s.C - p.P);
        var r = new Result();
        if (Math.Abs(d) > s.R + 1e-12) { r.Kind = Kind.Empty; return r; }
        var c = s.C - n * d;
        double rho2 = s.R * s.R - d * d;
        if (rho2 < 1e-18) { r.Kind = Kind.Tangent; r.Points.Add(c); return r; }
        r.Kind = Kind.Curves; r.Curves.Add(new ArcCurve3(Frame3.FromNormal(c, n), Math.Sqrt(rho2), 0, 2 * Math.PI));
        return r;
    }

    static Result SphereSphere(Sphere s, Sphere t)
    {
        var d = t.C - s.C; double L = d.Length;
        var r = new Result();
        if (L < 1e-12) { r.Kind = Math.Abs(s.R - t.R) < 1e-12 ? Kind.Coincident : Kind.Empty; return r; }
        if (L > s.R + t.R + 1e-12 || L < Math.Abs(s.R - t.R) - 1e-12) { r.Kind = Kind.Empty; return r; }
        var n = d / L;
        double x = (L * L + s.R * s.R - t.R * t.R) / (2 * L);
        double rho2 = s.R * s.R - x * x;
        var c = s.C + n * x;
        if (rho2 < 1e-18) { r.Kind = Kind.Tangent; r.Points.Add(c); return r; }
        r.Kind = Kind.Curves; r.Curves.Add(new ArcCurve3(Frame3.FromNormal(c, n), Math.Sqrt(rho2), 0, 2 * Math.PI));
        return r;
    }

    static readonly bool PcTangentOff = Environment.GetEnvironmentVariable("P3D_PCTANGENT") == "0";
    static Result PlaneCylinder(Plane p, Cylinder c, Vector3d bc, double br)
    {
        var n = p.N.Normalized(); var w = c.Axis.Normalized();
        double cosT = Vector3d.Dot(n, w);
        var r = new Result();
        if (Math.Abs(cosT) < 1e-12)
        {
            // Плоскость параллельна оси: расстояние от оси до плоскости.
            double d = Vector3d.Dot(n, c.P - p.P);
            if (Math.Abs(d) > c.R + 1e-12) { r.Kind = Kind.Empty; return r; }
            var foot = c.P - n * d;
            var side = Vector3d.Cross(w, n).Normalized();
            double h2 = c.R * c.R - d * d;
            // касание: по зазору между осью и плоскостью (R − |d| до 1e-12 отн.), а не по h² — у плоскости и ленты, построенных в
            // разных телах (пересечение тел «одно тело — разные скругления»), зазор 1e-15 давал h ~ 6e-8: две прямые и полосу уже
            // допуска слияния. Мутация P3D_PCTANGENT=0.
            if (h2 < 1e-18 || (!PcTangentOff && Math.Abs(c.R - Math.Abs(d)) <= 1e-12 * Math.Max(1, c.R))) { var l1 = Line(foot, w, bc, br); l1.Kind = l1.Curves.Count > 0 ? Kind.Tangent : Kind.Empty; return l1; }
            double h = Math.Sqrt(h2);
            foreach (double sgn in new[] { -1.0, 1.0 })
            {
                var l = Line(foot + side * (sgn * h), w, bc, br);
                r.Curves.AddRange(l.Curves);
            }
            r.Kind = r.Curves.Count > 0 ? Kind.Curves : Kind.Empty;
            return r;
        }
        // Центр — пересечение оси с плоскостью.
        double t = Vector3d.Dot(n, p.P - c.P) / cosT;
        var centre = c.P + w * t;
        if (Math.Abs(Math.Abs(cosT) - 1) < 1e-12)
        {
            r.Kind = Kind.Curves; r.Curves.Add(new ArcCurve3(Frame3.FromNormal(centre, n), c.R, 0, 2 * Math.PI));
            return r;
        }
        // Эллипс: малая полуось R поперёк проекции оси, большая — R/|cos θ| вдоль неё.
        var major = (w - n * cosT).Normalized();
        var minor = Vector3d.Cross(n, major).Normalized();
        r.Kind = Kind.Curves;
        r.Curves.Add(new EllipseCurve3(new Frame3(centre, major, minor), c.R / Math.Abs(cosT), c.R, 0, 2 * Math.PI));
        return r;
    }

    static Result CylinderSphere(Cylinder c, Sphere s)
    {
        var w = c.Axis.Normalized();
        var off = s.C - c.P; var radial = off - w * Vector3d.Dot(off, w);
        if (radial.Length > 1e-12) return !CylIntervalsOff && GeneratorsAgainst(c, s) is { } ga ? ga : Trace(c, s, s.C, s.R * 1.01 + 1e-6);
        var r = new Result();
        double h2 = s.R * s.R - c.R * c.R;
        if (h2 < -1e-12) { r.Kind = Kind.Empty; return r; }
        var cc = c.P + w * Vector3d.Dot(off, w);
        if (h2 < 1e-18) { r.Kind = Kind.Tangent; r.Curves.Add(new ArcCurve3(Frame3.FromNormal(cc, w), c.R, 0, 2 * Math.PI)); return r; }
        double h = Math.Sqrt(h2);
        r.Kind = Kind.Curves;
        foreach (double sgn in new[] { -1.0, 1.0 }) r.Curves.Add(new ArcCurve3(Frame3.FromNormal(cc + w * (sgn * h), w), c.R, 0, 2 * Math.PI));
        return r;
    }

    static Result CylinderCylinder(Cylinder c, Cylinder d, Vector3d bc, double br)
    {
        var wc = c.Axis.Normalized(); var wd = d.Axis.Normalized();
        var r = new Result();
        if (Vector3d.Cross(wc, wd).Length < 1e-12)
        {
            // Параллельные оси: окружности сечения пересекаются в 0–2 точках → прямые.
            var off = d.P - c.P; var dv = off - wc * Vector3d.Dot(off, wc);
            double L = dv.Length;
            if (L < 1e-12) { r.Kind = Math.Abs(c.R - d.R) < 1e-12 ? Kind.Coincident : Kind.Empty; return r; }
            if (L > c.R + d.R + 1e-12 || L < Math.Abs(c.R - d.R) - 1e-12) { r.Kind = Kind.Empty; return r; }
            var n = dv / L; double x = (L * L + c.R * c.R - d.R * d.R) / (2 * L);
            double y2 = c.R * c.R - x * x; var side = Vector3d.Cross(wc, n);
            var basePt = c.P + n * x;
            if (y2 < 1e-18) { var l = Line(basePt, wc, bc, br); l.Kind = Kind.Tangent; return l; }
            double y = Math.Sqrt(y2);
            foreach (double sgn in new[] { -1.0, 1.0 }) r.Curves.AddRange(Line(basePt + side * (sgn * y), wc, bc, br).Curves);
            r.Kind = r.Curves.Count > 0 ? Kind.Curves : Kind.Empty;
            return r;
        }
        if (EqualCylOn && !EqualCylOffForTest && EqualRadiiMeeting(c, d, wc, wd) is { } eq) return eq;
        // Общий случай: точка цилиндра D по углу φ: q(φ) = D.P + R_d(cos φ·u + sin φ·v) + s·wd.
        // На цилиндре C: |(q − C.P) − wc((q − C.P)·wc)|² = R_c² — квадратное уравнение по s.
        var fd = Frame3.FromNormal(d.P, wd);
        if (!CylIntervalsOff) return CylinderCylinderIntervals(c, d, wc, wd, fd);
        int n0 = 256;
        var branches = new List<List<Vector3d>> { new(), new() };
        bool anyMissing = false;
        // Обход начинается сразу после первого угла без решения: тогда допустимые углы идут
        // одним непрерывным куском (петля частичного касания не рвётся на шве угла).
        double Disc(double phi)
        {
            var q0 = d.P + (fd.U * Math.Cos(phi) + fd.V * Math.Sin(phi)) * d.R - c.P;
            var a0 = q0 - wc * Vector3d.Dot(q0, wc); var a1 = wd - wc * Vector3d.Dot(wd, wc);
            double A = a1.LengthSquared, B = 2 * Vector3d.Dot(a0, a1), C = a0.LengthSquared - c.R * c.R;
            return B * B - 4 * A * C;
        }
        int startIdx = 0;
        for (int i = 0; i < n0; i++) if (Disc(2 * Math.PI * i / n0) < 0) { startIdx = i; break; }
        for (int j = 0; j <= n0; j++)
        {
            int i = startIdx + j;
            double phi = 2 * Math.PI * i / n0;
            var q0 = d.P + (fd.U * Math.Cos(phi) + fd.V * Math.Sin(phi)) * d.R - c.P;
            var a0 = q0 - wc * Vector3d.Dot(q0, wc); var a1 = wd - wc * Vector3d.Dot(wd, wc);
            double A = a1.LengthSquared, B = 2 * Vector3d.Dot(a0, a1), C = a0.LengthSquared - c.R * c.R;
            double disc = B * B - 4 * A * C;
            if (disc < 0) { anyMissing = true; continue; }
            double sq = Math.Sqrt(disc);
            branches[0].Add(c.P + q0 + wd * ((-B - sq) / (2 * A)));
            branches[1].Add(c.P + q0 + wd * ((-B + sq) / (2 * A)));
        }
        if (branches[0].Count == 0) { r.Kind = Kind.Empty; return r; }
        var sc = new CylinderSurface(Frame3.FromNormal(c.P, wc), c.R);
        var sd = new CylinderSurface(Frame3.FromNormal(d.P, wd), d.R);
        if (!anyMissing)
        {
            // D проходит сквозь C целиком: две замкнутые кривые (вход и выход).
            foreach (var br0 in branches) r.Curves.Add(new IntersectionCurve3(sc, sd, br0));
        }
        else
        {
            // D задевает C частично: одна замкнутая петля — ветви соединены в точках касания.
            var loop = new List<Vector3d>(branches[0]);
            var back = new List<Vector3d>(branches[1]); back.Reverse();
            loop.AddRange(back);
            loop.Add(loop[0]);
            r.Curves.Add(new IntersectionCurve3(sc, sd, loop));
        }
        r.Kind = Kind.Curves;
        return r;
    }

    static readonly bool CylIntervalsOff = Environment.GetEnvironmentVariable("P3D_CYLINTERVALS") == "0";
    static readonly bool EqualCylOn = Environment.GetEnvironmentVariable("P3D_EQUALCYLSSI") != "0";
    public static long EqualCylSplits;
    /// <summary>Проверка отождествления кусков при сборке (KRN05-SHARED-BY-CONSTRUCTION): стык «на ус» — общим способом.</summary>
    public static volatile bool EqualCylOffForTest;

    /// <summary>
    /// KRN-06 (06.12): ЦИЛИНДРЫ РАВНОГО РАДИУСА С ПЕРЕСЕКАЮЩИМИСЯ ОСЯМИ (тройник, крестовина труб одного диаметра). Пересечение
    /// распадается на два плоских эллипса в биссекторных плоскостях осей, эллипсы пересекаются в «сёдлах» O ± R·n
    /// (n = wc × wd / |…|), где поверхности касаются. Дискриминант общего способа там имеет двойные корни — их положение
    /// найдено лишь до ~1e-7, и концы ветвей с разных граней не сходились в один узел (разбиение граней отказывало). Здесь —
    /// точно: X(θ) = O + R cos θ·n + R/|m⊥wc| · sin θ·m, m = (wc ± wd)/|…|; четыре половины эллипсов от седла к седлу.
    /// Мутация P3D_EQUALCYLSSI=0.
    /// </summary>
    static Result? EqualRadiiMeeting(Cylinder c, Cylinder d, Vector3d wc, Vector3d wd)
    {
        double R = c.R;
        if (Math.Abs(c.R - d.R) > 1e-12 * Math.Max(1, R)) return null;
        var nn = Vector3d.Cross(wc, wd); var n = nn.Normalized();
        var off = d.P - c.P;
        if (Math.Abs(Vector3d.Dot(off, n)) > 1e-12 * Math.Max(1, R)) return null;
        // точка встречи осей: c.P + wc·s = d.P + wd·t
        double cw = Vector3d.Dot(wc, wd), den = 1 - cw * cw;
        double s0 = (Vector3d.Dot(off, wc) - cw * Vector3d.Dot(off, wd)) / den;
        var O = c.P + wc * s0;
        var r = new Result();
        var sc = new CylinderSurface(Frame3.FromNormal(c.P, wc), c.R);
        var sd = new CylinderSurface(Frame3.FromNormal(d.P, wd), d.R);
        foreach (var mm in new[] { wc + wd, wc - wd })
        {
            var m = mm.Normalized(); var mp = m - wc * Vector3d.Dot(m, wc); double lam = R / mp.Length;
            for (int half = 0; half < 2; half++)
            {
                var pts = new List<Vector3d>();
                const int N = 256;
                for (int k = 0; k <= N; k++)
                {
                    double th = Math.PI * (half + k / (double)N);
                    double cs = k == 0 ? (half == 0 ? 1 : -1) : k == N ? (half == 0 ? -1 : 1) : Math.Cos(th), sn = k == 0 || k == N ? 0 : Math.Sin(th);
                    pts.Add(O + n * (R * cs) + m * (lam * sn));
                }
                r.Curves.Add(new IntersectionCurve3(sc, sd, pts));
            }
        }
        Interlocked.Increment(ref EqualCylSplits);
        r.Kind = Kind.Curves; return r;
    }

    /// <summary>
    /// KRN-04 (04.1, g.05): ВСЕ ВЕТВИ ПЕРЕСЕЧЕНИЯ ДВУХ ЦИЛИНДРОВ. Образующая D под углом φ пересекает C, когда дискриминант
    /// Disc(φ) ≥ 0; Disc — тригонометрический многочлен степени 2, его корни находятся ТОЧНО (подстановка половинного угла,
    /// SsiEvents.PolyRootsOn) — не выборкой 256 углов. Допустимые углы — 0, 1 или 2 интервала (малый цилиндр насквозь через
    /// большой, если параметр — по большому: два интервала — две петли), либо вся окружность (две замкнутые кривые). Каждый
    /// интервал — одна петля: ветвь «−» туда, ветвь «+» обратно (в концах интервала ветви сходятся). Прежде предполагался
    /// один интервал: при двух петля склеивалась через разрыв, и общий маршрут отказывал — исход зависел от порядка операндов.
    /// Мутация P3D_CYLINTERVALS=0.
    /// </summary>
    static Result CylinderCylinderIntervals(Cylinder c, Cylinder d, Vector3d wc, Vector3d wd, Frame3 fd)
    {
        var r = new Result();
        (double A, double B, double C) Q(double phi)
        {
            var q0 = d.P + (fd.U * Math.Cos(phi) + fd.V * Math.Sin(phi)) * d.R - c.P;
            var a0 = q0 - wc * Vector3d.Dot(q0, wc); var a1 = wd - wc * Vector3d.Dot(wd, wc);
            return (a1.LengthSquared, 2 * Vector3d.Dot(a0, a1), a0.LengthSquared - c.R * c.R);
        }
        double Disc(double phi) { var (A, B, C) = Q(phi); return B * B - 4 * A * C; }
        // корни Disc на [0, 2π): четыре куска по π/2, подстановка половинного угла (степень 4 по u)
        var roots = new List<double>();
        for (int k = 0; k < 4; k++)
        {
            double lo = Math.PI / 2 * k, hi = lo + Math.PI / 2, ac = (lo + hi) / 2, th = Math.Tan((hi - lo) / 4);
            double G(double u) { double tau = u * th; return Disc(ac + 2 * Math.Atan(tau)) * Math.Pow(1 + tau * tau, 2); }
            foreach (var u in Analytic.SsiEvents.PolyRootsOn(G, 4)) roots.Add(ac + 2 * Math.Atan(u * th));
        }
        roots.Sort();
        var rs = new List<double>();
        foreach (var x in roots) if (rs.Count == 0 || x - rs[^1] > 1e-12) rs.Add(x);
        if (rs.Count > 1 && rs[0] + 2 * Math.PI - rs[^1] <= 1e-12) rs.RemoveAt(rs.Count - 1);
        var sc = new CylinderSurface(Frame3.FromNormal(c.P, wc), c.R);
        var sd = new CylinderSurface(Frame3.FromNormal(d.P, wd), d.R);
        Vector3d Pt(double phi, int sgn)
        {
            var q0 = d.P + (fd.U * Math.Cos(phi) + fd.V * Math.Sin(phi)) * d.R - c.P;
            var (A, B, C) = Q(phi); double disc = sgn == 0 ? 0 : Math.Max(0, B * B - 4 * A * C);
            return c.P + q0 + wd * ((-B + sgn * Math.Sqrt(disc)) / (2 * A));
        }
        const int n = 256;
        if (rs.Count == 0)
        {
            if (Disc(0) < 0) { r.Kind = Kind.Empty; return r; }
            foreach (int sgn in new[] { -1, 1 })
            {
                var pts = new List<Vector3d>();
                for (int i = 0; i <= n; i++) pts.Add(Pt(2 * Math.PI * i / n, sgn));
                r.Curves.Add(new IntersectionCurve3(sc, sd, pts));
            }
            r.Kind = Kind.Curves; return r;
        }
        // интервалы между соседними корнями, где Disc > 0
        for (int i = 0; i < rs.Count; i++)
        {
            double a = rs[i], b = i + 1 < rs.Count ? rs[i + 1] : rs[0] + 2 * Math.PI;
            if (b - a < 1e-12 || Disc((a + b) / 2) <= 0) continue;
            // концы интервала — корни дискриминанта: обе ветви в одной точке (дискриминант ровно 0), петля замкнута точно
            var loop = new List<Vector3d>();
            for (int k = 0; k <= n; k++) { double t = a + (b - a) * k / (double)n; loop.Add(Pt(t, k == 0 || k == n ? 0 : -1)); }
            for (int k = n - 1; k >= 1; k--) { double t = a + (b - a) * k / (double)n; loop.Add(Pt(t, 1)); }
            loop.Add(loop[0]);
            RefineEnds(loop, n, c, d);
            r.Curves.Add(new IntersectionCurve3(sc, sd, loop));
        }
        if (r.Curves.Count == 0)
        {
            // только касания (двойные корни)
            r.Kind = Kind.Tangent; foreach (var x in rs) r.Points.Add(Pt(x, 1)); return r;
        }
        r.Kind = Kind.Curves; return r;
    }

    /// <summary>
    /// KRN-04 (04.1): ЦИЛИНДР × КВАДРИКА (шар, конус) — все ветви по образующим цилиндра: образующая под углом φ пересекает
    /// квадрику в корнях A s² + B s + C = 0 (A, B, C — из алгебраического уравнения квадрики, SsiEvents.Q, по трём точкам
    /// образующей); дискриминант — тригонометрический многочлен степени ≤ 2 по φ, его корни — точно (половинный угол), интервалы
    /// допустимых φ — петли (ветвь «−» туда, «+» обратно), вся окружность — две замкнутые кривые. Прежде — общее прослеживание
    /// по затравкам на сетке: малая петля (цилиндр задевает шар на 0.004 мм) пропускалась. null — случай не для этого способа
    /// (образующая почти параллельна образующей конуса; точки не на той полости конуса): тогда прослеживание.
    /// </summary>
    static Result? GeneratorsAgainst(Cylinder d, Shape target)
    {
        var wd = d.Axis.Normalized(); var fd = Frame3.FromNormal(d.P, wd);
        Vector3d Q0(double phi) => d.P + (fd.U * Math.Cos(phi) + fd.V * Math.Sin(phi)) * d.R;
        if (Analytic.SsiEvents.QLine(target, d.P, wd) == null) return null;
        (double A, double B, double C) Coef(double phi) => Analytic.SsiEvents.QLine(target, Q0(phi), wd)!.Value;
        double Disc(double phi) { var (A, B, C) = Coef(phi); return B * B - 4 * A * C; }
        // A ≈ 0 — образующая параллельна образующей конуса: корень уходит в бесконечность
        double amin = double.MaxValue, amax = 0;
        for (int i = 0; i < 64; i++) { var (A, _, _) = Coef(2 * Math.PI * i / 64); amin = Math.Min(amin, Math.Abs(A)); amax = Math.Max(amax, Math.Abs(A)); }
        if (amin < 1e-6 * Math.Max(1, amax)) return null;
        var roots = new List<double>();
        for (int k = 0; k < 4; k++)
        {
            double lo = Math.PI / 2 * k, hi = lo + Math.PI / 2, ac = (lo + hi) / 2, th = Math.Tan((hi - lo) / 4);
            double G(double u) { double tau = u * th; return Disc(ac + 2 * Math.Atan(tau)) * Math.Pow(1 + tau * tau, 4); }
            foreach (var u in Analytic.SsiEvents.PolyRootsOn(G, 8)) roots.Add(ac + 2 * Math.Atan(u * th));
        }
        roots.Sort();
        var rs = new List<double>();
        foreach (var x in roots) if (rs.Count == 0 || x - rs[^1] > 1e-12) rs.Add(x);
        if (rs.Count > 1 && rs[0] + 2 * Math.PI - rs[^1] <= 1e-12) rs.RemoveAt(rs.Count - 1);
        Vector3d Pt(double phi, int sgn)
        {
            var (A, B, C) = Coef(phi); double disc = sgn == 0 ? 0 : Math.Max(0, B * B - 4 * A * C);
            return Q0(phi) + wd * ((-B + sgn * Math.Sqrt(disc)) / (2 * A));
        }
        var sd = new CylinderSurface(fd, d.R);
        var r = new Result();
        var lists = new List<List<Vector3d>>();
        bool OnTarget(List<Vector3d> pts) => pts.All(p => Math.Abs(Implicit(target, p).F) < 1e-6 * Math.Max(1, (p - d.P).Length));
        const int n = 256;
        if (rs.Count == 0)
        {
            if (Disc(0) < 0) { r.Kind = Kind.Empty; return r; }
            foreach (int sgn in new[] { -1, 1 })
            {
                var pts = new List<Vector3d>();
                for (int i = 0; i <= n; i++) pts.Add(Pt(2 * Math.PI * i / n, sgn));
                if (!OnTarget(pts)) continue;
                lists.Add(pts);
            }
            return Build();
        }
        for (int i = 0; i < rs.Count; i++)
        {
            double a = rs[i], b = i + 1 < rs.Count ? rs[i + 1] : rs[0] + 2 * Math.PI;
            if (b - a < 1e-12 || Disc((a + b) / 2) <= 0) continue;
            // концы интервала — корни дискриминанта: обе ветви в одной точке (дискриминант ровно 0), петля замкнута точно
            var loop = new List<Vector3d>();
            for (int k = 0; k <= n; k++) { double t = a + (b - a) * k / (double)n; loop.Add(Pt(t, k == 0 || k == n ? 0 : -1)); }
            for (int k = n - 1; k >= 1; k--) { double t = a + (b - a) * k / (double)n; loop.Add(Pt(t, 1)); }
            loop.Add(loop[0]);
            RefineEnds(loop, n, d, target);
            if (!OnTarget(loop)) { if (target is Cone) continue; return null; }
            lists.Add(loop);
        }
        if (lists.Count == 0) { r.Kind = rs.Count > 0 && target is not Cone ? Kind.Tangent : Kind.Empty; foreach (var x in rs) r.Points.Add(Pt(x, 1)); return r; }
        return Build();

        // поверхность квадрики — по размеру области найденных кривых (у конуса образующая конечна: прежде 1e6 мм — разметка
        // такой поверхности съедала гигабайты, а по выборке углов — кривая выходила за образующую: «кривая ребра отходит от
        // поверхности на 6 мм»)
        Result? Build()
        {
            double ext = 0;
            foreach (var l in lists) foreach (var p in l) ext = Math.Max(ext, (p - d.P).Length);
            var st = SurfaceOf(target, d.P, 2 * ext + 1);
            if (st == null) return null;
            foreach (var l in lists) r.Curves.Add(new IntersectionCurve3(sd, st, l));
            r.Kind = r.Curves.Count > 0 ? Kind.Curves : Kind.Empty;
            return r;
        }
    }

    /// <summary>
    /// Концы интервала петли (дискриминант = 0, ветви сходятся) — уточнить на обе поверхности: в корне дискриминанта
    /// ошибка угла ε даёт ошибку точки ~√ε (у детали ×1000 вдали от начала — 6e-4 мм: «кривая ребра отходит от поверхностей»).
    /// Внутренние точки — равномерно по углу (сгущение у концов давало плохо обусловленные затравки).
    /// </summary>
    static void RefineEnds(List<Vector3d> loop, int n, Shape s1, Shape s2)
    {
        if (OntoBoth(s1, s2, loop[0]) is { } p0) { loop[0] = p0; loop[^1] = p0; }
        if (n < loop.Count && OntoBoth(s1, s2, loop[n]) is { } pn) loop[n] = pn;
    }

    static readonly bool CylConeOff = Environment.GetEnvironmentVariable("P3D_CYLCONE") == "0";
    static readonly bool ConeGenOff = Environment.GetEnvironmentVariable("P3D_CONEGEN") == "0";

    /// <summary>
    /// KRN-04 (04.1): КОНУС × КВАДРИКА (шар, конус) — все ветви по образующим конуса: образующая под углом φ — луч из вершины
    /// x = K + s·g(φ), s > 0 (одна полость); на квадрике A s² + B s + C = 0 (A, B, C по φ — тригонометрические многочлены
    /// степени ≤ 2), дискриминант — степени ≤ 4: корни точно (половинный угол, степень 8). Интервалы — петли, как у цилиндра.
    /// Ветвь на другой полости (s < 0) отбрасывается; если знак корней меняется внутри интервала (вершина на квадрике) или
    /// A ≈ 0 — null, прослеживание.
    /// </summary>
    static Result? ConeGeneratorsAgainst(Cone k, Shape target)
    {
        var w = k.Axis.Normalized(); var f = Frame3.FromNormal(k.Apex, w);
        double ca = Math.Cos(k.HalfAngle), sa = Math.Sin(k.HalfAngle);
        Vector3d Gd(double phi) => w * ca + (f.U * Math.Cos(phi) + f.V * Math.Sin(phi)) * sa;
        if (Analytic.SsiEvents.QLine(target, k.Apex, w) == null) return null;
        (double A, double B, double C) Coef(double phi) => Analytic.SsiEvents.QLine(target, k.Apex, Gd(phi))!.Value;
        double Disc(double phi) { var (A, B, C) = Coef(phi); return B * B - 4 * A * C; }
        double amin = double.MaxValue, amax = 0;
        for (int i = 0; i < 64; i++) { var (A, _, _) = Coef(2 * Math.PI * i / 64); amin = Math.Min(amin, Math.Abs(A)); amax = Math.Max(amax, Math.Abs(A)); }
        if (amin < 1e-6 * Math.Max(1, amax)) return null;
        // C = Q(вершины) не зависит от φ: вершина на квадрике — особый случай (прослеживание)
        var (_, _, C0) = Coef(0);
        if (Math.Abs(Implicit(target, k.Apex).F) < 1e-9) return null;
        var roots = new List<double>();
        for (int q = 0; q < 4; q++)
        {
            double lo = Math.PI / 2 * q, hi = lo + Math.PI / 2, ac = (lo + hi) / 2, th = Math.Tan((hi - lo) / 4);
            double G(double u) { double tau = u * th; return Disc(ac + 2 * Math.Atan(tau)) * Math.Pow(1 + tau * tau, 4); }
            foreach (var u in Analytic.SsiEvents.PolyRootsOn(G, 8)) roots.Add(ac + 2 * Math.Atan(u * th));
        }
        roots.Sort();
        var rs = new List<double>();
        foreach (var x in roots) if (rs.Count == 0 || x - rs[^1] > 1e-12) rs.Add(x);
        if (rs.Count > 1 && rs[0] + 2 * Math.PI - rs[^1] <= 1e-12) rs.RemoveAt(rs.Count - 1);
        double S(double phi, int sgn)
        {
            var (A, B, C) = Coef(phi); double disc = sgn == 0 ? 0 : Math.Max(0, B * B - 4 * A * C);
            return (-B + sgn * Math.Sqrt(disc)) / (2 * A);
        }
        Vector3d Pt(double phi, int sgn) => k.Apex + Gd(phi) * S(phi, sgn);
        double smax = 0;
        var r = new Result();
        var curves = new List<List<Vector3d>>();
        bool anyInterval = false;
        const int n = 256;
        if (rs.Count == 0)
        {
            if (Disc(0) < 0) { r.Kind = Kind.Empty; return r; }
            anyInterval = true;
            foreach (int sgn in new[] { -1, 1 })
            {
                var pts = new List<Vector3d>(); bool pos = true, neg = true;
                for (int i = 0; i <= n; i++) { double phi = 2 * Math.PI * i / n; double sv = S(phi, sgn); pos &= sv > 0; neg &= sv < 0; pts.Add(Pt(phi, sgn)); smax = Math.Max(smax, Math.Abs(sv)); }
                if (!pos && !neg) return null;
                if (pos) curves.Add(pts);
            }
        }
        else
            for (int i = 0; i < rs.Count; i++)
            {
                double a = rs[i], b = i + 1 < rs.Count ? rs[i + 1] : rs[0] + 2 * Math.PI;
                if (b - a < 1e-12 || Disc((a + b) / 2) <= 0) continue;
                anyInterval = true;
                var loop = new List<Vector3d>(); bool pos = true, neg = true;
                for (int q = 0; q <= n; q++) { double t = a + (b - a) * q / (double)n; int sg = q == 0 || q == n ? 0 : -1; double sv = S(t, sg); pos &= sv > 0; neg &= sv < 0; smax = Math.Max(smax, Math.Abs(sv)); loop.Add(Pt(t, sg)); }
                for (int q = n - 1; q >= 1; q--) { double t = a + (b - a) * q / (double)n; double sv = S(t, 1); pos &= sv > 0; neg &= sv < 0; smax = Math.Max(smax, Math.Abs(sv)); loop.Add(Pt(t, 1)); }
                loop.Add(loop[0]);
                RefineEnds(loop, n, k, target);
                if (!pos && !neg) return null;
                if (pos) curves.Add(loop);
            }
        var sk = SurfaceOf(k, k.Apex, 2 * smax + 1); var st = SurfaceOf(target, k.Apex, 2 * smax + 1);
        if (sk == null || st == null) return null;
        foreach (var c in curves)
        {
            if (!c.All(p => Math.Abs(Implicit(target, p).F) < 1e-6 * Math.Max(1, (p - k.Apex).Length))) return null;
            r.Curves.Add(new IntersectionCurve3(sk, st, c));
        }
        r.Kind = r.Curves.Count > 0 ? Kind.Curves : (!anyInterval && rs.Count > 0 ? Kind.Tangent : Kind.Empty);
        if (r.Kind == Kind.Tangent) foreach (var x in rs) r.Points.Add(Pt(x, 1));
        return r;
    }

    // ---------------------------------------------------------------- соосные тела вращения

    /// <summary>Профиль тела вращения в полуплоскости (ρ ≥ 0, z): отрезок/прямая или окружность.</summary>
    abstract record Prof;
    sealed record PLine(Vector2d P, Vector2d D) : Prof;            // точка и направление
    sealed record PCircle(Vector2d C, double R) : Prof;

    static (Vector3d O, Vector3d W)? AxisOf(Shape s) => s switch
    {
        Cylinder c => (c.P, c.Axis.Normalized()),
        Cone k => (k.Apex, k.Axis.Normalized()),
        Torus t => (t.C, t.Axis.Normalized()),
        _ => null
    };

    static Prof? ProfileOf(Shape s, Vector3d o, Vector3d w)
    {
        double Z(Vector3d x) => Vector3d.Dot(x - o, w);
        double Rho(Vector3d x) { var d = x - o; return (d - w * Vector3d.Dot(d, w)).Length; }
        switch (s)
        {
            case Plane p when Math.Abs(Math.Abs(Vector3d.Dot(p.N.Normalized(), w)) - 1) < 1e-12: return new PLine(new Vector2d(0, Z(p.P)), new Vector2d(1, 0));
            case Cylinder c when Rho(c.P) < 1e-12 && Math.Abs(Math.Abs(Vector3d.Dot(c.Axis.Normalized(), w)) - 1) < 1e-12: return new PLine(new Vector2d(c.R, 0), new Vector2d(0, 1));
            case Sphere sp when Rho(sp.C) < 1e-12: return new PCircle(new Vector2d(0, Z(sp.C)), sp.R);
            case Cone k when Rho(k.Apex) < 1e-12 && Math.Abs(Math.Abs(Vector3d.Dot(k.Axis.Normalized(), w)) - 1) < 1e-12:
            {
                double sgn = Math.Sign(Vector3d.Dot(k.Axis.Normalized(), w));
                return new PLine(new Vector2d(0, Z(k.Apex)), new Vector2d(Math.Sin(k.HalfAngle), sgn * Math.Cos(k.HalfAngle)));
            }
            case Torus t when Rho(t.C) < 1e-12 && Math.Abs(Math.Abs(Vector3d.Dot(t.Axis.Normalized(), w)) - 1) < 1e-12:
                return new PCircle(new Vector2d(t.R, Z(t.C)), t.r);
        }
        return null;
    }

    static Result? Coaxial(Shape a, Shape b)
    {
        var ax = AxisOf(a) ?? AxisOf(b);
        if (ax == null) return null;
        var (o, w) = ax.Value;
        var pa = ProfileOf(a, o, w); var pb = ProfileOf(b, o, w);
        if (pa == null || pb == null) return null;
        var pts = new List<Vector2d>();
        bool coincide = false, tangent = false;
        switch (pa, pb)
        {
            case (PLine l1, PLine l2): coincide = LineLine(l1, l2, pts); break;
            case (PLine l, PCircle c): tangent = LineCircle(l, c, pts); break;
            case (PCircle c, PLine l): tangent = LineCircle(l, c, pts); break;
            case (PCircle c1, PCircle c2): coincide = CircleCircle(c1, c2, pts, out tangent); break;
        }
        var r = new Result();
        if (coincide) { r.Kind = Kind.Coincident; return r; }
        var frame = Frame3.FromNormal(o, w);
        foreach (var q in pts.Where(q => q.X >= -1e-12))
        {
            var centre = o + w * q.Y;
            if (q.X < 1e-12) { r.Points.Add(centre); continue; }      // точка на оси (вершина конуса)
            r.Curves.Add(new ArcCurve3(new Frame3(centre, frame.U, frame.V), q.X, 0, 2 * Math.PI));
        }
        r.Kind = r.Curves.Count > 0 ? Kind.Curves : r.Points.Count > 0 ? Kind.Tangent : Kind.Empty;
        r.AllTangent = tangent && r.Curves.Count > 0;
        return r;
    }

    static bool LineLine(PLine a, PLine b, List<Vector2d> pts)
    {
        double den = a.D.X * b.D.Y - a.D.Y * b.D.X;
        var d = b.P - a.P;
        if (Math.Abs(den) < 1e-14) return Math.Abs(d.X * a.D.Y - d.Y * a.D.X) < 1e-12;   // параллельны: совпадают?
        double t = (d.X * b.D.Y - d.Y * b.D.X) / den;
        pts.Add(a.P + a.D * t);
        return false;
    }

    /// <returns>Касание (кратный корень): одна точка.</returns>
    static bool LineCircle(PLine l, PCircle c, List<Vector2d> pts)
    {
        var d = l.D.Normalized(); var f = l.P - c.C;
        double bq = Vector2d.Dot(f, d), cq = f.LengthSquared - c.R * c.R, disc = bq * bq - cq;
        if (disc < -1e-12) return false;
        double sq = Math.Sqrt(Math.Max(0, disc));
        pts.Add(l.P + d * (-bq - sq));
        if (sq > 1e-12) pts.Add(l.P + d * (-bq + sq));
        return sq <= 1e-12;
    }

    static bool CircleCircle(PCircle a, PCircle b, List<Vector2d> pts, out bool tangent)
    {
        tangent = false;
        var d = b.C - a.C; double L = d.Length;
        if (L < 1e-12) return Math.Abs(a.R - b.R) < 1e-12;
        if (L > a.R + b.R + 1e-12 || L < Math.Abs(a.R - b.R) - 1e-12) return false;
        double x = (L * L + a.R * a.R - b.R * b.R) / (2 * L), y2 = a.R * a.R - x * x;
        var n = d / L; var m = new Vector2d(-n.Y, n.X);
        double y = Math.Sqrt(Math.Max(0, y2));
        pts.Add(a.C + n * x + m * y);
        if (y > 1e-12) pts.Add(a.C + n * x - m * y);
        tangent = y <= 1e-12;
        return false;
    }

    // ---------------------------------------------------------------- плоскость × конус, тор

    static Result PlaneCone(Plane p, Cone k, Vector3d bc, double br)
    {
        var n = p.N.Normalized(); var w = k.Axis.Normalized();
        var f = Frame3.FromNormal(k.Apex, w);
        double ca = Math.Cos(k.HalfAngle), sa = Math.Sin(k.HalfAngle);
        Vector3d G(double phi) => w * ca + (f.U * Math.Cos(phi) + f.V * Math.Sin(phi)) * sa;
        var r = new Result();
        // Плоскость через вершину: 0–2 образующие (прямые).
        if (Math.Abs(Vector3d.Dot(n, k.Apex - p.P)) < 1e-12)
        {
            for (int i = 0; i < 720; i++)
            {
                double p0 = 2 * Math.PI * i / 720, p1 = 2 * Math.PI * (i + 1) / 720;
                double g0 = Vector3d.Dot(n, G(p0)), g1 = Vector3d.Dot(n, G(p1));
                if (g0 == 0 || g0 * g1 < 0)
                {
                    double lo = p0, hi = p1;
                    for (int it = 0; it < 60; it++) { double m = (lo + hi) / 2; if (Math.Sign(Vector3d.Dot(n, G(m))) == Math.Sign(g0)) lo = m; else hi = m; }
                    var dir = G((lo + hi) / 2).Normalized();
                    r.Curves.AddRange(Line(k.Apex, dir, bc, br).Curves.Select(c => (ICurve3)new LineCurve3(k.Apex, ((LineCurve3)c).B)));
                }
            }
            r.Kind = r.Curves.Count > 0 ? Kind.Curves : Kind.Tangent;
            if (r.Curves.Count == 0) r.Points.Add(k.Apex);
            return r;
        }
        // Общий случай: точки по образующим (t > 0 — одна полость конуса), ветви — непрерывные
        // куски допустимых углов; уточнение на обеих поверхностях — IntersectionCurve3.
        int N = 512;
        var pts = new (bool Ok, Vector3d X)[N];
        for (int i = 0; i < N; i++)
        {
            var g = G(2 * Math.PI * i / N); double den = Vector3d.Dot(n, g);
            if (Math.Abs(den) < 1e-14) { pts[i] = (false, default); continue; }
            double t = Vector3d.Dot(n, p.P - k.Apex) / den;
            var x = k.Apex + g * t;
            pts[i] = (t > 0 && (x - bc).Length <= br, x);
        }
        var plane = new PlaneSurface(Frame3.FromNormal(p.P, n));
        double far = (bc - k.Apex).Length + br;
        var cone = new RevolvedSurface(new LineCurve3(k.Apex, k.Apex + (w * ca + f.U * sa) * far), new Frame3(k.Apex, f.U, f.V), 0, 2 * Math.PI);
        if (pts.All(q => q.Ok))
        {
            var loop = pts.Select(q => q.X).ToList(); loop.Add(loop[0]);
            r.Curves.Add(new IntersectionCurve3(plane, cone, loop));
        }
        else
        {
            int start = Array.FindIndex(pts, q => !q.Ok);
            var cur = new List<Vector3d>();
            for (int j = 1; j <= N; j++)
            {
                var q = pts[(start + j) % N];
                if (q.Ok) cur.Add(q.X);
                else if (cur.Count > 1) { r.Curves.Add(new IntersectionCurve3(plane, cone, cur)); cur = new List<Vector3d>(); }
                else cur.Clear();
            }
            if (cur.Count > 1) r.Curves.Add(new IntersectionCurve3(plane, cone, cur));
        }
        r.Kind = r.Curves.Count > 0 ? Kind.Curves : Kind.Empty;
        return r;
    }

    static Result PlaneTorus(Plane p, Torus t)
    {
        var n = p.N.Normalized(); var w = t.Axis.Normalized();
        var r = new Result();
        // Плоскость через ось: два образующих круга (или 0, если плоскость их не задевает — не бывает).
        if (Math.Abs(Vector3d.Dot(n, w)) < 1e-12 && Math.Abs(Vector3d.Dot(n, t.C - p.P)) < 1e-12)
        {
            var dirIn = Vector3d.Cross(n, w).Normalized();
            foreach (double sgn in new[] { -1.0, 1.0 })
                r.Curves.Add(new ArcCurve3(new Frame3(t.C + dirIn * (sgn * t.R), dirIn, w), t.r, 0, 2 * Math.PI));
            r.Kind = Kind.Curves; return r;
        }
        return Trace(p, t, t.C, t.R + t.r + 1);
    }

    // ---------------------------------------------------------------- общее прослеживание

    /// <summary>Расстояние со знаком до поверхности и единичная нормаль (градиент).</summary>
    public static (double F, Vector3d G) Implicit(Shape s, Vector3d x)
    {
        switch (s)
        {
            case Plane p: { var n = p.N.Normalized(); return (Vector3d.Dot(n, x - p.P), n); }
            case Sphere sp: { var d = x - sp.C; double L = d.Length; return (L - sp.R, L > 0 ? d / L : Vector3d.UnitZ); }
            case Cylinder c: { var w = c.Axis.Normalized(); var d = x - c.P; var r = d - w * Vector3d.Dot(d, w); double L = r.Length; return (L - c.R, L > 0 ? r / L : Vector3d.UnitX); }
            case Cone k:
            {
                var w = k.Axis.Normalized(); var d = x - k.Apex; double h = Vector3d.Dot(d, w); var rad = d - w * h; double rho = rad.Length;
                double f = rho * Math.Cos(k.HalfAngle) - h * Math.Sin(k.HalfAngle);
                var g = (rho > 0 ? rad / rho : Vector3d.UnitX) * Math.Cos(k.HalfAngle) - w * Math.Sin(k.HalfAngle);
                return (f, g);
            }
            case Torus t:
            {
                var w = t.Axis.Normalized(); var d = x - t.C; double h = Vector3d.Dot(d, w); var rad = d - w * h;
                var ring = t.C + (rad.LengthSquared > 0 ? rad.Normalized() : Vector3d.UnitX) * t.R;
                var v = x - ring; double L = v.Length;
                return (L - t.r, L > 0 ? v / L : w);
            }
            case ParamSurf ps:
            {
                var (p, n) = ParamFoot(ps.S, x);
                return (Vector3d.Dot(x - p, n), n);
            }
        }
        throw new NotSupportedException(s.GetType().Name);
    }

    /// <summary>Точка поверхности по двум параметрам на прямоугольнике [0,1]² (для затравки).</summary>
    internal static Func<double, double, Vector3d> Param(Shape s, Vector3d bc, double br)
    {
        switch (s)
        {
            case Plane p:
            {
                var f = Frame3.FromNormal(p.P, p.N.Normalized()); var c = bc - f.W * Vector3d.Dot(bc - p.P, f.W);
                return (u, v) => c + f.U * ((2 * u - 1) * br) + f.V * ((2 * v - 1) * br);
            }
            case Cylinder cy:
            {
                var f = Frame3.FromNormal(cy.P, cy.Axis.Normalized()); double hc = Vector3d.Dot(bc - cy.P, f.W);
                return (u, v) => cy.P + (f.U * Math.Cos(2 * Math.PI * u) + f.V * Math.Sin(2 * Math.PI * u)) * cy.R + f.W * (hc + (2 * v - 1) * br);
            }
            case Sphere sp:
            {
                return (u, v) => { double th = 2 * Math.PI * u, ph = Math.PI * v; return sp.C + new Vector3d(Math.Sin(ph) * Math.Cos(th), Math.Sin(ph) * Math.Sin(th), Math.Cos(ph)) * sp.R; };
            }
            case Cone k:
            {
                var f = Frame3.FromNormal(k.Apex, k.Axis.Normalized()); double far = (bc - k.Apex).Length + br;
                return (u, v) => { double th = 2 * Math.PI * u; var g = f.W * Math.Cos(k.HalfAngle) + (f.U * Math.Cos(th) + f.V * Math.Sin(th)) * Math.Sin(k.HalfAngle); return k.Apex + g * (v * far); };
            }
            case Torus t:
            {
                var f = Frame3.FromNormal(t.C, t.Axis.Normalized());
                return (u, v) => { double th = 2 * Math.PI * u, ph = 2 * Math.PI * v; var rad = f.U * Math.Cos(th) + f.V * Math.Sin(th); return t.C + rad * (t.R + t.r * Math.Cos(ph)) + f.W * (t.r * Math.Sin(ph)); };
            }
        }
        throw new NotSupportedException(s.GetType().Name);
    }

    /// <summary>Наименьший сдвиг точки на пересечение двух поверхностей (Ньютон).</summary>
    static Vector3d? OntoBoth(Shape a, Shape b, Vector3d x)
    {
        for (int it = 0; it < 40; it++)
        {
            var (f1, g1) = Implicit(a, x); var (f2, g2) = Implicit(b, x);
            if (Math.Abs(f1) < 1e-13 && Math.Abs(f2) < 1e-13) return x;
            double a11 = Vector3d.Dot(g1, g1), a12 = Vector3d.Dot(g1, g2), a22 = Vector3d.Dot(g2, g2), det = a11 * a22 - a12 * a12;
            if (Math.Abs(det) < 1e-16) return null;                       // касание: нормали параллельны
            double l1 = (a22 * f1 - a12 * f2) / det, l2 = (-a12 * f1 + a11 * f2) / det;
            var dx = g1 * l1 + g2 * l2;
            x -= dx;
            if (dx.Length > 1e3) return null;
        }
        var (e1, _) = Implicit(a, x); var (e2, _) = Implicit(b, x);
        return Math.Abs(e1) < 1e-10 && Math.Abs(e2) < 1e-10 ? x : null;
    }

    /// <summary>
    /// G4. ОБЩЕЕ ПРОСЛЕЖИВАНИЕ пересечения двух поверхностей: затравки — смены знака расстояния
    /// до B на сетке параметров A (с уточнением на обе поверхности), затем шаг вдоль касательной
    /// gA × gB с поправкой на обе поверхности, до возврата к началу (замкнутая кривая) или выхода
    /// из области (открытая; тогда — и в обратную сторону). Затравки рядом с уже пройденной
    /// кривой не используются повторно. Касание поверхностей (нормали параллельны) останавливает шаг.
    /// </summary>
    public static Result Trace(Shape a, Shape b, Vector3d bc, double br)
    {
        var r = new Result();
        var P = Param(a, bc, br);
        int N = 128;
        var seeds = new List<Vector3d>();
        double FB(Vector3d x) => Implicit(b, x).F;
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
                foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
                {
                    if (i + di > N || j + dj > N) continue;
                    double u0 = (double)i / N, v0 = (double)j / N, u1 = (double)(i + di) / N, v1 = (double)(j + dj) / N;
                    var x0 = P(u0, v0); var x1 = P(u1, v1);
                    double f0 = FB(x0), f1 = FB(x1);
                    if (f0 == 0 || f0 * f1 < 0)
                    {
                        double lo = 0, hi = 1;
                        for (int it = 0; it < 50; it++) { double m = (lo + hi) / 2; if (Math.Sign(FB(P(u0 + (u1 - u0) * m, v0 + (v1 - v0) * m))) == Math.Sign(f0)) lo = m; else hi = m; }
                        if (OntoBoth(a, b, P(u0 + (u1 - u0) * lo, v0 + (v1 - v0) * lo)) is { } q && (q - bc).Length <= br) seeds.Add(q);
                    }
                }
        double h = Math.Max(br / 200, 1e-4);
        var traced = new List<List<Vector3d>>();
        bool Near(Vector3d q) => traced.Any(c => c.Any(p => (p - q).LengthSquared < 4 * h * h));
        void TraceFrom(Vector3d s0)
        {
            if (Near(s0)) return;
            List<Vector3d> Walk(Vector3d start, double dir, out bool closed)
            {
                closed = false;
                var pts = new List<Vector3d> { start };
                var x = start; double travelled = 0, hsStart = -1;
                for (int step = 0; step < 200000; step++)
                {
                    var (_, g1) = Implicit(a, x); var (_, g2) = Implicit(b, x);
                    var t = Vector3d.Cross(g1, g2);
                    if (t.Length < 1e-10) break;                            // касание
                    // g.05 (04.7): ШАГ ПО УГЛУ ПЕРЕСЕЧЕНИЯ — у почти касания точки чаще (шаг ≤ ¼ угла на кривизну): удостоверение
                    // единственности ветви доказывается в шаре радиусом в шаг, и редкие точки там его не допускали
                    double hs = h;
                    if (!UniqOff && !TestFixedStep && NormalLip(a, x, 0) is double la0 && NormalLip(b, x, 0) is double lb0 && la0 + lb0 > 0)
                        hs = Math.Clamp(0.25 * t.Length / (la0 + lb0), Math.Max(1e-5 * br, 1e-6), h);
                    t = t.Normalized() * dir;
                    if (hsStart < 0) hsStart = hs;
                    var y = OntoBoth(a, b, x + t * hs);
                    if (y == null) break;
                    travelled += (y.Value - x).Length;
                    x = y.Value;
                    if ((x - bc).Length > br) { pts.Add(x); break; }
                    // замыкание: обойдено больше трёх шагов у начала и вернулись ближе 0.8 шага (шаг у начала и текущий —
                    // при переменном шаге прежнее «ближе 0.8 h» срабатывало на первых мелких шагах)
                    double hc = UniqOff ? h : Math.Max(hs, hsStart);
                    if (step > 4 && travelled > 3 * hc && (x - start).Length < 0.8 * hc) { closed = true; break; }
                    pts.Add(x);
                }
                return pts;
            }
            var fwd = Walk(s0, 1, out bool closedF);
            List<Vector3d> curve;
            if (closedF) { curve = fwd; curve.Add(fwd[0]); }
            else { var back = Walk(s0, -1, out _); back.Reverse(); back.RemoveAt(back.Count - 1); curve = back; curve.AddRange(fwd); }
            if (curve.Count < 3) return;
            traced.Add(curve);
        }
        foreach (var s0 in seeds) TraceFrom(s0);
        // g.05: ПОЛНОТА ПРОСЛЕЖИВАНИЯ — исключение по Липшицу (CertifyTrace). Непояснённые ячейки — новые затравки; после
        // двух раундов непояснённая ячейка — отказ (Uncertified), а не тихо потерянная замкнутая ветвь. P3D_TRACECERT=0.
        if (!TraceCertOff)
        {
            for (int round = 0; round < 3; round++)
            {
                var extra = new List<Vector3d>();
                string? why = CertifyTrace(a, b, P, bc, br, traced, h, extra);
                if (why == null) break;
                int before = traced.Count;
                foreach (var q in extra) TraceFrom(q);
                if (traced.Count == before || round == 2)
                {
                    // ещё раз — без новых затравок: осталась ли непояснённая ячейка
                    if (CertifyTrace(a, b, P, bc, br, traced, h, null) is { } why2) { r.Uncertified = why2; Interlocked.Increment(ref TraceUncertified); }
                    break;
                }
                Interlocked.Add(ref TraceBranchesAdded, traced.Count - before);
            }
        }
        if (traced.Count == 0) { r.Kind = r.Uncertified != null ? Kind.Curves : Kind.Empty; return r; }
        var sa = SurfaceOf(a, bc, br); var sb = SurfaceOf(b, bc, br);
        foreach (var c in traced)
            r.Curves.Add(sa != null && sb != null ? new IntersectionCurve3(sa, sb, c) : new PolylineCurve3(c));
        r.Kind = r.Curves.Count > 0 ? Kind.Curves : Kind.Empty;
        return r;
    }

    static readonly bool TraceCertOff = Environment.GetEnvironmentVariable("P3D_TRACECERT") == "0";
    static readonly bool UniqOff = Environment.GetEnvironmentVariable("P3D_TRACEUNIQ") == "0";
    /// <summary>Мутация: P3D_TRACESTEPCERT=0 — без проверки звеньев ломаной и полноты прохода дуги в шаре ячейки.</summary>
    static readonly bool StepCertOff = Environment.GetEnvironmentVariable("P3D_TRACESTEPCERT") == "0";
    /// <summary>Для проверки: постоянный шаг прослеживания (без шага по углу) — ломаная может перескочить на соседнюю ветвь,
    /// удостоверение обязано это поймать.</summary>
    [ThreadStatic] internal static bool TestFixedStep;
    /// <summary>Замер: ячеек у прослеженной кривой, где единственность ветви не доказана до малого размера (отказ).</summary>
    public static long TraceNearTangent;

    /// <summary>Верхняя оценка поворота единичного градиента неявной формы на единицу длины в шаре (c, rb); null — нет оценки
    /// (шар задевает ось, центр, кольцо — особенность формы).</summary>
    internal static double? NormalLip(Shape s, Vector3d c, double rb)
    {
        switch (s)
        {
            case Plane: return 0;
            case Sphere sp: { double d = (c - sp.C).Length - rb; return d > 1e-9 ? 1 / d : null; }
            case Cylinder cy: { var w = cy.Axis.Normalized(); var d = c - cy.P; double rho = (d - w * Vector3d.Dot(d, w)).Length - rb; return rho > 1e-9 ? 1 / rho : null; }
            case Cone k: { var w = k.Axis.Normalized(); var d = c - k.Apex; double rho = (d - w * Vector3d.Dot(d, w)).Length - rb; return rho > 1e-9 ? Math.Cos(k.HalfAngle) / rho : null; }
            case Torus t:
            {
                var w = t.Axis.Normalized(); var d = c - t.C; double hh = Vector3d.Dot(d, w); var rad = d - w * hh; double rho = rad.Length;
                var ring = t.C + (rho > 0 ? rad / rho : Vector3d.UnitX) * t.R; double dr = (c - ring).Length - rb; double ra = rho - rb;
                return dr > 1e-9 && ra > 1e-9 ? 1 / dr + 1 / ra : null;
            }
        }
        return null;
    }

    /// <summary>Поверхности трансверсальны во всём шаре и шар мал против их кривизны (ветвь в шаре — одна дуга).</summary>
    internal static bool Transversal(Shape a, Shape b, Vector3d c, double rb)
    {
        if (NormalLip(a, c, rb) is not double la || NormalLip(b, c, rb) is not double lb) return false;
        double L = (la + lb) * rb; if (L > 0.25) return false;
        var ga = Implicit(a, c).G; var gb = Implicit(b, c).G;
        return Vector3d.Cross(ga.Normalized(), gb.Normalized()).Length - L > 1e-3;
    }

    /// <summary>Константы Липшица карты Param по u и v (на [0,1]²): плоскость — 2br, 2br; цилиндр — 2πR, 2br; шар — 2πR, πR;
    /// конус — 2π·far·sin α, far; тор — 2π(R + r), 2πr.</summary>
    /// <summary>Для проверки: точка карты и строгая оценка радиуса образа ячейки [u0,u1]×[v0,v1] от образа её центра.</summary>
    public static Vector3d ChartPoint(Shape s, Vector3d bc, double br, double u, double v) => Param(s, bc, br)(u, v);
    public static double? CellRadiusBound(Shape s, Vector3d bc, double br, double u0, double v0, double u1, double v1) =>
        ParamLipschitz(s, bc, br) is { } lp ? lp.Lu * (u1 - u0) / 2 + lp.Lv * (v1 - v0) / 2 : null;

    internal static (double Lu, double Lv)? ParamLipschitz(Shape s, Vector3d bc, double br) => s switch
    {
        Plane => (2 * br, 2 * br),
        Cylinder cy => (2 * Math.PI * cy.R, 2 * br),
        Sphere sp => (2 * Math.PI * sp.R, Math.PI * sp.R),
        Cone k => ((bc - k.Apex).Length + br) is double far ? (2 * Math.PI * far * Math.Sin(k.HalfAngle), far) : null,
        Torus t => (2 * Math.PI * (t.R + t.r), 2 * Math.PI * t.r),
        _ => null
    };
    /// <summary>Замер: ветвей, найденных удостоверением (пропущенных сеткой затравок), и отказов удостоверения.</summary>
    public static long TraceBranchesAdded, TraceUncertified;

    /// <summary>
    /// ИСКЛЮЧЕНИЕ ПО ЛИПШИЦУ. Расстояние со знаком до плоскости, цилиндра, шара, тора и форма конуса ρcosα − h sinα — функции
    /// с константой Липшица 1. Ячейка параметров поверхности A с центром c и радиусом образа ρ (наибольшее расстояние от c до
    /// углов и середин сторон, × 1.25 на изгиб) не содержит точек B, если |F_B(c)| > ρ. Остальные ячейки должны быть
    /// «пояснены» — найденная кривая проходит ближе ρ + h от c; иначе ячейка делится до ρ < ρ_min (1e-4 · радиус области),
    /// и в непояснённой малой ячейке ищется точка пересечения (Ньютон из c) — новая затравка (extra); не найдена — отказ.
    /// null — все ячейки исключены или пояснены.
    /// </summary>
    /// <summary>Для проверки: удостоверение готовых ломаных (те же карта, шаг и область, что у Trace).</summary>
    internal static string? CertifyTraceForTest(Shape a, Shape b, Vector3d bc, double br, List<List<Vector3d>> traced) =>
        CertifyTrace(a, b, Param(a, bc, br), bc, br, traced, Math.Max(br / 200, 1e-4), null);

    static string? CertifyTrace(Shape a, Shape b, Func<double, double, Vector3d> P, Vector3d bcC, double br, List<List<Vector3d>> traced, double h, List<Vector3d>? extra)
    {
        // сетка корзин по точкам кривых
        double cell = Math.Max(h * 4, br / 64);
        var buckets = new Dictionary<(long, long, long), List<(Vector3d P, int C)>>();
        (long, long, long) K(Vector3d x) => ((long)Math.Floor(x.X / cell), (long)Math.Floor(x.Y / cell), (long)Math.Floor(x.Z / cell));
        for (int ci = 0; ci < traced.Count; ci++) foreach (var p in traced[ci]) { var k = K(p); if (!buckets.TryGetValue(k, out var l)) buckets[k] = l = new(); l.Add((p, ci)); }
        bool Explained(Vector3d x, double rad) => NearestTraced(x, rad) != null;
        int nearestCurve = -1;
        Vector3d? NearestTraced(Vector3d x, double rad)
        {
            double rr = rad + 2 * h; int n = (int)Math.Ceiling(rr / cell);
            var k0 = K(x); Vector3d? best = null; double bd = rr * rr; nearestCurve = -1;
            for (long i = -n; i <= n; i++) for (long j = -n; j <= n; j++) for (long kk = -n; kk <= n; kk++)
                if (buckets.TryGetValue((k0.Item1 + i, k0.Item2 + j, k0.Item3 + kk), out var l))
                    foreach (var (p, ci) in l) { double d2 = (p - x).LengthSquared; if (d2 <= bd) { bd = d2; best = p; nearestCurve = ci; } }
            return best;
        }
        // 04.7: ЛОМАНАЯ БЕЗ СКАЧКОВ — каждое звено лежит в шаре, где ветвь одна (трансверсальность и малость против кривизны):
        // обе точки звена — на одной дуге, значит, ломаная идёт по одной связной ветви. Мутация P3D_TRACEUNIQ=0.
        if (!UniqOff && !StepCertOff)
            foreach (var cv in traced)
                for (int i = 1; i < cv.Count; i++)
                {
                    var mid = (cv[i] + cv[i - 1]) / 2; double half = (cv[i] - cv[i - 1]).Length / 2;
                    if (!Transversal(a, b, mid, half * 1.01 + 1e-12)) return $"trace certificate: trace step at {mid:0.######} not proven single-branch";
                }
        // ветвь в шаре ячейки пройдена ломаной целиком, если концы незамкнутой ломаной этой ветви — вне шара (или вне области)
        bool Covered(Vector3d c, double rb)
        {
            if (UniqOff || StepCertOff || nearestCurve < 0) return true;
            var cv = traced[nearestCurve]; if (cv.Count > 2 && (cv[0] - cv[^1]).Length < 1e-9) return true;
            foreach (var e in new[] { cv[0], cv[^1] })
                if ((e - c).Length < rb && (e - bcC).Length < br * (1 - 1e-9)) return false;
            return true;
        }
        double rhoMin = 1e-4 * br;
        (double Lu, double Lv)? Lip = ParamLipschitz(a, bcC, br);
        var stack = new Stack<(double U0, double V0, double U1, double V1)>();
        int G = 32;
        for (int i = 0; i < G; i++) for (int j = 0; j < G; j++) stack.Push(((double)i / G, (double)j / G, (double)(i + 1) / G, (double)(j + 1) / G));
        int visited = 0; string? fail = null;
        while (stack.Count > 0)
        {
            if (++visited > 2000000) return "trace certificate: too many cells";
            var (u0, v0, u1, v1) = stack.Pop();
            double um = (u0 + u1) / 2, vm = (v0 + v1) / 2;
            var c = P(um, vm);
            double rho = 0;
            foreach (var (uu, vv) in new[] { (u0, v0), (u1, v0), (u0, v1), (u1, v1), (um, v0), (um, v1), (u0, vm), (u1, vm) })
                rho = Math.Max(rho, (P(uu, vv) - c).Length);
            // строгая оценка: |P(u,v) − P(c)| ≤ L_u·Δu/2 + L_v·Δv/2 (L — наибольшие |∂P/∂u|, |∂P/∂v| карты); без известной
            // карты — по отсчётам с запасом 1.25
            rho = Lip is { } lp ? lp.Lu * (u1 - u0) / 2 + lp.Lv * (v1 - v0) / 2 : rho * 1.25;
            if ((c - bcC).Length > br + rho) continue;              // вне шара области — кривые там не нужны
            double f = Math.Abs(Implicit(b, c).F);
            if (f > rho) continue;                                  // исключена
            // пояснена найденной кривой; g.05 (04.7): и ДОКАЗАНО, что в шаре ячейки ветвь одна — поверхности трансверсальны во
            // всём шаре (угол нормалей в центре минус их поворот по шару > 0) и шар мал против кривизны: пересечение там —
            // одна дуга (теорема о неявной функции). Прежде ветвь ближе ~2 шагов к прослеженной считалась пояснённой без
            // доказательства. Мутация P3D_TRACEUNIQ=0.
            // шар — от центра ячейки до ближайшей точки найденной кривой (не меньше радиуса ячейки): в нём одна дуга, и она
            // проходит через найденную точку — значит, все точки пересечения в ячейке лежат на найденной ветви
            if (NearestTraced(c, rho) is { } pNear)
            {
                double rb1 = Math.Max(rho, (pNear - c).Length) * 1.01 + 1e-12;
                if (UniqOff || (Transversal(a, b, c, rb1) && Covered(c, rb1))) continue;
                // ближе: точка пересечения у центра (Ньютон) и ближайшая к ней найденная — шар через обе
                if (OntoBoth(a, b, c) is { } qn && NearestTraced(qn, 0) is { } pq)
                {
                    double rb2 = Math.Max(rho, Math.Max((qn - c).Length, (pq - c).Length)) * 1.01 + 1e-12;
                    if (Transversal(a, b, c, rb2) && Covered(c, rb2)) continue;
                }
                // у почти касания полоса неисключённых ячеек шире — делить глубже обычного предела
                if (rho <= 1e-2 * rhoMin) { Interlocked.Increment(ref TraceNearTangent); return $"trace certificate: near-tangent cell at {c:0.######} (single branch not proven)"; }
                stack.Push((u0, v0, um, vm)); stack.Push((um, v0, u1, vm)); stack.Push((u0, vm, um, v1)); stack.Push((um, vm, u1, v1));
                continue;
            }
            if (rho > rhoMin)
            {
                stack.Push((u0, v0, um, vm)); stack.Push((um, v0, u1, vm)); stack.Push((u0, vm, um, v1)); stack.Push((um, vm, u1, v1));
                continue;
            }
            // малая непояснённая ячейка: точка пересечения рядом?
            // Ньютон из центра ячейки — к точке пересечения (возможно, не в самой ячейке, а у ближайшей ветви); точка, лежащая
            // на найденной кривой, ячейку не поясняет — ветвь всё равно ищется дальше делением соседних ячеек
            if (OntoBoth(a, b, c) is { } q && (q - c).Length <= Math.Max(1e3 * rho, 10 * h))
            {
                if (Explained(q, 0) && (q - c).Length > 4 * rho) { }
                else
                {
                    if (extra == null) return $"trace certificate: an intersection point near {q:0.######} is on no traced curve";
                    if (!extra.Any(e => (e - q).LengthSquared < 16 * h * h)) extra.Add(q);
                    fail ??= "new seeds";
                    continue;
                }
            }
            // точки нет: касание (нормали почти параллельны) или ошибка оценки — не решено
            return $"trace certificate: unexplained near-contact cell at {c:0.######} (|F| {f:G3}, radius {rho:G3})";
        }
        return fail;
    }

    /// <summary>Поверхность ядра для формы (для уточнения точек кривой пересечения).</summary>
    static ISurface3? SurfaceOf(Shape s, Vector3d bc, double br)
    {
        switch (s)
        {
            case Plane p: return new PlaneSurface(Frame3.FromNormal(p.P, p.N.Normalized()));
            case Cylinder c: return new CylinderSurface(Frame3.FromNormal(c.P, c.Axis.Normalized()), c.R);
            case Sphere sp:
            {
                var f = new Frame3(sp.C, Vector3d.UnitX, Vector3d.UnitZ);
                return new RevolvedSurface(new ArcCurve3(f, sp.R, -Math.PI / 2, Math.PI / 2), new Frame3(sp.C, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
            }
            case Cone k:
            {
                var f = Frame3.FromNormal(k.Apex, k.Axis.Normalized()); double far = (bc - k.Apex).Length + br;
                return new RevolvedSurface(new LineCurve3(k.Apex, k.Apex + (f.W * Math.Cos(k.HalfAngle) + f.U * Math.Sin(k.HalfAngle)) * far), new Frame3(k.Apex, f.U, f.V), 0, 2 * Math.PI);
            }
            case Torus t:
            {
                var f = Frame3.FromNormal(t.C, t.Axis.Normalized());
                return new RevolvedSurface(new ArcCurve3(new Frame3(t.C + f.U * t.R, f.U, f.W), t.r, 0, 2 * Math.PI), new Frame3(t.C, f.U, f.V), 0, 2 * Math.PI);
            }
        }
        return null;
    }
}
