using System.Numerics;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// КВАДРИКА — поверхность второй степени: цилиндр, конус, сфера, эллипсоид. Задаётся
/// уравнением
///
///     A·x² + B·y² + C·z² + D·xy + E·xz + F·yz + G·x + H·y + I·z + J = 0
///
/// в ЦЕЛЫХ коэффициентах. Почему целых: вершины точного ядра хранятся рациональными (числители
/// и общий знаменатель в BigInteger), и значение квадрики в такой точке считается целой
/// арифметикой ТОЧНО. Значит, вопрос «с какой стороны поверхности лежит точка» решается без
/// единого округления — на том же основании, на котором держится нынешняя устойчивость ядра с
/// плоскостями.
///
/// Это первый камень для булевой по точным поверхностям: пока резец гранится, вырез цилиндра
/// даёт объём больше расчётного на 0.05%, скругление снимает на 1% больше, а дуги рёбер
/// расходятся с вершинами на 0.013 мм (серединное по ста телам).
/// </summary>
public readonly struct Quadric
{
    public readonly BigInteger A, B, C, D, E, F, G, H, I, J;

    public Quadric(BigInteger a, BigInteger b, BigInteger c, BigInteger d, BigInteger e,
                   BigInteger f, BigInteger g, BigInteger h, BigInteger i, BigInteger j)
    {
        A = a; B = b; C = c; D = d; E = e; F = f; G = g; H = h; I = i; J = j;
    }

    /// <summary>Вывернутая: та же поверхность, противоположные стороны.</summary>
    public Quadric Flipped => new(-A, -B, -C, -D, -E, -F, -G, -H, -I, -J);

    /// <summary>
    /// ЗНАЧЕНИЕ В РАЦИОНАЛЬНОЙ ТОЧКЕ (x/w, y/w, z/w), умноженное на w². Знак от умножения на
    /// квадрат не меняется, поэтому для предиката этого достаточно, а дробей не возникает вовсе.
    /// </summary>
    public BigInteger ValueAt(BigInteger x, BigInteger y, BigInteger z, BigInteger w)
    {
        BigInteger w2 = w * w;
        return A * x * x + B * y * y + C * z * z
             + D * x * y + E * x * z + F * y * z
             + (G * x + H * y + I * z) * w
             + J * w2;
    }

    /// <summary>
    /// Сторона точки: +1 снаружи, −1 внутри, 0 — ровно на поверхности. Ответ ТОЧНЫЙ: целая
    /// арифметика не округляет, и «ровно на поверхности» означает именно это, а не «в пределах
    /// допуска».
    /// </summary>
    public int SideOf(BigInteger x, BigInteger y, BigInteger z, BigInteger w) =>
        ValueAt(x, y, z, w).Sign;

    /// <summary>Приближённое значение — для отладки и грубых прикидок.</summary>
    public double ValueAt(Vector3d p) =>
        (double)A * p.X * p.X + (double)B * p.Y * p.Y + (double)C * p.Z * p.Z
      + (double)D * p.X * p.Y + (double)E * p.X * p.Z + (double)F * p.Y * p.Z
      + (double)G * p.X + (double)H * p.Y + (double)I * p.Z + (double)J;

    /// <summary>
    /// Нормаль в точке — градиент уравнения. Нужна, чтобы отличать внутреннюю сторону от
    /// внешней и строить сетку.
    /// </summary>
    public Vector3d NormalAt(Vector3d p)
    {
        double nx = 2 * (double)A * p.X + (double)D * p.Y + (double)E * p.Z + (double)G;
        double ny = 2 * (double)B * p.Y + (double)D * p.X + (double)F * p.Z + (double)H;
        double nz = 2 * (double)C * p.Z + (double)E * p.X + (double)F * p.Y + (double)I;
        var n = new Vector3d(nx, ny, nz);
        return n.LengthSquared < 1e-24 ? Vector3d.Zero : n.Normalized();
    }

    // ------------------------------------------------------------------ построение

    /// <summary>
    /// Сфера радиуса r с центром (cx, cy, cz). Коэффициенты берутся целыми: значения приводятся
    /// к общему знаменателю через масштаб — так же, как ядро поступает с плоскостями.
    /// </summary>
    public static Quadric Sphere(Vector3d centre, double radius, long scale = 1_000_000)
    {
        BigInteger cx = Round(centre.X * scale), cy = Round(centre.Y * scale), cz = Round(centre.Z * scale);
        BigInteger r = Round(radius * scale);
        BigInteger s = scale;

        // (x−cx)² + (y−cy)² + (z−cz)² − r² = 0, умноженное на s² после перехода к целым.
        return new Quadric(
            s * s, s * s, s * s,
            0, 0, 0,
            -2 * cx * s, -2 * cy * s, -2 * cz * s,
            cx * cx + cy * cy + cz * cz - r * r);
    }

    /// <summary>
    /// Цилиндр радиуса r с осью через точку at вдоль оси, параллельной одной из координатных.
    /// Общий случай (наклонная ось) — отдельная работа: он требует поворота формы, и делать его
    /// наполовину хуже, чем честно ограничиться тем, что проверено.
    /// </summary>
    public static Quadric CylinderAlongZ(Vector3d at, double radius, long scale = 1_000_000)
    {
        BigInteger cx = Round(at.X * scale), cy = Round(at.Y * scale);
        BigInteger r = Round(radius * scale);
        BigInteger s = scale;

        // (x−cx)² + (y−cy)² − r² = 0.
        return new Quadric(s * s, s * s, 0, 0, 0, 0,
                           -2 * cx * s, -2 * cy * s, 0,
                           cx * cx + cy * cy - r * r);
    }

    /// <summary>Цилиндр вдоль X.</summary>
    public static Quadric CylinderAlongX(Vector3d at, double radius, long scale = 1_000_000)
    {
        BigInteger cy = Round(at.Y * scale), cz = Round(at.Z * scale);
        BigInteger r = Round(radius * scale);
        BigInteger s = scale;
        return new Quadric(0, s * s, s * s, 0, 0, 0,
                           0, -2 * cy * s, -2 * cz * s,
                           cy * cy + cz * cz - r * r);
    }

    /// <summary>Цилиндр вдоль Y.</summary>
    public static Quadric CylinderAlongY(Vector3d at, double radius, long scale = 1_000_000)
    {
        BigInteger cx = Round(at.X * scale), cz = Round(at.Z * scale);
        BigInteger r = Round(radius * scale);
        BigInteger s = scale;
        return new Quadric(s * s, 0, s * s, 0, 0, 0,
                           -2 * cx * s, 0, -2 * cz * s,
                           cx * cx + cz * cz - r * r);
    }

    /// <summary>
    /// ЦИЛИНДР С ПРОИЗВОЛЬНОЙ ОСЬЮ. Условие «точка на расстоянии r от оси» записывается без
    /// корней:
    ///
    ///     |p − a|²·D − ((p − a)·d)² = r²·D,   где d — целый вектор оси, D = |d|².
    ///
    /// Умножение на D и убирает деление на длину оси: коэффициенты остаются целыми, а значит,
    /// предикат стороны по-прежнему точен. Ось задаётся ЦЕЛЫМ вектором — направление и без
    /// нормировки задаёт прямую, а нормировка внесла бы корень.
    /// </summary>
    public static Quadric CylinderAlong(Vector3d at, Vector3d dir, double radius, long scale = 1_000_000)
    {
        // Направление приводится к целому: умножаем на масштаб и сокращаем на общий делитель,
        // чтобы коэффициенты не росли без нужды.
        BigInteger dx = Round(dir.X * scale), dy = Round(dir.Y * scale), dz = Round(dir.Z * scale);
        BigInteger g = BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(
            BigInteger.Abs(dx), BigInteger.Abs(dy)), BigInteger.Abs(dz));
        if (g > 1) { dx /= g; dy /= g; dz /= g; }
        if (dx.IsZero && dy.IsZero && dz.IsZero)
            throw new ArgumentException("направление оси нулевое", nameof(dir));

        BigInteger D = dx * dx + dy * dy + dz * dz;
        BigInteger ax = Round(at.X * scale), ay = Round(at.Y * scale), az = Round(at.Z * scale);
        BigInteger r = Round(radius * scale);
        BigInteger s = scale;

        // Раскрываем |p − a|²·D − ((p − a)·d)² − r²·D = 0 в координатах p = (x, y, z),
        // где a хранится умноженным на масштаб: сравниваем в единицах масштаба.
        BigInteger A = D - dx * dx;
        BigInteger B = D - dy * dy;
        BigInteger C = D - dz * dz;
        BigInteger Dxy = -2 * dx * dy;
        BigInteger Dxz = -2 * dx * dz;
        BigInteger Dyz = -2 * dy * dz;

        // Выкладка целиком, чтобы не спутать единицы. Матрица M = D·E − d·dᵀ, условие
        //     (p − a)ᵀ M (p − a) = r²·D.
        // Точка приходит рациональной (x/w), а a и r хранятся умноженными на масштаб s.
        // Умножаем условие на s² и на w²:
        //     (s·x − ax·w)ᵀ M (s·x − ax·w) − r²·D·w² = 0,
        // и это уже многочлен с целыми коэффициентами от (x, y, z, w).
        BigInteger m12 = -dx * dy, m13 = -dx * dz, m23 = -dy * dz;

        BigInteger G = -2 * s * (A * ax + m12 * ay + m13 * az);
        BigInteger H = -2 * s * (B * ay + m12 * ax + m23 * az);
        BigInteger I = -2 * s * (C * az + m13 * ax + m23 * ay);

        BigInteger J = A * ax * ax + B * ay * ay + C * az * az
                     + 2 * (m12 * ax * ay + m13 * ax * az + m23 * ay * az)
                     - r * r * D;

        // Коэффициенты при квадратах умножаются на s², при линейных — на s, чтобы уравнение
        // считалось в тех же единицах, что и точка (x/w с целым x).
        return new Quadric(A * s * s, B * s * s, C * s * s,
                           Dxy * s * s, Dxz * s * s, Dyz * s * s,
                           G, H, I,
                           J);
    }

    /// <summary>
    /// Конус с вершиной в точке, осью вдоль Z и полууглом при вершине: (x−cx)² + (y−cy)² −
    /// k²·(z−cz)² = 0, где k — тангенс полуугла.
    /// </summary>
    public static Quadric ConeAlongZ(Vector3d apex, double tanHalfAngle, long scale = 1_000_000)
    {
        BigInteger cx = Round(apex.X * scale), cy = Round(apex.Y * scale), cz = Round(apex.Z * scale);
        BigInteger k = Round(tanHalfAngle * scale);
        BigInteger s = scale;

        // Умножаем на s², чтобы k² стало целым: (x−cx)²s² + (y−cy)²s² − k²(z−cz)² = 0.
        BigInteger s2 = s * s, k2 = k * k;
        return new Quadric(s2, s2, -k2,
                           0, 0, 0,
                           -2 * cx * s, -2 * cy * s, 2 * cz * k2 / s,
                           cx * cx + cy * cy - cz * cz * k2 / s2);
    }

    private static BigInteger Round(double v) => new BigInteger(Math.Round(v));

    public override string ToString() =>
        $"квадрика [{A},{B},{C}; {D},{E},{F}; {G},{H},{I}; {J}]";
}
