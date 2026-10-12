using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// KRN-01 / ВОРОТА g.06: «Пустой результат, неподдержанный случай, некорректный вход и внутренний сбой различаются» — во ВСЕХ
    /// операциях детали, не только в булевой. Прежде: (1) у операций через OperationContract (скругления, фаски, оболочка, уклон,
    /// ребро, вершины) Outcome при отказе не ставился и оставался Success; (2) отказ операции — строка без типа (99 мест);
    /// (3) у детали типизированного исхода операции не было вовсе.
    /// </summary>
    static void Krn01Gates()
    {
        Krn01GateG03();
        Check("KRN01-G06-OUTCOMES-ALL-OPERATIONS", () =>
        {
            // Некорректный вход у каждого вида операции: исход InvalidInput, тело не тронуто.
            var invalid = new (string Name, Func<Feature> Make)[]
            {
                ("extrude (no sketch)", () => new ExtrudeFeature { SketchId = 987654 }),
                ("revolve (no sketch)", () => new RevolveFeature { SketchId = 987654 }),
                ("round (no edges)", () => new RoundFeature()),
                ("chamfer (no edges)", () => new ChamferFeature()),
                ("draft (no faces)", () => new DraftFeature()),
                ("rib (no sketch)", () => new RibFeature { SketchId = 987654 }),
                ("mirror (no plane)", () => new MirrorFeature { Normal = Vector3d.Zero }),
                ("linear pattern (count 1)", () => new LinearPatternFeature { Count = new ValueRef(1) }),
                ("circular pattern (count 1)", () => new CircularPatternFeature { Count = new ValueRef(1) }),
                ("vertex round (R 0)", () => new VertexRoundFeature { Vertex = new Vector3d(30, 30, 30), Radius = new ValueRef(0) }),
                ("vertex chamfer (no vertex)", () => new VertexChamferFeature { Vertex = new Vector3d(1, 2, 3) }),
                ("hole (diameter 0)", () => new HoleFeature { Diameter = new ValueRef(0) }),
                ("sweep (no section)", () => new SweepFeature { SectionSketchId = 987654 }),
                ("loft (one section)", () => new LoftFeature()),
                ("shell (thickness 0)", () => new ShellFeature { Thickness = new ValueRef(0) }),
            };
            foreach (var (name, make) in invalid)
            {
                var p = new Part("g06"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
                double v0 = p.Volume; int b0 = p.Bodies.Count;
                var f = p.AddFeature(make());
                var r = p.Compute(null); p.Apply(r);
                Require(r.Errors[f] != null, $"{name}: accepted");
                Require(r.Outcomes.TryGetValue(f, out var o) && o == OperationOutcome.InvalidInput, $"{name}: outcome {(r.Outcomes.TryGetValue(f, out var o2) ? o2 : "none")}, error «{r.Errors[f]}»");
                Require(p.Bodies.Count == b0 && Math.Abs(p.Volume - v0) < 1e-9, $"{name}: the body changed on refusal");
            }

            // Неподдержанный случай: политика PreserveBRep, вырез призмой над сплайнами (точного маршрута нет — только
            // многогранный). До 2026-10-09 — вырез шаром, до 2026-10-11 — тором; оба теперь в точном маршруте.
            {
                using var _c = new KernelContext(KernelSettings.Capture() with { GeometryPolicy = GeometryPolicy.PreserveBRep }).Enter();
                var p = new Part("g06u"); TestGeometry.AddCube(p, 60); p.Rebuild(null); double v0 = p.Volume;
                var f = p.AddFeature(new CutSplinePrism(30, 30, 30, 10, 6, 8));
                var r = p.Compute(null); p.Apply(r);
                Require(r.Outcomes.TryGetValue(f, out var o) && o == OperationOutcome.Unsupported, $"preserve-B-rep spline-prism cut: outcome {(r.Outcomes.TryGetValue(f, out var o2) ? o2 : "none")}, «{r.Errors.GetValueOrDefault(f)}»");
                Require(Math.Abs(p.Volume - v0) < 1e-9, "the body changed on an unsupported case");
            }
            // Пустой результат: вырез, охватывающий всё тело, — штатный Empty, не ошибка.
            {
                var p = new Part("g06e"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
                var f = p.AddFeature(new CutBox(-100, -100, -100, 300, 300, 300));
                var r = p.Compute(null); p.Apply(r);
                Require(r.Errors[f] == null && r.Outcomes.TryGetValue(f, out var o) && o == OperationOutcome.Empty && p.Bodies.Count == 0,
                    $"empty cut: error «{r.Errors[f]}», outcome {(r.Outcomes.TryGetValue(f, out var o2) ? o2 : "none")}, bodies {p.Bodies.Count}");
            }
            // Внутренний сбой: исключение операции (не отказ с типом) — InternalFailure, тело не тронуто.
            {
                var p = new Part("g06i"); TestGeometry.AddCube(p, 60); p.Rebuild(null); double v0 = p.Volume;
                var f = p.AddFeature(new CrashFeature());
                var r = p.Compute(null); p.Apply(r);
                Require(r.Outcomes.TryGetValue(f, out var o) && o == OperationOutcome.InternalFailure && Math.Abs(p.Volume - v0) < 1e-9,
                    $"crash: outcome {(r.Outcomes.TryGetValue(f, out var o2) ? o2 : "none")}");
            }
            // Успех.
            {
                var p = new Part("g06s"); TestGeometry.AddCube(p, 60); var r = p.Compute(null); p.Apply(r);
                Require(r.Outcomes.Count > 0 && r.Outcomes.Values.All(o => o == OperationOutcome.Success), "success: " + string.Join(",", r.Outcomes.Values));
            }
            // Ядро: отказ операции через контракт — тип исхода по исключению (прежде — Success по умолчанию).
            {
                var start = new List<TSolid> { Box() };
                var r1 = OperationContract.Run("Round", start, b => throw new Parametric3D.Core.RebuildException(OperationOutcome.InvalidInput, "bad radius"));
                var r2 = OperationContract.Run("Round", start, b => throw new InvalidOperationException("crash"));
                var r3 = OperationContract.Run("Round", start, b => b.Clear());
                Require(!r1.Accepted && r1.Outcome == OperationOutcome.InvalidInput, "contract invalid: " + r1.Outcome);
                Require(!r2.Accepted && r2.Outcome == OperationOutcome.InternalFailure, "contract crash: " + r2.Outcome);
                Require(r3.Accepted && r3.Outcome == OperationOutcome.Empty, "contract empty: " + r3.Outcome);
            }
        });
        // ВОРОТА g.10: «Отмена/ошибка не повреждает вход, историю и опубликованное состояние» — для всех видов операций.
        // Перебор точек прерывания: у каждой операции каталога — все её границы этапов (крючок контекста), в каждой — отмена
        // и сбой. После прерывания: опубликованные тела детали те же (отпечаток), правка тела на месте запрещена
        // (VerifyPublishedBodies), применённый отказ откатывает к состоянию до операции, а следующий чистый расчёт даёт тот
        // же результат, что расчёт без прерываний (сохранённые состояния и кэши не испорчены).
        Check("KRN01-G10-INTERRUPT-ALL-OPERATIONS", () =>
        {
            var cases = K09Cases().Where(c => !double.IsNaN(c.Expected)).Select(c => (c.Name, c.Build)).ToList();
            cases.Add(("edge round R4", () => RoundTree(4).P));
            cases.Add(("edge chamfer 3", () =>
            {
                var p = new Part("ch"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
                var cf = new ChamferFeature { D1 = new ValueRef(3), D2 = new ValueRef(3) }; cf.Edges.Add(EdgeRef.Of(CornerEdge(p, 30, 30))); p.AddFeature(cf); return p;
            }));
            cases.Add(("hole d8 depth 20", () =>
            {
                var p = new Part("hole"); TestGeometry.AddCube(p, 60);
                var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(8), Depth = new ValueRef(20) }; h.Points.Add(new Vector3d(5, 5, 30)); p.AddFeature(h); return p;
            }));
            cases.Add(("vertex round R5", () => { var p = new Part("vr"); TestGeometry.AddCube(p, 60); p.AddFeature(new VertexRoundFeature { Vertex = new Vector3d(30, 30, 30), Radius = new ValueRef(5) }); return p; }));
            cases.Add(("vertex chamfer 5", () => { var p = new Part("vc"); TestGeometry.AddCube(p, 60); p.AddFeature(new VertexChamferFeature { Vertex = new Vector3d(30, 30, 30), Distance = new ValueRef(5) }); return p; }));
            cases.Add(("boolean sphere cut", () => { var p = new Part("bs"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutSphere(30, 30, 30, 10)); return p; }));
            cases.Add(("boolean torus cut", () => { var p = new Part("bt"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutTorus(30, 30, 30, 10, 3)); return p; }));
            cases.Add(("boolean spline-prism cut", () => { var p = new Part("bsp"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutSplinePrism(30, 30, 30, 10, 6, 8)); return p; }));

            bool verifyWas = Part.VerifyPublishedBodies; Part.VerifyPublishedBodies = true;
            int interruptions = 0, escaped = 0, refused = 0;
            var report = new List<string>();
            try
            {
                foreach (var (name, build) in cases)
                {
                    // эталон: расчёт без прерываний
                    var refP = build(); var refR = refP.Compute(null); refP.Apply(refR);
                    if (refP.Features.Any(f => f.Error != null)) { report.Add($"{name}: reference build fails"); continue; }
                    double refVol = refP.Volume; int refBodies = refP.Bodies.Count;
                    // сколько границ этапов у последней операции
                    var cp = build(); var target = cp.Features.Last(); target.Suppressed = true; cp.Apply(cp.Compute(null)); target.Suppressed = false;
                    int hits = 0;
                    using (new KernelContext().WithStageHook(_ => hits++).Enter()) cp.Compute(null);
                    var ks = Enumerable.Range(0, Math.Min(hits, 10)).Select(i => hits <= 10 ? i : (int)Math.Round(i * (hits - 1) / 9.0)).Distinct().ToList();
                    foreach (bool cancel in new[] { true, false })
                        foreach (int k in ks)
                        {
                            interruptions++;
                            var p = build(); var t = p.Features.Last(); t.Suppressed = true; p.Apply(p.Compute(null)); t.Suppressed = false;
                            var before = p.Bodies.Select(SolidFingerprint.Authoritative).ToList(); double v0 = p.Volume;
                            var held = p.Bodies.ToList();
                            int seen = 0; string lastStage = "?"; Part.BuildResult? r = null; Exception? thrown = null;
                            try
                            {
                                using (new KernelContext().WithStageHook(st => { if (seen++ == k) { lastStage = st; if (cancel) throw new OperationCanceledException("test cancel at " + st); throw new InvalidOperationException("test fault at " + st); } }).Enter())
                                    r = p.Compute(null);
                            }
                            catch (Exception ex) { thrown = ex; }
                            string where = $"{name} {(cancel ? "cancel" : "fault")} #{k}/{hits} at {lastStage}";
                            // опубликованное не тронуто
                            var after = held.Select(SolidFingerprint.Authoritative).ToList();
                            if (!before.SequenceEqual(after)) { report.Add($"{where}: a published body changed"); continue; }
                            if (thrown != null)
                            {
                                escaped++;
                                if (cancel && thrown is not OperationCanceledException) { report.Add($"{where}: cancellation surfaced as {thrown.GetType().Name}"); continue; }
                                if (!p.Bodies.SequenceEqual(held) || Math.Abs(p.Volume - v0) > 1e-9) { report.Add($"{where}: part state changed after an aborted build"); continue; }
                            }
                            else
                            {
                                if (r!.Errors.GetValueOrDefault(t) != null)
                                {
                                    refused++;
                                    if (r.IntegrityError != null) { report.Add($"{where}: integrity error {r.IntegrityError}"); continue; }
                                    p.Apply(r);
                                    if (Math.Abs(p.Volume - v0) > 1e-6 * Math.Max(1, v0)) { report.Add($"{where}: a refused operation left a trace (volume {v0:G10} → {p.Volume:G10})"); continue; }
                                }
                                else
                                {
                                    // сбой вне операции (разметка в конце) обязан быть виден: ошибка целостности
                                    if (!cancel && r.IntegrityError == null && Math.Abs(r.Volume - refVol) > 1e-6 * Math.Max(1, refVol))
                                    { report.Add($"{where}: a silent wrong result (volume {r.Volume:G10} vs {refVol:G10}, no error)"); continue; }
                                    p.Apply(r);
                                }
                            }
                            // восстановление: чистый расчёт = эталон
                            var r2 = p.Compute(null); p.Apply(r2);
                            if (p.Features.Any(f => f.Error != null) || p.Bodies.Count != refBodies || Math.Abs(p.Volume - refVol) > 1e-6 * Math.Max(1, refVol))
                                report.Add($"{where}: the next clean build differs: volume {p.Volume:G10} vs {refVol:G10}, bodies {p.Bodies.Count} vs {refBodies}, errors {string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => f.Error))}");
                        }
                }
            }
            finally { Part.VerifyPublishedBodies = verifyWas; }
            Console.WriteLine($"G10: operations {cases.Count}, interruptions {interruptions}, escaped {escaped}, refused {refused}, problems {report.Count}");
            foreach (var line in report) Console.WriteLine("G10PROBLEM " + line);
            Require(report.Count == 0, string.Join(" | ", report.Take(6)));
        });
    }

    /// <summary>Вырез тором (ось Z): с 2026-10-11 — точный маршрут.</summary>
    internal sealed class CutTorus(double x, double y, double z, double ring, double tube) : Feature
    {
        public override void Execute(BuildContext c) => BooleanOp.Subtract(c.Bodies, PrimitiveOp.Torus(new Vector3d(x, y, z), ring, tube), c.Warnings);
    }

    /// <summary>Вырез призмой над сплайнами: точного маршрута нет — многогранный путь аналитического операнда.</summary>
    internal sealed class CutSplinePrism(double x, double y, double z, double rx, double ry, double h) : Feature
    {
        public override void Execute(BuildContext c) => BooleanOp.Subtract(c.Bodies, SplinePrism(new Vector3d(x, y, z), rx, ry, h), c.Warnings);
    }

    /// <summary>Лазейка политики: операция сама переключает контекст на AllowBoundedFaceting и вырезает тор (многогранный путь).</summary>
    sealed class PolicyBypassFeature : Feature
    {
        public override void Execute(BuildContext c)
        {
            using var _k = new KernelContext(KernelSettings.Capture() with { GeometryPolicy = GeometryPolicy.AllowBoundedFaceting }).Enter();
            BooleanOp.Subtract(c.Bodies, SplinePrism(new Vector3d(30, 30, 30), 10, 6, 8), c.Warnings);
        }
    }

    static void Krn01GateG03()
    {
        // ВОРОТА g.03: «Аналитическая/B-rep-политика не допускает скрытого перехода к фасетам». (1) Каталог под PreserveBRep:
        // успех — только с телами из точных граней и рёбер (по аудиту B-rep, не по ярлыку), иначе отказ Unsupported без следа;
        // (2) лазейка — операция со своим контекстом политики: при PreserveBRep детали — отказ, при AllowBoundedFaceting —
        // видимое понижение (GeometryDowngrades, предупреждение); (3) повторный расчёт без правок отдаёт те же исходы.
        // Мутация P3D_GEOMGUARD=0 — лазейка проходит.
        Check("KRN01-G03-NO-HIDDEN-FACETING", () =>
        {
            int exact = 0, refused = 0;
            foreach (var (name, build) in GateCatalogue())
            {
                using var _c = new KernelContext(KernelSettings.Capture() with { GeometryPolicy = GeometryPolicy.PreserveBRep }).Enter();
                var p = build(); var f = p.Features.Last();
                f.Suppressed = true; p.Apply(p.Compute(null)); double v0 = p.Volume; f.Suppressed = false;
                var r = p.Compute(null); p.Apply(r);
                if (r.Errors.GetValueOrDefault(f) != null)
                {
                    refused++;
                    Require(r.Outcomes.GetValueOrDefault(f) == OperationOutcome.Unsupported, $"{name}: refusal outcome {r.Outcomes.GetValueOrDefault(f)}: {r.Errors[f]}");
                    Require(Math.Abs(p.Volume - v0) < 1e-9 * Math.Max(1, v0), $"{name}: refusal left a trace");
                }
                else
                {
                    exact++;
                    foreach (var b in p.Bodies)
                        Require(GeometryAudit.Faceted(b) == (0, 0), $"{name}: success with faceted geometry under PreserveBRep ({GeometryAudit.Faceted(b)})");
                }
                var r2 = p.Compute(null);
                Require(r2.Outcomes.GetValueOrDefault(f) == r.Outcomes.GetValueOrDefault(f), $"{name}: outcome lost on the unchanged rebuild");
            }
            Console.WriteLine($"G03: operations {exact + refused}, exact {exact}, refused {refused}");
            // лазейка
            {
                using var _c = new KernelContext(KernelSettings.Capture() with { GeometryPolicy = GeometryPolicy.PreserveBRep }).Enter();
                var p = new Part("bypass"); TestGeometry.AddCube(p, 60); p.Rebuild(null); double v0 = p.Volume;
                var f = p.AddFeature(new PolicyBypassFeature()); var r = p.Compute(null); p.Apply(r);
                Require(r.Errors.GetValueOrDefault(f) != null && r.Outcomes.GetValueOrDefault(f) == OperationOutcome.Unsupported && Math.Abs(p.Volume - v0) < 1e-9,
                    $"policy bypass under PreserveBRep: outcome {r.Outcomes.GetValueOrDefault(f)}, error «{r.Errors.GetValueOrDefault(f)}», volume {p.Volume:G10} vs {v0:G10}");
            }
            {
                var p = new Part("bypass2"); TestGeometry.AddCube(p, 60);
                var f = p.AddFeature(new PolicyBypassFeature()); var r = p.Compute(null); p.Apply(r);
                Require(r.Errors.GetValueOrDefault(f) == null && r.GeometryDowngrades.ContainsKey(f) && r.Warnings.Any(w => w.StartsWith("Geometry downgrade")),
                    $"downgrade under AllowBoundedFaceting is not reported: {string.Join(",", r.GeometryDowngrades.Keys.Select(k => k.Name))}");
            }
        });
    }

    sealed class CrashFeature : Feature
    {
        public override void Execute(BuildContext c) => throw new InvalidOperationException("simulated crash");
    }
}

static partial class Program
{
    /// <summary>Каталог для ворот g.10 и g.03: операции K09 + скругление/фаска ребра, отверстие, вершины, вырез шаром.</summary>
    internal static List<(string Name, Func<Part> Build)> GateCatalogue()
    {
        var cases = K09Cases().Where(c => !double.IsNaN(c.Expected)).Select(c => (c.Name, c.Build)).ToList();
        cases.Add(("edge round R4", () => RoundTree(4).P));
        cases.Add(("edge chamfer 3", () =>
        {
            var p = new Part("ch"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
            var cf = new ChamferFeature { D1 = new ValueRef(3), D2 = new ValueRef(3) }; cf.Edges.Add(EdgeRef.Of(CornerEdge(p, 30, 30))); p.AddFeature(cf); return p;
        }));
        cases.Add(("hole d8 depth 20", () =>
        {
            var p = new Part("hole"); TestGeometry.AddCube(p, 60);
            var h = new HoleFeature("H") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(8), Depth = new ValueRef(20) }; h.Points.Add(new Vector3d(5, 5, 30)); p.AddFeature(h); return p;
        }));
        cases.Add(("vertex round R5", () => { var p = new Part("vr"); TestGeometry.AddCube(p, 60); p.AddFeature(new VertexRoundFeature { Vertex = new Vector3d(30, 30, 30), Radius = new ValueRef(5) }); return p; }));
        cases.Add(("vertex chamfer 5", () => { var p = new Part("vc"); TestGeometry.AddCube(p, 60); p.AddFeature(new VertexChamferFeature { Vertex = new Vector3d(30, 30, 30), Distance = new ValueRef(5) }); return p; }));
        cases.Add(("boolean sphere cut", () => { var p = new Part("bs"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutSphere(30, 30, 30, 10)); return p; }));
        cases.Add(("boolean torus cut", () => { var p = new Part("bt"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutTorus(30, 30, 30, 10, 3)); return p; }));
        cases.Add(("boolean spline-prism cut", () => { var p = new Part("bsp"); TestGeometry.AddCube(p, 60); p.AddFeature(new CutSplinePrism(30, 30, 30, 10, 6, 8)); return p; }));
        return cases;
    }

    /// <summary>Замер ворот g.03: операции под PreserveBRep — исход, уровень тел, доля точных граней и рёбер.</summary>
    static void Krn01MeasureG03()
    {
        foreach (var policy in new[] { GeometryPolicy.AllowBoundedFaceting, GeometryPolicy.PreserveBRep })
        foreach (var (name, build) in GateCatalogue())
        {
            using var _c = new KernelContext(KernelSettings.Capture() with { GeometryPolicy = policy }).Enter();
            var p = build(); var r = p.Compute(null); p.Apply(r);
            var f = p.Features.Last();
            var o = r.Outcomes.TryGetValue(f, out var oo) ? oo.ToString() : "-";
            var bodies = string.Join(" ", p.Bodies.Select(b => { var a = GeometryAudit.Of(b); return $"[{b.Guarantee} faces {a.AnalyticFaces}/{a.Faces.Count} edges {a.AnalyticEdges}/{a.Edges.Count}]"; }));
            bool allExact = p.Bodies.All(b => GeometryAudit.Of(b).FullyAnalytic);
            string verdict = r.Errors.GetValueOrDefault(f) != null ? "REFUSED" : allExact ? "EXACT" : "FACETED";
            Console.WriteLine($"G03 {policy} {verdict} {name}: outcome {o} {bodies} {(r.Errors.GetValueOrDefault(f) is { } e ? "err: " + e[..Math.Min(120, e.Length)] : "")}");
        }
    }
}
