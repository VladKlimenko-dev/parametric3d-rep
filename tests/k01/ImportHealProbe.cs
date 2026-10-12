using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static string StepOf(TSolid b) { var sw = new StringWriter(); Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "p", new List<string>()); return sw.ToString(); }

    static List<(string Name, string Text)> ImportHealCases()
    {
        var bl = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(20, 15, -5), 6, 30), new List<string>());
        string t = StepOf(bl[0]);
        var lines = t.Split('\n').ToList();
        var res = new List<(string, string)> { ("as written", t) };
        // B: вершина сдвинута на 2e-5 мм (точность записи другого CAD)
        {
            var l2 = lines.ToList(); int k = l2.FindIndex(l => l.Contains("VERTEX_POINT")); var vp = l2[k];
            string ptRef = System.Text.RegularExpressions.Regex.Match(vp, @"#\d+(?=\s*\)\s*;)").Value;
            int kp = l2.FindIndex(l => l.StartsWith(ptRef + "=") || l.StartsWith(ptRef + " ="));
            l2[kp] = System.Text.RegularExpressions.Regex.Replace(l2[kp], @"\(\s*([-0-9.E+]+)\s*,", m => "(" + (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 2e-5).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",");
            res.Add(("vertex off by 2e-5", string.Join("\n", l2)));
        }
        // C: одна грань с обратным флагом стороны
        {
            var l2 = lines.ToList(); int k = l2.FindIndex(l => l.Contains("ADVANCED_FACE"));
            l2[k] = l2[k].Contains(".T.)") ? l2[k].Replace(".T.)", ".F.)") : l2[k].Replace(".F.)", ".T.)");
            res.Add(("one face sense flipped", string.Join("\n", l2)));
        }
        // D: все грани с обратным флагом (вывернутое тело)
        {
            var l2 = lines.Select(l => l.Contains("ADVANCED_FACE") ? (l.Contains(".T.)") ? l.Replace(".T.)", ".F.)") : l.Replace(".F.)", ".T.)")) : l).ToList();
            res.Add(("all faces flipped", string.Join("\n", l2)));
        }
        // E: грань выброшена из оболочки (открытая оболочка)
        {
            var l2 = lines.ToList(); int k = l2.FindIndex(l => l.Contains("CLOSED_SHELL"));
            l2[k] = System.Text.RegularExpressions.Regex.Replace(l2[k], @"\(\s*#\d+\s*,", "(");
            res.Add(("one face missing", string.Join("\n", l2)));
        }
        // F: вершина сдвинута на 5e-3 мм — больше бюджета
        {
            var l2 = lines.ToList(); int k = l2.FindIndex(l => l.Contains("VERTEX_POINT")); var vp = l2[k];
            string ptRef = System.Text.RegularExpressions.Regex.Match(vp, @"#\d+(?=\s*\)\s*;)").Value;
            int kp = l2.FindIndex(l => l.StartsWith(ptRef + "="));
            l2[kp] = System.Text.RegularExpressions.Regex.Replace(l2[kp], @"\(\s*([-0-9.E+]+)\s*,", m => "(" + (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 5e-3).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",");
            res.Add(("vertex off by 5e-3", string.Join("\n", l2)));
        }
        // G: выброшена цилиндрическая грань (стенка отверстия) — не плоская дыра
        {
            var l2 = lines.ToList(); int k = l2.FindIndex(l => l.Contains("CLOSED_SHELL"));
            int fc = l2.FindIndex(l => l.Contains("ADVANCED_FACE") && l2.Any(x => x.StartsWith(System.Text.RegularExpressions.Regex.Match(l, @",(#\d+),\.[TF]\.\)").Groups[1].Value + "=CYLINDRICAL_SURFACE")));
            string fid = l2[fc].Substring(0, l2[fc].IndexOf('='));
            l2[k] = l2[k].Replace("," + fid + ",", ",").Replace("(" + fid + ",", "(").Replace("," + fid + ")", ")");
            res.Add(("hole wall missing", string.Join("\n", l2)));
        }
        return res;
    }

    static void ImportHealProbe()
    {
        foreach (var (name, text) in ImportHealCases())
        {
            var w = new List<string>();
            var p = Parametric3D.Model.Exchange.Read(text, out _, w);
            string what = p == null ? "null" : string.Join(" | ", p.Bodies.Select(b => $"V {Parametric3D.Kernel.Topology.AnalyticVolume.Of(b)?.ToString("R") ?? "?"} mesh {Parametric3D.Kernel.Tess.MassProps.SignedVolume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b)):R} G {b.Guarantee} faces {b.Faces.Count()} {SolidValidator.Validate(b).Summary()}"));
            Console.WriteLine($"IH {name}: {what}\n   W: {string.Join(" | ", w.Take(4))}");
            if (p != null) foreach (var b in p.Bodies) foreach (var i in SolidValidator.Validate(b).Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3)) Console.WriteLine("   E " + i);
        }
    }
}
