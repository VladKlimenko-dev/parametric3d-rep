using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

/// <summary>
/// KRN-08 (08.10) ПРИЁМКА ДЛИННЫХ ИСТОРИЙ: деталь из ~26 операций (скругление и фаска рёбер, 12 отверстий, 6 сферических
/// лунок, 4 бобышки, линейный массив); правки РАННИХ параметров (диаметр первых отверстий, радиус скругления, радиус лунок),
/// подавление и возврат операций, смена числа копий массива. После каждой правки: перестроение от изменённой операции равно
/// построению с нуля с теми же параметрами (геометрия тел и объём побитно), ошибок нет, тела годны.
/// </summary>
static partial class Program
{
    sealed record HistParams(double D1, double RoundR, bool RoundOff, double SphR, int Copies, bool Hole7Off, double ChamferD);

    static Part BuildHistory(HistParams q)
    {
        var p = new Part("hist"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
        var round = new RoundFeature("R1") { Radius = new ValueRef(q.RoundR) }; round.Edges.Add(EdgeRef.Of(CornerEdge(p, 50, 50)));
        p.AddFeature(round); round.Suppressed = q.RoundOff;
        var ch = new ChamferFeature { Name = "C1", D1 = new ValueRef(q.ChamferD), D2 = new ValueRef(q.ChamferD) }; ch.Edges.Add(EdgeRef.Of(CornerEdge(p, -50, 50)));
        p.AddFeature(ch);
        for (int k = 0; k < 12; k++)
        {
            var h = new HoleFeature($"H{k + 1}") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(k < 6 ? q.D1 : 5) };
            h.Points.Add(new Vector3d(-25 + (k % 4) * 15, -30 + (k / 4) * 15, 50));
            p.AddFeature(h);
            if (k == 6) h.Suppressed = q.Hole7Off;
        }
        for (int k = 0; k < 6; k++) p.AddFeature(new CutSphere(-25 + (k % 3) * 20, 25 + (k / 3) * 12, 50, q.SphR) { Name = $"S{k + 1}" });
        for (int k = 0; k < 4; k++) p.AddFeature(new AddBlock(28, -48 + k * 23, 50) { Name = $"B{k + 1}" });
        if (q.Copies >= 2) p.AddFeature(new LinearPatternFeature("P1") { Step = new Vector3d(250, 0, 0), Count = new ValueRef(q.Copies) });
        p.Rebuild(null);
        return p;
    }

    /// <summary>
    /// Документ без временных номеров: номера элементов (Id и ссылки на них) выдаются при создании и при чтении заново —
    /// заменяются порядковыми по первому появлению; остальное (операции, параметры, ссылки на рёбра) — как есть.
    /// </summary>
    static string CanonDoc(string json)
    {
        var map = new Dictionary<string, int>();
        System.Text.Json.Nodes.JsonNode? Walk(System.Text.Json.Nodes.JsonNode? n, string? key)
        {
            switch (n)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var k in o.Select(kv => kv.Key).ToList())
                        if (k is "Modified" or "Created" or "Saved") o.Remove(k);       // время записи — свойство файла, не замысел
                        else o[k] = Walk(o[k]?.DeepClone(), k);
                    return o;
                case System.Text.Json.Nodes.JsonArray a:
                    for (int i = 0; i < a.Count; i++) a[i] = Walk(a[i]?.DeepClone(), key);
                    return a;
                case System.Text.Json.Nodes.JsonValue v when key != null && (key == "Id" || key.EndsWith("Id") || key is "A" or "B" or "C" or "Items") && v.TryGetValue<long>(out var num):
                    var sk = num.ToString(); if (!map.TryGetValue(sk, out var c)) map[sk] = c = map.Count + 1;
                    return System.Text.Json.Nodes.JsonValue.Create(c);
                default: return n;
            }
        }
        return Walk(System.Text.Json.Nodes.JsonNode.Parse(json), null)!.ToJsonString();
    }

    static void LongHistoryChecks()
    {
        Check("KRN08-STABLE-KEYS", () =>
        {
            // 08.1: постоянный идентификатор операции — ключ (Feature.Key), не временный номер элемента (ModelItem.Id выдаётся
            // заново при каждом чтении); имена граней несут ключ породившей операции, ссылки на рёбра — пары имён граней.
            // После сохранения и чтения: ключи те же, имена граней те же, ссылки разрешаются в те же рёбра, геометрия побитно.
            var p = new Part("keys"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
            var h = new HoleFeature("H1") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(10) }; h.Points.Add(new Vector3d(-20, -20, 50)); p.AddFeature(h);
            p.Rebuild(null);
            var rf = new RoundFeature("R1") { Radius = new ValueRef(5) }; rf.Edges.Add(EdgeRef.Of(CornerEdge(p, 50, 50))); p.AddFeature(rf);
            p.Rebuild(null);
            Require(p.Features.All(f => f.Error == null), "build failed");
            var (q, _) = DocumentFile.Load(DocumentFile.Save(p)); q.Rebuild(null);
            var k1 = p.Features.Select(f => f.Key).ToList(); var k2 = q.Features.Select(f => f.Key).ToList();
            Require(k1.SequenceEqual(k2), "feature keys changed by save/load");
            Require(!p.Features.Select(f => f.Id).SequenceEqual(q.Features.Select(f => f.Id)), "element ids are expected to be session-local (the check would not distinguish keys from ids)");
            var t1 = p.Bodies.SelectMany(b => b.Faces).Select(f => f.Tag).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var t2 = q.Bodies.SelectMany(b => b.Faces).Select(f => f.Tag).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Require(t1.SequenceEqual(t2), "face names differ after save/load");
            Require(t1.All(t => t.StartsWith("#")), "a face without the key of its operation");
            var r2 = q.Features.OfType<RoundFeature>().Single();
            var a1 = rf.Edges[0]; var a2 = r2.Edges[0];
            Require(a1.FaceA == a2.FaceA && a1.FaceB == a2.FaceB && a1.End0 == a2.End0 && a1.End1 == a2.End1, "the edge reference (face names) changed after save/load");
            Require(PartGeometry(p) == PartGeometry(q), "geometry differs after save/load");
            Require(rf.Edges[0].FaceA!.StartsWith("#") && rf.Edges[0].FaceB!.StartsWith("#"), "the edge reference is not by keyed face names");
        });
        Check("KRN08-PARAMETER-CYCLES", () =>
        {
            // 08.5 «без циклов»: цикл формул — ошибка у каждого участника и у всех, кто от них зависит; остальные считаются
            var t = new Parametric3D.Model.Params.ParameterTable();
            t.Add("d", 5);
            var a = t.AddFormula("a", "b + 1"); var b = t.AddFormula("b", "a * 2"); var c = t.AddFormula("c", "b + 3"); var e = t.AddFormula("e", "d * 2");
            var self = t.AddFormula("s", "s + 1");
            t.Recalculate();
            Require(a.Error != null && b.Error != null, $"cycle members: a '{a.Error}', b '{b.Error}'");
            Require(c.Error != null, "a parameter depending on a cycle got a number " + c.Number);
            Require(e.Error == null && Math.Abs(e.Number - 10) < 1e-12, $"independent formula: {e.Error} {e.Number}");
            Require(self.Error != null, "self reference");
            // разрыв цикла — все снова считаются
            a.Formula = "d + 1"; t.Recalculate();
            Require(a.Error == null && b.Error == null && c.Error == null && a.Number == 6 && b.Number == 12 && c.Number == 15, $"after breaking the cycle: {a.Number} {b.Number} {c.Number} / {a.Error} {b.Error} {c.Error}");
        });
        Check("KRN08-REORDER", () =>
        {
            // 08.5: перестановка операций в дереве — перестроение от места перестановки равно построению с нуля в новом порядке;
            // перестановка независимых операций (два отверстия) объёма не меняет; перенос отверстия перед скруглением (ссылка
            // скругления снята после него) — как сборка с нуля в новом порядке
            Part Make(string[] order)
            {
                var q = new Part("ro"); TestGeometry.AddCube(q, 100); q.Rebuild(null);
                foreach (var nm in order)
                    switch (nm)
                    {
                        case "H1": { var h = new HoleFeature("H1") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(10) }; h.Points.Add(new Vector3d(-20, -20, 50)); q.AddFeature(h); break; }
                        case "H2": { var h = new HoleFeature("H2") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(12) }; h.Points.Add(new Vector3d(38, 38, 50)); q.AddFeature(h); break; }
                        case "R1": { q.Rebuild(null); var rf = new RoundFeature("R1") { Radius = new ValueRef(8) }; rf.Edges.Add(EdgeRef.Of(CornerEdge(q, 50, 50))); q.AddFeature(rf); break; }
                    }
                q.Rebuild(null);
                return q;
            }
            var p = Make(new[] { "R1", "H1", "H2" });
            Require(p.Features.All(f => f.Error == null), "initial build failed");
            double v0 = p.Volume;
            // независимые: H2 перед H1
            p.MoveFeature(p.Features.Single(f => f.Name == "H2"), p.Features.IndexOf(p.Features.Single(f => f.Name == "H1"))); p.Rebuild(null);
            var fresh1 = Make(new[] { "R1", "H2", "H1" });
            Require(PartGeometry(p) == PartGeometry(fresh1), "reordered independent holes: incremental differs from a fresh build");
            Require(Math.Abs(p.Volume - v0) <= 1e-9 * v0, $"independent reorder changed the volume {p.Volume:R} vs {v0:R}");
            // отверстие H2 (у угла скругления) — перед скруглением
            p.MoveFeature(p.Features.Single(f => f.Name == "H2"), p.Features.IndexOf(p.Features.Single(f => f.Name == "R1"))); p.Rebuild(null);
            var fresh2 = Make(new[] { "H2", "R1", "H1" });
            Require(p.Features.All(f => f.Error == null) == fresh2.Features.All(f => f.Error == null), "errors differ from a fresh build");
            Require(PartGeometry(p) == PartGeometry(fresh2), "reordered dependent features: incremental differs from a fresh build");
            Log(new { test = "KRN08-REORDER", v0, v1 = p.Volume, errors = p.Features.Count(f => f.Error != null) });
        });
        Check("KRN08-UNDO-REDO", () =>
        {
            // 08.7: отмена и повтор по снимкам документа: после каждой отмены — тот же документ (замысел) и та же геометрия
            // (побитно), что были до правки; повтор — то, что было после; новая правка после отмены очищает повтор
            var p = new Part("undo"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
            var hist = new UndoHistory(); var states = new List<(string Doc, string Geo)>();
            void Snap() { Require(p.Features.All(f => f.Error == null), "edit failed: " + string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}"))); states.Add((DocumentFile.Save(p), PartGeometry(p))); }
            Snap();
            var edits = new List<(string Name, Action Do)>
            {
                ("add round", () => { var rf = new RoundFeature("R1") { Radius = new ValueRef(5) }; rf.Edges.Add(EdgeRef.Of(CornerEdge(p, 50, 50))); p.AddFeature(rf); }),
                ("add hole", () => { var h = new HoleFeature("H1") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(8) }; h.Points.Add(new Vector3d(-20, -20, 50)); p.AddFeature(h); }),
                ("round R5→3", () => { var rf = p.Features.OfType<RoundFeature>().Single(); rf.Radius = new ValueRef(3); p.MarkDirtyFrom(rf); }),
                ("suppress hole", () => { var h = p.Features.OfType<HoleFeature>().Single(); h.Suppressed = true; p.MarkDirtyFrom(h); }),
                ("add chamfer", () => { var c = new ChamferFeature { Name = "C1", D1 = new ValueRef(4), D2 = new ValueRef(4) }; c.Edges.Add(EdgeRef.Of(CornerEdge(p, -50, 50))); p.AddFeature(c); }),
                ("unsuppress hole", () => { var h = p.Features.OfType<HoleFeature>().Single(); h.Suppressed = false; p.MarkDirtyFrom(h); }),
                ("add pattern ×2", () => p.AddFeature(new LinearPatternFeature("P1") { Step = new Vector3d(150, 0, 0), Count = new ValueRef(2) })),
            };
            foreach (var (name, act) in edits) { hist.Record(p); act(); p.Rebuild(null); Snap(); }
            int n = states.Count - 1;
            for (int i = n - 1; i >= 0; i--)
            {
                p = hist.Undo(p)!.Value.Part;
                Require(CanonDoc(DocumentFile.Save(p)) == CanonDoc(states[i].Doc), $"undo to state {i}: the document differs");
                Require(PartGeometry(p) == states[i].Geo, $"undo to state {i}: the geometry differs");
            }
            Require(!hist.CanUndo && hist.RedoCount == n, $"undo stack {hist.UndoCount}, redo {hist.RedoCount}");
            for (int i = 1; i <= n; i++)
            {
                p = hist.Redo(p)!.Value.Part;
                Require(CanonDoc(DocumentFile.Save(p)) == CanonDoc(states[i].Doc) && PartGeometry(p) == states[i].Geo, $"redo to state {i} differs");
            }
            p = hist.Undo(p)!.Value.Part; p = hist.Undo(p)!.Value.Part;
            hist.Record(p); var rf2 = p.Features.OfType<RoundFeature>().Single(); rf2.Radius = new ValueRef(7); p.MarkDirtyFrom(rf2); p.Rebuild(null);
            Require(!hist.CanRedo, "a new edit after undo must clear redo");
            p = hist.Undo(p)!.Value.Part;
            Require(CanonDoc(DocumentFile.Save(p)) == CanonDoc(states[n - 2].Doc), "undo of the new edit");
            Log(new { test = "KRN08-UNDO-REDO", edits = edits.Count, volume = p.Volume });
        });
        Check("KRN08-LONG-HISTORY-EDITS", () =>
        {
            var q = new HistParams(6, 5, false, 4, 3, false, 3);
            var p = BuildHistory(q);
            Require(p.Features.All(f => f.Error == null), "initial build: " + string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}")));
            var edits = new List<(string Name, Func<HistParams, HistParams> Q, Action<Part> Apply)>
            {
                ("first holes d6→8", x => x with { D1 = 8 }, pp => { foreach (var h in pp.Features.OfType<HoleFeature>().Take(6)) h.Diameter = new ValueRef(8); pp.MarkDirtyFrom(pp.Features.OfType<HoleFeature>().First()); }),
                ("round R5→3", x => x with { RoundR = 3 }, pp => { var r = pp.Features.OfType<RoundFeature>().Single(); r.Radius = new ValueRef(3); pp.MarkDirtyFrom(r); }),
                ("suppress round", x => x with { RoundOff = true }, pp => { var r = pp.Features.OfType<RoundFeature>().Single(); r.Suppressed = true; pp.MarkDirtyFrom(r); }),
                ("dimples r4→3", x => x with { SphR = 3 }, pp => { foreach (var s in pp.Features.OfType<CutSphere>()) s.R = 3; pp.MarkDirtyFrom(pp.Features.OfType<CutSphere>().First()); }),
                ("unsuppress round", x => x with { RoundOff = false }, pp => { var r = pp.Features.OfType<RoundFeature>().Single(); r.Suppressed = false; pp.MarkDirtyFrom(r); }),
                ("suppress hole 7", x => x with { Hole7Off = true }, pp => { var h = pp.Features.OfType<HoleFeature>().ElementAt(6); h.Suppressed = true; pp.MarkDirtyFrom(h); }),
                ("pattern ×3→2", x => x with { Copies = 2 }, pp => { var pf = pp.Features.OfType<LinearPatternFeature>().Single(); pf.Count = new ValueRef(2); pp.MarkDirtyFrom(pf); }),
                ("chamfer 3→2", x => x with { ChamferD = 2 }, pp => { var c = pp.Features.OfType<ChamferFeature>().Single(); c.D1 = new ValueRef(2); c.D2 = new ValueRef(2); pp.MarkDirtyFrom(c); }),
                ("unsuppress hole 7", x => x with { Hole7Off = false }, pp => { var h = pp.Features.OfType<HoleFeature>().ElementAt(6); h.Suppressed = false; pp.MarkDirtyFrom(h); }),
                ("first holes d8→5.5", x => x with { D1 = 5.5 }, pp => { foreach (var h in pp.Features.OfType<HoleFeature>().Take(6)) h.Diameter = new ValueRef(5.5); pp.MarkDirtyFrom(pp.Features.OfType<HoleFeature>().First()); }),
            };
            var fails = new List<string>();
            foreach (var (name, fq, apply) in edits)
            {
                q = fq(q);
                apply(p); p.Rebuild(null);
                var fresh = BuildHistory(q);
                var errs = p.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}").ToList();
                var errsF = fresh.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}").ToList();
                if (errs.Count > 0 || errsF.Count > 0) fails.Add($"{name}: errors incremental [{string.Join("; ", errs)}] fresh [{string.Join("; ", errsF)}]");
                string g1 = PartGeometry(p), g2 = PartGeometry(fresh);
                if (g1 != g2) fails.Add($"{name}: incremental ≠ fresh (V {p.Volume:R} vs {fresh.Volume:R}, bodies {p.Bodies.Count}/{fresh.Bodies.Count})");
                foreach (var b in p.Bodies) { var v = SolidValidator.Validate(b); if (!v.IsValid) fails.Add($"{name}: {v.Summary()}"); }
                Log(new { test = "KRN08-LONG-HISTORY", edit = name, volume = p.Volume, bodies = p.Bodies.Count, levels = string.Join(",", p.Bodies.Select(b => b.Guarantee)) });
            }
            Require(fails.Count == 0, string.Join(" | ", fails.Take(6)));
        });
    }
}

static partial class Program
{
    static void HistNamesProbe()
    {
        var p = new Part("hist"); TestGeometry.AddCube(p, 100); p.Rebuild(null);
        Console.WriteLine("cube faces: " + string.Join(", ", p.Bodies[0].Faces.Select(f => f.Tag)));
        var e = CornerEdge(p, -50, 50); var er = EdgeRef.Of(e);
        Console.WriteLine($"edge ref: {er}");
        var round = new RoundFeature("R1") { Radius = new ValueRef(5) }; round.Edges.Add(EdgeRef.Of(CornerEdge(p, 50, 50)));
        p.AddFeature(round); p.Rebuild(null);
        Console.WriteLine("after round: " + string.Join(", ", p.Bodies[0].Faces.Select(f => f.Tag)));
        foreach (var ed in p.Bodies[0].Edges.Where(x => Math.Abs(x.V0.P.X + 50) < 1e-6 && Math.Abs(x.V0.P.Y - 50) < 1e-6 && Math.Abs(x.V1.P.X + 50) < 1e-6 && Math.Abs(x.V1.P.Y - 50) < 1e-6))
            Console.WriteLine($"corner edge faces: {string.Join(" / ", ed.CoEdges.Select(ce => ce.Loop?.Face?.Tag))} keys now [{EdgeRef.EndKey(ed.V0)}] [{EdgeRef.EndKey(ed.V1)}]; ref keys [{er.End0}] [{er.End1}] faceA {ed.FaceA?.Tag} faceB {ed.FaceB?.Tag}");
        var ch = new ChamferFeature { Name = "C1", D1 = new ValueRef(3), D2 = new ValueRef(3) }; ch.Edges.Add(er);
        p.AddFeature(ch); p.Rebuild(null);
        Console.WriteLine($"chamfer after round: {ch.Error}");
        var p2 = new Part("h2"); TestGeometry.AddCube(p2, 100); p2.Rebuild(null);
        var ch2 = new ChamferFeature { Name = "C1", D1 = new ValueRef(3), D2 = new ValueRef(3) }; ch2.Edges.Add(EdgeRef.Of(CornerEdge(p2, -50, 50)));
        p2.AddFeature(ch2); p2.Rebuild(null);
        Console.WriteLine($"chamfer alone: {ch2.Error}");
        var p3 = new Part("h3"); TestGeometry.AddCube(p3, 100); p3.Rebuild(null);
        var ch3 = new ChamferFeature { Name = "C1", D1 = new ValueRef(3), D2 = new ValueRef(3) }; ch3.Edges.Add(EdgeRef.Of(CornerEdge(p3, 50, 50)));
        p3.AddFeature(ch3); p3.Rebuild(null);
        Console.WriteLine($"chamfer alone at (50,50): {ch3.Error}");
    }
}
