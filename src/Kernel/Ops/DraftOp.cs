using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// УКЛОН: выбранные плоские грани наклоняются на угол вокруг линии, по которой они пересекают
/// нейтральную плоскость. На нейтральной плоскости сечение тела не меняется, в стороне от неё
/// грань уходит внутрь — материал снимается.
///
/// Первый вариант: только снятие материала и только плоские грани. Наращивания (уклон наружу)
/// и кривых граней пока нет.
/// </summary>
public static class DraftOp
{
    /// <param name="onFaces">Точки на гранях, к которым применяется уклон.</param>
    /// <param name="neutralPoint">Точка нейтральной плоскости.</param>
    /// <param name="neutralNormal">Нормаль нейтральной плоскости.</param>
    /// <param name="degrees">Угол уклона.</param>
    public static bool Apply(List<TSolid> bodies, IReadOnlyList<Vector3d> onFaces,
                             Vector3d neutralPoint, Vector3d neutralNormal, double degrees,
                             List<string> warnings)
    {
        if (bodies.Count == 0) { warnings.Add("Draft: no body."); return false; }
        if (Math.Abs(degrees) < 1e-9) { warnings.Add("Draft: the angle must be greater than zero."); return false; }
        Vector3d m = neutralNormal.LengthSquared > 1e-18 ? neutralNormal.Normalized() : Vector3d.UnitZ;

        // KRN-07 (07.6): ТОЧНАЯ ЗАМЕНА ПЛОСКОСТЕЙ — внутрь и наружу (отрицательный угол), одна или несколько граней.
        if (!TweakOff)
        {
            string? why = null;
            var t = TweakPlanar(bodies[0], onFaces, neutralPoint, m, degrees, ref why);
            if (t != null)
            {
                bodies[0] = t;
                warnings.Add($"Draft: {degrees:0.###}°, {onFaces.Count} face(s) drafted (exact plane replacement).");
                return true;
            }
            if (degrees < 0) { warnings.Add($"Draft: an outward draft is built only by plane replacement, which declined: {why}."); return false; }
            warnings.Add($"Draft: the exact plane replacement declined ({why}); half-space cuts are used.");
        }
        else if (degrees < 0) { warnings.Add("Draft: the angle must be greater than zero."); return false; }

        int done = 0;
        foreach (Vector3d probe in onFaces)
        {
            TFace? face = null;
            double best = double.MaxValue;
            Vector3d nOut = Vector3d.Zero, onFace = Vector3d.Zero;

            foreach (TFace f in bodies[0].Faces)
            {
                ISurface3? src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
                if (src == null || !src.IsPlanar) continue;

                var mb = new Tess.MeshBuilder();
                f.Surface.TessellateFace(f, mb);
                var md = new Tess.MeshData();
                mb.FillInto(md);
                for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                {
                    Vector3d x = mb.PositionsD[md.Indices[i]], y = mb.PositionsD[md.Indices[i + 1]], z = mb.PositionsD[md.Indices[i + 2]];
                    Vector3d cr = Vector3d.Cross(y - x, z - x);
                    if (cr.LengthSquared < 1e-24) continue;
                    // Расстояние до самого треугольника, а не до его центра: у грани из двух
                    // треугольников центр отстоит на десятки миллиметров.
                    double d = (ClosestOnTriangle(probe, x, y, z) - probe).LengthSquared;
                    if (d < best) { best = d; face = f; nOut = cr.Normalized(); onFace = (x + y + z) / 3; }
                }
            }

            // Точка должна лежать НА грани: иначе бралась ближайшая, где бы точка ни была.
            if (face == null || Math.Sqrt(best) > 0.02 * Extent(bodies[0]))
            {
                warnings.Add("Draft: no planar face at the given point.");
                continue;
            }

            // Ось поворота — направление линии пересечения грани с нейтральной плоскостью.
            Vector3d axis = Vector3d.Cross(nOut, m);
            if (axis.LengthSquared < 1e-12) { warnings.Add("Draft: the face is parallel to the neutral plane."); continue; }
            axis = axis.Normalized();

            // Точка на этой линии, ближайшая к центру грани.
            Vector3d p0 = LineOfPlanes(onFace, nOut, neutralPoint, m, axis, onFace);

            // Поворот нормали: грань уходит внутрь тела по мере удаления от нейтральной
            // плоскости в сторону материала.
            double a = degrees * Math.PI / 180;
            // СТОРОНА НАКЛОНА — ПО ТОМУ, ГДЕ ЛЕЖИТ ТЕЛО. Грань должна уходить внутрь материала
            // по мере удаления от нейтральной плоскости. Прежде сторона бралась «в сторону от
            // нейтральной плоскости» без учёта, с какой стороны от неё тело: у бруска 40×30×20 с
            // нейтралью по низу отсекаемая полуплоскость проходила снаружи, операция
            // отчитывалась «наклонена 1 грань», а объём оставался ровно 24 000 и нормаль грани
            // не менялась.
            Vector3d side = Vector3d.Cross(axis, nOut).Normalized();
            double bodySide = Vector3d.Dot(Centre(bodies[0]) - neutralPoint, m);
            if (Vector3d.Dot(side, m) * bodySide < 0) side = -side;
            Vector3d nNew = (nOut * Math.Cos(a) + side * Math.Sin(a)).Normalized();

            double size = Extent(bodies[0]) * 4;
            TSolid half = HalfSpace(p0, nNew, size);
            BooleanOp.Subtract(bodies, half, warnings);
            if (bodies.Count == 0) { warnings.Add("Draft: the angle removes the whole body."); return false; }
            done++;
        }

        if (done == 0) return false;
        warnings.Add($"Draft: {degrees:0.###}°, {done} face(s) drafted.");
        return true;
    }

    /// <summary>Мутация P3D_DRAFTTWEAK=0: только прежний путь (вычитание полупространств, только внутрь).</summary>
    static readonly bool TweakOff = Environment.GetEnvironmentVariable("P3D_DRAFTTWEAK") == "0";

    /// <summary>
    /// KRN-07 (07.6): УКЛОН ЗАМЕНОЙ ПЛОСКОСТИ. Прежде уклон был вычитанием полупространства: только снятие материала (уклон
    /// наружу — нет), а несколько граней — несколько вычитаний, многогранное тело (ExactPolyhedral). Теперь у тела, где все
    /// грани у сдвигаемых вершин плоские, а рёбра — отрезки: плоскость выбранной грани поворачивается вокруг линии её
    /// пересечения с нейтральной плоскостью (внутрь при угле &gt; 0, наружу при &lt; 0), каждая вершина выбранной грани —
    /// пересечение плоскостей её граней (их ровно три независимых, прочие проходят через ту же точку), рёбра — отрезки
    /// между новыми вершинами. Топология та же; ребро, сменившее направление (грань исчезла бы), — отказ: тогда прежний
    /// путь. Результат — ExactAnalytic, проверка B-rep и сетки, аналитический объём = объёму по сетке.
    /// </summary>
    static TSolid? TweakPlanar(TSolid body, IReadOnlyList<Vector3d> probes, Vector3d neutralPoint, Vector3d m, double degrees, ref string? why)
    {
        var faces = body.Faces.ToList();
        static Geometry.Ssi.Plane? PlaneOf(TFace f) => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)) as Geometry.Ssi.Plane;
        // грань под точкой: плоская, точка на ней (до 1e-6 мм), внутри её контуров
        var picks = new Dictionary<int, (Vector3d P, Vector3d N)>();
        double a = degrees * Math.PI / 180;
        Vector3d centre = Centre(body);
        var cyls = new List<(int Face, Vector3d Probe)>();
        foreach (var probe in probes)
        {
            int hit = -1;
            // KRN-07 (07.6): цилиндрическая грань с осью вдоль направления вытяжки — конус (DraftCylinders)
            if (!CylDraftOff)
                for (int i = 0; i < faces.Count; i++)
                    if (Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(faces[i])) is Geometry.Ssi.Cylinder cy
                        && Math.Abs(DistToCylinder(cy, probe)) <= 1e-6 && MeshDistance(faces[i], probe) <= Math.Max(1e-2, 0.03 * cy.R))
                    { if (!cyls.Any(c => c.Face == i)) cyls.Add((i, probe)); hit = -2; break; }
            if (hit == -2) continue;
            for (int i = 0; i < faces.Count; i++)
            {
                if (PlaneOf(faces[i]) is not { } pl) continue;
                var n = pl.N.Normalized();
                if (Math.Abs(Vector3d.Dot(probe - pl.P, n)) > 1e-6) continue;
                if (MeshDistance(faces[i], probe) > 1e-4) continue;
                if (hit >= 0) { why = "the point lies on several faces"; return null; }
                hit = i;
            }
            if (hit < 0) { why = "no planar face at a given point"; return null; }
            if (picks.ContainsKey(hit)) continue;
            var f = faces[hit]; var pf = PlaneOf(f)!;
            if (PlanarTweak.OutwardNormal(f) is not { } nOut) { why = "no outward normal"; return null; }
            var axis = Vector3d.Cross(nOut, m);
            if (axis.LengthSquared < 1e-12) { why = "a face is parallel to the neutral plane"; return null; }
            axis = axis.Normalized();
            var p0 = LineOfPlanes(probe, nOut, neutralPoint, m, axis, probe);
            Vector3d side = Vector3d.Cross(axis, nOut).Normalized();
            double bodySide = Vector3d.Dot(centre - neutralPoint, m);
            if (Vector3d.Dot(side, m) * bodySide < 0) side = -side;
            picks[hit] = (p0, (nOut * Math.Cos(a) + side * Math.Sin(a)).Normalized());
        }
        if (picks.Count == 0 && cyls.Count == 0) { why = "no faces"; return null; }

        var res = picks.Count == 0 ? body : PlanarTweak.Apply(body, (i, f) => picks.TryGetValue(i, out var q) ? q : null, ref why);
        if (res == null || cyls.Count == 0) return res;
        return DraftCylinders(res, cyls.Select(c => c.Face).ToList(), neutralPoint, m, a, ref why);
    }

    /// <summary>Мутация P3D_DRAFTCYL=0: цилиндрические грани уклону не поддаются (прежде).</summary>
    static readonly bool CylDraftOff = Environment.GetEnvironmentVariable("P3D_DRAFTCYL") == "0";

    static double DistToCylinder(Geometry.Ssi.Cylinder c, Vector3d p)
    {
        var w = c.Axis.Normalized(); var d = p - c.P; return (d - w * Vector3d.Dot(d, w)).Length - c.R;
    }

    /// <summary>
    /// KRN-07 (07.6): УКЛОН ЦИЛИНДРИЧЕСКОЙ ГРАНИ — КОНУС. Грань-цилиндр с осью вдоль нормали нейтральной плоскости,
    /// ограниченная только замкнутыми окружностями в плоскостях поперёк оси (боковая грань бобышки, стенка отверстия):
    /// радиус на высоте h от нейтрали — r + κ·Δ(h), Δ(h) = −tg α·h·s (s — сторона самой грани от нейтрали; грань уходит
    /// внутрь материала по мере удаления от нейтрали), κ = +1 у выпуклой (материал внутри), −1 у вогнутой. Поверхность —
    /// тело вращения отрезка (конус), окружности — те же плоскости и центры с новыми радиусами; соседние грани — плоскости
    /// поперёк оси, их форма та же. Иное — отказ (тогда прежний путь не применим: цилиндр уклону не поддаётся).
    /// </summary>
    static TSolid? DraftCylinders(TSolid body, List<int> idx, Vector3d neutralPoint, Vector3d m, double a, ref string? why)
    {
        var clone = TopologyClone.Deep(body);
        var faces = body.Faces.ToList(); var cf = clone.Faces.ToList();
        var touched = new HashSet<TFace>(ReferenceEqualityComparer.Instance);
        foreach (int i in idx)
        {
            if (Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(faces[i])) is not Geometry.Ssi.Cylinder cy) { why = "not a cylinder"; return null; }
            var w = cy.Axis.Normalized();
            if (Math.Abs(Math.Abs(Vector3d.Dot(w, m)) - 1) > 1e-9) { why = "the cylinder axis is not along the pull direction"; return null; }
            var f = cf[i];
            var arcs = f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge).Distinct().ToList();
            if (arcs.Count != 2 || arcs.Any(e => e.Curve is not ArcCurve3 ac || !ReferenceEquals(e.V0, e.V1) || Math.Abs(Math.Abs(Vector3d.Dot(ac.Frame.W.Normalized(), w)) - 1) > 1e-9
                                              || Math.Abs(ac.Radius - cy.R) > 1e-9 * Math.Max(1, cy.R)))
            { why = "the cylindrical face is not bounded by two full circles across its axis"; return null; }
            // выпуклость: наружная нормаль грани против направления от оси
            var src = GeometryAudit.AuthoritativeSurface(faces[i])!;
            var q0 = ((ArcCurve3)arcs[0].Curve).PointAt(((ArcCurve3)arcs[0].Curve).T0 + 0.3);
            Vector3d nq;
            if (faces[i].Surface is FacetedSurface fs && fs.Facets.Count > 0)
            {
                var best = fs.Facets.ReadView.OrderBy(t => ((t.A + t.B + t.C) / 3 - q0).LengthSquared).First();
                nq = Vector3d.Cross(best.B - best.A, best.C - best.A) * (faces[i].Reversed ? -1 : 1);
            }
            else nq = src.NormalAt(src.ProjectToUV(q0)) * (faces[i].Reversed ? -1 : 1);
            var rad = q0 - cy.P; rad -= w * Vector3d.Dot(rad, w);
            bool convex = Vector3d.Dot(nq, rad) > 0;
            double H(Vector3d x) => Vector3d.Dot(x - neutralPoint, m);
            var c0 = ((ArcCurve3)arcs[0].Curve).Frame.Origin; var c1 = ((ArcCurve3)arcs[1].Curve).Frame.Origin;
            double hm = (H(c0) + H(c1)) / 2;
            double sgn = Math.Abs(hm) > 1e-9 ? Math.Sign(hm) : Math.Sign(Vector3d.Dot(Centre(body) - neutralPoint, m));
            double R(Vector3d x) => cy.R + (convex ? 1 : -1) * (-Math.Tan(a) * H(x) * sgn);
            foreach (var e in arcs)
            {
                var ac = (ArcCurve3)e.Curve; double r = R(ac.Frame.Origin);
                if (!(r > 1e-6)) { why = "the cone would close before the end of the face"; return null; }
                var na = new ArcCurve3(ac.Frame, r, ac.A0, ac.A0 + ac.Sweep);
                e.Curve = na; e.PolylineCache = null; e.V0.P = na.PointAt(na.T0);
            }
            // конус: отрезок образующей в полуплоскости угла 0 и ось
            double t0 = Vector3d.Dot(c0 - cy.P, w), t1 = Vector3d.Dot(c1 - cy.P, w);
            var u = Vector3d.Cross(w, Math.Abs(w.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
            var axisO = cy.P;
            var gen = new LineCurve3(axisO + w * Math.Min(t0, t1) + u * R(axisO + w * Math.Min(t0, t1)), axisO + w * Math.Max(t0, t1) + u * R(axisO + w * Math.Max(t0, t1)));
            var cone = new RevolvedSurface(gen, new Frame3(axisO, u, Vector3d.Cross(w, u)), 0, 2 * Math.PI);
            f.Surface = cone;
            // сторона: нормаль конуса в точке грани против желаемой наружной (выпуклая — от оси, вогнутая — к оси)
            var qp = gen.PointAt(0.5); var nr = qp - axisO; nr -= w * Vector3d.Dot(nr, w);
            var nc = cone.NormalAt(cone.ProjectToUV(qp));
            f.Reversed = Vector3d.Dot(nc, nr) * (convex ? 1 : -1) < 0;
            touched.Add(f);
            foreach (var e in arcs) foreach (var ce in e.CoEdges) if (ce.Loop?.Face is { } nb) touched.Add(nb);
        }
        foreach (var f in touched)
        {
            if (f.Surface is FacetedSurface fs2)
            {
                if (fs2.Source is not PlaneSurface || PlanarTweak.OutwardNormal(f) is not { } no || Geometry.ExactEdges.ShapeOf(fs2.Source) is not Geometry.Ssi.Plane pl)
                { why = "a face next to the drafted cylinder is not planar"; return null; }
                f.Surface = new PlaneSurface(Frame3.FromNormal(pl.P, no)); f.Reversed = false;
            }
            else if (f.Surface is not (PlaneSurface or RevolvedSurface)) { why = "a face next to the drafted cylinder is not planar"; return null; }
            f.InteriorHint = null;
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) { ce.PCurve = null; ce.PCurveDeviation = 0; }
        }
        clone.Exact = null; clone.QExact = null; clone.DisplayMeshKey = null;
        var wl = new List<string>();
        if (Analytic.PrismBuild.Finish(clone, wl) > 0) { why = "faces not meshed: " + string.Join(" / ", wl.Take(2)); return null; }
        clone.Guarantee = GuaranteeLevel.ExactAnalytic;
        var vr = SolidValidator.Validate(clone);
        if (!vr.IsValid) { why = "result invalid: " + vr.Summary() + " " + string.Join(" / ", vr.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2)); return null; }
        if (AnalyticVolume.Of(clone) is not { } vol || vol <= 0) { why = "no analytic volume"; return null; }
        if (BooleanOp.VolumeVsFacets(clone) is { } vf && !vf.Agree) { why = $"volume {vf.Va:G8} vs facets {vf.Vf:G8}"; return null; }
        // KRN-01 (01.5): происхождение — та же грань (порядок клона), изменённая, если её форма или края сдвинулись
        if (!PlanarTweak.ProvOff)
        {
            var pv = new Provenance { Operation = "Draft" };
            var srcFaces = body.Faces.ToList(); var resFaces = clone.Faces.ToList();
            for (int k = 0; k < resFaces.Count; k++) pv.Faces.Add(new FaceProvenance(resFaces[k], srcFaces[k], touched.Contains(resFaces[k]) ? FaceOrigin.Modified : FaceOrigin.Kept));
            clone.SetProvenance(pv);
        }
        return clone;
    }

    static double MeshDistance(TFace f, Vector3d p)
    {
        var mb = new Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb); var md = new Tess.MeshData(); mb.FillInto(md);
        double best = double.MaxValue;
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
            best = Math.Min(best, (ClosestOnTriangle(p, mb.PositionsD[md.Indices[i]], mb.PositionsD[md.Indices[i + 1]], mb.PositionsD[md.Indices[i + 2]]) - p).Length);
        return best;
    }

    /// <summary>Середина габаритов тела — грубая, но достаточная для выбора стороны.</summary>
    private static Vector3d Centre(TSolid body)
    {
        Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
        foreach (var v in body.Vertices) { lo = Vector3d.ComponentMin(lo, v.P); hi = Vector3d.ComponentMax(hi, v.P); }
        return (lo + hi) * 0.5;
    }

    /// <summary>Ближайшая точка треугольника.</summary>
    private static Vector3d ClosestOnTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d n = Vector3d.Cross(b - a, c - a);
        if (n.LengthSquared < 1e-24) return a;
        n = n.Normalized();
        Vector3d q = p - n * Vector3d.Dot(p - a, n);
        if (Vector3d.Dot(Vector3d.Cross(b - a, q - a), n) >= 0 &&
            Vector3d.Dot(Vector3d.Cross(c - b, q - b), n) >= 0 &&
            Vector3d.Dot(Vector3d.Cross(a - c, q - c), n) >= 0) return q;

        Vector3d Seg(Vector3d s, Vector3d e)
        {
            Vector3d d = e - s;
            double l2 = d.LengthSquared;
            double t = l2 < 1e-30 ? 0 : Math.Clamp(Vector3d.Dot(p - s, d) / l2, 0, 1);
            return s + d * t;
        }
        Vector3d r1 = Seg(a, b), r2 = Seg(b, c), r3 = Seg(c, a);
        double d1 = (r1 - p).LengthSquared, d2 = (r2 - p).LengthSquared, d3 = (r3 - p).LengthSquared;
        return d1 <= d2 && d1 <= d3 ? r1 : d2 <= d3 ? r2 : r3;
    }

    /// <summary>Точка на линии пересечения двух плоскостей, ближайшая к near.</summary>
    private static Vector3d LineOfPlanes(Vector3d p1, Vector3d n1, Vector3d p2, Vector3d n2, Vector3d dir, Vector3d near)
    {
        // Решается в плоскости, перпендикулярной dir: две прямые пересекаются в одной точке.
        Vector3d u = Vector3d.Cross(n1, dir).Normalized();
        Vector3d v = Vector3d.Cross(n2, dir).Normalized();
        double d1 = Vector3d.Dot(p1 - near, n1), d2 = Vector3d.Dot(p2 - near, n2);
        double a11 = Vector3d.Dot(u, n1), a12 = Vector3d.Dot(v, n1);
        double a21 = Vector3d.Dot(u, n2), a22 = Vector3d.Dot(v, n2);
        double det = a11 * a22 - a12 * a21;
        if (Math.Abs(det) < 1e-12) return near;
        double s = (d1 * a22 - d2 * a12) / det;
        double t = (a11 * d2 - a21 * d1) / det;
        return near + u * s + v * t;
    }

    private static double Extent(TSolid s)
    {
        var md = Tess.Tessellator.Tessellate(s);
        Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
        foreach (var v in md.Vertices)
        {
            var q = new Vector3d(v.P.X, v.P.Y, v.P.Z);
            lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q);
        }
        return (hi - lo).Length;
    }

    private static TSolid HalfSpace(Vector3d at, Vector3d n, double size)
    {
        Vector3d u = Math.Abs(n.X) < 0.9 ? Vector3d.Cross(n, Vector3d.UnitX).Normalized() : Vector3d.Cross(n, Vector3d.UnitY).Normalized();
        Vector3d v = Vector3d.Cross(n, u).Normalized();
        var frame = new Frame3(at, u, v);
        var pts = new[] { at - u * size - v * size, at + u * size - v * size, at + u * size + v * size, at - u * size + v * size };
        var curves = new List<ICurve3>
        {
            new LineCurve3(pts[0], pts[1]), new LineCurve3(pts[1], pts[2]),
            new LineCurve3(pts[2], pts[3]), new LineCurve3(pts[3], pts[0])
        };
        return ExtrudeOp.Build(ProfileBuilder.Build(frame, curves), n * size);
    }
}
