using System.Globalization;
using System.Text;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Export.Parasolid;

/// <summary>
/// ТЕЛА PARASOLID XT → ТЕКСТ STEP (AP214, B-rep, мм). Импорт SLDPRT и Parasolid идёт тем же путём, что и импорт STEP
/// (StepImport: разметка граней по контурам, лечение, ориентация оболочек), а документ хранит этот текст в операции «Импорт».
/// Соответствие (Parasolid XT Format Reference, «Model structure», «Geometry»):
///   тело → замкнутая оболочка на каждую оболочку твёрдой области (REGION.type = 'S'); грань → ADVANCED_FACE, сторона —
///   совпадение знаков FACE.sense и SURFACE.sense (у конуса естественная нормаль Parasolid смотрит к оси — знак обращён);
///   цикл → EDGE_LOOP из «плавников» (HALFEDGE) по forward, плавник '+' — по ребру; ребро → EDGE_CURVE: конец — вершина
///   положительного плавника (forward vertex), начало — вершина другого; направление кривой — CURVE.sense.
///   Плоскость, цилиндр, конус, шар, тор, B-поверхность — точно; прямая, окружность, эллипс, B-кривая — точно; кривая
///   пересечения — ломаной по точкам карты (CHART, точность Parasolid); обрезанная кривая — её основа; скругление
///   (BLENDED_EDGE, катящийся шар) — B-сплайном по сечениям-дугам между точками касания с опорными поверхностями;
///   поверхность протяжки — SURFACE_OF_LINEAR_EXTRUSION.
/// </summary>
public static class XtToStep
{
    const double S = 1000.0;   // метры XT → мм STEP
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Convert(XtFile f, string name, List<string>? warnings = null) => Convert(new[] { f }, name, warnings);

    /// <summary>Несколько потоков XT (тела импортированных элементов SolidWorks) — в один файл STEP: тело на оболочку.</summary>
    public static string Convert(IReadOnlyList<XtFile> files, string name, List<string>? warnings = null)
    {
        var sb = new StringBuilder(); var box = new[] { 1 };
        Ctx c = null!;
        var shells = new List<int>();
        int skipped = 0; var skippedKinds = new HashSet<string>();
        double acc = 1e-6;
        // k184: ход импорта — по граням всех тел
        int facesAll = files.Sum(ff => ff.OfType("FACE").Count()), facesDone = 0;
        ImportProgress.SetFaceTotal(facesAll);
        foreach (var f in files)
        {
        c = new Ctx(f, warnings) { Sb = sb, NextBox = box };
        // точность модели — наибольший допуск рёбер и вершин XT (допусковые элементы Parasolid), мм; не меньше 10⁻⁶ мм
        foreach (var n in f.Order) if (n.Name is "EDGE" or "VERTEX" && n.D("tolerance") is double tl && tl > 0 && tl < 1e-3) acc = Math.Max(acc, tl * S);
        foreach (var body in f.OfType("BODY"))
        {
            // только твёрдые тела (body_type 1): листовые и проволочные тела — не оболочки тела
            if (body.I("body_type") is int bt && bt != 1 && bt != 0) { warnings?.Add($"Parasolid: a non-solid body (type {bt}) was left out."); continue; }
            var faceLists = new List<List<XtNode>>();
            foreach (var region in f.OfType("REGION").Where(r => r.P("body") == body.Id && r.C("type") == 'S'))
                for (var sh = f[region.P("shell")]; sh != null; sh = f[sh.P("next")])
                {
                    var faces = new List<XtNode>();
                    var seen = new HashSet<int>();
                    for (var fc = f[sh.P("face")]; fc != null && seen.Add(fc.Id); fc = f[fc.P("next")]) faces.Add(fc);
                    if (faces.Count > 0) faceLists.Add(faces);
                }
            if (faceLists.Count == 0)
            {
                // запас: все грани оболочек тела
                var faces = f.OfType("FACE").Where(fc => f[fc.P("shell")] is { } sh && sh.P("body") == body.Id).ToList();
                if (faces.Count > 0) faceLists.Add(faces);
            }
            foreach (var faces in faceLists)
            {
                var ids = new List<int>();
                foreach (var fc in faces) { if (c.Face(fc) is int fid) ids.Add(fid); ImportProgress.Convert(++facesDone, facesAll); }
                if (ids.Count > 0) shells.Add(c.E($"CLOSED_SHELL('',({string.Join(",", ids.Select(x => "#" + x))}))"));
            }
        }
        skipped += c.Skipped; skippedKinds.UnionWith(c.SkippedKinds);
        }
        if (shells.Count == 0) throw new InvalidDataException("Parasolid: no solid body faces in the file.");
        var solids = shells.Select(sh => c.E($"MANIFOLD_SOLID_BREP('{Esc(name)}',#{sh})")).ToList();
        int uLen = c.E("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
        int uAng = c.E("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))");
        int uSol = c.E("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())");
        int unc = c.E($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE({N(acc)}),#{uLen},'distance_accuracy_value','')");
        int ctx = c.E($"(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{unc}))GLOBAL_UNIT_ASSIGNED_CONTEXT((#{uLen},#{uAng},#{uSol}))REPRESENTATION_CONTEXT('',''))");
        int origin = c.E($"AXIS2_PLACEMENT_3D('',#{c.Pt(Vector3d.Zero)},#{c.Dir(Vector3d.UnitZ)},#{c.Dir(Vector3d.UnitX)})");
        c.E($"ADVANCED_BREP_SHAPE_REPRESENTATION('{Esc(name)}',(#{origin},{string.Join(",", solids.Select(x => "#" + x))}),#{ctx})");
        if (skipped > 0) warnings?.Add($"Parasolid: {skipped} face(s) with an unsupported surface were left out ({string.Join(", ", skippedKinds)}).");
        var w = new StringBuilder();
        w.AppendLine("ISO-10303-21;");
        w.AppendLine("HEADER;");
        w.AppendLine("FILE_DESCRIPTION(('Parametric3D import of a Parasolid body'),'2;1');");
        w.AppendLine($"FILE_NAME('{Esc(name)}','',(''),(''),'Parametric3D','{Esc(files[0].Modeller)}','');");
        w.AppendLine("FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));");
        w.AppendLine("ENDSEC;");
        w.AppendLine("DATA;");
        w.Append(sb);
        w.AppendLine("ENDSEC;");
        w.AppendLine("END-ISO-10303-21;");
        return w.ToString();
    }

    static string Esc(string s) => s.Replace("'", "''");
    static string N(double v) { var s = v.ToString("G17", Inv); if (!s.Contains('.') && !s.Contains('E')) s += "."; else if (s.Contains('E') && !s.Contains('.')) s = s.Replace("E", ".E"); return s; }

    sealed class Ctx
    {
        public StringBuilder Sb = new();
        readonly XtFile f; readonly List<string>? warn;
        public int[] NextBox = { 1 };
        readonly Dictionary<int, int> edges = new(), verts = new(), surfs = new(), curves = new();
        readonly Dictionary<int, bool> surfFlip = new();
        readonly Dictionary<int, int> blendSign = new();
        int curBlend;
        public int Skipped; public readonly HashSet<string> SkippedKinds = new();
        public Ctx(XtFile f, List<string>? w) { this.f = f; warn = w; }

        public int E(string body) { int id = NextBox[0]++; Sb.Append('#').Append(id).Append('=').Append(body).Append(";\n"); return id; }
        public int Pt(Vector3d p) => E($"CARTESIAN_POINT('',({N(p.X * S)},{N(p.Y * S)},{N(p.Z * S)}))");
        public int Dir(Vector3d d) { d = d.LengthSquared > 0 ? d.Normalized() : Vector3d.UnitZ; return E($"DIRECTION('',({N(d.X)},{N(d.Y)},{N(d.Z)}))"); }
        int Ax2(Vector3d o, Vector3d z, Vector3d x)
        {
            z = z.Normalized(); x -= z * Vector3d.Dot(x, z);
            if (x.LengthSquared < 1e-24) x = Math.Abs(z.X) < 0.9 ? Vector3d.Cross(z, Vector3d.UnitX) : Vector3d.Cross(z, Vector3d.UnitY);
            return E($"AXIS2_PLACEMENT_3D('',#{Pt(o)},#{Dir(z)},#{Dir(x)})");
        }

        public int? Face(XtNode fc)
        {
            var sn = f[fc.P("surface")];
            if (sn == null) { Skip("no surface"); return null; }
            int? sid;
            bool flip;
            using (Diagnostics.Prof.Enabled ? Diagnostics.Prof.Time("XT: поверхность " + sn.Name) : null) sid = Surface(sn, out flip);
            if (sid == null) { Skip(sn.Name); return null; }
            bool same = (fc.C("sense") != '-') == (sn.C("sense") != '-');
            if (flip) same = !same;
            if (sn.Name == "BLENDED_EDGE" && !BlendSideOff && BlendSame(fc, sn) is bool bs) same = bs;
            var bounds = new List<int>();
            var boundScore = new List<double>();
            var seenL = new HashSet<int>();
            for (var lp = f[fc.P("loop")]; lp != null && seenL.Add(lp.Id); lp = f[lp.P("next")])
            {
                double score = OuterScore(lp, sn, (fc.C("sense") != '-') == (sn.C("sense") != '-'));
                var oes = new List<int>();
                var h0 = f[lp.P("halfedge")];
                var seenH = new HashSet<int>();
                for (var h = h0; h != null && seenH.Add(h.Id); h = f[h.P("forward")])
                {
                    if (f[h.P("edge")] is not { } e) continue;
                    int? eid = Edge(e);
                    if (eid == null) continue;
                    oes.Add(E($"ORIENTED_EDGE('',*,*,#{eid},{(h.C("sense") != '-' ? ".T." : ".F.")})"));
                }
                if (oes.Count == 0) continue;
                int loop = E($"EDGE_LOOP('',({string.Join(",", oes.Select(x => "#" + x))}))");
                bounds.Add(loop); boundScore.Add(score);
            }
            // k183: НАРУЖНЫЙ КОНТУР — ПЕРВЫМ (FACE_OUTER_BOUND). Порядок петель XT не обещает наружную первой, а импорт берёт
            // первую за наружную: у корпуса 7812K81 плоская грань с отверстием читалась двумя несвязными кусками.
            // Мутация P3D_XTOUTERLOOP=0 — прежний порядок.
            if (bounds.Count > 0)
            {
                int oi = 0;
                if (!OuterLoopOff) for (int i = 1; i < bounds.Count; i++) if (boundScore[i] > boundScore[oi]) oi = i;
                var ordered = new List<int>();
                ordered.Add(E($"FACE_OUTER_BOUND('',#{bounds[oi]},.T.)"));
                for (int i = 0; i < bounds.Count; i++) if (i != oi) ordered.Add(E($"FACE_BOUND('',#{bounds[i]},.T.)"));
                bounds = ordered;
            }
            // грань без рёбер — вся замкнутая поверхность (шарик подшипника): грань без контуров
            if (bounds.Count == 0 && sn.Name is not ("SPHERE" or "TORUS" or "B_SURFACE" or "SPUN_SURF")) { Skip("face without edges"); return null; }
            return E($"ADVANCED_FACE('',({string.Join(",", bounds.Select(x => "#" + x))}),#{sid},{(same ? ".T." : ".F.")})");
        }

        static readonly bool BlendSideOff = Environment.GetEnvironmentVariable("P3D_XTBLENDSIDE") == "0";

        /// <summary>
        /// k183: СТОРОНА ГРАНИ СКРУГЛЕНИЯ — ПО ОПОРЕ. Наружная нормаль скругления в точке касания совпадает с наружной нормалью
        /// соседней грани опоры (касание); обе — вдоль радиуса шара. Знак радиуса у опоры — из её естественной нормали и знаков
        /// грани; у нашей поверхности — из построения (blendSign). Так сторона не зависит от того, как понимать параметры
        /// скругления XT при оси со знаком «−» (винт с полукруглой головкой: грань шлица была вывернута).
        /// </summary>
        bool? BlendSame(XtNode fc, XtNode sn)
        {
            if (!blendSign.TryGetValue(sn.Id, out int sOurs) || XtSurf.Of(f, sn) is not XtBlendSurf bl) { if (XtDebug) warn?.Add($"XTDBG blend {sn.Id}: no sign"); return null; }
            // опоры скругления (у смещённой — и её основа): соседняя грань на опоре касается скругления по ребру
            var sup = new HashSet<int>();
            foreach (int id in sn.PA("surface")) { sup.Add(id); if (f[id] is { Name: "OFFSET_SURF" } os) sup.Add(os.P("surface")); }
            foreach (bool supportsOnly in new[] { true, false })
            {
                var seenL = new HashSet<int>();
                for (var lp = f[fc.P("loop")]; lp != null && seenL.Add(lp.Id); lp = f[lp.P("next")])
                {
                    var seenH = new HashSet<int>();
                    for (var h = f[lp.P("halfedge")]; h != null && seenH.Add(h.Id); h = f[h.P("forward")])
                    {
                        var o = f[h.P("other")]; if (o == null) continue;
                        var of = f[f[o.P("loop")]?.P("face") ?? 1]; if (of == null) continue;
                        var osn = f[of.P("surface")]; if (osn == null || osn.Name == "BLENDED_EDGE") continue;
                        if (supportsOnly != sup.Contains(osn.Id)) continue;
                        // точка — середина ребра (по двум вершинам, спроецированная на соседа): у вершины-угла центр шара неточен
                        Vector3d? a0 = f[f[h.P("vertex")]?.P("point") ?? 1]?.V("pvec"), b0 = f[f[o.P("vertex")]?.P("point") ?? 1]?.V("pvec");
                        if (a0 == null && b0 == null) continue;
                        var mid = a0 != null && b0 != null ? (a0.Value + b0.Value) / 2 : (a0 ?? b0)!.Value;
                        var P = XtSurf.Of(f, osn) is { } xs ? xs.Project(mid) : mid;
                        var n = NatNormal(osn, P); if (n == null || !(n.Value.LengthSquared > 0)) continue;   // малые детали: нормаль по разностям ~10⁻¹⁸ м²
                        var nh = n.Value.Normalized();
                        // центр шара — на нормали соседа на расстоянии радиуса, с той стороны, где ось скругления ближе
                        // (у опор-NURBS уточнение центра на смещённых опорах ненадёжно: «нет касательного соседа» у сверла)
                        double r = Math.Max(bl.R0, bl.R1);
                        double cosR;
                        if (r > 1e-12)
                        {
                            var cp = P + nh * r; var cm = P - nh * r;
                            double dp = (bl.SpineNear(cp) - cp).Length, dm = (bl.SpineNear(cm) - cm).Length;
                            if (Math.Min(dp, dm) > 0.2 * r && !supportsOnly) continue;          // сосед не касается шара
                            cosR = dp < dm ? -1 : 1;                                             // P − c вдоль −n или +n
                        }
                        else
                        {
                            var c = bl.CentreAt(P); var rad = P - c; if (rad.LengthSquared < 1e-24) continue;
                            cosR = Vector3d.Dot(nh, rad.Normalized()); if (Math.Abs(cosR) < 0.9) continue;
                        }
                        double so = cosR * ((of.C("sense") != '-') == (osn.C("sense") != '-') ? 1 : -1);
                        if (XtDebug) warn?.Add($"XTDBG blend {sn.Id} face {fc.Id}: neighbour {of.Id} {osn.Name} support={supportsOnly} cos {cosR:F3} faceSense {of.C("sense")} surfSense {osn.C("sense")} ours {sOurs} -> same {(so > 0) == (sOurs > 0)}");
                        return (so > 0) == (sOurs > 0);
                    }
                }
            }
            if (XtDebug) warn?.Add($"XTDBG blend {sn.Id}: no tangent neighbour");
            return null;
        }
        static readonly bool XtDebug = Environment.GetEnvironmentVariable("P3D_XTDEBUG") == "1";

        /// <summary>Естественная нормаль поверхности XT в точке (не единичная), null — вид не поддержан.</summary>
        Vector3d? NatNormal(XtNode s, Vector3d q)
        {
            switch (s.Name)
            {
                case "PLANE": return s.V("normal");
                case "CYLINDER": { var a = s.V("axis").Normalized(); var d = q - s.V("pvec"); return d - a * Vector3d.Dot(d, a); }
                case "CONE":
                {
                    var a = s.V("axis").Normalized(); var d = q - s.V("pvec"); var e = d - a * Vector3d.Dot(d, a);
                    if (e.LengthSquared < 1e-30) return null; e.Normalize();
                    return e * s.D("cos_half_angle") - a * s.D("sin_half_angle");
                }
                case "SPHERE": return q - s.V("centre");
                case "OFFSET_SURF": return f[s.P("surface")] is { } ob ? NatNormal(ob, q) : null;
                case "TORUS":
                {
                    var a = s.V("axis").Normalized(); var d = q - s.V("centre"); var e = d - a * Vector3d.Dot(d, a);
                    if (e.LengthSquared < 1e-30) return null;
                    return q - (s.V("centre") + e.Normalized() * s.D("major_radius"));
                }
                case "B_SURFACE":
                    if (XtSurf.Of(f, s) is XtNurbsSurf ns && ns.ProjectUv(q) is var (u, v))
                    {
                        var (u0, u1, v0, v1) = ns.Range; double hu = 1e-6 * (u1 - u0), hv = 1e-6 * (v1 - v0);
                        return Vector3d.Cross(ns.At(Math.Min(u1, u + hu), v) - ns.At(Math.Max(u0, u - hu), v), ns.At(u, Math.Min(v1, v + hv)) - ns.At(u, Math.Max(v0, v - hv)));
                    }
                    return null;
            }
            return null;
        }

        static readonly bool OuterLoopOff = Environment.GetEnvironmentVariable("P3D_XTOUTERLOOP") == "0";

        /// <summary>
        /// Оценка «наружности» петли: у плоскости — площадь со знаком относительно наружной нормали грани (наружная петля
        /// обходит нормаль против часовой — площадь положительна и наибольшая; отверстия — отрицательны); у прочих поверхностей —
        /// диагональ габарита петли. Точки петли — вершины плавников и дуги окружностей между ними.
        /// </summary>
        double OuterScore(XtNode lp, XtNode sn, bool faceSameAsSurface)
        {
            var pts = new List<Vector3d>();
            var seenH = new HashSet<int>();
            for (var h = f[lp.P("halfedge")]; h != null && seenH.Add(h.Id); h = f[h.P("forward")])
            {
                var o = f[h.P("other")];
                bool fwd = h.C("sense") != '-';
                // начало хода по плавнику: вершина предыдущего (вершина плавника — его конец)
                var vEnd = f[h.P("vertex")]; var vStart = o != null ? f[o.P("vertex")] : null;
                Vector3d? pe = vEnd != null ? f[vEnd.P("point")]?.V("pvec") : null, ps = vStart != null ? f[vStart.P("point")]?.V("pvec") : null;
                var e = f[h.P("edge")]; var cn = e != null ? f[e.P("curve")] : null;
                if (cn != null && cn.Name == "CIRCLE")
                {
                    var c = cn.V("centre"); var z = cn.V("normal").Normalized(); var x = cn.V("x_axis").Normalized(); var y = Vector3d.Cross(z, x); double r = cn.D("radius");
                    // направление хода по окружности: знак кривой и знак плавника
                    bool ccw = (cn.C("sense") != '-') == fwd;
                    double A(Vector3d q) => Math.Atan2(Vector3d.Dot(q - c, y), Vector3d.Dot(q - c, x));
                    double a0, sw;
                    if (ps != null && pe != null && (ps.Value - pe.Value).Length > 1e-12)
                    {
                        a0 = A(ps.Value); double a1 = A(pe.Value);
                        sw = ccw ? a1 - a0 : a0 - a1; sw = ((sw % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI); if (!ccw) sw = -sw;
                    }
                    else { a0 = pe != null ? A(pe.Value) : 0; sw = ccw ? 2 * Math.PI : -2 * Math.PI; }
                    for (int i = 0; i < 32; i++) { double t = a0 + sw * i / 32; pts.Add(c + (x * Math.Cos(t) + y * Math.Sin(t)) * r); }
                }
                else if (ps != null) pts.Add(ps.Value);
            }
            if (pts.Count < 2) return 0;
            if (sn.Name == "PLANE")
            {
                var n = sn.V("normal").Normalized() * (faceSameAsSurface ? 1 : -1);
                var area = Vector3d.Zero; var c0 = pts[0];
                for (int i = 1; i + 1 < pts.Count; i++) area += Vector3d.Cross(pts[i] - c0, pts[i + 1] - c0);
                return Math.Abs(Vector3d.Dot(area, n)) * 0.5;   // наружная охватывает отверстия — наибольшая по модулю
            }
            var lo = pts[0]; var hi = pts[0];
            foreach (var q in pts) { lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q); }
            return (hi - lo).Length;
        }

        void Skip(string kind) { Skipped++; SkippedKinds.Add(kind); }

        int Vertex(XtNode v)
        {
            if (verts.TryGetValue(v.Id, out var id)) return id;
            var p = f[v.P("point")]?.V("pvec") ?? Vector3d.Zero;
            return verts[v.Id] = E($"VERTEX_POINT('',#{Pt(p)})");
        }

        int? Edge(XtNode e)
        {
            if (edges.TryGetValue(e.Id, out var id)) return id;
            var h = f[e.P("halfedge")];
            if (h == null) return null;
            var o = f[h.P("other")];
            // forward vertex положительного плавника — конец ребра, вершина другого — начало
            var vEnd = h.C("sense") != '-' ? f[h.P("vertex")] : o == null ? null : f[o.P("vertex")];
            var vStart = h.C("sense") != '-' ? (o == null ? null : f[o.P("vertex")]) : f[h.P("vertex")];
            var cn = f[e.P("curve")];
            bool sense = true;
            int? cid = null;
            Vector3d? ringStart = null;
            Vector3d? pS = vStart != null ? f[vStart.P("point")]?.V("pvec") : null, pE = vEnd != null ? f[vEnd.P("point")]?.V("pvec") : null;
            // кривые свободной формы (пересечение, B-кривая, кривая на поверхности) — плотной ломаной от начала ребра до конца,
            // затем кубическим B-сплайном: в файле они длиннее ребра (пределы кривой) или заданы редкой картой
            List<Vector3d>? free;
            using (Diagnostics.Prof.Enabled ? Diagnostics.Prof.Time("XT: кривая " + (cn?.Name ?? "допусковая")) : null)
                free = cn != null && (Dense(cn) is { Count: >= 2 } dn) ? dn : cn == null ? TolerantDense(h) : null;
            if (free != null)
            {
                bool closed = pS == null || pE == null || (pS.Value - pE.Value).Length < 1e-12;
                List<Vector3d> pts;
                // k185: ОТКРЫТОЕ РЕБРО НА ЗАМКНУТОЙ КРИВОЙ (карта пересечения — кольцо): участок от начала до конца ребра — по ходу
                // кривой (против хода при sense '-'), через стык кольца, а не по возрастанию номеров точек карты: прежде ребро
                // шло дополнительной дугой (300° вместо 60°), контуры плоских граней пересекали себя, сетка рвалась (тепловое
                // реле 7254K613). Мутация P3D_XTRINGDIR=0.
                if (!closed && !RingDirOff && XtPoly.IsRing(free))
                {
                    bool fw = cn == null || cn.C("sense") != '-';
                    pts = XtPoly.TrimRing(free, pS!.Value, pE!.Value, fw); sense = true;
                    if (IntDbg)
                    {
                        static double Len(List<Vector3d> q) { double l = 0; for (int i = 1; i < q.Count; i++) l += (q[i] - q[i - 1]).Length; return l; }
                        var other = XtPoly.TrimRing(free, pS!.Value, pE!.Value, !fw); var old = XtPoly.Trim(free, pS!.Value, pE!.Value);
                        Console.Error.WriteLine($"XTRING edge {e.Id} curve {cn?.Id} {cn?.Name} sense {cn?.C("sense")} chosen {Len(pts) * 1000:G4} mm other {Len(other) * 1000:G4} old {Len(old) * 1000:G4}");
                    }
                }
                else if (!closed) { pts = XtPoly.Trim(free, pS!.Value, pE!.Value); sense = true; }
                else
                {
                    pts = free; sense = cn == null || cn.C("sense") != '-';
                    // кольцо с вершиной: ломаная начинается и кончается точно в вершине (прежде — в начале кривой файла,
                    // и замкнутое ребро «не замыкалось» на свою вершину)
                    if (pS != null && pts.Count > 2)
                    {
                        var ring = pts.ToList(); if ((ring[0] - ring[^1]).Length < 1e-9) ring.RemoveAt(ring.Count - 1);
                        int bi = 0; double bd = double.MaxValue;
                        for (int i = 0; i < ring.Count; i++) { double d = (ring[i] - pS.Value).LengthSquared; if (d < bd) { bd = d; bi = i; } }
                        pts = new List<Vector3d> { pS.Value };
                        for (int i = 1; i < ring.Count; i++) pts.Add(ring[(bi + i) % ring.Count]);
                        pts.Add(pS.Value);
                        if (Math.Sqrt(bd) > 1e-6) warn?.Add("Parasolid: a closed free-form edge does not pass through its vertex.");
                    }
                    else if (pts.Count > 2)
                    {
                        // кольцо без вершины: конец ломаной — точно её начало (у карты пересечения последняя точка отстоит
                        // от первой на доли микрона, и замкнутое ребро «не замыкалось»)
                        pts = pts.ToList();
                        if ((pts[^1] - pts[0]).Length < 1e-6) pts[^1] = pts[0]; else pts.Add(pts[0]);
                    }
                }
                ringStart = pts[0];
                using (Diagnostics.Prof.Enabled ? Diagnostics.Prof.Time("XT: сплайн по точкам") : null) cid = Interp(pts);
            }
            else if (cn == null)
            {
                // ребро с допуском: кривая — у плавников (обрезанная кривая на поверхности)
                int guard = 0;
                for (var x = h; x != null && guard++ < 64; x = f[x.P("other")] is { } oo && oo.Id != h.Id ? oo : null)
                    if (f[x.P("curve")] is { } fcv && CurveOf(fcv, out sense, out ringStart) is int c0)
                    {
                        cid = c0; if (x.C("sense") == '-') sense = !sense; break;
                    }
            }
            else cid = CurveOf(cn, out sense, out ringStart);
            if (cid == null) { warn?.Add($"Parasolid: an edge curve ({cn?.Name ?? "none"}) is not supported; the edge is left out."); return null; }
            int vs, ve;
            if (vStart != null && vEnd != null) { vs = Vertex(vStart); ve = Vertex(vEnd); }
            else
            {
                // кольцевое ребро без вершин — одна вершина в начале кривой
                var p = ringStart ?? Vector3d.Zero;
                vs = ve = E($"VERTEX_POINT('',#{Pt(p)})");
            }
            // k184: ДОПУСК РЕБРА XT (допусковое ребро Parasolid: кривая — у плавников, грани сходятся в пределах допуска) —
            // в имени ребра: STEP AP214 своего поля для него не имеет, а без него проверка видела отход кривой плавника от второй
            // грани в 8 мкм при допуске ребра 7.5 мкм (панель 4574N11). Читается нашим импортом; прочим — просто имя.
            // допуск — наибольший из допусков ребра и его вершин (прямая между допусковыми вершинами отходит от граней на их
            // допуск: клеммник 7641K35), вдвое — запас на перевод кривой плавника в сплайн по точкам
            double etl = 0;
            foreach (var tn in new[] { e, vStart, vEnd }) if (tn?.D("tolerance") is double t0 && t0 > 1e-10 && t0 < 1e-3) etl = Math.Max(etl, t0);
            string nm = etl > 0 ? $"P3D_TOL={N(2 * etl * S)}" : "";
            return edges[e.Id] = E($"EDGE_CURVE('{nm}',#{vs},#{ve},#{cid},{(sense ? ".T." : ".F.")})");
        }

        /// <summary>Кривая STEP; sense — совпадает ли направление ребра с направлением кривой; ringStart — точка начала кривой.</summary>
        int? CurveOf(XtNode cn, out bool sense, out Vector3d? ringStart)
        {
            sense = cn.C("sense") != '-';
            ringStart = null;
            switch (cn.Name)
            {
                case "LINE":
                {
                    ringStart = cn.V("pvec");
                    if (curves.TryGetValue(cn.Id, out var id)) return id;
                    int v = E($"VECTOR('',#{Dir(cn.V("direction"))},1.)");
                    return curves[cn.Id] = E($"LINE('',#{Pt(cn.V("pvec"))},#{v})");
                }
                case "CIRCLE":
                {
                    ringStart = cn.V("centre") + cn.V("x_axis").Normalized() * cn.D("radius");
                    if (curves.TryGetValue(cn.Id, out var id)) return id;
                    return curves[cn.Id] = E($"CIRCLE('',#{Ax2(cn.V("centre"), cn.V("normal"), cn.V("x_axis"))},{N(cn.D("radius") * S)})");
                }
                case "ELLIPSE":
                {
                    ringStart = cn.V("centre") + cn.V("x_axis").Normalized() * cn.D("major_radius");
                    if (curves.TryGetValue(cn.Id, out var id)) return id;
                    return curves[cn.Id] = E($"ELLIPSE('',#{Ax2(cn.V("centre"), cn.V("normal"), cn.V("x_axis"))},{N(cn.D("major_radius") * S)},{N(cn.D("minor_radius") * S)})");
                }
                case "TRIMMED_CURVE":
                {
                    var b = f[cn.P("basis_curve")];
                    if (b == null) return null;
                    var r = CurveOf(b, out sense, out ringStart);
                    ringStart = cn.V("point_1");
                    return r;
                }
                case "B_CURVE":
                {
                    var nb = f[cn.P("nurbs")];
                    if (nb == null) return null;
                    if (curves.TryGetValue(cn.Id, out var id)) { ringStart = BCurveStart(nb); return id; }
                    var r = BCurve(nb, out var st);
                    ringStart = st;
                    if (r != null) curves[cn.Id] = r.Value;
                    return r;
                }
                case "INTERSECTION":
                {
                    var ch = f[cn.P("chart")];
                    var pts = ch?.VA("hvec") ?? Array.Empty<Vector3d>();
                    if (pts.Length < 2) return null;
                    ringStart = pts[0];
                    if (curves.TryGetValue(cn.Id, out var id)) return id;
                    return curves[cn.Id] = Interp(pts);
                }
                case "SP_CURVE":
                {
                    var sf = f[cn.P("surface")]; var bc = f[cn.P("b_curve")];
                    if (sf == null || bc == null || XtSurf.Of(f, sf) is not { } xs || f[bc.P("nurbs")] is not { } nb2) return null;
                    var pts = SampleSp(nb2, xs);
                    if (pts.Count < 2) return null;
                    ringStart = pts[0];
                    if (curves.TryGetValue(cn.Id, out var id)) return id;
                    return curves[cn.Id] = Interp(pts);
                }
            }
            return null;
        }

        /// <summary>Кубический B-сплайн через точки (кривая пересечения по карте Parasolid): ломаная восстанавливалась импортом
        /// в дуги по цепочкам хорд — у винтовых кривых резьбы дуга уходила с поверхности на миллиметры.</summary>
        int Interp(IReadOnlyList<Vector3d> raw)
        {
            var q = new List<Vector3d>();
            foreach (var p in raw) if (q.Count == 0 || (q[^1] - p).Length > 1e-12) q.Add(p);
            if (q.Count < 2 || PolyMode) return Poly(raw);
            // k184: ТОЧКА ВПЛОТНУЮ К СОСЕДНЕЙ убирается (кроме концов). Обрезка карты пересечения по вершине ставит вершину рядом
            // с точкой карты (звено 0.012 при соседних 0.2): кубический сплайн через такую пару раскачивается, вершины уходят на
            // миллиметры, и ребро отходит от своих поверхностей (гнездо 1423N12: 2.7 мм от плоскости). Мутация P3D_XTINTERPCLEAN=0.
            if (!InterpCleanOff && q.Count > 3)
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    bool dropped = false;
                    for (int i = 1; i + 1 < q.Count && q.Count > 3; i++)
                    {
                        double a = (q[i] - q[i - 1]).Length, b = (q[i + 1] - q[i]).Length;
                        if (Math.Min(a, b) < 0.15 * Math.Max(a, b) && Math.Min(a, b) < 0.15 * (i - 2 >= 0 && i + 2 < q.Count ? Math.Max((q[i - 1] - q[i - 2]).Length, (q[i + 2] - q[i + 1]).Length) : Math.Max(a, b)))
                        { q.RemoveAt(i); dropped = true; i--; }
                    }
                    if (!dropped) break;
                }
            }
            int deg = Math.Min(3, q.Count - 1);
            var prm = XtBasis.Chord(q);
            var (k, _) = XtBasis.Setup(prm, deg);
            var cp = XtBasis.Solve(k, deg, prm, q);
            // k184: проверка раскачки — сплайн между соседними точками не должен уходить от их хорды дальше четверти звена;
            // иначе — ломаная по тем же точкам (точно через них, без выбросов). Мутация P3D_XTINTERPCHECK=0.
            if (!InterpCheckOff && deg == 3)
            {
                double Turn(int m) { if (m <= 0 || m + 1 >= q.Count) return 0; var x = q[m] - q[m - 1]; var y = q[m + 1] - q[m]; double cs = Vector3d.Dot(x, y) / Math.Max(1e-300, x.Length * y.Length); return Math.Acos(Math.Clamp(cs, -1, 1)); }
                bool Wild(double[] kk, double[] pr, Vector3d[] cc)
                {
                    int ncp = cc.Length;
                    for (int i = 0; i + 1 < q.Count; i++)
                    {
                        double tm = 0.5 * (pr[i] + pr[i + 1]);
                        var N = XtBasis.Eval(kk, deg, ncp, tm); var c = Vector3d.Zero;
                        for (int j = 0; j < ncp; j++) if (N[j] != 0) c += cc[j] * N[j];
                        var ab = q[i + 1] - q[i]; double l = ab.Length;
                        double t = l > 0 ? Math.Clamp(Vector3d.Dot(c - q[i], ab) / (l * l), 0, 1) : 0;
                        // ожидаемый прогиб — по повороту соседних звеньев (дуга с поворотом α на звене l отходит на ≈ l·α/8)
                        double expect = l * Math.Max(Turn(i), Turn(i + 1)) / 8;
                        double dev = (q[i] + ab * t - c).Length;
                        // единицы XT — метры: запас 2 мкм
                        if (dev > 0.25 * l + 1e-9 || dev > 4 * expect + 2e-6) return true;
                    }
                    return false;
                }
                if (Wild(k, prm, cp))
                {
                    // центростремительные параметры (корень из хорды) раскачиваются меньше при неравных звеньях; не помогло — ломаная
                    var pc = new double[q.Count]; double acc = 0;
                    for (int i = 1; i < q.Count; i++) { acc += Math.Sqrt((q[i] - q[i - 1]).Length); pc[i] = acc; }
                    for (int i = 1; i < q.Count; i++) pc[i] /= acc;
                    var (k2, _) = XtBasis.Setup(pc, deg);
                    var cp2 = XtBasis.Solve(k2, deg, pc, q);
                    if (Wild(k2, pc, cp2)) return Poly(q);
                    k = k2; cp = cp2;
                }
            }
            var ks = new List<double>(); var ms = new List<int>();
            foreach (var x in k) { if (ks.Count > 0 && Math.Abs(ks[^1] - x) < 1e-14) ms[^1]++; else { ks.Add(x); ms.Add(1); } }
            string P = string.Join(",", cp.Select(p => "#" + Pt(p)));
            return E($"B_SPLINE_CURVE_WITH_KNOTS('',{deg},({P}),.UNSPECIFIED.,.F.,.F.,({string.Join(",", ms)}),({string.Join(",", ks.Select(N))}),.UNSPECIFIED.)");
        }
        static readonly bool IntDbg = Environment.GetEnvironmentVariable("P3D_XTINTDBG") == "1";
        static readonly bool PolyMode = Environment.GetEnvironmentVariable("P3D_XTPOLY") == "1";
        static readonly bool RingDirOff = Environment.GetEnvironmentVariable("P3D_XTRINGDIR") == "0";
        static readonly bool InterpCleanOff = Environment.GetEnvironmentVariable("P3D_XTINTERPCLEAN") == "0";
        static readonly bool InterpCheckOff = Environment.GetEnvironmentVariable("P3D_XTINTERPCHECK") == "0";
        static readonly bool ConeFlipOff = Environment.GetEnvironmentVariable("P3D_XTCONEFLIP") != "1";   // естественная нормаль конуса XT (P + vA + …) — наружу, как у STEP

        /// <summary>Плотная ломаная кривой свободной формы в естественном направлении; null — аналитическая кривая (прямая,
        /// окружность, эллипс — их обрезает сам импорт по вершинам).</summary>
        List<Vector3d>? Dense(XtNode cn, int depth = 0)
        {
            if (depth > 4) return null;
            switch (cn.Name)
            {
                case "INTERSECTION":
                {
                    var ch = f[cn.P("chart")]; var pts = ch?.VA("hvec");
                    if (pts == null || pts.Length < 2) return null;
                    var ss = cn.PA("surface");
                    XtSurf? a = ss.Length > 0 && f[ss[0]] is { } a0 ? XtSurf.Of(f, a0) : null, b = ss.Length > 1 && f[ss[1]] is { } b0 ? XtSurf.Of(f, b0) : null;
                    var dz = XtPoly.Densify(pts, a, b, 8);
                    if (IntDbg && a != null && b != null)
                    {
                        double wa = 0, wb = 0; foreach (var q in dz) { wa = Math.Max(wa, (a.Project(q) - q).Length); wb = Math.Max(wb, (b.Project(q) - q).Length); }
                        if (wa > 1e-7 || wb > 1e-7) Console.Error.WriteLine($"XTINT node {cn.Id} chart {pts.Length} dense {dz.Count} offA {wa * 1000:G3} mm ({a.GetType().Name}) offB {wb * 1000:G3} mm ({b.GetType().Name})");
                    }
                    return dz;
                }
                case "B_CURVE":
                {
                    if (f[cn.P("nurbs")] is not { } nb || XtNurbsCurve.Of(f, nb) is not { Dim: 3 } nc) return null;
                    return nc.Params(Math.Max(8, 4 * nc.Deg)).Select(nc.At3).ToList();
                }
                case "TRIMMED_CURVE":
                {
                    var bc = f[cn.P("basis_curve")];
                    if (bc == null || bc.Name is "LINE" or "CIRCLE" or "ELLIPSE") return null;
                    var d = Dense(bc, depth + 1);
                    if (d == null) return null;
                    return XtPoly.Trim(d, cn.V("point_1"), cn.V("point_2"));
                }
                case "SP_CURVE":
                {
                    var sf = f[cn.P("surface")]; var bcv = f[cn.P("b_curve")];
                    if (sf == null || bcv == null || XtSurf.Of(f, sf) is not { } xs || f[bcv.P("nurbs")] is not { } nb2) return null;
                    var pts = SampleSp(nb2, xs);
                    return pts.Count >= 2 ? pts : null;
                }
            }
            return null;
        }

        /// <summary>Ребро с допуском (без своей кривой): ломаная по кривой первого плавника, в направлении ребра.</summary>
        List<Vector3d>? TolerantDense(XtNode h)
        {
            int guard = 0;
            for (var x = h; x != null && guard++ < 64; x = f[x.P("other")] is { } oo && oo.Id != h.Id ? oo : null)
                if (f[x.P("curve")] is { } fc && Dense(fc) is { Count: >= 2 } d)
                {
                    bool rev = (fc.C("sense") == '-') != (x.C("sense") == '-');
                    if (rev) d.Reverse();
                    return d;
                }
            return null;
        }

        int Poly(IReadOnlyList<Vector3d> pts)
        {
            var ids = new List<int>();
            Vector3d? prev = null;
            foreach (var p in pts) { if (prev is { } q && (q - p).Length < 1e-12) continue; ids.Add(Pt(p)); prev = p; }
            if (ids.Count < 2) ids.Add(Pt(pts[^1] + new Vector3d(1e-9, 0, 0)));
            return E($"POLYLINE('',({string.Join(",", ids.Select(x => "#" + x))}))");
        }

        Vector3d? BCurveStart(XtNode nb)
        {
            int vd = nb.I("vertex_dim"); var vs = f[nb.P("bspline_vertices")]?.DA("vertices");
            if (vs == null || vs.Length < vd || vd < 3) return null;
            var p = new Vector3d(vs[0], vs[1], vs[2]);
            return nb.L("rational") && vd >= 4 && vs[3] != 0 ? p / vs[3] : p;
        }

        int? BCurve(XtNode nb, out Vector3d? start)
        {
            start = null;
            int deg = nb.I("degree"), n = nb.I("n_vertices"), vd = nb.I("vertex_dim");
            bool rat = nb.L("rational");
            var vs = f[nb.P("bspline_vertices")]?.DA("vertices");
            var kn = f[nb.P("knots")]?.DA("knots"); var km = f[nb.P("knot_mult")]?.IA("mult");
            if (vs == null || kn == null || km == null || vd < 3 || vs.Length < n * vd) return null;
            var pts = new List<int>(); var ws = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double w = rat ? vs[i * vd + vd - 1] : 1;
                var p = new Vector3d(vs[i * vd], vs[i * vd + 1], vs[i * vd + 2]) / (rat && w != 0 ? w : 1);
                if (i == 0) start = p;
                pts.Add(Pt(p)); ws.Add(w);
            }
            string P = string.Join(",", pts.Select(x => "#" + x));
            string M = string.Join(",", km.Take(kn.Length)); string K = string.Join(",", kn.Select(N));
            string closed = nb.L("closed") ? ".T." : ".F.";
            if (!rat) return E($"B_SPLINE_CURVE_WITH_KNOTS('',{deg},({P}),.UNSPECIFIED.,{closed},.F.,({M}),({K}),.UNSPECIFIED.)");
            return E($"(BOUNDED_CURVE()B_SPLINE_CURVE({deg},({P}),.UNSPECIFIED.,{closed},.F.)B_SPLINE_CURVE_WITH_KNOTS(({M}),({K}),.UNSPECIFIED.)CURVE()GEOMETRIC_REPRESENTATION_ITEM()RATIONAL_B_SPLINE_CURVE(({string.Join(",", ws.Select(N))}))REPRESENTATION_ITEM(''))");
        }

        List<Vector3d> SampleSp(XtNode nb, XtSurf xs)
        {
            int deg = nb.I("degree"), n = nb.I("n_vertices"), vd = nb.I("vertex_dim");
            bool rat = nb.L("rational");
            var vs = f[nb.P("bspline_vertices")]?.DA("vertices") ?? Array.Empty<double>();
            var k = XtNurbsSurf.Expand(f, nb.P("knots"), nb.P("knot_mult"));
            var res = new List<Vector3d>();
            if (vd < 2 || vs.Length < n * vd || k.Length != n + deg + 1) return res;
            double t0 = k[deg], t1 = k[n];
            int m = Math.Max(16, 8 * (n - deg));
            Vector3d F(double t)
            {
                var b = XtBasis.Eval(k, deg, n, t);
                double u = 0, v = 0, w = 0;
                for (int i = 0; i < n; i++)
                {
                    if (b[i] == 0) continue;
                    double wi = rat ? vs[i * vd + vd - 1] : 1;
                    u += b[i] * vs[i * vd]; v += b[i] * vs[i * vd + 1]; w += b[i] * wi;
                }
                if (rat && w != 0) { u /= w; v /= w; }
                return xs.At(u, v);
            }
            // k182: дробление по отклонению от хорды — прямая в параметрах резьбовой поверхности даёт в пространстве винтовую
            // линию в десятки витков (гайка: 16 точек на 8 витков — хорда отходила от резьбы на 14 мм)
            return XtPoly.Adaptive(F, t0, t1, m);
        }

        int? Surface(XtNode sn, out bool flip)
        {
            flip = sn.Name == "CONE" && !ConeFlipOff;
            if (surfs.TryGetValue(sn.Id, out var id)) { flip = surfFlip[sn.Id]; return id; }
            int? r = null;
            switch (sn.Name)
            {
                case "PLANE": r = E($"PLANE('',#{Ax2(sn.V("pvec"), sn.V("normal"), sn.V("x_axis"))})"); break;
                case "CYLINDER": r = E($"CYLINDRICAL_SURFACE('',#{Ax2(sn.V("pvec"), sn.V("axis"), sn.V("x_axis"))},{N(sn.D("radius") * S)})"); break;
                case "CONE":
                    // радиус растёт ПО оси A (проверено по окружностям рёбер на конусах всех десяти файлов: в справочнике XT
                    // формула напечатана как P − vA + …, но окружности ложатся на конус только при P + vA + …): ось STEP — та же
                    r = E($"CONICAL_SURFACE('',#{Ax2(sn.V("pvec"), sn.V("axis"), sn.V("x_axis"))},{N(sn.D("radius") * S)},{N(Math.Atan2(sn.D("sin_half_angle"), sn.D("cos_half_angle")))})"); break;
                case "SPHERE": r = E($"SPHERICAL_SURFACE('',#{Ax2(sn.V("centre"), sn.V("axis"), sn.V("x_axis"))},{N(sn.D("radius") * S)})"); break;
                case "TORUS":
                {
                    // «яблоко» (0 < R ≤ r) и «лимон» (R < 0) Parasolid — DEGENERATE_TOROIDAL_SURFACE с выбором листа
                    double R = sn.D("major_radius"), rr = sn.D("minor_radius");
                    var ax = Ax2(sn.V("centre"), sn.V("axis"), sn.V("x_axis"));
                    r = R > rr ? E($"TOROIDAL_SURFACE('',#{ax},{N(R * S)},{N(rr * S)})")
                               : E($"DEGENERATE_TOROIDAL_SURFACE('',#{ax},{N(Math.Abs(R) * S)},{N(rr * S)},{(R > 0 ? ".T." : ".F.")})");
                    break;
                }
                case "B_SURFACE": if (f[sn.P("nurbs")] is { } ns) r = BSurf(ns); break;
                case "SWEPT_SURF":
                    if (f[sn.P("section")] is { } sc && CurveOf(sc, out _, out _) is int cid)
                    {
                        var sw = sn.V("sweep");
                        r = E($"SURFACE_OF_LINEAR_EXTRUSION('',#{cid},#{E($"VECTOR('',#{Dir(sw)},{N(Math.Max(sw.Length, 1e-9) * S)})")})");
                    }
                    break;
                case "BLENDED_EDGE": r = Blend(sn); break;
                case "OFFSET_SURF":
                    // смещение по естественной нормали основы со знаком её «sense»; естественная нормаль — как у основы
                    if (f[sn.P("surface")] is { } bsn && Surface(bsn, out bool bflip) is int bid)
                    {
                        double d = sn.D("offset") * S * (bsn.C("sense") == '-' ? -1 : 1) * (bflip ? -1 : 1);
                        r = E($"OFFSET_SURFACE('',#{bid},{N(d)},.F.)");
                        flip = bflip;
                    }
                    break;
                case "SPUN_SURF":
                    // ISO 10303-42: у SURFACE_OF_REVOLUTION u — угол, v — образующая; у XT наоборот — нормаль обратная
                    if (f[sn.P("profile")] is { } pf && CurveOf(pf, out _, out _) is int pid)
                    {
                        r = E($"SURFACE_OF_REVOLUTION('',#{pid},#{E($"AXIS1_PLACEMENT('',#{Pt(sn.V("base"))},#{Dir(sn.V("axis"))})")})");
                        flip = true;
                    }
                    break;
            }
            if (r != null) { surfs[sn.Id] = r.Value; surfFlip[sn.Id] = flip; }
            return r;
        }

        int? BSurf(XtNode ns)
        {
            int du = ns.I("u_degree"), dv = ns.I("v_degree"), nu = ns.I("n_u_vertices"), nv = ns.I("n_v_vertices"), vd = ns.I("vertex_dim");
            bool rat = ns.L("rational");
            var vs = f[ns.P("bspline_vertices")]?.DA("vertices");
            var ku = f[ns.P("u_knots")]?.DA("knots"); var kv = f[ns.P("v_knots")]?.DA("knots");
            var mu = f[ns.P("u_knot_mult")]?.IA("mult"); var mv = f[ns.P("v_knot_mult")]?.IA("mult");
            if (vs == null || ku == null || kv == null || mu == null || mv == null || vd < 3 || vs.Length < nu * nv * vd) return null;
            var rows = new List<string>(); var wrows = new List<string>();
            for (int i = 0; i < nu; i++)
            {
                var row = new List<string>(); var wr = new List<string>();
                for (int j = 0; j < nv; j++)
                {
                    int o = (i * nv + j) * vd;
                    double w = rat ? vs[o + vd - 1] : 1;
                    var p = new Vector3d(vs[o], vs[o + 1], vs[o + 2]) / (rat && w != 0 ? w : 1);
                    row.Add("#" + Pt(p)); wr.Add(N(w));
                }
                rows.Add("(" + string.Join(",", row) + ")"); wrows.Add("(" + string.Join(",", wr) + ")");
            }
            string P = string.Join(",", rows);
            string uc = ns.L("u_closed") ? ".T." : ".F.", vc = ns.L("v_closed") ? ".T." : ".F.";
            string knots = $"({string.Join(",", mu.Take(ku.Length))}),({string.Join(",", mv.Take(kv.Length))}),({string.Join(",", ku.Select(N))}),({string.Join(",", kv.Select(N))})";
            if (!rat) return E($"B_SPLINE_SURFACE_WITH_KNOTS('',{du},{dv},({P}),.UNSPECIFIED.,{uc},{vc},.F.,{knots},.UNSPECIFIED.)");
            return E($"(BOUNDED_SURFACE()B_SPLINE_SURFACE({du},{dv},({P}),.UNSPECIFIED.,{uc},{vc},.F.)B_SPLINE_SURFACE_WITH_KNOTS({knots},.UNSPECIFIED.)GEOMETRIC_REPRESENTATION_ITEM()RATIONAL_B_SPLINE_SURFACE(({string.Join(",", wrows)}))REPRESENTATION_ITEM('')SURFACE())");
        }

        /// <summary>
        /// Скругление «катящийся шар»: ось шара (spine) — пересечение смещённых опорных поверхностей; сечение — дуга окружности
        /// с центром на оси от точки касания первой опорной (v = 0) до второй (v = 1; при обратной оси — наоборот). Поверхность —
        /// интерполяция сечений B-сплайном (по оси — кубическая по точкам оси, поперёк — кубическая по 9 точкам дуги).
        /// </summary>
        static readonly bool BlendArcOff = Environment.GetEnvironmentVariable("P3D_XTBLENDARC") == "0";

        int? Blend(XtNode sn)
        {
            var bl = XtSurf.Of(f, sn) as XtBlendSurf; curBlend = sn.Id;
            if (bl == null || bl.Spine.Count < 2) return null;
            double rad = Math.Max(Math.Max(bl.R0, bl.R1), 1e-6);
            var sp = bl.Spine;
            double len = 0; for (int i = 1; i < sp.Count; i++) len += (sp[i] - sp[i - 1]).Length;
            var cum = new double[sp.Count]; for (int i = 1; i < sp.Count; i++) cum[i] = cum[i - 1] + (sp[i] - sp[i - 1]).Length;
            Vector3d At(double s0)
            {
                int i = Array.BinarySearch(cum, s0); if (i < 0) i = ~i - 1; i = Math.Clamp(i, 0, sp.Count - 2);
                double seg = cum[i + 1] - cum[i]; return seg > 0 ? Vector3d.Lerp(sp[i], sp[i + 1], (s0 - cum[i]) / seg) : sp[i];
            }
            if (!BlendArcOff && ArcBlend(bl, len, rad, At) is int ex) return ex;
            // запасной путь: бикубическая сетка по 9 точкам дуги, сечения не реже чем через радиус шара (от 4 до 80)
            int nu = Math.Clamp((int)Math.Ceiling(len / rad) + 1, 4, 80);
            const int M = 9;
            var grid = new Vector3d[nu, M];
            for (int i = 0; i < nu; i++)
            {
                var arc = bl.Arc(At(len * i / (nu - 1)), M);
                for (int j = 0; j < M; j++) grid[i, j] = arc[j];
            }
            return FitSurface(grid);
        }

        /// <summary>
        /// k182: ПОВЕРХНОСТЬ СКРУГЛЕНИЯ — РАЦИОНАЛЬНАЯ, СЕЧЕНИЯ ТОЧНЫЕ ДУГИ. По v — рациональная квадратичная дуга (три вершины:
        /// точки касания и пересечение касательных, вес среднего cos(θ/2)) — сечение шаром точное; по u — кубическая интерполяция
        /// однородных вершин по сечениям через четверть радиуса (от 8 до 400). Прежде бикубическая сетка по 9 точкам дуги через
        /// радиус отходила от опор на десятые доли миллиметра (гайки: скругление шестигранника с конусом фаски). Мутация
        /// P3D_XTBLENDARC=0 — прежняя сетка.
        /// </summary>
        int? ArcBlend(XtBlendSurf bl, double len, double rad, Func<double, Vector3d> at)
        {
            // шаг сечений по кривизне оси: ошибка кубической интерполяции ≈ 5/384·h⁴/ρ³ (ρ — наименьший радиус кривизны оси)
            // не больше 0,1 мкм; не реже 1,5 радиуса шара, от 8 до 160 сечений (прежде — через четверть радиуса, до 400:
            // гайка с замком размечалась 11 минут — время уходило на сетку граней с сотнями пролётов)
            double rho = double.MaxValue; var spn = bl.Spine;
            for (int i = 1; i + 1 < spn.Count; i++)
            {
                var a = spn[i] - spn[i - 1]; var b = spn[i + 1] - spn[i]; double la = a.Length, lb = b.Length;
                if (la < 1e-12 || lb < 1e-12) continue;
                double ang = Math.Acos(Math.Clamp(Vector3d.Dot(a, b) / (la * lb), -1, 1));
                if (ang > 1e-9) rho = Math.Min(rho, 0.5 * (la + lb) / ang);
            }
            double h = Math.Min(1.5 * rad, rho < double.MaxValue ? Math.Pow(1e-7 * 384 / 5 * rho * rho * rho, 0.25) : double.MaxValue);
            int nu = Math.Clamp((int)Math.Ceiling(len / Math.Max(h, 1e-9)) + 1, 8, 160);
            // сечение — две половины дуги (рациональные квадратичные, пять вершин): вес середины половины cos(θ/4) ≥ 0,7 при
            // любом сечении до полукруга и больше — кубическая интерполяция весов по u не уходит в ноль (у кончика сверла
            // одна дуга почти в полкруга давала отрицательные веса и запасную сетку)
            const int NV = 5;
            var Pw = new Vector3d[nu, NV]; var W = new double[nu, NV]; var cen = new List<Vector3d>(nu);
            Vector3d? prevMid = null;
            for (int i = 0; i < nu; i++)
            {
                var c = bl.CentreAt(at(len * i / (nu - 1))); cen.Add(c);
                var (pa, pb) = bl.Contacts(c);
                var ea = pa - c; var eb = pb - c; double r = 0.5 * (ea.Length + eb.Length);
                if (r < 1e-12 || ea.LengthSquared < 1e-24 || eb.LengthSquared < 1e-24)
                {
                    for (int j = 0; j < NV; j++) { Pw[i, j] = c; W[i, j] = 1; }
                    continue;
                }
                var na = ea.Normalized(); var nb = eb.Normalized();
                var bis = na + nb;
                Vector3d nm;
                if (bis.LengthSquared > 1e-8) nm = bis.Normalized();
                else
                {
                    // полкруга: середина — поворот na на 90° вокруг оси (направление — как у соседнего сечения)
                    var tg = at(Math.Min(len, len * (i + 0.5) / (nu - 1))) - at(Math.Max(0, len * (i - 0.5) / (nu - 1)));
                    nm = Vector3d.Cross(tg, na); if (nm.LengthSquared < 1e-30) { warn?.Add("Parasolid: a blend section has no plane — fitted grid used."); return null; }
                    nm.Normalize(); if (prevMid is { } pm && Vector3d.Dot(pm, nm) < 0) nm = -nm;
                }
                prevMid = nm;
                double cosH = Math.Clamp(Vector3d.Dot(na, nm), -1, 1);          // cos(θ/2)
                double w = Math.Sqrt((1 + cosH) / 2);                             // cos(θ/4)
                var m1 = c + (na + nm).Normalized() * (r / w); var m2 = c + (nm + nb).Normalized() * (r / w);
                Pw[i, 0] = c + na * r; Pw[i, 1] = m1 * w; Pw[i, 2] = c + nm * r; Pw[i, 3] = m2 * w; Pw[i, 4] = c + nb * r;
                W[i, 0] = 1; W[i, 1] = w; W[i, 2] = 1; W[i, 3] = w; W[i, 4] = 1;
            }
            {
                // знак нормали нашей поверхности относительно радиуса шара (в середине оси): du × касательная дуги в A к B
                int m = nu / 2, m0 = Math.Max(0, m - 1), m1 = Math.Min(nu - 1, m + 1);
                var c = cen[m]; var ra = Pw[m, 0] - c; var rb = Pw[m, 2] - c;
                var tA = rb - ra * (Vector3d.Dot(rb, ra) / Math.Max(ra.LengthSquared, 1e-300));
                var nn = Vector3d.Cross(cen[m1] - cen[m0], tA);
                double sg = Vector3d.Dot(nn, ra);
                if (Math.Abs(sg) > 0) blendSign[curBlend] = sg > 0 ? 1 : -1;
            }
            var pu = XtBasis.Chord(cen);
            for (int i = 1; i < nu; i++) if (pu[i] <= pu[i - 1]) { warn?.Add("Parasolid: blend section centres repeat along the spine — fitted grid used."); return null; }
            int du = 3;
            var (ku, _) = XtBasis.Setup(pu, du);
            var P = new Vector3d[nu, NV]; var Wt = new double[nu, NV];
            for (int j = 0; j < NV; j++)
            {
                var xyz = XtBasis.Solve(ku, du, pu, Enumerable.Range(0, nu).Select(i => Pw[i, j]).ToList());
                var ww = XtBasis.Solve(ku, du, pu, Enumerable.Range(0, nu).Select(i => new Vector3d(W[i, j], 0, 0)).ToList());
                for (int i = 0; i < nu; i++)
                {
                    if (ww[i].X <= 1e-9) { warn?.Add("Parasolid: blend weights not positive — fitted grid used."); return null; }
                    Wt[i, j] = ww[i].X; P[i, j] = xyz[i] / ww[i].X;
                }
            }
            var rows = new List<string>(); var wrows = new List<string>();
            for (int i = 0; i < nu; i++)
            {
                rows.Add("(" + string.Join(",", Enumerable.Range(0, NV).Select(j => "#" + Pt(P[i, j]))) + ")");
                wrows.Add("(" + string.Join(",", Enumerable.Range(0, NV).Select(j => N(Wt[i, j]))) + ")");
            }
            var ks = new List<double>(); var ms = new List<int>();
            foreach (var x in ku) { if (ks.Count > 0 && Math.Abs(ks[^1] - x) < 1e-14) ms[^1]++; else { ks.Add(x); ms.Add(1); } }
            string knots = $"({string.Join(",", ms)}),(3,2,3),({string.Join(",", ks.Select(N))}),(0.,0.5,1.)";
            return E($"(BOUNDED_SURFACE()B_SPLINE_SURFACE({du},2,({string.Join(",", rows)}),.UNSPECIFIED.,.F.,.F.,.F.)B_SPLINE_SURFACE_WITH_KNOTS({knots},.UNSPECIFIED.)GEOMETRIC_REPRESENTATION_ITEM()RATIONAL_B_SPLINE_SURFACE(({string.Join(",", wrows)}))REPRESENTATION_ITEM('')SURFACE())");
        }

        List<Vector3d> SampleCurve(XtNode cn)
        {
            var res = new List<Vector3d>();
            switch (cn.Name)
            {
                case "LINE": res.Add(cn.V("pvec") - cn.V("direction") * 1); res.Add(cn.V("pvec") + cn.V("direction") * 1); break;
                case "CIRCLE":
                {
                    var c = cn.V("centre"); var z = cn.V("normal").Normalized(); var x = cn.V("x_axis").Normalized(); var y = Vector3d.Cross(z, x); double r = cn.D("radius");
                    for (int i = 0; i <= 64; i++) { double t = 2 * Math.PI * i / 64; res.Add(c + (x * Math.Cos(t) + y * Math.Sin(t)) * r); }
                    break;
                }
            }
            return res;
        }

        int? FitSurface(Vector3d[,] g)
        {
            int nu = g.GetLength(0), nv = g.GetLength(1);
            int du = Math.Min(3, nu - 1), dv = Math.Min(3, nv - 1);
            if (du < 1 || dv < 1) return null;
            // параметры: средние по хордам рядов
            var pu = new double[nu]; var pv = new double[nv];
            for (int j = 0; j < nv; j++) { var col = Enumerable.Range(0, nu).Select(i => g[i, j]).ToList(); var t = XtBasis.Chord(col); for (int i = 0; i < nu; i++) pu[i] += t[i] / nv; }
            for (int i = 0; i < nu; i++) { var row = Enumerable.Range(0, nv).Select(j => g[i, j]).ToList(); var t = XtBasis.Chord(row); for (int j = 0; j < nv; j++) pv[j] += t[j] / nu; }
            var (ku, _) = XtBasis.Setup(pu, du); var (kv, _) = XtBasis.Setup(pv, dv);
            var tmp = new Vector3d[nu, nv];
            for (int i = 0; i < nu; i++) { var row = XtBasis.Solve(kv, dv, pv, Enumerable.Range(0, nv).Select(j => g[i, j]).ToList()); for (int j = 0; j < nv; j++) tmp[i, j] = row[j]; }
            var P = new Vector3d[nu, nv];
            for (int j = 0; j < nv; j++) { var col = XtBasis.Solve(ku, du, pu, Enumerable.Range(0, nu).Select(i => tmp[i, j]).ToList()); for (int i = 0; i < nu; i++) P[i, j] = col[i]; }
            var rows = new List<string>();
            for (int i = 0; i < nu; i++) rows.Add("(" + string.Join(",", Enumerable.Range(0, nv).Select(j => "#" + Pt(P[i, j]))) + ")");
            static (string M, string K) Compress(double[] k)
            {
                var ks = new List<double>(); var ms = new List<int>();
                foreach (var x in k) { if (ks.Count > 0 && Math.Abs(ks[^1] - x) < 1e-14) ms[^1]++; else { ks.Add(x); ms.Add(1); } }
                return (string.Join(",", ms), string.Join(",", ks.Select(N)));
            }
            var (mu, kus) = Compress(ku); var (mv, kvs) = Compress(kv);
            return E($"B_SPLINE_SURFACE_WITH_KNOTS('',{du},{dv},({string.Join(",", rows)}),.UNSPECIFIED.,.F.,.F.,.F.,({mu}),({mv}),({kus}),({kvs}),.UNSPECIFIED.)");
        }
    }
}
