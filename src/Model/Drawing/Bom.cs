using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parametric3D.Model.Drawing;

/// <summary>Разделы спецификации ГОСТ 2.106 (порядок — как в стандарте).</summary>
public static class BomSections
{
    public const string Documentation = "Документация", Complexes = "Комплексы", Units = "Сборочные единицы", Parts = "Детали",
        Standard = "Стандартные изделия", Other = "Прочие изделия", Materials = "Материалы", Kits = "Комплекты";
    public static readonly string[] Order = { Documentation, Complexes, Units, Parts, Standard, Other, Materials, Kits };
    public static int Rank(string s) { int i = Array.IndexOf(Order, s); return i < 0 ? Order.Length : i; }
}

/// <summary>Строка перечня. Key — связь с моделью (обозначение, файл или имя детали; у строк без модели — пусто).</summary>
public sealed class BomRow
{
    public string Key { get; set; } = "";
    public string Section { get; set; } = BomSections.Parts;
    public string Format { get; set; } = "";
    public string Zone { get; set; } = "";
    public int Position { get; set; }
    public string Designation { get; set; } = "";
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public string Unit { get; set; } = "шт.";
    public string Material { get; set; } = "";
    /// <summary>Масса одной штуки, кг (0 — неизвестна).</summary>
    public double Mass { get; set; }
    public string Note { get; set; } = "";
    /// <summary>Строка добавлена или правлена пользователем: при обновлении по модели правленые поля сохраняются.</summary>
    public bool Manual { get; set; }
    /// <summary>Строка из модели, удалённая пользователем: при обновлении не возвращается.</summary>
    public bool Hidden { get; set; }
    public BomRow Clone() => (BomRow)MemberwiseClone();
}

/// <summary>
/// k155 (D1, DRAWING_MODULE.md п. 4): ПЕРЕЧЕНЬ (.p3dbom) — только из сборки. Шапка (название, обозначение, изменение, дата),
/// строки: одинаковые детали — одна строка с количеством; подсборки — строкой «Сборочные единицы» или раскрыты (Expand);
/// пользователь правит, удаляет и добавляет строки (прочие изделия, материалы — без модели). Обновление по сборке сохраняет
/// правки (Manual) и удаления (Hidden). Вид по ЕСКД — спецификация ГОСТ 2.106 (разделы, форма 1, надпись форма 2/2а),
/// по ISO — таблица ISO 7573.
/// </summary>
public sealed class BomDocument
{
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "";
    public string Designation { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Date { get; set; } = "";
    /// <summary>Файл сборки-источника (.p3a); пусто — открытая сборка.</summary>
    public string? SourcePath { get; set; }
    /// <summary>Подсборки раскрыты: детали подсборок — строками этого перечня (иначе — одна строка подсборки).</summary>
    public bool Expand { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public SheetStandard Standard { get; set; } = SheetStandard.Eskd;
    public List<BomRow> Rows { get; set; } = new();
    /// <summary>Шаблон листа (поля надписи — Values); null — A4 по стандарту.</summary>
    public SheetTemplate? Template { get; set; }

    static readonly bool GroupOff = Environment.GetEnvironmentVariable("P3D_BOMGROUP") == "0";
    /// <summary>Разбор: перечней, собранных из сборки.</summary>
    public static long Built;

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, Opts);
    public static BomDocument FromJson(string json) => JsonSerializer.Deserialize<BomDocument>(json, Opts) ?? throw new InvalidDataException("not a BoM");
    public void Save(string path) => File.WriteAllText(path, ToJson());
    public static BomDocument Load(string path) => FromJson(File.ReadAllText(path));

    static readonly string[] StandardWords = { "ГОСТ", "ОСТ ", "DIN", "ISO", "ANSI", "Болт", "Винт", "Гайка", "Шайба", "Шпилька", "Шплинт", "Штифт", "Подшипник", "Кольцо уплотнительное", "Bolt", "Screw", "Nut", "Washer", "Pin", "Bearing", "O-ring" };

    /// <summary>Раздел детали по обозначению и наименованию: крепёж и изделия по стандарту — «Стандартные изделия».</summary>
    public static string SectionOf(string designation, string name) =>
        StandardWords.Any(w => designation.Contains(w, StringComparison.OrdinalIgnoreCase) || name.StartsWith(w, StringComparison.OrdinalIgnoreCase) || name.Contains(" " + w + " ", StringComparison.OrdinalIgnoreCase))
            ? BomSections.Standard : BomSections.Parts;

    static string BaseName(string n) { int i = n.IndexOf(" ["); if (i < 0) i = n.IndexOf(" ("); return i > 0 ? n[..i] : n; }

    /// <summary>Строки из сборки (без правок пользователя): группировка одинаковых деталей, подсборки, вложенные сборки.</summary>
    public static List<BomRow> RowsFrom(Assembly asm, bool expand)
    {
        var rows = new Dictionary<string, BomRow>();
        var order = new List<string>();
        void Add(string key, Func<BomRow> make, int count)
        {
            if (GroupOff) key += "#" + order.Count;   // мутация: без объединения одинаковых
            if (rows.TryGetValue(key, out var r)) { r.Quantity += count; return; }
            var n = make(); n.Key = key; n.Quantity = count; rows[key] = n; order.Add(key);
        }
        void Walk(Assembly a, string? group)
        {
            foreach (var c in a.Children)
            {
                if (c is Part p)
                {
                    if (p.Skeleton || p.Suppressed || a.IsBeyondStage(p)) continue;
                    string sub = group ?? p.SubAssembly ?? "";
                    if (!expand && sub.Length > 0)
                    {
                        // подсборка — одна строка «Сборочные единицы» (её детали — в её собственной спецификации)
                        string top = sub.Split(" / ")[0];
                        string ukey = "unit:" + top;
                        if (!rows.ContainsKey(ukey)) Add(ukey, () => new BomRow { Section = BomSections.Units, Name = top, Designation = "" }, 1);
                        continue;
                    }
                    string des = p.Info.Code ?? "";
                    string name = string.IsNullOrEmpty(p.Info.Title) ? BaseName(p.Name) : p.Info.Title;
                    string key = des.Length > 0 ? "code:" + des : p.SourcePath is { Length: > 0 } sp ? "file:" + Path.GetFileName(sp) : "name:" + name;
                    Add(key, () => new BomRow
                    {
                        Section = SectionOf(des, name), Designation = des, Name = name,
                        Material = p.Material?.Name ?? "", Mass = p.Volume > 0 ? Math.Round(p.MassByBodies(), 4) : 0,
                    }, 1);
                }
                else if (c is Assembly sa)
                {
                    if (expand) { Walk(sa, sa.Name); continue; }
                    Add("unit:" + sa.Name, () => new BomRow { Section = BomSections.Units, Name = sa.Name }, 1);
                }
            }
        }
        Walk(asm, null);
        return order.Select(k => rows[k]).ToList();
    }

    /// <summary>
    /// Собрать или обновить перечень по сборке: строки модели — заново; правленые пользователем (Manual) — поля пользователя
    /// сохраняются, количество — из модели; удалённые (Hidden) — остаются скрытыми; строки без модели — на месте.
    /// Позиции — по порядку разделов, если пользователь не задал их сам.
    /// </summary>
    public void UpdateFrom(Assembly asm)
    {
        var fresh = RowsFrom(asm, Expand);
        var old = Rows.Where(r => r.Key.Length > 0).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var res = new List<BomRow>();
        foreach (var r in fresh)
        {
            if (old.TryGetValue(r.Key, out var o))
            {
                if (o.Manual) { var keep = o.Clone(); keep.Quantity = r.Quantity; res.Add(keep); }
                else { r.Position = o.Position; r.Hidden = o.Hidden; r.Note = o.Note; res.Add(r); }
            }
            else res.Add(r);
        }
        res.AddRange(Rows.Where(r => r.Key.Length == 0));   // строки пользователя без модели
        Rows = res.OrderBy(r => BomSections.Rank(r.Section)).ToList();
        if (Rows.Any(r => r.Position == 0 && !r.Hidden && r.Section != BomSections.Documentation)) Renumber();
        if (Title.Length == 0) Title = asm.Name;
        Interlocked.Increment(ref Built);
    }

    /// <summary>Позиции подряд по разделам (раздел «Документация» — без позиций, ГОСТ 2.106 п. 3.9).</summary>
    public void Renumber()
    {
        int n = 0;
        foreach (var r in Rows.OrderBy(r => BomSections.Rank(r.Section)))
            r.Position = r.Hidden || r.Section == BomSections.Documentation ? 0 : ++n;
        Rows = Rows.OrderBy(r => BomSections.Rank(r.Section)).ThenBy(r => r.Position == 0 ? int.MaxValue : r.Position).ToList();
    }

    public IEnumerable<BomRow> Visible => Rows.Where(r => !r.Hidden);

    /// <summary>Выгрузка CSV (разделитель «;», UTF-8 с BOM — открывается в Excel).</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        static string Q(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        sb.AppendLine("Раздел;Поз.;Обозначение;Наименование;Кол.;Ед.;Материал;Масса, кг;Примечание");
        foreach (var r in Visible)
            sb.AppendLine(string.Join(";", Q(r.Section), r.Position > 0 ? r.Position.ToString(CultureInfo.InvariantCulture) : "", Q(r.Designation), Q(r.Name),
                r.Quantity.ToString(CultureInfo.InvariantCulture), Q(r.Unit), Q(r.Material), r.Mass > 0 ? r.Mass.ToString("0.###", CultureInfo.InvariantCulture) : "", Q(r.Note)));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ листы

    /// <summary>Колонки спецификации ГОСТ 2.106 форма 1 (мм): формат, зона, поз., обозначение, наименование, кол., примечание.</summary>
    public static readonly double[] EskdColumns = { 6, 6, 8, 70, 63, 10, 22 };
    public static readonly string[] EskdHeads = { "Формат", "Зона", "Поз.", "Обозначение", "Наименование", "Кол.", "Приме-\nчание" };
    /// <summary>ISO 7573: позиция, количество, наименование, обозначение, материал, масса.</summary>
    public static readonly double[] IsoColumns = { 12, 12, 60, 50, 31, 15 };
    public static readonly string[] IsoHeads = { "Item", "Qty", "Title", "Reference", "Material", "Mass, kg" };
    public const double HeadH = 15, RowH = 8;

    /// <summary>Строки таблицы листов: заголовки разделов (подчёркнутые, по центру колонки наименования) и строки позиций, с пустой
    /// строкой после раздела (ГОСТ 2.106 п. 3.6).</summary>
    public List<(string[] Cells, bool SectionHead)> TableLines()
    {
        var res = new List<(string[], bool)>();
        bool eskd = Standard == SheetStandard.Eskd;
        foreach (var grp in Visible.GroupBy(r => r.Section).OrderBy(g => BomSections.Rank(g.Key)))
        {
            if (eskd)
            {
                if (res.Count > 0) res.Add((new string[7], false));
                res.Add((new[] { "", "", "", "", grp.Key, "", "" }, true));
                res.Add((new string[7], false));
            }
            foreach (var r in grp)
            {
                string pos = r.Position > 0 ? r.Position.ToString(CultureInfo.InvariantCulture) : "";
                string qty = r.Quantity.ToString(CultureInfo.InvariantCulture) + (r.Unit is "шт." or "" ? "" : " " + r.Unit);
                string mass = r.Mass > 0 ? r.Mass.ToString("0.###", CultureInfo.InvariantCulture) : "";
                res.Add(eskd
                    ? (new[] { r.Format, r.Zone, pos, r.Designation, r.Name, qty, r.Note }, false)
                    : (new[] { pos, qty, r.Name, r.Designation, r.Material, mass }, false));
            }
        }
        return res;
    }

    /// <summary>Листы перечня: первый — надпись форма 2 (ISO — блок 7200), последующие — форма 2а; таблица от верха рамки до
    /// надписи; число листов — в поле «Листов».</summary>
    public List<SheetGraphics> Pages(FieldSource? src = null)
    {
        var tpl = Template ?? new SheetTemplate { Format = "A4", Standard = Standard };
        tpl.Standard = Standard;
        bool eskd = Standard == SheetStandard.Eskd;
        var cols = eskd ? EskdColumns : IsoColumns;
        var heads = eskd ? EskdHeads : IsoHeads;
        var lines = TableLines();
        var (fx0, fy0, fx1, fy1) = SheetLayout.FrameRect(tpl);
        double tableW = cols.Sum();
        int Capacity(bool first)
        {
            double tb = eskd ? SheetLayout.TitleHeight(first ? TitleBlockForm.Form2 : TitleBlockForm.Form2a) : SheetLayout.TitleHeight(TitleBlockForm.Iso7200);
            return (int)Math.Floor((fy1 - fy0 - tb - HeadH) / RowH + 1e-9);
        }
        // разбивка по листам
        var chunks = new List<List<(string[] Cells, bool SectionHead)>>();
        int i = 0;
        do
        {
            int cap = Capacity(chunks.Count == 0);
            chunks.Add(lines.Skip(i).Take(cap).ToList());
            i += cap;
        } while (i < lines.Count);
        var pages = new List<SheetGraphics>();
        for (int k = 0; k < chunks.Count; k++)
        {
            var fs = src ?? new FieldSource();
            fs.Auto[Fields.Sheet] = (k + 1).ToString(CultureInfo.InvariantCulture);
            fs.Auto[Fields.Sheets] = chunks.Count.ToString(CultureInfo.InvariantCulture);
            if (Title.Length > 0) fs.Auto[Fields.Name] = Title;
            if (Designation.Length > 0) fs.Auto[Fields.Designation] = Designation;
            if (Revision.Length > 0) fs.Auto[Fields.Revision] = Revision;
            if (Date.Length > 0) fs.Auto[Fields.Date] = Date;
            var form = eskd ? (k == 0 ? TitleBlockForm.Form2 : TitleBlockForm.Form2a) : TitleBlockForm.Iso7200;
            var g = SheetLayout.Compose(tpl, fs, form);
            double tb = SheetLayout.TitleHeight(form);
            double x0 = fx1 - tableW, top = fy1;
            int cap = Capacity(k == 0);
            double bottom = top - HeadH - cap * RowH;
            // шапка таблицы
            g.Rect(x0, bottom, tableW, top - bottom, LineKind.Thick);
            g.Line(x0, top - HeadH, x0 + tableW, top - HeadH, LineKind.Thick);
            double cx = x0;
            for (int c = 0; c < cols.Length; c++)
            {
                if (c > 0) g.Line(cx, bottom, cx, top, LineKind.Thick);
                var hl = heads[c].Split('\n');
                for (int t = 0; t < hl.Length; t++)
                {
                    bool vertical = eskd && cols[c] <= 8 && c < 3;   // «Формат», «Зона», «Поз.» — вертикально, как в форме ГОСТ
                    if (vertical) g.CellVertical(cx, top - HeadH, cols[c], HeadH, SheetLayout.LabelH, heads[c]);
                    else g.Text(cx + cols[c] / 2, top - HeadH / 2 + (hl.Length - 1) * 2.2 - t * 4.4 - SheetLayout.ValueH / 2, SheetLayout.ValueH, hl[t], TextAlign.Center);
                    if (vertical) break;
                }
                cx += cols[c];
            }
            // строки: тонкие линии через 8 мм
            for (int r = 1; r < cap; r++) g.Line(x0, top - HeadH - r * RowH, x0 + tableW, top - HeadH - r * RowH);
            var chunk = chunks[k];
            for (int r = 0; r < chunk.Count; r++)
            {
                double y = top - HeadH - (r + 1) * RowH;
                cx = x0;
                for (int c = 0; c < cols.Length; c++)
                {
                    string? s = chunk[r].Cells[c];
                    if (!string.IsNullOrEmpty(s))
                    {
                        bool centered = chunk[r].SectionHead || (eskd ? c is 0 or 1 or 2 or 5 : c is 0 or 1 or 5);
                        double th = SheetLayout.ValueH;
                        if (centered) g.CellText(cx, y, cols[c], RowH, th, s); else g.CellLeft(cx, y, cols[c], RowH, th, s);
                        if (chunk[r].SectionHead) { double tw = s.Length * th * 0.55; g.Line(cx + (cols[c] - tw) / 2, y + 1.2, cx + (cols[c] + tw) / 2, y + 1.2); }
                    }
                    cx += cols[c];
                }
            }
            pages.Add(g);
        }
        return pages;
    }
}
