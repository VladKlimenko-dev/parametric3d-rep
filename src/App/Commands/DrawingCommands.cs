using Avalonia.Platform.Storage;
using Parametric3D.App.Views;
using Parametric3D.Model;
using Parametric3D.Model.Drawing;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>k155 (D1): команды листа — новый шаблон, новый перечень из сборки, открыть шаблон или перечень.</summary>
public static class DrawingCommands
{
    static SheetWindow Show(MainWindow w, SheetWindow win) => ShowSheet(w, win);

    /// <summary>Окно листа рядом с главным: связь с открытым документом (сборка — для перечня, деталь — поля надписи).</summary>
    public static SheetWindow ShowSheet(MainWindow w, SheetWindow win)
    {
        win.OpenAssembly = () => w.Session.IsAssembly ? w.Session.Document.Root : null;
        win.FieldPart = () => w.Session.Part ?? w.Session.Document.Root.Parts.FirstOrDefault();
        win.InvalidateViews();
        win.Refresh();
        win.Show(w);
        return win;
    }

    /// <summary>
    /// k155–k156 (D2): новый чертёж — открытой детали или открытой сборки (все детали в своих положениях, позиции по перечню);
    /// без открытой модели — выбор файла детали или сборки. Виды: главный, сверху, слева и изометрия на A3.
    /// </summary>
    public static async Task<SheetWindow?> NewDrawing(MainWindow w, string? modelPath = null)
    {
        bool openHasBodies = w.Session.Document.AllParts().Any(p => p.Bodies.Count > 0);
        if (modelPath == null && !openHasBodies)
        {
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("Чертёж: деталь или сборка"), AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(L("Деталь или сборка Parametric3D")) { Patterns = new[] { "*" + Services.DocumentSession.PartExtension, "*" + Services.DocumentSession.AssemblyExtension } } },
            });
            modelPath = files.FirstOrDefault()?.TryGetLocalPath();
            if (modelPath == null) return null;
        }
        SheetWindow win;
        if (modelPath != null)
        {
            win = new SheetWindow(SheetWindow.Mode.Drawing, null, null, null, new DrawingDocument { SourcePath = modelPath });
        }
        else
        {
            string? fp = w.Session.FilePath is { } f && !w.Session.Modified ? f : null;
            win = new SheetWindow(SheetWindow.Mode.Drawing, null, null, null, new DrawingDocument { SourcePath = fp });
            if (w.Session.IsAssembly) win.DrawingSource = () => DrawingInput.FromAssembly(w.Session.Document.Root);
            else
            {
                var part = w.Session.Part ?? w.Session.Document.Root.Parts.First();
                win.DrawingSource = () => DrawingInput.FromPart(part);
            }
        }
        return Show(w, win);
    }

    public static SheetWindow NewTemplate(MainWindow w) => Show(w, new SheetWindow(SheetWindow.Mode.Template, SheetLayout.Default()));

    /// <summary>Новый перечень — только из сборки: открытая сборка или файл сборки (выбор).</summary>
    public static async Task<SheetWindow?> NewBom(MainWindow w, string? assemblyPath = null)
    {
        var bom = new BomDocument { Date = DateTime.Now.ToString("dd.MM.yy", System.Globalization.CultureInfo.InvariantCulture) };
        Assembly? asm = null;
        if (assemblyPath == null && w.Session.IsAssembly) asm = w.Session.Document.Root;
        else
        {
            if (assemblyPath == null)
            {
                var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = L("Перечень: сборка-источник"), AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType(L("Сборка")) { Patterns = new[] { "*" + Services.DocumentSession.AssemblyExtension } } },
                });
                assemblyPath = files.FirstOrDefault()?.TryGetLocalPath();
                if (assemblyPath == null) { w.ShowToast(L("Перечень создаётся только из сборки")); return null; }
            }
            bom.SourcePath = assemblyPath;
            asm = AssemblyFile.Load(File.ReadAllText(assemblyPath)).Item1;
        }
        bom.UpdateFrom(asm);
        if (w.Session.IsAssembly && bom.SourcePath == null) bom.Title = w.Session.Document.Title;
        return Show(w, new SheetWindow(SheetWindow.Mode.Bom, null, bom));
    }

    public static async Task OpenSheet(MainWindow w)
    {
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Открыть чертёж, шаблон или перечень"), AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(L("Чертёж, шаблон листа, перечень")) { Patterns = new[] { "*" + SheetWindow.TemplateExt, "*" + SheetWindow.BomExt, "*" + SheetWindow.DrawingExt } } },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        try { Show(w, SheetWindow.OpenFile(path)); }
        catch (Exception ex) { w.ShowToast(L("Не удалось открыть: {0}", ex.Message)); }
    }

    public static void Register(CommandRegistry r, MainWindow w)
    {
        const string F = "Файл";
        r.Add(new AppCommand { Id = "file.new_drawing", Label = "Новый чертёж (из детали или сборки)", Group = F, Icon = "new_drawing_but.svg", Synonyms = "new drawing чертёж виды проекции", Run = async () => await NewDrawing(w) });
        r.Add(new AppCommand { Id = "file.new_template", Label = "Новый шаблон листа", Group = F, Icon = "new_template_but.svg", Synonyms = "new sheet template title block frame шаблон листа рамка основная надпись", Run = () => { NewTemplate(w); } });
        r.Add(new AppCommand { Id = "file.new_bom", Label = "Новый перечень (из сборки)", Group = F, Icon = "new_bom_but.svg", Synonyms = "new bom bill of materials parts list перечень спецификация", Run = async () => await NewBom(w) });
        r.Add(new AppCommand { Id = "file.open_sheet", Label = "Открыть чертёж, шаблон или перечень", Group = F, Icon = "doc_template_ico.svg", Synonyms = "open template bom открыть шаблон перечень p3dtpl p3dbom", Run = async () => await OpenSheet(w) });
    }
}
