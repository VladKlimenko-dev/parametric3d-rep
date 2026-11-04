using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>k158: разбор файла: тело из файла и тело полного перестроения — объём, проверка, сетка в STL (ASCII) для просмотра.</summary>
    static void InspectFile(string file, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        void Dump(string tag)
        {
            int i = 0;
            foreach (var b in p.Bodies)
            {
                var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
                var rep = SolidValidator.ValidateBRep(b);
                Console.WriteLine($"{tag} body {i}: g {b.Guarantee}, faces {b.Faces.Count()}, tris {m.Indices.Length / 3}, broken {m.BrokenFaces.Count}, valid {rep.IsValid}, issues {string.Join(" | ", rep.Issues.Take(6))}");
                using var w = new StreamWriter(Path.Combine(outDir, $"{tag}_{i}.stl"));
                w.WriteLine("solid s");
                for (int t = 0; t < m.Indices.Length; t += 3)
                {
                    w.WriteLine("facet normal 0 0 0\nouter loop");
                    for (int k = 0; k < 3; k++) { var v = m.Vertices[m.Indices[t + k]].P; w.WriteLine(FormattableString.Invariant($"vertex {v.X} {v.Y} {v.Z}")); }
                    w.WriteLine("endloop\nendfacet");
                }
                w.WriteLine("endsolid s");
                i++;
            }
        }
        Console.WriteLine($"restored {p.RestoredFromFile}, volume {p.Volume:0.###}");
        if (p.RestoredFromFile) Dump("file");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        p.Rebuild(pars);
        Console.WriteLine($"rebuild {sw.ElapsedMilliseconds} ms, volume {p.Volume:0.###}");
        foreach (var f in p.Features.Where(f => f.Error != null)) Console.WriteLine($"ERROR {f.Name}: {f.Error}");
        foreach (var w in p.Warnings) Console.WriteLine($"warn {w}");
        Dump("rebuilt");
    }
}
