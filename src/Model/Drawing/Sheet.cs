using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parametric3D.Model.Drawing;

/// <summary>
/// k155 (D1, DRAWING_MODULE.md): ЛИСТ — ПРИМИТИВЫ ВЫВОДА. Всё, что попадает на лист (рамка, основная надпись, таблицы,
/// текст), — отрезки и строки в миллиметрах листа (начало — левый нижний угол, Y вверх). Один набор примитивов рисуется
/// окном листа и пишется в PDF — что на экране, то и на бумаге.
/// </summary>
/// <summary>Линии ГОСТ 2.303: сплошная толстая основная, сплошная тонкая, штриховая (невидимый контур), штрихпунктирная тонкая (осевая).</summary>
public enum LineKind { Thin, Thick, Hidden, Center }
public enum TextAlign { Left, Center, Right }

public sealed record SheetLine(double X1, double Y1, double X2, double Y2, LineKind Kind);
/// <summary>Строка: точка привязки (по выравниванию — левый, средний или правый край), высота шрифта по ГОСТ 2.304 (мм).</summary>
public sealed record SheetText(double X, double Y, double Height, string Text, TextAlign Align = TextAlign.Left, string? Field = null, double Angle = 0);

/// <summary>Ломаная листа (линии видов): штрихи штриховой и штрихпунктирной идут по всей ломаной, а не по отрезкам.</summary>
public sealed record SheetPolyline(List<(double X, double Y)> Points, LineKind Kind);

public sealed class SheetGraphics
{
    public double Width, Height;
    public readonly List<SheetLine> Lines = new();
    public readonly List<SheetPolyline> Polylines = new();
    /// <summary>k156: залитые многоугольники (стрелки размеров, точки выносок, треугольник базы).</summary>
    public readonly List<List<(double X, double Y)>> Fills = new();
    /// <summary>k156: окружности (контур тонкой или толстой линией): выноски ISO, знак базы, выносной элемент.</summary>
    public readonly List<(double X, double Y, double R, LineKind Kind)> Circles = new();
    /// <summary>k161: толщина сплошной толстой основной линии S (мм; ГОСТ 2.303: 0.5–1.4). Тонкие — S/3…S/2, штриховая — S/2.</summary>
    public double ThickW = LineWidths.Thick;
    // k165: штриховая не толще основной (S от 0.1 мм)
    public double WidthOf(LineKind k) => k switch { LineKind.Thick => ThickW, LineKind.Hidden => Math.Min(ThickW, Math.Max(LineWidths.Thin, ThickW / 2)), _ => Math.Min(LineWidths.Thin, ThickW / 2) };
    /// <summary>k161: цветные многоугольники (закраска изометрии: тон грани), рисуются под линиями. Цвет — ARGB.</summary>
    public readonly List<(List<(double X, double Y)> Pts, uint Argb)> Shades = new();
    public void Poly(LineKind k, params (double X, double Y)[] pts) => Polylines.Add(new SheetPolyline(pts.ToList(), k));
    public readonly List<SheetText> Texts = new();
    /// <summary>k170: серый слой поверх листа (предлагаемый размер проверки полноты).</summary>
    public SheetGraphics? Ghost;
    public void Line(double x1, double y1, double x2, double y2, LineKind k = LineKind.Thin) => Lines.Add(new(x1, y1, x2, y2, k));
    public void Rect(double x, double y, double w, double h, LineKind k = LineKind.Thin)
    { Line(x, y, x + w, y, k); Line(x + w, y, x + w, y + h, k); Line(x + w, y + h, x, y + h, k); Line(x, y + h, x, y, k); }
    public void Text(double x, double y, double h, string t, TextAlign a = TextAlign.Left, string? field = null, double angle = 0) { if (!string.IsNullOrEmpty(t) || field != null) Texts.Add(new(x, y, h, t, a, field, angle)); }
    /// <summary>Текст по центру ячейки (x, y — левый нижний угол ячейки).</summary>
    public void CellText(double x, double y, double w, double h, double th, string t, string? field = null) => Text(x + w / 2, y + (h - th) / 2, th, t, TextAlign.Center, field);
    /// <summary>Текст по левому краю ячейки с отступом 1 мм.</summary>
    public void CellLeft(double x, double y, double w, double h, double th, string t, string? field = null) => Text(x + 1, y + (h - th) / 2, th, t, TextAlign.Left, field);
    /// <summary>Вертикальный текст (поворот 90° против часовой) по центру ячейки.</summary>
    public void CellVertical(double x, double y, double w, double h, double th, string t) => Text(x + (w + th) / 2, y + h / 2, th, t, TextAlign.Center, null, 90);
    /// <summary>Поле листа, записанное к экрану: всё на листе в границах (проверка раскладки).</summary>
    public bool InBounds(double eps = 1e-6) => Lines.All(l => Math.Min(l.X1, l.X2) >= -eps && Math.Max(l.X1, l.X2) <= Width + eps && Math.Min(l.Y1, l.Y2) >= -eps && Math.Max(l.Y1, l.Y2) <= Height + eps)
        && Polylines.All(pl => pl.Points.All(q => q.X >= -eps && q.X <= Width + eps && q.Y >= -eps && q.Y <= Height + eps));
}

/// <summary>Толщины линий по ГОСТ 2.303: сплошная основная s = 0.7 мм, тонкая — s/3.</summary>
public static class LineWidths
{
    public const double Thick = 0.7, Thin = 0.25, Hidden = 0.35;
    public static double Of(LineKind k) => k switch { LineKind.Thick => Thick, LineKind.Hidden => Hidden, _ => Thin };
    /// <summary>Штрихи (мм): штриховая — штрих 4, промежуток 1.5; штрихпунктирная — штрих 15, промежуток 2, точка (штрих 0.5).</summary>
    public static double[]? Dash(LineKind k) => k switch { LineKind.Hidden => new[] { 4.0, 1.5 }, LineKind.Center => new[] { 15.0, 2, 0.5, 2 }, _ => null };
}

/// <summary>Форматы листов ГОСТ 2.301 (ISO 5457): основные A0–A4 и дополнительные (кратные короткой стороне).</summary>
public static class SheetFormats
{
    public static readonly (string Code, double W, double H)[] All =
    {
        ("A0", 841, 1189), ("A1", 594, 841), ("A2", 420, 594), ("A3", 297, 420), ("A4", 210, 297),
        ("A4x3", 297, 630), ("A4x4", 297, 841), ("A4x5", 297, 1051), ("A3x3", 420, 891), ("A3x4", 420, 1189),
        ("A2x3", 594, 1261), ("A1x3", 841, 1783),
    };
    /// <summary>Размер листа (ширина × высота) в ориентации: книжная — короткая сторона по ширине.</summary>
    public static (double W, double H) Size(string code, bool landscape)
    {
        var f = All.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        if (f.Code == null) throw new ArgumentException($"unknown sheet format '{code}'");
        double s = Math.Min(f.W, f.H), l = Math.Max(f.W, f.H);
        return landscape ? (l, s) : (s, l);
    }
}

public enum SheetStandard { Eskd, Iso }
/// <summary>Основная надпись: ГОСТ 2.104 форма 1 (чертёж, 185×55), форма 2 (текстовый документ, первый лист, 185×40), форма 2а
/// (последующие листы, 185×15); ISO 7200 (180×≈30).</summary>
public enum TitleBlockForm { Form1, Form2, Form2a, Iso7200 }

/// <summary>Свободный элемент шаблона: текст, линия или таблица (координаты — мм от левого нижнего угла листа).</summary>
public sealed class TemplateItem
{
    public string Kind { get; set; } = "text";        // text | line | table
    public double X { get; set; }
    public double Y { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double Height { get; set; } = 5;
    public string Text { get; set; } = "";
    public bool Thick { get; set; }
    /// <summary>Таблица: ширины колонок и число строк (высота строки — Height); тексты ячеек — по строкам.</summary>
    public List<double>? Columns { get; set; }
    public int Rows { get; set; }
    public List<List<string>>? Cells { get; set; }
    /// <summary>k168 (замечание 05.11, 16:17, п. 10): элемент скрыт (кнопка «Таблица на листе» — переключатель видимости).</summary>
    public bool Hidden { get; set; }
    /// <summary>k168 (п. 11): название таблицы (над таблицей).</summary>
    public string Title { get; set; } = "";
    /// <summary>
    /// k168 (п. 14): к чему прилипла таблица: tr / tl / br / bl — угол рамки, title — над основной надписью справа; пусто —
    /// свободно. При смене формата и стандарта место пересчитывается.
    /// </summary>
    public string Snap { get; set; } = "";

    public double TableW => (Columns ?? new List<double> { 50 }).Sum();
    public double RowH => Height <= 0 ? 8 : Height;
    public double TableH => Math.Max(1, Rows) * RowH;

    /// <summary>k168: ячейки — по числу строк и столбцов (недостающие — пустые).</summary>
    public void NormalizeCells()
    {
        Columns ??= new List<double> { 50 };
        Cells ??= new();
        while (Cells.Count < Rows) Cells.Add(new());
        if (Cells.Count > Rows) Cells.RemoveRange(Rows, Cells.Count - Rows);
        foreach (var r in Cells) { while (r.Count < Columns.Count) r.Add(""); if (r.Count > Columns.Count) r.RemoveRange(Columns.Count, r.Count - Columns.Count); }
    }
    public void InsertRow(int at, List<string>? cells = null)
    {
        NormalizeCells(); at = Math.Clamp(at, 0, Rows);
        var row = Enumerable.Range(0, Columns!.Count).Select(i => cells != null && i < cells.Count ? cells[i] : "").ToList();
        Cells!.Insert(at, row); Rows++;
    }
    public void DeleteRow(int r) { NormalizeCells(); if (Rows <= 1 || r < 0 || r >= Rows) return; Cells!.RemoveAt(r); Rows--; }
    public void InsertColumn(int at, List<string>? cells = null, double width = 30)
    {
        NormalizeCells(); at = Math.Clamp(at, 0, Columns!.Count);
        Columns.Insert(at, width);
        for (int r = 0; r < Rows; r++) Cells![r].Insert(at, cells != null && r < cells.Count ? cells[r] : "");
    }
    public void DeleteColumn(int c) { NormalizeCells(); if (Columns!.Count <= 1 || c < 0 || c >= Columns.Count) return; Columns.RemoveAt(c); foreach (var r in Cells!) r.RemoveAt(c); }
    /// <summary>Ячейка (строка, столбец) в точке листа; -1 — вне таблицы.</summary>
    public (int Row, int Col) CellAt(double x, double y)
    {
        if (x < X || x > X + TableW || y > Y || y < Y - TableH) return (-1, -1);
        int r = Math.Clamp((int)((Y - y) / RowH), 0, Math.Max(1, Rows) - 1);
        double cx = X; var cols = Columns ?? new List<double> { 50 };
        for (int c = 0; c < cols.Count; c++) { if (x <= cx + cols[c]) return (r, c); cx += cols[c]; }
        return (r, cols.Count - 1);
    }
    public double ColX(int c) { double cx = X; var cols = Columns ?? new List<double> { 50 }; for (int i = 0; i < c && i < cols.Count; i++) cx += cols[i]; return cx; }
}

/// <summary>
/// k155 (D1): ШАБЛОН ЛИСТА (.p3dtpl) — формат и ориентация, стандарт оформления (ЕСКД / ISO), рамка, основная надпись,
/// поля автозаполнения (обозначение, наименование, материал, масса, масштаб, лист, листов, разработал, проверил,
/// организация, изменение, дата — из свойств детали или вручную), свободные тексты, линии и таблицы (технические требования,
/// таблица изменений и т. п.).
/// </summary>
public sealed class SheetTemplate
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Шаблон";
    public string Format { get; set; } = "A4";
    public bool Landscape { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public SheetStandard Standard { get; set; } = SheetStandard.Eskd;
    [JsonConverter(typeof(JsonStringEnumConverter))] public TitleBlockForm TitleBlock { get; set; } = TitleBlockForm.Form1;
    /// <summary>Значения полей основной надписи, заданные вручную (имя поля — Fields.*); пустое — из свойств детали.</summary>
    public Dictionary<string, string> Values { get; set; } = new();
    public List<TemplateItem> Items { get; set; } = new();
    /// <summary>k168 (замечание 05.11, 16:17, п. 12): язык надписей листа: ru, en; пусто — по стандарту (ЕСКД — ru, ISO — en).</summary>
    public string Language { get; set; } = "";
    public string Lang => Language.Length > 0 ? Language : Standard == SheetStandard.Iso ? "en" : "ru";
    /// <summary>k169 (п. 17): система единиц чертежа: «mm/kg/s», «in/lb/s» … (длина — первая).</summary>
    public string Units { get; set; } = "mm/kg/s";

    /// <summary>k169: копия шаблона (через JSON).</summary>
    public SheetTemplate Clone() => FromJson(ToJson());
    /// <summary>k169 (п. 17): имя шаблона по параметрам: «ЕСКД mm/kg/s A3 LS».</summary>
    public string AutoName => $"{(Standard == SheetStandard.Iso ? "ISO" : "ЕСКД")} {Units} {Format} {(Landscape ? "LS" : "PT")}";

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, Opts);
    public static SheetTemplate FromJson(string json) => JsonSerializer.Deserialize<SheetTemplate>(json, Opts) ?? throw new InvalidDataException("not a sheet template");
    public void Save(string path) => File.WriteAllText(path, ToJson());
    public static SheetTemplate Load(string path) => FromJson(File.ReadAllText(path));

    public (double W, double H) Size => SheetFormats.Size(Format, Landscape);
}

/// <summary>Поля основной надписи (имена — ключи значений шаблона и источника данных).</summary>
public static class Fields
{
    public const string Designation = "designation", Name = "name", Material = "material", Mass = "mass", Scale = "scale",
        Sheet = "sheet", Sheets = "sheets", Developer = "developer", Checker = "checker", TechControl = "tech_control",
        NormControl = "norm_control", Approver = "approver", Organization = "organization", Revision = "revision", Date = "date",
        Litera = "litera";
    public static readonly string[] All = { Designation, Name, Material, Mass, Scale, Sheet, Sheets, Developer, Checker, TechControl, NormControl, Approver, Organization, Revision, Date, Litera };
}

/// <summary>Значения полей: ручные (шаблон) поверх автоматических (свойства детали или сборки, номер листа).</summary>
public sealed class FieldSource
{
    public readonly Dictionary<string, string> Auto = new();
    public static FieldSource FromPart(Part? p, int sheet = 1, int sheets = 1)
    {
        var s = new FieldSource();
        s.Auto[Fields.Sheet] = sheet.ToString(CultureInfo.InvariantCulture);
        s.Auto[Fields.Sheets] = sheets.ToString(CultureInfo.InvariantCulture);
        s.Auto[Fields.Date] = DateTime.Now.ToString("dd.MM.yy", CultureInfo.InvariantCulture);
        if (p != null)
        {
            s.Auto[Fields.Name] = string.IsNullOrEmpty(p.Info.Title) ? p.Name : p.Info.Title;
            if (!string.IsNullOrEmpty(p.Info.Code)) s.Auto[Fields.Designation] = p.Info.Code;
            if (!string.IsNullOrEmpty(p.Info.Author)) s.Auto[Fields.Developer] = p.Info.Author;
            if (!string.IsNullOrEmpty(p.Info.Checker)) s.Auto[Fields.Checker] = p.Info.Checker;
            if (!string.IsNullOrEmpty(p.Info.Company)) s.Auto[Fields.Organization] = p.Info.Company;
            if (!string.IsNullOrEmpty(p.Info.Revision)) s.Auto[Fields.Revision] = p.Info.Revision;
            if (p.Material?.Name is { Length: > 0 } mn) s.Auto[Fields.Material] = mn;
            double m = p.MassByBodies();
            if (m > 0) s.Auto[Fields.Mass] = m.ToString("0.###", CultureInfo.InvariantCulture);   // кг (MassByBodies — кг)
        }
        return s;
    }
    public string Get(SheetTemplate t, string field) =>
        t.Values.TryGetValue(field, out var v) && v.Length > 0 ? v : Auto.TryGetValue(field, out var a) ? a : "";
}
