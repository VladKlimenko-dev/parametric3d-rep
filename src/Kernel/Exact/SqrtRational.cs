using System.Numerics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// ЧИСЛО ВИДА (p + q·√r)/s с целыми p, q, r, s. Именно такие числа появляются, когда прямая
/// пересекается с квадрикой: подстановка прямой в уравнение второй степени даёт квадратное
/// уравнение, а его корни — это корни из рационального числа.
///
/// Зачем отдельный вид числа. Нынешняя устойчивость ядра держится на том, что все координаты
/// рациональны и все предикаты решаются в целой арифметике без округления. Как только в булевой
/// появляются квадрики, координаты перестают быть рациональными — и если считать их в дробных
/// числах, теряется ровно то свойство, ради которого точное ядро и делалось: предикат «с какой
/// стороны» перестаёт быть надёжным у почти касательных поверхностей.
///
/// Здесь знак и сравнение считаются ТОЧНО: сравнение p/s с −q√r/s сводится к сравнению
/// квадратов, а квадраты — целые.
/// </summary>
public readonly struct SqrtRational
{
    /// <summary>Число равно (P + Q·√R)/S. Знаменатель S всегда положителен, R неотрицательно.</summary>
    public readonly BigInteger P, Q, R, S;

    public SqrtRational(BigInteger p, BigInteger q, BigInteger r, BigInteger s)
    {
        if (s.Sign == 0) throw new DivideByZeroException("знаменатель нуль");
        if (r.Sign < 0) throw new ArgumentOutOfRangeException(nameof(r), "под корнем отрицательное");

        // Знаменатель приводится к положительному, чтобы сравнения не зависели от его знака.
        if (s.Sign < 0) { p = -p; q = -q; s = -s; }

        // ПОДКОРЕННОЕ ПРИВОДИТСЯ К НАИМЕНЬШЕМУ ВИДУ: квадратные множители выносятся наружу,
        // √8 становится 2√2. Без этого одно и то же число в разных записях нельзя сравнить —
        // сравнение требует общего корня, а √8 и √2 формально разные.
        if (!q.IsZero && r > 1)
        {
            BigInteger f = LargestSquareFactor(r);
            if (f > 1)
            {
                q *= Isqrt(f);
                r /= f;
            }
            if (r.IsOne) { p += q; q = BigInteger.Zero; r = BigInteger.Zero; }
        }
        if (q.IsZero) r = BigInteger.Zero;

        P = p; Q = q; R = r; S = s;
    }

    public static SqrtRational FromRational(BigInteger num, BigInteger den) =>
        new(num, BigInteger.Zero, BigInteger.Zero, den);

    public static SqrtRational FromInteger(BigInteger v) => new(v, BigInteger.Zero, BigInteger.Zero, BigInteger.One);

    public bool IsRational => Q.IsZero || R.IsZero;

    /// <summary>Приближённое значение — только для показа и грубых прикидок, не для решений.</summary>
    public double Approx => ((double)P + (double)Q * Math.Sqrt((double)R)) / (double)S;

    /// <summary>
    /// ЗНАК ЧИСЛА, вычисленный точно. Сравнивается p с −q√r: обе части возводятся в квадрат, но
    /// только после того, как разобраны знаки — возведение в квадрат само по себе знак теряет.
    /// </summary>
    public int Sign()
    {
        if (Q.IsZero || R.IsZero) return P.Sign;      // просто рациональное
        if (P.IsZero) return Q.Sign;                  // q√r, знак по q

        if (P.Sign > 0 && Q.Sign > 0) return 1;       // оба слагаемых положительны
        if (P.Sign < 0 && Q.Sign < 0) return -1;

        // Знаки разные: сравниваем |p| с |q|√r через квадраты.
        BigInteger p2 = P * P;
        BigInteger q2r = Q * Q * R;

        int cmp = p2.CompareTo(q2r);
        if (cmp == 0) return 0;                       // равны по модулю — сумма ноль

        // Побеждает большее по модулю, его знак и есть знак суммы.
        return cmp > 0 ? P.Sign : Q.Sign;
    }

    /// <summary>Сравнение с другим таким числом: −1, 0 или +1. Требует одного и того же корня.</summary>
    public int CompareTo(SqrtRational other)
    {
        if (!R.Equals(other.R) && !IsRational && !other.IsRational)
            throw new InvalidOperationException(
                "сравнение чисел с разными корнями пока не поддержано: нужно приводить к общему корню");

        // Разность: (P·S₂ − P₂·S)/(S·S₂) + (Q·S₂ − Q₂·S)·√R/(S·S₂).
        BigInteger p = P * other.S - other.P * S;
        BigInteger q = Q * other.S - other.Q * S;
        BigInteger r = R.IsZero ? other.R : R;
        return new SqrtRational(p, q, r, S * other.S).Sign();
    }

    public SqrtRational Negated => new(-P, -Q, R, S);

    /// <summary>Сложение: возможно, пока корень общий (или одно из чисел рационально).</summary>
    public SqrtRational Plus(SqrtRational o)
    {
        if (!R.Equals(o.R) && !IsRational && !o.IsRational)
            throw new InvalidOperationException("сложение чисел с разными корнями пока не поддержано");
        BigInteger r = R.IsZero ? o.R : R;
        return new SqrtRational(P * o.S + o.P * S, Q * o.S + o.Q * S, r, S * o.S);
    }

    /// <summary>Умножение на рациональное.</summary>
    public SqrtRational Times(BigInteger num, BigInteger den) => new(P * num, Q * num, R, S * den);

    public override string ToString() =>
        Q.IsZero || R.IsZero ? $"{P}/{S}" : $"({P} + {Q}√{R})/{S}";

    // ------------------------------------------------------------------ корни уравнения

    /// <summary>
    /// КОРНИ КВАДРАТНОГО УРАВНЕНИЯ a·t² + b·t + c = 0 с целыми коэффициентами — то, что даёт
    /// пересечение прямой с квадрикой. Возвращает ноль, один или два корня в точном виде.
    ///
    /// Различитель хранится под корнем как есть: извлекать его нельзя, иначе всё построение
    /// теряет смысл.
    /// </summary>
    public static SqrtRational[] SolveQuadratic(BigInteger a, BigInteger b, BigInteger c)
    {
        if (a.IsZero)
        {
            if (b.IsZero) return Array.Empty<SqrtRational>();      // уравнение вырождено
            return new[] { FromRational(-c, b) };                  // прямая пересекает раз
        }

        BigInteger disc = b * b - 4 * a * c;
        if (disc.Sign < 0) return Array.Empty<SqrtRational>();     // пересечения нет

        if (disc.IsZero) return new[] { FromRational(-b, 2 * a) }; // касание

        // Если различитель — точный квадрат, корни рациональны, и это лучше знать: дальше с
        // ними работают как с обычными числами.
        BigInteger root = Isqrt(disc);
        if (root * root == disc)
            return new[]
            {
                FromRational(-b - root, 2 * a),
                FromRational(-b + root, 2 * a)
            };

        return new[]
        {
            new SqrtRational(-b, -1, disc, 2 * a),
            new SqrtRational(-b, 1, disc, 2 * a)
        };
    }

    /// <summary>
    /// Наибольший квадратный множитель числа — чтобы вынести его из-под корня. Перебор идёт по
    /// делителям до корня: подкоренные здесь невелики (различители квадратных уравнений), а
    /// разложение на множители в общем виде было бы дороже пользы.
    /// </summary>
    private static BigInteger LargestSquareFactor(BigInteger n)
    {
        if (n <= 1) return BigInteger.One;

        BigInteger best = BigInteger.One;
        BigInteger limit = Isqrt(n);

        // Ограничение на перебор: у больших чисел разложение не окупается, и тогда число
        // остаётся как есть — сравнение с ним просто потребует того же вида записи.
        if (limit > 1_000_000) return BigInteger.One;

        for (BigInteger d = 2; d <= limit; d++)
        {
            BigInteger sq = d * d;
            if (n % sq == 0) best = sq > best ? sq : best;
        }
        return best;
    }

    /// <summary>Целая часть квадратного корня — нужна, чтобы узнать, точный ли квадрат.</summary>
    public static BigInteger Isqrt(BigInteger n)
    {
        if (n.Sign <= 0) return BigInteger.Zero;
        if (n < 4) return BigInteger.One;

        BigInteger x = n, y = (x + 1) / 2;
        while (y < x) { x = y; y = (x + n / x) / 2; }
        return x;
    }
}
