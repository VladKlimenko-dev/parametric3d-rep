using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// 23:50: ТОР R20 r5 И БРУСОК, ГРАНЬ КОТОРОГО КАСАЕТСЯ ТОРА. (1) Плоскость x = 25 касается внешнего экватора в одной точке
    /// (точка эллиптическая) — точный маршрут: касание в точке ничего не делит (P3D_POINTTOUCHTOR, Ssi P3D_PLTORTAN); объём —
    /// V(тор) + V(брусок) − ∬ высоты тора над прямоугольником бруска (численно, сетка 4000², 10934.263920). Прежде прослеживание
    /// не удостоверялось у точки касания (1.5–3 с), и результат шёл многогранным путём (объём на 20 мм³ меньше). (2) Плоскость
    /// x = 15 касается внутреннего экватора (седло): касание решается сразу (без прослеживания) — отказ точного маршрута с
    /// причиной, многогранный путь.
    /// </summary>
    static void TorusTouchChecks()
    {
        // 23:50: точки колец соглашения — из памяти по кривой (P3D_RINGPTSMEMO=0): деталь с 12 отверстиями — повторное
        // соглашение (каждая операция) берёт гранение окружностей из памяти; тело то же (объём)
        Check("KRN10-RING-POINTS-MEMO", () =>
        {
            Parametric3D.Model.Part Build()
            {
                var p = new Parametric3D.Model.Part("rings"); TestGeometry.AddCube(p, 100);
                for (int i = 0; i < 12; i++)
                {
                    var h = new HoleFeature($"H{i}") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(4), ThroughAll = true };
                    h.Points.Add(new Vector3d(-40 + 7 * i, 10 * (i % 3) - 10, 50)); p.AddFeature(h);
                }
                return p;
            }
            long la0 = Part.LazyAreas;
            long h0 = TopologyConventions.RingPtsMemoHits, em0 = FeatureHistory.EdgeMapMemoHits, lf0 = FeatureHistory.HistLoopsFirstHits;
            var p1 = Build(); p1.Rebuild(null);
            long hits = TopologyConventions.RingPtsMemoHits - h0;
            Log(new { test = "KRN10-RING-POINTS-MEMO", hits, volume = p1.Volume, errors = p1.Features.Count(f => f.Error != null) });
            Require(p1.Features.All(f => f.Error == null), "holes failed");
            Require(Math.Abs(p1.Volume - (1e6 - 12 * Math.PI * 4 * 100)) < 1e-6 * 1e6, $"volume {p1.Volume}");
            Require(hits >= 12, $"ring points taken from memory {hits} times");
            // 21.10: площадь детали — по запросу (P3D_LAZYAREA=0 — на каждом перестроении); при чтении — та же, что по телам
            Require(Part.LazyAreas > la0, "part area was computed eagerly");
            double aSum = p1.Bodies.Sum(b => AnalyticVolume.AreaOf(b) ?? double.NaN);
            Require(Math.Abs(p1.Area - aSum) <= 1e-9 * aSum, $"part area {p1.Area} vs bodies {aSum}");
            // 23:50: история рёбер — карта тела «до» операции из памяти (P3D_EDGEMAPMEMO=0)
            // плоская грань с новым отверстием — «изменена» по числу контуров, без строк подписей (P3D_HISTLOOPSFIRST=0)
            Require(FeatureHistory.HistLoopsFirstHits - lf0 >= 10, $"planar loop-count decisions {FeatureHistory.HistLoopsFirstHits - lf0}");
            Require(FeatureHistory.EdgeMapMemoHits - em0 >= 10, $"edge maps taken from memory {FeatureHistory.EdgeMapMemoHits - em0} times");
        });
        Check("KRN06-TORUS-PLANE-TOUCH", () =>
        {
            var fails = new List<string>();
            var fr = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
            TSolid Torus() => FFPrimitiveFeature.Make(FFPrimitive.Torus, fr, 20, 5, 0);
            TSolid Box(double cx, double w) => FFPrimitiveFeature.Make(FFPrimitive.Box, new Frame3(new Vector3d(cx, 0, -10), Vector3d.UnitX, Vector3d.UnitY), w, 10, 20);
            long t0 = Parametric3D.Kernel.Geometry.Ssi.PlaneTorusTangents;
            var r1 = BooleanOp.Evaluate(new List<TSolid> { Torus() }, Box(20.5, 9), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict);
            double v1 = r1.Bodies.Sum(b => AnalyticVolume.Of(b) ?? double.NaN);
            Log(new { test = "KRN06-TORUS-PLANE-TOUCH", outer = r1.Guarantee.ToString(), v1 = v1.ToString("R"), tangents = Parametric3D.Kernel.Geometry.Ssi.PlaneTorusTangents - t0 });
            if (r1.Outcome != OperationOutcome.Success || r1.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"outer touch: {r1.Outcome} {r1.Guarantee}");
            else
            {
                if (!(Math.Abs(v1 - 10934.26392) <= 5e-5)) fails.Add($"outer touch: volume {v1:R} vs 10934.26392");
                foreach (var b in r1.Bodies) { var v = SolidValidator.Validate(b); if (!v.IsValid) fails.Add("outer touch: " + v.Summary()); }
            }
            if (Parametric3D.Kernel.Geometry.Ssi.PlaneTorusTangents - t0 < 1) fails.Add("outer touch: tangency not recognised directly");
            long t1 = Parametric3D.Kernel.Geometry.Ssi.PlaneTorusTangents;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r2 = BooleanOp.Evaluate(new List<TSolid> { Torus() }, Box(20, 10), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict);
            Log(new { test = "KRN06-TORUS-PLANE-TOUCH", both = r2.Guarantee.ToString(), outcome = r2.Outcome.ToString(), ms = sw.ElapsedMilliseconds });
            if (r2.Outcome != OperationOutcome.Success) fails.Add($"saddle touch: {r2.Outcome}");
            if (Parametric3D.Kernel.Geometry.Ssi.PlaneTorusTangents - t1 < 1) fails.Add("saddle touch: tangency not recognised directly");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
