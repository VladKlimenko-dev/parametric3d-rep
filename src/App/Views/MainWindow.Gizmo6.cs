using Avalonia.Controls;
using Avalonia.Layout;
using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Mates;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k149 (замечания 03.11, 08:17–08:18): ПЕРЕМЕЩЕНИЕ И ВРАЩЕНИЕ КОМПОНЕНТА С ШЕСТЬЮ СТЕПЕНЯМИ СВОБОДЫ. В режиме сборки
/// выбранный компонент, который не зафиксирован и не связан ограничениями (связь только «6 DOF» или никакой), получает маркер
/// на виде: стрелки и площадки осей — перенос, кольца — поворот вокруг осей сборки через центр компонента (Ctrl — шаг 15°).
/// Один шаг отмены на перетаскивание. Точные значения — «Переместить / повернуть…» (сдвиг и поворот полями). P3D_GIZMO6=0 —
/// маркера нет.
/// </summary>
public partial class MainWindow
{
    static readonly bool Gizmo6Off = Environment.GetEnvironmentVariable("P3D_GIZMO6") == "0";
    private Part? _g6Part;
    private Vector3 _g6T0;
    private Quaternion _g6R0;
    private Vector3d _g6C0;

    // ---------------------------------------------------------------- k149 (10:48, п. 5): ссылки для ограничений и соединений

    private Action? _asmRefRun;
    private string? _asmRefWhat;
    /// <summary>Идёт выбор ссылок (граней, рёбер, вершин) для ограничения или соединения сборки.</summary>
    public bool AsmRefPicking => _asmRefRun != null;

    /// <summary>В сборке вне команд выбирается деталь целиком.</summary>
    public bool WholePartMode() => Session.IsAssembly && Session.ActivePart == null && CurrentOperation == null && !InSketch && _asmRefRun == null;

    /// <summary>Начать выбор двух ссылок для команды сборки; когда выбраны подходящие — команда выполняется.</summary>
    public void BeginAsmRefPick(string what, Action run)
    {
        _asmRefRun = run; _asmRefWhat = what;
        View.Selection.Clear();
        View.Gizmo = null;
        ShowToast(L("«{0}»: выберите две ссылки на разных компонентах (грани, рёбра, вершины). Esc — отмена", L(what)), error: false);
        RefreshToolStates();
    }

    public void CancelAsmRefPick()
    {
        if (_asmRefRun == null) return;
        _asmRefRun = null; _asmRefWhat = null;
        View.Selection.Clear(); RefreshToolStates();
    }

    /// <summary>Выбор изменился: две ссылки готовы — выполнить команду.</summary>
    private void AsmRefPickStep()
    {
        if (_asmRefRun is not { } run) return;
        if (Commands.AssemblyCommands.SelectedGeos(this).Count < 2) return;
        _asmRefRun = null; _asmRefWhat = null;
        try { run(); } finally { View.Selection.Clear(); RefreshToolStates(); }
    }

    /// <summary>Компонент свободен: не зафиксирован, без ограничений, связи — только «6 DOF».</summary>
    public static bool IsFree6(Part p) => !p.Fixed && p.Constraints.Count == 0 && p.Mates.All(m => m.Type == MateType.Free);

    /// <summary>Компонент, которому показан маркер (один свободный выбранный компонент в режиме сборки).</summary>
    public Part? Gizmo6Target()
    {
        if (Gizmo6Off || !Session.IsAssembly || Session.ActivePart != null || CurrentOperation != null || InSketch) return null;
        var comps = Commands.AssemblyCommands.SelectedComponents(this);
        // k149d: в разнесённом виде двигается любой компонент (положение запоминается в виде, место в сборке не меняется)
        bool exploded = Commands.AssemblyCommands.IsExploded(this);
        return comps.Count == 1 && (exploded || IsFree6(comps[0])) && InActiveGroup(comps[0]) ? comps[0] : null;
    }

    private Vector3d CentreOf(Part p)
    {
        var b = p.Bounds;
        var local = b.IsEmpty ? Vector3d.Zero : (b.Min + b.Max) / 2;
        return Commands.AssemblyCommands.ToWorld(p, local);
    }

    /// <summary>Маркер — по выбранному (вызывается при смене выбора и после перемещения).</summary>
    public void RefreshGizmo6()
    {
        if (CurrentOperation != null || View.SectionSelected) return;
        var p = Gizmo6Target();
        if (p == null) { if (View.Gizmo is { Mode: GizmoMode.Full }) View.Gizmo = null; return; }
        if (View.GizmoDragging) return;
        View.Gizmo = new GizmoSpec(GizmoMode.Full, CentreOf(p), Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ);
    }

    private bool Gizmo6Start(GizmoPart part)
    {
        if (View.Gizmo is not { Mode: GizmoMode.Full } g || Gizmo6Target() is not { } p) return false;
        _g6Part = p; _g6T0 = p.Translation; _g6R0 = p.Rotation; _g6C0 = g.Origin;
        Session.BeginEdit(L("Переместить компонент"));
        return true;
    }

    private bool Gizmo6Move(GizmoDrag d)
    {
        if (_g6Part is not { } p) return false;
        ApplyMove(p, _g6T0, _g6R0, _g6C0, d.Part is GizmoPart.RingX or GizmoPart.RingY or GizmoPart.RingZ ? Vector3d.Zero : d.Delta,
            d.Part switch { GizmoPart.RingX => Vector3d.UnitX, GizmoPart.RingY => Vector3d.UnitY, GizmoPart.RingZ => Vector3d.UnitZ, _ => Vector3d.Zero }, d.AngleDeg);
        View.MarkDirty();
        return true;
    }

    private bool Gizmo6End()
    {
        if (_g6Part is not { } p) return false;
        _g6Part = null;
        if (p.Translation == _g6T0 && p.Rotation == _g6R0) Session.DiscardEdit();
        else { Commands.AssemblyCommands.RecordExplodeMove(this, p); Session.CommitEdit(); }
        View.Gizmo = null; RefreshGizmo6();
        UpdateStatus(); RefreshToolStates();
        return true;
    }

    /// <summary>Сдвиг (оси сборки) и поворот вокруг оси сборки через точку c: p' = q·(R·p + T − c) + c + Δ.</summary>
    public static void ApplyMove(Part p, Vector3 t0, Quaternion r0, Vector3d c, Vector3d delta, Vector3d axis, double angleDeg)
    {
        var q = axis.LengthSquared > 0 && angleDeg != 0 ? Quaternion.FromAxisAngle(new Vector3((float)axis.X, (float)axis.Y, (float)axis.Z), (float)(angleDeg * Math.PI / 180)) : Quaternion.Identity;
        var cf = new Vector3((float)c.X, (float)c.Y, (float)c.Z);
        p.Rotation = Quaternion.Normalize(q * r0);
        p.Translation = Vector3.Transform(t0 - cf, q) + cf + new Vector3((float)delta.X, (float)delta.Y, (float)delta.Z);
    }

    /// <summary>«Переместить / повернуть…»: сдвиг X, Y, Z и поворот вокруг осей X, Y, Z (градусы, через центр компонента).</summary>
    public async Task MoveComponentByValues()
    {
        var comps = Commands.AssemblyCommands.SelectedComponents(this);
        if (comps.Count != 1) { ShowToast(L("Выберите компонент"), error: false); return; }
        var p = comps[0];
        if (!IsFree6(p) && !Commands.AssemblyCommands.IsExploded(this)) { ShowToast(L("Компонент зафиксирован или связан ограничениями — его положение задают они"), error: false); return; }
        var boxes = new TextBox[6];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
        string[] labels = { "Сдвиг X, мм", "Сдвиг Y, мм", "Сдвиг Z, мм", "Поворот вокруг X, °", "Поворот вокруг Y, °", "Поворот вокруг Z, °" };
        for (int i = 0; i < 6; i++)
        {
            var t = new TextBlock { Text = L(labels[i]), VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 3, 12, 3) };
            boxes[i] = new TextBox { Text = "0", MinWidth = 110, Name = "mv_" + i, Margin = new Avalonia.Thickness(0, 3) };
            Grid.SetRow(t, i); Grid.SetRow(boxes[i], i); Grid.SetColumn(boxes[i], 1);
            grid.Children.Add(t); grid.Children.Add(boxes[i]);
        }
        bool ok = false;
        var w = new Window { Title = L("Переместить / повернуть"), SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, MinWidth = 320 };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("P3.PanelBg"));
        var bOk = new Button { Content = L("Применить"), MinWidth = 96, Name = "mv_ok" }; bOk.Classes.Add("accent");
        var bCancel = new Button { Content = L("Отмена"), MinWidth = 96 };
        bOk.Click += (_, _) => { ok = true; w.Close(); };
        bCancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12, Children = { grid, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { bOk, bCancel } } } };
        LastMoveWindow = w;
        await w.ShowDialog(this);
        if (!ok) return;
        double V(int i) => double.TryParse((boxes[i].Text ?? "0").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        MoveComponent(p, new Vector3d(V(0), V(1), V(2)), new Vector3d(V(3), V(4), V(5)));
    }
    public Window? LastMoveWindow;

    /// <summary>Сдвиг и поворот (градусы вокруг X, затем Y, затем Z — оси сборки через центр компонента) одним шагом отмены.</summary>
    public void MoveComponent(Part p, Vector3d shift, Vector3d anglesDeg)
    {
        var c = CentreOf(p);
        Session.Edit(L("Переместить компонент"), () =>
        {
            ApplyMove(p, p.Translation, p.Rotation, c, Vector3d.Zero, Vector3d.UnitX, anglesDeg.X);
            ApplyMove(p, p.Translation, p.Rotation, c, Vector3d.Zero, Vector3d.UnitY, anglesDeg.Y);
            ApplyMove(p, p.Translation, p.Rotation, c, shift, Vector3d.UnitZ, anglesDeg.Z);
            Commands.AssemblyCommands.RecordExplodeMove(this, p);
        });
        Session.CommitEdit();
        View.MarkDirty(); RefreshGizmo6(); UpdateStatus(); RefreshToolStates();
    }
}
