using OpenTK.Mathematics;
using Parametric3D.Kernel.Analytic;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn10PairChecks()
    {
        Check("KRN10-PAIR-BOUNDS-CONSERVATIVE", () =>
        {
            // 10.1: отбор пар граней общей булевой по габаритам, посчитанным один раз на грань. (а) Отбор консервативен: каждая
            // пара, отброшенная габаритом, у FaceIntersect.Of — пустая (на телах корпуса: детали K08, шар, тор, конус,
            // повёрнутые цилиндры — во всех сочетаниях). (б) Отбор работает: при перестроении детали K08 часть пар отброшена.
            var fails = new List<string>();
            var bodies = new List<TSolid>();
            for (int seed = 1; seed <= 3; seed++) bodies.AddRange(RealisticPart(seed, 10, out _).Bodies);
            bodies.Add(PrimitiveOp.Sphere(new Vector3d(20, 10, 5), 12));
            bodies.Add(PrimitiveOp.Torus(new Vector3d(-10, 0, 10), 20, 4));
            bodies.Add(PrimitiveOp.Cone(new Vector3d(30, -20, -5), 10, 2, 25));
            bodies.Add(TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -30), 6, 60), TransformOp.Rotate(Vector3d.Zero, new Vector3d(1, 1, 0).Normalized(), 37)));
            var faces = bodies.Select(b => b.Faces.Select(f => AnalyticFace.Of(f, out _)).OfType<AnalyticFace>().ToList()).ToList();
            int skipped = 0, wrong = 0;
            for (int i = 0; i < faces.Count; i++)
                for (int j = i + 1; j < faces.Count; j++)
                    foreach (var x in faces[i])
                        foreach (var y in faces[j])
                        {
                            if (FaceIntersect.BoxesMeet(FaceIntersect.PairBox(x), FaceIntersect.PairBox(y))) continue;
                            skipped++;
                            var r = FaceIntersect.Of(x, y);
                            if (r.Kind != FaceIntersect.Kind.Empty && wrong++ < 3) fails.Add($"pair {x.Face.Tag} × {y.Face.Tag} skipped by bounds, but Of = {r.Kind}");
                        }
            if (skipped < 100) fails.Add($"only {skipped} pairs skipped — the corpus does not exercise the bounds");
            Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset();
            try
            {
                // брусок с шестью отверстиями и бобышкой, затем наклонный цилиндр — общая булева
                var bl = new List<TSolid> { Box(0, 0, 0, 120, 60, 20) }; var w = new List<string>();
                for (int k = 0; k < 6; k++) BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(15 + 18 * k, 15, -5), 4, 30), w);
                BooleanOp.Unite(bl, PrimitiveOp.Cylinder(new Vector3d(100, 45, 20), 6, 10), w);
                Parametric3D.Kernel.Diagnostics.Prof.Reset();
                BooleanOp.Subtract(bl, TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -40), 5, 80), TransformOp.Rotate(new Vector3d(30, 45, 10), Vector3d.UnitY, 25)), w);
                long all = Parametric3D.Kernel.Diagnostics.Prof.CountOf("общая: пар граней"), cut = Parametric3D.Kernel.Diagnostics.Prof.CountOf("общая: пар отброшено габаритом");
                Log(new { test = "KRN10-PAIR-BOUNDS", corpusSkipped = skipped, wrong, partPairs = all, partSkipped = cut });
                if (all > 0 && cut == 0) fails.Add($"part rebuild: {all} face pairs, none skipped by bounds");
                if (all == 0) fails.Add("part rebuild: the general boolean was not reached");
            }
            finally { Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
