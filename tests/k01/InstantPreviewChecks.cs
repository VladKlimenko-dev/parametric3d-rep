using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

/// <summary>
/// k113 (цель 21:47 — предпросмотр любой операции ≤ 20 мс): МГНОВЕННЫЙ ПРЕДПРОСМОТР ПО СЕТКАМ. На пластине K08 (20 операций)
/// каждая операция-случай — последней в дереве: расчёт предпросмотра (как в окне) показывает носитель сетки, объём по сетке —
/// тот же, что у точного расчёта (≤ 0.05 %); правка средней операции — предпросмотр на её месте; массив резцов — по сеткам;
/// вырез, касающийся грани снаружи, ничего не добавляет. Время — K01Checks previewbench (замер, не проверка).
/// Мутации: P3D_INSTANTPREVIEW, P3D_INSTANTROLLBACK, P3D_INSTANTREPLAY, P3D_MESHCUTBOTHSIDES, P3D_MESHCUTREGIONS,
/// P3D_MESHCUTBSPMEMO, P3D_REACHTRIS, P3D_REACHCULL, P3D_SOLIDTRISMEMO (=0).
/// </summary>
static partial class Program
{
    static void InstantPreviewChecks()
    {
        static KernelSettings Pv() { var ks = KernelSettings.Capture(); return ks with { Preview = true, LivePreview = true, Chord = ks.Chord * 4 }; }
        static void Preview(Part p, Feature f) { p.MarkDirtyFrom(f); Part.BuildResult r; using (new KernelContext(Pv()).Enter()) r = p.Compute(null); p.Apply(r); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        static Part Base() { var p = RealisticPart(3, 20, out _); p.Rebuild(null); return p; }

        Check("KRN-INSTANT-PREVIEW-K113", () =>
        {
            long hits0 = InstantPreview.Hits, regions0 = MeshCut.RegionProbes, memo0 = MeshCut.BspMemoHits, reach0 = FilletOp.ReachTrisHits, cull0 = FilletOp.ReachCulls, stris0 = DirectRound.SolidTrisMemoHits;
            var cases = new List<(string Name, Func<Part, Feature> Add, Action<Feature> Change)>
            {
                ("boss", p => { var s = CircleSketch(p, 10, 30, 20, 6); var f = new ExtrudeFeature("B") { SketchId = s.Id }; f.Side1.Distance = new ValueRef(8); p.AddFeature(f); return f; },
                    f => ((ExtrudeFeature)f).Side1.Distance = new ValueRef(9)),
                ("pocket", p => { var s = RectSketch(p, 10, -60, -40, 20, 12); var f = new ExtrudeFeature("C") { SketchId = s.Id, Cut = true }; f.Side1.Distance = new ValueRef(4); f.Side1.Flip = true; p.AddFeature(f); return f; },
                    f => ((ExtrudeFeature)f).Side1.Distance = new ValueRef(5)),
                ("hole", p => { var f = new HoleFeature("H") { Direction = -Vector3d.UnitZ, Diameter = new ValueRef(5), ThroughAll = true }; f.Points.Add(new Vector3d(-40, 35, 10)); p.AddFeature(f); return f; },
                    f => ((HoleFeature)f).Diameter = new ValueRef(6)),
                ("round", p => { var f = new RoundFeature("R") { Radius = new ValueRef(3) }; f.Edges.Add(EdgeRef.Of(p.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Near(Math.Abs(e.V0.P.X), 80) && Near(e.V0.P.X, e.V1.P.X) && Near(e.V0.P.Z, 10) && Near(e.V1.P.Z, 10)))); p.AddFeature(f); return f; },
                    f => ((RoundFeature)f).Radius = new ValueRef(3.5)),
                ("chamfer", p => { var f = new ChamferFeature("K") { D1 = new ValueRef(2), D2 = new ValueRef(2) }; f.Edges.Add(EdgeRef.Of(p.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Near(Math.Abs(e.V0.P.X), 80) && Near(e.V0.P.X, e.V1.P.X) && Near(e.V0.P.Z, -10) && Near(e.V1.P.Z, -10)))); p.AddFeature(f); return f; },
                    f => { ((ChamferFeature)f).D1 = new ValueRef(2.5); ((ChamferFeature)f).D2 = new ValueRef(2.5); }),
                ("pattern", p => { var f = new LinearPatternFeature("P") { Step = new Vector3d(0, -12, 0), Count = new ValueRef(4) }; f.Sources.Add(p.Features.OfType<HoleFeature>().Last().Key); p.AddFeature(f); return f; },
                    f => ((LinearPatternFeature)f).Step = new Vector3d(0, -12.5, 0)),
                ("revolve", p =>
                    {
                        var s = new Sketch("Rev", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ)); p.Add(s);
                        var a = new SkPoint(50, 10); var b = new SkPoint(58, 10); var c = new SkPoint(58, 20); var d = new SkPoint(50, 20);
                        s.AddEntity(new SkLine(a, b)); s.AddEntity(new SkLine(b, c)); s.AddEntity(new SkLine(c, d)); s.AddEntity(new SkLine(d, a));
                        var ax = new SkLine(new SkPoint(40, 0), new SkPoint(40, 30)) { Role = SkRole.Construction }; s.AddEntity(ax);
                        var f = new RevolveFeature("V") { SketchId = s.Id, AxisEntityId = ax.Id }; p.AddFeature(f); return f;
                    }, f => ((RevolveFeature)f).EndAngle = new ValueRef(270)),
                // резец касается верхней грани снаружи (вырез вверх): по сеткам не должен ничего добавить
                ("touching cut", p => { var s = RectSketch(p, 10, -60, -40, 20, 12); var f = new ExtrudeFeature("T") { SketchId = s.Id, Cut = true }; f.Side1.Distance = new ValueRef(4); p.AddFeature(f); return f; },
                    f => ((ExtrudeFeature)f).Side1.Distance = new ValueRef(5)),
            };
            var bad = new List<string>();
            foreach (var (name, add, change) in cases)
            {
                var p = Base();
                var f = add(p); p.Rebuild(null);
                change(f);
                Preview(p, f);
                double vp = p.Volume; bool inst = p.ShowsInstant;
                p.MarkDirtyFrom(f); p.Rebuild(null);
                double ve = p.Volume;
                double dv = Math.Abs(vp - ve) / Math.Abs(ve);
                Console.WriteLine($"  {name,-13} instant {inst}, volume preview {vp:0.#} exact {ve:0.#} (dV {dv * 100:0.###}%), error {f.Error}");
                if (!inst) bad.Add($"{name}: not instant");
                if (!(dv < 5e-4)) bad.Add($"{name}: volume {vp:0.#} vs {ve:0.#}");
            }
            // правка средней операции — предпросмотр на её месте
            {
                var p = Base();
                var mid = p.Features.OfType<HoleFeature>().ElementAt(p.Features.OfType<HoleFeature>().Count() / 2);
                int at = p.Features.IndexOf(mid);
                long rb0 = Part.InstantRollbacks;
                mid.Diameter = new ValueRef(mid.Diameter.Value * 0.9);
                Preview(p, mid);
                Console.WriteLine($"  mid edit: instant {p.ShowsInstant} at {p.InstantFeatureIndex} (operation {at}), rollbacks {Part.InstantRollbacks - rb0}");
                if (!p.ShowsInstant || p.InstantFeatureIndex != at || Part.InstantRollbacks == rb0) bad.Add("mid edit: not previewed in place");
                // уточнение — со всего дерева от этой операции
                if (!p.RequestRefine()) bad.Add("mid edit: refine not requested");
                p.Rebuild(null);
                if (p.ShowsInstant || p.Features.Any(x => x.Error != null)) bad.Add("mid edit: refine did not rebuild the tree");
            }
            Console.WriteLine($"  counters: instant {InstantPreview.Hits - hits0}, region probes {MeshCut.RegionProbes - regions0}, tree memo {MeshCut.BspMemoHits - memo0}, reach memo {FilletOp.ReachTrisHits - reach0}, reach cull {FilletOp.ReachCulls - cull0}, solid triangles memo {DirectRound.SolidTrisMemoHits - stris0}");
            if (MeshCut.RegionProbes == regions0) bad.Add("region classification unused");
            if (MeshCut.BspMemoHits == memo0) bad.Add("tree memo unused");
            if (FilletOp.ReachTrisHits == reach0) bad.Add("end-reach triangle memo unused");
            if (FilletOp.ReachCulls == cull0) bad.Add("end-reach cull unused");
            if (DirectRound.SolidTrisMemoHits == stris0) bad.Add("direct-builder triangle memo unused");
            Require(bad.Count == 0, string.Join("; ", bad));
        });
    }
}
