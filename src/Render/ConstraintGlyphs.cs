using Parametric3D.Model.Sketching;

namespace Parametric3D.Render;

/// <summary>
/// Значки ограничений эскиза — векторные, как и чертёжный шрифт.
///
/// Каждый значок задан ломаными в клетке 1×1, начало координат в левом
/// нижнем углу. Рисуются тем же пакетом линий, что и всё остальное:
/// отдельных текстур и шрифтов не требуется.
///
/// Знаки подобраны так, чтобы различаться с одного взгляда даже при
/// размере в десяток пикселей. Поэтому, например, «середина» — это не
/// перечёркнутая черта (её не отличить от перпендикулярности), а черта
/// с разрывом и точкой посередине.
/// </summary>
public static class ConstraintGlyphs
{
    private static double[] Circle(double cx, double cy, double r, int n = 12)
    {
        var pts = new double[(n + 1) * 2];
        for (int i = 0; i <= n; i++)
        {
            double a = i * 2 * Math.PI / n;
            pts[i * 2] = cx + r * Math.Cos(a);
            pts[i * 2 + 1] = cy + r * Math.Sin(a);
        }
        return pts;
    }

    private static readonly double[][] Equal =
    {
        new[] { 0.10, 0.35, 0.90, 0.35 },
        new[] { 0.10, 0.65, 0.90, 0.65 }
    };

    private static readonly double[][] Parallel =
    {
        new[] { 0.22, 0.12, 0.52, 0.88 },
        new[] { 0.52, 0.12, 0.82, 0.88 }
    };

    // Вертикальная черта начинается ВЫШЕ основания. Шейдер продлевает
    // концы отрезка на половину толщины плюс пиксель сглаживания, и черта,
    // начатая ровно на основании, выпирала под него.
    private static readonly double[][] Perpendicular =
    {
        new[] { 0.50, 0.24, 0.50, 0.92 },
        new[] { 0.10, 0.15, 0.90, 0.15 }
    };

    private static readonly double[][] Tangent =
    {
        Circle(0.40, 0.42, 0.30),
        new[] { 0.05, 0.72, 0.95, 0.72 }
    };

    private static readonly double[][] Coincident =
    {
        Circle(0.50, 0.50, 0.30),
        Circle(0.50, 0.50, 0.09, 8)
    };

    private static readonly double[][] MidPoint =
    {
        new[] { 0.08, 0.50, 0.36, 0.50 },
        new[] { 0.64, 0.50, 0.92, 0.50 },
        Circle(0.50, 0.50, 0.10, 8)
    };

    // Горизонтальность и вертикальность — просто черта в соответствующую
    // сторону. Засечки на концах только загромождали знак: направление
    // и так очевидно, а спутать его не с чем.
    private static readonly double[][] Horizontal =
    {
        new[] { 0.05, 0.50, 0.95, 0.50 }
    };

    private static readonly double[][] Vertical =
    {
        new[] { 0.50, 0.05, 0.50, 0.95 }
    };

    private static readonly double[][] Symmetric =
    {
        new[] { 0.50, 0.08, 0.50, 0.92 },
        new[] { 0.10, 0.50, 0.32, 0.35, 0.32, 0.65, 0.10, 0.50 },
        new[] { 0.90, 0.50, 0.68, 0.35, 0.68, 0.65, 0.90, 0.50 }
    };

    private static readonly double[][] Concentric =
    {
        Circle(0.50, 0.50, 0.42),
        Circle(0.50, 0.50, 0.18)
    };

    private static readonly double[][] None = Array.Empty<double[]>();

    /// <summary>
    /// Собственный масштаб знака. У совпадения он крупнее: две окружности
    /// на размере обычного знака сливаются в пятно.
    /// </summary>
    public static double Scale(ConstraintType t) => t switch
    {
        ConstraintType.Coincident => 1.5,
        ConstraintType.PointOnEntity => 1.5,
        _ => 1.0
    };

    public static double[][] Get(ConstraintType t) => t switch
    {
        ConstraintType.Equal => Equal,
        ConstraintType.Parallel => Parallel,
        ConstraintType.Perpendicular => Perpendicular,
        ConstraintType.Tangent => Tangent,
        // «Точка на объекте» ставится той же кнопкой Coincident и по смыслу
        // тоже совпадение, поэтому значок общий.
        ConstraintType.Coincident => Coincident,
        ConstraintType.PointOnEntity => Coincident,
        ConstraintType.MidPoint => MidPoint,
        ConstraintType.Horizontal => Horizontal,
        ConstraintType.Vertical => Vertical,
        ConstraintType.Symmetric => Symmetric,
        ConstraintType.Concentric => Concentric,
        _ => None
    };
}
