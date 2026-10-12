using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Geometry;

/// <summary>
/// G4. ТОЧНАЯ БУЛЕВА: ТЕЛО МИНУС БЕСКОНЕЧНЫЙ ЦИЛИНДР — С ДЕЛЕНИЕМ ГРАНЕЙ.
///
/// Сквозное отверстие или паз, когда кромка пересекает край грани (у края детали, через угол,
/// у торца вала). Рёбра тела (отрезки и дуги) делятся в точках пересечения с цилиндром, куски
/// внутри цилиндра отбрасываются. В каждой грани обход контура там, где он заходит в цилиндр,
/// заменяется кромкой — куском точного пересечения поверхности грани с цилиндром (Ssi: эллипс,
/// окружность, прямая или прослеженная кривая) от точки входа ВНУТРЬ грани до ближайшей точки
/// выхода; грань может распасться на несколько, грань целиком внутри цилиндра исчезает.
/// Замкнутая кромка целиком внутри грани — её отверстие. Стенка — грань на цилиндре из тех же
/// кромок. Грани — плоскости и поверхности, известные Ssi (цилиндр, шар, конус, тор).
/// Иначе — null, и работает прежняя булева.
/// </summary>
public static class ExactCylinderCut
{
    /// <summary>
    /// Только для разбора (внутреннее, на поток): вернуть тело, даже если самопроверка его отвергла.
    /// Такое тело помечается приближённым и не может уйти дальше с производственной гарантией.
    /// </summary>
    [ThreadStatic] internal static bool DebugKeepInvalid;
    [ThreadStatic] internal static bool DebugNoOrient;

    public static TSolid? Cut(TSolid body, Ssi.Cylinder cyl, List<string>? warnings = null) => Cut(body, cyl, null, warnings);

    /// <summary>
    /// С дном: цилиндр от бесконечности до плоского дна ⟂ оси в точке bottom (ось — вглубь тела).
    /// Дно должно лежать в материале целиком (не касаться граней тела); иначе — отказ.
    /// </summary>
    public static TSolid? Cut(TSolid body, Ssi.Cylinder cyl, Vector3d? bottom, List<string>? warnings = null)
    {
        using var __p = Diagnostics.Prof.Time("цилиндр: весь вырез");
        var index = new CutIndex(body);
        var w = cyl.Axis.Normalized();
        double Rad(Vector3d x) { var d = x - cyl.P; return (d - w * Vector3d.Dot(d, w)).Length; }
        double Ax(Vector3d x) => Vector3d.Dot(x - cyl.P, w);
        double zb = bottom is { } bt0 ? Ax(bt0) : double.PositiveInfinity;
        bool InTool(Vector3d x) => Rad(x) < cyl.R && Ax(x) < zb;
        TSolid? No(string why) { warnings?.Add("ExactCylinderCut: " + why + " — the polyhedral boolean is used."); return null; }
        if (body.Edges.Any(e => e.Curve is not (LineCurve3 or ArcCurve3))) return No("the body has edges other than segments and arcs");
        var verts = body.Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().ToList();
        if (verts.Any(v => Math.Abs(Rad(v.P) - cyl.R) < 1e-7)) return No("a body vertex lies on the cylinder");
        double bodyR = verts.Max(v => (v.P - cyl.P).Length) + cyl.R + 1;

        var __st_4050 = Diagnostics.Prof.Time("цилиндр: этап 1 (рёбра)");
        // 1. Рёбра: точки пересечения с цилиндром; куски с их кривыми.
        var newV = new Dictionary<TVertex, TVertex>(ReferenceEqualityComparer.Instance);
        foreach (var v in verts) newV[v] = new TVertex(v.P);
        var pieces = new Dictionary<TEdge, List<(TVertex A, TVertex B, bool Inside, TEdge Sub)>>(ReferenceEqualityComparer.Instance);
        foreach (var e in body.Edges)
        {
            var c = e.Curve;
            bool fwd = ReferenceEquals(e.V0, e.V1) || (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared;
            // Параметр s ∈ [0, 1] от V0 к V1.
            Vector3d At(double s) => c.PointAt(c.T0 + (c.T1 - c.T0) * (fwd ? s : 1 - s));
            var ss = new List<double>();
            // Ребро не должно пересекать дно внутри цилиндра (дно — целиком в материале).
            if (bottom != null)
                for (int i = 0; i <= 256; i++)
                {
                    var q0 = At(i / 256.0); var q1 = At(Math.Min(1, (i + 1) / 256.0));
                    if ((Ax(q0) - zb) * (Ax(q1) - zb) <= 0 && (Rad(q0) < cyl.R || Rad(q1) < cyl.R) && i < 256)
                        return No("the hole bottom reaches a body edge");
                }
            if (c is LineCurve3)
            {
                Vector3d A = e.V0.P, D = e.V1.P - e.V0.P;
                var r0 = (A - cyl.P) - w * Vector3d.Dot(A - cyl.P, w); var rd = D - w * Vector3d.Dot(D, w);
                double qa = rd.LengthSquared, qb = 2 * Vector3d.Dot(r0, rd), qc = r0.LengthSquared - cyl.R * cyl.R;
                if (qa > 1e-18)
                {
                    double disc = qb * qb - 4 * qa * qc;
                    if (disc > 1e-18) { double sq = Math.Sqrt(disc); foreach (var t in new[] { (-qb - sq) / (2 * qa), (-qb + sq) / (2 * qa) }) if (t > 1e-9 && t < 1 - 1e-9 && Ax(A + D * t) < zb) ss.Add(t); }
                }
            }
            else
            {
                // Дуга: смены знака Rad − R по выборке, уточнение делением пополам.
                int n = 720; double F(double s) => Rad(At(s)) - cyl.R;
                double prev = F(0);
                for (int i = 1; i <= n; i++)
                {
                    double s1 = (double)i / n, f1 = F(s1);
                    if (prev * f1 < 0)
                    {
                        double lo = (double)(i - 1) / n, hi = s1;
                        for (int it = 0; it < 60; it++) { double m = (lo + hi) / 2; if (Math.Sign(F(m)) == Math.Sign(prev)) lo = m; else hi = m; }
                        if (Ax(At((lo + hi) / 2)) < zb) ss.Add((lo + hi) / 2);
                    }
                    prev = f1;
                }
            }
            ss.Sort();
            var pts = new List<(TVertex V, double S)> { (newV[e.V0], 0) };
            foreach (var s in ss) pts.Add((new TVertex(At(s)), s));
            pts.Add((newV[e.V1], 1));
            var list = new List<(TVertex, TVertex, bool, TEdge)>();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double s0 = pts[i].S, s1 = pts[i + 1].S;
                ICurve3 sub = c is ArcCurve3 ac
                    ? new ArcCurve3(ac.Frame, ac.Radius, ac.A0 + (ac.A1 - ac.A0) * (fwd ? s0 : 1 - s0), ac.A0 + (ac.A1 - ac.A0) * (fwd ? s1 : 1 - s1))
                    : new LineCurve3(pts[i].V.P, pts[i + 1].V.P);
                var se = new TEdge { V0 = pts[i].V, V1 = pts[i + 1].V, Curve = sub };
                list.Add((pts[i].V, pts[i + 1].V, InTool(At((s0 + s1) / 2)), se));
            }
            pieces[e] = list;
        }

        __st_4050.Dispose();
        var __st_6342 = Diagnostics.Prof.Time("цилиндр: этап 2 (грани)");
        // 2. Грани.
        var solid = new TSolid(); var shell = new TShell(); solid.Shells.Add(shell); shell.Solid = solid;
        var arcs = new List<TEdge>();
        foreach (var f in body.Faces)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            var shape = ExactEdges.ShapeOf(src);
            if (shape == null || src == null) return No("a face has no analytic surface");
            Ssi.Result conic;
            try { conic = Ssi.Intersect(shape, cyl, cyl.P, bodyR); } catch (NotSupportedException) { return No("no surface intersection for a face"); }
            ISurface3 newSurf = shape is Ssi.Plane pl ? new PlaneSurface(Frame3.FromNormal(pl.P, pl.N.Normalized())) : src;
            var nf = new TFace { Surface = newSurf, Tag = f.Tag, MaterialSlot = f.MaterialSlot };
            var probe = InteriorPoint(f, src);
            nf.Reversed = Vector3d.Dot(f.NormalAt(probe), newSurf.NormalAt(newSurf.ProjectToUV(probe))) < 0;
            bool anyKept = false;
            var runs = new List<List<(TVertex A, TEdge E)>>();
            foreach (var loop in f.Loops)
            {
                var walk = new List<(TVertex A, TVertex B, bool In, TEdge E)>();
                foreach (var ce in loop.CoEdges)
                {
                    var ps = pieces[ce.Edge];
                    if (!ce.Reversed) walk.AddRange(ps.Select(p => (p.A, p.B, p.Inside, p.Sub)));
                    else for (int i = ps.Count - 1; i >= 0; i--) walk.Add((ps[i].B, ps[i].A, ps[i].Inside, ps[i].Sub));
                }
                if (walk.All(x => x.In)) continue;
                // Шов, выделенный в отдельный контур (одно ребро туда и обратно), — не граница области:
                // у пояса цилиндра его не должно быть в контурах (разметка поясов узнаёт шов сама),
                // иначе сетка стенки вала с глухим отверстием выходила вывернутой.
                if (walk.Count == 2 && ReferenceEquals(walk[0].E, walk[1].E)) continue;
                anyKept = true;
                if (walk.All(x => !x.In))
                {
                    var nl0 = nf.AddLoop(loop.IsOuter);
                    foreach (var x in walk) nl0.Add(x.E, !ReferenceEquals(x.E.V0, x.A));
                    continue;
                }
                int n = walk.Count;
                int st = Enumerable.Range(0, n).First(i => !walk[i].In && walk[(i - 1 + n) % n].In);
                List<(TVertex, TEdge)>? cur = null;
                for (int k = 0; k < n; k++)
                {
                    var x = walk[(st + k) % n];
                    if (!x.In) { cur ??= new(); cur.Add((x.A, x.E)); }
                    else if (cur != null) { runs.Add(cur); cur = null; }
                }
                if (cur != null) runs.Add(cur);
            }
            if (runs.Count > 0)
            {
                var exits = runs.Select(r => r[0].A).ToList();
                var used = new bool[runs.Count];
                for (int r0 = 0; r0 < runs.Count; r0++)
                {
                    if (used[r0]) continue;
                    var nl = nf.AddLoop(true);
                    int r = r0, guard = 0;
                    while (!used[r] && guard++ < 1000)
                    {
                        used[r] = true;
                        foreach (var (a2, e2) in runs[r]) nl.Add(e2, !ReferenceEquals(e2.V0, a2));
                        var last = runs[r][^1];
                        var entry = ReferenceEquals(last.E.V0, last.A) ? last.E.V1 : last.E.V0;
                        var (arc, xi) = Connect(conic, entry, exits, f, src, index);
                        if (arc != null && bottom != null && Enumerable.Range(0, 33).Any(i => Ax(arc.PointAt(arc.T0 + (arc.T1 - arc.T0) * i / 32)) > zb + 1e-9))
                            return No("a rim curve runs below the hole bottom");
                        if (arc == null) return No($"no rim curve from {entry.P} inside face {f.Tag} ({src.GetType().Name}); curves {string.Join(",", conic.Curves.Select(cc => cc.GetType().Name))}; exits {string.Join(" ", exits.Select(x => x.P.ToString("0.###")))}");
                        var ae = new TEdge { V0 = entry, V1 = exits[xi], Curve = arc };
                        arcs.Add(ae);
                        nl.Add(ae, false);
                        r = xi;
                    }
                    if (r != r0) return No("a face contour does not close after splitting");
                }
            }
            // Замкнутая кромка целиком внутри грани — её отверстие.
            if (anyKept && conic.Kind == Ssi.Kind.Curves)
                foreach (var rim in conic.Curves)
                {
                    if (rim is LineCurve3 || (rim.PointAt(rim.T0) - rim.PointAt(rim.T1)).Length > 1e-6) continue;
                    var samples = Enumerable.Range(0, 32).Select(i => rim.PointAt(rim.T0 + (rim.T1 - rim.T0) * i / 32)).ToList();
                    if (!samples.All(q => index.OnFace(f, src, q))) continue;
                    if (samples.All(q => Ax(q) > zb)) continue;                       // за дном — не задета
                    if (samples.Any(q => Ax(q) > zb - 1e-9)) return No("the hole bottom cuts a face rim");
                    if (arcs.Any(a => samples.Any(q => (q - a.V0.P).Length < 1e-6))) continue;
                    // Замкнутая кромка — отверстие грани: по часовой вокруг её наружной нормали. Кривая
                    // пересечения идёт в любую сторону; если против часовой — берётся обратная копия
                    // (правило «в грани — вперёд, в стенке — обратно» должно остаться верным).
                    var nw = Vector3d.Zero; for (int i = 0; i < samples.Count; i++) nw += Vector3d.Cross(samples[i], samples[(i + 1) % samples.Count]);
                    var nOut = nf.NormalAt(samples[0]);
                    ICurve3 rimDir = rim;
                    if (Vector3d.Dot(nw, nOut) > 0)
                        rimDir = rim switch
                        {
                            ArcCurve3 ac => new ArcCurve3(ac.Frame, ac.Radius, ac.A1, ac.A0),
                            EllipseCurve3 ec => new EllipseCurve3(ec.Frame, ec.RU, ec.RV, ec.A1, ec.A0),
                            IntersectionCurve3 ic => new IntersectionCurve3(ic.A, ic.B, Enumerable.Range(0, 513).Select(i => ic.PointAt(ic.T1 - (ic.T1 - ic.T0) * i / 512)).ToList()),
                            _ => rim
                        };
                    var v0 = new TVertex(rimDir.PointAt(rimDir.T0));
                    var ce0 = new TEdge { V0 = v0, V1 = v0, Curve = rimDir };
                    arcs.Add(ce0);
                    nf.AddLoop(false).Add(ce0, false);
                }
            if (anyKept) shell.Add(nf);
        }
        if (arcs.Count == 0) return No("the cylinder does not cut the body");

        __st_6342.Dispose();
        var __st_2603 = Diagnostics.Prof.Time("цилиндр: этап 3 (стенка и дно)");
        // 3. Стенка.
        var allPts = arcs.SelectMany(a => Enumerable.Range(0, 9).Select(i => a.Curve.PointAt(a.Curve.T0 + (a.Curve.T1 - a.Curve.T0) * i / 8))).ToList();
        double z0 = allPts.Min(q => Vector3d.Dot(q - cyl.P, w)) - 4 * cyl.R - 1, z1 = Math.Max(allPts.Max(q => Vector3d.Dot(q - cyl.P, w)), bottom != null ? zb : double.MinValue) + 4 * cyl.R + 1;
        var wallSurf = new ExtrudedSurface(new ArcCurve3(Frame3.FromNormal(cyl.P + w * z0, w), cyl.R, 0, 2 * Math.PI), w * (z1 - z0));
        var wall = new TFace { Surface = wallSurf, Reversed = true, Tag = "exact:hole-wall" };
        var left = new HashSet<TEdge>(arcs, ReferenceEqualityComparer.Instance);
        // Каждая кромка в своей грани пройдена от V0 к V1; в стенке — обратно (V1 → V0): обход
        // согласован по построению, и знак компоненты не нужно угадывать.
        while (left.Count > 0)
        {
            var first = left.First(); left.Remove(first);
            var l = wall.AddLoop(true);
            l.Add(first, true);
            var end = first.V0; int guard = 0;
            while (!ReferenceEquals(end, first.V1) && guard++ < 10000)
            {
                var next = left.FirstOrDefault(a => ReferenceEquals(a.V1, end));
                if (next == null) return No("the hole-wall loop does not close");
                left.Remove(next);
                l.Add(next, true);
                end = next.V0;
            }
        }
        if (bottom != null)
        {
            // Дно: круг ⟂ оси; в грани дна обход — вперёд (против часовой вокруг −w, наружной
            // нормали тела), в стенке — обратно.
            var capC = cyl.P + w * zb;
            var capCurve = new ArcCurve3(Frame3.FromNormal(capC, -w), cyl.R, 0, 2 * Math.PI);
            var cv = new TVertex(capCurve.PointAt(capCurve.T0));
            var capEdge = new TEdge { V0 = cv, V1 = cv, Curve = capCurve };
            var cap = new TFace { Surface = new PlaneSurface(Frame3.FromNormal(capC, -w)), Reversed = false, Tag = "exact:hole-bottom" };
            cap.AddLoop(true).Add(capEdge, false);
            wall.AddLoop(true).Add(capEdge, true);
            shell.Add(cap);
            // Дно в материале: его центр и точки круга — внутри тела.
            if (!index.Inside(capC) || Enumerable.Range(0, 8).Any(i => !index.Inside(capCurve.PointAt(i / 8.0) * 0.999 + capC * 0.001)))
                return No("the hole bottom is not inside the material");
        }
        shell.Add(wall);
        __st_2603.Dispose();
        TopologyConventions.Report norm;
        using (Diagnostics.Prof.Time("цилиндр: соглашение о полурёбрах"))
            norm = TopologyConventions.Normalize(solid);
        // Знак компоненты без плоских колец не определился, но противоречий нет: обход согласован
        // по построению (контуры граней — в прежнем направлении, стенка — обратно) — принимается.
        if (solid.TopologyConvention == 0 && norm.Consistent && norm.Problems.Count == 0 && norm.Unanchored > 0)
        {
            TopologyConventions.SplitRegions(solid);
            solid.TopologyConvention = TopologyConventions.Version;
        }
        if (solid.TopologyConvention == 0) return No("topology conventions failed: " + norm + " | " + string.Join("; ", norm.Problems.Take(3)));
        using (Diagnostics.Prof.Time("цилиндр: разметка граней по контурам"))
            Export.StepImport.MeshFacesFromLoops(solid, warnings, "ExactCylinderCut");
        using (Diagnostics.Prof.Time("цилиндр: ориентация оболочки"))
            if (!DebugNoOrient) Export.StepImport.OrientShellPublic(solid);
        // Самопроверка: годное B-rep и годная сетка — иначе отказ (прежняя булева), а не брак.
        Topology.ValidationReport vr;
        using (Diagnostics.Prof.Time("цилиндр: самопроверка"))
            vr = SolidValidator.Validate(solid);
        if (!vr.IsValid && !DebugKeepInvalid) return No("the exact result failed validation (" + vr.Summary() + ")");
        if (!vr.IsValid)
        {
            solid.Guarantee = GuaranteeLevel.Approximate;
            warnings?.Add("ExactCylinderCut: DIAGNOSTIC result kept despite failed validation (" + vr.Summary() + ") — not a production result.");
            return solid;
        }
        solid.Guarantee = GuaranteeLevel.ExactQuadric;
        warnings?.Add($"ExactCylinderCut: {arcs.Count} rim curve(s) from exact surface intersections.");
        return solid;
    }

    /// <summary>
    /// ПОСТАВКА 3. ИНДЕКС НА ОПЕРАЦИЮ. Прежде «точка в теле» на каждый вызов заново гранила всё тело
    /// (проверка дна — 9 вызовов подряд), а «точка на грани» на каждую точку заново строила ломаные контуров
    /// плоской грани или гранила кривую. Теперь треугольники тела (с рёбрами и определителями под постоянное
    /// направление луча) и данные каждой грани готовятся один раз за вырез. Арифметика каждой проверки та же,
    /// что прежде, — ответы совпадают побитово.
    /// </summary>
    private sealed class CutIndex(TSolid body)
    {
        private static readonly Vector3d RayDir = new Vector3d(0.5773, 0.5571, 0.5967).Normalized();
        private (Vector3d A, Vector3d E1, Vector3d E2, Vector3d Pv, double Det)[]? _tris;
        private readonly Dictionary<TFace, FaceData> _faces = new(ReferenceEqualityComparer.Instance);

        private sealed class FaceData
        {
            public PlaneSurface? Plane;                          // плоская грань: её плоскость
            public List<(List<Vector2d> Poly, bool Outer)>? Loops;
            public List<(Vector3d A, Vector3d B, Vector3d C)>? Tris; // кривая: треугольники её сетки
        }

        public bool Inside(Vector3d q)
        {
            using var __p = Diagnostics.Prof.Time("цилиндр: Inside (точка в теле)");
            if (_tris == null)
            {
                var md = Tess.Tessellator.Tessellate(body, facets: true);
                var list = new List<(Vector3d, Vector3d, Vector3d, Vector3d, double)>(md.Indices.Length / 3);
                for (int t = 0; t + 2 < md.Indices.Length; t += 3)
                {
                    Vector3d P3(int i) { var p = md.Vertices[md.Indices[i]].P; return new Vector3d(p.X, p.Y, p.Z); }
                    var a = P3(t); var b = P3(t + 1); var c = P3(t + 2);
                    var e1 = b - a; var e2 = c - a; var pv = Vector3d.Cross(RayDir, e2); double det = Vector3d.Dot(e1, pv);
                    if (Math.Abs(det) < 1e-14) continue;
                    list.Add((a, e1, e2, pv, det));
                }
                _tris = list.ToArray();
            }
            int hits = 0;
            foreach (var (a, e1, e2, pv, det) in _tris)
            {
                double inv = 1 / det; var sv = q - a; double u = Vector3d.Dot(sv, pv) * inv; if (u < 0 || u > 1) continue;
                var qv = Vector3d.Cross(sv, e1); double v = Vector3d.Dot(RayDir, qv) * inv; if (v < 0 || u + v > 1) continue;
                if (Vector3d.Dot(e2, qv) * inv > 0) hits++;
            }
            return hits % 2 == 1;
        }

        /// <summary>Точка на грани: по многоугольнику (плоская) или по расстоянию до её сетки (кривая).</summary>
        public bool OnFace(TFace f, ISurface3? src, Vector3d q)
        {
            using var __p = Diagnostics.Prof.Time("цилиндр: OnFace (точка на грани)");
            if (!_faces.TryGetValue(f, out var fd)) _faces[f] = fd = Prepare(f, src);
            if (fd.Plane is { } ps)
            {
                if (Math.Abs(Vector3d.Dot(q - ps.F.Origin, ps.F.W.Normalized())) > 1e-6) return false;
                var uv = ps.F.ToLocal2(q);
                bool inOuter = false; int holes = 0;
                foreach (var (poly, outer) in fd.Loops!)
                {
                    bool inside = false;
                    for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                    {
                        var a = poly[i]; var b = poly[j];
                        if ((a.Y > uv.Y) != (b.Y > uv.Y) && uv.X < (b.X - a.X) * (uv.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                    }
                    if (outer) inOuter |= inside; else if (inside) holes++;
                }
                return inOuter && holes == 0;
            }
            double best = double.MaxValue;
            foreach (var (a, b, c) in fd.Tris!) best = Math.Min(best, DistTri(q, a, b, c));
            return best < 0.05;
        }

        private static FaceData Prepare(TFace f, ISurface3? src)
        {
            var fd = new FaceData();
            if (ExactEdges.ShapeOf(src) is Ssi.Plane pl)
            {
                var ps = new PlaneSurface(Frame3.FromNormal(pl.P, pl.N.Normalized()));
                fd.Plane = ps; fd.Loops = new();
                foreach (var loop in f.Loops)
                {
                    var pts = new List<Vector3d>(); foreach (var ce in loop.CoEdges) ce.Tessellate(pts, pts.Count == 0);
                    fd.Loops.Add((pts.Select(p => ps.F.ToLocal2(p)).ToList(), loop.IsOuter));
                }
                return fd;
            }
            var mb = new Tess.MeshBuilder(); mb.BeginFace(f.Id); f.Surface.TessellateFace(f, mb); mb.EndFace();
            var md = new Tess.MeshData(); mb.FillInto(md);
            fd.Tris = new();
            for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                fd.Tris.Add((mb.PositionsD[md.Indices[i]], mb.PositionsD[md.Indices[i + 1]], mb.PositionsD[md.Indices[i + 2]]));
            return fd;
        }
    }

    /// <summary>Точка внутри грани (центр наибольшего треугольника её сетки) на её поверхности.</summary>
    static Vector3d InteriorPoint(TFace f, ISurface3 src)
    {
        var mb = new Tess.MeshBuilder(); mb.BeginFace(f.Id); f.Surface.TessellateFace(f, mb); mb.EndFace();
        var md = new Tess.MeshData(); mb.FillInto(md);
        Vector3d best = f.Loops[0].CoEdges[0].Start.P; double ba = -1;
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            var a = mb.PositionsD[md.Indices[i]]; var b = mb.PositionsD[md.Indices[i + 1]]; var c = mb.PositionsD[md.Indices[i + 2]];
            double ar = Vector3d.Cross(b - a, c - a).LengthSquared; if (ar > ba) { ba = ar; best = (a + b + c) / 3; }
        }
        return src.PointAt(src.ProjectToUV(best));
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

    /// <summary>От точки входа — по кромке внутрь грани до ближайшей точки выхода на той же кромке.</summary>
    static (ICurve3? Arc, int Exit) Connect(Ssi.Result conic, TVertex entry, List<TVertex> exits, TFace f, ISurface3 src, CutIndex index)
    {
        using var __p = Diagnostics.Prof.Time("цилиндр: Connect (кромка в грани)");
        var a = entry.P;
        foreach (var c in conic.Curves)
        {
            // Параметр точки на кривой — по выборке и уточнению; кривая годится, если точка на ней.
            int N = 2048;
            var samples = Enumerable.Range(0, N + 1).Select(i => c.PointAt(c.T0 + (c.T1 - c.T0) * i / N)).ToArray();
            bool closed = (samples[0] - samples[N]).Length < 1e-6;
            double ParamOf(Vector3d q, out double dist)
            {
                int bi = 0; double bd = double.MaxValue;
                for (int i = 0; i <= N; i++) { double d = (samples[i] - q).LengthSquared; if (d < bd) { bd = d; bi = i; } }
                double lo = Math.Max(0, bi - 1) / (double)N, hi = Math.Min(N, bi + 1) / (double)N;
                for (int it = 0; it < 60; it++)
                {
                    double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                    if ((c.PointAt(c.T0 + (c.T1 - c.T0) * m1) - q).LengthSquared < (c.PointAt(c.T0 + (c.T1 - c.T0) * m2) - q).LengthSquared) hi = m2; else lo = m1;
                }
                double s = (lo + hi) / 2; dist = (c.PointAt(c.T0 + (c.T1 - c.T0) * s) - q).Length; return s;
            }
            double sa = ParamOf(a, out double da);
            if (da > 1e-5) continue;
            Vector3d P(double s) { if (closed) s -= Math.Floor(s); return c.PointAt(c.T0 + (c.T1 - c.T0) * Math.Clamp(s, 0, 1)); }
            // Кусок к каждой точке выхода на этой кривой — в ту сторону, где его середина лежит на
            // грани (проверка малым шагом от входа не годилась: край грани — вписанный многоугольник,
            // и точка у самой кромки оказывалась «вне грани» с обеих сторон).
            int best = -1; double bs = double.MaxValue, sgn = 0;
            for (int i = 0; i < exits.Count; i++)
            {
                double sx = ParamOf(exits[i].P, out double dx);
                if (dx > 1e-5 || ReferenceEquals(exits[i], entry)) continue;
                foreach (double dir in new[] { 1.0, -1.0 })
                {
                    double ds = (sx - sa) * dir;
                    if (closed) ds -= Math.Floor(ds);
                    if (ds <= 1e-9 || ds >= bs) continue;
                    if (!closed && (sa + dir * ds < -1e-9 || sa + dir * ds > 1 + 1e-9)) continue;
                    if (!index.OnFace(f, src, P(sa + dir * ds / 2))) continue;
                    bs = ds; best = i; sgn = dir;
                }
            }
            if (best < 0) continue;
            double sb = sa + sgn * bs;
            switch (c)
            {
                case LineCurve3: return (new LineCurve3(a, exits[best].P), best);
                case ArcCurve3 ac: { double t0 = ac.A0 + (ac.A1 - ac.A0) * sa, t1 = ac.A0 + (ac.A1 - ac.A0) * sb; return (new ArcCurve3(ac.Frame, ac.Radius, t0, t1), best); }
                case EllipseCurve3 ec: { double t0 = ec.A0 + (ec.A1 - ec.A0) * sa, t1 = ec.A0 + (ec.A1 - ec.A0) * sb; return (new EllipseCurve3(ec.Frame, ec.RU, ec.RV, t0, t1), best); }
                case IntersectionCurve3 ic:
                {
                    int m = Math.Max(16, (int)(Math.Abs(sb - sa) * N));
                    var seed = Enumerable.Range(0, m + 1).Select(i => P(sa + (sb - sa) * i / m)).ToList();
                    seed[0] = a; seed[^1] = exits[best].P;
                    return (new IntersectionCurve3(ic.A, ic.B, seed), best);
                }
            }
        }
        return (null, -1);
    }
}
