using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Features;

/// <summary>
/// K05. УСТОЙЧИВЫЕ ССЫЛКИ НА ГРАНИ ПО ТОЧКЕ И ИМЕНИ. Оболочка и уклон выбирали грани только по
/// точке: после правки размера (брусок стал выше) точка «на верхней грани» оказывалась в
/// воздухе или на другой грани, и операция молча открывала не ту грань или отказывала. Теперь
/// при первом удачном выборе запоминается устойчивое имя грани («#ключ/роль»), и дальше грань
/// ищется по имени; точка лишь выбирает кусок, если грань разделилась. Нет такой грани — явный
/// конфликт, а не подмена.
/// </summary>
public static class FaceRefs
{
    static readonly bool AmbiguityOff = Environment.GetEnvironmentVariable("P3D_REFAMBIGUITY") == "0";

    /// <summary>
    /// Точки для операции: для каждой — точка на НУЖНОЙ грани. names дополняется именами,
    /// найденными по точкам (при первом выполнении).
    /// </summary>
    public static List<Vector3d> Resolve(TSolid body, IReadOnlyList<Vector3d> points, List<string?> names, string what)
    {
        while (names.Count < points.Count) names.Add(null);
        var res = new List<Vector3d>();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (names[i] is { } name)
            {
                var candidates = body.Faces.Where(f => f.Tag == name).ToList();
                if (candidates.Count == 0)
                    throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"{what}: the referenced face '{EdgeRef.Bare(name)}' no longer exists.");
                // Точка уже на одной из граней с этим именем — она и остаётся.
                var ranked = candidates.Select(f => (F: f, D: Distance(f, p))).OrderBy(x => x.D).ToList();
                var on = ranked.Where(x => x.D <= 1e-3).ToList();
                if (on.Count == 1 || (on.Count > 1 && AmbiguityOff)) { res.Add(p); continue; }
                if (candidates.Count == 1 || AmbiguityOff) { res.Add(Inside(ranked[0].F)); continue; }
                // KRN-08: грань разделилась, а точка не на одном куске (ни на одном или на стыке кусков) — выбор не
                // делается за пользователя (прежде — ближайший кусок). Мутация P3D_REFAMBIGUITY=0.
                throw new AmbiguousReferenceException(what, ranked.Select(x => (x.F.Tag, Inside(x.F))).ToList(),
                    $"the face '{EdgeRef.Bare(name)}' is split into {candidates.Count} pieces and the stored point lies on {(on.Count == 0 ? "none" : on.Count.ToString())} of them");
            }
            else
            {
                var ranked = body.Faces.Select(x => (F: x, D: Distance(x, p))).OrderBy(x => x.D).ToList();
                var (f, d) = ranked[0];
                // первая привязка: точка на стыке граней с разными именами — неоднозначно
                if (!AmbiguityOff && d <= 1e-3 && ranked.Count > 1 && ranked[1].D <= d + 1e-6 && ranked[1].F.Tag != f.Tag)
                    throw new AmbiguousReferenceException(what, ranked.TakeWhile(x => x.D <= d + 1e-6).Select(x => (x.F.Tag, Inside(x.F))).ToList(),
                        "the point lies on the boundary of several faces");
                if (d <= 1e-3 && !string.IsNullOrEmpty(f.Tag)) names[i] = f.Tag;
                res.Add(p);
            }
        }
        return res;
    }

    private static List<(Vector3d A, Vector3d B, Vector3d C)> Tris(TFace f)
    {
        if (f.Surface is Kernel.Surfaces.FacetedSurface fs && fs.Facets.Count > 0) return fs.Facets.ToList();
        var mb = new Kernel.Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb);
        var md = new Kernel.Tess.MeshData(); mb.FillInto(md);
        var list = new List<(Vector3d, Vector3d, Vector3d)>();
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
            list.Add((mb.PositionsD[md.Indices[i]], mb.PositionsD[md.Indices[i + 1]], mb.PositionsD[md.Indices[i + 2]]));
        return list;
    }

    internal static double Distance(TFace f, Vector3d p)
    {
        double best = double.MaxValue;
        foreach (var (a, b, c) in Tris(f)) best = Math.Min(best, (Closest(p, a, b, c) - p).Length);
        return best;
    }

    /// <summary>Точка внутри грани: центр её наибольшего треугольника.</summary>
    internal static Vector3d Inside(TFace f)
    {
        var t = Tris(f).OrderByDescending(x => Vector3d.Cross(x.B - x.A, x.C - x.A).LengthSquared).First();
        return (t.A + t.B + t.C) / 3;
    }

    private static Vector3d Closest(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d ab = b - a, ac = c - a, ap = p - a;
        double d1 = Vector3d.Dot(ab, ap), d2 = Vector3d.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3d bp = p - b; double d3 = Vector3d.Dot(ab, bp), d4 = Vector3d.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3d cp = p - c; double d5 = Vector3d.Dot(ab, cp), d6 = Vector3d.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        double denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
