using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>07.2: одно и то же отверстие особым путём и общей булевой — объём, гарантия, имена граней, Rim.</summary>
    static List<(string Name, Func<(Part P, HoleFeature H)> Build)> HoleContractCases()
    {
        var down = new Vector3d(0, 0, -1); var top = new Vector3d(0, 0, 30);
        double a15 = 15 * Math.PI / 180; var tilt = new Vector3d(Math.Sin(a15), 0, -Math.Cos(a15));
        return new()
        {
            ("quadric-through", () => HoleInCube(top, down, 8, true)),
            ("quadric-blind", () => HoleInCube(top, down, 8, false)),
            ("quadric-counterbore", () => HoleInCube(top, down, 8, true, kind: HoleKind.Counterbore)),
            ("quadric-countersink", () => HoleInCube(top, down, 8, true, kind: HoleKind.Countersink)),
            ("exact-through (tilted)", () => HoleInCube(top, tilt, 8, true)),
            ("exact-blind (tilted)", () => HoleInCube(top, tilt, 8, false)),
            ("exact-cut-edge", () => HoleInCube(new Vector3d(-28, 0, 30), down, 10, true)),
            ("cross-hole", () =>
            {
                var p = new Part("shaft"); var sk = CircleSketch(p, 0, 0, 0, 10);
                var exf = new ExtrudeFeature { SketchId = sk.Id }; exf.Side1.Distance = new ValueRef(60); p.AddFeature(exf);
                var h = new HoleFeature("Cross") { Direction = Vector3d.UnitY, ThroughAll = true, Diameter = new ValueRef(4) };
                h.Points.Add(new Vector3d(0, -20, 30)); p.AddFeature(h); p.Rebuild(null);
                return (p, h);
            }),
        };
    }

    static void HoleContractProbe()
    {
        foreach (var (name, build) in HoleContractCases())
        {
            var (p1, h1) = build();
            (Part P, HoleFeature H) g;
            using (new KernelContext().WithExtension(ModelSettings.Capture() with { ExactHoles = false }).Enter()) g = build();
            string Roles(Part p, HoleFeature h) => string.Join(", ", p.Bodies.SelectMany(b => b.Faces).GroupBy(f => f.Tag.StartsWith("#" + h.Key + "/") ? "H:" + EdgeRef.Bare(f.Tag) : "other:" + EdgeRef.Bare(f.Tag)).OrderBy(x => x.Key).Select(x => $"{x.Key}×{x.Count()}"));
            int Rim(Part p, HoleFeature h) => new EdgeSelector(EdgeSelectorKind.Rim, h.Key).Resolve(p.Bodies).Count;
            Console.WriteLine($"HC {name}: route {Trace(h1)}");
            Console.WriteLine($"   special: V {p1.Volume:R} {string.Join(",", p1.Bodies.Select(b => b.Guarantee))} faces {p1.Bodies.Sum(b => b.Faces.Count())} rim {Rim(p1, h1)} err {h1.Error}");
            Console.WriteLine($"   general: V {g.P.Volume:R} {string.Join(",", g.P.Bodies.Select(b => b.Guarantee))} faces {g.P.Bodies.Sum(b => b.Faces.Count())} rim {Rim(g.P, g.H)} err {g.H.Error}");
            Console.WriteLine($"   roles special: {Roles(p1, h1)}");
            Console.WriteLine($"   roles general: {Roles(g.P, g.H)}");
        }
    }
}
