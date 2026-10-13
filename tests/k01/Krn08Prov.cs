using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>KRN-08 (08.2): происхождение граней — выход операции (прямое скругление, слоистая и общая булевы).</summary>
static partial class Program
{
    static void ProvenanceChecks()
    {
        Check("KRN08-SELECTORS-BY-ROLE", () =>
        {
            // 08.3: «скруглить кромки отверстия» — селектор Rim(отверстие); правка диаметра — селектор находит новые кромки,
            // объём — по формуле Паппа для нового диаметра; документ сохраняет селектор; геометрическая ссылка на старую
            // кромку после правки не находит ребра
            double k = 1 - Math.PI / 4, g = (10 - 3 * Math.PI) / (3 * (4 - Math.PI)), r = 1.5;
            double V(double R) => 1e6 - Math.PI * R * R * 100 - 2 * k * r * r * 2 * Math.PI * (R + g * r);
            var p = new Parametric3D.Model.Part("sel"); Parametric3D.Model.TestGeometry.AddCube(p, 100);
            var h = new Parametric3D.Model.Features.HoleFeature("H1") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(16) };
            h.Points.Add(new Vector3d(0, 0, 50)); p.AddFeature(h); p.Rebuild(null);
            var rf = new Parametric3D.Model.Features.RoundFeature("R1") { Radius = new Parametric3D.Model.Features.ValueRef(r) };
            rf.Selectors.Add(new Parametric3D.Model.Features.EdgeSelector(Parametric3D.Model.Features.EdgeSelectorKind.Rim, h.Key));
            p.AddFeature(rf); p.Rebuild(null);
            Require(p.Features.All(f => f.Error == null), "build: " + string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => f.Error)));
            Require(Math.Abs(p.Volume - V(8)) <= 1e-12 * V(8), $"R8 rims: volume {p.Volume:R} vs {V(8):R}");
            Require(p.Warnings.Any(w => w.StartsWith("Round: direct blend")), "the rims were not rounded by the direct builder (quarter arcs of the hole)");
            var geoRef = EdgeRef.Of(p.Bodies[0].Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 a && Math.Abs(a.Radius - 8) < 1e-6));
            h.Diameter = new Parametric3D.Model.Features.ValueRef(24); p.MarkDirtyFrom(h); p.Rebuild(null);
            Require(rf.Error == null, "after the hole edit: " + rf.Error);
            Require(Math.Abs(p.Volume - V(12)) <= 1e-12 * V(12), $"R12 rims: volume {p.Volume:R} vs {V(12):R}");
            Require(FilletOp.Resolve(p.Bodies, geoRef, out _) == null, "the geometric reference to the old rim still resolves");
            var (q, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(p)); q.Rebuild(null);
            var rq = q.Features.OfType<Parametric3D.Model.Features.RoundFeature>().Single();
            Require(rq.Selectors.Count == 1 && rq.Selectors[0].Kind == Parametric3D.Model.Features.EdgeSelectorKind.Rim && Math.Abs(q.Volume - V(12)) <= 1e-9 * V(12), "save/load lost the selector");
            // пустой селектор — ошибка, а не «ничего»
            rf.Selectors[0] = new Parametric3D.Model.Features.EdgeSelector(Parametric3D.Model.Features.EdgeSelectorKind.Between, h.Key, "nosuchkey"); p.MarkDirtyFrom(rf); p.Rebuild(null);
            Require(rf.Error != null && rf.Error.Contains("selects no edges"), "an empty selector did not fail: " + rf.Error);
            // фаска по роли: кромки отверстия H1 (новый диаметр 24) — конусы; объём по Паппу; документ сохраняет селектор
            {
                var p2 = new Parametric3D.Model.Part("selc"); Parametric3D.Model.TestGeometry.AddCube(p2, 100);
                var h2 = new Parametric3D.Model.Features.HoleFeature("H1") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(24) };
                h2.Points.Add(new Vector3d(0, 0, 50)); p2.AddFeature(h2); p2.Rebuild(null);
                var cf = new Parametric3D.Model.Features.ChamferFeature("C1") { D1 = new Parametric3D.Model.Features.ValueRef(2), D2 = new Parametric3D.Model.Features.ValueRef(2) };
                cf.Selectors.Add(new Parametric3D.Model.Features.EdgeSelector(Parametric3D.Model.Features.EdgeSelectorKind.Rim, h2.Key));
                p2.AddFeature(cf); p2.Rebuild(null);
                double vc = 1e6 - Math.PI * 144 * 100 - 2 * 2 * 2 * Math.PI * (12 + 2.0 / 3);
                Require(cf.Error == null && Math.Abs(p2.Volume - vc) <= 1e-12 * vc, $"chamfer by role: {cf.Error} volume {p2.Volume:R} vs {vc:R}");
                var (q2, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(p2)); q2.Rebuild(null);
                Require(q2.Features.OfType<Parametric3D.Model.Features.ChamferFeature>().Single().Selectors.Count == 1 && Math.Abs(q2.Volume - vc) <= 1e-12 * vc, "save/load lost the chamfer selector");
            }
            // прямое сопряжение — без предупреждения о хордах
            rf.Selectors[0] = new Parametric3D.Model.Features.EdgeSelector(Parametric3D.Model.Features.EdgeSelectorKind.Rim, h.Key); p.MarkDirtyFrom(rf); p.Rebuild(null);
            Require(!p.Warnings.Any(w => w.Contains("chord approximation")), "a direct round reported the chord approximation of the old builder");
        });
        Check("KRN08-PROVENANCE", () =>
        {
            var fails = new List<string>();
            // ожидания: число граней каждого вида, удалённых и слившихся
            void Case(string name, Func<TSolid> result, TSolid[] inputs, int kept, int modified, int split, int generated, int deleted, int merged)
            {
                TSolid r; try { r = result(); } catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); return; }
                var pv = r.Provenance;
                if (pv == null) { fails.Add($"{name}: no provenance (route {r.Guarantee})"); return; }
                var inFaces = new HashSet<TFace>(inputs.SelectMany(x => x.Faces), ReferenceEqualityComparer.Instance);
                int K(FaceOrigin o) => pv.Faces.Count(x => x.Kind == o);
                var got = $"kept {K(FaceOrigin.Kept)} modified {K(FaceOrigin.Modified)} split {K(FaceOrigin.Split)} generated {K(FaceOrigin.Generated)} deleted {pv.Deleted.Count} merged {pv.Merged.Count}";
                Log(new { test = "KRN08-PROVENANCE", name, operation = pv.Operation, got });
                var want = $"kept {kept} modified {modified} split {split} generated {generated} deleted {deleted} merged {merged}";
                if (got != want) fails.Add($"{name}: {got} (want {want})");
                // каждая грань результата учтена ровно раз; источник — грань операнда; сохранённая — та же граница и поверхность
                if (pv.Faces.Count != r.Faces.Count() || r.Faces.Any(f => pv.Faces.Count(x => ReferenceEquals(x.Result, f)) != 1)) fails.Add($"{name}: faces not covered once");
                foreach (var fp in pv.Faces)
                {
                    if (fp.Kind == FaceOrigin.Generated) { if (fp.Source != null) fails.Add($"{name}: generated face with a source"); continue; }
                    if (fp.Source == null || !inFaces.Contains(fp.Source)) { fails.Add($"{name}: {fp.Kind} face {fp.Result.Tag} without an operand source"); continue; }
                    if (fp.Result.Tag != fp.Source.Tag) fails.Add($"{name}: {fp.Result.Tag} from {fp.Source.Tag}");
                    if (fp.Kind == FaceOrigin.Kept)
                    {
                        var a = fp.Result.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
                        var b = fp.Source.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
                        if (a.Count != b.Count || a.Any(p => !b.Any(q => (p - q).Length < 1e-9))) fails.Add($"{name}: kept face {fp.Result.Tag} has another boundary");
                    }
                }
                foreach (var d in pv.Deleted.Concat(pv.Merged.Select(m => m.Source))) if (!inFaces.Contains(d)) fails.Add($"{name}: deleted/merged face not from an operand");
                if (pv.Merged.Any(m => !r.Faces.Contains(m.Into))) fails.Add($"{name}: merged into a face not in the result");
                // каждая грань операнда — либо источник, либо удалена, либо слилась
                foreach (var f in inFaces)
                    if (!pv.Faces.Any(x => ReferenceEquals(x.Source, f)) && !pv.Deleted.Contains(f) && !pv.Merged.Any(m => ReferenceEquals(m.Source, f)))
                        fails.Add($"{name}: operand face {f.Tag} unaccounted");
            }
            TSolid Bool(TSolid a, TSolid b, BooleanOp.BooleanKind k)
            {
                var res = BooleanOp.Evaluate(new List<TSolid> { a }, b, k, OperationPolicy.Strict);
                if (res.Outcome != OperationOutcome.Success || res.Bodies.Count != 1) throw new Exception(res.Describe());
                return res.Bodies[0];
            }
            {
                var b = Box(0, 0, 0, 60, 40, 20);
                Case("direct round of one edge", () => { var bl = new List<TSolid> { b }; FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(EdgeAt(b, new(60, 40, 0), new(60, 40, 20))) }, 5, new List<string>()); return bl[0]; },
                     new[] { b }, kept: 2, modified: 4, split: 0, generated: 1, deleted: 0, merged: 0);
            }
            {
                var b = Box(0, 0, 0, 60, 40, 20); var V = new Vector3d(60, 40, 20);
                Case("direct round of a corner", () => { var bl = new List<TSolid> { b }; FilletOp.RoundEdges(bl, new List<EdgeRef> { EdgeRef.Of(EdgeAt(b, new(0, 40, 20), V)), EdgeRef.Of(EdgeAt(b, new(60, 0, 20), V)), EdgeRef.Of(EdgeAt(b, new(60, 40, 0), V)) }, 4, new List<string>()); return bl[0]; },
                     new[] { b }, kept: 0, modified: 6, split: 0, generated: 4, deleted: 0, merged: 0);   // концы трёх рёбер — ещё три грани
            }
            {
                var a = Box(0, 0, 0, 60, 40, 20); var c = PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24);
                Case("box − through cylinder (2.5D)", () => Bool(a, c, BooleanOp.BooleanKind.Subtract), new[] { a, c }, kept: 4, modified: 3, split: 0, generated: 0, deleted: 2, merged: 0);
            }
            {
                var a = Box(0, 0, 0, 60, 40, 20); var c = Box(30, 10, 0, 60, 20, 20);
                Case("box ∪ box flush (2.5D)", () => Bool(a, c, BooleanOp.BooleanKind.Unite), new[] { a, c }, kept: 4, modified: 4, split: 2, generated: 0, deleted: 1, merged: 2);   // грань x = 60 — надвое; низ и верх B слились; стенка x = 30 удалена
            }
            {
                var a = Box(0, 0, 0, 60, 40, 20); var s = PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8);
                Case("box − sphere (general)", () => Bool(a, s, BooleanOp.BooleanKind.Subtract), new[] { a, s }, kept: 5, modified: 2, split: 0, generated: 0, deleted: 0, merged: 0);
            }
            {
                var a = Box(0, 0, 0, 60, 40, 20); var t = PrimitiveOp.Torus(new Vector3d(30, 20, 20), 12, 3);
                Case("box − torus (general)", () => Bool(a, t, BooleanOp.BooleanKind.Subtract), new[] { a, t }, kept: 5, modified: 1, split: 2, generated: 0, deleted: 0, merged: 0);   // верх — кольцевым пазом надвое
            }
            {
                // 07.6: уклон стенок со скруглениями (пересборка той же топологии): низ на нейтрали — сохранён, прочие изменены
                var bl0 = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
                FilletOp.RoundEdges(bl0, bl0[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 5, new List<string>());
                var r5 = bl0[0];
                Case("tangent draft of a rounded box", () => { var bl = new List<TSolid> { r5 }; DraftOp.Apply(bl, new[] { new Vector3d(60, 20, 10) }, Vector3d.Zero, Vector3d.UnitZ, 5, new List<string>()); return bl[0]; },
                     new[] { r5 }, kept: 1, modified: 9, split: 0, generated: 0, deleted: 0, merged: 0);
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
