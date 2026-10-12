using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Geometry;

static partial class Program
{
    /// <summary>04.7 замер: две близкие ветви — наклонная плоскость чуть ниже касания к верху тора (две замкнутые кривые рядом).</summary>
    static void UniqProbe()
    {
        Ssi.TestFixedStep = Environment.GetEnvironmentVariable("FIXEDSTEP") == "1";
        foreach (double eps in (Environment.GetEnvironmentVariable("UNIQEPS") ?? "1e-2,3e-3,1e-3,1e-4").Split(",").Select(double.Parse))
            foreach (double tilt in (Environment.GetEnvironmentVariable("UNIQTILT") ?? "1e-4,1e-3,0.1").Split(",").Select(double.Parse))
            {
                var n = new Vector3d(Math.Sin(tilt), 0, Math.Cos(tilt));
                var tor = new Ssi.Torus(Vector3d.Zero, Vector3d.UnitZ, 10, 2);
                // плоскость с нормалью n, касающаяся тора сверху (опорная точка тора по n), опущенная на eps
                double support = 10 * Math.Sin(tilt) + 2;   // max n·x по тору
                var pl = new Ssi.Plane(n * (support - eps), n);
                var r = Ssi.Intersect(pl, tor, Vector3d.Zero, 13);
                double len = r.Curves.Sum(c => c.Length);
                Console.WriteLine($"UNIQ eps {eps:G2} tilt {tilt}: kind {r.Kind} curves {r.Curves.Count} total length {len:F4} ref {(tilt < 0.01 ? TorusPlaneRefLength(tilt, support - eps, 20000) : double.NaN):F4} uncertified {r.Uncertified} types {string.Join(",", r.Curves.Select(c => c.GetType().Name).Distinct())}");
            }
    }
}

static partial class Program
{
    /// <summary>Тор (ось Z, центр 0) как рациональная поверхность NURBS 2×2 (9×9 опорных точек, точная).</summary>
    internal static Parametric3D.Kernel.Surfaces.NurbsSurface NurbsTorus(double R, double r)
    {
        double s = Math.Sqrt(0.5);
        var cx = new[] { 1.0, 1, 0, -1, -1, -1, 0, 1, 1 }; var cy = new[] { 0.0, 1, 1, 1, 0, -1, -1, -1, 0 };
        var wc = new[] { 1, s, 1, s, 1, s, 1, s, 1 };
        var kn = new[] { 0, 0, 0, 0.25, 0.25, 0.5, 0.5, 0.75, 0.75, 1, 1, 1 };
        var cp = new Vector3d[9, 9]; var w = new double[9, 9];
        for (int i = 0; i < 9; i++)
            for (int j = 0; j < 9; j++)
            {
                double rho = R + r * cx[j], z = r * cy[j];
                cp[i, j] = new Vector3d(rho * cx[i], rho * cy[i], z); w[i, j] = wc[i] * wc[j];
            }
        return new Parametric3D.Kernel.Surfaces.NurbsSurface(cp, 2, 2, w, kn, kn);
    }

    /// <summary>Независимая длина пересечения тора (R10, r2) с плоскостью (нормаль (sin T, 0, cos T), высота d): корни по φ.</summary>
    internal static double TorusPlaneRefLength(double tilt, double d, int N = 200000)
    {
        double refLen = 0; var prev = new Vector3d?[2];
        for (int i = 0; i <= N; i++)
        {
            double th = 2 * Math.PI * i / N;
            double G(double ph) => Math.Sin(tilt) * (10 + 2 * Math.Cos(ph)) * Math.Cos(th) + Math.Cos(tilt) * 2 * Math.Sin(ph) - d;
            double lo = 0, hi = Math.PI; for (int k = 0; k < 100; k++) { double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3; if (G(m1) < G(m2)) lo = m1; else hi = m2; }
            double pm = (lo + hi) / 2;
            for (int side = 0; side < 2; side++)
            {
                double a = side == 0 ? 0 : pm, b = side == 0 ? pm : Math.PI;
                for (int k = 0; k < 80; k++) { double m = (a + b) / 2; if ((G(m) > 0) == (side == 0)) b = m; else a = m; }
                double ph = (a + b) / 2; var p = new Vector3d((10 + 2 * Math.Cos(ph)) * Math.Cos(th), (10 + 2 * Math.Cos(ph)) * Math.Sin(th), 2 * Math.Sin(ph));
                if (prev[side] is { } q) refLen += (p - q).Length; prev[side] = p;
            }
        }
        return refLen;
    }

    static void NurbsUniqProbe()
    {
        var S = NurbsTorus(10, 2);
        foreach (double eps in (Environment.GetEnvironmentVariable("UNIQEPS") ?? "1e-2,3e-3,2e-3,8e-4,5e-4").Split(",").Select(double.Parse))
            foreach (double tilt in (Environment.GetEnvironmentVariable("UNIQTILT") ?? "3e-5,1e-4,0.1").Split(",").Select(double.Parse))
            {
                double az = double.Parse(Environment.GetEnvironmentVariable("UNIQAZ") ?? "0.785");
                var n = new Vector3d(Math.Sin(tilt) * Math.Cos(az), Math.Sin(tilt) * Math.Sin(az), Math.Cos(tilt)); double d = 10 * Math.Sin(tilt) + 2 - eps;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pl = new Parametric3D.Kernel.Geometry.Ssi.Plane(n * d, n);
                var r = Parametric3D.Kernel.Geometry.NurbsSsi.Intersect(S, 0, 1, 0, 1, pl, 13);
                double len = r.Curves.Sum(c => c.Length);
                var ra = Parametric3D.Kernel.Geometry.Ssi.Intersect(pl, new Parametric3D.Kernel.Geometry.Ssi.Torus(Vector3d.Zero, Vector3d.UnitZ, 10, 2), Vector3d.Zero, 13);
                double refLen = ra.Uncertified == null ? ra.Curves.Sum(c => c.Length) : double.NaN;
                Console.WriteLine($"eps {eps} tilt {tilt}: curves {r.Curves.Count} len {len:F4} ref {refLen:F4} rel {Math.Abs(len - refLen) / refLen:G3} {sw.ElapsedMilliseconds} ms unc {r.Uncertified}");
            }
    }
}

static partial class Program
{
    static void DirBoundProbe()
    {
        var S = NurbsTorus(10, 2);
        var bez = Parametric3D.Kernel.Surfaces.NurbsTools.Beziers(S);
        var q = new Vector2d(0.14604675265437875, 0.23441849646534468);
        var g = new Vector3d(Math.Sin(0.1) * Math.Cos(0.785), Math.Sin(0.1) * Math.Sin(0.785), Math.Cos(0.1));
        S.Derivs(q, out _, out var su, out var sv); double Lu = su.Length, Lv = sv.Length;
        foreach (double hs in new[] { 0.1, 0.0146, 0.002 })
        {
            var bp = bez.First(b => b.U0 <= q.X && q.X <= b.U1 && b.V0 <= q.Y && q.Y <= b.V1);
            var sub = bp.Sub(q.X - hs / Lu, q.X + hs / Lu, q.Y - hs / Lv, q.Y + hs / Lv);
            foreach (var w in new[] { new Vector2d(1 / Lu, 0), new Vector2d(0, 1 / Lv) })
            {
                var b = sub.DirBound(g, w); var db = sub.DerivBounds();
                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = 0; i <= 20; i++) for (int j = 0; j <= 20; j++)
                {
                    var p = new Vector2d(q.X + hs / Lu * (2.0 * i / 20 - 1), q.Y + hs / Lv * (2.0 * j / 20 - 1));
                    S.Derivs(p, out _, out var a, out var c); double val = Vector3d.Dot(g, a * w.X + c * w.Y);
                    lo = Math.Min(lo, val); hi = Math.Max(hi, val);
                }
                Console.WriteLine($"hs {hs} w {w}: true [{lo:G5}, {hi:G5}] bound [{b?.Lo:G5}, {b?.Hi:G5}] box {db?.Lo} {db?.Hi}");
            }
        }
    }
}

static partial class Program
{
    /// <summary>Плоскость (точка, нормаль) как билинейная поверхность NURBS — квадрат со стороной 2·half вокруг точки.</summary>
    internal static Parametric3D.Kernel.Surfaces.NurbsSurface NurbsPlane(Vector3d p, Vector3d n, double half)
    {
        n = n.Normalized(); var a = Vector3d.Cross(n, Math.Abs(n.Z) < 0.9 ? Vector3d.UnitZ : Vector3d.UnitX).Normalized(); var b = Vector3d.Cross(n, a);
        var cp = new Vector3d[2, 2];
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) cp[i, j] = p + a * half * (2 * i - 1) + b * half * (2 * j - 1);
        return new Parametric3D.Kernel.Surfaces.NurbsSurface(cp, 1, 1);
    }

    static void NurbsNNUniqProbe()
    {
        var S = NurbsTorus(10, 2);
        var full = new Parametric3D.Kernel.Geometry.NurbsNurbsSsi.Rect(0, 1, 0, 1);
        if (Environment.GetEnvironmentVariable("NNDBG") == "1") Parametric3D.Kernel.Geometry.NurbsNurbsSsi.Debug = m => Console.Error.WriteLine(m);
        foreach (double eps in (Environment.GetEnvironmentVariable("UNIQEPS") ?? "1e-2,3e-3,2e-3,8e-4,5e-4").Split(",").Select(double.Parse))
            foreach (double tilt in (Environment.GetEnvironmentVariable("UNIQTILT") ?? "3e-5,1e-4,0.1").Split(",").Select(double.Parse))
            {
                double az = 0.785;
                var n = new Vector3d(Math.Sin(tilt) * Math.Cos(az), Math.Sin(tilt) * Math.Sin(az), Math.Cos(tilt)); double d = 10 * Math.Sin(tilt) + 2 - eps;
                var P = NurbsPlane(n * d, n, 15);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = Parametric3D.Kernel.Geometry.NurbsNurbsSsi.Intersect(S, full, P, full, 13);
                double len = r.Curves.Sum(c => c.Length);
                double refLen = tilt < 0.01 ? TorusPlaneRefLength(tilt, d, 50000) : double.NaN;
                Console.WriteLine($"NN eps {eps} tilt {tilt}: curves {r.Curves.Count} len {len:F5} ref {refLen:F5} rel {Math.Abs(len - refLen) / refLen:G3} {sw.ElapsedMilliseconds} ms unc {r.Uncertified}");
            }
    }
}

static partial class Program
{
    /// <summary>04.2 замер: рёбра — прослеженные кривые (тор × цилиндр), затем вторая булева режет их плоскостью/цилиндром.</summary>
    static void TracedEventsProbe()
    {
        var c = new Vector3d(1, 2, 3);
        var cases = new (string Name, Func<TSolid> Second, BooleanOp.BooleanKind K)[]
        {
            ("slab across the hole", () => Box(c.X + 9.6, c.Y - 20, c.Z - 10, 0.8, 40, 20), BooleanOp.BooleanKind.Subtract),
            ("slab off-centre", () => Box(c.X + 10.3, c.Y - 20, c.Z - 10, 3, 40, 20), BooleanOp.BooleanKind.Subtract),
            ("thin cylinder across", () => { var q = c + new Vector3d(10, -20, 0.7); return TransformOp.Copy(PrimitiveOp.Cylinder(q, 0.4, 40), TransformOp.Rotate(q, Vector3d.UnitX, -90)); }, BooleanOp.BooleanKind.Subtract),
            ("upper half", () => Box(c.X - 20, c.Y - 20, c.Z + 0.5, 40, 40, 10), BooleanOp.BooleanKind.Intersect),
            ("cap 1e-3 below the top", () => Box(c.X - 20, c.Y - 20, c.Z + 3 - 1e-3, 40, 40, 10), BooleanOp.BooleanKind.Subtract),
            ("cap 1e-4 below the top", () => Box(c.X - 20, c.Y - 20, c.Z + 3 - 1e-4, 40, 40, 10), BooleanOp.BooleanKind.Subtract),
            ("cap 1e-5 below the top", () => Box(c.X - 20, c.Y - 20, c.Z + 3 - 1e-5, 40, 40, 10), BooleanOp.BooleanKind.Subtract),
            ("cap 1e-6 below the top", () => Box(c.X - 20, c.Y - 20, c.Z + 3 - 1e-6, 40, 40, 10), BooleanOp.BooleanKind.Subtract),
        };
        foreach (var (name, mk, k) in cases)
        {
            var t = PrimitiveOp.Torus(c, 10, 3);
            var bl = new List<TSolid> { t }; var w = new List<string>();
            BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(c + new Vector3d(10, 0, -10), 1.5, 20), w);
            var first = bl[0];
            long tc0 = Parametric3D.Kernel.Analytic.SsiEvents.TracedCertified, tu0 = Parametric3D.Kernel.Analytic.SsiEvents.TracedUncertain;
            var w2 = new List<string>(); var sw = System.Diagnostics.Stopwatch.StartNew();
            if (k == BooleanOp.BooleanKind.Subtract) BooleanOp.Subtract(bl, mk(), w2); else BooleanOp.Intersect(bl, mk(), w2);
            var b = bl[0];
            Console.WriteLine($"{name}: valid {SolidValidator.Validate(b).IsValid} first {first.Guarantee} edges traced {first.Edges.Count(e => e.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3)}; result {b.Guarantee} V {Parametric3D.Kernel.Topology.AnalyticVolume.Of(b)} {sw.ElapsedMilliseconds} ms; traced events certified {Parametric3D.Kernel.Analytic.SsiEvents.TracedCertified - tc0} uncertain {Parametric3D.Kernel.Analytic.SsiEvents.TracedUncertain - tu0} | {string.Join(" | ", w2.Take(2))}");
        }
    }
}

static partial class Program
{
    static void TracedEventsCount()
    {
        var c = new Vector3d(1, 2, 3);
        var bl = new List<TSolid> { PrimitiveOp.Torus(c, 10, 3) };
        BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(c + new Vector3d(10, 0, -10), 1.5, 20), new List<string>());
        var edges = bl[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.IntersectionCurve3).ToList();
        foreach (double d in new[] { 1e-2, 1e-3, 1e-4, 1e-5, 1e-6, 1e-7, 1e-8 })
        {
            var pl = new Ssi.Plane(c + new Vector3d(0, 0, 3 - d), Vector3d.UnitZ);
            var counts = edges.Select(e => { var l = Parametric3D.Kernel.Analytic.SsiEvents.CurveSurface(e.Curve!, pl, out _); return $"{l.Count}{(Parametric3D.Kernel.Analytic.SsiEvents.LastUncertain ? "?" : "")}"; });
            Console.WriteLine($"δ {d}: events per traced edge {string.Join(",", counts)}");
        }
    }
}

static partial class Program
{
    static void RigorProbe()
    {
        if (Environment.GetEnvironmentVariable("KDBG") == "1") Parametric3D.Kernel.Geometry.Krawczyk.Dbg = m => Console.WriteLine("  K " + m);
        // четверть цилиндра R10 (рациональная) и отрезок поперёк
        double s = Math.Sqrt(0.5);
        var cp = new Vector3d[3, 2]; var w = new double[3, 2];
        var xy = new[] { new Vector3d(10, 0, 0), new Vector3d(10, 10, 0), new Vector3d(0, 10, 0) }; var wc = new[] { 1, s, 1 };
        for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++) { cp[i, j] = xy[i] + new Vector3d(0, 0, 20 * j); w[i, j] = wc[i]; }
        var S = new Parametric3D.Kernel.Surfaces.NurbsSurface(cp, 2, 1, w);
        foreach (var (a, b) in new[] { (new Vector3d(0, 0, 5), new Vector3d(20, 20, 5)), (new Vector3d(-5, 3, 1), new Vector3d(15, 9, 17)), (new Vector3d(9.9, -5, 2), new Vector3d(9.9, 15, 2)) })
        {
            var L = new Parametric3D.Kernel.Curves.LineCurve3(a, b);
            var k = Parametric3D.Kernel.Analytic.RigorousEvents.CurveNurbs(L, S, 0, 1, 0, 1);
            // эталон: цилиндр x²+y²=100, z∈[0,20], x,y ≥ 0
            var d = b - a; double A = d.X * d.X + d.Y * d.Y, B = 2 * (a.X * d.X + a.Y * d.Y), C = a.X * a.X + a.Y * a.Y - 100, disc = B * B - 4 * A * C;
            var want = new List<Vector3d>();
            if (disc >= 0) foreach (var t in new[] { (-B - Math.Sqrt(disc)) / (2 * A), (-B + Math.Sqrt(disc)) / (2 * A) }) { var p = a + d * t; if (t >= 0 && t <= 1 && p.X >= -1e-12 && p.Y >= -1e-12 && p.Z >= 0 && p.Z <= 20) want.Add(p); }
            Console.WriteLine($"line {a}→{b}: rigorous {(k == null ? "null" : string.Join(" ", k.Select(p => p.ToString("0.######"))))} | want {string.Join(" ", want.Select(p => p.ToString("0.######")))}");
        }
    }
}

static partial class Program
{
    static void MergeGuardProbe()
    {
        foreach (double sc in (Environment.GetEnvironmentVariable("CORNER") ?? "5e-6,2e-6,5e-5,1e-3").Split(',').Select(double.Parse))
        {
            if (Environment.GetEnvironmentVariable("SPLITDBG") == "1") Parametric3D.Kernel.Analytic.FaceSplit.Debug = m => Console.WriteLine("SPLIT " + m);
            // угол бруска срезается плоскостью x + y + z = 30 − s (тетраэдр с рёбрами s): новые вершины в s от угла
            var bl0 = new List<TSolid> { Box(0, 0, 0, 10, 10, 10) }; var w0 = new List<string>();
            var n = new Vector3d(1, 1, 1).Normalized();
            // большой брусок, повёрнутый так, что его грань — эта плоскость: ось z бруска → n
            var axis = Vector3d.Cross(Vector3d.UnitZ, n).Normalized(); double ang = Math.Acos(Vector3d.Dot(Vector3d.UnitZ, n)) * 180 / Math.PI;
            var p0 = n * ((30 - sc) / Math.Sqrt(3));                         // точка плоскости на диагонали
            var big = Box(-50, -50, 0, 100, 100, 50);                          // нижняя грань z = 0 → плоскость
            var tool0 = TransformOp.Copy(big, TransformOp.Rotate(Vector3d.Zero, axis, ang));
            var tool = TransformOp.Copy(tool0, TransformOp.Move(p0));
            BooleanOp.AnalyticRouteDebug = m => { if (m.Contains("general")) Console.WriteLine("   ROUTE " + m); };
            BooleanOp.Subtract(bl0, tool, w0);
            BooleanOp.AnalyticRouteDebug = null;
            double want = 1000 - sc * sc * sc / 6;
            Console.WriteLine($"corner s {sc}: bodies {bl0.Count} {string.Join(",", bl0.Select(b => $"{b.Guarantee} V {AnalyticVolume.Of(b):R} (want {want:R}) valid {SolidValidator.Validate(b).IsValid} faces {b.Faces.Count()}"))} | {string.Join(" | ", w0.Take(2))}");
        }
        foreach (double d in new double[0])
        {
            var bl = new List<TSolid> { PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 10, 10) }; var w = new List<string>();
            var tool = TransformOp.Copy(Box(-20, -20, -5, 30 - d, 40, 20), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitZ, 0.0));
            BooleanOp.AnalyticRouteDebug = m => { if (m.Contains("general")) Console.WriteLine("   ROUTE " + m); };
            BooleanOp.Subtract(bl, tool, w);
            BooleanOp.AnalyticRouteDebug = null;
            Console.WriteLine($"δ {d}: bodies {bl.Count} {string.Join(",", bl.Select(b => $"{b.Guarantee} V {AnalyticVolume.Of(b)} valid {SolidValidator.Validate(b).IsValid} faces {b.Faces.Count()}"))} | {string.Join(" | ", w.Take(2))}");
        }
    }
}
