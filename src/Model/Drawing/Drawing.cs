using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Drawing;

/// <summary>
/// Вид чертежа. Name — уникальное имя (front, top, left, right, bottom, back, iso; у разреза и выносного элемента — «А–А», «Б»).
/// Dir — направление взгляда (пусто — по имени). Разрез: Parent — вид, на котором стоит линия разреза, CutDepth — глубина
/// секущей плоскости по взгляду разреза (мм модели), Label — буква. Выносной элемент: Parent, центр и радиус круга на виде
/// Parent (мм модели), свой масштаб.
/// </summary>
public sealed class DrawingView
{
    public string Name { get; set; } = "front";
    public string Dir { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Масштаб вида; 0 — масштаб листа.</summary>
    public double Scale { get; set; }
    public bool ShowHidden { get; set; } = true;
    public bool ShowTangent { get; set; }
    /// <summary>Осевые линии у очерков тел вращения (оси отверстий и валов).</summary>
    public bool CenterLines { get; set; } = true;
    public bool IsSection { get; set; }
    public bool IsDetail { get; set; }
    public string? Parent { get; set; }
    public string Label { get; set; } = "";
    public double CutDepth { get; set; }
    public double DetailX { get; set; }
    public double DetailY { get; set; }
    public double DetailR { get; set; }
    /// <summary>
    /// k158: вспомогательный вид ГОСТ 2.305 п. 6 (взгляд перпендикулярно наклонной линии вида-родителя): Dir — своя рамка
    /// «v:…» (ViewFrame.Encode), на виде-родителе — стрелка с буквой Label, над видом — буква.
    /// </summary>
    public bool IsAux { get; set; }
    /// <summary>k158: вид с разрывом ГОСТ 2.305 п. 9.3: полоса [BreakA, BreakB] по X вида (мм модели) не показывается, правая часть
    /// придвигается (промежуток BreakGap мм листа), на краях — тонкие волнистые линии обрыва. Размеры — по модели (полная длина).</summary>
    public double BreakA { get; set; }
    public double BreakB { get; set; }
    public double BreakGap { get; set; } = 8;
    /// <summary>k159: поворот вида на листе (градусы, против часовой) вокруг центра вида.</summary>
    public double Angle { get; set; }
    /// <summary>
    /// k161: вид отображения (п. 9): "" — линии (невидимые — по ShowHidden), "hidden" — с невидимыми штриховыми, "wire" — каркас
    /// (все рёбра сплошные), "shaded" — тона без линий, "shadedlines" — тона и видимые линии, "material" — цвет материала и
    /// видимые линии.
    /// </summary>
    public string Style { get; set; } = "";
    [JsonIgnore] public bool HasBreak => BreakB > BreakA;
}

/// <summary>
/// k156 (D3): РАЗМЕР ЧЕРТЕЖА (ГОСТ 2.307). Kind: h — горизонтальный, v — вертикальный, al — параллельный отрезку, dia —
/// диаметр, rad — радиус. Линейный: точки (X1, Y1), (X2, Y2) в координатах вида (мм модели), Offset — отступ размерной линии от
/// дальней точки (мм листа, знак — сторона). Диаметр и радиус: центр (X1, Y1), радиус X2, угол полки Y2 (градусы).
/// Значение — по геометрии модели (не листа). Auto — из «Автоматических размеров», Model — из эскизов («Размеры модели»).
/// </summary>
public sealed class DrawingDim
{
    public string View { get; set; } = "front";
    public string Kind { get; set; } = "h";
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double Offset { get; set; } = 10;
    /// <summary>Текст вместо значения (пусто — значение).</summary>
    public string Text { get; set; } = "";
    /// <summary>Допуск или посадка: «±0,1», «H7», «+0,2/-0,1».</summary>
    public string Tol { get; set; } = "";
    /// <summary>Число одинаковых элементов («4 отв.»).</summary>
    public int Count { get; set; } = 1;
    public bool Auto { get; set; }
    public bool Model { get; set; }
    public bool Hidden { get; set; }
    /// <summary>k159: отступ размерной линии — от габарита вида (а не от точек): габаритные размеры и цепочки за контуром.</summary>
    public bool FromBox { get; set; }
    /// <summary>k159: место выбирается автоматически (ряды за контуром, угол выноски без наложения текстов); перенос мышью — false.</summary>
    public bool Arrange { get; set; }
    /// <summary>k161: сдвиг текста вдоль размерной линии от середины (мм листа); за концом — размерная линия продлевается под текст.</summary>
    public double TextShift { get; set; }
    /// <summary>k161: стрелки: 0 — по месту (снаружи, если не помещаются), 1 — внутри, 2 — снаружи. У диаметра большой
    /// окружности: 2 — выноска с полкой вместо линии через центр.</summary>
    public int Arrows { get; set; }

    [JsonIgnore] public double Value => Kind switch
    {
        "h" => Math.Abs(X2 - X1),
        "v" => Math.Abs(Y2 - Y1),
        "dia" => 2 * X2,
        "rad" => X2,
        _ => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1)),
    };

    /// <summary>Подпись: unit — мм в единице длины чертежа (1 — мм, 25.4 — дюйм), fmt — формат числа.</summary>
    public string Caption(bool comma, double unit = 1, string fmt = "0.##")
    {
        if (Text.Length > 0) return Text;
        string v = (Value / unit).ToString(fmt, CultureInfo.InvariantCulture);
        if (comma) v = v.Replace('.', ',');
        string pre = Kind == "dia" ? DrawingDocument.Dia : Kind == "rad" ? "R" : "";
        string cnt = Count > 1 ? $"{Count} отв. " : "";
        return cnt + pre + v + Tol;
    }
}

/// <summary>
/// k156 (D4): ОБОЗНАЧЕНИЕ ЧЕРТЕЖА. Kind: text — текст (технические требования), rough — шероховатость ГОСТ 2.309 (Value —
/// «Ra 3,2»; без цели — неуказанная шероховатость в правом верхнем углу «… (√)»), gdt — рамка допуска ГОСТ 2.308 / ISO 1101
/// (Symbol, Value, Datums), datum — знак базы (Text — буква), balloon — позиция ГОСТ 2.109 (Position), holes — таблица отверстий.
/// X, Y — место на листе (мм); цель (TX, TY) — точка на виде View (мм модели), к ней — выноска.
/// </summary>
public sealed class DrawingNote
{
    public string Kind { get; set; } = "text";
    public double X { get; set; }
    public double Y { get; set; }
    public string? View { get; set; }
    public bool HasTarget { get; set; }
    public double TX { get; set; }
    public double TY { get; set; }
    public string Text { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Value { get; set; } = "";
    public string Datums { get; set; } = "";
    public int Position { get; set; }
    public double Height { get; set; } = 3.5;
    public bool Auto { get; set; }
}

/// <summary>Проекции видов чертежа: линии, оси, дуги, фигуры сечения (координаты вида, мм модели) и номера деталей тел.</summary>
public sealed class DrawingScene
{
    public readonly Dictionary<string, List<ViewLine>> Lines = new();
    public readonly Dictionary<string, List<ViewAxis>> Axes = new();
    public readonly Dictionary<string, List<ViewArc>> Arcs = new();
    public readonly Dictionary<string, List<(Vector2d A, Vector2d B)>> SectionBoundary = new();
    /// <summary>Имя детали (позиции) каждого тела; пусто — одна деталь.</summary>
    public List<string> BodyItems = new();
    public readonly List<string> Problems = new();
    /// <summary>k161: тона граней вида (закраска): треугольники в координатах вида от дальних к ближним, яркость 0…1, тело.</summary>
    public readonly Dictionary<string, List<(Vector2d A, Vector2d B, Vector2d C, float Shade, int Body)>> Shades = new();
    /// <summary>k161: цвет материала каждого тела (R, G, B 0…1) — стиль «Материал».</summary>
    public List<(double R, double G, double B)> BodyColors = new();

    /// <summary>Разбор: видов спроецировано.</summary>
    public static long ViewsProjected;

    /// <summary>Виды документа: обычные и разрезы — из тел, выносные элементы — из линий вида-родителя внутри круга.</summary>
    public static DrawingScene Build(DrawingDocument d, IReadOnlyList<TSolid> bodies, IReadOnlyList<string>? items = null, IReadOnlyList<(double, double, double)>? colors = null)
    {
        var sc = new DrawingScene { BodyItems = items?.ToList() ?? new(), BodyColors = colors?.ToList() ?? new() };
        foreach (var v in d.Views.Where(v => !v.IsDetail)) sc.BuildView(d, v, bodies);
        sc.BuildDetails(d);
        return sc;
    }

    /// <summary>k161: пересчитать один вид (поворот изометрии, стиль): линии, оси, дуги, тона; выносные элементы — заново.</summary>
    public void Rebuild(DrawingDocument d, string view, IReadOnlyList<TSolid> bodies, double chord = 0.02)
    {
        var v = d.Views.FirstOrDefault(x => x.Name == view);
        if (v == null || v.IsDetail) return;
        BuildView(d, v, bodies, chord);
        BuildDetails(d);
    }

    void BuildView(DrawingDocument d, DrawingView v, IReadOnlyList<TSolid> bodies, double chord = 0.02)
    {
        var sc = this;
        var frame = ViewFrame.Of(d.DirOf(v));
        IReadOnlyList<TSolid> src = bodies;
        if (v.IsSection)
        {
            var (cut, boundary, problems) = SectionCut.Cut(bodies, frame, v.CutDepth);
            src = cut; sc.SectionBoundary[v.Name] = boundary; sc.Problems.AddRange(problems.Select(p => $"{v.Name}: {p}"));
        }
        sc.Lines[v.Name] = ViewProjection.ProjectMany(src, frame, chord);
        sc.Axes[v.Name] = src.SelectMany(b => ViewProjection.Axes(b, frame)).ToList();
        sc.Arcs[v.Name] = ViewProjection.Arcs(src, frame);
        if (v.Style is "shaded" or "shadedlines" or "material") sc.Shades[v.Name] = ShadeTriangles(src, frame);
        else sc.Shades.Remove(v.Name);
        Interlocked.Increment(ref ViewsProjected);
    }

    /// <summary>
    /// k161: закраска вида: треугольники сетки тел, обращённые к наблюдателю, тон по нормали (свет от наблюдателя, сверху
    /// справа), порядок — от дальних к ближним (рисуются по порядку — ближние закрывают дальние).
    /// </summary>
    static List<(Vector2d, Vector2d, Vector2d, float, int)> ShadeTriangles(IReadOnlyList<TSolid> bodies, ViewFrame f)
    {
        var res = new List<(Vector2d, Vector2d, Vector2d, float, int, double)>();
        var light = (f.Toward * 0.8 + f.Up * 0.45 + f.Right * 0.25).Normalized();
        for (int bi = 0; bi < bodies.Count; bi++)
        {
            var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(bodies[bi]);
            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                Vector3d a = (Vector3d)m.Vertices[m.Indices[t]].P, b = (Vector3d)m.Vertices[m.Indices[t + 1]].P, c = (Vector3d)m.Vertices[m.Indices[t + 2]].P;
                var n = Vector3d.Cross(b - a, c - a); double nl = n.Length; if (nl < 1e-12) continue; n /= nl;
                if (Vector3d.Dot(n, f.Toward) <= 0) continue;
                float sh = (float)(0.35 + 0.65 * Math.Max(0, Vector3d.Dot(n, light)));
                res.Add((f.Project(a), f.Project(b), f.Project(c), sh, bi, (f.Depth(a) + f.Depth(b) + f.Depth(c)) / 3));
            }
        }
        return res.OrderBy(x => x.Item6).Select(x => (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5)).ToList();
    }

    void BuildDetails(DrawingDocument d)
    {
        var sc = this;
        foreach (var v in d.Views.Where(v => v.IsDetail))
        {
            if (v.Parent == null || !sc.Lines.TryGetValue(v.Parent, out var pl)) { sc.Lines[v.Name] = new(); continue; }
            var c = new Vector2d(v.DetailX, v.DetailY); double r = v.DetailR;
            sc.Lines[v.Name] = pl.SelectMany(l => ClipToCircle(l, c, r)).ToList();
            sc.Axes[v.Name] = sc.Axes[v.Parent].Where(a => (a.Center - c).Length < r).ToList();
            sc.Arcs[v.Name] = sc.Arcs[v.Parent].Where(a => (a.Center - c).Length < r).ToList();
            if (sc.SectionBoundary.TryGetValue(v.Parent, out var sb)) sc.SectionBoundary[v.Name] = sb.SelectMany(s => ClipSegment(s.A, s.B, c, r)).ToList();
        }
    }

    /// <summary>Ломаная внутри круга: части отрезков, лежащие в круге (точки входа и выхода — пересечение с окружностью).</summary>
    public static IEnumerable<ViewLine> ClipToCircle(ViewLine l, Vector2d c, double r)
    {
        var cur = new List<Vector2d>();
        for (int i = 0; i + 1 < l.Points.Count; i++)
            foreach (var (a, b) in ClipSegment(l.Points[i], l.Points[i + 1], c, r))
            {
                if (cur.Count > 0 && (cur[^1] - a).Length > 1e-9) { if (cur.Count > 1) yield return l with { Points = cur }; cur = new(); }
                if (cur.Count == 0) cur.Add(a);
                cur.Add(b);
            }
        if (cur.Count > 1) yield return l with { Points = cur };
    }

    public static IEnumerable<(Vector2d A, Vector2d B)> ClipSegment(Vector2d a, Vector2d b, Vector2d c, double r)
    {
        var d = b - a; var f = a - c;
        double A = Vector2d.Dot(d, d), B = 2 * Vector2d.Dot(f, d), C = Vector2d.Dot(f, f) - r * r;
        if (A < 1e-24) { if (C <= 0) yield return (a, b); yield break; }
        double disc = B * B - 4 * A * C;
        if (disc <= 0) yield break;
        double sq = Math.Sqrt(disc);
        double t0 = Math.Max(0, (-B - sq) / (2 * A)), t1 = Math.Min(1, (-B + sq) / (2 * A));
        if (t1 - t0 > 1e-12) yield return (a + d * t0, a + d * t1);
    }
}

/// <summary>
/// k155–k156 (D2–D5): ЧЕРТЁЖ (.p3drw) — деталь или сборка из файла (или открытая), шаблон листа, виды, размеры, обозначения.
/// Масштаб листа — из ряда ГОСТ 2.302; раскладка видов по ГОСТ 2.305 (метод первого угла): главный вид, вид сверху — под ним,
/// вид слева — справа от него, изометрия — в свободном углу; разрезы, выносные элементы и прочие виды — колонкой справа.
/// Линии видов — проекция со скрытием (ViewProjection): видимый контур — сплошная толстая, невидимый — штриховая, линии
/// перехода — тонкая (по выбору). Размеры — ГОСТ 2.307, обозначения — ГОСТ 2.308, 2.309, 2.109.
/// </summary>
public sealed class DrawingDocument
{
    public int Version { get; set; } = 2;
    /// <summary>Файл детали (.p3d) или сборки (.p3a); пусто — открытый документ.</summary>
    public string? SourcePath { get; set; }
    public SheetTemplate Template { get; set; } = SheetLayout.Default("A3", true);
    /// <summary>Масштаб листа (1 — 1:1, 0.5 — 1:2, 2 — 2:1); 0 — подобрать из ряда ГОСТ 2.302.</summary>
    public double Scale { get; set; }
    public List<DrawingView> Views { get; set; } = new();
    public List<DrawingDim> Dims { get; set; } = new();
    public List<DrawingNote> Notes { get; set; } = new();
    public bool ShowDims { get; set; } = true;
    /// <summary>Слой допусков формы и расположения (рамки и базы) — вкл/выкл.</summary>
    public bool ShowGdt { get; set; } = true;
    /// <summary>Габариты видов на листе после Compose (мм листа) — для выбора и переноса вида мышью.</summary>
    [JsonIgnore] public Dictionary<string, (double X0, double Y0, double X1, double Y1)> Boxes { get; } = new();
    /// <summary>Преобразование вида «модель → лист» после Compose: середина проекции, масштаб, центр на листе.</summary>
    [JsonIgnore] public Dictionary<string, (Vector2d Mid, double S, double X, double Y)> Xf { get; } = new();

    /// <summary>
    /// k159: единица длины чертежа: пусто — как у модели (система единиц детали), иначе mm, cm, m, in, ft. Значения размеров
    /// пересчитываются из мм модели.
    /// </summary>
    public string Units { get; set; } = "";
    /// <summary>k161 (п. 4): толщина сплошной толстой основной линии S, мм (ГОСТ 2.303: 0.5…1.4; прежде 0.7).</summary>
    public double LineS { get; set; } = 0.5;
    /// <summary>Единица длины модели (из детали; задаёт окно чертежа).</summary>
    [JsonIgnore] public string ModelUnits { get; set; } = "mm";
    static readonly bool UnitsOff = Environment.GetEnvironmentVariable("P3D_DRAWUNITS") == "0";
    public string EffectiveUnits => UnitsOff ? "mm" : Units.Length > 0 ? Units : ModelUnits;
    public static double MmPer(string u) => u switch { "cm" => 10, "m" => 1000, "in" => 25.4, "ft" => 304.8, _ => 1 };
    static string UnitFormat(string u) => u switch { "cm" => "0.###", "m" => "0.####", "in" => "0.###", "ft" => "0.####", _ => "0.##" };
    /// <summary>Подпись размера в единицах чертежа.</summary>
    public string CaptionOf(DrawingDim d) => d.Caption(Comma, MmPer(EffectiveUnits), UnitFormat(EffectiveUnits));
    /// <summary>k159: положения и свойства выключенных основных видов — при включении вид встаёт туда же.</summary>
    public Dictionary<string, double[]> Parked { get; set; } = new();
    /// <summary>Масштаб листа последнего Compose (подобранный или заданный).</summary>
    [JsonIgnore] public double LastScale { get; private set; }
    /// <summary>
    /// k159: геометрия элементов на листе после Compose — отрезки (мм листа) размеров и обозначений: подсветка под курсором,
    /// выделение, перенос.
    /// </summary>
    [JsonIgnore] public Dictionary<object, List<(double X1, double Y1, double X2, double Y2)>> Hit { get; } = new(ReferenceEqualityComparer.Instance);
    [JsonIgnore] public Dictionary<object, List<Vector2d[]>> HitTexts { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Знак диаметра: в шрифте ГОСТ тип А (GOST_A.TTF) он на месте U+2205 (на месте «Ø» — буква «Ш»).</summary>
    public const string Dia = "\u2205";

    public string DirOf(DrawingView v) =>
        v.IsDetail && v.Parent != null && Views.FirstOrDefault(x => x.Name == v.Parent) is { } p ? DirOf(p) : string.IsNullOrEmpty(v.Dir) ? v.Name : v.Dir;

    /// <summary>Вид → лист.</summary>
    public (double X, double Y) ToSheet(string view, Vector2d p)
    {
        var (mid, s, x, y) = Xf[view];
        var v = Views.FirstOrDefault(q => q.Name == view);
        return MapF(x, y, v?.Angle ?? 0, mid, s, new Vector2d(Fold(v, p.X, s), p.Y));
    }
    /// <summary>Лист → вид.</summary>
    public Vector2d ToView(string view, double sx, double sy)
    {
        var (mid, s, x, y) = Xf[view];
        var v = Views.FirstOrDefault(q => q.Name == view);
        var l = Rot(new Vector2d(sx - x, sy - y), -(v?.Angle ?? 0));
        return new Vector2d(Unfold(v, mid.X + l.X / s, s), mid.Y + l.Y / s);
    }
    /// <summary>k159: точка вида (X уже с разрывом) → лист: масштаб, поворот вокруг центра вида, перенос.</summary>
    static (double X, double Y) MapF(double x, double y, double angle, Vector2d mid, double s, Vector2d p)
    {
        var r = Rot((p - mid) * s, angle);
        return (x + r.X, y + r.Y);
    }
    public static Vector2d Rot(Vector2d p, double deg)
    {
        if (deg == 0) return p;
        double a = deg * Math.PI / 180, c = Math.Cos(a), sn = Math.Sin(a);
        return new Vector2d(p.X * c - p.Y * sn, p.X * sn + p.Y * c);
    }
    /// <summary>Направление вида → направление на листе (поворот вида).</summary>
    public Vector2d DirToSheet(string view, Vector2d d) => Rot(d, Views.FirstOrDefault(q => q.Name == view)?.Angle ?? 0);

    /// <summary>k158: мутация «вид с разрывом»: P3D_VIEWBREAK=0 — разрыв не применяется (вид целиком).</summary>
    static readonly bool BreakOff = Environment.GetEnvironmentVariable("P3D_VIEWBREAK") == "0";
    /// <summary>X вида → X вида с разрывом (правая часть придвинута; точки внутри полосы — к её левому краю).</summary>
    public static double Fold(DrawingView? v, double x, double s)
    {
        if (v == null || !v.HasBreak || BreakOff) return x;
        if (x <= v.BreakA) return x;
        if (x >= v.BreakB) return x - (v.BreakB - v.BreakA) + v.BreakGap / s;
        return v.BreakA;
    }
    public static double Unfold(DrawingView? v, double x, double s)
    {
        if (v == null || !v.HasBreak || BreakOff) return x;
        if (x <= v.BreakA) return x;
        double g = v.BreakGap / s;
        if (x >= v.BreakA + g) return x + (v.BreakB - v.BreakA) - g;
        return x - v.BreakA < g / 2 ? v.BreakA : v.BreakB;
    }
    /// <summary>Ломаная без полосы разрыва (части слева и справа от неё).</summary>
    public static IEnumerable<List<Vector2d>> ClipBreak(List<Vector2d> pts, double a, double b)
    {
        var cur = new List<Vector2d>();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var p = pts[i]; var q = pts[i + 1];
            var parts = new List<(double T0, double T1)>();
            foreach (var (lo, hi) in new[] { (double.NegativeInfinity, a), (b, double.PositiveInfinity) })
            {
                // часть отрезка с lo <= x <= hi
                double t0 = 0, t1 = 1, dx = q.X - p.X;
                if (Math.Abs(dx) < 1e-15) { if (p.X < lo || p.X > hi) continue; }
                else
                {
                    double ta = (lo - p.X) / dx, tb = (hi - p.X) / dx;
                    if (ta > tb) (ta, tb) = (tb, ta);
                    t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
                    if (t1 - t0 < 1e-12) continue;
                }
                parts.Add((t0, t1));
            }
            foreach (var (t0, t1) in parts.OrderBy(x => x.T0))
            {
                var A = p + (q - p) * t0; var B = p + (q - p) * t1;
                if (cur.Count > 0 && (cur[^1] - A).Length > 1e-9) { if (cur.Count > 1) yield return cur; cur = new(); }
                if (cur.Count == 0) cur.Add(A);
                cur.Add(B);
            }
        }
        if (cur.Count > 1) yield return cur;
    }

    /// <summary>
    /// Перенос вида с проекционной связью ГОСТ 2.305: главный — вместе с проекционными (их смещение то же); вид сверху и снизу —
    /// только по вертикали; слева, справа и сзади — только по горизонтали; изометрия, разрезы и выносные — свободно.
    /// </summary>
    public void MoveView(string name, double dx, double dy)
    {
        var v = Views.FirstOrDefault(x => x.Name == name);
        if (v == null) return;
        // k161 (замечание 05.11, 01:23, п. 6): виды переносятся свободно (у повёрнутого вида проекционная связь теряет смысл);
        // выравнивание с другими видами — притяжением при переносе (SnapView). Мутация P3D_VIEWLINK=0 — прежняя связь.
        if (!LinkOff || v.IsSection || v.IsDetail || v.IsAux) { v.X += dx; v.Y += dy; return; }
        switch (name)
        {
            case "front":
                foreach (var x in Views.Where(x => !x.IsSection && !x.IsDetail && x.Name is "front" or "top" or "left" or "right" or "bottom" or "back")) { x.X += dx; x.Y += dy; }
                break;
            case "top" or "bottom": v.Y += dy; break;
            case "left" or "right" or "back": v.X += dx; break;
            default: v.X += dx; v.Y += dy; break;
        }
    }
    static readonly bool LinkOff = Environment.GetEnvironmentVariable("P3D_VIEWLINK") == "0";

    /// <summary>
    /// k161: притяжение переносимого вида: центр по X или Y ближе tol мм к центру другого вида — выравнивается (проекционная
    /// связь по выбору пользователя). Возвращает линии-подсказки (мм листа) для показа.
    /// </summary>
    public List<(double X1, double Y1, double X2, double Y2)> SnapView(string name, double tol = 3)
    {
        var res = new List<(double, double, double, double)>();
        var v = Views.FirstOrDefault(x => x.Name == name);
        if (v == null || !Boxes.ContainsKey(name)) return res;
        var others = Views.Where(x => x != v && Boxes.ContainsKey(x.Name)).ToList();
        var bx = others.Where(o => Math.Abs(o.X - v.X) < tol).OrderBy(o => Math.Abs(o.X - v.X)).FirstOrDefault();
        var by = others.Where(o => Math.Abs(o.Y - v.Y) < tol).OrderBy(o => Math.Abs(o.Y - v.Y)).FirstOrDefault();
        if (bx != null) { v.X = bx.X; res.Add((v.X, Math.Min(v.Y, bx.Y), v.X, Math.Max(v.Y, bx.Y))); }
        if (by != null) { v.Y = by.Y; res.Add((Math.Min(v.X, by.X), v.Y, Math.Max(v.X, by.X), v.Y)); }
        return res;
    }

    /// <summary>Вид под точкой листа (по габариту).</summary>
    public string? ViewAt(double x, double y) =>
        Boxes.Where(b => x >= b.Value.X0 - 2 && x <= b.Value.X1 + 2 && y >= b.Value.Y0 - 2 && y <= b.Value.Y1 + 2)
             .OrderBy(b => (b.Value.X1 - b.Value.X0) * (b.Value.Y1 - b.Value.Y0)).Select(b => b.Key).FirstOrDefault();

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, Opts);
    public static DrawingDocument FromJson(string json) => JsonSerializer.Deserialize<DrawingDocument>(json, Opts) ?? throw new InvalidDataException("not a drawing");

    /// <summary>Ряд масштабов ГОСТ 2.302 (уменьшения и увеличения).</summary>
    public static readonly double[] GostScales = { 50, 40, 20, 10, 5, 4, 2.5, 2, 1, 0.5, 0.4, 0.25, 0.2, 0.1, 0.05, 0.04, 0.025, 0.02, 0.01 };
    public static string ScaleText(double s) => s >= 1 ? $"{s.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))}:1" : $"1:{(1 / s).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"))}";

    /// <summary>Оси тел вращения на виде.</summary>
    public static List<ViewAxis> AxesOf(IEnumerable<TSolid> bodies, string view) => bodies.SelectMany(b => ViewProjection.Axes(b, ViewFrame.Of(view))).ToList();

    /// <summary>Проекции тел на вид (мм модели) — общая сцена скрытия, масштаб не влияет.</summary>
    public static List<ViewLine> ProjectBodies(IEnumerable<TSolid> bodies, string view, double chord = 0.02) =>
        ViewProjection.ProjectMany(bodies.ToList(), ViewFrame.Of(view), chord);

    static (Vector2d Min, Vector2d Max) Box(List<ViewLine> lines)
    {
        var mn = new Vector2d(double.MaxValue); var mx = new Vector2d(double.MinValue);
        foreach (var l in lines) foreach (var p in l.Points) { mn = Vector2d.ComponentMin(mn, p); mx = Vector2d.ComponentMax(mx, p); }
        if (mn.X > mx.X) { mn = Vector2d.Zero; mx = Vector2d.Zero; }
        return (mn, mx);
    }

    /// <summary>Стандартный набор видов, если их нет: главный, сверху, слева, изометрия.</summary>
    public void EnsureViews()
    {
        if (Views.Count > 0) return;
        // аксонометрия — без невидимых линий (ГОСТ 2.317: невидимый контур на аксонометрии не показывают)
        foreach (var n in new[] { "front", "top", "left", "iso" }) Views.Add(new DrawingView { Name = n, ShowHidden = n != "iso" });
    }

    /// <summary>Следующая свободная буква обозначения (ГОСТ 2.316: кириллица без Ё, З, Й, О, Х, Ъ, Ы, Ь).</summary>
    public string NextLetter()
    {
        const string letters = "АБВГДЕЖИКЛМНПРСТУФЦЧШЩЭЮЯ";
        var used = Views.Select(v => v.Label).Concat(Notes.Where(n => n.Kind == "datum").Select(n => n.Text)).ToHashSet();
        foreach (char c in letters) if (!used.Contains(c.ToString())) return c.ToString();
        return "А1";
    }

    static readonly string[] Grid = { "front", "top", "left", "iso" };
    static readonly bool ArrangeOffV = Environment.GetEnvironmentVariable("P3D_VIEWARRANGE") == "0";

    /// <summary>Габарит для раскладки: у вида с разрывом — без полосы (промежуток не учитывается).</summary>
    static (Vector2d, Vector2d) BreakBox(DrawingView v, (Vector2d Min, Vector2d Max) b)
    {
        if (!v.HasBreak || BreakOff || v.IsDetail) return b;
        double cut = Math.Max(0, Math.Min(b.Max.X, v.BreakB) - Math.Max(b.Min.X, v.BreakA));
        return (b.Min, new Vector2d(b.Max.X - cut, b.Max.Y));
    }

    /// <summary>
    /// Раскладка: масштаб листа (если 0 — наибольший из ряда, при котором виды с промежутками 20 мм помещаются в рамку над
    /// основной надписью) и центры видов по ГОСТ 2.305 (у видов с X = Y = 0). Свой масштаб вида учитывается. Прочие виды
    /// (справа, снизу, сзади, разрезы, выносные) — колонкой справа от сетки. Возвращает выбранный масштаб.
    /// </summary>
    public double Layout(Dictionary<string, List<ViewLine>> proj)
    {
        var (x0, y0, x1, y1) = SheetLayout.FrameRect(Template);
        double tbH = SheetLayout.TitleHeight(Template.TitleBlock);
        var box = Views.ToDictionary(v => v.Name, v => v.IsDetail ? (new Vector2d(v.DetailX - v.DetailR, v.DetailY - v.DetailR), new Vector2d(v.DetailX + v.DetailR, v.DetailY + v.DetailR))
                                                     : proj.TryGetValue(v.Name, out var l) ? BreakBox(v, Box(l)) : (Vector2d.Zero, Vector2d.Zero));
        DrawingView? V(string n) => Views.FirstOrDefault(v => v.Name == n);
        // размер вида на листе при масштабе листа s (свой масштаб вида — постоянен)
        Vector2d Size(string n, double s)
        {
            if (V(n) is not { } v) return Vector2d.Zero;
            var e = (box[n].Item2 - box[n].Item1) * (v.Scale > 0 ? v.Scale : s);
            if (v.Angle == 0) return e;
            // k159: повёрнутый вид — габарит повёрнутого прямоугольника
            double a = v.Angle * Math.PI / 180, c = Math.Abs(Math.Cos(a)), sn = Math.Abs(Math.Sin(a));
            return new Vector2d(e.X * c + e.Y * sn, e.X * sn + e.Y * c);
        }
        var extras = Views.Where(v => !Grid.Contains(v.Name)).ToList();
        const double gap = 20;
        double availW = x1 - x0 - 2 * gap, availH = y1 - y0 - tbH - 2 * gap;
        // k161 (замечание 05.11, 01:23, п. 2): два варианта сетки — «вид слева — слева от главного, вид сверху — под главным,
        // изометрия — слева внизу» (просьба пользователя) и ГОСТ 2.305 (вид слева — справа, изометрия — справа внизу); берётся
        // тот, при котором масштаб листа крупнее (равный — первый). Мутация P3D_VIEWARRANGE=0 — только ГОСТ (прежнее).
        string[][] arrA = { new[] { "left", "iso" }, new[] { "front", "top" } }, arrB = { new[] { "front", "top" }, new[] { "left", "iso" } };
        (double wl, double wr, double ht, double hb, double we, double he) Dims(double s, string[][] arr)
        {
            double wl = arr[0].Max(n => Size(n, s).X), wr = arr[1].Max(n => Size(n, s).X);
            double ht = Math.Max(Size("front", s).Y, Size("left", s).Y), hb = Math.Max(Size("top", s).Y, Size("iso", s).Y);
            double we = extras.Count == 0 ? 0 : extras.Max(v => Size(v.Name, s).X) + 10;   // +10 — подписи
            double he = extras.Sum(v => Size(v.Name, s).Y + 12) + Math.Max(0, extras.Count - 1) * 8;
            return (wl, wr, ht, hb, we, he);
        }
        double Fit(double s, string[][] arr)
        {
            var (wl, wr, ht, hb, we, he) = Dims(s, arr);
            double w = wl + (wl > 0 && wr > 0 ? gap : 0) + wr + (we > 0 ? gap + we : 0), h = Math.Max(ht + (hb > 0 ? gap + hb : 0), he);
            return Math.Max(w / availW, h / availH);
        }
        double Pick(string[][] arr) { foreach (var s in GostScales) if (Fit(s, arr) <= 1) return s; return GostScales.Last(); }
        var arrangement = ArrangeOffV ? arrB : Pick(arrA) >= Pick(arrB) ? arrA : arrB;
        double scale = Scale;
        if (scale <= 0) scale = Pick(arrangement);
        var (wl2, wr2, ht2, hb2, we2, he2) = Dims(scale, arrangement);
        double g01 = wl2 > 0 && wr2 > 0 ? gap : 0;
        double totalW = wl2 + g01 + wr2 + (we2 > 0 ? gap + we2 : 0), totalH = Math.Max(ht2 + (hb2 > 0 ? gap + hb2 : 0), he2);
        double left = x0 + gap + Math.Max(0, (availW - totalW) / 2), top = y1 - gap - Math.Max(0, (availH - totalH) / 2);
        var centre = new Dictionary<string, (double X, double Y)>();
        for (int col = 0; col < 2; col++)
            for (int row = 0; row < 2; row++)
                centre[arrangement[col][row]] = (col == 0 ? left + wl2 / 2 : left + wl2 + g01 + wr2 / 2, row == 0 ? top - ht2 / 2 : top - ht2 - gap - hb2 / 2);
        var (wl2b, wr2b) = (wl2, wr2 + g01);
        double ex = left + wl2b + wr2b + gap + we2 / 2, ey = top;
        // k161: виды уже стоят (пользователь мог их перенести, сетка прежняя) — новый прочий вид ставится справа от стоящих
        // видов сетки и ниже стоящих прочих (прежде — по сетке заново: при новом виде сетка сдвигалась, и он ложился на главный)
        var placedGrid = Views.Where(v => Grid.Contains(v.Name) && (v.X != 0 || v.Y != 0)).ToList();
        if (placedGrid.Count > 0)
        {
            ex = placedGrid.Max(v => v.X + Size(v.Name, scale).X / 2) + gap + we2 / 2;
            ey = placedGrid.Max(v => v.Y + Size(v.Name, scale).Y / 2);
            var placedExtra = extras.Where(v => v.X != 0 || v.Y != 0).ToList();
            if (placedExtra.Count > 0) ey = placedExtra.Min(v => v.Y - Size(v.Name, scale).Y / 2) - 8;
        }
        foreach (var v in extras.Where(v => placedGrid.Count == 0 || v.X == 0 && v.Y == 0))
        {
            var sz = Size(v.Name, scale);
            ey -= 12;   // подпись над видом
            centre[v.Name] = (ex, ey - sz.Y / 2);
            ey -= sz.Y + 8;
        }
        // k159: вид без линий (модель ещё не пришла) места не получает — иначе раскладка по пустым видам закреплялась бы
        foreach (var v in Views)
            if (v.X == 0 && v.Y == 0 && centre.TryGetValue(v.Name, out var c) && (v.IsDetail || proj.TryGetValue(v.Name, out var pl) && pl.Count > 0)) { v.X = c.X; v.Y = c.Y; }
        return scale;
    }

    /// <summary>Совместимость (k155): лист по линиям и осям без дуг и сечений.</summary>
    public SheetGraphics Compose(Dictionary<string, List<ViewLine>> proj, FieldSource fields, Dictionary<string, List<ViewAxis>>? axes = null)
    {
        var sc = new DrawingScene();
        foreach (var (k, v) in proj) sc.Lines[k] = v;
        if (axes != null) foreach (var (k, v) in axes) sc.Axes[k] = v;
        return Compose(sc, fields);
    }

    bool Comma => Template.Standard == SheetStandard.Eskd;

    /// <summary>
    /// Лист чертежа: шаблон (надпись: масштаб листа, поля детали), линии видов, штриховка разрезов, линии разреза и круги
    /// выносных элементов на видах-родителях, подписи видов, размеры, обозначения.
    /// </summary>
    public SheetGraphics Compose(DrawingScene scene, FieldSource fields)
    {
        Boxes.Clear(); Xf.Clear();
        var proj = scene.Lines;
        double scale = Layout(proj);
        LastScale = scale; Hit.Clear(); HitTexts.Clear();
        fields.Auto[Fields.Scale] = ScaleText(scale);
        var g = SheetLayout.Compose(Template, fields);
        if (!LineSOff) g.ThickW = Math.Clamp(LineS, 0.3, 1.4);
        foreach (var v in Views)
        {
            if (!proj.TryGetValue(v.Name, out var lines)) continue;
            double s = v.Scale > 0 ? v.Scale : scale;
            var (mn, mx) = Box(lines);
            Vector2d mid;
            // выносной элемент — центр круга в середине вида
            if (v.IsDetail) { mid = new Vector2d(v.DetailX, v.DetailY); mn = mid - new Vector2d(v.DetailR); mx = mid + new Vector2d(v.DetailR); }
            else
            {
                // k158: вид с разрывом — габарит по придвинутой правой части
                mn.X = Fold(v, mn.X, s); mx.X = Fold(v, mx.X, s);
                mid = (mn + mx) / 2;
            }
            Xf[v.Name] = (mid, s, v.X, v.Y);
            bool brk = v.HasBreak && !BreakOff && !v.IsDetail;
            (double, double) T(Vector2d p) => MapF(v.X, v.Y, v.Angle, mid, s, new Vector2d(Fold(v, p.X, s), p.Y));
            (double X, double Y) TF(Vector2d p) => MapF(v.X, v.Y, v.Angle, mid, s, p);   // X уже с разрывом
            {
                var cs = new[] { TF(mn), TF(new Vector2d(mx.X, mn.Y)), TF(mx), TF(new Vector2d(mn.X, mx.Y)) };
                Boxes[v.Name] = (cs.Min(q => q.X), cs.Min(q => q.Y), cs.Max(q => q.X), cs.Max(q => q.Y));
            }
            // k161: тона граней (под линиями)
            if (scene.Shades.TryGetValue(v.Name, out var shades) && v.Style is "shaded" or "shadedlines" or "material")
                foreach (var (a, b, c, sh, bi) in shades)
                {
                    var (r, gg, bb) = v.Style == "material" && bi < scene.BodyColors.Count ? scene.BodyColors[bi] : (0.78, 0.8, 0.84);
                    uint argb = 0xFF000000u | (uint)Math.Clamp(r * sh * 255, 0, 255) << 16 | (uint)Math.Clamp(gg * sh * 255, 0, 255) << 8 | (uint)Math.Clamp(bb * sh * 255, 0, 255);
                    g.Shades.Add((new List<(double, double)> { T(a), T(b), T(c) }, argb));
                }
            bool showHidden = v.Style switch { "hidden" or "wire" => true, "shaded" or "shadedlines" or "material" => false, _ => v.ShowHidden };
            foreach (var l in lines)
            {
                if (v.Style == "shaded") break;
                if (!l.Visible && !showHidden) continue;
                if (l.Kind == ViewLineKind.Tangent && !v.ShowTangent) continue;
                var kind = !l.Visible ? (v.Style == "wire" ? LineKind.Thick : LineKind.Hidden) : l.Kind == ViewLineKind.Tangent ? LineKind.Thin : LineKind.Thick;
                if (brk) { foreach (var part in ClipBreak(l.Points, v.BreakA, v.BreakB)) g.Polylines.Add(new SheetPolyline(part.Select(T).ToList(), kind)); }
                else g.Polylines.Add(new SheetPolyline(l.Points.Select(T).ToList(), kind));
            }
            if (brk) BreakLines(g, v, s, mn.Y, mx.Y, TF);
            // штриховка фигуры сечения ГОСТ 2.306: 45°, шаг 3 мм листа
            if (scene.SectionBoundary.TryGetValue(v.Name, out var sb) && sb.Count > 0)
                foreach (var (a, b) in SectionCut.Hatch(sb, 45 - v.Angle, 3 / s))
                    foreach (var part in brk ? ClipBreak(new() { a, b }, v.BreakA, v.BreakB) : new[] { new List<Vector2d> { a, b } })
                        g.Polylines.Add(new SheetPolyline(part.Select(T).ToList(), LineKind.Thin));
            if (v.CenterLines && v.Name != "iso" && scene.Axes.TryGetValue(v.Name, out var ax)) AxisLines(g, ax, T, s);
            // подписи: «А–А», «Б (2:1)», свой масштаб «(1:2)»
            string caption = v.IsSection ? $"{v.Label}-{v.Label}" : v.IsDetail || v.IsAux ? v.Label : "";
            if (v.Scale > 0 && Math.Abs(v.Scale - scale) > 1e-12) caption = (caption + $" ({ScaleText(v.Scale)})").Trim();
            if (caption.Length > 0) g.Text(v.X, Boxes[v.Name].Y1 + 5, v.IsSection || v.IsDetail || v.IsAux ? 7 : SheetLayout.ValueH, caption, TextAlign.Center);
        }
        // линии разреза и круги выносных элементов — на виде-родителе
        foreach (var v in Views.Where(v => (v.IsSection || v.IsDetail || v.IsAux) && v.Parent != null && Xf.ContainsKey(v.Parent!)))
        {
            if (v.IsSection) CutLine(g, v);
            else if (v.IsAux) AuxArrow(g, v);
            else
            {
                var (cx, cy) = ToSheet(v.Parent!, new Vector2d(v.DetailX, v.DetailY));
                double r = v.DetailR * Xf[v.Parent!].S;
                g.Circles.Add((cx, cy, r, LineKind.Thin));
                // буква на полке выноски от круга (ГОСТ 2.305 п. 7.1)
                double a = Math.PI / 4;
                var p0 = (cx + r * Math.Cos(a), cy + r * Math.Sin(a)); var p1 = (p0.Item1 + 5, p0.Item2 + 5);
                g.Poly(LineKind.Thin, p0, p1, (p1.Item1 + 8, p1.Item2));
                g.Text(p1.Item1 + 4, p1.Item2 + 1, 7, v.Label, TextAlign.Center);
            }
        }
        // k159: сначала линейные размеры, затем диаметры и радиусы — у них угол выноски подбирается без наложения текстов
        if (ShowDims)
        {
            var rows = new Dictionary<(string, string, int), int>();
            foreach (var d in Dims.Where(d => !d.Hidden && Xf.ContainsKey(d.View)).OrderBy(d => d.Kind is "dia" or "rad" ? 1 : 0).ThenBy(d => d.Arrange && d.Kind is "h" or "v" ? d.Value : 0))
            {
                if (d.Arrange && !ArrangeOff)
                {
                    if (d.Kind is "dia" or "rad") PlaceRadial(g, d);
                    else if (d.Kind is "h" or "v") PlaceRow(g, d, rows);
                    // k161 (замечание 05.11, 01:23, п. 3): место выбирается один раз — после «Автоматических размеров» или
                    // «Размеров модели»; дальше размер стоит, где поставлен (прежде раскладка шла при каждом выводе листа:
                    // перенос размера мышью тормозил). Мутация P3D_ARRANGEONCE=0 — при каждом выводе.
                    if (!ArrangeOnceOff) d.Arrange = false;
                }
                Capture(g, d, () => DrawDim(g, d));
            }
        }
        foreach (var n in Notes)
        {
            if (!ShowGdt && n.Kind is "gdt" or "datum") continue;
            Capture(g, n, () => DrawNote(g, n, scene));
        }
        return g;
    }

    /// <summary>k159: мутация P3D_DIMARRANGE=0 — размеры без раскладки (ряды и углы выносок как заданы).</summary>
    static readonly bool ArrangeOff = Environment.GetEnvironmentVariable("P3D_DIMARRANGE") == "0";
    static readonly bool LineSOff = Environment.GetEnvironmentVariable("P3D_LINES") == "0";
    static readonly bool ArrangeOnceOff = Environment.GetEnvironmentVariable("P3D_ARRANGEONCE") == "0";

    /// <summary>Отрезки примитивов, добавленных draw(), — геометрия элемента для выбора мышью.</summary>
    void Capture(SheetGraphics g, object key, Action draw)
    {
        int nl = g.Lines.Count, np = g.Polylines.Count, nt = g.Texts.Count, nf = g.Fills.Count, nc = g.Circles.Count;
        draw();
        var segs = new List<(double, double, double, double)>();
        for (int i = nl; i < g.Lines.Count; i++) { var l = g.Lines[i]; segs.Add((l.X1, l.Y1, l.X2, l.Y2)); }
        for (int i = np; i < g.Polylines.Count; i++) { var q = g.Polylines[i].Points; for (int k = 0; k + 1 < q.Count; k++) segs.Add((q[k].X, q[k].Y, q[k + 1].X, q[k + 1].Y)); }
        for (int i = nf; i < g.Fills.Count; i++) { var q = g.Fills[i]; for (int k = 0; k < q.Count; k++) { var b = q[(k + 1) % q.Count]; segs.Add((q[k].X, q[k].Y, b.X, b.Y)); } }
        for (int i = nc; i < g.Circles.Count; i++) { var (cx, cy, r, _) = g.Circles[i]; for (int k = 0; k < 16; k++) { double a0 = k * Math.PI / 8, a1 = (k + 1) * Math.PI / 8; segs.Add((cx + r * Math.Cos(a0), cy + r * Math.Sin(a0), cx + r * Math.Cos(a1), cy + r * Math.Sin(a1))); } }
        // k161 (п. 11): текст — рамкой без диагоналей; попадание в текст — по прямоугольнику (HitTexts)
        var rects = new List<Vector2d[]>();
        for (int i = nt; i < g.Texts.Count; i++)
        {
            var c = TextCorners(g.Texts[i]);
            for (int k = 0; k < 4; k++) segs.Add((c[k].X, c[k].Y, c[(k + 1) % 4].X, c[(k + 1) % 4].Y));
            rects.Add(c);
        }
        Hit[key] = segs;
        HitTexts[key] = rects;
    }

    /// <summary>Углы прямоугольника текста на листе (ширина — оценка по ГОСТ тип А).</summary>
    public static Vector2d[] TextCorners(SheetText t)
    {
        double w = TextW(t.Text, t.Height), h = t.Height;
        double x0 = t.Align == TextAlign.Center ? -w / 2 : t.Align == TextAlign.Right ? -w : 0;
        var o = new Vector2d(t.X, t.Y);
        return new[] { new Vector2d(x0, 0), new Vector2d(x0 + w, 0), new Vector2d(x0 + w, h), new Vector2d(x0, h) }.Select(q => o + Rot(q, t.Angle)).ToArray();
    }
    static (double X0, double Y0, double X1, double Y1) TextBox(SheetText t)
    {
        var c = TextCorners(t);
        return (c.Min(q => q.X), c.Min(q => q.Y), c.Max(q => q.X), c.Max(q => q.Y));
    }
    static bool Overlap((double X0, double Y0, double X1, double Y1) a, (double X0, double Y0, double X1, double Y1) b, double gap = 0.5) =>
        a.X0 < b.X1 + gap && b.X0 < a.X1 + gap && a.Y0 < b.Y1 + gap && b.Y0 < a.Y1 + gap;

    /// <summary>Элемент (размер, обозначение) под точкой листа: ближайший отрезок его геометрии не дальше tol мм.</summary>
    public object? HitAt(double x, double y, double tol = 1.5)
    {
        object? best = null; double bd = tol;
        // внутри прямоугольника текста — попадание
        foreach (var (k, rs) in HitTexts)
            foreach (var c in rs)
            {
                var e1 = c[1] - c[0]; var e2 = c[3] - c[0]; var q = new Vector2d(x, y) - c[0];
                double a = Vector2d.Dot(q, e1) / Math.Max(1e-12, e1.LengthSquared), b = Vector2d.Dot(q, e2) / Math.Max(1e-12, e2.LengthSquared);
                if (a >= 0 && a <= 1 && b >= 0 && b <= 1) return k;
            }
        foreach (var (k, segs) in Hit)
            foreach (var (x1, y1, x2, y2) in segs)
            {
                double dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-18 ? 0 : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / l2, 0, 1);
                double d = Math.Sqrt(Math.Pow(x1 + dx * t - x, 2) + Math.Pow(y1 + dy * t - y, 2));
                if (d < bd) { bd = d; best = k; }
            }
        return best;
    }

    /// <summary>
    /// k159: ряд линейного размера: на своей стороне вида — следующий ряд (10, 17, 24 … мм от габарита, меньшие размеры ближе);
    /// если текст ложится на текст другого размера или на соседний вид (между видами 20 мм — там же размеры соседа), — на
    /// противоположную сторону.
    /// </summary>
    void PlaceRow(SheetGraphics g, DrawingDim d, Dictionary<(string, string, int), int> rows)
    {
        var occupied = g.Texts.Where(t => t.Text.Length > 0).Select(TextBox).ToList();
        var others = Boxes.Where(b => b.Key != d.View).Select(b => b.Value).ToList();
        var (fx0, fy0, fx1, fy1) = SheetLayout.FrameRect(Template);
        int pref = d.Offset < 0 ? -1 : 1;
        d.FromBox = true;
        foreach (int side in new[] { pref, -pref })
        {
            int k = rows.GetValueOrDefault((d.View, d.Kind, side));
            d.Offset = side * (10 + 7 * k);
            var g2 = new SheetGraphics { Width = g.Width, Height = g.Height };
            DrawDim(g2, d);
            var rects = g2.Texts.Select(TextBox).ToList();
            bool ok = rects.All(r => r.X0 >= fx0 && r.X1 <= fx1 && r.Y0 >= fy0 && r.Y1 <= fy1)
                      && !rects.Any(r => occupied.Any(o => Overlap(r, o)) || others.Any(o => Overlap(r, o, 0)));
            if (ok) { rows[(d.View, d.Kind, side)] = k + 1; return; }
        }
        int k0 = rows.GetValueOrDefault((d.View, d.Kind, pref));
        d.Offset = pref * (10 + 7 * k0); rows[(d.View, d.Kind, pref)] = k0 + 1;
    }

    /// <summary>
    /// k159: угол выноски диаметра и радиуса — первый из ряда (45°, 135°, 315°, 225°, 30°, 150°, 60°, 120°, …), при котором текст не
    /// накладывается на тексты листа и других размеров, не заходит на чужие виды и остаётся в рамке.
    /// </summary>
    void PlaceRadial(SheetGraphics g, DrawingDim d)
    {
        var occupied = g.Texts.Where(t => t.Text.Length > 0).Select(TextBox).ToList();
        var others = Boxes.Where(b => b.Key != d.View).Select(b => b.Value).ToList();
        var (fx0, fy0, fx1, fy1) = SheetLayout.FrameRect(Template);
        double keep = d.Y2, keepOff = d.Offset;
        double[] angles = { 45, 135, 315, 225, 30, 150, 330, 210, 60, 120, 300, 240, 15, 165, 345, 195, 75, 105, 285, 255 };
        var own = Boxes.TryGetValue(d.View, out var ob) ? ob : default;
        // проход 1: текст и за контуром своего вида (не по линиям детали); проход 2 — допускается на своём виде
        foreach (bool outside in new[] { true, false })
            foreach (var off in new[] { keepOff, 20, 30, 45, 60 })
                foreach (var a in off == keepOff ? angles.Prepend(keep) : angles)
                {
                    d.Y2 = a; d.Offset = off;
                    var g2 = new SheetGraphics { Width = g.Width, Height = g.Height };
                    DrawRadial(g2, d, CaptionOf(d));
                    var rects = g2.Texts.Select(TextBox).ToList();
                    bool ok = rects.All(r => r.X0 >= fx0 && r.X1 <= fx1 && r.Y0 >= fy0 && r.Y1 <= fy1)
                              && !rects.Any(r => occupied.Any(o => Overlap(r, o)) || others.Any(o => Overlap(r, o, 0)) || outside && Overlap(r, own, 0));
                    if (ok) return;
                }
        d.Y2 = keep; d.Offset = keepOff;
    }

    /// <summary>k158: линии обрыва ГОСТ 2.303 — тонкие волнистые на краях полосы разрыва (за контур вида на 2 мм).</summary>
    static void BreakLines(SheetGraphics g, DrawingView v, double s, double y0, double y1, Func<Vector2d, (double X, double Y)> tf)
    {
        // в координатах вида с разрывом (мм модели): края промежутка, за контур на 2 мм листа, волна 1.2 мм с шагом 12 мм листа
        foreach (double x in new[] { v.BreakA, v.BreakA + v.BreakGap / s })
        {
            var pts = new List<(double X, double Y)>();
            double ya = y0 - 2 / s, yb = y1 + 2 / s; int n = Math.Max(8, (int)((yb - ya) * s / 1.5));
            for (int i = 0; i <= n; i++) { double y = ya + (yb - ya) * i / n; pts.Add(tf(new Vector2d(x + 1.2 / s * Math.Sin((y - ya) * s / 6 * Math.PI), y))); }
            g.Polylines.Add(new SheetPolyline(pts, LineKind.Thin));
        }
    }

    /// <summary>Направление вспомогательного вида на виде-родителе (лист): от наклонной линии к наблюдателю.</summary>
    public Vector2d AuxDir(DrawingView aux)
    {
        var pf = ViewFrame.Of(DirOf(Views.First(x => x.Name == aux.Parent))); var af = ViewFrame.Of(DirOf(aux));
        var n2 = new Vector2d(Vector3d.Dot(af.Toward, pf.Right), Vector3d.Dot(af.Toward, pf.Up));
        return n2.Length < 1e-9 ? Vector2d.UnitY : n2.Normalized();
    }

    /// <summary>k158: стрелка вспомогательного вида ГОСТ 2.305 п. 6.2: перпендикулярно наклонной линии, к ней; буква у стрелки.</summary>
    void AuxArrow(SheetGraphics g, DrawingView aux)
    {
        var n2 = DirToSheet(aux.Parent!, AuxDir(aux));
        var (mx, my) = ToSheet(aux.Parent!, new Vector2d(aux.DetailX, aux.DetailY));
        var tip = (mx + n2.X * 6, my + n2.Y * 6); var tail = (mx + n2.X * 20, my + n2.Y * 20);
        g.Line(tail.Item1, tail.Item2, tip.Item1 + n2.X * ArrowL, tip.Item2 + n2.Y * ArrowL);
        Arrow(g, tip, (-n2.X, -n2.Y));
        var side = new Vector2d(-n2.Y, n2.X);
        g.Text(tail.Item1 + side.X * 4, tail.Item2 + side.Y * 4 - 3.5, 7, aux.Label, TextAlign.Center);
    }

    /// <summary>Линия разреза ГОСТ 2.305: разомкнутая (толстые штрихи 10 мм за контуром вида), стрелки по направлению взгляда, буквы.</summary>
    void CutLine(SheetGraphics g, DrawingView sec)
    {
        var parent = Views.First(x => x.Name == sec.Parent);
        var pf = ViewFrame.Of(DirOf(parent)); var sf = ViewFrame.Of(DirOf(sec));
        var n2 = new Vector2d(Vector3d.Dot(sf.Toward, pf.Right), Vector3d.Dot(sf.Toward, pf.Up));   // нормаль плоскости на виде-родителе
        if (n2.Length < 1e-9) return;
        double nl = n2.Length; n2 /= nl;
        double c = sec.CutDepth / nl;
        var dir = new Vector2d(-n2.Y, n2.X);
        var (bx0, by0, bx1, by1) = Boxes[parent.Name];
        var (mid, s, X, Y) = Xf[parent.Name];
        // концы линии — по габариту вида (лист), выход 3 мм
        var lineAt = n2 * c;
        var corners = new[] { ToView(parent.Name, bx0, by0), ToView(parent.Name, bx1, by0), ToView(parent.Name, bx1, by1), ToView(parent.Name, bx0, by1) };
        double t0 = corners.Min(q => Vector2d.Dot(q - lineAt, dir)) - 3 / s, t1 = corners.Max(q => Vector2d.Dot(q - lineAt, dir)) + 3 / s;
        var vdir = dir;
        { var rn = DirToSheet(parent.Name, n2); var rd = DirToSheet(parent.Name, dir); n2 = rn; dir = rd; }
        foreach (var (te, sgn) in new[] { (t0, 1.0), (t1, -1.0) })
        {
            var e0 = ToSheet(parent.Name, lineAt + vdir * te); var e1 = ToSheet(parent.Name, lineAt + vdir * (te + sgn * 10 / s));
            g.Line(e0.X, e0.Y, e1.X, e1.Y, LineKind.Thick);
            // стрелка у внешнего конца: перпендикулярно, по взгляду разреза (от наблюдателя): -n2
            var tip = (e0.X - n2.X * 8, e0.Y - n2.Y * 8);
            var basePt = (e0.X - n2.X * 2, e0.Y - n2.Y * 2);
            g.Line(e0.X - n2.X * 0.5, e0.Y - n2.Y * 0.5, basePt.Item1, basePt.Item2);
            g.Line(basePt.Item1, basePt.Item2, tip.Item1, tip.Item2);
            Arrow(g, tip, (-n2.X, -n2.Y));
            g.Text(tip.Item1 + dir.X * sgn * -4, tip.Item2 + dir.Y * sgn * -4 - 3.5, 7, sec.Label, TextAlign.Center);
        }
    }

    // ------------------------------------------------------------------ размеры ГОСТ 2.307

    const double ArrowL = 3, ArrowW = 0.5, TextH = 3.5;
    static double TextW(string t, double h) => t.Length * h * 0.62;

    /// <summary>Залитая стрелка: остриё в tip, направление острия dir (единичный вектор листа).</summary>
    static void Arrow(SheetGraphics g, (double X, double Y) tip, (double X, double Y) dir)
    {
        double l = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y); if (l < 1e-12) return;
        double ux = dir.X / l, uy = dir.Y / l, nx = -uy, ny = ux;
        double bx = tip.X - ux * ArrowL, by = tip.Y - uy * ArrowL;
        g.Fills.Add(new() { tip, (bx + nx * ArrowW, by + ny * ArrowW), (bx - nx * ArrowW, by - ny * ArrowW) });
    }

    void DrawDim(SheetGraphics g, DrawingDim d)
    {
        string text = CaptionOf(d);
        if (d.Kind is "dia" or "rad") { DrawRadial(g, d, text); return; }
        var (P1, P2, u, n, h1, h2, L) = DimFrame(d);
        var q1 = P1 + n * (L - h1); var q2 = P2 + n * (L - h2);
        double sg = Math.Sign(d.Offset == 0 ? 1 : d.Offset);
        // выносные линии: от точки до размерной + 2 мм за неё
        if (Math.Abs(L - h1) > 0.5) g.Line(P1.X + n.X * sg, P1.Y + n.Y * sg, q1.X + n.X * sg * 2, q1.Y + n.Y * sg * 2);
        if (Math.Abs(L - h2) > 0.5) g.Line(P2.X + n.X * sg, P2.Y + n.Y * sg, q2.X + n.X * sg * 2, q2.Y + n.Y * sg * 2);
        double len = (q2 - q1).Length;
        var along = len > 1e-9 ? (q2 - q1) / len : u;
        bool outside = d.Arrows == 1 ? false : d.Arrows == 2 || len < 2 * ArrowL + 4;
        if (outside)
        {
            g.Line(q1.X - along.X * 8, q1.Y - along.Y * 8, q2.X + along.X * 8, q2.Y + along.Y * 8);
            Arrow(g, (q1.X, q1.Y), (along.X, along.Y)); Arrow(g, (q2.X, q2.Y), (-along.X, -along.Y));
        }
        else
        {
            g.Line(q1.X, q1.Y, q2.X, q2.Y);
            Arrow(g, (q1.X, q1.Y), (-along.X, -along.Y)); Arrow(g, (q2.X, q2.Y), (along.X, along.Y));
        }
        // текст над размерной линией, по её направлению (читаемо: угол в (−90°, 90°])
        double ang = Math.Atan2(along.Y, along.X) * 180 / Math.PI;
        var up = new Vector2d(-along.Y, along.X);
        if (ang > 90.0001 || ang <= -90) { ang += ang > 0 ? -180 : 180; up = -up; }
        var m = (q1 + q2) / 2 + up * 1;
        if (d.TextShift == 0 && outside && len < TextW(text, TextH)) m += (q2 - q1).Length > 1e-9 ? along * (len / 2 + 8 + TextW(text, TextH) / 2) : Vector2d.Zero;
        if (d.TextShift != 0)
        {
            // k161: текст сдвинут вдоль линии; за концом — продление размерной линии под текст (полка)
            double t = d.TextShift * (Vector2d.Dot(along, u) >= 0 ? 1 : -1);
            m += along * t;
            double tw = TextW(text, TextH);
            var mid = (q1 + q2) / 2;
            if (Math.Abs(t) + tw / 2 > len / 2)
            {
                var end = t > 0 ? q2 : q1;
                var far = mid + along * (t + Math.Sign(t) * (tw / 2 + 1));
                g.Line(end.X, end.Y, far.X, far.Y);
            }
        }
        g.Text(m.X, m.Y, TextH, text, TextAlign.Center, null, Math.Abs(ang) < 1e-9 ? 0 : ang);
    }

    /// <summary>
    /// Линейный размер на листе: точки, направление размерной линии u (h/v — по осям вида, с поворотом), нормаль n, высоты точек
    /// и положение размерной линии L по нормали. Отступ — от дальней точки или (FromBox) от габарита вида.
    /// </summary>
    public (Vector2d P1, Vector2d P2, Vector2d U, Vector2d N, double H1, double H2, double L) DimFrame(DrawingDim d)
    {
        var p1 = ToSheet(d.View, new Vector2d(d.X1, d.Y1)); var p2 = ToSheet(d.View, new Vector2d(d.X2, d.Y2));
        Vector2d P1 = new(p1.X, p1.Y), P2 = new(p2.X, p2.Y);
        Vector2d u = d.Kind == "h" ? DirToSheet(d.View, Vector2d.UnitX) : d.Kind == "v" ? DirToSheet(d.View, Vector2d.UnitY) : (P2 - P1).Length > 1e-9 ? (P2 - P1).Normalized() : Vector2d.UnitX;
        var n = new Vector2d(-u.Y, u.X);
        double h1 = Vector2d.Dot(P1, n), h2 = Vector2d.Dot(P2, n);
        double hi = Math.Max(h1, h2), lo = Math.Min(h1, h2);
        if (d.FromBox && Boxes.TryGetValue(d.View, out var b))
        {
            var hs = new[] { new Vector2d(b.X0, b.Y0), new Vector2d(b.X1, b.Y0), new Vector2d(b.X1, b.Y1), new Vector2d(b.X0, b.Y1) }.Select(q => Vector2d.Dot(q, n)).ToList();
            hi = hs.Max(); lo = hs.Min();
        }
        double L = (d.Offset >= 0 ? hi : lo) + d.Offset;
        return (P1, P2, u, n, h1, h2, L);
    }

    /// <summary>k159: перенос размера мышью на (dx, dy) мм листа: линейный — размерная линия по нормали, диаметр и радиус — угол выноски.</summary>
    public void MoveDim(DrawingDim d, double dx, double dy, double sx, double sy)
    {
        d.Arrange = false;
        if (d.Kind is "dia" or "rad")
        {
            var c = ToSheet(d.View, new Vector2d(d.X1, d.Y1));
            d.Y2 = Math.Atan2(sy - c.Y, sx - c.X) * 180 / Math.PI;
            // k161: длина выноски — до курсора (без ограничения): Offset = выноска / 0.6
            double r = d.X2 * Xf[d.View].S, dist = Math.Sqrt((sx - c.X) * (sx - c.X) + (sy - c.Y) * (sy - c.Y));
            if (dist > r + 1) d.Offset = Math.Max(2, dist - r) / 0.6;
            return;
        }
        var (_, _, u0, n, h1, h2, L) = DimFrame(d);
        // k161: вдоль размерной линии — сдвиг текста (в том числе за концы — сбоку от размера)
        d.TextShift += dx * u0.X + dy * u0.Y;
        double Ln = L + dx * n.X + dy * n.Y;
        double hi = Math.Max(h1, h2), lo = Math.Min(h1, h2);
        if (d.FromBox && Boxes.TryGetValue(d.View, out var b))
        {
            var hs = new[] { new Vector2d(b.X0, b.Y0), new Vector2d(b.X1, b.Y0), new Vector2d(b.X1, b.Y1), new Vector2d(b.X0, b.Y1) }.Select(q => Vector2d.Dot(q, n)).ToList();
            hi = hs.Max(); lo = hs.Min();
        }
        if (Ln >= hi) d.Offset = Ln - hi;
        else if (Ln <= lo) d.Offset = Ln - lo;
        else if (d.FromBox) { d.FromBox = false; d.Offset = Ln >= Math.Max(h1, h2) ? Ln - Math.Max(h1, h2) : Ln <= Math.Min(h1, h2) ? Ln - Math.Min(h1, h2) : (d.Offset >= 0 ? 0 : -0.0001); }
        else d.Offset = d.Offset >= 0 ? 0 : -0.0001;
    }

    void DrawRadial(SheetGraphics g, DrawingDim d, string text)
    {
        var c = ToSheet(d.View, new Vector2d(d.X1, d.Y1));
        double r = d.X2 * Xf[d.View].S, a = d.Y2 * Math.PI / 180;
        var dir = (Math.Cos(a), Math.Sin(a));
        var p = (c.X + r * dir.Item1, c.Y + r * dir.Item2);
        double tw = TextW(text, TextH);
        if (d.Kind == "dia" && r > 12 && d.Arrows != 2)
        {
            // большая окружность: размерная линия через центр, стрелки изнутри на окружность
            var p2 = (c.X - r * dir.Item1, c.Y - r * dir.Item2);
            g.Line(p.Item1, p.Item2, p2.Item1, p2.Item2);
            Arrow(g, p, dir); Arrow(g, p2, (-dir.Item1, -dir.Item2));
            double ang = a * 180 / Math.PI; var up = (-dir.Item2, dir.Item1);
            if (ang > 90.0001 || ang <= -90) { ang += ang > 0 ? -180 : 180; up = (-up.Item1, -up.Item2); }
            g.Text(c.X + up.Item1, c.Y + up.Item2, TextH, text, TextAlign.Center, null, ang);
            return;
        }
        // выноска с полкой: от окружности наружу 6 мм (k159: у диаметра и радиуса Offset — длина выноски: 0.6·Offset, по
        // умолчанию 10 → 6 мм; раскладка удлиняет выноску, если ни один угол не свободен), полка по длине текста
        double lead = d.Offset > 0 ? 0.6 * d.Offset : 6;
        var k = (p.Item1 + dir.Item1 * lead, p.Item2 + dir.Item2 * lead);
        double side = dir.Item1 >= 0 ? 1 : -1;
        var e = (k.Item1 + side * (tw + 2), k.Item2);
        if (d.Kind == "rad")
        {
            // радиус: линия от центра через дугу, стрелка на дуге (остриём наружу)
            g.Poly(LineKind.Thin, (c.X, c.Y), p, k, e);
            Arrow(g, p, dir);
        }
        else
        {
            g.Poly(LineKind.Thin, p, k, e);
            Arrow(g, p, (-dir.Item1, -dir.Item2));
        }
        g.Text((k.Item1 + e.Item1) / 2, k.Item2 + 1, TextH, text, TextAlign.Center);
    }

    // ------------------------------------------------------------------ обозначения

    void DrawNote(SheetGraphics g, DrawingNote n, DrawingScene scene)
    {
        (double X, double Y)? target = n.HasTarget && n.View != null && Xf.ContainsKey(n.View) ? ToSheet(n.View, new Vector2d(n.TX, n.TY)) : null;
        switch (n.Kind)
        {
            case "rough":
                {
                    if (target is { } t)
                    {
                        // знак на поверхности: вершина в точке цели, полка вправо, значение над полкой
                        RoughSign(g, t.X, t.Y, n.Value, n.Height);
                    }
                    else
                    {
                        // неуказанная шероховатость — правый верхний угол рамки: «Ra 6,3 (√)»
                        var (_, _, fx1, fy1) = SheetLayout.FrameRect(Template);
                        double x = n.X != 0 ? n.X : fx1 - 30, y = n.Y != 0 ? n.Y : fy1 - 12;
                        RoughSign(g, x, y, n.Value, n.Height);
                        g.Text(x + 14, y + 1, n.Height, "(", TextAlign.Left);
                        RoughSign(g, x + 18, y + 1, "", n.Height * 0.8);
                        g.Text(x + 25, y + 1, n.Height, ")", TextAlign.Left);
                    }
                    break;
                }
            case "gdt":
                {
                    double h = 7, x = n.X, y = n.Y;
                    var cells = new List<(double W, string T, string? Sym)> { (h, "", n.Symbol) };
                    if (n.Value.Length > 0) cells.Add((Math.Max(h, TextW(n.Value, TextH) + 3), n.Value, null));
                    foreach (var dt in n.Datums.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)) cells.Add((h, dt, null));
                    double cx = x;
                    foreach (var (w, t, sym) in cells)
                    {
                        g.Rect(cx, y, w, h);
                        if (sym != null) GdtSymbol(g, sym, cx + w / 2, y + h / 2, h * 0.32);
                        else g.CellText(cx, y, w, h, TextH, t);
                        cx += w;
                    }
                    if (target is { } t2) { g.Poly(LineKind.Thin, (x, y + h / 2), (x - 5, y + h / 2), t2); Arrow(g, t2, (t2.X - (x - 5), t2.Y - (y + h / 2))); }
                    break;
                }
            case "datum":
                {
                    double h = 7, x = n.X, y = n.Y;
                    g.Rect(x - h / 2, y, h, h);
                    g.CellText(x - h / 2, y, h, h, TextH + 1, n.Text);
                    if (target is { } t)
                    {
                        g.Line(x, y, t.X, t.Y + 2.6);
                        g.Fills.Add(new() { (t.X - 2.5, t.Y), (t.X + 2.5, t.Y), (t.X, t.Y + 3.2) });   // зачернённый треугольник базы
                    }
                    break;
                }
            case "balloon":
                {
                    string num = n.Position.ToString(CultureInfo.InvariantCulture);
                    if (Template.Standard == SheetStandard.Iso)
                    {
                        double r = 5;
                        g.Circles.Add((n.X, n.Y, r, LineKind.Thin));
                        g.Text(n.X, n.Y - n.Height / 2 - 0.5, 5, num, TextAlign.Center);
                        if (target is { } t)
                        {
                            double dx = t.X - n.X, dy = t.Y - n.Y, l = Math.Sqrt(dx * dx + dy * dy);
                            if (l > r) g.Line(n.X + dx / l * r, n.Y + dy / l * r, t.X, t.Y);
                            Dot(g, t.X, t.Y);
                        }
                    }
                    else
                    {
                        // ГОСТ 2.109: полка с номером позиции, выноска с точкой на детали; шрифт на 1–2 размера больше размерных
                        double w = TextW(num, 5) + 4;
                        g.Line(n.X, n.Y, n.X + w, n.Y);
                        g.Text(n.X + w / 2, n.Y + 1, 5, num, TextAlign.Center);
                        if (target is { } t) { g.Line(n.X, n.Y, t.X, t.Y); Dot(g, t.X, t.Y); }
                    }
                    break;
                }
            case "holes": HoleTable(g, n, scene); break;
            default:
                {
                    var lines = n.Text.Replace("\r", "").Split('\n');
                    for (int i = 0; i < lines.Length; i++) g.Text(n.X, n.Y - i * n.Height * 1.6, n.Height, lines[i]);
                    if (target is { } t) { g.Poly(LineKind.Thin, (n.X - 1, n.Y + n.Height / 2), t); Arrow(g, t, (t.X - n.X + 1, t.Y - n.Y - n.Height / 2)); }
                    break;
                }
        }
    }

    static void Dot(SheetGraphics g, double x, double y)
    {
        var pts = new List<(double, double)>();
        for (int k = 0; k < 10; k++) pts.Add((x + 0.6 * Math.Cos(k * Math.PI / 5), y + 0.6 * Math.Sin(k * Math.PI / 5)));
        g.Fills.Add(pts);
    }

    /// <summary>Знак шероховатости ГОСТ 2.309 (без удаления слоя не различается): вершина (x, y), полка — над длинной стороной.</summary>
    static void RoughSign(SheetGraphics g, double x, double y, string value, double h)
    {
        double H = h * 1.6;
        var left = (x - H * 0.58, y + H); var top = (x + H * 1.15, y + 2 * H);
        g.Poly(LineKind.Thin, left, (x, y), top);
        if (value.Length > 0)
        {
            double w = TextW(value, h) + 1;
            g.Line(top.Item1, top.Item2, top.Item1 + w, top.Item2);
            g.Text(top.Item1 + w / 2 + 0.5, top.Item2 + 1, h, value, TextAlign.Center);
        }
    }

    /// <summary>Знаки допусков формы и расположения ГОСТ 2.308 — линиями (шрифт листа их не содержит).</summary>
    public static readonly (string Key, string Name)[] GdtSymbols =
    {
        ("str", "Прямолинейность"), ("flat", "Плоскостность"), ("circ", "Круглость"), ("cyl", "Цилиндричность"),
        ("par", "Параллельность"), ("perp", "Перпендикулярность"), ("ang", "Наклон"), ("pos", "Позиционный"),
        ("conc", "Соосность"), ("sym", "Симметричность"), ("run", "Радиальное биение"),
    };

    static void GdtSymbol(SheetGraphics g, string sym, double cx, double cy, double r)
    {
        void C(double rr) => g.Circles.Add((cx, cy, rr, LineKind.Thin));
        switch (sym)
        {
            case "str": g.Line(cx - r, cy, cx + r, cy); break;
            case "flat": g.Poly(LineKind.Thin, (cx - r, cy - r * 0.5), (cx + r * 0.6, cy - r * 0.5), (cx + r, cy + r * 0.5), (cx - r * 0.6, cy + r * 0.5), (cx - r, cy - r * 0.5)); break;
            case "circ": C(r); break;
            case "cyl": C(r * 0.75); g.Line(cx - r * 0.6, cy - r, cx + r * 0.2, cy + r); g.Line(cx - r * 0.2, cy - r, cx + r * 0.6, cy + r); break;
            case "par": g.Line(cx - r * 0.7, cy - r, cx - r * 0.1, cy + r); g.Line(cx + r * 0.1, cy - r, cx + r * 0.7, cy + r); break;
            case "perp": g.Line(cx - r, cy - r, cx + r, cy - r); g.Line(cx, cy - r, cx, cy + r); break;
            case "ang": g.Line(cx - r, cy - r, cx + r, cy - r); g.Line(cx - r, cy - r, cx + r, cy + r * 0.3); break;
            case "pos": C(r * 0.6); g.Line(cx - r, cy, cx + r, cy); g.Line(cx, cy - r, cx, cy + r); break;
            case "conc": C(r * 0.45); C(r); break;
            case "sym": g.Line(cx - r, cy, cx + r, cy); g.Line(cx - r * 0.6, cy + r * 0.5, cx + r * 0.6, cy + r * 0.5); g.Line(cx - r * 0.6, cy - r * 0.5, cx + r * 0.6, cy - r * 0.5); break;
            case "run": g.Line(cx - r * 0.5, cy - r, cx + r * 0.5, cy + r); g.Line(cx + r * 0.5, cy + r, cx + r * 0.1, cy + r * 0.4); break;
            default: g.Text(cx, cy - 1.75, 3.5, sym, TextAlign.Center); break;
        }
    }

    // ------------------------------------------------------------------ таблица отверстий (D5)

    /// <summary>Отверстия вида (перекрестия — оси вдоль взгляда) от левого нижнего угла габарита: номер, X, Y, диаметр.</summary>
    public static List<(int No, double X, double Y, double D)> Holes(DrawingScene scene, string view)
    {
        if (!scene.Lines.TryGetValue(view, out var lines) || !scene.Axes.TryGetValue(view, out var axes)) return new();
        var (mn, _) = Box(lines);
        return axes.Where(a => a.Cross).OrderByDescending(a => a.Center.Y).ThenBy(a => a.Center.X)
                   .Select((a, i) => (i + 1, a.Center.X - mn.X, a.Center.Y - mn.Y, 2 * a.Radius)).ToList();
    }

    void HoleTable(SheetGraphics g, DrawingNote n, DrawingScene scene)
    {
        string view = n.View ?? "top";
        var holes = Holes(scene, view);
        if (holes.Count == 0) return;
        double[] cols = { 12, 20, 20, 16 }; string[] heads = { "Отв.", "X", "Y", Dia };
        double rh = 7, x = n.X, top = n.Y, w = cols.Sum();
        int rows = holes.Count + 1;
        g.Rect(x, top - rows * rh, w, rows * rh, LineKind.Thick);
        for (int r = 1; r < rows; r++) g.Line(x, top - r * rh, x + w, top - r * rh, r == 1 ? LineKind.Thick : LineKind.Thin);
        double cx = x;
        for (int c = 0; c < cols.Length; c++)
        {
            if (c > 0) g.Line(cx, top, cx, top - rows * rh, LineKind.Thick);
            g.CellText(cx, top - rh, cols[c], rh, TextH, heads[c]);
            for (int r = 0; r < holes.Count; r++)
            {
                var h = holes[r];
                string t = c switch { 0 => h.No.ToString(CultureInfo.InvariantCulture), 1 => Num(h.X), 2 => Num(h.Y), _ => Num(h.D) };
                g.CellText(cx, top - (r + 2) * rh, cols[c], rh, TextH, t);
            }
            cx += cols[c];
        }
        // номера отверстий на виде
        if (Xf.ContainsKey(view) && scene.Lines.TryGetValue(view, out var vl))
        {
            var (mn, _) = Box(vl);
            foreach (var h in holes)
            {
                var p = ToSheet(view, new Vector2d(mn.X + h.X, mn.Y + h.Y));
                g.Text(p.X + 2 + h.D * Xf[view].S / 2, p.Y + 1, 2.5, h.No.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    string Num(double v) { var t = v.ToString("0.##", CultureInfo.InvariantCulture); return Comma ? t.Replace('.', ',') : t; }

    /// <summary>
    /// Осевые линии ГОСТ 2.303 (штрихпунктирная тонкая, выход за контур 3 мм): ось вдоль взгляда — перекрестие центра окружности,
    /// поперёк взгляда — ось по длине поверхности вращения.
    /// </summary>
    static void AxisLines(SheetGraphics g, List<ViewAxis> axes, Func<Vector2d, (double X, double Y)> T, double s)
    {
        double ext = 3 / s;
        foreach (var a in axes)
        {
            if (a.Cross)
            {
                double r = a.Radius + ext;
                g.Polylines.Add(new SheetPolyline(new() { T(a.Center - Vector2d.UnitX * r), T(a.Center + Vector2d.UnitX * r) }, LineKind.Center));
                g.Polylines.Add(new SheetPolyline(new() { T(a.Center - Vector2d.UnitY * r), T(a.Center + Vector2d.UnitY * r) }, LineKind.Center));
            }
            else g.Polylines.Add(new SheetPolyline(new() { T(a.Center + a.Dir * (a.Lo - ext)), T(a.Center + a.Dir * (a.Hi + ext)) }, LineKind.Center));
        }
    }

    // ------------------------------------------------------------------ автоматические размеры и позиции

    /// <summary>
    /// D3: АВТОМАТИЧЕСКИЕ РАЗМЕРЫ. Габаритные: на главном виде — ширина (снизу) и высота (справа), на виде сверху — глубина
    /// (слева); диаметры отверстий — по одному на размер на виде, где оси вдоль взгляда («4 отв. ⌀6»); радиусы скруглений
    /// (дуги не полного оборота) — по одному на значение. Прежние автоматические размеры заменяются; скрытые пользователем
    /// остаются скрытыми.
    /// </summary>
    /// <summary>k158: мутация P3D_VDIMSIDE=0 — вертикальные габаритные размеры внутри вида (прежнее, ошибка k156).</summary>
    static readonly bool VDimSideOff = Environment.GetEnvironmentVariable("P3D_VDIMSIDE") == "0";

    public void AutoDimensions(DrawingScene scene)
    {
        var hidden = Dims.Where(d => d.Auto && d.Hidden).Select(Key).ToHashSet();
        Dims.RemoveAll(d => d.Auto);
        void Add(DrawingDim d) { d.Auto = true; d.Arrange = true; d.Hidden = hidden.Contains(Key(d)); Dims.Add(d); }
        foreach (var v in Views.Where(v => !v.IsDetail && v.Name != "iso"))
        {
            if (!scene.Lines.TryGetValue(v.Name, out var lines) || lines.Count == 0) continue;
            var (mn, mx) = Box(lines);
            // k159: концы габаритных размеров — крайние точки контура (у ступенчатой детали угол габарита пуст: выносные линии
            // шли бы от пустого места). Мутация P3D_DIMEXTREME=0 — углы габарита (прежнее).
            var pts = lines.Where(l => l.Visible).SelectMany(l => l.Points).ToList();
            if (pts.Count == 0) pts = lines.SelectMany(l => l.Points).ToList();
            Vector2d Ext(Func<Vector2d, double> key, Func<Vector2d, double> tie, Vector2d corner)
            {
                if (ExtremeOff) return corner;
                double k = pts.Min(key);
                return pts.Where(q => key(q) <= k + 1e-6).OrderBy(tie).First();
            }
            if (v.Name == "front" || v.IsSection && !Views.Any(x => x.Name == "front"))
            {
                // k158: у вертикального размера нормаль размерной линии — влево (−X листа): справа от вида — отступ < 0
                var a = Ext(q => q.X, q => q.Y, mn); var b = Ext(q => -q.X, q => q.Y, new Vector2d(mx.X, mn.Y));
                Add(new DrawingDim { View = v.Name, Kind = "h", X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = -10, FromBox = !ExtremeOff });
                var c = Ext(q => q.Y, q => -q.X, new Vector2d(mx.X, mn.Y)); var e = Ext(q => -q.Y, q => -q.X, mx);
                Add(new DrawingDim { View = v.Name, Kind = "v", X1 = c.X, Y1 = c.Y, X2 = e.X, Y2 = e.Y, Offset = VDimSideOff ? 10 : -10, FromBox = !ExtremeOff });
            }
            if (v.Name == "top")
            {
                var c = Ext(q => q.Y, q => q.X, mn); var e = Ext(q => -q.Y, q => q.X, new Vector2d(mn.X, mx.Y));
                Add(new DrawingDim { View = v.Name, Kind = "v", X1 = c.X, Y1 = c.Y, X2 = e.X, Y2 = e.Y, Offset = VDimSideOff ? -10 : 10, FromBox = !ExtremeOff });
            }
            if (v.Name == "left" && !Views.Any(x => x.Name == "top"))
            {
                var a = Ext(q => q.X, q => q.Y, mn); var b = Ext(q => -q.X, q => q.Y, new Vector2d(mx.X, mn.Y));
                Add(new DrawingDim { View = v.Name, Kind = "h", X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = -10, FromBox = !ExtremeOff });
            }
        }
        // диаметры отверстий: вид с наибольшим числом перекрестий
        var holeView = Views.Where(v => !v.IsDetail && scene.Axes.ContainsKey(v.Name)).OrderByDescending(v => scene.Axes[v.Name].Count(a => a.Cross)).FirstOrDefault();
        if (holeView != null)
            foreach (var grp in scene.Axes[holeView.Name].Where(a => a.Cross).GroupBy(a => Math.Round(a.Radius, 4)))
            {
                var a = grp.OrderByDescending(x => x.Center.Y).ThenByDescending(x => x.Center.X).First();
                Add(new DrawingDim { View = holeView.Name, Kind = "dia", X1 = a.Center.X, Y1 = a.Center.Y, X2 = a.Radius, Y2 = 45, Count = grp.Count() });
            }
        // радиусы: дуги не полного оборота, по одному на значение
        var seen = new HashSet<double>();
        foreach (var v in Views.Where(v => !v.IsDetail && v.Name != "iso" && scene.Arcs.ContainsKey(v.Name)))
            foreach (var arc in scene.Arcs[v.Name].Where(a => !a.Full))
            {
                double r = Math.Round(arc.Radius, 4);
                if (!seen.Add(r)) continue;
                double am = (arc.A0 + arc.A1) / 2; if (arc.A1 < arc.A0) am += Math.PI;
                Add(new DrawingDim { View = v.Name, Kind = "rad", X1 = arc.Center.X, Y1 = arc.Center.Y, X2 = arc.Radius, Y2 = am * 180 / Math.PI });
            }
        ArrangeDims(scene);
    }

    static readonly bool ExtremeOff = Environment.GetEnvironmentVariable("P3D_DIMEXTREME") == "0";

    /// <summary>
    /// k159: РАСКЛАДКА РАЗМЕРОВ (ГОСТ 2.307 п. 1.12: первая размерная линия — не ближе 10 мм к контуру, параллельные — через 7 мм,
    /// меньший размер ближе к контуру). Линейные размеры с автоматическим местом (Arrange) — по видам и сторонам: сторона — как
    /// задана (габаритные) или ближайшая к точкам (размеры модели); ряды 10, 17, 24 … мм от габарита вида; совпадающие размеры
    /// (вид, вид размера, значение) — один. Угол выносок диаметров и радиусов — при выводе листа (PlaceRadial).
    /// </summary>
    public void ArrangeDims(DrawingScene scene)
    {
        if (ArrangeOff) return;
        // совпадающие: одинаковые вид, вид размера и значение — остаётся первый (автоматические — раньше размеров модели)
        var seen = new HashSet<string>();
        foreach (var d in Dims.Where(d => d.Arrange && d.Kind is "h" or "v").OrderBy(d => d.Auto ? 0 : 1).ToList())
            if (!seen.Add($"{d.View}|{d.Kind}|{Math.Round(d.Value, 4)}")) Dims.Remove(d);
        foreach (var grp in Dims.Where(d => d.Arrange && !d.Hidden && d.Kind is "h" or "v").GroupBy(d => d.View))
        {
            if (!scene.Lines.TryGetValue(grp.Key, out var lines) || lines.Count == 0) continue;
            var (mn, mx) = Box(lines); var c = (mn + mx) / 2;
            foreach (var d in grp)
            {
                d.FromBox = true;
                if (d.Auto) continue;   // сторона габаритных задана
                // размер модели — к ближней стороне: h — сверху или снизу (n = +Y), v — справа или слева (n = −X)
                d.Offset = d.Kind == "h" ? ((d.Y1 + d.Y2) / 2 >= c.Y ? 1 : -1) : ((d.X1 + d.X2) / 2 >= c.X ? -1 : 1);
            }
            foreach (var side in grp.GroupBy(d => (d.Kind, Math.Sign(d.Offset))))
            {
                int k = 0;
                foreach (var d in side.OrderBy(d => d.Value)) d.Offset = side.Key.Item2 * (10 + 7 * k++);
            }
        }
    }

    static string Key(DrawingDim d) => $"{d.View}|{d.Kind}|{Math.Round(d.X1, 3)}|{Math.Round(d.Y1, 3)}|{Math.Round(d.X2, 3)}|{Math.Round(d.Y2, 3)}";

    /// <summary>
    /// D3: РАЗМЕРЫ МОДЕЛИ — размеры эскизов детали на виде, плоскость которого параллельна плоскости эскиза (ГОСТ: размер
    /// — в натуральную величину). Расстояния (точка–точка, по горизонтали и вертикали эскиза), радиусы и диаметры; отключённые
    /// и справочные не переносятся.
    /// </summary>
    public int ModelDimensions(Part part)
    {
        Dims.RemoveAll(d => d.Model);
        int added = 0;
        foreach (var sk in part.Sketches)
        {
            var w = sk.Plane.W.Normalized();
            var view = Views.Where(v => !v.IsDetail && !v.IsSection && v.Name != "iso")
                            .FirstOrDefault(v => Math.Abs(Math.Abs(Vector3d.Dot(ViewFrame.Of(DirOf(v)).Toward, w)) - 1) < 1e-6);
            if (view == null) continue;
            var f = ViewFrame.Of(DirOf(view));
            Vector2d P(Vector2d skp) => f.Project(sk.Plane.ToWorld(skp));
            foreach (var d in sk.Dimensions)
            {
                if (!d.Enabled || d.Reference) continue;
                DrawingDim? dd = null;
                Vector2d[] pts = d.Items.SelectMany(PointsOf).ToArray();
                switch (d.Type)
                {
                    case Sketching.DimensionType.DistancePointPoint when pts.Length >= 2:
                    case Sketching.DimensionType.DistanceHorizontal when pts.Length >= 2:
                    case Sketching.DimensionType.DistanceVertical when pts.Length >= 2:
                        {
                            var a = P(pts[0]); var b = P(pts[^1]);
                            // ось U эскиза на виде: идёт ли она по горизонтали листа
                            var uOnView = f.Project(sk.Plane.ToWorld(new Vector2d(1, 0))) - f.Project(sk.Plane.ToWorld(Vector2d.Zero));
                            bool uIsH = Math.Abs(uOnView.X) > 0.5;
                            string kind = d.Type switch
                            {
                                Sketching.DimensionType.DistanceHorizontal => uIsH ? "h" : "v",
                                Sketching.DimensionType.DistanceVertical => uIsH ? "v" : "h",
                                _ => Math.Abs(a.Y - b.Y) < 1e-9 ? "h" : Math.Abs(a.X - b.X) < 1e-9 ? "v" : "al",
                            };
                            dd = new DrawingDim { View = view.Name, Kind = kind, X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Offset = 8 + 7 * (added % 3) };
                            break;
                        }
                    case Sketching.DimensionType.Radius or Sketching.DimensionType.Diameter:
                        {
                            var e = d.Items.FirstOrDefault(x => x is Sketching.SkCircle or Sketching.SkArc);
                            if (e is Sketching.SkCircle ci) dd = new DrawingDim { View = view.Name, Kind = d.Type == Sketching.DimensionType.Radius ? "rad" : "dia", X1 = P(ci.Center.P).X, Y1 = P(ci.Center.P).Y, X2 = ci.Radius, Y2 = 45 };
                            else if (e is Sketching.SkArc ar) dd = new DrawingDim { View = view.Name, Kind = d.Type == Sketching.DimensionType.Radius ? "rad" : "dia", X1 = P(ar.Center.P).X, Y1 = P(ar.Center.P).Y, X2 = ar.Radius, Y2 = 45 };
                            break;
                        }
                }
                if (dd == null) continue;
                dd.Model = true; dd.Arrange = true;
                Dims.Add(dd); added++;
            }
        }
        return added;
    }

    static IEnumerable<Vector2d> PointsOf(Sketching.SkEntity e) => e switch
    {
        Sketching.SkPoint p => new[] { p.P },
        Sketching.SkLine l => new[] { l.A.P, l.B.P },
        Sketching.SkCircle c => new[] { c.Center.P },
        Sketching.SkArc a => new[] { a.Center.P },
        _ => Array.Empty<Vector2d>(),
    };

    /// <summary>
    /// D4: ПОЗИЦИИ СБОРКИ (ГОСТ 2.109) на главном виде: по одной на деталь (номер — из перечня по имени, иначе по порядку),
    /// выноска к середине самой длинной видимой линии детали, полки — столбцом справа от вида с шагом 9 мм (без наложений).
    /// </summary>
    public void AutoBalloons(DrawingScene scene, IReadOnlyDictionary<string, int>? positions = null, string view = "front")
    {
        Notes.RemoveAll(n => n.Kind == "balloon" && n.Auto);
        if (!scene.Lines.TryGetValue(view, out var lines) || !Boxes.ContainsKey(view) || scene.BodyItems.Count == 0) return;
        var items = scene.BodyItems.Select((it, i) => (it, i)).GroupBy(x => x.it).ToList();
        var (bx0, by0, bx1, by1) = Boxes[view];
        var list = new List<(double TY, DrawingNote N)>();
        int next = 1;
        foreach (var grp in items)
        {
            var bodies = grp.Select(x => x.i).ToHashSet();
            var best = lines.Where(l => l.Visible && bodies.Contains(l.Body) && l.Points.Count >= 2).OrderByDescending(l => Len(l.Points)).FirstOrDefault();
            if (best == null) continue;
            var m = MidOf(best.Points);
            int pos = positions != null && positions.TryGetValue(grp.Key, out var pp) ? pp : next;
            next = Math.Max(next, pos) + 1;
            var t = ToSheet(view, m);
            list.Add((t.Y, new DrawingNote { Kind = "balloon", View = view, HasTarget = true, TX = m.X, TY = m.Y, Position = pos, Text = grp.Key, Auto = true, X = bx1 + 12 }));
        }
        // столбец полок: порядок — по высоте цели, шаг не меньше 9 мм
        double y = double.MaxValue;
        foreach (var (ty, n) in list.OrderByDescending(x => x.TY))
        {
            y = Math.Min(Math.Min(ty, by1), y - 9);
            n.Y = y;
            Notes.Add(n);
        }
    }

    static double Len(List<Vector2d> p) { double s = 0; for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length; return s; }
    static Vector2d MidOf(List<Vector2d> p)
    {
        double half = Len(p) / 2, acc = 0;
        for (int i = 1; i < p.Count; i++) { double l = (p[i] - p[i - 1]).Length; if (acc + l >= half) return p[i - 1] + (p[i] - p[i - 1]) * ((half - acc) / Math.Max(l, 1e-12)); acc += l; }
        return p[^1];
    }

    /// <summary>D5: разрез по виду-родителю: плоскость через середину габарита вида-родителя, взгляд — dir.</summary>
    public DrawingView AddSection(DrawingScene scene, string parent, string dir, double? depth = null)
    {
        var letter = NextLetter();
        var f = ViewFrame.Of(dir);
        double d = depth ?? 0;
        if (depth == null && scene.Lines.TryGetValue(parent, out var pl) && pl.Count > 0)
        {
            // середина габарита родителя по направлению нормали разреза на виде-родителе
            var pf = ViewFrame.Of(DirOf(Views.First(x => x.Name == parent)));
            var n2 = new Vector2d(Vector3d.Dot(f.Toward, pf.Right), Vector3d.Dot(f.Toward, pf.Up));
            var (mn, mx) = Box(pl);
            d = Vector2d.Dot((mn + mx) / 2, n2);
        }
        var v = new DrawingView { Name = $"{letter}-{letter}", Dir = dir, IsSection = true, Parent = parent, Label = letter, CutDepth = d, ShowHidden = false };
        Views.Add(v);
        return v;
    }

    /// <summary>
    /// k158: вспомогательный вид: взгляд перпендикулярно линии (a, b) вида-родителя (координаты вида, мм модели) — наклонная
    /// грань видна в натуральную величину. Наблюдатель — с внешней стороны линии (от середины габарита вида-родителя).
    /// </summary>
    public DrawingView AddAuxiliary(DrawingScene scene, string parent, Vector2d a, Vector2d b)
    {
        var pv = Views.First(x => x.Name == parent);
        var pf = ViewFrame.Of(DirOf(pv));
        var t2 = b - a; if (t2.Length < 1e-9) throw new ArgumentException("aux: zero line");
        t2.Normalize();
        var n2 = new Vector2d(-t2.Y, t2.X);
        var m = (a + b) / 2;
        if (scene.Lines.TryGetValue(parent, out var pl) && pl.Count > 0)
        {
            var (mn, mx) = Box(pl);
            if (Vector2d.Dot(m - (mn + mx) / 2, n2) < 0) n2 = -n2;
        }
        var T3 = (pf.Right * t2.X + pf.Up * t2.Y).Normalized();
        var N3 = (pf.Right * n2.X + pf.Up * n2.Y).Normalized();
        // мутация P3D_AUXFRAME=0 — вид смотрит как вид-родитель (без своей рамки)
        var frame = AuxFrameOff ? pf : new ViewFrame(T3, Vector3d.Cross(N3, T3).Normalized());
        var letter = NextLetter();
        var v = new DrawingView { Name = letter, Dir = frame.Encode(), IsAux = true, Parent = parent, Label = letter, DetailX = m.X, DetailY = m.Y, ShowHidden = false };
        Views.Add(v);
        return v;
    }

    static readonly bool AuxFrameOff = Environment.GetEnvironmentVariable("P3D_AUXFRAME") == "0";

    /// <summary>k158: разрыв вида: полоса [a, b] по X вида (мм модели) не показывается.</summary>
    public void AddBreak(string view, double a, double b)
    {
        var v = Views.First(x => x.Name == view);
        v.BreakA = Math.Min(a, b); v.BreakB = Math.Max(a, b);
    }

    /// <summary>D5: выносной элемент: круг на виде-родителе (центр и радиус — мм модели), масштаб — в 2 раза крупнее листа.</summary>
    public DrawingView AddDetail(string parent, Vector2d centre, double radius, double scale)
    {
        var letter = NextLetter();
        var v = new DrawingView { Name = letter, IsDetail = true, Parent = parent, Label = letter, DetailX = centre.X, DetailY = centre.Y, DetailR = radius, Scale = scale, ShowHidden = false };
        Views.Add(v);
        return v;
    }
}
