using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_SWEEPEXACT=0: протяжка по общей траектории — прежним многогранным путём.</summary>
    internal static readonly bool SweepExactOff = Environment.GetEnvironmentVariable("P3D_SWEEPEXACT") == "0";
    /// <summary>Мутация P3D_SWEEPCLOSED=0: замкнутая гладкая траектория — прежним путём.</summary>
    static readonly bool ClosedSweepOff = Environment.GetEnvironmentVariable("P3D_SWEEPCLOSED") == "0";
    public static long ExactSweeps;

    /// <summary>
    /// KRN-07 (07.3): ТОЧНАЯ ПРОТЯЖКА ПО ОБЩЕЙ ГЛАДКОЙ ТРАЕКТОРИИ (сплайн, кривая Безье, эллипс — не отрезок и не дуга, у тех
    /// свои точные пути). Рамка вдоль траектории — БЕЗ ВРАЩЕНИЯ (rotation-minimizing): r' = −(r·T')T; таблица двойным
    /// отражением (Ван и др., 2008) на 2048 шагах и кубическая Эрмита с точной производной r' в узлах, с подгонкой под
    /// касательную. Закрутка — поворот рамки вокруг T на угол, линейный по параметру. Звено сечения (отрезок, дуга — дуги
    /// делятся на куски ≤ 90°) заметает грань SectionSurface по формуле сечения (отрезок/дуга в рамке траектории); рельсы —
    /// процедурные кривые концов звеньев той же формулы, торцы — плоские грани по точным перенесённым сечениям. Тело
    /// собирается и проверяется как у прямых сопряжений (годность, объём по формуле поверхности, объём по фасетам).
    /// Замкнутая траектория — без торцов, рамка замыкается поправкой закрутки, грани — по кускам. null — отказ (причина в why).
    /// </summary>
    internal static TSolid? ExactSweep(Profile section, ICurve3 path, double frac, double twistRad, ref string? why) =>
        ExactSweep(section, path, frac, twistRad, false, ref why);

    /// <summary>
    /// keepOrientation — закон ориентации «сечение не поворачивается» (перенос без поворота: сечения — параллельные копии
    /// исходного, плоскость сечения остаётся своей; как «Keep normal constant»). Закрутка тогда не задаётся.
    /// </summary>
    internal static TSolid? ExactSweep(Profile section, ICurve3 path, double frac, double twistRad, bool keepOrientation, ref string? why)
    {
        if (keepOrientation && Math.Abs(twistRad) > 1e-12) { why = "exact sweep: a twist with the kept orientation"; return null; }
        if (SweepExactOff) { why = "exact sweep is off"; return null; }
        // ЗАМКНУТАЯ траектория (целиком): торцов нет; рамка без вращения по возвращении повёрнута на δ вокруг касательной —
        // поправка −δ линейно по траектории (рамка замыкается); тело — куски по u (не меньше трёх) (у замкнутой ленты так же: шов без
        // грани-шва), сечения на стыках кусков — общие рёбра. Закрутка замкнутой — только кратная 360°.
        bool closedP = path.IsClosed && frac >= 1 - 1e-12;
        if (closedP && ClosedSweepOff) { why = "exact sweep: closed path"; return null; }
        if (closedP && Math.Abs(Math.IEEERemainder(twistRad, 2 * Math.PI)) > 1e-9) { why = "exact sweep: a closed path needs a twist of whole turns"; return null; }
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
        double corr = 0;
        if (closedP)
        {
            // угол от перенесённой рамки к начальной вокруг касательной в конце (= в начале)
            var rs = R[0] - T[NT] * Vector3d.Dot(R[0], T[NT]); rs.Normalize();
            corr = Math.Atan2(Vector3d.Dot(Vector3d.Cross(R[NT], rs), T[NT]), Vector3d.Dot(R[NT], rs));
            if ((P[0] - P[NT]).Length > 1e-9 * Math.Max(1, P[0].Length) || (T[0] - T[NT]).Length > 1e-7) { why = "exact sweep: the closed path is not smooth at its start"; return null; }
        }
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
            if (keepOrientation) return (path.PointAt(s), section.Plane.U, section.Plane.V);
            var t = Tan(s); var r = RAt(s); double ph = (twistRad + corr) * (s - s0) / (s1 - s0);
            var u = r * Math.Cos(ph) + Vector3d.Cross(t, r) * Math.Sin(ph);
            return (path.PointAt(s), u, Vector3d.Cross(t, u));
        }
        var pl = section.Plane;
        Vector3d MapP(double s, Vector3d x) { var q = pl.ToLocal2(x); var f = Fr(s); return f.O + f.U * q.X + f.V * q.Y; }
        Vector3d MapD(double s, Vector3d d) { var q = pl.ToLocal2(pl.Origin + d); var f = Fr(s); return f.U * q.X + f.V * q.Y; }
        double len = 0; for (int i = 0; i < NT; i++) len += (P[i + 1] - P[i]).Length;
        double kU = len / (s1 - s0);
        var faces = new List<FDesc>();
        // промежутки по траектории: открытая — один, замкнутая — две половины (шов без грани-шва)
        // у двух половин рельсы пары соединяли бы одни и те же две вершины (рёбра неразличимы по концам)
        // замкнутая — куски не короче трёх и с поворотом касательной ≤ 90° на кусок (у трети эллипса в 120° поворота разбиение
        // грани по контуру давало вывернутые треугольники на внутренней стороне)
        double turn = 0; for (int i = 0; i < NT; i++) turn += Math.Acos(Math.Clamp(Vector3d.Dot(T[i], T[i + 1]), -1, 1));
        int nSp = Math.Max(3, (int)Math.Ceiling(turn / (Math.PI / 2) - 1e-9));
        var spans = closedP ? Enumerable.Range(0, nSp).Select(k => (s0 + (s1 - s0) * k / nSp, k == nSp - 1 ? s1 : s0 + (s1 - s0) * (k + 1) / nSp)).ToArray() : new[] { (s0, s1) };
        // рельсы — по одной процедурной кривой на точку стыка звеньев и промежуток (общие у соседних граней)
        var rails = new Dictionary<(long, long, long, int), ProcCurve3>();
        ProcCurve3 Rail(Vector3d x, int sp)
        {
            var key = ((long)Math.Round(x.X * 1e7), (long)Math.Round(x.Y * 1e7), (long)Math.Round(x.Z * 1e7), sp);
            var (sa, sb) = spans[sp];
            if (!rails.TryGetValue(key, out var rc)) rails[key] = rc = new ProcCurve3(s => MapP(s, x), sa, sb, false, "sweep rail");
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
                // сечения на краях промежутков: одна кривая на стык (у замкнутой конец = начало)
                var cuts = new List<(ICurve3 C, Vector3d A, Vector3d B)>();
                foreach (var (sa, _) in spans) { var cc = MapCurve(c, sa); if (cc == null) { why = "exact sweep: a section curve cannot be moved"; return null; } cuts.Add((cc, MapP(sa, A), MapP(sa, B))); }
                if (closedP) cuts.Add(cuts[0]);
                else { var cc = MapCurve(c, s1); if (cc == null) { why = "exact sweep: a section curve cannot be moved"; return null; } cuts.Add((cc, MapP(s1, A), MapP(s1, B))); }
                for (int sp = 0; sp < spans.Length; sp++)
                {
                    var (sa, sb) = spans[sp]; double Ua = (sa - s0) * kU, Ub = (sb - s0) * kU;
                    const int NS = 128;
                    var us = Enumerable.Range(0, NS + 1).Select(i => Ua + (Ub - Ua) * i / NS).ToList();
                    var surf = new SectionSurface(gen, us, us.Select(gen).ToList(), false, Ua, Ub, null, Math.Max((Ub - Ua) / 20000, (Ub - Ua) / 4096));
                    // таблица — представление формулы (как у лент): отход в пятой доле точности модели
                    double worst = 0;
                    for (int i = 0; i < 400; i++) { double U = Ua + (Ub - Ua) * (i + 0.5) / 400; var ta = surf.SectionAt(U); var ex = surf.ExactSectionAt(U); foreach (double v in new[] { 0.0, 0.5, 1.0 }) worst = Math.Max(worst, (ta.Point(v) - ex.Point(v)).Length); }
                    if (worst > Math.Max(1e-8, 0.2 * ToleranceContext.Current.ModelAccuracy)) { why = $"exact sweep: surface table deviates from the formula ({worst:E1} mm)"; return null; }
                    var (c0, a0, b0) = cuts[sp]; var (c1, a1, b1) = cuts[sp + 1];
                    var fd = new FDesc { Surf = surf, Tag = $"sweep:side[{li}.{segIdx}]" + (spans.Length > 1 ? $":{sp}" : ""), Touched = false };
                    fd.Loops.Add(new List<Seg>
                    {
                        new() { Curve = c0, A = a0, B = b0 },
                        new() { Curve = Rail(B, sp), A = b0, B = b1 },
                        new() { Curve = c1, A = b1, B = a1 },
                        new() { Curve = Rail(A, sp), A = a1, B = a0 },
                    });
                    // сторона: наружу — от области сечения (контуры нормализованы: правая нормаль звена — наружу)
                    double Um = (Ua + Ub) / 2; var mid = surf.PointAt(new Vector2d(Um, 0.5));
                    var tg2 = pl.ToLocal2(c.PointAt((c.T0 + c.T1) / 2 + 1e-6 * (c.T1 - c.T0))) - pl.ToLocal2(c.PointAt((c.T0 + c.T1) / 2 - 1e-6 * (c.T1 - c.T0)));
                    var out2 = new Vector2d(tg2.Y, -tg2.X).Normalized();
                    var fm = Fr(s0 + Um / kU); var out3 = fm.U * out2.X + fm.V * out2.Y;
                    fd.Rev = Vector3d.Dot(surf.NormalAt(surf.ProjectToUV(mid)), out3) < 0;
                    fd.Hint = mid;
                    faces.Add(fd);
                }
                segIdx++;
                if (!closedP)
                {
                    sLoops[li].Add(new Seg { Curve = cuts[0].C, A = cuts[0].A, B = cuts[0].B });
                    eLoops[li].Add(new Seg { Curve = cuts[^1].C, A = cuts[^1].A, B = cuts[^1].B });
                }
            }
        }
        if (!closedP)
        {
            // торцы: начало — наружу против касательной, конец — по ней
            var fs0 = Fr(s0); var fs1 = Fr(s1); var t0 = Tan(s0); var t1 = Tan(s1);
            if (keepOrientation)
            {
                // торцы — в плоскости сечения (она не поворачивается); наружу — против хода траектории в начале, по ходу в конце
                var W = section.Plane.W;
                if (Math.Abs(Vector3d.Dot(W, t0)) < 0.05 || Math.Abs(Vector3d.Dot(W, t1)) < 0.05) { why = "exact sweep: the path runs along the section plane"; return null; }
                t0 = W * Math.Sign(Vector3d.Dot(W, t0)); t1 = W * Math.Sign(Vector3d.Dot(W, t1));
            }
            var cap0 = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(fs0.O, -t0)), Rev = false, Tag = "cap:start", Touched = true, N = -t0 };
            var cap1 = new FDesc { Surf = new PlaneSurface(Frame3.FromNormal(fs1.O, t1)), Rev = false, Tag = "cap:end", Touched = true, N = t1 };
            foreach (var l in sLoops) cap0.Loops.Add(l);
            foreach (var l in eLoops) cap1.Loops.Add(l);
            faces.Add(cap0); faces.Add(cap1);
        }
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
