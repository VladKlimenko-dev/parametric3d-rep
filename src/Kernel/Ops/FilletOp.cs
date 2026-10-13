using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// Скругления и фаски по рёбрам — СОБСТВЕННЫМИ средствами, без ядра.
///
/// СПОСОБ: СЕЧЕНИЕ, ПРИЗМА, ВЫЧИТАНИЕ.
///
/// Прямое ребро между двумя плоскими гранями выглядит в сечении поперёк
/// себя как угол из двух лучей. Снимаемый материал — плоская фигурка
/// в этом сечении: у фаски треугольник, у скругления уголок между двумя
/// касательными и дугой. Фигурка выдавливается вдоль ребра и вычитается.
///
/// Так операция сводится к тому, что уже есть и уже проверяется: профиль,
/// выдавливание, булева. Отдельной топологической хирургии с обрезкой
/// смежных граней и «пробками» в вершинах не нужно, и весь риск собран
/// в одном месте — в булевой, у которой теперь есть независимая сверка.
///
/// ЧЕМ ЭТО ХУЖЕ НАСТОЯЩЕГО СКРУГЛЕНИЯ. Дуга сечения уходит в булеву
/// многоугольником, поэтому поверхность скругления выходит ГРАНЁНОЙ:
/// вместо цилиндра получается набор узких плоских полосок. Фаска этим
/// не страдает — она и есть плоскость, и остаётся точной.
///
/// Скругления соседних рёбер, сходящихся в вершине, каждое срезают свой
/// уголок, но сферической пробки в самой вершине не появляется. Для отладки
/// алгоритмов этого хватает; для чертежа — нет.
///
/// ЧЕГО ЗДЕСЬ НЕТ НАМЕРЕННО. Кривые рёбра, неплоские грани и вогнутые рёбра
/// не обрабатываются, и это говорится вслух, а не обходится молча. Скругление
/// вогнутого ребра материал ДОБАВЛЯЕТ, и снимаемая фигурка для него другая;
/// сделать вид, что разницы нет, значило бы тихо съесть кусок детали.
/// </summary>
public static class FilletOp
{
    /// <summary>
    /// ДИАГНОСТИКА: резец, которым операция только что успешно резала.
    ///
    /// Нужна проверочному прогону (Parametric3D.Verify): результат скругления
    /// или фаски сверяется с исходным телом точка за точкой — «было внутри
    /// и не попало в резец» должно совпасть с «внутри после». Без доступа
    /// к резцам проверить это нельзя. В приложении не подписан никто.
    /// </summary>
    public static Action<TSolid>? AppliedTool;

    /// <summary>ДИАГНОСТИКА: каждый построенный резец — до вычитания, в том числе откаченный.</summary>
    public static Action<TSolid>? BuiltTool;

    /// <summary>ДИАГНОСТИКА: прибавка у вогнутого ребра, которую операция только что успешно прибавила.</summary>
    public static Action<TSolid>? AppliedAddition;

    /// <summary>
    /// Насколько призма выступает за концы ребра.
    ///
    /// Инструмент, обрезанный ровно по длине ребра, совпадает торцами
    /// с гранями тела. Совпадающие плоскости — худший вход для разбиения
    /// пространства: принадлежность полигона решается знаком величины,
    /// которая равна нулю. Свес уводит торцы от этих плоскостей.
    /// </summary>
    private const double Overhang = 1e-3;

    /// <summary>
    /// НАСКОЛЬКО ГРАНИ ДОЛЖНЫ РАЗОЙТИСЬ, ЧТОБЫ СТЫК СЧИТАЛСЯ РЕБРОМ, градусы.
    ///
    /// Гранёное скругление состоит из узких полосок, и соседние полоски
    /// сходятся почти в одну плоскость: при делении прямого угла на 16
    /// частей стык между ними — 174.4°. Формально это ребро, и прежняя
    /// проверка «грани не касательны» его пропускала: она отсекала только
    /// стыки площе 0.081°.
    ///
    /// А фаска на таком стыке — не фаска. Направления «вдоль грани» у двух
    /// почти совпадающих плоскостей смотрят почти в РАЗНЫЕ СТОРОНЫ, и
    /// треугольник среза вырождается в полосу во всю ширину двух катетов:
    /// при d = 10 это срез шириной 20, идущий поперёк всего скругления.
    /// Отсюда и взялся завиток на снимке — это не ошибка булевой, это
    /// честно вычтенная бессмысленная фигура.
    ///
    /// Десять градусов: скругление пришлось бы дробить на 18 полосок
    /// и мельче, чтобы его стыки попали под порог, а настоящие рёбра
    /// деталей столь пологими не бывают.
    /// </summary>
    private const double MinTurnDegrees = 10.0;

    public static void RoundEdges(List<TSolid> bodies, IReadOnlyList<EdgeRef> edges,
                                  double radius, List<string> warnings)
    {
        // KRN-06: прямой построитель (плоскость/плоскость) — без резцов и булевых; не поддержано — прежний путь
        if (DirectRound.TryRound(bodies, edges, radius, warnings)) return;
        var corners = BallCorners(bodies, edges, radius);
        Apply(bodies, edges, warnings, "Round", radius, radius, true);
        if (corners.Count > 0) BlendCorners(bodies, corners, radius, warnings);
    }

    /// <summary>
    /// K09. ВЕРШИННЫЙ ПЕРЕХОД КАТЯЩИМСЯ ШАРОМ. Три скруглённых выпуклых ребра одного радиуса,
    /// сходящиеся в вершине трёх взаимно перпендикулярных плоских граней, прежде давали угол
    /// пересечения трёх цилиндров (три шва-излома; материала на 1.68 мм³ больше шара при r = 3).
    /// Настоящий переход — восьмая часть шара с центром в r от всех трёх граней; лишнее
    /// снимается вычитанием «куб угла минус шар». Шар касается цилиндров скруглений по их
    /// торцевым дугам, поверхность гладкая.
    /// </summary>
    private static List<(Vector3d P, Vector3d N1, Vector3d N2, Vector3d N3)> BallCorners(List<TSolid> bodies, IReadOnlyList<EdgeRef> edges, double r)
    {
        var res = new List<(Vector3d, Vector3d, Vector3d, Vector3d)>();
        var chosen = edges.Select(e => Resolve(bodies, e)).OfType<TEdge>().ToHashSet();
        if (chosen.Count < 3) return res;
        var verts = chosen.SelectMany(e => new[] { e.V0, e.V1 }).Distinct();
        foreach (var v in verts)
        {
            var incident = chosen.Where(e => e.V0 == v || e.V1 == v).ToList();
            if (incident.Count != 3 || incident.Any(e => e.Curve is not LineCurve3)) continue;
            var faces = incident.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (faces.Count != 3) continue;
            var normals = new List<Vector3d>();
            foreach (var f in faces)
            {
                var src = f.Surface is Surfaces.FacetedSurface fs ? fs.Source : f.Surface;
                if (src == null || !src.IsPlanar) { normals.Clear(); break; }
                normals.Add(f.NormalAt(v.P).Normalized());
            }
            if (normals.Count != 3) continue;
            // K09: любой выпуклый угол трёх плоских граней (не только прямой) — нормали должны быть
            // линейно независимы, тогда центр шара однозначен.
            double triple = Vector3d.Dot(normals[0], Vector3d.Cross(normals[1], normals[2]));
            if (Math.Abs(triple) < 1e-6) continue;
            // ПОКА ТОЛЬКО ПРЯМОЙ УГОЛ. У непрямого угла (треугольная призма, 60°) шар касается граней
            // в точках, которые не совпадают с вершинами гранения шара-резца: гранёный шар лежит
            // чуть ниже грани, и вычитание оставляло плёнку-осколок у точки касания (сетка
            // открывалась, точный объём недоступен). Нужен резец, касающийся граней точно.
            // Шар-резец строится сектором вращения так, что все три точки касания — его вершины
            // (см. BallSector). НО у непрямого угла (призма, 60°) сетка всё ещё открывается у торцов
            // цилиндров скруглений (деление дуги сектора и цилиндра не совпадает), и точный объём
            // недоступен — поэтому пока только прямой угол: там результат точен (2e-8).
            bool orthogonal = Math.Abs(Vector3d.Dot(normals[0], normals[1])) < 1e-9 && Math.Abs(Vector3d.Dot(normals[1], normals[2])) < 1e-9
                           && Math.Abs(Vector3d.Dot(normals[0], normals[2])) < 1e-9;
            if (!orthogonal) continue;
            // Выпуклый угол: центр шара c = P − r Σn лежит внутри тела (проверка по треугольникам
            // не нужна: у прямоугольного угла выпуклость равна тому, что рёбра выпуклые).
            if (incident.Any(e => !IsConvex(e))) continue;
            res.Add((v.P, normals[0], normals[1], normals[2]));
        }
        return res;
    }

    private static bool IsConvex(TEdge e)
    {
        var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
        if (fs.Count != 2) return false;
        var mid = (e.V0.P + e.V1.P) / 2;
        var na = fs[0].NormalAt(mid).Normalized(); var nb = fs[1].NormalAt(mid).Normalized();
        // Точка грани A, отступившая от ребра внутрь неё, лежит позади грани B у выпуклого ребра.
        var ca = fs[0].Edges.SelectMany(x => new[] { x.V0.P, x.V1.P }).Aggregate(Vector3d.Zero, (s, q) => s + q) / (2 * fs[0].Edges.Count());
        return Vector3d.Dot(ca - mid, nb) < 0;
    }

    private static void BlendCorners(List<TSolid> bodies, List<(Vector3d P, Vector3d N1, Vector3d N2, Vector3d N3)> corners, double r, List<string> warnings)
    {
        int done = 0;
        foreach (var (p, n1, n2, n3) in corners)
        {
            var ns = new[] { n1, n2, n3 };
            // Центр шара: на расстоянии r внутрь от всех трёх плоскостей: n_i·(c − P) = −r.
            if (!Solve3(ns, new[] { -r, -r, -r }, out var dc)) continue;
            var c = p + dc;
            // Область угла: трёхгранный клин с вершиной c, ограниченный плоскостями торцов трёх
            // цилиндров скруглений (через c, поперёк оси каждого ребра), и самими гранями
            // (чуть наружу). В ней материал должен быть ровно шаром; лишнее — снимается.
            var halfspaces = new List<(Vector3d At, Vector3d Inward)>();
            foreach (var (i, j) in new[] { (0, 1), (1, 2), (0, 2) })
            {
                var axis = Vector3d.Cross(ns[i], ns[j]).Normalized();
                if (Vector3d.Dot(p - c, axis) < 0) axis = -axis;
                halfspaces.Add((c, axis));
            }
            double eps = r * 0.02;
            foreach (var nn in ns) halfspaces.Add((p + nn * eps, -nn));
            TSolid? wedge = HalfspaceCell(c, p, r, halfspaces, warnings);
            if (wedge == null) continue;
            var ball = BallSector(c, r, ns);
            if (ball == null) continue;
            var tool = new List<TSolid> { wedge };
            BooleanOp.Subtract(tool, ball, warnings);
            if (tool.Count == 0) continue;
            var before = bodies.ToList();
            try { BooleanOp.Subtract(bodies, tool[0], warnings); done++; }
            catch (OperationFailedException ex) { bodies.Clear(); bodies.AddRange(before); warnings.Add("Round: a corner blend was skipped: " + ex.Message); }
        }
        if (done > 0) warnings.Add($"Round: {done} corner(s) blended with a rolling ball.");
    }

    /// <summary>
    /// K09. ШАР УГЛА, КАСАЮЩИЙСЯ ГРАНЕЙ ТОЧНО. Гранёный шар лежит внутри настоящего и касается
    /// плоскости только своей вершиной; если точка касания попадает между вершинами гранения,
    /// вычитание «клин минус шар» оставляло у неё плёнку-осколок (призма, угол 60°). Здесь берётся
    /// сектор шара, полученный вращением: ось — к точке касания третьей грани (она — полюс),
    /// поворот — от меридиана через точку касания первой грани до меридиана второй (их полярные
    /// углы — узлы образующей). Все три точки касания — вершины резца. Плоскости меридианов —
    /// это и есть торцы цилиндров скруглений первой и второй граней с третьей, клин угла внутри.
    /// </summary>
    private static TSolid? BallSector(Vector3d c, double r, Vector3d[] ns)
    {
        // Полюс — касание той грани, что ближе всех к перпендикуляру к двум другим: тогда торец
        // цилиндра скругления между двумя другими гранями — экватор сектора, и его деление
        // совпадает с делением дуги самого цилиндра (иначе их точки не сходились у торца).
        int k0 = Enumerable.Range(0, 3).OrderBy(k => Math.Abs(Vector3d.Dot(ns[k], ns[(k + 1) % 3])) + Math.Abs(Vector3d.Dot(ns[k], ns[(k + 2) % 3]))).First();
        ns = new[] { ns[k0], ns[(k0 + 1) % 3], ns[(k0 + 2) % 3] };
        var w = ns[0].Normalized();
        var f1 = ns[1].Normalized(); var f2 = ns[2].Normalized();
        var u = (f1 - w * Vector3d.Dot(f1, w));
        if (u.LengthSquared < 1e-18) return null;
        u = u.Normalized();
        var v = Vector3d.Cross(w, u);
        double t1 = Math.Acos(Math.Clamp(Vector3d.Dot(f1, w), -1, 1)), t2 = Math.Acos(Math.Clamp(Vector3d.Dot(f2, w), -1, 1));
        double phi = Math.Atan2(Vector3d.Dot(f2, v), Vector3d.Dot(f2, u));
        if (phi < 0) { v = -v; phi = -phi; }
        if (phi < 1e-6) return null;
        double tMax = Math.Min(Math.PI - 1e-3, Math.Max(t1, t2) + 0.35);
        // Образующая в полуплоскости меридиана (u — от оси, w — вдоль оси), узлы — t1 и t2.
        var plane = new Frame3(c, u, w);
        var knots = new SortedSet<double> { 0, tMax };
        foreach (var t in new[] { t1, t2 }) if (t > 1e-6 && t < tMax - 1e-6) knots.Add(t);
        var ks = knots.ToList();
        var curves = new List<ICurve3> { new LineCurve3(c, c + w * r) };
        var meridian = new Frame3(c, w, u);   // угол отсчитывается от оси w к u
        for (int i = 0; i + 1 < ks.Count; i++)
            curves.Add(new ArcCurve3(meridian, r, ks[i], ks[i + 1]));
        var end = c + r * (Math.Cos(tMax) * w + Math.Sin(tMax) * u);
        curves.Add(new LineCurve3(end, c));
        var profile = ProfileBuilder.Build(plane, curves);
        if (profile.IsEmpty) return null;
        return RevolveOp.Build(profile, new Frame3(c, u, v), 0, phi);
    }

    /// <summary>Выпуклая ячейка — пересечение полупространств, обрезанное кубом вокруг угла.</summary>
    internal static TSolid? HalfspaceCell(Vector3d c, Vector3d p, double r, List<(Vector3d At, Vector3d Inward)> halfspaces, List<string> warnings)
    {
        double size = 8 * r + (p - c).Length * 4;
        var centre = (p + c) / 2;
        var cell = new List<TSolid> { CubeAt(centre, size) };
        foreach (var (at, inward) in halfspaces)
        {
            var w = inward.Normalized();
            var frame = Frame3.FromNormal(at, w);
            double big = size * 4;
            var sq = new List<ICurve3>
            {
                new LineCurve3(frame.ToWorld(new Vector2d(-big, -big)), frame.ToWorld(new Vector2d(big, -big))),
                new LineCurve3(frame.ToWorld(new Vector2d(big, -big)), frame.ToWorld(new Vector2d(big, big))),
                new LineCurve3(frame.ToWorld(new Vector2d(big, big)), frame.ToWorld(new Vector2d(-big, big))),
                new LineCurve3(frame.ToWorld(new Vector2d(-big, big)), frame.ToWorld(new Vector2d(-big, -big)))
            };
            var slab = ExtrudeOp.Build(ProfileBuilder.Build(frame, sq), w * big);
            try { BooleanOp.Intersect(cell, slab, warnings); }
            catch (OperationFailedException) { return null; }
            if (cell.Count == 0) return null;
        }
        return cell.Count == 1 ? cell[0] : null;
    }

    private static TSolid CubeAt(Vector3d centre, double size)
    {
        var o = centre - new Vector3d(size / 2);
        var f = new Frame3(o, Vector3d.UnitX, Vector3d.UnitY);
        var sq = new List<ICurve3>
        {
            new LineCurve3(o, o + Vector3d.UnitX * size), new LineCurve3(o + Vector3d.UnitX * size, o + (Vector3d.UnitX + Vector3d.UnitY) * size),
            new LineCurve3(o + (Vector3d.UnitX + Vector3d.UnitY) * size, o + Vector3d.UnitY * size), new LineCurve3(o + Vector3d.UnitY * size, o)
        };
        return ExtrudeOp.Build(ProfileBuilder.Build(f, sq), Vector3d.UnitZ * size);
    }

    private static bool Solve3(Vector3d[] n, double[] rhs, out Vector3d x)
    {
        double det = Vector3d.Dot(n[0], Vector3d.Cross(n[1], n[2]));
        x = Vector3d.Zero;
        if (Math.Abs(det) < 1e-12) return false;
        x = (rhs[0] * Vector3d.Cross(n[1], n[2]) + rhs[1] * Vector3d.Cross(n[2], n[0]) + rhs[2] * Vector3d.Cross(n[0], n[1])) / det;
        return true;
    }

    /// <summary>
    /// СКРУГЛЕНИЕ С ПЕРЕМЕННЫМ РАДИУСОМ: радиус меняется вдоль ребра от r0 к r1 по прямой.
    /// Нужно там, где скругление должно сойти на нет или расшириться к краю — постоянный
    /// радиус этого не умеет.
    /// </summary>
    public static void RoundEdgesVariable(List<TSolid> bodies, IReadOnlyList<EdgeRef> edges,
                                          double r0, double r1, List<string> warnings)
    {
        if (r0 <= Tol.Weld && r1 <= Tol.Weld)
        {
            warnings.Add("Round: both radii are zero.");
            return;
        }

        // KRN-06 (06.14): одно прямое ребро между плоскостями — точный конус прямым построителем
        if (DirectRound.TryRoundVariable(bodies, edges, r0, r1, warnings)) return;
        VariableLaw = t => r0 + (r1 - r0) * Math.Clamp(t, 0, 1);
        try { Apply(bodies, edges, warnings, "Round", Math.Max(r0, r1), Math.Max(r0, r1), true); }
        finally { VariableLaw = null; }
    }

    /// <summary>ДИАГНОСТИКА: каким путём строится резец.</summary>
    public static Action<string>? PathDebug;

    /// <summary>Закон изменения радиуса вдоль цепочки; null — радиус постоянный.</summary>
    [ThreadStatic] private static Func<double, double>? VariableLaw;

    public static void ChamferEdges(List<TSolid> bodies, IReadOnlyList<EdgeRef> edges,
                                    double d1, double d2, List<string> warnings)
    {
        if (DirectRound.TryChamfer(bodies, edges, d1, d2, warnings)) return;
        Apply(bodies, edges, warnings, "Chamfer", d1, d2, false);
    }

    /// <summary>
    /// РЁБРА ОБРАБАТЫВАЮТСЯ ПО ОДНОМУ, И ССЫЛКА СОПОСТАВЛЯЕТСЯ ЗАНОВО.
    ///
    /// Каждое вычитание строит новое тело с новыми номерами, а заодно
    /// укорачивает соседние рёбра: фаска вертикального ребра куба обрезает
    /// верхнее. Искать все рёбра разом, до первой операции, значит держать
    /// ссылки на то, чего уже нет.
    /// </summary>
    static readonly bool EstimateOff = Environment.GetEnvironmentVariable("P3D_BLENDESTIMATE") == "0";
    /// <summary>Проверка KRN06: множитель размера протянутого резца (внесённая ошибка построения резца).</summary>
    [ThreadStatic] public static double ToolScaleForTest;
    static double ToolScale => ToolScaleForTest == 0 ? 1 : ToolScaleForTest;
    /// <summary>Разбор: изменение объёма обходным путём и оценка по сечениям (последний вызов).</summary>
    [ThreadStatic] public static (double Changed, double Estimate)? LastEstimate;

    /// <summary>
    /// Оценка объёма полос скругления/фаски по сечениям: Σ по рёбрам ∫ A(θ(s)) ds, θ — двугранный угол материала (у вогнутого
    /// ребра — пустоты) между гранями ребра по их наружным нормалям: скругление A = r²(ctg(θ/2) − (π − θ)/2), фаска с равными
    /// катетами A = d²·sin θ / 2 (неравные — d1·d2·sin θ / 2). Без поправок на кривизну ребра и граней (порядка r/R) — поэтому
    /// сверка грубая (втрое), но независимая от построения резца.
    /// </summary>
    public static double EstimateBlendVolume(IReadOnlyList<TEdge> edges, double s1, double s2, bool round)
    {
        double total = 0;
        foreach (var e in edges)
        {
            var fs = e.CoEdges.Select(c => c.Loop?.Face).OfType<TFace>().Distinct().ToList();
            if (fs.Count != 2) return double.NaN;
            const int K = 48;
            var c = e.Curve; Vector3d prev = c.PointAt(c.T0);
            for (int i = 1; i <= K; i++)
            {
                double t = c.T0 + (c.T1 - c.T0) * i / K, tm = c.T0 + (c.T1 - c.T0) * (i - 0.5) / K;
                var p = c.PointAt(t); var m = c.PointAt(tm);
                double ds = (p - prev).Length; prev = p;
                var n1 = fs[0].NormalAt(m).Normalized(); var n2 = fs[1].NormalAt(m).Normalized();
                double phi = Math.Acos(Math.Clamp(Vector3d.Dot(n1, n2), -1, 1));
                double th = Math.PI - phi;
                if (th <= 1e-6 || th >= Math.PI - 1e-6) continue;
                double a = round ? s1 * s1 * (1 / Math.Tan(th / 2) - (Math.PI - th) / 2) : s1 * s2 * Math.Sin(th) / 2;
                total += a * ds;
            }
        }
        return total;
    }

    /// <summary>KRN-01 (01.11): выбранные рёбра — устойчивыми именами (для диагностики отказа).</summary>
    static List<string> EdgeNames(List<TSolid> bodies, IReadOnlyList<EdgeRef> edges) =>
        Parametric3D.Kernel.OperationContract.DiagEntitiesOff ? new List<string>()
        : edges.Select(e => Resolve(bodies, e, out _)).OfType<TEdge>().Select(EntityNames.Edge).Distinct().ToList();

    private static void Apply(List<TSolid> bodies, IReadOnlyList<EdgeRef> edges,
                              List<string> warnings, string what,
                              double size1, double size2, bool round)
    {
        if (bodies.Count == 0)
        {
            warnings.Add($"{what}: there is no body yet.");
            return;
        }

        // ВСЕ РЕЗЦЫ СТРОЯТСЯ ПО ИСХОДНОМУ ТЕЛУ, И ТОЛЬКО ПОТОМ ВЫЧИТАЮТСЯ.
        //
        // ИСПРАВЛЕНО. Раньше рёбра обрабатывались по очереди: найти, построить
        // резец, вычесть — и так по кругу. На цепочке это разваливалось,
        // и не из-за поиска ребра, а по существу.
        //
        // Фаска прямого ребра сверху срезает с грани полосу шириной d.
        // Дуга скругления лежит в той же грани, у самого угла, и ЦЕЛИКОМ
        // попадает в эту полосу — вместе с полосой от соседнего ребра.
        // К моменту, когда очередь доходила до дуги, дуги на теле уже
        // не было, и сообщение «ребро больше не существует» было
        // совершенно верным. Чинить поиск было бессмысленно: искать нечего.
        //
        // Теперь резцы строятся все сразу, по ОДНОМУ И ТОМУ ЖЕ телу,
        // и ни один из них не успевает съесть кромку соседа. Порядок
        // вычитания после этого безразличен: пересечение областей
        // на результат не влияет, вычитание объединения равно
        // последовательному вычитанию частей.
        var jobs = new List<(EdgeRef Ref, TSolid Tool, string Kind, double Weight, bool Adds, int Count)>();

        // Сначала все рёбра находятся на исходном теле.
        var found = new List<(EdgeRef Ref, TEdge Edge)>();

        var whole = WholeNamedEdges(bodies, edges);
        foreach (var wanted in edges)
        {
            TEdge? edge = Resolve(bodies, wanted, out string? conflict, out var amb);
            if (edge == null && whole.TryGetValue(wanted, out var w)) { edge = w; conflict = null; amb = null; }

            // K05: неоднозначная или потерянная именованная ссылка — отказ операции с
            // объяснением, а не скругление другой кромки и не молчаливый пропуск.
            // KRN-08: несколько кусков ребра с тем же именем — исход Ambiguous с кандидатами (запрос выбора), а не
            // «некорректный вход»: ссылка верна, но неоднозначна. Мутация P3D_REFAMBIGUITY=0.
            if (conflict != null && amb is { Count: > 1 } && !RefAmbiguityOff)
                throw new AmbiguousReferenceException(what, amb.Select(e => (e.Tag, e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2))).ToList(), "edge reference conflict — " + conflict);
            if (conflict != null)
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"{what}: edge reference conflict — {conflict}.");

            if (edge == null)
            {
                // Приписка про этап не украшение: она говорит, что поиск
                // шёл по ИСХОДНОМУ телу, до единого реза. Без неё строку
                // не отличить от прежней, где ребро и правда успевал
                // съесть соседний рез.
                warnings.Add($"{what}: edge {Show(wanted.Start)}-{Show(wanted.End)} " +
                             "was not found on the body (searched before any cut).");
                continue;
            }

            found.Add((wanted, edge));
        }

        // КОНЦЫ, КОТОРЫЕ ДЕЛИТ ДРУГОЕ РЕБРО ТОЙ ЖЕ ОПЕРАЦИИ.
        //
        // Резец продлевается за конец ребра, чтобы пройти угол насквозь, —
        // когда угол принадлежит чужой, уже готовой операции. Если же в том же
        // конце начинается другое ребро этой же операции (дуга в цепочке
        // «ребро — дуга — ребро»), угол обработает его собственный резец:
        // над дугой это конус. Продлённая прямая фаска срезала бы угол косо
        // и оставила бы поперечное ребро между лентами.
        //
        // Считаются только рёбра, чей резец ДЕЙСТВИТЕЛЬНО построился: если дуга
        // откажет, прямым надо по-прежнему проходить угол насквозь. Поэтому
        // проход повторяется, пока набор построившихся рёбер не перестанет
        // меняться (обычно — один повтор, если кто-то отказал).
        var built = new Dictionary<int, (TSolid Tool, string? Refusal, bool Adds)>();
        // Стороны несимметричной фаски — по ходу цепочки (см. ChamferSides).
        bool[] swap = !round && Math.Abs(size1 - size2) > Tol.Weld
            ? ChamferSides(found.Select(f => f.Edge).ToList())
            : new bool[found.Count];

        var alive = Enumerable.Range(0, found.Count).ToHashSet();

        // ПРОТЯНУТЫЕ ЦЕПОЧКИ. Если в связной цепочке фаски есть ребро, которое не берёт
        // ни один специальный построитель (кривая пересечения, прямая или дуга между двумя
        // кривыми гранями), вся цепочка режется ОДНИМ резцом, протянутым вдоль неё
        // (BuildSweptChamfer): без ступенек и плёнок на стыках разных построителей.
        var swept = new HashSet<int>();
        if (true)   // фаски и скругления: протягивание по цепочкам, которые не берут специальные построители
        {
            foreach (var comp in SmoothPieces(found.Select(f => f.Edge).ToList()))
            {
                // Протягивание — если в цепочке есть ребро, которое не берёт ни один
                // специальный построитель, ИЛИ это гладкая цепочка из дуг и прямых.
                //
                // ИСПРАВЛЕНО: конус дуги и призма прямой, перекрытые на стыке на микрон,
                // оставляли на новой кромке фаски рёбра-огрызки длиной 0.001 мм, и рост
                // касательной цепочки по этой кромке обрывался. Один протянутый резец
                // стыков не имеет — кромка фаски выходит одной непрерывной линией.
                var compEdges = comp.Select(k => found[k].Edge).ToList();
                bool mixedSmooth = !round && comp.Count >= 2 &&
                                   compEdges.Any(e => e.Curve is ArcCurve3) &&
                                   compEdges.Any(e => e.Curve is LineCurve3) &&
                                   SmoothChain(compEdges);
                // ПЕРЕМЕННЫЙ РАДИУС ТРЕБУЕТ ПРОТЯГИВАНИЯ. Резец по ребру строится одного
                // размера на всю длину, и закон изменения радиуса в него не заложить; поэтому
                // при заданном законе цепочка идёт протянутым резцом всегда, а не только когда
                // специальные построители не берут её.
                if (VariableLaw == null && !mixedSmooth && !comp.Any(k => NeedsSweep(found[k].Edge))) continue;

                TSolid? sweptTool;
                string? why;
                PathDebug?.Invoke($"резец: протянутый, звеньев {comp.Count}, закон {(VariableLaw != null ? "есть" : "нет")}");
                using (Diagnostics.Prof.Time("скругление: протянутый резец"))
                    sweptTool = BuildSweptChamfer(bodies, comp.Select(k => (found[k].Edge, swap[k])).ToList(),
                                                  size1 * ToolScale, (round ? size1 : size2) * ToolScale, out why, round, VariableLaw);
                if (sweptTool == null)
                {
                    warnings.Add($"{what}: {why}.");
                    continue;                                   // остаётся обычный путь по рёбрам
                }

                double w = Math.Abs(TotalVolume(new List<TSolid> { sweptTool }));
                jobs.Add((found[comp[0]].Ref, sweptTool, (round ? $"swept chain of {comp.Count} edge(s) rounded R{size1:0.###}" : $"swept chain of {comp.Count} edge(s) cut {size1:0.###}x{size2:0.###}"), w, false, comp.Count));
                swept.UnionWith(comp);
            }
            alive.ExceptWith(swept);
        }

        for (int pass = 0; pass < 4; pass++)
        {
            // 0 — конец свободен; 1 — его делит сосед того же вида (прямое с прямым,
            // дуга с дугой); 2 — сосед другого вида (дуга с прямым). Только в последнем
            // случае резцы перехлёстываются (см. ClaimedOverlap): у соседей одного вида
            // торцы совпадают и так, и перехлёст наложил бы почти совпадающие грани.
            int Claimed(int self, Vector3d p)
            {
                bool selfArc = found[self].Edge.Curve is ArcCurve3;
                int result = 0;
                foreach (int k in alive)
                {
                    if (k == self) continue;
                    if ((found[k].Edge.V0.P - p).LengthSquared >= 1e-12 &&
                        (found[k].Edge.V1.P - p).LengthSquared >= 1e-12) continue;
                    // Встык — только дуга с дугой: у соседних торов одной окружности
                    // перехлёст накладывал почти совпадающие гранёные поверхности. Прямые
                    // резцы лежат на одних и тех же плоскостях, перехлёст на микрон им
                    // безвреден и убирает щель между торцами, построенными от разных
                    // вершин (плёнка на стыке кусков одной прямой — нашёл прогон).
                    if (!(selfArc && found[k].Edge.Curve is ArcCurve3)) return 2;
                    result = 1;
                }
                return result;
            }

            built.Clear();
            var next = new HashSet<int>();

            for (int k = 0; k < found.Count; k++)
            {
                if (swept.Contains(k)) continue;
                int self = k;
                string? refusal;
                TSolid? tool;
                bool adds;
                PathDebug?.Invoke($"резец: по ребру, закон {(VariableLaw != null ? "есть" : "нет")}");
                using (Diagnostics.Prof.Time("скругление: построение резца"))
                    refusal = BuildTool(bodies, found[k].Edge,
                                        swap[k] ? size2 : size1, swap[k] ? size1 : size2, round,
                                        p => Claimed(self, p), out tool, out adds);
                built[k] = (tool!, refusal ?? (tool == null ? "the tool body could not be built" : null), adds);
                if (built[k].Refusal == null) next.Add(k);
            }

            if (next.SetEquals(alive)) break;
            alive = next;
        }

        for (int k = 0; k < found.Count; k++)
        {
            if (swept.Contains(k)) continue;
            var (wanted, edge) = found[k];
            var (tool, refusal, addsHere) = built[k];

            if (refusal != null)
            {
                warnings.Add($"{what}: {refusal}.");
                continue;
            }

            // В отчёт идут ЧИСЛА, а не только вид кромки. У дуги решают
            // радиус и катеты среза: если катет по плоской грани дотягивает
            // до оси, резать нечего, и отказ обязан сработать. Видеть эти
            // три числа в отчёте — единственный способ проверить, что он
            // срабатывает там, где должен.
            string kind = edge.Curve is ArcCurve3 onArc2
                ? round ? $"arc R{onArc2.Radius:0.###} rounded R{size1:0.###}"
                        : $"arc R{onArc2.Radius:0.###} cut {size1:0.###}x{size2:0.###}"
                : "straight";

            // Вес резца запоминается здесь же: он понадобится, чтобы
            // проверить рез. Вычитание не может снять больше материала,
            // чем весит сам резец, — это верно всегда и ни от чего
            // не зависит.
            if (addsHere) kind = "concave " + kind;
            double toolWeight;
            using (Diagnostics.Prof.Time("скругление: вес резца"))
                // Та же double-геометрия, что у Damage, не float-буфер для экрана.
                // На ступеньке 60000×40000×20 float давал вес 31426 вместо ~34701
                // и откатывал правильную прибавку даже после исправления вогнутости.
                toolWeight = Math.Abs(TotalVolume(new List<TSolid> { tool }));
            jobs.Add((wanted, tool, kind, toolWeight, addsHere, 1));
        }

        int done = 0;
        var kinds = new List<string>();

        double? knownVolume = null;
        // KRN-06 (06.16): независимая оценка объёма полос обходного пути (см. EstimateBlendVolume) — до первого реза
        double estimate = EstimateOff ? double.NaN : EstimateBlendVolume(found.Select(f => f.Edge).ToList(), size1, size2, round);
        double startVolume = TotalVolume(bodies);
        var original = new List<TSolid>(bodies);

        foreach (var (wanted, tool, kind, weight, adds, edgeCount) in jobs)
        {
            // СОСТОЯНИЕ ДО РЕЗА СОХРАНЯЕТСЯ ЦЕЛИКОМ.
            //
            // BooleanOp кладёт в список НОВОЕ тело, а прежнее не трогает,
            // поэтому копии самого списка достаточно, чтобы вернуться.
            var before = new List<TSolid>(bodies);
            // Объём «до» — это объём «после» предыдущего среза; считается один раз.
            double volumeBefore;
            using (Diagnostics.Prof.Time("скругление: объём до"))
                volumeBefore = knownVolume ??= TotalVolume(before);

            BuiltTool?.Invoke(tool);

            // У вогнутого ребра скругление и фаска ПРИБАВЛЯЮТ материал.
            if (adds) BooleanOp.Unite(bodies, tool, warnings);
            else BooleanOp.Subtract(bodies, tool, warnings);

            string? broke;
            double volumeAfter;
            using (Diagnostics.Prof.Time("скругление: защитная проверка"))
                broke = Damage(bodies, volumeBefore, weight, adds, out volumeAfter);
            if (broke == null) knownVolume = volumeAfter;

            if (broke != null)
            {
                // ОТКАТ ВМЕСТО ПОРЧИ.
                //
                // Булева, споткнувшись на совпадающих плоскостях, возвращает
                // не отказ, а тело — с необрезанными кусками прежних граней,
                // вывернутыми нормалями и висящими рёбрами. Дальше такое тело
                // идёт в следующий рез и портится ещё сильнее, а виноватым
                // выглядит последний шаг.
                //
                // Здесь результат проверяется сразу и, если он негоден,
                // откатывается: лучше кромка без фаски, чем деталь,
                // которой нельзя верить.
                bodies.Clear();
                bodies.AddRange(before);

                warnings.Add($"{what}: the cut on edge {Show(wanted.Start)}-{Show(wanted.End)} " +
                             $"was rolled back — {broke}.");
                continue;
            }

            if (bodies.Count == 0)
            {
                warnings.Add($"{what}: the cut removed the whole body — check the size.");
                return;
            }

            kinds.Add(kind);
            done += edgeCount;   // протянутый резец режет всю свою цепочку

            if (adds) AppliedAddition?.Invoke(tool);
            else AppliedTool?.Invoke(tool);
        }

        // KRN-06 (06.16): ОБХОДНОЙ ПУТЬ СВЕРЯЕТСЯ С НЕЗАВИСИМОЙ ОЦЕНКОЙ. Протянутый резец по кривой пересечения (кромка
        // поперечного отверстия в валу, r = 1) снимал 90.7 мм³ при эталоне OpenCascade 7.75 — в 12 раз больше, и результат
        // принимался (проверка «не больше веса резца» не ловит неверный резец). Изменение объёма сверяется с оценкой
        // ∫ A(θ) ds по двугранному углу вдоль рёбер; расхождение больше чем втрое — откат и отказ (результату нельзя верить).
        // Мутация P3D_BLENDESTIMATE=0.
        if (!EstimateOff && done > 0 && double.IsFinite(estimate) && estimate > 0)
        {
            double changed = Math.Abs(TotalVolume(bodies) - startVolume);
            LastEstimate = (changed, estimate);
            if (changed > 3 * estimate + 1e-9 || changed < estimate / 3 - 1e-9)
            {
                bodies.Clear(); bodies.AddRange(original);
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported,
                    $"{what}: the result of the cutter route could not be trusted — it changed the volume by {changed:G4} mm³ where the blend " +
                    $"cross-sections along the edges give about {estimate:G4} mm³") { Entities = EdgeNames(bodies, edges) };
            }
        }

        // KRN-06 (06.13, g.02): ИТОГ ОБХОДНОГО ПУТИ — ПОЛНАЯ ПРОВЕРКА (B-rep, точная форма, сетка показа). Угол из четырёх
        // скруглений без общего шара (пирамида с разными уклонами граней) давал микрорёбра 1e-6 мм у основания и сетку с
        // рёбрами кратности 4 — тело принималось (защитная проверка смотрит объём и открытые рёбра своей разметки).
        // Негодный итог — откат и отказ. Мутация P3D_FILLETFINALCHECK=0.
        if (!FinalCheckOff && done > 0)
            foreach (var b in bodies.Where(x => !original.Any(o => ReferenceEquals(o, x))))
            {
                ValidationReport vr;
                using (Diagnostics.Prof.Time("скругление: итоговая проверка")) vr = SolidValidator.Validate(b);
                if (vr.IsValid) continue;
                var bad = vr.Issues.First(i => i.Severity == ValidationSeverity.Error);
                bodies.Clear(); bodies.AddRange(original);
                Interlocked.Increment(ref FinalCheckRefusals);
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported,
                    $"{what}: the result of the cutter route failed validation ({bad}) — not applied") { Entities = EdgeNames(bodies, edges) };
            }

        if (done == 0 && edges.Count > 0)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, $"{what}: none of the selected edges could be processed.")
            { Entities = EdgeNames(bodies, edges) };

        // ОТЧЁТ ОБЯЗАТЕЛЕН, А НЕ ПО СЛУЧАЮ.
        //
        // Молчание при полном успехе выглядит точно так же, как молчание
        // при том, что в операцию пришло одно ребро вместо трёх: на экране
        // фаска на одном ребре и пустое окно. Разбираться в этих двух
        // случаях надо в разных местах — в операции и в выборе, — поэтому
        // число пришедших рёбер называется ВСЕГДА.
        //
        // Заодно перечисляются виды обработанных кромок: прямая и дуга
        // режутся по-разному (призма против тела вращения), и знать,
        // какой путь отработал, важнее, чем беречь строку в списке.
        // ОБЪЁМ ТЕЛА НАЗЫВАЕТСЯ ПРЯМО.
        //
        // Прошлый круг показал, что обсуждать «сняло больше, чем весит
        // резец» вслепую нельзя: неизвестно, верен ли сам отсчёт. Объём
        // куба со скруглением R10 обязан быть 997842 с точностью
        // до гранения дуги. Если после скругления в отчёте стоит заметно
        // меньше, оболочка рвётся уже там, и фаска тут ни при чём.
        warnings.Add($"{what}: {edges.Count} edge(s) requested, {done} processed" +
                     (kinds.Count > 0 ? $" ({string.Join(", ", kinds)})" : "") +
                     $"; body volume now {TotalVolume(bodies):0.###}, " +
                     $"{OpenEdges(bodies)}.");
    }

    /// <summary>
    /// ЧТО СЧИТАЕТСЯ ИСПОРЧЕННЫМ ТЕЛОМ. Возвращает описание беды либо null.
    ///
    /// Проверки нарочно грубые и дешёвые: они должны ловить обвал, а не
    /// мерить качество. Тонкую неточность поймает сверка с объёмной моделью,
    /// а здесь ловится то, после чего продолжать нельзя.
    /// </summary>
    static readonly bool FinalCheckOff = Environment.GetEnvironmentVariable("P3D_FILLETFINALCHECK") == "0";
    /// <summary>Число отказов итоговой проверки обходного пути (разбор).</summary>
    public static long FinalCheckRefusals;

    private static string? Damage(List<TSolid> bodies, double volumeBefore, double toolWeight, bool adds, out double after)
    {
        after = volumeBefore;
        if (bodies.Count == 0) return null;   // «съело всё» разбирается отдельно

        // Одно разбиение на все проверки (см. SurfaceScan).
        var scan = new SurfaceScan(bodies);

        after = 0;
        for (int bi = 0; bi < bodies.Count; bi++)
        {
            if (scan.BodyEmpty[bi]) return "the result has no surface left";
            if (scan.BodyVolume[bi] <= 0) return "the result came out inside out (its faces point inward)";
            after += scan.BodyVolume[bi];
        }

        if (adds)
        {
            // Прибавка: объём растёт, но не больше, чем весит сама прибавка.
            double added = after - volumeBefore;
            if (added < -volumeBefore * 1e-6)
                return $"the volume shrank from {volumeBefore:0.###} to {after:0.###}, which an addition cannot do";
            if (added > toolWeight * 1.05 + 1e-6)
                return $"the addition put on {added:0.###} while it weighs only {toolWeight:0.###}; {scan.OpenEdgesText()}";
        }
        else if (after > volumeBefore * (1 + 1e-6))
            return $"the volume grew from {volumeBefore:0.###} to {after:0.###}, which a cut cannot do";

        double removed = volumeBefore - after;
        if (!adds && removed > toolWeight * 1.05 + 1e-6)
            return $"the cut removed {removed:0.###} while the tool itself weighs only {toolWeight:0.###}; {scan.OpenEdgesText()}";

        // Поверхность обязана остаться чистой: касание в точке, по линии, дыры.
        string? pinch = scan.PinchPoint();
        if (pinch != null)
            return $"the result touches itself at a point {pinch}";

        string edges = scan.OpenEdgesText();
        if (!edges.StartsWith("0 open edges"))
            return scan.TouchesOnly()
                ? $"the result touches itself along an edge (four faces meet there): {edges}"
                : $"the result is not a clean closed surface: {edges}";

        return null;
    }

    /// <summary>
    /// ОБЗОР ПОВЕРХНОСТИ — одно разбиение граней на треугольники для всех проверок.
    ///
    /// УСКОРЕНИЕ. Защитная проверка после среза состояла из четырёх частей — объём,
    /// открытые рёбра, касание по линии, касание в точке, — и каждая заново разбивала
    /// все грани тела. На скруглении по кругу отверстия это 0.67 с из 4.8. Теперь
    /// разбиение одно, а части читают из него.
    /// </summary>
    private sealed class SurfaceScan
    {
        public readonly List<Vector3d> Points = new();
        public readonly List<(int A, int B, int C, uint Face, int Body)> Tris = new();
        public readonly Dictionary<(int, int), List<uint>> Users = new();
        public readonly double[] BodyVolume;
        public readonly bool[] BodyEmpty;
        private readonly Dictionary<(double, double, double), int> _ids = new();

        public SurfaceScan(List<TSolid> bodies)
        {
            BodyVolume = new double[bodies.Count];
            BodyEmpty = new bool[bodies.Count];

            for (int bi = 0; bi < bodies.Count; bi++)
            {
                int before = Tris.Count;
                Vector3d? origin = null;
                double compensation = 0;
                foreach (TFace face in bodies[bi].Faces)
                {
                    var mb = new MeshBuilder();
                    // Треугольники разбиения, не видимая сетка: сравнение с точным телом.
                    // Защитной проверке нужны только положения: дешёвое гранение без гладких
                    // нормалей (они стоят проекции каждой вершины на точную поверхность).
                    try { if (face.Surface is FacetedSurface fsd) fsd.TessellateFacetsFlat(face, mb); else face.Surface.TessellateFace(face, mb); }
                    catch { continue; }
                    var md = new MeshData();
                    mb.FillInto(md);

                    for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                    {
                        Vector3d pa = mb.PositionsD[md.Indices[i]], pb = mb.PositionsD[md.Indices[i + 1]], pc = mb.PositionsD[md.Indices[i + 2]];
                        int p = Id(pa), q = Id(pb), r = Id(pc);
                        Tris.Add((p, q, r, face.Id, bi));
                        // Объём замкнутой оболочки не зависит от начала координат.
                        // Считаем относительно точки самого тела и компенсируем суммирование,
                        // чтобы малый резец вдали от (0,0,0) не терял значащие цифры.
                        origin ??= pa;
                        var local = origin.Value;
                        double term = Vector3d.Dot(pa - local, Vector3d.Cross(pb - local, pc - local)) / 6;
                        double y = term - compensation;
                        double sum = BodyVolume[bi] + y;
                        compensation = (sum - BodyVolume[bi]) - y;
                        BodyVolume[bi] = sum;
                        Add(p, q, face.Id); Add(q, r, face.Id); Add(r, p, face.Id);
                    }
                }
                BodyEmpty[bi] = Tris.Count == before;
            }
        }

        private int Id(Vector3d p)
        {
            var key = (p.X, p.Y, p.Z);
            if (_ids.TryGetValue(key, out int at)) return at;
            _ids[key] = at = _ids.Count;
            Points.Add(p);
            return at;
        }

        private void Add(int a, int b, uint face)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!Users.TryGetValue(key, out var list)) Users[key] = list = new List<uint>();
            list.Add(face);
        }

        public string OpenEdgesText()
        {
            int open = 0;
            var where = new List<string>();
            foreach (var pair in Users)
            {
                if (pair.Value.Count == 2) continue;
                open++;
                if (where.Count >= 3) continue;
                Vector3d u = Points[pair.Key.Item1], v = Points[pair.Key.Item2];
                where.Add($"({(u.X + v.X) / 2:0.##}, {(u.Y + v.Y) / 2:0.##}, {(u.Z + v.Z) / 2:0.##})" +
                          $" faces [{string.Join(",", pair.Value)}]");
            }
            return open == 0 ? "0 open edges" : $"{open} open edges, first at {string.Join("; ", where)}";
        }

        /// <summary>Все «неправильные» рёбра — касания (чётное число граней больше двух), а не дыры.</summary>
        public bool TouchesOnly() => Users.Values.All(l => l.Count == 2 || (l.Count > 2 && l.Count % 2 == 0));

        /// <summary>Вершина, вокруг которой треугольники составляют не один веер; иначе null.</summary>
        public string? PinchPoint()
        {
            var around = new Dictionary<int, List<int>>();
            for (int t = 0; t < Tris.Count; t++)
                foreach (int v in new[] { Tris[t].A, Tris[t].B, Tris[t].C })
                {
                    if (!around.TryGetValue(v, out var list)) around[v] = list = new List<int>();
                    list.Add(t);
                }

            foreach (var (v, list) in around)
            {
                var parent = new Dictionary<int, int>();
                foreach (int t in list) parent[t] = t;
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

                var byOther = new Dictionary<int, int>();
                foreach (int t in list)
                    foreach (int o in new[] { Tris[t].A, Tris[t].B, Tris[t].C })
                    {
                        if (o == v) continue;
                        if (byOther.TryGetValue(o, out int u)) parent[Find(t)] = Find(u);
                        else byOther[o] = t;
                    }

                if (list.Select(Find).Distinct().Count() > 1)
                {
                    Vector3d p = Points[v];
                    return $"({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})";
                }
            }
            return null;
        }
    }

    private static string OpenEdges(List<TSolid> bodies) => new SurfaceScan(bodies).OpenEdgesText();

    private static double TotalVolume(List<TSolid> bodies) => new SurfaceScan(bodies).BodyVolume.Sum();

    // ------------------------------------------------------- поиск ребра

    /// <summary>
    /// Ищет живое ребро по геометрии ссылки.
    ///
    /// Сначала по концам, потом — по опорной прямой с перекрытием: после
    /// соседней операции ребро остаётся тем же ребром, но становится короче,
    /// и совпадения концов уже нет.
    /// </summary>
    /// <summary>ДИАГНОСТИКА: подписи граней резца.</summary>
    public static Action<string>? TagDebug;

    /// <summary>
    /// K05. ПРИВЯЗКА ССЫЛКИ НА РЕБРО — БЕЗ МОЛЧАЛИВОЙ ПОДМЕНЫ.
    ///
    /// Ссылка с подписями граней (устойчивое имя) ищется ТОЛЬКО по подписям:
    ///   • одно ребро с этими подписями — оно;
    ///   • несколько (ребро разрезано) — то, на кривой которого лежит запомненная середина;
    ///     если таких нет или их несколько — КОНФЛИКТ: прежде бралось ближайшее по середине, и
    ///     ссылка могла уйти на соседний кусок;
    ///   • ни одного — КОНФЛИКТ «ребро исчезло». Прежде поиск продолжался по координатам и
    ///     находил другое ребро в том же месте (например, кромку, созданную позже).
    /// Поиск по координатам остаётся только для старых ссылок без подписей.
    /// </summary>
    private static TEdge? Resolve(List<TSolid> bodies, EdgeRef wanted) => Resolve(bodies, wanted, out _);

    static readonly bool MidFallbackOff = Environment.GetEnvironmentVariable("P3D_REFMIDFALLBACK") == "0";
    static readonly bool RefAmbiguityOff = Environment.GetEnvironmentVariable("P3D_REFAMBIGUITY") == "0";
    internal static TEdge? Resolve(List<TSolid> bodies, EdgeRef wanted, out string? conflict) => Resolve(bodies, wanted, out conflict, out _);
    internal static TEdge? Resolve(List<TSolid> bodies, EdgeRef wanted, out string? conflict, out List<TEdge>? ambiguous)
    {
        conflict = null; ambiguous = null;
        double tol = Tol.Weld * 100;

        if (wanted.FaceA != null && wanted.FaceB != null)
        {
            var named = new List<TEdge>();
            foreach (var body in bodies)
                foreach (var e in body.Edges)
                    if (wanted.NamesMatch(e)) named.Add(e);

            if (named.Count == 1) return named[0];
            // KRN-06: концы ребра изменены другой операцией (скругление соседнего ребра срезало вершину — её грани уже
            // другие): то же ребро — между теми же гранями и проходит через прежнюю середину. Мутация P3D_REFMIDFALLBACK=0.
            if (named.Count == 0 && !MidFallbackOff && wanted.Mid is { } m0)
            {
                double onTol0 = Math.Max(1e-6, Tol.Chord);
                var byFaces = bodies.SelectMany(b => b.Edges).Where(e => wanted.FacesMatch(e) && DistanceToCurve(e, m0) <= onTol0).ToList();
                if (byFaces.Count == 1) return byFaces[0];
            }
            if (named.Count == 0)
            {
                conflict = $"the edge between faces '{wanted.FaceA}' and '{wanted.FaceB}' no longer exists";
                return null;
            }

            if (wanted.Mid is not { } mid)
            {
                conflict = $"{named.Count} edges between faces '{wanted.FaceA}' and '{wanted.FaceB}' and no point to choose by";
                ambiguous = named;
                return null;
            }
            double onTol = Math.Max(1e-6, Tol.Chord);
            var holding = named.Where(e => DistanceToCurve(e, mid) <= onTol).ToList();
            if (holding.Count == 1) return holding[0];
            if (holding.Count > 1 && wanted.Piece != null)
            {
                var byPiece = holding.Where(e => e.Tag == wanted.Piece).ToList();
                if (byPiece.Count == 1) return byPiece[0];
            }
            conflict = holding.Count == 0
                ? $"the edge between faces '{wanted.FaceA}' and '{wanted.FaceB}' was split and no piece contains the referenced point"
                : $"{holding.Count} pieces of the edge between faces '{wanted.FaceA}' and '{wanted.FaceB}' contain the referenced point";
            ambiguous = holding.Count > 1 ? holding : named;
            return null;
        }

        // Среди рёбер с совпадающими концами — ближайшее по середине, если она известна.
        TEdge? best = null;
        double bestD = double.MaxValue;
        foreach (var body in bodies)
            foreach (var e in body.Edges)
            {
                if (!wanted.Matches(e.V0.P, e.V1.P, tol)) continue;
                if (wanted.Mid is not { } mid) return e;
                double d = (e.Curve.PointAt(0.5) - mid).LengthSquared;
                if (d < bestD) { bestD = d; best = e; }
            }
        if (best != null) return best;

        // ДУГА ИЩЕТСЯ ПО ОКРУЖНОСТИ, КАК ОТРЕЗОК — ПО ПРЯМОЙ.
        //
        // ДОБАВЛЕНО. Запасной поиск умел только отрезки, и кромка скругления
        // после соседнего реза теряла своё ребро: концы сдвинулись,
        // совпадения нет, а другого пути не было. Опорой для дуги служит
        // её окружность — центр, радиус и плоскость: они переживают
        // укорочение так же, как прямая переживает его у отрезка.
        TEdge? onArc = ResolveArc(bodies, wanted, tol);
        if (onArc != null) return onArc;

        Vector3d wd = wanted.End - wanted.Start;
        if (wd.Length < Tol.Weld) return null;

        Vector3d wdir = wd.Normalized();

        foreach (var body in bodies)
            foreach (var e in body.Edges)
            {
                if (e.Curve is not LineCurve3) continue;

                Vector3d ed = e.V1.P - e.V0.P;
                if (ed.Length < Tol.Weld) continue;

                // Та же прямая: направление совпадает (с точностью до знака)
                // и начало ссылки лежит на ней.
                if (Math.Abs(Vector3d.Dot(ed.Normalized(), wdir)) < 1 - 1e-6) continue;

                Vector3d off = e.V0.P - wanted.Start;
                if (Vector3d.Cross(off, wdir).Length > tol) continue;

                // Отрезки перекрываются хоть сколько-нибудь.
                double a0 = 0, a1 = wd.Length;
                double b0 = Vector3d.Dot(e.V0.P - wanted.Start, wdir);
                double b1 = Vector3d.Dot(e.V1.P - wanted.Start, wdir);

                if (b0 > b1) (b0, b1) = (b1, b0);

                if (Math.Min(a1, b1) - Math.Max(a0, b0) > tol) return e;
            }

        return ResolveNear(bodies, wanted);
    }

    /// <summary>Мутация P3D_REFWHOLE=0: выбранное целиком ребро из нескольких кусков, сдвинутое правкой, — неоднозначность (прежде).</summary>
    static readonly bool RefWholeOff = Environment.GetEnvironmentVariable("P3D_REFWHOLE") == "0";
    /// <summary>
    /// KRN-07 (07.7): ВЫБРАНО ВСЁ РЕБРО МЕЖДУ ДВУМЯ ГРАНЯМИ. Кромка отверстия у соосного пути — четыре дуги с одними именами;
    /// ссылка на кромку — четыре ссылки. Правка, сдвинувшая отверстие (шаг массива, положение), уводила все запомненные
    /// точки с кусков, и каждая ссылка отдельно была неоднозначна («was split and no piece contains the referenced point»).
    /// Если k ссылок с одной парой имён граней и ни одна не лежит на своём куске, а кусков с этими именами ровно k — выбрано
    /// ребро целиком: берутся все k кусков. Иное число кусков — прежняя неоднозначность.
    /// </summary>
    internal static Dictionary<EdgeRef, TEdge> WholeNamedEdges(List<TSolid> bodies, IReadOnlyList<EdgeRef> edges)
    {
        var res = new Dictionary<EdgeRef, TEdge>();
        if (RefWholeOff) return res;
        foreach (var g in edges.Where(r => r.FaceA != null && r.FaceB != null && r.Mid != null)
                               .GroupBy(r => string.CompareOrdinal(r.FaceA, r.FaceB) < 0 ? r.FaceA + "\n" + r.FaceB : r.FaceB + "\n" + r.FaceA))
        {
            var refs = g.Distinct().ToList();
            if (refs.Count < 2) continue;
            var named = bodies.SelectMany(b => b.Edges).Where(e => refs[0].FacesMatch(e)).ToList();
            if (named.Count != refs.Count) continue;
            double onTol = Math.Max(1e-6, Tol.Chord);
            if (refs.Any(r => named.Any(e => DistanceToCurve(e, r.Mid!.Value) <= onTol))) continue;
            for (int i = 0; i < refs.Count; i++) res[refs[i]] = named[i];
        }
        return res;
    }

    private static double DistanceToCurve(TEdge e, Vector3d p)
    {
        var pts = new List<Vector3d>();
        e.Curve.Tessellate(pts, true);
        double d = double.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++) d = Math.Min(d, (ClosestOnSegment(p, pts[i], pts[i + 1]) - p).Length);
        return pts.Count == 1 ? (pts[0] - p).Length : d;
    }

    /// <summary>
    /// ЗАПАСНОЙ ПОИСК ДЛЯ КРИВОЙ ЛЮБОГО ВИДА: концы и середина рядом с запомненными.
    ///
    /// ДОБАВЛЕНО. Запасные пути были только у отрезка (по прямой) и дуги (по окружности).
    /// Край фаски на кривой грани — изолиния или кривая пересечения; уточнение ядра сдвигает
    /// её концы на доли миллиметра (замыкание кольца фаски — на 0.16 мм), и сохранённое в
    /// модели скругление по такой кромке переставало находить свои рёбра (снимок
    /// пользователя 22.09: «none of the selected edges could be processed»). Допуск — 2% длины
    /// ребра, не меньше 0.5 мм; из подходящих — ближайшее по сумме расстояний.
    /// </summary>
    private static TEdge? ResolveNear(List<TSolid> bodies, EdgeRef wanted)
    {
        double len = (wanted.End - wanted.Start).Length;
        double tolEnd = Math.Max(0.5, 0.02 * len);
        TEdge? best = null; double bestD = double.MaxValue;
        foreach (var body in bodies)
            foreach (var e in body.Edges)
            {
                double d0 = (e.V0.P - wanted.Start).Length + (e.V1.P - wanted.End).Length;
                double d1 = (e.V1.P - wanted.Start).Length + (e.V0.P - wanted.End).Length;
                double de = Math.Min(d0, d1);
                if (de > 2 * tolEnd) continue;
                double dm = 0;
                if (wanted.Mid is { } mid)
                {
                    // Середина — на самой кривой ребра (не обязательно в её середине по параметру).
                    var pts = new List<Vector3d>();
                    e.Curve.Tessellate(pts, true);
                    dm = double.MaxValue;
                    for (int i = 0; i + 1 < pts.Count; i++) dm = Math.Min(dm, (ClosestOnSegment(mid, pts[i], pts[i + 1]) - mid).Length);
                    if (dm > tolEnd) continue;
                }
                if (de + dm < bestD) { bestD = de + dm; best = e; }
            }
        return best;
    }

    /// <summary>
    /// Дуга тела, на окружности которой лежат оба конца ссылки и чей
    /// угловой размах с ней перекрывается.
    ///
    /// Двух точек мало, чтобы задать окружность, поэтому окружность берётся
    /// У КАНДИДАТА, а от ссылки требуется лишь лежать на ней. Ложное
    /// совпадение при этом практически исключено: нужно, чтобы обе точки
    /// попали на ту же окружность в той же плоскости и ещё перекрылись
    /// по углу.
    /// </summary>
    private static TEdge? ResolveArc(List<TSolid> bodies, EdgeRef wanted, double tol)
    {
        foreach (var body in bodies)
            foreach (var e in body.Edges)
            {
                if (e.Curve is not ArcCurve3 arc) continue;
                if (arc.Radius < Tol.Weld) continue;

                if (!OnCircle(arc, wanted.Start, tol)) continue;
                if (!OnCircle(arc, wanted.End, tol)) continue;

                double a = AngleOf(arc, wanted.Start);
                double b = AngleOf(arc, wanted.End);

                // Середина КОРОТКОЙ дуги между концами ссылки: если она
                // лежит на кандидате, перекрытие есть.
                double half = Shortest(b - a) / 2;

                if (Contains(arc, a + half)) return e;

                // И наоборот: ребро могло стать короче ссылки целиком.
                if (Contains(arc, a) || Contains(arc, b)) return e;

                double mid = arc.A0 + (arc.A1 - arc.A0) / 2;
                double toMid = Shortest(mid - a);

                if (Math.Abs(toMid) <= Math.Abs(half) + 1e-9) return e;
            }

        return null;
    }

    /// <summary>Лежит ли точка на окружности дуги: и по радиусу, и по плоскости.</summary>
    private static bool OnCircle(ArcCurve3 arc, Vector3d p, double tol)
    {
        Vector3d d = p - arc.Frame.Origin;

        if (Math.Abs(Vector3d.Dot(d, arc.Frame.W)) > tol) return false;

        double r = (d - arc.Frame.W * Vector3d.Dot(d, arc.Frame.W)).Length;

        return Math.Abs(r - arc.Radius) <= tol;
    }

    /// <summary>Угол точки в осях дуги.</summary>
    private static double AngleOf(ArcCurve3 arc, Vector3d p)
    {
        Vector3d d = p - arc.Frame.Origin;

        return Math.Atan2(Vector3d.Dot(d, arc.Frame.V), Vector3d.Dot(d, arc.Frame.U));
    }

    /// <summary>Разность углов, приведённая к полуинтервалу от минус пи до пи.</summary>
    private static double Shortest(double angle)
    {
        while (angle <= -Math.PI) angle += 2 * Math.PI;
        while (angle > Math.PI) angle -= 2 * Math.PI;
        return angle;
    }

    /// <summary>Попадает ли угол в размах дуги. Знак размаха задаёт направление обхода.</summary>
    private static bool Contains(ArcCurve3 arc, double angle)
    {
        double sweep = arc.A1 - arc.A0;
        double d = angle - arc.A0;

        if (sweep >= 0)
        {
            while (d < -1e-9) d += 2 * Math.PI;
            while (d > 2 * Math.PI) d -= 2 * Math.PI;
            return d <= sweep + 1e-9;
        }

        while (d > 1e-9) d -= 2 * Math.PI;
        while (d < -2 * Math.PI) d += 2 * Math.PI;
        return d >= sweep - 1e-9;
    }

    // -------------------------------------------------- построение резца

    /// <summary>Строит призму-инструмент. Возвращает причину отказа либо null.</summary>
    private static string? BuildTool(List<TSolid> bodies, TEdge edge,
                                     double size1, double size2, bool round,
                                     Func<Vector3d, int>? claimed,
                                     out TSolid? tool, out bool adds)
    {
        tool = null;
        adds = false;

        if (size1 <= Tol.Weld || size2 <= Tol.Weld) return "the size must be greater than zero";

        // Дуга обрабатывается отдельно: у неё срез не призма, а тело
        // вращения — треугольника у фаски, «уголка без четверти круга»
        // у скругления.
        if (edge.Curve is ArcCurve3 onArc)
            return BuildArcTool(bodies, edge, onArc, size1, round ? size1 : size2, round, claimed, out tool, out adds);

        if (edge.Curve is not LineCurve3) return "only straight edges are supported so far";

        TFace? fa = edge.FaceA, fb = edge.FaceB;

        if (fa == null || fb == null) return "the edge does not have two faces";

        // ПРЯМОЕ РЕБРО ВДОЛЬ ОБРАЗУЮЩЕЙ ЦИЛИНДРА — между плоскостью и цилиндром
        // (стенка отверстия, вышедшая на боковую грань; бобышка, срезанная плоскостью).
        if (fa.Surface.IsPlanar && CylinderOf(fb) is { } cylB)
            return BuildLineOnCylinderTool(bodies, edge, fa, fb, cylB, size1, round ? size1 : size2, round, claimed, out tool, out adds);
        if (fb.Surface.IsPlanar && CylinderOf(fa) is { } cylA)
            return BuildLineOnCylinderTool(bodies, edge, fb, fa, cylA, size1, round ? size1 : size2, round, claimed, out tool, out adds);

        // ПЛОСКАЯ ГРАНЬ — ЭТО СВОЙСТВО ПОВЕРХНОСТИ, А НЕ ЕЁ КЛАСС.
        //
        // ИСПРАВЛЕНО. Прежде здесь стояло «поверхность есть PlaneSurface»,
        // и на обычном выдавливании скругление отказывало всегда: боковые
        // грани призмы несёт ExtrudedSurface — прямая, протянутая вдоль
        // вектора. Геометрически это плоскость, но класс другой, и проверка
        // по классу её не признавала. Отказ при этом выглядел осмысленно
        // («грани должны быть плоскими»), поэтому и не вызывал подозрений.
        //
        // У поверхностей на этот случай есть собственный ответ — IsPlanar,
        // и спрашивать надо его.
        if (!fa.Surface.IsPlanar || !fb.Surface.IsPlanar)
            return "both faces must be planar so far " +
                   $"({fa.Surface.GetType().Name} and {fb.Surface.GetType().Name} here)";

        Vector3d a = edge.V0.P, b = edge.V1.P;
        Vector3d along = b - a;
        double length = along.Length;

        // Резать вдоль ребра короче тысячной миллиметра бессмысленно: резец выходит
        // невесомым, и вычитание им только откатывается. Такие рёбра — осколки
        // точной топологии (две вершины на расстоянии 1e-5). Нашёл проверочный
        // прогон (Parametric3D.Verify).
        // Ребро в десятки раз короче самого среза — обрубок, нарезанный соседними
        // элементами: резец по нему почти целиком состоит из продлений за концы,
        // их торцы ложатся поперёк чужих граней, и результат выходит незамкнутым.
        // Нашёл проверочный прогон (Parametric3D.Verify).
        if (length < Math.Max(1e-3, 0.05 * Math.Max(size1, size2)))
            return $"the edge is only {length:0.######} long — too short for a cut of {Math.Max(size1, size2):0.###}";

        Vector3d t = along / length;

        Vector3d mid = (a + b) * 0.5;
        Vector3d n1 = fa.NormalAt(mid).Normalized();
        Vector3d n2 = fb.NormalAt(mid).Normalized();

        // НОРМАЛИ — В ПЛОСКОСТИ СЕЧЕНИЯ.
        //
        // ИСПРАВЛЕНО. Сечение резца лежит в плоскости, перпендикулярной ребру,
        // а центр дуги скругления и точки касания считаются от нормалей граней.
        // Нормаль грани, восстановленной из вершин на сетке, отклоняется от
        // идеальной на ~1e-6 и уже не строго перпендикулярна ребру — сечение
        // вываливалось из своей плоскости на 1e-5 при радиусе 12. Торцы резца
        // проецируют свой контур на плоскость, стороны — нет; точки расходились,
        // резец входил в ядро дырявым, и вычитание им рвало тело. Нашёл
        // проверочный прогон (Parametric3D.Verify). Составляющая вдоль ребра
        // убирается.
        n1 = (n1 - t * Vector3d.Dot(n1, t)).Normalized();
        n2 = (n2 - t * Vector3d.Dot(n2, t)).Normalized();

        // СГЛАЖЕННЫЙ СТЫК — НЕ РЕБРО.
        //
        // Ребро, спрятанное как гладкое, топологии нужно, а показывать
        // и обрабатывать его нечего: это шов внутри одной поверхности.
        // Ровно так помечены стыки полосок гранёного скругления, и именно
        // они попадают в выбор, когда ведут цепочку «по дуге».
        if (edge.Hidden) return "this is a smooth join inside a surface, not an edge";

        double cos = Vector3d.Dot(n1, n2);
        double turn = Math.Acos(Math.Clamp(cos, -1, 1)) * 180.0 / Math.PI;

        if (turn < MinTurnDegrees)
            return $"the faces meet at {180 - turn:0.#}\u00b0, which is a smooth join " +
                   "(most likely a facet of an earlier round) — nothing to cut here";

        if (cos < -1 + 1e-6) return "the faces are opposite at this edge";

        // ВОГНУТОЕ РЕБРО — ПРИБАВКА.
        //
        // ДОБАВЛЕНО. Прежде отказ. У вогнутого ребра материал занимает три
        // четверти, пустая — одна, и скругление должно ДОБАВИТЬ материал в этой
        // пустой четверти. Сечение строится теми же функциями, что у выпуклого,
        // с развёрнутыми нормалями: центр окружности уходит в пустоту, точки
        // касания ложатся на грани, направления вдоль граней смотрят туда, где
        // грани продолжаются. Прибавка вытягивается ровно по ребру — продолжение
        // за конец торчало бы наружу — и прибавляется к телу (см. Apply).
        // Исходные нормали нужны отдельно: направление «вдоль грани прочь от ребра» от
        // выпуклости НЕ зависит, а от разворота нормалей — зависит.
        Vector3d n1Face = n1, n2Face = n2;

        // A02. Базовая ступенька 60×40×20 работает и старым способом; воспроизведённый
        // отказ возникает, когда проба diagonal*1e-3 перескакивает тонкий участок либо
        // размер пробы раздувает удалённое второе тело. Для двух плоскостей материал
        // определяется по локальным треугольникам у ребра, без пространственной пробы.
        if (!Convex(bodies, mid, n1, n2, edge))
        {
            adds = true;
            n1 = -n1;
            n2 = -n2;
        }

        // ПЕРЕХЛЁСТ — ТОЛЬКО ДЛЯ ПРЕЖНЕГО ЯДРА.
        //
        // Прежняя булева спотыкалась, когда торец резца ложился в плоскость
        // грани, и призму выпускали за концы ребра на тысячную миллиметра.
        // Но выпущенный торец режет и то, что за концом: у ребра, упёртого
        // в стенку уступа, он прорезал стенку на микрон (тест
        // Extending_the_tool_never_gouges_a_wall это поймал). Точное ядро
        // совпадающие плоскости решает сравнением номеров, и перехлёст ему
        // не нужен вовсе.
        double over = BooleanOp.UseExactKernel || adds ? 0 : Math.Max(Overhang, length * 1e-6);

        // НАПРАВЛЕНИЯ ВДОЛЬ ГРАНЕЙ БЕРУТСЯ ОТ РАЗВЁРНУТЫХ НОРМАЛЕЙ — так и было. Пробовал брать
        // от исходных: по существу это верно (грань тянется от ребра в одну сторону, режем мы или
        // прибавляем), но проверка на ступеньке отказала по-прежнему, А ЗАТО СЛОМАЛАСЬ уже
        // работавшая прибавка на другом вогнутом ребре. Откатил.
        Vector3d f1 = AlongFace(n1, t, n2);
        Vector3d f2 = AlongFace(n2, t, n1);

        double k = 1 + Vector3d.Dot(-n1, -n2);

        double need1 = round ? size1 * Vector3d.Dot(-n2, f1) / k : size1;
        double need2 = round ? size1 * Vector3d.Dot(-n1, f2) / k : size2;

        // РЕЗЕЦ ПРОХОДИТ УГОЛ НАСКВОЗЬ.
        //
        // Ребро, укороченное соседней фаской или скруглением, кончается
        // на её грани, а та наклонна. Резец, обрезанный у конца ребра
        // перпендикулярно, оставлял между своим торцом и этой гранью шип;
        // фаска больше скругления оставляла торчать торец скругления.
        // Продление — ровно на то, насколько грани, сходящиеся в вершине,
        // уходят внутрь клина резца (см. EndReach). У стенки уступа все её
        // вершины лежат в плоскости торца, и продления нет.
        // Конец, который делит другое ребро этой же операции, не продлевается:
        // угол там обработает резец соседа (см. Apply). Но резцы соседей
        // ПЕРЕКРЫВАЮТСЯ на тысячную миллиметра (ClaimedOverlap): их торцы строятся
        // разными путями и расходятся на микроны, и без перехлёста между ними
        // оставалась тончайшая пластина — плёнка на стыке фаски. Только на стыке
        // дуги с прямым: у соседей одного вида торцы совпадают и так. Нашёл
        // проверочный прогон (Parametric3D.Verify).
        double overlap = ClaimedOverlap(Math.Max(need1, need2));
        int claimA = claimed?.Invoke(edge.V0.P) ?? 0, claimB = claimed?.Invoke(edge.V1.P) ?? 0;

        // Прибавка за свободный конец не продлевается — торчала бы наружу; на стыке
        // с соседом другого вида перекрывается на микрон, как срезы: иначе торцы двух
        // прибавок расходились на микроны, и между ними оставалась пустая щель —
        // плёнка. Нашёл проверочный прогон (Parametric3D.Verify).
        if (adds)
        {
            if (claimA == 0) claimA = 1;
            if (claimB == 0) claimB = 1;
        }
        double reachA = claimA == 0 ? EndReach(bodies, fa, fb, edge.V0.P, -t, n1, n2, f1, f2, need1, need2)
                      : claimA == 2 ? overlap : 0;
        double reachB = claimB == 0 ? EndReach(bodies, fa, fb, edge.V1.P, t, n1, n2, f1, f2, need1, need2)
                      : claimB == 2 ? overlap : 0;

        double extA = Math.Max(over, reachA);
        double extB = Math.Max(over, reachB);

        Vector3d corner = a - t * extA;

        Frame3 section = SectionFrame(corner, t);

        // РЕЗЕЦ ОБЯЗАН ПОМЕСТИТЬСЯ НА СВОИХ ГРАНЯХ.
        //
        // Срез, который шире самой грани, выходит за её дальний край
        // и режет уже не то ребро, что выбрали, а соседнюю стенку.
        // Проверять это надо до вычитания: после него виноватой выглядит
        // булева, хотя ей дали заведомо негодный инструмент.
        // ПАРЫ «ГРАНЬ И ЕЁ НАПРАВЛЕНИЕ» ЗДЕСЬ ВЕРНЫ — проверено выводом чисел на ступеньке:
        // грань пола с нормалью (0,0,−1) получает направление (1,0,0), грань стенки с нормалью
        // (−1,0,0) получает (0,0,1). Обе пары правильные.
        //
        // И главное: для ступеньки этот путь НЕ ВЫЗЫВАЕТСЯ ВОВСЕ — отказ приходит из другой
        // ветви построения резца. Искать надо там, а здесь всё в порядке.
        string? tooBig = Fits(fa, corner, f1, need1, "first")
                      ?? Fits(fb, corner, f2, need2, "second");

        if (tooBig != null) return tooBig;

        List<ICurve3>? curves = round
            ? RoundSection(section, corner, n1, n2, size1)
            : ChamferSection(corner, t, n1, n2, size1, size2);

        if (curves == null) return "the cross-section of the cut came out degenerate";

        Profile profile = ProfileBuilder.Build(section, curves);

        if (profile.IsEmpty) return "the cross-section did not close into a contour";

        tool = ExtrudeOp.Build(profile, t * (length + extA + extB));

        // ПОДПИСИ ГРАНЕЙ РЕЗЦА. Резец построен выдавливанием, и его грани унаследовали бы имена
        // выдавливания («extrude[0]:side[...]»), попав в тело: тогда кромка скругления
        // притворялась бы кромкой эскиза. Грань от ДУГИ — это и есть поверхность скругления.
        {
            string kind = round ? "round" : "chamfer";
            int side = 0;
            foreach (var fz in tool.Faces)
            {
                ISurface3? sz = fz.Surface is FacetedSurface fsz ? fsz.Source : fz.Surface;
                fz.Tag = sz is ExtrudedSurface ez && ez.Base is ArcCurve3
                    ? kind + ":face"
                    : $"{kind}:tool[{side++}]";
            }
        }
        // A06: only a straight additive tool with no end extension. Use the
        // owning body's contact planes, not independently rounded copies.
        if (adds && BooleanOp.UseExactKernel && extA == 0 && extB == 0 &&
            (t.X != 0 ? 1 : 0) + (t.Y != 0 ? 1 : 0) + (t.Z != 0 ? 1 : 0) > 1 &&
            fa.Shell?.Solid is {} owner)
            Exact.ExactContactInput.TryAttach(tool, owner, edge);
        return null;
    }

    /// <summary>
    /// НАСКОЛЬКО ЗА КОНЦОМ РЕБРА ПРОДОЛЖАЕТСЯ УГОЛ, КОТОРЫЙ НАДО СНЯТЬ.
    ///
    /// Торец — грани, сходящиеся в конечной вершине, кроме двух граней самого
    /// ребра: грань соседней фаски, скругления или стенки. Резец продлевается
    /// до тех пор, пока его сечение не выйдет из тела через торец.
    ///
    /// ПЛОСКИЙ ТОРЕЦ считается прямо. Из каждой вершины сечения резца идём
    /// вдоль ребра до плоскости торца; самая дальняя точка встречи и есть
    /// продление. Размер соседней операции при этом не важен: у фаски любого
    /// размера продление выходит равным размеру сечения — сколько оно
    /// пересекает её плоскость.
    ///
    /// ИСПРАВЛЕНО. Прежде учитывались вершины торцевой грани, лежащие не дальше
    /// полутора размеров сечения. У фаски вдвое больше скругления её дальняя
    /// вершина лежала за этим пределом, продления не было, и шип оставался.
    ///
    /// КРИВОЙ ТОРЕЦ (торец скругления) — по точкам его рёбер внутри клина.
    ///
    /// ЧУЖОЕ НЕ ТРОГАЕТСЯ. Считаются только торцы, через которые сечение
    /// ВЫХОДИТ из тела — у плоского по нормали грани, у кривого по нормали
    /// в каждой точке. В стенку уступа сечение бы входило.
    ///
    /// Пробой «есть ли материал сразу за концом» стенку от угла не отличить:
    /// за концом ребра фаски у торца скругления материал тоже есть — это и есть
    /// торец, который надо срезать. Такая проба была и отменяла продление
    /// там, где оно нужнее всего.
    ///
    /// Потолок — три размера сечения.
    /// </summary>
    private static double EndReach(List<TSolid> bodies,
                                   TFace fa, TFace fb, Vector3d end, Vector3d outward,
                                   Vector3d n1, Vector3d n2, Vector3d f1, Vector3d f2,
                                   double need1, double need2)
    {
        double size = Math.Max(need1, need2);
        double tol = Tol.Weld * 100;
        double limit = size * 3;

        bool AtEnd(Vector3d p) => (p - end).LengthSquared < tol * tol;

        // ---- 1. СТЕНКА ИЛИ УГОЛ. Если сечение ВХОДИТ хоть в одну торцевую
        // грань — за концом стенка уступа, и продлевать нельзя вовсе: лучи
        // из стенки вышли бы далеко, и резец прорезал бы её.
        bool anyEnd = false;

        foreach (TSolid body in bodies)
            foreach (TFace face in body.Faces)
            {
                if (ReferenceEquals(face, fa) || ReferenceEquals(face, fb)) continue;
                if (!face.Edges.Any(e => AtEnd(e.V0.P) || AtEnd(e.V1.P))) continue;

                // Нормаль торца у вершины — по его собственным треугольникам,
                // сходящимся в ней. NormalAt гранёной грани определяет знак по
                // одному запомненному треугольнику; у четверти цилиндра тот лежит
                // на другом краю дуги, и нормаль у конца ребра выходила
                // вывернутой — торец скругления принимался за стенку.
                Vector3d ne = NormalNear(face, end, tol);
                if (ne.LengthSquared < 1e-18) continue;
                anyEnd = true;

                if (Vector3d.Dot(outward, ne) < -1e-6) return 0;
            }

        if (!anyEnd) { ReachDebug?.Invoke($"продление у ({end.X:0.###},{end.Y:0.###},{end.Z:0.###}): торца нет, 0"); return 0; }

        // ---- 2. ЛУЧИ. Из точек сечения резца на конце ребра идём вдоль ребра
        // и находим, где луч выходит из тела. Самый дальний выход — продление.
        //
        // ИСПРАВЛЕНО. Прежде у кривого торца перебирались точки на его рёбрах.
        // Но сечение выходит из тела через середину торца, а не через края:
        // у косого ребра между двумя фасками, упёртого в скругление, резец
        // выходил короче нужного, и у скругления оставался недорезанный уголок.
        // Лучи видят настоящую форму торца, плоского или кривого.
        // Треугольники — только чтобы найти, ЧЕРЕЗ КАКУЮ ГРАНЬ выходит луч; само расстояние
        // выхода уточняется по точной поверхности этой грани. Иначе длина резца зависела от
        // разбиения: одно и то же скругление на одном и том же теле давало резцы 594 и 636 мм³.
        var tris = new List<(Vector3d A, Vector3d B, Vector3d C)>();
        var triFace = new List<TFace>();
        foreach (TSolid body in bodies)
            foreach (TFace face in body.Faces)
                if (face.Surface is FacetedSurface fsr)
                    foreach (var t in fsr.Facets) { tris.Add(t); triFace.Add(face); }
                else
                {
                    var mb0 = new MeshBuilder(); face.Surface.TessellateFace(face, mb0);
                    var md0 = new MeshData(); mb0.FillInto(md0);
                    for (int i = 0; i + 2 < md0.Indices.Length; i += 3)
                    { tris.Add((mb0.PositionsD[md0.Indices[i]], mb0.PositionsD[md0.Indices[i + 1]], mb0.PositionsD[md0.Indices[i + 2]])); triFace.Add(face); }
                }

        double reach = 0;
        const int N = 24;
        var best = new List<(double Exit, double U, double V)>();

        // Точки сечения сдвигаются внутрь ТЕЛА — поперёк граней ребра — на
        // несколько микрометров. Тогда можно брать всё сечение, с вершинами
        // и краями: выход из самой вершины сечения бывает дальше, чем рядом с ней,
        // а луч с неё не скользит по грани. Прежний отступ внутрь сечения терял
        // эти точки и оставлял за торцом наноскопический осколок.
        Vector3d inward = -(n1 + n2) * Math.Max(1e-5, size * 1e-5);

        Vector3d At(double u, double v) => end + f1 * (need1 * u) + f2 * (need2 * v) + inward;

        for (int i = 0; i <= N; i++)
            for (int j = 0; i + j <= N; j++)
            {
                // Торец резца плоский, поэтому он обязан уйти за САМУЮ ДАЛЬНЮЮ точку
                // выхода по всему сечению. А самые дальние выходы — у краёв сечения,
                // где оно прижато к граням и к соседнему скруглению.
                //
                // ИСПРАВЛЕНО. Сетка отступала от краёв на две сотых и была редкой:
                // ровно та область, где выход дальше всего, в неё не попадала,
                // и над торцом вертикальной фаски оставался уступ.
                //
                double u = (double)i / N, v = (double)j / N;

                double exit = FirstExitExact(tris, triFace, At(u, v), outward, size);
                best.Add((exit, u, v));
                if (exit > reach) reach = exit;
            }

        // УТОЧНЕНИЕ МАКСИМУМА. Самая дальняя точка выхода лежит на гребне, где
        // выход через одну грань сравнивается с выходом через другую, и гребень
        // проходит между узлами сетки. Ближайший узел недобирал десятые доли
        // миллиметра — ровно столько, чтобы над торцом остался уступ. От лучших
        // узлов идёт поиск с делением шага до десятитысячной доли размера.
        double Exit(double u, double v)
        {
            if (u < 0 || v < 0 || u + v > 1) return -1;
            return FirstExitExact(tris, triFace, At(u, v), outward, size);
        }

        foreach (var (e0, u0, v0) in best.Where(b => b.Exit > 0).OrderByDescending(b => b.Exit).Take(6).ToList())
        {
            double bu = u0, bv = v0, be = e0;
            double step = 1.0 / N;

            while (step > 1e-4)
            {
                bool moved = false;
                foreach (var (du, dv) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, -1), (-1, 1), (1, 1), (-1, -1) })
                {
                    double e = Exit(bu + du * step, bv + dv * step);
                    if (e > be) { be = e; bu += du * step; bv += dv * step; moved = true; break; }
                }
                if (!moved) step *= 0.5;
            }

            if (be > reach) reach = be;
        }

        // Запас за самым дальним выходом. В угловом случае за ним в сечении пусто
        // (стенки отсечены проверкой выше), а сетка лучей между узлами может
        // недобрать до настоящего предела.
        if (reach > 0) reach += Math.Max(1e-3, size * 2e-3);

        ReachDebug?.Invoke($"продление у ({end.X:0.###},{end.Y:0.###},{end.Z:0.###}): {Math.Min(reach, limit):0.#####}");
        return Math.Min(reach, limit);
    }

    /// <summary>
    /// Углы вершин гранёной стенки (цилиндра выдавливания) относительно направления
    /// radial вокруг оси spin — те, что попадают внутрь (lo, hi), плюс сами lo и hi.
    /// Стенка делит свою образующую-дугу ровно так: Base.PointAt(k / n),
    /// n = ArcSegments(радиус, развёртка). Null — если стенка не дуга.
    /// </summary>
    private static List<double>? WallAngles(ExtrudedSurface wall, Vector3d centre, Vector3d spin,
                                            Vector3d radial, double lo, double hi)
    {
        if (wall.Base is not ArcCurve3 arc) return null;

        var pts = new List<Vector3d>();
        arc.Tessellate(pts, true);

        // Узлы ближе 0.01 мм по дуге сливаются: конец резца лежит в вершине цепочки,
        // то есть в вершине стенки, но его угол вычислен своим путём и расходится
        // с углом вершины на 1e-7. Оставь оба — и между ними выйдет грань шириной
        // в нанометры, вырождающаяся при округлении на сетку.
        double r = Math.Max((pts.Count > 0 ? (pts[0] - centre) - spin * Vector3d.Dot(pts[0] - centre, spin) : radial).Length, 1e-6);
        double gap = 0.01 / r;

        var inner = new List<double>();
        foreach (Vector3d p in pts)
        {
            Vector3d d = p - centre;
            d -= spin * Vector3d.Dot(d, spin);
            if (d.LengthSquared < 1e-18) continue;

            double a = Math.Atan2(Vector3d.Dot(Vector3d.Cross(radial, d), spin), Vector3d.Dot(radial, d));

            // Угол может понадобиться и со сдвигом на оборот — резец бывает длиннее π.
            foreach (double cand in new[] { a, a - 2 * Math.PI, a + 2 * Math.PI })
                if (cand > lo + gap && cand < hi - gap) inner.Add(cand);
        }

        inner.Sort();
        var clean = new List<double> { lo };
        foreach (double a in inner)
            if (a - clean[^1] > gap) clean.Add(a);
        if (hi - clean[^1] <= gap && clean.Count > 1) clean.RemoveAt(clean.Count - 1);
        clean.Add(hi);

        // Слишком частые узлы не нужны, а редкие допустимы: шаг стенки и так
        // выбран по допуску хорды. Если стенка дала меньше двух узлов — пусть
        // резец делится своим шагом.
        return clean.Count >= 2 ? clean : null;
    }

    /// <summary>
    /// ПЕРЕХЛЁСТ СОСЕДНИХ РЕЗЦОВ НА ОБЩЕМ КОНЦЕ — тысячная миллиметра или десятитысячная
    /// доля размера. Лишнее, что он снимает, — величина второго порядка (около 1e-7 мм):
    /// продолжение соседнего сечения за общий конец расходится с ним лишь по кривизне.
    /// </summary>
    private static double ClaimedOverlap(double size) => NoClaimedOverlap ? 0 : Math.Max(1e-3, size * 1e-4);

    /// <summary>
    /// Эксперимент r50 (P3D_NOOVERLAP=1): резцы впритык на общих концах — для аналитического пути перехлёст в 0.001 мм даёт
    /// щели шириной в тысячную миллиметра (почти параллельные грани), а совпадающие торцы точное ядро решает само.
    /// </summary>
    public static readonly bool NoClaimedOverlap = Environment.GetEnvironmentVariable("P3D_NOOVERLAP") == "1";

    /// <summary>
    /// НА КАКОЙ УГОЛ ЗА КОНЕЦ ДУГИ ПРОДОЛЖАЕТСЯ УГОЛ, КОТОРЫЙ НАДО СНЯТЬ.
    ///
    /// Точки сечения на конце дуги поворачиваются дальше вокруг оси, пока
    /// не выйдут из тела; продление — самый дальний выход, с уточнением
    /// делением шага. Как у прямых рёбер: конец, общий с другим ребром той же
    /// операции, не продлевается, а если сечение ВХОДИТ в торцевую грань —
    /// за концом стенка, и продления нет. Потолок — три размера сечения по дуге.
    /// </summary>
    private static double ArcEndReach(List<TSolid> bodies, List<(Vector3d, Vector3d, Vector3d)> tris,
                                      TEdge edge, TFace flat, TFace wall,
                                      Vector3d centre, Vector3d spin, double endAngle, int dir,
                                      Vector3d p0, Vector3d toMaterial, Vector3d axial,
                                      double flatLeg, double wallLeg, double r,
                                      Func<Vector3d, int>? claimed)
    {
        Vector3d Rot(Vector3d p, double a) => RevolvedSurface.RotateAbout(p, centre, spin, a);

        Vector3d end = Rot(p0, endAngle);
        TVertex endVertex = (edge.V0.P - end).LengthSquared <= (edge.V1.P - end).LengthSquared ? edge.V0 : edge.V1;
        if ((endVertex.P - end).Length > Math.Max(1e-3, r * 1e-3)) return 0;   // конец не нашёлся — не рискуем

        // Общий с соседом той же операции конец: с соседом-дугой — стык встык,
        // с прямым соседом — только перехлёст (см. ClaimedOverlap).
        int claim = claimed?.Invoke(endVertex.P) ?? 0;
        if (claim == 1) return 0;
        if (claim == 2) return ClaimedOverlap(Math.Max(flatLeg, wallLeg)) / Math.Max(r, 1e-9);

        Vector3d tangent = Vector3d.Cross(spin, end - centre);
        if (tangent.LengthSquared < 1e-18) return 0;
        tangent = tangent.Normalized() * dir;

        // Стенка, угол или продолжение — по нормали торцевых граней у вершины.
        //
        // Продлевать можно, только если резец ВЫХОДИТ из тела через торец,
        // пересекая его. Грань, в которую он входит, — стенка уступа. Грань,
        // лишь касательно продолжающая дугу (у торца скругления — соседняя
        // плоская грань), — это не угол, а продолжение кромки: если его не
        // выбрали, фаска кончается торцом, а продление прорезало бы желобок
        // вдоль невыбранного ребра.
        double tol = Tol.Weld * 100;
        bool exits = false;
        foreach (TSolid body in bodies)
            foreach (TFace face in body.Faces)
            {
                if (ReferenceEquals(face, flat) || ReferenceEquals(face, wall)) continue;
                if (!face.Edges.Any(e => (e.V0.P - endVertex.P).LengthSquared < tol * tol ||
                                         (e.V1.P - endVertex.P).LengthSquared < tol * tol)) continue;

                Vector3d ne = NormalNear(face, endVertex.P, tol);
                if (ne.LengthSquared < 1e-18) continue;

                double along = Vector3d.Dot(tangent, ne);
                if (along < -1e-6) return 0;
                if (along > 0.05) exits = true;
            }
        if (!exits) return 0;

        double size = Math.Max(flatLeg, wallLeg);
        double maxAngle = Math.Min(Math.PI / 2, 3 * size / Math.Max(r, size));

        // СТЕНКА ЗА КОНЦОМ ПРОДОЛЖАЕТСЯ?
        //
        // Угол, который стоит проходить насквозь, есть только там, где за концом
        // дуги стенка идёт дальше, а плоская грань кончилась — край уходит под
        // соседнюю фаску. Если кончилась сама стенка — отверстие вскрыло боковую
        // грань, и окружность ушла наружу, — угла за концом нет. А плоская грань
        // там продолжается, и сечение под ней не выходило из тела до потолка:
        // тор выедал канавку по верхней грани и отсекал куски материала. Это
        // нашёл проверочный прогон (Parametric3D.Verify): род тела по Эйлеру
        // вышел отрицательным.
        //
        // Проверка — две точки чуть за концом, на середине стенки по глубине:
        // со стороны материала обязано быть внутри тела, со стороны пустоты —
        // снаружи.
        {
            double step = Math.Max(0.02, 0.05 * size) / Math.Max(r, size);
            double eps = Math.Max(0.01, 0.02 * size);
            Vector3d mid = p0 + axial * (0.5 * wallLeg);
            bool material = Voxels.PointInSolid.Test(tris, Rot(mid + toMaterial * eps, endAngle + dir * step));
            bool voidSide = Voxels.PointInSolid.Test(tris, Rot(mid - toMaterial * eps, endAngle + dir * step));
            if (!material || voidSide) return 0;
        }
        double inset = Math.Max(1e-5, size * 1e-5);
        Vector3d inward = (toMaterial + axial) * inset;

        double reach = 0;
        const int N = 8, Steps = 32;

        for (int i = 0; i <= N; i++)
            for (int j = 0; i + j <= N; j++)
            {
                double u = (double)i / N, v = (double)j / N;
                Vector3d q = p0 + toMaterial * (flatLeg * u) + axial * (wallLeg * v) + inward;

                if (!Voxels.PointInSolid.Test(tris, Rot(q, endAngle))) continue;

                // Шагами до выхода, затем делением шага.
                double lo = 0, hi = -1;
                for (int k = 1; k <= Steps; k++)
                {
                    double a = maxAngle * k / Steps;
                    if (!Voxels.PointInSolid.Test(tris, Rot(q, endAngle + dir * a))) { hi = a; break; }
                    lo = a;
                }
                if (hi < 0) { reach = Math.Max(reach, maxAngle); continue; }

                for (int it = 0; it < 12; it++)
                {
                    double mid = (lo + hi) / 2;
                    if (Voxels.PointInSolid.Test(tris, Rot(q, endAngle + dir * mid))) lo = mid; else hi = mid;
                }

                reach = Math.Max(reach, hi);
            }

        // Запас за самым дальним выходом — в пустоте.
        if (reach > 0) reach += Math.Max(1e-3, size * 2e-3) / Math.Max(r, size);
        return Math.Min(reach, maxAngle);
    }

    /// <summary>Средняя нормаль треугольников грани, сходящихся в точке p; ноль, если таких нет.</summary>
    private static Vector3d NormalNear(TFace face, Vector3d p, double tol)
    {
        // Треугольники грани — напрямую, без гранения: здесь нужны только их нормали,
        // а гранение с гладкими нормалями стоит проекции каждой вершины на точную поверхность.
        if (face.Surface is FacetedSurface direct)
        {
            Vector3d acc = Vector3d.Zero;
            foreach (var (a, b, c) in direct.Facets)
                if ((a - p).LengthSquared <= tol * tol || (b - p).LengthSquared <= tol * tol || (c - p).LengthSquared <= tol * tol)
                    acc += Vector3d.Cross(b - a, c - a);
            return acc.LengthSquared > 1e-24 ? acc.Normalized() : Vector3d.Zero;
        }

        var mb = new MeshBuilder();
        face.Surface.TessellateFace(face, mb);
        var md = new MeshData();
        mb.FillInto(md);

        Vector3d sum = Vector3d.Zero;
        for (int i = 0; i + 2 < md.Indices.Length; i += 3)
        {
            Vector3d a = mb.PositionsD[md.Indices[i]], b = mb.PositionsD[md.Indices[i + 1]], c = mb.PositionsD[md.Indices[i + 2]];
            if ((a - p).LengthSquared > tol * tol && (b - p).LengthSquared > tol * tol && (c - p).LengthSquared > tol * tol) continue;
            sum += Vector3d.Cross(b - a, c - a);
        }

        return sum.LengthSquared < 1e-24 ? Vector3d.Zero : sum.Normalized();
    }

    /// <summary>
    /// Где луч из q по направлению dir впервые выходит из тела, если q внутри
    /// или на самой поверхности. Если луч сначала входит в тело дальше — это
    /// чужой материал за промежутком, и ответ ноль.
    /// </summary>
    /// <summary>
    /// Первый выход луча из тела, уточнённый по точной поверхности грани, через которую он
    /// вышел. Треугольники отвечают только на вопрос «через какую грань»; расстояние —
    /// из точной геометрии, поэтому оно не зависит от разбиения.
    /// </summary>
    private static double FirstExitExact(List<(Vector3d A, Vector3d B, Vector3d C)> tris, List<TFace> owner,
                                         Vector3d q, Vector3d dir, double size)
    {
        double t0 = FirstExit(tris, q, dir, size, out int hit);
        if (t0 <= 0 || hit < 0 || hit >= owner.Count) return t0;
        var src = owner[hit].Surface is FacetedSurface fs ? fs.Source : owner[hit].Surface;
        if (src == null || src is FacetedSurface) return t0;

        Vector3d d = dir.Normalized();
        double t = t0;
        for (int it = 0; it < 12; it++)
        {
            Vector3d p = q + d * t;
            var (proj, n) = Curves.ExactSurfaces.Project(src, p);
            if (n.LengthSquared < 1e-18) return t0;
            n = n.Normalized();
            double f = Vector3d.Dot(p - proj, n), slope = Vector3d.Dot(d, n);
            if (Math.Abs(slope) < 1e-6) return t0;
            double next = t - f / slope;
            if (double.IsNaN(next) || Math.Abs(next - t0) > 0.5 * Math.Max(size, 1)) return t0;
            if (Math.Abs(next - t) < 1e-12) { t = next; break; }
            t = next;
        }
        return t;
    }

    private static double FirstExit(List<(Vector3d A, Vector3d B, Vector3d C)> tris,
                                    Vector3d q, Vector3d dir, double size) => FirstExit(tris, q, dir, size, out _);

    private static double FirstExit(List<(Vector3d A, Vector3d B, Vector3d C)> tris,
                                    Vector3d q, Vector3d dir, double size, out int hitIndex)
    {
        hitIndex = -1;
        var hits = new List<(double T, int Sign, int Index)>();

        for (int ti = 0; ti < tris.Count; ti++)
        {
            var (a, b, c) = tris[ti];
            Vector3d e1 = b - a, e2 = c - a;
            Vector3d p = Vector3d.Cross(dir, e2);
            double det = Vector3d.Dot(e1, p);
            if (Math.Abs(det) < 1e-14) continue;

            double inv = 1 / det;
            Vector3d s = q - a;
            double bu = Vector3d.Dot(s, p) * inv;
            if (bu < -1e-12 || bu > 1 + 1e-12) continue;

            Vector3d qv = Vector3d.Cross(s, e1);
            double bv = Vector3d.Dot(dir, qv) * inv;
            if (bv < -1e-12 || bu + bv > 1 + 1e-12) continue;

            double t = Vector3d.Dot(e2, qv) * inv;
            if (t <= 1e-9) continue;

            double facing = Vector3d.Dot(Vector3d.Cross(e1, e2), dir);
            if (Math.Abs(facing) < 1e-14) continue;

            hits.Add((t, facing > 0 ? 1 : -1, ti));   // +1 — выход, −1 — вход
        }

        if (hits.Count == 0) return 0;
        hits.Sort((x, y) => x.T.CompareTo(y.T));

        // Попадания в общее ребро соседних треугольников дают дубли — берём
        // первое по порядку и пропускаем совпадающие.
        double eps = Math.Max(1e-7, size * 1e-7);

        if (hits[0].Sign > 0) { hitIndex = hits[0].Index; return hits[0].T; }

        // Сначала вход: годится, только если он вплотную к началу — точка
        // сечения лежала на поверхности. Тогда ищем следующий выход.
        if (hits[0].T > size * 1e-4) return 0;

        foreach (var (t, sign, idx) in hits)
            if (sign > 0 && t > hits[0].T + eps) { hitIndex = idx; return t; }

        return 0;
    }

    /// <summary>
    /// ФАСКА НА КРОМКЕ СКРУГЛЕНИЯ: СРЕЗ — ТЕЛО ВРАЩЕНИЯ.
    ///
    /// Ребро здесь дуга: окружность, по которой цилиндр скругления
    /// упирается в плоскую грань. Призмой такой срез не снять — снимаемая
    /// область идёт по кругу, и её сечение поворачивается вместе с ним.
    ///
    /// Зато если ось цилиндра перпендикулярна плоскости дуги — а у кромки
    /// скругления это так по построению, — картина в осевом сечении
    /// не зависит от угла: тот же треугольник, что у обычной фаски.
    /// Остаётся провернуть его вокруг оси, и срез выходит усечённым
    /// конусом, ТОЧНЫМ, а не приближённым.
    ///
    /// ОСТАЛЬНЫЕ СЛУЧАИ ОТВЕРГАЮТСЯ. Если ось и плоскость дуги
    /// не перпендикулярны, сечение по кругу меняется, и тело вращения
    /// среза уже не описывает. Сделать вид, что описывает, значило бы
    /// срезать не то.
    /// </summary>
    private static string? BuildArcTool(List<TSolid> bodies, TEdge edge, ArcCurve3 arc,
                                        double d1, double d2, bool rounding,
                                        Func<Vector3d, int>? claimed, out TSolid? tool, out bool adds)
    {
        tool = null;
        adds = false;

        if (d1 <= Tol.Weld || d2 <= Tol.Weld) return "the size must be greater than zero";

        TFace? fa = edge.FaceA, fb = edge.FaceB;
        if (fa == null || fb == null) return "the edge does not have two faces";

        // Одна грань плоская, другая — цилиндр скругления.
        TFace flat = fa.Surface.IsPlanar ? fa : fb;
        TFace round = ReferenceEquals(flat, fa) ? fb : fa;

        if (!flat.Surface.IsPlanar) return "neither face at this arc is planar";
        if (round.Surface.IsPlanar) return "both faces at this arc are planar";

        ExtrudedSurface? cylinder = Unwrap(round.Surface);

        if (cylinder == null || cylinder.Base is not ArcCurve3)
            return "the curved face is not a fillet surface";

        Vector3d axis = arc.Frame.W;
        if (axis.LengthSquared < 1e-18) return "the arc has no plane";

        axis = axis.Normalized();

        // Ось цилиндра обязана совпасть с осью дуги: только тогда срез
        // описывается вращением.
        Vector3d along = cylinder.Dir;
        if (along.Length < Tol.Weld) return "the fillet surface is degenerate";

        if (Math.Abs(Vector3d.Dot(along.Normalized(), axis)) < 1 - 1e-6)
            return "the arc and the fillet axis are not perpendicular; " +
                   "only that case is exact and only it is built";

        // ЦЕНТР И РАДИУС — ИЗ ПОВЕРХНОСТИ СКРУГЛЕНИЯ, А НЕ ИЗ ВОССТАНОВЛЕННОЙ ДУГИ.
        //
        // ИСПРАВЛЕНО. Дуга на экране восстановлена по точкам, округлённым
        // на сетку (MergeArcs), и её центр уходит от заданного на несколько
        // нанометров. Конус, построенный вокруг такого центра, упирался
        // торцами в плоскости, отстоящие от торцов прямых резцов на шаг сетки,
        // и между ними оставались пластины толщиной в нанометры — на экране
        // лишние треугольные грани на стыках фаски.
        //
        // У грани скругления сохранена её исходная поверхность — цилиндр,
        // построенный по заданным числам: центр и радиус там точные. Центр
        // конуса — точка оси цилиндра в плоскости дуги; концы ребра точные
        // вершины, и развёртка с направлением середины берутся из них.
        Vector3d centre = arc.Frame.Origin;
        double r = arc.Radius;
        Vector3d exactMid = default;
        double exactSweep = double.NaN;
        bool longArc = false;

        if (cylinder.Base is ArcCurve3 designed)
        {
            Vector3d dirN = cylinder.Dir.Normalized();
            Vector3d o = designed.Frame.Origin;
            Vector3d c = o + dirN * Vector3d.Dot(edge.V0.P - o, dirN);

            Vector3d u0 = edge.V0.P - c, u1 = edge.V1.P - c;
            u0 -= dirN * Vector3d.Dot(u0, dirN);
            u1 -= dirN * Vector3d.Dot(u1, dirN);

            // Годится, только если концы ребра лежат на этой окружности:
            // иначе ребро — не край этого скругления.
            //
            // ИСПРАВЛЕНО. Допуск был 1e-4. Но конец дуги, где она выходит
            // на соседнюю грань, — пересечение этой грани с ХОРДОЙ стенки,
            // и от окружности он отстоит до стрелы прогиба, Tol.Chord. Проверка
            // не проходила, резец строился по окружности, подогнанной по точкам,
            // — у вас R39.442 вместо заданной, — и ложился на полмиллиметра
            // мимо стенки: внутри фаски оставались полоски стенки.
            double tol = Math.Max(1e-4, 3 * Tol.Chord);
            if (Math.Abs(u0.Length - designed.Radius) < tol && Math.Abs(u1.Length - designed.Radius) < tol)
            {
                centre = c;
                r = designed.Radius;
                axis = Vector3d.Dot(dirN, axis) < 0 ? -dirN : dirN;

                Vector3d bis = u0.Normalized() + u1.Normalized();
                if (bis.LengthSquared > 1e-12)
                {
                    exactMid = c + bis.Normalized() * r;
                    exactSweep = Math.Acos(Math.Clamp(Vector3d.Dot(u0.Normalized(), u1.Normalized()), -1, 1));

                    // ДУГА ДЛИННЕЕ ПОЛУОКРУЖНОСТИ.
                    //
                    // ИСПРАВЛЕНО. Арккосинус не бывает больше 180°, и у края отверстия,
                    // вышедшего за две грани, — дуга в 225° — резец строился на
                    // дополнительных 135° с другой стороны: край почти не скруглялся.
                    // Нашёл проверочный прогон (Parametric3D.Verify). Длина дуги
                    // берётся у восстановленной кривой: если она больше 180°,
                    // развёртка — дополнение до оборота, а середина — напротив.
                    if (Math.Abs(arc.Sweep) > Math.PI)
                    {
                        exactSweep = 2 * Math.PI - exactSweep;
                        exactMid = c - bis.Normalized() * r;
                        longArc = true;
                    }
                }
            }
        }

        if (r < Tol.Weld) return "the arc radius is degenerate";

        // Куда уходить по плоской грани. У кромки выпуклого скругления
        // материал со стороны оси, значит срез идёт К ОСИ.
        Vector3d mid = double.IsNaN(exactSweep) ? edge.Curve.PointAt(0.5) : exactMid;
        Vector3d radial = mid - centre;

        if (radial.LengthSquared < 1e-18) return "the arc midpoint sits on its own axis";

        radial = radial.Normalized();

        // КУДА УХОДИТ ЦИЛИНДР — СПРАШИВАЕМ У САМОГО ЦИЛИНДРА.
        //
        // ИСПРАВЛЕНО. Направление бралось у плоской грани: «внутрь тела —
        // это против её наружной нормали». Но знак нормали грани после
        // булевой восстанавливается по ориентированному многоугольнику,
        // и полагаться на него в решающем месте не стоит: ошибёшься —
        // и проба уходит наружу, а отказ звучит осмысленно («вогнутая
        // дуга»), хотя ответ просто не получен.
        //
        // У поверхности скругления направление своё собственное: она
        // протянута от базовой кривой вдоль Dir. Дуга лежит на одном
        // из её концов, значит тело цилиндра — по ту сторону, где его
        // середина. Это чистая геометрия, без нормалей и без лучей.
        Vector3d cylinderMid = cylinder.Base.PointAt(0.5) + cylinder.Dir * 0.5;

        double reach = Vector3d.Dot(cylinderMid - mid, axis);

        if (Math.Abs(reach) < Tol.Weld)
            return "the arc lies in the middle of the fillet, not at its end";

        Vector3d axial = reach > 0 ? axis : -axis;

        // Плоская грань обязана быть поперёк оси: только тогда сечение
        // по кругу не меняется.
        Vector3d outward = flat.NormalAt(mid);

        if (outward.LengthSquared < 1e-18) return "the planar face has no normal";

        if (Math.Abs(Vector3d.Dot(outward.Normalized(), axis)) < 1 - 1e-6)
            return "the planar face is not perpendicular to the fillet axis";

        // ВОГНУТАЯ КРОМКА ОТВЕРГАЕТСЯ.
        //
        // ДОБАВЛЕНО. У выпуклой кромки материал вдоль стенки идёт ПРОТИВ
        // наружной нормали плоской грани: у края отверстия — вниз от верха.
        // У вогнутой — в ту же сторону: кольцо у основания бобышки, дно
        // глухого отверстия. Там срез вычел бы клин из материала и прорезал
        // выемку; скругление вогнутой кромки добавляет материал, а это пока
        // не сделано.
        //
        // ВОГНУТАЯ КРОМКА — ПРИБАВКА (кольцо у основания бобышки, дно глухого
        // отверстия). Тот же построитель, с направлениями, развёрнутыми в пустоту:
        // вдоль стенки — прочь от плоской грани (по её наружной нормали), вдоль
        // плоской грани — на ту сторону стенки, где пусто. Полоска через стенку
        // уходит тогда в материал и перекрывает щель между хордами стенки
        // и окружностью; продления за концы нет.
        bool concaveArc = Vector3d.Dot(outward, axial) > 0;
        if (concaveArc)
        {
            adds = true;
            axial = outward.Normalized();
        }

        var tris = new List<(Vector3d, Vector3d, Vector3d)>();
        foreach (var s in bodies)
            tris.AddRange(Voxels.Occupancy.Triangles(Tessellator.Tessellate(s, facets: true)));

        var bounds = Bounds3.Empty;
        foreach (var s in bodies) bounds.Add(s.Bounds);

        double step = Math.Max(bounds.Diagonal * 1e-3, Tol.Weld * 10);

        // У ВЫПУКЛОГО СКРУГЛЕНИЯ МАТЕРИАЛ ЛЕЖИТ СО СТОРОНЫ ОСИ.
        //
        // ИСПРАВЛЕНО, и ошибка была не в коде, а в моём представлении
        // о том, что строит скругление. Я считал, что оно оставляет
        // материал СНАРУЖИ цилиндра, и вся арифметика среза шла от этого.
        //
        // На деле наоборот. Ось катящегося шара лежит внутри тела,
        // на расстоянии R от обеих граней. Скругление срезает уголок
        // между гранями и цилиндром, то есть всё, что ДАЛЬШЕ R от оси.
        // На кубе с углом (-50, 50) и R = 10 ось приходится на (-40, 40):
        // точка (-45, 45) отстоит от неё на 7.07 — материал; точка
        // (-49, 49) на 12.73 — вырезано.
        //
        // Отсюда и упорный отказ: проба уходила радиально НАРУЖУ, то есть
        // в срезанный уголок, и честно получала «снаружи». Все прошлые
        // круги искали ошибку в пробе, а ошибочной была сторона.
        //
        // ПРОБА ДОЛЖНА УЙТИ С ОБЕИХ ПОВЕРХНОСТЕЙ СРАЗУ.
        //
        // ИСПРАВЛЕНО. Проба ставилась только радиально: mid + radial * step.
        // Радиальный сдвиг уводит с цилиндра, но точка при этом остаётся
        // В ПЛОСКОСТИ плоской грани — то есть на поверхности тела.
        // Для луча такая точка не внутри и не снаружи: он упирается
        // в грань на нулевом расстоянии, ответ отбрасывается как сомнительный,
        // и после восьми попыток PointInSolid возвращает false.
        //
        // Ложь выходила правдоподобной: «материал со стороны оси, дуга
        // вогнутая». На выпуклой кромке скругления это неверно, но проверить
        // было нечем — отказ выглядел содержательным.
        //
        // Теперь проба сдвигается ещё и вдоль оси, внутрь тела: она сходит
        // и с цилиндра, и с плоскости, и ответ становится определённым.
        //
        // МАТЕРИАЛ БЫВАЕТ И СНАРУЖИ ОТ ОСИ — У КРАЯ ОТВЕРСТИЯ.
        //
        // ИСПРАВЛЕНО. Проба ставилась только к оси, и край круглого выреза
        // отвергался как «вогнутая дуга, добавление материала не реализовано».
        // Но у края отверстия ничего добавлять не надо: ребро выпуклое, угол
        // материала прямой, и срез его снимает. Изогнута только кромка, а не
        // угол. Материал там лежит ОТ оси, и катет вдоль плоской грани
        // откладывается наружу.
        Vector3d probeIn = mid - radial * step + axial * step;
        Vector3d probeOut = mid + radial * step + axial * step;

        // У выпуклой кромки — сторона материала; у вогнутой — сторона ПУСТОТЫ
        // над плоской гранью (axial там уже смотрит в пустоту).
        Vector3d toMaterial;
        if (concaveArc)
        {
            bool inIn = Voxels.PointInSolid.Test(tris, probeIn), inOut = Voxels.PointInSolid.Test(tris, probeOut);
            if (inIn == inOut)
                return $"cannot tell the empty side of the concave arc: the probes at {Show(probeIn)} and " +
                       $"{Show(probeOut)} came out the same";
            toMaterial = inIn ? radial : -radial;
        }
        else if (Voxels.PointInSolid.Test(tris, probeIn)) toMaterial = -radial;
        else if (Voxels.PointInSolid.Test(tris, probeOut)) toMaterial = radial;
        else
            return $"no material next to the arc: the probes at {Show(probeIn)} and " +
                   $"{Show(probeOut)} both came out outside the body";

        bool towardAxis = Vector3d.Dot(toMaterial, radial) < 0;

        if (d2 > along.Length)
            return $"the cut reaches {d2:0.###} along the fillet, which is only " +
                   $"{along.Length:0.###} long";

        // СРЕЗ ДОЛЖЕН ОСТАВИТЬ ОТ СКРУГЛЕНИЯ ХОТЬ ЧТО-ТО.
        //
        // Катет по плоской грани откладывается от кромки К ОСИ, и при
        // d1 = R он приходит ровно в ось. Сечение вырождается в треугольник
        // с вершиной на оси, тело вращения — в конус без отверстия, и резец
        // съедает весь скруглённый угол разом. Разбиение пространства
        // на таком входе даёт крошево из осколков — это и видно на модели.
        //
        // Запас в десятую долю радиуса: при d1 = 0.9R от скругления
        // остаётся узкая полоска, и дальше начинается бессмыслица.
        // ФАСКА НЕ БОЛЬШЕ РАДИУСА — КОНУС СУЩЕСТВУЕТ.
        //
        // ИСПРАВЛЕНО. Отказ стоял уже при d1 ≥ 0.9R. Но при d1 = R срез по дуге —
        // законный конус с вершиной на оси скругления, и с прямыми лентами фаски
        // он стыкуется касательно: его крайняя образующая лежит в плоскости
        // соседней ленты. Отказ оставлял дугу без фаски, прямые резцы
        // продлевались через угол и встречались косым стыком — поперечным ребром.
        //
        // Отказ остаётся только при d1 > R: смещение дуги внутрь выворачивается
        // через ось, и гладкого среза по дуге не существует.
        //
        // Радиус дуги ядро восстанавливает по точкам сетки (MergeArcs), и он
        // выходит чуть меньше заданного: 9.99999 вместо 10. Поэтому сравнение
        // с допуском в точность восстановления, а фаска в пределах допуска
        // прижимается ровно к радиусу — вершина конуса ложится точно на ось.
        double fitTol = Math.Max(1e-3, r * 1e-4);

        // Предел по радиусу — только когда катет идёт К ОСИ: у края отверстия
        // катет уходит от неё, и через ось ему не перейти.
        if (towardAxis && d1 > r + fitTol)
            return $"the cut goes {d1:0.###} across the face towards the axis, beyond the " +
                   $"fillet radius {r:0.###}; a {(rounding ? "round" : "chamfer")} larger than the round cannot follow its arc";

        if (towardAxis && d1 > r) d1 = r;

        // Сечение в плоскости «радиус — ось» у середины дуги: вершина на кромке,
        // один катет по плоской грани в сторону материала, другой вдоль стенки.
        Vector3d p0 = centre + radial * r;
        var plane = new Frame3(centre, radial, axial);

        // ПОЛОСКА ЧЕРЕЗ СТЕНКУ.
        //
        // ИСПРАВЛЕНО. Стенка гранёная: её хорды лежат внутри окружности, ближе
        // к оси, на стрелу прогиба до Tol.Chord. Резец строится по точной
        // окружности и начинался ровно на ней. У края отверстия материал снаружи
        // стенки, и слой между хордами и окружностью оставался — вертикальные
        // полоски внутри фаски. Сечение продлевается поперёк стенки, от материала,
        // на три допуска хорды: там пустота и у края отверстия, и у торца
        // скругления, и лишнего полоска не снимает.
        double strip = 3 * Tol.Chord;
        Vector3d back = -toMaterial * strip;
        double wallLeg = rounding ? d1 : d2;

        List<ICurve3>? curves;

        if (rounding)
        {
            // Скругление — то же сечение, что у прямого ребра (RoundSection):
            // наружные нормали граней в этой плоскости — против оси у плоской
            // и против материала у стенки.
            var section = RoundSection(new Frame3(p0, radial, axial), p0, -axial, -toMaterial, d1);
            if (section == null || section[1] is not ArcCurve3 arcPart) return "the round cross-section could not be built";

            Vector3d t1 = section[0].PointAt(1), t2 = section[2].PointAt(0);
            curves = new List<ICurve3>
            {
                new LineCurve3(p0 + back, t1),
                arcPart,
                new LineCurve3(t2, t2 + back),
                new LineCurve3(t2 + back, p0 + back)
            };
        }
        else
        {
            Vector3d p1 = p0 + toMaterial * d1;
            Vector3d p2 = p0 + axial * d2;

            curves = new List<ICurve3>
            {
                new LineCurve3(p0 + back, p1),
                new LineCurve3(p1, p2),
                new LineCurve3(p2, p2 + back),
                new LineCurve3(p2 + back, p0 + back)
            };
        }

        Profile profile = ProfileBuilder.Build(plane, curves);
        if (profile.IsEmpty) return "the cross-section did not close into a contour";

        // ПЕРЕХЛЁСТА ПО УГЛУ НЕТ, И ЭТО НАМЕРЕННО.
        //
        // ИСПРАВЛЕНО. Стоял градус — «чтобы крышки не легли на плоскости
        // соседних граней». Вышло хуже совпадения: крышка, разошедшаяся
        // с гранью на градус, режет её на тончайшие клинья, у каждого
        // из которых снова находится точка на стороне, и снятие T-стыков
        // уходило в удвоение треугольников за проход. Приложение вставало.
        //
        // Ровное совпадение плоскостей разбиение хотя бы РАЗБИРАЕТ:
        // у него для этого есть отдельная ветвь. Почти совпадение —
        // худший вход из возможных.
        double over = 0.0;
        double sweep = arc.Sweep;
        double sign = sweep < 0 ? -1 : 1;

        // Развёртка — от точных концов ребра, если центр точный: восстановленная
        // дуга давала 90.00011° вместо 90°, и торцы конуса поворачивались мимо.
        if (!double.IsNaN(exactSweep))
        {
            // Направление обхода: от V0 к V1 вокруг оси axis·sign.
            Vector3d u0 = edge.V0.P - centre, u1 = edge.V1.P - centre;
            double turn = Vector3d.Dot(Vector3d.Cross(u0, u1), axis);
            sign = turn < 0 ? -1 : 1;

            // У дуги длиннее полуокружности обход идёт в сторону, обратную короткому пути.
            if (longArc) sign = -sign;
            sweep = sign * exactSweep;
        }

        // Сечение построено в положении СЕРЕДИНЫ дуги, поэтому вращать
        // надо в обе стороны от неё.
        double half = Math.Abs(sweep) / 2 + over;

        // ПРОДЛЕНИЕ ЗА КОНЦЫ ДУГИ — то же правило, что у прямых рёбер (EndReach).
        //
        // ИСПРАВЛЕНО. Тело вращения шло ровно по углу дуги, и там, где дуга
        // кончается на соседней грани — например, на конической фаске, —
        // угол не срезался: соседняя грань оставалась нетронутой.
        Vector3d spin = axis * sign;
        // Прибавка: без продления, но с перекрытием на стыке с прямым соседом (см. BuildTool).
        Func<Vector3d, int>? addClaim = claimed == null ? null : p => claimed(p) == 2 ? 2 : 1;
        Func<Vector3d, int>? endClaim = adds ? addClaim ?? (_ => 1) : claimed;
        double extStart = ArcEndReach(bodies, tris, edge, flat, round, centre, spin, -half, -1,
                                      p0, toMaterial, axial, d1, wallLeg, r, endClaim);
        double extEnd = ArcEndReach(bodies, tris, edge, flat, round, centre, spin, half, +1,
                                    p0, toMaterial, axial, d1, wallLeg, r, endClaim);

        // Полный оборот не превышается.
        double room = Math.Max(0, 2 * Math.PI - 1e-3 - 2 * half);
        if (extStart + extEnd > room) { double k = room / (extStart + extEnd); extStart *= k; extEnd *= k; }

        // УГЛЫ РАЗБИЕНИЯ — ПО ВЕРШИНАМ СТЕНКИ.
        //
        // ИСПРАВЛЕНО. Резец делился по углу своим шагом, стенка — своим, и две
        // гранёные версии одной окружности перемежались: донышко резца прорастало
        // сквозь хорды стенки островками (17 у одной грани), топология тела
        // расходилась с его сеткой. Нашёл проверочный прогон (Parametric3D.Verify).
        // Теперь внутри резца — ровно углы вершин стенки, плюс концы резца.
        double lo = -half - extStart, hi = half + extEnd;
        List<double>? breaks = WallAngles(cylinder, centre, spin, radial, lo, hi);

        TSolid made = RevolveOp.Build(profile, Frame3.FromNormal(centre, spin), lo, hi, breaks);
        sweep = sign * (2 * half + extStart + extEnd);

        // РЕЗЕЦ ПРОВЕРЯЕТСЯ ДО ТОГО, КАК ИМ РЕЖУТ.
        //
        // Объём тела вращения известен заранее, без всякой геометрии:
        // по теореме Гульдина это площадь сечения, умноженная на путь
        // её центра тяжести. У треугольника с катетами d1 и d2, вершина
        // которого сидит на радиусе r, площадь равна d1*d2/2, а центр
        // тяжести отстоит от оси на r - d1/3.
        //
        // Если построенное тело весит не столько, резец собран неверно —
        // и вычитать им нельзя. Без этой проверки неверный резец уходит
        // в разбиение пространства, рвёт оболочку, и виноватой выглядит
        // булева: она возвращает тело, у которого объём уехал на порядки,
        // а разбираться приходится с конца.
        //
        // Центр тяжести сечения смещён от кромки в сторону материала: у фаски
        // на треть катета, у скругления на r(5/6 − π/4)/(1 − π/4) — это центр
        // «уголка без четверти круга». К оси — радиус меньше, от оси — больше.
        double area = rounding ? d1 * d1 * (1 - Math.PI / 4) : 0.5 * d1 * d2;
        double shift = rounding ? d1 * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4) : d1 / 3;
        double expected = area * (r + (towardAxis ? -shift : shift)) * Math.Abs(sweep)
                        + strip * wallLeg * (r + (towardAxis ? strip / 2 : -strip / 2)) * Math.Abs(sweep);
        double actual = MassProps.SignedVolume(Tessellator.Tessellate(made, facets: true));

        if (Math.Abs(Math.Abs(actual) - expected) > expected * 0.05)
            return $"the revolved tool came out wrong: its volume is {actual:0.###}, " +
                   $"but a {(rounding ? $"round ring R{d1:0.###}" : $"cone ring of {d1:0.###}x{d2:0.###}")} at radius {r:0.###} over " +
                   $"{Math.Abs(sweep) * 180 / Math.PI:0.#}\u00b0 must weigh {expected:0.###}";

        // Знак объёма говорит и о стороне: у тела с нормалями наружу он
        // положителен. Отрицательный означает вывернутый резец, а вычесть
        // вывернутым — значит вычесть всё, кроме него.
        if (actual < 0)
            return "the revolved tool came out inside out";

        tool = made;
        return null;
    }

    /// <summary>Аналитическая поверхность под гранёной обёрткой, если она там есть.</summary>
    private static ExtrudedSurface? Unwrap(ISurface3 surface) => surface switch
    {
        ExtrudedSurface ex => ex,
        FacetedSurface fs => fs.Source as ExtrudedSurface,
        _ => null
    };

    /// <summary>
    /// Плоскость сечения: нормаль вдоль ребра.
    ///
    /// Оси U и V выбираются так, чтобы U x V смотрело ВДОЛЬ ребра —
    /// от этого зависит, куда выдавливать, и ошибка здесь вывернула бы
    /// инструмент наизнанку.
    /// </summary>
    /// <summary>
    /// СТОРОНЫ НЕСИММЕТРИЧНОЙ ФАСКИ ПО ХОДУ ЦЕПОЧКИ.
    ///
    /// ИСПРАВЛЕНО. Построитель кладёт первый размер на «свою первую» грань: у ребра
    /// между плоскостями — на грань A (какую топология назвала первой), у дуги и у ребра
    /// вдоль цилиндра — на плоскую. Выбор этот у соседних рёбер не связан, и фаска 10×5
    /// на одной кромке выходила то 10×5, то 5×10.
    ///
    /// Правило, общее для цепочки (как в CAD-системах): первый размер — на грани СЛЕВА
    /// по ходу цепочки, если смотреть снаружи тела. Выбранные рёбра обходятся цепочками
    /// в одном направлении; для граней с наружными нормалями n₁, n₂ и хода t грань n₁
    /// слева, если (n₁ × n₂)·t > 0. Правило верно и для вогнутых рёбер: «верх» — биссектриса
    /// наружных нормалей, она смотрит в пустоту. Возвращает для каждого ребра, менять ли
    /// размеры местами.
    /// </summary>
    private static bool[] ChamferSides(List<TEdge> edges)
    {
        int n = edges.Count;
        var dir = new int[n];                       // +1: V0→V1, −1: навстречу

        (long, long, long) Key(Vector3d p) => ((long)Math.Round(p.X * 1e6), (long)Math.Round(p.Y * 1e6), (long)Math.Round(p.Z * 1e6));
        var at = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < n; i++)
            foreach (var p in new[] { edges[i].V0.P, edges[i].V1.P })
            {
                var k = Key(p);
                if (!at.TryGetValue(k, out var l)) at[k] = l = new List<int>();
                l.Add(i);
            }

        // Обход цепочками. Начало — ГЕОМЕТРИЧЕСКОЕ, не по порядку выбора: иначе та же
        // кромка, выбранная в обратном порядке, получала размеры наоборот. Открытая
        // цепочка начинается с конца с наименьшей точкой (по X, затем Y, затем Z),
        // замкнутая — с наименьшей вершины, в сторону ребра с меньшим другим концом.
        static bool Less(Vector3d a, Vector3d b) =>
            a.X < b.X - 1e-9 || (Math.Abs(a.X - b.X) <= 1e-9 && (a.Y < b.Y - 1e-9 || (Math.Abs(a.Y - b.Y) <= 1e-9 && a.Z < b.Z - 1e-9)));

        while (true)
        {
            int start = -1; Vector3d from = default;

            // Концы открытых цепочек: вершина, где осталось одно необойдённое ребро.
            for (int i = 0; i < n; i++)
            {
                if (dir[i] != 0) continue;
                foreach (var p in new[] { edges[i].V0.P, edges[i].V1.P })
                    if (at[Key(p)].Count(j => dir[j] == 0) == 1 && (start < 0 || Less(p, from))) { start = i; from = p; }
            }

            // Замкнутые: наименьшая вершина, ребро к меньшему другому концу.
            if (start < 0)
            {
                Vector3d otherBest = default;
                for (int i = 0; i < n; i++)
                {
                    if (dir[i] != 0) continue;
                    foreach (var (p, q) in new[] { (edges[i].V0.P, edges[i].V1.P), (edges[i].V1.P, edges[i].V0.P) })
                        if (start < 0 || Less(p, from) || (!Less(from, p) && Less(q, otherBest)))
                        { start = i; from = p; otherBest = q; }
                }
            }
            if (start < 0) break;

            int e = start;
            Vector3d v = from;
            while (e >= 0)
            {
                bool forward = Key(edges[e].V0.P).Equals(Key(v));
                dir[e] = forward ? 1 : -1;
                v = forward ? edges[e].V1.P : edges[e].V0.P;
                e = at[Key(v)].FirstOrDefault(j => dir[j] == 0, -1);
            }
        }

        var swap = new bool[n];
        for (int i = 0; i < n; i++)
        {
            TEdge edge = edges[i];
            if (edge.FaceA == null || edge.FaceB == null) continue;

            // Грань, на которую построитель кладёт первый размер (см. BuildTool,
            // BuildArcTool, BuildLineOnCylinderTool).
            TFace f1 = SizeOneFace(edge), f2 = ReferenceEquals(f1, edge.FaceA) ? edge.FaceB : edge.FaceA;

            Vector3d mid = edge.Curve.PointAt(0.5);
            Vector3d t = edge.Curve.PointAt(0.5 + 1e-4) - edge.Curve.PointAt(0.5 - 1e-4);
            if (t.LengthSquared < 1e-24) continue;

            // Направление обхода (dir) считается от V0 к V1, а касательная — по параметру
            // кривой. Кривая ребра бывает записана навстречу вершинам: тогда касательная
            // разворачивается, иначе «рука» у этого ребра перевернулась бы.
            Vector3d start = edge.Curve.PointAt(0);
            if ((start - edge.V0.P).LengthSquared > (start - edge.V1.P).LengthSquared) t = -t;

            t = t.Normalized() * dir[i];

            Vector3d n1 = OutwardNormalAt(f1, mid), n2 = OutwardNormalAt(f2, mid);
            swap[i] = Vector3d.Dot(Vector3d.Cross(n1, n2), t) < 0;
        }

        // ОБЩАЯ ГРАНЬ — ГЛАВНОЕ ПРАВИЛО.
        //
        // ИСПРАВЛЕНО. Правило «по одну руку от обхода» давало согласованность только
        // внутри связной цепочки, и то непредсказуемую: у нижнего угла передней грани
        // 10 ложилось на переднюю грань, у верхнего — на боковую. Несвязанные рёбра
        // ориентировались каждое само по себе, и у вертикального ребра выходило 10×5,
        // у горизонтального — 5×10. Теперь, если у всех выбранных рёбер ровно одна
        // общая грань, первый размер ложится на неё — на каждом ребре: «10×5» значит
        // «10 по общей грани, 5 по боковым». Нет общей грани — правило по руке.
        if (n >= 2 && edges.All(e => e.FaceA != null && e.FaceB != null))
        {
            var common = new HashSet<TFace>(new[] { edges[0].FaceA!, edges[0].FaceB! }, ReferenceEqualityComparer.Instance);
            foreach (var e in edges.Skip(1))
                common.IntersectWith(new[] { e.FaceA!, e.FaceB! });

            if (common.Count == 1)
            {
                TFace shared = common.First();
                for (int i = 0; i < n; i++)
                    swap[i] = !ReferenceEquals(SizeOneFace(edges[i]), shared);
            }
        }

        return swap;
    }

    /// <summary>Грань, на которую построитель резца кладёт первый размер.</summary>
    /// <summary>KRN-06 (06.15): грань, на которую ложится ПЕРВЫЙ размер несимметричной фаски, — по тому же правилу, что у резцов
    /// (SizeOneFace и сторона по ходу цепочки, ChamferSides).</summary>
    internal static Dictionary<TEdge, TFace> FirstSizeFaces(List<TEdge> edges)
    {
        var swap = ChamferSides(edges); var res = new Dictionary<TEdge, TFace>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i]; if (e.FaceA == null || e.FaceB == null) continue;
            var f1 = SizeOneFace(e); var f2 = ReferenceEquals(f1, e.FaceA) ? e.FaceB : e.FaceA;
            res[e] = swap[i] ? f2 : f1;
        }
        return res;
    }

    private static TFace SizeOneFace(TEdge edge)
    {
        TFace a = edge.FaceA!, b = edge.FaceB!;
        if (edge.Curve is ArcCurve3) return a.Surface.IsPlanar ? a : b;            // дуга: плоская
        if (a.Surface.IsPlanar && CylinderOf(b) != null) return a;                 // вдоль цилиндра: плоская
        if (b.Surface.IsPlanar && CylinderOf(a) != null) return b;
        return a;                                                                  // две плоскости: A
    }

    /// <summary>
    /// Наружная нормаль грани у точки. У гранёной — по ближайшему к точке треугольнику:
    /// NormalAt гранёной грани повёрнут по исходной поверхности, а не по телу.
    /// </summary>
    private static Vector3d OutwardNormalAt(TFace face, Vector3d p)
    {
        if (face.Surface is FacetedSurface fs && fs.Facets.Count > 0)
        {
            double best = double.MaxValue; Vector3d nn = Vector3d.Zero;
            foreach (var (a, b, c) in fs.Facets)
            {
                Vector3d cr = Vector3d.Cross(b - a, c - a);
                if (cr.LengthSquared < 1e-24) continue;
                double d = (ClosestOnTriangle(p, a, b, c) - p).LengthSquared;
                if (d < best) { best = d; nn = cr.Normalized(); }
            }
            return nn;
        }
        Vector3d m = face.NormalAt(p);
        return m.LengthSquared > 1e-24 ? m.Normalized() : Vector3d.Zero;
    }

    private static Vector3d ClosestOnTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d ab = b - a, ac = c - a, ap = p - a;
        double d1 = Vector3d.Dot(ab, ap), d2 = Vector3d.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3d bp = p - b;
        double d3 = Vector3d.Dot(ab, bp), d4 = Vector3d.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3d cp = p - c;
        double d5 = Vector3d.Dot(ab, cp), d6 = Vector3d.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        double den = 1 / (va + vb + vc);
        return a + ab * (vb * den) + ac * (vc * den);
    }

    /// <summary>Цилиндр выдавливания под гранью (исходная поверхность гранёной грани), иначе null.</summary>
    private static ExtrudedSurface? CylinderOf(TFace face)
    {
        ISurface3? src = face.Surface is FacetedSurface fs ? fs.Source : face.Surface;
        return src is ExtrudedSurface ex && ex.Base is ArcCurve3 ? ex : null;
    }

    /// <summary>
    /// РЕЗЕЦ ПРЯМОГО РЕБРА ВДОЛЬ ОБРАЗУЮЩЕЙ ЦИЛИНДРА.
    ///
    /// ДОБАВЛЕНО. Прежде отказ «обе грани должны быть плоскими» — самый частый
    /// отказ проверочного прогона (Parametric3D.Verify). Вдоль образующей сечение
    /// постоянно: в плоскости сечения грань — прямая, цилиндр — окружность радиуса R.
    /// Скругление — окружность радиуса r, касающаяся прямой и окружности стенки;
    /// фаска — от точки на грани в d1 от угла до точки на стенке в d2 по стенке.
    /// Резец — выдавливание сечения вдоль оси. Выпуклое ребро — срез, вогнутое —
    /// прибавка (как у двух плоскостей).
    ///
    /// Сторона сечения вдоль стенки строится НЕ по точной окружности, а по вершинам
    /// хорд самой стенки: грани резца ложатся ровно на её грани, и не остаётся ни
    /// полосок между хордой и окружностью, ни перемежающихся островков.
    /// </summary>
    private static string? BuildLineOnCylinderTool(List<TSolid> bodies, TEdge edge, TFace fp, TFace fc,
                                                   ExtrudedSurface cyl, double size1, double size2, bool round,
                                                   Func<Vector3d, int>? claimed, out TSolid? tool, out bool adds)
    {
        tool = null;
        adds = false;

        var baseArc = (ArcCurve3)cyl.Base;
        Vector3d axisDir = cyl.Dir.Normalized();

        Vector3d a = edge.V0.P, b = edge.V1.P;
        double length = (b - a).Length;
        // Ребро в десятки раз короче самого среза — обрубок, нарезанный соседними
        // элементами: резец по нему почти целиком состоит из продлений за концы,
        // их торцы ложатся поперёк чужих граней, и результат выходит незамкнутым.
        // Нашёл проверочный прогон (Parametric3D.Verify).
        if (length < Math.Max(1e-3, 0.05 * Math.Max(size1, size2)))
            return $"the edge is only {length:0.######} long — too short for a cut of {Math.Max(size1, size2):0.###}";

        Vector3d t = (b - a) / length;
        if (Math.Abs(Vector3d.Dot(t, axisDir)) < 1 - 1e-6)
            return "the straight edge on the cylinder does not run along its axis";

        double R = baseArc.Radius;
        Vector3d C0 = baseArc.Frame.Origin;
        Vector3d mid = (a + b) / 2;

        Vector3d CentreAt(Vector3d p) => C0 + axisDir * Vector3d.Dot(p - C0, axisDir);

        // Нормали в плоскости сечения: плоской грани и цилиндра (наружная, по знаку грани).
        Vector3d nP = fp.NormalAt(mid);
        nP = (nP - t * Vector3d.Dot(nP, t)).Normalized();
        Vector3d radialMid = mid - CentreAt(mid);
        if (radialMid.LengthSquared < 1e-18) return "the edge lies on the cylinder axis";
        radialMid = radialMid.Normalized();
        if (Math.Abs(Vector3d.Dot(nP, radialMid)) > 1 - 1e-6)
            return "the faces meet tangentially here — a smooth join, not an edge";

        // СТОРОНА МАТЕРИАЛА У СТЕНКИ — ПРОБОЙ, А НЕ ПО НОРМАЛИ ИСХОДНОГО ЦИЛИНДРА.
        //
        // Нормаль гранёной грани повёрнута по построению исходного цилиндра, а не по
        // телу: у отверстия исходный цилиндр — резец, и его «наружу» смотрит в тело.
        // Пробы чуть за плоской гранью, сдвинутые от оси и к оси: где материал,
        // туда и не смотрит наружная нормаль стенки. Если материал с обеих сторон
        // стенки — угол вогнутый (стенка за плоской гранью не продолжается), и
        // сторона берётся по второй паре проб — чуть перед плоской гранью.
        var probeTris = new List<(Vector3d, Vector3d, Vector3d)>();
        foreach (var s0 in bodies) probeTris.AddRange(Voxels.Occupancy.Triangles(Tessellator.Tessellate(s0, facets: true)));
        double pr = Math.Max(0.01, Math.Min(size1, R) * 0.05);
        bool behindOut = Voxels.PointInSolid.Test(probeTris, mid - nP * pr + radialMid * pr);
        bool behindIn = Voxels.PointInSolid.Test(probeTris, mid - nP * pr - radialMid * pr);
        bool frontOut = Voxels.PointInSolid.Test(probeTris, mid + nP * pr + radialMid * pr);
        bool frontIn = Voxels.PointInSolid.Test(probeTris, mid + nP * pr - radialMid * pr);

        Vector3d nC;
        if (behindOut != behindIn) nC = behindOut ? -radialMid : radialMid;        // выпуклый угол
        else if (frontOut != frontIn) nC = frontOut ? -radialMid : radialMid;      // вогнутый угол
        else return "cannot tell which side of the cylinder wall the material is on";

        adds = !Convex(bodies, mid, nP, nC);

        // Нормали «в сторону области»: у среза — наружные, у прибавки — развёрнутые.
        Vector3d n1 = adds ? -nP : nP, n2 = adds ? -nC : nC;
        Vector3d f1 = AlongFace(n1, t, n2);      // вдоль плоской грани от угла
        Vector3d f2 = AlongFace(n2, t, n1);      // вдоль стенки от угла (касательная)

        double need1 = size1, need2 = size2;

        // Продление за концы — как у двух плоскостей (касательная плоскость цилиндра
        // у ребра постоянна вдоль образующей); у прибавки — без продления.
        int claimA = claimed?.Invoke(a) ?? 0, claimB = claimed?.Invoke(b) ?? 0;
        if (adds) { if (claimA == 0) claimA = 1; if (claimB == 0) claimB = 1; }
        double overlap = ClaimedOverlap(Math.Max(need1, need2));
        double extA = claimA == 0 ? EndReach(bodies, fp, fc, a, -t, n1, n2, f1, f2, need1, need2) : claimA == 2 ? overlap : 0;
        double extB = claimB == 0 ? EndReach(bodies, fp, fc, b, t, n1, n2, f1, f2, need1, need2) : claimB == 2 ? overlap : 0;

        Vector3d corner = a - t * extA;
        Vector3d Cc = CentreAt(corner);

        // Вершины хорд стенки в плоскости сечения, по порядку вдоль окружности.
        var basePts = new List<Vector3d>();
        baseArc.Tessellate(basePts, true);
        var wall = basePts.Select(p => p + axisDir * Vector3d.Dot(corner - p, axisDir)).ToList();
        bool closed = (wall[0] - wall[^1]).Length < 1e-9;
        if (closed) wall.RemoveAt(wall.Count - 1);
        int n = wall.Count;
        if (n < 2) return "the cylinder wall has too few vertices";

        // Хорда стенки, на которой лежит угол.
        int seg = -1; double bestD = double.MaxValue;
        int segCount = closed ? n : n - 1;
        for (int i = 0; i < segCount; i++)
        {
            double d = (ClosestOnSegment(corner, wall[i], wall[(i + 1) % n]) - corner).Length;
            if (d < bestD) { bestD = d; seg = i; }
        }
        if (bestD > Math.Max(1e-4, 3 * Tol.Chord)) return "the edge does not lie on the cylinder wall";

        // Направление обхода стенки в сторону области.
        int dir = Vector3d.Dot(wall[(seg + 1) % n] - wall[seg], f2) >= 0 ? 1 : -1;

        // Обход ломаной стенки от угла до точки остановки; промежуточные вершины и конец.
        (List<Vector3d> Path, Vector3d End)? Walk(Func<Vector3d, Vector3d, double, (bool Stop, Vector3d At)> stopOn)
        {
            var path = new List<Vector3d>();
            Vector3d here = corner;
            int k = dir > 0 ? (seg + 1) % n : seg;
            double walked = 0;
            for (int guard = 0; guard <= n; guard++)
            {
                Vector3d next = wall[k];
                var (stop, at) = stopOn(here, next, walked);
                if (stop) return (path, at);
                walked += (next - here).Length;
                path.Add(next);
                here = next;
                int nk = k + dir;
                if (!closed && (nk < 0 || nk >= n)) return null;
                k = ((nk % n) + n) % n;
            }
            return null;
        }

        var curves = new List<ICurve3>();

        if (!round)
        {
            string? tooBig = Fits(fp, a, f1, size1, "first");
            if (tooBig != null) return tooBig;

            Vector3d A = corner + f1 * size1;
            var walk = Walk((p, q, done) =>
            {
                double segLen = (q - p).Length;
                if (done + segLen < size2) return (false, default);
                return (true, p + (q - p) * ((size2 - done) / segLen));
            });
            if (walk == null) return $"the cut reaches {size2:0.###} along the cylinder, beyond the end of its face";
            var (path, B) = walk.Value;

            curves.Add(new LineCurve3(corner, A));
            curves.Add(new LineCurve3(A, B));
            Vector3d from = B;
            for (int i = path.Count - 1; i >= 0; i--) { curves.Add(new LineCurve3(from, path[i])); from = path[i]; }
            curves.Add(new LineCurve3(from, corner));
        }
        else
        {
            double r = size1;

            // СКРУГЛЕНИЕ КАСАЕТСЯ ХОРДЫ СТЕНКИ, А НЕ ТОЧНОЙ ОКРУЖНОСТИ.
            //
            // Сторона резца вдоль стенки идёт по её хордам, а они лежат внутри
            // окружности на стрелу прогиба. Скругление, касающееся точной окружности,
            // у точки касания проходило между хордой и окружностью и пересекало хорду —
            // контур сечения пересекал сам себя. Теперь для каждой хорды на пути от угла
            // решается касание двух прямых — плоской грани и хорды, — и берётся та хорда,
            // на которую точка касания действительно попадает.
            Vector3d w = Vector3d.Cross(t, nP).Normalized();
            double sideSign = Vector3d.Dot(n2, radialMid) >= 0 ? 1 : -1;   // n2 = sideSign · радиальное

            Vector3d Q = default, T1 = default, T2 = default;
            bool found = false;

            var walk = Walk((p, q, _) =>
            {
                Vector3d dirPQ = q - p;
                if (dirPQ.LengthSquared < 1e-24) return (false, default);
                Vector3d nChord = Vector3d.Cross(dirPQ, t).Normalized();
                Vector3d radialHere = ((p + q) / 2 - Cc);
                if (Vector3d.Dot(nChord, radialHere) * sideSign < 0) nChord = -nChord;

                double den = Vector3d.Dot(w, nChord);
                if (Math.Abs(den) < 1e-12) return (false, default);

                // Q = corner − r·n1 + y·w;  расстояние до прямой хорды = r (со стороны области).
                Vector3d baseQ = corner - n1 * r;
                double y = (-r - Vector3d.Dot(baseQ - p, nChord)) / den;
                Vector3d q0 = baseQ + w * y;
                Vector3d t2 = q0 + nChord * r;
                double k = Vector3d.Dot(t2 - p, dirPQ) / dirPQ.LengthSquared;
                if (k < -1e-9 || k > 1 + 1e-9) return (false, default);

                Q = q0; T1 = q0 + n1 * r; T2 = t2; found = true;
                return (true, t2);
            });

            if (walk == null || !found) return "the round does not touch both the face and the cylinder wall";
            if (Vector3d.Dot(T1 - corner, f1) <= 0) return "the round does not touch the face on the side of the edge";

            string? tooBig = Fits(fp, a, f1, (T1 - corner).Length, "first");
            if (tooBig != null) return tooBig;

            var (path, T2w) = walk.Value;

            // Дуга скругления от T1 к T2 — короткая, со стороны угла.
            Vector3d u1 = (T1 - Q).Normalized();
            Vector3d v1 = Vector3d.Cross(t, u1).Normalized();
            Vector3d d2v = (T2 - Q).Normalized();
            double ang = Math.Atan2(Vector3d.Dot(d2v, v1), Vector3d.Dot(d2v, u1));

            curves.Add(new LineCurve3(corner, T1));
            curves.Add(new ArcCurve3(new Frame3(Q, u1, v1), r, 0, ang));
            Vector3d from = T2;
            for (int i = path.Count - 1; i >= 0; i--) { curves.Add(new LineCurve3(from, path[i])); from = path[i]; }
            curves.Add(new LineCurve3(from, corner));
        }

        Frame3 section = SectionFrame(corner, t);
        Profile profile = ProfileBuilder.Build(section, curves);
        if (profile.IsEmpty) return "the cross-section did not close into a contour";

        tool = ExtrudeOp.Build(profile, t * (length + extA + extB));
        return null;
    }

    private static Vector3d ClosestOnSegment(Vector3d p, Vector3d a, Vector3d b)
    {
        Vector3d ab = b - a;
        double l2 = ab.LengthSquared;
        if (l2 < 1e-24) return a;
        double k = Math.Clamp(Vector3d.Dot(p - a, ab) / l2, 0, 1);
        return a + ab * k;
    }

    // ================================================================ протянутый резец
    /// <summary>
    /// Ребро, которое не берёт ни один специальный построитель: кривая общего вида,
    /// прямая или дуга между двумя кривыми гранями (кроме прямой вдоль образующей
    /// цилиндра и дуги на плоскости с цилиндрической стенкой).
    /// </summary>
    /// <summary>ДИАГНОСТИКА: выпуклость ребра так, как её видит построитель (грани, их нормали у середины).</summary>
    public static string DescribeEdge(List<TSolid> bodies, TEdge e)
    {
        Vector3d p = e.Curve.PointAt(0.5);
        double tol = Tol.Weld * 100;
        Vector3d na = SmoothNormal(e.FaceA!, p, tol), nb = SmoothNormal(e.FaceB!, p, tol);
        string K(TFace f) => f.Surface is FacetedSurface fs ? $"Faceted({fs.Source?.GetType().Name}) planar={f.Surface.IsPlanar}" : $"{f.Surface.GetType().Name} planar={f.Surface.IsPlanar}";
        return $"A: {K(e.FaceA!)} n=({na.X:0.#####},{na.Y:0.#####},{na.Z:0.#####}); B: {K(e.FaceB!)} n=({nb.X:0.#####},{nb.Y:0.#####},{nb.Z:0.#####}); " +
               $"NeedsSweep={NeedsSweep(e)}; Convex={Convex(bodies, p, na, nb, e)}";
    }

    public static bool NeedsSweep(TEdge e)
    {
        if (e.FaceA == null || e.FaceB == null) return false;
        bool pa = e.FaceA.Surface.IsPlanar, pb = e.FaceB.Surface.IsPlanar;

        if (e.Curve is LineCurve3)
        {
            if (pa && pb) return false;
            ExtrudedSurface? cyl = pa ? CylinderOf(e.FaceB) : pb ? CylinderOf(e.FaceA) : null;
            Vector3d d = e.V1.P - e.V0.P;
            if (cyl != null && d.LengthSquared > 0 &&
                Math.Abs(Vector3d.Dot(d.Normalized(), cyl.Dir.Normalized())) > 1 - 1e-6) return false;
            return true;
        }

        if (e.Curve is ArcCurve3)
            return !((pa && CylinderOf(e.FaceB) != null) || (pb && CylinderOf(e.FaceA) != null));

        return true;
    }

    /// <summary>
    /// ФАСКА, ПРОТЯНУТАЯ ВДОЛЬ ЦЕПОЧКИ РЁБЕР.
    ///
    /// ДОБАВЛЕНО. Фаска — не набор готовых фигур (призма у прямой, конус у дуги),
    /// а резец, протянутый вдоль всей выбранной цепочки: прямые, дуги и кривые
    /// пересечения поверхностей подряд. Край отверстия, уходящий с плоского верха
    /// на скругление, — дуга плюс полсотни отрезков между двумя цилиндрами; конус
    /// брал только дугу и обрывался ступенькой.
    ///
    /// В каждой точке цепочки — сечение в плоскости, перпендикулярной ребру:
    /// A — на первой грани в d1 от ребра, B — на второй в d2 (обе спроецированы на
    /// сами грани), O — вынесена наружу за угол, чтобы треугольник накрыл весь
    /// уголок материала. Сечения соединяются по цепочке в одно тело: замкнутая
    /// цепочка — кольцо без торцов, открытая — продлевается на концах тем же
    /// правилом, что у прямых рёбер (EndReach).
    ///
    /// Точки сечений — не по формуле кривой, а в ВЕРШИНАХ граней, лежащих на самом
    /// ребре: стороны резца совпадают с хордами граней, и не остаётся ни полосок,
    /// ни перемежающихся островков.
    /// </summary>
    /// <summary>ДИАГНОСТИКА: объёмы отрезков протянутого резца.</summary>
    public static Action<string>? SweepDebug;

    /// <summary>ДИАГНОСТИКА: сечения протянутого резца (точка ребра, A, B, O).</summary>
    public static Action<Vector3d, Vector3d, Vector3d, Vector3d>? SweepSections;

    private static TSolid? BuildSweptChamfer(List<TSolid> bodies, List<(TEdge Edge, bool Swap)> chain,
                                             double d1, double d2, out string? refusal, bool round = false,
                                             Func<double, double>? radiusAt = null)
    {
        refusal = null;

        // ---- порядок цепочки: концы по вершинам, обход от конца (или откуда угодно у кольца)
        static (double, double, double) K(Vector3d p) => (Math.Round(p.X, 7), Math.Round(p.Y, 7), Math.Round(p.Z, 7));
        var at = new Dictionary<(double, double, double), List<int>>();
        for (int i = 0; i < chain.Count; i++)
            foreach (var p in new[] { chain[i].Edge.V0.P, chain[i].Edge.V1.P })
            {
                if (!at.TryGetValue(K(p), out var l)) at[K(p)] = l = new List<int>();
                l.Add(i);
            }
        if (at.Values.Any(l => l.Count > 2)) { refusal = "the chain branches; a swept chamfer needs a simple chain"; return null; }

        var ends = at.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToList();
        bool closed = ends.Count == 0;
        int first = closed ? 0 : at[ends[0]][0];
        Vector3d from = closed ? chain[0].Edge.V0.P
                      : K(chain[first].Edge.V0.P).Equals(ends[0]) ? chain[first].Edge.V0.P : chain[first].Edge.V1.P;

        var order = new List<(TEdge Edge, bool Swap, Vector3d From, Vector3d To)>();
        var used = new bool[chain.Count];
        int cur = first; Vector3d v = from;
        while (cur >= 0 && !used[cur])
        {
            used[cur] = true;
            var e = chain[cur].Edge;
            Vector3d to = K(e.V0.P).Equals(K(v)) ? e.V1.P : e.V0.P;
            order.Add((e, chain[cur].Swap, v, to));
            v = to;
            cur = at[K(v)].FirstOrDefault(j => !used[j], -1);
        }
        if (order.Count != chain.Count) { refusal = "the chain could not be walked end to end"; return null; }

        // ---- точки сечений: вершины граней на самом ребре
        var samples = new List<(Vector3d P, TFace F1, TFace F2)>();
        var sampleK = new List<int>();                    // номер ребра цепочки у сечения
        var edgeFaces = new List<(TFace F1, TFace F2)>();  // грани каждого ребра по порядку
        TFace? prevF1 = null, prevF2 = null;
        bool symmetric = round || Math.Abs(d1 - d2) < 1e-12;
        foreach (var (e, sw, a, b) in order)
        {
            TFace s1 = SizeOneFace(e), s2 = ReferenceEquals(s1, e.FaceA) ? e.FaceB! : e.FaceA!;
            TFace f1 = sw ? s2 : s1, f2 = sw ? s1 : s2;

            // ПОРЯДОК ГРАНЕЙ НАСЛЕДУЕТСЯ ВДОЛЬ ЦЕПОЧКИ (когда размеры равны).
            //
            // ИСПРАВЛЕНО. «Первая» грань у каждого ребра своя: у прямой — плоская, у дуги —
            // та, что записана первой. Сечения на прямых и на дуге обходились в разные
            // стороны, полосы соединяли точку на одной грани с точкой на другой, и резец
            // выворачивался (скругление по кромке фаски 10×5: отрезок объёмом −155).
            // Та же грань — на той же стороне; нет общей — ближайшая по нормали в стыке.
            // У фаски с разными размерами сторону задаёт правило общей грани (ChamferSides).
            if (symmetric && prevF1 != null && prevF2 != null)
            {
                bool keep = ReferenceEquals(f1, prevF1) || ReferenceEquals(f2, prevF2);
                bool flip = ReferenceEquals(f2, prevF1) || ReferenceEquals(f1, prevF2);
                if (!keep && flip) (f1, f2) = (f2, f1);
                else if (!keep)
                {
                    double tl = Tol.Weld * 100;
                    Vector3d np = SmoothNormal(prevF1, a, tl);
                    if (Vector3d.Dot(SmoothNormal(f2, a, tl), np) > Vector3d.Dot(SmoothNormal(f1, a, tl), np)) (f1, f2) = (f2, f1);
                }
            }
            prevF1 = f1; prevF2 = f2;
            edgeFaces.Add((f1, f2));

            var pts = OnEdgePoints(e, a, b);
            // В ОДНОЙ ТОЧКЕ — ОДНО СЕЧЕНИЕ. На стыке рёбер с разными гранями (дуга на
            // верху → кривая на скруглении) два сечения в одной точке давали полосу
            // нулевой длины — плоский лист, резец перекрывал сам себя, и булева теряла
            // замкнутость. Остаётся сечение по граням уходящего ребра.
            if (samples.Count > 0 && (samples[^1].P - pts[0]).LengthSquared < 1e-18)
            { samples.RemoveAt(samples.Count - 1); sampleK.RemoveAt(sampleK.Count - 1); }
            foreach (var p in pts) { samples.Add((p, f1, f2)); sampleK.Add(edgeFaces.Count - 1); }
        }
        if (closed && samples.Count > 1 && (samples[^1].P - samples[0].P).LengthSquared < 1e-18)
        { samples.RemoveAt(samples.Count - 1); sampleK.RemoveAt(sampleK.Count - 1); }

        // Точки ближе 2% размера к предыдущей — пропускаются (кроме концов цепочки): формы
        // они не добавляют, а сечения, поставленные через тысячные, перехлёстывались от
        // малейшего поворота.
        {
            double minGap = Math.Max(1e-3, 0.02 * Math.Max(d1, d2));
            var thin = new List<(Vector3d P, TFace F1, TFace F2)>();
            var thinK = new List<int>();
            for (int i = 0; i < samples.Count; i++)
            {
                bool last = i == samples.Count - 1;
                if (thin.Count > 0 && !last && (samples[i].P - thin[^1].P).Length < minGap) continue;
                if (thin.Count > 1 && last && !closed && (samples[i].P - thin[^1].P).Length < minGap)
                { thin.RemoveAt(thin.Count - 1); thinK.RemoveAt(thinK.Count - 1); }
                thin.Add(samples[i]); thinK.Add(sampleK[i]);
            }
            if (closed && thin.Count > 2 && (thin[^1].P - thin[0].P).Length < minGap)
            { thin.RemoveAt(thin.Count - 1); thinK.RemoveAt(thinK.Count - 1); }
            samples = thin; sampleK = thinK;
        }
        if (samples.Count < 2) { refusal = "the chain is too short to sweep along"; return null; }

        // ---- сечения
        double tol = Tol.Weld * 100;

        // Вынос O за угол — с небольшим запасом, а не вдвое больше фаски: его хватает,
        // чтобы накрыть выпуклость кривой грани между ребром и точкой фаски, а при
        // поворотах сечения качаются в разы меньше (при выносе 2·d соседние сечения
        // над стыком верха со скруглением перехлёстывались, и резец пересекал сам себя).
        // Грани соседних рёбер цепочки на той же стороне — для точки фаски за линией касания.
        IEnumerable<TFace> NeighbourFaces(int i, bool first)
        {
            int k = sampleK[i], E = edgeFaces.Count;
            foreach (int j in new[] { k - 1, k + 1 })
            {
                int jj = closed ? (j + E) % E : j;
                if (jj < 0 || jj >= E || jj == k) continue;
                yield return first ? edgeFaces[jj].F1 : edgeFaces[jj].F2;
            }
        }
        double reach = 0.6 * Math.Max(d1, d2);
        int n = samples.Count;
        var A = new Vector3d[n]; var B = new Vector3d[n]; var O = new Vector3d[n]; var T = new Vector3d[n];
        var N1 = new Vector3d[n]; var N2 = new Vector3d[n]; var F1v = new Vector3d[n]; var F2v = new Vector3d[n];
        var A0 = new Vector3d[n]; var B0 = new Vector3d[n];      // точки фаски до продления наружу

        for (int i = 0; i < n; i++)
        {
            var (p, f1, f2) = samples[i];

            // Касательная — по соседним несовпадающим точкам цепочки.
            Vector3d prev = p, next = p;
            for (int k = 1; k < n && (prev - p).LengthSquared < 1e-18; k++)
                prev = closed ? samples[(i - k + n) % n].P : samples[Math.Max(0, i - k)].P;
            for (int k = 1; k < n && (next - p).LengthSquared < 1e-18; k++)
                next = closed ? samples[(i + k) % n].P : samples[Math.Min(n - 1, i + k)].P;
            Vector3d chainDir = next - prev;
            if (chainDir.LengthSquared < 1e-24) { refusal = "the chain has a degenerate point"; return null; }
            chainDir = chainDir.Normalized();

            Vector3d n1 = SmoothNormal(f1, p, tol), n2 = SmoothNormal(f2, p, tol);

            // КАСАТЕЛЬНАЯ — ПО ГЛАДКИМ НОРМАЛЯМ: ребро — пересечение двух поверхностей,
            // и его направление — n1 × n2. По соседним точкам ломаной оно скачет:
            // вершины края на скруглении стоят то через 0.1, то через 1 мм, направление
            // ломаной прыгает на градусы, и соседние сечения в 3 мм перехлёстывались.
            // Знак — по направлению цепочки; у почти касательных граней — сама цепочка.
            Vector3d t = Vector3d.Cross(n1, n2);
            if (t.LengthSquared < 1e-6) t = chainDir;
            else
            {
                t = t.Normalized();
                if (Vector3d.Dot(t, chainDir) < 0) t = -t;
            }
            n1 = (n1 - t * Vector3d.Dot(n1, t)); n2 = (n2 - t * Vector3d.Dot(n2, t));
            if (n1.LengthSquared < 1e-18 || n2.LengthSquared < 1e-18) { refusal = "a face is tangent to the edge"; return null; }
            n1 = n1.Normalized(); n2 = n2.Normalized();
            if (Vector3d.Dot(n1, n2) > 1 - 1e-6) { refusal = "the faces meet tangentially here — a smooth join, not an edge"; return null; }

            Vector3d g1 = AlongFace(n1, t, n2), g2 = AlongFace(n2, t, n1);
            A[i] = OntoFaceNear(f1, NeighbourFaces(i, first: true), p + g1 * d1);
            B[i] = OntoFaceNear(f2, NeighbourFaces(i, first: false), p + g2 * d2);

            // ЛИНИЯ ФАСКИ ПРОДЛЕВАЕТСЯ ЗА A И B НАРУЖУ.
            //
            // ИСПРАВЛЕНО. A и B стоят на точной поверхности, а тело гранёное: на
            // выпуклом скруглении хорда между соседними A уходит под грани, на стенке
            // отверстия B и вовсе внутри материала (хорды стенки ближе к оси). Внешние
            // полосы резца местами задевали тело, в теле появлялись крошечные грани
            // (0.08 и 0.67 мм² на 33 и 129 рёбер), кромка фаски дробилась на огрызки,
            // и цепочка по ней рвалась. Продлённая линия выходит из тела по обе стороны,
            // и тело пересекает только полоса самой фаски. Форма фаски не меняется:
            // на плоской грани линия пересекает её ровно в A.
            //
            // По ВСЕЙ линии, и у плоских граней тоже: исключение для плоских давало неровный
            // стык кромки на линии касания плоскости со скруглением (точная точка с одной
            // стороны, продлённая — с другой), и кромки сливались в петлю с огрызками.
            // Цена — положение кромки на плоской грани с точностью шага сетки (1e-5 мм).
            A0[i] = A[i]; B0[i] = B[i];
            Vector3d hyp = A[i] - B[i];
            if (hyp.LengthSquared > 1e-18)
            {
                hyp = hyp.Normalized();
                double past = Math.Max(0.1, 0.1 * Math.Max(d1, d2));
                A[i] += hyp * past;
                B[i] -= hyp * past;
            }
            O[i] = p + (n1 + n2).Normalized() * (reach + Math.Max(0.1, 0.1 * Math.Max(d1, d2)));
            T[i] = t; N1[i] = n1; N2[i] = n2; F1v[i] = g1; F2v[i] = g2;
        }

        // ФАСКА КРУПНЕЕ СКРУГЛЕНИЯ — прежнее объяснение, как у резца по дуге: точка фаски
        // на плоской грани, уходящая к оси цилиндра дальше его радиуса, проскочила бы ось.
        for (int i = 0; i < n; i++)
        {
            var (p, f1, f2) = samples[i];
            foreach (var (flatFace, cylFace, dFlat, g) in new[] { (f1, f2, d1, F1v[i]), (f2, f1, d2, F2v[i]) })
            {
                if (!flatFace.Surface.IsPlanar || CylinderOf(cylFace) is not { } cyl) continue;
                var arc = (ArcCurve3)cyl.Base;
                Vector3d axis = cyl.Dir.Normalized();
                Vector3d rel = p - arc.Frame.Origin;
                Vector3d toAxis = -(rel - axis * Vector3d.Dot(rel, axis));
                if (Vector3d.Dot(g, toAxis) > 0 && dFlat > arc.Radius + Math.Max(1e-3, arc.Radius * 1e-4))
                {
                    refusal = $"the cut goes {dFlat:0.###} across the face towards the axis, beyond the fillet radius {arc.Radius:0.###}; " +
                              "a chamfer larger than the round cannot follow its arc";
                    return null;
                }
            }
        }

        // Только выпуклые углы: у вогнутых фаска прибавляет материал — это отдельный путь.
        int mid = n / 2;
        if (!Convex(bodies, samples[mid].P, N1[mid], N2[mid]))
        { refusal = round ? "the swept round on a concave chain is not implemented yet"
                          : "the swept chamfer on a concave chain is not implemented yet"; return null; }

        if (SweepSections != null) for (int i = 0; i < n; i++) SweepSections(samples[i].P, A[i], B[i], O[i]);

        // ---- сечения как многоугольники
        //
        // Фаска — [A, B, O]. Скругление — дуга радиуса r = d1, касательная к обеим граням
        // в плоскости сечения (точки касания в r / tg(α/2) от ребра, центр на биссектрисе),
        // и три точки снаружи: у каждой точки касания и за углом. Число точек дуги одно на
        // всю цепочку — по шагу гранения, чтобы полосы соединяли соответствующие точки.
        string what = round ? "swept round" : "swept chamfer";
        int arcSeg = 0;
        var alpha = new double[n];
        if (round)
        {
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                alpha[i] = Math.Acos(Math.Clamp(Vector3d.Dot(F1v[i], F2v[i]), -1, 1));
                if (alpha[i] < 1e-3 || alpha[i] > Math.PI - 1e-3)
                { refusal = "the faces meet almost flat or folded back here — there is no corner to round"; return null; }
                worst = Math.Max(worst, Math.PI - alpha[i]);
            }
            arcSeg = Math.Max(2, (int)Math.Ceiling(worst / Tol.ChordAngle - 1e-9));
        }

        // Центр и точки касания шара в сечении i — по точным поверхностям граней (если есть).
        var ballCache = new (Vector3d C, Vector3d T1, Vector3d T2)?[n];
        var ballDone = new bool[n];
        (Vector3d C, Vector3d T1, Vector3d T2) Ball(int i)
        {
            Vector3d p = samples[i].P, g1 = F1v[i], g2 = F2v[i];
            // ПЕРЕМЕННЫЙ РАДИУС: если задан закон, радиус берётся по месту станции вдоль
            // цепочки. Иначе — прежний постоянный.
            double r = radiusAt == null ? d1 : radiusAt(n <= 1 ? 0 : i / (double)(n - 1));
            double half = alpha[i] / 2;
            Vector3d c0 = p + (g1 + g2).Normalized() * (r / Math.Sin(half));
            var plane = (c0, p + g1 * (r / Math.Tan(half)), p + g2 * (r / Math.Tan(half)));
            if (!ballDone[i])
            {
                ballDone[i] = true;
                static ISurface3? Ex(TFace f) { var q = f.Surface is FacetedSurface fs ? fs.Source : f.Surface; return q is FacetedSurface ? null : q; }
                var s1 = Ex(samples[i].F1); var s2 = Ex(samples[i].F2);
                if (s1 != null && s2 != null)
                    ballCache[i] = RollingBall(s1, NeighbourFaces(i, true).Select(Ex).Where(x => x != null)!,
                                               s2, NeighbourFaces(i, false).Select(Ex).Where(x => x != null)!, T[i], r, c0);
            }
            return ballCache[i] ?? plane;
        }

        Vector3d[] Section(int i)
        {
            if (!round) return new[] { A[i], B[i], O[i] };

            Vector3d p = samples[i].P;
            double r = radiusAt == null ? d1 : radiusAt(n <= 1 ? 0 : i / (double)(n - 1));
            var (centre, t1, t2) = Ball(i);

            double r1 = r, r2 = r;
            Vector3d e1 = (t1 - centre).Normalized(), v2 = (t2 - centre).Normalized();
            double phi = Math.Acos(Math.Clamp(Vector3d.Dot(e1, v2), -1, 1));
            Vector3d e2 = (v2 - e1 * Vector3d.Dot(v2, e1)).Normalized();

            var pts = new Vector3d[arcSeg + 4];
            for (int j = 0; j <= arcSeg; j++)
            {
                double th = phi * j / arcSeg;
                double rr = r1 + (r2 - r1) * j / arcSeg;      // концы — ровно в точках касания
                pts[j] = centre + (e1 * Math.Cos(th) + e2 * Math.Sin(th)) * rr;
            }
            double out_ = Math.Max(0.1, 0.3 * r);
            pts[arcSeg + 1] = t2 + N2[i] * out_;
            pts[arcSeg + 2] = p + (N1[i] + N2[i]).Normalized() * out_;
            pts[arcSeg + 3] = t1 + N1[i] * out_;
            return pts;
        }
        // Точная рабочая полоса сечения i (этап 2): дуга скругления или отрезок фаски —
        // то же, что Section(i)[0..arcSeg], но формулой, а не хордами.
        SweepSection WorkingBand(int i)
        {
            if (!round) return SweepSection.Line(A0[i], B0[i]);
            double r = radiusAt == null ? d1 : radiusAt(n <= 1 ? 0 : i / (double)(n - 1));
            var (centre, t1, t2) = Ball(i);
            Vector3d e1 = (t1 - centre).Normalized(), v2 = (t2 - centre).Normalized();
            double phi = Math.Acos(Math.Clamp(Vector3d.Dot(e1, v2), -1, 1));
            Vector3d e2 = (v2 - e1 * Vector3d.Dot(v2, e1)).Normalized();
            return SweepSection.Arc(centre, e1, e2, r, phi);
        }
        int centreIdx = round ? arcSeg + 2 : 2;          // точка за углом: из неё виден весь контур
        // Полосы, примыкающие к концам дуги, — той же метки, что и дуга: на вогнутой вдоль
        // цепочки грани их хорды срезают ленты в микроны, и ленты отдельными гранями рвали шов
        // касания углами. С меткой дуги срез — часть грани скругления, шов — одна изолиния.
        uint BandTag(int j) => round
            ? (j <= arcSeg || j == arcSeg + 3 ? 1u : (uint)(j - arcSeg + 2))
            : (uint)(j + 1);

        // ---- продление открытых концов — как у прямых рёбер
        //
        // Открытый конец, за которым кромка ГЛАДКО продолжается невыбранным ребром, не
        // продлевается: резец кончается там торцом (как у дуги на касательном переходе);
        // продление прорезало бы желобок вдоль невыбранного ребра.
        // Разбивка внутри построения резца: без неё непонятно, что распараллеливать.
        using var profSecs = Diagnostics.Prof.Time("резец: сечения");

        // ЗАСЕЧКИ ВНУТРИ ЗАКРЫВАЮТСЯ САМИ. Прежде я закрывал их вручную, а метод уходит раньше
        // по отказу — и засечка не записывалась: в отчёте её просто не было, и казалось, что код
        // не выполняется. Теперь каждая обведена блоком, который закрывается при любом выходе.
        var profParts = new Dictionary<string, long>();
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        void Mark(string what)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Diagnostics.Prof.Add("резец: " + what, now - mark);
            mark = now;
        }
        var secs = new List<Vector3d[]>();
        var work = new List<SweepSection>();              // рабочая полоса каждого сечения
        double ext0 = 0, extL = 0;                          // продление открытых концов
        var chainEdges = new HashSet<TEdge>(order.Select(o => o.Edge));
        // ПОИСК ГЛАДКОГО ПРОДОЛЖЕНИЯ идёт по ВСЕМ рёбрам тел, и это сделано не по небрежности.
        // Пробовал искать только среди рёбер вершины, совпавшей с концом цепочки: времени это не
        // сэкономило (сечения резца как были 784 мс, так и остались), а проверку стыков фасок
        // сломало — ребро может подходить к той же точке через ДРУГОЙ объект вершины, и перебор
        // по вершине его не находит.
        bool ContinuesSmoothly(Vector3d end, Vector3d outward)
        {
            double limit = Math.Cos(EdgeChain.DefaultBreakDegrees * Math.PI / 180);
            foreach (var body in bodies)
                foreach (var e in body.Edges)
                {
                    if (chainEdges.Contains(e) || e.Hidden) continue;
                    bool at0 = (e.V0.P - end).LengthSquared < 1e-12, at1 = (e.V1.P - end).LengthSquared < 1e-12;
                    if (!at0 && !at1) continue;
                    if (Vector3d.Dot(EdgeOutOf(e, at0), outward) > limit) return true;
                }
            return false;
        }

        Vector3d[] Shifted(Vector3d[] pts, Vector3d by) => pts.Select(x => x + by).ToArray();

        if (!closed)
        {
            double ext = ContinuesSmoothly(samples[0].P, -T[0]) ? 0
                : Math.Max(EndReach(bodies, samples[0].F1, samples[0].F2, samples[0].P, -T[0], N1[0], N2[0], F1v[0], F2v[0], d1, d2),
                           ClaimedOverlap(Math.Max(d1, d2)));
            ext0 = ext;
            secs.Add(Shifted(Section(0), -T[0] * ext));
            work.Add(WorkingBand(0).Shifted(-T[0] * ext));
        }
        Mark("сечения — подготовка");
        for (int i = 0; i < n; i++) { secs.Add(Section(i)); work.Add(WorkingBand(i)); }
        Mark("сечения — станции");
        if (!closed)
        {
            int l = n - 1;
            double ext = ContinuesSmoothly(samples[l].P, T[l]) ? 0
                : Math.Max(EndReach(bodies, samples[l].F1, samples[l].F2, samples[l].P, T[l], N1[l], N2[l], F1v[l], F2v[l], d1, d2),
                           ClaimedOverlap(Math.Max(d1, d2)));
            extL = ext;
            secs.Add(Shifted(Section(l), T[l] * ext));
            work.Add(WorkingBand(l).Shifted(T[l] * ext));
        }

        SweepDebug?.Invoke($"продление: начало {ext0:0.###}, конец {extL:0.###}, сечений {secs.Count}");
        // ---- тело: полосы между соседними сечениями, торцы у открытой цепочки
        var polys = new List<CsgPolygon>();
        var polySeg = new List<int>();          // номер отрезка треугольника (торцы — −1)
        int curSeg = -1;
        void Tri(Vector3d a, Vector3d b, Vector3d c, uint tag)
        {
            if ((a - b).LengthSquared < 1e-20 || (b - c).LengthSquared < 1e-20 || (c - a).LengthSquared < 1e-20) return;
            polys.Add(new CsgPolygon(new List<Vector3d> { a, b, c }, tag));
            polySeg.Add(curSeg);
        }
        int m = secs.Count;
        int ks = secs[0].Length;
        int segs = closed ? m : m - 1;
        for (int i = 0; i < segs; i++)
        {
            curSeg = i;
            var s0 = secs[i]; var s1 = secs[(i + 1) % m];
            for (int j = 0; j < ks; j++)
            {
                int jn = (j + 1) % ks;
                uint tag = BandTag(j);
                Tri(s0[j], s1[j], s1[jn], tag);
                Tri(s0[j], s1[jn], s0[jn], tag);
            }
        }
        // Торец — веером из точки за углом; начальный по обходу сечения, конечный — навстречу.
        void Cap(Vector3d[] sec, bool reverse, uint tag)
        {
            for (int j = 1; j + 1 < ks; j++)
            {
                Vector3d c = sec[centreIdx], x = sec[(centreIdx + j) % ks], y = sec[(centreIdx + j + 1) % ks];
                if (reverse) Tri(c, y, x, tag); else Tri(c, x, y, tag);
            }
        }
        curSeg = -1;
        if (!closed)
        {
            Cap(secs[0], false, 100);
            Cap(secs[m - 1], true, 101);
        }

        // Ориентация — наружу: по знаку объёма.
        double vol = 0;
        foreach (var p in polys) vol += Vector3d.Dot(p.V[0], Vector3d.Cross(p.V[1], p.V[2])) / 6;
        if (vol < 0)
        {
            vol = -vol;
            for (int i = 0; i < polys.Count; i++)
            {
                var q = polys[i];
                polys[i] = new CsgPolygon(new List<Vector3d> { q.V[0], q.V[2], q.V[1] }, q.Tag);
            }
        }

        // СНАЧАЛА ПРОВЕРИТЬ РЕЗЕЦ, ПОТОМ РЕЗАТЬ.
        //   1. Складка — разные знаки объёма у отрезков между соседними сечениями
        //      (знак зависит от обхода сечения, поэтому важна разница, а не знак).
        //   2. Резец не пересекает сам себя (пары несмежных треугольников).
        //      Складка — когда отрицательные отрезки весят больше процента положительных
        //      (или наоборот): крошечные отрезки со знаком, сбитым округлением, не в счёт.
        double plus = 0, minus = 0;
        for (int i = 0; i < segs; i++)
        {
            double pv = SegmentVolume(secs[i], secs[(i + 1) % m], centreIdx);
            SweepDebug?.Invoke($"отрезок {i}: объём {pv:0.######}, начало ({secs[i][0].X:0.##},{secs[i][0].Y:0.##},{secs[i][0].Z:0.##})");
            if (pv > 0) plus += pv; else minus -= pv;
        }
        if (Math.Min(plus, minus) > 0.01 * Math.Max(plus, minus))
        { refusal = $"the {what} folds over itself here — the chain turns too sharply for this size"; return null; }
        if (SelfIntersects(polys))
        { refusal = $"the {what} would intersect itself — the chain turns too sharply for this size"; return null; }

        // ---- ТОЧНЫЕ ПОВЕРХНОСТИ ГРАНЕЙ РЕЗЦА (этап 2: граней без хозяина не бывает).
        //
        // ДОБАВЛЕНО. Резец собирался из треугольников без исходных поверхностей, и грань
        // фаски/скругления в теле знала о себе только треугольники. Рабочая полоса — точная
        // поверхность по сечениям (дуга или отрезок в каждом сечении); вспомогательные
        // полосы — поверхности по отрезкам; торцы — плоскости сечений. Вспомогательные
        // в тело обычно не попадают, но если кусок попал — хозяин есть и у него.
        var sources = new Dictionary<uint, ISurface3?>();
        sources[1] = SweptWorkingSurface(order, edgeFaces, samples, sampleK, work, T, N1, N2, closed, ext0, extL, d1, d2, round)
                     ?? new SectionSurface(work, closed);
        for (int j = round ? arcSeg : 1; j < ks; j++)
        {
            if (BandTag(j) == 1) continue;                      // часть рабочей грани
            int jn = (j + 1) % ks;
            sources[BandTag(j)] = new SectionSurface(secs.Select(sec => SweepSection.Line(sec[j], sec[jn])).ToList(), closed);
        }
        if (!closed)
        {
            foreach (var (sec, tag) in new[] { (secs[0], 100u), (secs[m - 1], 101u) })
            {
                Vector3d nrm = Vector3d.Zero;
                for (int j = 1; j + 1 < ks; j++)
                    nrm += Vector3d.Cross(sec[(centreIdx + j) % ks] - sec[centreIdx], sec[(centreIdx + j + 1) % ks] - sec[centreIdx]);
                if (nrm.LengthSquared > 1e-24) sources[tag] = new PlaneSurface(Frame3.FromNormal(sec[centreIdx], nrm));
            }
        }

        // ПОДПИСИ ГРАНЕЙ РЕЗЦА. Рабочая грань — сама поверхность скругления (или фаски), полосы и
        // торцы — вспомогательные. Имя не зависит ни от размеров, ни от того, какое ребро
        // скругляется, поэтому ссылка на кромку скругления переживает изменение размеров.
        string kind = round ? "round" : "chamfer";
        var names = new Dictionary<uint, string> { [1] = kind + ":face" };
        for (int j = round ? arcSeg : 1; j < ks; j++)
        {
            uint bt = BandTag(j);
            if (bt == 1) continue;
            names[bt] = $"{kind}:band[{j}]";
        }
        names[100u] = kind + ":cap[0]";
        names[101u] = kind + ":cap[1]";

        profSecs.Dispose();
        PathDebug?.Invoke($"резец: станций {n}, точек сечения {arcSeg + 1}, многоугольников {polys.Count}");
        using (Diagnostics.Prof.Time("резец: сборка тела"))
            return BooleanOp.SolidFromPolygons(polys, sources, names);
    }

    /// <summary>ДИАГНОСТИКА: расхождение формулы рабочей поверхности с сечениями резца.</summary>
    public static Action<string>? SurfaceDebug;

    /// <summary>
    /// ТОЧНАЯ РАБОЧАЯ ПОВЕРХНОСТЬ ПРОТЯНУТОГО РЕЗЦА — ПО ФОРМУЛЕ СЕЧЕНИЯ (этап 2).
    ///
    /// Сечение при параметре хребта u — то же построение, что у резца, но в любой точке,
    /// а не только в вершинах граней: точка на точной кривой ребра, нормали точных
    /// поверхностей обеих граней (знак — по гладкой нормали грани у середины ребра),
    /// у фаски — отрезок между точками на точных поверхностях в d1 и d2 от ребра, у
    /// скругления — дуга, касательная к обеим граням в плоскости сечения.
    /// u — длина дуги хребта в мм, [0, L]; открытые концы продлены прямо:
    /// u ∈ [−ext0, 0] и [L, L + extL] — сдвиг крайнего сечения вдоль касательной.
    ///
    /// Линейная вставка между сечениями (прежний хозяин) давала кромку фаски на плоской
    /// грани ломаной из хорд — «кривая» кромка выходила точной ломаной. По формуле край
    /// фаски по дуге — дуга.
    ///
    /// null — у какой-то грани нет точной поверхности, или формула не сходится с
    /// сечениями резца (тогда остаётся линейная вставка).
    /// </summary>
    private static SectionSurface? SweptWorkingSurface(
        List<(TEdge Edge, bool Swap, Vector3d From, Vector3d To)> order,
        List<(TFace F1, TFace F2)> edgeFaces,
        List<(Vector3d P, TFace F1, TFace F2)> samples, List<int> sampleK,
        List<SweepSection> work, Vector3d[] T, Vector3d[] N1, Vector3d[] N2,
        bool closed, double ext0, double extL, double d1, double d2, bool round)
    {
        int E = order.Count;
        if (edgeFaces.Count != E) return null;

        static ISurface3? Exact(TFace f)
        {
            ISurface3? s = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            return s is FacetedSurface ? null : s;
        }

        var src1 = new ISurface3[E]; var src2 = new ISurface3[E];
        var forward = new bool[E];
        for (int k = 0; k < E; k++)
        {
            var (f1, f2) = edgeFaces[k];
            if (Exact(f1) is not { } a || Exact(f2) is not { } b) return null;
            src1[k] = a; src2[k] = b;
            var c = order[k].Edge.Curve;
            forward[k] = (c.PointAt(c.T0) - order[k].From).LengthSquared <= (c.PointAt(c.T1) - order[k].From).LengthSquared;
        }

        // ЗНАК НОРМАЛЕЙ — ОТ БЛИЖАЙШЕГО ПО ХРЕБТУ СЕЧЕНИЯ РЕЗЦА.
        //
        // ИСПРАВЛЕНО. Опорная нормаль бралась в середине ребра через SmoothNormal; там
        // точка обычно не вершина треугольников, и знак брался от средней нормали всей
        // грани. У цилиндрической стенки кармана средняя нормаль почти нулевая — знак
        // случайный: направление вдоль грани переворачивалось, формула расходилась с
        // сечениями резца на удвоенный размер (10 мм у фаски 10×5), и оставалась линейная
        // вставка с нормалью, скачущей от полосы к полосе, — скругление R5 по верхней
        // кромке такой фаски «пересекало само себя» (снимок пользователя 22.09).
        // В сечениях резца нормали посчитаны по треугольникам в самой точке ребра.
        var S = new double[E + 1];
        for (int k = 0; k < E; k++) S[k + 1] = S[k] + Math.Max(order[k].Edge.Curve.Length, 1e-9);
        double L = S[E];
        double[] sampleU = new double[samples.Count];
        (Vector3d R1, Vector3d R2) RefAt(double u)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < sampleU.Length; i++)
            {
                double d = Math.Abs(sampleU[i] - u);
                if (closed) d = Math.Min(d, S[E] - d);
                if (d < bd) { bd = d; best = i; }
            }
            return (N1[best], N2[best]);
        }

        IEnumerable<ISurface3> Near(ISurface3[] src, int k)
        {
            foreach (int j in new[] { k - 1, k + 1 })
            {
                int jj = closed ? (j + E) % E : j;
                if (jj >= 0 && jj < E && jj != k) yield return src[jj];
            }
        }

        SweepSection? AtSpine(int k, double t)
        {
            var c = order[k].Edge.Curve;
            double tau = forward[k] ? c.T0 + (c.T1 - c.T0) * t : c.T1 - (c.T1 - c.T0) * t;
            Vector3d p = c.PointAt(tau);
            Vector3d along = c.TangentAt(tau) * (forward[k] ? 1 : -1);

            var (_, n1) = ExactSurfaces.ProjectUnsigned(src1[k], p);
            var (_, n2) = ExactSurfaces.ProjectUnsigned(src2[k], p);
            var (r1, r2) = RefAt(S[k] + (S[k + 1] - S[k]) * t);
            if (Vector3d.Dot(n1, r1) < 0) n1 = -n1;
            if (Vector3d.Dot(n2, r2) < 0) n2 = -n2;

            Vector3d tt = Vector3d.Cross(n1, n2);
            if (tt.LengthSquared < 1e-6) tt = along;
            else { tt = tt.Normalized(); if (Vector3d.Dot(tt, along) < 0) tt = -tt; }
            n1 -= tt * Vector3d.Dot(n1, tt); n2 -= tt * Vector3d.Dot(n2, tt);
            if (n1.LengthSquared < 1e-18 || n2.LengthSquared < 1e-18) return null;
            n1 = n1.Normalized(); n2 = n2.Normalized();

            Vector3d g1 = AlongFace(n1, tt, n2), g2 = AlongFace(n2, tt, n1);
            if (!round)
                return SweepSection.Line(OntoSurfaceNear(src1[k], Near(src1, k), p + g1 * d1),
                                         OntoSurfaceNear(src2[k], Near(src2, k), p + g2 * d2));

            double alpha = Math.Acos(Math.Clamp(Vector3d.Dot(g1, g2), -1, 1));
            if (alpha < 1e-6 || alpha > Math.PI - 1e-6) return null;
            double r = d1, half = alpha / 2;
            Vector3d centre = p + (g1 + g2).Normalized() * (r / Math.Sin(half));
            Vector3d c1 = p + g1 * (r / Math.Tan(half)), c2 = p + g2 * (r / Math.Tan(half));
            // Шар по точным поверхностям — так же, как в сечениях резца.
            if (RollingBall(src1[k], Near(src1, k), src2[k], Near(src2, k), tt, r, centre) is { } ball)
            { centre = ball.C; c1 = ball.T1; c2 = ball.T2; }
            Vector3d e1 = (c1 - centre).Normalized();
            Vector3d v2 = (c2 - centre).Normalized();
            double phi = Math.Acos(Math.Clamp(Vector3d.Dot(e1, v2), -1, 1));
            Vector3d e2 = v2 - e1 * Vector3d.Dot(v2, e1);
            if (e2.LengthSquared < 1e-24) return null;
            return SweepSection.Arc(centre, e1, e2.Normalized(), r, phi);
        }

        // ПАРАМЕТР ХРЕБТА — ДЛИНА ДУГИ (мм): ребро k занимает [S[k], S[k+1]], продления —
        // [−ext0, 0] и [L, L + extL]. Скорость по u всюду около единицы. Прежде u был номером
        // ребра, и на стыке ребра с продлением скорость менялась в сотни раз: разностная
        // производная, перешагнув стык, давала шаг Ньютона не того масштаба, проекция у концов
        // застревала, и кромка скругления со стенкой отверстия не становилась кривой
        // пересечения — оставалась хордами с рёбрами в микроны между ними (прогон, 385:4–5).

        SweepSection lastGood = work[closed ? 0 : 1];
        SweepSection Gen(double u)
        {
            if (!closed && u < 0) return Gen(0).Shifted(T[0] * u);
            if (!closed && u > L) return Gen(L).Shifted(T[^1] * (u - L));
            int k = Array.BinarySearch(S, u);
            if (k < 0) k = ~k - 1;
            k = Math.Clamp(k, 0, E - 1);
            var sec = AtSpine(k, Math.Clamp((u - S[k]) / (S[k + 1] - S[k]), 0, 1));
            if (sec is { } ok) { lastGood = ok; return ok; }
            return lastGood;
        }

        // Параметры сечений резца на хребте.
        int n = samples.Count;
        // Сечение, чья точка лежит не на кривой ребра (вершина грани у стыка рёбер), в сверку
        // формулы не берётся: неверна точка сечения, а не формула. Из-за одного такого
        // сечения у стыка рёбер кольца (0.76 мм от кривой) формула отвергалась целиком.
        var offCurve = new bool[n];
        var stationU = new List<double>();
        if (!closed) stationU.Add(-ext0);
        for (int i = 0; i < n; i++)
        {
            int k = sampleK[i];
            var c = order[k].Edge.Curve;
            double tau = CurveParam.Closest(c, samples[i].P);
            offCurve[i] = (c.PointAt(tau) - samples[i].P).Length > Math.Max(0.05, 0.005 * Math.Max(d1, d2));
            double t = (tau - c.T0) / (c.T1 - c.T0);
            if (!forward[k]) t = 1 - t;
            stationU.Add(S[k] + (S[k + 1] - S[k]) * Math.Clamp(t, 0, 1));
            sampleU[i] = stationU[^1];
        }
        if (!closed) stationU.Add(L + extL);
        for (int i = 1; i < stationU.Count; i++)
            if (stationU[i] < stationU[i - 1] - 1e-12) { SurfaceDebug?.Invoke($"параметры сечений не по порядку: {i}"); return null; }

        // Сверка: формула в параметре сечения должна давать сечение резца.
        double worst = 0, size = Math.Max(d1, d2);
        double limit = Math.Max(1e-3, 0.01 * size);
        int outliers = 0, checkedCount = 0;
        for (int i = 0; i < work.Count; i++)
        {
            int si0 = closed ? i : i - 1;
            if (si0 >= 0 && si0 < n && offCurve[si0]) continue;
            var g = Gen(stationU[i]);
            double here = 0;
            foreach (double v in new[] { 0.0, 0.5, 1.0 })
                here = Math.Max(here, (g.Point(v) - work[i].Point(v)).Length);
            if (here > 1e-3 && SurfaceDebug != null)
            {
                int si = closed ? i : i - 1;
                string K = si >= 0 && si < n ? $"ребро {sampleK[si]}" : "продление";
                SurfaceDebug($"  сечение {i} (u {stationU[i]:0.###}, {K}): расхождение {here:0.###}; формула A {g.Point(0)}, резец A {work[i].Point(0)}");
            }
            worst = Math.Max(worst, here);
            checkedCount++;
            if (here > limit) outliers++;
        }
        SurfaceDebug?.Invoke($"рабочая поверхность по формуле: расхождение с сечениями резца {worst:E2}");
        // Одиночные выбросы не отменяют формулу: она — определение поверхности, резец лишь
        // приближает её хордами, и сечение у стыка рёбер бывает построено по вершине грани,
        // стоящей не на своём месте. ИСПРАВЛЕНО: одно сечение из 156 с расхождением 0.18 мм
        // отменяло точную поверхность скругления, и шов касания рассыпался на 600 отрезков.
        // Допускается до 5% сечений за допуском и ни одного больше чем вдесятеро.
        if (worst > 10 * limit || outliers > Math.Max(1, checkedCount / 20)) return null;

        // Стыки рёбер и продлений — узлы таблицы формулы (там кривизна скачет).
        var breaks = new List<double>(S);
        return new SectionSurface(Gen, stationU, work, closed, closed ? 0 : -ext0, closed ? L : L + extL, breaks);
    }

    /// <summary>
    /// Ближайшая к точке точка САМОЙ грани: на её треугольниках (гранёная) или на плоскости.
    /// </summary>
    private static Vector3d ClosestOnFace(TFace f, Vector3d p)
    {
        if (f.Surface is FacetedSurface fs && fs.Facets.Count > 0)
        {
            double best = double.MaxValue; Vector3d q = p;
            foreach (var (a, b, c) in fs.Facets)
            {
                Vector3d x = ClosestOnTriangle(p, a, b, c);
                double d = (x - p).LengthSquared;
                if (d < best) { best = d; q = x; }
            }
            return q;
        }
        return OntoFace(f, p);
    }

    /// <summary>
    /// Объём отрезка резца между двумя сечениями-многоугольниками (знак — ориентация):
    /// замкнутая поверхность из двух торцов-вееров и боковых полос.
    /// </summary>
    private static double SegmentVolume(Vector3d[] a, Vector3d[] b, int centre)
    {
        int k = a.Length;
        Vector3d c = Vector3d.Zero;
        foreach (var p in a) c += p;
        foreach (var p in b) c += p;
        c /= 2 * k;
        double v = 0;
        void T(Vector3d p, Vector3d q, Vector3d r) => v += Vector3d.Dot(p - c, Vector3d.Cross(q - c, r - c)) / 6;
        for (int j = 1; j + 1 < k; j++)
        {
            T(a[centre], a[(centre + j) % k], a[(centre + j + 1) % k]);
            T(b[centre], b[(centre + j + 1) % k], b[(centre + j) % k]);
        }
        for (int j = 0; j < k; j++)
        {
            int jn = (j + 1) % k;
            T(a[j], b[j], b[jn]);
            T(a[j], b[jn], a[jn]);
        }
        return v;
    }

    /// <summary>Объём «призмы» между двумя треугольными сечениями (знак — ориентация).</summary>
    private static double PrismVolume(Vector3d a0, Vector3d b0, Vector3d o0, Vector3d a1, Vector3d b1, Vector3d o1)
    {
        // Замкнутая поверхность: два торца и три боковых четырёхугольника (по два треугольника).
        var t = new (Vector3d, Vector3d, Vector3d)[]
        {
            (a0, o0, b0), (a1, b1, o1),
            (a0, a1, b1), (a0, b1, b0),
            (b0, b1, o1), (b0, o1, o0),
            (o0, o1, a1), (o0, a1, a0)
        };
        Vector3d c = (a0 + b0 + o0 + a1 + b1 + o1) / 6;
        double v = 0;
        foreach (var (p, q, r) in t) v += Vector3d.Dot(p - c, Vector3d.Cross(q - c, r - c)) / 6;
        return v;
    }

    /// <summary>Пересекаются ли несмежные треугольники набора (отсечка по габаритам).</summary>
    /// <summary>
    /// Пересекаются ли несмежные треугольники набора (отсечка по габаритам). С номерами
    /// отрезков: треугольники отрезков, отстоящих не дальше чем на два, не сравниваются —
    /// при густо поставленных сечениях они касаются на уровне округления, а настоящую
    /// складку между соседями ловит проверка знака объёма. ring — число отрезков кольца
    /// (0 у открытой цепочки).
    /// </summary>
    private static bool SelfIntersects(List<CsgPolygon> polys, List<int> seg, int ring)
    {
        bool Near(int a, int b)
        {
            if (a < 0 || b < 0) return false;
            int d = Math.Abs(a - b);
            if (ring > 0) d = Math.Min(d, ring - d);
            return d <= 2;
        }
        return SelfIntersectsCore(polys, (i, j) => Near(seg[i], seg[j]));
    }

    private static bool SelfIntersects(List<CsgPolygon> polys) => SelfIntersectsCore(polys, (_, _) => false);

    private static bool SelfIntersectsCore(List<CsgPolygon> polys, Func<int, int, bool> skip)
    {
        int n = polys.Count;
        var lo = new Vector3d[n]; var hi = new Vector3d[n];
        for (int i = 0; i < n; i++)
        {
            var v = polys[i].V;
            lo[i] = Vector3d.ComponentMin(v[0], Vector3d.ComponentMin(v[1], v[2]));
            hi[i] = Vector3d.ComponentMax(v[0], Vector3d.ComponentMax(v[1], v[2]));
        }
        double eps = 1e-9;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (lo[i].X > hi[j].X + eps || lo[j].X > hi[i].X + eps ||
                    lo[i].Y > hi[j].Y + eps || lo[j].Y > hi[i].Y + eps ||
                    lo[i].Z > hi[j].Z + eps || lo[j].Z > hi[i].Z + eps) continue;

                if (skip(i, j)) continue;
                var a = polys[i].V; var b = polys[j].V;
                bool adjacent = false;
                foreach (var x in a) foreach (var y in b) if ((x - y).LengthSquared < 1e-18) adjacent = true;
                if (adjacent) continue;

                for (int k = 0; k < 3; k++)
                    if (SegmentHitsTriangle(a[k], a[(k + 1) % 3], b[0], b[1], b[2]) ||
                        SegmentHitsTriangle(b[k], b[(k + 1) % 3], a[0], a[1], a[2]))
                    {
                        SweepDebug?.Invoke($"пересечение: треугольники {i} ({polys[i].Tag}) и {j} ({polys[j].Tag}): {Show(a[0])} {Show(a[1])} {Show(a[2])} / {Show(b[0])} {Show(b[1])} {Show(b[2])}");
                        return true;
                    }
            }
        return false;
    }

    /// <summary>Отрезок pq проходит сквозь внутренность треугольника abc (Мёллер — Трумбор).</summary>
    private static bool SegmentHitsTriangle(Vector3d p, Vector3d q, Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d e1 = b - a, e2 = c - a, d = q - p;

        // ОТРЕЗОК ДОЛЖЕН ПРОХОДИТЬ ПЛОСКОСТЬ ТРЕУГОЛЬНИКА НАСКВОЗЬ — концы по разные стороны
        // с запасом. ИСПРАВЛЕНО (этап 2). У полос резца, построенных по точным нормалям,
        // на прямом участке цепочки сечения — точные сдвиги одно другого, и соседние
        // треугольники лежат в одной плоскости до округления. Детерминант Мёллера —
        // Трумбора у такой пары — шум, и проверка находила «самопересечение» там, где
        // треугольники лишь соседствуют (скругление по кромке фаски отказывало).
        // Касание и наложение в одной плоскости сквозным пересечением не считаются:
        // складку ловит проверка знака объёма отрезков.
        Vector3d nrm = Vector3d.Cross(e1, e2);
        double nl = nrm.Length;
        if (nl < 1e-300) return false;
        nrm /= nl;
        double scale = Math.Max(Math.Max(e1.Length, e2.Length), d.Length);
        double eps = 1e-9 * Math.Max(1, scale);
        double dp = Vector3d.Dot(p - a, nrm), dq = Vector3d.Dot(q - a, nrm);
        if (dp > -eps && dq > -eps || dp < eps && dq < eps) return false;

        Vector3d h = Vector3d.Cross(d, e2);
        double det = Vector3d.Dot(e1, h);
        if (Math.Abs(det) < 1e-18) return false;
        double f = 1 / det;
        Vector3d s = p - a;
        double u = f * Vector3d.Dot(s, h);
        if (u <= 1e-9 || u >= 1 - 1e-9) return false;
        Vector3d qq = Vector3d.Cross(s, e1);
        double v = f * Vector3d.Dot(d, qq);
        if (v <= 1e-9 || u + v >= 1 - 1e-9) return false;
        double t = f * Vector3d.Dot(e2, qq);
        return t > 1e-9 && t < 1 - 1e-9;
    }

    /// <summary>
    /// Нормаль грани у точки — по ИСХОДНОЙ гладкой поверхности, знак — по треугольнику.
    /// Нормали треугольников гранёной грани скачут между полосами (у скругления на 7°),
    /// и сечения протянутого резца, поставленные часто, перехлёстывались.
    /// </summary>
    private static Vector3d SmoothNormal(TFace face, Vector3d p, double tol)
    {
        // ЗНАК — ОТ БЛИЖАЙШЕГО ТРЕУГОЛЬНИКА ГРАНИ. Сама нормаль ниже берётся из точной
        // поверхности, сетка нужна только чтобы понять, куда она смотрит. Прежде знак давало
        // гладкое гранение всей грани (NormalNear): оно считает проекцию каждой вершины на
        // точную поверхность и звалось на каждую точку сечения. Средняя нормаль всей грани
        // тоже не годится: у кольцевой грани она почти нулевая.
        Vector3d facet = Vector3d.Zero;
        if (face.Surface is FacetedSurface fsn0 && fsn0.Facets.Count > 0)
        {
            double best = double.MaxValue;
            foreach (var (a, b, c) in fsn0.Facets)
            {
                Vector3d cr = Vector3d.Cross(b - a, c - a);
                if (cr.LengthSquared < 1e-24) continue;
                double d = (ClosestOnTriangle(p, a, b, c) - p).LengthSquared;
                if (d < best) { best = d; facet = cr.Normalized(); }
            }
        }
        if (facet.LengthSquared < 1e-18) facet = NormalNear(face, p, tol);
        if (facet.LengthSquared < 1e-18) facet = face.NormalAt(p);

        // Цилиндр выдавливания — нормаль точно по радиусу от оси. Через проекцию
        // в параметры она приближённая (проекция идёт по ломаной основания), и точки
        // фаски на кромке скругления расходились с окружностью на 0.05 мм: кромка
        // не собиралась в дугу, и цепочка по ней рвалась на изломах.
        if (CylinderOf(face) is { } cyl)
        {
            var arc = (ArcCurve3)cyl.Base;
            Vector3d axis = cyl.Dir.Normalized();
            Vector3d rel = p - arc.Frame.Origin;
            Vector3d radial = rel - axis * Vector3d.Dot(rel, axis);
            if (radial.LengthSquared > 1e-18)
            {
                radial = radial.Normalized();
                return Vector3d.Dot(radial, facet) < 0 ? -radial : radial;
            }
        }

        // Грань без исходной поверхности (грань протянутой фаски) — среднее треугольников
        // в радиусе 0.5 мм с весом по площади: нормаль в одной вершине скачет на градусы,
        // и сечения протянутого скругления по такой кромке перехлёстывались.
        if (face.Surface is FacetedSurface fsn && fsn.Source == null && fsn.Facets.Count > 1)
        {
            const double rho = 0.5;
            Vector3d sum = Vector3d.Zero;
            foreach (var (a, b, c) in fsn.Facets)
            {
                Vector3d q = ClosestOnTriangle(p, a, b, c);
                if ((q - p).LengthSquared > rho * rho) continue;
                Vector3d cr = Vector3d.Cross(b - a, c - a);
                if (Vector3d.Dot(cr, facet) > 0) sum += cr; else sum -= cr;
            }
            if (sum.LengthSquared > 1e-24) return sum.Normalized();
        }

        if (face.Surface is FacetedSurface fs && fs.Source != null)
        {
            Vector3d smooth = fs.Source.NormalAt(fs.Source.ProjectToUV(p));
            if (smooth.LengthSquared > 1e-18)
            {
                smooth = smooth.Normalized();
                return Vector3d.Dot(smooth, facet) < 0 ? -smooth : smooth;
            }
        }
        return facet.LengthSquared > 1e-18 ? facet.Normalized() : facet;
    }

    /// <summary>
    /// Точки ребра для сечений: концы и вершины граней, лежащие на самом ребре,
    /// по порядку от a к b. Вершина «на ребре» — общая для треугольников обеих граней
    /// (у плоской грани без треугольников — лежащая в её плоскости) и близкая к кривой.
    /// </summary>
    /// <summary>ДИАГНОСТИКА: продление резца за конец ребра.</summary>
    public static Action<string>? ReachDebug;

    /// <summary>Ставить сечения по вершинам граней (прежнее поведение) — для разбора.</summary>
    private static bool _gExactSectionsByFacets;
    /// <summary>K04: значение контекста вычисления, если он задан; иначе общее по умолчанию.</summary>
    public static bool ExactSectionsByFacets
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.ExactSectionsByFacets : _gExactSectionsByFacets;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gExactSectionsByFacets, value, "FilletOp.ExactSectionsByFacets");
    }

    private static List<Vector3d> OnEdgePoints(TEdge e, Vector3d a, Vector3d b)
    {
        // Прямой отрезок — одна хорда: внутренних точек нет. Прежде их искали с допуском
        // 0.04 мм, и у края, нарезанного на отрезки по 0.3 мм, в допуск попадали вершины
        // СОСЕДНИХ отрезков: сечения шли не по порядку, резец складывался гармошкой.
        if (e.Curve is LineCurve3) return new List<Vector3d> { a, b };

        // СЕЧЕНИЯ — ПО ГРАНЕНИЮ САМОЙ КРИВОЙ, А НЕ ПО ЗАТРАВКЕ. Затравка кривой пересечения и
        // изолинии — это вершины граней, то есть след разбиения: один и тот же рез, выполненный
        // разными путями булевой, давал резцы разного объёма (594 и 636 мм³ на одном и том же
        // теле) и разный результат. Гранение кривой по прогибу — свойство геометрии.
        if (e.Curve is IntersectionCurve3 or SectionIsoCurve)
        {
            var dense = new List<Vector3d>();
            GeomUtil.PathMode = true;
            try { e.Curve.Tessellate(dense, true); }
            finally { GeomUtil.PathMode = false; }
            if (dense.Count < 2) return new List<Vector3d> { a, b };
            if ((dense[0] - a).LengthSquared > (dense[^1] - a).LengthSquared) dense.Reverse();
            dense[0] = a; dense[^1] = b;
            return dense;
        }

        var poly = new List<Vector3d>();
        GeomUtil.PathMode = true;
        try { e.Curve.Tessellate(poly, true); }
        finally { GeomUtil.PathMode = false; }
        if ((poly[0] - a).LengthSquared > (poly[^1] - a).LengthSquared) poly.Reverse();

        // СЕЧЕНИЯ ПО ГРАНЕНИЮ КРИВОЙ, А НЕ ПО ВЕРШИНАМ ГРАНЕЙ. Вершины граней — след разбиения:
        // одно и то же скругление на одном и том же теле давало резцы 594 и 636 мм³ в
        // зависимости от того, каким путём шла предыдущая булева. Гранение кривой по прогибу
        // одинаково всегда.
        if (poly.Count >= 2 && !ExactSectionsByFacets)
        {
            poly[0] = a; poly[^1] = b;
            return poly;
        }

        HashSet<(double, double, double)> Verts(TFace f)
        {
            var set = new HashSet<(double, double, double)>();
            if (f.Surface is FacetedSurface fs)
                foreach (var (x, y, z) in fs.Facets) { set.Add((x.X, x.Y, x.Z)); set.Add((y.X, y.Y, y.Z)); set.Add((z.X, z.Y, z.Z)); }
            return set;
        }
        var va = Verts(e.FaceA!); var vb = Verts(e.FaceB!);
        IEnumerable<(double, double, double)> cand =
            va.Count > 0 && vb.Count > 0 ? va.Where(vb.Contains)
            : va.Count > 0 ? va.Where(p => Math.Abs(Vector3d.Dot(new Vector3d(p.Item1, p.Item2, p.Item3) - a, e.FaceB!.NormalAt(a).Normalized())) < 1e-6)
            : vb.Count > 0 ? vb.Where(p => Math.Abs(Vector3d.Dot(new Vector3d(p.Item1, p.Item2, p.Item3) - a, e.FaceA!.NormalAt(a).Normalized())) < 1e-6)
            : Enumerable.Empty<(double, double, double)>();

        // У дуги — расстояние до самой окружности, с допуском в десять шагов сетки:
        // вершины граней лежат на ней с точностью округления. У кривой общего вида —
        // до её ломаной, так же жёстко.
        double near = Math.Max(1e-4, 2 * Tol.Chord);
        var inner = new List<(double S, Vector3d P)>();
        foreach (var c in cand)
        {
            var p = new Vector3d(c.Item1, c.Item2, c.Item3);
            if ((p - a).LengthSquared < 1e-18 || (p - b).LengthSquared < 1e-18) continue;

            if (e.Curve is ArcCurve3 arc)
            {
                Vector3d rel = p - arc.Frame.Origin;
                double off = Vector3d.Dot(rel, arc.Frame.W.Normalized());
                double radial = (rel - arc.Frame.W.Normalized() * off).Length - arc.Radius;
                if (Math.Abs(off) > 1e-4 || Math.Abs(radial) > 1e-4) continue;
            }

            // Положение вдоль ломаной кривой и расстояние до неё.
            double best = double.MaxValue, sBest = 0, acc = 0;
            for (int i = 0; i + 1 < poly.Count; i++)
            {
                Vector3d q = ClosestOnSegment(p, poly[i], poly[i + 1]);
                double d = (q - p).LengthSquared;
                if (d < best) { best = d; sBest = acc + (q - poly[i]).Length; }
                acc += (poly[i + 1] - poly[i]).Length;
            }
            double limit = e.Curve is ArcCurve3 ? near : 1e-4;
            if (Math.Sqrt(best) <= limit && sBest > 1e-9 && sBest < acc - 1e-9) inner.Add((sBest, p));
        }

        var list = new List<Vector3d> { a };
        list.AddRange(inner.OrderBy(x => x.S).Select(x => x.P));
        list.Add(b);
        return Densified(e.Curve, list);
    }

    /// <summary>
    /// ПРОМЕЖУТКИ БЕЗ ТОЧЕК — ТОЧКАМИ САМОЙ КРИВОЙ.
    ///
    /// ДОБАВЛЕНО. Вершины граней на ребре отбираются жёстко (на окружности до 1e-4), и у дуги
    /// R25 края фаски кармана их прошло три на полукруг: резец из трёх сечений складывался,
    /// скругление R5 по кромке отказывало (снимок пользователя 22.09). Где касательная между
    /// соседними точками поворачивает больше чем на два шага гранения, промежуток
    /// дополняется точками кривой с шагом гранения.
    /// </summary>
    private static List<Vector3d> Densified(ICurve3 c, List<Vector3d> pts)
    {
        if (c is LineCurve3 || pts.Count < 2) return pts;
        var ts = pts.Select(q => CurveParam.Closest(c, q)).ToList();
        var res = new List<Vector3d> { pts[0] };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double ta = ts[i], tb = ts[i + 1];
            Vector3d da = c.TangentAt(Math.Clamp(ta, c.T0, c.T1)), db = c.TangentAt(Math.Clamp(tb, c.T0, c.T1));
            double turn = Math.Acos(Math.Clamp(Vector3d.Dot(da, db), -1, 1));
            // Дуга, почти замкнутая между двумя точками, — поворот не виден по касательным.
            if (c is ArcCurve3 arc) turn = Math.Max(turn, Math.Abs(arc.Sweep) * Math.Abs(tb - ta));
            int m = (int)Math.Ceiling(turn / Tol.ChordAngle - 1e-9);
            if (turn > 2 * Tol.ChordAngle && Math.Abs(tb - ta) > 1e-12)
                for (int j = 1; j < m; j++) res.Add(c.PointAt(ta + (tb - ta) * j / m));
            res.Add(pts[i + 1]);
        }
        return res;
    }

    /// <summary>
    /// ШАР, КАТЯЩИЙСЯ ПО ТОЧНЫМ ПОВЕРХНОСТЯМ (пункт 5 плана), — в плоскости сечения ⟂ t.
    ///
    /// ДОБАВЛЕНО. Окружность скругления ставилась касательной к КАСАТЕЛЬНЫМ ПЛОСКОСТЯМ граней
    /// в точке ребра. На кривой грани (выпуклое скругление R20) она отходила от настоящей
    /// поверхности на стрелу прогиба — у R5 с плечом 5 мм до 0.6 мм: вместо касания выходил
    /// слабый излом, шов рассыпался на сотни отрезков (снимок пользователя 22.09).
    /// Центр c ищется Ньютоном из условия «расстояние до каждой точной поверхности равно r»;
    /// точки касания — проекции центра. Затравка — прежний центр по касательным плоскостям.
    /// null — не сошлось (остаётся прежнее построение).
    /// </summary>
    /// <summary>ДИАГНОСТИКА: исход расчёта шара.</summary>
    public static Action<string>? BallDebug;

    internal static (Vector3d C, Vector3d T1, Vector3d T2)? RollingBall(
        ISurface3 s1, IEnumerable<ISurface3> near1, ISurface3 s2, IEnumerable<ISurface3> near2,
        Vector3d t, double r, Vector3d c0)
    {
        (Vector3d P, Vector3d N) On(ISurface3 s, IEnumerable<ISurface3> near, Vector3d x)
        {
            if (InArcRange(s, x)) return ExactSurfaces.ProjectUnsigned(s, x);
            foreach (var n in near)
                if (!ReferenceEquals(n, s) && InArcRange(n, x)) return ExactSurfaces.ProjectUnsigned(n, x);
            return ExactSurfaces.ProjectUnsigned(s, x);
        }

        var c = c0;
        Vector3d ref1 = Vector3d.Zero, ref2 = Vector3d.Zero;
        Vector3d q1 = c, q2 = c;
        for (int it = 0; it < 40; it++)
        {
            var (a1, m1) = On(s1, near1, c);
            var (a2, m2) = On(s2, near2, c);
            if (m1.LengthSquared < 1e-24 || m2.LengthSquared < 1e-24) return null;
            m1 = m1.Normalized(); m2 = m2.Normalized();
            if (it == 0) { ref1 = c0 - a1; ref2 = c0 - a2; }
            if (Vector3d.Dot(m1, ref1) < 0) m1 = -m1;
            if (Vector3d.Dot(m2, ref2) < 0) m2 = -m2;
            q1 = a1; q2 = a2;
            double f1 = Vector3d.Dot(c - a1, m1) - r, f2 = Vector3d.Dot(c - a2, m2) - r;
            if (Math.Abs(f1) < 1e-11 && Math.Abs(f2) < 1e-11) break;
            // m1·δ = −f1, m2·δ = −f2, t·δ = 0.
            Vector3d bc = Vector3d.Cross(m2, t), ca = Vector3d.Cross(t, m1), ab = Vector3d.Cross(m1, m2);
            double det = Vector3d.Dot(m1, bc);
            if (Math.Abs(det) < 1e-9) { BallDebug?.Invoke($"шар: det {det:E1} на шаге {it}"); return null; }  // грани почти касаются
            Vector3d d = (bc * -f1 + ca * -f2) / det;
            if (d.Length > r) d = d.Normalized() * r;            // шаг не больше радиуса
            c += d;
        }
        if ((c - c0).Length > 2 * r) { BallDebug?.Invoke($"шар: ушёл на {(c - c0).Length:0.###}"); return null; }
        if (Math.Abs((c - q1).Length - r) > 1e-6 * Math.Max(1, r) || Math.Abs((c - q2).Length - r) > 1e-6 * Math.Max(1, r))
        { BallDebug?.Invoke($"шар: не сошёлся, |c−q1| {(c - q1).Length:0.######} |c−q2| {(c - q2).Length:0.######} (r {r}); поверхности {s1.GetType().Name}/{s2.GetType().Name}"); return null; }
        BallDebug?.Invoke($"шар: сдвиг центра {(c - c0).Length:0.####}");
        return (c, q1, q2);
    }

    /// <summary>
    /// ТОЧКА НА ГРАНИ ИЛИ НА КАСАТЕЛЬНОЙ К НЕЙ СОСЕДНЕЙ ГРАНИ — ТАМ, ГДЕ ОНА ЛЕЖИТ НА ТЕЛЕ.
    ///
    /// ДОБАВЛЕНО. Край кармана переходит с верхней грани на скругление R20 по касательной;
    /// точка фаски в 5 мм от ребра у стыка проецировалась на своей стороне на плоскость верха,
    /// продолженную за её край, а на соседнем ребре — на цилиндр скругления. Поверхность фаски
    /// разрывалась на замыкании кольца на 0.16 мм, кривая пересечения по краю фаски не
    /// строилась, и R5 по этому краю отказывал (снимок пользователя 22.09).
    /// Дуговая поверхность выдавливания (скругление) ограничена углом дуги: точка, попавшая
    /// в этот угол, лежит на скруглении; иначе — на своей грани.
    /// </summary>
    private static Vector3d OntoFaceNear(TFace f, IEnumerable<TFace> neighbours, Vector3d p)
    {
        var own = f.Surface is FacetedSurface fo ? fo.Source : f.Surface;
        if (InArcRange(own, p)) return OntoFace(f, p);
        foreach (var n in neighbours)
        {
            if (ReferenceEquals(n, f)) continue;
            var src = n.Surface is FacetedSurface fs ? fs.Source : n.Surface;
            if (InArcRange(src, p)) return OntoFace(n, p);
        }
        return OntoFace(f, p);
    }

    /// <summary>То же для точных поверхностей (формула рабочей поверхности).</summary>
    internal static Vector3d OntoSurfaceNear(ISurface3 own, IEnumerable<ISurface3> neighbours, Vector3d p)
    {
        if (InArcRange(own, p)) return ExactSurfaces.ProjectUnsigned(own, p).P;
        foreach (var n in neighbours)
            if (!ReferenceEquals(n, own) && InArcRange(n, p)) return ExactSurfaces.ProjectUnsigned(n, p).P;
        return ExactSurfaces.ProjectUnsigned(own, p).P;
    }

    /// <summary>Поверхность — дуга, выдавленная поперёк своей плоскости, и точка в угле дуги.</summary>
    private static bool InArcRange(ISurface3? s, Vector3d p)
    {
        if (s is not ExtrudedSurface ex || ex.Base is not ArcCurve3 arc) return false;
        double u = CurveParam.Closest(arc, p - ex.Dir.Normalized() * Vector3d.Dot(p - arc.Frame.Origin, ex.Dir.Normalized()));
        return u >= -1e-9 && u <= 1 + 1e-9 && !arc.IsClosed;
    }

    /// <summary>
    /// Точка, спроецированная на ПОВЕРХНОСТЬ грани — неограниченную: на плоскость или на
    /// исходную гладкую поверхность. Не на треугольники самой грани: за её границей
    /// проекция прилипала к кромке (у стыка верха со скруглением точка фаски вставала
    /// на линию касания), сечения сплющивались, и резец складывался гармошкой.
    /// </summary>
    private static Vector3d OntoFace(TFace f, Vector3d p)
    {
        ISurface3? src = f.Surface is FacetedSurface fsrc ? fsrc.Source : f.Surface;
        if (src is PlaneSurface plane)
        {
            Vector3d o = plane.F.Origin, nn = plane.F.W;
            return p - nn * Vector3d.Dot(p - o, nn);
        }

        // ТОЧНЫЕ ПРОЕКЦИИ ДЛЯ ЧАСТЫХ СЛУЧАЕВ.
        //
        // ИСПРАВЛЕНО. Грани бруска — не «плоскость», а выдавливание отрезка, и проекция
        // шла через параметры исходной поверхности: они приближённые и ограничены концами
        // отрезка. Точки фаски сдвигались на сотые (9.974 вместо 10), и кромка фаски
        // не собиралась в дугу. Плоская грань — на её плоскость (нормаль и любая вершина),
        // цилиндр — по радиусу от оси. Обе проекции неограниченные.
        if (f.Surface.IsPlanar && f.Surface is FacetedSurface fp && fp.Facets.Count > 0)
        {
            Vector3d nn = SmoothNormal(f, fp.Facets[0].Item1, Tol.Weld * 100);
            if (nn.LengthSquared > 0.5)
                return p - nn * Vector3d.Dot(p - fp.Facets[0].Item1, nn);
        }
        if (CylinderOf(f) is { } cyl)
        {
            var arc = (ArcCurve3)cyl.Base;
            Vector3d axis = cyl.Dir.Normalized();
            Vector3d rel = p - arc.Frame.Origin;
            Vector3d along = axis * Vector3d.Dot(rel, axis);
            Vector3d radial = rel - along;
            if (radial.LengthSquared > 1e-18)
                return arc.Frame.Origin + along + radial.Normalized() * arc.Radius;
        }
        if (src != null && src is not FacetedSurface)
            return src.PointAt(src.ProjectToUV(p));

        if (f.Surface is FacetedSurface fs && fs.Facets.Count > 0)
        {
            double best = double.MaxValue; Vector3d q = p;
            foreach (var (a, b, c) in fs.Facets)
            {
                Vector3d x = ClosestOnTriangle(p, a, b, c);
                double d = (x - p).LengthSquared;
                if (d < best) { best = d; q = x; }
            }
            return q;
        }
        if (f.Surface is PlaneSurface ps)
        {
            Vector3d o = ps.F.Origin, nn = ps.F.W;
            return p - nn * Vector3d.Dot(p - o, nn);
        }
        return p;
    }

    /// <summary>
    /// Гладкая ли цепочка: в каждой общей вершине два ребра уходят почти в противоположные
    /// стороны — излом меньше порога касательной цепочки. Вершина с тремя и более рёбрами
    /// — не гладкая.
    /// </summary>
    private static bool SmoothChain(List<TEdge> edges)
    {
        static (double, double, double) K(Vector3d p) => (Math.Round(p.X, 7), Math.Round(p.Y, 7), Math.Round(p.Z, 7));
        var at = new Dictionary<(double, double, double), List<(TEdge E, bool AtStart)>>();
        foreach (var e in edges)
        {
            foreach (var (p, start) in new[] { (e.V0.P, true), (e.V1.P, false) })
            {
                if (!at.TryGetValue(K(p), out var l)) at[K(p)] = l = new List<(TEdge, bool)>();
                l.Add((e, start));
            }
        }

        double limit = Math.Cos(EdgeChain.DefaultBreakDegrees * Math.PI / 180);
        foreach (var list in at.Values)
        {
            if (list.Count == 1) continue;
            if (list.Count > 2) return false;
            Vector3d a = EdgeOutOf(list[0].E, list[0].AtStart), b = EdgeOutOf(list[1].E, list[1].AtStart);
            if (Vector3d.Dot(a, b) > -limit) return false;
        }
        return true;
    }

    /// <summary>Направление, в котором ребро уходит из своего конца (V0 при atStart, иначе V1).</summary>
    private static Vector3d EdgeOutOf(TEdge e, bool atStart)
    {
        {
            Vector3d from = atStart ? e.V0.P : e.V1.P;
            Vector3d c0 = e.Curve.PointAt(0), c1 = e.Curve.PointAt(1);
            bool curveFromHere = (c0 - from).LengthSquared <= (c1 - from).LengthSquared;
            Vector3d d = curveFromHere ? e.Curve.PointAt(1e-4) - c0 : e.Curve.PointAt(1 - 1e-4) - c1;
            return d.LengthSquared > 0 ? d.Normalized() : Vector3d.Zero;
        }
    }

    /// <summary>
    /// ГЛАДКИЕ УЧАСТКИ ВЫБОРА: рёбра объединяются, только если в общей вершине их ровно
    /// два и стык гладкий (излом меньше порога цепочек).
    ///
    /// ДОБАВЛЕНО. Протянутый резец рассчитан на гладкую цепочку. Край кармана, заходящий
    /// с верхней грани на наклонную фаску, — кольцо из двух рёбер с двумя изломами на линии
    /// стыка граней: сечения на изломе разворачивались, и резец «пересекал сам себя» —
    /// фаска и скругление по такой кромке отказывали (снимок пользователя 22.09). Теперь
    /// каждый гладкий участок режется своим резцом (протянутым или специальным), а угол
    /// между ними проходят продления концов — как у отдельных рёбер.
    /// </summary>
    private static List<List<int>> SmoothPieces(List<TEdge> edges)
    {
        static (double, double, double) K(Vector3d p) => (Math.Round(p.X, 7), Math.Round(p.Y, 7), Math.Round(p.Z, 7));
        var parent = Enumerable.Range(0, edges.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var at = new Dictionary<(double, double, double), List<(int I, bool AtStart)>>();
        for (int i = 0; i < edges.Count; i++)
            foreach (var (p, start) in new[] { (edges[i].V0.P, true), (edges[i].V1.P, false) })
            {
                if (!at.TryGetValue(K(p), out var l)) at[K(p)] = l = new List<(int, bool)>();
                l.Add((i, start));
            }
        double limit = Math.Cos(EdgeChain.DefaultBreakDegrees * Math.PI / 180);
        foreach (var list in at.Values)
        {
            if (list.Count != 2 || list[0].I == list[1].I) continue;
            Vector3d a = TangentOutOf(edges[list[0].I], list[0].AtStart), b = TangentOutOf(edges[list[1].I], list[1].AtStart);
            if (Vector3d.Dot(a, b) <= -limit) parent[Find(list[0].I)] = Find(list[1].I);
        }
        return Enumerable.Range(0, edges.Count).GroupBy(Find).Select(g => g.ToList()).ToList();
    }

    /// <summary>
    /// Точная касательная ребра на конце, направленная от этого конца внутрь ребра.
    /// Не разностью «точка через 1e-4 минус конец»: у кривой пересечения конец — вершина
    /// на сетке ядра, а соседняя точка уточнена, и такая разность давала случайный излом —
    /// гладкий край отверстия со скругления на верх рвался на куски.
    /// </summary>
    private static Vector3d TangentOutOf(TEdge e, bool atStart)
    {
        var c = e.Curve;
        Vector3d from = atStart ? e.V0.P : e.V1.P;
        bool curveFromHere = (c.PointAt(c.T0) - from).LengthSquared <= (c.PointAt(c.T1) - from).LengthSquared;
        Vector3d t = curveFromHere ? c.TangentAt(c.T0) : -c.TangentAt(c.T1);
        return t.LengthSquared > 0 ? t.Normalized() : EdgeOutOf(e, atStart);
    }

    /// <summary>Связные цепочки выбранных рёбер: номера рёбер, делящих вершины.</summary>
    private static List<List<int>> Components(List<TEdge> edges)
    {
        var parent = Enumerable.Range(0, edges.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var byVertex = new Dictionary<(double, double, double), int>();
        for (int i = 0; i < edges.Count; i++)
            foreach (var p in new[] { edges[i].V0.P, edges[i].V1.P })
            {
                var k = (Math.Round(p.X, 7), Math.Round(p.Y, 7), Math.Round(p.Z, 7));
                if (byVertex.TryGetValue(k, out int o)) parent[Find(i)] = Find(o); else byVertex[k] = i;
            }
        return Enumerable.Range(0, edges.Count).GroupBy(Find).Select(g => g.ToList()).ToList();
    }

    private static Frame3 SectionFrame(Vector3d origin, Vector3d t)
    {
        Vector3d any = Math.Abs(t.Z) < 0.9 ? Vector3d.UnitZ : Vector3d.UnitX;
        Vector3d u = Vector3d.Cross(any, t).Normalized();
        Vector3d v = Vector3d.Cross(t, u).Normalized();

        return new Frame3(origin, u, v);
    }

    /// <summary>
    /// ВЫПУКЛОЕ ЛИ РЕБРО.
    ///
    /// Двугранный угол между нормалями об этом не говорит: у выпуклого
    /// ребра куба и у вогнутого угла того же куба он одинаков — 90°.
    /// Отличает их то, с какой стороны материал: чуть-чуть наружу вдоль
    /// биссектрисы нормалей от выпуклого ребра — пустота, от вогнутого —
    /// материал.
    /// </summary>
    /// <summary>
    /// ВЫПУКЛОЕ ЛИ РЕБРО.
    ///
    /// ИСПРАВЛЕНО. Проба ставилась ПЕРЕД обеими гранями, и пусто там должно
    /// было означать «выпуклое». Но у вогнутого угла перед обеими гранями тоже
    /// пусто: материал занимает три четверти из четырёх, и пустая — как раз эта.
    /// Вогнутое ребро — там, где прибавленный брусок встречает грань тела, —
    /// проходило за выпуклое, и скругление вычитало клин из материала,
    /// прорезая выемку. Это нашёл проверочный прогон (Parametric3D.Verify).
    ///
    /// Верная проба — перед одной гранью и ЗА другой: у выпуклого ребра там
    /// пусто (материал только за обеими), у вогнутого — материал.
    ///
    /// ДЫРЯВАЯ СЕТКА, НА КОТОРУЮ Я ГРЕШИЛ, БЫЛА СЛЕДСТВИЕМ МОЕЙ ЖЕ ОШИБКИ В ПОСТРОЕНИИ СЦЕНЫ.
    /// Резец строился по профилю, точки которого лежали ВНЕ своей плоскости: вытягивание берёт
    /// их проекцию, и тело выходило не ступенькой, а половиной бруска с лишней гранью. Отсюда и
    /// шесть сторон без пары, и ложная проба у поверхности.
    ///
    /// Когда плоскость профиля проведена через сам профиль, ступенька строится ЧИСТО: объём
    /// 36 000 ровно, сетка замкнута, замечаний топологии нет. Это закреплено проверкой
    /// A_step_cut_leaves_a_sound_body.
    ///
    /// Поэтому вопрос о вогнутых скруглениях надо ставить заново — на верно построенной
    /// ступеньке, а не на той, где я искал изъян четыре захода.
    /// </summary>
    private static bool Convex(List<TSolid> bodies, Vector3d mid, Vector3d n1, Vector3d n2,
                               TEdge? edge = null)
    {
        if (edge != null && TryPlanarConvexity(edge, mid, n1, n2, out bool convex))
            return convex;

        Vector3d d12 = n1 - n2, d21 = n2 - n1;
        if (d12.Length < 1e-12) return true;

        d12 = d12.Normalized();
        d21 = d21.Normalized();

        var bounds = Bounds3.Empty;
        foreach (var s in bodies) bounds.Add(s.Bounds);

        double step = Math.Max(bounds.Diagonal * 1e-3, Tol.Weld * 10);

        var tris = new List<(Vector3d, Vector3d, Vector3d)>();

        foreach (var s in bodies)
            tris.AddRange(Voxels.Occupancy.Triangles(Tessellator.Tessellate(s, facets: true)));

        return !Voxels.PointInSolid.Test(tris, mid + d12 * step) &&
               !Voxels.PointInSolid.Test(tris, mid + d21 * step);
    }

    /// <summary>
    /// Локальный двугранный угол прямого ребра двух плоскостей. Внутренность первой грани
    /// лежит за плоскостью второй у выпуклого угла и перед ней у вогнутого. Требуем согласия
    /// ОБЕИХ граней. Направлению coedge не доверяем: его соглашение ещё не унифицировано.
    /// </summary>
    private static bool TryPlanarConvexity(TEdge edge, Vector3d mid, Vector3d n1, Vector3d n2,
                                          out bool convex)
    {
        convex = false;
        if (edge.Curve is not LineCurve3 || edge.FaceA is not { } a || edge.FaceB is not { } b ||
            !a.Surface.IsPlanar || !b.Surface.IsPlanar)
            return false;
        var delta = edge.V1.P - edge.V0.P;
        if (delta.LengthSquared < 1e-24) return false;
        var tangent = delta.Normalized();
        double tolerance = Math.Max(2 * Exact.ExactGrid.StepMm,
            4 * Math.Max(Tol.Weld, edge.EffectiveTolerance));

        int Side(TFace face, Vector3d ownNormal, Vector3d otherNormal)
        {
            IEnumerable<(Vector3d A, Vector3d B, Vector3d C)> Triangles()
            {
                if (face.Surface is FacetedSurface fs)
                {
                    // Facets принадлежат геометрии Boolean. Display может зависеть от LOD.
                    foreach (var tri in fs.Facets) yield return tri;
                    yield break;
                }
                var mesh = new MeshBuilder();
                face.Surface.TessellateFace(face, mesh);
                var p = mesh.PositionsD;
                var ids = mesh.IndicesList;
                for (int i = 0; i + 2 < ids.Count; i += 3)
                    yield return (p[ids[i]], p[ids[i + 1]], p[ids[i + 2]]);
            }

            int side = 0;
            foreach (var (pa, pb, pc) in Triangles())
            {
                for (int i = 0; i < 3; i++)
                {
                    var p = i == 0 ? pa : i == 1 ? pb : pc;
                    var q = i == 0 ? pb : i == 1 ? pc : pa;
                    var r = i == 0 ? pc : i == 1 ? pa : pb;
                    // Берём только треугольник, прилегающий к СЕРЕДИНЕ выбранного ребра.
                    // Дальние вершины невыпуклой грани не определяют локальный угол.
                    if (Vector3d.Cross(p - mid, tangent).Length > tolerance ||
                        Vector3d.Cross(q - mid, tangent).Length > tolerance ||
                        (ClosestOnSegment(mid, p, q) - mid).Length > tolerance)
                        continue;
                    if (Math.Abs(Vector3d.Dot(r - mid, ownNormal)) > tolerance) continue;
                    double across = Vector3d.Dot(r - mid, otherNormal);
                    if (Math.Abs(across) <= tolerance) continue;
                    int next = Math.Sign(across);
                    if (side != 0 && side != next) return 0; // неоднозначная/повреждённая окрестность
                    side = next;
                }
            }
            return side;
        }

        int sa = Side(a, n1, n2), sb = Side(b, n2, n1);
        if (sa == 0 || sa != sb) return false;
        convex = sa < 0;
        return true;
    }

    // ------------------------------------------------------------ сечения

    /// <summary>
    /// Треугольник фаски: угол и две точки на гранях.
    ///
    /// Направление «вдоль грани от ребра» выбирается по соседней грани:
    /// из двух перпендикуляров к нормали в плоскости сечения нужен тот,
    /// что уходит ВНУТРЬ материала, то есть за отрицательную сторону
    /// нормали соседа.
    /// </summary>
    private static List<ICurve3>? ChamferSection(Vector3d corner, Vector3d t,
                                                 Vector3d n1, Vector3d n2, double d1, double d2)
    {
        Vector3d f1 = AlongFace(n1, t, n2);
        Vector3d f2 = AlongFace(n2, t, n1);

        Vector3d p1 = corner + f1 * d1;
        Vector3d p2 = corner + f2 * d2;

        if (Vector3d.Cross(p1 - corner, p2 - corner).Length < Tol.Weld) return null;

        return new List<ICurve3>
        {
            new LineCurve3(corner, p1),
            new LineCurve3(p1, p2),
            new LineCurve3(p2, corner)
        };
    }

    /// <summary>
    /// Уголок скругления: два отрезка до точек касания и дуга между ними.
    ///
    /// Центр катящегося шара — точка, равноудалённая на r от обеих граней
    /// и лежащая внутри материала: C = O + r*(d1+d2)/(1+d1·d2), где d1 и d2 —
    /// внутренние нормали. Точки касания получаются проекцией центра
    /// на грани, то есть сдвигом на r вдоль НАРУЖНЫХ нормалей.
    ///
    /// Проверено на прямом угле: при r = 10 центр уходит в (10,10),
    /// касания ложатся на (0,10) и (10,0), площадь уголка r² − πr²/4 = 21.46,
    /// а срез по ребру длиной 100 даёт 2146 — ровно столько, сколько снимает
    /// скругление R10 на кубе 100.
    /// </summary>
    private static List<ICurve3>? RoundSection(Frame3 section, Vector3d corner,
                                               Vector3d n1, Vector3d n2, double r)
    {
        Vector3d d1 = -n1, d2 = -n2;

        double k = 1 + Vector3d.Dot(d1, d2);
        if (k < 1e-9) return null;

        Vector3d centre = corner + (d1 + d2) * (r / k);

        Vector3d t1 = centre + n1 * r;
        Vector3d t2 = centre + n2 * r;

        // Дуга строится во фрейме с центром в шаре и теми же осями,
        // что у сечения: углы берутся в них.
        var arcFrame = new Frame3(centre, section.U, section.V);

        Vector2d l1 = arcFrame.ToLocal2(t1);
        Vector2d l2 = arcFrame.ToLocal2(t2);

        double a1 = Math.Atan2(l1.Y, l1.X);
        double a2 = Math.Atan2(l2.Y, l2.X);

        // Нужна КОРОТКАЯ дуга — та, что выгибается к углу. Длинная обогнула
        // бы центр с другой стороны и срезала бы половину детали.
        double sweep = a2 - a1;
        while (sweep <= -Math.PI) sweep += 2 * Math.PI;
        while (sweep > Math.PI) sweep -= 2 * Math.PI;

        if (Math.Abs(sweep) < 1e-9) return null;

        return new List<ICurve3>
        {
            new LineCurve3(corner, t1),
            new ArcCurve3(arcFrame, r, a1, a1 + sweep),
            new LineCurve3(t2, corner)
        };
    }

    /// <summary>
    /// Помещается ли срез на грани: как далеко грань тянется от ребра
    /// в нужную сторону.
    ///
    /// Мерится по вершинам контуров грани — этого достаточно для плоской
    /// грани с прямыми рёбрами и не требует ни разбора петель, ни проекции
    /// в параметры поверхности.
    /// </summary>
    /// <remarks>
    /// ВОГНУТОЕ РЕБРО ОТКАЗЫВАЕТ ИМЕННО ЗДЕСЬ. На ступеньке (внутренний угол между дном и стенкой)
    /// запас грани выходит нулевым, и построение резца отказывается словами
    /// «срез уходит на 4 поперёк первой грани, а она там шириной 0».
    ///
    /// Запас меряется как наибольшее продвижение вершин грани в направлении `dir`. У выпуклого
    /// ребра это направление уходит ВДОЛЬ грани прочь от ребра, и вершины дают положительные
    /// значения. У вогнутого грань лежит по другую сторону, все произведения выходят
    /// отрицательными, наибольшее — ноль, и проверка объявляет грань бесконечно узкой.
    ///
    /// РАЗБОР ДОВЕДЁН ДО ТОЧНОГО МЕСТА. На ступеньке (брусок 60×40×20 минус брусок от x=30,
    /// z=10) отказ приходит с углом (30, 0, 10) и направлением (1, 0, 0). Это направление вдоль
    /// ПОЛА ступеньки, который тянется от x=30 до x=60 — запас там 30 мм, а не ноль. Значит,
    /// проверка получает направление одной грани, а меряет его по другой: по стенке x=30, у
    /// которой в этом направлении протяжённости нет вовсе.
    ///
    /// Пробовал брать направления вдоль граней от ИСХОДНЫХ нормалей, а не от развёрнутых: это
    /// верно по существу (грань тянется от ребра в одну сторону независимо от того, режем мы или
    /// прибавляем), но отказ остался тем же — значит, перепутаны не знаки, а сами ПАРЫ «грань и
    /// её направление». Чинить надо именно это соответствие, и прежде чем менять, надо понять,
    /// где пары расходятся: в развороте нормалей или в выборе первой и второй грани.
    /// </remarks>
    private static string? Fits(TFace face, Vector3d corner, Vector3d dir,
                                double need, string which)
    {
        double reach = 0;

        foreach (var loop in face.Loops)
            foreach (var ce in loop.CoEdges)
            {
                reach = Math.Max(reach, Vector3d.Dot(ce.Edge.V0.P - corner, dir));
                reach = Math.Max(reach, Vector3d.Dot(ce.Edge.V1.P - corner, dir));
            }

        if (need <= reach + Tol.Weld) return null;

        return $"the cut reaches {need:0.###} across the {which} face, which is only " +
               $"{reach:0.###} wide there";
    }

    /// <summary>Направление в плоскости сечения вдоль грани, прочь от ребра.</summary>
    private static Vector3d AlongFace(Vector3d n, Vector3d t, Vector3d otherNormal)
    {
        Vector3d f = Vector3d.Cross(n, t).Normalized();
        return Vector3d.Dot(f, otherNormal) < 0 ? f : -f;
    }

    private static string Show(Vector3d p) => $"({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})";

    /// <summary>Находит рёбра по id во всех телах.</summary>
    public static IEnumerable<TEdge> ResolveEdges(List<TSolid> bodies, IReadOnlyList<uint> ids)
    {
        var set = new HashSet<uint>(ids);

        foreach (var b in bodies)
            foreach (var e in b.Edges)
                if (set.Contains(e.Id)) yield return e;
    }
}
