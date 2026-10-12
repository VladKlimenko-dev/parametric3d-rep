using System.Numerics;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// БУЛЕВА НАД ТОЧНЫМИ ТЕЛАМИ С КВАДРИКАМИ — без единой хорды.
///
/// Прежде вычитание цилиндра означало гранение его в 55-гранную призму, и отсюда все измеренные
/// погрешности. Здесь резец остаётся цилиндром: тело-результат описывается теми же
/// поверхностями, а вопрос «где точка» решается точно.
///
/// ОГРАНИЧЕНИЕ, ПОСТАВЛЕННОЕ СОЗНАТЕЛЬНО: тела считаются выпуклыми (пересечение полупространств).
/// Для них пересечение и вычитание выражаются набором поверхностей без разбиения пространства.
/// Тело общего вида требует дерева ветвления, и это следующий шаг; делать его наполовину хуже,
/// чем честно ограничиться проверяемым.
/// </summary>
public static class QBoolean
{
    /// <summary>
    /// Результат: тело, заданное списком поверхностей со сторонами, и способ ответить, где точка.
    /// Хранится не сеткой, а описанием — потому и точен.
    /// </summary>
    public sealed class Result
    {
        public readonly PlaneRegistry Planes;
        public readonly QuadricRegistry Quadrics;

        /// <summary>Пересечение полупространств: точка внутри, если внутри каждого.</summary>
        public readonly List<ExactSurfaceRef> Inside = new();

        /// <summary>Вычитаемые тела: точка снаружи результата, если внутри любого из них.</summary>
        public readonly List<List<ExactSurfaceRef>> Holes = new();

        private readonly QSolidTester _tester;

        public Result(PlaneRegistry planes, QuadricRegistry quadrics)
        {
            Planes = planes;
            Quadrics = quadrics;
            _tester = new QSolidTester(planes, quadrics);
        }

        /// <summary>Где точка: −1 внутри, 0 на поверхности, +1 снаружи. Ответ точен.</summary>
        public int Where(BigInteger x, BigInteger y, BigInteger z, BigInteger w)
        {
            bool onSurface = false;

            foreach (var s in Inside)
            {
                int side = _tester.Side(x, y, z, w, s);
                if (side > 0) return 1;
                if (side == 0) onSurface = true;
            }

            // Вырез: если точка строго внутри вычитаемого тела, её нет в результате.
            foreach (var hole in Holes)
            {
                bool allInside = true, touching = false;
                foreach (var s in hole)
                {
                    int side = _tester.Side(x, y, z, w, s);
                    if (side > 0) { allInside = false; break; }
                    if (side == 0) touching = true;
                }

                if (!allInside) continue;
                if (touching) { onSurface = true; continue; }   // на стенке выреза
                return 1;                                       // внутри выреза — вне тела
            }

            return onSurface ? 0 : -1;
        }

        public int Where(Vector3d p, long scale = 1_000_000)
        {
            BigInteger X = new(Math.Round(p.X * scale));
            BigInteger Y = new(Math.Round(p.Y * scale));
            BigInteger Z = new(Math.Round(p.Z * scale));
            return Where(X, Y, Z, new BigInteger(scale));
        }

        /// <summary>Где точка с иррациональными координатами.</summary>
        public int Where(QPoint p)
        {
            bool onSurface = false;

            foreach (var s in Inside)
            {
                int side = _tester.Side(p, s);
                if (side > 0) return 1;
                if (side == 0) onSurface = true;
            }

            foreach (var hole in Holes)
            {
                bool allInside = true, touching = false;
                foreach (var s in hole)
                {
                    int side = _tester.Side(p, s);
                    if (side > 0) { allInside = false; break; }
                    if (side == 0) touching = true;
                }

                if (!allInside) continue;
                if (touching) { onSurface = true; continue; }
                return 1;
            }

            return onSurface ? 0 : -1;
        }

        public bool HasQuadric =>
            Inside.Any(s => s.IsQuadric) || Holes.Any(h => h.Any(s => s.IsQuadric));
    }

    /// <summary>Тело как есть.</summary>
    public static Result Of(QSolid body)
    {
        var res = new Result(body.Planes, body.Quadrics);
        foreach (var f in body.Faces) res.Inside.Add(f.Support);
        return res;
    }

    /// <summary>
    /// ВЫЧИТАНИЕ: из первого тела убирается второе. Резец НЕ ГРАНИТСЯ — он остаётся набором
    /// своих поверхностей, и стенка выреза будет ровно цилиндром, а не ломаной.
    /// </summary>
    public static Result Subtract(QSolid body, QSolid tool)
    {
        var res = Of(body);
        res.Holes.Add(tool.Faces.Select(f => f.Support).ToList());
        return res;
    }

    /// <summary>
    /// ПЕРЕСЕЧЕНИЕ выпуклых тел: точка внутри, если внутри обоих, то есть поверхности просто
    /// складываются.
    /// </summary>
    public static Result Intersect(QSolid a, QSolid b)
    {
        var res = Of(a);
        foreach (var f in b.Faces) res.Inside.Add(f.Support);
        return res;
    }

    /// <summary>
    /// Объём по описанию — счётом точек. Точного объёма у тела с квадриками в общем виде нет,
    /// и честнее считать его численно, зная погрешность, чем выдавать приближение за точное.
    /// Возвращает объём и оценку его погрешности (по разбросу счёта).
    /// </summary>
    public static (double Volume, double Error) MeasureVolume(Result body, Vector3d lo, Vector3d hi,
                                                              int samples = 200_000, int seed = 12345)
    {
        var rnd = new Random(seed);
        Vector3d span = hi - lo;
        double boxVolume = span.X * span.Y * span.Z;

        int inside = 0;
        for (int i = 0; i < samples; i++)
        {
            var p = new Vector3d(lo.X + span.X * rnd.NextDouble(),
                                 lo.Y + span.Y * rnd.NextDouble(),
                                 lo.Z + span.Z * rnd.NextDouble());
            if (body.Where(p) < 0) inside++;
        }

        double fraction = inside / (double)samples;
        double volume = fraction * boxVolume;

        // Погрешность счёта точек: корень из доли, делённый на корень из числа проб.
        double error = boxVolume * Math.Sqrt(Math.Max(fraction * (1 - fraction), 1e-12) / samples) * 3;
        return (volume, error);
    }
}
