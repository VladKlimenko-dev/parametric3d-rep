namespace Parametric3D.Model.Drawing;

/// <summary>
/// k155 (D1): РАСКЛАДКА ЛИСТА ПО ШАБЛОНУ — рамка (ГОСТ 2.104: поля 20/5 мм; ISO 5457: 20/10 мм и метки центрирования),
/// дополнительные графы на поле подшивки (ЕСКД: инв. № подлинника, подпись и дата, взам. инв. №, инв. № дубликата),
/// основная надпись (ГОСТ 2.104 формы 1, 2, 2а; ISO 7200) в правом нижнем углу рамки, свободные элементы шаблона.
/// Все размеры — мм; начало — левый нижний угол листа.
/// </summary>
public static class SheetLayout
{
    public const double LabelH = 2.5, ValueH = 3.5, BigH = 5;

    /// <summary>Рамка: внутренний прямоугольник (x0, y0, x1, y1).</summary>
    public static (double X0, double Y0, double X1, double Y1) FrameRect(SheetTemplate t)
    {
        var (w, h) = t.Size;
        return t.Standard == SheetStandard.Iso ? (20, 10, w - 10, h - 10) : (20, 5, w - 5, h - 5);
    }

    /// <summary>Высота основной надписи формы (мм).</summary>
    public static double TitleHeight(TitleBlockForm f) => f switch { TitleBlockForm.Form1 => 55, TitleBlockForm.Form2 => 40, TitleBlockForm.Form2a => 15, _ => 36 };
    public static double TitleWidth(TitleBlockForm f) => f == TitleBlockForm.Iso7200 ? 180 : 185;

    /// <summary>Лист по шаблону: рамка, графы, основная надпись с полями, элементы шаблона.</summary>
    public static SheetGraphics Compose(SheetTemplate t, FieldSource src, TitleBlockForm? form = null)
    {
        var (w, h) = t.Size;
        var g = new SheetGraphics { Width = w, Height = h };
        var (x0, y0, x1, y1) = FrameRect(t);
        g.Rect(x0, y0, x1 - x0, y1 - y0, LineKind.Thick);
        var f = form ?? t.TitleBlock;
        string V(string field) => src.Get(t, field);
        if (t.Standard == SheetStandard.Iso)
        {
            // метки центрирования ISO 5457: середины сторон, от края листа до рамки
            g.Line(w / 2, 0, w / 2, y0, LineKind.Thick); g.Line(w / 2, y1, w / 2, h, LineKind.Thick);
            g.Line(0, h / 2, x0, h / 2, LineKind.Thick); g.Line(x1, h / 2, w, h / 2, LineKind.Thick);
            Iso7200(g, x1 - 180, y0, V);
        }
        else
        {
            SideColumns(g, y0);
            double bx = x1 - 185, by = y0;
            switch (f)
            {
                case TitleBlockForm.Form1: Form1(g, bx, by, V); break;
                case TitleBlockForm.Form2: Form2(g, bx, by, V); break;
                case TitleBlockForm.Form2a: Form2a(g, bx, by, V); break;
                default: Iso7200(g, x1 - 180, y0, V); break;
            }
        }
        // k168 (п. 12): подписи рамки и основной надписи — на языке чертежа (значения полей — как есть)
        string lang = t.Lang;
        for (int i = 0; i < g.Texts.Count; i++)
        {
            var tx = g.Texts[i];
            if (tx.Field != null) continue;
            string tr = Tr(tx.Text, lang);
            if (tr == tx.Text && !TrOff)
                foreach (var (ru, en) in new[] { ("Листов ", "Sheets "), ("Лист ", "Sheet ") })
                {
                    if (lang == "en" && tx.Text.StartsWith(ru)) { tr = en + tx.Text[ru.Length..]; break; }
                    if (lang == "ru" && tx.Text.StartsWith(en)) { tr = ru + tx.Text[en.Length..]; break; }
                }
            if (tr != tx.Text) g.Texts[i] = tx with { Text = tr };
        }
        foreach (var it in t.Items) Item(g, it, lang);
        return g;
    }

    /// <summary>Дополнительные графы 19–23 ГОСТ 2.104 на поле подшивки (x 8…20 мм): подпись 5 мм, значение 7 мм.</summary>
    static void SideColumns(SheetGraphics g, double y0)
    {
        (string L, double H)[] rows = { ("Инв. № подл.", 25), ("Подп. и дата", 35), ("Взам. инв. №", 25), ("Инв. № дубл.", 25), ("Подп. и дата", 35) };
        double y = y0;
        foreach (var (l, hh) in rows)
        {
            g.Rect(8, y, 5, hh, LineKind.Thick); g.Rect(13, y, 7, hh, LineKind.Thick);
            g.CellVertical(8, y, 5, hh, LabelH, l);
            y += hh;
        }
    }

    /// <summary>Левая часть форм 1, 2, 2а (65 мм: колонки 7/10/23/15/10): строки изменений, строка заголовков, строки подписей.</summary>
    static void LeftPart(SheetGraphics g, double bx, double by, int changeRows, string[] signs, Func<string, string> v)
    {
        double[] cx = { 0, 7, 17, 40, 55, 65 };
        int n = changeRows + 1 + signs.Length;
        double top = by + n * 5;
        // строки подписей (снизу вверх — в обратном порядке списка)
        for (int i = 0; i < signs.Length; i++)
        {
            double y = by + (signs.Length - 1 - i) * 5;
            g.Line(bx, y + 5, bx + 65, y + 5, i == 0 ? LineKind.Thick : LineKind.Thin);
            g.CellLeft(bx, y, 17, 5, LabelH, signs[i]);
            string field = signs[i] switch { "Разраб." => Fields.Developer, "Пров." => Fields.Checker, "Т.контр." => Fields.TechControl, "Н.контр." => Fields.NormControl, "Утв." => Fields.Approver, _ => "" };
            if (field.Length > 0) g.CellLeft(bx + 17, y, 23, 5, ValueH, v(field), field);
            if (signs[i] == "Разраб.") g.CellText(bx + 55, y, 10, 5, LabelH, v(Fields.Date), Fields.Date);
        }
        double hy = by + signs.Length * 5;   // строка заголовков
        string[] heads = { "Изм.", "Лист", "№ докум.", "Подп.", "Дата" };
        for (int c = 0; c < 5; c++) g.CellText(bx + cx[c], hy, cx[c + 1] - cx[c], 5, LabelH, heads[c]);
        g.Line(bx, hy + 5, bx + 65, hy + 5, LineKind.Thick);
        for (int r = 1; r <= changeRows; r++) if (r < changeRows) g.Line(bx, hy + 5 + r * 5, bx + 65, hy + 5 + r * 5);
        if (changeRows > 0 && v(Fields.Revision) is { Length: > 0 } rev) g.CellText(bx, hy + 5, 7, 5, ValueH, rev, Fields.Revision);
        // вертикали: в зоне изменений и заголовков — все пять колонок; в зоне подписей — 17, 40, 55
        for (int c = 1; c < 5; c++) g.Line(bx + cx[c], hy, bx + cx[c], top, LineKind.Thick);
        foreach (double x in new double[] { 17, 40, 55 }) g.Line(bx + x, by, bx + x, hy, LineKind.Thick);
        g.Line(bx + 65, by, bx + 65, top, LineKind.Thick);
    }

    /// <summary>ГОСТ 2.104 форма 1 — 185×55 (первый лист чертежа).</summary>
    static void Form1(SheetGraphics g, double bx, double by, Func<string, string> v)
    {
        g.Rect(bx, by, 185, 55, LineKind.Thick);
        LeftPart(g, bx, by, 4, new[] { "Разраб.", "Пров.", "Т.контр.", "", "Н.контр.", "Утв." }, v);
        double r = bx + 65;
        g.Line(r, by + 40, bx + 185, by + 40, LineKind.Thick);                       // под обозначением
        g.CellText(r, by + 40, 120, 15, 7, v(Fields.Designation), Fields.Designation);
        g.Line(r + 70, by, r + 70, by + 40, LineKind.Thick);
        g.Line(r, by + 15, r + 70, by + 15, LineKind.Thick);                          // наименование / материал
        g.CellText(r, by + 15, 70, 25, BigH, v(Fields.Name), Fields.Name);
        g.CellText(r, by, 70, 15, BigH, v(Fields.Material), Fields.Material);
        double q = r + 70;                                                            // правая часть 50 мм
        g.Line(q, by + 35, q + 50, by + 35, LineKind.Thick);
        g.Line(q, by + 20, q + 50, by + 20, LineKind.Thick);
        g.Line(q, by + 15, q + 50, by + 15, LineKind.Thick);
        g.Line(q + 15, by + 20, q + 15, by + 40, LineKind.Thick); g.Line(q + 32, by + 20, q + 32, by + 40, LineKind.Thick);
        g.Line(q + 5, by + 20, q + 5, by + 35); g.Line(q + 10, by + 20, q + 10, by + 35);
        g.CellText(q, by + 35, 15, 5, LabelH, "Лит."); g.CellText(q + 15, by + 35, 17, 5, LabelH, "Масса"); g.CellText(q + 32, by + 35, 18, 5, LabelH, "Масштаб");
        g.CellText(q, by + 20, 5, 15, ValueH, v(Fields.Litera), Fields.Litera);
        g.CellText(q + 15, by + 20, 17, 15, ValueH, v(Fields.Mass), Fields.Mass);
        g.CellText(q + 32, by + 20, 18, 15, ValueH, v(Fields.Scale) is { Length: > 0 } sc ? sc : "1:1", Fields.Scale);
        g.Line(q + 20, by + 15, q + 20, by + 20, LineKind.Thick);
        g.CellLeft(q, by + 15, 20, 5, LabelH, "Лист " + v(Fields.Sheet));
        g.CellLeft(q + 20, by + 15, 30, 5, LabelH, "Листов " + v(Fields.Sheets));
        g.CellText(q, by, 50, 15, ValueH, v(Fields.Organization), Fields.Organization);
    }

    /// <summary>ГОСТ 2.104 форма 2 — 185×40 (первый лист текстового документа и спецификации).</summary>
    static void Form2(SheetGraphics g, double bx, double by, Func<string, string> v)
    {
        g.Rect(bx, by, 185, 40, LineKind.Thick);
        LeftPart(g, bx, by, 2, new[] { "Разраб.", "Пров.", "", "Н.контр.", "Утв." }, v);
        double r = bx + 65;
        g.Line(r, by + 25, bx + 185, by + 25, LineKind.Thick);
        g.CellText(r, by + 25, 120, 15, 7, v(Fields.Designation), Fields.Designation);
        g.Line(r + 70, by, r + 70, by + 25, LineKind.Thick);
        g.CellText(r, by, 70, 25, BigH, v(Fields.Name), Fields.Name);
        double q = r + 70;
        g.Line(q, by + 20, q + 50, by + 20, LineKind.Thick);
        g.Line(q, by + 15, q + 50, by + 15, LineKind.Thick);
        g.Line(q + 15, by + 15, q + 15, by + 25, LineKind.Thick); g.Line(q + 30, by + 15, q + 30, by + 25, LineKind.Thick);
        g.Line(q + 5, by + 15, q + 5, by + 20); g.Line(q + 10, by + 15, q + 10, by + 20);
        g.CellText(q, by + 20, 15, 5, LabelH, "Лит."); g.CellText(q + 15, by + 20, 15, 5, LabelH, "Лист"); g.CellText(q + 30, by + 20, 20, 5, LabelH, "Листов");
        g.CellText(q, by + 15, 5, 5, LabelH, v(Fields.Litera), Fields.Litera);
        g.CellText(q + 15, by + 15, 15, 5, LabelH, v(Fields.Sheet), Fields.Sheet);
        g.CellText(q + 30, by + 15, 20, 5, LabelH, v(Fields.Sheets), Fields.Sheets);
        g.CellText(q, by, 50, 15, ValueH, v(Fields.Organization), Fields.Organization);
    }

    /// <summary>ГОСТ 2.104 форма 2а — 185×15 (последующие листы).</summary>
    static void Form2a(SheetGraphics g, double bx, double by, Func<string, string> v)
    {
        g.Rect(bx, by, 185, 15, LineKind.Thick);
        LeftPart(g, bx, by, 2, Array.Empty<string>(), v);
        double r = bx + 65;
        g.Line(r + 110, by, r + 110, by + 15, LineKind.Thick);
        g.Line(r + 110, by + 8, r + 120, by + 8);
        g.CellText(r, by, 110, 15, 7, v(Fields.Designation), Fields.Designation);
        g.CellText(r + 110, by + 8, 10, 7, LabelH, "Лист");
        g.CellText(r + 110, by, 10, 8, ValueH, v(Fields.Sheet), Fields.Sheet);
    }

    /// <summary>ISO 7200 — 180×36: владелец, разработал, утвердил; вид и статус документа; наименование; номер, изменение, дата
    /// выпуска; материал, масса, масштаб, лист.</summary>
    static void Iso7200(SheetGraphics g, double bx, double by, Func<string, string> v)
    {
        g.Rect(bx, by, 180, 36, LineKind.Thick);
        void Cell(double x, double y, double w, double h, string label, string field, double th = ValueH)
        {
            g.Rect(bx + x, by + y, w, h);
            g.Text(bx + x + 1, by + y + h - 1 - 1.8, 1.8, label);
            g.Text(bx + x + w / 2, by + y + 1.5, th, v(field), TextAlign.Center, field);
        }
        Cell(0, 18, 60, 18, "Legal owner", Fields.Organization, BigH);
        Cell(0, 9, 60, 9, "Created by", Fields.Developer);
        Cell(0, 0, 60, 9, "Approved by", Fields.Approver);
        Cell(60, 27, 60, 9, "Document type", "doc_type");
        Cell(120, 27, 60, 9, "Document status", "doc_status");
        Cell(60, 9, 80, 18, "Title", Fields.Name, BigH);
        Cell(140, 18, 40, 9, "Identification number", Fields.Designation);
        Cell(140, 9, 15, 9, "Rev.", Fields.Revision);
        Cell(155, 9, 25, 9, "Date of issue", Fields.Date);
        Cell(60, 0, 60, 9, "Material", Fields.Material);
        Cell(120, 0, 20, 9, "Mass, kg", Fields.Mass);
        Cell(140, 0, 15, 9, "Scale", Fields.Scale);
        g.Rect(bx + 155, by, 25, 9);
        g.Text(bx + 156, by + 9 - 1 - 1.8, 1.8, "Sheet");
        g.Text(bx + 167.5, by + 1.5, ValueH, $"{v(Fields.Sheet)}/{v(Fields.Sheets)}", TextAlign.Center);
    }

    /// <summary>
    /// k168 (замечание 05.11, 16:17, п. 12): подписи листа на языке чертежа: основная надпись ЕСКД и ISO, боковые графы, шапки
    /// таблиц по умолчанию. Текст пользователя (не из словаря) — как введён.
    /// </summary>
    static readonly (string Ru, string En)[] Labels =
    {
        ("Изм.", "Rev."), ("Лист", "Sheet"), ("№ докум.", "Doc. No."), ("Подп.", "Sign."), ("Дата", "Date"), ("Разраб.", "Drawn"),
        ("Пров.", "Checked"), ("Т.контр.", "Tech. ctrl"), ("Н.контр.", "Std. ctrl"), ("Утв.", "Approved"), ("Лит.", "Status"),
        ("Масса", "Mass"), ("Масштаб", "Scale"), ("Листов", "Sheets"), ("Инв. № подл.", "Orig. inv. No."), ("Подп. и дата", "Sign. & date"),
        ("Взам. инв. №", "Repl. inv. No."), ("Инв. № дубл.", "Dupl. inv. No."), ("Содержание", "Description"),
        ("Владелец", "Legal owner"), ("Разработал", "Created by"), ("Утвердил", "Approved by"), ("Тип документа", "Document type"),
        ("Статус документа", "Document status"), ("Наименование", "Title"), ("Обозначение", "Identification number"),
        ("Дата выпуска", "Date of issue"), ("Материал", "Material"), ("Масса, кг", "Mass, kg"),
    };
    public static string Tr(string s, string lang)
    {
        if (string.IsNullOrEmpty(s) || TrOff) return s;
        string t = s.TrimEnd(); string tail = s[t.Length..];
        foreach (var (ru, en) in Labels)
        {
            if (lang == "en" && t == ru) return en + tail;
            if (lang == "ru" && t == en) return ru + tail;
        }
        return s;
    }
    static readonly bool TrOff = Environment.GetEnvironmentVariable("P3D_SHEETLANG") == "0";

    /// <summary>Свободный элемент шаблона.</summary>
    static void Item(SheetGraphics g, TemplateItem it, string lang = "ru")
    {
        if (it.Hidden) return;
        switch (it.Kind)
        {
            case "line": g.Line(it.X, it.Y, it.X2, it.Y2, it.Thick ? LineKind.Thick : LineKind.Thin); break;
            case "table":
                {
                    var cols = it.Columns ?? new List<double> { 50 };
                    double w = cols.Sum(), rh = it.Height <= 0 ? 8 : it.Height;
                    int rows = Math.Max(1, it.Rows);
                    if (it.Title.Length > 0) g.Text(it.X + w / 2, it.Y + 2, ValueH, it.Title, TextAlign.Center);
                    g.Rect(it.X, it.Y - rows * rh, w, rows * rh, LineKind.Thick);
                    for (int r = 1; r < rows; r++) g.Line(it.X, it.Y - r * rh, it.X + w, it.Y - r * rh);
                    double cx = it.X;
                    for (int c = 0; c < cols.Count; c++)
                    {
                        if (c > 0) g.Line(cx, it.Y, cx, it.Y - rows * rh, LineKind.Thick);
                        for (int r = 0; r < rows; r++)
                            if (it.Cells != null && r < it.Cells.Count && c < it.Cells[r].Count)
                                g.CellLeft(cx, it.Y - (r + 1) * rh, cols[c], rh, Math.Min(ValueH, rh * 0.6), Tr(it.Cells[r][c], lang));
                        cx += cols[c];
                    }
                    break;
                }
            default:
                {
                    // текст: строки сверху вниз, межстрочный шаг 1.6 высоты (ГОСТ 2.304)
                    var lines = it.Text.Replace("\r", "").Split('\n');
                    for (int i = 0; i < lines.Length; i++) g.Text(it.X, it.Y - i * it.Height * 1.6, it.Height, lines[i]);
                    break;
                }
        }
    }

    /// <summary>Шаблоны по умолчанию: A4 и A3 ЕСКД (форма 1), A4 ISO.</summary>
    public static SheetTemplate Default(string format = "A4", bool landscape = false, SheetStandard std = SheetStandard.Eskd) =>
        new() { Name = $"{format} {(std == SheetStandard.Eskd ? "ЕСКД" : "ISO")}", Format = format, Landscape = landscape, Standard = std, TitleBlock = std == SheetStandard.Iso ? TitleBlockForm.Iso7200 : TitleBlockForm.Form1 };
}
