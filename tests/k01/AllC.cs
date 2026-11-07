using System.Diagnostics;
using System.Text;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k181 (замечание 07.11, 21:36): «Tests AllC» — КОМБИНАТОРНЫЙ НАБОР ОПЕРАЦИЙ. Базовая деталь — блок 100×60×40
    /// (x −50..50, y −30..30, z 0..40). Наборы:
    ///  S1 — каждая операция в 5 размерах (слот грани/ребра меняется с размером);
    ///  S2 — три одинаковые операции подряд на соседних слотах (грани верх/+X/+Y, рёбра и вершины у угла (50,30,40));
    ///  S3 — все упорядоченные пары разных операций в 3 размерах (вторая — на соседнем слоте);
    ///  S4 — детерминированная выборка троек разных операций в 3 размерах (P3D_ALLC_TRIPLES, по умолчанию 200).
    /// На каждый шаг: время пересчёта, параметры, ошибка, ΔV, ожидаемый ΔV (на нетронутом блоке), грани, гарантия, годность,
    /// понижения, предупреждения. Вердикты: FAIL (ошибка, негодное тело, ΔV не того знака или нулевой, объём не сходится),
    /// SLOW (> 10 с), WARN (гранение, неточная гарантия, неточный объём), OK. Отбор: P3D_ALLC_SETS=1,2,3,4,
    /// P3D_ALLC_ONLY=подстрока идентификатора; P3D_ALLC_OUT=папка — модели упавших случаев (.p3d) для воспроизведения.
    /// </summary>
    static void AllCChecks()
    {
        var ops = AllCOps();
        string sets = Environment.GetEnvironmentVariable("P3D_ALLC_SETS") ?? "1,2,3,4";
        string? only = Environment.GetEnvironmentVariable("P3D_ALLC_ONLY");
        string? outDir = Environment.GetEnvironmentVariable("P3D_ALLC_OUT");
        int triples = int.TryParse(Environment.GetEnvironmentVariable("P3D_ALLC_TRIPLES"), out var tt) ? tt : 200;
        double stepSec = double.TryParse(Environment.GetEnvironmentVariable("P3D_ALLC_STEPSEC"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ss) ? ss : 90;
        double[] five = { 0.1, 0.25, 0.45, 0.65, 0.85 }, three = { 0.15, 0.4, 0.7 };

        var cases = new List<(string Set, string Id, List<(AllCOp Op, double F, int Slot)> Steps)>();
        if (sets.Contains('1'))
            foreach (var op in ops) for (int k = 0; k < five.Length; k++) cases.Add(("S1", $"S1:{op.Name}@{five[k]:0.##}/s{k % 3}", new() { (op, five[k], k % 3) }));
        if (sets.Contains('2'))
            foreach (var op in ops.Where(o => o.Repeat)) foreach (var f in three) cases.Add(("S2", $"S2:{op.Name}x3@{f:0.##}", new() { (op, f, 0), (op, f, 1), (op, f, 2) }));
        if (sets.Contains('3'))
            foreach (var a in ops) foreach (var b in ops) if (a != b) foreach (var f in three)
                        cases.Add(("S3", $"S3:{a.Name}+{b.Name}@{f:0.##}", new() { (a, f, 0), (b, f, 1) }));
        if (sets.Contains('4'))
        {
            var rnd = new Random(181);
            var seen = new HashSet<string>();
            int guard = 0;
            while (seen.Count < triples && guard++ < triples * 50)
            {
                int i = rnd.Next(ops.Count), j = rnd.Next(ops.Count), k = rnd.Next(ops.Count);
                if (i == j || j == k || i == k) continue;
                if (new[] { i, j, k }.Count(x => !ops[x].Repeat) > 1) continue;
                if (!seen.Add($"{i},{j},{k}")) continue;
                foreach (var f in three)
                    cases.Add(("S4", $"S4:{ops[i].Name}+{ops[j].Name}+{ops[k].Name}@{f:0.##}", new() { (ops[i], f, 0), (ops[j], f, 1), (ops[k], f, 2) }));
            }
        }
        if (!string.IsNullOrEmpty(only)) cases = cases.Where(c => c.Id.Contains(only)).ToList();
        Console.WriteLine($"ALLC: {ops.Count} operations, {cases.Count} cases (sets {sets}, triples {triples}, step deadline {stepSec} s)");
        Console.WriteLine("ALLC: operations: " + string.Join(", ", ops.Select(o => o.Name)));

        var rows = new List<AllCRow>();
        var total = Stopwatch.StartNew();
        int ci = 0;
        foreach (var (set, id, steps) in cases)
        {
            ci++;
            var caseRows = AllCRun(set, id, steps, stepSec);
            rows.AddRange(caseRows);
            string worst = caseRows.Any(r => r.Verdict == "FAIL") ? "FAIL" : caseRows.Any(r => r.Verdict == "SLOW") ? "SLOW" : caseRows.Any(r => r.Verdict == "WARN") ? "WARN" : "OK";
            foreach (var r in caseRows) Log(r.ToLog());
            Log(new { test = "ALLC " + id, ok = worst != "FAIL", verdict = worst, ms = caseRows.Sum(r => r.Ms),
                      error = worst == "FAIL" ? string.Join(" | ", caseRows.Where(r => r.Verdict == "FAIL").Select(r => $"step {r.Step} {r.Op}: {r.Why}")) : null });
            if (worst == "FAIL" && !string.IsNullOrEmpty(outDir) && caseRows.LastOrDefault()?.Model is { } m)
                try { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, "allc_" + string.Concat(id.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')) + ".p3d"), m); } catch { }
            if (ci % 50 == 0) Console.WriteLine($"ALLC progress: {ci}/{cases.Count} cases, {total.Elapsed:hh\\:mm\\:ss}, FAIL steps {rows.Count(r => r.Verdict == "FAIL")}");
        }
        AllCSummary(rows, total.Elapsed);
    }

    sealed class AllCRow
    {
        public string Set = "", Case = "", Op = "", Params = "", Verdict = "OK", Why = "", Guarantee = "";
        public int Step, Faces, Bodies;
        public double F, Ms, V0, V1, Dv; public double? Want, Rel;
        public int Slot; public bool Valid; public string? Error; public List<string> Warnings = new(); public string? Downgrade;
        public string? Model;
        public object ToLog() => new { allc = Case, set = Set, step = Step, op = Op, f = F, slot = Slot, prm = Params, verdict = Verdict, why = Why == "" ? null : Why,
            ms = Math.Round(Ms, 1), v0 = Math.Round(V0, 4), v1 = Math.Round(V1, 4), dv = Math.Round(Dv, 4), want = Want is { } w ? Math.Round(w, 4) : (double?)null,
            rel = Rel, faces = Faces, bodies = Bodies, guarantee = Guarantee, valid = Valid, error = Error, downgrade = Downgrade,
            warnings = Warnings.Count == 0 ? null : Warnings.Select(x => x.Length > 300 ? x[..300] + "…" : x).ToList() };
    }

    /// <summary>Операция AllC: имя, знак ΔV (+1 добавляет, −1 снимает, 0 — любой), применение к детали (размер f ∈ (0,1), слот 0..2)
    /// с возвратом параметров и ожидаемого ΔV на нетронутом блоке (null — нет формулы).</summary>
    sealed record AllCOp(string Name, int Sign, Func<Part, double, int, (string Prm, double? Want)> Apply)
    {
        /// <summary>Повтор подряд имеет смысл (оболочка оболочки — нет: стенки уже тонкие).</summary>
        public bool Repeat { get; init; } = true;
    }

    // грани-слоты: верх (+Z), +X, +Y — сходятся в углу (50,30,40); рамка (центр, U, V), U×V = наружная нормаль; полуразмеры по U и V; толщина вдоль −n
    static (Vector3d C, Vector3d U, Vector3d V, double A, double B, double T) AllCFace(int slot) => slot switch
    {
        0 => (new Vector3d(0, 0, 40), Vector3d.UnitX, Vector3d.UnitY, 50, 30, 40),
        1 => (new Vector3d(50, 0, 20), Vector3d.UnitY, Vector3d.UnitZ, 30, 20, 100),
        _ => (new Vector3d(0, 30, 20), Vector3d.UnitZ, Vector3d.UnitX, 20, 50, 60),
    };
    // рёбра-слоты у угла (50,30,40): середина и длина на нетронутом блоке
    static (Vector3d Mid, double L) AllCEdge(int slot) => slot switch
    {
        0 => (new Vector3d(50, 0, 40), 60), 1 => (new Vector3d(0, 30, 40), 100), _ => (new Vector3d(50, 30, 20), 40),
    };
    static Vector3d AllCVertex(int slot) => slot switch { 0 => new(50, 30, 40), 1 => new(50, -30, 40), _ => new(50, 30, 0) };
    // боковые грани для уклона (нейтраль z = 0): +X, +Y, −Y; ширина грани
    static (Vector3d At, double W) AllCSide(int slot) => slot switch { 0 => (new(50, 0, 20), 60), 1 => (new(0, 30, 20), 100), _ => (new(0, -30, 20), 100) };

    static Sketch AllCPoly(Part p, Frame3 fr, params (double X, double Y)[] pts)
    {
        var s = new Sketch($"S{p.Sketches.Count() + 1}", fr); p.Add(s);
        var sp = pts.Select(q => new SkPoint(q.X, q.Y)).ToList();
        for (int i = 0; i < sp.Count; i++) s.AddEntity(new SkLine(sp[i], sp[(i + 1) % sp.Count]));
        return s;
    }
    static Sketch AllCCircle(Part p, Frame3 fr, double x, double y, double r)
    {
        var s = new Sketch($"S{p.Sketches.Count() + 1}", fr); p.Add(s);
        s.AddEntity(new SkCircle(new SkPoint(x, y), r));
        return s;
    }
    static Sketch AllCRect(Part p, Frame3 fr, double cx, double cy, double hw, double hd) =>
        AllCPoly(p, fr, (cx - hw, cy - hd), (cx + hw, cy - hd), (cx + hw, cy + hd), (cx - hw, cy + hd));

    static TEdge? AllCNearestEdge(Part p, Vector3d at) =>
        p.Bodies.SelectMany(b => b.Edges).OrderBy(e => (e.Curve.PointAt(0.5 * (e.Curve.T0 + e.Curve.T1)) - at).Length).FirstOrDefault();
    static Vector3d? AllCNearestVertex(Part p, Vector3d at) =>
        p.Bodies.SelectMany(b => b.Vertices).Select(v => (Vector3d?)v.P).OrderBy(v => (v!.Value - at).Length).FirstOrDefault();

    static List<AllCOp> AllCOps()
    {
        var ops = new List<AllCOp>();
        string N(double x) => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        // центр элемента на грани: смещение к общему углу (+U, +V) на 35 % полуразмеров; footprint внутри грани — есть формула
        (Frame3 Fr, double Cx, double Cy, double A, double B, double T) Place(int slot)
        {
            var (c, u, v, a, b, t) = AllCFace(slot);
            return (new Frame3(c, u, v), 0.35 * a, 0.35 * b, a, b, t);
        }
        bool Inside(double cx, double cy, double hw, double hd, double a, double b) => Math.Abs(cx) + hw < a - 1e-9 && Math.Abs(cy) + hd < b - 1e-9;

        ops.Add(new("boss_rect", +1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, _) = Place(s); double hw = f * 0.6 * a, hd = f * 0.6 * b, h = 4 + f * 30;
            var sk = AllCRect(p, fr, cx, cy, hw, hd);
            var ex = new ExtrudeFeature("Boss") { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(h); p.AddFeature(ex);
            return ($"rect {N(2 * hw)}x{N(2 * hd)} h{N(h)} face{s}", Inside(cx, cy, hw, hd, a, b) ? 4 * hw * hd * h : null);
        }));
        ops.Add(new("boss_circle", +1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, _) = Place(s); double r = f * 0.6 * Math.Min(a, b), h = 4 + f * 30;
            var sk = AllCCircle(p, fr, cx, cy, r);
            var ex = new ExtrudeFeature("BossC") { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(h); p.AddFeature(ex);
            return ($"circle r{N(r)} h{N(h)} face{s}", Inside(cx, cy, r, r, a, b) ? Math.PI * r * r * h : null);
        }));
        ops.Add(new("pocket_rect", -1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, t) = Place(s); double hw = f * 0.6 * a, hd = f * 0.6 * b, d = (0.1 + 0.6 * f) * t;
            var sk = AllCRect(p, fr, cx, cy, hw, hd);
            var ex = new ExtrudeFeature("Pocket") { SketchId = sk.Id, Cut = true }; ex.Side1.Distance = new ValueRef(d); ex.Side1.Flip = true; p.AddFeature(ex);
            return ($"rect {N(2 * hw)}x{N(2 * hd)} depth {N(d)} face{s}", Inside(cx, cy, hw, hd, a, b) ? -4 * hw * hd * d : null);
        }));
        ops.Add(new("pocket_circle", -1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, t) = Place(s); double r = f * 0.6 * Math.Min(a, b), d = (0.1 + 0.6 * f) * t;
            var sk = AllCCircle(p, fr, cx, cy, r);
            var ex = new ExtrudeFeature("PocketC") { SketchId = sk.Id, Cut = true }; ex.Side1.Distance = new ValueRef(d); ex.Side1.Flip = true; p.AddFeature(ex);
            return ($"circle r{N(r)} depth {N(d)} face{s}", Inside(cx, cy, r, r, a, b) ? -Math.PI * r * r * d : null);
        }));
        ops.Add(new("cut_rect_through", -1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, t) = Place(s); double hw = f * 0.5 * a, hd = f * 0.5 * b;
            var sk = AllCRect(p, fr, cx, cy, hw, hd);
            var ex = new ExtrudeFeature("CutThru") { SketchId = sk.Id, Cut = true }; ex.Side1.End = EndType.ThroughAll; ex.Side1.Flip = true; p.AddFeature(ex);
            return ($"rect {N(2 * hw)}x{N(2 * hd)} through face{s}", Inside(cx, cy, hw, hd, a, b) ? -4 * hw * hd * t : null);
        }));
        ops.Add(new("cut_circle_through", -1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, t) = Place(s); double r = f * 0.5 * Math.Min(a, b);
            var sk = AllCCircle(p, fr, cx, cy, r);
            var ex = new ExtrudeFeature("CutThruC") { SketchId = sk.Id, Cut = true }; ex.Side1.End = EndType.ThroughAll; ex.Side1.Flip = true; p.AddFeature(ex);
            return ($"circle r{N(r)} through face{s}", Inside(cx, cy, r, r, a, b) ? -Math.PI * r * r * t : null);
        }));
        ops.Add(new("hole_through", -1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, t) = Place(s); double r = f * 0.4 * Math.Min(a, b);
            var h = new HoleFeature("HoleT") { Direction = -fr.W, ThroughAll = true, Diameter = new ValueRef(2 * r) };
            h.Points.Add(fr.ToWorld(cx, cy)); p.AddFeature(h);
            return ($"d{N(2 * r)} through face{s}", Inside(cx, cy, r, r, a, b) ? -Math.PI * r * r * t : null);
        }));
        ops.Add(new("hole_blind", -1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, t) = Place(s); double r = f * 0.4 * Math.Min(a, b), d = (0.15 + 0.5 * f) * t;
            var h = new HoleFeature("HoleB") { Direction = -fr.W, Diameter = new ValueRef(2 * r), Depth = new ValueRef(d) };
            h.Points.Add(fr.ToWorld(cx, cy)); p.AddFeature(h);
            return ($"d{N(2 * r)} depth {N(d)} face{s}", null);
        }));
        ops.Add(new("revolve_add", +1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, _) = Place(s); double R = f * 0.6 * Math.Min(a, b), h = 4 + f * 30;
            var c = fr.ToWorld(cx, cy);
            var sk = AllCPoly(p, new Frame3(c, fr.U, fr.W), (0, 0), (R, 0), (0, h));
            var axis = new SkLine(new SkPoint(0, 0), new SkPoint(0, h)) { Role = SkRole.Centerline }; sk.AddEntity(axis);
            p.AddFeature(new RevolveFeature("RevAdd") { SketchId = sk.Id, AxisEntityId = axis.Id });
            return ($"cone R{N(R)} h{N(h)} face{s}", Inside(cx, cy, R, R, a, b) ? Math.PI * R * R * h / 3 : null);
        }));
        ops.Add(new("revolve_cut", -1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, t) = Place(s); double R = f * 0.6 * Math.Min(a, b), h = (0.1 + 0.6 * f) * t;
            var c = fr.ToWorld(cx, cy);
            var sk = AllCPoly(p, new Frame3(c, fr.U, fr.W), (0, 0), (0, -h), (R, 0));
            var axis = new SkLine(new SkPoint(0, -h), new SkPoint(0, 0)) { Role = SkRole.Centerline }; sk.AddEntity(axis);
            p.AddFeature(new RevolveFeature("RevCut") { SketchId = sk.Id, AxisEntityId = axis.Id, Cut = true });
            return ($"cone R{N(R)} depth {N(h)} face{s}", Inside(cx, cy, R, R, a, b) ? -Math.PI * R * R * h / 3 : null);
        }));
        ops.Add(new("loft_add", +1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, _) = Place(s); double hw = f * 0.6 * a, hd = f * 0.6 * b, h = 4 + f * 30;
            var s1 = AllCRect(p, fr, cx, cy, hw, hd);
            var s2 = AllCRect(p, new Frame3(fr.Origin + fr.W * h, fr.U, fr.V), cx, cy, hw / 2, hd / 2);
            var lf = new LoftFeature("Loft"); lf.SectionSketchIds.Add(s1.Id); lf.SectionSketchIds.Add(s2.Id); p.AddFeature(lf);
            return ($"rect {N(2 * hw)}x{N(2 * hd)} -> half, h{N(h)} face{s}", Inside(cx, cy, hw, hd, a, b) ? 7.0 / 12 * 4 * hw * hd * h : null);
        }));
        ops.Add(new("sweep_add", +1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, _) = Place(s); double r = 1 + f * 0.25 * Math.Min(a, b), Ra = 3 * r + f * 20;
            var c = fr.ToWorld(cx, cy);
            var sec = AllCCircle(p, new Frame3(c, fr.U, fr.V), 0, 0, r);
            var path = new Sketch($"S{p.Sketches.Count() + 1}", new Frame3(c, fr.U, fr.W)); p.Add(path);
            path.AddEntity(new SkArc(new SkPoint(Ra, 0), new SkPoint(0, 0), new SkPoint(Ra, Ra), false));
            p.AddFeature(new SweepFeature("Sweep") { SectionSketchId = sec.Id, PathSketchId = path.Id });
            return ($"circle r{N(r)} along quarter arc R{N(Ra)} face{s}", Math.PI * r * r * Math.PI / 2 * Ra);
        }));
        ops.Add(new("round_edge", -1, (p, f, s) =>
        {
            var (mid, L) = AllCEdge(s); double R = f * 18;
            var e = AllCNearestEdge(p, mid) ?? throw new Exception("no edge");
            var rf = new RoundFeature("Round") { Radius = new ValueRef(R) }; rf.Edges.Add(EdgeRef.Of(e)); p.AddFeature(rf);
            return ($"R{N(R)} edge{s}", -(1 - Math.PI / 4) * R * R * L);
        }));
        ops.Add(new("chamfer_edge", -1, (p, f, s) =>
        {
            var (mid, L) = AllCEdge(s); double D = f * 18;
            var e = AllCNearestEdge(p, mid) ?? throw new Exception("no edge");
            var cf = new ChamferFeature("Chamfer") { D1 = new ValueRef(D), D2 = new ValueRef(D) }; cf.Edges.Add(EdgeRef.Of(e)); p.AddFeature(cf);
            return ($"D{N(D)} edge{s}", -D * D / 2 * L);
        }));
        ops.Add(new("vertex_round", -1, (p, f, s) =>
        {
            double R = f * 15;
            var v = AllCNearestVertex(p, AllCVertex(s)) ?? throw new Exception("no vertex");
            p.AddFeature(new VertexRoundFeature("VRound") { Vertex = v, Radius = new ValueRef(R) });
            return ($"R{N(R)} vertex{s}", null);
        }));
        ops.Add(new("vertex_chamfer", -1, (p, f, s) =>
        {
            double D = f * 15;
            var v = AllCNearestVertex(p, AllCVertex(s)) ?? throw new Exception("no vertex");
            p.AddFeature(new VertexChamferFeature("VChamfer") { Vertex = v, Distance = new ValueRef(D) });
            return ($"D{N(D)} vertex{s}", -D * D * D / 6);
        }));
        ops.Add(new("shell", -1, (p, f, s) =>
        {
            var (c, u, v, a, b, t) = AllCFace(s); double th = 1 + f * 8;
            var sh = new ShellFeature("Shell") { Thickness = new ValueRef(th) }; sh.OpenFaces.Add(c); p.AddFeature(sh);
            return ($"t{N(th)} open face{s}", -(2 * a - 2 * th) * (2 * b - 2 * th) * (t - th));
        }) { Repeat = false });
        ops.Add(new("draft_in", -1, (p, f, s) =>
        {
            var (at, w) = AllCSide(s); double ang = 1 + f * 12;
            var df = new DraftFeature("DraftIn") { NeutralPoint = Vector3d.Zero, NeutralNormal = Vector3d.UnitZ, Angle = new ValueRef(ang) }; df.OnFaces.Add(at); p.AddFeature(df);
            return ($"{N(ang)} deg side{s}, neutral z=0", -w * 40 * 40 / 2 * Math.Tan(ang * Math.PI / 180));
        }));
        ops.Add(new("draft_out", +1, (p, f, s) =>
        {
            var (at, w) = AllCSide(s); double ang = 1 + f * 12;
            var df = new DraftFeature("DraftOut") { NeutralPoint = Vector3d.Zero, NeutralNormal = Vector3d.UnitZ, Angle = new ValueRef(-ang) }; df.OnFaces.Add(at); p.AddFeature(df);
            return ($"-{N(ang)} deg side{s}, neutral z=0", w * 40 * 40 / 2 * Math.Tan(ang * Math.PI / 180));
        }));
        ops.Add(new("linear_pattern", +1, (p, f, s) =>
        {
            var (fr, _, _, a, b, _) = Place(s); double r = 1.5 + f * 0.12 * Math.Min(a, b), h = 3 + f * 10, step = 2 * r + 3;
            double cx = -step, cy = -0.4 * b;
            var sk = AllCCircle(p, fr, cx, cy, r);
            var ex = new ExtrudeFeature("PinSrc") { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(h); p.AddFeature(ex);
            var lp = new LinearPatternFeature("LinPat") { Step = fr.U * step, Count = new ValueRef(3) }; lp.Sources.Add(ex.Key); p.AddFeature(lp);
            return ($"pin r{N(r)} h{N(h)} x3 step {N(step)} face{s}", Inside(cx + step, cy, r + step, r, a, b) ? 3 * Math.PI * r * r * h : null);
        }));
        ops.Add(new("circular_pattern", -1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, t) = Place(s); double rr = 0.45 * Math.Min(a, b) * (0.4 + 0.5 * f), r = Math.Max(0.8, f * 0.25 * rr);
            var h = new HoleFeature("HoleSrc") { Direction = -fr.W, ThroughAll = true, Diameter = new ValueRef(2 * r) };
            h.Points.Add(fr.ToWorld(cx + rr, cy)); p.AddFeature(h);
            var cp = new CircularPatternFeature("CircPat") { At = fr.ToWorld(cx, cy), Axis = fr.W, Count = new ValueRef(4) }; cp.Sources.Add(h.Key); p.AddFeature(cp);
            return ($"hole d{N(2 * r)} x4 on R{N(rr)} face{s}", Inside(cx, cy, rr + r, rr + r, a, b) ? -4 * Math.PI * r * r * t : null);
        }));
        ops.Add(new("mirror", +1, (p, f, s) =>
        {
            var (fr, cx, cy, a, b, _) = Place(s); double r = 1 + f * 0.25 * Math.Min(a, b), h = 3 + f * 15;
            var sk = AllCCircle(p, fr, cx, cy, r);
            var ex = new ExtrudeFeature("MirSrc") { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(h); p.AddFeature(ex);
            var m = new MirrorFeature("Mirror") { At = fr.Origin, Normal = fr.U }; m.Sources.Add(ex.Key); p.AddFeature(m);
            return ($"boss r{N(r)} h{N(h)} + mirror across U face{s}", Inside(cx, cy, r, r, a, b) ? 2 * Math.PI * r * r * h : null);
        }));
        return ops;
    }

    static List<AllCRow> AllCRun(string set, string id, List<(AllCOp Op, double F, int Slot)> steps, double stepSec)
    {
        var rows = new List<AllCRow>();
        var p = new Part("allc");
        var s0 = RectSketch(p, 0, -50, -30, 100, 60);
        var body = new ExtrudeFeature("Block") { SketchId = s0.Id }; body.Side1.Distance = new ValueRef(40); p.AddFeature(body);
        try { p.Rebuild(null); } catch (Exception e) { rows.Add(new AllCRow { Set = set, Case = id, Op = "block", Verdict = "FAIL", Why = "base block: " + e.Message }); return rows; }
        bool pristine = true;
        for (int k = 0; k < steps.Count; k++)
        {
            var (op, f, slot) = steps[k];
            var r = new AllCRow { Set = set, Case = id, Step = k + 1, Op = op.Name, F = f, Slot = slot, V0 = p.Volume };
            rows.Add(r);
            int nf = p.Features.Count;
            double? want;
            try { (r.Params, want) = op.Apply(p, f, slot); }
            catch (Exception e) { r.Verdict = "FAIL"; r.Why = "setup: " + e.Message; break; }
            if (pristine) r.Want = want;
            var before = p.Bodies.ToList();
            if (Environment.GetEnvironmentVariable("P3D_ALLC_DIAG") == "1")
                BooleanOp.GeneralAcceptProbe = (ba, bb, kind, res) =>
                {
                    Console.WriteLine($"ALLCDIAG general boolean {kind} accepted: operand A, operand B, result");
                    foreach (var x in new[] { ba, bb, res }) AllCDumpFaces(x);
                };
            p.Limits = new KernelLimits { Deadline = DateTime.UtcNow.AddSeconds(stepSec) };
            var sw = Stopwatch.StartNew();
            try { p.Rebuild(null); }
            catch (Exception e) { r.Error = "exception: " + e.GetType().Name + ": " + e.Message; }
            r.Ms = sw.Elapsed.TotalMilliseconds;
            BooleanOp.GeneralAcceptProbe = null;
            p.Limits = KernelLimits.None;
            var added = p.Features.Skip(nf).ToList();
            r.Error ??= added.Select(x => x.Error).FirstOrDefault(x => x != null);
            r.V1 = p.Volume; r.Dv = r.V1 - r.V0;
            r.Bodies = p.Bodies.Count; r.Faces = p.Bodies.Sum(b => b.Faces.Count());
            r.Guarantee = string.Join(",", p.Bodies.Select(b => b.Guarantee));
            string? invalidWhy = null;
            try
            {
                r.Valid = p.Bodies.Count > 0;
                foreach (var b in p.Bodies)
                {
                    var vr = SolidValidator.Validate(b);
                    if (vr.IsValid) continue;
                    r.Valid = false;
                    invalidWhy ??= vr.Summary() + " " + string.Join(" / ", vr.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(3));
                }
            }
            catch (Exception e) { r.Valid = false; invalidWhy = "validator: " + e.Message; }
            r.Warnings.AddRange(p.Warnings.Distinct());
            var dg = added.Where(x => p.Downgrades.ContainsKey(x)).Select(x => p.Downgrades[x]).ToList();
            if (dg.Count > 0) r.Downgrade = $"{dg.Sum(x => x.Faces)} faceted face(s), {dg.Sum(x => x.Edges)} faceted edge(s)";
            // вердикт
            var why = new List<string>(); string v = "OK";
            void Fail(string w) { why.Add(w); v = "FAIL"; }
            void Warn(string w) { why.Add(w); if (v == "OK") v = "WARN"; }
            if (r.Error != null) Fail("error: " + r.Error);
            else
            {
                if (p.Bodies.Count == 0) Fail("no body");
                else if (!r.Valid) Fail("invalid body: " + invalidWhy);
                double tiny = 1e-10 * Math.Max(1, r.V0);
                if (Math.Abs(r.Dv) <= tiny) Fail("no change in volume");
                else if (op.Sign != 0 && Math.Sign(r.Dv) != op.Sign) Fail($"volume changed the wrong way ({r.Dv:G6})");
                if (r.Want is { } wv && Math.Abs(wv) > 0)
                {
                    r.Rel = (r.Dv - wv) / Math.Abs(wv);
                    if (Math.Abs(r.Rel.Value) > 2e-3) Fail($"volume change {r.Dv:G8} vs expected {wv:G8} (rel {r.Rel:E2})");
                    else if (Math.Abs(r.Rel.Value) > 1e-5) Warn($"volume change inexact (rel {r.Rel:E2})");
                }
                if (r.Downgrade != null) Warn("faceted: " + r.Downgrade);
                if (p.Bodies.Any(b => b.Guarantee is not (GuaranteeLevel.ExactAnalytic or GuaranteeLevel.ExactQuadric))) Warn("guarantee " + r.Guarantee);
            }
            if (r.Ms > 10000 && v != "FAIL") { why.Add($"slow {r.Ms / 1000:0.0} s"); v = "SLOW"; }
            r.Verdict = v; r.Why = string.Join("; ", why);
            pristine = false;
            if (v == "FAIL" && Environment.GetEnvironmentVariable("P3D_ALLC_DIAG") == "1")
            {
                Console.WriteLine($"ALLCDIAG {id} step {k + 1} {op.Name}: before");
                foreach (var b in before) AllCDumpFaces(b);
                Console.WriteLine($"ALLCDIAG {id} step {k + 1} {op.Name}: after");
                foreach (var b in p.Bodies) AllCDumpFaces(b);
            }
            if (v == "FAIL")
            {
                try { r.Model = Parametric3D.Model.DocumentFile.Save(p); } catch { }
                break;
            }
        }
        return rows;
    }

    static void AllCDumpFaces(TSolid b)
    {
        Console.WriteLine($"  body guarantee {b.Guarantee}, faces {b.Faces.Count()}, exact {(b.Exact != null)}, qexact {(b.QExact != null)}");
        Parametric3D.Kernel.Tess.MeshData? mesh = null;
        try { mesh = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b); } catch (Exception e) { Console.WriteLine("  tessellate: " + e.Message); }
        int i = 0;
        foreach (var f in b.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            string facet = "-";
            if (f.Surface is FacetedSurface fs && !fs.Facets.IsPending && fs.Facets.Count > 0)
            {
                var n = Vector3d.Zero; var c = Vector3d.Zero; int m = 0;
                foreach (var (a, bb, cc) in fs.Facets.ReadView) { n += Vector3d.Cross(bb - a, cc - a); c += (a + bb + cc) / 3; m++; }
                c /= m;
                double dn = double.NaN; try { dn = Vector3d.Dot(n.Normalized(), f.NormalAt(c)); } catch { }
                facet = $"facets {m} sum-n ({n.X:0.#},{n.Y:0.#},{n.Z:0.#}) dot(face normal) {dn:0.###} sense {fs.Sense} lazy {fs.LazySense}";
                if (fs.DisplaySnapshot is { } ds)
                {
                    var dn2 = Vector3d.Zero; foreach (var (a, bb, cc) in ds.Tris) dn2 += Vector3d.Cross(bb - a, cc - a);
                    facet += $" display {ds.Tris.Count} sum-n ({dn2.X:0.#},{dn2.Y:0.#},{dn2.Z:0.#})";
                }
            }
            string sn = "-";
            try { var c0 = f.Loops[0].CoEdges[0].Edge.Curve.PointAt(0.5 * (f.Loops[0].CoEdges[0].Edge.Curve.T0 + f.Loops[0].CoEdges[0].Edge.Curve.T1)); var q = src!.NormalAt(src.ProjectToUV(c0)); sn = $"({q.X:0.##},{q.Y:0.##},{q.Z:0.##})"; } catch { }
            string ms = "-";
            if (mesh != null && mesh.FaceRanges.TryGetValue(f.Id, out var fr))
            {
                var n = Vector3d.Zero;
                for (int t = fr.IndexStart; t + 2 < fr.IndexStart + fr.IndexCount; t += 3)
                {
                    var a = mesh.Vertices[mesh.Indices[t]].P; var bb = mesh.Vertices[mesh.Indices[t + 1]].P; var cc = mesh.Vertices[mesh.Indices[t + 2]].P;
                    var cr = Vector3.Cross(bb - a, cc - a); n += new Vector3d(cr.X, cr.Y, cr.Z);
                }
                ms = $"mesh tris {fr.IndexCount / 3} sum-n ({n.X:0.#},{n.Y:0.#},{n.Z:0.#})";
            }
            Console.WriteLine($"  F{i++} id {f.Id} tag {f.Tag} {src?.GetType().Name} src-n {sn} rev {f.Reversed} loops {f.Loops.Count} {facet} | {ms}");
        }
    }

    static void AllCSummary(List<AllCRow> rows, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=================== ALLC SUMMARY ===================");
        sb.AppendLine($"steps {rows.Count}, cases {rows.Select(r => r.Case).Distinct().Count()}, total {elapsed:hh\\:mm\\:ss}, step time sum {rows.Sum(r => r.Ms) / 1000:0.0} s");
        foreach (var g in rows.GroupBy(r => r.Verdict).OrderBy(g => g.Key)) sb.AppendLine($"  {g.Key,-5} {g.Count()}");
        sb.AppendLine("by set: " + string.Join("; ", rows.GroupBy(r => r.Set).Select(g => $"{g.Key}: {g.Count()} steps, FAIL {g.Count(r => r.Verdict == "FAIL")}, WARN {g.Count(r => r.Verdict == "WARN")}, SLOW {g.Count(r => r.Verdict == "SLOW")}")));
        sb.AppendLine();
        sb.AppendLine("by operation (step verdicts; time median / max ms):");
        foreach (var g in rows.GroupBy(r => r.Op).OrderByDescending(g => g.Count(r => r.Verdict == "FAIL")).ThenBy(g => g.Key))
        {
            var ms = g.Select(r => r.Ms).OrderBy(x => x).ToList();
            sb.AppendLine($"  {g.Key,-20} n {g.Count(),5}  FAIL {g.Count(r => r.Verdict == "FAIL"),4}  WARN {g.Count(r => r.Verdict == "WARN"),4}  SLOW {g.Count(r => r.Verdict == "SLOW"),3}  median {ms[ms.Count / 2],8:0}  max {ms[^1],8:0}");
        }
        sb.AppendLine();
        sb.AppendLine("failure reasons (grouped, operation | reason head):");
        foreach (var g in rows.Where(r => r.Verdict == "FAIL").GroupBy(r => r.Op + " | " + Head(r.Why)).OrderByDescending(g => g.Count()).Take(60))
            sb.AppendLine($"  {g.Count(),4}  {g.Key}   e.g. {g.First().Case} step {g.First().Step} [{g.First().Params}]");
        sb.AppendLine();
        sb.AppendLine("pairs that fail only together (first op passes alone, second fails after it):");
        foreach (var g in rows.Where(r => r.Verdict == "FAIL" && r.Step > 1).GroupBy(r => r.Case.Split('@')[0]).OrderBy(g => g.Key).Take(80))
            sb.AppendLine($"  {g.Key}: {string.Join(", ", g.Select(r => r.Case.Split('@').Last() + " " + Head(r.Why)))}");
        sb.AppendLine();
        sb.AppendLine("slowest 25 steps:");
        foreach (var r in rows.OrderByDescending(r => r.Ms).Take(25))
            sb.AppendLine($"  {r.Ms,9:0} ms  {r.Case} step {r.Step} {r.Op} [{r.Params}] {r.Verdict} faces {r.Faces}");
        sb.AppendLine();
        sb.AppendLine("warnings (grouped, operation | reason head):");
        foreach (var g in rows.Where(r => r.Verdict == "WARN").GroupBy(r => r.Op + " | " + Head(r.Why)).OrderByDescending(g => g.Count()).Take(40))
            sb.AppendLine($"  {g.Count(),4}  {g.Key}   e.g. {g.First().Case}");
        sb.AppendLine("Reproduce one case: K01Checks allC with P3D_ALLC_ONLY=<case id>, P3D_ALLC_OUT=<folder> saves failed models.");
        Console.WriteLine(sb.ToString());
        Log(new { test = "ALLC-SUMMARY", ok = true, steps = rows.Count, fail = rows.Count(r => r.Verdict == "FAIL"), warn = rows.Count(r => r.Verdict == "WARN"), slow = rows.Count(r => r.Verdict == "SLOW"), seconds = Math.Round(elapsed.TotalSeconds) });

        static string Head(string w)
        {
            var s = w.Split(';')[0];
            s = System.Text.RegularExpressions.Regex.Replace(s, @"-?\d+(\.\d+)?(E[-+]?\d+)?", "#");
            return s.Length > 110 ? s[..110] : s;
        }
    }
}

static partial class Program
{
    /// <summary>
    /// k181: ОТОБРАННЫЕ СЛУЧАИ AllC (в allA). Бобышка по окружности (точный путь) + следующая общая булева — низ тела в общей разметке
    /// квадрик (QMesher) выходил вывернутым (P3D_QMPLANESIDE=0); скругление + бобышка на соседней боковой грани — плоская грань
    /// выдавливания с вырезом гранилась полной полосой (P3D_STRIPTRIM=0); бобышка по окружности — гарантия ExactQuadric
    /// (P3D_EXTRUDEQGUAR=0).
    /// </summary>
    static void AllCRegressChecks()
    {
        Check("ALLC-REGRESS-K181", () =>
        {
            var ops = AllCOps().ToDictionary(o => o.Name);
            var fails = new List<string>();
            foreach (var (a, b) in new[] { ("boss_circle", "loft_add"), ("boss_circle", "hole_through"), ("boss_circle", "vertex_chamfer"), ("round_edge", "boss_circle"), ("round_edge", "cut_circle_through") })
                foreach (var f in new[] { 0.15, 0.4 })
                {
                    string id = $"R:{a}+{b}@{f}";
                    var rows = AllCRun("R", id, new() { (ops[a], f, 0), (ops[b], f, 1) }, 90);
                    foreach (var r in rows) Log(r.ToLog());
                    foreach (var r in rows.Where(r => r.Verdict == "FAIL" || r.Op == "boss_circle" && !r.Guarantee.Contains("Exact")))
                        fails.Add($"{id} step {r.Step} {r.Op}: {r.Verdict} {r.Why} ({r.Guarantee})");
                }
            Require(fails.Count == 0, string.Join(" | ", fails.Take(6)));
        });
    }
}
