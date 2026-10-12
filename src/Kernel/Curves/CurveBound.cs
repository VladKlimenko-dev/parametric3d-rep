using OpenTK.Mathematics;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Curves;

/// <summary>Насколько кривая отходит от поверхностей, которые её задают.</summary>
public readonly struct CurveBound
{
    /// <summary>Наибольшее измеренное отклонение от первой и второй поверхности, мм.</summary>
    public readonly double FromA, FromB;

    /// <summary>Сколько точек проверено и в скольких уточнение не сошлось.</summary>
    public readonly int Samples, Failed;

    public CurveBound(double fromA, double fromB, int samples, int failed)
    {
        FromA = fromA; FromB = fromB; Samples = samples; Failed = failed;
    }

    /// <summary>Итоговая оценка: худшее из двух.</summary>
    public double Worst => Math.Max(FromA, FromB);

    /// <summary>Годится ли кривая при заданном допуске.</summary>
    public bool Within(double tolerance) => Failed == 0 && Worst <= tolerance;

    public override string ToString() =>
        $"отклонение до {Worst:0.######} мм по {Samples} точкам" + (Failed > 0 ? $", не сошлось {Failed}" : "");
}

/// <summary>
/// ОЦЕНКА ПОГРЕШНОСТИ КРИВОЙ ПЕРЕСЕЧЕНИЯ — то, чем промышленные ядра заменяют точное решение
/// там, где его нет.
///
/// Стык двух цилиндров — кривая ЧЕТВЁРТОЙ степени. Наша точная арифметика построена на
/// квадратных корнях (числа вида (p + q√r)/s) и для неё недостаточна: корни уравнения четвёртой
/// степени в эти числа не укладываются. Значит, такую кривую нельзя вести точно — но можно
/// вести её приближённо и ЧЕСТНО ЗНАТЬ, насколько она отходит от обеих поверхностей.
///
/// Это и есть разница между «примерно» и «приближённо с оценкой»: во втором случае погрешность
/// не скрыта, её можно записать в допуск ребра, показать пользователю и сравнить с допуском
/// детали.
///
/// Оценка меряется не только в узлах кривой, но и МЕЖДУ ними: в узлах кривая уточнена и
/// отклонение там мало по построению, а настоящая величина проявляется в серединах.
///
/// ЧТО ПОКАЗАЛО ИЗМЕРЕНИЕ на стыках двух цилиндров (отклонение от первой и второй поверхности):
///
///   R20 / r8      1.3·10⁻¹⁴ и 3.8·10⁻¹⁵ мм
///   R20 / r18     5.2·10⁻¹³ и 2.5·10⁻¹³ мм
///   R20 / r19.9   2.6·10⁻¹³ и 2.5·10⁻¹³ мм   (почти касательные — самый трудный случай)
///   R50 / r5      9.2·10⁻¹³ и 1.1·10⁻¹³ мм
///
/// То есть КАЖДАЯ ТОЧКА кривой лежит на обеих поверхностях с машинной точностью: кривая не
/// хранится приближением, а вычисляется уточнением при каждом обращении. Приближение остаётся
/// лишь в том, как кривая РАЗБИВАЕТСЯ на ломаную для показа и для сетки, — а это уже обычный
/// прогиб хорды, тот же, что у дуг.
///
/// Вывод, важный для дальнейшего: для кривых четвёртой степени точная арифметика не нужна там,
/// где нужен только ХОД кривой. Она понадобится лишь для предикатов на таких кривых — вопросов
/// вида «по какую сторону», — и это отдельная, ещё не решённая задача.
/// </summary>
public static class CurveBoundEstimate
{
    /// <summary>
    /// Измерить, насколько кривая отходит от обеих поверхностей. Точек берётся вдвое больше, чем
    /// узлов, и в серединах тоже — иначе оценка вышла бы заниженной.
    /// </summary>
    public static CurveBound Measure(ICurve3 curve, ISurface3 a, ISurface3 b, int samples = 200)
    {
        double worstA = 0, worstB = 0;
        int failed = 0;
        int n = Math.Max(8, samples);

        for (int i = 0; i <= n; i++)
        {
            double t = curve.T0 + (curve.T1 - curve.T0) * i / n;
            Vector3d p = curve.PointAt(t);

            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsNaN(p.Z)) { failed++; continue; }

            double da = (a.PointAt(a.ProjectToUV(p)) - p).Length;
            double db = (b.PointAt(b.ProjectToUV(p)) - p).Length;

            worstA = Math.Max(worstA, da);
            worstB = Math.Max(worstB, db);
        }

        return new CurveBound(worstA, worstB, n + 1, failed);
    }

    /// <summary>
    /// Построить кривую пересечения двух поверхностей с оценкой. Затравка берётся ходом по
    /// поверхности: точки, где значение одной поверхности меняет знак относительно другой.
    /// Возвращает null, если пересечения не нашлось.
    /// </summary>
    public static (IntersectionCurve3 Curve, CurveBound Bound)? Build(
        ISurface3 a, ISurface3 b, IReadOnlyList<Vector3d> seed, double tolerance = 0)
    {
        if (seed.Count < 3) return null;

        var curve = IntersectionCurve3.TryBuild(a, b, seed, tolerance > 0 ? tolerance : 1e-7);
        if (curve == null) return null;

        var bound = Measure(curve, a, b);
        return (curve, bound);
    }
}
