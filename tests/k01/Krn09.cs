using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;

/// <summary>KRN-09 (09.6): сериализация — недопустимые значения и пределы ресурсов.</summary>
static partial class Program
{
    static void Krn09Checks()
    {
        Check("KRN09-THIN-ELEMENTS-AND-WATERTIGHT-DISPLAY", () =>
        {
            // m.p3: тонкие элементы — плита, стенка, стенка после выреза, тонкостенная труба, отверстие у края — при
            // t = 0.1…1e-4 мм: объём точно, тело и сетка показа годны. Найдено: сетка выдавливания профиля с гранями разной
            // длины (стенка 100×t, «стадион» из отрезков и дуг) была незамкнутой — деления по высоте у соседних граней не
            // совпадали (P3D_DISPLAYONEROW).
            Frame3 XYat(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
            var fails = new List<string>();
            void Want(string name, Part p, double v)
            {
                var errs = string.Join(" | ", p.Features.Where(f => f.Error != null).Select(f => f.Error));
                if (errs.Length > 0 || p.Integrity != null || p.Bodies.Count != 1) fails.Add($"{name}: {errs} {p.Integrity} bodies {p.Bodies.Count}");
                else if (Math.Abs(p.Volume - v) > 1e-9 * v) fails.Add($"{name}: V={p.Volume:R} vs {v:R}");
            }
            ExtrudeFeature Ex(Part p, Parametric3D.Model.Sketching.Sketch s, double h, bool cut = false)
            { var e = new ExtrudeFeature("E" + p.Features.Count) { SketchId = s.Id, Cut = cut }; e.Side1.End = EndType.Distance; e.Side1.Distance = new ValueRef(h); e.Side1.Flip = cut; p.AddFeature(e); return e; }
            {
                var p = new Part("stadium"); var s = new Parametric3D.Model.Sketching.Sketch("s", XYat(0)); p.Add(s);
                var a = new Parametric3D.Model.Sketching.SkPoint(-20, -5); var b = new Parametric3D.Model.Sketching.SkPoint(20, -5);
                var c = new Parametric3D.Model.Sketching.SkPoint(20, 5); var d = new Parametric3D.Model.Sketching.SkPoint(-20, 5);
                s.AddEntity(new Parametric3D.Model.Sketching.SkLine(a, b)); s.AddEntity(new Parametric3D.Model.Sketching.SkArc(new Parametric3D.Model.Sketching.SkPoint(20, 0), b, c) { CCW = true });
                s.AddEntity(new Parametric3D.Model.Sketching.SkLine(c, d)); s.AddEntity(new Parametric3D.Model.Sketching.SkArc(new Parametric3D.Model.Sketching.SkPoint(-20, 0), d, a) { CCW = true });
                Ex(p, s, 30); p.Rebuild(null);
                Want("stadium", p, 40 * 10 * 30 + Math.PI * 25 * 30);
            }
            foreach (double t in new[] { 0.1, 0.01, 0.001, 1e-4 })
            {
                var p = new Part("plate"); Ex(p, PolySketch(p, XYat(0), (0, 0), (100, 0), (100, 100), (0, 100)), t); p.Rebuild(null); Want($"plate {t}", p, 1e4 * t);
                var q = new Part("wall"); Ex(q, PolySketch(q, XYat(0), (0, 0), (100, 0), (100, t), (0, t)), 50); q.Rebuild(null); Want($"wall {t}", q, 100 * t * 50);
                var r = new Part("pocket"); Ex(r, PolySketch(r, XYat(0), (0, 0), (20, 0), (20, 20), (0, 20)), 20);
                Ex(r, PolySketch(r, XYat(20), (t, -5), (25, -5), (25, 25), (t, 25)), 10, cut: true); r.Rebuild(null); Want($"wall {t} left by a cut", r, 8000 - (20 - t) * 20 * 10);
                var u = new Part("tube"); var s5 = CircleSketch(u, 0, 0, 0, 10); s5.AddEntity(new Parametric3D.Model.Sketching.SkCircle(new Parametric3D.Model.Sketching.SkPoint(0, 0), 10 - t));
                Ex(u, s5, 30); u.Rebuild(null); Want($"tube wall {t}", u, Math.PI * (100 - (10 - t) * (10 - t)) * 30);
                var h = new Part("edgehole"); Ex(h, PolySketch(h, XYat(0), (0, 0), (40, 0), (40, 40), (0, 40)), 10);
                var hf = new HoleFeature("H") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new ValueRef(10) }; hf.Points.Add(new Vector3d(5 + t, 20, 10)); h.AddFeature(hf); h.Rebuild(null);
                Want($"hole {t} from the edge", h, 16000 - Math.PI * 25 * 10);
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN09-LOAD-INVALID-VALUES-AND-LIMITS", () =>
        {
            var fails = new List<string>();
            var p = new Part("ser"); TestGeometry.AddCube(p, 60);
            p.AddFeature(new LinearPatternFeature("P") { Step = new Vector3d(100, 0, 0), Count = new ValueRef(2) });
            p.Rebuild(null);
            string json = DocumentFile.Save(p);
            if (!json.Contains("\"Distance\": 60,") || !json.Contains("\"Distance\": 2,")) fails.Add("probe document shape changed");
            void MustRefuse(string what, string text)
            {
                try { DocumentFile.Load(text); fails.Add($"{what}: accepted"); }
                catch (InvalidDataException) { }
                catch (Exception e) { fails.Add($"{what}: {e.GetType().Name} instead of InvalidDataException ({e.Message})"); }
            }
            MustRefuse("sketch plane of 3 numbers", System.Text.RegularExpressions.Regex.Replace(json, "\"Plane\": \\[[^\\]]*\\]", "\"Plane\": [0, 0, 0]"));
            MustRefuse("empty sketch plane", System.Text.RegularExpressions.Regex.Replace(json, "\"Plane\": \\[[^\\]]*\\]", "\"Plane\": []"));
            MustRefuse("collinear sketch plane axes", System.Text.RegularExpressions.Regex.Replace(json, "\"Plane\": \\[[^\\]]*\\]", "\"Plane\": [0,0,0, 1,0,0, 1,0,0]"));
            MustRefuse("NaN as a string", json.Replace("\"Distance\": 60,", "\"Distance\": \"NaN\","));
            string? BuildError(string what, string text, out double seconds)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); seconds = 0;
                try
                {
                    var (q, t) = DocumentFile.Load(text);
                    var r = q.Compute(t, null, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token); q.Apply(r);
                    seconds = sw.Elapsed.TotalSeconds;
                    return string.Join(" | ", q.Features.Where(f => f.Error != null).Select(f => f.Error));
                }
                catch (Exception e) { seconds = sw.Elapsed.TotalSeconds; fails.Add($"{what}: {e.GetType().Name}: {e.Message}"); return null; }
            }
            var e1 = BuildError("pattern count 1e9", json.Replace("\"Distance\": 2,", "\"Distance\": 1000000000,"), out double s1);
            if (e1 != null && (!e1.Contains("exceeds the limit") || s1 > 10)) fails.Add($"pattern count 1e9: error «{e1}», {s1:0.#} s");
            var e2 = BuildError("cube 1e300", json.Replace("\"Distance\": 60,", "\"Distance\": 1e300,"), out _);
            if (e2 != null && !e2.Contains("outside the supported model range")) fails.Add($"cube 1e300: error «{e2}»");
            var e3 = BuildError("cube 1e5 (inside the range)", json.Replace("\"Distance\": 60,", "\"Distance\": 100000,"), out _);
            if (e3 != null && e3.Length > 0) fails.Add($"cube 1e5 must build: «{e3}»");
            {   // отмена доходит до цикла копий: массив из 9000 копий отменяется за секунды
                var (q, t) = DocumentFile.Load(json.Replace("\"Distance\": 2,", "\"Distance\": 9000,"));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { q.Compute(t, null, new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token); fails.Add("9000 copies: not cancelled"); }
                catch (OperationCanceledException) { if (sw.Elapsed.TotalSeconds > 5) fails.Add($"9000 copies: cancelled only after {sw.Elapsed.TotalSeconds:0.#} s"); }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
