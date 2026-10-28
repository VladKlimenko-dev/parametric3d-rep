using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

/// <summary>
/// k126 (замечание 28.10, 10:09 — «скорость хотя бы как у OCCT»): ускорения ядра дают те же ответы, что прежний полный
/// обход, а расчёт с отложенными проверками и их принятием — то же тело, что расчёт с проверками сразу.
/// </summary>
static partial class Program
{
    static void Krn126Checks()
    {
        Check("KRN-SPEED-K126-GRID-EQUIV", () =>
        {
            // сетка ячеек точки внутри области (FaceSplit.PolyGrid) против полного обхода ломаных: плоские и с периодом
            // (развёртка цилиндра), чётность луча и зазор — побитово. P3D_SPLITGRID=0 — сетки нет, сравнивать нечего.
            var (cmp, bad) = Parametric3D.Kernel.Analytic.FaceSplit.GridSelfCheck(20261028, 24, 400);
            Console.WriteLine($"  grid: compared {cmp}, mismatches {bad}");
            Require(cmp >= 1000 && bad == 0, $"grid answers differ from the full scan: {bad} of {cmp}");
        });

        Check("KRN-SPEED-K126-BOUNDS-EQUIV", () =>
        {
            // указатель отрезков границы грани (AnalyticFace.BoundsIndex) против полного обхода — на гранях деталей с кривыми
            // пересечения: принадлежность и расстояние до границы — побитово. P3D_BOUNDSINDEX=0 — указателей нет.
            int cmp = 0, bad = 0, idx = 0, seed = 7;
            foreach (var name in new[] { "user_k116.p3d", "seq_handles_k75.p3d", "load_session_k75b.p3d" })
            {
                var (p, prm) = DocumentFile.Load(File.ReadAllText(Fixture(name)));
                var doc = new Document(); doc.Root.Add(p);
                p.Rebuild(prm);
                foreach (var b in p.Bodies)
                {
                    var r = Parametric3D.Kernel.Analytic.AnalyticFace.BoundsIndexSelfCheck(b, 60, seed++);
                    cmp += r.Compared; bad += r.Mismatches; idx += r.Indexed;
                }
            }
            Console.WriteLine($"  bounds index: faces {idx}, compared {cmp}, mismatches {bad}");
            Require(idx > 0 && cmp >= 200 && bad == 0, $"indexed face queries differ from the full scan: {bad} of {cmp} (indexed faces {idx})");
        });

        Check("KRN-SPEED-K126-DEFERRED-SAME", () =>
        {
            // расчёт с отложенными проверками (сетка граней — по требованию, проверки разбиения — при принятии) и затем
            // обычный расчёт (принятие отложенного) — то же тело, что расчёт с проверками сразу: объём, число граней, ошибки
            // операций; после принятия ничего не ждёт проверки. P3D_LAZYMESH=0 — сетки не откладываются.
            long lazy0 = Parametric3D.Kernel.Export.StepImport.LazyFaces;
            var fails = new List<string>(); int n = 0;
            IEnumerable<(string Name, Func<(Part P, Parametric3D.Model.Params.ParameterTable? Prm)> Make)> Cases()
            {
                foreach (var name in new[] { "user_k116.p3d", "seq_handles_k75.p3d", "load_session_k75b.p3d" })
                    yield return (name, () => { var (p, prm) = DocumentFile.Load(File.ReadAllText(Fixture(name))); new Document().Root.Add(p); return (p, prm); });
                yield return ("tube k116", () => (Tube116(), null));
                yield return ("twin pockets + groove", () => (TwinPockets116(groove: true), null));
                yield return ("twin pockets, circles, mirror", () => (TwinPockets116(circle: true, mirror: true), null));
            }
            foreach (var (name, make) in Cases())
            {
                var (pe, prmE) = make(); pe.Rebuild(prmE);
                var (pd, prmD) = make();
                using (new KernelContext(KernelSettings.Capture() with { Preview = true }).Enter()) pd.Rebuild(prmD);
                bool pending = pd.NeedsAcceptance;
                pd.Rebuild(prmD);
                n++;
                int fe = pe.Bodies.Sum(b => b.Faces.Count()), fd = pd.Bodies.Sum(b => b.Faces.Count());
                int ee = pe.Features.Count(f => f.Error != null), ed = pd.Features.Count(f => f.Error != null);
                Console.WriteLine($"  {name}: eager V {pe.Volume:0.######} faces {fe} errors {ee}; deferred V {pd.Volume:0.######} faces {fd} errors {ed}; pending before {pending}, after {pd.NeedsAcceptance}");
                if (!(Math.Abs(pe.Volume - pd.Volume) <= 1e-9 * Math.Max(1, Math.Abs(pe.Volume))) || fe != fd || ee != ed || pd.NeedsAcceptance)
                    fails.Add($"{name}: V {pd.Volume:R} vs {pe.Volume:R}, faces {fd} vs {fe}, errors {ed} vs {ee}, pending {pd.NeedsAcceptance}");
            }
            long lazy = Parametric3D.Kernel.Export.StepImport.LazyFaces - lazy0;
            Console.WriteLine($"  lazy faces {lazy}");
            Require(fails.Count == 0, string.Join("; ", fails));
            Require(lazy > 0, "no face meshing was deferred");
        });
    }
}
