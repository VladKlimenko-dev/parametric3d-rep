using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Parametric3D.App.Services.Drawing;
using Parametric3D.Model.Drawing;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k169 (замечание 05.11, 16:17, п. 17): ОКНО СОЗДАНИЯ ДОКУМЕНТА: имя файла, деталь или сборка, тип документа (чертёж,
/// спецификация, аннотация), базовый шаблон (из папки шаблонов), имя шаблона, стандарт, система единиц, формат, ориентация и
/// картинка листа с основной надписью. Изменение параметров (кроме шаблона) даёт имя вида «ЕСКД in/kg/s A4 LS»; если шаблона
/// с таким именем нет — он создаётся в папке на основе выбранного.
/// </summary>
public sealed class NewDocumentDialog : Window
{
    public sealed record Result(string FileName, string? ModelPath, string DocType, SheetTemplate Template, bool TemplateCreated);

    public static string TemplatesDir => Environment.GetEnvironmentVariable("P3D_TEMPLATES") is { Length: > 0 } d ? d : Path.Combine(Services.UserPrefs.Dir, "Templates");
    public const string TemplateExt = ".p3dtpl";
    public static readonly string[] UnitSystems = { "mm/kg/s", "cm/g/s", "m/kg/s", "in/lb/s", "ft/lb/s" };
    public static readonly string[] Formats = { "A0", "A1", "A2", "A3", "A4" };

    /// <summary>Шаблоны папки; пустая папка — заполняется шаблонами по умолчанию (ЕСКД и ISO, A3 и A4).</summary>
    public static List<(string Name, string Path, SheetTemplate T)> LoadTemplates()
    {
        var dir = TemplatesDir;
        try
        {
            Directory.CreateDirectory(dir);
            if (!Directory.EnumerateFiles(dir, "*" + TemplateExt).Any())
                foreach (var (f, l, std) in new[] { ("A3", true, SheetStandard.Eskd), ("A4", false, SheetStandard.Eskd), ("A3", true, SheetStandard.Iso), ("A4", false, SheetStandard.Iso) })
                {
                    var t = SheetLayout.Default(f, l, std); t.Name = t.AutoName;
                    t.Save(Path.Combine(dir, FileNameOf(t.Name)));
                }
        }
        catch (Exception ex) { Core.AppLog.Write("templates dir", ex); }
        var res = new List<(string, string, SheetTemplate)>();
        try
        {
            foreach (var p in Directory.EnumerateFiles(dir, "*" + TemplateExt).OrderBy(x => x))
                try { var t = SheetTemplate.Load(p); res.Add((t.Name.Length > 0 ? t.Name : Path.GetFileNameWithoutExtension(p), p, t)); } catch { }
        }
        catch { }
        if (res.Count == 0) { var t = SheetLayout.Default("A3", true); t.Name = t.AutoName; res.Add((t.Name, "", t)); }
        return res;
    }
    public static string FileNameOf(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c)) + TemplateExt;

    readonly TextBox _file = new() { Name = "nd_file", MinWidth = 260 };
    readonly TextBox _model = new() { Name = "nd_model", MinWidth = 220, IsReadOnly = true };
    readonly ComboBox _type = new() { Name = "nd_type", MinWidth = 260 };
    readonly ComboBox _base = new() { Name = "nd_base", MinWidth = 260 };
    readonly TextBox _name = new() { Name = "nd_name", MinWidth = 260 };
    readonly ComboBox _std = new() { Name = "nd_std", MinWidth = 260, ItemsSource = new[] { "ЕСКД", "ISO" } };
    readonly ComboBox _units = new() { Name = "nd_units", MinWidth = 260, ItemsSource = UnitSystems };
    readonly ComboBox _format = new() { Name = "nd_format", MinWidth = 260, ItemsSource = Formats };
    readonly ComboBox _orient = new() { Name = "nd_orient", MinWidth = 260 };
    readonly Image _preview = new() { Name = "nd_preview", Width = 320, Height = 240, Stretch = Stretch.Uniform };
    readonly List<(string Name, string Path, SheetTemplate T)> _templates;
    readonly string[] _types;
    string? _modelPath;
    bool _nameEdited, _filling;

    public string? ModelPath => _modelPath;
    public SheetTemplate Current { get; private set; } = SheetLayout.Default("A3", true);
    public Result? Accepted { get; private set; }
    /// <summary>Последнее открытое окно (для проверок).</summary>
    public static NewDocumentDialog? LastOpened { get; private set; }

    public NewDocumentDialog(string modelName, bool sheetMode = false, SheetTemplate? baseTemplate = null)
    {
        Title = sheetMode ? L("Новый лист") : L("Новый документ");
        LastOpened = this;
        Width = 720; Height = 520; CanResize = true;
        this.Bind(BackgroundProperty, this.GetResourceObservable("P3.PanelBg"));
        _templates = LoadTemplates();
        _types = sheetMode ? new[] { "drawing", "annotation" } : new[] { "drawing", "bom", "annotation" };
        _type.ItemsSource = _types.Select(t => t switch { "drawing" => L("Чертёж"), "bom" => L("Спецификация (BoM)"), _ => L("Аннотация") }).ToList();
        _type.SelectedIndex = 0;
        _orient.ItemsSource = new[] { L("Альбомная"), L("Книжная") };
        _file.Text = sheetMode ? L("Лист") : L("Чертёж1");
        _model.Text = modelName;
        _base.ItemsSource = _templates.Select(t => t.Name).ToList();
        int bi = baseTemplate != null ? _templates.FindIndex(t => t.Name == baseTemplate.Name) : -1;
        if (bi < 0 && baseTemplate != null) { _templates.Insert(0, (baseTemplate.Name.Length > 0 ? baseTemplate.Name : baseTemplate.AutoName, "", baseTemplate.Clone())); _base.ItemsSource = _templates.Select(t => t.Name).ToList(); bi = 0; }
        _base.SelectedIndex = Math.Max(0, bi);
        _base.SelectionChanged += (_, _) => { if (!_filling) { _nameEdited = false; FillFromBase(); } };
        foreach (var c in new[] { _std, _units, _format, _orient }) c.SelectionChanged += (_, _) => { if (!_filling) Update(); };
        _name.TextChanged += (_, _) => { if (!_filling) _nameEdited = true; };
        var browse = new Button { Content = "…", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(browse, L("Выбрать файл детали или сборки"));
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Чертёж: деталь или сборка"), AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(L("Деталь или сборка Parametric3D")) { Patterns = new[] { "*" + Services.DocumentSession.PartExtension, "*" + Services.DocumentSession.AssemblyExtension } } },
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } p) SetModel(p);
        };
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", 9))), Margin = new Thickness(12) };
        void Row(int r, string label, Control c)
        {
            var l = new TextBlock { Text = L(label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 10, 3) };
            Grid.SetRow(l, r); form.Children.Add(l);
            c.Margin = new Thickness(0, 3); Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(c);
        }
        Row(0, "Имя файла", _file);
        Row(1, "Деталь или сборка", new DockPanel { Children = { Dock(browse, Avalonia.Controls.Dock.Right), _model } });
        Row(2, "Тип документа", _type);
        Row(3, "Базовый шаблон", _base);
        Row(4, "Имя шаблона", _name);
        Row(5, "Стандарт", _std);
        Row(6, "Система единиц", _units);
        Row(7, "Формат листа", _format);
        Row(8, "Ориентация листа", _orient);
        var ok = new Button { Content = L("Создать"), IsDefault = true, Padding = new Thickness(16, 4), Name = "nd_ok" };
        var cancel = new Button { Content = L("Отмена"), IsCancel = true, Padding = new Thickness(16, 4) };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close(null);
        var right = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children = { new Border { Child = _preview, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } };
        var root = new DockPanel();
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right); root.Children.Add(right); root.Children.Add(form);
        Content = root;
        FillFromBase();
    }
    static Control Dock(Control c, Avalonia.Controls.Dock d) { DockPanel.SetDock(c, d); return c; }

    public void SetModel(string path) { _modelPath = path; _model.Text = Path.GetFileName(path); }

    /// <summary>Параметры — из выбранного базового шаблона.</summary>
    void FillFromBase()
    {
        var b = _templates[Math.Max(0, _base.SelectedIndex)].T;
        _filling = true;
        _std.SelectedIndex = b.Standard == SheetStandard.Iso ? 1 : 0;
        _units.SelectedIndex = Math.Max(0, Array.IndexOf(UnitSystems, b.Units));
        _format.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, b.Format));
        _orient.SelectedIndex = b.Landscape ? 0 : 1;
        _filling = false;
        Update();
    }

    /// <summary>Шаблон по параметрам: копия базового; имя — базового (параметры те же) или «стандарт единицы формат ориентация».</summary>
    void Update()
    {
        var (bname, _, b) = _templates[Math.Max(0, _base.SelectedIndex)];
        var t = b.Clone();
        var std = _std.SelectedIndex == 1 ? SheetStandard.Iso : SheetStandard.Eskd;
        if (t.Standard != std) { t.Standard = std; t.TitleBlock = std == SheetStandard.Iso ? TitleBlockForm.Iso7200 : TitleBlockForm.Form1; }
        t.Units = UnitSystems[Math.Max(0, _units.SelectedIndex)];
        t.Format = Formats[Math.Max(0, _format.SelectedIndex)];
        t.Landscape = _orient.SelectedIndex == 0;
        bool same = t.Standard == b.Standard && t.Units == b.Units && t.Format == b.Format && t.Landscape == b.Landscape;
        t.Name = same ? bname : t.AutoName;
        Current = t;
        if (!_nameEdited) { _filling = true; _name.Text = t.Name; _filling = false; }
        try { _preview.Source = Preview(t); } catch (Exception ex) { Core.AppLog.Write("new document preview", ex); }
    }

    /// <summary>Картинка листа с рамкой и основной надписью, ориентация — как выбрана.</summary>
    public static Bitmap Preview(SheetTemplate t, int maxW = 320, int maxH = 240)
    {
        var g = SheetLayout.Compose(t, new FieldSource());
        float sc = (float)Math.Min(maxW / g.Width, maxH / g.Height);
        int w = Math.Max(1, (int)(g.Width * sc)), h = Math.Max(1, (int)(g.Height * sc));
        using var surf = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(w, h));
        surf.Canvas.Clear(SkiaSharp.SKColors.White);
        SheetRender.Draw(surf.Canvas, g, sc);
        using var img = surf.Snapshot(); using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        using var ms = new MemoryStream(data.ToArray());
        return new Bitmap(ms);
    }

    /// <summary>
    /// Принять: имя шаблона — из поля; шаблона с этим именем в папке нет — создаётся (на основе выбранного, с новыми параметрами).
    /// </summary>
    public void Accept()
    {
        var t = Current.Clone();
        t.Name = (_name.Text ?? "").Trim().Length > 0 ? _name.Text!.Trim() : t.AutoName;
        bool created = false;
        if (!_templates.Any(x => x.Name == t.Name))
            try { Directory.CreateDirectory(TemplatesDir); t.Save(Path.Combine(TemplatesDir, FileNameOf(t.Name))); created = true; }
            catch (Exception ex) { Core.AppLog.Write("save template", ex); }
        Accepted = new Result((_file.Text ?? "").Trim(), _modelPath, _types[Math.Max(0, _type.SelectedIndex)], t, created);
        Close(Accepted);
    }

    /// <summary>Для проверок: задать параметр по имени поля.</summary>
    public void Set(string field, int index)
    {
        var c = field switch { "type" => _type, "base" => _base, "std" => _std, "units" => _units, "format" => _format, "orient" => _orient, _ => throw new ArgumentException(field) };
        c.SelectedIndex = index;
    }
    public string TemplateName => _name.Text ?? "";
    public bool HasPreview => _preview.Source != null;
}
