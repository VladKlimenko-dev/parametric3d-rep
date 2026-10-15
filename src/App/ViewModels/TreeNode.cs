using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Parametric3D.App.Icons;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.ViewModels;

/// <summary>
/// УЗЕЛ ДЕРЕВА МОДЕЛИ: деталь, опорные элементы, эскизы, операции. Эскиз, поглощённый операцией, показывается внутри
/// неё (как в прежнем приложении и в Creo). Состояния: ошибка (красный), подавлена или ниже строки отката (тусклый),
/// скрыт (глаз). Модель не знает о дереве: дерево перестраивается по событиям документа.
/// </summary>
public sealed partial class TreeNode : ObservableObject
{
    public ModelItem Item { get; }
    public ObservableCollection<TreeNode> Children { get; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isVisible = true;

    public string? Error { get; private set; }
    public bool HasError => Error != null;
    public bool IsDim { get; private set; }
    public bool HasEye { get; }
    public string IconFile { get; }
    public IImage? Icon => IconProvider.Get(IconFile);
    public bool HasIcon => Icon != null;
    public string Glyph { get; }
    public string Tooltip => Error != null ? $"{Name}\n{Error}" : $"{KindLabel(Item)}: {Name}";

    public TreeNode(ModelItem item, Part? part)
    {
        Item = item;
        _name = item is Feature f ? f.Summary : item.Name;
        _isVisible = item.Visible;
        Error = item.Error;
        IsDim = item.Suppressed || (item is Feature fe && part != null && part.IsBeyondStage(fe));
        HasEye = item is not Feature;
        IconFile = IconOf(item);
        Glyph = IconProvider.Abbrev(KindLabel(item));
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (Item.Visible == value) return;
        Item.Visible = value;
        Item.VisibleUserSet = true;
        VisibilityToggled?.Invoke(this);
    }

    public static event Action<TreeNode>? VisibilityToggled;

    public static string IconOf(ModelItem item) => item switch
    {
        Assembly => "tree_assembly_ico.svg",
        Part => "tree_part_ico.svg",
        Sketch => "tree_sketch_ico.svg",
        DatumPlane => "tree_plane_ico.svg",
        DatumAxis => "tree_axis_ico.svg",
        DatumPoint => "tree_point_ico.svg",
        Line3DItem => "tree_curve_ico.svg",
        ExtrudeFeature => "f_extrude_but.svg",
        RevolveFeature => "f_revolve_but.svg",
        SweepFeature => "f_sweep_but.svg",
        LoftFeature => "f_blend_but.svg",
        HoleFeature => "f_hole_but.svg",
        RoundFeature => "f_round_but.svg",
        ChamferFeature => "f_chamfer_but.svg",
        VertexRoundFeature => "f_round_vertex_but.svg",
        VertexChamferFeature => "f_chamfer_vertex_but.svg",
        ShellFeature => "f_shell_but.svg",
        DraftFeature => "f_draft_but.svg",
        RibFeature => "f_rib_but.svg",
        MirrorFeature => "f_mirror_but.svg",
        LinearPatternFeature => "f_pattern_linear_but.svg",
        CircularPatternFeature => "f_pattern_circular_but.svg",
        DatumPlaneFeature => "dat_plane_but.svg",
        DatumAxisFeature => "dat_axis_but.svg",
        DatumPointFeature => "dat_point_but.svg",
        _ => "",
    };

    public static string KindLabel(ModelItem item) => item switch
    {
        Assembly => "Сборка",
        Part => "Деталь",
        Sketch => "Эскиз",
        DatumPlane => "Плоскость",
        DatumAxis => "Ось",
        DatumPoint => "Точка",
        Line3DItem => "Линия",
        ExtrudeFeature => "Выдавливание",
        RevolveFeature => "Вращение",
        SweepFeature => "Протяжка",
        LoftFeature => "Переход",
        HoleFeature => "Отверстие",
        RoundFeature => "Скругление",
        ChamferFeature => "Фаска",
        VertexRoundFeature => "Скругление вершины",
        VertexChamferFeature => "Фаска угла",
        ShellFeature => "Оболочка",
        DraftFeature => "Уклон",
        RibFeature => "Ребро жёсткости",
        MirrorFeature => "Зеркало",
        LinearPatternFeature => "Массив",
        CircularPatternFeature => "Круговой массив",
        Feature => "Операция",
        _ => item.Kind.ToString(),
    };

    /// <summary>Дерево документа: деталь документа детали — корень; у сборки — сборка.</summary>
    public static List<TreeNode> Build(Document doc)
    {
        var res = new List<TreeNode>();
        var parts = doc.Root.Children.OfType<Part>().ToList();
        if (parts.Count == 1 && doc.Root.Children.OfType<Assembly>().Count() == 0) res.Add(BuildItem(parts[0], parts[0]));
        else res.Add(BuildItem(doc.Root, null));
        return res;
    }

    private static TreeNode BuildItem(ModelItem item, Part? part)
    {
        var node = new TreeNode(item, part);
        var owner = item as Part ?? part;
        var consumed = new HashSet<uint>(item.Children.OfType<Feature>().SelectMany(f => f.ConsumedSketchIds()));
        foreach (var c in item.Children)
        {
            if (c is Sketch sk && consumed.Contains(sk.Id)) continue;
            var child = BuildItem(c, owner);
            if (c is Feature f)
                foreach (var id in f.ConsumedSketchIds())
                    if (item.Children.FirstOrDefault(x => x.Id == id) is Sketch s)
                    {
                        child.Children.Add(new TreeNode(s, owner));
                        child.IsExpanded = false;
                    }
            node.Children.Add(child);
        }
        return node;
    }

    public IEnumerable<TreeNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children) foreach (var d in c.SelfAndDescendants()) yield return d;
    }
}
