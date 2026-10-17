using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Tess;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Viewport;

using Parametric3D.Render;

/// <summary>
/// Отрисовка размеров эскиза: выносные линии, размерная линия со стрелками
/// и текст значения.
///
/// Всё строится ломаными и уходит в тот же пакет линий, что и остальная
/// графика: текст рисуется штриховым шрифтом (см. StrokeFont), стрелки —
/// двумя отрезками. Отдельной подсистемы вывода текста не требуется.
///
/// Размеры задаются в ПИКСЕЛЯХ и переводятся в единицы модели через
/// текущий масштаб, поэтому при зуме размерные линии и цифры сохраняют
/// одинаковый вид, а не растут вместе с деталью.
/// </summary>
public static class SketchDimensions
{
    private static Settings S => Settings.Current;

    public static double ArrowLengthPx => S.ArrowLengthPx;
    /// <summary>Полуширина наконечника. По чертёжной норме длина втрое больше ширины.</summary>
    public static double ArrowHalfWidthPx => S.ArrowLengthPx / S.ArrowLengthToWidth * 0.5;
    public static double TextHeightPx => S.DimensionTextHeightPx;
    public static double GapPx => S.DimensionGapPx;
    public static double OverrunPx => S.DimensionOverrunPx;

    /// <summary>Порядковый номер стиля размера: 0 обычный, 1 заблокированный, 2 справочный.</summary>
    public static int StyleSlot(SkDimension d) => d.Reference ? 2 : d.Locked ? 1 : 0;

    /// <param name="pixelSize">Сколько единиц модели приходится на один пиксель.</param>
    /// <summary>Направления экрана «вправо» и «вверх» в координатах плоскости эскиза (задаёт вид перед Build).</summary>
    public static Vector2d ScreenRight = new(1, 0), ScreenUp = new(0, 1);

    public static void Build(Sketch sk, DimBatch batch, double pixelSize)
    {
        if (pixelSize <= 0) return;
        Frame3 plane = sk.Plane;
        _loops = ClosedLoops(sk);

        foreach (var d in sk.Dimensions)
        {
            if (!d.Enabled) continue;

            // Цвет говорит о роли размера:
            //   красный   — конфликтует и исключён из решения,
            //   фиолетовый — справочный,
            //   голубой   — заблокирован, то есть управляет эскизом,
            //   серый     — измеряет геометрию, но не задаёт её.
            LineStyle style = d.Conflict ? LineStyle.DimensionConflict
                            : d.Reference ? LineStyle.DimensionReference
                            : d.Locked ? LineStyle.DimensionLocked
                            : LineStyle.DimensionDefault;

            // Всё, что относится к одному размеру, ложится в буфер подряд
            // и получает общий диапазон подсветки: иначе при наведении
            // загоралась бы только последняя ломаная.
            CurrentPickId = d.Id;
            batch.BeginGroup(d.Id);

            switch (d.Type)
            {
                case DimensionType.Radius:
                case DimensionType.Diameter:
                case DimensionType.EllipseRadiusU:
                case DimensionType.EllipseRadiusV:
                case DimensionType.ArcLength:
                    BuildRadial(d, plane, batch, pixelSize, style);
                    break;

                case DimensionType.AngleLineLine:
                    BuildAngle(d, plane, batch, pixelSize, style);
                    break;

                default:
                    BuildLinear(d, plane, batch, pixelSize, style);
                    break;
            }

            batch.EndGroup();
        }

        CurrentPickId = 0;
        _loops = null;
    }

    private static List<List<Vector2d>>? _loops;

    /// <summary>Замкнутые контуры эскиза — по ним размер понимает, где «снаружи».</summary>
    private static List<List<Vector2d>> ClosedLoops(Sketch sk)
    {
        var result = new List<List<Vector2d>>();
        try
        {
            var pr = sk.ToProfile();
            foreach (var loop in pr.Loops)
            {
                if (loop.SelfIntersects) continue;
                var pts = loop.Sample(pr.Plane);
                if (pts.Count >= 3) result.Add(pts);
            }
        }
        catch
        {
            // Недостроенный контур не мешает: сторона тогда любая.
        }
        return result;
    }

    private static bool InsideAny(Vector2d p)
    {
        if (_loops == null) return false;
        foreach (var loop in _loops)
            if (GeomUtil.PointInPolygon(loop, p)) return true;
        return false;
    }

    /// <summary>Идентификатор размера, который рисуется сейчас.</summary>
    private static uint CurrentPickId;

    // ------------------------------------------------------------ линейные

    private static void BuildLinear(SkDimension d, Frame3 plane, DimBatch batch,
                                    double px, LineStyle style)
    {
        if (!LinearEnds(d, out Vector2d a, out Vector2d b)) return;

        // Если замер идёт до ПРЯМОЙ, проекция может лечь за пределы самого
        // отрезка — тогда выносная линия висела в пустоте и не доходила
        // до геометрии. Чертёжная норма в этом случае требует тонкого
        // продолжения отрезка до точки замера.
        switch (d.Type)
        {
            case DimensionType.DistancePointLine when d.Items.Count >= 2 && d.Items[1] is SkLine gl:
                GeometryExtension(batch, plane, style, gl, b);
                break;
            case DimensionType.DistanceLineLine when d.Items.Count >= 2:
                if (d.Items[0] is SkLine g1) GeometryExtension(batch, plane, style, g1, a);
                if (d.Items[1] is SkLine g2) GeometryExtension(batch, plane, style, g2, b);
                break;
        }

        Vector2d span = b - a;
        if (span.Length < 1e-9) return;

        Vector2d dir = span.Normalized();
        Vector2d nrm = new(-dir.Y, dir.X);

        // Смещение подписи раскладывается на ОБЕ составляющие: поперёк
        // размера оно задаёт вынос размерной линии, вдоль — сдвиг подписи.
        // Раньше вдольная составляющая отбрасывалась, и текст двигался
        // только в одну сторону.
        double off = Vector2d.Dot(d.TextOffset, nrm);
        double along = Vector2d.Dot(d.TextOffset, dir);
        if (Math.Abs(off) < px) off = px * 20;

        // Пока подпись не поставили мышью, размер сам уходит НАРУЖУ контура.
        // Иначе размеры прямоугольника ложатся внутрь него и перекрывают
        // и заливку, и саму фигуру.
        if (!d.UserPlaced)
        {
            Vector2d mid0 = (a + b) * 0.5;
            if (InsideAny(mid0 + nrm * Math.Abs(off))) off = -Math.Abs(off);
            else off = Math.Abs(off);
        }

        Vector2d a2 = a + nrm * off;
        Vector2d b2 = b + nrm * off;

        double sign = Math.Sign(off);
        double gap = GapPx * px * sign;
        double over = (Math.Abs(off) + OverrunPx * px) * sign;

        // Выносные линии — от геометрии чуть дальше размерной.
        Line(batch, plane, style, a + nrm * gap, a + nrm * over);
        Line(batch, plane, style, b + nrm * gap, b + nrm * over);

        // Стрелки внутри или снаружи. В режиме Auto они выносятся наружу,
        // когда между выносными линиями не помещаются ни две стрелки,
        // ни подпись между ними: иначе размер превращается в кляксу.
        double arrowLen = ArrowLengthPx * px;
        double textWidth = StrokeFont.Width(Label(d)) * TextHeightPx * px;

        bool outside = d.Arrows switch
        {
            ArrowPlacement.Inside => false,
            ArrowPlacement.Outside => true,
            _ => span.Length < arrowLen * 2 + textWidth
        };

        if (outside)
        {
            // Внутренней линии замера НЕТ: стрелки снаружи потому и выносятся,
            // что между выносными линиями места не осталось, и линия там
            // только загромождала бы чертёж. Рисуются лишь короткие полки,
            // на которых стоят стрелки.
            Line(batch, plane, style, a2 - dir * arrowLen, a2);
            Line(batch, plane, style, b2, b2 + dir * arrowLen);
            Arrow(batch, plane, style, a2, -dir, px);
            Arrow(batch, plane, style, b2, dir, px);
        }
        else
        {
            Line(batch, plane, style, a2, b2);
            Arrow(batch, plane, style, a2, dir, px);
            Arrow(batch, plane, style, b2, -dir, px);
        }

        // Подпись может уехать за пределы замера — тогда размерная линия
        // продолжается до неё, а стрелки остаются на месте.
        double half = span.Length * 0.5;
        Vector2d mid = (a2 + b2) * 0.5;

        double textHalf = textWidth * 0.5 + px * 2;

        // Снаружи стрелки — снаружи и текст: между выносными линиями
        // для него места нет, ради этого стрелки и выносились.
        if (outside && Math.Abs(along) < half + arrowLen + textHalf)
            along = half + arrowLen + textHalf;

        Vector2d textAt = mid + dir * along;
        double right = along + textHalf;
        double left = along - textHalf;

        // Линия доводится до ДАЛЬНЕГО края подписи, а не до ближнего:
        // текст должен целиком опираться на размерную линию.
        if (right > half) Line(batch, plane, style, b2, mid + dir * right);
        if (left < -half) Line(batch, plane, style, a2, mid + dir * left);

        Text(batch, plane, style, d, textAt, dir, nrm, px);
    }

    /// <summary>Две точки, между которыми меряется расстояние.</summary>
    private static bool LinearEnds(SkDimension d, out Vector2d a, out Vector2d b)
    {
        a = b = Vector2d.Zero;
        var it = d.Items;

        switch (d.Type)
        {
            case DimensionType.DistancePointPoint:
                if (it.Count >= 2 && it[0] is SkPoint p && it[1] is SkPoint q)
                { a = p.P; b = q.P; return true; }
                return false;

            case DimensionType.DistancePointLine:
                if (it.Count >= 2 && it[0] is SkPoint pp && it[1] is SkLine pl)
                { a = pp.P; b = Project(pp.P, pl); return true; }
                return false;

            case DimensionType.DistanceLineLine:
                if (it.Count >= 2 && it[0] is SkLine l1 && it[1] is SkLine l2)
                { a = l1.A.P; b = Project(l1.A.P, l2); return true; }
                return false;

            case DimensionType.CoordX:
                if (it.Count >= 1 && it[0] is SkPoint cx)
                { a = new Vector2d(0, cx.Y); b = cx.P; return true; }
                return false;

            case DimensionType.CoordY:
                if (it.Count >= 1 && it[0] is SkPoint cy)
                { a = new Vector2d(cy.X, 0); b = cy.P; return true; }
                return false;
        }
        return false;
    }

    /// <summary>
    /// Тонкое продолжение отрезка до точки замера, если та лежит за его
    /// концом. Ничего не рисует, когда точка попадает внутрь отрезка.
    /// </summary>
    private static void GeometryExtension(DimBatch batch, Frame3 plane, LineStyle style,
                                          SkLine l, Vector2d anchor)
    {
        Vector2d dir = l.B.P - l.A.P;
        double len2 = dir.LengthSquared;
        if (len2 < 1e-12) return;

        double t = Vector2d.Dot(anchor - l.A.P, dir) / len2;
        if (t >= 0 && t <= 1) return;                 // точка внутри отрезка

        Vector2d from = t < 0 ? l.A.P : l.B.P;
        Line(batch, plane, style, from, anchor);
    }

    private static Vector2d Project(Vector2d p, SkLine l)
    {
        Vector2d d = l.B.P - l.A.P;
        double len2 = d.LengthSquared;
        if (len2 < 1e-12) return l.A.P;
        double t = Vector2d.Dot(p - l.A.P, d) / len2;
        return l.A.P + d * t;
    }

    // ------------------------------------------------- радиус и диаметр

    /// <summary>
    /// Радиус, диаметр и полуоси эллипса.
    ///
    /// Когда подпись остаётся ВНУТРИ фигуры, размер рисуется как обычно:
    /// луч из центра со стрелкой в кривую, у диаметра — насквозь с двумя
    /// стрелками.
    ///
    /// Когда подпись вынесена НАРУЖУ, чертёжная норма разная:
    ///   * у радиуса остаётся одна стрелка в кривую, а от неё к подписи
    ///     идёт выноска, заканчивающаяся горизонтальной полкой под текстом;
    ///   * у диаметра стрелки остаются на линии внутри окружности, а наружу
    ///     выходит только продолжение этой линии с подписью.
    /// </summary>
    private static void BuildRadial(SkDimension d, Frame3 plane, DimBatch batch,
                                    double px, LineStyle style)
    {
        Vector2d center;
        double ru, rv, rot = 0;

        switch (d.Items.Count > 0 ? d.Items[0] : null)
        {
            case SkCircle c: center = c.Center.P; ru = rv = c.Radius; break;
            case SkArc arc: center = arc.Center.P; ru = rv = arc.Radius; break;
            case SkEllipse el: center = el.Center.P; ru = el.RU; rv = el.RV; rot = el.Rotation; break;
            default: return;
        }

        // Направление луча. У полуосей эллипса оно задано самой полуосью,
        // у круглых — туда, куда утащили подпись.
        Vector2d dir;
        double radius;

        switch (d.Type)
        {
            case DimensionType.EllipseRadiusU:
                dir = new Vector2d(Math.Cos(rot), Math.Sin(rot));
                radius = ru;
                break;
            case DimensionType.EllipseRadiusV:
                dir = new Vector2d(-Math.Sin(rot), Math.Cos(rot));
                radius = rv;
                break;
            default:
                dir = d.TextOffset.Length > 1e-9 ? d.TextOffset.Normalized() : new Vector2d(1, 0);
                radius = ru;
                break;
        }

        if (radius < 1e-9) return;

        Vector2d nrm = new(-dir.Y, dir.X);
        Vector2d onCurve = center + dir * radius;

        // Насколько далеко утащили подпись вдоль луча.
        double reach = Vector2d.Dot(d.TextOffset, dir);
        bool outside = reach > radius;

        double textHalf = StrokeFont.Width(Label(d)) * TextHeightPx * px * 0.5;
        bool diameter = d.Type == DimensionType.Diameter;

        // u12: ДИАМЕТР КАК ОБЫЧНЫЙ РАЗМЕР — между двумя точками окружности поперёк направления на подпись: выносные линии
        // и размерная линия со стрелками, подпись посередине
        if (diameter && d.DiameterAsLinear)
        {
            var t = ScreenRight.LengthSquared > 1e-12 ? ScreenRight.Normalized() : new Vector2d(1, 0);
            var n = new Vector2d(-t.Y, t.X);
            double off = Vector2d.Dot(d.TextOffset, n);
            if (Math.Abs(off) < radius + 10 * px) off = (off < 0 ? -1 : 1) * (radius + 24 * px);
            var a0 = center - t * radius; var b0 = center + t * radius;
            var a1 = a0 + n * off; var b1 = b0 + n * off;
            double sg = Math.Sign(off), gap = GapPx * px * sg, over = OverrunPx * px * sg;
            Line(batch, plane, style, a0 + n * gap, a1 + n * over);
            Line(batch, plane, style, b0 + n * gap, b1 + n * over);
            Line(batch, plane, style, a1, b1);
            Arrow(batch, plane, style, a1, t, px);
            Arrow(batch, plane, style, b1, -t, px);
            Text(batch, plane, style, d, (a1 + b1) * 0.5 + n * (sg * TextHeightPx * px * 0.8), t, n, px);
            return;
        }

        if (!outside)
        {
            Vector2d from = diameter ? center - dir * radius : center;
            Line(batch, plane, style, from, onCurve);
            Arrow(batch, plane, style, onCurve, -dir, px);
            if (diameter && !d.SingleArrow) Arrow(batch, plane, style, from, dir, px);

            Vector2d at = diameter ? center : center + dir * (radius * 0.5);
            Text(batch, plane, style, d, at, dir, nrm, px);
            return;
        }

        // Подпись снаружи.
        Vector2d textAt = center + dir * reach;

        if (diameter)
        {
            // Линия замера остаётся внутри окружности, наружу уходит
            // только её продолжение под подпись.
            Line(batch, plane, style, d.SingleArrow ? center : center - dir * radius, onCurve);
            Arrow(batch, plane, style, onCurve, -dir, px);
            if (!d.SingleArrow) Arrow(batch, plane, style, center - dir * radius, dir, px);
            Line(batch, plane, style, onCurve, textAt + dir * textHalf);
            Text(batch, plane, style, d, textAt, dir, nrm, px);
            return;
        }

        // Радиус: одна стрелка в кривую и полка под подписью.
        //
        // Тело стрелки лежит СНАРУЖИ окружности, остриё касается кривой
        // и смотрит внутрь. Раньше тело уходило внутрь — при вынесенной
        // наружу подписи это читалось так, будто мерят что-то другое.
        // Выноска начинается от хвоста стрелки, а не от кривой, иначе
        // линия проходит сквозь наконечник.
        double arrowLen = ArrowLengthPx * px;
        Vector2d tail = onCurve + dir * arrowLen;

        Arrow(batch, plane, style, onCurve, dir, px);
        Line(batch, plane, style, tail, textAt - dir * textHalf);

        // Полка — горизонтальная, длиной с подпись, что бы ни делал луч:
        // текст читается по горизонтали, полка идёт под ним.
        Vector2d shelfDir = new(1, 0);
        if (dir.X < 0) shelfDir = new Vector2d(-1, 0);
        Vector2d shelfStart = textAt - dir * textHalf;
        Line(batch, plane, style, shelfStart, shelfStart + shelfDir * (textHalf * 2));

        Text(batch, plane, style, d,
             shelfStart + shelfDir * textHalf, shelfDir, new Vector2d(-shelfDir.Y, shelfDir.X), px);
    }

    // ---------------------------------------------------------- угловые

    private static void BuildAngle(SkDimension d, Frame3 plane, DimBatch batch,
                                   double px, LineStyle style)
    {
        if (d.Items.Count < 2 || d.Items[0] is not SkLine l1 || d.Items[1] is not SkLine l2) return;

        // Лучи берутся ровно те же, по которым считается величина размера.
        // Иначе дуга и число расходятся, и это уже случалось.
        if (!DimensionMeasure.AngleRays(l1, l2, out Vector2d c, out Vector2d d1, out Vector2d d2)) return;

        double r = d.TextOffset.Length;
        if (r < px * 10) r = px * 40;

        double a0 = Math.Atan2(d1.Y, d1.X);
        double a1 = Math.Atan2(d2.Y, d2.X);

        // Короткая дуга между направлениями: значение в (-pi, pi].
        double shortSweep = GeomUtil.Norm2Pi(a1 - a0);
        if (shortSweep > Math.PI) shortSweep -= Math.PI * 2;

        // Дополняющая дуга — обход в другую сторону, то есть 360 минус угол.
        double sign = shortSweep >= 0 ? 1 : -1;
        double longSweep = shortSweep - Math.PI * 2 * sign;

        // Что показывать, решает СТОРОНА, куда пользователь утащил подпись:
        // сравниваем направление смещения с серединами обеих дуг. Раньше
        // всегда бралась короткая, и подпись можно было двигать лишь
        // в одну сторону от угла.
        // Дополняющую дугу показываем ТОЛЬКО если подпись двигал сам
        // пользователь. У размера, поставленного приложением, смещение
        // подписи ещё случайное, и по нему нельзя решать, какой из двух
        // углов имелся в виду: так на ровном месте получались 353°.
        Vector2d want = d.UserPlaced && d.TextOffset.LengthSquared > 1e-12
            ? d.TextOffset.Normalized()
            : Dir(a0 + shortSweep * 0.5);

        double sweep = Vector2d.Dot(want, Dir(a0 + longSweep * 0.5))
                     > Vector2d.Dot(want, Dir(a0 + shortSweep * 0.5))
            ? longSweep
            : shortSweep;

        // Признак нужен подписи: у дополняющей дуги показывается 360 - угол.
        d.Reflex = sweep != shortSweep;

        // Дуга размера. Сегментов вдвадцатеро больше, чем градусов в дуге,
        // иначе на крупном радиусе видны изломы.
        var arc = new List<Vector2d>();
        int n = Math.Max(40, (int)(Math.Abs(sweep) * 120 / Math.PI));
        for (int i = 0; i <= n; i++) arc.Add(c + Dir(a0 + sweep * i / n) * r);
        Polyline(batch, plane, style, arc);

        // Выносные линии до дуги.
        Line(batch, plane, style, c + d1 * (r * 0.15), c + d1 * (r + OverrunPx * px));
        Line(batch, plane, style, c + d2 * (r * 0.15), c + d2 * (r + OverrunPx * px));

        // Стрелки по касательной к дуге, остриями наружу.
        double s = Math.Sign(sweep);
        Arrow(batch, plane, style, arc[0], new Vector2d(-Math.Sin(a0), Math.Cos(a0)) * s, px);
        Arrow(batch, plane, style, arc[^1], new Vector2d(Math.Sin(a1), -Math.Cos(a1)) * s, px);

        Vector2d radial = Dir(a0 + sweep * 0.5);
        Vector2d mid = c + radial * r;

        // Пара направлений должна быть ПРАВОЙ, иначе строка выходит
        // зеркальной: касательная берётся поворотом радиуса на -90 градусов.
        Text(batch, plane, style, d, mid, new Vector2d(radial.Y, -radial.X), radial, px);
    }

    private static Vector2d Dir(double angle) => new(Math.Cos(angle), Math.Sin(angle));


    // ------------------------------------------------------------ примитивы

    /// <summary>Подпись размера так, как она выводится на экран.</summary>
    public static string Label(SkDimension d)
    {
        // Координатные размеры хранят знак: он говорит, по какую сторону
        // от нуля лежит точка, и нужен решателю. Но на чертеже величина
        // без знака — расстояние отрицательным не бывает.
        string v = d.IsAngular
            ? S.FormatAngle(d.Reflex ? 360 - d.Value : d.Value)
            : S.FormatLength(Math.Abs(d.Value), S.DimensionShowUnits);
        return d.Reference ? "(" + v + ")" : v;
    }

    private static double TextWidth(SkDimension d) => StrokeFont.Width(Label(d));

    /// <summary>
    /// Подпись всегда над размерной линией — как требует чертёжная норма.
    /// Если письмо оказалось «вверх ногами», строка разворачивается на 180°.
    /// </summary>
    private static void Text(DimBatch batch, Frame3 plane, LineStyle style, SkDimension d,
                             Vector2d anchor, Vector2d dir, Vector2d up, double px)
    {
        // читаемость по экрану: письмо — слева направо на экране, знаки — не зеркально (вид может смотреть на плоскость
        // с любым поворотом)
        var R = ScreenRight; var U = ScreenUp;
        double along = Vector2d.Dot(dir, R);
        if (along < -1e-9 || (Math.Abs(along) <= 1e-9 && Vector2d.Dot(dir, U) < 0)) { dir = -dir; up = -up; }
        double hand = R.X * U.Y - R.Y * U.X;
        if ((dir.X * up.Y - dir.Y * up.X) * hand < 0) up = -up;

        double h = TextHeightPx * px;
        string text = Label(d);
        double w = StrokeFont.Width(text) * h;

        Vector2d origin = anchor - dir * (w * 0.5) + up * (px * 3);

        // Центр подписи запоминается для выбора мышью.
        d.TextCenter = origin + dir * (w * 0.5) + up * (h * 0.5);

        // Цифры рисуются своим стилем: толщина их штриха настраивается
        // отдельно от размерных линий.
        LineStyle textStyle = style switch
        {
            LineStyle.DimensionConflict => LineStyle.DimensionTextConflict,
            LineStyle.DimensionDefault => LineStyle.DimensionTextDefault,
            LineStyle.DimensionLocked => LineStyle.DimensionTextLocked,
            LineStyle.DimensionReference => LineStyle.DimensionTextReference,
            _ => LineStyle.DimensionText
        };

        var strokes = new List<List<Vector2d>>();
        StrokeFont.Layout(text, origin, dir, up, h, strokes);
        foreach (var poly in strokes) Polyline(batch, plane, textStyle, poly);
    }

    /// <summary>
    /// Наконечник стрелки — ОДНА сужающаяся полоса.
    ///
    /// Шейдер линий умеет вести полосу от заданной ширины к нулю, считая
    /// покрытие края аналитически. Такая полоса и есть треугольник
    /// с ровными сглаженными сторонами — тем же способом, что и обычные
    /// линии, поэтому выглядит она так же чисто.
    ///
    /// Прежний способ набирал наконечник двумя десятками поперечных
    /// полосок. У каждой были свои сглаженные края и торцы, они ложились
    /// друг на друга, и стрелка выходила размытой и заметно шире, чем
    /// задумано.
    ///
    /// Остриё отводится от выносной линии: вплотную оно читается как её
    /// продолжение, а не как стрелка.
    /// </summary>
    private static void Arrow(DimBatch batch, Frame3 plane, LineStyle style,
                              Vector2d tip, Vector2d along, double px)
    {
        // Полуширина выносной линии плюс настраиваемый зазор. Прибавки
        // на растяжение четырёхугольника больше нет: сужающаяся полоса
        // вдоль оси не растягивается, и остриё лежит ровно там, где задано.
        double gap = (S.DimensionLineWidth * 0.5 + S.ArrowTipGapPx) * px;
        Vector2d apex = tip + along * gap;
        Vector2d back = apex + along * (ArrowLengthPx * px);

        // Ширина основания задаётся отношением: длина втрое больше ширины.
        float widthPx = (float)(ArrowLengthPx / S.ArrowLengthToWidth);

        batch.AddArrow(W(plane, back), W(plane, apex), widthPx, style, CurrentPickId);
    }

    private static void Line(DimBatch batch, Frame3 plane, LineStyle style, Vector2d a, Vector2d b)
    {
        batch.AddSegment(W(plane, a), W(plane, b), style, CurrentPickId);
    }

    private static void Polyline(DimBatch batch, Frame3 plane, LineStyle style, List<Vector2d> pts)
    {
        for (int i = 1; i < pts.Count; i++) Line(batch, plane, style, pts[i - 1], pts[i]);
    }

    private static Vector3 W(Frame3 plane, Vector2d p)
    {
        Vector3d w = plane.ToWorld(p);
        return new Vector3((float)w.X, (float)w.Y, (float)w.Z);
    }
}

/// <summary>Стиль линии размера (цвет — по роли размера).</summary>
public enum LineStyle
{
    DimensionDefault, DimensionLocked, DimensionReference, DimensionConflict,
    DimensionText, DimensionTextDefault, DimensionTextLocked, DimensionTextReference, DimensionTextConflict,
}

/// <summary>Сборщик отрезков размеров (вместо прежнего пакета линий рендера): отрезок, стиль, номер размера.</summary>
public sealed class DimBatch
{
    public readonly List<(Vector3 A, Vector3 B, LineStyle Style, uint Dim)> Segments = new();
    /// <summary>Мировых единиц на пиксель и нормаль плоскости — для наконечников.</summary>
    public double PixelSize = 1;
    public Vector3 Normal = Vector3.UnitZ;

    public void AddSegment(Vector3 a, Vector3 b, LineStyle style, uint pickId, uint flags = 0) => Segments.Add((a, b, style, pickId));
    public void BeginGroup(uint pickId) { }
    public void EndGroup() { }

    /// <summary>Наконечник: треугольник от основания к острию, заполненный несколькими отрезками.</summary>
    public void AddArrow(Vector3 back, Vector3 apex, float widthPx, LineStyle style, uint pickId)
    {
        var dir = apex - back;
        if (dir.LengthSquared < 1e-20f) return;
        var side = Vector3.Cross(Normal, dir).Normalized() * (float)(widthPx * 0.5 * PixelSize);
        for (int i = -2; i <= 2; i++)
            Segments.Add((back + side * (i / 2f), apex, style, pickId));
    }
}
