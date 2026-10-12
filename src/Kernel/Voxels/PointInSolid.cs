using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Kernel.Voxels;

/// <summary>
/// ЛЕЖИТ ЛИ ТОЧКА ВНУТРИ ТЕЛА — ПО ТРЕУГОЛЬНИКАМ ОБОЛОЧКИ.
///
/// Луч из точки в случайную сторону пересекает замкнутую оболочку чётное
/// число раз снаружи и нечётное изнутри. Это не зависит ни от выпуклости,
/// ни от числа полостей, ни от того, как построено тело, — только от
/// замкнутости оболочки.
///
/// НАПРАВЛЕНИЕ СЛУЧАЙНО И ПЕРЕВЫБИРАЕТСЯ. Луч, прошедший точно по ребру
/// или через вершину, считается дважды или ни разу, и ответ переворачивается.
/// Такое попадание нельзя ни предсказать, ни исключить выбором «хорошего»
/// направления: у любого фиксированного направления найдётся тело, где оно
/// ляжет на ребро. Поэтому сомнительный луч отбрасывается целиком и берётся
/// новый — а не подправляется допуском.
///
/// Способ дорогой: каждый запрос перебирает все треугольники. Он и нужен
/// поштучно — решить, выпуклое ребро или вогнутое, — а не для заполнения
/// объёма, где ту же работу делает залив по ячейкам.
/// </summary>
public static class PointInSolid
{
    /// <summary>Сколько раз пробовать другое направление, прежде чем сдаться.</summary>
    private const int Attempts = 8;

    public static bool Test(MeshData mesh, Vector3d p) =>
        Test(Occupancy.Triangles(mesh), p);

    public static bool Test(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris, Vector3d p)
    {
        // Направления берутся из постоянного набора, а не из генератора:
        // один и тот же вопрос обязан давать один и тот же ответ, иначе
        // перестроение детали становится невоспроизводимым.
        var seed = new Random(20260919);

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            Vector3d dir = Direction(seed);
            int hits = 0;
            bool doubtful = false;

            foreach (var (a, b, c) in tris)
            {
                double t = Hit(p, dir, a, b, c, out bool onEdge);

                if (double.IsNaN(t)) continue;

                // ПОЗАДИ ЛУЧА — НЕ СЧИТАЕТСЯ И НЕ МЕШАЕТ.
                //
                // ИСПРАВЛЕНО, и это была главная беда всего способа.
                // Мёллер–Трумбор возвращает ЗНАКОВОЕ расстояние: у граней
                // позади точки оно отрицательно. Прежнее условие «t < 1e-9 —
                // сомнительно» считало сомнительными все такие пересечения,
                // а позади точки, лежащей внутри тела, грани есть ВСЕГДА.
                //
                // Поэтому каждая попытка объявлялась сомнительной, все восемь
                // направлений отбрасывались, и функция возвращала false —
                // то есть «снаружи» — для ЛЮБОЙ точки внутри тела.
                //
                // Отсюда и «вогнутая дуга» на выпуклой кромке. Проверка
                // выпуклости прямых рёбер при этом работала по случайности:
                // там спрашивают про точку СНАРУЖИ и ждут false, и она его
                // получала — но не потому, что точка снаружи.
                if (t < -1e-9) continue;

                // Попадание почти в точку старта: точка лежит на оболочке.
                // Ответ «внутри или снаружи» для неё смысла не имеет.
                //
                // Край треугольника учитывается только у попаданий ВПЕРЁД:
                // общее ребро двух треугольников луч засчитал бы дважды.
                if (t < 1e-9 || onEdge) { doubtful = true; break; }

                hits++;
            }

            if (!doubtful) return (hits & 1) == 1;
        }

        return false;
    }

    private static Vector3d Direction(Random r)
    {
        // Равномерно по сфере; нули и оси исключены — именно они чаще
        // всего ложатся на рёбра осевых тел.
        double z = r.NextDouble() * 1.6 - 0.8;
        double a = r.NextDouble() * Math.PI * 2;
        double s = Math.Sqrt(1 - z * z);

        return new Vector3d(s * Math.Cos(a), s * Math.Sin(a), z).Normalized();
    }

    /// <summary>
    /// Пересечение луча с треугольником (Мёллер–Трумбор). NaN — мимо.
    /// <paramref name="onEdge"/> — попадание пришлось на край треугольника,
    /// то есть на общее ребро с соседом: считать такое нельзя.
    /// </summary>
    private static double Hit(Vector3d o, Vector3d d, Vector3d a, Vector3d b, Vector3d c,
                              out bool onEdge)
    {
        const double Edge = 1e-9;

        onEdge = false;

        Vector3d e1 = b - a, e2 = c - a;
        Vector3d pv = Vector3d.Cross(d, e2);
        double det = Vector3d.Dot(e1, pv);

        if (Math.Abs(det) < 1e-14) return double.NaN;   // луч в плоскости треугольника

        double inv = 1.0 / det;
        Vector3d tv = o - a;

        double u = Vector3d.Dot(tv, pv) * inv;
        if (u < -Edge || u > 1 + Edge) return double.NaN;

        Vector3d qv = Vector3d.Cross(tv, e1);
        double v = Vector3d.Dot(d, qv) * inv;
        if (v < -Edge || u + v > 1 + Edge) return double.NaN;

        if (u < Edge || v < Edge || u + v > 1 - Edge) onEdge = true;

        return Vector3d.Dot(e2, qv) * inv;
    }
}
