using OpenTK.Mathematics;
using Parametric3D.Kernel;

namespace Parametric3D.Render;

/// <summary>
/// Адаптивная сетка в плоскости — по логике вашего исходного Grid.cs.
///
/// Главная идея, которую я перенёс: мелкие линии не исчезают скачком.
/// Шаг мелкой клетки — степень десяти, подобранная так, чтобы клетка
/// на экране была не мельче MinMinorPx. Экранный шаг всегда лежит внутри
/// одной декады, и прозрачность мелких линий равна log10 от отношения
/// текущего шага к нижней границе: 0 у нижней границы, 1 у верхней.
/// Поэтому прозрачность меняется при ЛЮБОМ движении колеса, а в момент
/// смены шага исчезающие линии уже полностью прозрачны — скачка нет.
///
/// Каждая десятая линия — крупная: рисуется отдельным стилем, толще
/// и всегда полностью непрозрачной.
///
/// Отличие от исходника: линии складываются в те же LineBatch, что и вся
/// остальная графика, а не в собственные VBO с SSBO цветов. Прозрачность
/// мелких линий задаётся не массивом цветов на линию, а множителем
/// непрозрачности на весь батч — это один uniform вместо перезаливки
/// буфера цветов каждый кадр.
/// </summary>
public sealed class GridRenderer : IDisposable
{
    private readonly LineBatch _minor = new();
    private readonly LineBatch _major = new();

    public Frame3 Plane = Frame3.XY;
    public bool ShowAxes = true;

    /// <summary>Сколько мелких шагов в крупной клетке. Каждая десятая линия — крупная.</summary>
    public int MinorStepsInCell = 10;

    /// <summary>
    /// Экранный шаг мелкой клетки, ниже которого сетка переключается
    /// на следующий десятичный шаг. К этому моменту мелкие линии уже
    /// полностью прозрачны, поэтому переключение не видно.
    /// </summary>
    public float MinMinorPx = Core.Settings.Current.GridMinCellPx;

    /// <summary>Запас за краями экрана, в долях от размера экрана.</summary>
    public float OffscreenMargin = 0.25f;

    /// <summary>Предел на число линий — страховка от вырожденных ракурсов.</summary>
    public int MaxLines = 4000;

    private double _majorStep = -1;
    private double _minorStep = 1;
    private Vector2d _min, _max;
    private float _minorAlpha = 1f;
    private float _minorPx;
    private bool _valid;

    /// <summary>Шаг мелкой клетки — им пользуется привязка к сетке.</summary>
    public double Step => _minorStep;
    /// <summary>Шаг крупной клетки.</summary>
    public double MajorStep => _majorStep;
    /// <summary>Текущая прозрачность мелких линий: 0 — растворились полностью.</summary>
    public float MinorAlpha => _minorAlpha;

    public void Update(Camera cam)
    {
        float pxPerUnit = Math.Max(1e-6f, cam.WorldToPx);

        // 1. Шаг МЕЛКОЙ клетки: наименьшая степень десяти, при которой
        //    клетка на экране не мельче MinMinorPx.
        double minor = FirstStepAtLeast(MinMinorPx / pxPerUnit);
        double minorPx = minor * pxPerUnit;

        // 2. Прозрачность мелких линий — ЛОГАРИФМИЧЕСКИ по всей декаде.
        //
        //    minorPx всегда лежит в [MinMinorPx, MinMinorPx*10): как только
        //    клетка сжимается до нижней границы, шаг переключается на
        //    следующий. Поэтому log10(minorPx / MinMinorPx) пробегает ровно
        //    от 0 до 1 за один цикл, и КАЖДОЕ движение колеса меняет
        //    прозрачность. У нижней границы мелкие линии исчезли полностью,
        //    у верхней — видны в полную силу; в момент переключения бывшие
        //    крупные линии становятся мелкими, уже непрозрачными, поэтому
        //    скачка нет.
        //
        //    Прошлые две попытки были линейными по пикселям и покрывали
        //    лишь кусок декады: большую часть диапазона прозрачность стояла
        //    на месте, и растворения не было видно.
        double t = Math.Log10(Math.Max(1e-6, minorPx / MinMinorPx));
        _minorAlpha = (float)Math.Clamp(t, 0.0, 1.0);
        _minorPx = (float)minorPx;

        // 3. Крупная линия — каждая десятая.
        double major = minor * MinorStepsInCell;

        // 4. Границы: экран разворачиваем в плоскость сетки.
        if (!ComputeExtent(cam, minor, out Vector2d lo, out Vector2d hi))
        {
            _valid = false;
            return;
        }

        bool same = _valid
                    && Math.Abs(minor - _minorStep) < minor * 1e-9
                    && (lo - _min).LengthSquared < 1e-12
                    && (hi - _max).LengthSquared < 1e-12;

        _majorStep = major;
        _minorStep = minor;
        _min = lo;
        _max = hi;
        _valid = true;

        if (!same) Rebuild();
    }

    /// <summary>Наименьший шаг вида 10^k, не меньший заданного.</summary>
    private double FirstStepAtLeast(double minWorld)
    {
        double s = 1e-4;
        int guard = 0;
        while (s < minWorld && guard++ < 40) s *= MinorStepsInCell;
        return s;
    }

    /// <summary>
    /// Прямоугольник сетки в координатах плоскости: разворачиваем четыре угла
    /// экрана в плоскость и берём их габарит. Углы, чей луч уходит от плоскости
    /// (взгляд почти вдоль неё), отбрасываются — иначе габарит уходит в бесконечность.
    /// </summary>
    private bool ComputeExtent(Camera cam, double step, out Vector2d lo, out Vector2d hi)
    {
        lo = hi = Vector2d.Zero;

        int w = cam.ViewportWidth, h = cam.ViewportHeight;
        int mx = (int)(w * OffscreenMargin), my = (int)(h * OffscreenMargin);

        var origin = new Vector3((float)Plane.Origin.X, (float)Plane.Origin.Y, (float)Plane.Origin.Z);
        var normal = new Vector3((float)Plane.W.X, (float)Plane.W.Y, (float)Plane.W.Z);

        Span<(int x, int y)> corners = stackalloc (int, int)[4]
        {
            (-mx, -my), (w + mx, -my), (w + mx, h + my), (-mx, h + my)
        };

        bool any = false;
        var min = new Vector2d(double.MaxValue, double.MaxValue);
        var max = new Vector2d(double.MinValue, double.MinValue);

        foreach (var (px, py) in corners)
        {
            cam.ScreenRay(px, py, out Vector3 ro, out Vector3 rd);
            float den = Vector3.Dot(rd, normal);
            if (MathF.Abs(den) < 1e-4f) continue;             // луч почти параллелен плоскости

            float tt = Vector3.Dot(origin - ro, normal) / den;
            if (tt <= 0 || tt > 1e7f) continue;               // плоскость позади камеры

            Vector3 p = ro + rd * tt;
            Vector2d q = Plane.ToLocal2(new Vector3d(p.X, p.Y, p.Z));
            min = Vector2d.ComponentMin(min, q);
            max = Vector2d.ComponentMax(max, q);
            any = true;
        }

        if (!any)
        {
            // Взгляд идёт вдоль плоскости: строим сетку вокруг точки взгляда.
            Vector3 t = cam.State.Target;
            Vector2d c = Plane.ToLocal2(new Vector3d(t.X, t.Y, t.Z));
            double r = cam.State.Distance;
            min = c - new Vector2d(r, r);
            max = c + new Vector2d(r, r);
        }

        // Ограничиваем число линий: при скользящем взгляде габарит может быть огромным.
        double maxSpan = step * MaxLines * 0.5;
        Vector2d size = max - min;
        if (size.X > maxSpan || size.Y > maxSpan)
        {
            Vector2d c = (min + max) * 0.5;
            double half = maxSpan * 0.5;
            min = c - new Vector2d(half, half);
            max = c + new Vector2d(half, half);
        }

        // Прижимаем к сетке, чтобы линии не «плыли» при панорамировании.
        lo = new Vector2d(Math.Floor(min.X / step) * step, Math.Floor(min.Y / step) * step);
        hi = new Vector2d(Math.Ceiling(max.X / step) * step, Math.Ceiling(max.Y / step) * step);
        return true;
    }

    private void Rebuild()
    {
        _minor.Begin();
        _major.Begin();

        double s = _minorStep;
        int i0 = (int)Math.Round(_min.X / s), i1 = (int)Math.Round(_max.X / s);
        int j0 = (int)Math.Round(_min.Y / s), j1 = (int)Math.Round(_max.Y / s);

        // Вертикальные линии
        for (int i = i0; i <= i1; i++)
        {
            double x = i * s;
            int mod = Mod(i, MinorStepsInCell);
            bool axis = ShowAxes && i == 0;
            bool major = mod == 0;

            Vector3 a = W(x, _min.Y), b = W(x, _max.Y);
            if (axis) _major.AddSegment(a, b, LineStyle.GridAxisY, 0);
            else if (major) _major.AddSegment(a, b, LineStyle.GridMajor, 0);
            else _minor.AddSegment(a, b, LineStyle.GridMinor, 0);
        }

        // Горизонтальные линии
        for (int j = j0; j <= j1; j++)
        {
            double y = j * s;
            int mod = Mod(j, MinorStepsInCell);
            bool axis = ShowAxes && j == 0;
            bool major = mod == 0;

            Vector3 a = W(_min.X, y), b = W(_max.X, y);
            if (axis) _major.AddSegment(a, b, LineStyle.GridAxisX, 0);
            else if (major) _major.AddSegment(a, b, LineStyle.GridMajor, 0);
            else _minor.AddSegment(a, b, LineStyle.GridMinor, 0);
        }

        _minor.End();
        _major.End();
    }

    private static int Mod(int a, int n)
    {
        int r = a % n;
        return r < 0 ? r + n : r;
    }

    private Vector3 W(double x, double y)
    {
        Vector3d p = Plane.ToWorld(x, y);
        return new Vector3((float)p.X, (float)p.Y, (float)p.Z);
    }

    /// <summary>Цвет мелкой линии при полностью «раскрытой» клетке.</summary>
    public Vector4 MinorColor => Core.Settings.Current.GridMinorColor;

    /// <summary>
    /// Цвет, к которому мелкие линии сходятся при растворении.
    /// Ставится видом равным цвету фона.
    /// </summary>
    public Vector4 FadeToColor = new(0.10f, 0.11f, 0.13f, 1f);

    public void Draw(RenderContext ctx)
    {
        if (!_valid) return;

        // Растворение вписывается в САМ ЦВЕТ, а не только в прозрачность.
        //
        // Прозрачность зависит от режима смешивания: если он где-то
        // сбился, альфа просто игнорируется, и линии выходят одинаково
        // яркими при любом масштабе — ровно то, что и наблюдалось.
        // Цвет же доходит до экрана всегда. Поэтому мелкая линия
        // не «гаснет по альфе», а физически перекрашивается в сторону
        // фона: при нулевом растворении она с фоном совпадает.
        Vector4 faded = Vector4.Lerp(FadeToColor, MinorColor, _minorAlpha);
        faded.W = MinorColor.W * _minorAlpha;

        Vector4 saved = ctx.Styles.GetLineColor(LineStyle.GridMinor);
        ctx.Styles.SetLineColor(LineStyle.GridMinor, faded);

        if (_minorAlpha > 0.01f) _minor.Draw(ctx);

        // Крупные линии и оси — всегда в полную силу.
        _major.Draw(ctx);

        ctx.Styles.SetLineColor(LineStyle.GridMinor, saved);
    }

    /// <summary>Строка для статусной панели: шаг, его экранный размер и растворение.</summary>
    public string Info =>
        $"Grid {_minorStep:0.####} / {_majorStep:0.####}   " +
        $"cell {_minorPx:0.0} px   fade {(int)Math.Round(_minorAlpha * 100)}%";

    public void Dispose()
    {
        _minor.Dispose();
        _major.Dispose();
    }
}
