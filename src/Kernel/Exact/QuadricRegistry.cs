using System.Numerics;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Exact;

/// <summary>
/// Ссылка на квадрику в справочнике, со стороной. Устроена так же, как ссылка на плоскость:
/// одна и та же поверхность может ограничивать тело с любой стороны.
/// </summary>
public readonly record struct OQuadric(int Id, int Side)
{
    public OQuadric Flipped => new(Id, -Side);
}

/// <summary>
/// СПРАВОЧНИК КВАДРИК — рядом со справочником плоскостей.
///
/// Точное ядро хранит тела ПЛОСКОСТЯМИ: каждая грань — это плоскость из справочника, а вершина
/// — пересечение трёх плоскостей, с рациональными координатами. Отсюда и устойчивость: все
/// предикаты решаются целой арифметикой.
///
/// Из-за этого цилиндрический резец приходится гранить в 55-гранную призму, и тела с настоящими
/// цилиндрическими гранями в точное представление не укладываются вовсе — измерено: скругление
/// по кромке такого отверстия не строится, потому что у тела нет точного описания.
///
/// Здесь появляется второй справочник, для квадрик. Он устроен так же: одинаковые поверхности
/// сливаются в одну запись, ссылка несёт сторону, а предикат «где точка» точен — квадрика
/// хранится целыми коэффициентами, и подстановка рациональной точки не округляет.
/// </summary>
public sealed class QuadricRegistry
{
    private readonly record struct Key(BigInteger A, BigInteger B, BigInteger C,
                                       BigInteger D, BigInteger E, BigInteger F,
                                       BigInteger G, BigInteger H, BigInteger I, BigInteger J);

    private readonly List<Quadric> _all = new();
    private readonly Dictionary<Key, int> _byKey = new();

    public int Count => _all.Count;

    public Quadric this[int id] => _all[id];

    /// <summary>
    /// Добавить квадрику, слив с равной. Коэффициенты приводятся к общему виду делением на их
    /// общий делитель и выбором знака: иначе одна и та же поверхность, записанная с разными
    /// множителями, попала бы в справочник дважды, а тела перестали бы сходиться.
    /// </summary>
    public OQuadric Add(Quadric q)
    {
        var (norm, flipped) = Normalise(q);
        var key = KeyOf(norm);

        if (!_byKey.TryGetValue(key, out int id))
        {
            id = _all.Count;
            _all.Add(norm);
            _byKey[key] = id;
        }

        return new OQuadric(id, flipped ? -1 : 1);
    }

    /// <summary>Сторона рациональной точки: +1 снаружи, −1 внутри, 0 — на поверхности.</summary>
    public int Side(BigInteger x, BigInteger y, BigInteger z, BigInteger w, OQuadric q) =>
        q.Side * _all[q.Id].SideOf(x, y, z, w);

    /// <summary>Сторона точки с иррациональными координатами — тех, что даёт пересечение с квадрикой.</summary>
    public int Side(QPoint p, OQuadric q)
    {
        var quad = _all[q.Id];

        // Значение квадрики в такой точке — снова число с корнем, и его знак известен точно.
        // Раскрывать произведения координат нужно с общим знаменателем.
        var value = Evaluate(quad, p);
        return q.Side * value.Sign();
    }

    /// <summary>
    /// Значение квадрики в точке с корнем. Каждая координата имеет вид (p + q√r)/s с ОДНИМ
    /// подкоренным, поэтому произведения снова того же вида: (√r)² = r — рациональное.
    /// </summary>
    public static SqrtRational Evaluate(Quadric quad, QPoint pt)
    {
        SqrtRational Mul(SqrtRational a, SqrtRational b)
        {
            // (p₁ + q₁√r)(p₂ + q₂√r) = (p₁p₂ + q₁q₂r) + (p₁q₂ + q₁p₂)√r, знаменатели множатся.
            BigInteger r = a.R.IsZero ? b.R : a.R;
            BigInteger p = a.P * b.P + a.Q * b.Q * r;
            BigInteger q = a.P * b.Q + a.Q * b.P;
            return new SqrtRational(p, q, r, a.S * b.S);
        }

        SqrtRational Scale(SqrtRational a, BigInteger k) => new(a.P * k, a.Q * k, a.R, a.S);

        var terms = new List<SqrtRational>
        {
            Scale(Mul(pt.X, pt.X), quad.A),
            Scale(Mul(pt.Y, pt.Y), quad.B),
            Scale(Mul(pt.Z, pt.Z), quad.C),
            Scale(Mul(pt.X, pt.Y), quad.D),
            Scale(Mul(pt.X, pt.Z), quad.E),
            Scale(Mul(pt.Y, pt.Z), quad.F),
            Scale(pt.X, quad.G),
            Scale(pt.Y, quad.H),
            Scale(pt.Z, quad.I),
            SqrtRational.FromInteger(quad.J)
        };

        var sum = terms[0];
        for (int i = 1; i < terms.Count; i++) sum = sum.Plus(terms[i]);
        return sum;
    }

    /// <summary>
    /// Привести коэффициенты к общему виду: поделить на общий делитель и сделать первый
    /// ненулевой положительным. Возвращает приведённую квадрику и признак, что знак менялся.
    /// </summary>
    private static (Quadric Norm, bool Flipped) Normalise(Quadric q)
    {
        var c = new[] { q.A, q.B, q.C, q.D, q.E, q.F, q.G, q.H, q.I, q.J };

        BigInteger g = BigInteger.Zero;
        foreach (var v in c) g = BigInteger.GreatestCommonDivisor(g, BigInteger.Abs(v));
        if (g.IsZero) g = BigInteger.One;

        bool flip = false;
        foreach (var v in c)
            if (!v.IsZero) { flip = v.Sign < 0; break; }

        BigInteger k = flip ? -g : g;
        for (int i = 0; i < c.Length; i++) c[i] /= k;

        return (new Quadric(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9]), flip);
    }

    private static Key KeyOf(Quadric q) =>
        new(q.A, q.B, q.C, q.D, q.E, q.F, q.G, q.H, q.I, q.J);
}
