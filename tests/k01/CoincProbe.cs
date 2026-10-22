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

    /// <summary>
    /// k95: вогнутое ребро «торец цилиндра — шар» по радиусам (цилиндр r15 h40 сдвинут на (8, 0, 5), шар r20): точная форма
    /// и объём добавки против Монте-Карло (4·10⁶ точек по формулам). R3 — почти двойное касание (окружность касания шара
    /// скругления с торцом почти касается стенки): события кривой «тор ∩ стенка» с касательной плоскостью тора доказываются
    /// по односторонности (P3D_TRACEDONESIDED=0 — отказ). R 3.09 — у критического радиуса 3.08: отказ называет его.
    /// </summary>
    static void CapSphereChecks()
    {
        TSolid MakeB() { var l = new List<TSolid> { PrimitiveOp.Cylinder(new Vector3d(8, 0, 5), 15, 40) }; BooleanOp.Unite(l, PrimitiveOp.Sphere(Vector3d.Zero, 20), new List<string>()); return l[0]; }
        (TSolid? Body, string Err, double Added) Round(double r)
        {
            var b = MakeB(); double v0 = AnalyticVolume.Of(b) ?? double.NaN;
            var e = b.Edges.First(x => x.FaceA != null && x.FaceB != null && x.FaceA.Surface.IsPlanar != x.FaceB.Surface.IsPlanar
                && new[] { x.FaceA, x.FaceB }.Any(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) is Parametric3D.Kernel.Geometry.Ssi.Sphere)
                && new[] { x.FaceA, x.FaceB }.Any(f => f.Surface.IsPlanar));
            var bodies = new List<TSolid> { b }; var w = new List<string>();
            try { FilletOp.RoundEdges(bodies, new List<EdgeRef> { EdgeRef.Of(e) }, r, w); }
            catch (Exception ex) { return (null, ex.Message + " | " + string.Join(" | ", w), double.NaN); }
            return (bodies[0], "", (AnalyticVolume.Of(bodies[0]) ?? double.NaN) - v0);
        }
        Check("KRN-ROUND-CAP-SPHERE-RADII", () =>
        {
            long n0 = FilletOp.CapSphereRounds;
            foreach (var (r, mc, tol) in new[] { (2.0, 36.78, 0.6), (3.0, 72.45, 0.8), (8.0, 260.75, 2.0) })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (body, err, added) = Round(r);
                Require(body != null, $"R{r}: refused: {err}");
                Require(body!.Guarantee is GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric, $"R{r}: guarantee {body.Guarantee}");
                Require(Math.Abs(added - mc) < tol, $"R{r}: added {added:0.###} (Monte Carlo {mc} ± {tol})");
                Require(body.Faces.Count() <= 6, $"R{r}: {body.Faces.Count()} faces");
                Console.WriteLine($"  R{r}: added {added:0.###} (MC {mc}), faces {body.Faces.Count()}, {sw.ElapsedMilliseconds} ms");
            }
            Require(FilletOp.CapSphereRounds >= n0 + 3, "not rounded by the filler of revolution");
            var (b2, err2, _) = Round(3.09);
            Require(b2 == null && err2.Contains("critical value 3.08"), "R3.09: expected a refusal naming the critical radius 3.08: " + (b2 == null ? err2 : "built"));
        });
    }
}
