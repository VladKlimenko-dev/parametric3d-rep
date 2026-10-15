using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// ПРОТЯЖКА ПО СЕЧЕНИЯМ: тело между двумя и более плоскими сечениями.
///
/// Сечения размечаются одинаковым числом точек по длине контура, обход приводится к одной
/// стороне, а начало каждого следующего сечения подбирается так, чтобы точки шли напротив
/// предыдущих — иначе тело закручивается. Боковая поверхность строится линейчатой, торцы —
/// плоскими гранями.
///
/// Чего пока нет: направляющих кривых, касательности на концах и сечений с отверстиями.
/// </summary>
public static class LoftOp
{
    static readonly bool HolesOff = Environment.GetEnvironmentVariable("P3D_LOFTHOLES") == "0";
    static readonly bool OrderOff = Environment.GetEnvironmentVariable("P3D_LOFTORDER") == "0";
    static readonly bool AmbigOff = Environment.GetEnvironmentVariable("P3D_LOFTAMBIG") == "0";

    /// <param name="smooth">KRN-07 (07.4): гладкий лофт (интерполяция поперёк сечений степени до 3) вместо линейчатого.</param>
    /// <param name="guides">KRN-07 (07.4): направляющие кривые — каждая проходит через соответственные вершины всех сечений
    /// (отрезок, кривая Безье, дуга короче 180°); тело — точное (NURBS-грани Кунса), иначе отказ.</param>
    /// <param name="tanStart">KRN-07 (07.4): касательная к нормали первого сечения (длина — доля расстояния между центрами
    /// крайних сечений; 0 — без условия); <paramref name="tanEnd"/> — у последнего. Условие делает лофт гладким.</param>
    public static TSolid Build(IReadOnlyList<Profile> sections, int samples = 64, bool smooth = false, IReadOnlyList<ICurve3>? guides = null,
                               double tanStart = 0, double tanEnd = 0)
    {
        if (guides is { Count: > 0 })
        {
            if (Math.Abs(tanStart) > 1e-12 || Math.Abs(tanEnd) > 1e-12)
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Loft: end tangency together with guide curves is not supported.");
            return Guided(sections, smooth, guides);
        }
        _smooth = smooth || Math.Abs(tanStart) > 1e-12 || Math.Abs(tanEnd) > 1e-12; _tan0 = tanStart; _tan1 = tanEnd;
        try { return BuildCore(sections, samples); } finally { _smooth = false; _tan0 = _tan1 = 0; }
    }
    [ThreadStatic] static bool _smooth;
    [ThreadStatic] static double _tan0, _tan1;

    static TSolid BuildCore(IReadOnlyList<Profile> sections, int samples)
    {
        if (sections.Count < 2) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: at least two sections are needed.");
        // KRN-07 (07.4): НЕСКОЛЬКО КОНТУРОВ. Прежде брался только внешний контур — отверстия сечений молча заливались
        // (кольцо r10/r5 → r6/r3: объём на 33% больше). Теперь контуры сечений сопоставляются (внешний с внешним,
        // отверстия — по положению относительно внешнего), каждая пара — свой лофт, отверстия вычитаются. Разное число
        // контуров или неразличимые отверстия — отказ. Мутация P3D_LOFTHOLES=0.
        if (!HolesOff && sections.Any(x => x.Loops.Count > 1)) return BuildWithHoles(sections, samples);
        return BuildSingle(sections, samples);
    }

    /// <summary>Сечения для точного лофта: внешний контур из отрезков и дуг; целая окружность — на N равных дуг (N — число
    /// звеньев у сечений-многоугольников), начало — по направлению первой вершины соседнего сечения. null — не подходит.</summary>
    static TSolid Guided(IReadOnlyList<Profile> sections, bool smooth, IReadOnlyList<ICurve3> guides)
    {
        if (sections.Count < 2) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: at least two sections are needed.");
        if (sections.Any(x => x.Loops.Count > 1)) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Loft: guide curves with holes in the sections are not supported.");
        var secs = ExactSections(sections, false)
                   ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Loft: guide curves need sections of lines, arcs and Bezier curves.");
        string? why = null;
        return DirectRound.NurbsLoft(secs, smooth, ref why, guides)
               ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Loft: the loft along guide curves declined: " + why);
    }

    static List<(List<ICurve3> Loop, Vector3d Normal)>? ExactSections(IReadOnlyList<Profile> sections, bool splitCircles = true)
    {
        var raw = new List<(List<ICurve3> Loop, Vector3d Normal)>();
        foreach (var s in sections)
        {
            if (s.Loops.Count != 1) return null;
            var cs = s.Loops[0].Curves;
            if (cs.Any(c => c is not LineCurve3 && c is not ArcCurve3 && c is not BezierCurve3)) return null;
            raw.Add((cs.ToList(), s.Plane.W));
        }
        int n = raw.Where(r => !(r.Loop.Count == 1 && r.Loop[0] is ArcCurve3 { IsClosed: true })).Select(r => r.Loop.Count).DefaultIfEmpty(1).Max();
        for (int k = 0; k < raw.Count; k++)
        {
            if (!(raw[k].Loop.Count == 1 && raw[k].Loop[0] is ArcCurve3 { IsClosed: true } a) || n == 1 || !splitCircles) continue;
            // начало деления — направление первой вершины ближайшего сечения-многоугольника (относительно его центра)
            var other = raw.Select((r, i) => (r, i)).Where(x => x.r.Loop.Count == n).OrderBy(x => Math.Abs(x.i - k)).FirstOrDefault();
            double a0 = a.A0;
            if (other.r.Loop != null)
            {
                var oc = other.r.Loop.Aggregate(Vector3d.Zero, (acc, c) => acc + c.PointAt(c.T0)) / n;
                var d = other.r.Loop[0].PointAt(other.r.Loop[0].T0) - oc;
                a0 = Math.Atan2(Vector3d.Dot(d, a.Frame.V.Normalized()), Vector3d.Dot(d, a.Frame.U.Normalized()));
            }
            double sw = Math.Sign(a.Sweep) * 2 * Math.PI / n;
            raw[k] = (Enumerable.Range(0, n).Select(i => (ICurve3)new ArcCurve3(a.Frame, a.Radius, a0 + sw * i, a0 + sw * (i + 1))).ToList(), raw[k].Normal);
        }
        return raw;
    }

    private static Profile OneLoop(Profile s, ProfileLoop l)
    {
        var p = new Profile { Plane = s.Plane };
        var nl = new ProfileLoop { IsOuter = true, Area = Math.Abs(l.Area) };
        nl.Curves.AddRange(l.Curves);
        p.Loops.Add(nl);
        return p;
    }

    private static Vector3d LoopCentre(ProfileLoop l)
    {
        var pts = new List<Vector3d>();
        foreach (var c in l.Curves) c.Tessellate(pts, pts.Count == 0);
        return Centre(pts);
    }

    private static TSolid BuildWithHoles(IReadOnlyList<Profile> sections, int samples)
    {
        int n0 = sections[0].Loops.Count;
        for (int k = 0; k < sections.Count; k++)
        {
            if (sections[k].Loops.Count != n0)
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput,
                    $"Loft: section {k + 1} has {sections[k].Loops.Count} contours, section 1 has {n0} — the correspondence of contours is undefined.");
            if (sections[k].Outers.Count() != 1)
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, $"Loft: section {k + 1} has {sections[k].Outers.Count()} outer contours; one is supported.");
        }
        // цепочки отверстий: отверстие следующего сечения — ближайшее по смещению от центра внешнего контура (в осях сечения)
        var holes0 = sections[0].Inners.ToList();
        var chains = holes0.Select(h => new List<ProfileLoop> { h }).ToList();
        for (int k = 1; k < sections.Count; k++)
        {
            var prev = sections[k - 1]; var cur = sections[k];
            var oPrev = LoopCentre(prev.Outers.First()); var oCur = LoopCentre(cur.Outers.First());
            Vector2d Off(Profile s, Vector3d o, ProfileLoop h) { var d = LoopCentre(h) - o; return new Vector2d(Vector3d.Dot(d, s.Plane.U), Vector3d.Dot(d, s.Plane.V)); }
            var free = cur.Inners.ToList();
            foreach (var ch in chains)
            {
                var want = Off(prev, oPrev, ch[^1]);
                var ranked = free.Select(h => (H: h, D: (Off(cur, oCur, h) - want).Length)).OrderBy(x => x.D).ToList();
                if (ranked.Count > 1 && ranked[1].D - ranked[0].D <= 1e-6 * Math.Max(1, ranked[0].D))
                    throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Ambiguous,
                        $"Loft: the holes of sections {k} and {k + 1} cannot be matched unambiguously (two holes equally close).");
                ch.Add(ranked[0].H); free.Remove(ranked[0].H);
            }
        }
        var outer = BuildSingle(sections.Select(x => OneLoop(x, x.Outers.First())).ToList(), samples);
        var bodies = new List<TSolid> { outer };
        var warnings = new List<string>();
        for (int i = 0; i < chains.Count; i++)
        {
            var tool = BuildSingle(chains[i].Select((h, k) => OneLoop(sections[k], h)).ToList(), samples);
            BooleanOp.Subtract(bodies, tool, warnings);
            if (bodies.Count != 1)
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Loft: the hole {i + 1} splits or removes the body.");
        }
        return bodies[0];
    }

    private static TSolid BuildSingle(IReadOnlyList<Profile> sections, int samples)
    {

        // K09. ДВЕ СООСНЫЕ ОКРУЖНОСТИ В ПАРАЛЛЕЛЬНЫХ ПЛОСКОСТЯХ — ЭТО УСЕЧЁННЫЙ КОНУС (или
        // цилиндр). Общий путь сшивает ломаные из 64 точек: объём на 0.4% меньше точного,
        // кромки — многоугольники. Здесь тело строится вращением точного профиля.
        if (sections.Count == 2 && _tan0 == 0 && _tan1 == 0 && CoaxialCircles(sections[0], sections[1]) is { } cone)
            return cone;

        // KRN-07 (07.4): ТОЧНЫЙ ЛОФТ — сечения из отрезков и дуг (у всех одно число звеньев; целая окружность делится на
        // столько дуг, сколько звеньев у соседа) — NURBS-грани по звеньям, линейчатый или гладкий (DirectRound.NurbsLoft).
        // Гладкий без этого пути не строится (отказ), линейчатый — прежним путём по ломаным. Мутация P3D_LOFTNURBS=0.
        // многоугольники (без дуг) у линейчатого лофта — прежним точным путём по вершинам (призматоид, P3D_LOFTCORNERS)
        if (!DirectRound.LoftNurbsOff && ExactSections(sections) is { } secs && (_smooth || secs.Any(x => x.Loop.Any(c => c is ArcCurve3 or BezierCurve3))))
        {
            string? why = null;
            var exact = DirectRound.NurbsLoft(secs, _smooth, ref why, null, _tan0, _tan1);
            if (exact != null) return exact;
            if (_smooth) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Loft: the smooth loft declined: " + why);
        }
        else if (_smooth) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Loft: a smooth loft needs sections of lines and arcs with the same number of curves.");

        var rings = new List<List<Vector3d>>();
        var planes = new List<Frame3>();
        var loops = new List<ProfileLoop>();
        foreach (Profile s in sections)
        {
            var loop = s.Loops.FirstOrDefault(l => l.IsOuter) ?? s.Loops.FirstOrDefault();
            if (loop == null) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: a section is empty or not closed.");
            loops.Add(loop);
            rings.Add(Resample(loop, samples));
            planes.Add(s.Plane);
        }

        // Направление хода — от первого сечения к последнему.
        Vector3d run = Centre(rings[^1]) - Centre(rings[0]);
        if (run.LengthSquared < 1e-18) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: the sections lie on top of each other.");

        // KRN-07 (07.4): ПОРЯДОК СЕЧЕНИЙ. Соседние сечения не пересекаются, и ход не поворачивает назад: каждое следующее
        // сечение целиком по ходу от плоскости предыдущего, и через внутреннее сечение ход идёт в одну сторону. Прежде
        // сечения z=0, 30, 10 давали тело, сложенное само на себя, без отказа. Мутация P3D_LOFTORDER=0.
        if (!OrderOff)
        {
            for (int i = 0; i + 1 < rings.Count; i++)
            {
                var d = Centre(rings[i + 1]) - Centre(rings[i]);
                var ni = planes[i].W * Math.Sign(Vector3d.Dot(planes[i].W, d));
                var nj = planes[i + 1].W * Math.Sign(Vector3d.Dot(planes[i + 1].W, d));
                double tolO = 1e-9 * Math.Max(1, d.Length);
                if (Vector3d.Dot(ni, d) <= tolO || rings[i + 1].Any(p => Vector3d.Dot(p - Centre(rings[i]), ni) <= tolO)
                    || rings[i].Any(p => Vector3d.Dot(p - Centre(rings[i + 1]), nj) >= -tolO))
                    throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput,
                        $"Loft: sections {i + 1} and {i + 2} intersect or face each other edge-on — the loft would cross itself.");
                if (i > 0)
                {
                    var dPrev = Centre(rings[i]) - Centre(rings[i - 1]);
                    if (Vector3d.Dot(planes[i].W, dPrev) * Vector3d.Dot(planes[i].W, d) <= 0)
                        throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput,
                            $"Loft: the sections are out of order — the loft turns back at section {i + 1}.");
                }
            }
        }

        // KRN-07 (07.4): СООТВЕТСТВИЕ ПО ВЕРШИНАМ. Равномерная разметка по длине срезала углы многоугольных сечений
        // (прямоугольник 20×10 → 18×12: шаг 60/64 не попадает в углы, объём на 0.25% меньше формулы призматоида). Сечения с
        // одинаковым числом кривых сопоставляются кривая к кривой (сдвиг начала — по вершинам, ничья — неоднозначность),
        // каждая пара кривых размечается одинаковым числом точек от вершины. Мутация P3D_LOFTCORNERS=0.
        bool matched = !CornersOff && loops[0].Curves.Count >= 2 && loops.All(l => l.Curves.Count == loops[0].Curves.Count);
        if (matched) rings = MatchedRings(loops, run, samples);
        else
            for (int i = 0; i < rings.Count; i++)
            {
                // Обход каждого сечения — против часовой стрелки, если смотреть вдоль хода.
                if (Vector3d.Dot(Normal(rings[i]), run) < 0) rings[i].Reverse();
                // Начало — напротив начала предыдущего сечения.
                if (i > 0) rings[i] = Align(rings[i - 1], rings[i], i);
            }
        samples = rings[0].Count;

        var polys = new List<CsgPolygon>();
        var sources = new Dictionary<uint, ISurface3?>();
        var names = new Dictionary<uint, string>();
        uint tag = 1;

        for (int s = 0; s + 1 < rings.Count; s++)
        {
            var a = rings[s];
            var b = rings[s + 1];
            for (int i = 0; i < samples; i++)
            {
                int j = (i + 1) % samples;
                Add(polys, tag, a[i], a[j], b[j]);
                Add(polys, tag, a[i], b[j], b[i]);
            }
            sources[tag] = null;                   // линейчатая поверхность: точной пока нет
            names[tag] = $"loft:band[{s}]";        // устойчивое имя: номер пары сечений
            tag++;
        }

        // Торцы: первый смотрит назад, последний — вперёд.
        names[tag] = "loft:cap[0]";
        Cap(polys, sources, ref tag, rings[0], planes[0], -run);
        names[tag] = "loft:cap[1]";
        Cap(polys, sources, ref tag, rings[^1], planes[^1], run);

        var solid = BooleanOp.SolidFromPolygons(polys, sources, names);
        // KRN-07 (07.4): тело, сложенное само на себя (сечения закручены или сходятся через ось), — отказ, а не
        // опубликованное негодное тело (прежде: лофт 20×10 → 10×20 давал 8 ошибок сетки при «успешной» операции).
        // Мутация P3D_LOFTVALID=0.
        if (!ValidOff)
        {
            var v = SolidValidator.Validate(solid);
            // сбой разметки грани (внутренний, не свойство входа) — не отказ по входу
            if (v.Issues.Any(i => i.Code == "face-missing"))
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InternalFailure, "Loft: the body could not be meshed for validation — " + v.Summary());
            if (!v.IsValid)
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: the lofted body is not a valid solid (self-crossing or folded bands) — " + v.Summary());
        }
        return solid;
    }
    static readonly bool ValidOff = Environment.GetEnvironmentVariable("P3D_LOFTVALID") == "0";

    static readonly bool CornersOff = Environment.GetEnvironmentVariable("P3D_LOFTCORNERS") == "0";

    private static List<Vector3d> Poly(ICurve3 c) { var t = new List<Vector3d>(); c.Tessellate(t, true); return t; }
    private static double Len(List<Vector3d> t) { double l = 0; for (int i = 1; i < t.Count; i++) l += (t[i] - t[i - 1]).Length; return l; }

    private static List<List<Vector3d>> MatchedRings(List<ProfileLoop> loops, Vector3d run, int samples)
    {
        int m = loops[0].Curves.Count;
        var seqs = new List<List<ICurve3>>();
        foreach (var l in loops)
        {
            var cs = l.Curves.ToList();
            var ring = new List<Vector3d>();
            foreach (var c in cs) { var t = Poly(c); ring.AddRange(t.Take(t.Count - 1)); }
            if (Vector3d.Dot(Normal(ring), run) < 0) { cs.Reverse(); cs = cs.Select(c => c.Reversed()).ToList(); }
            seqs.Add(cs);
        }
        for (int i = 1; i < seqs.Count; i++)
        {
            var prev = seqs[i - 1]; var cur = seqs[i];
            var ps = prev.Select(c => c.PointAt(c.T0)).ToList(); var qs = cur.Select(c => c.PointAt(c.T0)).ToList();
            var cp = Centre(ps); var cq = Centre(qs);
            var sums = new double[m]; int best = 0; double scale = 0;
            for (int k = 0; k < m; k++) scale += (ps[k] - cp).LengthSquared + (qs[k] - cq).LengthSquared;
            for (int off = 0; off < m; off++)
            {
                double sum = 0;
                for (int k = 0; k < m; k++) sum += ((ps[k] - cp) - (qs[(k + off) % m] - cq)).LengthSquared;
                sums[off] = sum; if (sum < sums[best]) best = off;
            }
            if (!AmbigOff)
                for (int off = 0; off < m; off++)
                    if (off != best && sums[off] - sums[best] <= 1e-9 * Math.Max(scale, 1e-12))
                        throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Ambiguous,
                            $"Loft: the vertices of section {i + 1} match section {i} in two different ways (twist ambiguity); the correspondence must be chosen.");
            seqs[i] = Enumerable.Range(0, m).Select(k => cur[(k + best) % m]).ToList();
        }
        var len = new double[m]; double total = 0;
        for (int k = 0; k < m; k++) { foreach (var sq in seqs) len[k] += Len(Poly(sq[k])); len[k] /= seqs.Count; total += len[k]; }
        var counts = len.Select(x => Math.Max(1, (int)Math.Round(samples * x / total))).ToArray();
        var rings = new List<List<Vector3d>>();
        foreach (var sq in seqs)
        {
            var ring = new List<Vector3d>();
            for (int k = 0; k < m; k++)
            {
                var t = Poly(sq[k]); double L = Len(t); int nk = counts[k];
                var acc = new List<double> { 0 }; for (int j = 1; j < t.Count; j++) acc.Add(acc[^1] + (t[j] - t[j - 1]).Length);
                int seg = 0;
                for (int j = 0; j < nk; j++)
                {
                    double want = L * j / nk;
                    while (seg + 1 < acc.Count - 1 && acc[seg + 1] < want) seg++;
                    double sl = acc[seg + 1] - acc[seg];
                    double u = sl <= 1e-15 ? 0 : (want - acc[seg]) / sl;
                    ring.Add(t[seg] + (t[seg + 1] - t[seg]) * u);
                }
            }
            rings.Add(ring);
        }
        return rings;
    }

    private static void Add(List<CsgPolygon> polys, uint tag, Vector3d a, Vector3d b, Vector3d c)
    {
        if (Vector3d.Cross(b - a, c - a).LengthSquared < 1e-24) return;
        polys.Add(new CsgPolygon(new List<Vector3d> { a, b, c }, tag));
    }

    private static void Cap(List<CsgPolygon> polys, Dictionary<uint, ISurface3?> sources, ref uint tag,
                            List<Vector3d> ring, Frame3 plane, Vector3d outward)
    {
        Vector3d c = Centre(ring);
        bool flip = Vector3d.Dot(Normal(ring), outward) < 0;
        for (int i = 0; i < ring.Count; i++)
        {
            int j = (i + 1) % ring.Count;
            if (flip) Add(polys, tag, c, ring[j], ring[i]);
            else Add(polys, tag, c, ring[i], ring[j]);
        }
        sources[tag] = new PlaneSurface(plane);
        tag++;
    }

    /// <summary>Равномерная разметка контура по длине.</summary>
    private static List<Vector3d> Resample(ProfileLoop loop, int samples)
    {
        var pts = new List<Vector3d>();
        foreach (var c in loop.Curves)
        {
            var part = new List<Vector3d>();
            c.Tessellate(part, true);
            for (int i = 0; i + 1 < part.Count; i++) pts.Add(part[i]);
        }
        if (pts.Count < 3) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: a section is degenerate.");

        var acc = new List<double> { 0 };
        for (int i = 1; i <= pts.Count; i++) acc.Add(acc[^1] + (pts[i % pts.Count] - pts[i - 1]).Length);
        double total = acc[^1];
        if (total <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Loft: a section is degenerate.");

        var res = new List<Vector3d>(samples);
        int k = 0;
        for (int i = 0; i < samples; i++)
        {
            double want = total * i / samples;
            while (k + 1 < acc.Count - 1 && acc[k + 1] < want) k++;
            double seg = acc[k + 1] - acc[k];
            double t = seg <= Tol.Weld ? 0 : (want - acc[k]) / seg;
            res.Add(pts[k] + (pts[(k + 1) % pts.Count] - pts[k]) * t);
        }
        return res;
    }

    private static Vector3d Centre(List<Vector3d> ring)
    {
        Vector3d c = Vector3d.Zero;
        foreach (var p in ring) c += p;
        return c / ring.Count;
    }

    /// <summary>Нормаль контура по площади (правило правой руки относительно обхода).</summary>
    private static Vector3d Normal(List<Vector3d> ring)
    {
        Vector3d c = Centre(ring), n = Vector3d.Zero;
        for (int i = 0; i < ring.Count; i++)
            n += Vector3d.Cross(ring[i] - c, ring[(i + 1) % ring.Count] - c);
        return n;
    }

    /// <summary>Сдвиг начала второго контура так, чтобы точки шли напротив первого.</summary>
    private static List<Vector3d> Align(List<Vector3d> a, List<Vector3d> b, int section = 0)
    {
        int n = b.Count, best = 0;
        double bestSum = double.MaxValue;
        var sums = new double[n];
        for (int shift = 0; shift < n; shift++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += (a[i] - b[(i + shift) % n]).LengthSquared;
            sums[shift] = sum;
            if (sum < bestSum) { bestSum = sum; best = shift; }
        }
        // KRN-07 (07.4): НЕОДНОЗНАЧНОЕ СООТВЕТСТВИЕ. Два разных локальных минимума с равной суммой (прямоугольник 20×10 →
        // 10×20: закрутка на +90° и на −90° равноправны, тела зеркальны) — выбор за пользователя, а не первый по номеру.
        // Мутация P3D_LOFTAMBIG=0.
        if (!AmbigOff)
        {
            Vector3d ca = Centre(a), cb = Centre(b);
            double scale = 0; for (int i = 0; i < n; i++) scale += (a[i] - ca).LengthSquared + (b[i] - cb).LengthSquared;
            for (int k = 0; k < n; k++)
            {
                int dist = Math.Min((k - best + n) % n, (best - k + n) % n);
                if (dist < 2) continue;
                bool localMin = sums[k] <= sums[(k + 1) % n] && sums[k] <= sums[(k - 1 + n) % n];
                // неразличимо при разметке: разница меньше изменения суммы при сдвиге лучшего на одну точку
                double step = Math.Min(Math.Abs(sums[(best + 1) % n] - bestSum), Math.Abs(sums[(best - 1 + n) % n] - bestSum));
                if (localMin && sums[k] - bestSum <= Math.Max(step, 1e-9 * Math.Max(scale, 1e-12)))
                    throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Ambiguous,
                        $"Loft: the start of section {section + 1} matches section {section} in two different ways (twist ambiguity); the correspondence must be chosen.");
            }
        }
        var res = new List<Vector3d>(n);
        for (int i = 0; i < n; i++) res.Add(b[(i + best) % n]);
        return res;
    }

    private static TSolid? CoaxialCircles(Profile a, Profile b)
    {
        static ArcCurve3? Circle(Profile p) =>
            p.Loops.Count == 1 && p.Loops[0].Curves.Count == 1 && p.Loops[0].Curves[0] is ArcCurve3 c && c.IsClosed ? c : null;
        var ca = Circle(a); var cb = Circle(b);
        if (ca == null || cb == null) return null;
        Vector3d wa = ca.Frame.W.Normalized(), wb = cb.Frame.W.Normalized();
        if (Math.Abs(Math.Abs(Vector3d.Dot(wa, wb)) - 1) > 1e-12) return null;
        Vector3d axis = cb.Frame.Origin - ca.Frame.Origin;
        double h = axis.Length;
        if (h < Tol.Weld || Math.Abs(Math.Abs(Vector3d.Dot(axis / h, wa)) - 1) > 1e-12) return null;
        var w = axis / h;
        // Профиль в плоскости через ось: трапеция (r0 у начала, r1 у конца), вращение на 360°.
        var u = ca.Frame.U.Normalized(); u = (u - w * Vector3d.Dot(u, w)).Normalized();
        var o = ca.Frame.Origin;
        var plane = new Frame3(o, u, w);
        var pts = new[] { o, o + u * ca.Radius, o + w * h + u * cb.Radius, o + w * h };
        var curves = new List<ICurve3>();
        for (int i = 0; i < 4; i++) curves.Add(new LineCurve3(pts[i], pts[(i + 1) % 4]));
        var profile = ProfileBuilder.Build(plane, curves);
        if (profile.IsEmpty) return null;
        var axisFrame = Frame3.FromNormal(o, w);
        return RevolveOp.Build(profile, new Frame3(o, u, Vector3d.Cross(w, u)), 0, 2 * Math.PI);
    }
}
