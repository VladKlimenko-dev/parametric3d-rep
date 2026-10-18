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

static partial class Program
{
    static void ProvenanceAllOpsChecks()
    {
        Check("KRN08-PROVENANCE-ALL-OPS", () =>
        {
            // m.h2 / 08.2: ПРОИСХОЖДЕНИЕ ГРАНЕЙ У КАЖДОЙ ОПЕРАЦИИ — карта результата (OperationResult.ProvenanceMap): запись
            // построителя или, если её нет, по тождеству поверхностей; в том числе многогранный путь булевой и многотельный
            // результат (по телу). Для каждой операции: карта есть, каждая грань каждого тела результата учтена ровно раз,
            // источник — грань входа, сохранённая грань — та же граница, каждая грань входа — источник, удалена или слилась;
            // и ожидаемое по смыслу (у переноса — все изменены, каждая из своей; у оболочки — наружные сохранены, внутренние —
            // производные изменённые копии, не «части»; у многогранной булевой — грани операндов не теряются).
            var fails = new List<string>();
            void Case(string name, IReadOnlyList<TSolid> inputs, Action<List<TSolid>> op, Func<Provenance, bool>? expect = null, string? expectText = null)
            {
                OperationResult r;
                try { r = OperationContract.Run(name, inputs, op, new List<string>()); }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); return; }
                if (!r.Accepted) { fails.Add($"{name}: {r.Outcome} {r.FailureCode}"); return; }
                var maps = r.ProvenanceMaps;
                if (maps.Count != r.Bodies.Count) { fails.Add($"{name}: {maps.Count} map(s) for {r.Bodies.Count} bodies"); return; }
                var inFaces = new HashSet<TFace>(inputs.SelectMany(x => x.Faces), ReferenceEqualityComparer.Instance);
                var accounted = new HashSet<TFace>(ReferenceEqualityComparer.Instance);
                for (int bi = 0; bi < r.Bodies.Count; bi++)
                {
                    var body = r.Bodies[bi]; var pv = maps[bi];
                    if (pv.Faces.Count != body.Faces.Count() || body.Faces.Any(f => pv.Faces.Count(x => ReferenceEquals(x.Result, f)) != 1)) fails.Add($"{name} body {bi}: faces not covered once ({pv.Operation})");
                    foreach (var fp in pv.Faces)
                    {
                        if (fp.Source == null) { if (fp.Kind != FaceOrigin.Generated) fails.Add($"{name}: {fp.Kind} without a source"); continue; }
                        if (!inFaces.Contains(fp.Source)) { fails.Add($"{name}: source not an input face"); continue; }
                        accounted.Add(fp.Source);
                        if (fp.Kind == FaceOrigin.Kept)
                        {
                            var a = fp.Result.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
                            var b = fp.Source.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
                            if (a.Count != b.Count || a.Any(p => !b.Any(q => (p - q).Length < 1e-9))) fails.Add($"{name}: kept face {fp.Result.Tag} has another boundary");
                        }
                    }
                    foreach (var d in pv.Deleted) accounted.Add(d);
                    foreach (var m in pv.Merged) accounted.Add(m.Source);
                }
                // у многотельного результата грань входа может быть учтена другим телом
                foreach (var f in inFaces) if (!accounted.Contains(f)) { fails.Add($"{name}: input face {f.Tag} unaccounted"); break; }
                var all = maps.SelectMany(m => m.Faces).ToList();
                string got = $"kept {all.Count(x => x.Kind == FaceOrigin.Kept)} modified {all.Count(x => x.Kind == FaceOrigin.Modified)} split {all.Count(x => x.Kind == FaceOrigin.Split)} generated {all.Count(x => x.Kind == FaceOrigin.Generated)} deleted {maps.Sum(m => m.Deleted.Count)} merged {maps.Sum(m => m.Merged.Count)}";
                Console.WriteLine($"  {name}: {r.Bodies.Count} bodies, {r.Guarantee}; {got} [{string.Join(", ", maps.Select(m => m.Operation).Distinct())}]");
                if (expect != null && !maps.All(expect)) fails.Add($"{name}: {got} — want {expectText}");
            }
                var box = Box(0, 0, 0, 60, 40, 20);
            // перенос — каждая грань из своей (геометрия сдвинута: изменена)
            Case("move", new[] { box }, b => b[0] = TransformOp.Copy(b[0], TransformOp.Move(new Vector3d(5, 6, 7))), p => p.Faces.All(x => x.Kind == FaceOrigin.Modified && x.Source != null), "all modified, each from its face");
            Case("mirror (keep original)", new[] { box }, b => TransformOp.MirrorBody(b, new Vector3d(60, 0, 0), Vector3d.UnitX, new List<string>()));
            Case("linear pattern ×3", new[] { box }, b => TransformOp.LinearPattern(b, new Vector3d(100, 0, 0), 3, new List<string>()));
            Case("round 4 edges (direct)", new[] { box }, b => FilletOp.RoundEdges(b, b[0].Edges.Where(e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), 3, new List<string>()),
                 p => p.Faces.Count(x => x.Kind == FaceOrigin.Generated) == 4, "4 generated");
            Case("chamfer top loop", new[] { box }, b => FilletOp.ChamferEdges(b, b[0].Edges.Where(e => e.V0.P.Z > 19 && e.V1.P.Z > 19).Select(EdgeRef.Of).ToList(), 2, 2, new List<string>()),
                 p => p.Faces.Count(x => x.Kind == FaceOrigin.Generated) >= 4, "≥ 4 generated");
            Case("shell, open top", new[] { box }, b => ShellOp.Hollow(b, 2, new[] { new Vector3d(30, 20, 20) }, new List<string>()),
                 p => p.Faces.Count(x => x.Note == ProvenanceBySurface.Derived && x.Kind == FaceOrigin.Modified) >= 5 && p.Faces.All(x => x.Kind != FaceOrigin.Split), "≥ 5 derived (inner walls, modified), no split");
            Case("draft side walls", new[] { box }, b => DraftOp.Apply(b, new[] { new Vector3d(60, 20, 10), new Vector3d(0, 20, 10) }, Vector3d.Zero, Vector3d.UnitZ, 4, new List<string>()));
            // булева многогранным путём: шар со смещённым центром дуги (веретенообразный тор — точный маршрут его не берёт)
            var lemon = ExactCopy(Lemon(-2));
            Case("boolean, polyhedral route (lemon − box)", new[] { lemon }, b => { var r0 = BooleanOp.Evaluate(b.ToList(), Box(0.5, -10, -10, 10, 20, 20), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict); if (!r0.Accepted) throw new Exception(r0.Describe()); b.Clear(); b.AddRange(r0.Bodies); });
            // многотельный результат: брусок разрезается сквозной плитой на два тела
            Case("boolean into two bodies", new[] { box }, b => { var r0 = BooleanOp.Evaluate(b.ToList(), Box(25, -5, -5, 10, 50, 30), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict); if (!r0.Accepted) throw new Exception(r0.Describe()); b.Clear(); b.AddRange(r0.Bodies); });
            Case("hole through (boolean with a cylinder)", new[] { box }, b => BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(30, 20, -5), 5, 30), new List<string>()),
                 p => p.Faces.Count(x => x.Kind == FaceOrigin.Kept) == 4, "4 kept");
            Require(fails.Count == 0, string.Join("; ", fails.Take(8)));
        });
    }
}

static partial class Program
{
    internal static TSolid DBody()
    {
        // «D»: отрезок (−10, 0) → (10, 0) и полуокружность радиуса 10 через (0, 10); выдавливание на 20
        var pl = Frame3.XY;
        var seg = new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(-10, 0, 0), new Vector3d(10, 0, 0));
        var arc = new Parametric3D.Kernel.Curves.ArcCurve3(Frame3.XY, 10, 0, Math.PI);
        return ExactCopy(ExtrudeOp.Build(ProfileBuilder.Build(pl, new Parametric3D.Kernel.Curves.ICurve3[] { seg, arc }), new Vector3d(0, 0, 20)));
    }

    static void VertexCurvedChecks()
    {
        Check("K09-VERTEX-CURVED-EXACT", () =>
        {
            // m.f3: ФАСКА И СКРУГЛЕНИЕ ВЕРШИНЫ С КРИВЫМИ РЁБРАМИ/ГРАНЯМИ — угол «D»-выдавливания (верх, плоская стенка, цилиндр;
            // ребро-дуга, два прямых), угол бруска с отверстием, срезающим ребро (дуга, эллипс, прямое), и вогнутый угол
            // (паз в цилиндре). Результат: точный (ExactAnalytic), годный; одно тело; снятый объём = V(тело ∩ резец) (до 1e-9);
            // у фаски — новая плоская грань с вершинами на рёбрах на расстоянии d от вершины. Седловая вершина — отказ с
            // причиной. Мутация P3D_VERTEXCURVED=0 — отказ, как прежде (ловится).
            var fails = new List<string>(); var removedBy = new Dictionary<string, double>();
            var cases = new List<(string Name, Func<TSolid> Body, Vector3d At, bool Round, double Size)>
            {
                ("D corner, chamfer 2", DBody, new Vector3d(10, 0, 20), false, 2),
                ("D corner, round 2", DBody, new Vector3d(10, 0, 20), true, 2),
                ("D corner bottom, chamfer 3", DBody, new Vector3d(-10, 0, 0), false, 3),
            };
            {
                // брусок с отверстием r8 у ребра x = 60, y = 40: угол «верх — отверстие — стенка x = 60»
                TSolid B() { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(56, 36, -5), 8, 30), new List<string>()); return bl[0]; }
                var b0 = B();
                // вершина, где отверстие выходит на ребро x = 60 верхней грани: (60, 36 − √48, 20)
                var v = new Vector3d(60, 36 - Math.Sqrt(48), 20);
                cases.Add(("box corner cut by a hole, chamfer 1.5", B, v, false, 1.5));
                cases.Add(("box corner cut by a hole, round 1.5", B, v, true, 1.5));
            }
            {
                // вогнутый угол: карман «D» глубиной 10 в бруске — угол дна (дно, плоская стенка, цилиндр кармана)
                TSolid Pk() { var bl = new List<TSolid> { Box(-20, -20, 0, 40, 40, 20) }; BooleanOp.Subtract(bl, TransformOp.Copy(DBody(), TransformOp.Move(new Vector3d(0, 0, 10))), new List<string>()); return bl[0]; }
                cases.Add(("D pocket corner (concave), chamfer 2", Pk, new Vector3d(10, 0, 10), false, 2));
                cases.Add(("D pocket corner (concave), round 2", Pk, new Vector3d(10, 0, 10), true, 2));
            }
            foreach (var (name, mk, at, round, size) in cases)
            {
                try
                {
                    var b = mk(); double v0 = OpVolume(b); int f0 = b.Faces.Count();
                    var bl = new List<TSolid> { b }; var w = new List<string>();
                    var r = OperationContract.Run(round ? "VertexRound" : "VertexChamfer", bl, x => { if (round) VertexOp.Round(x, at, size, w); else VertexOp.Chamfer(x, at, new[] { size }, w); }, w);
                    if (!r.Accepted) { fails.Add($"{name}: {r.Outcome} {r.Diagnostics.FirstOrDefault()?.Message}"); continue; }
                    var res = r.Bodies.Single();
                    var vr = SolidValidator.Validate(res);
                    double v1 = OpVolume(res);
                    Console.WriteLine($"  {name}: {res.Guarantee}, V {v0:R} → {v1:R} (removed {v0 - v1:G6}), faces {f0} → {res.Faces.Count()} | {w.LastOrDefault()}");
                    removedBy[name] = v0 - v1;
                    if (res.Guarantee != GuaranteeLevel.ExactAnalytic || !vr.IsValid) fails.Add($"{name}: {res.Guarantee} {vr.Summary()}");
                    if (!(Math.Abs(v1 - v0) > 1e-9)) fails.Add($"{name}: nothing removed");
                    if (!round)
                    {
                        // новая плоская грань: 3 вершины на расстоянии d от угла
                        var nf = res.Faces.Where(f => f.Surface.IsPlanar && f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Start.P).Count(p => Math.Abs((p - at).Length - size) < 1e-6) >= 3).ToList();
                        if (nf.Count != 1) fails.Add($"{name}: {nf.Count} chamfer faces with corners at distance {size}");
                        if (res.Faces.Count() != f0 + 1) fails.Add($"{name}: faces {f0} → {res.Faces.Count()} (want +1)");
                    }
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
            }
            // карман «D» — отпечаток тела «D»: у вогнутого угла добавлено ровно столько, сколько снято у выпуклого (до 1e-9)
            foreach (var (cv, cc) in new[] { ("D corner, chamfer 2", "D pocket corner (concave), chamfer 2"), ("D corner, round 2", "D pocket corner (concave), round 2") })
                if (removedBy.TryGetValue(cv, out var a1) && removedBy.TryGetValue(cc, out var a2) && !(Math.Abs(a1 + a2) <= 1e-9 * Math.Max(1, Math.Abs(a1))))
                    fails.Add($"{cc}: added {-a2:R}, the convex twin removed {a1:R}");
            // седловая вершина: угол «D»-выдавливания, к которому снизу приставлен брусок (вогнутое ребро + выпуклые) — отказ
            try
            {
                var bl = new List<TSolid> { DBody() };
                BooleanOp.Unite(bl, Box(-5, -10, 0, 20, 10, 20), new List<string>());
                var sv = bl[0].Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).FirstOrDefault(p => (p - new Vector3d(10, 0, 20)).Length < 1e-9);
                if (sv != default)
                {
                    var r = OperationContract.Run("VertexChamfer", bl, x => VertexOp.Chamfer(x, sv, new[] { 1.0 }, new List<string>()), new List<string>());
                    Console.WriteLine($"  saddle: {r.Outcome} {r.Diagnostics.FirstOrDefault()?.Message}");
                    if (r.Accepted) fails.Add("saddle vertex: accepted");
                }
            }
            catch (Exception ex) { fails.Add($"saddle: {ex.GetType().Name} {ex.Message}"); }
            Require(fails.Count == 0, string.Join("; ", fails.Take(8)));
        });
    }
}
