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
    public bool IsDim { get; internal set; }
    public bool HasEye { get; private set; }
    /// <summary>k115 (п. 3): строка-ссылка — операция, которую повторяет родитель (зеркало, массив); не тянется и без глаза.</summary>
    public bool IsReference { get; private set; }

    /// <summary>k115: строка-ссылка на операцию-источник под зеркалом или массивом.</summary>
    public static TreeNode Reference(Feature src, Part? owner)
    {
        var n = new TreeNode(src, owner) { IsReference = true, HasEye = false, IsDim = true };
        n.Name = "↳ " + src.Name;
        return n;
    }

    /// <summary>Строка отката (детали, сборки или подсборки): рисуется цветной полосой, тянется мышью.</summary>
    public bool IsBar => Item is InfoItem { Role: "rollback" or "asm_rollback" or "sub_rollback" };
    public bool IsNotBar => !IsBar;
    /// <summary>Цвет строки: деталь — оранжевая, сборка — синяя, подсборка — фиолетовая.</summary>
    public IBrush? BarBrush => Item is InfoItem { Role: var r } ? r switch
    {
        "rollback" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        "asm_rollback" => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
        "sub_rollback" => new SolidColorBrush(Color.FromRgb(168, 85, 247)),
        _ => null,
    } : null;
    private readonly string _iconFile;
    public string IconFile => ComponentIcon ?? _iconFile;
    public IImage? Icon => IconProvider.Get(IconFile);
    public bool HasIcon => Icon != null;
    public string Glyph { get; }
    private readonly string? _summary;
    public string Tooltip => Error != null ? $"{Name}\n{Error}" : $"{KindLabel(Item)}: {Name}" + (_summary != null ? $"\n{_summary}" : "");

    /// <summary>u13: значок состояния поверх значка узла (ошибка, предупреждение, подавлена, ниже строки отката, потеряна
    /// ссылка, эскиз не определён или переопределён); без файла значка — знак.</summary>
    private readonly string _stateFile = "", _stateGlyph = "", _stateTip = "";
    public string StateFile => StateFileOverride?.File ?? _stateFile;
    public IImage? StateIcon => StateFile.Length == 0 ? null : IconProvider.Get(StateFile);
    public bool HasStateIcon => StateIcon != null;
    public string StateGlyph => StateFileOverride?.Glyph ?? _stateGlyph;
    public bool HasStateGlyph => StateGlyph.Length > 0 && !HasStateIcon;
    public string StateTip => StateFileOverride?.Tip ?? _stateTip;
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
        _iconFile = IconOf(item);
        Glyph = IconProvider.Abbrev(KindLabel(item));
        (_stateFile, _stateGlyph, _stateTip) = StateOf(item, part);
    }

    public static (string File, string Glyph, string Tip) StateOf(ModelItem item, Part? part)
    {
        if (item.Error is { } err)
        {
            bool lost = err.Contains("no longer exists") || err.Contains("not found") || err.Contains("missing") || err.Contains("Ambiguous") || err.Contains("ambiguous");
            return lost ? ("state_lost_ref_ico.svg", "⚑", Loc.L("Потеряна ссылка")) : ("state_error_ico.svg", "!", Loc.L("Ошибка"));
        }
        if (item.Suppressed) return ("state_suppressed_ico.svg", "⏸", Loc.L("Подавлена"));
        // u13h: узел FreeForm — ссылки изменились (вход отличается от запомненного)
        if (item is FreeFormFeature { RefsChanged: true }) return ("state_refs_changed_ico.svg", "↯", Loc.L("Ссылки FreeForm изменились: «Обновить ссылки»"));
        if (item is Feature f && part != null && part.IsBeyondStage(f)) return ("state_rolled_back_ico.svg", "↓", Loc.L("Ниже строки отката"));
        if (item is Sketch sk)
        {
            if (sk.LastSolve is SolveStatus.Overconstrained or SolveStatus.Inconsistent) return ("state_overdefined_ico.svg", "+", Loc.L("Эскиз переопределён"));
            if (sk.Entities.Count > 0 && sk.FreeDegrees > 0) return ("state_underdefined_ico.svg", "−", Loc.L("Эскиз не полностью определён: степеней свободы {0}", sk.FreeDegrees));
        }
        if (item is Feature fw && part != null && part.Downgrades.TryGetValue(fw, out var dg)) return ("state_warning_ico.svg", "⚠", Loc.L("Приближение: гранёных граней {0}, рёбер {1}", dg.Faces, dg.Edges));
        // u13h: шаг FreeForm — зафиксирован (замок) или ещё правится; компонент сборки — закреплён
        if (item is Feature { FreeForm: not null } st && item is not FreeFormFeature)
            return st.Frozen ? ("lock_ico.svg", "🔒", Loc.L("Шаг зафиксирован")) : ("unlocked_ico.svg", "", Loc.L("Шаг правится (параметры доступны до завершения команды)"));
        if (item is Part { Fixed: true }) return ("lock_ico.svg", "🔒", Loc.L("Закреплён"));
        // u13h: деталь — идёт перестроение; есть сообщения; тело проверено и годно
        if (item is Part pt)
        {
            if (pt.IsDirty) return ("state_building_ico.svg", "…", Loc.L("Идёт перестроение"));
            if (pt.Error == null && pt.Warnings.Count > 0) return ("state_info_ico.svg", "i", Loc.L("Сведения: сообщений {0}", pt.Warnings.Count));
            if (pt.Error == null && pt.Bodies.Count > 0 && pt.Integrity == null) return ("state_ok_ico.svg", "✓", Loc.L("Годно (тело проверено)"));
        }
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
        InfoItem { Role: "quilt" } => "tree_quilt_ico.svg",
        InfoItem { Role: "rollback" or "asm_rollback" or "sub_rollback" } => "rollback_bar_ico.svg",
        FreeFormFeature => "tree_freeform_ico.svg",
        Feature { FreeForm: not null } => "tree_ff_step_ico.svg",
        InfoItem { Role: "subassembly" } => "as_subassembly_but.svg",
        InfoItem { Role: "mates" } => "tree_mates_ico.svg",
        InfoItem { Role: "constraint" } => "mate_coincident_but.svg",
        InfoItem { Role: "joint" } => "joint_pin_but.svg",
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
        MeasureFeature => "dim_linear_but.svg",
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
        InfoItem { Role: "subassembly" } => "Подсборка",
        InfoItem { Role: "mates" } => "Ограничения",
        InfoItem { Role: "constraint" } => "Ограничение",
        InfoItem { Role: "joint" } => "Соединение",
        DatumCsys => "Система координат",
        DatumCurve => "Кривая",
        HelicalSweepFeature => "Винтовая протяжка",
        MeasureFeature => "Замер",
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
    public static List<TreeNode> Build(Document doc, bool assembly = false)
    {
        var res = new List<TreeNode>();
        var parts = doc.Root.Children.OfType<Part>().ToList();
        if (!assembly && parts.Count == 1 && doc.Root.Children.OfType<Assembly>().Count() == 0) res.Add(BuildItem(parts[0], parts[0]));
        else res.Add(BuildAssembly(doc.Root, doc.Title));
        return res;
    }

    /// <summary>u13: сборка — компоненты (по подсборкам), опорные сборки, папка ограничений и соединений.</summary>
    private static TreeNode BuildAssembly(Assembly asm, string title)
    {
        var node = new TreeNode(asm, null) { Name = title };
        var subs = new Dictionary<string, TreeNode>();
        // строка отката сборки — между компонентами (синяя); у подсборки — своя в конце (фиолетовая)
        int compIndex = 0; bool asmBar = false;
        TreeNode AsmBar() => new(new InfoItem("asm_rollback", Loc.L("— строка отката сборки —"), asm.Children.OfType<Part>().FirstOrDefault() ?? new Part("-")) { Asm = asm }, null);
        foreach (var c in asm.Children)
        {
            if (c is Part && !asmBar && asm.StageIndex >= 0 && compIndex == asm.StageIndex) { node.Children.Add(AsmBar()); asmBar = true; }
            if (c is Part) compIndex++;
            if (c is Part p)
            {
                var pn = BuildItem(p, p);
                pn.SetComponent(p);
                var target = node;
                if (!string.IsNullOrEmpty(p.SubAssembly))
                {
                    if (!subs.TryGetValue(p.SubAssembly, out var sn)) { sn = new TreeNode(new InfoItem("subassembly", p.SubAssembly, p), p); subs[p.SubAssembly] = sn; node.Children.Add(sn); }
                    target = sn;
                }
                pn.RestoreExpanded(false);
                if (asm.IsBeyondStage(p)) pn.IsDim = true;
                target.Children.Add(pn);
            }
            else if (c is not Assembly) node.Children.Add(new TreeNode(c, null));
            else node.Children.Add(BuildAssembly((Assembly)c, c.Name));
        }
        foreach (var sn in subs.Values) sn.Children.Add(new TreeNode(new InfoItem("sub_rollback", Loc.L("— строка отката подсборки —"), ((InfoItem)sn.Item).Owner), null));
        if (!asmBar) node.Children.Add(AsmBar());
        var mates = new TreeNode(new InfoItem("mates", Loc.L("Ограничения и соединения"), asm.Children.OfType<Part>().FirstOrDefault() ?? new Part("-")), null);
        foreach (var p in asm.Children.OfType<Part>())
        {
            int i = 0;
            foreach (var c in p.Constraints) mates.Children.Add(new TreeNode(new InfoItem("constraint", $"{c.Name}: {p.Name}{(c.BasePart != null ? " → " + c.BasePart.Name : "")}", p) { Index = i++ }, null));
            foreach (var m in p.Mates) mates.Children.Add(new TreeNode(new InfoItem("joint", $"{m.Name}: {p.Name}{(m.BasePart != null ? " → " + m.BasePart.Name : "")}", p) { Index = i++ }, null));
        }
        if (mates.Children.Count > 0) node.Children.Add(mates);
        return node;
    }

    /// <summary>Узел детали — компонент сборки: свой значок и состояние «зафиксирован».</summary>
    private void SetComponent(Part p)
    {
        ComponentIcon = "tree_component_ico.svg";
        if (p.Fixed && StateFileOverride == null) StateFileOverride = ("state_fixed_ico.svg", "⚓", Loc.L("Зафиксирован"));
        if (p.Skeleton) ComponentIcon = "as_skeleton_but.svg";
    }
    private string? ComponentIcon;
    private (string File, string Glyph, string Tip)? StateFileOverride;

    public static readonly bool TreeInfoOff = Environment.GetEnvironmentVariable("P3D_TREEINFO") == "0";

    private static TreeNode BuildItem(ModelItem item, Part? part)
    {
        var node = new TreeNode(item, part);
        var owner = item as Part ?? part;
        // u13: у детали — узлы параметров, материала, исполнений и тел (двойной щелчок — правка)
        if (item is Part pp)
        {
            // k120 (замечание 15:06, пп. 1, 6, 7): узлов «Параметры», «Материал» и «Тело N» в дереве больше нет — всё это
            // свойства детали: щелчок по детали показывает их в панели свойств (там же и правка). P3D_TREEINFO=0 — прежние узлы.
            if (TreeInfoOff)
            {
                node.Children.Add(new TreeNode(new InfoItem("params", Loc.L("Параметры"), pp), pp));
                node.Children.Add(new TreeNode(new InfoItem("material", Loc.L("Материал: {0}", pp.Material?.Name ?? "—"), pp), pp));
            }
            if (pp.Configurations.Count > 0) node.Children.Add(new TreeNode(new InfoItem("configs", Loc.L("Исполнения ({0}){1}", pp.Configurations.Count, pp.ActiveConfiguration.Length > 0 ? ": " + pp.ActiveConfiguration : ""), pp), pp));
            if (TreeInfoOff) for (int bi = 0; bi < pp.Bodies.Count; bi++) node.Children.Add(new TreeNode(new InfoItem("body", bi < pp.BodyNameList.Count ? Loc.L(pp.BodyNameList[bi]) : Loc.L("Тело {0}", bi + 1), pp) { Index = bi }, pp));
            // u13h: квилты (поверхности без объёма)
            for (int qi = 0; qi < pp.Quilts.Count; qi++) node.Children.Add(new TreeNode(new InfoItem("quilt", Loc.L("Квилт «{0}»", pp.Quilts[qi].Name), pp) { Index = qi }, pp));
        }
        var consumed = new HashSet<uint>(item.Children.OfType<Feature>().SelectMany(f => f.ConsumedSketchIds()));
        var groups = new Dictionary<string, TreeNode>();
        var ffNodes = new Dictionary<string, TreeNode>();
        // u13h: ручка строки отката (режим вставки) — узел на её месте; двойной щелчок — строка в конец
        // строка отката детали — всегда (в конце или на месте режима вставки); тянется мышью между операциями
        bool barDone = item is not Part;
        TreeNode Bar(Part bp) => new(new InfoItem("rollback", Loc.L("— строка отката —"), bp), bp);
        foreach (var c in item.Children)
        {
            if (!barDone && c is Feature bf && item is Part bpp && bpp.Features.IndexOf(bf) >= bpp.EffectiveStage) { node.Children.Add(Bar(bpp)); barDone = true; }
            if (c is Sketch sk && consumed.Contains(sk.Id)) continue;
            var child = BuildItem(c, owner);
            // u13: операции группы — внутри узла группы (на месте первой из них)
            var target = node;
            // u13h: шаги FreeForm — внутри своего узла FreeForm (журнал шагов)
            if (c is Feature ffs && ffs.FreeForm != null && c is not FreeFormFeature && ffNodes.TryGetValue(ffs.FreeForm, out var ffn))
            {
                ffn.Children.Add(child);
                continue;
            }
            if (c is FreeFormFeature ffm) ffNodes[ffm.Key] = child;
            if (c is Feature gf && !string.IsNullOrEmpty(gf.Group) && item is Part gp)
            {
                if (!groups.TryGetValue(gf.Group, out var gn))
                {
                    gn = new TreeNode(new InfoItem("group", gf.Group, gp), gp);
                    groups[gf.Group] = gn; node.Children.Add(gn);
                }
                target = gn;
            }
            // k115 (п. 3): зеркало и массивы операций — что именно они повторяют (строки-ссылки на операции-источники)
            if (c is Feature rf && item is Part rp && !ReplayChildrenOff && SourcesOf(rf) is { Count: > 0 } keys)
            {
                foreach (var k in keys)
                    if (rp.Features.FirstOrDefault(x => x.Key == k) is { } srcF) child.Children.Add(Reference(srcF, owner));
                child.RestoreExpanded(true);
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
        if (!barDone && item is Part bpe) node.Children.Add(Bar(bpe));
        return node;
    }

    static readonly bool ReplayChildrenOff = Environment.GetEnvironmentVariable("P3D_TREEREPLAY") == "0";
    static IReadOnlyList<string>? SourcesOf(Feature f) => f switch
    {
        MirrorFeature m => m.Sources, LinearPatternFeature l => l.Sources, CircularPatternFeature c => c.Sources, _ => null,
    };

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
    /// <summary>Строка отката сборки — её сборка (у строки детали — null).</summary>
    public Assembly? Asm;
    public InfoItem(string role, string name, Part owner) { Role = role; Name = name; Owner = owner; }
}

