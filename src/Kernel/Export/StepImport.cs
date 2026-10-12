using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// ЧТЕНИЕ STEP В ТЕЛО. Разбор файла в треугольники уже был (ReadStepFaceted), но им никто не
/// пользовался: треугольники — это ещё не тело. Здесь они собираются в TSolid.
///
/// Треугольники объединяются в грани по ОПОРНОЙ ПЛОСКОСТИ: иначе у прочитанного куба было бы
/// двенадцать граней вместо шести, и ни скругление, ни выбор грани работать бы не стали.
/// Кривизну прочитанного тела это не восстанавливает — гранёный STEP её и не несёт.
/// </summary>
public static class StepImport
{
    public static List<TSolid> ReadSolids(string text, List<string>? warnings = null)
    {
        var solids = new List<TSolid>();
        var shells = MeshExport.ReadStepFaceted(text);

        foreach (var tris in shells)
        {
            if (tris.Count == 0) continue;

            // Метка грани — её опорная плоскость: нормаль и расстояние до начала, огрублённые
            // до допуска склейки, иначе одна плоскость разошлась бы на несколько из-за счёта.
            var polys = new List<CsgPolygon>();
            var sources = new Dictionary<uint, ISurfaceRef>();
            var byPlane = new Dictionary<(long, long, long, long), uint>();
            uint next = 1;

            foreach (var (a, b, c) in tris)
            {
                Vector3d n = Vector3d.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-24) continue;
                n = n.Normalized();
                double d = Vector3d.Dot(n, a);

                var key = ((long)Math.Round(n.X * 1e6), (long)Math.Round(n.Y * 1e6),
                           (long)Math.Round(n.Z * 1e6), (long)Math.Round(d * 1e4));
                if (!byPlane.TryGetValue(key, out uint tag))
                {
                    byPlane[key] = tag = next++;
                    sources[tag] = new ISurfaceRef(Frame3.FromNormal(a, n));
                }
                polys.Add(new CsgPolygon(new List<Vector3d> { a, b, c }, tag));
            }

            if (polys.Count == 0) continue;

            var srcs = new Dictionary<uint, Surfaces.ISurface3?>();
            var names = new Dictionary<uint, string>();
            foreach (var (tag, r) in sources)
            {
                srcs[tag] = new Surfaces.PlaneSurface(r.Frame);
                names[tag] = $"step:face[{tag}]";
            }

            solids.Add(BooleanOp.SolidFromPolygons(polys, srcs, names));
        }

        if (warnings != null && solids.Count == 0) warnings.Add("STEP: no solids found in the file.");
        return solids;
    }

    /// <summary>
    /// ЧТЕНИЕ ТОЧНОГО STEP В ТЕЛО. Грани ADVANCED_FACE собираются в оболочку с ОБЩИМИ рёбрами и
    /// вершинами: без общих рёбер это был бы набор отдельных лоскутов, непригодный ни для
    /// булевой, ни для скруглений. Вершины и рёбра сопоставляются по положению с допуском
    /// склейки — в файле они всегда заданы отдельными сущностями.
    /// </summary>
    /// <summary>Мутация g.14 (P3D_STEPNORMAL=0): same_sense — относительно нормали нашей поверхности, как прежде.</summary>
    static readonly bool StepNormalOff = Environment.GetEnvironmentVariable("P3D_STEPNORMAL") == "0";

    /// <summary>
    /// g.14: совпадает ли нормаль нашей поверхности (по параметризации) с нормалью квадрики ПО СТАНДАРТУ STEP: цилиндр и
    /// конус — наружу от оси, шар — от центра, тор — от окружности центров трубки. Плоскость, B-сплайн, линейная протяжка —
    /// нормаль по параметризации совпадает со стандартной.
    /// </summary>
    static readonly bool MeshWhyDbg = Environment.GetEnvironmentVariable("P3D_MESHWHY") == "1";
    static readonly bool OrientVoteOff = Environment.GetEnvironmentVariable("P3D_ORIENTVOTE") == "0";
    static readonly bool ImportApexOff = Environment.GetEnvironmentVariable("P3D_IMPAPEX") == "0";
    static readonly bool ImportSeamOff = Environment.GetEnvironmentVariable("P3D_IMPSEAM") == "0";

    internal static bool OurNormalIsCanonical(Surfaces.ISurface3 s)
    {
        if (StepNormalOff) return true;
        static Vector3d Radial(Vector3d o, Vector3d w, Vector3d p) { var d = p - o; return d - w * Vector3d.Dot(d, w); }
        var uv = new Vector2d(0.5, 0.5);
        Vector3d canon;
        switch (s)
        {
            case Surfaces.ExtrudedSurface ex when ex.Base is Curves.ArcCurve3 arc:
                canon = Radial(arc.Frame.Origin, arc.Frame.W.Normalized(), s.PointAt(uv)); break;
            case Surfaces.RevolvedSurface rv when rv.Base is Curves.LineCurve3:
                canon = Radial(rv.Axis.Origin, rv.Axis.W.Normalized(), s.PointAt(uv)); break;
            case Surfaces.RevolvedSurface rv when rv.Base is Curves.ArcCurve3 gen:
            {
                var p = s.PointAt(uv); var w = rv.Axis.W.Normalized(); var c = gen.Frame.Origin;
                double off = Radial(rv.Axis.Origin, w, c).Length;
                if (off < Tol.Weld) { canon = p - c; break; }
                var centre = rv.Axis.Origin + w * Vector3d.Dot(c - rv.Axis.Origin, w);
                canon = p - (centre + Radial(centre, w, p).Normalized() * off); break;
            }
            default: return true;
        }
        var n = s.NormalAt(uv);
        return Vector3d.Dot(n, canon) >= 0;
    }

    public static List<TSolid> ReadExactSolids(string text, List<string>? warnings = null)
    {
        var parser = StepParser.Parse(text);
        if (parser.LengthUnitNote != null) warnings?.Add(parser.LengthUnitNote);
        var solids = new List<TSolid>();
        var solidOfShell = new Dictionary<int, TSolid>();

        foreach (var (id, e) in parser.Entities)
        {
            if (e.Type != "CLOSED_SHELL" && e.Type != "OPEN_SHELL") continue;

            var solid = new TSolid();
            var shell = new TShell { Solid = solid };
            solid.Shells.Add(shell);

            var verts = new Dictionary<(long, long, long), TVertex>();
            // В ключ ребра входит и СЕРЕДИНА кривой: у грани сплайн и замыкающий отрезок
            // соединяют одни и те же точки, и ключ по одним концам сливал их в одно ребро —
            // крышки прочитанного тела оставались с одним ребром и нулевой площадью.
            var edges = new Dictionary<string, TEdge>();

            static (long, long, long) K(Vector3d p) =>
                ((long)Math.Round(p.X / Tol.Weld), (long)Math.Round(p.Y / Tol.Weld), (long)Math.Round(p.Z / Tol.Weld));

            TVertex V(Vector3d p)
            {
                var k = K(p);
                if (verts.TryGetValue(k, out var v)) return v;
                return verts[k] = new TVertex(p);
            }

            int faceCount = 0;
            foreach (string faceRef in StepParser.SplitArgs(e.Args[1].Trim('(', ')')))
            {
                var got = parser.Face(faceRef);
                if (got == null) continue;

                // ПРОТЯЖЁННОСТЬ БЕСКОНЕЧНОЙ ПОВЕРХНОСТИ БЕРЁТСЯ ИЗ КОНТУРА. Цилиндр в STEP
                // бесконечен, и выдавливание по единичному вектору оси дало бы поверхность
                // высотой 1: у прочитанного цилиндра площадь боковой грани выходила 157 вместо
                // 9425, а объём 40 536 вместо 117 810.
                Surfaces.ISurface3 surface = got.Value.Surface;

                // ПРОТЯЖЁННОСТЬ КОНУСА берётся из контура так же, как у цилиндра: в файле
                // образующая задана лишь наклоном, а её длина следует из границ грани.
                if (surface is Surfaces.RevolvedSurface rvc && rvc.Base is Curves.LineCurve3 gl0)
                {
                    Vector3d axis0 = rvc.Axis.W.Normalized();
                    double lo0 = double.MaxValue, hi0 = double.MinValue;
                    foreach (var lc in got.Value.Loops)
                        foreach (var c in lc)
                            foreach (double t in new[] { c.T0, (c.T0 + c.T1) / 2, c.T1 })
                            {
                                double z = Vector3d.Dot(c.PointAt(t) - rvc.Axis.Origin, axis0);
                                lo0 = Math.Min(lo0, z); hi0 = Math.Max(hi0, z);
                            }

                    // g.05/KRN-09: ГРАНЬ ОСТРОГО КОНУСА — контур из одной окружности (вершина — особая точка, в контуре её нет):
                    // грань — от окружности до вершины (единственная конечная область одной полости, ограниченная одной
                    // окружностью). Прежде образующая оставалась нулевой высоты: наш же острый конус читался телом объёмом ~0.
                    // Мутация P3D_IMPAPEX=0.
                    if (!ImportApexOff && hi0 - lo0 <= Tol.Weld)
                    {
                        double za1 = Vector3d.Dot(gl0.A - rvc.Axis.Origin, axis0), zb1 = Vector3d.Dot(gl0.B - rvc.Axis.Origin, axis0);
                        double Rho(Vector3d q) { var d = q - rvc.Axis.Origin; return (d - axis0 * Vector3d.Dot(d, axis0)).Length; }
                        double ra = Rho(gl0.A), rb = Rho(gl0.B);
                        if (Math.Abs(zb1 - za1) > Tol.Weld && Math.Abs(rb - ra) > Tol.Weld)
                        {
                            Vector3d dir1 = (gl0.B - gl0.A) / (zb1 - za1);          // на единицу высоты
                            double drho = (rb - ra) / (zb1 - za1);
                            double zApex = za1 - ra / drho;
                            var pc = gl0.A + dir1 * (lo0 - za1); var pa = gl0.A + dir1 * (zApex - za1);
                            // порядок точек — как у исходной образующей (по возрастанию высоты, если она возрастала)
                            bool up = zb1 > za1;
                            var (q0, q1) = (zApex < lo0) == up ? (pa, pc) : (pc, pa);
                            surface = new Surfaces.RevolvedSurface(new Curves.LineCurve3(q0, q1), rvc.Axis, 0, 2 * Math.PI);
                        }
                    }
                    if (hi0 - lo0 > Tol.Weld)
                    {
                        // Точки образующей на границах по оси: радиус меняется по наклону.
                        double za0 = Vector3d.Dot(gl0.A - rvc.Axis.Origin, axis0);
                        double zb0 = Vector3d.Dot(gl0.B - rvc.Axis.Origin, axis0);
                        if (Math.Abs(zb0 - za0) > Tol.Weld)
                        {
                            Vector3d dir0 = (gl0.B - gl0.A) / (zb0 - za0);
                            Vector3d p0 = gl0.A + dir0 * (lo0 - za0);
                            Vector3d p1 = gl0.A + dir0 * (hi0 - za0);
                            surface = new Surfaces.RevolvedSurface(new Curves.LineCurve3(p0, p1), rvc.Axis, 0, 2 * Math.PI);
                        }
                    }
                }
                if (surface is Surfaces.ExtrudedSurface ex && ex.Base is Curves.ArcCurve3 baseArc)
                {
                    double lo = double.MaxValue, hi = double.MinValue;
                    Vector3d axis = ex.Dir.Normalized();
                    foreach (var loopCurves in got.Value.Loops)
                        foreach (var c in loopCurves)
                            foreach (double t in new[] { c.T0, (c.T0 + c.T1) / 2, c.T1 })
                            {
                                double s2 = Vector3d.Dot(c.PointAt(t) - baseArc.Frame.Origin, axis);
                                lo = Math.Min(lo, s2); hi = Math.Max(hi, s2);
                            }

                    if (hi - lo > Tol.Weld)
                    {
                        // УГЛОВОЙ РАЗМАХ, А НЕ ПРЕДЕЛЫ ПАРАМЕТРА. Параметр дуги нормирован (0..1),
                        // и передача этих чисел как углов давала дугу в один радиан: длина 25
                        // вместо 157, и грань накрывалась на 1/2π своей площади.
                        var moved = new Curves.ArcCurve3(
                            new Frame3(baseArc.Frame.Origin + axis * lo, baseArc.Frame.U, baseArc.Frame.V),
                            baseArc.Radius, baseArc.A0, baseArc.A1);
                        surface = new Surfaces.ExtrudedSurface(moved, axis * (hi - lo));
                    }
                }

                // УГЛОВОЙ РАЗМАХ ТЕЛА ВРАЩЕНИЯ — ПО КОНТУРУ, С РАЗВОРОТОМ ЧЕРЕЗ ШОВ. Поверхность
                // в файле описана полным оборотом, а грань занимает часть; сетка по всему обороту
                // уходит за деталь. Размах нельзя брать по наименьшему и наибольшему значению: у
                // грани, пересекающей шов, получаются одновременно 0 и 1. Поэтому обход идёт
                // подряд, и скачок больше полуоборота снимается сдвигом на оборот.
                // g.05/KRN-09: ГРАНЬ — ВСЯ ЗАМКНУТАЯ ПОВЕРХНОСТЬ (контур из одних швов: каждая кривая пройдена дважды — сфера с
                // меридианом-швом, тор с двумя швами): размах и широта не обрезаются по контуру. Прежде угловой размах повёрнутой
                // сферы по точкам шва выходил π, образующая тора обрезалась до дуги в 0.3 мм — наш же STEP читался объёмом 0/NaN.
                // Мутация P3D_IMPSEAM=0.
                bool seamOnlyFace = false;
                if (!ImportSeamOff)
                {
                    var allC = got.Value.Loops.SelectMany(l => l).ToList();
                    Vector3d Mid(Curves.ICurve3 c) => c.PointAt((c.T0 + c.T1) / 2);
                    seamOnlyFace = allC.Count >= 2 && allC.All(c => allC.Count(d => (Mid(d) - Mid(c)).Length < 10 * Tol.Weld) >= 2);
                }
                if (surface is Surfaces.RevolvedSurface rvAng && !seamOnlyFace)
                {
                    double lo = double.MaxValue, hi = double.MinValue;
                    foreach (var lc in got.Value.Loops)
                    {
                        double prev = double.NaN, shift = 0;
                        foreach (var c in lc)
                            for (int k = 0; k <= 8; k++)
                            {
                                double v = rvAng.ProjectToUV(c.PointAt(c.T0 + (c.T1 - c.T0) * k / 8.0)).Y;
                                if (!double.IsNaN(prev))
                                {
                                    double d = v + shift - prev;
                                    if (d > 0.5) shift -= 1;
                                    else if (d < -0.5) shift += 1;
                                }
                                double vv = v + shift;
                                prev = vv;
                                lo = Math.Min(lo, vv); hi = Math.Max(hi, vv);
                            }
                    }

                    double span = hi - lo;
                    if (span > 1e-6 && span < 0.999)
                    {
                        double full = rvAng.A1 - rvAng.A0;
                        surface = new Surfaces.RevolvedSurface(rvAng.Base, rvAng.Axis,
                                                               rvAng.A0 + full * lo, rvAng.A0 + full * hi);
                    }
                }

                // K10. ШИРОТА ТЕЛА ВРАЩЕНИЯ — ТОЖЕ ПО КОНТУРУ. В файле шар описан целиком, а грань —
                // чаша; запасная разметка накрывала всю сферу (площадь 801 вместо 400 мм²). Образующая
                // обрезается по параметрам точек контура; если грань содержит полюс (контур обходит
                // ось), образующая продолжается до этого полюса. Какой полюс внутри, решает
                // направление обхода: материал слева, если смотреть против наружной нормали.
                if (surface is Surfaces.RevolvedSurface rvLat && !seamOnlyFace &&
                    (rvLat.Base is Curves.ArcCurve3 || rvLat.Base is Curves.LineCurve3))
                {
                    var bc = rvLat.Base;
                    Vector3d w = rvLat.Axis.W.Normalized();
                    double Rad(Vector3d q) { var d = q - rvLat.Axis.Origin; return (d - w * Vector3d.Dot(d, w)).Length; }
                    double tLo = double.MaxValue, tHi = double.MinValue, winding = 0;
                    var u0 = rvLat.Axis.U.Normalized(); var v0 = Vector3d.Cross(w, u0);
                    double Theta(Vector3d q) { var d = q - rvLat.Axis.Origin; return Math.Atan2(Vector3d.Dot(d, v0), Vector3d.Dot(d, u0)); }
                    for (int li = 0; li < got.Value.Loops.Count; li++)
                        foreach (var c in got.Value.Loops[li])
                        {
                            double prevT = double.NaN;
                            for (int k = 0; k <= 16; k++)
                            {
                                var q = c.PointAt(c.T0 + (c.T1 - c.T0) * k / 16.0);
                                double t = rvLat.ProjectToUV(q).X;
                                tLo = Math.Min(tLo, t); tHi = Math.Max(tHi, t);
                                double th = Theta(q);
                                if (!double.IsNaN(prevT)) { double d = th - prevT; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; if (li == 0) winding += d; }
                                prevT = th;
                            }
                        }
                    double span = bc.T1 - bc.T0;
                    // Замкнутая образующая (тор): диапазон — дополнение к наибольшему промежутку
                    // параметров на окружности, иначе грань, пересекающая шов образующей, давала
                    // весь круг.
                    if (bc is Curves.ArcCurve3 gArc && gArc.IsClosed)
                    {
                        var ts = new List<double>();
                        foreach (var lc in got.Value.Loops)
                            foreach (var c in lc)
                                for (int k = 0; k <= 16; k++)
                                    ts.Add(((rvLat.ProjectToUV(c.PointAt(c.T0 + (c.T1 - c.T0) * k / 16.0)).X - bc.T0) / span % 1 + 1) % 1);
                        ts.Sort();
                        double gap = 0, after = 0, before = 1;
                        for (int i = 0; i < ts.Count; i++)
                        {
                            double nx = i + 1 < ts.Count ? ts[i + 1] : ts[0] + 1;
                            if (nx - ts[i] > gap) { gap = nx - ts[i]; before = ts[i]; after = nx % 1; }
                        }
                        if (gap > 1e-3)
                        {
                            double a0 = after, a1 = before < after ? before + 1 : before;
                            var trimmedT = new Curves.ArcCurve3(gArc.Frame, gArc.Radius, gArc.A0 + gArc.Sweep * a0, gArc.A0 + gArc.Sweep * a1);
                            surface = new Surfaces.RevolvedSurface(trimmedT, rvLat.Axis, rvLat.A0, rvLat.A1);
                        }
                        goto latitudeDone;
                    }
                    bool pole0 = Rad(bc.PointAt(bc.T0)) < Tol.Weld, pole1 = Rad(bc.PointAt(bc.T1)) < Tol.Weld;
                    double sgnOut = (got.Value.SameSense ^ !OurNormalIsCanonical(rvLat)) ? 1 : -1;
                    bool Contains(double tEnd, double toward)
                    {
                        double tIn = tEnd + toward * 1e-3 * span;
                        var nrm = rvLat.NormalAt(new Vector2d(tIn, 0.5)) * sgnOut;
                        return Math.Abs(winding) > Math.PI && Math.Sign(winding) == Math.Sign(Vector3d.Dot(nrm, w));
                    }
                    double lo2 = tLo, hi2 = tHi;
                    if (pole0 && Contains(bc.T0, +1)) lo2 = bc.T0;
                    else if (pole1 && Contains(bc.T1, -1)) hi2 = bc.T1;
                    if (hi2 - lo2 > 1e-9 && (lo2 > bc.T0 + 1e-9 || hi2 < bc.T1 - 1e-9))
                    {
                        Curves.ICurve3 trimmed = bc is Curves.ArcCurve3 ga
                            ? new Curves.ArcCurve3(ga.Frame, ga.Radius, ga.A0 + ga.Sweep * (lo2 - bc.T0) / span, ga.A0 + ga.Sweep * (hi2 - bc.T0) / span)
                            : new Curves.LineCurve3(bc.PointAt(lo2), bc.PointAt(hi2));
                        surface = new Surfaces.RevolvedSurface(trimmed, rvLat.Axis, rvLat.A0, rvLat.A1);
                    }
                    latitudeDone:;
                }

                var face = new TFace
                {
                    Surface = surface,
                    // флаг из файла, а не догадка; same_sense в STEP — относительно НОРМАЛИ ПО СТАНДАРТУ (у квадрик — наружу от
                    // оси, центра, окружности тора), а у нашей поверхности нормаль задана параметризацией (g.14)
                    Reversed = !got.Value.SameSense ^ !OurNormalIsCanonical(surface),
                    Tag = $"step:face[{faceCount}]"
                };
                for (int li = 0; li < got.Value.Loops.Count; li++)
                {
                    var loop = face.AddLoop(li == 0);
                    foreach (var c in got.Value.Loops[li])
                    {
                        Vector3d a = c.PointAt(c.T0), b = c.PointAt(c.T1);
                        if (parser.LastEnds.TryGetValue(c, out var fileEnds)) { a = fileEnds.Start; b = fileEnds.End; }
                        Vector3d mid = c.PointAt((c.T0 + c.T1) / 2);
                        var ka = K(a); var kb = K(b); var km = K(mid);
                        string ends = string.CompareOrdinal(ka.ToString(), kb.ToString()) <= 0
                            ? ka + "|" + kb : kb + "|" + ka;
                        string key = ends + "|" + km;

                        if (!edges.TryGetValue(key, out var edge))
                        {
                            edge = new TEdge { V0 = V(a), V1 = V(b), Curve = c };
                            // Допуск ребра — расхождение концов кривой с вершинами файла (у дуги,
                            // подогнанной к точкам, — микроны).
                            Vector3d ca = c.PointAt(c.T0), cb = c.PointAt(c.T1);
                            edge.Tolerance = Math.Max(Math.Min((ca - edge.V0.P).Length, (ca - edge.V1.P).Length),
                                                      Math.Min((cb - edge.V0.P).Length, (cb - edge.V1.P).Length));
                            edges[key] = edge;
                        }
                        // НАПРАВЛЕНИЕ ОБХОДА. У замкнутой окружности начало и конец совпадают,
                        // и сравнение концов всегда давало «не развёрнуто»: верхняя окружность
                        // цилиндра шла в ту же сторону, что нижняя, контур не замыкался, и грань
                        // покрывалась сеткой лишь на шестую часть. Поэтому для замкнутых кривых
                        // направление берётся по КАСАТЕЛЬНОЙ в начале.
                        bool rev;
                        if ((a - b).Length <= Tol.Weld)
                        {
                            Vector3d tMine = c.TangentAt(c.T0);
                            Vector3d tEdge = edge.Curve.TangentAt(edge.Curve.T0);
                            rev = Vector3d.Dot(tMine, tEdge) < 0;
                        }
                        else rev = (edge.V0.P - a).Length > Tol.Weld;
                        loop.Add(edge, rev);
                    }
                }
                // Обёртка в гранёную поверхность — под переключателем: так к прочитанному телу
                // можно применить точный построитель и увидеть его причины отказа.
                // ОБЁРТКА В ГРАНЁНУЮ ПОВЕРХНОСТЬ ОТКЛЮЧЕНА: без неё точный построитель к
                // прочитанным граням не применяется, но с ней непарных сторон становилось 6 213
                // вместо 2 186 (см. ниже).

                // ПРОБОВАЛ оборачивать грань в гранёную поверхность с черновой сеткой, чтобы
                // к прочитанному телу можно было применить точный построитель (как после
                // булевой). Стало хуже: непарных сторон 9 951 вместо 3 432. Значит, дело не в
                // отсутствии черновой сетки, а в самом построителе на таких гранях.

                shell.Add(face);
                faceCount++;
            }

            if (faceCount > 0)
            {
                // Видимая сетка строится тем же расчётом, что и после булевой: иначе грань
                // накрывается по всей области поверхности, а не по своему контуру (у детали из
                // обменного файла сетка уходила до радиуса 4.03 при контуре 3.625).
                // ПРИЁМ ПО ГРАНЯМ ПРОБОВАЛ — ХУЖЕ. Замысел: у прочитанного тела согласованной
                // сетки нет и так, значит, 55 хороших граней из 56 лучше, чем ни одной. Измерено:
                // новую сетку получили 32 грани из 56, а непарных сторон стало 6 168 вместо
                // 2 186 — смешение новых и старых граней рвёт стыки сильнее, чем грубая, но
                // однородная сетка. Возможность оставлена в ядре (perFace), здесь не включается.
                // ВСЕ ГРАНИ СТРОЯТСЯ ЗАНОВО ОТ КОНТУРОВ, с общими точками рёбер: точки ребра
                // считаются один раз, и обе соседние грани берут их же — швов по построению нет.
                // Прежние попытки (обёртка поверх грубой сетки, приём по граням) только рвали
                // стыки, потому что смешивали сетки разного происхождения.
                MeshFacesFromLoops(solid, warnings, "STEP");

                // СОГЛАСОВАНИЕ ОРИЕНТАЦИИ ПО ТЕЛУ. Нормаль поверхности не задаёт «наружу»: у
                // плоскости она произвольна. Поэтому грани обходятся от первой, и сосед
                // разворачивается, если общую сторону оба проходят в одну сторону. В конце знак
                // объёма приводится к положительному.
                OrientShell(solid);
                solids.Add(solid);
                solidOfShell[id] = solid;
            }
        }

        // g.14: ПОЛОСТИ (BREP_WITH_VOIDS). Оболочка полости в файле — самостоятельное тело (прочитано и приведено к
        // положительному объёму); в тело она входит полостью: грани развёрнуты (сторона, контуры, фасеты).
        foreach (var (_, e) in parser.Entities)
        {
            if (e.Type != "BREP_WITH_VOIDS" || e.Args.Count < 3) continue;
            if (!int.TryParse(e.Args[1].Trim().TrimStart('#'), out int outerId) || !solidOfShell.TryGetValue(outerId, out var outer)) continue;
            foreach (var vr in StepParser.SplitArgs(e.Args[2].Trim().Trim('(', ')')))
            {
                if (!int.TryParse(vr.Trim().TrimStart('#'), out int ocsId) || !parser.Entities.TryGetValue(ocsId, out var ocs)) continue;
                string shRef = ocs.Type == "ORIENTED_CLOSED_SHELL" && ocs.Args.Count >= 3 ? ocs.Args[2] : vr;
                if (!int.TryParse(shRef.Trim().TrimStart('#'), out int shId) || !solidOfShell.TryGetValue(shId, out var vs)) continue;
                var cav = new TShell { Solid = outer }; outer.Shells.Add(cav);
                foreach (var f in vs.Faces.ToList())
                {
                    FlipFace(f);
                    cav.Add(f);
                }
                solids.Remove(vs);
            }
        }

        if (warnings != null && solids.Count == 0) warnings.Add("STEP: no exact shells found in the file.");
        return solids;
    }

    /// <summary>
    /// Согласовать обход треугольников всех граней тела: соседи по общей стороне должны
    /// проходить её навстречу.
    /// </summary>

    /// <summary>
    /// K10. Разметка граней прочитанного тела по их контурам (общие точки рёбер — общие
    /// вершины сетки) и сторона по флагу грани. Общая для STEP и IGES.
    /// </summary>
    /// <summary>Сколько граней получили сетку от донора (разбор, проверки).</summary>
    [ThreadStatic] public static int DonorReused;

    /// <summary>Возвращает число граней, не построенных по контурам (оставленных как есть).</summary>
    internal static int MeshFacesFromLoops(TSolid solid, List<string>? warnings, string format,
                                           Func<TFace, Surfaces.FacetedSurface?>? donor = null)
    {
        using var _oh = Diagnostics.Overhead.Scope();
        int built = 0, failed = 0;
        foreach (var f2 in solid.Faces.ToList())
        {
            // Донор (аналитический маршрут): та же грань у операнда — по строгому ключу (поверхность по содержанию,
            // контуры с параметрами кривых побитово) — даёт готовую сетку: разметчик получил бы те же входные данные.
            if (donor?.Invoke(f2) is { } dn)
            {
                var copy = new Surfaces.FacetedSurface(dn.Source, dn.FallbackNormal);
                foreach (var t in dn.Facets.ReadView) copy.Facets.Add(t);
                copy.SetDisplay(dn.Display, dn.DisplayNormals);
                copy.FromLoops = true;
                f2.Surface = copy;
                built++; DonorReused++;
                continue;
            }
            // Грань, у которой контур — ОДИН ШОВ (полный шар, замкнутая поверхность):
            // строить по такому контуру нечего, у неё остаётся своя точная сетка.
            // Шов — ОДНО ребро, пройденное контуром дважды. Контур из одной замкнутой кромки (стенка
            // отверстия между двумя кромками, диск) — не шов: прежнее условие пропускало такую
            // стенку, и её сетка не сходилась с соседними гранями (точная булева, G4).
            bool seamOnly = f2.Loops.All(l => l.CoEdges.Count >= 2 && l.CoEdges.Select(c => c.Edge).Distinct().Count() <= 1);
            // KRN-05/g.14: ЗАМКНУТАЯ ПОВЕРХНОСТЬ БЕЗ КОНТУРОВ (шар-полость из общей булевой): сетка — по самой поверхности,
            // замкнутая (полюсы — одной вершиной, шов — общими вершинами). Мутация P3D_CLOSEDMESH=0 — прежний отказ.
            if (f2.Loops.All(l => l.CoEdges.Count == 0) && !ClosedMeshOff && ClosedSurfaceMesh(f2)) { built++; continue; }
            if (seamOnly) { failed++; continue; }

            (List<Vector3d> P, List<int> T)? got2; string? why2;
            using (Diagnostics.Prof.Enabled ? Diagnostics.Prof.Time("разметка грани: " + (Topology.GeometryAudit.AuthoritativeSurface(f2)?.GetType().Name ?? "?") + (Topology.GeometryAudit.AuthoritativeSurface(f2) is Surfaces.RevolvedSurface rvp ? "/" + Geometry.ExactEdges.ShapeOf(rvp)?.GetType().Name : "")) : null)
                got2 = Tess.ExactFaceMesher.Mesh(f2, out why2, out _);
            if (got2 == null)
            {
                // Грань, которую построитель отверг, остаётся со своей отдельной сеткой,
                // и соседи по ней расходятся: у трубы так остаются две грани из четырёх
                // (238 непарных сторон из 1 762). Попытка брать «лучшую попытку» без
                // придирок не помогла — отказ происходит раньше этой проверки.
                failed++;
                warnings?.Add($"{format}: грань {f2.Id} [{GeometryAudit.AuthoritativeSurface(f2)?.GetType().Name}] не построена по контурам: {why2}");
                if (MeshWhyDbg) { string uvw; try { uvw = Tess.UvMesher.Mesh(f2, Core.Tol.Chord, out var w3) is { } ? "ok" : w3 ?? "null"; } catch (Exception ex) { uvw = "exception " + ex.Message; } Console.Error.WriteLine($"MESHWHY {format} face {f2.Tag} id {f2.Id} hint {f2.InteriorHint} {Topology.GeometryAudit.AuthoritativeSurface(f2)?.GetType().Name} loops {f2.Loops.Count} ces {string.Join("/", f2.Loops.Select(l => l.CoEdges.Count))}: {why2} || uv: {uvw}"); }
                continue;
            }

            var (pts2, tri2) = got2.Value;
            Vector3d n2 = Vector3d.Zero;
            var fs2 = new Surfaces.FacetedSurface(f2.Surface is Surfaces.FacetedSurface fx ? fx.Source : f2.Surface, Vector3d.UnitZ);
            for (int i = 0; i + 2 < tri2.Count; i += 3)
            {
                Vector3d a2 = pts2[tri2[i]], b2 = pts2[tri2[i + 1]], c2 = pts2[tri2[i + 2]];
                fs2.Facets.Add((a2, b2, c2));
                n2 += Vector3d.Cross(b2 - a2, c2 - a2);
            }
            if (fs2.Facets.Count == 0) { failed++; continue; }

            // СТОРОНА ГРАНИ — ПО ФЛАГУ ИЗ ФАЙЛА И НОРМАЛИ ПОВЕРХНОСТИ. Построитель
            // определяет сторону сравнением с прежней сеткой, а у прочитанной грани её
            // нет: соседи проходили общие рёбра в одну сторону (все 431 непарная сторона
            // была «2 вперёд, 0 назад»).
            var facets2 = fs2.Facets.ToList();
            if (facets2.Count > 0 && fs2.Source != null)
            {
                var (ta, tb, tc) = facets2[0];
                Vector3d nt = Vector3d.Cross(tb - ta, tc - ta);
                Vector3d g2 = (ta + tb + tc) / 3;
                Vector3d ns = fs2.Source.NormalAt(fs2.Source.ProjectToUV(g2));
                if (ns.LengthSquared > 1e-18 && nt.LengthSquared > 1e-18)
                {
                    double want = f2.Reversed ? -1 : 1;
                    if (Vector3d.Dot(nt.Normalized(), ns.Normalized()) * want < 0)
                    {
                        for (int i = 0; i < facets2.Count; i++)
                            facets2[i] = (facets2[i].Item1, facets2[i].Item3, facets2[i].Item2);
                        n2 = -n2;
                    }
                }
            }

            var res2 = new Surfaces.FacetedSurface(fs2.Source, n2.LengthSquared > 1e-18 ? n2.Normalized() : Vector3d.UnitZ);
            foreach (var t in facets2) res2.Facets.Add(t);
            res2.FromLoops = true;
            f2.Surface = res2;
            // Сторону теперь несёт гранёная поверхность (её нормаль — по треугольникам,
            // уже развёрнутым по флагу файла). Флаг Reversed сверху разворачивал её ещё раз:
            // нормаль грани смотрела внутрь, и соглашение K05 по такой грани не сходилось
            // («ни одно кольцо не ограничивает область» у 1–5 граней реалистичных деталей).
            f2.Reversed = false;
            built++;
        }
        warnings?.Add($"{format}: {built} face(s) meshed from their loops, {failed} left as is.");
        return failed;
    }

    static readonly bool ClosedMeshOff = Environment.GetEnvironmentVariable("P3D_CLOSEDMESH") == "0";

    /// <summary>
    /// Сетка грани — целой замкнутой поверхности вращения (полный оборот, образующая — дуга от оси до оси: шар; или
    /// замкнутая окружность вне оси: тор). Сетка (параметр образующей × угол) с общими вершинами на шве и в полюсах.
    /// Сторона — по флагу грани и нормали поверхности. false — поверхность не такая.
    /// </summary>
    static bool ClosedSurfaceMesh(TFace f)
    {
        var src = f.Surface is Surfaces.FacetedSurface fx ? fx.Source : f.Surface;
        if (src is not Surfaces.RevolvedSurface rv || Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) > 1e-9 || rv.Base is not Curves.ArcCurve3 arc) return false;
        var poles = Topology.SurfaceDomain.Poles(rv);
        bool sphere = poles.Count == 2, torus = poles.Count == 0 && arc.IsClosed;
        if (!sphere && !torus) return false;
        double chord = Math.Max(1e-6, Tol.Chord), ang = Math.Max(1e-3, Tol.ChordAngle);
        int Steps(double r, double sweep) { double step = Math.Min(ang, r > chord ? 2 * Math.Acos(1 - chord / r) : ang); return Math.Max(8, (int)Math.Ceiling(Math.Abs(sweep) / step)); }
        var w = rv.Axis.W.Normalized();
        double rMax = 0;
        for (int i = 0; i <= 16; i++) { var d = arc.PointAt(i / 16.0) - rv.Axis.Origin; rMax = Math.Max(rMax, (d - w * Vector3d.Dot(d, w)).Length); }
        int nt = Steps(arc.Radius, arc.Sweep), nth = Steps(Math.Max(rMax, arc.Radius), 2 * Math.PI);
        var pts = new List<Vector3d>(); var idx = new int[nt + 1, nth];
        for (int i = 0; i <= nt; i++)
        {
            bool pole = sphere && (i == 0 || i == nt);
            int ii = torus && i == nt ? 0 : i;                      // тор: образующая замкнута
            for (int j = 0; j < nth; j++)
            {
                if (torus && i == nt) { idx[i, j] = idx[0, j]; continue; }
                if (pole && j > 0) { idx[i, j] = idx[i, 0]; continue; }
                idx[i, j] = pts.Count; pts.Add(rv.PointAt(new Vector2d((double)ii / nt, (double)j / nth)));
            }
        }
        var fs = new Surfaces.FacetedSurface(src, Vector3d.UnitZ);
        var tris = new List<(Vector3d, Vector3d, Vector3d)>();
        for (int i = 0; i < nt; i++)
            for (int j = 0; j < nth; j++)
            {
                int a = idx[i, j], b = idx[i, (j + 1) % nth], c = idx[i + 1, (j + 1) % nth], d = idx[i + 1, j];
                if (a != b && b != c && a != c) tris.Add((pts[a], pts[b], pts[c]));
                if (a != c && c != d && a != d) tris.Add((pts[a], pts[c], pts[d]));
            }
        if (tris.Count == 0) return false;
        // сторона: нормаль треугольника против нормали поверхности × флаг грани
        var (ta, tb, tc) = tris[tris.Count / 2];
        var nt0 = Vector3d.Cross(tb - ta, tc - ta); var g = (ta + tb + tc) / 3;
        var ns = src.NormalAt(src.ProjectToUV(g));
        bool flip = Vector3d.Dot(nt0, ns) * (f.Reversed ? -1 : 1) < 0;
        var n = Vector3d.Zero;
        foreach (var (x, y, z) in tris) { var t = flip ? (x, z, y) : (x, y, z); fs.Facets.Add(t); n += Vector3d.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1); }
        var res = new Surfaces.FacetedSurface(src, n.LengthSquared > 1e-18 ? n.Normalized() : Vector3d.UnitZ);
        foreach (var t in fs.Facets) res.Facets.Add(t);
        f.Surface = res; f.Reversed = false;
        return true;
    }

    internal static void OrientShellPublic(TSolid solid) => OrientShell(solid);
    static readonly bool OrientLocalOff = Environment.GetEnvironmentVariable("P3D_ORIENTLOCAL") == "0";

    /// <summary>Развернуть грань целиком: сторона, обход контуров, фасеты (область та же).</summary>
    static void FlipFace(TFace f)
    {
        f.Reversed = !f.Reversed;
        foreach (var l in f.Loops)
        {
            var items = l.CoEdges.Select(c => (c.Edge, c.Reversed)).ToList();
            foreach (var c in l.CoEdges.ToList()) { c.Edge.CoEdges.Remove(c); l.CoEdges.Remove(c); }
            for (int i = items.Count - 1; i >= 0; i--) l.Add(items[i].Edge, !items[i].Reversed);
        }
        if (f.Surface is Surfaces.FacetedSurface fs)
        {
            var list = fs.Facets.ToList(); var n = Vector3d.Zero;
            foreach (var t in list) n += Vector3d.Cross(t.Item3 - t.Item1, t.Item2 - t.Item1);
            var flipped = new Surfaces.FacetedSurface(fs.Source, n.LengthSquared > 1e-18 ? n.Normalized() : Vector3d.UnitZ);
            foreach (var t in list) flipped.Facets.Add((t.Item1, t.Item3, t.Item2));
            f.Surface = flipped;
        }
    }

    private static void OrientShell(TSolid solid)
    {
        var faces = solid.Faces.Where(f => f.Surface is Surfaces.FacetedSurface).ToList();
        if (faces.Count == 0) return;

        static (long, long, long) K(Vector3d p) =>
            ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));

        void Flip(TFace f)
        {
            // Вместе с треугольниками разворачивается и нормаль гранёной поверхности: она
            // задаёт сторону грани (NormalAt). Прежде нормаль оставалась прежней, и у
            // развёрнутых граней сторона смотрела внутрь — соглашение K05 по телу из IGES не
            // сходилось («ни одно кольцо не ограничивает область» у 5 граней каждой детали).
            var fs = (Surfaces.FacetedSurface)f.Surface;
            var list = fs.Facets.ToList();
            var n = Vector3d.Zero;
            foreach (var t in list) n += Vector3d.Cross(t.Item3 - t.Item1, t.Item2 - t.Item1);
            var flipped = new Surfaces.FacetedSurface(fs.Source, n.LengthSquared > 1e-18 ? n.Normalized() : Vector3d.UnitZ);
            foreach (var t in list) flipped.Facets.Add((t.Item1, t.Item3, t.Item2));
            f.Surface = flipped;
        }

        // ПОСТАВКА 3. КАРТА СТОРОН — ОДИН РАЗ. Прежде она строилась заново по всем треугольникам всех граней
        // для КАЖДОЙ грани из очереди: O(граней × треугольников) — у выреза в «лестнице» из 40 граней 22.6 мс
        // из 42 и 26 МБ. Теперь — по неориентированным сторонам, с исходным направлением каждого вхождения;
        // текущее направление — исходное XOR «грань развёрнута». Это ровно то, что дала бы пересчитанная
        // карта в момент обработки грани; порядок соседей тот же (по номеру, сначала «в ту же сторону», затем
        // «навстречу»), поэтому развороты — те же.
        var sides = new Dictionary<((long, long, long), (long, long, long)), List<(int Face, bool Forward)>>();
        for (int i = 0; i < faces.Count; i++)
            foreach (var t in ((Surfaces.FacetedSurface)faces[i].Surface).Facets)
                foreach (var (a, b) in new[] { (t.Item1, t.Item2), (t.Item2, t.Item3), (t.Item3, t.Item1) })
                {
                    var ka = K(a); var kb = K(b);
                    bool forward = ka.CompareTo(kb) <= 0;
                    var key = forward ? (ka, kb) : (kb, ka);
                    if (!sides.TryGetValue(key, out var l)) sides[key] = l = new List<(int, bool)>();
                    if (!l.Contains((i, forward))) l.Add((i, forward));
                }
        var flipped = new bool[faces.Count];

        var done = new bool[faces.Count];
        var queue = new Queue<int>();
        done[0] = true; queue.Enqueue(0);

        // g.05: РЕШЕНИЕ ПО БОЛЬШИНСТВУ ОБЩИХ СТОРОН. Прежде соседа разворачивала ПЕРВАЯ общая сторона, пройденная в ту же
        // сторону; у тонкой линзы (цилиндр задевает шар на 0.004 мм) вершины двух граней у края сливались по ключу 1e-4 мм — 246
        // мнимых сторон «в ту же сторону» против 374 настоящих «навстречу», и верная грань разворачивалась. Мутация P3D_ORIENTVOTE=0.
        if (!OrientVoteOff)
        {
            var same = new Dictionary<(int, int), int>(); var opp = new Dictionary<(int, int), int>();
            foreach (var l in sides.Values)
                for (int x = 0; x < l.Count; x++)
                    for (int y = 0; y < l.Count; y++)
                    {
                        if (l[x].Face == l[y].Face) continue;
                        var key = (l[x].Face, l[y].Face);
                        if (l[x].Forward == l[y].Forward) same[key] = same.GetValueOrDefault(key) + 1; else opp[key] = opp.GetValueOrDefault(key) + 1;
                    }
            var nbr = same.Keys.Concat(opp.Keys).Distinct().GroupBy(k => k.Item1).ToDictionary(g => g.Key, g => g.Select(k => k.Item2).ToList());
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                if (!nbr.TryGetValue(i, out var js)) continue;
                foreach (int j in js.OrderBy(x => x))
                {
                    if (done[j]) continue;
                    bool sameDir = same.GetValueOrDefault((i, j)) > opp.GetValueOrDefault((i, j));   // по исходным направлениям
                    bool flipJ = sameDir ^ flipped[i];
                    if (flipJ) { Flip(faces[j]); flipped[j] = true; }
                    done[j] = true; queue.Enqueue(j);
                }
            }
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            foreach (var t in ((Surfaces.FacetedSurface)faces[i].Surface).Facets)
                foreach (var (a, b) in new[] { (t.Item1, t.Item2), (t.Item2, t.Item3), (t.Item3, t.Item1) })
                {
                    var ka = K(a); var kb = K(b);
                    bool forward = ka.CompareTo(kb) <= 0;               // направление этой стороны у грани i сейчас
                    if (!sides.TryGetValue(forward ? (ka, kb) : (kb, ka), out var list)) continue;
                    // Сосед, проходящий сторону В ТУ ЖЕ сторону, развёрнут относительно нас.
                    foreach (var (j, fwd0) in list)
                        if (j != i && !done[j] && (fwd0 ^ flipped[j]) == forward) { Flip(faces[j]); flipped[j] = true; done[j] = true; queue.Enqueue(j); }
                    foreach (var (j, fwd0) in list)
                        if (j != i && !done[j] && (fwd0 ^ flipped[j]) != forward) { done[j] = true; queue.Enqueue(j); }
                }
        }

        // Знак объёма: тело должно быть положительным, иначе разворачиваем всё.
        // KRN-02 (02.9): в ЛОКАЛЬНОЙ системе (от первой вершины фасет). В мировых координатах у детали 3 мм на 3e5 мм знак
        // выходил неверным, все грани разворачивались внутрь, и общий маршрут отвергался сверкой «объём против фасет»
        // (−5.27 при точном 5.27). Мутация P3D_ORIENTLOCAL=0.
        double vol = 0;
        var o = OrientLocalOff ? Vector3d.Zero : ((Surfaces.FacetedSurface)faces[0].Surface).Facets.ReadView.FirstOrDefault().Item1;
        foreach (var f in faces)
            foreach (var t in ((Surfaces.FacetedSurface)f.Surface).Facets)
                vol += Vector3d.Dot(t.Item1 - o, Vector3d.Cross(t.Item2 - o, t.Item3 - o)) / 6;
        if (vol < 0) foreach (var f in faces) Flip(f);
    }

    /// <summary>Пересобрать гранёную поверхность с нужной средней нормалью.</summary>
    private static Surfaces.FacetedSurface Rebuild(Surfaces.FacetedSurface fs, Vector3d n)
    {
        var res = new Surfaces.FacetedSurface(fs.Source, n);
        foreach (var t in fs.Facets) res.Facets.Add(t);
        return res;
    }

    private readonly struct ISurfaceRef
    {
        public readonly Frame3 Frame;
        public ISurfaceRef(Frame3 f) { Frame = f; }
    }
}
