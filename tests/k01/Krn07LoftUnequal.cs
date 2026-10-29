using Parametric3D.Model.Features;
using Parametric3D.Model;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// k137 (замечание 29.10, 18:13, пп. 1–2; файл пользователя Sweep_Blend): ПЕРЕХОД ПО СЕЧЕНИЯМ С РАЗНЫМ ЧИСЛОМ СТОРОН.
    /// Сечения: круг R30 с квадратным отверстием, круг R40 с прямоугольным, прямоугольник с треугольным. Гладкий переход
    /// по Sketch2 + Sketch3 и по всем трём отказывал «sections differ in the number of curves» (прямоугольник → треугольник).
    /// Теперь у сечения с меньшим числом сторон делится самая длинная. Норма: тело точное и годное; объём по трём сечениям —
    /// по формуле Симпсона из площадей сечений с допуском 2 % (переход плавный); по двум — между меньшим и большим сечением
    /// × длина. Мутация P3D_LOFTEQUALIZE=0 роняет.
    /// </summary>
    static void Krn07LoftUnequalChecks()
    {
        Check("KRN07-LOFT-UNEQUAL-SIDES", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "sweep_blend_k137.p3d")))!;
            var fails = new List<string>();
            // площади сечений (с отверстиями) и расстояния: z = 0, −100, −200
            foreach (var (name, secs) in new[] { ("S2+S3", new[] { 2974, 3018 }), ("S1+S2+S3", new[] { 2946, 2974, 3018 }) })
            {
                var arr = new System.Text.Json.Nodes.JsonArray(); foreach (var id in secs) arr.Add(id);
                json["Features"]![0]!["Sections"] = arr;
                var (p, pars) = DocumentFile.Load(json.ToJsonString()); p.Rebuild(pars);
                var f = p.Features[0];
                if (f.Error != null) { fails.Add($"{name}: {f.Error.Split('\n')[0]}"); continue; }
                var b = p.Bodies.Single(); bool valid = SolidValidator.Validate(b).IsValid;
                double a1 = AreaOfSketch(p, "Sketch1"), a2 = AreaOfSketch(p, "Sketch2"), a3 = AreaOfSketch(p, "Sketch3");
                double v = p.Volume;
                Log(new { name, guarantee = b.Guarantee.ToString(), valid, volume = Math.Round(v, 2), faces = b.Faces.Count(), a3 = Math.Round(a3, 2) });
                if (!valid || !b.Guarantee.ToString().StartsWith("Exact")) fails.Add($"{name}: {b.Guarantee} valid {valid}");
                if (secs.Length == 3)
                {
                    double simpson = 200.0 / 6 * (a1 + 4 * a2 + a3);
                    if (Math.Abs(v - simpson) > 0.02 * simpson) fails.Add($"{name}: volume {v:0} vs Simpson {simpson:0}");
                }
                else if (!(v > Math.Min(a2, a3) * 100 && v < Math.Max(a2, a3) * 100)) fails.Add($"{name}: volume {v:0} outside [{Math.Min(a2, a3) * 100:0}, {Math.Max(a2, a3) * 100:0}]");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    /// <summary>
    /// k137 (замечание 29.10, 18:35, п. 12): СКРУГЛЕНИЕ РЕБРА ОТВЕРСТИЯ НА ТОРЦЕ ПЕРЕХОДА. Ребро квадратного отверстия (торец ×
    /// NURBS-стенка отверстия) — выпуклое, но протянутый резец отказывал «on a concave chain»: у прямого ребра два сечения,
    /// и проба выпуклости шла у вершины, задевая соседние грани. Норма: скругление R1 принято, тело годное, объём уменьшился.
    /// Мутация P3D_SWEEPCONVEXMID=0 роняет.
    /// </summary>
    static void Krn06SweptConvexMidChecks()
    {
        Check("KRN06-SWEPT-CONVEX-MID", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var (p, pars) = DocumentFile.Load(File.ReadAllText(Path.Combine(dir, "sweep_blend_k137.p3d"))); p.Rebuild(pars);
            double v0 = p.Volume;
            var A = new OpenTK.Mathematics.Vector3d(10, 10, 0); var B = new OpenTK.Mathematics.Vector3d(10, -10, 0);
            var e = p.Bodies.SelectMany(b => b.Edges).OrderBy(q => Math.Min((q.V0.P - A).Length + (q.V1.P - B).Length, (q.V0.P - B).Length + (q.V1.P - A).Length)).First();
            var rf = new RoundFeature("R") { Radius = new ValueRef(1) };
            rf.Edges.Add(EdgeRef.Of(e)); p.AddFeature(rf); p.Rebuild(pars);
            Log(new { error = rf.Error?.Split('\n').Last(), volume = p.Volume, v0 });
            Require(rf.Error == null, rf.Error?.Split('\n').Last() ?? "");
            Require(p.Bodies.Count == 1 && SolidValidator.Validate(p.Bodies[0]).IsValid && p.Volume < v0, "invalid result or volume did not decrease");
        });
    }

    /// <summary>Площадь области эскиза (внешний контур минус отверстия) по сетке плоской грани профиля.</summary>
    static double AreaOfSketch(Part p, string name)
    {
        var sk = p.Sketches.First(s => s.Name == name);
        var prof = sk.ToSolidProfile("probe");
        double area = 0;
        foreach (var l in prof.Loops) area += l.IsOuter ? Math.Abs(l.Area) : -Math.Abs(l.Area);
        return area;
    }
}
