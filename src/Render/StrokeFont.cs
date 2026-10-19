using OpenTK.Mathematics;

namespace Parametric3D.Render;

/// <summary>
/// Векторный штриховой шрифт: каждый символ — набор ломаных.
///
/// ПОЧЕМУ ТАК, А НЕ АТЛАС ГЛИФОВ. Движок уже умеет рисовать ломаные —
/// одним пакетом, с толщиной в пикселях и с попаданием мышью. Растровый
/// шрифт потребовал бы отдельной текстуры, своего шейдера, своего пакета
/// и разбирательств со сглаживанием и масштабом. Для чисел на размерах
/// этого не нужно: цифр десяток, чертёжный шрифт и так штриховой.
///
/// Символ описан в клетке шириной 0.6 и высотой 1.0, начало координат —
/// левый нижний угол, базовая линия совпадает с низом клетки.
/// </summary>
public static class StrokeFont
{
    /// <summary>Собственная ширина клетки знака, без настроек.</summary>
    private const double BaseAdvance = 0.68;
    /// <summary>Точка и запятая узкие.</summary>
    private const double BaseNarrowAdvance = 0.34;

    private static Core.Settings S => Core.Settings.Current;

    /// <summary>Ширина клетки знака с учётом настроек ширины и разрядки.</summary>
    public static double Advance => BaseAdvance * S.DimensionTextWidthScale * S.DimensionTextSpacing;
    public static double NarrowAdvance => BaseNarrowAdvance * S.DimensionTextWidthScale * S.DimensionTextSpacing;

    private static readonly Dictionary<char, double[][]> _glyphs = Build();

    /// <summary>Текст в окне вида — шрифтом ГОСТ тип А (Render.GostFont); P3D_STROKEFONT=1 — прежний штриховой.</summary>
    public static bool UseGost => !GostOff && GostFont.Available;
    private static readonly bool GostOff = Environment.GetEnvironmentVariable("P3D_STROKEFONT") == "1";

    public static double Width(string text)
    {
        if (UseGost)
        {
            double gw = 0;
            foreach (char c in text) gw += GostFont.Glyph(c) is { } g ? g.Advance * S.DimensionTextWidthScale * S.DimensionTextSpacing : Advance;
            return gw;
        }
        double w = 0;
        foreach (char c in text) w += c is '.' or ',' ? NarrowAdvance : Advance;
        return w;
    }

    /// <summary>
    /// Раскладывает строку в ломаные.
    /// </summary>
    /// <param name="origin">Левый край базовой линии.</param>
    /// <param name="dirX">Направление письма, единичное.</param>
    /// <param name="dirY">Направление вверх, единичное.</param>
    /// <param name="size">Высота прописного знака в тех же единицах, что и origin.</param>
    public static void Layout(string text, Vector2d origin, Vector2d dirX, Vector2d dirY,
                              double size, List<List<Vector2d>> dst)
    {
        double pen = 0;
        double wide = S.DimensionTextWidthScale;

        foreach (char c in text)
        {
            if (UseGost && GostFont.Glyph(c) is { } gg)
            {
                foreach (var ct in gg.Contours)
                {
                    var line = new List<Vector2d>(ct.Length);
                    foreach (var q in ct) line.Add(origin + dirX * ((pen + q.X * wide) * size) + dirY * (q.Y * size));
                    if (line.Count >= 2) dst.Add(line);
                }
                pen += gg.Advance * S.DimensionTextWidthScale * S.DimensionTextSpacing;
                continue;
            }
            if (_glyphs.TryGetValue(c, out var strokes))
            {
                foreach (var s in strokes)
                {
                    var line = new List<Vector2d>(s.Length / 2);
                    for (int i = 0; i + 1 < s.Length; i += 2)
                    {
                        // По ширине знак растягивается вместе с шагом,
                        // по высоте — нет: высота задана отдельно.
                        double gx = pen + s[i] * wide;
                        line.Add(origin + dirX * (gx * size) + dirY * (s[i + 1] * size));
                    }
                    if (line.Count >= 2) dst.Add(line);
                }
            }
            pen += c is '.' or ',' ? NarrowAdvance : Advance;
        }
    }

    /// <summary>
    /// Дуга как ломаная. Шаг мелкий: на крупных цифрах редкие звенья
    /// видны изломами, а на стыках звеньев торцы полос накладываются
    /// и дают утолщения — из-за них шрифт и выглядел неровным.
    /// </summary>
    private static double[] Arc(double cx, double cy, double rx, double ry,
                                double a0, double a1, int n = 20)
    {
        var pts = new double[(n + 1) * 2];
        for (int i = 0; i <= n; i++)
        {
            double a = a0 + (a1 - a0) * i / n;
            pts[i * 2] = cx + rx * Math.Cos(a);
            pts[i * 2 + 1] = cy + ry * Math.Sin(a);
        }
        return pts;
    }

    private static double[] Circle(double cx, double cy, double r, int n = 24)
        => Arc(cx, cy, r, r, 0, Math.PI * 2, n);

    private static double[] Join(params double[][] parts)
    {
        var list = new List<double>();
        foreach (var p in parts)
        {
            // Стык без повтора точки: иначе на нём оказываются два торца.
            int from = list.Count > 0 ? 2 : 0;
            for (int i = from; i < p.Length; i++) list.Add(p[i]);
        }
        return list.ToArray();
    }

    private const double PI = Math.PI;

    /// <summary>
    /// Начертания.
    ///
    /// Правила, из-за нарушения которых прежний набор читался плохо:
    ///   * каждый знак — как можно меньше ОТДЕЛЬНЫХ ломаных: на каждом
    ///     разрыве появляются два торца, и в месте стыка видно утолщение;
    ///   * дуги ведутся мелким шагом, иначе кривые кажутся тоньше прямых;
    ///   * ломаные не пересекают сами себя и не заходят одна за другую.
    ///
    /// Пропорции обычные для чертёжного шрифта: высота 1.0, ширина знака
    /// около 0.55, овалы слегка сужены.
    /// </summary>
    private static Dictionary<char, double[][]> Build()
    {
        const double L = 0.08, R = 0.50, M = 0.29;   // левый край, правый, середина
        const double T = 1.00, B = 0.00, C = 0.50;   // верх, низ, центр по высоте

        return new Dictionary<char, double[][]>
        {
            // Овал одной непрерывной ломаной.
            ['0'] = new[] { Arc(M, C, 0.21, 0.50, -PI / 2, PI * 1.5, 28) },

            ['1'] = new[]
            {
                new[] { 0.10, 0.78, M, T, M, B },
                new[] { 0.06, B, R, B }
            },

            // Верхняя дуга переходит в наклонную и в основание — одной линией.
            ['2'] = new[]
            {
                Join(Arc(M, 0.74, 0.21, 0.26, PI, -PI * 0.30, 14),
                     new[] { 0.47, 0.55, L, B, R, B })
            },

            ['3'] = new[]
            {
                Join(new[] { L, 0.92 },
                     Arc(0.28, 0.74, 0.22, 0.26, PI * 0.85, -PI * 0.5, 14),
                     Arc(0.27, 0.26, 0.23, 0.26, PI * 0.5, -PI * 0.85, 16))
            },

            ['4'] = new[]
            {
                new[] { 0.38, B, 0.38, T, L, 0.30, R, 0.30 }
            },

            ['5'] = new[]
            {
                Join(new[] { 0.47, T, 0.12, T, 0.09, 0.55 },
                     Arc(0.27, 0.28, 0.23, 0.28, PI * 0.5, -PI * 0.8, 16))
            },

            // Шестёрка, восьмёрка и девятка — из ЗАМКНУТЫХ овалов и дуг,
            // без соединительных отрезков. Соединять их одной ломаной
            // нельзя: прямая перемычка между концом дуги и началом овала
            // прочерчивается насквозь через знак.
            ['6'] = new[]
            {
                Arc(0.29, 0.26, 0.22, 0.26, 0, PI * 2, 22),
                Arc(0.29, 0.56, 0.22, 0.44, PI * 0.15, PI * 1.50, 20)
            },

            ['7'] = new[] { new[] { L, T, R, T, 0.20, B } },

            ['8'] = new[]
            {
                Arc(0.29, 0.745, 0.19, 0.245, 0, PI * 2, 22),
                Arc(0.29, 0.255, 0.22, 0.255, 0, PI * 2, 22)
            },

            // Девятка — та же шестёрка, повёрнутая на 180 градусов.
            ['9'] = new[]
            {
                Arc(0.29, 0.74, 0.22, 0.26, 0, PI * 2, 22),
                Arc(0.29, 0.44, 0.22, 0.44, PI * 1.15, PI * 2.50, 20)
            },

            ['.'] = new[] { Circle(0.16, 0.05, 0.05, 8) },
            [','] = new[] { Join(Circle(0.16, 0.05, 0.05, 8), new[] { 0.16, 0.05, 0.10, -0.14 }) },
            ['-'] = new[] { new[] { L, C, R, C } },

            ['('] = new[] { Arc(0.52, C, 0.34, 0.52, PI * 0.72, PI * 1.28, 14) },
            [')'] = new[] { Arc(0.06, C, 0.34, 0.52, -PI * 0.28, PI * 0.28, 14) },

            ['R'] = new[]
            {
                Join(new[] { L, B, L, T, 0.30, T },
                     Arc(0.30, 0.755, 0.20, 0.245, PI * 0.5, -PI * 0.5, 12),
                     new[] { L, 0.51 }),
                new[] { 0.28, 0.51, R, B }
            },

            ['x'] = new[]
            {
                new[] { 0.10, 0.10, 0.48, 0.62 },
                new[] { 0.48, 0.10, 0.10, 0.62 }
            },

            ['°'] = new[] { Circle(0.28, 0.82, 0.13, 14) },

            ['Ø'] = new[]
            {
                Arc(0.29, C, 0.23, 0.44, 0, PI * 2, 24),
                new[] { 0.04, 0.06, 0.54, 0.94 }
            },

            [' '] = Array.Empty<double[]>()
        };
    }
}
