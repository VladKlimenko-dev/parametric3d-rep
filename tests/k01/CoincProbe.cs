using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Разбор (22.10): общая булева с совпадающими поверхностями шара.</summary>
    static void CoincProbe()
    {
        BooleanOp.AnalyticRouteDebug = m => Console.WriteLine("   route: " + m);
        var S = PrimitiveOp.Sphere(Vector3d.Zero, 20);
        var cases = new (string, Func<TSolid>, Func<TSolid>, BooleanOp.BooleanKind)[]
        {
            ("S ∪ (S ∩ box)", () => PrimitiveOp.Sphere(Vector3d.Zero, 20), () => { var l = new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 20) }; BooleanOp.Intersect(l, Box(-5, -30, -30, 40, 60, 60), new List<string>()); return l[0]; }, BooleanOp.BooleanKind.Unite),
            ("(S ∪ box) ∩ (cyl ∪ S)", () => { var l = new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 20) }; BooleanOp.Unite(l, Box(-40, -40, 5, 80, 80, 40), new List<string>()); return l[0]; },
                () => { var l = new List<TSolid> { PrimitiveOp.Cylinder(new Vector3d(8, 0, -30), 15, 75) }; BooleanOp.Unite(l, PrimitiveOp.Sphere(Vector3d.Zero, 20), new List<string>()); return l[0]; }, BooleanOp.BooleanKind.Intersect),
        };
        foreach (var (name, a, b, k) in cases)
        {
            if (Environment.GetEnvironmentVariable("P3D_COINCSKIP") == "1") break;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = BooleanOp.Evaluate(new List<TSolid> { a() }, b(), k, OperationPolicy.Strict, new List<string>());
            Console.WriteLine($"{name}: {r.Outcome} {r.Guarantee} {sw.ElapsedMilliseconds} ms, bodies {r.Bodies.Count}, vol {(r.Bodies.Count > 0 ? AnalyticVolume.Of(r.Bodies[0]) : null)}");
        }
        // вогнутое скругление «торец цилиндра / шар»: B ∪ (F ∩ Cext), F — тело вращения сечения-заполнителя
        {
            TSolid MakeB() { var l = new List<TSolid> { PrimitiveOp.Cylinder(new Vector3d(8, 0, 5), 15, 40) }; BooleanOp.Unite(l, PrimitiveOp.Sphere(Vector3d.Zero, 20), new List<string>()); return l[0]; }
            double R = double.Parse(Environment.GetEnvironmentVariable("P3D_COINCR") ?? "5", System.Globalization.CultureInfo.InvariantCulture), z0 = 5, rs = 20;
            double rhoE = Math.Sqrt(rs * rs - z0 * z0), qz = z0 - R, rhoC = Math.Sqrt((rs + R) * (rs + R) - qz * qz);
            var fr = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);   // (ρ, z) в плоскости XZ
            Vector3d P2(double r, double z) => new Vector3d(r, 0, z);
            var pE = P2(rhoE, z0); var tP = P2(rhoC, z0);
            var Q = P2(rhoC, qz); var tS = Q * (rs / (rs + R));
            double aTp = Math.PI / 2, aTs = Math.Atan2(tS.Z - Q.Z, tS.X - Q.X);
            if (aTs < Math.PI / 2) aTs += 2 * Math.PI;
            var torusArc = new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(Q, Vector3d.UnitX, Vector3d.UnitZ), R, aTp, aTs);
            double aE = Math.Atan2(pE.Z, pE.X), aS = Math.Atan2(tS.Z, tS.X);
            var sphArc = new Parametric3D.Kernel.Curves.ArcCurve3(fr, rs, aS, aE);
            var prof = ProfileBuilder.Build(fr, new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.LineCurve3(pE, tP), torusArc, sphArc });
            Console.WriteLine($"filler profile empty {prof.IsEmpty}");
            var F = RevolveOp.Build(prof, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
            Console.WriteLine($"F volume {AnalyticVolume.Of(F)}");
            var Cext = PrimitiveOp.Cylinder(new Vector3d(8, 0, -30), 15, 40);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = BooleanOp.Evaluate(new List<TSolid> { F }, Cext, BooleanOp.BooleanKind.Intersect, OperationPolicy.Strict, new List<string>());
            Console.WriteLine($"G = F ∩ Cext: {g.Outcome} {g.Guarantee} {sw.ElapsedMilliseconds} ms vol {(g.Bodies.Count > 0 ? AnalyticVolume.Of(g.Bodies[0]) : null)}");
            if (g.Bodies.Count == 1)
            {
                var b = MakeB(); double vb = AnalyticVolume.Of(b) ?? double.NaN;
                sw.Restart();
                if (Environment.GetEnvironmentVariable("P3D_COINCFS") == "1") Parametric3D.Kernel.Analytic.FaceSplit.Debug = s => Console.WriteLine("FS " + s);
                var u = BooleanOp.Evaluate(new List<TSolid> { b }, g.Bodies[0], BooleanOp.BooleanKind.Unite, OperationPolicy.Strict, new List<string>());
                Console.WriteLine($"B ∪ G: {u.Outcome} {u.Guarantee} {sw.ElapsedMilliseconds} ms, vol {(u.Bodies.Count > 0 ? AnalyticVolume.Of(u.Bodies[0]) : null)} (B {vb}), faces {(u.Bodies.Count > 0 ? u.Bodies[0].Faces.Count() : 0)}");
            }
        }
        BooleanOp.AnalyticRouteDebug = null;
    }
}
