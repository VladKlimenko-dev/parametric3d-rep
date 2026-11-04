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

    [JsonIgnore] public double Value => Kind switch
    {
        "h" => Math.Abs(X2 - X1),
        "v" => Math.Abs(Y2 - Y1),
        "dia" => 2 * X2,
        "rad" => X2,
        _ => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1)),
    };

    public string Caption(bool comma)
    {
        if (Text.Length > 0) return Text;
        string v = Value.ToString("0.##", CultureInfo.InvariantCulture);
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

    /// <summary>Разбор: видов спроецировано.</summary>
    public static long ViewsProjected;

    /// <summary>Виды документа: обычные и разрезы — из тел, выносные элементы — из линий вида-родителя внутри круга.</summary>
    public static DrawingScene Build(DrawingDocument d, IReadOnlyList<TSolid> bodies, IReadOnlyList<string>? items = null)
    {
        var sc = new DrawingScene { BodyItems = items?.ToList() ?? new() };
        foreach (var v in d.Views.Where(v => !v.IsDetail))
        {
            var frame = ViewFrame.Of(d.DirOf(v));
            IReadOnlyList<TSolid> src = bodies;
            if (v.IsSection)
            {
                var (cut, boundary, problems) = SectionCut.Cut(bodies, frame, v.CutDepth);
                src = cut; sc.SectionBoundary[v.Name] = boundary; sc.Problems.AddRange(problems.Select(p => $"{v.Name}: {p}"));
            }
            sc.Lines[v.Name] = ViewProjection.ProjectMany(src, frame);
            sc.Axes[v.Name] = src.SelectMany(b => ViewProjection.Axes(b, frame)).ToList();
            sc.Arcs[v.Name] = ViewProjection.Arcs(src, frame);
            Interlocked.Increment(ref ViewsProjected);
        }
        foreach (var v in d.Views.Where(v => v.IsDetail))
        {
            if (v.Parent == null || !sc.Lines.TryGetValue(v.Parent, out var pl)) { sc.Lines[v.Name] = new(); continue; }
            var c = new Vector2d(v.DetailX, v.DetailY); double r = v.DetailR;
            sc.Lines[v.Name] = pl.SelectMany(l => ClipToCircle(l, c, r)).ToList();
            sc.Axes[v.Name] = sc.Axes[v.Parent].Where(a => (a.Center - c).Length < r).ToList();
            sc.Arcs[v.Name] = sc.Arcs[v.Parent].Where(a => (a.Center - c).Length < r).ToList();
            if (sc.SectionBoundary.TryGetValue(v.Parent, out var sb)) sc.SectionBoundary[v.Name] = sb.SelectMany(s => ClipSegment(s.A, s.B, c, r)).ToList();
        }
        return sc;
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

    /// <summary>Знак диаметра: в шрифте ГОСТ тип А (GOST_A.TTF) он на месте U+2205 (на месте «Ø» — буква «Ш»).</summary>
    public const string Dia = "\u2205";

    public string DirOf(DrawingView v) =>
        v.IsDetail && v.Parent != null && Views.FirstOrDefault(x => x.Name == v.Parent) is { } p ? DirOf(p) : string.IsNullOrEmpty(v.Dir) ? v.Name : v.Dir;

    /// <summary>Вид → лист.</summary>
    public (double X, double Y) ToSheet(string view, Vector2d p)
    {
        var (mid, s, x, y) = Xf[view];
        return (x + (p.X - mid.X) * s, y + (p.Y - mid.Y) * s);
    }
    /// <summary>Лист → вид.</summary>
    public Vector2d ToView(string view, double sx, double sy)
    {
        var (mid, s, x, y) = Xf[view];
        return new Vector2d(mid.X + (sx - x) / s, mid.Y + (sy - y) / s);
    }

    /// <summary>
    /// Перенос вида с проекционной связью ГОСТ 2.305: главный — вместе с проекционными (их смещение то же); вид сверху и снизу —
    /// только по вертикали; слева, справа и сзади — только по горизонтали; изометрия, разрезы и выносные — свободно.
    /// </summary>
    public void MoveView(string name, double dx, double dy)
    {
        var v = Views.FirstOrDefault(x => x.Name == name);
        if (v == null) return;
        if (v.IsSection || v.IsDetail) { v.X += dx; v.Y += dy; return; }
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
                                                     : proj.TryGetValue(v.Name, out var l) ? Box(l) : (Vector2d.Zero, Vector2d.Zero));
        DrawingView? V(string n) => Views.FirstOrDefault(v => v.Name == n);
        // размер вида на листе при масштабе листа s (свой масштаб вида — постоянен)
        Vector2d Size(string n, double s) => V(n) is { } v ? (box[n].Item2 - box[n].Item1) * (v.Scale > 0 ? v.Scale : s) : Vector2d.Zero;
        var extras = Views.Where(v => !Grid.Contains(v.Name)).ToList();
        const double gap = 20;
        double availW = x1 - x0 - 2 * gap, availH = y1 - y0 - tbH - 2 * gap;
        (double wl, double wr, double ht, double hb, double we, double he) Dims(double s)
        {
            double wl = Math.Max(Size("front", s).X, Size("top", s).X), wr = Math.Max(Size("left", s).X, Size("iso", s).X);
            double ht = Math.Max(Size("front", s).Y, Size("left", s).Y), hb = Math.Max(Size("top", s).Y, Size("iso", s).Y);
            double we = extras.Count == 0 ? 0 : extras.Max(v => Size(v.Name, s).X) + 10;   // +10 — подписи
            double he = extras.Sum(v => Size(v.Name, s).Y + 12) + Math.Max(0, extras.Count - 1) * 8;
            return (wl, wr, ht, hb, we, he);
        }
        double Fit(double s)
        {
            var (wl, wr, ht, hb, we, he) = Dims(s);
            double w = wl + (wr > 0 ? gap + wr : 0) + (we > 0 ? gap + we : 0), h = Math.Max(ht + (hb > 0 ? gap + hb : 0), he);
            return Math.Max(w / availW, h / availH);
        }
        double scale = Scale;
        if (scale <= 0) { scale = GostScales.Last(); foreach (var s in GostScales) if (Fit(s) <= 1) { scale = s; break; } }
        var (wl2, wr2, ht2, hb2, we2, he2) = Dims(scale);
        double totalW = wl2 + (wr2 > 0 ? gap + wr2 : 0) + (we2 > 0 ? gap + we2 : 0), totalH = Math.Max(ht2 + (hb2 > 0 ? gap + hb2 : 0), he2);
        double left = x0 + gap + Math.Max(0, (availW - totalW) / 2), top = y1 - gap - Math.Max(0, (availH - totalH) / 2);
        var centre = new Dictionary<string, (double X, double Y)>
        {
            ["front"] = (left + wl2 / 2, top - ht2 / 2),
            ["left"] = (left + wl2 + gap + wr2 / 2, top - ht2 / 2),
            ["top"] = (left + wl2 / 2, top - ht2 - gap - hb2 / 2),
            ["iso"] = (left + wl2 + gap + wr2 / 2, top - ht2 - gap - hb2 / 2),
        };
        double ex = left + wl2 + (wr2 > 0 ? gap + wr2 : 0) + gap + we2 / 2, ey = top;
        foreach (var v in extras)
        {
            var sz = Size(v.Name, scale);
            ey -= 12;   // подпись над видом
            centre[v.Name] = (ex, ey - sz.Y / 2);
            ey -= sz.Y + 8;
        }
        foreach (var v in Views)
            if (v.X == 0 && v.Y == 0 && centre.TryGetValue(v.Name, out var c)) { v.X = c.X; v.Y = c.Y; }
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
        fields.Auto[Fields.Scale] = ScaleText(scale);
        var g = SheetLayout.Compose(Template, fields);
        foreach (var v in Views)
        {
            if (!proj.TryGetValue(v.Name, out var lines)) continue;
            double s = v.Scale > 0 ? v.Scale : scale;
            var (mn, mx) = Box(lines);
            Vector2d mid;
            // выносной элемент — центр круга в середине вида
            if (v.IsDetail) { mid = new Vector2d(v.DetailX, v.DetailY); mn = mid - new Vector2d(v.DetailR); mx = mid + new Vector2d(v.DetailR); }
            else mid = (mn + mx) / 2;
            Xf[v.Name] = (mid, s, v.X, v.Y);
            (double, double) T(Vector2d p) => (v.X + (p.X - mid.X) * s, v.Y + (p.Y - mid.Y) * s);
            Boxes[v.Name] = (v.X + (mn.X - mid.X) * s, v.Y + (mn.Y - mid.Y) * s, v.X + (mx.X - mid.X) * s, v.Y + (mx.Y - mid.Y) * s);
            foreach (var l in lines)
            {
                if (!l.Visible && !v.ShowHidden) continue;
                if (l.Kind == ViewLineKind.Tangent && !v.ShowTangent) continue;
                var kind = !l.Visible ? LineKind.Hidden : l.Kind == ViewLineKind.Tangent ? LineKind.Thin : LineKind.Thick;
                g.Polylines.Add(new SheetPolyline(l.Points.Select(T).ToList(), kind));
            }
            // штриховка фигуры сечения ГОСТ 2.306: 45°, шаг 3 мм листа
            if (scene.SectionBoundary.TryGetValue(v.Name, out var sb) && sb.Count > 0)
                foreach (var (a, b) in SectionCut.Hatch(sb, 45, 3 / s))
                    g.Polylines.Add(new SheetPolyline(new() { T(a), T(b) }, LineKind.Thin));
            if (v.CenterLines && v.Name != "iso" && scene.Axes.TryGetValue(v.Name, out var ax)) AxisLines(g, ax, T, s);
            // подписи: «А–А», «Б (2:1)», свой масштаб «(1:2)»
            string caption = v.IsSection ? $"{v.Label}-{v.Label}" : v.IsDetail ? v.Label : "";
            if (v.Scale > 0 && Math.Abs(v.Scale - scale) > 1e-12) caption = (caption + $" ({ScaleText(v.Scale)})").Trim();
            if (caption.Length > 0) g.Text(v.X, Boxes[v.Name].Y1 + 5, v.IsSection || v.IsDetail ? 7 : SheetLayout.ValueH, caption, TextAlign.Center);
        }
        // линии разреза и круги выносных элементов — на виде-родителе
        foreach (var v in Views.Where(v => (v.IsSection || v.IsDetail) && v.Parent != null && Xf.ContainsKey(v.Parent!)))
        {
            if (v.IsSection) CutLine(g, v);
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
        if (ShowDims) foreach (var d in Dims.Where(d => !d.Hidden && Xf.ContainsKey(d.View))) DrawDim(g, d);
        foreach (var n in Notes)
        {
            if (!ShowGdt && n.Kind is "gdt" or "datum") continue;
            DrawNote(g, n, scene);
        }
        return g;
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
        foreach (var (te, sgn) in new[] { (t0, 1.0), (t1, -1.0) })
        {
            var e0 = ToSheet(parent.Name, lineAt + dir * te); var e1 = ToSheet(parent.Name, lineAt + dir * (te + sgn * 10 / s));
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
        string text = d.Caption(Comma);
        if (d.Kind is "dia" or "rad") { DrawRadial(g, d, text); return; }
        var p1 = ToSheet(d.View, new Vector2d(d.X1, d.Y1)); var p2 = ToSheet(d.View, new Vector2d(d.X2, d.Y2));
        Vector2d P1 = new(p1.X, p1.Y), P2 = new(p2.X, p2.Y);
        Vector2d u = d.Kind == "h" ? Vector2d.UnitX : d.Kind == "v" ? Vector2d.UnitY : (P2 - P1).Length > 1e-9 ? (P2 - P1).Normalized() : Vector2d.UnitX;
        var n = new Vector2d(-u.Y, u.X);
        double h1 = Vector2d.Dot(P1, n), h2 = Vector2d.Dot(P2, n);
        double L = (d.Offset >= 0 ? Math.Max(h1, h2) : Math.Min(h1, h2)) + d.Offset;
        var q1 = P1 + n * (L - h1); var q2 = P2 + n * (L - h2);
        double sg = Math.Sign(d.Offset == 0 ? 1 : d.Offset);
        // выносные линии: от точки до размерной + 2 мм за неё
        if (Math.Abs(L - h1) > 0.5) g.Line(P1.X + n.X * sg, P1.Y + n.Y * sg, q1.X + n.X * sg * 2, q1.Y + n.Y * sg * 2);
        if (Math.Abs(L - h2) > 0.5) g.Line(P2.X + n.X * sg, P2.Y + n.Y * sg, q2.X + n.X * sg * 2, q2.Y + n.Y * sg * 2);
        double len = (q2 - q1).Length;
        var along = len > 1e-9 ? (q2 - q1) / len : u;
        bool outside = len < 2 * ArrowL + 4;
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
        if (outside && len < TextW(text, TextH)) m += (q2 - q1).Length > 1e-9 ? along * (len / 2 + 8 + TextW(text, TextH) / 2) : Vector2d.Zero;
        g.Text(m.X, m.Y, TextH, text, TextAlign.Center, null, Math.Abs(ang) < 1e-9 ? 0 : ang);
    }

    void DrawRadial(SheetGraphics g, DrawingDim d, string text)
    {
        var c = ToSheet(d.View, new Vector2d(d.X1, d.Y1));
        double r = d.X2 * Xf[d.View].S, a = d.Y2 * Math.PI / 180;
        var dir = (Math.Cos(a), Math.Sin(a));
        var p = (c.X + r * dir.Item1, c.Y + r * dir.Item2);
        double tw = TextW(text, TextH);
        if (d.Kind == "dia" && r > 12)
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
        // выноска с полкой: от окружности наружу 6 мм, полка по длине текста
        var k = (p.Item1 + dir.Item1 * 6, p.Item2 + dir.Item2 * 6);
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
    public void AutoDimensions(DrawingScene scene)
    {
        var hidden = Dims.Where(d => d.Auto && d.Hidden).Select(Key).ToHashSet();
        Dims.RemoveAll(d => d.Auto);
        void Add(DrawingDim d) { d.Auto = true; d.Hidden = hidden.Contains(Key(d)); Dims.Add(d); }
        foreach (var v in Views.Where(v => !v.IsDetail && v.Name != "iso"))
        {
            if (!scene.Lines.TryGetValue(v.Name, out var lines) || lines.Count == 0) continue;
            var (mn, mx) = Box(lines);
            if (v.Name == "front" || v.IsSection && !Views.Any(x => x.Name == "front"))
            {
                Add(new DrawingDim { View = v.Name, Kind = "h", X1 = mn.X, Y1 = mn.Y, X2 = mx.X, Y2 = mn.Y, Offset = -10 });
                Add(new DrawingDim { View = v.Name, Kind = "v", X1 = mx.X, Y1 = mn.Y, X2 = mx.X, Y2 = mx.Y, Offset = 10 });
            }
            if (v.Name == "top")
                Add(new DrawingDim { View = v.Name, Kind = "v", X1 = mn.X, Y1 = mn.Y, X2 = mn.X, Y2 = mx.Y, Offset = -10 });
            if (v.Name == "left" && !Views.Any(x => x.Name == "top"))
                Add(new DrawingDim { View = v.Name, Kind = "h", X1 = mn.X, Y1 = mn.Y, X2 = mx.X, Y2 = mn.Y, Offset = -10 });
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
                dd.Model = true;
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

    /// <summary>D5: выносной элемент: круг на виде-родителе (центр и радиус — мм модели), масштаб — в 2 раза крупнее листа.</summary>
    public DrawingView AddDetail(string parent, Vector2d centre, double radius, double scale)
    {
        var letter = NextLetter();
        var v = new DrawingView { Name = letter, IsDetail = true, Parent = parent, Label = letter, DetailX = centre.X, DetailY = centre.Y, DetailR = radius, Scale = scale, ShowHidden = false };
        Views.Add(v);
        return v;
    }
}
