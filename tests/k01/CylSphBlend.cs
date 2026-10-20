using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// 17:31 (FreeForm пользователя): ЦИЛИНДР R15 H40 ∪ ШАР R20, цилиндр смещён (x = 8, z = 5) — его дно частью выходит из шара,
    /// кривая пересечения — незамкнутое ребро, кончающееся на торце (вершины с дном цилиндра). Скругление R5 и фаска 3 этого
    /// ребра — прямым построителем (продление ребра за торец ходом по A ∩ B, P3D_DROPENGENERIC, P3D_DRMARCH): тело годное,
    /// точное, объём растёт (ребро вогнутое). Скругление верхней кромки цилиндра (окружность плоскость/цилиндр, у которого
    /// второй край — кривая пересечения) — общим построителем замкнутых рёбер (P3D_DRCIRCLECLOSED), точно: снятый объём —
    /// по Паппу 2π(R − x̄)·r²(1 − π/4), x̄ = r(10 − 3π)/(3(4 − π)). Ни одна операция не падает исключением (NaN).
    /// </summary>
    static void CylSphBlendChecks()
    {
        Check("KRN06-FF-CYL-SPHERE-BLENDS", () =>
        {
            var fails = new List<string>();
            var cyl = FFPrimitiveFeature.Make(FFPrimitive.Cylinder, new Frame3(new Vector3d(8, 0, 5), Vector3d.UnitX, Vector3d.UnitY), 15, 40, 0);
            var sph = FFPrimitiveFeature.Make(FFPrimitive.Sphere, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 20, 0, 0);
            var list = new List<TSolid> { cyl };
            BooleanOp.Unite(list, sph, new List<string>());
            Require(list.Count == 1, "union is one body");
            var body = list[0];
            double v0 = AnalyticVolume.Of(body) ?? OpVolume(body);
            string Kind(TEdge e) => string.Join("/", e.CoEdges.Select(c => GeometryAudit.AuthoritativeSurface(c.Loop.Face)?.GetType().Name).OrderBy(x => x));
            var edges = body.Edges.ToList();
            int iInter = edges.FindIndex(e => Kind(e) == "ExtrudedSurface/RevolvedSurface" && !ReferenceEquals(e.V0, e.V1));
            int iTop = edges.FindIndex(e => Kind(e) == "ExtrudedSurface/PlaneSurface" && ReferenceEquals(e.V0, e.V1));
            Require(iInter >= 0 && iTop >= 0, $"edges: intersection {iInter}, top {iTop} ({string.Join(", ", edges.Select(Kind))})");
            long endPts = 0, tabSecs = 0, parTabs = 0, loopPar = 0, auditPar = 0, volPar = 0, openPar = 0;
            (TSolid? B, string Msg, double Ms) Run(int ei, bool round, double d)
            {
                var b = new List<TSolid> { TopologyClone.Deep(body) }; var w = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long e0 = DirectRound.OpenEndPoints, ts0 = Parametric3D.Kernel.Surfaces.SectionSurface.TableSections, pt0 = Parametric3D.Kernel.Surfaces.SectionSurface.ParallelTables, lp0 = Parametric3D.Kernel.Export.StepImport.LoopMeshParallelRuns, ap0 = Parametric3D.Kernel.Topology.GeometryAudit.ParallelRuns, vp0 = Parametric3D.Kernel.Topology.AnalyticVolume.ParallelRuns, op0 = DirectRound.OpenParallelRuns;
                try
                {
                    var er = new List<EdgeRef> { EdgeRef.Of(b[0].Edges.ElementAt(ei)) };
                    // четыре потока, как в приложении (у проверок по умолчанию — один)
                    using (new KernelContext(KernelSettings.Capture() with { BooleanThreads = 4, MeshThreads = 4 }).Enter())
                        if (round) FilletOp.RoundEdges(b, er, d, w); else FilletOp.ChamferEdges(b, er, d, d, w);
                    return (b[0], string.Join("; ", w.Take(2)), sw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) { return (null, ex.Message, sw.Elapsed.TotalMilliseconds); }
                finally { endPts = DirectRound.OpenEndPoints - e0; tabSecs = Parametric3D.Kernel.Surfaces.SectionSurface.TableSections - ts0; parTabs = Parametric3D.Kernel.Surfaces.SectionSurface.ParallelTables - pt0; loopPar = Parametric3D.Kernel.Export.StepImport.LoopMeshParallelRuns - lp0; auditPar = Parametric3D.Kernel.Topology.GeometryAudit.ParallelRuns - ap0; volPar = Parametric3D.Kernel.Topology.AnalyticVolume.ParallelRuns - vp0; openPar = DirectRound.OpenParallelRuns - op0; }
            }
            foreach (var (name, ei, round, d) in new[] { ("round intersection R5", iInter, true, 5.0), ("chamfer intersection 3", iInter, false, 3.0), ("round top R5", iTop, true, 5.0) })
            {
                var (b, msg, ms) = Run(ei, round, d);
                if (b == null) { fails.Add($"{name}: {msg}"); if (msg.Contains("Not-a-Number")) fails.Add($"{name}: NaN exception"); continue; }
                // СКОРОСТЬ (счётчики, не время): (1) проверка тела после операции — из памяти: построитель проверил то же тело,
                // а ключ памяти не зависит от отложенной разметки (P3D_VALKEYDISPLAY); (2) край ленты на торце —
                // точными точками только для вставки (P3D_DRFITCURVES); (3) p-кривые процедурных рёбер — одной выборкой
                // проекции (P3D_PCPROCFIRST)
                long hits0 = SolidValidator.ValidationCacheHits;
                using (new KernelContext(KernelSettings.Capture() with { DeferMeshing = true }).Enter()) SolidValidator.Validate(b);
                bool cached = SolidValidator.ValidationCacheHits > hits0;
                long smp0 = PCurves.Samplings;
                PCurves.Measure(b);
                long samplings = PCurves.Samplings - smp0; int coedges = b.Faces.Sum(f => f.Loops.Sum(l => l.CoEdges.Count));
                Log(new { test = "KRN06-FF-CYL-SPHERE-BLENDS", name, cached, endPts, samplings, coedges });
                if (!cached) fails.Add($"{name}: validation after the operation is not taken from memory");
                if (ei == iInter && endPts > 1500) fails.Add($"{name}: {endPts} exact end-curve points (curves not fitted?)");
                // 20:17: таблица формулы ленты — пятой степени, шаг крупнее (P3D_SECHIGH); квадратура ленты — по сходимости
                // (P3D_BANDADAPT); площадь грани — из памяти (P3D_AREACACHE); сетка той же грани копии тела — из памяти (P3D_FACEMESHMEMO)
                if (ei == iInter)
                {
                    var cl = TopologyClone.Deep(b);
                    long bp0 = AnalyticVolume.BandPoints; AnalyticVolume.Of(cl); long bandPts = AnalyticVolume.BandPoints - bp0;
                    var bf = cl.Faces.First(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface);
                    long ac0 = AnalyticVolume.AreasComputed; AnalyticVolume.FaceArea(bf); AnalyticVolume.FaceArea(bf); long areas = AnalyticVolume.AreasComputed - ac0;
                    var mw = new List<string>();
                    BooleanOp.MeshExactlySolid(TopologyClone.Deep(b), mw);
                    long mh0 = Parametric3D.Kernel.Tess.ExactFaceMesher.FaceMemoHits; BooleanOp.MeshExactlySolid(TopologyClone.Deep(b), mw); long memoHits = Parametric3D.Kernel.Tess.ExactFaceMesher.FaceMemoHits - mh0;
                    Log(new { test = "KRN06-FF-CYL-SPHERE-BLENDS", name, tabSecs, bandPts, areas, memoHits });
                    if (tabSecs > 4000) fails.Add($"{name}: {tabSecs} exact sections for the formula tables");
                    // таблица ленты — в несколько потоков (P3D_SECPAR), если их больше одного
                    if (Environment.ProcessorCount > 1 && parTabs < 1) fails.Add($"{name}: the formula table was not computed in parallel");
                    // 20:17: грани результата размечаются параллельно (P3D_LOOPMESHPAR=0 — по одной)
                    if (Environment.ProcessorCount > 1 && loopPar < 1) fails.Add($"{name}: faces were not meshed in parallel");
                    // аудит геометрии (P3D_AUDITPAR=0) и объём (P3D_VOLPAR=0) — по рёбрам/граням параллельно
                    if (Environment.ProcessorCount > 1 && auditPar < 1) fails.Add($"{name}: the geometry audit was not parallel");
                    if (Environment.ProcessorCount > 1 && volPar < 1) fails.Add($"{name}: the volume was not computed in parallel");
                    if (Environment.ProcessorCount > 1 && name.Contains("intersection") && openPar < 1) fails.Add($"{name}: the open builder probes were not parallel");
                    if (bandPts > 60000) fails.Add($"{name}: {bandPts} band quadrature points for the volume");
                    if (areas != 1) fails.Add($"{name}: face area computed {areas} times for two requests");
                    if (memoHits < 1) fails.Add($"{name}: no face mesh taken from memory for a copy of the body");
                    // 20:17: разбор той же сетки (тело и его копия, сетка показа — из памяти граней) — из памяти (P3D_MESHVALMEMO=0)
                    var md1 = Parametric3D.Kernel.Tess.Tessellator.Tessellate(TopologyClone.Deep(b)); var md2 = Parametric3D.Kernel.Tess.Tessellator.Tessellate(TopologyClone.Deep(b));
                    var r1 = SolidValidator.ValidateMesh(md1); long mv0 = SolidValidator.MeshValMemoHits; var r2 = SolidValidator.ValidateMesh(md2);
                    if (SolidValidator.MeshValMemoHits <= mv0) fails.Add($"{name}: the same mesh was analysed again (no memory)");
                    if (r1.Summary() != r2.Summary() || r1.Metrics.Count != r2.Metrics.Count) fails.Add($"{name}: mesh analysis from memory differs");
                }
                if (samplings > coedges + 2) fails.Add($"{name}: {samplings} p-curve samplings for {coedges} coedges");
                var val = SolidValidator.Validate(b);
                double v1 = AnalyticVolume.Of(b) ?? double.NaN;
                Log(new { test = "KRN06-FF-CYL-SPHERE-BLENDS", name, ms = Math.Round(ms), v0, v1 = v1.ToString("R"), guarantee = b.Guarantee.ToString(), valid = val.Summary(), msg });
                if (!val.IsValid) fails.Add($"{name}: {val.Summary()}");
                if (b.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {b.Guarantee}");
                if (ei == iInter)
                {
                    // вогнутое ребро: материал добавлен; оценка сверху — r²·длина (квадрат сечения)
                    double len = edges[iInter].Curve.Length, dv = v1 - v0;
                    if (!(dv > 0 && dv < d * d * len * 1.5)) fails.Add($"{name}: volume change {dv:G6} (edge length {len:F2})");
                }
                else
                {
                    // кромка плоскость/цилиндр — тор (P3D_DRCIRCLEANY), а не лента по формуле
                    if (!b.Faces.Any(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.RevolvedSurface { Base: Parametric3D.Kernel.Curves.ArcCurve3 }))
                        fails.Add($"{name}: the blend is not a torus");
                    if (b.Faces.Count() != body.Faces.Count() + 1) fails.Add($"{name}: {b.Faces.Count()} faces (one torus band expected)");
                    double R = 15, r = d, xb = r * (10 - 3 * Math.PI) / (3 * (4 - Math.PI));
                    double want = 2 * Math.PI * (R - xb) * r * r * (1 - Math.PI / 4);
                    if (!(Math.Abs(v0 - v1 - want) < 1e-6 * want)) fails.Add($"{name}: removed {v0 - v1:R} want {want:R}");
                }
            }
            // цилиндр с поперечным отверстием в стенке (грань цилиндра — не пояс из двух окружностей): кромка — общим
            // построителем замкнутых рёбер (P3D_DRCIRCLECLOSED), точно
            {
                var c2 = FFPrimitiveFeature.Make(FFPrimitive.Cylinder, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 15, 40, 0);
                var hole = FFPrimitiveFeature.Make(FFPrimitive.Cylinder, new Frame3(new Vector3d(0, 0, 15), Vector3d.UnitY, Vector3d.UnitZ), 3, 40, 0);
                var l2 = new List<TSolid> { c2 }; BooleanOp.Subtract(l2, hole, new List<string>());
                var top = l2[0].Edges.ToList().FindIndex(e => ReferenceEquals(e.V0, e.V1) && e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Frame.Origin.Z - 40) < 1e-9);
                Require(top >= 0, "holed cylinder: top edge");
                var b2 = new List<TSolid> { l2[0] }; var w2 = new List<string>();
                double v20 = AnalyticVolume.Of(l2[0]) ?? double.NaN;
                try { FilletOp.RoundEdges(b2, new List<EdgeRef> { EdgeRef.Of(l2[0].Edges.ElementAt(top)) }, 4, w2); }
                catch (Exception ex) { fails.Add("holed cylinder round: " + ex.Message); }
                double R = 15, r = 4, xb = r * (10 - 3 * Math.PI) / (3 * (4 - Math.PI)), want = 2 * Math.PI * (R - xb) * r * r * (1 - Math.PI / 4);
                double v21 = AnalyticVolume.Of(b2[0]) ?? double.NaN;
                Log(new { test = "KRN06-FF-CYL-SPHERE-BLENDS", name = "holed cylinder round top R4", guarantee = b2[0].Guarantee.ToString(), removed = (v20 - v21).ToString("R"), want, msg = string.Join("; ", w2.Take(2)) });
                if (b2[0].Guarantee != GuaranteeLevel.ExactAnalytic || !(Math.Abs(v20 - v21 - want) < 1e-6 * want)) fails.Add($"holed cylinder round: guarantee {b2[0].Guarantee}, removed {v20 - v21:R} want {want:R}");
            }
            // ряд размеров ребра пересечения (в том числе умолчания команд: скругление R2, фаска 1 — фаска 1 отказывала:
            // «blend range beyond the extension» не вёл к повтору с опорной кривой)
            foreach (var (rnd, sz) in new[] { (true, 0.5), (true, 1.0), (true, 2.0), (true, 3.0), (true, 8.0), (false, 0.5), (false, 1.0), (false, 2.0), (false, 5.0) })
            {
                var (bq, msgq, _) = Run(iInter, rnd, sz);
                if (bq == null) { fails.Add($"{(rnd ? "round" : "chamfer")} {sz} on the intersection: {msgq}"); continue; }
                if (bq.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{(rnd ? "round" : "chamfer")} {sz} on the intersection: guarantee {bq.Guarantee}");
                double vq = AnalyticVolume.Of(bq) ?? double.NaN;
                if (!(vq > v0)) fails.Add($"{(rnd ? "round" : "chamfer")} {sz} on the intersection: volume {vq:R} not above {v0:R}");
            }
            // прогрев ядра (малая сцена: цилиндр R6 ∪ шар R8): ребро пересечения R1 — продление по углу отказывает внутри скобки
            // торца (сечение не находится); прежде — исключение Math.Sign(NaN) на всю операцию, теперь — повтор с опорной кривой
            {
                var c3 = FFPrimitiveFeature.Make(FFPrimitive.Cylinder, new Frame3(new Vector3d(3, 0, 2), Vector3d.UnitX, Vector3d.UnitY), 6, 16, 0);
                var s3 = FFPrimitiveFeature.Make(FFPrimitive.Sphere, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 8, 0, 0);
                var l3 = new List<TSolid> { c3 }; BooleanOp.Unite(l3, s3, new List<string>());
                int i3 = l3[0].Edges.ToList().FindIndex(e => Kind(e) == "ExtrudedSurface/RevolvedSurface" && !ReferenceEquals(e.V0, e.V1));
                var b3 = new List<TSolid> { l3[0] }; var w3 = new List<string>();
                try { FilletOp.RoundEdges(b3, new List<EdgeRef> { EdgeRef.Of(l3[0].Edges.ElementAt(i3)) }, 1, w3); if (b3[0].Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"small scene round: guarantee {b3[0].Guarantee}"); }
                catch (Exception ex) { fails.Add("small scene round: " + ex.Message); }
                // ошибка вычисления в прямом построителе — его отказ, операция идёт прежним путём (P3D_DRCATCH)
                var b4 = new List<TSolid> { TopologyClone.Deep(body) }; var w4 = new List<string>();
                long ex0 = DirectRound.Exceptions;
                try
                {
                    using (FaultInjection.Arm("direct.round")) FilletOp.RoundEdges(b4, new List<EdgeRef> { EdgeRef.Of(b4[0].Edges.ElementAt(iTop)) }, 5, w4);
                    if (DirectRound.Exceptions != ex0 + 1) fails.Add("fault in the direct builder not counted");
                }
                catch (Exception ex) { fails.Add("fault in the direct builder stops the round: " + ex.Message); }
            }
            Require(fails.Count == 0, string.Join(" | ", fails));
        });
    }
}
