using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

static partial class Program
{
    /// <summary>
    /// KRN-10 (10.4): РЕЗЕЦ НЕИЗМЕННОЙ ОПЕРАЦИИ — ИЗ ПАМЯТИ. Детали K08 (3 зерна, 16 операций) и деталь с тонким выдавливанием:
    /// перестроение без контрольных точек (все операции заново) берёт резцы выдавливаний из памяти — результат тот же, что у
    /// свежей детали (объём побитно, грани, имена, вершины); правка размера резца — новый резец; тонкая стенка из памяти —
    /// быстрее построения заново.
    /// </summary>
    static void Krn10ToolReuseChecks()
    {
        Check("KRN10-TOOL-REUSE", () =>
        {
            var fails = new List<string>();
            string Sig(Part p) => string.Join(";", p.Bodies.Select(b => $"{OpVolume(b):R}|{b.Faces.Count()}|{string.Join(",", b.Faces.Select(f => System.Text.RegularExpressions.Regex.Replace(f.Tag, "#[0-9a-f]+", "#")).OrderBy(x => x, StringComparer.Ordinal))}|{string.Join(",", b.Vertices.Select(v => $"{v.P.X:R}/{v.P.Y:R}/{v.P.Z:R}").OrderBy(x => x, StringComparer.Ordinal))}"));
            long r0 = ExtrudeFeature.ToolsReused;
            for (int seed = 1; seed <= 3; seed++)
            {
                var p = RealisticPart(seed, 16, out _); p.Rebuild(null);
                p.Invalidate(); p.Rebuild(null);
                var fresh = RealisticPart(seed, 16, out _); fresh.Rebuild(null);
                if (Sig(p) != Sig(fresh)) { var a = Sig(p); var b = Sig(fresh); int k = 0; while (k < Math.Min(a.Length, b.Length) && a[k] == b[k]) k++; fails.Add($"seed {seed}: rebuild with reused tools differs from a fresh part at …{a.Substring(Math.Max(0, k - 40), Math.Min(120, a.Length - Math.Max(0, k - 40)))} vs …{b.Substring(Math.Max(0, k - 40), Math.Min(120, b.Length - Math.Max(0, k - 40)))}"); }
            }
            long reused = ExtrudeFeature.ToolsReused - r0;
            // тонкое выдавливание: стенка резца из памяти; правка толщины — новый резец
            Part Thin(double t)
            {
                var p = new Part("thin");
                var plate = RectSketch(p, 0, -40, -30, 80, 60);
                var ex = new ExtrudeFeature("Plate") { SketchId = plate.Id }; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
                var c = CircleSketch(p, 10, 0, 0, 12);
                var th = new ExtrudeFeature("Tube") { SketchId = c.Id, Thin = new ValueRef(t) }; th.Side1.Distance = new ValueRef(25); p.AddFeature(th);
                return p;
            }
            var pt = Thin(1.5); var sw = System.Diagnostics.Stopwatch.StartNew(); pt.Rebuild(null); double tFirst = sw.Elapsed.TotalMilliseconds;
            long r1 = ExtrudeFeature.ToolsReused;
            pt.Invalidate(); sw.Restart(); pt.Rebuild(null); double tAgain = sw.Elapsed.TotalMilliseconds;
            if (ExtrudeFeature.ToolsReused - r1 < 1) fails.Add("thin extrude: the tool was not reused");
            if (Sig(pt) != Sig(Thin(1.5).Also(x => x.Rebuild(null)))) fails.Add("thin extrude: reused tool gives another result");
            ((ExtrudeFeature)pt.Features[1]).Thin = new ValueRef(2.0); pt.ParametersChanged(); pt.Invalidate();
            long r2 = ExtrudeFeature.ToolsReused; pt.Rebuild(null);
            var t2 = Thin(2.0); t2.Rebuild(null);
            if (Sig(pt) != Sig(t2)) fails.Add("thin extrude: after the thickness edit the result is not that of thickness 2");
            Log(new { test = "KRN10-TOOL-REUSE", reused, thinFirstMs = Math.Round(tFirst), thinAgainMs = Math.Round(tAgain), reusedAfterEdit = ExtrudeFeature.ToolsReused - r2 });
            if (reused < 10) fails.Add($"only {reused} tools reused on the K08 parts");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
static class AlsoExt { public static T Also<T>(this T x, Action<T> a) { a(x); return x; } }
