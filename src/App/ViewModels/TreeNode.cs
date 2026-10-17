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
    private readonly string? _summary;
    public string Tooltip => Error != null ? $"{Name}\n{Error}" : $"{KindLabel(Item)}: {Name}" + (_summary != null ? $"\n{_summary}" : "");

    /// <summary>u13: значок состояния поверх значка узла (ошибка, предупреждение, подавлена, ниже строки отката, потеряна
    /// ссылка, эскиз не определён или переопределён); без файла значка — знак.</summary>
    public string StateFile { get; } = "";
    public IImage? StateIcon => StateFile.Length == 0 ? null : IconProvider.Get(StateFile);
    public bool HasStateIcon => StateIcon != null;
    public string StateGlyph { get; } = "";
    public bool HasStateGlyph => StateGlyph.Length > 0 && !HasStateIcon;
    public string StateTip { get; } = "";
    /// <summary>u13: глаз — значками, если они есть (иначе рисунок).</summary>
    public IImage? EyeOn => IconProvider.Get("eye_visible_ico.svg");
    public IImage? EyeOff => IconProvider.Get("eye_hidden_ico.svg");
    public bool HasEyeIcons => EyeOn != null && EyeOff != null;
    public bool NoEyeIcons => !HasEyeIcons;

    public TreeNode(ModelItem item, Part? part)
    {
        Item = item;
        _name = item.Name;
        _summary = item is Feature f ? f.Summary : null;
        _isVisible = item.Visible;
        Error = item.Error;
        IsDim = item.Suppressed || (item is Feature fe && part != null && part.IsBeyondStage(fe));
        HasEye = item is not Feature and not InfoItem;
        IconFile = IconOf(item);
        Glyph = IconProvider.Abbrev(KindLabel(item));
        (StateFile, StateGlyph, StateTip) = StateOf(item, part);
    }

    public static (string File, string Glyph, string Tip) StateOf(ModelItem item, Part? part)
    {
        if (item.Error is { } err)
        {
            bool lost = err.Contains("no longer exists") || err.Contains("not found") || err.Contains("missing") || err.Contains("Ambiguous") || err.Contains("ambiguous");
            return lost ? ("state_lost_ref_ico.svg", "⚑", Loc.L("Потеряна ссылка")) : ("state_error_ico.svg", "!", Loc.L("Ошибка"));
        }
        if (item.Suppressed) return ("state_suppressed_ico.svg", "⏸", Loc.L("Подавлена"));
        if (item is Feature f && part != null && part.IsBeyondStage(f)) return ("state_rolled_back_ico.svg", "↓", Loc.L("Ниже строки отката"));
        if (item is Sketch sk)
        {
            if (sk.LastSolve is SolveStatus.Overconstrained or SolveStatus.Inconsistent) return ("state_overdefined_ico.svg", "+", Loc.L("Эскиз переопределён"));
            if (sk.Entities.Count > 0 && sk.DegreesOfFreedom > 0) return ("state_underdefined_ico.svg", "−", Loc.L("Эскиз не полностью определён"));
        }
        if (item is Feature fw && part != null && part.Downgrades.TryGetValue(fw, out var dg)) return ("state_warning_ico.svg", "⚠", Loc.L("Приближение: гранёных граней {0}, рёбер {1}", dg.Faces, dg.Edges));
        return ("", "", "");
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (Item.Visible == value) return;
        Item.Visible = value;
        Item.VisibleUserSet = true;
        // деталь и сборка: глаз переключает и всё внутри (п. 4) — элементы скрытой детали не видны и не выбираются
        if (Item is Part or Assembly)
        {
            foreach (var d in Item.Descendants()) { d.Visible = value; d.VisibleUserSet = true; }
            foreach (var n in SelfAndDescendants()) if (!ReferenceEquals(n, this)) n.SetVisibleSilently(value);
        }
        VisibilityToggled?.Invoke(this);
    }

    /// <summary>Глаз узла без события (каскад от родителя).</summary>
    private void SetVisibleSilently(bool v)
    {
        _cascading = true;
        try { IsVisible = v; } finally { _cascading = false; }
    }
    private bool _cascading;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!_building) ExpandedChanged?.Invoke(this);
    }
    private bool _building;

    public static event Action<TreeNode>? VisibilityToggled;
    /// <summary>Узел свёрнут или развёрнут пользователем — окно запоминает (дерево перестраивается и не теряет его).</summary>
    public static event Action<TreeNode>? ExpandedChanged;

    /// <summary>Состояние «развёрнут» без события (восстановление после перестройки дерева).</summary>
    public void RestoreExpanded(bool v)
    {
        _building = true;
        try { IsExpanded = v; } finally { _building = false; }
    }

    public static string IconOf(ModelItem item) => item switch
    {
        InfoItem { Role: "params" } => "tree_params_ico.svg",
        InfoItem { Role: "material" } => "tree_material_ico.svg",
        InfoItem { Role: "configs" } => "tree_config_ico.svg",
        InfoItem { Role: "body" } => "tree_body_ico.svg",
        InfoItem { Role: "group" } => "tree_group_ico.svg",
        DatumCsys => "tree_csys_ico.svg",
        DatumCurve => "tree_curve_ico.svg",
        HelicalSweepFeature => "f_helical_sweep_but.svg",
        MoveCopyFeature => "f_move_copy_but.svg",
        FaceEditFeature { Mode: FaceEditMode.Radius } => "dir_new_radius_but.svg",
        FaceEditFeature { Mode: FaceEditMode.Delete } => "dir_delete_face_but.svg",
        FaceEditFeature => "dir_offset_face_but.svg",
        PointsPatternFeature { Mode: PointsPatternMode.Curve } => "f_pattern_curve_but.svg",
        PointsPatternFeature { Mode: PointsPatternMode.Fill } => "f_pattern_fill_but.svg",
        PointsPatternFeature => "f_pattern_points_but.svg",
        CosmeticThreadFeature => "f_thread_but.svg",
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

    public static string KindLabel(ModelItem item) => Loc.L(KindLabelRu(item));

    public static string KindLabelRu(ModelItem item) => item switch
    {
        InfoItem { Role: "params" } => "Параметры",
        InfoItem { Role: "material" } => "Материал",
        InfoItem { Role: "configs" } => "Исполнения",
        InfoItem { Role: "body" } => "Тело",
        InfoItem { Role: "group" } => "Группа",
        DatumCsys => "Система координат",
        DatumCurve => "Кривая",
        HelicalSweepFeature => "Винтовая протяжка",
        MoveCopyFeature => "Перенос и поворот",
        FaceEditFeature => "Правка граней",
        PointsPatternFeature => "Массив",
        CosmeticThreadFeature => "Косметическая резьба",
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
        // u13: у детали — узлы параметров, материала, исполнений и тел (двойной щелчок — правка)
        if (item is Part pp)
        {
            node.Children.Add(new TreeNode(new InfoItem("params", Loc.L("Параметры"), pp), pp));
            node.Children.Add(new TreeNode(new InfoItem("material", Loc.L("Материал: {0}", pp.Material?.Name ?? "—"), pp), pp));
            if (pp.Configurations.Count > 0) node.Children.Add(new TreeNode(new InfoItem("configs", Loc.L("Исполнения ({0}){1}", pp.Configurations.Count, pp.ActiveConfiguration.Length > 0 ? ": " + pp.ActiveConfiguration : ""), pp), pp));
            for (int bi = 0; bi < pp.Bodies.Count; bi++) node.Children.Add(new TreeNode(new InfoItem("body", Loc.L("Тело {0}", bi + 1), pp) { Index = bi }, pp));
        }
        var consumed = new HashSet<uint>(item.Children.OfType<Feature>().SelectMany(f => f.ConsumedSketchIds()));
        var groups = new Dictionary<string, TreeNode>();
        foreach (var c in item.Children)
        {
            if (c is Sketch sk && consumed.Contains(sk.Id)) continue;
            var child = BuildItem(c, owner);
            // u13: операции группы — внутри узла группы (на месте первой из них)
            var target = node;
            if (c is Feature gf && !string.IsNullOrEmpty(gf.Group) && item is Part gp)
            {
                if (!groups.TryGetValue(gf.Group, out var gn))
                {
                    gn = new TreeNode(new InfoItem("group", gf.Group, gp), gp);
                    groups[gf.Group] = gn; node.Children.Add(gn);
                }
                target = gn;
            }
            if (c is Feature f)
                foreach (var id in f.ConsumedSketchIds())
                    if (item.Children.FirstOrDefault(x => x.Id == id) is Sketch s)
                    {
                        child.Children.Add(new TreeNode(s, owner));
                        child.RestoreExpanded(false);
                    }
            target.Children.Add(child);
        }
        return node;
    }

    public IEnumerable<TreeNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children) foreach (var d in c.SelfAndDescendants()) yield return d;
    }
}

/// <summary>u13: узел дерева без собственного элемента модели — параметры, материал, исполнения, тело детали.</summary>
public sealed class InfoItem : ModelItem
{
    public override ItemKind Kind => Role switch { "material" => ItemKind.Material, "params" => ItemKind.Parameter, "body" => ItemKind.Body, _ => ItemKind.Part };
    public readonly string Role;
    public readonly Part Owner;
    public int Index;
    public InfoItem(string role, string name, Part owner) { Role = role; Name = name; Owner = owner; }
}

