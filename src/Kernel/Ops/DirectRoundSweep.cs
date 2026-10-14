using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_SWEEPEXACT=0: протяжка по общей траектории — прежним многогранным путём.</summary>
    internal static readonly bool SweepExactOff = Environment.GetEnvironmentVariable("P3D_SWEEPEXACT") == "0";
    public static long ExactSweeps;

    /// <summary>
    /// KRN-07 (07.3): ТОЧНАЯ ПРОТЯЖКА ПО ОБЩЕЙ ГЛАДКОЙ ТРАЕКТОРИИ (сплайн, кривая Безье, эллипс — не отрезок и не дуга, у тех
    /// свои точные пути). Рамка вдоль траектории — БЕЗ ВРАЩЕНИЯ (rotation-minimizing): r' = −(r·T')T; таблица двойным
    /// отражением (Ван и др., 2008) на 2048 шагах и кубическая Эрмита с точной производной r' в узлах, с подгонкой под
    /// касательную. Закрутка — поворот рамки вокруг T на угол, линейный по параметру. Звено сечения (отрезок, дуга — дуги
    /// делятся на куски ≤ 90°) заметает грань SectionSurface по формуле сечения (отрезок/дуга в рамке траектории); рельсы —
    /// процедурные кривые концов звеньев той же формулы, торцы — плоские грани по точным перенесённым сечениям. Тело
    /// собирается и проверяется как у прямых сопряжений (годность, объём по формуле поверхности, объём по фасетам).
    /// Замкнутая траектория — пока прежний путь. null — отказ (причина в why).
    /// </summary>
    internal static TSolid? ExactSweep(Profile section, ICurve3 path, double frac, double twistRad, ref string? why)
    {
        if (SweepExactOff) { why = "exact sweep is off"; return null; }
        if (path.IsClosed) { why = "exact sweep: closed path"; return null; }
        if (section.Loops.SelectMany(l => l.Curves).Any(c => c is not (LineCurve3 or ArcCurve3))) { why = "exact sweep: a section curve is neither a line nor an arc"; return null; }
        double s0 = path.T0, s1 = path.T0 + (path.T1 - path.T0) * Math.Clamp(frac, 1e-4, 1);
        double hs = 1e-6 * (s1 - s0);
        Vector3d Tan(double s) { var d = path.PointAt(s + hs) - path.PointAt(s - hs); return d.Normalized(); }
        Vector3d TanD(double s) => (Tan(s + 1e-4 * (s1 - s0)) - Tan(s - 1e-4 * (s1 - s0))) / (2e-4 * (s1 - s0));
        // ТАБЛИЦА РАМКИ: двойное отражение
        const int NT = 2048; double h = (s1 - s0) / NT;
        var P = new Vector3d[NT + 1]; var T = new Vector3d[NT + 1]; var R = new Vector3d[NT + 1]; var dR = new Vector3d[NT + 1];
        for (int i = 0; i <= NT; i++) { double s = s0 + h * i; P[i] = path.PointAt(s); T[i] = Tan(s); }
        if (T.Any(x => double.IsNaN(x.X))) { why = "exact sweep: path tangent undefined"; return null; }
        // начальная рамка: плоскость сечения поворачивается нормалью по касательной в начале (как у прежней протяжки)
        var f0 = Align(section.Plane, P[0], T[0]);
        R[0] = f0.U;
        for (int i = 0; i < NT; i++)
        {
            var v1 = P[i + 1] - P[i]; double c1 = Vector3d.Dot(v1, v1);
            if (c1 < 1e-30) { why = "exact sweep: path speed vanishes"; return null; }
            var rL = R[i] - v1 * (2 / c1 * Vector3d.Dot(v1, R[i])); var tL = T[i] - v1 * (2 / c1 * Vector3d.Dot(v1, T[i]));
            var v2 = T[i + 1] - tL; double c2 = Vector3d.Dot(v2, v2);
            R[i + 1] = c2 < 1e-30 ? rL : rL - v2 * (2 / c2 * Vector3d.Dot(v2, rL));
            R[i + 1] = (R[i + 1] - T[i + 1] * Vector3d.Dot(R[i + 1], T[i + 1])).Normalized();
        }
        for (int i = 0; i <= NT; i++) { double s = s0 + h * i; dR[i] = -T[i] * Vector3d.Dot(R[i], TanD(s)); }
        Vector3d RAt(double s)
        {
            double x = (s - s0) / h; int i = Math.Clamp((int)Math.Floor(x), 0, NT - 1); double u = x - i;
            double h00 = 2 * u * u * u - 3 * u * u + 1, h10 = u * u * u - 2 * u * u + u, h01 = -2 * u * u * u + 3 * u * u, h11 = u * u * u - u * u;
            var r = R[i] * h00 + dR[i] * (h10 * h) + R[i + 1] * h01 + dR[i + 1] * (h11 * h);
            var t = Tan(s); r -= t * Vector3d.Dot(r, t); return r.Normalized();
        }
        // рамка с закруткой: U = r cos φ + (T × r) sin φ, V = T × U
        (Vector3d O, Vector3d U, Vector3d V) Fr(double s)
        {
            var t = Tan(s); var r = RAt(s); double ph = twistRad * (s - s0) / (s1 - s0);
            var u = r * Math.Cos(ph) + Vector3d.Cross(t, r) * Math.Sin(ph);
            return (path.PointAt(s), u, Vector3d.Cross(t, u));
        }
        var pl = section.Plane;
        Vector3d MapP(double s, Vector3d x) { var q = pl.ToLocal2(x); var f = Fr(s); return f.O + f.U * q.X + f.V * q.Y; }
        Vector3d MapD(double s, Vector3d d) { var q = pl.ToLocal2(pl.Origin + d); var f = Fr(s); return f.U * q.X + f.V * q.Y; }
        double len = 0; for (int i = 0; i < NT; i++) len += (P[i + 1] - P[i]).Length;
        double kU = len / (s1 - s0);
        var faces = new List<FDesc>();
        // рельсы — по одной процедурной кривой на точку стыка звеньев (общие у соседних граней)
        var rails = new Dictionary<(long, long, long), ProcCurve3>();
        ProcCurve3 Rail(Vector3d x)
        {
            var key = ((long)Math.Round(x.X * 1e7), (long)Math.Round(x.Y * 1e7), (long)Math.Round(x.Z * 1e7));
            if (!rails.TryGetValue(key, out var rc)) rails[key] = rc = new ProcCurve3(s => MapP(s, x), s0, s1, false, "sweep rail");
            return rc;
        }
        var sLoops = section.Loops.Select(l => new List<Seg>()).ToList(); var eLoops = section.Loops.Select(l => new List<Seg>()).ToList();
        int segIdx = 0; int li = -1;
        foreach (var loop in section.Loops)
        {
            li++;
            var lsegs = new List<ICurve3>();
            foreach (var c in loop.Curves)
            {
                if (c is ArcCurve3 a) { int m = Math.Max(1, (int)Math.Ceiling(Math.Abs(a.A1 - a.A0) / (Math.PI / 2) - 1e-9)); for (int k = 0; k < m; k++) lsegs.Add(new ArcCurve3(a.Frame, a.Radius, a.A0 + (a.A1 - a.A0) * k / m, a.A0 + (a.A1 - a.A0) * (k + 1) / m)); }
                else lsegs.Add(c);
            }
            // ЗВЕНЬЯ СЕЧЕНИЯ: дуги — куски ≤ 90° (лента одним рациональным куском в STEP); у каждого — формула сечения в рамке
            foreach (var c in lsegs)
            {
                var A = c.PointAt(c.T0); var B = c.PointAt(c.T1);
                Func<double, SweepSection> gen;
                if (c is LineCurve3) gen = U => { double s = s0 + U / kU; return SweepSection.Line(MapP(s, A), MapP(s, B)); };
                else
                {
                    var a = (ArcCurve3)c; var C0 = a.Frame.Origin; var e1 = (A - C0).Normalized();
                    var e2 = (a.Frame.U * -Math.Sin(a.A0) + a.Frame.V * Math.Cos(a.A0)) * Math.Sign(a.A1 - a.A0);
                    double phi = Math.Abs(a.A1 - a.A0), rad = a.Radius;
                    gen = U => { double s = s0 + U / kU; return SweepSection.Arc(MapP(s, C0), MapD(s, e1).Normalized(), MapD(s, e2).Normalized(), rad, phi); };
                }
                const int NS = 128;
                var us = Enumerable.Range(0, NS + 1).Select(i => len * i / NS).ToList();
                var surf = new SectionSurface(gen, us, us.Select(gen).ToList(), false, 0, len, null, Math.Max(len / 20000, len / 4096));
                // таблица — представление формулы (как у лент): отход в пятой доле точности модели
                double worst = 0;
                for (int i = 0; i < 400; i++) { double U = len * (i + 0.5) / 400; var ta = surf.SectionAt(U); var ex = surf.ExactSectionAt(U); foreach (double v in new[] { 0.0, 0.5, 1.0 }) worst = Math.Max(worst, (ta.Point(v) - ex.Point(v)).Length); }
                if (worst > Math.Max(1e-8, 0.2 * ToleranceContext.Current.ModelAccuracy)) { why = $"exact sweep: surface table deviates from the formula ({worst:E1} mm)"; return null; }
                var c0 = MapCurve(c, s0); var c1 = MapCurve(c, s1);
                if (c0 == null || c1 == null) { why = "exact sweep: a section curve cannot be moved"; return null; }
                var a0 = MapP(s0, A); var b0 = MapP(s0, B); var a1 = MapP(s1, A); var b1 = MapP(s1, B);
                var fd = new FDesc { Surf = surf, Tag = $"sweep:side[{li}.{segIdx++}]", Touched = false };
                fd.Loops.Add(new List<Seg>
                {
                    new() { Curve = c0, A = a0, B = b0 },
                    new() { Curve = Rail(B), A = b0, B = b1 },
                    new() { Curve = c1, A = b1, B = a1 },
                    new() { Curve = Rail(A), A = a1, B = a0 },
                });
                // сторона: наружу — от области сечения (контуры нормализованы: правая нормаль звена — наружу)
                double vm = 0.5; var mid = surf.PointAt(new Vector2d(len / 2, vm));
                var tg2 = pl.ToLocal2(c.PointAt((c.T0 + c.T1) / 2 + 1e-6 * (c.T1 - c.T0))) - pl.ToLocal2(c.PointAt((c.T0 + c.T1) / 2 - 1e-6 * (c.T1 - c.T0)));
                var out2 = new Vector2d(tg2.Y, -tg2.X).Normalized();
                var fm = Fr(s0 + len / 2 / kU); var out3 = fm.U * out2.X + fm.V * out2.Y;
                fd.Rev = Vector3d.Dot(surf.NormalAt(surf.ProjectToUV(mid)), out3) < 0;
                fd.Hint = mid;
                faces.Add(fd);
                sLoops[li].Add(new Seg { Curve = c0, A = a0, B = b0 });
                eLoops[li].Add(new Seg { Curve = c1, A = a1, B = b1 });
            }
        }
        // торцы: начало — наружу против касательной, конец — по ней
        var fs0 = Fr(s0); var fs1 = Fr(s1); var t0 = Tan(s0); var t1 = Tan(s1);
        var cap0 = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(fs0.O, -t0)), Rev = false, Tag = "cap:start", Touched = true, N = -t0 };
        var cap1 = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(fs1.O, t1)), Rev = false, Tag = "cap:end", Touched = true, N = t1 };
        foreach (var l in sLoops) cap0.Loops.Add(l);
        foreach (var l in eLoops) cap1.Loops.Add(l);
        faces.Add(cap0); faces.Add(cap1);
        var res = FinishDirect(faces, new List<FDesc>(), "sweep.exact", ref why);
        if (res != null) Interlocked.Increment(ref ExactSweeps);
        return res;

        ICurve3? MapCurve(ICurve3 c, double s)
        {
            var f = Fr(s); var to = new Frame3(f.O, f.U, f.V);
            switch (c)
            {
                case LineCurve3 l: return new LineCurve3(MapP(s, l.A), MapP(s, l.B));
                case ArcCurve3 a:
                {
                    var o = MapP(s, a.Frame.Origin); var u = MapD(s, a.Frame.U); var v = MapD(s, a.Frame.V);
                    return new ArcCurve3(new Frame3(o, u, v), a.Radius, a.A0, a.A1);
                }
            }
            return null;
        }
    }

    static Frame3 Align(Frame3 src, Vector3d origin, Vector3d newNormal)
    {
        var axis = Vector3d.Cross(src.W, newNormal); double sn = axis.Length;
        if (sn < 1e-12) return Vector3d.Dot(src.W, newNormal) >= 0 ? new Frame3(origin, src.U, src.V) : new Frame3(origin, src.U, -src.V);
        axis /= sn; double ang = Math.Atan2(sn, Vector3d.Dot(src.W, newNormal));
        return new Frame3(origin, RevolvedSurface.RotateAbout(src.U, Vector3d.Zero, axis, ang), RevolvedSurface.RotateAbout(src.V, Vector3d.Zero, axis, ang));
    }
}
