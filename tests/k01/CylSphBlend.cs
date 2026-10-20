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
            long endPts = 0;
            (TSolid? B, string Msg, double Ms) Run(int ei, bool round, double d)
            {
                var b = new List<TSolid> { TopologyClone.Deep(body) }; var w = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long e0 = DirectRound.OpenEndPoints;
                try
                {
                    var er = new List<EdgeRef> { EdgeRef.Of(b[0].Edges.ElementAt(ei)) };
                    if (round) FilletOp.RoundEdges(b, er, d, w); else FilletOp.ChamferEdges(b, er, d, d, w);
                    return (b[0], string.Join("; ", w.Take(2)), sw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) { return (null, ex.Message, sw.Elapsed.TotalMilliseconds); }
                finally { endPts = DirectRound.OpenEndPoints - e0; }
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
                    double R = 15, r = d, xb = r * (10 - 3 * Math.PI) / (3 * (4 - Math.PI));
                    double want = 2 * Math.PI * (R - xb) * r * r * (1 - Math.PI / 4);
                    if (!(Math.Abs(v0 - v1 - want) < 1e-6 * want)) fails.Add($"{name}: removed {v0 - v1:R} want {want:R}");
                }
            }
            Require(fails.Count == 0, string.Join(" | ", fails));
        });
    }
}
