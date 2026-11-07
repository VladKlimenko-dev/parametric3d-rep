using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k181 (замечание 07.11, 21:08): ВЫРЕЗ НАСКВОЗЬ ОТ ТОРЦА. (1) «Насквозь» — по направлению стороны: вырез от верхней грани
    /// плиты, обращённый внутрь (Flip — так операция ставит вырез на грани), снимает материал; не обращённый — ошибка «does not
    /// reach the body», а не молчаливая операция без изменений (P3D_THROUGHFLIP=0, P3D_CUTMISS=0). (2) Отверстие в торце
    /// перехода шестерен (файл пользователя, грани NURBS отверстие не задевают) — точным путём: снят ровно цилиндр
    /// π·20²·150, тело точное, быстро (P3D_QUADRICNURBS=0 — общая булева, ~100 с и многогранное тело).
    /// </summary>
    static void Cut181Checks()
    {
        Check("CUT-THROUGH-FLIP-K181", () =>
        {
            var fails = new List<string>();
            Part Plate(bool flip, bool circle)
            {
                var p = new Part("plate");
                var s0 = RectSketch(p, 0, -50, -30, 100, 60);
                var body = new ExtrudeFeature("Body") { SketchId = s0.Id }; body.Side1.Distance = new ValueRef(20); p.AddFeature(body);
                var s1 = circle ? CircleSketch(p, 20, 10, 0, 8) : RectSketch(p, 20, 0, -10, 20, 20);
                var cut = new ExtrudeFeature("Cut") { SketchId = s1.Id, Cut = true }; cut.Side1.End = EndType.ThroughAll; cut.Side1.Flip = flip; p.AddFeature(cut);
                p.Rebuild(null);
                return p;
            }
            foreach (bool circle in new[] { false, true })
            {
                double want = 100 * 60 * 20 - (circle ? Math.PI * 64 * 20 : 400 * 20);
                var pin = Plate(true, circle); var cin = pin.Features[1];
                var pout = Plate(false, circle); var cout = pout.Features[1];
                Log(new { test = "CUT-THROUGH-FLIP-K181", circle, inward = new { cin.Error, pin.Volume, want }, outward = new { cout.Error, pout.Volume } });
                if (cin.Error != null || Math.Abs(pin.Volume - want) > 1e-6 * want) fails.Add($"{(circle ? "circle" : "rectangle")} cut into the plate: {cin.Error} volume {pin.Volume} want {want}");
                if (cout.Error == null || !cout.Error.Contains("does not reach")) fails.Add($"{(circle ? "circle" : "rectangle")} cut away from the plate: no error ({cout.Error}), volume {pout.Volume}");
            }
            // файл пользователя: переход шестерен и вырез окружностью R20 насквозь от торца
            string uf = Path.Combine("tests", "k01", "data", "user_k181_gearcut.p3d");
            if (File.Exists(uf))
            {
                var (p, ps) = DocumentFile.Load(File.ReadAllText(uf));
                p.StageIndex = 1; p.MarkStageMoved(); p.Rebuild(ps);
                double v0 = p.Volume;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                p.StageIndex = 2; p.MarkStageMoved(); p.Rebuild(ps);
                long ms = sw.ElapsedMilliseconds;
                var f = p.Features[1];
                double removed = v0 - p.Volume, want = Math.PI * 400 * 150;
                string g = string.Join(",", p.Bodies.Select(b => b.Guarantee));
                Log(new { test = "gear cut", f.Error, ms, removed, want, guarantee = g, faces = p.Bodies.Sum(b => b.Faces.Count()), warnings = p.Warnings.ToList() });
                if (f.Error != null || Math.Abs(removed - want) > 1e-6 * want) fails.Add($"gear cut: {f.Error}, removed {removed} want {want}");
                if (g != "ExactAnalytic") fails.Add($"gear cut: body {g}");
                if (ms > 30000) fails.Add($"gear cut: {ms} ms");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
