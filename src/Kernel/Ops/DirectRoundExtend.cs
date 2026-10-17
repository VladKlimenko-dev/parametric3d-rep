using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// u12b: ТЕЛО, ПРОДЛЁННОЕ СКВОЗЬ ОТКРЫТУЮ ГРАНЬ — для полости оболочки, когда открытая плоская грань F касается одних соседей
/// (скругления) и встречает других под углом. Сдвинуть F наружу той же топологией нельзя (касательное скругление остаётся
/// на месте, точки на его продолжении нет), а «все рёбра — касания» (кромки) здесь не годится. Вместо F — боковины до крышки
/// на высоте h над F: у острого ребра — продолжение поверхности соседа (плоскость, цилиндр с осью вдоль n), у касания —
/// стенка вдоль n над ребром (плоскость над отрезком, цилиндр над дугой). Смещение такого тела внутрь на t — полость:
/// у острых рёбер её стенка — продолжение смещённого соседа, у касаний — стенка в t от ребра (обод шириной t в плоскости F).
/// Мутация P3D_SHELLEXTEND=0.
/// </summary>
public static partial class DirectRound
{
    /// <summary>Полуребро контура с заданным направлением (начало, конец).</summary>
    sealed record OCE(TEdge Edge, TVertex Start, TVertex End);
    static readonly bool ExtendOff = Environment.GetEnvironmentVariable("P3D_SHELLEXTEND") == "0";
    public static int ExtendedThroughOpen;

    /// <summary>
    /// Продление на высоту h; боковины, сходящиеся над узкой гранью (фаска: продолжения соседей встречаются в прежнем углу),
    /// крышку выворачивают — тогда высота уменьшается (шагами ×0.85, до hMin).
    /// </summary>
    internal static TSolid? ExtendThroughOpen(TSolid body, IReadOnlyCollection<TFace> open, double h, ref string? why, double hMin = 0)
    {
        for (double hh = h; ; hh *= 0.85)
        {
            string? w = null;
            var r = ExtendThroughOpenAt(body, open, Math.Max(hh, hMin), ref w);
            if (r != null) return r;
            why = w;
            if (!(hh > hMin) || hMin <= 0 || w == null || w.StartsWith("extend: a sharp") || w.StartsWith("extend: an open face")) return null;
        }
    }

    static TSolid? ExtendThroughOpenAt(TSolid body, IReadOnlyCollection<TFace> open, double h, ref string? why)
    {
        if (ExtendOff) { why = "extension through the open face is off"; return null; }
        double sc = 1; foreach (var v in body.Vertices) sc = Math.Max(sc, v.P.Length);
        double tol = 1e-9 * sc;
        ISurface3 S(TFace f) => GeometryAudit.AuthoritativeSurface(f)!;
        var faces = new List<FDesc>();
        foreach (var f in body.Faces)
        {
            if (open.Contains(f)) continue;
            var src = S(f); bool rev = FaceRev(f);
            var fd = new FDesc { Surf = src, Rev = rev, Tag = f.Tag, Hint = f.InteriorHint, Slot = f.MaterialSlot, Orig = f, Touched = true };
            foreach (var l in f.Loops)
            {
                if (l.CoEdges.Count == 0) { fd.Loops.Add(new List<Seg>()); continue; }
                var ord = l.Ordered(); if (ord == null) { why = "extend: loop not closed"; return null; }
                fd.Loops.Add(ord.Select(ce => new Seg { Curve = ce.Edge.Curve, A = ce.Start.P, B = ce.End.P, Id = ce.Edge, Orig = ce.Edge }).ToList());
            }
            if (src.IsPlanar) fd.N = (src.NormalAt(src.ProjectToUV(fd.Loops[0][0].A)) * (rev ? -1 : 1)).Normalized();
            faces.Add(fd);
        }
        foreach (var F in open)
        {
            var srcF = S(F);
            if (!srcF.IsPlanar) { why = "extend: an open face is not planar"; return null; }
            var p0 = F.Loops[0].CoEdges[0].Start.P;
            // нормаль — по точной плоскости (у гранёной грани F.NormalAt — по фасетам, с ошибкой до 1e-5)
            var n = (srcF.NormalAt(srcF.ProjectToUV(p0)) * (FaceRev(F) ? -1 : 1)).Normalized();
            var capPlane = new PlaneSurface(Frame3.FromNormal(p0 + n * h, n));
            var cap = new FDesc { Surf = capPlane, Rev = false, N = n, Tag = "shell:extend:cap", Touched = true, Slot = F.MaterialSlot };
            foreach (var l in F.Loops)
            {
                var ord0 = l.Ordered(); if (ord0 == null || ord0.Count == 0) { why = "extend: open face loop not closed"; return null; }
                // обход — против часовой вокруг n у наружного контура (по часовой у внутреннего): после смещения и соглашения
                // контур образа грани бывает записан встречно — тогда он берётся в обратном порядке
                var ord = ord0.Select(c => new OCE(c.Edge, c.Start, c.End)).ToList();
                {
                    var pts = new List<Vector3d>();
                    foreach (var c in ord) { pts.Add(c.Start.P); var cv = c.Edge.Curve; if (cv is not LineCurve3) pts.Add(cv.PointAt(0.5 * (cv.T0 + cv.T1))); }
                    var area = Vector3d.Zero; for (int k = 0; k < pts.Count; k++) area += Vector3d.Cross(pts[k], pts[(k + 1) % pts.Count]);
                    bool ccw = Vector3d.Dot(area, n) > 0;
                    if (ccw != l.IsOuter) { ord.Reverse(); ord = ord.Select(c => new OCE(c.Edge, c.End, c.Start)).ToList(); }
                }
                int m = ord.Count;
                // поверхность боковины над каждым ребром
                var side = new (ISurface3 Surf, bool Rev, Vector3d N, bool OfNeighbour)[m];
                for (int i = 0; i < m; i++)
                {
                    var ce = ord[i]; var e = ce.Edge;
                    var G = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().FirstOrDefault(x => !ReferenceEquals(x, F));
                    if (G == null || open.Contains(G)) { why = "extend: an open face borders another open face"; return null; }
                    var sG = S(G);
                    bool tangent = EdgeClass.IsTangentSeam(e);
                    if (!tangent && sG.IsPlanar)
                    {
                        bool rg = FaceRev(G);
                        side[i] = (sG, rg, (sG.NormalAt(sG.ProjectToUV(ce.Start.P)) * (rg ? -1 : 1)).Normalized(), true);
                        continue;
                    }
                    if (!tangent && sG is CylinderSurface cg && Math.Abs(Math.Abs(Vector3d.Dot(cg.Axis.W.Normalized(), n)) - 1) < 1e-9)
                    { side[i] = (sG, FaceRev(G), Vector3d.Zero, true); continue; }
                    if (!tangent) { why = $"extend: a sharp {sG.GetType().Name} neighbour cannot be continued past the open face"; return null; }
                    // касание: стенка вдоль n над ребром; наружу — от внутренности F
                    switch (e.Curve)
                    {
                        case LineCurve3:
                        {
                            var dir = (ce.End.P - ce.Start.P).Normalized();
                            var outw = Vector3d.Cross(dir, n).Normalized();   // F слева от обхода (против часовой вокруг n)
                            side[i] = (new PlaneSurface(Frame3.FromNormal(ce.Start.P, outw)), false, outw, false);
                            break;
                        }
                        case ArcCurve3 a when Math.Abs(Math.Abs(Vector3d.Dot(a.Frame.W.Normalized(), n)) - 1) < 1e-9:
                        {
                            // ось — по самой дуге (её плоскость бывает не строго плоскостью F); сторона W — к n
                            var u = a.Frame.U.Normalized(); var wv = a.Frame.W.Normalized(); if (Vector3d.Dot(wv, n) < 0) wv = -wv;
                            var cyl = new CylinderSurface(new Frame3(a.Frame.Origin, u, Vector3d.Cross(wv, u).Normalized()), a.Radius);
                            // сторона: середина дуги, касательная по обходу, «влево» — внутрь F
                            var cv = a; double tm = 0.5 * (cv.T0 + cv.T1); var pm = cv.PointAt(tm); var tg = cv.TangentAt(tm).Normalized();
                            bool fwd = (cv.PointAt(cv.T0) - ce.Start.P).Length <= (cv.PointAt(cv.T0) - ce.End.P).Length;
                            if (!fwd) tg = -tg;
                            var into = Vector3d.Cross(n, tg); var radial = pm - a.Frame.Origin; radial -= n * Vector3d.Dot(radial, n);
                            bool outerIsRadial = Vector3d.Dot(into, radial) < 0;
                            side[i] = (cyl, !outerIsRadial, Vector3d.Zero, false);
                            break;
                        }
                        default: why = $"extend: a tangent {e.Curve.GetType().Name} edge of the open face"; return null;
                    }
                }
                // вершины крышки: на соседних боковинах и плоскости крышки
                var w = new Vector3d[m];
                for (int i = 0; i < m; i++)
                {
                    var v = ord[i].Start.P; var prev = side[(i - 1 + m) % m].Surf; var cur = side[i].Surf;
                    var q = OnAll(v + n * h, new[] { prev, cur, capPlane }, tol);
                    if (q == null) { why = "extend: no cap corner"; return null; }
                    w[i] = q.Value;
                }
                // рёбра: боковые (из вершин F вверх) и края крышки
                var up = new ICurve3[m]; var capE = new ICurve3[m];
                for (int i = 0; i < m; i++)
                {
                    var v = ord[i].Start.P;
                    up[i] = new LineCurve3(v, w[i]);
                    var mid = (v + w[i]) / 2; var prev = side[(i - 1 + m) % m].Surf; var cur = side[i].Surf;
                    if ((prev.PointAt(prev.ProjectToUV(mid)) - mid).Length > 10 * tol || (cur.PointAt(cur.ProjectToUV(mid)) - mid).Length > 10 * tol)
                    { why = "extend: a side edge is not straight"; return null; }
                }
                for (int i = 0; i < m; i++)
                {
                    var e = ord[i].Edge; var a0 = w[i]; var b0 = w[(i + 1) % m];
                    switch (e.Curve)
                    {
                        case LineCurve3: capE[i] = new LineCurve3(a0, b0); break;
                        case ArcCurve3 arc when side[i].Surf is CylinderSurface cs:
                        {
                            var c = arc.Frame.Origin + n * Vector3d.Dot(a0 - arc.Frame.Origin, n);
                            var shifted = new ArcCurve3(new Frame3(c, arc.Frame.U, arc.Frame.V), arc.Radius, arc.A0, arc.A1);
                            bool fw = (arc.PointAt(arc.A0) - ord[i].Start.P).Length <= (arc.PointAt(arc.A0) - ord[i].End.P).Length;
                            var sa = shifted.PointAt(shifted.A0); var sb = shifted.PointAt(shifted.A1);
                            if ((fw ? (sa - a0).Length : (sb - a0).Length) > 10 * tol || (fw ? (sb - b0).Length : (sa - b0).Length) > 10 * tol)
                            { why = "extend: a cap arc does not meet its corners"; return null; }
                            capE[i] = shifted; break;
                        }
                        default: why = "extend: a cap edge is neither a line nor an arc"; return null;
                    }
                    var mid = capE[i].PointAt(0.5 * (capE[i].T0 + capE[i].T1));
                    if ((side[i].Surf.PointAt(side[i].Surf.ProjectToUV(mid)) - mid).Length > 10 * tol) { why = "extend: a cap edge leaves its side"; return null; }
                }
                // боковины
                for (int i = 0; i < m; i++)
                {
                    var ce = ord[i]; var A = ce.Start.P; var B = ce.End.P; var wa = w[i]; var wb = w[(i + 1) % m];
                    var (surf, rev, nn, ofN) = side[i];
                    var sd = new FDesc { Surf = surf, Rev = rev, N = nn, Tag = "shell:extend", Touched = true, Slot = F.MaterialSlot };
                    if (!surf.IsPlanar) sd.Hint = surf.PointAt(surf.ProjectToUV(ce.Edge.Curve.PointAt(0.5 * (ce.Edge.Curve.T0 + ce.Edge.Curve.T1)) + n * (h / 2)));
                    // новые рёбра наследуют допуск рёбер F (дуга F бывает не строго в плоскости F — на долю её допуска)
                    double et = Math.Max(ce.Edge.Tolerance, Math.Max(ce.Start.Tolerance, ce.End.Tolerance));
                    double tA = Math.Max(et, ord[(i - 1 + m) % m].Edge.Tolerance), tB = Math.Max(et, ord[(i + 1) % m].Edge.Tolerance);
                    sd.Loops.Add(new List<Seg>
                    {
                        new() { Curve = ce.Edge.Curve, A = A, B = B, Id = ce.Edge, Orig = ce.Edge },
                        new() { Curve = up[(i + 1) % m], A = B, B = wb, Tol = tB },
                        new() { Curve = capE[i], A = wb, B = wa, Tol = et },
                        new() { Curve = up[i], A = wa, B = A, Tol = tA },
                    });
                    faces.Add(sd);
                }
                cap.Loops.Add(Enumerable.Range(0, m).Select(i => new Seg { Curve = capE[i], A = w[i], B = w[(i + 1) % m], Tol = ord[i].Edge.Tolerance }).ToList());
            }
            faces.Add(cap);
        }
        var solid = FinishFaces(faces, "extend", ref why, measureFirst: true, inherit: body.Guarantee);
        if (solid == null) return null;
        Interlocked.Increment(ref ExtendedThroughOpen);
        return solid;
    }
}
