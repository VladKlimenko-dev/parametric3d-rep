using System.Numerics;

namespace Parametric3D.Kernel.Exact;

/// <summary>Где лежит многоугольник относительно квадрики.</summary>
public enum QSide
{
    /// <summary>Целиком внутри (значение квадрики отрицательно).</summary>
    Inside,
    /// <summary>Целиком снаружи.</summary>
    Outside,
    /// <summary>Лежит на поверхности (все вершины дают ноль).</summary>
    On,
    /// <summary>Пересекает поверхность — требует разрезания.</summary>
    Crossing
}

/// <summary>
/// ВЕТВЛЕНИЕ ПО КВАДРИКЕ: отнесение многоугольника к сторонам поверхности и его разрезание.
///
/// Нынешнее разбиение пространства умеет делить только плоскостью, поэтому цилиндрический резец
/// приходится превращать в 55-гранную призму — отсюда и погрешность объёма 0.05%, и расхождение
/// дуг 0.013 мм. Здесь узел дерева делит пространство САМОЙ квадрикой.
///
/// Разрез получается точным: точки, где сторона многоугольника встречает поверхность, считаются
/// пересечением прямой с квадрикой, то есть числами с корнем, а не приближением.
///
/// ОГОВОРКА, важная для честности: сам разрез остаётся ЛОМАНЫМ. Настоящая граница куска —
/// кривая на поверхности, и чтобы хранить её как кривую, нужен следующий шаг — рёбра-дуги,
/// заданные пересечением двух поверхностей. Пока же точны ВЕРШИНЫ разреза, и это уже снимает
/// главную часть погрешности: вершины перестают лежать на хордах.
/// </summary>
public static class QuadricSplit
{
    /// <summary>
    /// Отнести многоугольник к стороне квадрики. Вершины задаются целыми координатами с общим
    /// знаменателем — так их хранит ядро.
    /// </summary>
    public static QSide Classify(Quadric q, IReadOnlyList<(BigInteger X, BigInteger Y, BigInteger Z, BigInteger W)> poly)
    {
        bool anyIn = false, anyOut = false, anyOn = false;

        foreach (var v in poly)
        {
            int s = q.SideOf(v.X, v.Y, v.Z, v.W);
            if (s < 0) anyIn = true;
            else if (s > 0) anyOut = true;
            else anyOn = true;
        }

        if (anyIn && anyOut) return QSide.Crossing;
        if (anyIn) return QSide.Inside;
        if (anyOut) return QSide.Outside;
        return anyOn ? QSide.On : QSide.Outside;
    }

    /// <summary>
    /// Разрезать многоугольник квадрикой. Возвращает две части: внутреннюю и внешнюю. Вершины
    /// разреза — точные точки пересечения сторон с поверхностью.
    ///
    /// Сторона многоугольника может встретить квадрику ДВАЖДЫ (хорда, входящая и выходящая), и
    /// это учитывается: иначе выпуклый кусок, проходящий сквозь цилиндр, резался бы неверно.
    /// </summary>
    public static (List<QPoint> Inside, List<QPoint> Outside) Split(
        Quadric q, IReadOnlyList<(BigInteger X, BigInteger Y, BigInteger Z, BigInteger W)> poly)
    {
        var inside = new List<QPoint>();
        var outside = new List<QPoint>();

        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];

            int sa = q.SideOf(a.X, a.Y, a.Z, a.W);
            var pa = QPoint.Rational(a.X, a.Y, a.Z, a.W);

            if (sa <= 0) inside.Add(pa);
            if (sa >= 0) outside.Add(pa);

            // Точки пересечения стороны с поверхностью: направление берётся от a к b в общем
            // знаменателе, чтобы координаты оставались целыми.
            BigInteger dx = b.X * a.W - a.X * b.W;
            BigInteger dy = b.Y * a.W - a.Y * b.W;
            BigInteger dz = b.Z * a.W - a.Z * b.W;

            var hits = QuadricLine.Intersect(q, a.X, a.Y, a.Z, a.W, dx, dy, dz);

            foreach (var h in hits)
            {
                // Оставляем только те, что лежат ВНУТРИ стороны: параметр от нуля до единицы.
                if (!OnSegment(a, dx, dy, dz, h)) continue;
                inside.Add(h);
                outside.Add(h);
            }
        }

        return (inside, outside);
    }

    /// <summary>
    /// Лежит ли точка пересечения внутри стороны. Проверяется по той координате направления,
    /// которая не нулевая: сравнение точное, потому что идёт числами с корнем.
    /// </summary>
    private static bool OnSegment((BigInteger X, BigInteger Y, BigInteger Z, BigInteger W) a,
                                  BigInteger dx, BigInteger dy, BigInteger dz, QPoint h)
    {
        // Параметр t восстанавливается из той координаты, где направление ненулевое:
        // t = (h - a) / d. Сравниваем t с 0 и 1.
        SqrtRational coord;
        BigInteger a0, d0;

        if (!dx.IsZero) { coord = h.X; a0 = a.X; d0 = dx; }
        else if (!dy.IsZero) { coord = h.Y; a0 = a.Y; d0 = dy; }
        else if (!dz.IsZero) { coord = h.Z; a0 = a.Z; d0 = dz; }
        else return false;                                  // сторона нулевой длины

        // t = (coord − a0/a.W) · a.W / d0 — знаменатели целые, знак сохраняется.
        var num = new SqrtRational(coord.P * a.W - a0 * coord.S, coord.Q * a.W, coord.R, coord.S);

        int signT = num.Sign() * d0.Sign;
        if (signT < 0) return false;                        // t < 0

        // t ≤ 1 равносильно num ≤ d0/a.W, то есть сравнению с рациональным числом.
        var one = SqrtRational.FromRational(d0, a.W);
        int cmp = num.CompareTo(one);
        return d0.Sign > 0 ? cmp <= 0 : cmp >= 0;
    }
}
