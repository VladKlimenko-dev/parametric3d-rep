using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// Протягивание плоского сечения вдоль траектории с переносом системы координат
/// (parallel transport frames — без «скручивания» сечения).
///
/// Реализация построена на дискретной траектории: каждая грань получает
/// PolylineCurve3-образующие. Этого достаточно для отрисовки и масс-свойств;
/// точная аналитическая поверхность протягивания — следующий шаг.
/// </summary>
public static class SweepOp
{
    public static TSolid Build(Profile section, ICurve3 path, double lengthFraction = 1.0) => Build(section, path, lengthFraction, 0);

    /// <summary>Протяжка с закруткой сечения вокруг касательной на угол twistDeg (градусы, линейно по траектории).</summary>
    public static TSolid Build(Profile section, ICurve3 path, double lengthFraction, double twistDeg) => Build(section, path, lengthFraction, twistDeg, false);

    /// <summary>keepOrientation — сечение переносится без поворота (закон ориентации «не поворачивать»), только точно.</summary>
    public static TSolid Build(Profile section, ICurve3 path, double lengthFraction, double twistDeg, bool keepOrientation)
    {
        if (section.IsEmpty) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: the section is empty or not closed.");
        if (section.HasSelfIntersections) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: the section contour crosses itself.");
        lengthFraction = Math.Clamp(lengthFraction, 1e-4, 1.0);
        CheckClearance(section, new[] { path }, path.IsClosed);
        if (keepOrientation)
        {
            string? whyK = null;
            var kp = DirectRound.ExactSweep(section, path, lengthFraction, twistDeg * Math.PI / 180, true, ref whyK);
            return kp ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Sweep: the kept orientation needs the exact sweep (" + whyK + ").");
        }
        // KRN-07 (07.3): ЗАКРУТКА — только точной протяжкой (рамка без вращения, поворот вокруг касательной); иначе отказ
        if (Math.Abs(twistDeg) > 1e-12)
        {
            string? whyT = null;
            var tw = DirectRound.ExactSweep(section, path, lengthFraction, twistDeg * Math.PI / 180, ref whyT);
            return tw ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Sweep: twist needs the exact sweep (" + (whyT ?? "closed path") + ").");
        }

        // 1. Дискретизация траектории и построение переносимых фреймов.
        var raw = new List<Vector3d>();
        path.Tessellate(raw, true);
        int cut = Math.Max(1, (int)Math.Round((raw.Count - 1) * lengthFraction));
        var pathPts = raw.Take(cut + 1).ToList();
        if (pathPts.Count < 2) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: the path is too short.");

        var frames = BuildTransportFrames(section.Plane, pathPts);

        // K09. ПРЯМАЯ ТРАЕКТОРИЯ — ЭТО ВЫДАВЛИВАНИЕ. Протяжка по отрезку строилась ломаными
        // кромками и гранью SweptSurface: у круга R5 кромки выходили 48-угольниками (объём на
        // 0.29% меньше), а сетка стенки не сходилась с крышками у шва (4 стороны без пары).
        // Сечение переносится в плоскость, перпендикулярную отрезку, точными кривыми и
        // выдавливается — стенка становится цилиндром, кромки — окружностями.
        // K09. ТРАЕКТОРИЯ-ДУГА — ЭТО ВРАЩЕНИЕ сечения вокруг оси дуги: плоскость сечения после
        // переноса содержит и радиус, и ось. Ломаной протяжкой объём выходил на 0.4% меньше
        // теоремы Паппа (393.18 вместо 394.78 у круга r2 по четверти окружности R20).
        if (path is ArcCurve3 arcPath && Math.Abs(arcPath.Sweep) > 1e-9)
        {
            // Плоскость сечения — по ТОЧНОЙ касательной в начале дуги, а не по первой хорде
            // ломаной: наклон на полшага угла уводил кромки с поверхности на 0.16 мм.
            var start0 = AlignNormal(section.Plane, arcPath.PointAt(0), arcPath.TangentAt(0));
            var moved = new Profile { Plane = start0 };
            foreach (var loop in section.Loops)
            {
                var nl = new ProfileLoop { IsOuter = loop.IsOuter, Area = loop.Area };
                foreach (var c in loop.Curves) nl.Curves.Add(MapCurveExact(c, section.Plane, start0));
                moved.Loops.Add(nl);
            }
            double ang = arcPath.Sweep * lengthFraction;
            var w = arcPath.Frame.W.Normalized() * Math.Sign(ang);
            var axisFrame = Frame3.FromNormal(arcPath.Frame.Origin, w);
            // Начало вращения — у начала траектории: угол отсчитывается от точки дуги в t = 0.
            var start = arcPath.PointAt(0) - arcPath.Frame.Origin;
            var u = (start - w * Vector3d.Dot(start, w)).Normalized();
            axisFrame = new Frame3(arcPath.Frame.Origin, u, Vector3d.Cross(w, u));
            return RevolveOp.Build(moved, axisFrame, 0, Math.Abs(ang));
        }
        if (path is LineCurve3 straight)
        {
            var dir = (straight.B - straight.A) * lengthFraction;
            var moved = new Profile { Plane = frames[0] };
            foreach (var loop in section.Loops)
            {
                var nl = new ProfileLoop { IsOuter = loop.IsOuter, Area = loop.Area };
                foreach (var c in loop.Curves) nl.Curves.Add(MapCurveExact(c, section.Plane, frames[0]));
                moved.Loops.Add(nl);
            }
            return ExtrudeOp.Build(moved, dir);
        }

        // KRN-07 (07.3): ОБЩАЯ ТРАЕКТОРИЯ — МНОГОГРАННОЕ ТЕЛО С ЧЕСТНОЙ ПОМЕТКОЙ. Прежде боковые грани получали
        // SweptSurface с ломаными-образующими, а p-кривые по ним не сходились с рёбрами (до 12.8 мм): протяжка по
        // сплайну давала негодное B-rep (6 ошибок проверки) при «успешной» операции. Теперь тело собирается из
        // многоугольников (боковые — без точной поверхности, торцы — плоскости), как многогранный путь булевой; его
        // гранение видно в аудите и отвергается политикой PreserveBRep. Мутация P3D_SWEEPPOLY=0 — прежний путь.
        // KRN-07 (07.3): ГЛАДКАЯ ОТКРЫТАЯ ТРАЕКТОРИЯ — ТОЧНО: грани по формуле сечения в рамке без вращения (DirectRound.ExactSweep;
        // мутация P3D_SWEEPEXACT=0 — многогранный путь, как прежде)
        if (!PolyOff)
        {
            string? whyX = null;
            var ex = DirectRound.ExactSweep(section, path, lengthFraction, 0, ref whyX);
            if (ex != null) return ex;
            DirectRound.Debug?.Invoke("sweep: exact declined: " + whyX);
        }
        if (!PolyOff) return Polyhedral(section, frames, path.IsClosed || (pathPts[0] - pathPts[^1]).Length <= Tol.Weld && lengthFraction >= 1);

        // 2. Переносим точки сечения по всем станциям траектории.
        var tb = new TopoBuilder();
        var solid = new TSolid();
        var shell = solid.AddShell();
        int nStations = frames.Count;

        var capStart = new TFace { Surface = new PlaneSurface(frames[0]), Reversed = true, Tag = "cap:start" };
        var capEnd = new TFace { Surface = new PlaneSurface(frames[^1]), Reversed = false, Tag = "cap:end" };

        int loopIndex = -1;
        foreach (var loop in section.Loops)
        {
            loopIndex++;
            int n = loop.Curves.Count;
            var loc = new Vector2d[n];
            for (int i = 0; i < n; i++) loc[i] = section.Plane.ToLocal2(loop.Curves[i].PointAt(0));

            var vStart = new TVertex[n];
            var vEnd = new TVertex[n];
            for (int i = 0; i < n; i++)
            {
                vStart[i] = tb.Vertex(frames[0].ToWorld(loc[i]));
                vEnd[i] = tb.Vertex(frames[^1].ToWorld(loc[i]));
            }

            var es = new TEdge[n]; var esRev = new bool[n];
            var ee = new TEdge[n]; var eeRev = new bool[n];
            var er = new TEdge[n]; var erRev = new bool[n];

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                es[i] = tb.Edge(vStart[i], vStart[j], MapCurve(loop.Curves[i], section.Plane, frames[0]), out esRev[i]);
                ee[i] = tb.Edge(vEnd[i], vEnd[j], MapCurve(loop.Curves[i], section.Plane, frames[^1]), out eeRev[i]);

                var rail = new List<Vector3d>(nStations);
                for (int s = 0; s < nStations; s++) rail.Add(frames[s].ToWorld(loc[i]));
                er[i] = tb.Edge(vStart[i], vEnd[i], new PolylineCurve3(rail), out erRev[i]);
            }

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var f = new TFace
                {
                    Surface = new SweptSurface(loop.Curves[i], section.Plane, frames),
                    // Контуры нормализованы (см. ProfileBuilder): отдельного
                    // разворота для отверстий не требуется.
                    Reversed = false,
                    // ПОДПИСЬ ПО МЕСТУ В ЭСКИЗЕ, А НЕ ПО ХЕШ-КОДУ (см. выдавливание).
                    Tag = $"sweep:side[{loopIndex}.{i}]"
                };
                var fl = f.AddLoop(true);
                fl.Add(es[i], esRev[i]);
                fl.Add(er[j], erRev[j]);
                fl.Add(ee[i], !eeRev[i]);
                fl.Add(er[i], !erRev[i]);
                shell.Add(f);
            }

            var ls = capStart.AddLoop(loop.IsOuter);
            var le = capEnd.AddLoop(loop.IsOuter);
            for (int i = 0; i < n; i++) { ls.Add(es[i], esRev[i]); le.Add(ee[i], eeRev[i]); }
        }

        shell.Add(capStart);
        shell.Add(capEnd);
        return solid;
    }

    static readonly bool PolyOff = Environment.GetEnvironmentVariable("P3D_SWEEPPOLY") == "0";
    static readonly bool ClearOff = Environment.GetEnvironmentVariable("P3D_SWEEPCLEAR") == "0";

    /// <summary>Наибольшее удаление точки сечения от начала его плоскости (точки, идущей по траектории).</summary>
    public static double Extent(Profile section)
    {
        double r = 0;
        foreach (var l in section.Loops) foreach (var c in l.Curves)
        {
            var t = new List<Vector3d>(); c.Tessellate(t, true);
            foreach (var p in t) r = Math.Max(r, (p - section.Plane.Origin).Length);
        }
        return r;
    }

    /// <summary>
    /// KRN-07 (07.3): ЗАЗОР ТРУБЫ. Сечение с размахом ρ (от точки траектории) проходит без самопересечения, если
    /// (1) радиус кривизны траектории везде больше ρ (дуга — её радиус, кривая — описанная окружность соседних точек на
    /// гладких участках); (2) точки траектории, разнесённые по длине больше чем на πρ, не ближе 2ρ (петля, возврат) — кроме
    /// соседних кусков ломаной, где стык «на ус» разделяет материал; (3) стык «на ус» с поворотом φ не выходит за куски:
    /// ρ·tg(φ/2) меньше длины каждого. Прежде: протяжка r5 по дуге R3 отказывала лишь проверкой булевой («кривая отходит
    /// на 6.45 мм»), петля сплайна давала негодное тело. Мутация P3D_SWEEPCLEAR=0.
    /// </summary>
    public static void CheckClearance(Profile section, IReadOnlyList<ICurve3> pieces, bool closed)
    {
        if (ClearOff || pieces.Count == 0) return;
        double rho = Extent(section);
        if (rho <= 0) return;
        void Refuse(string why) => throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Sweep: the swept body would cross itself — {why} (section extent {rho:0.###}).");
        // (1) кривизна
        for (int k = 0; k < pieces.Count; k++)
        {
            var c = pieces[k];
            if (c is LineCurve3) continue;
            if (c is ArcCurve3 a) { if (a.Radius <= rho) Refuse($"the path arc radius {a.Radius:0.###} is not larger than the section"); continue; }
            var t = new List<Vector3d>(); c.Tessellate(t, true);
            for (int i = 1; i + 1 < t.Count; i++)
            {
                Vector3d u = t[i] - t[i - 1], v = t[i + 1] - t[i];
                if (u.Length < 1e-12 || v.Length < 1e-12) continue;
                double cos = Vector3d.Dot(u, v) / (u.Length * v.Length);
                if (cos < Math.Cos(Math.PI / 6)) continue;                 // излом — не кривизна
                double cross = Vector3d.Cross(u, v).Length;
                if (cross < 1e-15) continue;
                double R = u.Length * v.Length * (t[i + 1] - t[i - 1]).Length / (2 * cross);
                if (R <= rho) Refuse($"the path curvature radius {R:0.###} near ({t[i].X:0.##}, {t[i].Y:0.##}, {t[i].Z:0.##}) is not larger than the section");
            }
        }
        // (3) стыки «на ус»
        for (int k = 0; k + 1 < pieces.Count; k++)
        {
            var t1 = pieces[k].TangentAt(pieces[k].T1).Normalized(); var t2 = pieces[k + 1].TangentAt(pieces[k + 1].T0).Normalized();
            double phi = Math.Acos(Math.Clamp(Vector3d.Dot(t1, t2), -1, 1));
            if (phi < 1e-9) continue;
            double zone = phi >= Math.PI - 1e-9 ? double.PositiveInfinity : rho * Math.Tan(phi / 2);
            double l1 = Len(pieces[k]), l2 = Len(pieces[k + 1]);
            if (zone >= Math.Min(l1, l2)) Refuse($"the corner {k + 1} turns by {phi * 180 / Math.PI:0.#}° and its mitre ({zone:0.###}) runs past the adjacent segment");
        }
        // (2) сближение
        var pts = new List<(Vector3d P, int Piece, double S)>();
        double s0 = 0;
        for (int k = 0; k < pieces.Count; k++)
        {
            var t = new List<Vector3d>(); pieces[k].Tessellate(t, true);
            var dense = new List<Vector3d> { t[0] };
            for (int i = 1; i < t.Count; i++)
            {
                int m = Math.Max(1, (int)Math.Ceiling((t[i] - t[i - 1]).Length / (rho / 4)));
                m = Math.Min(m, 400);
                for (int j = 1; j <= m; j++) dense.Add(t[i - 1] + (t[i] - t[i - 1]) * (j / (double)m));
            }
            for (int i = 0; i < dense.Count; i++)
            {
                if (i > 0) s0 += (dense[i] - dense[i - 1]).Length;
                if (k > 0 && i == 0) continue;
                pts.Add((dense[i], k, s0));
            }
        }
        if (pts.Count > 6000) { var step = (int)Math.Ceiling(pts.Count / 6000.0); pts = pts.Where((x, i) => i % step == 0).ToList(); }
        double total = s0;
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                if (Math.Abs(pts[i].Piece - pts[j].Piece) == 1 || (closed && pieces.Count > 1 && Math.Abs(pts[i].Piece - pts[j].Piece) == pieces.Count - 1)) continue;
                double sep = pts[j].S - pts[i].S;
                if (closed) sep = Math.Min(sep, total - sep);
                if (sep <= Math.PI * rho) continue;
                double d = (pts[i].P - pts[j].P).Length;
                if (d < 2 * rho * (1 - 1e-9))
                    Refuse($"the path comes back within {d:0.###} of itself (points {pts[i].S:0.##} and {pts[j].S:0.##} along it)");
            }
    }

    private static double Len(ICurve3 c) { var t = new List<Vector3d>(); c.Tessellate(t, true); double l = 0; for (int i = 1; i < t.Count; i++) l += (t[i] - t[i - 1]).Length; return l; }

    /// <summary>Многогранное тело протяжки по рамкам станций (см. Build).</summary>
    private static TSolid Polyhedral(Profile section, List<Frame3> frames, bool closed)
    {
        if (closed && frames.Count > 2)
        {
            // замкнутая траектория: последняя станция совпадает с первой; рамки должны совпасть (без закрутки)
            var f0 = frames[0]; var fn = frames[^1];
            if ((f0.U - fn.U).Length > 1e-6 || (f0.V - fn.V).Length > 1e-6)
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Sweep: a closed path whose transported frame does not return to itself (twist) is not supported.");
            frames = frames.Take(frames.Count - 1).ToList();
        }
        var polys = new List<CsgPolygon>();
        var sources = new Dictionary<uint, ISurface3?>();
        var names = new Dictionary<uint, string>();
        uint tag = 1;
        var loops2 = new List<(List<Vector2d> Pts, bool Outer)>();
        int li = -1;
        foreach (var loop in section.Loops)
        {
            li++;
            var loc = new List<Vector2d>(); var curveOf = new List<uint>();
            for (int ci = 0; ci < loop.Curves.Count; ci++)
            {
                var t = new List<Vector3d>(); loop.Curves[ci].Tessellate(t, true);
                for (int i = 0; i + 1 < t.Count; i++) { loc.Add(section.Plane.ToLocal2(t[i])); curveOf.Add(tag + (uint)ci); }
            }
            for (int ci = 0; ci < loop.Curves.Count; ci++) { sources[tag + (uint)ci] = null; names[tag + (uint)ci] = $"sweep:side[{li}.{ci}]"; }
            tag += (uint)loop.Curves.Count;
            // обход: внешний — против часовой (нормаль сечения — по ходу), отверстие — по часовой
            double area = GeomUtil.SignedArea(loc);
            if ((loop.IsOuter && area < 0) || (!loop.IsOuter && area > 0))
            {
                loc.Reverse(); curveOf.Reverse();
                // метка отрезка i→i+1 после разворота — у отрезка, который был i+1→i
                var c2 = new List<uint>(curveOf.Count);
                for (int i = 0; i < curveOf.Count; i++) c2.Add(curveOf[(i + 1) % curveOf.Count]);
                curveOf = c2;
            }
            loops2.Add((loc, loop.IsOuter));
            int n = loc.Count, ns = frames.Count;
            int last = closed ? ns : ns - 1;
            for (int s = 0; s < last; s++)
            {
                var fa = frames[s]; var fb = frames[(s + 1) % ns];
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    Vector3d a0 = fa.ToWorld(loc[i]), a1 = fa.ToWorld(loc[j]), b0 = fb.ToWorld(loc[i]), b1 = fb.ToWorld(loc[j]);
                    AddTri(polys, curveOf[i], a0, a1, b1);
                    AddTri(polys, curveOf[i], a0, b1, b0);
                }
            }
        }
        if (!closed)
        {
            // торцы: внешние контуры с их отверстиями
            var outers = loops2.Where(x => x.Outer).Select(x => x.Pts).ToList();
            var inners = loops2.Where(x => !x.Outer).Select(x => x.Pts).ToList();
            foreach (var (cap, f, outward) in new[] { ("cap:start", frames[0], false), ("cap:end", frames[^1], true) })
            {
                sources[tag] = new PlaneSurface(f); names[tag] = cap;
                foreach (var o in outers)
                {
                    var hs = inners.Where(h => GeomUtil.PointInPolygon(o, h[0])).Select(h => (IReadOnlyList<Vector2d>)h).ToList();
                    var idx = Triangulator.Triangulate(o, hs, out var tp);
                    for (int i = 0; i + 2 < idx.Count; i += 3)
                    {
                        Vector3d p0 = f.ToWorld(tp[idx[i]]), p1 = f.ToWorld(tp[idx[i + 1]]), p2 = f.ToWorld(tp[idx[i + 2]]);
                        if (outward) AddTri(polys, tag, p0, p1, p2); else AddTri(polys, tag, p0, p2, p1);
                    }
                }
                tag++;
            }
        }
        var solid = BooleanOp.SolidFromPolygons(polys, sources, names);
        var v = SolidValidator.Validate(solid);
        // сбой разметки грани (внутренний, не свойство входа) — не отказ по входу
        if (v.Issues.Any(i => i.Code == "face-missing"))
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InternalFailure, "Sweep: the body could not be meshed for validation — " + v.Summary());
        if (!v.IsValid)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: the swept body is not a valid solid — " + v.Summary());
        return solid;
    }

    private static void AddTri(List<CsgPolygon> polys, uint tag, Vector3d a, Vector3d b, Vector3d c)
    {
        if (Vector3d.Cross(b - a, c - a).LengthSquared < 1e-24) return;
        polys.Add(new CsgPolygon(new List<Vector3d> { a, b, c }, tag));
    }

    /// <summary>Parallel transport: минимальное вращение фрейма вдоль ломаной.</summary>
    private static List<Frame3> BuildTransportFrames(Frame3 sectionPlane, List<Vector3d> pts)
    {
        var frames = new List<Frame3>(pts.Count);
        Vector3d t0 = (pts[1] - pts[0]).Normalized();

        // Начальный фрейм: сечение поворачиваем так, чтобы его нормаль совпала с касательной.
        Frame3 f = AlignNormal(sectionPlane, pts[0], t0);
        frames.Add(f);

        for (int i = 1; i < pts.Count; i++)
        {
            Vector3d tPrev = (pts[i] - pts[i - 1]).Normalized();
            Vector3d tNext = i + 1 < pts.Count ? (pts[i + 1] - pts[i]).Normalized() : tPrev;
            Vector3d t = (tPrev + tNext).Normalized();

            Vector3d axis = Vector3d.Cross(f.W, t);
            double s = axis.Length;
            Vector3d u = f.U, v = f.V;
            if (s > 1e-12)
            {
                axis /= s;
                double ang = Math.Atan2(s, Vector3d.Dot(f.W, t));
                u = RevolvedSurface.RotateAbout(f.U, Vector3d.Zero, axis, ang);
                v = RevolvedSurface.RotateAbout(f.V, Vector3d.Zero, axis, ang);
            }
            f = new Frame3(pts[i], u, v);
            frames.Add(f);
        }
        return frames;
    }

    private static Frame3 AlignNormal(Frame3 src, Vector3d origin, Vector3d newNormal)
    {
        Vector3d axis = Vector3d.Cross(src.W, newNormal);
        double s = axis.Length;
        // K09: нормаль ПРОТИВ касательной (поворот на 180°) — прежде рамка оставалась как есть, и
        // сечение уходило вдоль траектории изнанкой; теперь разворот вокруг U.
        if (s < 1e-12)
            return Vector3d.Dot(src.W, newNormal) >= 0 ? new Frame3(origin, src.U, src.V) : new Frame3(origin, src.U, -src.V);
        axis /= s;
        double ang = Math.Atan2(s, Vector3d.Dot(src.W, newNormal));
        Vector3d u = RevolvedSurface.RotateAbout(src.U, Vector3d.Zero, axis, ang);
        Vector3d v = RevolvedSurface.RotateAbout(src.V, Vector3d.Zero, axis, ang);
        return new Frame3(origin, u, v);
    }

    /// <summary>Перенос кривой из одной плоскости в другую с сохранением вида (отрезок, дуга).</summary>
    /// <summary>
    /// K09. ПРОТЯЖКА ПО ЦЕПОЧКЕ ОТРЕЗКОВ И ДУГ С КАСАТЕЛЬНЫМ СОПРЯЖЕНИЕМ. Каждый кусок строится
    /// точно (отрезок — выдавливание, дуга — вращение), сечение переносится в начало следующего
    /// куска тем же движением (сдвигом или поворотом вокруг оси дуги), куски объединяются.
    /// Прежде цепочка сшивалась в одну ломаную в порядке элементов эскиза, без проверки
    /// направления, и протягивалась ломаной. null — если цепочка не отрезки и дуги или в ней
    /// излом (тогда вызывающий идёт прежним путём).
    /// </summary>
    public static TSolid? BuildChain(Profile section, IReadOnlyList<ICurve3> segs, List<string>? warnings = null)
    {
        if (segs.Count == 0 || segs.Any(c => c is not (LineCurve3 or ArcCurve3))) return null;
        CheckClearance(section, segs, (segs[0].PointAt(segs[0].T0) - segs[^1].PointAt(segs[^1].T1)).Length <= Tol.Weld);
        bool corners = false;
        for (int i = 0; i + 1 < segs.Count; i++)
        {
            if ((segs[i].PointAt(1) - segs[i + 1].PointAt(0)).Length > Tol.Weld) return null;
            if (Vector3d.Dot(segs[i].TangentAt(1).Normalized(), segs[i + 1].TangentAt(0).Normalized()) < 1 - 1e-9) corners = true;
        }
        if (corners)
            return segs.All(c => c is LineCurve3) ? MiteredPolyline(section, segs.Cast<LineCurve3>().ToList(), warnings) : null;
        var frame = AlignNormal(section.Plane, segs[0].PointAt(0), segs[0].TangentAt(0));
        var prof = Move(section, section.Plane, frame);
        var pieces = new List<TSolid>();
        foreach (var seg in segs)
        {
            switch (seg)
            {
                case LineCurve3 l:
                {
                    var d = l.B - l.A;
                    pieces.Add(ExtrudeOp.Build(prof, d));
                    var next = new Frame3(frame.Origin + d, frame.U, frame.V);
                    prof = Move(prof, frame, next); frame = next;
                    break;
                }
                case ArcCurve3 a:
                {
                    var w = a.Frame.W.Normalized() * Math.Sign(a.Sweep);
                    double ang = Math.Abs(a.Sweep);
                    var start = a.PointAt(0) - a.Frame.Origin;
                    var u = (start - w * Vector3d.Dot(start, w)).Normalized();
                    pieces.Add(RevolveOp.Build(prof, new Frame3(a.Frame.Origin, u, Vector3d.Cross(w, u)), 0, ang));
                    Vector3d Rot(Vector3d v) => v * Math.Cos(ang) + Vector3d.Cross(w, v) * Math.Sin(ang) + w * Vector3d.Dot(w, v) * (1 - Math.Cos(ang));
                    var next = new Frame3(a.Frame.Origin + Rot(frame.Origin - a.Frame.Origin), Rot(frame.U), Rot(frame.V));
                    prof = Move(prof, frame, next); frame = next;
                    break;
                }
            }
        }
        var bodies = new List<TSolid> { pieces[0] };
        for (int i = 1; i < pieces.Count; i++) BooleanOp.Unite(bodies, pieces[i], warnings ?? new List<string>());
        return bodies.Count == 1 ? bodies[0] : null;
    }

    /// <summary>
    /// K09. ПРОТЯЖКА ПО ЛОМАНОЙ С ИЗЛОМАМИ — СТЫКИ «НА УС». Каждый отрезок выдавливается с запасом
    /// и обрезается плоскостями-биссектрисами стыков; сечение переносится через излом поворотом
    /// вокруг оси (касательная₁ × касательная₂), без закрутки; куски объединяются. Объём круглой
    /// трубы со стыками на ус точно равен площади сечения на длину осевой ломаной. Прежде излом
    /// уходил в протяжку по ломаной с переносом рамок и давал грубое приближение.
    /// </summary>
    private static TSolid? MiteredPolyline(Profile section, List<LineCurve3> segs, List<string>? warnings)
    {
        var w = warnings ?? new List<string>();
        // Размер сечения — для запаса выдавливания и размера срезающих плит.
        double size = 0;
        foreach (var l in section.Loops) foreach (var c in l.Curves)
            foreach (var t in new[] { c.T0, (c.T0 + c.T1) / 2, c.T1 }) size = Math.Max(size, (c.PointAt(t) - section.Plane.Origin).Length);
        if (size <= 0) return null;
        double margin = size * 4;
        var frame = AlignNormal(section.Plane, segs[0].A, segs[0].B - segs[0].A);
        var prof = Move(section, section.Plane, frame);
        var pieces = new List<TSolid>();
        for (int i = 0; i < segs.Count; i++)
        {
            var s0 = segs[i]; var t = (s0.B - s0.A).Normalized();
            // Выдавливание с запасом с обеих сторон.
            var startFrame = new Frame3(s0.A - t * margin, frame.U, frame.V);
            var startProf = Move(prof, frame, startFrame);
            var piece = new List<TSolid> { ExtrudeOp.Build(startProf, t * ((s0.B - s0.A).Length + 2 * margin)) };
            // Срез биссектрисами: в начале (кроме первого) и в конце (кроме последнего).
            if (i > 0)
            {
                var tp = (segs[i - 1].B - segs[i - 1].A).Normalized();
                var nb = (tp + t).Normalized();            // нормаль плоскости стыка
                if (!Clip(piece, s0.A, nb, margin * 4, w)) return null;            // оставить сторону по ходу
            }
            else if (!Clip(piece, s0.A, t, margin * 4, w)) return null;
            if (i + 1 < segs.Count)
            {
                var tn = (segs[i + 1].B - segs[i + 1].A).Normalized();
                var nb = (t + tn).Normalized();
                if (!Clip(piece, s0.B, -nb, margin * 4, w)) return null;
            }
            else if (!Clip(piece, s0.B, -t, margin * 4, w)) return null;
            if (piece.Count != 1) return null;
            pieces.Add(piece[0]);
            // Перенос сечения через излом: поворот вокруг t × tn на угол между ними.
            if (i + 1 < segs.Count)
            {
                var tn = (segs[i + 1].B - segs[i + 1].A).Normalized();
                var axis = Vector3d.Cross(t, tn);
                double ang = Math.Atan2(axis.Length, Vector3d.Dot(t, tn));
                Vector3d Rot(Vector3d v) { if (axis.LengthSquared < 1e-24) return v; var k = axis.Normalized(); return v * Math.Cos(ang) + Vector3d.Cross(k, v) * Math.Sin(ang) + k * Vector3d.Dot(k, v) * (1 - Math.Cos(ang)); }
                var next = new Frame3(segs[i + 1].A, Rot(frame.U), Rot(frame.V));
                var moved = Move(prof, new Frame3(s0.A, frame.U, frame.V), next);
                prof = moved; frame = next;
            }
            else
            {
                // кадр не нужен дальше
            }
            // Сдвиг кадра к началу следующего куска для прямого отрезка учтён в next.
        }
        var bodies = new List<TSolid> { pieces[0] };
        for (int i = 1; i < pieces.Count; i++) BooleanOp.Unite(bodies, pieces[i], w);
        return bodies.Count == 1 ? bodies[0] : null;
    }

    /// <summary>Оставить у тел часть по сторону normal от плоскости через at (пересечение с плитой).</summary>
    private static bool Clip(List<TSolid> bodies, Vector3d at, Vector3d normal, double size, List<string> w)
    {
        var n = normal.Normalized();
        var f = Frame3.FromNormal(at, n);
        var sq = new List<ICurve3>
        {
            new LineCurve3(f.ToWorld(new Vector2d(-size, -size)), f.ToWorld(new Vector2d(size, -size))),
            new LineCurve3(f.ToWorld(new Vector2d(size, -size)), f.ToWorld(new Vector2d(size, size))),
            new LineCurve3(f.ToWorld(new Vector2d(size, size)), f.ToWorld(new Vector2d(-size, size))),
            new LineCurve3(f.ToWorld(new Vector2d(-size, size)), f.ToWorld(new Vector2d(-size, -size)))
        };
        var slab = ExtrudeOp.Build(ProfileBuilder.Build(f, sq), n * size);
        try { BooleanOp.Intersect(bodies, slab, w); } catch (OperationFailedException) { return false; }
        return bodies.Count > 0;
    }

    private static Profile Move(Profile p, Frame3 from, Frame3 to)
    {
        var moved = new Profile { Plane = to };
        foreach (var loop in p.Loops)
        {
            var nl = new ProfileLoop { IsOuter = loop.IsOuter, Area = loop.Area };
            foreach (var c in loop.Curves) nl.Curves.Add(MapCurveExact(c, from, to));
            moved.Loops.Add(nl);
        }
        return moved;
    }

    private static ICurve3 MapCurveExact(ICurve3 c, Frame3 from, Frame3 to)
    {
        Vector3d P(Vector3d q) => to.ToWorld(from.ToLocal2(q));
        Vector3d Dir(Vector3d d) { var l = from.ToLocal2(from.Origin + d); return to.U * l.X + to.V * l.Y; }
        switch (c)
        {
            case LineCurve3 l: return new LineCurve3(P(l.A), P(l.B));
            case ArcCurve3 a when Math.Abs(Math.Abs(Vector3d.Dot(a.Frame.W.Normalized(), from.W.Normalized())) - 1) < 1e-12:
            {
                // Рамка дуги переносится целиком; если её ось против нормали плоскости,
                // направление обхода сохраняется заменой V на −V через ориентацию to.
                var u = Dir(a.Frame.U); var v = Dir(a.Frame.V);
                return new ArcCurve3(new Frame3(P(a.Frame.Origin), u, v), a.Radius, a.A0, a.A1);
            }
            default: return MapCurve(c, from, to);
        }
    }

    private static ICurve3 MapCurve(ICurve3 c, Frame3 from, Frame3 to)
    {
        var pts = new List<Vector3d>();
        c.Tessellate(pts, true);
        return new PolylineCurve3(pts.Select(p => to.ToWorld(from.ToLocal2(p))));
    }
}

/// <summary>Поверхность протягивания: сечение по станциям траектории.</summary>
public sealed class SweptSurface : ISurface3
{
    private readonly ICurve3 _sectionCurve;
    private readonly Frame3 _sectionPlane;
    private readonly List<Frame3> _frames;

    public SweptSurface(ICurve3 sectionCurve, Frame3 sectionPlane, List<Frame3> frames)
    { _sectionCurve = sectionCurve; _sectionPlane = sectionPlane; _frames = frames; }

    public bool IsPlanar => false;

    private Frame3 FrameAt(double v)
    {
        int i = (int)Math.Round(Math.Clamp(v, 0, 1) * (_frames.Count - 1));
        return _frames[Math.Clamp(i, 0, _frames.Count - 1)];
    }

    public Vector3d PointAt(Vector2d uv)
        => FrameAt(uv.Y).ToWorld(_sectionPlane.ToLocal2(_sectionCurve.PointAt(uv.X)));

    public Vector3d NormalAt(Vector2d uv)
    {
        const double h = 1e-3;
        Vector3d du = PointAt(new Vector2d(Math.Min(1, uv.X + h), uv.Y)) - PointAt(new Vector2d(Math.Max(0, uv.X - h), uv.Y));
        Vector3d dv = PointAt(new Vector2d(uv.X, Math.Min(1, uv.Y + h))) - PointAt(new Vector2d(uv.X, Math.Max(0, uv.Y - h)));
        Vector3d n = Vector3d.Cross(du, dv);
        return n.LengthSquared < 1e-24 ? Vector3d.UnitZ : n.Normalized();
    }

    public Vector2d ProjectToUV(Vector3d p)
    {
        double best = double.MaxValue; Vector2d bu = Vector2d.Zero;
        for (int j = 0; j < _frames.Count; j++)
        {
            double v = j / (double)Math.Max(1, _frames.Count - 1);
            for (int i = 0; i <= 24; i++)
            {
                double u = i / 24.0;
                double d = (PointAt(new Vector2d(u, v)) - p).LengthSquared;
                if (d < best) { best = d; bu = new Vector2d(u, v); }
            }
        }
        return bu;
    }

    public void TessellateFace(TFace face, Tess.MeshBuilder mb)
    {
        var sec = new List<Vector3d>();
        _sectionCurve.Tessellate(sec, true);
        int nu = sec.Count - 1;
        int nv = _frames.Count - 1;
        if (nu < 1 || nv < 1) return;

        var loc = sec.Select(_sectionPlane.ToLocal2).ToArray();

        int start = mb.VertexCount;
        for (int i = 0; i <= nu; i++)
            for (int j = 0; j <= nv; j++)
            {
                Vector3d p = _frames[j].ToWorld(loc[i]);

                // НОРМАЛЬ — СРЕДНЕЕ ПО ОБХОДУ СОСЕДНИХ КЛЕТОК. Нормаль поверхности (du × dv)
                // меняет знак вдоль сечения, и сборка тела разворачивала такие треугольники:
                // круг вдоль четверти окружности давал 7404 вместо 4935. Среднее по клеткам
                // согласовано с обходом по построению и остаётся гладким.
                Vector3d n = Vector3d.Zero;
                for (int di = -1; di <= 0; di++)
                    for (int dj = -1; dj <= 0; dj++)
                    {
                        int ic = i + di, jc = j + dj;
                        if (ic < 0 || jc < 0 || ic >= nu || jc >= nv) continue;
                        Vector3d pc = _frames[jc].ToWorld(loc[ic]);
                        Vector3d pu = _frames[jc].ToWorld(loc[ic + 1]) - pc;
                        Vector3d pv = _frames[jc + 1].ToWorld(loc[ic]) - pc;
                        Vector3d w = face.Reversed ? Vector3d.Cross(pv, pu) : Vector3d.Cross(pu, pv);
                        if (w.LengthSquared > 1e-24) n += w.Normalized();
                    }
                if (n.LengthSquared < 1e-24)
                {
                    n = NormalAt(new Vector2d(i / (double)nu, j / (double)nv));
                    if (face.Reversed) n = -n;
                }
                else n = n.Normalized();

                // КАЖДАЯ НОРМАЛЬ СОГЛАСУЕТСЯ С ОБХОДОМ СВОЕЙ КЛЕТКИ. Нормаль поверхности —
                // это производная (du × dv), и её знак меняется вдоль сечения; сборка тела
                // разворачивала такие треугольники по нормали, и объём выходил неверным
                // (круг вдоль четверти окружности: 7404 вместо 4935).
                mb.AddVertex(p, n);
            }

        int stride = nv + 1;
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                int a = start + i * stride + j, b = a + 1, c = a + stride, d = c + 1;
                if (face.Reversed) { mb.AddTriangle(a, b, c); mb.AddTriangle(b, d, c); }
                else { mb.AddTriangle(a, c, b); mb.AddTriangle(b, c, d); }
            }
    }
}
