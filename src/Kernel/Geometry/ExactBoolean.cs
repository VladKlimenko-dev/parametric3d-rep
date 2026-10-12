using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Geometry;

/// <summary>
/// G4. ТОЧНАЯ БУЛЕВА, ПЕРВЫЙ СЛУЧАЙ: сквозное цилиндрическое отверстие в теле из плоских граней.
///
/// Прежняя булева режет точный многогранник и восстанавливает кривые по его точкам; здесь
/// новое тело строится из точной геометрии: кромки отверстия — точные кривые пересечения
/// цилиндра с плоскостями граней (окружность или эллипс, Ssi), стенка — грань на самом
/// цилиндре, ограниченная этими кромками. Пройденные грани получают по отверстию-контуру.
///
/// Случай ограничен (иначе — null, и работает прежняя булева):
///  • все грани тела плоские; цилиндр пересекает ровно две грани, и каждая кромка целиком
///    лежит внутри своей грани, не задевая её края и отверстий;
///  • середина оси между кромками — внутри тела (отверстие проходит сквозь материал).
/// Деление грани кривой, пересекающей её край, и общие случаи — следующие шаги.
/// </summary>
public static class ExactBoolean
{
    public static TSolid? SubtractThroughCylinder(TSolid body, Ssi.Cylinder cyl, List<string>? warnings = null)
        => Subtract(body, cyl, null, warnings);

    /// <summary>
    /// ГЛУХОЕ ОТВЕРСТИЕ: цилиндр от входа до плоского дна, перпендикулярного оси, в точке bottom
    /// (ось цилиндра направлена ВГЛУБЬ тела). Вход — одна грань; дно — новая плоская грань-круг.
    /// </summary>
    public static TSolid? SubtractBlindCylinder(TSolid body, Ssi.Cylinder cyl, Vector3d bottom, List<string>? warnings = null)
        => Subtract(body, cyl, bottom, warnings);

    static TSolid? Subtract(TSolid body, Ssi.Cylinder cyl, Vector3d? bottom, List<string>? warnings)
    {
        var w = cyl.Axis.Normalized();
        double zBottom = bottom is { } bt ? Vector3d.Dot(bt - cyl.P, w) : double.PositiveInfinity;
        // 1. Кромки: пересечение цилиндра с плоскостями граней.
        var hits = new List<(TFace Face, ICurve3 Rim, Vector3d Centre)>();
        foreach (var f in body.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            var shape = ExactEdges.ShapeOf(src);
            if (shape == null) return Refuse(warnings, "a face has no analytic surface");
            Ssi.Result r;
            try { r = Ssi.Intersect(shape, cyl, cyl.P, 1e6 > 0 && shape is Ssi.Plane ? 1e6 : BodyRadius(body, cyl.P) * 2 + 1); }
            catch (NotSupportedException) { return Refuse(warnings, "no surface intersection for a face"); }
            if (r.Kind == Ssi.Kind.Empty) continue;
            if (r.Kind != Ssi.Kind.Curves) return Refuse(warnings, "the cylinder touches or coincides with a face");
            foreach (var rim in r.Curves)
            {
                if (rim is LineCurve3) { if (OnFace(f, src, rim.PointAt((rim.T0 + rim.T1) / 2))) return Refuse(warnings, "the cylinder runs along a face"); continue; }
                bool closed = (rim.PointAt(rim.T0) - rim.PointAt(rim.T1)).Length < 1e-6;
                // Кромка целиком внутри грани (и не у её края/отверстий)?
                int inside = 0, outside = 0;
                for (int i = 0; i < 64; i++)
                {
                    var q = rim.PointAt(rim.T0 + (rim.T1 - rim.T0) * i / 64);
                    if (OnFace(f, src, q)) inside++; else outside++;
                }
                if (inside == 0) continue;
                if (!closed) return Refuse(warnings, "an open rim on a face");
                if (bottom != null)
                {
                    double zmin = double.MaxValue, zmax = double.MinValue;
                    for (int i = 0; i < 64; i++) { var q = rim.PointAt(rim.T0 + (rim.T1 - rim.T0) * i / 64); double zq = Vector3d.Dot(q - cyl.P, w); zmin = Math.Min(zmin, zq); zmax = Math.Max(zmax, zq); }
                    if (zmin > zBottom) continue;
                    if (zmax > zBottom - 1e-9) return Refuse(warnings, "the hole bottom cuts a face rim");
                }
                if (outside > 0) return Refuse(warnings, "a rim crosses a face boundary (face splitting is not implemented yet)");
                var centre = Enumerable.Range(0, 64).Select(i => rim.PointAt(rim.T0 + (rim.T1 - rim.T0) * i / 64)).Aggregate(Vector3d.Zero, (acc, q) => acc + q) / 64;
                hits.Add((f, rim, centre));
            }
        }
        ICurve3? capRim = null; Vector3d capCentre = default;
        if (bottom != null)
        {
            if (hits.Count != 1) return Refuse(warnings, $"a blind hole must enter through one face, not {hits.Count}");
            capCentre = cyl.P + w * zBottom;
            capRim = new ArcCurve3(Frame3.FromNormal(capCentre, w), cyl.R, 0, 2 * Math.PI);
            // Дно целиком в материале: точки круга дна и его центр — внутри тела.
            for (int i = 0; i < 16; i++) if (!InsideSolid(body, capRim.PointAt(i / 16.0) - (capRim.PointAt(i / 16.0) - capCentre) * 1e-6)) return Refuse(warnings, "the hole bottom is not inside the material");
            if (!InsideSolid(body, (hits[0].Centre + capCentre) / 2)) return Refuse(warnings, "the hole does not run into material");
        }
        else
        {
            if (hits.Count != 2) return Refuse(warnings, $"the cylinder crosses {hits.Count} face(s), not 2");
            var mid = (hits[0].Centre + hits[1].Centre) / 2;
            if (!InsideSolid(body, mid)) return Refuse(warnings, "the hole does not run through material");
        }

        // 2. Новое тело: копия, у двух граней — отверстия, плюс стенка на цилиндре.
        var copy = Ops.TransformOp.Copy(body, Ops.TransformOp.Move(Vector3d.Zero));
        copy.Exact = null;
        // Соответствие граней копии граням тела — по геометрии (копия может менять их порядок:
        // прежнее сопоставление по порядку клало кромку в соседнюю грань).
        Vector3d Mid(TFace f) { var pts = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(pts, pts.Count == 0); return pts.Aggregate(Vector3d.Zero, (a, q) => a + q) / Math.Max(1, pts.Count); }
        var copyMids = copy.Faces.Select(cf => (F: cf, M: Mid(cf))).ToList();
        // Сначала по имени и виду (имя грани копия сохраняет), иначе — по точке ВНУТРИ грани (центр
        // её наибольшего треугольника): центр контура у стенки вала — точка на оси, та же, что у
        // торца, и кромки отверстия попадали в торец.
        Vector3d Inner(TFace f)
        {
            var mb = new Tess.MeshBuilder(); mb.BeginFace(f.Id); f.Surface.TessellateFace(f, mb); mb.EndFace();
            var md = new Tess.MeshData(); mb.FillInto(md);
            Vector3d best = Mid(f); double ba = -1;
            for (int i = 0; i + 2 < md.Indices.Length; i += 3)
            {
                var a = mb.PositionsD[md.Indices[i]]; var b = mb.PositionsD[md.Indices[i + 1]]; var c = mb.PositionsD[md.Indices[i + 2]];
                double ar = Vector3d.Cross(b - a, c - a).LengthSquared; if (ar > ba) { ba = ar; best = (a + b + c) / 3; }
            }
            return best;
        }
        var copyInner = copy.Faces.Select(cf => (F: cf, M: Inner(cf))).ToList();
        var faceMap = new Dictionary<TFace, TFace>(ReferenceEqualityComparer.Instance);
        foreach (var bf in body.Faces)
        {
            var sameTag = copy.Faces.Where(cf => cf.Tag == bf.Tag && cf.Tag.Length > 0 && cf.Loops.Count == bf.Loops.Count).ToList();
            if (sameTag.Count == 1) { faceMap[bf] = sameTag[0]; continue; }
            var m = Inner(bf);
            faceMap[bf] = copyInner.OrderBy(x => (x.M - m).LengthSquared).First().F;
        }
        var rimEdges = new List<TEdge>();
        foreach (var (f0, rim, _) in hits)
        {
            var f = faceMap[f0];
            var start = new TVertex(rim.PointAt(rim.T0));
            var edge = new TEdge { V0 = start, V1 = start, Curve = rim };
            rimEdges.Add(edge);
            var loop = f.AddLoop(false);
            loop.Add(edge, false);                      // обход поправит соглашение K05

        }
        // Стенка — выдавливание полной окружности вдоль оси (параметр угла нормирован на [0, 1] и
        // периодичен, как у стенок, которые даёт прежняя булева). CylinderSurface с углом в радианах
        // разметка и кривые в параметрах не узнавали как замкнутую по углу: допуск рёбер выходил
        // 7–11 мм, сетка стенки — с ошибками.
        double z0 = hits.Min(h => Vector3d.Dot(h.Centre - cyl.P, w)) - 4 * cyl.R - 1;
        double z1 = Math.Max(hits.Max(h => Vector3d.Dot(h.Centre - cyl.P, w)), bottom != null ? zBottom : double.MinValue) + 4 * cyl.R + 1;
        var baseFrame = Frame3.FromNormal(cyl.P + w * z0, w);
        var wallSurf = new ExtrudedSurface(new ArcCurve3(baseFrame, cyl.R, 0, 2 * Math.PI), w * (z1 - z0));
        var wall = new TFace { Surface = wallSurf, Reversed = true, Tag = "exact:hole-wall" };
        foreach (var e in rimEdges) { var l = wall.AddLoop(true); l.Add(e, true); }
        TFace? cap = null; TEdge? capEdge = null;
        if (capRim != null)
        {
            var cv = new TVertex(capRim.PointAt(capRim.T0));
            capEdge = new TEdge { V0 = cv, V1 = cv, Curve = capRim };
            var lw = wall.AddLoop(true); lw.Add(capEdge, true);
            // Дно смотрит наружу из тела — против оси (ось направлена вглубь).
            cap = new TFace { Surface = new PlaneSurface(Frame3.FromNormal(capCentre, -w)), Reversed = false, Tag = "exact:hole-bottom" };
            var lc = cap.AddLoop(true); lc.Add(capEdge, false);
            copy.Shells[0].Add(cap);
        }
        copy.Shells[0].Add(wall);
        // Все грани — на свои точные поверхности (разметка заново по контурам: прежние треугольники
        // отверстия не знают). Сторона грани сохраняется: флаг Reversed — по прежней нормали.
        foreach (var f in copy.Faces)
        {
            if (f == wall || f == cap) continue;
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src == null) continue;
            // Плоская грань любого вида (выдавливание отрезка) — явной плоскостью: соглашение K05
            // проверяет вложенность контуров в плоскости грани.
            if (src is not PlaneSurface && ExactEdges.ShapeOf(src) is Ssi.Plane pl) src = new PlaneSurface(Frame3.FromNormal(pl.P, pl.N.Normalized()));
            if (ReferenceEquals(src, f.Surface)) continue;
            // Наружная сторона — геометрически: точка чуть снаружи по нормали не должна быть в теле.
            // (Нормаль грани примитива по её прежней поверхности бывала обращена внутрь.)
            var ring = new List<Vector3d>(); foreach (var ce in f.Loops[0].CoEdges) ce.Tessellate(ring, ring.Count == 0);
            Vector3d probe;
            var oldTris = (f.Surface as FacetedSurface)?.Facets;
            if (oldTris is { Count: > 0 })
            {
                // Точка внутри грани — центр её наибольшего треугольника (у кривой грани центр
                // контура лежит вне поверхности: у стенки вала — на оси).
                var big = oldTris.OrderByDescending(t => Vector3d.Cross(t.B - t.A, t.C - t.A).LengthSquared).First();
                probe = src.PointAt(src.ProjectToUV((big.A + big.B + big.C) / 3));
            }
            else
            {
                probe = ring.Aggregate(Vector3d.Zero, (acc, q) => acc + q) / ring.Count;
                if (!InsideFace(f, src as PlaneSurface ?? new PlaneSurface(Frame3.FromNormal(probe, src.NormalAt(src.ProjectToUV(probe)))), probe, 1e-6))
                    probe = ring[0] * 0.98 + ring[ring.Count / 2] * 0.02;
            }
            var nNew = src.NormalAt(src.ProjectToUV(probe)).Normalized();
            double eps = 1e-3;
            f.Surface = src;
            f.Reversed = InsideSolid(body, probe + nNew * eps);
        }

        // Обход кромок задаётся явно: в плоской грани отверстие обходится по часовой стрелке
        // относительно наружной нормали грани; в стенке — та же кромка в обратную сторону.
        foreach (var (f0, rim, _) in hits)
        {
            var f = faceMap[f0];
            var holeLoop = f.Loops.Last();
            var pts = new List<Vector3d>();
            for (int i = 0; i < 64; i++) pts.Add(rim.PointAt(rim.T0 + (rim.T1 - rim.T0) * i / 64));
            var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
            var nOut = f.NormalAt(pts[0]);
            bool rev = Vector3d.Dot(nw, nOut) > 0;          // кривая идёт против часовой — развернуть
            holeLoop.CoEdges[0].Reversed = rev;
            var edge = holeLoop.CoEdges[0].Edge;
            foreach (var l in wall.Loops) if (l.CoEdges[0].Edge == edge) l.CoEdges[0].Reversed = !rev;
        }
        if (cap != null && capEdge != null)
        {
            var pts = new List<Vector3d>(); for (int i = 0; i < 64; i++) pts.Add(capRim!.PointAt(i / 64.0));
            var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
            bool rev = Vector3d.Dot(nw, -w) < 0;                    // наружный контур дна — против часовой вокруг -w
            cap.Loops[0].CoEdges[0].Reversed = rev;
            foreach (var l in wall.Loops) if (l.CoEdges[0].Edge == capEdge) l.CoEdges[0].Reversed = !rev;
        }
        var norm = TopologyConventions.Normalize(copy);
        if (copy.TopologyConvention == 0)
            foreach (var f in copy.Faces.Where(ff => ff.Loops.Count > 1))
                foreach (var l in f.Loops)
                {
                    var pts = new List<Vector3d>(); foreach (var ce in l.CoEdges) ce.Tessellate(pts, pts.Count == 0);
                    var nw = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
                    warnings?.Add($"DBG face {f.Id} {f.Surface.GetType().Name} rev {f.Reversed} loop outer {l.IsOuter} n {l.CoEdges.Count} newell·normal {Vector3d.Dot(nw, f.NormalAt(pts[0])):0.##} first {pts[0]:0.##} ce.rev {string.Join(",", l.CoEdges.Select(c => c.Reversed))}");
                }
        if (copy.TopologyConvention == 0) return Refuse(warnings, "topology conventions failed: " + norm + " | " + string.Join("; ", norm.Problems.Take(3)) + " | faces: " + string.Join(", ", copy.Faces.Select(ff => $"{ff.Id}:{ff.Surface.GetType().Name}/{ff.Loops.Count}/{(ff.Reversed ? "R" : "")}")));
        Export.StepImport.MeshFacesFromLoops(copy, warnings, "ExactBoolean");
        Export.StepImport.OrientShellPublic(copy);
        copy.Guarantee = GuaranteeLevel.ExactQuadric;
        warnings?.Add("ExactBoolean: through hole built from exact surface intersections.");
        return copy;
    }

    static double BodyRadius(TSolid b, Vector3d c) => b.Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).Max(q => (q - c).Length);

    /// <summary>
    /// Точка лежит на грани (внутри её контуров): плоская — проверкой многоугольника, кривая —
    /// расстоянием до треугольников её гранения (они покрывают ровно область грани).
    /// </summary>
    static bool OnFace(TFace f, ISurface3? src, Vector3d q)
    {
        if (ExactEdges.ShapeOf(src) is Ssi.Plane pl)
            return InsideFace(f, new PlaneSurface(Frame3.FromNormal(pl.P, pl.N.Normalized())), q, 1e-6);
        var tris = f.Surface is FacetedSurface fs ? (fs.Display is { Count: > 0 } ? fs.Display : fs.Facets.ToList()) : null;
        if (tris == null)
        {
            // Грань на точной поверхности без гранения (тело из выдавливания): сетка грани по её контурам.
            var mb = new Tess.MeshBuilder(); mb.BeginFace(f.Id); f.Surface.TessellateFace(f, mb); mb.EndFace();
            var md = new Tess.MeshData(); mb.FillInto(md);
            tris = new List<(Vector3d, Vector3d, Vector3d)>();
            for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                tris.Add((mb.PositionsD[md.Indices[i]], mb.PositionsD[md.Indices[i + 1]], mb.PositionsD[md.Indices[i + 2]]));
        }
        double best = double.MaxValue;
        foreach (var (a, b, c) in tris) best = Math.Min(best, DistTri(q, a, b, c));
        return best < 0.05;
    }

    static double DistTri(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        var n = Vector3d.Cross(b - a, c - a); double nl = n.Length; if (nl < 1e-18) return (p - a).Length;
        n /= nl; double d = Vector3d.Dot(p - a, n); var q = p - n * d;
        bool In(Vector3d x, Vector3d y) => Vector3d.Dot(Vector3d.Cross(y - x, q - x), n) >= -1e-12;
        if (In(a, b) && In(b, c) && In(c, a)) return Math.Abs(d);
        double Seg(Vector3d x, Vector3d y) { var xy = y - x; double t = Math.Clamp(Vector3d.Dot(p - x, xy) / xy.LengthSquared, 0, 1); return (x + xy * t - p).Length; }
        return Math.Min(Seg(a, b), Math.Min(Seg(b, c), Seg(c, a)));
    }

    static TSolid? Refuse(List<string>? w, string why) { w?.Add("ExactBoolean: " + why + " — the polyhedral boolean is used."); return null; }

    /// <summary>Точка плоской грани внутри её внешнего контура и вне отверстий, дальше tol от края.</summary>
    static bool InsideFace(TFace f, PlaneSurface ps, Vector3d q, double tol)
    {
        if (Math.Abs(Vector3d.Dot(q - ps.F.Origin, ps.F.W.Normalized())) > 1e-6) return false;
        var uv = ps.F.ToLocal2(q);
        bool inOuter = false; int holesHit = 0; double minDist = double.MaxValue;
        foreach (var loop in f.Loops)
        {
            var pts = new List<Vector3d>();
            foreach (var ce in loop.CoEdges) ce.Tessellate(pts, pts.Count == 0);
            var poly = pts.Select(p => ps.F.ToLocal2(p)).ToList();
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > uv.Y) != (b.Y > uv.Y) && uv.X < (b.X - a.X) * (uv.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                var ab = b - a; double t = Math.Clamp(Vector2d.Dot(uv - a, ab) / Math.Max(ab.LengthSquared, 1e-300), 0, 1);
                minDist = Math.Min(minDist, (a + ab * t - uv).Length);
            }
            if (loop.IsOuter) inOuter |= inside; else if (inside) holesHit++;
        }
        return inOuter && holesHit == 0 && minDist > tol;
    }

    /// <summary>Точка внутри тела: чётность пересечений луча с треугольниками его сетки.</summary>
    static bool InsideSolid(TSolid s, Vector3d q)
    {
        var md = Tess.Tessellator.Tessellate(s, facets: true);
        var dir = new Vector3d(0.5773, 0.5571, 0.5967).Normalized();
        int hits = 0;
        for (int t = 0; t + 2 < md.Indices.Length; t += 3)
        {
            var a = Pos(md, t); var b = Pos(md, t + 1); var c = Pos(md, t + 2);
            var e1 = b - a; var e2 = c - a; var p = Vector3d.Cross(dir, e2); double det = Vector3d.Dot(e1, p);
            if (Math.Abs(det) < 1e-14) continue;
            double inv = 1 / det; var sv = q - a; double u = Vector3d.Dot(sv, p) * inv; if (u < 0 || u > 1) continue;
            var qv = Vector3d.Cross(sv, e1); double v = Vector3d.Dot(dir, qv) * inv; if (v < 0 || u + v > 1) continue;
            if (Vector3d.Dot(e2, qv) * inv > 0) hits++;
        }
        return hits % 2 == 1;
    }

    static Vector3d Pos(Tess.MeshData md, int i) { var p = md.Vertices[md.Indices[i]].P; return new Vector3d(p.X, p.Y, p.Z); }
}

