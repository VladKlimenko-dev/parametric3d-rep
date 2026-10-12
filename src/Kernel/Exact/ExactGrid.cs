using System.Numerics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// СЕТКА ДОКУМЕНТА.
///
/// Все координаты, попадающие в точное ядро, округляются на эту сетку
/// ОДИН РАЗ, на входе. Дальше ни одного округления: плоскости получают
/// целые коэффициенты, решения принимаются знаком целого числа.
///
/// Шаг 0.00001 мм (10 нм) выбран по итогам этапа 2: размер на наклонной
/// грани уходит не больше чем на половину шага, то есть на 5 нм.
/// Допустимый диапазон — от 0.00001 до 0.000001 мм: при более мелкой
/// сетке нормали плоскостей перестают помещаться в 64 бита.
/// </summary>
public static class ExactGrid
{
    /// <summary>Единиц сетки в миллиметре — ЦЕЛОЕ число.</summary>
    ///
    /// Задаётся целым, а не как 1.0 / StepMm: в double это 99999.99999999999,
    /// и при переводе в целое получается 99999. Масштаб всех объёмов тогда
    /// уходит на три стотысячных, и точные дроби перестают совпадать.
    public const long UnitsPerMmInt = 100_000;

    /// <summary>Единиц сетки в миллиметре, для умножения в double.</summary>
    public const double UnitsPerMm = UnitsPerMmInt;

    /// <summary>Шаг сетки в миллиметрах.</summary>
    public const double StepMm = 1.0 / UnitsPerMmInt;

    /// <summary>Координата в миллиметрах — в целые единицы сетки.</summary>
    public static long Snap(double mm)
    {
        double u = Math.Round(mm * UnitsPerMm, MidpointRounding.AwayFromZero);

        // Габарит, не помещающийся в long, при шаге 10 нм — это 92 000 км.
        // Такое бывает только от испорченных данных, и молча обрезать их нельзя.
        if (double.IsNaN(u) || Math.Abs(u) > 4e18)
            throw new ArgumentOutOfRangeException(nameof(mm), mm, "coordinate is outside the exact kernel range");

        return (long)u;
    }

    /// <summary>Единицы сетки — в миллиметры. Только для вывода, не для решений.</summary>
    public static double ToMm(double units) => units / UnitsPerMm;
}

/// <summary>
/// ТОЧНАЯ ДРОБЬ. Нужна для измерений — объёма, площади, проверки замкнутости.
/// В решениях ядра не участвует: там достаточно знака целого числа.
/// </summary>
public readonly struct BigRational : IEquatable<BigRational>, IComparable<BigRational>
{
    public readonly BigInteger Num;
    public readonly BigInteger Den;

    public BigRational(BigInteger num, BigInteger den)
    {
        if (den.IsZero) throw new DivideByZeroException();

        if (den.Sign < 0) { num = -num; den = -den; }

        BigInteger g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(num), den);

        if (!g.IsZero && !g.IsOne) { num /= g; den /= g; }

        Num = num; Den = num.IsZero ? BigInteger.One : den;
    }

    public static readonly BigRational Zero = new(0, 1);

    public static implicit operator BigRational(long v) => new(v, 1);
    public static implicit operator BigRational(BigInteger v) => new(v, 1);

    public static BigRational operator +(BigRational a, BigRational b) => new(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    public static BigRational operator -(BigRational a, BigRational b) => new(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
    public static BigRational operator *(BigRational a, BigRational b) => new(a.Num * b.Num, a.Den * b.Den);
    public static BigRational operator /(BigRational a, BigRational b) => new(a.Num * b.Den, a.Den * b.Num);
    public static BigRational operator -(BigRational a) => new(-a.Num, a.Den);

    public static bool operator ==(BigRational a, BigRational b) => a.Equals(b);
    public static bool operator !=(BigRational a, BigRational b) => !a.Equals(b);
    public static bool operator <(BigRational a, BigRational b) => a.CompareTo(b) < 0;
    public static bool operator >(BigRational a, BigRational b) => a.CompareTo(b) > 0;

    public bool Equals(BigRational o) => Num == o.Num && Den == o.Den;
    public override bool Equals(object? obj) => obj is BigRational r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Num, Den);
    public int CompareTo(BigRational o) => (Num * o.Den).CompareTo(o.Num * Den);

    public bool IsZero => Num.IsZero;
    public int Sign => Num.Sign;

    /// <summary>
    /// В double — для вывода.
    ///
    /// ИСПРАВЛЕНО. Дробная часть считалась как (double)r / (double)Den.
    /// У объёма тела с тором знаменатель выходит за пределы double: оба
    /// перевода давали бесконечность, а их частное — NaN. Теперь дробная
    /// часть берётся целочисленно, с 64 двоичными знаками: r·2⁶⁴ / Den.
    /// </summary>
    public double ToDouble()
    {
        BigInteger q = BigInteger.DivRem(Num, Den, out BigInteger r);
        BigInteger frac = (r << 64) / Den;
        return (double)q + (double)frac / 18446744073709551616.0;
    }

    public override string ToString() => Den.IsOne ? Num.ToString() : $"{Num}/{Den}";
}
