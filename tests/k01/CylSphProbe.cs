using System.Diagnostics;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Features;
using Parametric3D.Kernel.Surfaces;

static partial class Program
{
    /// <summary>17:31: FreeForm — цилиндр ∪ шар, скругление / фаска ребра пересечения и верхнего ребра цилиндра: время и исход.</summary>
    static void CylSphProbe(string[] args)
    {
        var cases = new (string Name, Vector3d CylAt, Vector3d Axis)[]
        {
            ("coaxial", new(0, 0, 5), Vector3d.UnitZ),
            ("offset", new(8, 0, 5), Vector3d.UnitZ),
            ("inside", new(4, 0, 5), Vector3d.UnitZ),
            ("warm", new(3, 0, 2), Vector3d.UnitZ),
            ("tilted", new(5, 0, 5), new Vector3d(0.3, 0, 1).Normalized()),
        };
        if (Environment.GetEnvironmentVariable("P3D_CSDBG") == "1") DirectRound.Debug = m => Console.WriteLine("      dr: " + m);
        if (Environment.GetEnvironmentVariable("P3D_CSPROF") == "1") DirectRound.Prof = m => Console.WriteLine("      prof: " + m);
        foreach (var (name, at, ax) in cases)
        {
            if (args.Length > 1 && !name.StartsWith(args[1])) continue;
            var u = Vector3d.Cross(Vector3d.UnitY, ax).Normalized(); if (u.LengthSquared < 0.5) u = Vector3d.UnitX;
            var v = Vector3d.Cross(ax, u);
            bool wm = name == "warm";
            var cyl = FFPrimitiveFeature.Make(FFPrimitive.Cylinder, new Frame3(at, u, v), wm ? 6 : 15, wm ? 16 : 40, 0);
            var sph = FFPrimitiveFeature.Make(FFPrimitive.Sphere, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), wm ? 8 : 20, 0, 0);
            var w = new List<string>();
            var sw = Stopwatch.StartNew();
            var list = new List<TSolid> { cyl };
            BooleanOp.Unite(list, sph, w);
            Console.WriteLine($"{name}: union {sw.ElapsedMilliseconds} ms, bodies {list.Count}, faces {list[0].Faces.Count()}, guarantee {list[0].Guarantee}, exact {list[0].Exact != null}/{list[0].ExactAuthoritative}");
            foreach (var f in list[0].Faces) Console.WriteLine($"   face {f.Id} {f.Surface.GetType().Name}{(f.Surface is FacetedSurface fs ? " src " + fs.Source?.GetType().Name : "")}");
            var body = list[0];
            foreach (var e in body.Edges)
            {
                var fs2 = e.CoEdges.Select(c => c.Loop.Face.Surface.GetType().Name + (c.Loop.Face.Surface is FacetedSurface f3 ? "/" + f3.Source?.GetType().Name : "")).ToList();
                Console.WriteLine($"   edge {e.Id} {e.Curve.GetType().Name} len {e.Curve.Length:F2} faces [{string.Join(", ", fs2)}]");
            }
            var edgeList = body.Edges.ToList();
            for (int ei = 0; ei < edgeList.Count; ei++)
            {
                var e = edgeList[ei];
                foreach (var (op, r) in new[] { ("round", name == "warm" ? 1.0 : 5.0), ("chamfer", name == "warm" ? 0.8 : 3.0), ("chamfer", 1.0), ("round", 2.0), ("round", 0.5), ("chamfer", 0.5) })
                {
                    var b = new List<TSolid> { TopologyClone.Deep(body) };
                    var ww = new List<string>();
                    var t = Stopwatch.StartNew();
                    string res;
                    try
                    {
                        var er = new List<EdgeRef> { EdgeRef.Of(b[0].Edges.ElementAt(ei)) };
                        if (op == "round") FilletOp.RoundEdges(b, er, r, ww); else FilletOp.ChamferEdges(b, er, r, r, ww);
                        res = $"ok faces {b[0].Faces.Count()} guarantee {b[0].Guarantee} valid {SolidValidator.Validate(b[0]).Summary()}";
                    }
                    catch (Exception ex) { res = "FAIL " + ex.Message + (Environment.GetEnvironmentVariable("P3D_CSDBG") == "1" ? "\n" + ex.StackTrace : ""); }
                    Console.WriteLine($"   {op} edge {e.Id} ({e.Curve.GetType().Name}): {t.ElapsedMilliseconds} ms — {res}{(ww.Count > 0 ? " | " + string.Join("; ", ww.Take(3)) : "")}");
                }
            }
        }
    }
}
