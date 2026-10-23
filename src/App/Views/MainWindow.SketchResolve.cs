using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Parametric3D.App.Icons;
using Parametric3D.Core;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k96 (отзыв 17:55, пп. 15–16): ПРОТИВОРЕЧИЯ ЭСКИЗА — участники красным с перечнем в сообщении и панель «Разрешить эскиз»
/// (как Resolve Sketch в Creo): состояние эскиза, отвергнутая правка, участники противоречия с действиями «Удалить»,
/// «Справочный» (размер), «Слабый» (размер); после каждого действия отвергнутая правка пробуется снова.
/// </summary>
public partial class MainWindow
{
    private bool _resolvePanel;
    /// <summary>Отвергнутая правка: описание и повтор (null — нет).</summary>
    private (string What, Func<SketchEditResult> Retry)? _pendingEdit;
    private SketchEditResult? _lastConflict;
    private DispatcherTimer? _flagTimer;

    public bool ResolvePanelOpen => _resolvePanel;

    /// <summary>Имя связи для пользователя: «Горизонтально», «Совпадение»…</summary>
    public static string ConstraintName(ConstraintType t) => L(t switch
    {
        ConstraintType.Coincident => "Совпадение",
        ConstraintType.PointOnEntity => "Точка на кривой",
        ConstraintType.Horizontal => "Горизонтально",
        ConstraintType.Vertical => "Вертикально",
        ConstraintType.Parallel => "Параллельно",
        ConstraintType.Perpendicular => "Перпендикулярно",
        ConstraintType.Tangent => "Касание",
        ConstraintType.Equal => "Равенство",
        ConstraintType.Symmetric => "Симметрия",
        ConstraintType.MidPoint => "Середина",
        ConstraintType.Concentric => "Концентрично",
        ConstraintType.Fix => "Зафиксировать",
        _ => t.ToString(),
    });

    public static string DimensionName(SkDimension d)
    {
        string kind = L(d.Type switch
        {
            DimensionType.Radius => "радиус",
            DimensionType.Diameter => "диаметр",
            DimensionType.AngleLineLine => "угол",
            DimensionType.ArcLength => "длина дуги",
            DimensionType.CoordX or DimensionType.CoordY => "координата",
            _ => "размер",
        });
        string role = L(d.Reference ? "справочный" : d.UserLocked ? "с замком" : d.IsDefault || !d.Locked ? "слабый" : "сильный");
        return $"{kind} {d.DisplayText()} ({role})";
    }

    /// <summary>Снять красную пометку участников прошлого противоречия.</summary>
    public void ClearConflictFlags(Sketch? sk = null)
    {
        sk ??= View.ActiveSketch;
        if (sk == null) return;
        foreach (var c in sk.Constraints) c.Flagged = false;
        foreach (var d in sk.Dimensions) d.Flagged = false;
        View.MarkDirty();
    }

    /// <summary>
    /// Правка отвергнута: участники — красным, в сообщении — кто именно; повтор — для панели «Разрешить эскиз».
    /// </summary>
    public void ReportSketchConflict(Sketch sk, SketchEditResult r, string what, Func<SketchEditResult>? retry)
    {
        ClearConflictFlags(sk);
        foreach (var c in r.Constraints) c.Flagged = true;
        foreach (var d in r.Dimensions) d.Flagged = true;
        _lastConflict = r;
        _pendingEdit = retry != null ? (what, retry) : null;
        var names = r.Constraints.Select(c => ConstraintName(c.Type)).Concat(r.Dimensions.Select(DimensionName)).Distinct().ToList();
        string why = r.Failure switch
        {
            SketchEditFailure.Redundant => L("{0}: связь избыточна — она уже следует из других", what),
            SketchEditFailure.Singular => L("{0}: элемент вырождается (нулевая длина или радиус)", what),
            SketchEditFailure.DidNotConverge => L("{0}: решение не найдено", what),
            _ => L("{0}: противоречит другим связям эскиза", what),
        };
        if (names.Count > 0) why += ". " + L("Конфликтует с: {0}", string.Join(", ", names.Take(6)) + (names.Count > 6 ? "…" : ""));
        why += ". " + L("Разрешить — кнопка «Разрешить эскиз».");
        ShowToast(why);
        // пометка держится, пока открыта панель; без панели — снимается через 10 с
        _flagTimer?.Stop();
        _flagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _flagTimer.Tick += (_, _) => { _flagTimer?.Stop(); if (!_resolvePanel) ClearConflictFlags(sk); };
        _flagTimer.Start();
        if (_resolvePanel) ShowResolvePanel();
        View.MarkDirty();
    }

    public void ToggleResolvePanel()
    {
        if (_resolvePanel) { CloseResolvePanel(); return; }
        if (View.ActiveSketch == null) return;
        _refsPanel = false;
        View.Tool = null;
        ShowResolvePanel(inspect: _pendingEdit == null);
    }

    private void CloseResolvePanel()
    {
        _resolvePanel = false;
        _pendingEdit = null;
        ClearConflictFlags();
        PropsPanel.IsVisible = _propsOn;
        if (_propsOn) ShowProperties();
        RefreshToolStates();
    }

    /// <summary>Повторить отвергнутую правку (после удаления участника и т. п.).</summary>
    private void RetryPending(Sketch sk)
    {
        if (_pendingEdit is not { } pe) { ShowResolvePanel(inspect: true); return; }
        ClearConflictFlags(sk);
        var r = pe.Retry();
        AfterSketchEdit(sk);
        if (r.Ok)
        {
            _pendingEdit = null; _lastConflict = null;
            ShowToast(L("Правка применена: {0}", pe.What), error: false);
            ShowResolvePanel(inspect: true);
            return;
        }
        ReportSketchConflict(sk, r, pe.What, pe.Retry);
        ShowResolvePanel();
    }

    /// <summary>После правки эскиза: слабые размеры, перерисовка, дерево.</summary>
    public void AfterSketchEdit(Sketch sk)
    {
        // k102: размеры с формулами (d1 = d0 + 5) — пересчитать по новым значениям других размеров и решить снова
        if (sk.Dimensions.Any(x => !string.IsNullOrWhiteSpace(x.Formula)))
        {
            var table = Session.Part != null ? Session.Document.ScopeFor(Session.Part) : Session.Document.Parameters;
            var keep = sk.Dimensions.Select(x => (x, x.Value)).ToList();
            sk.UpdateDimensionsFromParameters(table);
            if (keep.Any(k => Math.Abs(k.x.Value - k.Value) > 1e-12))
            {
                var st = sk.Solve(Session.Document.Solver);
                if (st is SolveStatus.Inconsistent or SolveStatus.DidNotConverge) ShowToast(L("Размер по формуле не выполняется: эскиз противоречив — «Разрешить эскиз»"));
            }
        }
        sk.BumpStructure();
        View.RefreshDefaultDimensions();
        sk.NotifyChanged(sk);
        View.MarkDirty();
        RefreshToolStates();
    }

    private void ShowResolvePanel(bool inspect = false)
    {
        if (View.ActiveSketch is not { } sk) return;
        _resolvePanel = true;
        PropsPanel.IsVisible = true;
        PropsTitle.Text = L("РАЗРЕШИТЬ ЭСКИЗ");
        PropsBody.Children.Clear();
        PropsButtons.Children.Clear();
        var solver = Session.Document.Solver;

        SketchEditResult info;
        if (_pendingEdit == null && inspect)
        {
            ClearConflictFlags(sk);
            info = SketchEditing.Inspect(sk, solver);
            foreach (var c in info.Constraints) c.Flagged = true;
            foreach (var d in info.Dimensions) d.Flagged = true;
            _lastConflict = info;
        }
        else info = _lastConflict ?? SketchEditing.Inspect(sk, solver);

        // состояние эскиза
        string state = sk.LastSolve switch
        {
            SolveStatus.Ok => L("Эскиз определён полностью"),
            SolveStatus.Underconstrained when sk.FreeDegrees == 0 => L("Эскиз определён: слабых размеров {0} (их можно сделать сильными или заменить своими)", sk.DegreesOfFreedom),
            SolveStatus.Underconstrained => L("Эскиз недоопределён: степеней свободы {0}", sk.FreeDegrees),
            SolveStatus.Overconstrained => L("Эскиз избыточен: есть лишние (согласные) связи"),
            SolveStatus.Inconsistent => L("Эскиз противоречив: связи и размеры несовместимы"),
            SolveStatus.Singular => L("Эскиз вырожден: элементы нулевого размера"),
            _ => L("Решение не найдено"),
        };
        PropsBody.Children.Add(new TextBlock { Text = state, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        if (_pendingEdit is { } pe)
            PropsBody.Children.Add(new TextBlock
            {
                Text = L("Отвергнутая правка: {0}. Удалите или ослабьте одного из участников (красные) — правка будет применена.", pe.What),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "dim" }, FontSize = 12,
            });

        int rows = 0;
        void Header(string text) => PropsBody.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 0), Classes = { "dim" }, FontSize = 12 });
        Button Small(string text, string tip, Action act)
        {
            var b = new Button { Content = L(text), Classes = { "tool" }, Padding = new Thickness(6, 0), MinHeight = 22, FontSize = 12 };
            ToolTip.SetTip(b, L(tip));
            b.Click += (_, _) => act();
            return b;
        }
        void Row(string text, object target, params Control[] buttons)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var b in buttons) sp.Children.Add(b);
            DockPanel.SetDock(sp, Dock.Right);
            row.Children.Add(sp);
            var name = new Button { Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, Classes = { "tool" }, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 1) };
            ToolTip.SetTip(name, L("Выбрать в эскизе"));
            name.Click += (_, _) => SelectSketchItem(sk, target);
            row.Children.Add(name);
            PropsBody.Children.Add(row);
            rows++;
        }

        if (info.Constraints.Count + info.Dimensions.Count > 0)
        {
            Header(info.Failure == SketchEditFailure.Redundant && _pendingEdit == null ? L("Избыточные (любая из них лишняя):") : L("Участники противоречия:"));
            foreach (var c in info.Constraints.ToList())
            {
                var cc = c;
                Row($"{ConstraintName(c.Type)} #{c.Id}", c,
                    Small("Удалить", "Удалить связь", () => { sk.Constraints.Remove(cc); AfterResolveAction(sk); }));
            }
            foreach (var d in info.Dimensions.ToList())
            {
                var dd = d;
                Row(DimensionName(d), d,
                    Small("Справ.", "Сделать справочным (размер измеряет, а не задаёт)", () => { dd.Reference = true; dd.Locked = false; dd.IsDefault = false; dd.UserLocked = false; AfterResolveAction(sk); }),
                    Small("Слабый", "Сделать слабым", () => { SketchEditing.MakeWeak(sk, new[] { dd }, solver); AfterResolveAction(sk); }),
                    Small("×", "Удалить размер", () => { SketchEditing.DeleteDimensions(sk, new[] { dd }, solver); AfterResolveAction(sk); }));
            }
        }
        if (info.Degenerate.Count > 0 || sk.DegenerateEntities.Count > 0)
        {
            Header(L("Выродившиеся элементы:"));
            foreach (var e in info.Degenerate.Concat(sk.DegenerateEntities).Distinct())
                Row($"{L(Parametric3D.App.Viewport.Selection.KindNameRu(new PickRef(e.Id, PickKind.SketchCurve, sk, e)))} #{e.Id}", e);
        }
        if (Model.Sketching.NumericSketchSolver.Unsupported.Count > 0)
        {
            Header(L("Связи без уравнения (не действуют):"));
            foreach (var u in Model.Sketching.NumericSketchSolver.Unsupported) PropsBody.Children.Add(new TextBlock { Text = u, FontSize = 12 });
        }
        if (rows == 0 && _pendingEdit == null)
            PropsBody.Children.Add(new TextBlock { Text = L("Противоречий нет."), Margin = new Thickness(0, 8, 0, 0) });

        if (_pendingEdit != null)
        {
            var undo = new Button { Content = L("Отменить правку"), MinWidth = 90 };
            ToolTip.SetTip(undo, L("Оставить эскиз как был (правка уже не применена)"));
            undo.Click += (_, _) => { _pendingEdit = null; ClearConflictFlags(sk); ShowResolvePanel(inspect: true); };
            PropsButtons.Children.Add(undo);
            var again = new Button { Content = L("Повторить") };
            again.Click += (_, _) => RetryPending(sk);
            PropsButtons.Children.Add(again);
        }
        else
        {
            var refresh = new Button { Content = L("Проверить") };
            refresh.Click += (_, _) => ShowResolvePanel(inspect: true);
            PropsButtons.Children.Add(refresh);
        }
        var close = new Button { Content = L("Готово"), Classes = { "accent" }, MinWidth = 90 };
        close.Click += (_, _) => CloseResolvePanel();
        PropsButtons.Children.Add(close);
        RefreshToolStates();
        View.MarkDirty();
    }

    private void AfterResolveAction(Sketch sk)
    {
        sk.BumpStructure();
        try { sk.Solve(Session.Document.Solver); } catch (Exception ex) { AppLog.Write("sketch solve", ex); }
        if (_pendingEdit != null) { RetryPending(sk); return; }
        AfterSketchEdit(sk);
        ShowResolvePanel(inspect: true);
    }

    private void SelectSketchItem(Sketch sk, object target)
    {
        PickRef r = target switch
        {
            SkConstraint c => new PickRef(c.Id, PickKind.SketchConstraint, sk, c),
            SkDimension d => new PickRef(d.Id, PickKind.SketchDimension, sk, d),
            SkPoint p => new PickRef(p.Id, PickKind.SketchPoint, sk, p),
            SkEntity e => new PickRef(e.Id, PickKind.SketchCurve, sk, e),
            _ => PickRef.Empty,
        };
        if (r.IsEmpty) return;
        View.Selection.Select(r, additive: false);
        View.MarkDirty();
    }
}
