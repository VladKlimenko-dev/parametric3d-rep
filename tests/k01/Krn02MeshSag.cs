using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// 02.5 (вторая половина): РАЗМЕТКА ГРАНИ ДЕРЖИТ ХОРДОВЫЙ ДОПУСК ПО ВСЕЙ ГРАНИ, а не в точках проб. Шаг правильной сетки
    /// берётся по кривизне в нескольких точках грани (середина и до 32 точек у края), поэтому допуск по всей грани
    /// обеспечивает доводка: центры и стороны треугольников, отходящие больше допуска, делятся. Проверка меряет отход
    /// сетки от точной поверхности ПО ВСЕЙ ГРАНИ: в каждом треугольнике 15 точек (вершины сторон, их середины и четверти,
    /// центр и точки между центром и вершинами), отход — расстояние до проекции на поверхность. Грани: шар целый и с
    /// отверстием, тор (кривизна вдоль кольца меняется в 1.7 раза), конус 60° у вершины, цилиндр, скругление ребра куба,
    /// NURBS-грань. Норма — отход не больше хордового допуска модели с запасом 25 % (центр треугольника против середины
    /// стороны). Мутация P3D_UVCURVMAX=0 (кривизна в одной точке) проверку не роняет — допуск держит
    /// доводка; P3D_MESHSAG_MUT=1 (норма без запаса, в 0.5 допуска) — роняет.
    /// </summary>
    static void Krn02MeshSagChecks()
    {
        Check("KRN02-MESH-SAG-WHOLE-FACE", () =>
        {
            var fails = new List<string>();
            var sph = new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 10) };
            BooleanOp.Subtract(sph, PrimitiveOp.Cylinder(new Vector3d(0, 0, -20), 4, 40), new List<string>());
            var round = new Parametric3D.Model.Part("sag"); Parametric3D.Model.TestGeometry.AddCube(round, 60); round.Rebuild(null);
            var rf = new Parametric3D.Model.Features.RoundFeature { Radius = 8 }; rf.Edges.Add(EdgeRef.Of(CornerEdge(round, 30, 30)));
            round.AddFeature(rf); round.Rebuild(null);
            var bodies = new List<(string, TSolid)>
            {
                ("sphere", PrimitiveOp.Sphere(Vector3d.Zero, 25)),
                ("sphere with a hole", sph[0]),
                ("torus", PrimitiveOp.Torus(Vector3d.Zero, 30, 8)),
                ("cone 60°", PrimitiveOp.Cone(Vector3d.Zero, 20, 20 - 10 * Math.Sqrt(3), 10)),
                ("cylinder", PrimitiveOp.Cylinder(Vector3d.Zero, 10, 30)),
                ("NURBS quarter cylinder", NurbsQuarterCylinder()),
            };
            if (rf.Error == null && round.Bodies.Count == 1) bodies.Add(("cube with a round", round.Bodies[0])); else fails.Add("round: " + rf.Error);
            bool mut = Environment.GetEnvironmentVariable("P3D_MESHSAG_MUT") == "1";
            var stats = new List<object>();
            // допуск — хордовый допуск модели: рёбра тела размечены им же (у меньшего допуска край грани остаётся прежним)
            foreach (double tol in new[] { Parametric3D.Core.Tol.Chord })
                foreach (var (name, body) in bodies)
                {
                    double worst = 0, ratio = 0; int tris = 0, faces = 0, skipped = 0;
                    foreach (var f in body.Faces)
                    {
                        var srf = GeometryAudit.AuthoritativeSurface(f);
                        if (srf == null || Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(srf) is Parametric3D.Kernel.Geometry.Ssi.Plane) continue;
                        var m = Parametric3D.Kernel.Tess.UvMesher.Mesh(f, tol, out var why);
                        if (m == null) { skipped++; continue; }   // грань размечается другим построителем (целая сфера, конус у вершины)
                        faces++;
                        var (P, T) = m.Value;
                        for (int t = 0; t < T.Count; t += 3)
                        {
                            Vector3d A = P[T[t]], B = P[T[t + 1]], C = P[T[t + 2]];
                            tris++;
                            foreach (var (a, b) in Bary)
                            {
                                var q = A + (B - A) * a + (C - A) * b;
                                var on = srf.PointAt(srf.ProjectToUV(q));
                                double d = (on - q).Length;
                                if (d > worst) worst = d;
                            }
                        }
                    }
                    ratio = worst / tol;
                    stats.Add(new { name, tol, faces, skipped, tris, worstMm = Math.Round(worst, 6), ofTol = Math.Round(ratio, 3) });
                    double limit = mut ? 0.5 : 1.25;
                    if (ratio > limit) fails.Add($"{name} at tol {tol}: sag {worst:G4} mm = {ratio:F2}·tol");
                }
            foreach (var s in stats) Log(s);
            Require(stats.Count(x => (int)x.GetType().GetProperty("faces")!.GetValue(x)! > 0) >= 5, "too few meshed bodies");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    /// <summary>Точки треугольника в барицентрических (a, b): вершины, середины и четверти сторон, центр, полпути центр–вершина.</summary>
    static readonly (double, double)[] Bary =
    {
        (0, 0), (1, 0), (0, 1), (0.5, 0), (0, 0.5), (0.5, 0.5), (0.25, 0), (0.75, 0), (0, 0.25), (0, 0.75), (0.25, 0.75), (0.75, 0.25),
        (1.0 / 3, 1.0 / 3), (1.0 / 6, 1.0 / 6), (2.0 / 3, 1.0 / 6),
    };
}
