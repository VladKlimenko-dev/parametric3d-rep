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
            // KRN-07 (07.6): стенки со скруглёнными вертикальными рёбрами — плоскости и касательные цилиндры (конусы)
            if (TangentFaces(bodies[0], onFaces) is { } sel)
            {
                string? whyT = null;
                var dt = DirectRound.DraftSolid(bodies[0], sel, neutralPoint, m, degrees * Math.PI / 180, out int prop, ref whyT);
                if (dt != null)
                {
                    bodies[0] = dt;
                    warnings.Add($"Draft: {degrees:0.###}°, {onFaces.Count} face(s) drafted" + (prop > 0 ? $" + {prop} tangent face(s)" : "") + " (exact: planes and cones).");
                    return true;
                }
                why += " | tangent draft: " + whyT;
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

    // ============================================================ k112: уклон по ребру-шарниру

    /// <summary>Мутация P3D_DRAFTHINGE=0: шарнир-ребро не учитывается (уклон — только от нейтральной плоскости, прежде).</summary>
    public static readonly bool HingeOff = Environment.GetEnvironmentVariable("P3D_DRAFTHINGE") == "0";

    /// <summary>
    /// k112: ШАРНИР УКЛОНА. Плоская грань поворачивается вокруг своего прямого ребра (оно неподвижно), цилиндрическая
    /// становится конусом, у которого неподвижна окружность-шарнир. M — направление в грани от шарнира к смещаемому ребру
    /// (у цилиндра — вдоль оси), NOut — наружная нормаль грани у шарнира. Положительный угол — смещаемое ребро уходит
    /// внутрь материала (MoveDir в его середине), отрицательный — наружу. Угол к соседней грани (через шарнир) или к
    /// опорной плоскости — между направлением M (повёрнутым) и направлением от шарнира в соседней грани (RefIn).
    /// </summary>
    public sealed class HingeInfo
    {
        public bool Cone;
        public int FaceIndex;
        public Vector3d HingeP, HingeDir, M, NOut, MoveAt, MoveDir;
        /// <summary>Концы шарнира (у окружности — точка в середине).</summary>
        public Vector3d HingeA, HingeB;
        public double Lever;
        public Vector3d? RefIn;
        public Vector3d ConeCentre;

        /// <summary>Направление в грани от шарнира после поворота на deg.</summary>
        public Vector3d MAt(double deg) { double a = deg * Math.PI / 180; return (M * Math.Cos(a) - NOut * Math.Sin(a)).Normalized(); }
        public Vector3d NAt(double deg) { double a = deg * Math.PI / 180; return (NOut * Math.Cos(a) + M * Math.Sin(a)).Normalized(); }

        /// <summary>Направление от шарнира в опорной плоскости (refN) или в соседней грани (null — RefIn).</summary>
        public Vector3d? RefDir(Vector3d? refN)
        {
            if (refN is not { } w || w.LengthSquared < 1e-18) return RefIn;
            var r = Vector3d.Cross(w.Normalized(), HingeDir);
            if (r.LengthSquared < 1e-12) return null;      // шарнир поперёк опорной плоскости — угол не определён
            r = r.Normalized();
            var toward = RefIn ?? -M;
            return Vector3d.Dot(r, toward) >= 0 ? r : -r;
        }

        /// <summary>Угол (°) между гранью, повёрнутой на deg, и соседней гранью / опорной плоскостью; null — не определён.</summary>
        public double? AngleTo(double deg, Vector3d? refN = null)
        {
            if (RefDir(refN) is not { } r) return null;
            return Math.Acos(Math.Clamp(Vector3d.Dot(MAt(deg), r), -1, 1)) * 180 / Math.PI;
        }

        /// <summary>Поворот (°), при котором угол к соседней грани / опорной плоскости станет target; ближайший к нулю.</summary>
        public double? Solve(double target, Vector3d? refN = null)
        {
            if (AngleTo(0, refN) is not { } a0) return null;
            if (Math.Abs(a0 - target) < 1e-12) return 0;
            double F(double d) => AngleTo(d, refN)!.Value - target;
            double? best = null;
            const double lim = 89.5, step = 0.25;
            for (double lo = -lim; lo < lim - 1e-12; lo += step)
            {
                double hi = Math.Min(lim, lo + step), flo = F(lo), fhi = F(hi);
                if (flo == 0) { if (best == null || Math.Abs(lo) < Math.Abs(best.Value)) best = lo; continue; }
                if (flo * fhi > 0) continue;
                double x0 = lo, x1 = hi, f0 = flo;
                for (int it = 0; it < 80; it++)
                {
                    double xm = (x0 + x1) / 2, fm = F(xm);
                    if (f0 * fm <= 0) x1 = xm; else { x0 = xm; f0 = fm; }
                }
                double root = (x0 + x1) / 2;
                if (best == null || Math.Abs(root) < Math.Abs(best.Value)) best = root;
            }
            return best;
        }
    }

    static Vector3d OutAt(TFace f, Vector3d p)
    {
        if (f.Surface is FacetedSurface fs && fs.Facets.Count > 0)
        {
            var best = fs.Facets.ReadView.OrderBy(t => (ClosestOnTriangle(p, t.A, t.B, t.C) - p).LengthSquared).First();
            return (Vector3d.Cross(best.B - best.A, best.C - best.A) * (f.Reversed ? -1 : 1)).Normalized();
        }
        var src = GeometryAudit.AuthoritativeSurface(f) ?? f.Surface;
        return (src.NormalAt(src.ProjectToUV(p)) * (f.Reversed ? -1 : 1)).Normalized();
    }

    /// <summary>Грань под точкой: плоская или цилиндрическая; -1 — нет.</summary>
    static int FaceAt(List<TFace> faces, Vector3d probe)
    {
        for (int i = 0; i < faces.Count; i++)
        {
            var sh = Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(faces[i]));
            if (sh is Geometry.Ssi.Plane pl)
            {
                if (Math.Abs(Vector3d.Dot(probe - pl.P, pl.N.Normalized())) > 1e-6 || MeshDistance(faces[i], probe) > 1e-4) continue;
                return i;
            }
            if (sh is Geometry.Ssi.Cylinder cy && Math.Abs(DistToCylinder(cy, probe)) <= 1e-6 && MeshDistance(faces[i], probe) <= Math.Max(1e-2, 0.03 * cy.R)) return i;
        }
        return -1;
    }

    /// <summary>Направление от точки шарнира в соседней грани (поперёк шарнира, в касательной плоскости соседней грани).</summary>
    static Vector3d? InFace(TFace? adj, Vector3d at, Vector3d hingeDir, double scale)
    {
        if (adj == null) return null;
        var n = adj.Surface is FacetedSurface || GeometryAudit.AuthoritativeSurface(adj) is { IsPlanar: true } ? (PlanarTweak.OutwardNormal(adj) ?? OutAt(adj, at)) : OutAt(adj, at);
        var r = Vector3d.Cross(n, hingeDir);
        if (r.LengthSquared < 1e-12) return null;
        r = r.Normalized();
        double eps = Math.Max(1e-4, 0.02 * scale);
        return MeshDistance(adj, at + r * eps) <= MeshDistance(adj, at - r * eps) ? r : -r;
    }

    static TEdge? Edge(List<TSolid> bodies, EdgeRef r) => FilletOp.Resolve(bodies, r, out _);

    /// <summary>k112: шарнир уклона грани под probe; null — why.</summary>
    public static HingeInfo? Hinge(List<TSolid> bodies, Vector3d probe, EdgeRef hinge, EdgeRef? moving, out string? why)
    {
        why = null;
        if (bodies.Count == 0) { why = "no body"; return null; }
        var body = bodies[0];
        var faces = body.Faces.ToList();
        int fi = FaceAt(faces, probe);
        if (fi < 0) { why = "the drafted face must be planar or cylindrical"; return null; }
        var face = faces[fi];
        if (Edge(bodies, hinge) is not { } he) { why = "the fixed edge is not found"; return null; }
        if (!ReferenceEquals(he.FaceA, face) && !ReferenceEquals(he.FaceB, face)) { why = "the fixed edge does not belong to the drafted face"; return null; }
        TEdge? me = null;
        if (moving is { } mr)
        {
            me = Edge(bodies, mr);
            if (me == null) { why = "the moving edge is not found"; return null; }
            if (ReferenceEquals(me, he)) { why = "the moving edge is the fixed edge"; return null; }
            if (!ReferenceEquals(me.FaceA, face) && !ReferenceEquals(me.FaceB, face)) { why = "the moving edge does not belong to the drafted face"; return null; }
        }
        var adj = ReferenceEquals(he.FaceA, face) ? he.FaceB : he.FaceA;
        var info = new HingeInfo { FaceIndex = fi };
        var sh = Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(face));
        if (sh is Geometry.Ssi.Plane)
        {
            Vector3d a = he.V0.P, b = he.V1.P, mid = he.Curve.PointAt(0.5 * (he.Curve.T0 + he.Curve.T1));
            double len = (b - a).Length;
            var d = len > 1e-12 ? (b - a) / len : Vector3d.Zero;
            var off = mid - a - d * Vector3d.Dot(mid - a, d);
            if (len < 1e-9 || off.Length > 1e-7 * Math.Max(1, len)) { why = "the fixed edge of a planar face must be straight"; return null; }
            if (PlanarTweak.OutwardNormal(face) is not { } nOut) { why = "no outward normal"; return null; }
            info.HingeDir = d; info.HingeP = (a + b) / 2; info.NOut = nOut; info.HingeA = a; info.HingeB = b;
            var m = Vector3d.Cross(nOut, d).Normalized();
            Vector3d target;
            if (me != null) target = me.Curve.PointAt(0.5 * (me.Curve.T0 + me.Curve.T1));
            else
            {
                // смещаемое не задано — самая далёкая от шарнира вершина грани
                target = face.Edges.SelectMany(e => new[] { e.V0.P, e.V1.P, e.Curve.PointAt(0.5 * (e.Curve.T0 + e.Curve.T1)) })
                    .OrderByDescending(p => Math.Abs(Vector3d.Dot(p - info.HingeP, m))).First();
            }
            if (Vector3d.Dot(target - info.HingeP, m) < 0) m = -m;
            info.M = m; info.Lever = Vector3d.Dot(target - info.HingeP, m);
            if (info.Lever < 1e-6) { why = "the moving edge lies on the fixed edge line"; return null; }
            info.MoveAt = target; info.MoveDir = -nOut;
            info.RefIn = InFace(adj, info.HingeP, d, info.Lever);
            return info;
        }
        if (sh is Geometry.Ssi.Cylinder cy)
        {
            var w = cy.Axis.Normalized();
            var arcs = face.Edges.Where(e => e.Curve is ArcCurve3 ac && ReferenceEquals(e.V0, e.V1) && Math.Abs(Math.Abs(Vector3d.Dot(ac.Frame.W.Normalized(), w)) - 1) < 1e-9).ToList();
            if (he.Curve is not ArcCurve3 ha || !arcs.Contains(he)) { why = "the fixed edge of a cylindrical face must be one of its end circles"; return null; }
            var other = arcs.Where(e => !ReferenceEquals(e, he)).ToList();
            if (other.Count != 1) { why = "the cylindrical face must be bounded by two full circles"; return null; }
            if (me != null && !ReferenceEquals(me, other[0])) { why = "the moving edge of a cylindrical face must be its other end circle"; return null; }
            var oa = (ArcCurve3)other[0].Curve;
            var c0 = ha.Frame.Origin; var c1 = oa.Frame.Origin;
            var m = Vector3d.Dot(c1 - c0, w) >= 0 ? w : -w;
            info.Cone = true; info.ConeCentre = c0; info.M = m; info.Lever = Math.Abs(Vector3d.Dot(c1 - c0, w));
            if (info.Lever < 1e-6) { why = "the end circles coincide"; return null; }
            // направления — в середине рёбер (k112: у кривой грани направление зависит от места)
            info.HingeP = ha.PointAt(0.5); info.HingeDir = ha.TangentAt(0.5).Normalized(); info.HingeA = info.HingeB = info.HingeP;
            info.NOut = OutAt(face, info.HingeP);
            info.NOut = (info.NOut - m * Vector3d.Dot(info.NOut, m)).Normalized();
            info.MoveAt = oa.PointAt(0.5);
            var no = OutAt(face, info.MoveAt); no = (no - m * Vector3d.Dot(no, m)).Normalized();
            info.MoveDir = -no;
            info.RefIn = InFace(adj, info.HingeP, info.HingeDir, cy.R);
            return info;
        }
        why = "the drafted face must be planar or cylindrical";
        return null;
    }

    /// <summary>k112: уклон по шарниру на degrees (&gt; 0 — смещаемое ребро внутрь материала, &lt; 0 — наружу).</summary>
    public static bool ApplyHinged(List<TSolid> bodies, HingeInfo h, double degrees, List<string> warnings)
    {
        if (Math.Abs(degrees) < 1e-9) { warnings.Add("Draft: the angle must not be zero."); return false; }
        if (Math.Abs(degrees) >= 89.9) { warnings.Add("Draft: the angle must be less than 90°."); return false; }
        string? why = null;
        if (h.Cone)
        {
            var r = DraftCylinders(bodies[0], new List<int> { h.FaceIndex }, h.ConeCentre, h.M, degrees * Math.PI / 180, ref why);
            if (r == null) { warnings.Add("Draft: the cone could not be built: " + why + "."); return false; }
            bodies[0] = r;
            warnings.Add($"Draft: {degrees:0.###}° about the fixed circle (exact cone).");
            return true;
        }
        var nNew = h.NAt(degrees);
        var t = PlanarTweak.Apply(bodies[0], (i, f) => i == h.FaceIndex ? (h.HingeP, nNew) : null, ref why);
        if (t != null)
        {
            bodies[0] = t;
            warnings.Add($"Draft: {degrees:0.###}° about the fixed edge (exact plane replacement).");
            return true;
        }
        if (degrees < 0) { warnings.Add($"Draft: an outward draft about an edge is built only by plane replacement, which declined: {why}."); return false; }
        BooleanOp.Subtract(bodies, HalfSpace(h.HingeP, nNew, Extent(bodies[0]) * 4), warnings);
        if (bodies.Count == 0) { warnings.Add("Draft: the angle removes the whole body."); return false; }
        warnings.Add($"Draft: {degrees:0.###}° about the fixed edge (half-space cut: {why}).");
        return true;
    }

    /// <summary>Грани под точками (плоские и цилиндрические, точка на поверхности и на сетке грани); null — не все найдены.</summary>
    static HashSet<TFace>? TangentFaces(TSolid body, IReadOnlyList<Vector3d> probes)
    {
        var res = new HashSet<TFace>();
        foreach (var q in probes)
        {
            TFace? hit = null;
            foreach (var f in body.Faces)
            {
                var su = GeometryAudit.AuthoritativeSurface(f);
                if (su == null || (su.PointAt(su.ProjectToUV(q)) - q).Length > 1e-6) continue;
                if (MeshDistance(f, q) > 1e-2) continue;
                hit = f; break;
            }
            if (hit == null) return null;
            res.Add(hit);
        }
        return res;
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
    /// <summary>Мутация P3D_DRAFTSEAM=0: цилиндр со швом уклону не поддаётся (k111 и прежде).</summary>
    static readonly bool SeamOff = Environment.GetEnvironmentVariable("P3D_DRAFTSEAM") == "0";

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
            var allE = f.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge).Distinct().ToList();
            // k112: шов цилиндра (отрезок вдоль оси между окружностями) — допускается, становится образующей конуса
            var seams = SeamOff ? new List<TEdge>() : allE.Where(e => e.Curve is LineCurve3 && !ReferenceEquals(e.V0, e.V1)
                && Math.Abs(Math.Abs(Vector3d.Dot((e.V1.P - e.V0.P).Normalized(), w)) - 1) < 1e-9).ToList();
            var arcs = allE.Except(seams).ToList();
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
            foreach (var e in seams) { e.Curve = new LineCurve3(e.V0.P, e.V1.P); e.PolylineCache = null; }
            // конус: отрезок образующей в полуплоскости угла 0 и ось (есть шов — угол 0 на шве)
            double t0 = Vector3d.Dot(c0 - cy.P, w), t1 = Vector3d.Dot(c1 - cy.P, w);
            var u = Vector3d.Cross(w, Math.Abs(w.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
            if (seams.Count > 0) { var sr = seams[0].V0.P - cy.P; sr -= w * Vector3d.Dot(sr, w); if (sr.LengthSquared > 1e-18) u = sr.Normalized(); }
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
