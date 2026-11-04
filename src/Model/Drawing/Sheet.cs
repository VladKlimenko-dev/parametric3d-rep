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
    public void Poly(LineKind k, params (double X, double Y)[] pts) => Polylines.Add(new SheetPolyline(pts.ToList(), k));
    public readonly List<SheetText> Texts = new();
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
