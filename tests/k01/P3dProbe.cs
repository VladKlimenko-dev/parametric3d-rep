using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>Разбор одного файла: тело до последней операции и после — объём (точный и по сетке), гарантия, годность,
    /// предупреждения последней операции. Режим «p3dprobe файл».</summary>
    static void P3dProbe(string file)
    {
        string text = File.ReadAllText(file);
        var (p0, pars0) = DocumentFile.Load(text);
        var last = p0.Features[^1];
        p0.RemoveFeature(last);
        p0.Rebuild(pars0);
        static double MeshVol(TSolid t) => Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(t));
        object Desc(Part p) => p.Bodies.Select(b => new { g = b.Guarantee.ToString(), valid = SolidValidator.Validate(b).IsValid, exact = AnalyticVolume.Of(b), mesh = MeshVol(b), faces = b.Faces.Count() }).ToList();
        Log(new { stage = "before", bodies = Desc(p0) });
        var (p, pars) = DocumentFile.Load(text);
        // k150: отладка прямого построителя на последней операции (PROBE_DR=<подстрока>)
        if (Environment.GetEnvironmentVariable("PROBE_DR") is { } pdr) Parametric3D.Kernel.Ops.DirectRound.Debug = s => { if (s.Contains(pdr)) Console.WriteLine("DBG " + s); };
        // k152: отказы основания перпендикуляра (PROBE_FOOT=1), не больше 40 строк
        int footN = 0; if (Environment.GetEnvironmentVariable("PROBE_FOOT") == "1") Parametric3D.Kernel.Geometry.Ssi.FootDebug = s => { if (footN++ < 40) Console.WriteLine("FOOT " + s); };
        p.Rebuild(pars);
        Parametric3D.Kernel.Ops.DirectRound.Debug = null;
        var f = p.Features[^1];
        Log(new { stage = "after", feature = f.Name, error = f.Error, errors = string.Join("; ", p.Features.Where(x => x.Error != null).Select(x => x.Name + ": " + x.Error)), warnings = p.Warnings, bodies = Desc(p) });
        if (Environment.GetEnvironmentVariable("PROBE_FACES") == "1") { FacesOf(p0, "before"); FacesOf(p, "after"); }
    }
    static void FacesOf(Part p, string stage)
    {
            foreach (var b in p.Bodies)
            {
                var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
                foreach (var fc in b.Faces)
                {
                    if (!md.FaceRanges.TryGetValue((uint)fc.Id, out var fr)) { Log(new { stage, face = fc.Id, fc.Tag, mesh = "none" }); continue; }
                    double am = 0, vm = 0; var va = OpenTK.Mathematics.Vector3d.Zero;
                    var org = (b.Bounds.Min + b.Bounds.Max) / 2;
                    for (int i = fr.IndexStart; i + 2 < fr.IndexStart + fr.IndexCount; i += 3)
                    {
                        var a = md.Vertices[md.Indices[i]].P; var bb = md.Vertices[md.Indices[i + 1]].P; var c = md.Vertices[md.Indices[i + 2]].P;
                        var A = new OpenTK.Mathematics.Vector3d(a.X, a.Y, a.Z); var B = new OpenTK.Mathematics.Vector3d(bb.X, bb.Y, bb.Z); var C = new OpenTK.Mathematics.Vector3d(c.X, c.Y, c.Z);
                        var cr = OpenTK.Mathematics.Vector3d.Cross(B - A, C - A); am += cr.Length / 2;
                        vm += OpenTK.Mathematics.Vector3d.Dot(A - org, OpenTK.Mathematics.Vector3d.Cross(B - org, C - org)) / 6;
                    }
                    double? ae = AnalyticVolume.FaceAreaRough(fc);
                    double? ve = AnalyticVolume.FaceVolumeAt(fc, org);
                    if (Environment.GetEnvironmentVariable("PROBE_LOOPS") is { } pl && fc.Tag.EndsWith(pl))
                        foreach (var lp in fc.Loops)
                            Log(new { stage, face = fc.Id, rev = fc.Reversed, loop = string.Join(" | ", lp.CoEdges.Select(ce => $"e{ce.Edge.Id} {ce.Edge.Curve.GetType().Name} {(ce.Reversed ? "R" : "F")} {ce.Start.P:0.###}->{ce.End.P:0.###}")) });
                    Log(new { stage, face = fc.Id, fc.Tag, surf = GeometryAudit.AuthoritativeSurface(fc)?.GetType().Name, meshArea = Math.Round(am, 4), exactArea = ae is { } x ? Math.Round(Math.Abs(x), 4) : (double?)null, meshVol = Math.Round(vm, 3), exactVol = ve is { } y ? Math.Round(y, 3) : (double?)null, tris = fr.IndexCount / 3 });
                }
            }
    }
}
