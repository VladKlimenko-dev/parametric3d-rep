using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k146: ЦЕПОЧКА ФАСОК по касательным рёбрам — кромка торца выдавливания скруглённого прямоугольника (8 рёбер). Стык звеньев —
    /// сечение в вершине (P3D_DRCHJOINT): шов боковых граней выдавливания лежит в плоскости сечения, точки фасок на нём общие.
    /// Снятый объём — по Паппу: d₁d₂/2 · (L_прямых + 2π(R − x̄)), x̄ = d/3 (центр треугольника от стенки, d — катет на торце).
    /// Лофт подобных контуров (шов наклонён) — отказ прямого пути с причиной, фаска строится резцом.
    /// </summary>
    static void Krn06ChamferChainK146Checks()
    {
        Check("KRN06-CHAMFER-CHAIN-K146", () =>
        {
            var fails = new List<string>();
            const double a = 30, b = 18, rc = 7;
            foreach (var (name, d1, d2) in new[] { ("equal", 2.0, 2.0), ("two distances", 1.5, 2.5) })
            {
                var p = new Part("cc");
                var s = RoundedRectSketch(p, 0, a, b, rc);
                var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(25); p.AddFeature(ex);
                p.Rebuild(null);
                double v0 = p.Volume;
                var top = p.Bodies.Single().Edges.Where(e => Math.Abs(e.V0.P.Z - 25) < 1e-6 && Math.Abs(e.V1.P.Z - 25) < 1e-6).ToList();
                var cf = new ChamferFeature("C") { D1 = new ValueRef(d1), D2 = new ValueRef(d2) }; foreach (var e in top) cf.Edges.Add(EdgeRef.Of(e));
                long j0 = DirectRound.JointsBuilt, c0 = DirectRound.CurvedChamfers;
                p.AddFeature(cf); p.Rebuild(null);
                if (cf.Error != null) { fails.Add($"{name}: {cf.Error.Split('\n').Last()}"); continue; }
                var body = p.Bodies.Single(); bool valid = SolidValidator.Validate(body).IsValid;
                double L = 4 * (a - rc) + 4 * (b - rc);
                double W(double dt, double ds) => dt * ds / 2 * (L + 2 * Math.PI * (rc - dt / 3));   // dt — катет на торце
                double got = v0 - p.Volume, w1 = W(d1, d2), w2 = W(d2, d1);
                double want = Math.Abs(got - w1) <= Math.Abs(got - w2) ? w1 : w2;
                Log(new { test = "KRN06-CHAMFER-CHAIN-K146", model = name, removed = got.ToString("R"), want, valid, guarantee = body.Guarantee.ToString(), joints = DirectRound.JointsBuilt - j0, direct = DirectRound.CurvedChamfers > c0 });
                if (!valid) fails.Add($"{name}: invalid body");
                if (body.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add($"{name}: guarantee {body.Guarantee}");
                // равные расстояния по замкнутому контуру — прежний путь «касательная петля» (стыков нет); два расстояния — стыки
                if (d1 != d2 && DirectRound.JointsBuilt - j0 != 8) fails.Add($"{name}: {DirectRound.JointsBuilt - j0} joints (8 expected)");
                if (Math.Abs(got - want) > 1e-6 * want) fails.Add($"{name}: removed {got:G12}, expected {want:G12}");
            }
            // лофт одинаковых контуров — призма с боковыми гранями NURBS: прямые рёбра между плоскостью и NURBS (прежде прямые рёбра в
            // путь кривых фасок не допускались — P3D_DRSTRAIGHTCH) и стыки на вертикальных швах; объём — та же формула
            {
                var p = new Part("ccp");
                var s1 = RoundedRectSketch(p, 0, a, b, rc); var s2 = RoundedRectSketch(p, -40, a, b, rc);
                var lf = new LoftFeature("Loft"); lf.SectionSketchIds.Add(s1.Id); lf.SectionSketchIds.Add(s2.Id); p.AddFeature(lf);
                p.Rebuild(null);
                double v0 = p.Volume;
                var top = p.Bodies.Single().Edges.Where(e => Math.Abs(e.V0.P.Z) < 1e-6 && Math.Abs(e.V1.P.Z) < 1e-6).ToList();
                const double d1 = 1.5, d2 = 2.5;
                var cf = new ChamferFeature("C") { D1 = new ValueRef(d1), D2 = new ValueRef(d2) }; foreach (var e in top) cf.Edges.Add(EdgeRef.Of(e));
                long j0 = DirectRound.JointsBuilt;
                p.AddFeature(cf); p.Rebuild(null);
                if (cf.Error != null) fails.Add("loft prism: " + cf.Error.Split('\n').Last());
                else
                {
                    var body = p.Bodies.Single(); bool valid = SolidValidator.Validate(body).IsValid;
                    double L = 4 * (a - rc) + 4 * (b - rc);
                    double W(double dt, double ds) => dt * ds / 2 * (L + 2 * Math.PI * (rc - dt / 3));
                    double got = v0 - p.Volume, w1 = W(d1, d2), w2 = W(d2, d1);
                    double want = Math.Abs(got - w1) <= Math.Abs(got - w2) ? w1 : w2;
                    Log(new { test = "KRN06-CHAMFER-CHAIN-K146", model = "loft prism", removed = got.ToString("R"), want, valid, guarantee = body.Guarantee.ToString(), joints = DirectRound.JointsBuilt - j0 });
                    if (!valid) fails.Add("loft prism: invalid body");
                    if (body.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add("loft prism: guarantee " + body.Guarantee);
                    if (DirectRound.JointsBuilt - j0 != 8) fails.Add($"loft prism: {DirectRound.JointsBuilt - j0} joints (8 expected)");
                    if (Math.Abs(got - want) > 1e-6 * want) fails.Add($"loft prism: removed {got:G12}, expected {want:G12}");
                }
            }
            // лофт подобных контуров: шов наклонён. k146 — отказ с причиной; k147 — боковая точка звена переходит на соседнюю
            // грань у шва (P3D_DRCHSWITCH): прямой путь, 8 переходов, точное тело; сетка против сетки — в пределах 1.5 % от
            // резца (у лофта «точный до, сетка после» резца — не мера: сетка боковых граней грубее точного объёма)
            {
                var p = new Part("ccl");
                var s1 = RoundedRectSketch(p, 0, a, b, rc); var s2 = RoundedRectSketch(p, -60, 0.7 * a, 0.7 * b, 0.7 * rc);
                var lf = new LoftFeature("Loft"); lf.SectionSketchIds.Add(s1.Id); lf.SectionSketchIds.Add(s2.Id); p.AddFeature(lf);
                p.Rebuild(null);
                double v0 = p.Volume;
                double MeshVol(TSolid x) => Parametric3D.Kernel.Tess.MassProps.SignedVolume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(TopologyClone.Deep(x)));
                double m0 = MeshVol(p.Bodies.Single());
                List<EdgeRef> Top() => p.Bodies.Single().Edges.Where(e => Math.Abs(e.V0.P.Z) < 1e-6 && Math.Abs(e.V1.P.Z) < 1e-6).Select(EdgeRef.Of).ToList();
                var cf = new ChamferFeature("C") { D1 = new ValueRef(2), D2 = new ValueRef(2) }; foreach (var e in Top()) cf.Edges.Add(e);
                string? dr = null; var dbg = DirectRound.Debug; DirectRound.Debug = x => { if (x.StartsWith("declined")) dr = x; };
                long sw0 = DirectRound.ChamferSwitches;
                try { p.AddFeature(cf); p.Rebuild(null); } finally { DirectRound.Debug = dbg; }
                var body = p.Bodies.Single(); bool valid = SolidValidator.Validate(body).IsValid;
                double got = v0 - p.Volume, gotMesh = m0 - MeshVol(body);
                Log(new { test = "KRN06-CHAMFER-CHAIN-K146", model = "loft", error = cf.Error, removed = got.ToString("R"), gotMesh, valid, guarantee = body.Guarantee.ToString(), switches = DirectRound.ChamferSwitches - sw0, dr });
                if (Environment.GetEnvironmentVariable("PROBE_FACES") == "1") FacesOf(p, "after");   // k151: разбор сетки по граням
                if (cf.Error != null) fails.Add("loft: " + cf.Error.Split('\n').Last());
                else
                {
                    if (dr != null) fails.Add("loft: not built by the direct builder: " + dr);
                    if (!valid) fails.Add("loft: invalid body");
                    if (body.Guarantee != GuaranteeLevel.ExactAnalytic) fails.Add("loft: guarantee " + body.Guarantee);
                    if (DirectRound.ChamferSwitches - sw0 != 8) fails.Add($"loft: {DirectRound.ChamferSwitches - sw0} side-point switches (8 expected)");
                    p.RemoveFeature(cf); p.Rebuild(null);
                    var c2 = new ChamferFeature("C2") { D1 = new ValueRef(2), D2 = new ValueRef(2) }; foreach (var e in Top()) c2.Edges.Add(e);
                    DirectRound.OffForTest = true;
                    try { p.AddFeature(c2); p.Rebuild(null); } finally { DirectRound.OffForTest = false; }
                    double cutMesh = m0 - MeshVol(p.Bodies.Single());
                    Log(new { test = "KRN06-CHAMFER-CHAIN-K146", model = "loft by the cutter", cutMesh, guarantee = p.Bodies.Single().Guarantee.ToString() });
                    // k151: сверка — точный снятый объём прямого пути против «сетка до − сетка после» резца, до 1 %. Прежняя мера
                    // «сетка против сетки» (до 1.5 %) упиралась в шум сетки NURBS-боковин лофта: у прямого тела число треугольников
                    // боковины скачет (60 или 1644) при сдвиге стыка на 7e-7 мм, недобор сетки — 1.7…2.8 мм³ на боковину
                    if (c2.Error != null || !(Math.Abs(cutMesh - got) < 0.01 * got)) fails.Add($"loft: cutter removed {cutMesh:G6} (mesh) against {got:G6} (exact)");
                }
            }
            Require(fails.Count == 0, string.Join(" | ", fails));
        });
    }
}
