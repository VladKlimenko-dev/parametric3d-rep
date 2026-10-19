using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// KRN-03 (03.12): СОГЛАСИЕ КОНТУРОВ С НОРМАЛЬЮ — ПО ТОЧНОЙ ПОВЕРХНОСТИ, БЕЗ ФАСЕТ. Точные тела корпуса (отверстие, бобышка,
    /// вырез шаром, конус, скруглённые брусок и цилиндр, повёрнутые копии) — ни одной несогласованной грани; кривая грань
    /// (цилиндр, конус) на точной поверхности без фасет с обращённой нормалью (Reversed) — ошибка loop-orientation «по точной
    /// поверхности» (прежде у таких граней согласие не проверялось вовсе: проверка шла только по вычислительным фасетам).
    /// Проверяется тело, приведённое к соглашению о полурёбрах (у сырого примитива конуса все контуры — по часовой).
    /// </summary>
    static void NormalProbe()
    {
        foreach (var (n, b) in new[] { ("cone", PrimitiveOp.Cone(Vector3d.Zero, 6, 2, 10)), ("cyl", PrimitiveOp.Cylinder(Vector3d.Zero, 5, 12)), ("sph", PrimitiveOp.Sphere(Vector3d.Zero, 5)) })
        {
            var pb = Parametric3D.Kernel.Query.PreparedBody.For(b);
            Console.WriteLine($"{n} validate: {SolidValidator.Validate(b).Summary()}");
            foreach (var f in b.Faces)
            {
                var ce = f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault(); if (ce == null) { Console.WriteLine($"{n} {f.Tag}: no loops"); continue; }
                var c = ce.Edge.Curve; var p = c.PointAt((c.T0 + c.T1) / 2);
                var src = GeometryAudit.AuthoritativeSurface(f)!; var uv = src.ProjectToUV(p);
                // точка внутри грани: немного вглубь по поверхности
                var nrm = f.NormalAt(p);
                Console.WriteLine($"{n} {f.Tag} {f.Surface.GetType().Name} rev {f.Reversed} p {p} n {nrm} out+ {pb.Classify(p + nrm * 0.01)} out- {pb.Classify(p - nrm * 0.01)}");
            }
        }
    }

    static void Krn03LoopExactChecks()
    {
        Check("KRN03-LOOP-ORIENTATION-EXACT", () =>
        {
            var fails = new List<string>(); var w = new List<string>();
            var rx = TransformOp.Rotate(new Vector3d(5, 3, 1), new Vector3d(0.3, 0.7, 0.2).Normalized(), 41);
            var bodies = new List<(string N, TSolid B)>();
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, PrimitiveOp.Cylinder(new Vector3d(20, 15, -5), 6, 30), w); bodies.Add(("hole", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Unite(l, PrimitiveOp.Cylinder(new Vector3d(20, 15, 20), 6, 8), w); bodies.Add(("boss", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, PrimitiveOp.Sphere(new Vector3d(20, 15, 20), 8), w); bodies.Add(("sphere cut", l[0])); }
            { var l = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(l, PrimitiveOp.Cone(new Vector3d(20, 15, 8), 6, 0, 14), w); bodies.Add(("cone cut", l[0])); }
            bodies.Add(("sphere", PrimitiveOp.Sphere(new Vector3d(1, 2, 3), 7)));
            bodies.Add(("cylinder", PrimitiveOp.Cylinder(Vector3d.Zero, 5, 12)));
            bodies.Add(("cone", PrimitiveOp.Cone(Vector3d.Zero, 6, 2, 10)));
            { var l = new List<TSolid> { Box(0, 0, 0, 30, 20, 10) }; var b0 = l[0]; FilletOp.RoundEdges(l, b0.Edges.Select(EdgeRef.Of).ToList(), 2, w); bodies.Add(("rounded box", l[0])); }
            { var c = PrimitiveOp.Cylinder(Vector3d.Zero, 8, 15); var l = new List<TSolid> { c }; FilletOp.RoundEdges(l, c.Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3).Select(EdgeRef.Of).ToList(), 2, w); bodies.Add(("rounded cylinder", l[0])); }
            foreach (var (n, b) in bodies.ToList()) bodies.Add((n + " rotated", TransformOp.Copy(b, rx)));
            int curved = 0, caught = 0, faceted = 0, facetedCaught = 0;
            foreach (var (name, b0) in bodies)
            {
                // проверка — у тела, приведённого к соглашению о полурёбрах (сырой примитив ещё не приведён)
                var b = TopologyClone.Deep(b0); if (b.TopologyConvention < TopologyConventions.Version) TopologyConventions.Normalize(b);
                var rep = SolidValidator.Validate(b);
                if (rep.Issues.Any(i => i.Code == "loop-orientation")) fails.Add($"{name}: false loop-orientation: {rep.Issues.First(i => i.Code == "loop-orientation").Message}");
                // порча: у каждой кривой грани по очереди обращена нормаль
                var faces = b.Faces.Select((f, i) => (f, i)).Where(x => x.f.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface || ((Parametric3D.Kernel.Surfaces.FacetedSurface)x.f.Surface).Source != null)
                    .Where(x => GeometryAudit.AuthoritativeSurface(x.f) is { IsPlanar: false }).Select(x => x.i).ToList();
                foreach (int fi in faces)
                {
                    var c = TopologyClone.Deep(b); c.Guarantee = GuaranteeLevel.ExactAnalytic; c.TopologyConvention = b.TopologyConvention;
                    var cf = c.Faces.ElementAt(fi);
                    // грань с фасетами (2026-10-19, вечер): проверка по точной поверхности-источнику; порча — обход контура
                    // обращён (нормаль граней с фасетами задают фасеты, флаг Reversed у них стороны не меняет)
                    if (cf.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface)
                    {
                        if (Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(cf)) is not (Parametric3D.Kernel.Geometry.Ssi.Cylinder or Parametric3D.Kernel.Geometry.Ssi.Cone or Parametric3D.Kernel.Geometry.Ssi.Sphere)) continue;
                        if (!cf.Loops.SelectMany(l => l.CoEdges).GroupBy(x => x.Edge).Any(g => g.Count() == 1)) continue;
                        faceted++;
                        foreach (var lp in cf.Loops)
                        {
                            var ces = lp.CoEdges.ToList(); lp.CoEdges.Clear();
                            foreach (var x in Enumerable.Reverse(ces)) { x.Reversed = !x.Reversed; lp.CoEdges.Add(x); }
                        }
                        var rf = SolidValidator.Validate(c);
                        if (rf.Issues.Any(i => i.Code == "loop-orientation" && i.Message.Contains("по точной поверхности"))) facetedCaught++;
                        else fails.Add($"{name}: faceted face {fi} {cf.Tag} with reversed loops not reported by the exact check: {rf.Summary()}");
                        continue;
                    }
                    if (Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(cf.Surface) is not (Parametric3D.Kernel.Geometry.Ssi.Cylinder or Parametric3D.Kernel.Geometry.Ssi.Cone or Parametric3D.Kernel.Geometry.Ssi.Sphere)) continue;
                    // грань без собственного края (целый шар) — контуров нет, обращение ловит знак объёма, не эта проверка
                    var uses = cf.Loops.SelectMany(l => l.CoEdges).GroupBy(x => x.Edge).ToList();
                    if (!uses.Any(g => g.Count() == 1)) continue;
                    curved++;
                    cf.Reversed = !cf.Reversed;
                    var rc = SolidValidator.Validate(c);
                    if (rc.Issues.Any(i => i.Code == "loop-orientation" && i.Message.Contains("по точной поверхности"))) caught++;
                    else fails.Add($"{name}: face {fi} {cf.Tag} ({GeometryAudit.AuthoritativeSurface(cf)?.GetType().Name}) with reversed normal not reported: {rc.Summary()}");
                }
            }
            Log(new { test = "KRN03-LOOP-ORIENTATION-EXACT", bodies = bodies.Count, curvedFaces = curved, caught, facetedFaces = faceted, facetedCaught });
            if (faceted < 1 && Environment.GetEnvironmentVariable("P3D_LOOPFACETED") != "0") fails.Add("no faceted curved face exercised");
            if (curved < 2) fails.Add($"only {curved} curved faces exercised");
            Require(fails.Count == 0, string.Join("; ", fails.Take(8)));
        });
    }
}
