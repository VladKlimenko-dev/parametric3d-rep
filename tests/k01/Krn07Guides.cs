using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    static void Krn07GuideLoftChecks()
    {
        Check("KRN07-LOFT-GUIDES", () =>
        {
            // 07.4: ЛОФТ С НАПРАВЛЯЮЩИМИ. Квадрат полустороны a = 5 (z = 0) → b = 3 (z = 30); четыре направляющие — квадратичные
            // Безье через углы (±a, ±a, 0) → (±c, ±c, 15) → (±b, ±b, 30), c = 7. Грань Кунса стороны x = +: x = f(v), z = 30v,
            // f(v) = a(1−v)² + 2c·v(1−v) + b·v² (члены сечений сокращаются с билинейным) — сечение тела на высоте z — квадрат
            // полустороны f, V = 4·30·∫f² dv (многочлен 4-й степени — Гаусс по 3 узлам точен). Три сечения (средний квадрат
            // полустороны f(½) на z = 15) — тот же объём (направляющие делятся в своих точках). Окружности r10 → r5 и четыре
            // прямые направляющие — точно усечённый конус πh(a² + ab + b²)/3 (рациональный Кунс в однородных координатах).
            // Направляющая против хода сечений — обращается; мимо вершины — честный отказ. Модель: LoftFeature.GuideSketchIds
            // (эскизы со сплайнами в диагональных плоскостях), сохранение/загрузка документа.
            var fails = new List<string>();
            const double a = 5, b = 3, c = 7, h = 30;
            double F(double v) => a * (1 - v) * (1 - v) + 2 * c * v * (1 - v) + b * v * v;
            double I = 0; { double[] gx = { -0.7745966692414834, 0, 0.7745966692414834 }, gw = { 5.0 / 9, 8.0 / 9, 5.0 / 9 }; for (int q = 0; q < 3; q++) { double v = 0.5 + 0.5 * gx[q]; I += 0.5 * gw[q] * F(v) * F(v); } }
            double wantSq = 4 * h * I;
            Profile Sq(double s, double z)
            {
                var pl = new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
                Vector3d P(double x, double y) => new(x, y, z);
                return ProfileBuilder.Build(pl, new ICurve3[] { new LineCurve3(P(-s, -s), P(s, -s)), new LineCurve3(P(s, -s), P(s, s)), new LineCurve3(P(s, s), P(-s, s)), new LineCurve3(P(-s, s), P(-s, -s)) });
            }
            Profile Circ(double r, double z) => ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new ICurve3[] { new ArcCurve3(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), r, 0, 2 * Math.PI) });
            List<ICurve3> Guides(bool reversed = false)
            {
                var gs = new List<ICurve3>();
                foreach (var (sx, sy) in new[] { (1, 1), (-1, 1), (-1, -1), (1, -1) })
                {
                    var pts = new[] { new Vector3d(sx * a, sy * a, 0), new Vector3d(sx * c, sy * c, h / 2), new Vector3d(sx * b, sy * b, h) };
                    gs.Add(new BezierCurve3(reversed ? Enumerable.Reverse(pts) : pts));
                }
                return gs;
            }
            void Case(string name, Func<TSolid> build, double want)
            {
                long g0 = DirectRound.GuidedLofts;
                try
                {
                    var body = build();
                    double v = AnalyticVolume.Of(body) ?? double.NaN;
                    Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}) {body.Guarantee}, {body.Faces.Count()} faces");
                    if (DirectRound.GuidedLofts == g0) fails.Add($"{name}: not the guided loft");
                    if (body.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(body).IsValid) fails.Add($"{name}: {body.Guarantee} {SolidValidator.Validate(body).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                    var sw = new StringWriter(); var ws = new List<string>();
                    if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { body }, "lg", ws) || ws.Any(x => x.Contains("not written exactly"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Take(2))})");
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
            }
            Case("square 5 → 3, four Bezier guides", () => LoftOp.Build(new[] { Sq(a, 0), Sq(b, h) }, 64, false, Guides()), wantSq);
            Case("same, guides drawn top to bottom", () => LoftOp.Build(new[] { Sq(a, 0), Sq(b, h) }, 64, false, Guides(true)), wantSq);
            Case("three sections (middle on the guides)", () => LoftOp.Build(new[] { Sq(a, 0), Sq(F(0.5), h / 2), Sq(b, h) }, 64, false, Guides()), wantSq);
            Case("circle r10 → r5, four straight guides", () => LoftOp.Build(new[] { Circ(10, 0), Circ(5, h) }, 64, false,
                    new ICurve3[] { new LineCurve3(new(10, 0, 0), new(5, 0, h)), new LineCurve3(new(0, 10, 0), new(0, 5, h)), new LineCurve3(new(-10, 0, 0), new(-5, 0, h)), new LineCurve3(new(0, -10, 0), new(0, -5, h)) }),
                 Math.PI * h * (100 + 50 + 25) / 3);
            // мимо вершины — отказ с причиной
            try
            {
                LoftOp.Build(new[] { Sq(a, 0), Sq(b, h) }, 64, false, new ICurve3[] { new LineCurve3(new(a, 0, 0), new(b, b, h)) });
                fails.Add("a guide off the vertices: accepted");
            }
            catch (RebuildException ex) when (ex.Message.Contains("vertex")) { Console.WriteLine($"  guide off the vertices: refused ({ex.Message})"); }
            catch (Exception ex) { fails.Add($"a guide off the vertices: {ex.GetType().Name} {ex.Message}"); }

            // МОДЕЛЬ: направляющие — сплайны эскизов в диагональных плоскостях (оси u = (±1, 1, 0)/√2, v = z)
            {
                var p = new Part("loftg");
                Sketch SqSk(double s, double z)
                {
                    var sk = new Sketch($"S{p.Sketches.Count() + 1}", new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY)); p.Add(sk);
                    var pts = new[] { new SkPoint(-s, -s), new SkPoint(s, -s), new SkPoint(s, s), new SkPoint(-s, s) };
                    for (int i = 0; i < 4; i++) sk.AddEntity(new SkLine(pts[i], pts[(i + 1) % 4]));
                    return sk;
                }
                var s0 = SqSk(a, 0); var s1 = SqSk(b, h);
                double r2 = Math.Sqrt(2);
                Sketch GuideSk(Vector3d u)
                {
                    var sk = new Sketch($"G{p.Sketches.Count() + 1}", new Frame3(Vector3d.Zero, u, Vector3d.UnitZ)); p.Add(sk);
                    foreach (int sg in new[] { 1, -1 }) sk.AddEntity(new SkSpline(new[] { new SkPoint(sg * a * r2, 0), new SkPoint(sg * c * r2, h / 2), new SkPoint(sg * b * r2, h) }));
                    return sk;
                }
                var g1 = GuideSk(new Vector3d(1, 1, 0).Normalized()); var g2 = GuideSk(new Vector3d(1, -1, 0).Normalized());
                var lf = new LoftFeature(); lf.SectionSketchIds.Add(s0.Id); lf.SectionSketchIds.Add(s1.Id); lf.GuideSketchIds.Add(g1.Id); lf.GuideSketchIds.Add(g2.Id);
                p.AddFeature(lf); p.Rebuild(null);
                if (lf.Error != null) fails.Add("model: " + lf.Error);
                else
                {
                    double v = p.Volume;
                    Console.WriteLine($"  model (LoftFeature.GuideSketchIds): V {v:R} want {wantSq:R} ({(v - wantSq) / wantSq:E1})");
                    if (!(Math.Abs(v - wantSq) <= 1e-9 * wantSq)) fails.Add($"model: V {v:R} vs {wantSq:R}");
                    var (q, pars) = DocumentFile.Load(DocumentFile.Save(p));
                    q.Rebuild(pars);
                    var lq = q.Features.OfType<LoftFeature>().Single();
                    if (lq.GuideSketchIds.Count != 2 || lq.Error != null || !(Math.Abs(q.Volume - v) <= 1e-9 * v)) fails.Add($"model reload: guides {lq.GuideSketchIds.Count}, error {lq.Error}, V {q.Volume:R}");
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }

    static void Krn07LoftTangencyChecks()
    {
        Check("KRN07-LOFT-END-TANGENCY", () =>
        {
            // 07.4: КОНТРОЛЬ ФОРМЫ — касательная к нормали крайнего сечения длиной k·L (L — расстояние между центрами крайних
            // сечений). Два подобных соосных сечения: у столбца опорных точек (x, y) одинаковы у Q0, Q1 и у Q2, Q3, поэтому срез
            // v = const — подобная фигура «смеси» s(v) на высоте z(v), где s и z — многочлены Безье с опорными [0, 0, 1, 1] и
            // [0, kL/3, h − k′L/3, h] (одна касательная: степень 2, [0, 0, 1] и [0, kL/2, h]); V = ∫ A(s) z′ dv — Гаусс (точно).
            var fails = new List<string>();
            double[] gx = { -0.9324695142031521, -0.6612093864662645, -0.2386191860831969, 0.2386191860831969, 0.6612093864662645, 0.9324695142031521 };
            double[] gw = { 0.1713244923791704, 0.3607615730481386, 0.4679139345726910, 0.4679139345726910, 0.3607615730481386, 0.1713244923791704 };
            static double Bern(double[] c, double v) { var b = (double[])c.Clone(); for (int r = 1; r < b.Length; r++) for (int i = 0; i < b.Length - r; i++) b[i] = b[i] * (1 - v) + b[i + 1] * v; return b[0]; }
            static double BernD(double[] c, double v) { int n = c.Length - 1; var d = new double[n]; for (int i = 0; i < n; i++) d[i] = n * (c[i + 1] - c[i]); return Bern(d, v); }
            double Want(Func<double, double> area, double[] sc, double[] zc) { double V = 0; for (int q = 0; q < 6; q++) { double v = 0.5 + 0.5 * gx[q]; V += 0.5 * gw[q] * area(Bern(sc, v)) * BernD(zc, v); } return V; }
            const double h = 30;
            Profile Sq(double s, double z)
            {
                Vector3d P(double x, double y) => new(x, y, z);
                return ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new ICurve3[] { new LineCurve3(P(-s, -s), P(s, -s)), new LineCurve3(P(s, -s), P(s, s)), new LineCurve3(P(s, s), P(-s, s)), new LineCurve3(P(-s, s), P(-s, -s)) });
            }
            Profile Circ(double r, double z) => ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new ICurve3[] { new ArcCurve3(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), r, 0, 2 * Math.PI) });
            void Case(string name, Func<TSolid> build, double want)
            {
                try
                {
                    var body = build();
                    double v = AnalyticVolume.Of(body) ?? double.NaN;
                    Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}) {body.Guarantee}");
                    if (body.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(body).IsValid) fails.Add($"{name}: {body.Guarantee} {SolidValidator.Validate(body).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-9 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); }
            }
            Func<double, double> SqA(double a, double b) => s => 4 * Math.Pow(a * (1 - s) + b * s, 2);
            Func<double, double> CiA(double a, double b) => s => Math.PI * Math.Pow(a * (1 - s) + b * s, 2);
            Case("square 5 → 3, tangents 1 and 0.5", () => LoftOp.Build(new[] { Sq(5, 0), Sq(3, h) }, 64, false, null, 1, 0.5), Want(SqA(5, 3), new double[] { 0, 0, 1, 1 }, new[] { 0, h / 3, h - 0.5 * h / 3, h }));
            Case("circle r10 → r5, tangents 1 and 1", () => LoftOp.Build(new[] { Circ(10, 0), Circ(5, h) }, 64, false, null, 1, 1), Want(CiA(10, 5), new double[] { 0, 0, 1, 1 }, new[] { 0, h / 3, h - h / 3, h }));
            Case("square 5 → 3, start tangent 0.8 only", () => LoftOp.Build(new[] { Sq(5, 0), Sq(3, h) }, 64, false, null, 0.8, 0), Want(SqA(5, 3), new double[] { 0, 0, 1 }, new[] { 0, 0.8 * h / 2, h }));
            // три сечения с касательными: тело годно и точно, объём сходится с фасетами (эталона формулой нет)
            try
            {
                var b3 = LoftOp.Build(new[] { Sq(5, 0), Sq(4, 15), Sq(3, h) }, 64, true, null, 1, 1);
                Console.WriteLine($"  three sections, tangents 1, 1: {b3.Guarantee}, {SolidValidator.Validate(b3).Summary()}");
                if (b3.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(b3).IsValid) fails.Add($"three sections: {b3.Guarantee}");
            }
            catch (Exception ex) { fails.Add($"three sections: {ex.GetType().Name} {ex.Message}"); }
            // модель и документ
            {
                var p = new Part("loftt");
                Sketch SqSk(double sz, double z)
                {
                    var sk = new Sketch($"S{p.Sketches.Count() + 1}", new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY)); p.Add(sk);
                    var pts = new[] { new SkPoint(-sz, -sz), new SkPoint(sz, -sz), new SkPoint(sz, sz), new SkPoint(-sz, sz) };
                    for (int i = 0; i < 4; i++) sk.AddEntity(new SkLine(pts[i], pts[(i + 1) % 4]));
                    return sk;
                }
                var s0 = SqSk(5, 0); var s1 = SqSk(3, h);
                var lf = new LoftFeature { StartTangent = 1, EndTangent = 0.5 }; lf.SectionSketchIds.Add(s0.Id); lf.SectionSketchIds.Add(s1.Id);
                p.AddFeature(lf); p.Rebuild(null);
                double want = Want(SqA(5, 3), new double[] { 0, 0, 1, 1 }, new[] { 0, h / 3, h - 0.5 * h / 3, h });
                var (q, pars) = DocumentFile.Load(DocumentFile.Save(p)); q.Rebuild(pars);
                Console.WriteLine($"  model: V {p.Volume:R}, reloaded {q.Volume:R}, want {want:R}");
                if (lf.Error != null || !(Math.Abs(p.Volume - want) <= 1e-9 * want) || !(Math.Abs(q.Volume - want) <= 1e-9 * want)) fails.Add($"model: error {lf.Error}, V {p.Volume:R} / {q.Volume:R} vs {want:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}

