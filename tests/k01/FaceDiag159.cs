using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>k159: разбор граней тела файла: тип поверхности, треугольники, «веер» (доля треугольников у одной вершины), сетка граней.</summary>
    static void FaceDiag(string file, string outDir, bool rebuild)
    {
        Directory.CreateDirectory(outDir);
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        if (rebuild || !p.RestoredFromFile) p.Rebuild(pars);
        foreach (var w in p.Warnings) Console.WriteLine("warn " + w);
        Console.WriteLine($"volume {p.Volume:0.###}");
        var b = p.Bodies[0];
        var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
        using var tw = new StreamWriter(Path.Combine(outDir, "tris.txt"));
        foreach (var f in b.Faces)
        {
            if (!m.FaceRanges.TryGetValue(f.Id, out var r)) { Console.WriteLine($"face {f.Id} no mesh"); continue; }
            var cnt = new Dictionary<Vector3, int>();
            int nt = r.IndexCount / 3; double area = 0;
            for (int t = 0; t < r.IndexCount; t += 3)
            {
                var a = m.Vertices[m.Indices[r.IndexStart + t]].P; var bb = m.Vertices[m.Indices[r.IndexStart + t + 1]].P; var c = m.Vertices[m.Indices[r.IndexStart + t + 2]].P;
                area += Vector3.Cross(bb - a, c - a).Length / 2;
                foreach (var q in new[] { a, bb, c }) cnt[q] = cnt.GetValueOrDefault(q) + 1;
                tw.WriteLine(FormattableString.Invariant($"{f.Id} {a.X} {a.Y} {a.Z} {bb.X} {bb.Y} {bb.Z} {c.X} {c.Y} {c.Z}"));
            }
            var top = cnt.OrderByDescending(kv => kv.Value).First();
            Console.WriteLine($"face {f.Id} {f.Surface.GetType().Name} tag '{f.Tag}' src {f.SourceTag} tris {nt} area {area:0.##} fan {(double)top.Value / Math.Max(1, nt):0.00} at ({top.Key.X:0.##},{top.Key.Y:0.##},{top.Key.Z:0.##}) loops {f.Loops.Count}");
        }
    }
}
