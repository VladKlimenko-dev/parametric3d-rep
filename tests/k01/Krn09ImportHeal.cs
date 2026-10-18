using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn09ImportHealChecks()
    {
        Check("KRN09-IMPORT-HEAL-REPORTED", () =>
        {
            // 09.5: импорт STEP лечится в бюджете (ToleranceContext.ImportHeal = 1e-4 мм) с отчётом — строка на тело. Брусок
            // 40×30×20 со сквозным отверстием R6, записанный нашим точным STEP и испорченный: вершина сдвинута на 2e-5 (лечится:
            // перенос в пересечение плоскостей), флаги стороны граней (читаются верно и без лечения), выпала плоская грань
            // (закрывается), вершина сдвинута на 5e-3 (больше бюджета — «NOT valid» с причиной), выпала стенка отверстия (не
            // плоская дыра — не закрывается, «NOT valid»). Объём здорового и вылеченного тела — точный (аналитический).
            var fails = new List<string>();
            double exact = 24000 - Math.PI * 36 * 20;
            foreach (var (name, text) in ImportHealCases())
            {
                var w = new List<string>();
                var p = Parametric3D.Model.Exchange.Read(text, out _, w);
                string? rep = w.FirstOrDefault(x => x.StartsWith("Import: body 1:"));
                double? v = p?.Bodies.Count == 1 ? AnalyticVolume.Of(p.Bodies[0]) : null;
                Log(new { test = "KRN09-IMPORT-HEAL", name, report = rep, volume = v });
                if (rep == null) { fails.Add($"{name}: no healing report"); continue; }
                bool valid = rep.EndsWith(" valid.");
                switch (name)
                {
                    case "as written" or "one face sense flipped" or "all faces flipped":
                        if (!valid || v is not double v0 || Math.Abs(v0 - exact) > 1e-9 * exact) fails.Add($"{name}: {rep} V {v}");
                        break;
                    case "vertex off by 2e-5":
                        if (!valid || !rep.Contains("vertex(es) snapped") || rep.Contains(" 0 vertex(es) snapped") || v is not double v1 || Math.Abs(v1 - exact) > 0.05) fails.Add($"{name}: {rep} V {v}");
                        break;
                    case "one face missing":
                        if (!valid || !rep.Contains("1 hole(s) filled") || v is not double v2 || Math.Abs(v2 - exact) > 1e-9 * exact) fails.Add($"{name}: {rep} V {v}");
                        break;
                    case "vertex off by 5e-3":
                        if (valid || !rep.Contains("budget")) fails.Add($"{name}: over-budget gap not reported: {rep}");
                        break;
                    case "hole wall missing":
                        if (valid || rep.Contains("1 hole(s) filled") || rep.Contains("2 hole(s) filled")) fails.Add($"{name}: a non-planar opening must not be filled: {rep}");
                        break;
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN09-IGES-IMPORT-HEAL", () =>
        {
            // 09.5: то же лечение — у IGES (наш файл грани 144). Брусок 40×30×20 с отверстием R6: как записан; угол сдвинут на 2e-5
            // (лечится переносом в пересечение плоскостей); выпала плоская грань (закрывается); угол сдвинут на 5e-3 (сверх бюджета
            // — «NOT valid» с причиной). Объём — точный (у сдвинутого угла — в пределах сдвига).
            var fails = new List<string>();
            double exact = 24000 - Math.PI * 36 * 20;
            TSolid Make()
            {
                var bl = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(20, 15, -5), 6, 30), new List<string>());
                return TopologyClone.Deep(bl[0]);
            }
            string Corner(double dx)
            {
                var b = Make(); var v = b.Vertices.First(x => (x.P - Vector3d.Zero).Length < 1e-9);
                v.P += new Vector3d(dx, 0, 0);
                foreach (var e in b.Edges.Where(e => ReferenceEquals(e.V0, v) || ReferenceEquals(e.V1, v)))
                    if (e.Curve is Parametric3D.Kernel.Curves.LineCurve3 l) { bool fw = (l.A - e.V0.P).Length < (l.B - e.V0.P).Length + 1; e.Curve = new Parametric3D.Kernel.Curves.LineCurve3(e.V0.P, e.V1.P); e.PolylineCache = null; }
                return Parametric3D.Kernel.Export.IgesSolid.Write(b, new List<string>());
            }
            string Missing()
            {
                var b = Make();
                var f = b.Faces.First(x => GeometryAudit.AuthoritativeSurface(x) is { IsPlanar: true } && x.Loops.Count == 1 && x.Loops[0].CoEdges.All(ce => Math.Abs(ce.Start.P.X) < 1e-9));
                foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) ce.Edge.CoEdges.Remove(ce);
                foreach (var sh in b.Shells) sh.Faces.Remove(f);
                return Parametric3D.Kernel.Export.IgesSolid.Write(b, new List<string>());
            }
            foreach (var (name, text) in new[] { ("as written", Parametric3D.Kernel.Export.IgesSolid.Write(Make(), new List<string>())), ("corner off by 2e-5", Corner(2e-5)), ("one face missing", Missing()), ("corner off by 5e-3", Corner(5e-3)) })
            {
                var w = new List<string>();
                var p = Parametric3D.Model.Exchange.Read(text, out _, w);
                string? rep = w.FirstOrDefault(x => x.StartsWith("Import: body 1:"));
                double? v = p?.Bodies.Count == 1 ? AnalyticVolume.Of(p.Bodies[0]) : null;
                Console.WriteLine($"  IGES {name}: {rep} V {v}");
                if (rep == null) { fails.Add($"{name}: no healing report"); continue; }
                bool valid = rep.EndsWith(" valid.");
                switch (name)
                {
                    case "as written": if (!valid || v is not double v0 || Math.Abs(v0 - exact) > 1e-9 * exact) fails.Add($"{name}: {rep} V {v}"); break;
                    case "corner off by 2e-5": if (!valid || rep.Contains(" 0 vertex(es) snapped") || v is not double v1 || Math.Abs(v1 - exact) > 0.05) fails.Add($"{name}: {rep} V {v}"); break;
                    case "one face missing": if (!valid || !rep.Contains("1 hole(s) filled") || v is not double v2 || Math.Abs(v2 - exact) > 1e-9 * exact) fails.Add($"{name}: {rep} V {v}"); break;
                    case "corner off by 5e-3": if (valid || !rep.Contains("budget")) fails.Add($"{name}: over-budget gap not reported: {rep}"); break;
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
