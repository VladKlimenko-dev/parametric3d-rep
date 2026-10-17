using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

public static partial class DirectRound
{
    /// <summary>Мутация P3D_OFFSETSOLID=0: смещение тела с кривыми гранями (шар, тор, сопряжения) не строится (прежний отказ).</summary>
    internal static readonly bool OffsetSolidOff = Environment.GetEnvironmentVariable("P3D_OFFSETSOLID") == "0";
    public static long OffsetSolids;

    /// <summary>
    /// KRN-07 (07.5): СМЕЩЕНИЕ ТЕЛА БЕЗ СМЕНЫ ТОПОЛОГИИ — каждая грань сдвигается вдоль наружной нормали на d(грань):
    /// плоскость — параллельно, цилиндр — радиус R ± d (та же ось), шар и тор — радиус образующей окружности ± d (та же ось
    /// вращения и центр образующей). Вершина — решение n_i·(P′ − V) = d_i по различным нормалям её граней (у гладкой
    /// вершины — одна нормаль, P′ = V + d·n); ребро: отрезок — между новыми вершинами, дуга — окружность через три смещённые
    /// точки (параметризация сохраняется). Каждая новая точка ПРОВЕРЯЕТСЯ: лежит на всех смещённых поверхностях своих граней
    /// (до 1e-9 отн.) — иначе отказ (смещение не той формы, ребро исчезло бы и т. п.). Тело собирается заново, проверка
    /// годности, объём — аналитический и по фасетам. null — отказ (причина в why).
    /// </summary>
    /// <summary>Расстояние от точки до треугольника (для проверок).</summary>
    public static double DistToTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c) => (ClosestOnTri(p, a, b, c) - p).Length;
    /// <summary>Ближайшая к p точка треугольника abc (Эриксон, «Real-Time Collision Detection», 5.1.5).</summary>
    static Vector3d ClosestOnTri(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        double d1 = Vector3d.Dot(ab, ap), d2 = Vector3d.Dot(ac, ap); if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; double d3 = Vector3d.Dot(ab, bp), d4 = Vector3d.Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; double d5 = Vector3d.Dot(ab, cp), d6 = Vector3d.Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        double den = 1 / (va + vb + vc); return a + ab * (vb * den) + ac * (vc * den);
    }
    static readonly bool NearTanOff = Environment.GetEnvironmentVariable("P3D_OFFSETNEARTAN") == "0";
    static readonly bool AcrossOff = Environment.GetEnvironmentVariable("P3D_OFFSETACROSS") == "0";
    static readonly bool LineOnOff = Environment.GetEnvironmentVariable("P3D_OFFSETLINEON") == "0";
    static readonly bool EllOff = Environment.GetEnvironmentVariable("P3D_OFFSETELLIPSE") == "0";
    static readonly bool SeamVOff = Environment.GetEnvironmentVariable("P3D_OFFSETSEAMV") == "0";
    static readonly bool SlideDirOff = Environment.GetEnvironmentVariable("P3D_OFFSETSLIDEDIR") == "0";
    static readonly bool ConeExtOff = Environment.GetEnvironmentVariable("P3D_OFFSETCONEEXT") == "0";
    static readonly bool OffsetProcOff = Environment.GetEnvironmentVariable("P3D_OFFSETPROC") == "0";
    static readonly bool OffsetConflictOff = Environment.GetEnvironmentVariable("P3D_OFFSETSLIDE") == "0";
    public static long OffsetSlides;
    static readonly bool OffsetNewtonOff = Environment.GetEnvironmentVariable("P3D_OFFSETNEWTON") == "0";
    internal static TSolid? OffsetSolid(TSolid body, Func<TFace, double> offset, string? tag, ref string? why)
    {
        if (OffsetSolidOff) { why = "offset of curved faces is off"; return null; }
        if (!SharpenOff)
        {
            // u12b: сопряжения, чей радиус при смещении исчезает (|d| ≥ R в сторону оси), — заострить и смещать острое тело
            double sc = 1; foreach (var v in body.Vertices) sc = Math.Max(sc, v.P.Length);
            double tl = 1e-9 * sc;
            var vanish = new HashSet<TFace>();
            foreach (var f in body.Faces) if (OffsetRadius(f, offset(f)) is { } r && r <= 1e3 * tl) vanish.Add(f);
            if (vanish.Count > 0)
            {
                var sharp = Sharpen(body, vanish, tl, out var faceOf, ref why);
                if (sharp == null) { why = $"offset: a blend radius vanishes ({why})"; return null; }
                Debug?.Invoke($"offset: sharpened {vanish.Count} vanishing blend face(s)");
                return OffsetSolidCore(sharp, f => faceOf.TryGetValue(f, out var o) ? offset(o) : offset(f), tag, ref why);
            }
        }
        return OffsetSolidCore(body, offset, tag, ref why);
    }

    static TSolid? OffsetSolidCore(TSolid body, Func<TFace, double> offset, string? tag, ref string? why)
    {
        var newSurf = new Dictionary<TFace, (ISurface3 S, bool Rev, double D)>();
        double scale = 1;
        foreach (var v in body.Vertices) scale = Math.Max(scale, v.P.Length);
        double tol = 1e-9 * scale;

        Vector3d Out(TFace f, Vector3d x)
        {
            var su = GeometryAudit.AuthoritativeSurface(f)!;
            var n = su.NormalAt(su.ProjectToUV(x)).Normalized();
            return RevOf(f) ? -n : n;
        }
        bool RevOf(TFace f)
        {
            var src = GeometryAudit.AuthoritativeSurface(f)!;
            bool rev = f.Reversed;
            if (!ReferenceEquals(src, f.Surface) && f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P is Vector3d sp
                && Vector3d.Dot(src.NormalAt(src.ProjectToUV(sp)) * (f.Reversed ? -1 : 1), f.NormalAt(sp)) < 0)
                rev = !rev;
            return rev;
        }
        bool OnSurf(ISurface3 s, Vector3d x) => (s.PointAt(s.ProjectToUV(x)) - x).Length <= tol;

        // поверхности
        foreach (var f in body.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src == null) { why = "face without an exact surface"; return null; }
            double d = offset(f);
            var probe = f.Loops.SelectMany(l => l.CoEdges).FirstOrDefault()?.Start.P ?? f.InteriorHint ?? src.PointAt(new Vector2d(0.3, 0.3));
            var no = Out(f, probe);
            ISurface3? ns = null;
            switch (src)
            {
                case { IsPlanar: true }:
                    ns = new PlaneSurface(Frame3.FromNormal(probe + no * d, no)); break;
                case CylinderSurface cs:
                {
                    var w = cs.Axis.W.Normalized(); var rad = probe - cs.Axis.Origin; rad -= w * Vector3d.Dot(rad, w);
                    double R = cs.Radius + (Vector3d.Dot(rad, no) > 0 ? d : -d);
                    if (!(R > tol)) { why = "offset: a cylinder radius vanishes"; return null; }
                    ns = new CylinderSurface(cs.Axis, R); break;
                }
                case ExtrudedSurface { Base: ArcCurve3 ba } ex when Math.Abs(Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), ex.Dir.Normalized())) - 1) < 1e-12:
                {
                    var w = ex.Dir.Normalized(); var rad = probe - ba.Frame.Origin; rad -= w * Vector3d.Dot(rad, w);
                    double R = ba.Radius + (Vector3d.Dot(rad, no) > 0 ? d : -d);
                    if (!(R > tol)) { why = "offset: a cylinder radius vanishes"; return null; }
                    ns = new ExtrudedSurface(new ArcCurve3(ba.Frame, R, ba.A0, ba.A1), ex.Dir); break;
                }
                case RevolvedSurface { Base: LineCurve3 gl } rvl:
                {
                    // конус (цилиндр, плоское кольцо) вращения: образующая сдвигается вдоль наружной нормали (в её меридиане)
                    var sg = rvl.NormalAt(rvl.ProjectToUV(gl.PointAt(0.5))).Normalized() * (RevOf(f) ? -1 : 1);
                    var sh = sg * d;
                    var ga2 = gl.A + sh; var gb2 = gl.B + sh;
                    if (!ConeExtOff)
                    {
                        // u12b: ОБРАЗУЮЩАЯ ПРОДЛЕВАЕТСЯ. Грань смещённого тела шире сдвинутого отрезка (соседи тоже сдвинуты, их пересечение
                        // с конусом уходит за концы): за концом образующей проекция на поверхность зажималась, интеграл объёма грани
                        // считался по чужой области (Part3: фаска 15 на дуге R40, оболочка наружу 3.66 — площадь 1169 вместо 1336,
                        // объём не сходился с фасетами). Продление — на max(4|d|, длина) с каждого конца (точка у открытой грани уходит вдоль шва
                        // фаски на ~1.4|d|), не дальше оси (вершины конуса). Мутация P3D_OFFSETCONEEXT=0.
                        var ax = rvl.Axis.W.Normalized(); var ao = rvl.Axis.Origin;
                        double Rad(Vector3d q) { var r0 = q - ao; return (r0 - ax * Vector3d.Dot(r0, ax)).Length; }
                        var dir = gb2 - ga2; double len = dir.Length;
                        if (len > tol)
                        {
                            dir /= len; double ext = Math.Max(4 * Math.Abs(d), len);
                            double ra = Rad(ga2), rb = Rad(gb2), dr = (rb - ra) / len; // радиус вдоль образующей линеен (меридиан)
                            double ea = ext, eb = ext;
                            if (dr > 1e-12) ea = Math.Min(ext, 0.9 * ra / dr);   // к вершине со стороны A
                            if (dr < -1e-12) eb = Math.Min(ext, 0.9 * rb / -dr); // к вершине со стороны B
                            ga2 -= dir * ea; gb2 += dir * eb;
                        }
                    }
                    ns = new RevolvedSurface(new LineCurve3(ga2, gb2), rvl.Axis, rvl.A0, rvl.A1); break;
                }
                case RevolvedSurface { Base: ArcCurve3 ga } rv:
                {
                    // шар (центр образующей на оси) или тор: точка образующей сдвигается вдоль нормали — радиус ± d
                    var cg = ga.Frame.Origin;
                    // центр образующей «в положении» точки probe: поворот вокруг оси на угол probe
                    var su = rv; var uv = su.ProjectToUV(probe);
                    var onGen = su.PointAt(uv);
                    var axW = rv.Axis.W.Normalized(); var ao = rv.Axis.Origin;
                    var cg0 = cg - ao; double hz = Vector3d.Dot(cg0, axW); var cgr = cg0 - axW * hz; double rho = cgr.Length;
                    var pr = onGen - ao; pr -= axW * Vector3d.Dot(pr, axW);
                    var cAt = ao + axW * hz + (rho > 1e-12 && pr.Length > 1e-12 ? pr.Normalized() * rho : Vector3d.Zero);
                    double R = ga.Radius + (Vector3d.Dot(probe - cAt, no) > 0 ? d : -d);
                    if (!(R > tol)) { why = "offset: a sphere or torus radius vanishes"; return null; }
                    ns = new RevolvedSurface(new ArcCurve3(ga.Frame, R, ga.A0, ga.A1), rv.Axis, rv.A0, rv.A1); break;
                }
            }
            if (ns == null) { why = $"offset: a {src.GetType().Name} face is not supported"; return null; }
            var p2 = probe + no * d;
            if (!OnSurf(ns, p2)) { Debug?.Invoke($"offset surf {f.Tag} {src} probe {probe} d {d} moved {p2} on new at {ns.PointAt(ns.ProjectToUV(p2))}" + (ns is RevolvedSurface { Base: LineCurve3 nb } ? $" gen {nb.A}->{nb.B} orig {((RevolvedSurface)src).Base.PointAt(0)}->{((RevolvedSurface)src).Base.PointAt(1)} axis {((RevolvedSurface)src).Axis.Origin} {((RevolvedSurface)src).Axis.W}" : "")); why = $"offset: the {src.GetType().Name} offset does not carry the moved point"; return null; }
            bool rev2 = Vector3d.Dot(ns.NormalAt(ns.ProjectToUV(p2)), no) < 0;
            newSurf[f] = (ns, rev2, d);
        }

        // вершины
        var vmap = new Dictionary<TVertex, Vector3d>();
        // u12: грани вершины — по рёбрам тела (у тел после булевых список рёбер вершины бывает пуст: вершина «без граней»
        // давала отказ «нет согласованного положения»)
        var facesAtV = new Dictionary<TVertex, HashSet<TFace>>();
        foreach (var e in body.Edges)
            foreach (var vx in new[] { e.V0, e.V1 })
            {
                if (!facesAtV.TryGetValue(vx, out var hs)) facesAtV[vx] = hs = new HashSet<TFace>();
                foreach (var c in e.CoEdges) if (c.Loop?.Face is { } ff) hs.Add(ff);
            }
        List<TFace> FacesAt(TVertex v) => facesAtV.TryGetValue(v, out var hs) && hs.Count > 0 ? hs.ToList()
            : v.Edges.SelectMany(e => e.CoEdges.Select(c => c.Loop?.Face)).OfType<TFace>().Distinct().ToList();
        Vector3d? Move(Vector3d x, IReadOnlyList<TFace> fs)
        {
            var ns = new List<(Vector3d N, double D)>();
            foreach (var f in fs)
            {
                var n = Out(f, x); double d = newSurf[f].D;
                int j = ns.FindIndex(q => (q.N - n).Length < 1e-9);
                // u12b: ПОЧТИ КАСАНИЕ (нормали расходятся на 1e-4: лента после булевых касается соседа не строго) с тем же сдвигом —
                // одна строка, иначе система вырождена и вершина «без согласованного положения»; Ньютон по поверхностям затем
                // доводит точку на все грани (не довёл — отказ, как прежде). Мутация P3D_OFFSETNEARTAN=0.
                if (j < 0 && !NearTanOff) j = ns.FindIndex(q => (q.N - n).Length < 2e-4 && Math.Abs(q.D - d) <= tol);
                if (j >= 0)
                {
                    // u12: КАСАТЕЛЬНЫЕ ГРАНИ С РАЗНЫМ СДВИГОМ (открытая грань стоит, касательное к ней скругление смещается: стенка
                    // наружу) — касание теряется, точка уходит вдоль неподвижной грани до пересечения со смещённой (шов → ребро)
                    if (Math.Abs(ns[j].D - d) > tol) return OffsetConflictOff ? null : SlideSolve(x, fs, n);
                    continue;
                }
                ns.Add((n, d));
            }
            Vector3d? r = ns.Count switch
            {
                1 => x + ns[0].N * ns[0].D,
                2 => Two(ns[0], ns[1]),
                _ => Three(ns),
            };
            if (r == null) return null;
            // u12: КРИВАЯ ГРАНЬ В ВЕРШИНЕ — точное положение уточняется Ньютоном по смещённым поверхностям. Касательные
            // плоскости в старой точке дают положение точно лишь у плоскостей и при сдвиге вдоль нормали кривой грани; у
            // вершины «скругление — фаска — торец» (стенка наружу) точка уходит вдоль фаски, и цилиндр R + t её не нёс —
            // отказ «вершина без согласованного положения». Мутация P3D_OFFSETNEWTON=0.
            if (!OffsetNewtonOff && fs.Any(f => !OnSurf(newSurf[f].S, r.Value))) r = Refine(r.Value, fs);
            if (r == null) return null;
            foreach (var f in fs) if (!OnSurf(newSurf[f].S, r.Value)) return null;
            return r;
            Vector3d? Refine(Vector3d y, IReadOnlyList<TFace> faces)
            {
                // различные смещённые поверхности (одна на совпадающие)
                var surfs = new List<ISurface3>();
                foreach (var f in faces) { var sf = newSurf[f].S; if (!surfs.Any(o => ReferenceEquals(o, sf))) surfs.Add(sf); }
                for (int it = 0; it < 40; it++)
                {
                    var rows = new List<(Vector3d N, double G)>();
                    foreach (var sf in surfs)
                    {
                        var uv = sf.ProjectToUV(y); var q = sf.PointAt(uv); var n = sf.NormalAt(uv);
                        if (!(n.LengthSquared > 1e-18)) return null;
                        n.Normalize();
                        double g = Vector3d.Dot(y - q, n);
                        if (rows.Any(rw => (rw.N - n).Length < 1e-9 || (rw.N + n).Length < 1e-9)) { if (Math.Abs(g) > tol) { /* та же нормаль, другая поверхность */ } continue; }
                        rows.Add((n, g));
                    }
                    double worst = rows.Count == 0 ? 0 : rows.Max(rw => Math.Abs(rw.G));
                    if (worst <= 0.1 * tol) return y;
                    // шаг: наименьшая поправка, гасящая отклонения по нормалям (1, 2 или 3 независимые строки)
                    Vector3d step;
                    if (rows.Count == 1) step = -rows[0].N * rows[0].G;
                    else if (rows.Count == 2)
                    {
                        var (a, ga) = rows[0]; var (b, gb) = rows[1];
                        double c = Vector3d.Dot(a, b), det = 1 - c * c; if (det < 1e-12) return null;
                        double ka = (-ga + c * gb) / det, kb = (-gb + c * ga) / det;
                        step = a * ka + b * kb;
                    }
                    else
                    {
                        Vector3d? st = null;
                        for (int i0 = 0; i0 < rows.Count && st == null; i0++)
                            for (int i1 = i0 + 1; i1 < rows.Count && st == null; i1++)
                                for (int i2 = i1 + 1; i2 < rows.Count && st == null; i2++)
                                    st = Solve3(new[] { rows[i0].N, rows[i1].N, rows[i2].N }, new[] { -rows[i0].G, -rows[i1].G, -rows[i2].G });
                        if (st == null) return null;
                        step = st.Value;
                    }
                    y += step;
                    if (step.Length > 10 * Math.Max(1, Math.Abs(newSurf[faces[0]].D)) + 1) return null;
                }
                return null;
            }
            // точка на всех смещённых поверхностях, найденная от старта, сдвинутого вдоль общей касательной плоскости в сторону
            // каждой из граней (касание: первый порядок вырожден — нужен шаг поперёк); Гаусс — Ньютон с затуханием
            Vector3d? SlideSolve(Vector3d x0, IReadOnlyList<TFace> faces, Vector3d n0)
            {
                var surfs = new List<ISurface3>();
                foreach (var f in faces) { var sf = newSurf[f].S; if (!surfs.Any(o => ReferenceEquals(o, sf))) surfs.Add(sf); }
                double dmax = faces.Max(f => Math.Abs(newSurf[f].D));
                var cands = new List<Vector3d>();
                // u12b: СНАЧАЛА — В СТОРОНУ СМЕЩАЕМОЙ касательной грани. Точка скользит по неподвижной грани туда, где смещённая
                // грань её пересекает со стороны своей области; у пары «открытая плоскость — скругление R5, стенка наружу 2.82»
                // плоскость пересекает цилиндр R7.82 дважды на равном расстоянии от вершины, и сторона «по плоскости» давала
                // нижний корень (ребро не сходилось с вершиной). Мутация P3D_OFFSETSLIDEDIR=0.
                IEnumerable<TFace> order = faces;
                if (!SlideDirOff)
                    order = faces.OrderBy(f => (Out(f, x0) - n0).Length < 1e-6 && Math.Abs(Math.Abs(newSurf[f].D) - dmax) <= tol ? 0 : 1);
                var taus = new List<Vector3d>();
                if (!SlideDirOff)
                    foreach (var f in faces)
                    {
                        // направления ВНУТРЬ касательной кривой грани: шаги по её параметрам, точка остаётся на грани (по сетке)
                        if ((Out(f, x0) - n0).Length >= 1e-6 || Math.Abs(newSurf[f].D) <= tol) continue;
                        var src = GeometryAudit.AuthoritativeSurface(f); if (src == null || src.IsPlanar) continue;
                        var uv0 = src.ProjectToUV(x0); var p0 = src.PointAt(uv0);
                        double hs = Math.Max(dmax, 1e-3);
                        foreach (var (du, dv) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
                        {
                            // шаг по параметру длиной ~hs в пространстве
                            var e1 = src.PointAt(new Vector2d(uv0.X + du * 1e-4, uv0.Y + dv * 1e-4)) - p0; double l1 = e1.Length; if (l1 < 1e-12) continue;
                            double k = 1e-4 * hs / l1;
                            var pq = src.PointAt(new Vector2d(uv0.X + du * k, uv0.Y + dv * k));
                            var fake = pq + Out(f, pq) * 0; // точка на исходной поверхности
                            if (f.Surface is FacetedSurface fq && fq.Facets.Count > 0)
                            {
                                double best = double.MaxValue; foreach (var (a, b, c) in fq.Facets.ReadView) best = Math.Min(best, (ClosestOnTri(fake, a, b, c) - fake).Length);
                                if (best > 0.05 * hs + tol) continue;
                            }
                            var tq = pq - x0; tq -= n0 * Vector3d.Dot(tq, n0);
                            if (tq.LengthSquared > 1e-18) taus.Add(tq.Normalized());
                        }
                    }
                foreach (var f in order)
                {
                    // сторона грани: точка её контура подальше от x0, спроецированная на касательную плоскость
                    var pts = f.Edges.Where(e => e.Curve != null).SelectMany(e => Enumerable.Range(0, 5).Select(i => e.Curve!.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * i / 4.0))).ToList();
                    if (pts.Count == 0) continue;
                    var far = pts.OrderByDescending(q => (q - x0).LengthSquared).First();
                    var tau0 = far - x0; tau0 -= n0 * Vector3d.Dot(tau0, n0);
                    if (tau0.LengthSquared < 1e-18) continue;
                    taus.Add(tau0.Normalized());
                }
                foreach (var tau in taus)
                {
                    foreach (double sc in new[] { 1.0, 4.0, 16.0, 0.25 })
                    {
                        var y = x0 + tau * (sc * Math.Max(dmax, 1e-3)) + n0 * faces.Where(g => Math.Abs(Vector3d.Dot(Out(g, x0), n0)) < 0.999).Select(g => 0.0).DefaultIfEmpty(0).First();
                        bool ok = false;
                        for (int it = 0; it < 80; it++)
                        {
                            // строки: нормаль и отклонение каждой поверхности
                            double[,] JtJ = new double[3, 3]; double[] Jtg = new double[3]; double worst = 0;
                            foreach (var sf in surfs)
                            {
                                var uv = sf.ProjectToUV(y); var q = sf.PointAt(uv); var nn = sf.NormalAt(uv);
                                if (!(nn.LengthSquared > 1e-18)) { worst = double.NaN; break; }
                                nn.Normalize(); double g = Vector3d.Dot(y - q, nn); worst = Math.Max(worst, Math.Abs(g));
                                double[] r = { nn.X, nn.Y, nn.Z };
                                for (int a2 = 0; a2 < 3; a2++) { Jtg[a2] += r[a2] * g; for (int b2 = 0; b2 < 3; b2++) JtJ[a2, b2] += r[a2] * r[b2]; }
                            }
                            if (double.IsNaN(worst)) break;
                            if (worst <= 0.1 * tol) { ok = true; break; }
                            double lam = 1e-9 + 1e-3 * (JtJ[0, 0] + JtJ[1, 1] + JtJ[2, 2]);
                            for (int a2 = 0; a2 < 3; a2++) JtJ[a2, a2] += lam;
                            var st = Solve3(new[] { new Vector3d(JtJ[0, 0], JtJ[0, 1], JtJ[0, 2]), new Vector3d(JtJ[1, 0], JtJ[1, 1], JtJ[1, 2]), new Vector3d(JtJ[2, 0], JtJ[2, 1], JtJ[2, 2]) }, new[] { -Jtg[0], -Jtg[1], -Jtg[2] });
                            if (st == null) break;
                            y += st.Value;
                            if ((y - x0).Length > 100 * Math.Max(dmax, 1e-3) + 1) break;
                        }
                        if (ok && Vector3d.Dot(y - x0, tau) > 0 && faces.All(g => OnSurf(newSurf[g].S, y)))
                        {
                            if (SlideDirOff) { Interlocked.Increment(ref OffsetSlides); return y; }
                            if (!cands.Any(c0 => (c0 - y).Length <= tol)) cands.Add(y);
                        }
                    }
                }
                Debug?.Invoke($"slide {x0}: {string.Join("; ", cands.Select(y => $"{y} back {Score(y):G3}"))}");
                if (cands.Count == 0) return null;
                Interlocked.Increment(ref OffsetSlides);
                if (cands.Count == 1) return cands[0];
                // несколько корней — тот, что смещён ИЗ САМИХ граней: точка, возвращённая на исходную поверхность каждой
                // смещаемой грани, лежит на этой грани (по её сетке), а не на продолжении поверхности
                if (!AcrossOff)
                {
                    // u12b: СТОРОНА ПОПЕРЁК ШВА. У каждой касательной смещаемой кривой грани — направление наибольшей кривизны
                    // (поперёк шва) внутрь грани; годен корень, ушедший в эту сторону у всех таких граней (две касательные
                    // ленты у одной вершины — по своей стороне каждая). Обратная проекция путалась: в её счёт входил и законный
                    // сдвиг вдоль осей лент. Мутация P3D_OFFSETACROSS=0.
                    var across = new List<Vector3d>();
                    foreach (var g in faces)
                    {
                        if (Math.Abs(newSurf[g].D) <= tol || (Out(g, x0) - n0).Length >= 1e-6) continue;
                        if (Across(g, x0) is { } a) across.Add(a);
                    }
                    if (across.Count > 0)
                    {
                        var good = cands.Where(y => across.All(a => Vector3d.Dot(y - x0, a) > tol)).ToList();
                        if (good.Count > 0) return good.OrderBy(Score).First();
                    }
                }
                return cands.OrderBy(Score).First();
                // счёт — по касательным смещаемым граням (вдоль которых точка и скользит): у прочих граней вершины точка законно
                // уходит за прежний контур
                double Score(Vector3d y) => faces.Where(g => Math.Abs(newSurf[g].D) > tol && (Out(g, x0) - n0).Length < 1e-6).Sum(g => BackDist(g, y));
            }
            // направление поперёк шва внутрь кривой грани g в точке x: шаг по параметру с наибольшим поворотом нормали,
            // оставшийся на грани (по её сетке)
            Vector3d? Across(TFace g, Vector3d x)
            {
                var src = GeometryAudit.AuthoritativeSurface(g); if (src == null || src.IsPlanar) return null;
                var uv0 = src.ProjectToUV(x); var p0 = src.PointAt(uv0); var nn0 = src.NormalAt(uv0).Normalized();
                double hs = Math.Max(1e-3, 1e-3 * scale);
                Vector3d? best = null; double bestK = -1;
                foreach (var (du, dv) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
                {
                    var e1 = src.PointAt(new Vector2d(uv0.X + du * 1e-6, uv0.Y + dv * 1e-6)) - p0; double l1 = e1.Length; if (l1 < 1e-15) continue;
                    double k = 1e-6 * hs / l1;
                    var uvq = new Vector2d(uv0.X + du * k, uv0.Y + dv * k); var pq = src.PointAt(uvq);
                    if (g.Surface is FacetedSurface fq && fq.Facets.Count > 0)
                    {
                        double bd = double.MaxValue; foreach (var (a, b, c) in fq.Facets.ReadView) bd = Math.Min(bd, (ClosestOnTri(pq, a, b, c) - pq).Length);
                        if (bd > 0.05 * hs + 1e-3) continue;
                    }
                    double turn = (src.NormalAt(uvq).Normalized() - nn0).Length / hs;
                    if (turn > bestK) { bestK = turn; var dd = pq - p0; dd -= nn0 * Vector3d.Dot(dd, nn0); if (dd.LengthSquared > 1e-24) best = dd.Normalized(); }
                }
                return bestK > 1e-9 ? best : null;
            }
            double BackDist(TFace g, Vector3d y)
            {
                var src = GeometryAudit.AuthoritativeSurface(g);
                if (src == null || g.Surface is not FacetedSurface fsg || fsg.Facets.Count == 0) return 0;
                var q = src.PointAt(src.ProjectToUV(y));
                double best = double.MaxValue;
                foreach (var (a, b, c) in fsg.Facets.ReadView) best = Math.Min(best, (ClosestOnTri(q, a, b, c) - q).LengthSquared);
                return Math.Sqrt(best);
            }
            Vector3d? Two((Vector3d N, double D) a, (Vector3d N, double D) b)
            {
                double c = Vector3d.Dot(a.N, b.N), det = 1 - c * c;
                if (det < 1e-12) return null;
                double ka = (a.D - c * b.D) / det, kb = (b.D - c * a.D) / det;
                return x + a.N * ka + b.N * kb;
            }
            Vector3d? Three(List<(Vector3d N, double D)> q)
            {
                for (int i0 = 0; i0 < q.Count; i0++)
                    for (int i1 = i0 + 1; i1 < q.Count; i1++)
                        for (int i2 = i1 + 1; i2 < q.Count; i2++)
                            if (Solve3(new[] { q[i0].N, q[i1].N, q[i2].N }, new[] { q[i0].D + Vector3d.Dot(q[i0].N, x), q[i1].D + Vector3d.Dot(q[i1].N, x), q[i2].D + Vector3d.Dot(q[i2].N, x) }) is { } s)
                                return s;
                return null;
            }
        }
        // u12b: ВЕРШИНА НА ПРЯМОМ КАСАТЕЛЬНОМ ШВЕ (плоскость — цилиндр, одинаковый сдвиг): по касанию положение вдоль шва
        // определено лишь до √(2R·tol) (у скругления R8 — 1e-4 мм), и прямое ребро-шов не совпадало со смещёнными точками.
        // Смещённый шов — та же прямая, сдвинутая на d·n; вершина — на ней, по остальным граням (одномерный Ньютон).
        // Мутация P3D_OFFSETSEAMV=0.
        var edgesAtV = new Dictionary<TVertex, List<TEdge>>();
        foreach (var e in body.Edges) foreach (var vx in new[] { e.V0, e.V1 }) { if (!edgesAtV.TryGetValue(vx, out var le)) edgesAtV[vx] = le = new(); if (!le.Contains(e)) le.Add(e); }
        Vector3d? SeamVertex(TVertex v)
        {
            if (SeamVOff || !edgesAtV.TryGetValue(v, out var es)) return null;
            var all = FacesAt(v);
            foreach (var e in es)
            {
                if (e.Curve is not LineCurve3 l) continue;
                var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
                if (fs.Count != 2) continue;
                var na = Out(fs[0], v.P); var nb = Out(fs[1], v.P);
                if ((na - nb).Length > 1e-7 || Math.Abs(newSurf[fs[0]].D - newSurf[fs[1]].D) > tol) continue;
                if (GeometryAudit.AuthoritativeSurface(fs[0])!.IsPlanar && GeometryAudit.AuthoritativeSurface(fs[1])!.IsPlanar) continue;
                var sdir = (l.B - l.A).Normalized(); double d = newSurf[fs[0]].D;
                var others = all.Where(g => !fs.Contains(g) && !((Out(g, v.P) - na).Length < 1e-7 && Math.Abs(newSurf[g].D - d) <= tol)).ToList();
                var g0 = others.FirstOrDefault(g => Math.Abs(Vector3d.Dot(Out(g, v.P), sdir)) > 1e-3);
                if (g0 == null) continue;
                var sg = newSurf[g0].S; var y = v.P + na * d; double lam = 0; bool ok = false;
                for (int it = 0; it < 50; it++)
                {
                    var yy = y + sdir * lam; var uv = sg.ProjectToUV(yy); var q = sg.PointAt(uv); var nq = sg.NormalAt(uv).Normalized();
                    double g = Vector3d.Dot(yy - q, nq), dg = Vector3d.Dot(sdir, nq);
                    if (Math.Abs(g) <= 0.01 * tol) { ok = true; break; }
                    if (Math.Abs(dg) < 1e-6) break;
                    lam -= g / dg;
                }
                if (!ok) continue;
                var res = y + sdir * lam;
                if (all.All(g => OnSurf(newSurf[g].S, res))) return res;
                if (Debug != null) foreach (var g in all) if (newSurf[g].S is RevolvedSurface { Base: LineCurve3 bl } rvd) Debug($"   cone axis {rvd.Axis.Origin} {rvd.Axis.W} gen {bl.A}->{bl.B}, orig gen {((RevolvedSurface)GeometryAudit.AuthoritativeSurface(g)!).Base.PointAt(0)}->{((RevolvedSurface)GeometryAudit.AuthoritativeSurface(g)!).Base.PointAt(1)}");
                Debug?.Invoke($"seam vertex {v.P}: candidate {res} off {string.Join(",", all.Select(g => $"{g.Tag}:{(newSurf[g].S.PointAt(newSurf[g].S.ProjectToUV(res)) - res).Length:G3}"))}");
            }
            return null;
        }
        foreach (var v in body.Vertices)
        {
            var p = SeamVertex(v) ?? Move(v.P, FacesAt(v));
            if (p == null)
            {
                Debug?.Invoke($"offset vertex {v.P}: faces {string.Join(" | ", FacesAt(v).Select(f => $"{GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} {f.Tag} n {Out(f, v.P)} d {newSurf[f].D}"))}");
                why = "offset: a vertex has no consistent position on the offset faces"; return null;
            }
            vmap[v] = p.Value;
        }

        // процедурная смещённая кривая ребра: внутренние точки — смещением, концы — точно смещённые вершины
        ProcCurve3 ProcOffset(TEdge e, ICurve3 oc, IReadOnlyList<TFace> fs)
        {
            bool fw = (oc.PointAt(oc.T0) - e.V0.P).Length <= (oc.PointAt(oc.T0) - e.V1.P).Length;
            var a = vmap[fw ? e.V0 : e.V1]; var b = vmap[fw ? e.V1 : e.V0];
            return new ProcCurve3(t => t <= oc.T0 ? a : t >= oc.T1 ? b : Move(oc.PointAt(t), fs) ?? new Vector3d(double.NaN), oc.T0, oc.T1, false, "offset edge");
        }

        // рёбра
        var emap = new Dictionary<TEdge, ICurve3>();
        var reAngled = new HashSet<TEdge>();
        foreach (var e in body.Edges)
        {
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Count != 2 && !(fs.Count == 1 && e.CoEdges.Count == 2)) { why = "offset: an edge is not between two faces"; return null; }   // шов — одна грань
            ICurve3? nc = null;
            switch (e.Curve)
            {
                case LineCurve3 l:
                {
                    bool fw = (l.A - e.V0.P).Length <= (l.A - e.V1.P).Length;
                    var a = vmap[fw ? e.V0 : e.V1]; var b = vmap[fw ? e.V1 : e.V0];
                    nc = new LineCurve3(a, b);
                    break;
                }
                case ArcCurve3 arc:
                {
                    var pts = new[] { 0.0, 1.0 / 3, 2.0 / 3 }.Select(t => Move(arc.PointAt(t), fs)).ToList();
                    if (pts.Any(p => p == null)) { why = "offset: an arc point has no position on the offset faces"; return null; }
                    var w = arc.Frame.W.Normalized();
                    // центр — на оси дуги (окружности соосны): проекция и равные расстояния
                    var c = Circle3(pts[0]!.Value, pts[1]!.Value, pts[2]!.Value);
                    if (c == null) { why = "offset: arc points are collinear"; return null; }
                    double R = (pts[0]!.Value - c.Value).Length;
                    var u = arc.Frame.U; var vv = arc.Frame.V;
                    nc = new ArcCurve3(new Frame3(c.Value, u, vv), R, arc.A0, arc.A1);
                    if (!SlideDirOff && !arc.IsClosed && e.V0 != e.V1 && vmap.TryGetValue(e.V0, out var w0) && vmap.TryGetValue(e.V1, out var w1))
                    {
                        // u12b: концы сместились вдоль дуги (касание с неподвижной гранью стало ребром) — тот же круг, углы по вершинам
                        bool fw = (arc.PointAt(arc.A0) - e.V0.P).Length <= (arc.PointAt(arc.A0) - e.V1.P).Length;
                        var pa = fw ? w0 : w1; var pb = fw ? w1 : w0;
                        var na = (ArcCurve3)nc;
                        if (((na.PointAt(na.A0) - pa).Length > tol || (na.PointAt(na.A1) - pb).Length > tol)
                            && Math.Abs((pa - c.Value).Length - R) <= tol && Math.Abs((pb - c.Value).Length - R) <= tol)
                        {
                            double Ang(Vector3d q) { var d0 = q - c.Value; return Math.Atan2(Vector3d.Dot(d0, vv), Vector3d.Dot(d0, u)); }
                            double a0 = Ang(pa), a1 = Ang(pb), span = arc.A1 - arc.A0;
                            // ближайший к прежнему ход углов (та же сторона обхода)
                            while (a0 - arc.A0 > Math.PI) a0 -= 2 * Math.PI; while (arc.A0 - a0 > Math.PI) a0 += 2 * Math.PI;
                            while (span > 0 ? a1 < a0 : a1 > a0) a1 += span > 0 ? 2 * Math.PI : -2 * Math.PI;
                            while (span > 0 ? a1 - a0 > 2 * Math.PI : a0 - a1 > 2 * Math.PI) a1 -= span > 0 ? 2 * Math.PI : -2 * Math.PI;
                            nc = new ArcCurve3(new Frame3(c.Value, u, vv), R, a0, a1);
                            reAngled.Add(e);
                        }
                    }
                    break;
                }
            }
            if (nc == null && !EllOff && e.Curve is EllipseCurve3 && fs.Count == 2)
            {
                // u12b: ЭЛЛИПС «ПЛОСКОСТЬ — ЦИЛИНДР» — точное сечение смещённых поверхностей. Точками смещения (процедурная кривая)
                // при сильном сжатии цилиндра (R8 → 1.58) Ньютон уходил на дальнюю ветвь сечения — край грани выходил за контур.
                // Мутация P3D_OFFSETELLIPSE=0.
                (Vector3d At, Vector3d Dir, double R)? Cyl(ISurface3 q) => q switch
                {
                    CylinderSurface c => (c.Axis.Origin, c.Axis.W.Normalized(), c.Radius),
                    ExtrudedSurface { Base: ArcCurve3 ba } ex => (ba.Frame.Origin, ex.Dir.Normalized(), ba.Radius),
                    _ => null,
                };
                var s0 = newSurf[fs[0]].S; var s1 = newSurf[fs[1]].S;
                var pl = s0 as PlaneSurface ?? s1 as PlaneSurface; var cy = Cyl(s0 is PlaneSurface ? s1 : s0);
                if (pl != null && cy is { } cc && vmap.TryGetValue(e.V0, out var q0) && vmap.TryGetValue(e.V1, out var q1)
                    && Exact.QuadricSection.CylinderEllipse(cc.At, cc.Dir, cc.R, pl.F.Origin, pl.F.W.Normalized()) is { } sec)
                {
                    var (centre, major, minor, A, B) = sec;
                    double Ang(Vector3d p) => Math.Atan2(Vector3d.Dot(p - centre, minor) / B, Vector3d.Dot(p - centre, major) / A);
                    var oc = e.Curve; bool fw = (oc.PointAt(oc.T0) - e.V0.P).Length <= (oc.PointAt(oc.T0) - e.V1.P).Length;
                    var pa = fw ? q0 : q1; var pb = fw ? q1 : q0;
                    double a0 = Ang(pa), sw = Ang(pb) - a0;
                    while (sw <= -Math.PI) sw += 2 * Math.PI; while (sw > Math.PI) sw -= 2 * Math.PI;
                    var fr = new Frame3(centre, major, minor);
                    var c1 = new EllipseCurve3(fr, A, B, a0, a0 + sw);
                    var c2 = new EllipseCurve3(fr, A, B, a0, a0 + sw - Math.Sign(sw) * 2 * Math.PI);
                    var wantMid = Move(oc.PointAt(0.5 * (oc.T0 + oc.T1)), fs);
                    if (wantMid != null && (c1.PointAt(0) - pa).Length <= 10 * tol && (c1.PointAt(1) - pb).Length <= 10 * tol)
                    {
                        nc = (c1.PointAt(0.5) - wantMid.Value).Length <= (c2.PointAt(0.5) - wantMid.Value).Length ? c1 : c2;
                        reAngled.Add(e);
                    }
                }
            }
            if (nc == null && !OffsetProcOff)
            {
                // u12: кривая общего вида (пересечение, сплайн) — точками смещения той же параметризации
                var fs3 = fs; var oc3 = e.Curve;
                if (Move(oc3.PointAt(oc3.T0), fs3) != null && Move(oc3.PointAt(oc3.T1), fs3) != null)
                    nc = ProcOffset(e, oc3, fs3);
            }
            if (nc == null) { why = $"offset: a {e.Curve.GetType().Name} edge is not supported"; return null; }
            // проверка: точки новой кривой — смещённые точки старой (та же параметризация) и лежат на обеих смещённых гранях
            for (int k = 0; k <= 6; k++)
            {
                double t = e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 6.0;
                var q = nc.PointAt(nc.T0 + (nc.T1 - nc.T0) * k / 6.0);
                var want = k == 0 || k == 6 ? (Vector3d?)null : Move(e.Curve.PointAt(t), fs);
                // отрезок: смещённые точки — на прямой (концы могут сдвинуться вдоль неё); дуга — та же параметризация
                double off = want == null ? 0 : nc is LineCurve3 nl ? DistToLine(nl, want.Value)
                    // дуга с новыми углами (концы сместились вдоль неё): смещённые точки — на той же окружности
                    : nc is EllipseCurve3 && reAngled.Contains(e) ? fs.Max(f => (newSurf[f].S.PointAt(newSurf[f].S.ProjectToUV(want.Value)) - want.Value).Length)
                    : nc is ArcCurve3 ra && reAngled.Contains(e) ? Math.Abs((want.Value - ra.Frame.Origin).Length - ra.Radius) + Math.Abs(Vector3d.Dot(want.Value - ra.Frame.Origin, ra.Frame.W.Normalized()))
                    : (want.Value - q).Length;
                if (want != null && off > tol && !OffsetProcOff && !(nc is ProcCurve3) && e.Curve is ArcCurve3 oa)
                {
                    // u12: смещённая дуга — не окружность (ось кривой грани не строго ⟂ плоскости дуги, тело после булевых):
                    // кривая — точками смещения той же параметризации (процедурная, точна по построению). Мутация P3D_OFFSETPROC=0.
                    var fs2 = fs; var oc = e.Curve;
                    var p0 = Move(oc.PointAt(oc.T0), fs2); var p1 = Move(oc.PointAt(oc.T1), fs2);
                    if (p0 != null && p1 != null)
                    {
                        nc = ProcOffset(e, oc, fs2);
                        k = -1; continue;
                    }
                }
                // u12b: ПРЯМАЯ ПО СМЕЩЁННЫМ ВЕРШИНАМ, лежащая на обеих смещённых поверхностях, — и есть их пересечение: точки «по
                // смещению» у касательного шва с разным сдвигом (скольжение) могут уйти на другой корень, а у ребра в доли
                // миллиметра — разойтись с прямой на 1e-5 (касание). Мутация P3D_OFFSETLINEON=0.
                if (want != null && off > tol && !LineOnOff && nc is LineCurve3 && fs.All(f => OnSurf(newSurf[f].S, q))) off = 0;
                if (want != null && off > tol) Debug?.Invoke($"offset edge {e.V0.P}->{e.V1.P} curve {e.Curve} faces {string.Join(" | ", fs.Select(f => $"{GeometryAudit.AuthoritativeSurface(f)} {f.Tag}"))} want {want} got {q} ends {vmap[e.V0]} {vmap[e.V1]} faces at ends {string.Join(",", FacesAt(e.V0).Select(f => f.Tag))} / {string.Join(",", FacesAt(e.V1).Select(f => f.Tag))}");
                if (want != null && off > tol) { why = $"offset: the new edge curve does not follow the offset points ({e.Curve.GetType().Name}, k {k}, off {off:G3}, faces {string.Join("/", fs.Select(f => GeometryAudit.AuthoritativeSurface(f)?.GetType().Name))})"; return null; }
                foreach (var f in fs) if (!OnSurf(newSurf[f].S, q)) { why = "offset: the new edge curve leaves an offset face"; return null; }
            }
            emap[e] = nc;
        }
        // концы новых рёбер — новые вершины
        foreach (var e in body.Edges)
        {
            var nc = emap[e]; var a = nc.PointAt(nc.T0); var b = nc.PointAt(nc.T1);
            var va = vmap[e.V0]; var vb = vmap[e.V1];
            if (!(((a - va).Length <= tol && (b - vb).Length <= tol) || ((a - vb).Length <= tol && (b - va).Length <= tol)))
            { Debug?.Invoke($"offset end: edge {e.V0.P}->{e.V1.P} {e.Curve} new {nc} {a}->{b} vmap {va}->{vb} faces {string.Join(" | ", e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Select(f => $"{GeometryAudit.AuthoritativeSurface(f)} {f.Tag}"))}"); why = "offset: an edge does not end at its offset vertices"; return null; }
            if (e.V0 != e.V1 && (va - vb).Length < 1e-6) { why = "offset: an edge would vanish"; return null; }
        }

        var res = Rebuild(body, newSurf.ToDictionary(kv => kv.Key, kv => (kv.Value.S, kv.Value.Rev)), vmap, emap, tag,
                          (f, h) => h + Out(f, h) * newSurf[f].D, "offset", ref why);
        if (res != null) Interlocked.Increment(ref OffsetSolids);
        return res;
    }

    /// <summary>Сборка тела той же топологии с новыми поверхностями граней, новыми точками вершин и кривыми рёбер;
    /// проверка годности, аналитический объём, объём по фасетам. null — отказ (причина в why).</summary>
    internal static TSolid? Rebuild(TSolid body, Dictionary<TFace, (ISurface3 S, bool Rev)> newSurf, Dictionary<TVertex, Vector3d> vmap,
                                    Dictionary<TEdge, ICurve3> emap, string? tag, Func<TFace, Vector3d, Vector3d> hintMap, string what, ref string? why)
    {
        var faces = new List<FDesc>();
        foreach (var f in body.Faces)
        {
            var (ns, rev) = newSurf[f];
            Vector3d? hint = f.InteriorHint is { } h ? hintMap(f, h) : null;
            if (hint is { } hh) hint = ns.PointAt(ns.ProjectToUV(hh));
            var fd = new FDesc { Surf = ns, Rev = rev, Tag = tag ?? f.Tag, Hint = hint, Slot = f.MaterialSlot, Orig = f, Touched = true };
            if (ns.IsPlanar) fd.N = (ns.NormalAt(ns.ProjectToUV(vmap[f.Loops.SelectMany(l => l.CoEdges).First().Start])) * (rev ? -1 : 1)).Normalized();
            foreach (var l in f.Loops)
            {
                if (l.CoEdges.Count == 0) { fd.Loops.Add(new List<Seg>()); continue; }   // замкнутая поверхность без края (целый шар)
                var ord = l.Ordered();
                if (ord == null) { why = "loop not closed"; return null; }
                fd.Loops.Add(ord.Select(ce => new Seg { Curve = emap[ce.Edge], A = vmap[ce.Start], B = vmap[ce.End], Id = ce.Edge }).ToList());
            }
            faces.Add(fd);
        }
        var solid = FinishFaces(faces, what, ref why);
        if (solid == null) return null;
        if (Prov)
        {
            // KRN-08 (08.2): происхождение — грань той же поверхности и с теми же точками контура сохранена, прочие изменены
            var pv = new Provenance { Operation = what + ".rebuild" };
            foreach (var fd in faces)
            {
                if (fd.Made == null || fd.Orig == null) continue;
                var src = GeometryAudit.AuthoritativeSurface(fd.Orig);
                var oldPts = fd.Orig.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
                var newPts = fd.Made.Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
                bool same = ReferenceEquals(src, fd.Surf) && oldPts.Count == newPts.Count && newPts.All(p => oldPts.Any(q => (p - q).Length < 1e-12));
                pv.Faces.Add(new FaceProvenance(fd.Made, fd.Orig, same ? FaceOrigin.Kept : FaceOrigin.Modified, null));
            }
            solid.SetProvenance(pv);
        }
        return solid;
    }

    /// <summary>Сборка тела из описаний граней: точки граней, сборка, соглашение, разметка, проверки (годность,
    /// аналитический объём, объём по фасетам), допуски сущностей. null — отказ.</summary>
    static TSolid? FinishFaces(List<FDesc> faces, string what, ref string? why, bool measureFirst = false, GuaranteeLevel? inherit = null)
    {
        foreach (var fd in faces.Where(x => x.Surf.IsPlanar)) fd.Hint = InteriorPoint(fd) ?? fd.Hint;

        var solid = Assemble(faces, ref why);
        if (solid == null) return null;
        TopologyConventions.Normalize(solid);
        var w2 = new List<string>();
        if (Analytic.PrismBuild.Finish(solid, w2, BooleanOp.DonorFrom(faces.Where(d => d.Orig != null).Select(d => d.Orig!))) > 0) {
            if (Debug != null) foreach (var fd in faces) Debug($"offface {fd.Surf.GetType().Name} {fd.Tag} N {fd.N}: {string.Join(" ; ", fd.Loops.Select(l => string.Join(" ", l.Select(sg => $"{sg.Curve.GetType().Name[..3]} {sg.A}->{sg.B}"))))}");
            why = what + ": faces not meshed: " + string.Join(" / ", w2.Take(3)); return null; }
        solid.Guarantee = GuaranteeLevel.ExactAnalytic;
        // u12b: тело из рёбер входа (заострение, продление сквозь открытую грань) — допуски сущностей меряются до проверки:
        // рёбра входа после булевых отходят от поверхностей на 1e-6 и с допуском 0 у точного тела считались браком
        if (measureFirst) Analytic.EntityTolerance.Assign(solid);
        // рёбра входа приближённые (сечения по хордам после резца) — у тела та же гарантия, что у входа
        if (inherit is { } gi && gi != GuaranteeLevel.ExactAnalytic) solid.Guarantee = gi;
        var vr = SolidValidator.Validate(solid);
        if (!vr.IsValid) { why = what + ": result invalid: " + vr.Summary() + " " + string.Join(" / ", vr.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2)); return null; }
        if (AnalyticVolume.Of(solid) == null) { why = what + ": no analytic volume"; return null; }
        if (BooleanOp.VolumeVsFacets(solid) is { } vf && !vf.Agree) { why = $"{what}: volume {vf.Va:G8} vs facets {vf.Vf:G8}"; return null; }
        Analytic.EntityTolerance.Assign(solid);
        return solid;
    }

    static double DistToLine(LineCurve3 l, Vector3d x)
    {
        var d = (l.B - l.A).Normalized(); var r = x - l.A; return (r - d * Vector3d.Dot(r, d)).Length;
    }

    /// <summary>Центр окружности через три точки (null — точки на одной прямой).</summary>
    static Vector3d? Circle3(Vector3d a, Vector3d b, Vector3d c)
    {
        var ab = b - a; var ac = c - a; var n = Vector3d.Cross(ab, ac);
        double n2 = n.LengthSquared; if (n2 < 1e-24) return null;
        return a + (Vector3d.Cross(n, ab) * ac.LengthSquared + Vector3d.Cross(ac, n) * ab.LengthSquared) / (2 * n2);
    }
}
