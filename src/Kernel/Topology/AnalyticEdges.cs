using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// K08. КРИВАЯ РЕБРА — ИЗ ПЕРЕСЕЧЕНИЯ ПОВЕРХНОСТЕЙ, А НЕ ИЗ ТОЧЕК СЕТКИ.
///
/// Восстановление кривых после булевой работает по точкам гранения: цепочка хорд становится
/// дугой, ряд точек на прямой — отрезком. У короткого ребра между плоскостью и цилиндром,
/// когда плоскость наклонна к оси, точек две, и ребро записывалось ОТРЕЗКОМ, хотя настоящее
/// пересечение — дуга эллипса. Проверка авторитетной геометрии нашла такое на повёрнутом
/// скруглении: отрезок длиной 1.11 мм отходил от цилиндра скругления на 0.080 мм в середине.
///
/// Здесь для ребра «плоскость — цилиндр», чья кривая не лежит на цилиндре, кривая заменяется
/// дугой эллипса сечения (QuadricSection.CylinderEllipse) между проекциями концов. Замена
/// принимается, только если новая кривая лежит на обеих поверхностях и её концы совпадают с
/// вершинами ребра в пределах допуска; иначе ребро остаётся как было.
/// </summary>
public static class AnalyticEdges
{
    public static Action<string>? Debug;

    public static int Repair(TSolid solid, List<string>? warnings = null)
    {
        solid.BeginEdit("AnalyticEdges.Repair", EditKind.Derive);
        int fixedCount = 0;
        foreach (var e in solid.Edges)
        {
            if (e.Curve is not (LineCurve3 or PolylineCurve3)) continue;
            var faces = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (faces.Count != 2) continue;
            var s0 = GeometryAudit.AuthoritativeSurface(faces[0]);
            var s1 = GeometryAudit.AuthoritativeSurface(faces[1]);
            if (!PlaneCylinder(s0, s1, out var plane, out var cyl) && !PlaneCylinder(s1, s0, out plane, out cyl))
            {
                if (Debug != null && (s0 is ExtrudedSurface { Base: ArcCurve3 } || s1 is ExtrudedSurface { Base: ArcCurve3 }))
                {
                    var ex = (s0 as ExtrudedSurface) ?? (ExtrudedSurface)s1!;
                    if (ex.Base is ArcCurve3 ab) Debug($"edge {e.Id}: not plane-cylinder; arc W·dir = {Vector3d.Dot(ab.Frame.W.Normalized(), ex.Dir.Normalized()):R}, other {(ReferenceEquals(ex, s0) ? s1 : s0)?.GetType().Name}");
                }
                continue;
            }

            var (pAt, pN) = plane;
            var (cAt, cDir, cR) = cyl;
            var mid = e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2);
            double offMid = OffCylinder(mid, cAt, cDir, cR);
            double offEnds = Math.Max(OffCylinder(e.V0.P, cAt, cDir, cR), OffCylinder(e.V1.P, cAt, cDir, cR));
            // Кривая лежит на цилиндре — чинить нечего (в том числе образующая при плоскости вдоль оси).
            if (offMid <= Math.Max(1e-6, 2 * offEnds)) continue;

            var sec = Exact.QuadricSection.CylinderEllipse(cAt, cDir, cR, pAt, pN);
            if (sec == null) continue;
            var (centre, major, minor, A, B) = sec.Value;
            double Angle(Vector3d p) => Math.Atan2(Vector3d.Dot(p - centre, minor) / B, Vector3d.Dot(p - centre, major) / A);
            double a0 = Angle(e.V0.P), a1 = Angle(e.V1.P);
            double s = a1 - a0;
            while (s <= -Math.PI) s += 2 * Math.PI;
            while (s > Math.PI) s -= 2 * Math.PI;
            var frame = new Frame3(centre, major, minor);
            // Короткая дуга или дополнительная — та, чья середина ближе к прежней кривой.
            var shortArc = new EllipseCurve3(frame, A, B, a0, a0 + s);
            var longArc = new EllipseCurve3(frame, A, B, a0, a0 + s - Math.Sign(s) * 2 * Math.PI);
            var arc = (shortArc.PointAt(0.5) - mid).Length <= (longArc.PointAt(0.5) - mid).Length ? shortArc : longArc;

            // Проверка новой кривой: на обеих поверхностях и концами в вершинах.
            double worst = 0;
            for (int k = 0; k <= 16; k++)
            {
                var q = arc.PointAt(k / 16.0);
                worst = Math.Max(worst, Math.Max(OffCylinder(q, cAt, cDir, cR), Math.Abs(Vector3d.Dot(q - pAt, pN))));
            }
            double gap = Math.Max((arc.PointAt(0) - e.V0.P).Length, (arc.PointAt(1) - e.V1.P).Length);
            // Вершина сама может отстоять от цилиндра (она — точка точного многогранника, а не
            // аналитического пересечения): конец дуги — её проекция, и зазор не больше этого отхода.
            double allowed = Math.Max(Math.Max(1e-4, 4 * e.Tolerance), 1.01 * offEnds + 1e-7);
            if (worst > 1e-9 * Math.Max(1, cR) || gap > allowed)
            {
                Debug?.Invoke($"edge {e.Id}: rejected, off {worst:E2}, end gap {gap:E2} (allowed {allowed:E2}), mid off before {offMid:E2}");
                continue;
            }

            e.Curve = arc;
            // Видимые сетки двух граней посчитаны по прежней кривой — сбросить, иначе кромка
            // в соседних гранях размечена по-разному (неманифолдные стороны, K09-проверка
            // повёрнутых скруглений).
            foreach (var f in faces) if (f.Surface is FacetedSurface fsd) { fsd.SetDisplay(null, null); }
            e.Tolerance = Math.Max(e.Tolerance, gap);
            e.V0.Tolerance = Math.Max(e.V0.Tolerance, (arc.PointAt(0) - e.V0.P).Length);
            e.V1.Tolerance = Math.Max(e.V1.Tolerance, (arc.PointAt(1) - e.V1.P).Length);
            fixedCount++;
        }
        bool curvesReplaced = fixedCount > 0;
        fixedCount += SnapIntersectionVertices(solid, warnings);
        // G4: рёбра между плоскостями, цилиндрами и шарами — точными пересечениями их
        // поверхностей, вершины — на общую точку поверхностей (видимые сетки сбрасываются).
        if (Geometry.ExactEdges.Enabled)
        {
            // Снимок, чтобы откатить замену, если тело после неё не проходит проверку B-rep
            // (найдено: скругление всех рёбер призмы — контур грани размыкался; причина не найдена).
            var vSnap = solid.Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().Select(v => (v, v.P, v.Tolerance)).ToList();
            var eSnap = solid.Edges.Select(e => (e, e.Curve, e.Tolerance)).ToList();
            bool wasValid = SolidValidator.ValidateBRep(solid).IsValid;
            var ex = Geometry.ExactEdges.Apply(solid);
            if ((ex.Replaced > 0 || ex.VerticesSnapped > 0) && wasValid && !SolidValidator.ValidateBRep(solid).IsValid)
            {
                foreach (var (v, p, t) in vSnap) { v.P = p; v.Tolerance = t; }
                foreach (var (e, c, t) in eSnap) { e.Curve = c; e.Tolerance = t; e.PolylineCache = null; }
                warnings?.Add("Edges: exact intersection curves were rolled back (the body would fail the B-rep check).");
                ex = new Geometry.ExactEdges.Report();
            }
            if (ex.Replaced > 0 || ex.VerticesSnapped > 0)
            {
                curvesReplaced = true;
                warnings?.Add($"Edges: {ex.Replaced} exact intersection curve(s), {ex.VerticesSnapped} vertex(es) on the exact surface intersection.");
            }
        }
        // m.x2: стенка-выдавливание сплайна — вершины на точное пересечение, рёбра поперёк — сдвинутым сплайном
        {
            bool wasValid = !Geometry.SplineWallEdges.Off && SolidValidator.ValidateBRep(solid).IsValid;
            var vSnap = solid.Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().Select(v => (v, v.P, v.Tolerance)).ToList();
            var eSnap = solid.Edges.Select(e => (e, e.Curve, e.Tolerance)).ToList();
            int sw = Geometry.SplineWallEdges.Apply(solid);
            if (sw > 0 && wasValid && !SolidValidator.ValidateBRep(solid).IsValid)
            {
                foreach (var (v, p, t) in vSnap) { v.P = p; v.Tolerance = t; }
                foreach (var (e, c, t) in eSnap) { e.Curve = c; e.Tolerance = t; e.PolylineCache = null; }
                warnings?.Add("Edges: spline-wall exact edges were rolled back (the body would fail the B-rep check).");
                sw = 0;
            }
            if (sw > 0) { curvesReplaced = true; warnings?.Add($"Edges: {sw} spline-wall vertex(es)/edge(s) made exact."); }
        }
        if (curvesReplaced && !Ops.BooleanOp.DeferMeshing && Ops.BooleanOp.ExactMeshing)
        {
            solid.DisplayMeshKey = null;
            Ops.BooleanOp.MeshExactlySolid(solid, warnings);
        }
        if (fixedCount > 0)
        {
            solid.DisplayMeshKey = null;
            warnings?.Add($"Edges: {fixedCount} plane–cylinder edge(s) given their exact elliptic curve.");
        }
        return fixedCount;
    }

    /// <summary>
    /// K10. ВЕРШИНА НА КРИВОЙ ПЕРЕСЕЧЕНИЯ — НА НАСТОЯЩЕМ ПЕРЕСЕЧЕНИИ. Вершина кромки поперечного
    /// отверстия — точка точного многогранника, до 0.012 мм от настоящей кривой пересечения двух
    /// цилиндров; кривая ребра шла от неё изломом, а запись в обменный файл расходилась с телом.
    /// Вершина, вокруг которой ровно две поверхности (внутренняя точка кромки), переносится на их
    /// пересечение уточнением кривой; кривые её рёбер пересобираются с новым концом. Вершины, где
    /// сходятся три поверхности и больше, не трогаются.
    /// </summary>
    private static int SnapIntersectionVertices(TSolid solid, List<string>? warnings)
    {
        int moved = 0;
        var byVertex = new Dictionary<TVertex, List<TEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in solid.Edges)
            foreach (var v in new[] { e.V0, e.V1 }.Distinct())
            {
                if (!byVertex.TryGetValue(v, out var l)) byVertex[v] = l = new List<TEdge>();
                l.Add(e);
            }
        foreach (var (v, list) in byVertex)
        {
            if (list.Count != 2 || list.Any(e => e.Curve is not IntersectionCurve3)) continue;
            var surfaces = list.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>()
                               .Select(GeometryAudit.AuthoritativeSurface).Distinct().ToList();
            if (surfaces.Count != 2 || surfaces.Any(x => x == null)) continue;
            var ic = (IntersectionCurve3)list[0].Curve;
            var other = list[0].V0 == v ? list[0].V1 : list[0].V0;
            Vector3d tangent = (other.P - v.P).LengthSquared > 0 ? (other.P - v.P).Normalized() : Vector3d.UnitX;
            var p = ic.Refine(v.P, tangent, out double residual);
            double shift = (p - v.P).Length;
            if (residual > 1e-9 || shift > 0.05 || shift < 1e-12) continue;
            v.P = p;
            foreach (var e in list)
            {
                var c = (IntersectionCurve3)e.Curve;
                var seed = c.Seed.ToArray();
                bool startHere = (seed[0] - p).LengthSquared <= (seed[^1] - p).LengthSquared;
                if (startHere) seed[0] = p; else seed[^1] = p;
                e.Curve = new IntersectionCurve3(c.A, c.B, seed);
            }
            // Видимые сетки граней у этих рёбер посчитаны по прежней вершине — они сбрасываются,
            // иначе соседние грани показывали бы кромку в разных точках (открытая сетка).
            foreach (var f in list.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct())
                if (f.Surface is FacetedSurface fsd) { fsd.SetDisplay(null, null); }
            moved++;
        }
        if (moved > 0)
        {
            warnings?.Add($"Edges: {moved} vertex(es) moved onto the exact surface intersection.");
            solid.DisplayMeshKey = null;
            // Без отложенной разметки тело уже размечено — размечается заново по новым вершинам.
            if (!Ops.BooleanOp.DeferMeshing && Ops.BooleanOp.ExactMeshing) Ops.BooleanOp.MeshExactlySolid(solid, warnings);
        }
        return moved;
    }

    private static bool PlaneCylinder(ISurface3? a, ISurface3? b, out (Vector3d At, Vector3d N) plane, out (Vector3d At, Vector3d Dir, double R) cyl)
    {
        plane = default; cyl = default;
        switch (a)
        {
            case PlaneSurface p: plane = (p.F.Origin, p.F.W.Normalized()); break;
            case ExtrudedSurface ex when ex.Base is LineCurve3 ln:
            {
                var n = Vector3d.Cross((ln.B - ln.A).Normalized(), ex.Dir.Normalized());
                if (n.LengthSquared < 1e-18) return false;
                plane = (ln.A, n.Normalized()); break;
            }
            default: return false;
        }
        switch (b)
        {
            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc && ex.Dir.LengthSquared > 1e-18
                                     && Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), ex.Dir.Normalized())) > 1 - 1e-12:
                cyl = (arc.Frame.Origin, ex.Dir.Normalized(), arc.Radius); return true;
            case CylinderSurface cs:
                cyl = (cs.Axis.Origin, cs.Axis.W.Normalized(), cs.Radius); return true;
            default: return false;
        }
    }

    private static double OffCylinder(Vector3d p, Vector3d at, Vector3d dir, double r)
    {
        var d = p - at;
        return Math.Abs((d - dir * Vector3d.Dot(d, dir)).Length - r);
    }
}
