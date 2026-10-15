using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_SHELLOPENBLEND=0: открытая грань у сопряжения — отказ (как прежде).</summary>
    static readonly bool ShellOpenBlendOff = Environment.GetEnvironmentVariable("P3D_SHELLOPENBLEND") == "0";
    public static long ShellOpenLips;

    /// <summary>
    /// KRN-07 (07.5): ОБОЛОЧКА С ОТКРЫТОЙ ГРАНЬЮ У СОПРЯЖЕНИЙ — прямой сборкой, без булевых. Открытая плоская грань F касается
    /// соседей по всем своим рёбрам (скругления). Тело, смещённое внутрь на t (все грани, F тоже), даёт внутренние грани; у
    /// касания нормали F и соседа на ребре совпадают, поэтому образ ребра на смещённом теле — то же ребро, сдвинутое на −t·n
    /// (n — нормаль F). Результат: наружные грани без F, внутренние без образа F (обращённые), и по каждому ребру F —
    /// «кромка»: полоса между ребром и его образом (над отрезком — плоскость, над дугой — цилиндр с осью вдоль n). Сборка и
    /// проверки — как у прямых сопряжений (годность, аналитический объём, объём по фасетам). null — отказ (причина в why).
    /// </summary>
    internal static TSolid? ShellOpenAtBlends(TSolid body, IReadOnlyCollection<TFace> open, double t, ref string? why)
    {
        if (ShellOpenBlendOff) { why = "open face at blends is off"; return null; }
        if (open.Count == 0 || !(t > 0)) { why = "no open face"; return null; }
        // открытые грани — плоские, касаются соседей по всем рёбрам (иначе — прежние пути)
        var nOf = new Dictionary<TFace, Vector3d>();
        foreach (var F in open)
        {
            var src = GeometryAudit.AuthoritativeSurface(F);
            if (src == null || !src.IsPlanar) { why = "an open face is not planar"; return null; }
            var p0 = F.Loops[0].CoEdges[0].Start.P; var n = F.NormalAt(p0).Normalized(); nOf[F] = n;
            foreach (var ce in F.Loops.SelectMany(l => l.CoEdges))
            {
                var e = ce.Edge; var G = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().FirstOrDefault(x => x != F);
                if (G == null || open.Contains(G)) { why = "an open face borders another open face"; return null; }
                if (e.Curve is not (LineCurve3 or ArcCurve3)) { why = "an open face edge is neither a line nor an arc"; return null; }
                for (int k = 0; k <= 4; k++)
                {
                    var x = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 4.0);
                    if ((G.NormalAt(x).Normalized() - n).Length > 1e-9) { why = "an open face meets a neighbour at a sharp edge (not at a blend)"; return null; }
                }
            }
        }
        var inner = OffsetSolid(body, f => -t, "shell:inner", ref why);
        if (inner == null) return null;
        var outerD = Describe(body, out var byOuter, ref why); if (outerD == null) return null;
        var innerD = Describe(inner, out var byInner, ref why); if (innerD == null) return null;
        var faces = new List<FDesc>();
        foreach (var d in outerD) if (!open.Contains(d.Orig!)) faces.Add(d);
        // образы открытых граней на смещённом теле — плоскость F, сдвинутая на −t·n, нормаль та же
        var drop = new HashSet<FDesc>();
        foreach (var F in open)
        {
            var n = nOf[F]; var target = F.Loops[0].CoEdges[0].Start.P - n * t;
            var g = innerD.FirstOrDefault(d => d.Surf.IsPlanar && d.N.Length > 0.5 && Vector3d.Dot(d.N.Normalized(), n) > 1 - 1e-12
                                               && Math.Abs(Vector3d.Dot(d.Loops[0][0].A - target, n)) < 1e-9);
            if (g == null) { why = "the offset of an open face is not found"; return null; }
            drop.Add(g);
        }
        foreach (var d in innerD)
        {
            if (drop.Contains(d)) continue;
            // внутренняя грань: сторона обращена, контуры — навстречу; грань новая
            var r = new FDesc { Surf = d.Surf, Rev = !d.Rev, Tag = "shell:inner" + (d.Tag.Length > 0 ? ":" + EdgeRef.Bare(d.Tag) : ""), Hint = d.Hint, Slot = d.Slot, Touched = true,
                                N = -d.N, IsCyl = d.IsCyl, AxO = d.AxO, AxW = d.AxW, AxR = d.AxR };
            foreach (var l in d.Loops) r.Loops.Add(l.AsEnumerable().Reverse().Select(s => new Seg { Curve = s.Curve, A = s.B, B = s.A }).ToList());
            faces.Add(r);
        }
        // кромки: по каждому ребру открытой грани — полоса до его образа (−t·n)
        foreach (var F in open)
        {
            var n = nOf[F]; var dF = byOuter[F];
            foreach (var loop in dF.Loops)
                foreach (var s in loop)
                {
                    var A = s.A; var B = s.B; var A1 = A - n * t; var B1 = B - n * t;
                    ICurve3 img; ISurface3 surf; Vector3d hint;
                    if (s.Curve is LineCurve3)
                    {
                        img = new LineCurve3(A1, B1);
                        var nl = Vector3d.Cross(n, B - A).Normalized();
                        surf = new PlaneSurface(Frame3.FromNormal(A, nl));
                        hint = (A + B) / 2 - n * (t / 2);
                    }
                    else
                    {
                        var a = (ArcCurve3)s.Curve;
                        img = new ArcCurve3(new Frame3(a.Frame.Origin - n * t, a.Frame.U, a.Frame.V), a.Radius, a.A0, a.A1);
                        var ax = a.Frame.W.Normalized();
                        if (Math.Abs(Math.Abs(Vector3d.Dot(ax, n)) - 1) > 1e-12) { why = "an open face arc is not across the face normal"; return null; }
                        surf = new CylinderSurface(new Frame3(a.Frame.Origin - n * t, a.Frame.U.Normalized(), Vector3d.Cross(n, a.Frame.U.Normalized()).Normalized()), a.Radius);
                        hint = a.PointAt((a.T0 + a.T1) / 2) - n * (t / 2);
                    }
                    var lip = new FDesc { Surf = surf, Rev = false, Tag = "shell:lip", Touched = true, Hint = hint };
                    // сторона кромки: наружу — в проём (внутрь F от ребра)
                    var mid = s.Curve.PointAt((s.Curve.T0 + s.Curve.T1) / 2);
                    var nm = surf.NormalAt(surf.ProjectToUV(hint)).Normalized();
                    var into = IntoPlanarFace(dF, mid, nm);
                    if (into == null) { why = "lip side undecided"; return null; }
                    lip.Rev = Vector3d.Dot(nm, into.Value) < 0;
                    if (surf is PlaneSurface) lip.N = nm * (lip.Rev ? -1 : 1);
                    lip.Loops.Add(new List<Seg>
                    {
                        new() { Curve = s.Curve, A = A, B = B },
                        new() { Curve = new LineCurve3(B, B1), A = B, B = B1 },
                        new() { Curve = img, A = B1, B = A1 },
                        new() { Curve = new LineCurve3(A1, A), A = A1, B = A },
                    });
                    faces.Add(lip);
                }
        }
        var res = FinishDirect(faces, new List<FDesc>(), "shell.open", ref why);
        if (res != null) Interlocked.Increment(ref ShellOpenLips);
        return res;
    }
}
