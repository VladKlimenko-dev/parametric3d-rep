using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>k153: ДИАГНОСТИКА — грани тела перед последней операцией файла: метка, площадь, длины рёбер (оценка ширины полосы).</summary>
    static void OvfDiag(string file, string tag)
    {
        var (p, pars) = DocumentFile.Load(File.ReadAllText(file));
        var last = p.Features[^1];
        p.RemoveFeature(last);
        p.Rebuild(pars);
        foreach (var f in p.Bodies.Single().Faces)
        {
            if (!f.Tag.Contains(tag)) continue;
            double a = AnalyticVolume.FaceArea(f) ?? -1;
            var lens = f.Edges.Where(e => e.Curve != null).Select(e => e.Curve!.Length).ToList();
            double per = lens.Sum();
            Console.WriteLine($"{f.Tag} {f.Surface?.GetType().Name} area {a:0.####} perim {per:0.###} width~{2 * a / Math.Max(per, 1e-9):0.####} edges [{string.Join(", ", lens.Select(x => x.ToString("0.###")))}]");
        }
    }
}
