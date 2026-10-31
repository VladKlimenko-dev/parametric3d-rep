using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>Скруглённый прямоугольник (полуразмеры a × b, радиус углов rc) в эскизе на высоте z: отрезки и дуги подряд, касательно.</summary>
    static Sketch RoundedRectSketch(Part p, double z, double a, double b, double rc)
    {
        var s = new Sketch($"S{p.Sketches.Count() + 1}", new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY)); p.Add(s);
        var pts = new[] { (-a + rc, -b), (a - rc, -b), (a, -b + rc), (a, b - rc), (a - rc, b), (-a + rc, b), (-a, b - rc), (-a, -b + rc) }.Select(q => new SkPoint(q.Item1, q.Item2)).ToArray();
        var cs = new[] { (a - rc, -b + rc), (a - rc, b - rc), (-a + rc, b - rc), (-a + rc, -b + rc) };
        for (int i = 0; i < 4; i++)
        {
            s.AddEntity(new SkLine(pts[2 * i], pts[2 * i + 1]));
            s.AddEntity(new SkArc(new SkPoint(cs[i].Item1, cs[i].Item2), pts[2 * i + 1], pts[(2 * i + 2) % 8], true));
        }
        return s;
    }

    /// <summary>
    /// k145 (скругления «как в Parasolid»): КАСАТЕЛЬНАЯ ЦЕПОЧКА — кромка торца лофта между подобными скруглёнными прямоугольниками
    /// (8 рёбер: отрезки и дуги, в вершинах — касательно, третье ребро вершины — гладкий шов боковых граней). Прямой построитель
    /// строит ленты звеньев и стыкует их по общему сечению на гладком шве (P3D_DRJOINT): тело точное (ExactAnalytic), годное,
    /// стыков — 8. Выдавливание того же контура: снятый объём — по Паппу, A·(L_прямых + 2π(R − x̄)), A = r²(1 − π/4).
    /// Шов, у которого излом растёт от вершины (лофт окружности в многоугольник), прямым путём не строится — отказ с причиной.
    /// </summary>
    static void Krn06TangentChainK145Checks()
    {
        Check("KRN06-TANGENT-CHAIN-K145", () =>
        {
            var fails = new List<string>();
            const double a = 30, b = 18, rc = 7, r = 3;
            // выдавливание: формула
            {
                var p = new Part("tcx");
                var s = RoundedRectSketch(p, 0, a, b, rc);
                var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(25); p.AddFeature(ex);
                p.Rebuild(null);
                double v0 = p.Volume;
                var top = p.Bodies.Single().Edges.Where(e => Math.Abs(e.V0.P.Z - 25) < 1e-6 && Math.Abs(e.V1.P.Z - 25) < 1e-6).ToList();
                if (top.Count != 8) fails.Add($"extrude: {top.Count} top edges");
                var rf = new RoundFeature("R") { Radius = new ValueRef(r) }; foreach (var e in top) rf.Edges.Add(EdgeRef.Of(e));
                long b0 = DirectRound.Built;
                p.AddFeature(rf); p.Rebuild(null);
                if (rf.Error != null) fails.Add("extrude: " + rf.Error.Split('\n').Last());
                else
                {
                    var body = p.Bodies.Single(); bool valid = SolidValidator.Validate(body).IsValid;
                    double xb = r * (10 - 3 * Math.PI) / (3 * (4 - Math.PI)), A = r * r * (1 - Math.PI / 4);
                    double want = A * (4 * (a - rc) + 4 * (b - rc) + 2 * Math.PI * (rc - xb));
                    double got = v0 - p.Volume;
                    Log(new { test = "KRN06-TANGENT-CHAIN-K145", model = "extrude", removed = got.ToString("R"), want, valid, guarantee = body.Guarantee.ToString(), direct = DirectRound.Built > b0 });
                    if (!valid) fails.Add("extrude: invalid body");
                    if (body.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add("extrude: guarantee " + body.Guarantee);
                    if (Math.Abs(got - want) > 1e-5 * want) fails.Add($"extrude: removed {got:G10}, expected {want:G10}");
                }
            }
            // лофт одинаковых контуров — призма с боковыми гранями NURBS: тот же открытый построитель со стыками, объём — по Паппу
            {
                var p = new Part("tcp");
                var s1 = RoundedRectSketch(p, 0, a, b, rc); var s2 = RoundedRectSketch(p, -40, a, b, rc);
                var lf = new LoftFeature("Loft"); lf.SectionSketchIds.Add(s1.Id); lf.SectionSketchIds.Add(s2.Id); p.AddFeature(lf);
                p.Rebuild(null);
                double v0 = p.Volume;
                var kinds = string.Join(",", p.Bodies.Single().Faces.Select(f => f.Surface.GetType().Name).Distinct().OrderBy(x => x));
                var top = p.Bodies.Single().Edges.Where(e => Math.Abs(e.V0.P.Z) < 1e-6 && Math.Abs(e.V1.P.Z) < 1e-6).ToList();
                var rf = new RoundFeature("R") { Radius = new ValueRef(r) }; foreach (var e in top) rf.Edges.Add(EdgeRef.Of(e));
                long j0 = DirectRound.JointsBuilt;
                p.AddFeature(rf); p.Rebuild(null);
                if (rf.Error != null) fails.Add("loft prism: " + rf.Error.Split('\n').Last());
                else
                {
                    var body = p.Bodies.Single(); bool valid = SolidValidator.Validate(body).IsValid;
                    double xb = r * (10 - 3 * Math.PI) / (3 * (4 - Math.PI)), A = r * r * (1 - Math.PI / 4);
                    double want = A * (4 * (a - rc) + 4 * (b - rc) + 2 * Math.PI * (rc - xb));
                    double got = v0 - p.Volume;
                    Log(new { test = "KRN06-TANGENT-CHAIN-K145", model = "loft prism", faces = kinds, removed = got.ToString("R"), want, valid, guarantee = body.Guarantee.ToString(), joints = DirectRound.JointsBuilt - j0 });
                    if (!valid) fails.Add("loft prism: invalid body");
                    if (body.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add("loft prism: guarantee " + body.Guarantee);
                    if (Math.Abs(got - want) > 2e-7) fails.Add($"loft prism: removed {got:G10}, expected {want:G10}");
                }
            }
            // лофт подобных контуров (второй — 0.7 первого): швы боковых граней гладкие по всей длине
            {
                var p = new Part("tcl");
                var s1 = RoundedRectSketch(p, 0, a, b, rc); var s2 = RoundedRectSketch(p, -60, 0.7 * a, 0.7 * b, 0.7 * rc);
                var lf = new LoftFeature("Loft"); lf.SectionSketchIds.Add(s1.Id); lf.SectionSketchIds.Add(s2.Id); p.AddFeature(lf);
                p.Rebuild(null);
                double v0 = p.Volume;
                double MeshVol(TSolid x) => Parametric3D.Kernel.Tess.MassProps.SignedVolume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(TopologyClone.Deep(x)));
                double m0 = MeshVol(p.Bodies.Single());
                var top = p.Bodies.Single().Edges.Where(e => Math.Abs(e.V0.P.Z) < 1e-6 && Math.Abs(e.V1.P.Z) < 1e-6).ToList();
                if (top.Count != 8) fails.Add($"loft: {top.Count} top edges");
                var rf = new RoundFeature("R") { Radius = new ValueRef(r) }; foreach (var e in top) rf.Edges.Add(EdgeRef.Of(e));
                long b0 = DirectRound.Built, j0 = DirectRound.JointsBuilt;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                p.AddFeature(rf); p.Rebuild(null);
                if (rf.Error != null) fails.Add("loft: " + rf.Error.Split('\n').Last());
                else
                {
                    var body = p.Bodies.Single(); bool valid = SolidValidator.Validate(body).IsValid;
                    double got = v0 - p.Volume, gotMesh = m0 - MeshVol(body);
                    Log(new { test = "KRN06-TANGENT-CHAIN-K145", model = "loft", gotMesh, ms = sw.ElapsedMilliseconds, removed = got.ToString("R"), valid, guarantee = body.Guarantee.ToString(), direct = DirectRound.Built > b0, joints = DirectRound.JointsBuilt - j0, warn = string.Join("; ", p.Warnings.Where(x => x.Contains("Round")).Take(2)) });
                    if (!valid) fails.Add("loft: invalid body");
                    if (body.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add("loft: guarantee " + body.Guarantee);
                    if (DirectRound.Built == b0) fails.Add("loft: not built by the direct builder");
                    if (DirectRound.JointsBuilt - j0 != 8) fails.Add($"loft: {DirectRound.JointsBuilt - j0} tangent joints (8 expected)");
                    // снятый объём — тот же, что при первом прогоне (точная лента), и в пределах 1.5 % от резца (у резца хорда —
                    // снятое больше примерно на полпроцента)
                    if (Math.Abs(got - 440.4815759647) > 1e-6) fails.Add($"loft: removed {got:G13}, expected 440.4815759647");
                    p.RemoveFeature(rf); p.Rebuild(null);
                    var rc2 = new RoundFeature("R2") { Radius = new ValueRef(r) }; foreach (var e in p.Bodies.Single().Edges.Where(e => Math.Abs(e.V0.P.Z) < 1e-6 && Math.Abs(e.V1.P.Z) < 1e-6)) rc2.Edges.Add(EdgeRef.Of(e));
                    DirectRound.OffForTest = true;
                    try { p.AddFeature(rc2); p.Rebuild(null); } finally { DirectRound.OffForTest = false; }
                    // резец — сетка против сетки (у лофта сетка боковых граней грубее точного объёма; разность объёмов «точный до,
                    // сетка после» у резца — не мера). Хорда резца снимает чуть больше: в пределах 1.5 %
                    double cutMesh = m0 - MeshVol(p.Bodies.Single());
                    Log(new { test = "KRN06-TANGENT-CHAIN-K145", model = "loft by the cutter", cutMesh, guarantee = p.Bodies.Single().Guarantee.ToString() });
                    if (rc2.Error != null || !(Math.Abs(cutMesh - gotMesh) < 0.015 * gotMesh)) fails.Add($"loft: cutter removed {cutMesh:G6} (mesh) against {gotMesh:G6}");
                }
            }
            Require(fails.Count == 0, string.Join(" | ", fails));
        });
    }
}
