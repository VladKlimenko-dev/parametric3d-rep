using OpenTK.Mathematics;

namespace Parametric3D.Model.Sketching;

/// <summary>
/// Размеры по умолчанию: те, что приложение ставит само, чтобы эскиз был
/// определён полностью.
///
/// ЗАЧЕМ. На чертеже не бывает «висящей в воздухе» геометрии: положение
/// каждого узла, длина каждой линии и угол между линиями чем-то заданы.
/// В эскизе они тоже заданы — координатами точек, — но пока это не
/// показано размером, пользователь не видит, что именно держит фигуру,
/// и не может это изменить числом.
///
/// КАК. Способ подбора — жадный, а не по заранее написанным правилам для
/// каждой фигуры. Составляется список кандидатов в порядке предпочтения,
/// и каждый примеряется: если он убирает хотя бы одну степень свободы —
/// остаётся, если нет — отбрасывается. Так набор получается минимальным
/// по построению и не требует отдельного разбора прямоугольника,
/// окружности и всего остального.
///
/// Порядок кандидатов и есть главное решение здесь. Он идёт от местного
/// к общему: сначала собственные размеры фигуры, потом углы и расстояния
/// до БЛИЖАЙШЕГО соседа, и только в конце — привязка к нулю эскиза.
/// Иначе всё оказалось бы измерено от одной оси, а такой чертёж читать
/// невозможно.
/// </summary>
public static class DefaultDimensions
{
    /// <summary>Предел на число примерок, страховка от вырожденных случаев.</summary>
    private const int MaxTrials = 200;

    /// <summary>
    /// Пересобирает размеры по умолчанию. Уже существующие удаляются:
    /// пользователь мог добавить свой размер, и прежний набор устарел.
    /// </summary>
    public static void Refresh(Sketch sk, ISketchSolver solver)
    {
        if (!Core.Settings.Current.ShowDefaultDimensions) return;

        // Пересобираем только НЕзаблокированные: заблокированный размер
        // пользователь оставил намеренно, и трогать его нельзя.
        sk.Dimensions.RemoveAll(d => d.IsDefault && !d.Locked);

        // Подбор — это ИЗМЕРЕНИЕ, а не построение: он не имеет права
        // сдвинуть ни одной точки. Но примерка каждого кандидата требует
        // решить эскиз, а решатель работает на живой геометрии. Поэтому
        // положение запоминается до подбора и возвращается после.
        //
        // Страховка не лишняя: любое расхождение между тем, как размер
        // измеряется и как он записан в уравнение, немедленно превращает
        // безобидную примерку в перестроение чертежа.
        var snapshot = Snapshot(sk);

        solver.Solve(sk, null, out int dof);
        if (dof <= 0) { Restore(snapshot); return; }

        int trials = 0;
        var chosen = new List<SkDimension>();

        foreach (var candidate in Candidates(sk))
        {
            if (dof <= 0 || ++trials > MaxTrials) break;

            // На время примерки кандидат делается УПРАВЛЯЮЩИМ: только так
            // решатель его учтёт и покажет, убирает ли он степень свободы.
            // Управление снимается в конце — иначе решатель пропускал бы
            // кандидатов и подбор ничего бы не измерил.
            var d = sk.AddDimension(candidate.Type, candidate.Value, candidate.Items);
            d.Locked = true;

            solver.Solve(sk, null, out int after);

            // Кандидат ничего не убрал — он повторяет то, что уже задано.
            if (after >= dof) { sk.Dimensions.Remove(d); continue; }

            chosen.Add(d);
            dof = after;
        }

        // Теперь они становятся показывающими: серыми и не управляющими.
        // Набор при этом остаётся тем самым минимумом, который определяет
        // фигуру, — просто он не мешает её двигать. Заблокируйте любой
        // из них, и он станет обычным управляющим размером.
        foreach (var d in chosen)
        {
            d.IsDefault = true;
            d.Locked = false;
        }

        Restore(snapshot);
    }

    /// <summary>Положение всех свободных величин эскиза.</summary>
    private readonly struct State
    {
        public readonly List<(SkPoint P, double X, double Y)> Points;
        public readonly List<(SkCircle C, double R)> Circles;
        public readonly List<(SkEllipse E, double U, double V)> Ellipses;

        public State(Sketch sk)
        {
            Points = sk.AllPoints().Select(p => (p, p.X, p.Y)).ToList();
            Circles = sk.Entities.OfType<SkCircle>().Select(c => (c, c.Radius)).ToList();
            Ellipses = sk.Entities.OfType<SkEllipse>().Select(e => (e, e.RU, e.RV)).ToList();
        }
    }

    private static State Snapshot(Sketch sk) => new(sk);

    private static void Restore(State s)
    {
        foreach (var (p, x, y) in s.Points) { p.X = x; p.Y = y; }
        foreach (var (c, r) in s.Circles) c.Radius = r;
        foreach (var (e, u, v) in s.Ellipses) { e.RU = u; e.RV = v; }
    }

    private readonly struct Candidate
    {
        public readonly DimensionType Type;
        public readonly double Value;
        public readonly SkEntity[] Items;

        public Candidate(DimensionType t, double v, params SkEntity[] items)
        {
            Type = t; Value = v; Items = items;
        }
    }

    /// <summary>Кандидаты в порядке предпочтения: от местного к общему.</summary>
    private static IEnumerable<Candidate> Candidates(Sketch sk)
    {
        // Осевые линии тоже размеряются: у них есть длина и угол, и без
        // них ось вращения висела бы в эскизе ничем не заданной.
        // В контур для построения тела они по-прежнему не входят.
        var geometry = sk.Entities
            .Where(e => (e.Role == SkRole.Object || e.Role == SkRole.Centerline) && !e.Fixed)
            .ToList();

        // 1. Собственные размеры фигур: длина, радиус, полуоси.
        foreach (var e in geometry)
        {
            switch (e)
            {
                case SkLine l when l.Length > 1e-9:
                    yield return new Candidate(DimensionType.DistancePointPoint, l.Length, l.A, l.B);
                    break;
                case SkCircle c when c.Radius > 1e-9:
                    yield return new Candidate(
                        Core.Settings.Current.PreferDiameter ? DimensionType.Diameter : DimensionType.Radius,
                        Core.Settings.Current.PreferDiameter ? c.Radius * 2 : c.Radius, c);
                    break;
                case SkArc a when a.Radius > 1e-9:
                    yield return new Candidate(DimensionType.Radius, a.Radius, a);
                    break;
                case SkEllipse el:
                    yield return new Candidate(DimensionType.EllipseRadiusU, el.RU, el);
                    yield return new Candidate(DimensionType.EllipseRadiusV, el.RV, el);
                    break;
            }
        }

        // 2. Углы между линиями, имеющими общую точку. Соседние стороны —
        //    самый естественный способ задать форму контура.
        var lines = geometry.OfType<SkLine>().ToList();
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (!ShareEndpoint(lines[i], lines[j])) continue;

                double angle = DimensionMeasure.LineAngleDegrees(lines[i], lines[j]);
                if (angle < 1e-6 || angle > 180 - 1e-6) continue;

                yield return new Candidate(DimensionType.AngleLineLine, angle, lines[i], lines[j]);
            }

        // 3. Расстояния до ближайшего соседа. Точки перебираются в порядке
        //    удалённости от нуля, и каждая привязывается к уже пройденной —
        //    так цепочка размеров идёт по фигуре, а не расходится веером
        //    от одной точки.
        var points = geometry.SelectMany(e => e.Handles).Distinct()
            .Where(p => !p.Fixed)
            .OrderBy(p => p.P.LengthSquared)
            .ToList();

        var placed = new List<SkPoint>();
        foreach (var p in points)
        {
            SkPoint? nearest = null;
            double best = double.MaxValue;

            foreach (var q in placed)
            {
                double dist = (q.P - p.P).LengthSquared;
                if (dist < best && dist > 1e-12) { best = dist; nearest = q; }
            }

            if (nearest != null)
                yield return new Candidate(DimensionType.DistancePointPoint,
                                           (p.P - nearest.P).Length, p, nearest);

            placed.Add(p);
        }

        // 4. Привязка к нулю эскиза — в последнюю очередь и только для
        //    точек, которые иначе остались бы ни к чему не привязаны.
        foreach (var p in points)
        {
            yield return new Candidate(DimensionType.CoordX, p.X, p);
            yield return new Candidate(DimensionType.CoordY, p.Y, p);
        }
    }

    private static bool ShareEndpoint(SkLine a, SkLine b) =>
        Same(a.A, b.A) || Same(a.A, b.B) || Same(a.B, b.A) || Same(a.B, b.B);

    private static bool Same(SkPoint p, SkPoint q) =>
        ReferenceEquals(p, q) || (p.P - q.P).Length < 1e-7;

}
