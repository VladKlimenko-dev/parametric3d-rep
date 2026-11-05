using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.Model.Drawing;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k169 (замечание 05.11, 16:17, п. 15–16): ЛИСТЫ ЧЕРТЕЖА. Лист 1 — сам документ, следующие — DrawingDocument.MoreSheets: у
/// каждого свой шаблон (формат, надпись), виды, размеры, обозначения и модель — как отдельный документ. Ярлыки листов над
/// строкой состояния («Лист 1», «Лист 2», «+»): щелчок — лист и его свойства в панели; «+» — окно создания документа;
/// «Добавить лист» — лист того же формата, что и первый (основная надпись ЕСКД — форма 2а, ГОСТ 2.104 для последующих листов).
/// </summary>
public sealed partial class SheetWindow
{
    DrawingDocument? _root;
    readonly Dictionary<DrawingDocument, DrawingScene> _scenes = new(ReferenceEqualityComparer.Instance);
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2, 6, 0) };

    /// <summary>Листы чертежа: лист 1 — документ, затем MoreSheets.</summary>
    public List<DrawingDocument> Sheets => _root == null ? new() : new[] { _root }.Concat(_root.MoreSheets).ToList();
    public int SheetIndex => Drawing == null ? 0 : Math.Max(0, Sheets.IndexOf(Drawing));

    /// <summary>Перейти на лист i: свой шаблон, виды, панель свойств.</summary>
    public void SwitchSheet(int i)
    {
        var all = Sheets;
        if (Kind != Mode.Drawing || i < 0 || i >= all.Count || ReferenceEquals(all[i], Drawing)) { UpdateTabs(); return; }
        EndCellEdit(true);
        if (Drawing != null)
        {
            if (_scene != null) _scenes[Drawing] = _scene;
            Drawing.Template = Template;
        }
        string? prevSrc = Drawing?.SourcePath;
        Drawing = all[i];
        Template = Drawing.Template;
        _scene = _scenes.GetValueOrDefault(Drawing);
        if (Drawing.SourcePath != prevSrc) { _input = null; _partFields = null; if (_scene == null) _fileInput = null; }
        _sel = null; _hover = null; _elSel.Clear(); _pick = null;
        _canvas.Fit();
        Refresh(); BuildProps(); UpdateTabs();
        _status.Text = L("Лист {0} из {1}", i + 1, all.Count);
    }

    /// <summary>
    /// Новый лист: шаблон — копия шаблона листа 1 без свободных элементов (или заданный), основная надпись ЕСКД — форма 2а; без
    /// видов (виды — кнопками «Главный вид», «Проекционные виды», «3D-вид»).
    /// </summary>
    public DrawingDocument AddSheet(SheetTemplate? tpl = null, string? sourcePath = null, bool annotation = false)
    {
        if (_root == null) throw new InvalidOperationException("not a drawing");
        if (ReferenceEquals(Drawing, _root)) _root.Template = Template;
        var t = tpl ?? _root.Template.Clone();
        if (tpl == null)
        {
            t.Items.Clear();
            if (t.Standard == SheetStandard.Eskd && !Form2aOff) t.TitleBlock = TitleBlockForm.Form2a;
        }
        var doc = new DrawingDocument { SourcePath = sourcePath ?? _root.SourcePath, Template = t, Units = _root.Units, LineS = _root.LineS, NoDefaultViews = true, Scale = _root.Scale };
        if (!annotation && tpl != null) { doc.NoDefaultViews = false; doc.EnsureViews(); }
        _root.MoreSheets.Add(doc);
        Dirty = true; UpdateTitle();
        SwitchSheet(Sheets.Count - 1);
        return doc;
    }
    static readonly bool Form2aOff = Environment.GetEnvironmentVariable("P3D_SHEETFORM2A") == "0";

    public void DeleteSheet(int i)
    {
        if (_root == null || i <= 0 || i >= Sheets.Count) return;
        var doc = Sheets[i];
        if (ReferenceEquals(doc, Drawing)) SwitchSheet(i - 1);
        _root.MoreSheets.Remove(doc); _scenes.Remove(doc);
        Dirty = true; UpdateTitle(); Refresh(); UpdateTabs();
    }

    /// <summary>Ярлыки листов: текущий — светлый, остальные — серые; «+» — новый лист через окно создания документа.</summary>
    void UpdateTabs()
    {
        _tabs.Children.Clear();
        if (Kind != Mode.Drawing || _root == null) return;
        var all = Sheets; int cur = SheetIndex;
        for (int i = 0; i < all.Count; i++)
        {
            int k = i;
            var b = new Button
            {
                Content = L("Лист {0}", i + 1), Padding = new Thickness(14, 3), Margin = new Thickness(0),
                Background = i == cur ? new SolidColorBrush(Color.FromArgb(255, 245, 245, 245)) : new SolidColorBrush(Color.FromArgb(255, 170, 172, 176)),
                Foreground = Brushes.Black, CornerRadius = new CornerRadius(4, 4, 0, 0), Name = "dr_sheet_tab_" + (i + 1),
            };
            b.Click += (_, _) => SwitchSheet(k);
            if (i > 0)
            {
                var del = new MenuItem { Header = L("Удалить лист") }; del.Click += (_, _) => DeleteSheet(k);
                b.ContextMenu = new ContextMenu { ItemsSource = new[] { del } };
            }
            _tabs.Children.Add(b);
        }
        var plus = new Button { Content = "+", Padding = new Thickness(12, 3), CornerRadius = new CornerRadius(4, 4, 0, 0), Name = "dr_sheet_tab_add" };
        ToolTip.SetTip(plus, L("Новый лист: окно создания документа"));
        plus.Click += async (_, _) => await NewSheetDialogAsync();
        _tabs.Children.Add(plus);
    }

    /// <summary>«+»: окно создания документа (лист этого чертежа: чертёж или аннотация).</summary>
    async System.Threading.Tasks.Task NewSheetDialogAsync()
    {
        var dlg = new NewDocumentDialog(_root?.SourcePath is { } sp ? Path.GetFileNameWithoutExtension(sp) : L("открытый документ"), sheetMode: true, baseTemplate: _root?.Template);
        var res = await dlg.ShowDialog<NewDocumentDialog.Result?>(this);
        if (res != null) ApplyNewSheet(res);
    }

    /// <summary>Лист по ответу окна создания документа (и для проверок).</summary>
    public DrawingDocument ApplyNewSheet(NewDocumentDialog.Result res)
    {
        var doc = AddSheet(res.Template, res.ModelPath, res.DocType == "annotation");
        doc.Units = res.Template.Units.Split('/')[0] is var u && u is "mm" or "cm" or "m" or "in" or "ft" ? u : "";
        Refresh(); BuildProps();
        return doc;
    }
}
