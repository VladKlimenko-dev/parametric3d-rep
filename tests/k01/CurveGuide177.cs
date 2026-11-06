using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>
    /// k177: КРИВАЯ ЧЕРЕЗ ТОЧКИ С КАСАТЕЛЬНЫМИ И НАПРАВЛЯЮЩАЯ «ПО СЕЧЕНИЯМ». Модель пользователя gear.p3d (07.11): две шестерни
    /// (z10, m10 на z = 0 и m 22.6, поворот 45° на z = −70), кривая через вершину вершины зуба нижней и соответственную вершину
    /// верхней — «По сечениям» не брал опорную кривую направляющей. Норма: CurveThrough — точно через точки, касательные по
    /// условиям (знак по ходу, «обратить», вес — длина контрольного плеча), у точки без условия — по соседям, G1 на стыках;
    /// переход «По сечениям» с кривой-направляющей строится (боковое ребро тела идёт по кривой), касание крайних сечений при
    /// направляющих не ставится (предупреждение); запись и чтение: номера направляющих и условия точек на месте.
    /// Мутация P3D_GUIDECURVES=0 — ребра по кривой нет.
    /// </summary>
    static void CurveGuideChecks()
    {
        Check("CURVE-TANGENTS-GUIDE-K177", () =>
        {
            var problems = new List<string>();
            var rep = new List<object>();
            static Vector3d D(SplineCurve3 s, double t) { double h = 1e-6; return (s.PointAt(Math.Min(1, t + h)) - s.PointAt(Math.Max(0, t - h))).Normalized(); }

            // 1. две точки, обе по нормали (0, 0, 1): знак — по ходу (вниз), второй обращён
            var a = new Vector3d(15, 58, 0); var b = new Vector3d(-67, 118, -70);
            var s1 = CurveThrough.Build(new[] { a, b }, new Vector3d?[] { Vector3d.UnitZ, Vector3d.UnitZ }, new[] { false, false }, null, false)!;
            var cp = s1.ControlPoints;
            if (cp.Count != 4 || CurveThrough.AsBezier(s1) == null) problems.Add($"two points: {cp.Count} control points");
            if ((s1.PointAt(0) - a).Length > 1e-12 || (s1.PointAt(1) - b).Length > 1e-12) problems.Add("two points: ends");
            var t0 = (cp[1] - cp[0]).Normalized(); var t1 = (cp[3] - cp[2]).Normalized();
            if (Math.Abs(t0.Z + 1) > 1e-12 || Math.Abs(t1.Z + 1) > 1e-12) problems.Add($"two points: tangents {t0} {t1}");
            var s1f = CurveThrough.Build(new[] { a, b }, new Vector3d?[] { Vector3d.UnitZ, Vector3d.UnitZ }, new[] { false, true }, new[] { 1.0, 2.0 }, false)!;
            var cf = s1f.ControlPoints;
            if (Math.Abs((cf[3] - cf[2]).Normalized().Z - 1) > 1e-12) problems.Add("flip does not reverse");
            if (Math.Abs((cf[3] - cf[2]).Length - 2 * (b - a).Length / 3) > 1e-9) problems.Add($"weight 2: arm {(cf[3] - cf[2]).Length}");
            // 2. четыре точки, условие только у второй; у прочих — по соседям; G1
            var q = new[] { new Vector3d(0, 0, 0), new Vector3d(10, 5, 0), new Vector3d(20, 0, 3), new Vector3d(30, 4, 6) };
            var dir = new Vector3d(1, 1, 1).Normalized();
            var s2 = CurveThrough.Build(q, new Vector3d?[] { null, dir, null, null }, null, null, false)!;
            double[] kn = s2.Knots.ToArray();
            double tAt1 = kn[4];
            var dl = (s2.PointAt(tAt1) - s2.PointAt(tAt1 - 1e-7)).Normalized(); var dr = (s2.PointAt(tAt1 + 1e-7) - s2.PointAt(tAt1)).Normalized();
            if ((s2.PointAt(tAt1) - q[1]).Length > 1e-9) problems.Add("four points: through the 2nd point");
            if (Math.Abs(Vector3d.Dot(dl, dir) - 1) > 1e-6 || Math.Abs(Vector3d.Dot(dr, dir) - 1) > 1e-6) problems.Add($"four points: tangent at the 2nd {dl} {dr}");
            double tAt2 = kn[7];
            var el = (s2.PointAt(tAt2) - s2.PointAt(tAt2 - 1e-7)).Normalized(); var er = (s2.PointAt(tAt2 + 1e-7) - s2.PointAt(tAt2)).Normalized();
            if (Vector3d.Dot(el, er) < 1 - 1e-6) problems.Add("four points: G1 at a free point");
            if (CurveThrough.AsBezier(s2) != null) problems.Add("a three-piece curve passed as one Bezier");

            // 3. модель пользователя: две шестерни, кривая через соответственные вершины, переход по сечениям
            Part MakePart(bool guide)
            {
                var part = new Part("gear");
                var sk1 = new Sketch("Sketch1", Frame3.XY); part.Add(sk1);
                var g1 = sk1.AddGear(new GearParams { Teeth = 10, Module = 10, PitchCircle = false }, out _)!;
                var fr2 = new Frame3(new Vector3d(0, 0, -70), Vector3d.UnitX, Vector3d.UnitY);
                var sk2 = new Sketch("Sketch2", fr2); part.Add(sk2);
                var g2p = new GearParams { Teeth = 10, Module = 22.627416997969522, Rotation = 45, PitchCircle = false };
                sk2.AddGear(g2p, out _);
                // вершина тип «вершина зуба» второго зуба (сторона +1): конец дуги вершины — по сегментам построения
                var segs1 = GearGeometry.Build(sk1.Gears[0].P, out _)!; var segs2 = GearGeometry.Build(g2p, out _)!;
                int tipArc = Enumerable.Range(0, segs1.Count).Where(i => segs1[i].Kind == GearGeometry.SegKind.Arc && (segs1[i].C).Length < 1e-9 && segs1[i].End.Length > 59).Skip(2).First();
                var A = new Vector3d(segs1[tipArc].End.X, segs1[tipArc].End.Y, 0);
                var B2 = fr2.ToWorld(segs2[tipArc].End);
                var specs = new[] { new CurvePointSpec { SketchId = sk1.Id, SketchNormal = Vector3d.UnitZ, NormalToSketch = true }, new CurvePointSpec { SketchId = sk2.Id, SketchNormal = Vector3d.UnitZ, NormalToSketch = true } };
                var sp = CurveThrough.Build(new[] { A, B2 }, specs.Select(x => x.Direction).ToList(), new[] { false, false }, null, false)!;
                var dc = new DatumCurve("Curve1", Array.Empty<Vector3d>(), "through points") { Mode = 0 };
                dc.Inputs.Add(A); dc.Inputs.Add(B2); dc.Specs.AddRange(specs); dc.SetSpline(sp);
                part.Add(dc);
                var bf = new BlendFeature { Mode = BlendKind.Sections, StartTangent = 0.5, EndTangent = 0.5, Smooth = true };
                bf.SectionSketchIds.Add(sk1.Id); bf.SectionSketchIds.Add(sk2.Id);
                if (guide) bf.GuideCurveIds.Add(dc.Id);
                part.AddFeature(bf);
                part.Rebuild(null);
                return part;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var p = MakePart(true);
            var bfe = p.Features.OfType<BlendFeature>().Single();
            var dcv = p.Children.OfType<DatumCurve>().Single();
            var mid = dcv.Spline!.PointAt(0.5);
            double edgeOff = p.Bodies.Count == 0 ? double.NaN : p.Bodies.SelectMany(x => x.Edges).Min(e => (e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) - mid).Length);
            rep.Add(new { test = "gear blend", ms = sw.ElapsedMilliseconds, error = bfe.Error, volume = p.Volume, bodies = p.Bodies.Count, edgeOff });
            if (bfe.Error != null || p.Bodies.Count != 1 || !(p.Volume > 0)) problems.Add($"blend with the guide curve: {bfe.Error}");
            else if (!(edgeOff < 1e-6)) problems.Add($"no side edge along the guide curve (nearest {edgeOff:E2})");
            // запись и чтение
            var (r, rp) = DocumentFile.Load(DocumentFile.Save(p, null)); r.Rebuild(rp);
            var rbf = r.Features.OfType<BlendFeature>().Single(); var rdc = r.Children.OfType<DatumCurve>().Single();
            rep.Add(new { test = "file", guides = rbf.GuideCurveIds.ToList(), curve = rdc.Id, specs = rdc.Specs.Count, volume = r.Volume, rbf.Error });
            if (rbf.GuideCurveIds.Count != 1 || rbf.GuideCurveIds[0] != rdc.Id) problems.Add("guide curve id lost in the file");
            if (rdc.Specs.Count != 2 || !rdc.Specs.All(x => x.NormalToSketch && x.FromSketch)) problems.Add("point conditions lost in the file");
            if (rbf.Error != null || Math.Abs(r.Volume - p.Volume) > 1e-6 * p.Volume) problems.Add($"file volume {r.Volume} vs {p.Volume} ({rbf.Error})");

            Log(new { test = "CURVE-TANGENTS-GUIDE-K177", rep, problems });
            Require(problems.Count == 0, string.Join("; ", problems));
        });
    }
}
