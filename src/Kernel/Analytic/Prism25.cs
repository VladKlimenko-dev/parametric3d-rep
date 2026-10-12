using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// «2.5-МЕРНОЕ» ТЕЛО (аналитическое ядро, этап 1): все грани — торцы (плоскости ⟂ d) или боковые (плоскости ∥ d,
/// цилиндры с осью ∥ d). Его сечение плоскостью ⟂ d постоянно между уровнями торцов, и тело задаётся уровнями и
/// сечениями слоёв — плоскими областями из отрезков и дуг (Region2). Сечения берутся из ТОРЦОВ: на уровне l материал,
/// бывший ниже, кончается на торцах, смотрящих вверх, и начинается на смотрящих вниз:
///     сечение выше l = (сечение ниже l − торцы вверх) ∪ торцы вниз.
/// Контракт: каждая грань распознана; рёбра торцов — точные отрезки и дуги окружностей (дуга, не восстановленная из
/// хорд, — отказ: «точность» была бы мнимой); над верхним уровнем сечение пусто.
/// </summary>
public sealed class Prism25
{
    public Vector3d D, U, V;                 // направление и рамка сечения (U × V = D)
    /// <summary>KRN-02 (02.9): начало локальной системы (PrismBuild.LocalOrigin на время булевой).</summary>
    public Vector3d O;
    public double[] Levels = Array.Empty<double>();
    public Region2[] Sections = Array.Empty<Region2>();   // Sections[k] — между Levels[k] и Levels[k + 1]

    /// <summary>Грани тела для наследования имён: боковые — с несущей и пределами по высоте; торцы — уровень и область.</summary>
    readonly List<(TFace F, Ssi.Shape Shape, double Lo, double Hi)> _sides = new();
    readonly List<(TFace F, double Level, Region2 Region)> _caps = new();

    public double Volume
    {
        get { double v = 0; for (int k = 0; k < Sections.Length; k++) v += Sections[k].Area * (Levels[k + 1] - Levels[k]); return v; }
    }

    public Vector2d To2(Vector3d p) => new(Vector3d.Dot(p - O, U), Vector3d.Dot(p - O, V));
    /// <summary>Направление в рамке сечения (без сдвига начала).</summary>
    public Vector2d To2Dir(Vector3d v) => new(Vector3d.Dot(v, U), Vector3d.Dot(v, V));
    public double H(Vector3d p) => Vector3d.Dot(p - O, D);

    const double Par = 1e-9;                 // допуск параллельности направлений
    const double LevelTol = 1e-7;            // совпадение уровней, мм

    /// <summary>Распознать тело как 2.5-мерное; null — не подходит (причина — в why).</summary>
    public static Prism25? Recognize(TSolid s, out string why)
    {
        var shapes = new Dictionary<TFace, Ssi.Shape?>(ReferenceEqualityComparer.Instance);
        foreach (var f in s.Faces) shapes[f] = ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
        if (shapes.Values.Any(x => x is null)) { why = "a face without a recognised surface"; return null; }
        if (shapes.Values.Any(x => x is not (Ssi.Plane or Ssi.Cylinder))) { why = "a face that is neither a plane nor a cylinder"; return null; }

        // Направления-кандидаты: ось первого цилиндра; без цилиндров — нормали плоскостей (по порядку, без повторов).
        var cands = new List<Vector3d>();
        void AddCand(Vector3d v) { v = v.Normalized(); if (cands.All(c => Math.Abs(Math.Abs(Vector3d.Dot(c, v)) - 1) > Par)) cands.Add(v); }
        foreach (var x in shapes.Values) if (x is Ssi.Cylinder c) { AddCand(c.Axis); break; }
        if (cands.Count == 0) foreach (var x in shapes.Values) if (x is Ssi.Plane p) AddCand(p.N);
        why = "no direction makes every face a cap or a side";
        foreach (var d0 in cands.Take(6))
        {
            var r = TryDirection(s, shapes, d0, out var w);
            if (r != null) { why = ""; return r; }
            why = w;
        }
        return null;
    }

    /// <summary>Направления-кандидаты для пары тел: оси цилиндров (обоих), затем нормали плоскостей; без повторов.</summary>
    public static List<Vector3d> DirectionCandidates(params TSolid[] bodies)
    {
        var cands = new List<Vector3d>();
        void Add(Vector3d v) { v = v.Normalized(); if (cands.All(c => Math.Abs(Math.Abs(Vector3d.Dot(c, v)) - 1) > Par)) cands.Add(v); }
        var shapes = bodies.SelectMany(b => b.Faces).Select(f => ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f))).ToList();
        foreach (var x in shapes) if (x is Ssi.Cylinder c) Add(c.Axis);
        foreach (var x in shapes) if (x is Ssi.Plane p) Add(p.N);
        return cands.Take(8).ToList();
    }

    /// <summary>Распознать вдоль заданного направления и в заданной рамке (второй операнд булевой — в рамке первого).</summary>
    public static Prism25? RecognizeAlong(TSolid s, Vector3d d, Vector3d U, Vector3d V, out string why)
    {
        var shapes = new Dictionary<TFace, Ssi.Shape?>(ReferenceEqualityComparer.Instance);
        foreach (var f in s.Faces) shapes[f] = ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
        if (shapes.Values.Any(x => x is null)) { why = "a face without a recognised surface"; return null; }
        if (shapes.Values.Any(x => x is not (Ssi.Plane or Ssi.Cylinder))) { why = "a face that is neither a plane nor a cylinder"; return null; }
        return TryDirection(s, shapes, d, out why, U, V);
    }

    /// <summary>Имя боковой грани тела на несущей c (прямая или окружность) на высоте h; null — нет такой.</summary>
    public string? SideTagAt(Seg2 c, double h) => SideFaceAt(c, h)?.Tag;

    /// <summary>Боковая грань тела на несущей c на высоте h (источник грани результата); null — нет такой.</summary>
    public TFace? SideFaceAt(Seg2 c, double h)
    {
        var mid = c.At(0.5);
        foreach (var (f, sh, lo, hi) in _sides)
        {
            if (h < lo - LevelTol || h > hi + LevelTol) continue;
            if (c.IsArc && sh is Ssi.Cylinder cy)
            {
                var cc = To2(cy.P);
                if ((cc - c.C).Length > Region2Ops.Eps || Math.Abs(cy.R - c.R) > Region2Ops.Eps) continue;
            }
            else if (!c.IsArc && sh is Ssi.Plane pl)
            {
                var n2 = To2Dir(pl.N) ; if (n2.Length < 1e-12) continue; n2 = n2.Normalized();
                double off = Vector2d.Dot(To2(pl.P), n2);
                if (Math.Abs(Vector2d.Dot(c.A, n2) - off) > Region2Ops.Eps || Math.Abs(Vector2d.Dot(c.B, n2) - off) > Region2Ops.Eps) continue;
            }
            else continue;
            // по месту на несущей: середина куска — в пределах проекции грани
            var pts = f.Loops.SelectMany(l => l.CoEdges).SelectMany(ce => { var p = new List<Vector3d>(); ce.Tessellate(p, true); return p; }).Select(To2).ToList();
            if (!c.IsArc)
            {
                var t = (c.B - c.A).Normalized();
                double a = pts.Min(p => Vector2d.Dot(p, t)), b = pts.Max(p => Vector2d.Dot(p, t)), m = Vector2d.Dot(mid, t);
                if (m < a - Region2Ops.Eps || m > b + Region2Ops.Eps) continue;
            }
            else if (pts.Count > 0 && pts.Min(p => (p - mid).Length) > c.Length / 2 + Region2Ops.Eps) continue;
            return f;
        }
        return null;
    }

    /// <summary>Имя торца тела на уровне h, область которого содержит точку p; null — нет такого.</summary>
    public string? CapTagAt(double h, Vector2d p) => CapFaceAt(h, p)?.Tag;

    /// <summary>Лежит ли грань f этого тела на той же несущей, что кусок результата (боковая — та же прямая/окружность
    /// с перекрытием по высоте; торец — тот же уровень).</summary>
    public bool OnCarrierOf(TFace f, bool side, Seg2 c, double lo, double hi)
    {
        if (side)
        {
            foreach (var (g, sh, glo, ghi) in _sides)
            {
                if (!ReferenceEquals(g, f)) continue;
                if (ghi < lo - LevelTol || glo > hi + LevelTol) return false;
                if (c.IsArc && sh is Ssi.Cylinder cy) return (To2(cy.P) - c.C).Length <= Region2Ops.Eps && Math.Abs(cy.R - c.R) <= Region2Ops.Eps;
                if (!c.IsArc && sh is Ssi.Plane pl)
                {
                    var n2 = To2Dir(pl.N); if (n2.Length < 1e-12) return false; n2 = n2.Normalized();
                    double off = Vector2d.Dot(To2(pl.P), n2);
                    return Math.Abs(Vector2d.Dot(c.A, n2) - off) <= Region2Ops.Eps && Math.Abs(Vector2d.Dot(c.B, n2) - off) <= Region2Ops.Eps;
                }
                return false;
            }
            return false;
        }
        foreach (var (g, level, _) in _caps) if (ReferenceEquals(g, f)) return Math.Abs(level - lo) <= LevelTol;
        return false;
    }

    /// <summary>Торец тела на уровне h, область которого содержит точку p; null — нет такого.</summary>
    public TFace? CapFaceAt(double h, Vector2d p)
    {
        foreach (var (f, level, reg) in _caps)
            if (Math.Abs(level - h) <= LevelTol && reg.Contains(p)) return f;
        return null;
    }

    /// <summary>
    /// РАЗБОР ЭТАПА 2 (локальная 2.5-мерность): лежат ли все грани тела, проекция которых вдоль d пересекает проекцию
    /// резца (габариты в рамке сечения, с запасом), торцами или боковыми вдоль d. Консервативно: по габаритам проекций.
    /// </summary>
    public static bool LocallyPrismatic(TSolid body, TSolid tool, Vector3d d, Vector3d U, Vector3d V, out int blocking)
    {
        (Vector2d Lo, Vector2d Hi) Box2(IEnumerable<Vector3d> pts)
        {
            var lo = new Vector2d(double.MaxValue); var hi = new Vector2d(double.MinValue);
            foreach (var p in pts) { var q = new Vector2d(Vector3d.Dot(p, U), Vector3d.Dot(p, V)); lo = Vector2d.ComponentMin(lo, q); hi = Vector2d.ComponentMax(hi, q); }
            return (lo, hi);
        }
        IEnumerable<Vector3d> Pts(TFace f) { var l = new List<Vector3d>(); foreach (var ce in f.Loops.SelectMany(x => x.CoEdges)) ce.Tessellate(l, l.Count == 0); return l; }
        var tb = Box2(tool.Faces.SelectMany(Pts)); var m = new Vector2d(1e-3);
        tb = (tb.Lo - m, tb.Hi + m);
        blocking = 0;
        foreach (var f in body.Faces)
        {
            var sh = ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
            bool ok = sh switch
            {
                Ssi.Plane pl => Math.Abs(Math.Abs(Vector3d.Dot(pl.N.Normalized(), d)) - 1) < Par || Math.Abs(Vector3d.Dot(pl.N.Normalized(), d)) < Par,
                Ssi.Cylinder cy => Math.Abs(Math.Abs(Vector3d.Dot(cy.Axis.Normalized(), d)) - 1) < Par,
                _ => false,
            };
            if (ok) continue;
            var fb = Box2(Pts(f));
            if (fb.Lo.X <= tb.Hi.X && tb.Lo.X <= fb.Hi.X && fb.Lo.Y <= tb.Hi.Y && tb.Lo.Y <= fb.Hi.Y) blocking++;
        }
        return blocking == 0;
    }

    /// <summary>Сечение на высоте z (вне уровней — пусто).</summary>
    public Region2 SectionAt(double z)
    {
        for (int k = 0; k + 1 < Levels.Length; k++)
            if (z > Levels[k] && z < Levels[k + 1]) return Sections[k];
        return new Region2();
    }

    static Prism25? TryDirection(TSolid s, Dictionary<TFace, Ssi.Shape?> shapes, Vector3d d, out string why,
                                 Vector3d? givenU = null, Vector3d? givenV = null)
    {
        // Рамка: U ⟂ d, V = d × U (или заданная).
        var u0 = Math.Abs(d.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
        var U = givenU ?? (u0 - d * Vector3d.Dot(u0, d)).Normalized(); var V = givenV ?? Vector3d.Cross(d, U);
        var pr = new Prism25 { D = d, U = U, V = V, O = PrismBuild.LocalOrigin };

        var caps = new List<(TFace F, double Level, bool Up)>();
        foreach (var (f, sh) in shapes)
        {
            switch (sh)
            {
                case Ssi.Plane pl:
                    double c = Vector3d.Dot(pl.N.Normalized(), d);
                    if (Math.Abs(Math.Abs(c) - 1) < Par)
                    {
                        // Наружная нормаль грани — по нормали поверхности с учётом обращения грани.
                        var p0 = f.Loops[0].CoEdges[0].Start.P;
                        bool facesUp = Vector3d.Dot(f.NormalAt(p0), d) > 0;
                        caps.Add((f, pr.H(pl.P), facesUp));
                    }
                    else if (Math.Abs(c) > Par) { why = "a plane that is neither across nor along the direction"; return null; }
                    break;
                case Ssi.Cylinder cy:
                    if (Math.Abs(Math.Abs(Vector3d.Dot(cy.Axis.Normalized(), d)) - 1) > Par) { why = "a cylinder not along the direction"; return null; }
                    break;
            }
        }
        if (caps.Count < 2) { why = "fewer than two caps"; return null; }

        // Уровни (с объединением близких).
        var lv = new List<double>();
        foreach (var l in caps.Select(c => c.Level).OrderBy(x => x)) if (lv.Count == 0 || l - lv[^1] > LevelTol) lv.Add(l);
        pr.Levels = lv.ToArray();

        // Боковые грани — для наследования имён (несущая и пределы по высоте).
        foreach (var (f, sh) in shapes)
        {
            if (sh is Ssi.Plane plS && Math.Abs(Vector3d.Dot(plS.N.Normalized(), d)) > Par) continue;      // торец
            var hs = f.Loops.SelectMany(l => l.CoEdges).Select(ce => pr.H(ce.Start.P)).ToList();
            if (hs.Count > 0) pr._sides.Add((f, sh!, hs.Min(), hs.Max()));
        }
        // Торцы → области (2D), по уровням и сторонам.
        var up = lv.Select(_ => new Region2()).ToArray(); var down = lv.Select(_ => new Region2()).ToArray();
        foreach (var (f, level, isUp) in caps)
        {
            int k = lv.FindIndex(x => Math.Abs(x - level) <= LevelTol);
            var reg = pr.FaceRegion(f, out why);
            if (reg == null) return null;
            pr._caps.Add((f, level, reg));
            (isUp ? up[k] : down[k]).Loops.AddRange(reg.Loops);
        }

        // Сечения слоёв: ниже нижнего уровня — пусто.
        var cur = new Region2();
        var secs = new List<Region2>();
        for (int k = 0; k < lv.Count; k++)
        {
            if (up[k].Loops.Count > 0) cur = Region2Ops.Boolean(cur, up[k], Bool2.Subtract);
            if (down[k].Loops.Count > 0) cur = Region2Ops.Boolean(cur, down[k], Bool2.Union);
            if (k < lv.Count - 1) secs.Add(cur);
        }
        if (Math.Abs(cur.Area) > 1e-9 * Math.Max(1, secs.Count > 0 ? secs.Max(x => Math.Abs(x.Area)) : 1))
        { why = $"the section above the top cap is not empty ({cur.Area:G6})"; return null; }
        pr.Sections = secs.ToArray();
        why = "";
        return pr;
    }

    /// <summary>Наибольший сдвиг вершины при лечении торцов последнего распознавания (мм).</summary>
    public double HealShift;

    /// <summary>
    /// Допуск лечения вершин, мм: вершины тел из многогранного пути лежат на гранях вписанного многогранника (ошибка
    /// гранения — до ~0.02…0.05 мм при допуске хорды 0.02); дальше — тело не годится для точного пути.
    /// </summary>
    public const double HealTol = 0.1;

    /// <summary>
    /// Плоская грань-торец → область в рамке сечения (наружный контур — против часовой).
    ///
    /// ЛЕЧЕНИЕ ВЕРШИН. Тело, построенное многогранным путём, несогласовано: кривые рёбер восстановлены точными (окружность
    /// R10), а вершины стоят на гранях вписанного многогранника (стык стороны паза с окружностью — на 0.019 мм внутри).
    /// Точный путь вершинам из гранения не доверяет: каждая вершина контура ставится в точное пересечение НЕСУЩИХ кривых
    /// соседних кусков (прямая×прямая, прямая×окружность, окружность×окружность — ближайший к вершине корень). Направление
    /// дуги — по собственной ломаной ребра (параметр восстановленной кривой ребру не соответствует).
    /// </summary>
    public Region2? FaceRegion(TFace f, out string why)
    {
        var reg = new Region2();
        HealShift = Math.Max(HealShift, 0);
        // ШВЫ: ребро, встречающееся в грани дважды (шов вращения у кольца R4…R7: петля «внешняя окружность — шов —
        // внутренняя навстречу — шов назад»), — не граница области; выбрасывается, а оставшиеся куски разбиваются на
        // замкнутые цепочки — каждая свой контур. Прежде такая петля давала мусор в 2D-булевой (сечение 0).
        var useCount = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) useCount[ce.Edge] = useCount.GetValueOrDefault(ce.Edge) + 1;
        var runs = new List<List<TCoEdge>>();
        foreach (var loop in f.Loops)
        {
            var kept = loop.CoEdges.Where(ce => useCount[ce.Edge] == 1).ToList();
            // ЦЕПОЧКИ ПО ВЕРШИНАМ — всегда: петля торца тела вращения содержит две ЗАМКНУТЫЕ окружности подряд (кольцо
            // R4…R7 — одна петля «R4, R7» без соединяющих отрезков); замкнутое ребро — свой контур. Обычный контур
            // при разбивке не меняется.
            var left = new List<TCoEdge>(kept);
            while (left.Count > 0)
            {
                // Вершины сравниваются по ссылке ИЛИ по положению: у копии тела (TransformOp.Copy) соседние рёбра
                // ссылаются на разные объекты вершин в одной точке.
                static bool Same(TVertex a, TVertex b) => ReferenceEquals(a, b) || (a.P - b.P).LengthSquared <= 1e-18;
                var run = new List<TCoEdge> { left[0] }; left.RemoveAt(0);
                while (!Same(run[^1].End, run[0].Start))
                {
                    int nx = left.FindIndex(c => Same(c.Start, run[^1].End));
                    if (nx < 0) { why = "a cap loop does not close after dropping seam edges"; return null; }
                    run.Add(left[nx]); left.RemoveAt(nx);
                }
                runs.Add(run);
            }
        }
        foreach (var loopCes in runs)
        {
            // Несущие кривые кусков: прямая (точка, направление) или окружность (центр, радиус) + направление обхода дуги.
            var carriers = new List<(bool Arc, Vector2d P, Vector2d Dir, Vector2d C, double R, bool Ccw, Vector2d Start, Vector2d End)>();
            foreach (var ce in loopCes)
            {
                var a = To2(ce.Start.P); var b = To2(ce.End.P);
                switch (ce.Edge.Curve)
                {
                    case LineCurve3:
                        if ((a - b).Length > Region2Ops.Eps) carriers.Add((false, a, (b - a).Normalized(), default, 0, false, a, b));
                        break;
                    case ArcCurve3 arc:
                        {
                            var c = To2(arc.Frame.Origin);
                            // направление обхода — по собственной ломаной ребра в сторону полуребра
                            var pts = new List<Vector3d>(); ce.Tessellate(pts, true);
                            if (pts.Count < 3) { why = "an arc edge without a polyline"; return null; }
                            bool closedArc = (a - b).Length <= Region2Ops.Eps;
                            // у замкнутой дуги середина ломаной — ровно напротив начала (сравнение с π неустойчиво):
                            // направление — по точке на четверти пути
                            var m = To2(pts[closedArc ? Math.Max(1, pts.Count / 4) : pts.Count / 2]);
                            double ta = Math.Atan2(a.Y - c.Y, a.X - c.X), tm = Math.Atan2(m.Y - c.Y, m.X - c.X), tb = Math.Atan2(b.Y - c.Y, b.X - c.X);
                            bool ccw = closedArc ? Norm(tm - ta) < Math.PI : Norm(tm - ta) < Norm(tb - ta);
                            carriers.Add((true, default, default, c, arc.Radius, ccw, a, b));
                            break;
                        }
                    default:
                        why = $"a cap edge is {ce.Edge.Curve.GetType().Name}, not a line or a circular arc";
                        return null;
                }
            }
            int n = carriers.Count;
            if (n == 0) continue;
            // Вылеченные вершины: стык куска i и i+1 — точное пересечение их несущих, ближайшее к общей вершине.
            var joint = new Vector2d[n];
            for (int i = 0; i < n; i++)
            {
                var x = carriers[i]; var y = carriers[(i + 1) % n];
                var v = x.End;
                Vector2d? best = null;
                foreach (var q in CarrierIntersections(x, y))
                    if (best == null || (q - v).Length < (best.Value - v).Length) best = q;
                var hv = best ?? v;
                if ((hv - v).Length > HealTol) { why = $"a vertex is {(hv - v).Length:G3} mm off its carrier curves"; return null; }
                HealShift = Math.Max(HealShift, (hv - v).Length);
                joint[i] = hv;
            }
            var segs = new List<Seg2>();
            for (int i = 0; i < n; i++)
            {
                var x = carriers[i];
                var s0 = joint[(i + n - 1) % n]; var s1 = joint[i];
                if (!x.Arc) { if ((s1 - s0).Length > Region2Ops.Eps) segs.Add(Seg2.Line(s0, s1)); continue; }
                double t0 = Math.Atan2(s0.Y - x.C.Y, s0.X - x.C.X), t1 = Math.Atan2(s1.Y - x.C.Y, s1.X - x.C.X);
                bool closedArc = n == 1 || (s1 - s0).Length <= Region2Ops.Eps;
                double sweep = closedArc ? (x.Ccw ? 2 * Math.PI : -2 * Math.PI) : (x.Ccw ? Norm(t1 - t0) : Norm(t1 - t0) - 2 * Math.PI);
                var start = x.C + x.R * new Vector2d(Math.Cos(t0), Math.Sin(t0));
                if (closedArc)
                {
                    var mid = x.C + x.R * new Vector2d(Math.Cos(t0 + sweep / 2), Math.Sin(t0 + sweep / 2));
                    segs.Add(new Seg2(start, mid, true, x.C, x.R, t0, sweep / 2));
                    segs.Add(new Seg2(mid, start, true, x.C, x.R, t0 + sweep / 2, sweep / 2));
                }
                else segs.Add(new Seg2(s0, s1, true, x.C, x.R, t0, sweep));
            }
            if (segs.Count > 0) reg.Loops.Add(segs);
        }
        // Ориентация: площадь грани положительна (наружные — против часовой); иначе обратить все контуры.
        if (reg.Area < 0)
            for (int i = 0; i < reg.Loops.Count; i++)
            {
                var l = reg.Loops[i]; var rl = new List<Seg2>(l.Count);
                for (int j = l.Count - 1; j >= 0; j--) rl.Add(l[j].Reversed());
                reg.Loops[i] = rl;
            }
        why = "";
        return reg;
    }

    /// <summary>Пересечения несущих кривых двух кусков (прямые и окружности целиком, не отрезки).</summary>
    static IEnumerable<Vector2d> CarrierIntersections((bool Arc, Vector2d P, Vector2d Dir, Vector2d C, double R, bool Ccw, Vector2d Start, Vector2d End) x,
                                                      (bool Arc, Vector2d P, Vector2d Dir, Vector2d C, double R, bool Ccw, Vector2d Start, Vector2d End) y)
    {
        if (!x.Arc && !y.Arc)
        {
            double den = x.Dir.X * y.Dir.Y - x.Dir.Y * y.Dir.X;
            if (Math.Abs(den) < 1e-12) yield break;                 // параллельные: стыка нет (оставить вершину)
            var q = y.P - x.P; double t = (q.X * y.Dir.Y - q.Y * y.Dir.X) / den;
            yield return x.P + x.Dir * t;
            yield break;
        }
        if (x.Arc && y.Arc)
        {
            var d = y.C - x.C; double dist = d.Length;
            if (dist < 1e-12) yield break;
            double a = (dist * dist + x.R * x.R - y.R * y.R) / (2 * dist);
            double h2 = x.R * x.R - a * a; double h = h2 > 0 ? Math.Sqrt(h2) : 0;
            var e = d / dist; var nrm = new Vector2d(-e.Y, e.X);
            yield return x.C + e * a + nrm * h; yield return x.C + e * a - nrm * h;
            yield break;
        }
        var ln = x.Arc ? y : x; var ci = x.Arc ? x : y;
        var f = ln.P - ci.C; double B = 2 * Vector2d.Dot(f, ln.Dir), C2 = f.LengthSquared - ci.R * ci.R;
        double disc = B * B - 4 * C2;
        if (disc < 0) { yield return ln.P + ln.Dir * (-B / 2); yield break; }     // касание с недобором — ближайшая точка
        double sq = Math.Sqrt(disc);
        foreach (double t in new[] { (-B - sq) / 2, (-B + sq) / 2 })
        {
            var p = ln.P + ln.Dir * t; var v = p - ci.C; double vl = v.Length;
            yield return vl > 0 ? ci.C + v * (ci.R / vl) : p;
        }
    }

    static double Norm(double a) { a %= 2 * Math.PI; if (a < 0) a += 2 * Math.PI; return a; }
}
