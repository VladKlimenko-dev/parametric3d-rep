using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using OpenTK.Mathematics;
using Parametric3D.App.Operations;
using Parametric3D.App.Services;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// u13d: КОНТЕКСТНАЯ ПАНЕЛЬ (таблица иконок): «Ещё…» — остальные допустимые операции, закрепить команду, недавние для
/// набора, поиск, повысить выбор, выбрать похожие, применить и отмена, режим «добавить / вырезать», перевернуть
/// направление, в обе стороны, следующее поле.
/// </summary>
public static class ContextCommands
{
    public static void Register(CommandRegistry r, MainWindow w)
    {
        KeyGesture K(Key k, KeyModifiers m = KeyModifiers.None) => new(k, m);
        const string C = "Контекстная панель";
        bool Op() => w.CurrentOperation != null;
        r.Add(new AppCommand { Id = "ctx.more", Label = "Ещё… (остальные допустимые операции)", Short = "Ещё…", Group = C, Icon = "more_but.svg", Synonyms = "more operations ещё операции",
            CanRun = () => w.Session?.Part != null && !w.InSketch, Run = () => MoreMenu(w, null) });
        r.Add(new AppCommand { Id = "ctx.pin", Label = "Закрепить команды в панели", Short = "Закрепить", Group = C, Icon = "pin_but.svg", Synonyms = "pin favourite закрепить избранное",
            Run = () => PinMenu(w, null) });
        r.Add(new AppCommand { Id = "ctx.search", Label = "Поиск команды", Short = "Поиск", Group = C, Icon = "search_ico.svg", Synonyms = "search find поиск", Run = () => w.OpenPalette() });
        r.Add(new AppCommand { Id = "ctx.up", Label = "Повысить выбор (вершина → ребро → грань → тело)", Short = "Повысить", Group = C, Icon = "breadcrumb_up_but.svg", Synonyms = "promote selection up повысить выбор",
            CanRun = () => !w.ViewPort.Selection.IsEmpty && !w.InSketch, Run = () => Promote(w) });
        r.Add(new AppCommand { Id = "ctx.similar", Label = "Выбрать похожие", Short = "Похожие", Group = C, Icon = "select_similar_but.svg", Synonyms = "select similar похожие",
            CanRun = () => w.ViewPort.Selection.Items.Any(x => x.Kind is PickKind.Face or PickKind.Edge), Run = () => w.ShowToast(L("Добавлено похожих: {0}", SelectSimilar(w)), error: false) });
        r.Add(new AppCommand { Id = "ctx.apply", Label = "Применить", Short = "ОК", Group = C, Icon = "apply_but.svg", Synonyms = "apply ok применить", CanRun = Op, Run = () => w.CommitOperation() });
        r.Add(new AppCommand { Id = "ctx.cancel", Label = "Отмена", Short = "Отмена", Group = C, Icon = "cancel_but.svg", Synonyms = "cancel отмена", CanRun = Op, Run = () => w.CancelOperation() });
        r.Add(new AppCommand { Id = "ctx.mode_add", Label = "Режим: добавить материал", Short = "Добавить", Group = C, Icon = "mode_add_but.svg", Synonyms = "add material join добавить",
            CanRun = () => CutCapable(w), IsChecked = () => CutCapable(w) && !w.CurrentOperation!.EditedFeature!.Cut, Run = () => SetCut(w, false) });
        r.Add(new AppCommand { Id = "ctx.mode_cut", Label = "Режим: вырезать", Short = "Вырезать", Group = C, Icon = "mode_cut_but.svg", Synonyms = "cut remove вырезать",
            CanRun = () => CutCapable(w), IsChecked = () => CutCapable(w) && w.CurrentOperation!.EditedFeature!.Cut, Run = () => SetCut(w, true) });
        r.Add(new AppCommand { Id = "ctx.flip", Label = "Перевернуть направление", Short = "Перевернуть", Group = C, Icon = "flip_direction_but.svg", Synonyms = "flip direction reverse перевернуть",
            CanRun = () => w.CurrentOperation?.EditedFeature is ExtrudeFeature or HelicalSweepFeature or RevolveFeature, Run = () => Flip(w) });
        r.Add(new AppCommand { Id = "ctx.both_sides", Label = "В обе стороны (симметрично)", Short = "Симметрично", Group = C, Icon = "both_sides_but.svg", Synonyms = "both sides symmetric симметрично обе стороны",
            CanRun = () => w.CurrentOperation?.EditedFeature is ExtrudeFeature, IsChecked = () => w.CurrentOperation?.EditedFeature is ExtrudeFeature { Symmetric: true }, Run = () => BothSides(w) });
        r.Add(new AppCommand { Id = "ctx.next", Label = "Следующее поле", Short = "Далее", Group = C, Icon = "next_handle_but.svg", Synonyms = "next field handle tab следующее",
            CanRun = Op, Run = () => w.CurrentOperation?.NextField() });
    }

    /// <summary>Окружения: деталь (параметрика), эскиз, FreeForm, сборка — переключатель в шапке.</summary>
    public static void RegisterEnv(CommandRegistry r, MainWindow w)
    {
        const string En = "Окружение";
        r.Add(new AppCommand { Id = "env.part", Label = "Окружение: деталь (параметрика)", Short = "Деталь", Group = En, Icon = "env_part_but.svg", Synonyms = "part environment деталь окружение",
            IsChecked = () => !w.InSketch && !w.InFreeForm && w.Session?.Part != null,
            Run = () => { if (w.InSketch) w.FinishSketch(); else if (w.CurrentOperation != null) w.CancelOperation(); else if (w.InFreeForm && w.Session.Part?.FreeFormDoc != true) FreeFormCommands.Exit(w); } });
        r.Add(new AppCommand { Id = "env.sketch", Label = "Окружение: эскиз", Short = "Эскиз", Group = En, Icon = "env_sketch_but.svg", Synonyms = "sketch environment эскиз окружение",
            IsChecked = () => w.InSketch, CanRun = () => w.Session?.Part != null, Run = () => { if (!w.InSketch) CommandRegistry.Instance.Find("op.sketch")?.Execute(); } });
        r.Add(new AppCommand { Id = "env.freeform", Label = "Окружение: FreeForm", Short = "FreeForm", Group = En, Icon = "env_freeform_but.svg", Synonyms = "freeform environment окружение",
            IsChecked = () => w.InFreeForm,
            Run = () => { if (w.InFreeForm) return; if (w.Session?.Part != null && !(w.Session.IsAssembly && w.Session.ActivePart == null)) CommandRegistry.Instance.Find("op.edit_in_freeform")?.Execute(); else CommandRegistry.Instance.Find("file.new_freeform")?.Execute(); } });
        r.Add(new AppCommand { Id = "env.assembly", Label = "Окружение: сборка", Short = "Сборка", Group = En, Icon = "env_assembly_but.svg", Synonyms = "assembly environment сборка окружение",
            IsChecked = () => w.Session?.IsAssembly == true && w.Session.ActivePart == null,
            Run = () => { if (w.Session.IsAssembly) AssemblyCommands.EditInContext(w, null); else CommandRegistry.Instance.Find("file.new_assembly")?.Execute(); } });
    }

    public static void EnvMenu(MainWindow w, Control anchor)
    {
        var sp = new StackPanel { Spacing = 1 };
        var fl = new Flyout { Content = sp, Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var id in new[] { "env.part", "env.sketch", "env.freeform", "env.assembly" })
            if (CommandRegistry.Instance.Find(id) is { } c)
            {
                var b = new Button { Content = (c.IsChecked?.Invoke() == true ? "● " : "   ") + c.Text, Classes = { "tool" }, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 220, IsEnabled = c.Enabled };
                b.Click += (_, _) => { fl.Hide(); c.Execute(); };
                sp.Children.Add(b);
            }
        fl.ShowAt(anchor);
    }

    static bool CutCapable(MainWindow w) => w.CurrentOperation?.EditedFeature is ExtrudeFeature or RevolveFeature or SweepFeature or LoftFeature or HelicalSweepFeature;

    static void After(MainWindow w)
    {
        var op = w.CurrentOperation; if (op == null) return;
        op.Panel?.Refresh();
        op.FieldChanged();
        w.RefreshToolStates();
    }

    public static void SetCut(MainWindow w, bool cut) { if (w.CurrentOperation?.EditedFeature is { } f) { f.Cut = cut; After(w); } }

    public static void Flip(MainWindow w)
    {
        switch (w.CurrentOperation?.EditedFeature)
        {
            case ExtrudeFeature e: e.Side1.Flip = !e.Side1.Flip; break;
            case HelicalSweepFeature h: h.LeftHand = !h.LeftHand; break;
            case RevolveFeature rv: rv.EndAngle = new ValueRef(-rv.EndAngle.Value) { Formula = rv.EndAngle.Formula == null ? null : $"-({rv.EndAngle.Formula})" }; break;
        }
        After(w);
    }

    public static void BothSides(MainWindow w)
    {
        if (w.CurrentOperation?.EditedFeature is ExtrudeFeature e) { e.Symmetric = !e.Symmetric; if (e.Symmetric) e.TwoSided = false; After(w); }
    }

    /// <summary>Допустимые сейчас операции модели, не вошедшие в предложенные.</summary>
    public static List<AppCommand> MoreOps(MainWindow w)
    {
        var shown = w.SuggestedOps().ToHashSet();
        return CommandRegistry.Instance.All.Where(c => c.Id.StartsWith("op.") && !shown.Contains(c.Id) && c.Enabled).ToList();
    }

    public static void MoreMenu(MainWindow w, Control? anchor)
    {
        var sp = new StackPanel { Spacing = 1 };
        var fl = new Flyout { Content = new ScrollViewer { Content = sp, MaxHeight = 520 }, Placement = PlacementMode.TopEdgeAlignedLeft };
        foreach (var c in MoreOps(w))
        {
            var b = new Button { Content = c.Text, Classes = { "tool" }, HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 240 };
            b.Click += (_, _) => { fl.Hide(); c.Execute(); };
            sp.Children.Add(b);
        }
        if (anchor != null) fl.ShowAt(anchor); else fl.ShowAt(w.ViewPort, showAtPointer: true);
    }

    public static void PinMenu(MainWindow w, Control? anchor)
    {
        var prefs = UserPrefs.Current;
        var sp = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        sp.Children.Add(new TextBlock { Text = L("Закреплённые команды показываются в контекстной панели, когда доступны"), Classes = { "dim" }, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var c in CommandRegistry.Instance.All.Where(c => c.Id.StartsWith("op.") || c.Id.StartsWith("an.") || c.Id.StartsWith("view.") || c.Id.StartsWith("sel.")))
        {
            var cb = new CheckBox { Content = c.Text, IsChecked = prefs.PinnedCommands.Contains(c.Id) };
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) { if (!prefs.PinnedCommands.Contains(c.Id)) prefs.PinnedCommands.Add(c.Id); } else prefs.PinnedCommands.Remove(c.Id); prefs.Save(); w.RefreshContext(); };
            sp.Children.Add(cb);
        }
        var fl = new Flyout { Content = new ScrollViewer { Content = sp, MaxHeight = 520 }, Placement = PlacementMode.TopEdgeAlignedLeft };
        if (anchor != null) fl.ShowAt(anchor); else fl.ShowAt(w.ViewPort, showAtPointer: true);
    }

    /// <summary>Повысить выбор: вершины → рёбра при них, рёбра → смежные грани, грани → тело (деталь), тело → операции нет.</summary>
    public static int Promote(MainWindow w)
    {
        var sel = w.ViewPort.Selection; var items = sel.Items.ToList();
        var add = new List<PickRef>();
        foreach (var r in items)
        {
            if (r.Owner is not Part p) continue;
            if (r.Kind == PickKind.Vertex && p.FindVertex(r.Id) is { } v)
                add.AddRange(p.Bodies.SelectMany(b => b.Edges).Where(e => e.V0 == v || e.V1 == v).Select(e => new PickRef(e.Id, PickKind.Edge, p, null)));
            else if (r.Kind == PickKind.Edge && p.FindEdge(r.Id) is { } e)
                add.AddRange(Topo.FacesOf(p, e).Select(f => new PickRef(f.Id, PickKind.Face, p, null)));
            else if (r.Kind == PickKind.Face) add.Add(new PickRef(p.Id, PickKind.Body, p.Parent, p));
        }
        if (add.Count == 0) return 0;
        sel.Clear();
        foreach (var a in add.GroupBy(x => (x.Id, x.Kind)).Select(g => g.First())) sel.Select(a, additive: true);
        return sel.Count;
    }

    /// <summary>Похожие: грани того же вида поверхности и размера (плоские — с той же нормалью или площадью; круглые — радиус), рёбра той же длины и вида.</summary>
    public static int SelectSimilar(MainWindow w)
    {
        var sel = w.ViewPort.Selection; int n0 = sel.Count;
        foreach (var r in sel.Items.ToList())
        {
            if (r.Owner is not Part p) continue;
            if (r.Kind == PickKind.Face && p.FindFace(r.Id) is { } f)
            {
                var sf = GeometryAudit.AuthoritativeSurface(f);
                double? rad = Kernel.Ops.DirectEdit.RadiusAfter(f, 0);
                double area = ViewSelectCommands.FaceArea(p, f) ?? 0;
                foreach (var g in p.Bodies.SelectMany(b => b.Faces))
                {
                    if (g == f || sel.Contains(g.Id)) continue;
                    var sg = GeometryAudit.AuthoritativeSurface(g);
                    if (sf == null || sg == null || sf.GetType() != sg.GetType()) continue;
                    bool same = rad is { } r0 ? Kernel.Ops.DirectEdit.RadiusAfter(g, 0) is { } r1 && Math.Abs(r1 - r0) < 1e-6
                        : Math.Abs((ViewSelectCommands.FaceArea(p, g) ?? -1) - area) < 1e-6 * Math.Max(1, area);
                    if (same) sel.Select(new PickRef(g.Id, PickKind.Face, p, null), additive: true);
                }
            }
            else if (r.Kind == PickKind.Edge && p.FindEdge(r.Id) is { } e)
                foreach (var x in p.Bodies.SelectMany(b => b.Edges))
                    if (x != e && !sel.Contains(x.Id) && x.Curve.GetType() == e.Curve.GetType() && Math.Abs(x.Curve.Length - e.Curve.Length) < 1e-6)
                        sel.Select(new PickRef(x.Id, PickKind.Edge, p, null), additive: true);
        }
        return sel.Count - n0;
    }
}
