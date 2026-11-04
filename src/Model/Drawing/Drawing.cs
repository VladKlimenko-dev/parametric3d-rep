using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Drawing;

/// <summary>Вид чертежа: направление (front, top, left, right, bottom, back, iso), место центра на листе (мм), масштаб.</summary>
public sealed class DrawingView
{
    public string Name { get; set; } = "front";
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Масштаб вида; 0 — масштаб листа.</summary>
    public double Scale { get; set; }
    public bool ShowHidden { get; set; } = true;
    public bool ShowTangent { get; set; }
    /// <summary>Осевые линии у очерков тел вращения (оси отверстий и валов).</summary>
    public bool CenterLines { get; set; } = true;
}

/// <summary>
/// k155 (D2, начало): ЧЕРТЁЖ (.p3drw) — деталь из файла (или открытая), шаблон листа, виды. Масштаб листа — из ряда ГОСТ
/// 2.302; раскладка видов по ГОСТ 2.305 (метод первого угла): главный вид, вид сверху — под ним, вид слева — справа от него,
/// изометрия — в свободном углу. Линии видов — проекция со скрытием (ViewProjection): видимый контур — сплошная толстая,
/// невидимый — штриховая, линии перехода — тонкая (по выбору).
/// </summary>
public sealed class DrawingDocument
{
    public int Version { get; set; } = 1;
    /// <summary>Файл детали (.p3d); пусто — открытая деталь.</summary>
    public string? SourcePath { get; set; }
    public SheetTemplate Template { get; set; } = SheetLayout.Default("A3", true);
    /// <summary>Масштаб листа (1 — 1:1, 0.5 — 1:2, 2 — 2:1); 0 — подобрать из ряда ГОСТ 2.302.</summary>
    public double Scale { get; set; }
    public List<DrawingView> Views { get; set; } = new();
    /// <summary>Габариты видов на листе после Compose (мм листа) — для выбора и переноса вида мышью.</summary>
    [JsonIgnore] public Dictionary<string, (double X0, double Y0, double X1, double Y1)> Boxes { get; } = new();

    /// <summary>
    /// Перенос вида с проекционной связью ГОСТ 2.305: главный — вместе с проекционными (их смещение то же); вид сверху и снизу —
    /// только по вертикали; слева, справа и сзади — только по горизонтали; изометрия — свободно.
    /// </summary>
    public void MoveView(string name, double dx, double dy)
    {
        var v = Views.FirstOrDefault(x => x.Name == name);
        if (v == null) return;
        switch (name)
        {
            case "front":
                foreach (var x in Views.Where(x => x.Name != "iso")) { x.X += dx; x.Y += dy; }
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

    /// <summary>Проекции тел на вид (мм модели) — один раз на вид, масштаб не влияет.</summary>
    public static List<ViewLine> ProjectBodies(IEnumerable<TSolid> bodies, string view, double chord = 0.02)
    {
        var frame = ViewFrame.Of(view);
        var res = new List<ViewLine>();
        foreach (var b in bodies) res.AddRange(ViewProjection.Project(b, frame, chord));
        return res;
    }

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

    /// <summary>
    /// Раскладка: масштаб листа (если 0 — наибольший из ряда, при котором виды с промежутками 20 мм помещаются в рамку над
    /// основной надписью) и центры видов по ГОСТ 2.305 (у видов с X = Y = 0). Возвращает выбранный масштаб.
    /// </summary>
    public double Layout(Dictionary<string, List<ViewLine>> proj)
    {
        var (x0, y0, x1, y1) = SheetLayout.FrameRect(Template);
        double tbH = SheetLayout.TitleHeight(Template.TitleBlock);
        var box = Views.ToDictionary(v => v.Name, v => Box(proj[v.Name]));
        Vector2d Size(string n) => box.TryGetValue(n, out var b) ? b.Max - b.Min : Vector2d.Zero;
        bool has(string n) => Views.Any(v => v.Name == n);
        const double gap = 20;
        double availW = x1 - x0 - 2 * gap, availH = y1 - y0 - tbH - 2 * gap;
        double Fit(double s)
        {
            // сетка 2×2: [главный | слева] над [сверху | изометрия]
            double wL = Math.Max(Size("front").X, has("top") ? Size("top").X : 0) * s;
            double wR = Math.Max(has("left") ? Size("left").X : 0, has("iso") ? Size("iso").X : 0) * s;
            double hT = Math.Max(Size("front").Y, has("left") ? Size("left").Y : 0) * s;
            double hB = Math.Max(has("top") ? Size("top").Y : 0, has("iso") ? Size("iso").Y : 0) * s;
            double w = wL + (wR > 0 ? gap + wR : 0), h = hT + (hB > 0 ? gap + hB : 0);
            return Math.Max(w / availW, h / availH);
        }
        double scale = Scale;
        if (scale <= 0) { scale = GostScales.Last(); foreach (var s in GostScales) if (Fit(s) <= 1) { scale = s; break; } }
        // центры: колонки и строки сетки
        double wl = Math.Max(Size("front").X, has("top") ? Size("top").X : 0) * scale;
        double wr = Math.Max(has("left") ? Size("left").X : 0, has("iso") ? Size("iso").X : 0) * scale;
        double ht = Math.Max(Size("front").Y, has("left") ? Size("left").Y : 0) * scale;
        double hb = Math.Max(has("top") ? Size("top").Y : 0, has("iso") ? Size("iso").Y : 0) * scale;
        double totalW = wl + (wr > 0 ? gap + wr : 0), totalH = ht + (hb > 0 ? gap + hb : 0);
        double left = x0 + gap + Math.Max(0, (availW - totalW) / 2), top = y1 - gap - Math.Max(0, (availH - totalH) / 2);
        var centre = new Dictionary<string, (double X, double Y)>
        {
            ["front"] = (left + wl / 2, top - ht / 2),
            ["left"] = (left + wl + gap + wr / 2, top - ht / 2),
            ["top"] = (left + wl / 2, top - ht - gap - hb / 2),
            ["iso"] = (left + wl + gap + wr / 2, top - ht - gap - hb / 2),
        };
        foreach (var v in Views)
            if (v.X == 0 && v.Y == 0 && centre.TryGetValue(v.Name, out var c)) { v.X = c.X; v.Y = c.Y; }
        return scale;
    }

    /// <summary>
    /// Лист чертежа: шаблон (надпись: масштаб листа, поля детали) и линии видов. Проекционная связь ГОСТ 2.305: главный вид и
    /// вид слева — центры на одной высоте по середине габарита главного вида, вид сверху — под главным по той же вертикали
    /// (центр вида — середина габарита проекции).
    /// </summary>
    public SheetGraphics Compose(Dictionary<string, List<ViewLine>> proj, FieldSource fields, Dictionary<string, List<ViewAxis>>? axes = null)
    {
        Boxes.Clear();
        double scale = Layout(proj);
        fields.Auto[Fields.Scale] = ScaleText(scale);
        var g = SheetLayout.Compose(Template, fields);
        foreach (var v in Views)
        {
            if (!proj.TryGetValue(v.Name, out var lines)) continue;
            double s = v.Scale > 0 ? v.Scale : scale;
            var (mn, mx) = Box(lines);
            var mid = (mn + mx) / 2;
            (double, double) T(Vector2d p) => (v.X + (p.X - mid.X) * s, v.Y + (p.Y - mid.Y) * s);
            Boxes[v.Name] = (v.X + (mn.X - mid.X) * s, v.Y + (mn.Y - mid.Y) * s, v.X + (mx.X - mid.X) * s, v.Y + (mx.Y - mid.Y) * s);
            foreach (var l in lines)
            {
                if (!l.Visible && !v.ShowHidden) continue;
                if (l.Kind == ViewLineKind.Tangent && !v.ShowTangent) continue;
                var kind = !l.Visible ? LineKind.Hidden : l.Kind == ViewLineKind.Tangent ? LineKind.Thin : LineKind.Thick;
                g.Polylines.Add(new SheetPolyline(l.Points.Select(T).ToList(), kind));
            }
            if (v.CenterLines && v.Name != "iso" && axes != null && axes.TryGetValue(v.Name, out var ax)) AxisLines(g, ax, T, s);
            if (v.Scale > 0 && Math.Abs(v.Scale - scale) > 1e-12) g.Text(v.X, v.Y + (mx.Y - mid.Y) * s + 5, SheetLayout.ValueH, $"({ScaleText(v.Scale)})", TextAlign.Center);
        }
        return g;
    }

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

}
