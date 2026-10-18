using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// ЗАПИСЬ ТЕЛА В IGES. Каждая грань выходит обрезанной поверхностью: сама поверхность плюс её
/// контуры. Так файл несёт не проволоку, а грани — то, что принимающая система и ждёт.
///
/// Что пишется точно: плоскость, цилиндр (выдавливанием окружности), поверхность вращения,
/// сплайновая поверхность. Контуры — отрезками, дугами и сплайнами.
///
/// ЧЕГО МЫ НЕ ДЕЛАЕМ и почему. Не пишем кривые на поверхности (сущность 142) в параметрах грани:
/// для этого нужна развёртка каждого ребра в параметры своей поверхности, а при разных
/// поверхностях у одного ребра — две развёртки. Пока контуры записаны в пространстве, и это
/// честнее: приёмная система сама спроецирует их на поверхность, а не получит приблизительную
/// развёртку под видом точной.
/// </summary>
public static class IgesSolid
{
    /// <summary>Записать тело.</summary>
    public static string Write(TSolid solid, List<string>? warnings = null) =>
        Write(new[] { solid }, warnings);

    /// <summary>
    /// Записать НЕСКОЛЬКО тел в один файл. Деталь может состоять из нескольких тел, и терять их
    /// при выводе нельзя: сущности всех тел идут в одном файле подряд, как и положено IGES.
    /// </summary>
    public static string Write(IEnumerable<TSolid> solids, List<string>? warnings = null)
    {
        if (solids.Any(b => b.Guarantee == GuaranteeLevel.Approximate))
            warnings?.Add("IGES: an approximate body is exported (built by an approximate route).");
        var f = new IgesFile();
        int written = 0, skipped = 0, bodies = 0;
        // первый проход: деления рёбер швами граней вращения (см. _splits)
        _splits = new Dictionary<TEdge, List<double>>(ReferenceEqualityComparer.Instance);
        _pieces = new Dictionary<ICurve3, (TEdge, double, double)>(ReferenceEqualityComparer.Instance);
        if (!PCurveOff)
        {
            var dummy = new IgesFile();
            foreach (var sd in solids) foreach (var fc in sd.Faces) { try { RevolvedFace(dummy, fc); } catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { } }
        }

        foreach (var solid in solids)
        {
            bodies++;
            foreach (var face in solid.Faces)
        {
                // m.x2: грань вращения (цилиндр, конус, шар, тор, поверхность вращения отрезка или дуги) — поверхностью вращения
                // (120) со швом там, где нет местных контуров, и с кривыми границ ещё и в параметрах (как пишет OpenCascade).
                // P3D_IGESPCURVE=0 — прежняя запись.
                int mark = f.Count;
                if (!PCurveOff && NurbsFace(f, face) is int trimmedN)
                {
                    if (!string.IsNullOrEmpty(face.Tag)) f.Label(trimmedN, face.Tag);
                    written++; continue;
                }
                if (!TruncOff) f.Truncate(mark);
                if (!PCurveOff && RevolvedFace(f, face) is int trimmedR)
                {
                    if (!string.IsNullOrEmpty(face.Tag)) f.Label(trimmedR, face.Tag);
                    written++; continue;
                }
                if (!TruncOff) f.Truncate(mark);   // m.x2: неудавшаяся попытка — без ничьих сущностей (P3D_IGESTRUNC=0 — прежде)
                int surface = Surface(f, face, warnings);
                if (surface == 0) { skipped++; continue; }

                int outer = 0;
                var holes = new List<int>();

                // m.x2: ГРАНЬ ВРАЩЕНИЯ С ДВУМЯ ПОЯСАМИ (боковая цилиндра, пояс конуса, тора, шара) — граница одним контуром
                // «пояс A → шов → пояс B → шов назад» (в параметрах — прямоугольник); прежде второй пояс уходил отверстием, и
                // OpenCascade читал грань нулевой площади. Контуры — по ходу полурёбер (дуги — рациональными сплайнами: у дуги
                // IGES (100) обход всегда против часовой). P3D_IGESBELT=0 — прежняя запись.
                if (!BeltOff && Belts(face) is { } bl)
                {
                    outer = bl.Outer;
                    foreach (var loop in bl.Locals) { int curve = LoopOriented(f, loop); if (curve != 0) holes.Add(curve); }
                }
                else
                foreach (var loop in face.Loops)
                {
                    int curve = BeltOff ? Loop(f, loop) : LoopOriented(f, loop);
                    if (curve == 0) continue;

                    if (loop.IsOuter && outer == 0) outer = curve;
                    else holes.Add(curve);
                }

                (int Outer, List<TLoop> Locals)? Belts(TFace fc)
                {
                    var sr = fc.Surface is FacetedSurface fs0 ? fs0.Source : fc.Surface;
                    (Vector3d O, Vector3d W)? ax = sr switch
                    {
                        CylinderSurface cs => (cs.Axis.Origin, cs.Axis.W.Normalized()),
                        ExtrudedSurface { Base: ArcCurve3 ab } ex when Math.Abs(Math.Abs(Vector3d.Dot(ab.Frame.W.Normalized(), ex.Dir.Normalized())) - 1) < 1e-9 => (ab.Frame.Origin, ex.Dir.Normalized()),
                        RevolvedSurface rv when Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) < 1e-9 => (rv.Axis.Origin, rv.Axis.W.Normalized()),
                        _ => null
                    };
                    if (ax is not { } a) return null;
                    var belts = new List<TLoop>(); var locals = new List<TLoop>();
                    foreach (var l in fc.Loops) (Math.Abs(Winding(l, a.O, a.W)) > Math.PI ? belts : locals).Add(l);
                    if (belts.Count != 2) return null;
                    int b0 = BeltBoundary(f, sr!, belts[0], belts[1], a.O, a.W);
                    return b0 == 0 ? null : (b0, locals);
                }

                // Имя грани переживает обмен: оно кладётся в раздел описаний.
                int trimmed = f.AddTrimmed(surface, outer, holes);
                if (!string.IsNullOrEmpty(face.Tag)) f.Label(trimmed, face.Tag);

                written++;
            }
        }

        if (skipped > 0)
            warnings?.Add($"IGES: {skipped} face(s) skipped — the surface type is not written yet.");

        warnings?.Add($"IGES: {written} face(s) of {bodies} body(ies) written.");
        var text = f.Write();
        _splits = null; _pieces = null;
        return text;
    }

    /// <summary>Поверхность грани. Возвращает 0, если вид поверхности не записывается.</summary>
    private static int Surface(IgesFile f, TFace face, List<string>? warnings)
    {
        var src = face.Surface is FacetedSurface fs ? fs.Source : face.Surface;

        switch (src)
        {
            // m.x2: нормаль плоскости — наружу грани (по обходу внешнего контура, Ньюэлл: у граней-сеток сторона — в сетке, не в Reversed): у 144 знака стороны нет,
            // и контур, пройденный по ходу грани, должен идти против часовой относительно нормали. P3D_IGESPLANESENSE=0 — прежде.
            case PlaneSurface pl:
                return f.AddPlane(PlaneSenseOff ? pl.F.W : OutwardPlaneNormal(face, pl.F.W), pl.F.Origin);

            // m.x2: цилиндр с осью не вдоль Z — поверхность вращения отрезка (дуга IGES (100) — только в плоскости XY)
            case ExtrudedSurface exr when exr.Base is ArcCurve3 arcr && !BeltOff && Math.Abs(Math.Abs(Vector3d.Dot(arcr.Frame.W.Normalized(), Vector3d.UnitZ)) - 1) > 1e-9:
                return CylinderAsRevolved(f, face, arcr.Frame.Origin, exr.Dir.Normalized(), arcr.Frame.U.Normalized(), arcr.Radius);

            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc:
            {
                // Цилиндр: окружность плюс перенос.
                Vector3d c = arc.Frame.Origin;
                Vector3d start = arc.PointAt(arc.T0);
                int circle = f.AddArcXY(c, start, start);
                return f.AddExtruded(circle, IgesImport.Iges122Old ? ex.Dir : start + ex.Dir);   // 122: конец образующей
            }

            case ExtrudedSurface ex2 when ex2.Base is LineCurve3 ln:
            {
                // Плоская боковая грань: записывается плоскостью, как и любая другая.
                Vector3d u = (ln.B - ln.A).Normalized();
                Vector3d n = Vector3d.Cross(u, ex2.Dir.Normalized());
                if (n.LengthSquared < 1e-18) return 0;
                return f.AddPlane(n.Normalized(), ln.A);
            }

            // KRN-06: точный цилиндр произвольной оси (лента прямого скругления) — поверхность вращения отрезка-образующей
            // вокруг оси (120); длина образующей — по проекциям вершин грани на ось. Мутация P3D_IGESCYL=0.
            case CylinderSurface cyl when !IgesCylOff:
                return CylinderAsRevolved(f, face, cyl.Axis.Origin, cyl.Axis.W.Normalized(), cyl.Axis.U.Normalized(), cyl.Radius);

            case RevolvedSurface rev:
            {
                int axis = IgesImport.Iges120Old ? f.AddLine(rev.Axis.Origin, rev.Axis.Origin + rev.Axis.W) : f.AddLine(rev.Axis.Origin + rev.Axis.W, rev.Axis.Origin);   // 120: ось — от начала к концу (A→B)
                int gen = Curve(f, rev.Base);
                return gen == 0 ? 0 : f.AddRevolved(axis, gen, rev.A0, rev.A1);
            }

            case NurbsSurface ns:
                return f.AddSplineSurface(ns);
        }

        warnings?.Add($"IGES: surface {src?.GetType().Name ?? "unknown"} is not written yet.");
        return 0;
    }

    static readonly bool BeltOff = Environment.GetEnvironmentVariable("P3D_IGESBELT") == "0";
    static readonly bool PCurveOff = Environment.GetEnvironmentVariable("P3D_IGESPCURVE") == "0";
    static Vector3d OutwardPlaneNormal(TFace face, Vector3d w)
    {
        var outer = face.Loops.FirstOrDefault(l => l.IsOuter && l.CoEdges.Count > 0) ?? face.Loops.FirstOrDefault(l => l.CoEdges.Count > 0);
        if (outer == null) return face.Reversed ? -w : w;
        var pts = new List<Vector3d>();
        foreach (var c in Oriented(outer)) for (int i = 0; i < 32; i++) pts.Add(c.PointAt(c.T0 + (c.T1 - c.T0) * i / 32.0));
        var nw = Vector3d.Zero; var p0 = pts.Count > 0 ? pts[0] : Vector3d.Zero;
        for (int i = 0; i < pts.Count; i++) nw += Vector3d.Cross(pts[i] - p0, pts[(i + 1) % pts.Count] - p0);
        if (nw.Length < 1e-12) return face.Reversed ? -w : w;
        return Vector3d.Dot(nw, w) < 0 ? -w : w;
    }
    static readonly bool PatchOff = Environment.GetEnvironmentVariable("P3D_IGESPATCH") == "0";
    static readonly bool CapOff = Environment.GetEnvironmentVariable("P3D_IGESCAP") == "0";
    static readonly bool TruncOff = Environment.GetEnvironmentVariable("P3D_IGESTRUNC") == "0";
    static readonly bool PlaneSenseOff = Environment.GetEnvironmentVariable("P3D_IGESPLANESENSE") == "0";
    static readonly bool WholeOff = Environment.GetEnvironmentVariable("P3D_IGESWHOLE") == "0";
    static readonly bool GenArc = Environment.GetEnvironmentVariable("P3D_IGESGENARC") == "1";
    static readonly bool SplitDbg = Environment.GetEnvironmentVariable("P3D_IGESSPLITDBG") == "1";
    static readonly bool ArcXformOff = Environment.GetEnvironmentVariable("P3D_IGESARC124") == "0";

    /// <summary>
    /// Грань вращения: ось (o, w), образующая в полуплоскости угла 0 (опорное направление e0), промежуток углов. Шов полного
    /// оборота — в середине наибольшего промежутка углов, не занятого местными контурами; пояса (контуры вокруг оси) — один
    /// контур-прямоугольник «пояс A, шов, пояс B, шов назад». Пояс один (шапка с полюсом) или ни одного у полного оборота —
    /// null (прежняя запись).
    /// </summary>
    static int? Rf(int line) { if (SplitDbg) Console.Error.WriteLine($"RevolvedFace: null at {line}"); return null; }
    static int? RevolvedFace(IgesFile f, TFace face)
    {
        var src = face.Surface is FacetedSurface fs ? fs.Source : face.Surface;
        Vector3d o, w; ICurve3 G; double a0, a1;
        switch (src)
        {
            case CylinderSurface cs:
            {
                o = cs.Axis.Origin; w = cs.Axis.W.Normalized(); var e = cs.Axis.U.Normalized();
                if (Extent(face, o, w) is not { } ex) return Rf(223);
                G = new LineCurve3(o + e * cs.Radius + w * ex.Lo, o + e * cs.Radius + w * ex.Hi); a0 = 0; a1 = 2 * Math.PI; break;
            }
            case ExtrudedSurface { Base: ArcCurve3 arc } exs when Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), exs.Dir.Normalized())) - 1) < 1e-9:
            {
                o = arc.Frame.Origin; w = exs.Dir.Normalized(); var e = arc.Frame.U.Normalized();
                if (Extent(face, o, w) is not { } ex) return Rf(229);
                G = new LineCurve3(o + e * arc.Radius + w * ex.Lo, o + e * arc.Radius + w * ex.Hi);
                if (arc.IsClosed) { a0 = 0; a1 = 2 * Math.PI; }
                else
                {
                    // угол дуги вокруг w: дуга по своей оси W; если W против w — углы с обратным знаком
                    double sg = Vector3d.Dot(arc.Frame.W, w) >= 0 ? 1 : -1;
                    a0 = sg * arc.A0; a1 = sg * arc.A1;
                }
                break;
            }
            case RevolvedSurface rv when rv.Base is LineCurve3 or ArcCurve3:
                o = rv.Axis.Origin; w = rv.Axis.W.Normalized(); G = rv.Base; a0 = rv.A0; a1 = rv.A1;
                if (rv.Base is ArcCurve3 ga && Math.Abs(Vector3d.Dot(ga.Frame.W.Normalized(), w)) > 1e-9) return Rf(242);   // дуга не в меридиане
                break;
            default: return Rf(244);
        }
        if (a1 < a0) (a0, a1) = (a1, a0);
        bool full = a1 - a0 > 2 * Math.PI - 1e-9;
        // опорное направление e0 — радиальное направление образующей (её точки в полуплоскости угла 0)
        var gm = G.PointAt((G.T0 + G.T1) / 2) - o; gm -= w * Vector3d.Dot(gm, w);
        if (gm.Length < 1e-12) return Rf(250);
        var e0 = gm.Normalized(); var e1 = Vector3d.Cross(w, e0);
        double Ang(Vector3d p) { var d = p - o; return Math.Atan2(Vector3d.Dot(d, e1), Vector3d.Dot(d, e0)); }
        var loops = face.Loops.Where(l => l.CoEdges.Count > 0).Select(l => (L: l, C: Oriented(l))).Where(x => x.C.Count > 0).ToList();
        // целый шар (дуга от полюса до полюса, полный оборот, без контуров): граница — меридиан шва туда и обратно (как у
        // OpenCascade: в параметрах — два отрезка при v = 2π и v = 0, без вырожденных кривых полюсов). P3D_IGESWHOLE=0 — прежняя запись.
        bool OnAxis(Vector3d p) { var d = p - o; d -= w * Vector3d.Dot(d, w); return d.Length < 1e-9; }
        bool whole = !WholeOff && loops.Count == 0 && full && G is ArcCurve3 && OnAxis(G.PointAt(G.T0)) && OnAxis(G.PointAt(G.T1));
        if (loops.Count == 0 && !whole) return Rf(259);
        var belts = loops.Where(x => Math.Abs(Winding(x.L, o, w)) > Math.PI).ToList();
        var locals = loops.Where(x => Math.Abs(Winding(x.L, o, w)) <= Math.PI).ToList();
        // полный оборот с собственным швом (ребро, пройденное гранью дважды) — контур уже прямоугольник; шов — у этого ребра
        var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in face.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
        var seamEdge = uses.Where(kv => kv.Value == 2).Select(kv => kv.Key).FirstOrDefault();
        bool ownSeam = full && belts.Count == 0 && seamEdge != null;
        bool cap = !CapOff && full && belts.Count == 1 && !ownSeam && (OnAxis(G.PointAt(G.T0)) || OnAxis(G.PointAt(G.T1)));
        // m.x2: КУСОК ВНУТРИ ПОЛНОГО ОБОРОТА (контуры не обходят ось: угол скругления вершины на шаре, часть цилиндра, у которой
        // поверхность — целый цилиндр): контур — внешний в параметрах, шов — в свободном промежутке углов. Дополнение (грань —
        // всё, кроме куска) исключается: у образующей без полюсов оно касалось бы краёв образующей; полюс — вне сетки грани.
        // Замкнутая образующая (тор) — прежней записью. P3D_IGESPATCH=0 — прежде.
        bool patch = false;
        if (!PatchOff && full && belts.Count == 0 && !ownSeam && !whole && loops.Count >= 1 && !(G is ArcCurve3 { IsClosed: true }))
        {
            patch = true;
            foreach (var pe in new[] { G.PointAt(G.T0), G.PointAt(G.T1) }.Where(OnAxis))
            {
                if (face.Surface is not FacetedSurface pfs || pfs.Facets.Count == 0) { patch = false; break; }
                double scale = Math.Max(1, (G.PointAt(G.T0) - G.PointAt(G.T1)).Length);
                double dmin = pfs.Facets.Min(t => Math.Min((t.Item1 - pe).Length, Math.Min((t.Item2 - pe).Length, (t.Item3 - pe).Length)));
                if (dmin < 1e-3 * scale) { patch = false; break; }
            }
        }
        if (full && belts.Count != 2 && !ownSeam && !whole && !cap && !patch) return Rf(267);
        if (!full && belts.Count != 0) return Rf(268);
        // шов: середина наибольшего свободного промежутка углов (у полного оборота); у части — начало промежутка
        double th;
        if (ownSeam) th = Ang(seamEdge!.Curve.PointAt((seamEdge.Curve.T0 + seamEdge.Curve.T1) / 2));
        else if (full)
        {
            var occ = new List<double>();
            foreach (var l in locals) foreach (var c in l.C) for (int i = 0; i <= 32; i++) { var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 32.0); var rq = q - o; rq -= w * Vector3d.Dot(rq, w); if (rq.Length > 1e-9) occ.Add(Ang(q)); }
            th = 0;
            if (occ.Count > 0)
            {
                occ.Sort(); double best = -1;
                for (int i = 0; i < occ.Count; i++) { double x0 = occ[i], x1 = i + 1 < occ.Count ? occ[i + 1] : occ[0] + 2 * Math.PI; if (x1 - x0 > best) { best = x1 - x0; th = (x0 + x1) / 2; } }
                if (best < 1e-3) return Rf(281);
            }
        }
        else th = a0;
        double ta = full ? 2 * Math.PI : a1 - a0;
        // образующая на угле шва
        Vector3d Rot(Vector3d p, double ang) => RevolvedSurface.RotateAbout(p, o, w, ang);
        ICurve3? Gs = G switch
        {
            LineCurve3 l => new LineCurve3(Rot(l.A, th), Rot(l.B, th)),
            ArcCurve3 a => new ArcCurve3(new Frame3(Rot(a.Frame.Origin, th), Rot(a.Frame.Origin + a.Frame.U, th) - Rot(a.Frame.Origin, th), Rot(a.Frame.Origin + a.Frame.V, th) - Rot(a.Frame.Origin, th)), a.Radius, a.A0, a.A1),
            _ => null
        };
        if (Gs == null) return Rf(294);
        // образующая-дуга — дугой 100 (с матрицей 124): у читателя — окружность (периодическая), и у грани трубки шов узнаётся;
        // параметр u — угол дуги в её системе (как в записи AddArc). Прежде — рациональный сплайн (не периодический).
        // (опыт: дугой 100 — у OpenCascade отрезок тора хуже; пока — рациональным сплайном; P3D_IGESGENARC=1 — дугой)
        SplineCurve3? gsp = Gs is ArcCurve3 gArc && !GenArc && !whole && !cap ? ArcAsRational(gArc) : null;   // целый шар — дугой 100, как у OpenCascade
        bool closedGen = Gs is ArcCurve3 { IsClosed: true };
        double uPer = Gs is ArcCurve3 && gsp == null ? 2 * Math.PI : 1;
        // система дуги-образующей как в AddArc: обход против часовой; углы начала/конца
        Vector3d gU = default, gV = default, gC = default; double ga0 = 0, ga1 = 1;
        if (Gs is ArcCurve3 gA && gsp == null)
        {
            gC = gA.Frame.Origin; gU = gA.Frame.U.Normalized(); var gW = Vector3d.Cross(gA.Frame.U, gA.Frame.V).Normalized(); gV = Vector3d.Cross(gW, gU);
            ga0 = gA.A0; ga1 = gA.A1; if (ga1 < ga0) { gV = -gV; ga0 = -ga0; ga1 = -ga1; }
        }
        // часть оборота замкнутой образующей (отрезок тора): два контура-«кольца трубки» (торцы) — прямоугольник
        // «торец A от точки u = 0, шов вдоль оборота, торец B, шов назад»
        bool tube = !full && closedGen && loops.Count == 2 && belts.Count == 0;
        int genId = gsp != null ? f.AddSpline(gsp)
                  : Gs is ArcCurve3 gArc2 ? f.AddArc(gArc2.Frame.Origin, gArc2.Frame.U, gArc2.Frame.V, gArc2.Radius, gArc2.A0, gArc2.A1)
                  : f.AddLine(((LineCurve3)Gs).A, ((LineCurve3)Gs).B);
        int axisId = IgesImport.Iges120Old ? f.AddLine(o, o + w) : f.AddLine(o + w, o);
        int surf = f.AddRevolved(axisId, genId, 0, ta);
        // параметры точки: v — угол от шва, u — параметр образующей (отрезок 0…1; дуга — параметр её рационального сплайна)
        double UOf(Vector3d p)
        {
            double v0 = Ang(p) - Ang(Gs.PointAt((Gs.T0 + Gs.T1) / 2));
            var q = Rot(p, -v0);
            if (Gs is LineCurve3 ln) { var d = ln.B - ln.A; return Math.Clamp(Vector3d.Dot(q - ln.A, d) / d.LengthSquared, 0, 1); }
            if (gsp == null)
            {
                // угол в системе дуги-образующей, в промежутке [ga0, ga1] (с точностью до оборота)
                double ph = Math.Atan2(Vector3d.Dot(q - gC, gV), Vector3d.Dot(q - gC, gU));
                while (ph < ga0 - 1e-9) ph += 2 * Math.PI; while (ph > ga0 + 2 * Math.PI + 1e-9) ph -= 2 * Math.PI;
                return ph;
            }
            double bt = 0, bd = double.MaxValue;
            for (int i = 0; i <= 256; i++) { double t = i / 256.0, dd = (gsp!.PointAt(t) - q).LengthSquared; if (dd < bd) { bd = dd; bt = t; } }
            double lo = Math.Max(0, bt - 1 / 256.0), hi = Math.Min(1, bt + 1 / 256.0);
            for (int it = 0; it < 80; it++) { double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3; if ((gsp!.PointAt(m1) - q).LengthSquared < (gsp.PointAt(m2) - q).LengthSquared) hi = m2; else lo = m1; }
            return (lo + hi) / 2;
        }
        double thG = Ang(Gs.PointAt((Gs.T0 + Gs.T1) / 2));
        (int Model, int Param) Boundary(List<ICurve3> curves)
        {
            var model = new List<int>(); var pars = new List<List<Vector2d>>(); var cub = new List<bool>();
            double? prevV = null, prevU = null;
            foreach (var c in curves)
            {
                var pts = new List<Vector2d>(); int K = PcK(c);
                for (int i = 0; i <= K; i++)
                {
                    var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / K);
                    var rq = q - o; rq -= w * Vector3d.Dot(rq, w);
                    // точка на оси (полюс): угол — по соседней точке кривой
                    double v = rq.Length < 1e-9 ? (prevV ?? Ang(c.PointAt(c.T0 + (c.T1 - c.T0) * 1e-3)) - thG) : Ang(q) - thG;
                    if (prevV is { } pv) { while (v - pv > Math.PI) v -= 2 * Math.PI; while (v - pv < -Math.PI) v += 2 * Math.PI; }
                    double uq = UOf(q);
                    // замкнутая образующая (окружность тора): параметр 0 и 1 — одна точка; по соседней точке
                    if (closedGen && prevU is { } pu) { while (uq - pu > uPer / 2) uq -= uPer; while (uq - pu < -uPer / 2) uq += uPer; }
                    prevV = v; prevU = uq; pts.Add(new Vector2d(uq, v));
                }
                pars.Add(pts); cub.Add(PcGenOff || !General(c)); model.Add(CurveOriented(f, c));
            }
            // сдвиг на 2π: контур — в [0, ta]; у замкнутой образующей — u в [0, 1]
            double mn = pars.SelectMany(x => x).Min(q => q.Y);
            double sh = -2 * Math.PI * Math.Floor((mn + 1e-7) / (2 * Math.PI));
            double shu = closedGen ? -uPer * Math.Floor((pars.SelectMany(x => x).Min(q => q.X) - (gsp == null && Gs is ArcCurve3 ? ga0 : 0)) / uPer + 1e-7) : 0;
            var ids = pars.Select((x, k) => f.AddParamPolyline(x.Select(q => new Vector2d(q.X + shu, q.Y + sh)).ToList(), cub[k])).ToList();
            if (model.Any(x => x == 0)) return (0, 0);
            return (model.Count == 1 ? model[0] : f.AddComposite(model), ids.Count == 1 ? ids[0] : f.AddComposite(ids));
        }
        // пояса на угле шва: каждый делится там, где проходит угол th
        List<ICurve3>? FromSeam(List<ICurve3> belt)
        {
            for (int i = 0; i < belt.Count; i++)
            {
                var c = belt[i]; const int N = 256; double D(double t) { double d = Ang(c.PointAt(t)) - th; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return d; }
                double prev = D(c.T0);
                if (Math.Abs(prev) < 1e-9) return belt.Skip(i).Concat(belt.Take(i)).ToList();
                for (int j = 1; j <= N; j++)
                {
                    double t = c.T0 + (c.T1 - c.T0) * j / N, dv = D(t);
                    if (Math.Sign(dv) != Math.Sign(prev) && Math.Abs(dv - prev) < Math.PI)
                    {
                        double lo = c.T0 + (c.T1 - c.T0) * (j - 1) / N, hi = t, slo = Math.Sign(prev);
                        for (int it = 0; it < 100; it++) { double m = (lo + hi) / 2; if (Math.Sign(D(m)) == slo) lo = m; else hi = m; }
                        if (SplitAt(c, (lo + hi) / 2) is not { } sp) return null;
                        RegisterSplit(c, (lo + hi) / 2);
                        var res = new List<ICurve3>();
                        if (sp.Tail != null) res.Add(sp.Tail);
                        res.AddRange(belt.Skip(i + 1)); res.AddRange(belt.Take(i));
                        if (sp.Head != null) res.Add(sp.Head);
                        return res;
                    }
                    prev = dv;
                }
            }
            return null;
        }
        (int, int) outer; var holes = new List<(int, int)>();
        if (tube)
        {
            var p0 = Gs.PointAt(Gs.T0);
            // торец на угле v: начало — точка образующей u = 0, повёрнутая на v
            List<ICurve3>? FromPoint(List<ICurve3> loop, Vector3d at)
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    var c = loop[i]; double bt = 0, bd = double.MaxValue;
                    for (int j = 0; j <= 512; j++) { double t = c.T0 + (c.T1 - c.T0) * j / 512.0, d = (c.PointAt(t) - at).Length; if (d < bd) { bd = d; bt = t; } }
                    if (bd > 1e-3) continue;
                    double lo = Math.Max(c.T0, bt - (c.T1 - c.T0) / 512.0), hi = Math.Min(c.T1, bt + (c.T1 - c.T0) / 512.0);
                    for (int it = 0; it < 100; it++) { double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3; if ((c.PointAt(m1) - at).Length < (c.PointAt(m2) - at).Length) hi = m2; else lo = m1; }
                    double tt = (lo + hi) / 2; if ((c.PointAt(tt) - at).Length > 1e-7) continue;
                    if (SplitAt(c, tt) is not { } sp) return null;
                    RegisterSplit(c, tt);
                    var res = new List<ICurve3>();
                    if (sp.Tail != null) res.Add(sp.Tail);
                    res.AddRange(loop.Skip(i + 1)); res.AddRange(loop.Take(i));
                    if (sp.Head != null) res.Add(sp.Head);
                    return res;
                }
                return null;
            }
            double VOf(List<ICurve3> loop) { var q = loop[0].PointAt((loop[0].T0 + loop[0].T1) / 2); double d = Ang(q) - th; while (d < -1e-9) d += 2 * Math.PI; while (d > 2 * Math.PI) d -= 2 * Math.PI; return d; }
            var la = loops[0].C; var lb = loops[1].C;
            if (VOf(la) > VOf(lb)) (la, lb) = (lb, la);
            double va = VOf(la), vb = VOf(lb);
            var A = FromPoint(la, Rot(p0, va)); var B = FromPoint(lb, Rot(p0, vb));
            if (A == null || B == null) return Rf(394);
            // шов вдоль оборота через точку u = 0
            var zc = o + w * Vector3d.Dot(p0 - o, w); var rdir = p0 - zc; double rr = rdir.Length; if (rr < 1e-9) return Rf(396);
            var fr = new Frame3(zc, rdir, Vector3d.Cross(w, rdir));
            var seamFwd = new ArcCurve3(fr, rr, va, vb);
            outer = Boundary(new List<ICurve3>(A) { seamFwd }.Concat(B).Append(seamFwd.Reversed()).ToList());
        }
        else if (whole)
        {
            double u0 = UOf(Gs.PointAt(Gs.T0)), u1 = UOf(Gs.PointAt(Gs.T1));
            int mA = CurveOriented(f, Gs.Reversed()), mB = CurveOriented(f, Gs);
            int pA = f.AddParamPolyline(new List<Vector2d> { new(u1, ta), new(u0, ta) });
            int pB = f.AddParamPolyline(new List<Vector2d> { new(u0, 0), new(u1, 0) });
            outer = mA == 0 || mB == 0 ? (0, 0) : (f.AddComposite(new List<int> { mA, mB }), f.AddComposite(new List<int> { pA, pB }));
        }
        else if (ownSeam)
        {
            var withSeam = loops.First(x => x.L.CoEdges.Any(ce => ReferenceEquals(ce.Edge, seamEdge)));
            outer = Boundary(withSeam.C);
            foreach (var l in loops.Where(x => !ReferenceEquals(x.L, withSeam.L))) holes.Add(Boundary(l.C));
        }
        else if (patch)
        {
            var outerP = loops.FirstOrDefault(x => x.L.IsOuter); if (outerP.L == null) outerP = loops[0];
            outer = Boundary(outerP.C);
            foreach (var l in loops.Where(x => !ReferenceEquals(x.L, outerP.L))) holes.Add(Boundary(l.C));
        }
        else if (cap)
        {
            // m.x2: ШАПКА С ПОЛЮСОМ (кусок шара, конус до вершины; угол скругления вершины): как у OpenCascade — меридиан шва от
            // полюса к поясу, пояс целиком, меридиан обратно к полюсу; сторона полюса в параметрах — без кривой (вырождена).
            // Прежде — прежняя запись без кривых в параметрах, и OpenCascade и наш импорт брали дополнение грани. P3D_IGESCAP=0.
            var A = FromSeam(belts[0].C); if (A == null) return Rf(9001);
            var sB = A[0].PointAt(A[0].T0);
            Vector3d q0 = Gs.PointAt(Gs.T0), q1 = Gs.PointAt(Gs.T1);
            Vector3d pole;
            if (OnAxis(q0) != OnAxis(q1)) pole = OnAxis(q0) ? q0 : q1;
            else if (face.Surface is FacetedSurface ffs && ffs.Facets.Count > 0)
            {
                double D(Vector3d p) => ffs.Facets.Min(t => Math.Min((t.Item1 - p).LengthSquared, Math.Min((t.Item2 - p).LengthSquared, (t.Item3 - p).LengthSquared)));
                pole = D(q0) <= D(q1) ? q0 : q1;
            }
            else
            {
                // сторона области: слева от хода пояса при взгляде с наружной нормали
                var c0 = A[0]; var pa = c0.PointAt(c0.T0); var pb = c0.PointAt(c0.T0 + (c0.T1 - c0.T0) * 1e-3);
                double dv = Ang(pb) - Ang(pa); while (dv > Math.PI) dv -= 2 * Math.PI; while (dv < -Math.PI) dv += 2 * Math.PI;
                double tg = 0, bd = double.MaxValue; for (int i = 0; i <= 64; i++) { double t = Gs.T0 + (Gs.T1 - Gs.T0) * i / 64; double d = (Rot(Gs.PointAt(t), Ang(sB) - th) - sB).Length; if (d < bd) { bd = d; tg = t; } }
                var r = sB - o; r -= w * Vector3d.Dot(r, w);
                var nIges = Vector3d.Cross(Rot(Gs.PointAt(Math.Min(Gs.T1, tg + 1e-6)), Ang(sB) - th) - Rot(Gs.PointAt(Math.Max(Gs.T0, tg - 1e-6)), Ang(sB) - th), Vector3d.Cross(w, r));
                var nOut = src!.NormalAt(src.ProjectToUV(sB)) * (face.Reversed ? -1 : 1);
                double side = -Math.Sign(dv) * Math.Sign(Vector3d.Dot(nIges, nOut));   // < 0 — к началу образующей
                pole = side < 0 ? q0 : q1;
            }
            var seamBack = Seam(src!, sB, pole); if (seamBack == null) return Rf(9002);
            outer = Boundary(new List<ICurve3> { seamBack.Reversed() }.Concat(A).Append(seamBack).ToList());
        }
        else if (full)
        {
            var A = FromSeam(belts[0].C); var B = FromSeam(belts[1].C);
            if (A == null || B == null) return Rf(446);
            var sA = A[0].PointAt(A[0].T0); var sB = B[0].PointAt(B[0].T0);
            var seam = Seam(src!, sA, sB); if (seam == null) return Rf(448);
            var all = new List<ICurve3>(A) { seam }; all.AddRange(B); all.Add(seam.Reversed());
            outer = Boundary(all);
        }
        else
        {
            var outerL = loops.FirstOrDefault(x => x.L.IsOuter); if (outerL.L == null) outerL = loops[0];
            outer = Boundary(outerL.C);
            foreach (var l in loops.Where(x => !ReferenceEquals(x.L, outerL.L))) holes.Add(Boundary(l.C));
        }
        if (full && !ownSeam && !tube && !patch) foreach (var l in locals) holes.Add(Boundary(l.C));
        if (outer.Item1 == 0 || holes.Any(h => h.Item1 == 0)) return Rf(459);
        return f.AddTrimmedParam(surf, outer, holes);
    }

    /// <summary>
    /// m.x2: ГРАНЬ СВОБОДНОЙ ФОРМЫ — сплайновой поверхностью (128) с кривыми границ и в параметрах: NURBS как есть; лента по
    /// формуле сечения и угловой лоскут — их B-сплайном в бюджете экспорта (как в STEP); выдавливание сплайна — точно (сплайн ×
    /// отрезок). Параметры точек границы — проекцией на сплайн; у замкнутой поверхности край 0/1 выбирается по соседней точке.
    /// </summary>
    static int? NurbsFace(IgesFile f, TFace face)
    {
        var src = face.Surface is FacetedSurface fs ? fs.Source : face.Surface;
        NurbsSurface? ns = src switch
        {
            NurbsSurface n => n,
            SectionSurface band when band.IsProcedural && Export.StepExact.BandSpan(face, band) is { } brg
                => BandNurbs.Fit(band, brg.Ua, brg.Ub, ToleranceContext.Current.ExportFit / 10)?.S,
            CornerPatchSurface cp => CornerPatchNurbs.Fit(cp, ToleranceContext.Current.ExportFit / 2)?.S,
            ExtrudedSurface { Base: SplineCurve3 sp } ex when Extent(face, sp.PointAt(sp.T0), ex.Dir.Normalized()) is { } e2 => ExtrudedSpline(sp, ex.Dir.Normalized(), e2.Lo, e2.Hi),
            _ => null
        };
        if (ns == null) return null;
        var loops = face.Loops.Where(l => l.CoEdges.Count > 0).Select(l => (L: l, C: Oriented(l))).Where(x => x.C.Count > 0).ToList();
        if (loops.Count == 0) return null;
        int surf = f.AddSplineSurface(ns);
        // замкнутость по направлениям: края 0 и 1 совпадают
        bool ClosedU() { for (int k = 0; k <= 8; k++) { double v = k / 8.0; if ((ns.PointAt(new Vector2d(0, v)) - ns.PointAt(new Vector2d(1, v))).Length > 1e-7) return false; } return true; }
        bool ClosedV() { for (int k = 0; k <= 8; k++) { double u = k / 8.0; if ((ns.PointAt(new Vector2d(u, 0)) - ns.PointAt(new Vector2d(u, 1))).Length > 1e-7) return false; } return true; }
        bool cu = ClosedU(), cv = ClosedV();
        (int Model, int Param) Boundary(List<ICurve3> curves)
        {
            var model = new List<int>(); var ids = new List<int>();
            Vector2d? prev = null;
            foreach (var c in curves)
            {
                var pts = new List<Vector2d>(); int K = PcK(c);
                for (int i = 0; i <= K; i++)
                {
                    var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / K);
                    var uv = prev is { } h ? ns.ProjectToUV(q, h) : ns.ProjectToUV(q);
                    if (prev is { } pv)
                    {
                        if (cu && Math.Abs(uv.X - pv.X) > 0.5) uv.X = uv.X < 0.5 ? 1 : 0;
                        if (cv && Math.Abs(uv.Y - pv.Y) > 0.5) uv.Y = uv.Y < 0.5 ? 1 : 0;
                    }
                    pts.Add(uv); prev = uv;
                }
                ids.Add(f.AddParamPolyline(pts, PcGenOff || !General(c))); model.Add(CurveOriented(f, c));
            }
            if (model.Any(x => x == 0)) return (0, 0);
            return (model.Count == 1 ? model[0] : f.AddComposite(model), ids.Count == 1 ? ids[0] : f.AddComposite(ids));
        }
        var outerL = loops.FirstOrDefault(x => x.L.IsOuter); if (outerL.L == null) outerL = loops[0];
        var outer = Boundary(outerL.C);
        var holes = loops.Where(x => !ReferenceEquals(x.L, outerL.L)).Select(x => Boundary(x.C)).ToList();
        if (outer.Model == 0 || holes.Any(h => h.Model == 0)) return null;
        return f.AddTrimmedParam(surf, outer, holes);
    }

    /// <summary>Выдавливание сплайна — сплайн × отрезок (степень 1 по v), точно; v ∈ [lo, hi] вдоль направления.</summary>
    static NurbsSurface? ExtrudedSpline(SplineCurve3 sp, Vector3d dir, double lo, double hi)
    {
        var u = sp.WithUnitKnots();
        int n = u.ControlPoints.Count;
        var cp = new Vector3d[n, 2]; var w = new double[n, 2];
        for (int i = 0; i < n; i++) { cp[i, 0] = u.ControlPoints[i] + dir * lo; cp[i, 1] = u.ControlPoints[i] + dir * hi; w[i, 0] = w[i, 1] = u.Weights[i]; }
        return new NurbsSurface(cp, u.Degree, 1, w, u.Knots.ToArray(), new double[] { 0, 0, 1, 1 });
    }

    /// <summary>Пределы граней вдоль оси (проекции точек рёбер) с запасом.</summary>
    static (double Lo, double Hi)? Extent(TFace face, Vector3d o, Vector3d w)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var ce in face.Loops.SelectMany(l => l.CoEdges))
            for (int k = 0; k <= 8; k++) { var q = ce.Edge.Curve.PointAt(ce.Edge.Curve.T0 + (ce.Edge.Curve.T1 - ce.Edge.Curve.T0) * k / 8.0); double h = Vector3d.Dot(q - o, w); lo = Math.Min(lo, h); hi = Math.Max(hi, h); }
        if (lo > hi) return null;
        double pad = Math.Max(1e-3, 0.01 * (hi - lo));
        return (lo - pad, hi + pad);
    }

    /// <summary>Полный угол обхода контура вокруг оси (≈ ±2π у пояса, ≈ 0 у местного контура).</summary>
    static double Winding(TLoop l, Vector3d o, Vector3d w)
    {
        var pts = new List<Vector3d>();
        foreach (var ce in l.CoEdges) { var q = new List<Vector3d>(); ce.Tessellate(q, true); pts.AddRange(q); }
        if (pts.Count < 3) return 0;
        var u = Vector3d.Cross(w, Math.Abs(w.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized(); var v = Vector3d.Cross(w, u);
        double Ang(Vector3d p) { var d = p - o; return Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u)); }
        double tot = 0;
        for (int i = 0; i < pts.Count; i++) { double da = Ang(pts[(i + 1) % pts.Count]) - Ang(pts[i]); while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI; tot += da; }
        return tot;
    }

    /// <summary>Кривые контура по ходу полурёбер (ребро, пройденное наоборот, — обращённой кривой).</summary>
    /// <summary>
    /// m.x2: ДЕЛЕНИЯ РЁБЕР ШВАМИ. Шов грани вращения ставится там, где у пояса может не быть вершины — кривая пояса делится, и
    /// то же ребро должно делиться так же и в соседней грани (иначе читатель не сошьёт «дугу» с «двумя дугами»). Первый проход
    /// записи собирает деления (параметры кривой ребра), второй пишет все грани с ними.
    /// </summary>
    [ThreadStatic] static Dictionary<TEdge, List<double>>? _splits;
    [ThreadStatic] static Dictionary<ICurve3, (TEdge E, double E0, double E1)>? _pieces;

    static ICurve3? Sub(ICurve3 c, double a, double b) => c switch
    {
        LineCurve3 l => new LineCurve3(l.PointAt(a), l.PointAt(b)),
        ArcCurve3 r => new ArcCurve3(r.Frame, r.Radius, r.A0 + r.Sweep * a, r.A0 + r.Sweep * b),
        EllipseCurve3 e => new EllipseCurve3(e.Frame, e.RU, e.RV, e.A0 + (e.A1 - e.A0) * a, e.A0 + (e.A1 - e.A0) * b),
        _ when !PieceOff => new Piece(c, a, b, false),
        _ => null
    };

    /// <summary>
    /// m.x2: КУСОК КРИВОЙ ОБЩЕГО ВИДА (кривая пересечения, кривая по формуле) — в параметре самой кривой [A, B] (обратный —
    /// с параметром A + B − t). Прежде такая кривая не делилась, и пояс грани вращения не разрезался на угле шва (стык
    /// цилиндров протяжки по ломаной писался прежней записью). Пишется сплайном (Curve). P3D_IGESPIECE=0 — прежде.
    /// </summary>
    sealed class Piece : ICurve3
    {
        readonly ICurve3 _c; readonly double _a, _b; readonly bool _rev;
        public Piece(ICurve3 c, double a, double b, bool rev) { _c = c; _a = a; _b = b; _rev = rev; }
        public double T0 => _a;
        public double T1 => _b;
        public bool IsClosed => false;
        double Map(double t) => _rev ? _a + _b - t : t;
        public Vector3d PointAt(double t) => _c.PointAt(Map(t));
        public Vector3d TangentAt(double t) => _rev ? -_c.TangentAt(Map(t)) : _c.TangentAt(Map(t));
        public double Length { get { double l = 0; var p = PointAt(_a); for (int i = 1; i <= 128; i++) { var q = PointAt(_a + (_b - _a) * i / 128.0); l += (q - p).Length; p = q; } return l; } }
        public Bounds3 Bounds { get { var bb = Bounds3.Empty; for (int i = 0; i <= 64; i++) bb.Add(PointAt(_a + (_b - _a) * i / 64.0)); return bb; } }
        public ICurve3 Reversed() => new Piece(_c, _a, _b, !_rev);
        public void Tessellate(List<Vector3d> dst, bool includeFirstPoint) { for (int i = includeFirstPoint ? 0 : 1; i <= 64; i++) dst.Add(PointAt(_a + (_b - _a) * i / 64.0)); }
    }
    /// <summary>m.x2: точек кривой в параметрах — 24 у отрезка, дуги, эллипса; 96 у кривой общего вида (кубическая
    /// интерполяция по 24 точкам отходила на 1e-3 мм от кривой — OpenCascade разворачивал стык цилиндров протяжки в пять
    /// оборотов). P3D_IGESPCK=24 — прежде.</summary>
    static int PcK(ICurve3 c) => !General(c) || PcKOld ? 24 : 96;
    /// <summary>Кривая общего вида (пересечения, по формуле, кусок такой): её кривая в параметрах — ломаной (96 точек), не
    /// кубической интерполяцией: у стыка цилиндров протяжки кубическая давала OpenCascade лишние обороты грани.
    /// P3D_IGESPCGEN=0 — кубической (как у прочих).</summary>
    static bool General(ICurve3 c) => c is not (LineCurve3 or ArcCurve3 or EllipseCurve3 or SplineCurve3);
    static readonly bool PcGenOff = Environment.GetEnvironmentVariable("P3D_IGESPCGEN") == "0";
    static readonly bool PcKOld = Environment.GetEnvironmentVariable("P3D_IGESPCK") == "24";
    static readonly bool PieceOff = Environment.GetEnvironmentVariable("P3D_IGESPIECE") == "0";

    /// <summary>Зарегистрировать деление куска контура в его параметре t (переводится в параметр кривой ребра).</summary>
    static void RegisterSplit(ICurve3 piece, double t)
    {
        if (SplitDbg) Console.Error.WriteLine($"IGESSPLIT {(_splits == null ? "no registry" : _pieces!.ContainsKey(piece) ? "ok" : "piece unknown")} t {t:G6} {piece.GetType().Name}");
        if (_splits == null || _pieces == null || !_pieces.TryGetValue(piece, out var info)) return;
        double s = info.E0 + (info.E1 - info.E0) * (t - piece.T0) / (piece.T1 - piece.T0);
        if (!_splits.TryGetValue(info.E, out var l)) _splits[info.E] = l = new List<double>();
        if (!l.Any(x => Math.Abs(x - s) < 1e-9)) l.Add(s);
    }

    static List<ICurve3> Oriented(TLoop l)
    {
        // разрез «туда и обратно» (то же ребро подряд в обе стороны — лишний шов) — убирается: OpenCascade делил по нему грань
        var ces = l.CoEdges.ToList();
        for (bool again = true; again && ces.Count > 2;)
        {
            again = false;
            for (int i = 0; i < ces.Count && ces.Count > 2; i++)
            {
                var x = ces[i]; var y = ces[(i + 1) % ces.Count];
                if (ReferenceEquals(x.Edge, y.Edge) && x.Reversed != y.Reversed)
                {
                    int j = (i + 1) % ces.Count;
                    if (j > i) { ces.RemoveAt(j); ces.RemoveAt(i); } else { ces.RemoveAt(i); ces.RemoveAt(j); }
                    again = true; break;
                }
            }
        }
        var res = new List<ICurve3>();
        foreach (var ce in ces)
        {
            var c = ce.Edge.Curve;
            var cuts = _splits != null && _splits.TryGetValue(ce.Edge, out var sl) ? sl.Where(x => x > c.T0 + 1e-9 && x < c.T1 - 1e-9).OrderBy(x => x).ToList() : null;
            var ranges = new List<(double A, double B)>();
            if (cuts == null || cuts.Count == 0 || Sub(c, c.T0, c.T1) == null) ranges.Add((c.T0, c.T1));
            else { double prev = c.T0; foreach (var x in cuts) { ranges.Add((prev, x)); prev = x; } ranges.Add((prev, c.T1)); }
            if (ce.Reversed) ranges.Reverse();
            foreach (var (ra, rb) in ranges)
            {
                ICurve3 pc;
                // кривая общего вида — куском в её собственном параметре (обратный ход — тем же параметром наоборот): деление
                // в реестре по параметру куска переводится в параметр ребра линейно
                if (!PieceOff && c is not (LineCurve3 or ArcCurve3 or EllipseCurve3 or SplineCurve3))
                    pc = new Piece(c, Math.Min(ra, rb), Math.Max(ra, rb), ce.Reversed);
                else
                {
                    pc = ranges.Count == 1 ? c : Sub(c, ra, rb)!;
                    if (ce.Reversed) pc = pc.Reversed();
                }
                _pieces?.TryAdd(pc, (ce.Edge, ce.Reversed ? rb : ra, ce.Reversed ? ra : rb));
                res.Add(pc);
            }
        }
        return res;
    }

    /// <summary>Кривая по ходу: дуги и эллипсы — рациональным сплайном (направление сохраняется), прочие — как есть.</summary>
    static int CurveOriented(IgesFile f, ICurve3 c) => c switch
    {
        ArcCurve3 a when !ArcXformOff => f.AddArc(a.Frame.Origin, a.Frame.U, a.Frame.V, a.Radius, a.A0, a.A1),
        ArcCurve3 a => f.AddSpline(ArcAsRational(a)),
        EllipseCurve3 el => f.AddSpline(EllipseAsRational(el)),
        SplineCurve3 sp => f.AddSpline(sp),
        _ => Curve(f, c),
    };

    static int LoopOriented(IgesFile f, TLoop loop)
    {
        var parts = new List<int>();
        foreach (var c in Oriented(loop)) { int id = CurveOriented(f, c); if (id != 0) parts.Add(id); }
        if (parts.Count == 0) return 0;
        return parts.Count == 1 ? parts[0] : f.AddComposite(parts);
    }

    /// <summary>
    /// Граница грани-пояса: пояс A от своей первой вершины S_A, шов (образующая) от S_A до точки S_B пояса B на том же угле,
    /// пояс B от S_B (кривая, на которую приходится угол, делится), шов назад. 0 — не удалось (поверхность, деление кривой).
    /// </summary>
    static int BeltBoundary(IgesFile f, ISurface3 surf, TLoop a, TLoop b, Vector3d o, Vector3d w)
    {
        var ca = Oriented(a); var cb = Oriented(b);
        var sA = ca[0].PointAt(ca[0].T0);
        var u = sA - o; u -= w * Vector3d.Dot(u, w); if (u.Length < 1e-9) return 0; u.Normalize(); var v = Vector3d.Cross(w, u);
        double Ang(Vector3d p) { var d = p - o; return Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u)); }   // угол S_A — 0
        // на поясе B — кривая, проходящая угол 0 (смена знака угла через 0, не через ±π)
        int k = -1; double tk = 0;
        for (int i = 0; i < cb.Count && k < 0; i++)
        {
            var c = cb[i]; const int N = 256; double prev = Ang(c.PointAt(c.T0));
            if (Math.Abs(prev) < 1e-12) { k = i; tk = c.T0; break; }
            for (int j = 1; j <= N; j++)
            {
                double t = c.T0 + (c.T1 - c.T0) * j / N, an = Ang(c.PointAt(t));
                if (Math.Abs(an) < 1e-12) { k = i; tk = t; break; }
                if (Math.Sign(an) != Math.Sign(prev) && Math.Abs(an - prev) < Math.PI)
                {
                    double lo = c.T0 + (c.T1 - c.T0) * (j - 1) / N, hi = t; double slo = Math.Sign(prev);
                    for (int it = 0; it < 100; it++) { double m = (lo + hi) / 2; if (Math.Sign(Ang(c.PointAt(m))) == slo) lo = m; else hi = m; }
                    k = i; tk = (lo + hi) / 2; break;
                }
                prev = an;
            }
        }
        if (k < 0) return 0;
        var parts = SplitAt(cb[k], tk);
        if (parts == null) return 0;
        // пояс B от S_B: хвост кривой k, кривые после неё, кривые до неё, голова кривой k
        var beltB = new List<ICurve3>();
        if (parts.Value.Tail != null) beltB.Add(parts.Value.Tail);
        for (int i = k + 1; i < cb.Count; i++) beltB.Add(cb[i]);
        for (int i = 0; i < k; i++) beltB.Add(cb[i]);
        if (parts.Value.Head != null) beltB.Add(parts.Value.Head);
        var sB = beltB[0].PointAt(beltB[0].T0);
        var seam = Seam(surf, sA, sB);
        if (seam == null) return 0;
        var ids = new List<int>();
        foreach (var c in ca) ids.Add(CurveOriented(f, c));
        ids.Add(CurveOriented(f, seam));
        foreach (var c in beltB) ids.Add(CurveOriented(f, c));
        ids.Add(CurveOriented(f, seam.Reversed()));
        if (ids.Any(x => x == 0)) return 0;
        return f.AddComposite(ids);
    }

    /// <summary>Деление кривой в параметре t: (голова до t, хвост от t); у конца — одна часть null.</summary>
    static (ICurve3? Head, ICurve3? Tail)? SplitAt(ICurve3 c, double t)
    {
        double span = c.T1 - c.T0;
        if (Math.Abs(t - c.T0) < 1e-12 * Math.Max(1, Math.Abs(span))) return (null, c);
        if (Math.Abs(t - c.T1) < 1e-12 * Math.Max(1, Math.Abs(span))) return (c, null);
        switch (c)
        {
            case LineCurve3 l: { var p = l.PointAt(t); return (new LineCurve3(l.A, p), new LineCurve3(p, l.B)); }
            case ArcCurve3 a: { double am = a.A0 + a.Sweep * t; return (new ArcCurve3(a.Frame, a.Radius, a.A0, am), new ArcCurve3(a.Frame, a.Radius, am, a.A1)); }
            case EllipseCurve3 e: { double am = e.A0 + (e.A1 - e.A0) * t; return (new EllipseCurve3(e.Frame, e.RU, e.RV, e.A0, am), new EllipseCurve3(e.Frame, e.RU, e.RV, am, e.A1)); }
        }
        if (!PieceOff) return (new Piece(c, c.T0, t, false), new Piece(c, t, c.T1, false));
        return null;
    }

    /// <summary>Шов грани вращения между точками на одной образующей: отрезок (цилиндр, конус) или дуга образующей.</summary>
    static ICurve3? Seam(ISurface3 surf, Vector3d a, Vector3d b)
    {
        switch (surf)
        {
            case CylinderSurface or ExtrudedSurface: return new LineCurve3(a, b);
            case RevolvedSurface rv when rv.Base is LineCurve3: return new LineCurve3(a, b);
            case RevolvedSurface rv when rv.Base is ArcCurve3 g:
            {
                // дуга образующей, повёрнутая в полуплоскость точек: центр — образ центра образующей на том же угле
                var o = rv.Axis.Origin; var w = rv.Axis.W.Normalized();
                var gc = g.Frame.Origin; var dc = gc - o; double zc = Vector3d.Dot(dc, w); var rc = dc - w * zc;
                var ra = a - o; ra -= w * Vector3d.Dot(ra, w);
                if (ra.Length < 1e-12) return null;
                var dir = ra.Normalized();
                // сторона центра образующей относительно оси — как у самой образующей
                var gm = g.PointAt(0.5) - o; gm -= w * Vector3d.Dot(gm, w);
                double sideC = rc.Length < 1e-12 ? 0 : (Vector3d.Dot(rc, gm) >= 0 ? 1 : -1) * rc.Length;
                var c = o + w * zc + dir * sideC;
                var e1 = (a - c).Normalized(); var nrm = Vector3d.Cross(dir, w).Normalized();
                var e2 = Vector3d.Cross(nrm, e1);
                double ang = Math.Atan2(Vector3d.Dot(b - c, e2), Vector3d.Dot(b - c, e1));
                // по образующей: середина дуги должна лежать на поверхности (иначе — дополнительная дуга)
                var arc = new ArcCurve3(new Frame3(c, e1, e2), g.Radius, 0, ang);
                var mid = arc.PointAt(0.5);
                var uvm = rv.ProjectToUV(mid);
                if ((rv.PointAt(uvm) - mid).Length > 1e-6 * Math.Max(1, g.Radius)) arc = new ArcCurve3(new Frame3(c, e1, e2), g.Radius, 0, ang > 0 ? ang - 2 * Math.PI : ang + 2 * Math.PI);
                return arc;
            }
        }
        return null;
    }

    /// <summary>
    /// Контур грани целиком — составной кривой. Прежде сохранялась только первая кривая, и от
    /// четырёхугольной грани оставался один отрезок.
    /// </summary>
    private static int Loop(IgesFile f, TLoop loop)
    {
        var parts = new List<int>();
        foreach (var ce in loop.CoEdges)
        {
            int id = Curve(f, ce.Edge.Curve);
            if (id != 0) parts.Add(id);
        }

        if (parts.Count == 0) return 0;
        return parts.Count == 1 ? parts[0] : f.AddComposite(parts);
    }

    /// <summary>Кривая: отрезок, дуга или сплайн.</summary>
    private static int Curve(IgesFile f, ICurve3? c) => c switch
    {
        LineCurve3 ln => f.AddLine(ln.A, ln.B),

        // Дуга пишется, только если лежит в плоскости, параллельной XY: у наклонной в IGES
        // нужна матрица преобразования, и подменять её поворотом координат нельзя.
        ArcCurve3 arc when Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), Vector3d.UnitZ)) - 1) < 1e-9
            // Дуга IGES (100) идёт от начала к концу ПРОТИВ часовой стрелки в плоскости XY.
            // Дуга, идущая по часовой (размах со знаком оси меньше нуля), пишется с переставленными
            // концами — иначе файл описывал дополнительную дугу, и кромка отверстия при чтении
            // становилась вдвое длиннее. Направление ребра читатель восстанавливает по стыкам.
            => arc.Sweep * Math.Sign(Vector3d.Dot(arc.Frame.W, Vector3d.UnitZ)) >= 0 || arc.IsClosed
                ? f.AddArcXY(arc.Frame.Origin, arc.PointAt(arc.T0), arc.PointAt(arc.T1))
                : f.AddArcXY(arc.Frame.Origin, arc.PointAt(arc.T1), arc.PointAt(arc.T0)),

        SplineCurve3 sp => f.AddSpline(sp),

        // K10: наклонная дуга (образующая шара и тора, кромка в вертикальной плоскости) —
        // ТОЧНЫЙ рациональный квадратичный B-сплайн; прежде такая кривая не писалась вовсе, и
        // грань вращения выпадала из файла.
        ArcCurve3 tilted => f.AddSpline(ArcAsRational(tilted)),

        // KRN-06: эллипс (стык цепочки, наклонный торец ленты) — аффинный образ дуги: точный рациональный B-сплайн.
        EllipseCurve3 el when !IgesCylOff => f.AddSpline(EllipseAsRational(el)),

        // m.x2: кривая пересечения (плоскость × выдавливание сплайна и т. п.) и кривая по формуле — кубическим B-сплайном с
        // проверенным отходом (бюджет экспорта / 100: плоская грань и сплайновая поверхность по разные стороны кривой — объём
        // у OpenCascade отходил на 3e-6 при 1e-4), как в STEP; прежде — 0, и контур грани выпадал (грань без границы).
        // P3D_IGESFIT=0 — прежде.
        _ when c != null && !FitOff && SplineFit.Fit(c, ToleranceContext.Current.ExportFit / 100) is { } fit => f.AddSpline(fit.Spline),

        _ => 0
    };
    static readonly bool FitOff = Environment.GetEnvironmentVariable("P3D_IGESFIT") == "0";

    /// <summary>
    /// Дуга окружности как рациональный квадратичный B-сплайн (точное представление): куски
    /// не больше 90°, средний вес — cos(половины угла куска), внутренние узлы двойные.
    /// </summary>
    /// <summary>Цилиндр как поверхность вращения (120): ось и отрезок-образующая; длина — по проекциям точек рёбер грани на ось.</summary>
    static int CylinderAsRevolved(IgesFile f, TFace face, Vector3d o, Vector3d wv, Vector3d uv, double radius)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var ce in face.Loops.SelectMany(l => l.CoEdges))
            for (int k = 0; k <= 8; k++)
            {
                var q = ce.Edge.Curve.PointAt(ce.Edge.Curve.T0 + (ce.Edge.Curve.T1 - ce.Edge.Curve.T0) * k / 8.0);
                double h = Vector3d.Dot(q - o, wv); lo = Math.Min(lo, h); hi = Math.Max(hi, h);
            }
        if (lo > hi) return 0;
        double pad = Math.Max(1e-3, 0.01 * (hi - lo));
        int axis = IgesImport.Iges120Old ? f.AddLine(o, o + wv) : f.AddLine(o + wv, o);
        int gen = f.AddLine(o + uv * radius + wv * (lo - pad), o + uv * radius + wv * (hi + pad));
        return f.AddRevolved(axis, gen, 0, 2 * Math.PI);
    }

    static readonly bool IgesCylOff = Environment.GetEnvironmentVariable("P3D_IGESCYL") == "0";

    internal static SplineCurve3 EllipseAsRational(EllipseCurve3 e)
    {
        var u = e.Frame.U.Normalized(); var v = e.Frame.V.Normalized(); var o = e.Frame.Origin;
        var unit = ArcAsRational(new ArcCurve3(new Frame3(o, u, v), 1, e.A0, e.A1));
        var pts = unit.ControlPoints.Select(p => o + u * (e.RU * Vector3d.Dot(p - o, u)) + v * (e.RV * Vector3d.Dot(p - o, v))).ToList();
        return new SplineCurve3(pts, 2, unit.Weights, unit.Knots);
    }

    internal static SplineCurve3 ArcAsRational(ArcCurve3 a)
    {
        double sweep = a.Sweep;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-12));
        double step = sweep / n;
        var pts = new List<Vector3d>(); var w = new List<double>(); var knots = new List<double> { 0, 0, 0 };
        double wm = Math.Cos(Math.Abs(step) / 2);
        Vector3d P(double ang) => a.Frame.Origin + a.Radius * (Math.Cos(ang) * a.Frame.U.Normalized() + Math.Sin(ang) * a.Frame.V.Normalized());
        for (int i = 0; i < n; i++)
        {
            double t0 = a.A0 + step * i, t1 = t0 + step, tm = (t0 + t1) / 2;
            if (i == 0) { pts.Add(P(t0)); w.Add(1); }
            // Вершина угла касательных: на биссектрисе на расстоянии R / cos(θ/2).
            var mid = a.Frame.Origin + (a.Radius / wm) * (Math.Cos(tm) * a.Frame.U.Normalized() + Math.Sin(tm) * a.Frame.V.Normalized());
            pts.Add(mid); w.Add(wm);
            pts.Add(P(t1)); w.Add(1);
            if (i + 1 < n) { knots.Add((i + 1.0) / n); knots.Add((i + 1.0) / n); }
        }
        knots.AddRange(new[] { 1.0, 1.0, 1.0 });
        return new SplineCurve3(pts, 2, w, knots);
    }
}
