using System.Numerics;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// МОСТ МЕЖДУ РАСШИРЕННЫМ ЯДРОМ И ОБЫЧНЫМ ТЕЛОМ. Описание («внутри этих поверхностей, кроме этих
/// вырезов») точно, но показать его, сохранить и подать в следующую операцию нельзя: всё
/// остальное ядро работает с гранями, рёбрами и вершинами.
///
/// Здесь описание переводится в обычное тело. Перевод делается не заново: цилиндрический вырез
/// в теле из плоскостей уже умеет строить QuadricCut, и он проверен. Мост лишь читает описание и
/// говорит, что построить, — так проверенный путь получает нового хозяина, а не дублируется.
///
/// ГРАНИЦЫ ПРИМЕНИМОСТИ: один выпуклый кусок из плоскостей и вырезы-цилиндры вдоль осей. Это ровно
/// то, что покрыто проверками; всё прочее мост честно отклоняет, и тогда работает обычный путь.
/// </summary>
public static class QRegionBridge
{
    /// <summary>
    /// Построить обычное тело по описанию. Возвращает null, если случай за пределами
    /// применимости, — с указанием причины.
    /// </summary>
    public static TSolid? ToSolid(QRegion region, TSolid baseBody, List<string>? warnings = null)
    {
        if (region.Pieces.Count != 1)
        {
            warnings?.Add($"QRegionBridge: pieces {region.Pieces.Count}, only one is supported yet.");
            return null;
        }

        var piece = region.Pieces[0];
        if (piece.Holes.Count == 0)
        {
            warnings?.Add("QRegionBridge: nothing to cut.");
            return baseBody;
        }

        TSolid current = baseBody;

        foreach (var hole in piece.Holes)
        {
            var cyl = CylinderOf(region, hole);
            if (cyl == null)
            {
                warnings?.Add("QRegionBridge: a hole is not an axis-aligned cylinder.");
                return null;
            }

            var (at, axis, radius) = cyl.Value;
            var cut = QuadricCut.Cut(current, at, radius, warnings, axis);
            if (cut == null) return null;

            current = cut;
        }

        return current;
    }

    /// <summary>
    /// Разобрать вырез: цилиндр вдоль оси плюс два торца. Возвращает точку на оси, направление и
    /// радиус, иначе ничего.
    /// </summary>
    private static (Vector3d At, Vector3d Axis, double Radius)? CylinderOf(
        QRegion region, List<ExactSurfaceRef> hole)
    {
        OQuadric? found = null;
        foreach (var s in hole)
        {
            if (!s.IsQuadric) continue;
            if (found != null) return null;                 // больше одной квадрики — не наш случай
            found = s.Quadric;
        }
        if (found == null) return null;

        var q = region.Quadrics[found.Value.Id];

        // Цилиндр вдоль Z: A = B ≠ 0, C = 0, смешанных членов нет.
        // Уравнение имеет вид A(x² + y²) + Gx + Hy + J = 0, центр (−G/2A, −H/2A),
        // радиус² = (G² + H²)/(4A²) − J/A.
        if (q.A.IsZero || q.A != q.B || !q.D.IsZero || !q.E.IsZero || !q.F.IsZero) 
            return AlongOther(q);

        if (!q.C.IsZero || !q.I.IsZero) return null;

        double A = (double)q.A, G = (double)q.G, H = (double)q.H, J = (double)q.J;
        double cx = -G / (2 * A), cy = -H / (2 * A);
        double r2 = cx * cx + cy * cy - J / A;
        if (r2 <= 0) return null;

        return (new Vector3d(cx, cy, 0), Vector3d.UnitZ, Math.Sqrt(r2));
    }

    /// <summary>Тот же разбор для осей X и Y.</summary>
    private static (Vector3d At, Vector3d Axis, double Radius)? AlongOther(Quadric q)
    {
        if (!q.D.IsZero || !q.E.IsZero || !q.F.IsZero) return null;

        // Вдоль X: B = C ≠ 0, A = 0.
        if (q.A.IsZero && !q.B.IsZero && q.B == q.C && q.G.IsZero)
        {
            double B = (double)q.B, H = (double)q.H, I = (double)q.I, J = (double)q.J;
            double cy = -H / (2 * B), cz = -I / (2 * B);
            double r2 = cy * cy + cz * cz - J / B;
            return r2 <= 0 ? null : (new Vector3d(0, cy, cz), Vector3d.UnitX, Math.Sqrt(r2));
        }

        // Вдоль Y: A = C ≠ 0, B = 0.
        if (q.B.IsZero && !q.A.IsZero && q.A == q.C && q.H.IsZero)
        {
            double A = (double)q.A, G = (double)q.G, I = (double)q.I, J = (double)q.J;
            double cx = -G / (2 * A), cz = -I / (2 * A);
            double r2 = cx * cx + cz * cz - J / A;
            return r2 <= 0 ? null : (new Vector3d(cx, 0, cz), Vector3d.UnitY, Math.Sqrt(r2));
        }

        return null;
    }
}
