using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// ВЕРШИНЫ СТАВЯТСЯ НА ПЕРЕСЕЧЕНИЕ ВСЕХ СМЕЖНЫХ ПОВЕРХНОСТЕЙ.
///
/// Резец гранится, поэтому вершина после булевой — это конец ХОРДЫ, а не точка настоящей
/// поверхности: измеренное расхождение по ста телам — серединное 0.0129 мм при наибольшем
/// 0.036. Тот же корень даёт и погрешность объёма (у выреза цилиндра 0.05%).
///
/// Мельчить гранение дорого: при допуске 0.005 сцена идёт вчетверо дольше. Здесь другой путь —
/// оставить гранение как есть, а вершины поставить туда, где сходятся точные поверхности их
/// граней.
///
/// ИЗМЕРЕНО, ЧТО ЭТО НЕ ПОМОГАЕТ, и вот почему это ценно знать. На выборке из ста тел
/// перестановка не сдвинула ни одной вершины: охват остался 90, серединное расхождение 0.01286
/// и наихудшее 0.036047 — ровно теми же. Значит, вершины УЖЕ лежат на точных поверхностях, а
/// расходится с ними ДУГА ребра.
///
/// Отсюда следует главное: при гранёном резце настоящее ребро — не дуга, а ломаная пересечения,
/// и представление его дугой неизбежно расходится на прогиб. Лечится это только негранёным
/// резцом (точные квадрики в булевой), а не подгонкой вершин или подбором дуги. Возможность
/// оставлена в ядре под переключателем P3D_SNAPV: она понадобится, когда резец перестанет
/// граниться.
///
/// ВАЖНО: проецировать на ОДНУ поверхность нельзя — прошлая попытка так и делала, и вершина
/// уходила с соседней плоскости (проверка «ребро на плоскости x = −50» перестала находить
/// ребро). Поэтому берутся ВСЕ смежные поверхности и вершина сводится к их общей точке
/// поочерёдными проекциями: каждая проекция возвращает точку на свою поверхность, и при
/// пересекающихся поверхностях последовательность сходится к их общей точке.
/// </summary>
public static class VertexSnap
{
    /// <summary>Сколько вершин переставлено и на сколько сдвинулась самая дальняя.</summary>
    public static int Moved;
    public static double WorstShift;

    /// <summary>
    /// Предел переноса: вершина не должна уезжать дальше, чем на прогиб гранения, иначе это уже
    /// не уточнение, а порча тела.
    /// </summary>
    public static double MaxShift => Math.Max(Tol.Chord * 2, 0.05);

    public static int Apply(TSolid solid, List<string>? warnings = null)
    {
        solid.BeginEdit("VertexSnap.Apply", EditKind.Derive);
        Moved = 0;
        WorstShift = 0;

        foreach (var v in solid.Vertices)
        {
            var surfaces = Adjacent(v);
            if (surfaces.Count < 2) continue;         // по одной поверхности точка не определена

            Vector3d start = v.P;
            Vector3d p = start;

            // Поочерёдные проекции: при пересекающихся поверхностях сходятся к общей точке.
            for (int pass = 0; pass < 24; pass++)
            {
                Vector3d before = p;
                foreach (var s in surfaces)
                {
                    Vector3d q = s.PointAt(s.ProjectToUV(p));
                    if (!Good(q)) { p = before; break; }
                    p = q;
                }

                if ((p - before).Length < 1e-9) break;
                if ((p - start).Length > MaxShift) { p = start; break; }
            }

            double shift = (p - start).Length;
            if (shift <= Tol.Weld || shift > MaxShift) continue;

            // Проверка по существу: новая точка обязана лежать на ВСЕХ поверхностях ближе, чем
            // старая. Иначе поочерёдные проекции сошлись не туда, и трогать вершину нельзя.
            if (Far(surfaces, p) >= Far(surfaces, start)) continue;

            v.P = p;
            Moved++;
            WorstShift = Math.Max(WorstShift, shift);
        }

        if (warnings != null && Moved > 0)
            warnings.Add($"Snap: {Moved} vertex(es) moved onto the exact surfaces, up to {WorstShift:0.#####} mm.");
        // K03: перенос вершин меняет и точное описание, и видимую сетку.
        if (Moved > 0) { solid.DisplayMeshKey = null; }

        return Moved;
    }

    /// <summary>Наибольшее расстояние от точки до поверхностей набора.</summary>
    private static double Far(List<ISurface3> surfaces, Vector3d p)
    {
        double worst = 0;
        foreach (var s in surfaces)
            worst = Math.Max(worst, (s.PointAt(s.ProjectToUV(p)) - p).Length);
        return worst;
    }

    /// <summary>Точные поверхности граней, сходящихся в вершине (без повторов по объекту).</summary>
    private static List<ISurface3> Adjacent(TVertex v)
    {
        var res = new List<ISurface3>();
        foreach (var e in v.Edges)
            foreach (var ce in e.CoEdges)
            {
                var f = ce.Loop?.Face;
                if (f == null) continue;
                var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
                if (src == null) continue;
                if (!res.Any(x => ReferenceEquals(x, src))) res.Add(src);
            }
        return res;
    }

    private static bool Good(Vector3d p) =>
        !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsNaN(p.Z) &&
        !double.IsInfinity(p.X) && !double.IsInfinity(p.Y) && !double.IsInfinity(p.Z);
}
