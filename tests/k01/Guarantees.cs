using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// Допустимые изменяемые статические поля, не привязанные к контексту, — с причиной. Всё остальное, что
    /// влияет на результат, обязано идти через KernelSettings / ModelSettings. Новое поле без записи здесь
    /// роняет G5-GLOBALS-INVENTORY; запись без поля — тоже (список не должен устаревать).
    /// </summary>
    static readonly Dictionary<string, string> AllowedGlobals = new()
    {
        // интерфейс и окружение приложения
        ["Core.Settings.Current"] = "настройки интерфейса (цвета, шрифты), не геометрия",
        ["Kernel.Topology.AnalyticVolume.OriginShift"] = "KRN-02: переключатель разбора (P3D_VOLORIGIN), только для замеров",
        ["Core.AppLog.Path"] = "журнал приложения", ["Core.AppLog.MaxBytes"] = "журнал приложения",
        ["Model.DocumentStorage.MaxBytes"] = "предел размера файла документа при загрузке",
        // атомарные выдачи номеров
        ["Core.Ids._next"] = "выдача номеров (Interlocked)",
        ["Model.Features.Feature._keyCounter"] = "детерминированные ключи проверок (Interlocked)",
        ["Kernel.KernelContext._verifying"] = "счётчик контекстов с проверкой предикатов (Interlocked)",
        // разбор и профили
        ["Kernel.Diagnostics.Prof.Enabled"] = "профилирование",
        ["Kernel.Tess.ExactFaceMesher.ObjDumpPath"] = "отладочная выгрузка",
        ["Kernel.Tess.ExactFaceMesher.UVDumpPath"] = "отладочная выгрузка",
        ["Kernel.Tess.ExactFaceMesher.StrayDump"] = "отладочная выгрузка",
        ["Kernel.Tess.TetFlips.LastRefusal"] = "причина последнего отказа (разбор)",
        ["Kernel.Tess.TetMesher.LastCavityBroken"] = "разбор",
    };
    /// <summary>Счётчики статистики: только наблюдение, на результат не влияют (гонки дают лишь неточный счёт).</summary>
    static readonly string[] StatisticsPrefixes =
    {
        "Kernel.Exact.ExactBsp.", "Kernel.Exact.PlaneRegistry.Total", "Kernel.Ops.VertexSnap.",
        "Kernel.Tess.AnalysisMesher.Rounds", "Kernel.Tess.Cdt.Merged", "Kernel.Tess.Tessellator.",
        "Kernel.Topology.TopologyHeal.Removed", "Kernel.Exact.ExactAdapter.StaleExactUses",
        "Kernel.Tess.ExactFaceMesher.Chord", "Kernel.Tess.ExactFaceMesher.Refine", "Kernel.Tess.ExactFaceMesher.Skip",
        "Kernel.Tess.ExactFaceMesher.PoleDeclined", "Kernel.Tess.ExactFaceMesher.RejectedByArea",
        "Kernel.Tess.ExactFaceMesher.Reused", "Kernel.Tess.ExactFaceMesher.SplitCount",
        // KRN-02: счётчики разбора
        "Kernel.Analytic.GeneralBoolean.SideUndecided", "Kernel.Tess.ExactFaceMesher.CapByLoopPicks", "Kernel.Tess.ExactFaceMesher.CapLoopUndecided", "Kernel.Tess.UvMesher.PolePicksByLoop", "Kernel.Tess.UvMesher.PoleUndecided", "Kernel.Tess.UvMesher.PoleCutsMoved", "Kernel.Analytic.AnalyticFace.PoleClampHits", "Kernel.Analytic.GeneralBoolean.SphereChartsChanged", "Kernel.Tess.UvMesher.StereoFaces", "Kernel.Tess.UvMesher.FullPeriodicFaces", "Kernel.Topology.AnalyticVolume.ArcFormulaRejected", "Kernel.Topology.AnalyticVolume.SphereClosedUsed", "Kernel.Topology.AnalyticVolume.SphereClosedRejected", "Kernel.Topology.AnalyticVolume.PolyVolumeRejected", "Kernel.Ops.TransformOp.ExactCopies", "Kernel.Analytic.GeneralBoolean.OpenLoopsMerged", "Kernel.Analytic.GeneralBoolean.MergesRefused", "Kernel.Analytic.FaceIntersect.RefineFails", "Kernel.Tess.ExactFaceMesher.RefineEvals", "Kernel.Analytic.FaceIntersect.EventClips", "Kernel.Analytic.FaceIntersect.UnresolvedClips", "Kernel.Analytic.GeneralBoolean.BndEventEdges", "Kernel.Analytic.GeneralBoolean.BndSampledEdges", "Kernel.Analytic.SsiEvents.AlgebraicEdges", "Kernel.Analytic.SsiEvents.CoincidentEdges", "Kernel.Analytic.SsiEvents.SampledEdges", "Kernel.Analytic.SsiEvents.TangentEvents", "Kernel.Tess.UvMesher.TorusRestFaces", "Kernel.Analytic.FaceIntersect.EventCertFailures", "Kernel.Topology.AnalyticVolume.GeneralCutRefused", "Kernel.Tess.ExactFaceMesher.FoldRejected", "Kernel.Geometry.Ssi.TraceBranchesAdded", "Kernel.Geometry.Ssi.TraceUncertified", "Kernel.Geometry.Ssi.TraceNearTangent", "Kernel.Ops.DirectRound.Built", "Kernel.Ops.DirectRound.Declined", "Kernel.Topology.SurfaceDomain.PoleInsideTests",
    };

    static TSolid TiltedBox(Vector3d axis, double angle)
    {
        Vector3d R(Vector3d v) => Parametric3D.Kernel.Surfaces.RevolvedSurface.RotateAbout(v, Vector3d.Zero, axis, angle);
        Vector3d U = R(Vector3d.UnitX), Vv = R(Vector3d.UnitY), W = R(Vector3d.UnitZ), O = new(12, -8, 3);
        var f = new Parametric3D.Kernel.Frame3(O, U, Vv);
        var p = new[] { f.ToWorld(0, 0), f.ToWorld(60, 0), f.ToWorld(60, 40), f.ToWorld(0, 40) };
        return ExtrudeOp.Build(ProfileBuilder.Build(f, Enumerable.Range(0, 4)
            .Select(i => (ICurve3)new LineCurve3(p[i], p[(i + 1) % 4])).ToList()), W * 20);
    }
    static int TagsWithSeveralPlanes(ExactSolid s) =>
        s.Polygons.GroupBy(p => p.Tag).Count(g => g.Select(p => p.Support.Id).Distinct().Count() > 1);

    static void Guarantees()
    {
        // Поставка 2, остаток: ВНУТРЕННЕЕ СОСТОЯНИЕ ТОЧНОГО ТЕЛА НЕИЗМЕНЯЕМО ПО УСТРОЙСТВУ (прежде — только проверка
        // отпечатком). Многоугольники — копия в обёртке только для чтения; границы и вершины многоугольника — ReadOnlySpan.
        // Поставка 2, остаток: ГОНКА ДОСТРОЙКИ ВИДИМОЙ СЕТКИ. Писатель попеременно ставит грани опубликованного тела сетки A и
        // B (разное число треугольников, свои нормали), читатель гранит грань: каждый результат — ровно A или ровно B.
        Check("G6-DISPLAY-ATOMIC", () =>
        {
            var p = RealisticPart(8001, 4, out _); p.Rebuild(null);
            var face = p.Bodies[0].Faces.First(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface { Display: { Count: > 8 }, DisplayNormals: not null });
            var fs = (Parametric3D.Kernel.Surfaces.FacetedSurface)face.Surface;
            var trisA = fs.Display!.ToList(); var nA = fs.DisplayNormals!.ToList();
            var trisB = trisA.Take(trisA.Count - 3).ToList();
            var nB = nA.Take(nA.Count - 3).Select(t => (-t.A, -t.B, -t.C)).ToList();   // другие нормали: смесь видна сразу
            var setM = typeof(Parametric3D.Kernel.Surfaces.FacetedSurface).GetMethod("SetDisplay");
            var fD = typeof(Parametric3D.Kernel.Surfaces.FacetedSurface).GetField("Display");
            var fN = typeof(Parametric3D.Kernel.Surfaces.FacetedSurface).GetField("DisplayNormals");
            void Set(List<(Vector3d, Vector3d, Vector3d)> t, List<(Vector3d, Vector3d, Vector3d)> n)
            {
                if (setM != null) setM.Invoke(fs, new object?[] { t, n });
                else { fD!.SetValue(fs, t); System.Threading.Thread.Yield(); fN!.SetValue(fs, n); }   // прежнее устройство
            }
            string Render()
            {
                var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); mb.BeginFace(face.Id); fs.TessellateFace(face, mb); mb.EndFace();
                var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
                var h = new System.HashCode();
                foreach (var v in md.Vertices) { h.Add(v.P); h.Add(v.N); }
                return md.Vertices.Length + ":" + h.ToHashCode();
            }
            // Детерминированная часть: устройство не допускает раздельной записи треугольников и нормалей. (Нагрузочная часть
            // ниже на одноядерной машине прежнюю гонку не ловит: две записи подряд почти не разрываются вытеснением.)
            var tFs = typeof(Parametric3D.Kernel.Surfaces.FacetedSurface);
            var writable = new List<string>();
            foreach (var n in new[] { "Display", "DisplayNormals" })
            {
                if (tFs.GetField(n) is { IsInitOnly: false }) writable.Add(n + " (field)");
                if (tFs.GetProperty(n) is { CanWrite: true } pr && pr.SetMethod!.IsPublic) writable.Add(n + " (setter)");
            }
            Require(writable.Count == 0, "the display mesh can be written piecewise: " + string.Join(", ", writable));
            Require(setM != null, "no atomic SetDisplay");
            Set(trisA, nA); string ra = Render();
            Set(trisB, nB); string rb = Render();
            Require(ra != rb, "the two meshes render the same");
            int mixed = 0, reads = 0; bool stop = false;
            var writer = new System.Threading.Thread(() =>
            {
                for (int i = 0; i < 200000 && !System.Threading.Volatile.Read(ref stop); i++) { if ((i & 1) == 0) Set(trisA, nA); else Set(trisB, nB); }
            });
            writer.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (writer.IsAlive && sw.ElapsedMilliseconds < 4000)
            {
                string r = Render(); reads++;
                if (r != ra && r != rb) mixed++;
            }
            System.Threading.Volatile.Write(ref stop, true); writer.Join();
            Set(trisA, nA);
            Log(new { test = "G6-DISPLAY-ATOMIC", reads, mixed, atomic = setM != null });
            Require(mixed == 0, $"{mixed} of {reads} renders mixed two meshes (triangles of one, normals of the other)");
        });

        Check("G6-EXACT-IMMUTABLE", () =>
        {
            using var __poly = PolyhedralPath();   // проверяется сама многогранная точная форма
            var w = new List<string>();
            var bodies = new List<TSolid> { Box() };
            BooleanOp.Subtract(bodies, Box(10, 10, 10, 5, 5, 20), w);
            var es = bodies[0].Exact!.Solid;
            int n0 = es.Polygons.Count;
            // 1. Правка исходного списка после создания тела его не меняет.
            var src = es.Polygons.ToList();
            var copy = new Parametric3D.Kernel.Exact.ExactSolid(es.Registry, src);
            src.Clear();
            Require(copy.Polygons.Count == n0, "the exact solid shares the creator's list");
            // 2. Приведение к изменяемому интерфейсу не даёт записать.
            bool threw = false;
            try { ((IList<Parametric3D.Kernel.Exact.ExactPolygon>)es.Polygons).Add(es.Polygons[0]); } catch (NotSupportedException) { threw = true; }
            Require(threw && es.Polygons.Count == n0, "the polygon list accepts writes through IList");
            threw = false;
            try { ((IList<Parametric3D.Kernel.Exact.ExactPolygon>)es.Polygons)[0] = es.Polygons[1]; } catch (NotSupportedException) { threw = true; }
            Require(threw, "the polygon list accepts element writes through IList");
            // 3. Опись: открытые поля — только для чтения и не изменяемые коллекции/массивы.
            var bad = new List<string>();
            foreach (var t in new[] { typeof(Parametric3D.Kernel.Exact.ExactSolid), typeof(Parametric3D.Kernel.Exact.ExactPolygon) })
                foreach (var fi in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    bool mutableType = fi.FieldType.IsArray || (fi.FieldType.IsGenericType && fi.FieldType.GetGenericTypeDefinition() == typeof(List<>));
                    if (!fi.IsInitOnly || mutableType) bad.Add($"{t.Name}.{fi.Name}");
                }
            Require(bad.Count == 0, "mutable public state: " + string.Join(", ", bad));
            Log(new { test = "G6-EXACT-IMMUTABLE", polygons = n0, publicFields = "readonly, no arrays or lists" });
        });

        Check("G5-GLOBALS-INVENTORY", () =>
        {
            var found = UnboundGlobals();
            var unknown = found.Where(g => !AllowedGlobals.ContainsKey(g) && !StatisticsPrefixes.Any(g.StartsWith)).ToList();
            Require(unknown.Count == 0, "global mutable state outside the kernel context: " + string.Join(", ", unknown));
            var stale = AllowedGlobals.Keys.Where(k => !found.Contains(k)).ToList();
            Require(stale.Count == 0, "allow-list entries without a field: " + string.Join(", ", stale));
            Log(new { test = "G5-GLOBALS-INVENTORY", unbound = found.Count, allowed = AllowedGlobals.Count, statistics = found.Count - AllowedGlobals.Count });
        });

        Check("G5-SETGLOBAL-INSIDE-THROWS", () =>
        {
            using (new KernelContext().Enter())
            {
                foreach (var (name, set) in new (string, Action)[]
                {
                    ("ExactFaceMesher.UseUvMesher", () => ExactFaceMesher.UseUvMesher = false),
                    ("UvMesher.AllowBands", () => UvMesher.AllowBands = false),
                    ("SolidValidator.TJunctionSeverity", () => SolidValidator.TJunctionSeverity = ValidationSeverity.Warning),
                    ("Part.ValidateResults", () => Part.ValidateResults = false),
                    ("HoleFeature.ExactHoles", () => HoleFeature.ExactHoles = false),
                })
                {
                    bool threw = false;
                    try { set(); } catch (InvalidOperationException) { threw = true; }
                    Require(threw, name + " changed inside a kernel context");
                }
            }
            Require(ExactFaceMesher.UseUvMesher && UvMesher.AllowBands && Part.ValidateResults, "global default changed");
        });

        Check("G5-CONTEXT-ISOLATION", () =>
        {
            // Два расчёта одновременно с разными снимками: каждый видит свой, общие значения не меняются.
            var baseS = KernelSettings.Capture();
            var a = new KernelContext(baseS with { UseUvMesher = false, UvAllowBands = false, PlanarInput = false, ToleranceFloor = 1e-6 })
                .WithExtension(ModelSettings.Capture() with { ExactHoles = false, ValidateResults = false });
            var b = new KernelContext(baseS with { UseUvMesher = true, UvAllowBands = true, PlanarInput = true, ToleranceFloor = 1e-7 })
                .WithExtension(ModelSettings.Capture() with { ExactHoles = true, ValidateResults = true });
            using var barrier = new Barrier(2);
            string? Run(KernelContext c, bool flag, double floor)
            {
                using (c.Enter())
                    for (int i = 0; i < 2000; i++)
                    {
                        if (i % 500 == 0) barrier.SignalAndWait();
                        if (ExactFaceMesher.UseUvMesher != flag || UvMesher.AllowBands != flag || ExactPlanarInput.Enabled != flag ||
                            Tolerances.Floor != floor || HoleFeature.ExactHoles != flag || Part.ValidateResults != flag)
                            return $"context {flag}: saw a foreign value at step {i}";
                    }
                return null;
            }
            var ta = Task.Run(() => Run(a, false, 1e-6)); var tb = Task.Run(() => Run(b, true, 1e-7));
            Require(Task.WaitAll(new Task[] { ta, tb }, 30000), "timeout");
            Require(ta.Result == null && tb.Result == null, ta.Result ?? tb.Result!);
            Require(KernelContext.Current == null && ExactFaceMesher.UseUvMesher && ExactPlanarInput.Enabled && HoleFeature.ExactHoles,
                "globals changed by contexts");
        });

        Check("G5-CONTEXT-DRIVES-RESULT", () =>
        {
            // Флаг из снимка действительно управляет вычислением: вход «грань — одна плоскость» в одном контексте
            // и прежний вход в другом — на одном и том же повёрнутом бруске.
            var box = TiltedBox(new Vector3d(.2, .7, .4).Normalized(), 0.9);
            int With(bool planar)
            {
                using (new KernelContext(KernelSettings.Capture() with { PlanarInput = planar }).Enter())
                    return TagsWithSeveralPlanes(ExactAdapter.ToExact(box, new PlaneRegistry(), new()).Solid);
            }
            int on = With(true), off = With(false);
            Log(new { test = "G5-CONTEXT-DRIVES-RESULT", tagsWithSeveralPlanes_on = on, tagsWithSeveralPlanes_off = off });
            Require(on == 0, $"planar input in context: {on} faces with several planes");
            Require(off > 0, "context switch had no effect (old input should split rotated faces)");
        });

        Check("G5-MODEL-SNAPSHOT-DURING-REBUILD", () =>
        {
            // Перестроение в фоне; на первом этапе интерфейс (вне контекста) меняет общие значения модели.
            // Расчёт обязан доработать со снимком, снятым при входе.
            bool validate0 = Part.ValidateResults, holes0 = HoleFeature.ExactHoles;
            var seen = new System.Collections.Concurrent.ConcurrentBag<(bool V, bool H)>();
            using var started = new ManualResetEventSlim(); using var flipped = new ManualResetEventSlim();
            var outer = new KernelContext().WithStageHook(_ =>
            {
                var ms = ModelSettings.Current;
                if (ms != null) seen.Add((ms.ValidateResults, ms.ExactHoles));
                if (!started.IsSet) { started.Set(); flipped.Wait(10000); }
            });
            var part = TreeVariant(1);
            var job = Task.Run(() => { using (outer.Enter()) part.Apply(part.Compute(null)); });
            try
            {
                Require(started.Wait(30000), "rebuild did not reach a stage");
                Part.ValidateResults = !validate0; HoleFeature.ExactHoles = !holes0;    // правка «из интерфейса»
                flipped.Set();
                Require(job.Wait(60000), "rebuild timeout");
            }
            finally { flipped.Set(); job.Wait(60000); Part.ValidateResults = validate0; HoleFeature.ExactHoles = holes0; }
            Require(seen.Count > 0, "no model snapshot visible inside the rebuild");
            Require(seen.All(x => x.V == validate0 && x.H == holes0),
                $"rebuild saw changed model settings: {string.Join(",", seen.Distinct())}");
            Log(new { test = "G5-MODEL-SNAPSHOT-DURING-REBUILD", stages = seen.Count });
        });
    }
}
