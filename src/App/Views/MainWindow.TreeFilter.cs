using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.App.ViewModels;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k149d (замечание 03.11, 14:36): ФИЛЬТР ДЕРЕВА МОДЕЛИ — какие виды узлов показывать: сборки, детали, эскизы, опорные
/// элементы, ограничения (mates), соединения (joints); все, несколько или один. Узел скрытого вида пропадает, а его показанные
/// потомки поднимаются на его место (только эскизы — список эскизов деталей). Кнопка-воронка у шевронов дерева; при неполном
/// наборе она подсвечена. P3D_TREEFILTER=0 — без фильтра.
/// </summary>
public partial class MainWindow
{
    public static readonly bool TreeFilterOff = Environment.GetEnvironmentVariable("P3D_TREEFILTER") == "0";
    public static readonly string[] TreeKinds = { "assembly", "part", "sketch", "datum", "mate", "joint" };
    static readonly Dictionary<string, string> TreeKindLabels = new()
    {
        ["assembly"] = "Сборки", ["part"] = "Детали и операции", ["sketch"] = "Эскизы", ["datum"] = "Опорные элементы",
        ["mate"] = "Ограничения (Mates)", ["joint"] = "Соединения (Joints)",
    };

    /// <summary>Показываемые виды узлов дерева.</summary>
    public readonly HashSet<string> TreeFilter = new(TreeKinds);
    public bool TreeFiltered => !TreeFilterOff && TreeFilter.Count < TreeKinds.Length;
    private Button? _treeFilterBtn;

    /// <summary>Вид узла дерева для фильтра; null — папка, видна, если виден хоть кто-то внутри.</summary>
    public static string? TreeKindOf(TreeNode n) => n.Item switch
    {
        Assembly => "assembly",
        InfoItem { Role: "subassembly" or "asm_rollback" or "sub_rollback" } => "assembly",
        InfoItem { Role: "mates" } => null,
        InfoItem { Role: "constraint" } => "mate",
        InfoItem { Role: "joint" } => "joint",
        Model.Sketching.Sketch => "sketch",
        DatumPlane or DatumAxis or DatumPoint or DatumCsys or DatumCurve => "datum",
        DatumPlaneFeature or DatumAxisFeature or DatumPointFeature => "datum",
        _ => "part",
    };

    /// <summary>Фильтр по видам: скрытый узел заменяется своими показанными потомками.</summary>
    public List<TreeNode> FilterTree(List<TreeNode> nodes)
    {
        if (!TreeFiltered) return nodes;
        var res = new List<TreeNode>();
        foreach (var n in nodes)
        {
            var kids = FilterTree(n.Children.ToList());
            var kind = TreeKindOf(n);
            bool show = kind == null ? kids.Count > 0 : TreeFilter.Contains(kind);
            if (show)
            {
                n.Children.Clear();
                foreach (var k in kids) n.Children.Add(k);
                res.Add(n);
            }
            else res.AddRange(kids);
        }
        return res;
    }

    private Button TreeFilterButton()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L12,0 L7.5,5.5 L7.5,11 L4.5,9.5 L4.5,5.5 Z"),
            StrokeThickness = 1.4, Width = 12, Height = 11, Stretch = Stretch.Uniform, StrokeJoin = PenLineJoin.Round,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, this.GetResourceObservable("P3.Text"));
        var b = new Button { Content = path, Classes = { "tool" }, Width = 24, Height = 24, Padding = new Thickness(0), Name = "tree_filter" };
        ToolTip.SetTip(b, L("Фильтр дерева: какие элементы показывать"));
        b.Click += (_, _) => ShowTreeFilter(b);
        _treeFilterBtn = b;
        MarkTreeFilterButton();
        return b;
    }

    private void MarkTreeFilterButton()
    {
        if (_treeFilterBtn?.Content is not Avalonia.Controls.Shapes.Path p) return;
        if (TreeFiltered) p.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, this.GetResourceObservable("P3.Accent"));
        else p.Fill = null;
    }

    /// <summary>Флажки видов: «Все», шесть видов; «только» у вида — показать один его.</summary>
    public void ShowTreeFilter(Control anchor)
    {
        var sp = new StackPanel { Spacing = 2, Margin = new Thickness(8), MinWidth = 230 };
        var boxes = new Dictionary<string, CheckBox>();
        var all = new CheckBox { Content = L("Все"), IsChecked = !TreeFiltered, Name = "treefilter_all", FontWeight = FontWeight.SemiBold };
        bool sync = false;
        void Sync()
        {
            sync = true;
            foreach (var (k, cb) in boxes) cb.IsChecked = TreeFilter.Contains(k);
            all.IsChecked = TreeFilter.Count == TreeKinds.Length;
            sync = false;
        }
        all.IsCheckedChanged += (_, _) =>
        {
            if (sync) return;
            TreeFilter.Clear();
            if (all.IsChecked == true) foreach (var k in TreeKinds) TreeFilter.Add(k);
            else TreeFilter.Add("part");   // пустое дерево бессмысленно: остаются детали
            Sync(); ApplyTreeFilter();
        };
        sp.Children.Add(all);
        sp.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 3), Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)) });
        foreach (var k in TreeKinds)
        {
            var kk = k;
            var cb = new CheckBox { Content = L(TreeKindLabels[k]), IsChecked = TreeFilter.Contains(k), Name = "treefilter_" + k };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (sync) return;
                if (cb.IsChecked == true) TreeFilter.Add(kk);
                else if (TreeFilter.Count > 1) TreeFilter.Remove(kk);
                else { sync = true; cb.IsChecked = true; sync = false; return; }   // хотя бы один вид
                Sync(); ApplyTreeFilter();
            };
            var only = new Button { Content = L("только"), Classes = { "tool" }, Padding = new Thickness(6, 0), MinHeight = 20, Name = "treefilter_only_" + k, HorizontalAlignment = HorizontalAlignment.Right };
            ToolTip.SetTip(only, L("Показать только этот вид"));
            only.Click += (_, _) => { TreeFilter.Clear(); TreeFilter.Add(kk); Sync(); ApplyTreeFilter(); };
            var dp = new DockPanel();
            DockPanel.SetDock(only, Dock.Right);
            dp.Children.Add(only); dp.Children.Add(cb);
            boxes[k] = cb;
            sp.Children.Add(dp);
        }
        var fl = new Flyout { Content = sp, Placement = PlacementMode.BottomEdgeAlignedRight };
        LastTreeFilterPanel = sp;
        fl.ShowAt(anchor);
    }
    public StackPanel? LastTreeFilterPanel;

    /// <summary>Фильтр изменился: дерево заново (развёрнутые ветки — прежние).</summary>
    public void ApplyTreeFilter()
    {
        _treeSig = null;
        RebuildTree();
        MarkTreeFilterButton();
    }
}
