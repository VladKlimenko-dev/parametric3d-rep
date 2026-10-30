using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.App.Diagnostics;

static partial class Program
{
    /// <summary>
    /// k139 (замечание 30.10, 00:56, п. 4; файл пользователя Sweep_Blend: лофт «окружность с квадратом → окружность с
    /// прямоугольником → прямоугольник с треугольником»):
    /// — лофт с отверстием — одним телом, без булевой (P3D_LOFTHOLLOW=0): объём тот же, тело точное;
    /// — 4.1: скругление R10.29 кромки квадратного окна: шар у угла окна — на продолжении изогнутой стенки (P3D_BALLEXTEND=0 —
    ///   сечения плоские, бугры), торец резца — по нависающей соседней стенке (P3D_WALLCAP=0 — вырез в стенке);
    /// — 4.3: скругление R6.27 верхней кромки торца-прямоугольника: сечения и внутри прямого ребра (P3D_SWEEPLINESTATIONS=0 —
    ///   полоска вдоль скругления);
    /// — 4.4: фаска 2.18 кромки, упёртой в скругление, — сбегает в него (P3D_BLENDRUNOUT=0 — ступенька).
    /// Норма: правка принята, тело годное, объём в пределах 0.5 мм³ от замера k140, счётчики путей сработали.
    /// k140: замер пересмотрен — проекция на NURBS стенки лофта Ньютоном (P3D_NURBSPROJNEWTON) сходится точнее прежней
    /// (−0.76 мм³ во всех трёх случаях), точки касания внутри материала (P3D_TANGENTINSET) — ещё −0.08 мм³.
    /// </summary>
    static void Krn06LoftBlendsK139Checks()
    {
        Check("KRN06-LOFT-BLENDS-K139", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var fails = new List<string>();
            string text = File.ReadAllText(Path.Combine(dir, "sweep_blend_k139.p3d"));
            Part Stage(params int[] keep)
            {
                var (p, pars) = DocumentFile.Load(text);
                var drop = p.Features.Where((f, i) => !keep.Contains(i)).ToList();
                foreach (var f in drop) p.RemoveFeature(f);
                p.Rebuild(pars); return p;
            }
            // лофт
            long h0 = Parametric3D.Kernel.Ops.DirectRound.HollowLofts;
            var loft = Stage(0);
            double v0 = loft.Volume;
            Log(new { loft = Math.Round(v0, 3), hollow = Parametric3D.Kernel.Ops.DirectRound.HollowLofts - h0, guarantee = loft.Bodies.Single().Guarantee.ToString() });
            if (Math.Abs(v0 - 645529.99) > 0.05) fails.Add($"loft volume {v0:0.###}");
            if (Parametric3D.Kernel.Ops.DirectRound.HollowLofts == h0) fails.Add("the loft with a hole went through the boolean");
            void Case(string tag, Part p, double[] a, bool round, double size, double want, Func<string?> extra)
            {
                var A = new OpenTK.Mathematics.Vector3d(a[0], a[1], a[2]); var B = new OpenTK.Mathematics.Vector3d(a[3], a[4], a[5]);
                var e = p.Bodies.SelectMany(b => b.Edges).OrderBy(q => Math.Min((q.V0.P - A).Length + (q.V1.P - B).Length, (q.V0.P - B).Length + (q.V1.P - A).Length)).First();
                Feature f = round ? new RoundFeature("R") { Radius = new ValueRef(size) } : new ChamferFeature("C") { D1 = new ValueRef(size), D2 = new ValueRef(size) };
                if (f is RoundFeature rf) rf.Edges.Add(EdgeRef.Of(e)); else ((ChamferFeature)f).Edges.Add(EdgeRef.Of(e));
                p.AddFeature(f); p.Rebuild(null);
                if (f.Error != null) { fails.Add($"{tag}: {f.Error.Split('\n').Last()}"); return; }
                var body = p.Bodies.Single(); bool valid = SolidValidator.Validate(body).IsValid; double v = p.Volume;
                Log(new { tag, volume = Math.Round(v, 3), valid, guarantee = body.Guarantee.ToString() });
                if (!valid) fails.Add(tag + ": invalid body");
                if (Math.Abs(v - want) > 0.5) fails.Add($"{tag}: volume {v:0.###}, expected {want:0.###}");
                if (extra() is { } x) fails.Add(tag + ": " + x);
            }
            long fb0 = Parametric3D.Kernel.Ops.FilletOp.BallFlatFallbacks, wc0 = Parametric3D.Kernel.Ops.FilletOp.WallCaps;
            Case("4.1 round R10.29", Stage(0), new[] { 10.0, 10, 0, -10, 10, 0 }, true, 10.29, 644280.526,
                () => Parametric3D.Kernel.Ops.FilletOp.BallFlatFallbacks != fb0 ? "a section fell back to tangent planes"
                    : Parametric3D.Kernel.Ops.FilletOp.WallCaps == wc0 ? "the tool end was not laid on the wall" : null);
            long ls0 = Parametric3D.Kernel.Ops.FilletOp.LineStations;
            Case("4.3 round R6.27", Stage(0), new[] { -59.27, 83.41, -200, -7.45, 83.41, -200 }, true, 6.27, 644850.616,
                () => Parametric3D.Kernel.Ops.FilletOp.LineStations == ls0 ? "no stations inside the straight edge" : null);
            long ro0 = Parametric3D.Kernel.Ops.FilletOp.BlendRunouts;
            Case("4.4 chamfer 2.18 at the round", Stage(0, 2), new[] { -59.27, 83.41, -200, -59.27, 53.36, -200 }, false, 2.18, 644787.999,
                () => Parametric3D.Kernel.Ops.FilletOp.BlendRunouts == ro0 ? "the chamfer did not run out into the round" : null);
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
