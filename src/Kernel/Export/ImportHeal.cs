using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// KRN-09 (09.5): ЛЕЧЕНИЕ ИМПОРТА В БЮДЖЕТЕ С ОТЧЁТОМ. Прочитанное тело приводится к соглашению о полурёбрах (иначе у него
/// нет точного объёма), затем:
/// <list type="number">
/// <item>вершина, отстоящая от поверхностей своих граней (точность записи другого CAD), — у плоских граней переносится в
/// пересечение трёх их плоскостей, рёбра-отрезки у неё перестраиваются; зазор больше бюджета — не лечится;</item>
/// <item>дыра в оболочке (контур свободных рёбер), лежащая в одной плоскости в пределах бюджета, — закрывается плоской
/// гранью «import:filled»;</item>
/// <item>итог проверяется (B-rep); негодное тело остаётся как прочитано и сообщается.</item>
/// </list>
/// Каждое действие и каждый отказ — строка отчёта; геометрия меняется только в пределах бюджета ToleranceContext.ImportHeal.
/// </summary>
public static class ImportHeal
{
    /// <summary>Мутация P3D_IMPORTHEAL=0: тело — как прочитано, без лечения и отчёта (прежде).</summary>
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_IMPORTHEAL") == "0";

    public sealed record Report(double MaxVertexGap, int Snapped, int Filled, int OpenEdgesLeft, bool Valid, string? Problem)
    {
        public override string ToString() =>
            $"max vertex gap {MaxVertexGap:G3} mm, {Snapped} vertex(es) snapped, {Filled} hole(s) filled, {OpenEdgesLeft} open edge(s) left, " + (Valid ? "valid" : "NOT valid: " + Problem);
    }

    static Geometry.Ssi.Plane? PlaneOf(TFace f) => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) as Geometry.Ssi.Plane;

    static double Gap(TFace f, Vector3d p)
    {
        var sh = Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
        switch (sh)
        {
            case Geometry.Ssi.Plane pl: return Math.Abs(Vector3d.Dot(p - pl.P, pl.N.Normalized()));
            case Geometry.Ssi.Cylinder cy: { var w = cy.Axis.Normalized(); var d = p - cy.P; return Math.Abs((d - w * Vector3d.Dot(d, w)).Length - cy.R); }
            case Geometry.Ssi.Sphere sp: return Math.Abs((p - sp.C).Length - sp.R);
        }
        var src = GeometryAudit.AuthoritativeSurface(f);
        if (src == null) return 0;
        try { return (src.PointAt(src.ProjectToUV(p)) - p).Length; } catch { return 0; }
    }

    public static Report Heal(TSolid b, List<string>? warnings, string label)
    {
        double budget = ToleranceContext.Current.ImportHeal;
        if (b.TopologyConvention < TopologyConventions.Version) TopologyConventions.Normalize(b);
        bool changed = false;
        // 1. зазоры вершин
        var facesOf = new Dictionary<TVertex, HashSet<TFace>>(ReferenceEqualityComparer.Instance);
        foreach (var f in b.Faces)
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
                foreach (var v in new[] { ce.Start, ce.End })
                {
                    if (!facesOf.TryGetValue(v, out var s)) facesOf[v] = s = new HashSet<TFace>(ReferenceEqualityComparer.Instance);
                    s.Add(f);
                }
        double maxGap = 0; int snapped = 0; string? problem = null;
        var touched = new HashSet<TFace>(ReferenceEqualityComparer.Instance);
        foreach (var (v, fs) in facesOf)
        {
            double g = fs.Max(f => Gap(f, v.P));
            maxGap = Math.Max(maxGap, g);
            if (g <= 1e-9) continue;
            if (g > budget) { problem ??= $"a vertex is {g:G3} mm off its faces (budget {budget:G3} mm)"; continue; }
            var pls = fs.Select(PlaneOf).ToList();
            var edges = b.Edges.Where(e => ReferenceEquals(e.V0, v) || ReferenceEquals(e.V1, v)).ToList();
            if (pls.Any(x => x == null) || edges.Any(e => e.Curve is not LineCurve3)) { v.Tolerance = Math.Max(v.Tolerance, g); continue; }
            Geometry.Ssi.Plane[]? basis = null;
            for (int x = 0; x < pls.Count && basis == null; x++)
                for (int y = x + 1; y < pls.Count && basis == null; y++)
                    for (int z = y + 1; z < pls.Count && basis == null; z++)
                        if (Math.Abs(Vector3d.Dot(pls[x]!.N.Normalized(), Vector3d.Cross(pls[y]!.N.Normalized(), pls[z]!.N.Normalized()))) > 1e-6) basis = new[] { pls[x]!, pls[y]!, pls[z]! };
            if (basis == null) { v.Tolerance = Math.Max(v.Tolerance, g); continue; }
            var n1 = basis[0].N.Normalized(); var n2 = basis[1].N.Normalized(); var n3 = basis[2].N.Normalized();
            double d1 = Vector3d.Dot(n1, basis[0].P), d2 = Vector3d.Dot(n2, basis[1].P), d3 = Vector3d.Dot(n3, basis[2].P);
            var x0 = (Vector3d.Cross(n2, n3) * d1 + Vector3d.Cross(n3, n1) * d2 + Vector3d.Cross(n1, n2) * d3) / Vector3d.Dot(n1, Vector3d.Cross(n2, n3));
            if ((x0 - v.P).Length > budget || pls.Any(pl => Math.Abs(Vector3d.Dot(x0 - pl!.P, pl.N.Normalized())) > 1e-9 * Math.Max(1, x0.Length))) { v.Tolerance = Math.Max(v.Tolerance, g); continue; }
            v.P = x0; snapped++; changed = true; touched.UnionWith(fs);
            foreach (var e in edges) { e.Curve = new LineCurve3(e.V0.P, e.V1.P); e.PolylineCache = null; }
        }
        // 2. дыры: контуры свободных рёбер
        int filled = 0;
        var free = b.Edges.Where(e => e.CoEdges.Count == 1).ToList();
        var byStart = new Dictionary<TVertex, List<TCoEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in free) { var c = e.CoEdges[0]; if (!byStart.TryGetValue(c.End, out var l)) byStart[c.End] = l = new(); l.Add(c); }
        var usedC = new HashSet<TCoEdge>(ReferenceEqualityComparer.Instance);
        foreach (var e in free)
        {
            var c0 = e.CoEdges[0]; if (usedC.Contains(c0)) continue;
            var chain = new List<TCoEdge>(); var c = c0; bool closed = false;
            for (int guard = 0; guard < 10000; guard++)
            {
                chain.Add(c); usedC.Add(c);
                // новая грань обходит ребро наоборот: от c.End к c.Start; следующий — свободный, начинающийся (в новом обходе) в c.Start
                if (!byStart.TryGetValue(c.Start, out var nx) || nx.Count != 1) break;
                c = nx[0];
                if (ReferenceEquals(c, c0)) { closed = true; break; }
                if (usedC.Contains(c)) break;
            }
            if (!closed) { problem ??= "open edges do not form a closed loop"; continue; }
            var pts = new List<Vector3d>();
            foreach (var ce in Enumerable.Reverse(chain)) { var t = new List<Vector3d>(); ce.Edge.Curve.Tessellate(t, true); if (!ce.Reversed) t.Reverse(); pts.AddRange(t); }
            Vector3d area = Vector3d.Zero; var cen = pts.Aggregate(Vector3d.Zero, (s, q) => s + q) / pts.Count;
            for (int i = 0; i < pts.Count; i++) area += Vector3d.Cross(pts[i] - cen, pts[(i + 1) % pts.Count] - cen);
            if (area.LengthSquared < 1e-24) { problem ??= "a hole loop has no area"; continue; }
            var n = area.Normalized();
            if (pts.Any(q => Math.Abs(Vector3d.Dot(q - cen, n)) > budget)) { problem ??= "a hole loop is not planar within the budget"; continue; }
            // контур — отверстие в грани той же плоскости (выпала стенка отверстия, а не плоская грань): закрывать нечем
            if (chain.Any(ce => ce.Loop?.Face is { } nf && PlaneOf(nf) is { } npl && Math.Abs(Math.Abs(Vector3d.Dot(npl.N.Normalized(), n)) - 1) < 1e-9 && Math.Abs(Vector3d.Dot(cen - npl.P, npl.N.Normalized())) <= budget))
            { problem ??= "an opening lies in the plane of its neighbour face (a missing non-planar face)"; continue; }
            var face = new TFace { Surface = new PlaneSurface(Frame3.FromNormal(cen, n)), Reversed = false, Tag = "import:filled" };
            var loop = face.AddLoop(true);
            foreach (var ce in Enumerable.Reverse(chain)) loop.Add(ce.Edge, !ce.Reversed);
            b.Shells[0].Add(face);
            filled++; changed = true; touched.Add(face);
        }
        int openLeft = b.Edges.Count(e => e.CoEdges.Count == 1);
        if (changed)
        {
            foreach (var f in touched) { f.InteriorHint = null; foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) { ce.PCurve = null; ce.PCurveDeviation = 0; } }
            b.DisplayMeshKey = null;
            TopologyConventions.Normalize(b);
            // разметка — только у тронутых граней (у прочих — прежняя сетка как донор)
            StepImport.MeshFacesFromLoops(b, null, "Heal", f => touched.Contains(f) ? null : f.Surface as FacetedSurface);
        }
        var vr = SolidValidator.ValidateBRep(b);
        bool overBudget = maxGap > budget;
        bool valid = vr.IsValid && openLeft == 0 && !overBudget && problem == null;
        if (!valid) problem ??= openLeft > 0 ? $"{openLeft} open edge(s)" : vr.Summary();
        var rep = new Report(maxGap, snapped, filled, openLeft, valid, valid ? null : problem);
        warnings?.Add($"Import: {label}: {rep}.");
        return rep;
    }
}
