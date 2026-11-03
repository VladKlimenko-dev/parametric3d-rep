using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Parametric3D.App.Commands;
using Parametric3D.Model;
using Parametric3D.Model.Mates;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Views;

/// <summary>
/// k149d (замечание 03.11, 14:32, п. 3): СВЯЗИ КОМПОНЕНТА В ПАНЕЛИ СВОЙСТВ. У компонента сборки (подсборки) — «Зафиксирован»,
/// «Свободно — 6 степеней», список его ограничений и соединений: включение, значение (расстояние, угол), вид соединения,
/// удаление; связи других компонентов с ним (удаление); «Добавить связь» — выбор ссылок для ограничения или соединения.
/// P3D_ASMPROPS=0 — без раздела.
/// </summary>
public partial class MainWindow
{
    public static readonly bool AsmPropsOff = Environment.GetEnvironmentVariable("P3D_ASMPROPS") == "0";

    static readonly MateType[] JointKinds = { MateType.Weld, MateType.Pin, MateType.Slider, MateType.Cylinder, MateType.Planar, MateType.Ball, MateType.BallCylinder, MateType.Slot };

    static string KindLabel(ConstraintKind k) => L(k switch
    {
        ConstraintKind.Mate => "Совпадение", ConstraintKind.Align => "Выравнивание", ConstraintKind.Orient => "Ориентация",
        ConstraintKind.Insert => "Вставка", ConstraintKind.Cylindrical => "Цилиндрическое", ConstraintKind.PointOnLine => "Точка на линии",
        ConstraintKind.PointOnSurface => "Точка на поверхности", ConstraintKind.Tangent => "Касание", ConstraintKind.CoordinateSystem => "Системы координат",
        _ => "По умолчанию",
    });

    /// <summary>Правка связей: один шаг отмены, решение сборки, дерево и панель — заново.</summary>
    private void LinkEdit(string undo, Action a)
    {
        Session.Edit(undo, a);
        AssemblyCommands.Solve(this);
        RebuildTree(); View.MarkDirty(); RefreshGizmo6(); RefreshToolStates();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_propsOn && CurrentOperation == null) ShowProperties(); });
    }

    private void ShowComponentLinks(Part part)
    {
        var root = Session.Document.Root;
        PropsBody.Children.Add(new TextBlock { Text = L("СВЯЗИ В СБОРКЕ"), Classes = { "dim" }, FontSize = 12, Margin = new Thickness(0, 2, 0, 4), Name = "prop_links" });

        var fixedCb = new CheckBox { Content = L("Зафиксирован"), IsChecked = part.Fixed, Name = "prop_fixed" };
        fixedCb.IsCheckedChanged += (_, _) =>
        {
            bool v = fixedCb.IsChecked == true; if (v == part.Fixed) return;
            LinkEdit(v ? L("Зафиксировать") : L("Освободить"), () => part.Fixed = v);
        };
        PropsBody.Children.Add(fixedCb);

        bool free = part.Mates.Any(m => m.Type == MateType.Free);
        var freeCb = new CheckBox { Content = L("Свободно — 6 степеней (6 DOF)"), IsChecked = free, Name = "prop_free", IsEnabled = !part.Fixed };
        ToolTip.SetTip(freeCb, L("Включено — компонент двигается маркером, прочие связи снимаются. Выключено — связь «6 DOF» снимается: задайте ограничения или соединения"));
        freeCb.IsCheckedChanged += (_, _) =>
        {
            bool v = freeCb.IsChecked == true; if (v == part.Mates.Any(m => m.Type == MateType.Free)) return;
            LinkEdit(v ? L("Соединение: шесть степеней") : L("Снять «6 DOF»"), () =>
            {
                if (v) { part.Mates.Clear(); part.Constraints.Clear(); part.Mates.Add(new Mate(MateType.Free, L("Шесть степеней"))); }
                else part.Mates.RemoveAll(m => m.Type == MateType.Free);
            });
        };
        PropsBody.Children.Add(freeCb);
        Row("Степеней свободы", part.Fixed ? "0" : part.Mates.Any(m => m.Type == MateType.Free) ? "6" : part.FreeDegrees.ToString());

        // свои ограничения
        for (int i = 0; i < part.Constraints.Count; i++)
        {
            var c = part.Constraints[i];
            var box = LinkRow((c.Name.Length > 0 ? c.Name : KindLabel(c.Kind)) + (c.BasePart != null ? " → " + c.BasePart.Name : ""), "prop_con_" + i,
                () => LinkEdit(L("Удалить ограничение"), () => part.Constraints.Remove(c)));
            var en = new CheckBox { Content = L("Включено"), IsChecked = c.Enabled, Name = "prop_con_on_" + i, Margin = new Thickness(12, 0, 0, 0) };
            en.IsCheckedChanged += (_, _) => { bool v = en.IsChecked == true; if (v != c.Enabled) LinkEdit(v ? L("Включить ограничение") : L("Выключить ограничение"), () => c.Enabled = v); };
            box.Children.Add(en);
            if (c.Kind is ConstraintKind.Mate or ConstraintKind.Align or ConstraintKind.PointOnSurface or ConstraintKind.Tangent)
                box.Children.Add(ValueBox(c.Kind == ConstraintKind.Tangent ? "Радиус, мм" : "Расстояние, мм", c.Offset, "prop_con_val_" + i, v => LinkEdit(L("Изменить ограничение"), () => c.Offset = v)));
            else if (c.Kind == ConstraintKind.Orient)
                box.Children.Add(ValueBox("Угол, °", c.Angle, "prop_con_val_" + i, v => LinkEdit(L("Изменить ограничение"), () => c.Angle = v)));
        }
        // свои соединения (кроме «6 DOF» — у него флажок выше)
        for (int i = 0; i < part.Mates.Count; i++)
        {
            var m = part.Mates[i];
            if (m.Type == MateType.Free) continue;
            var box = LinkRow(m.Name + (m.BasePart != null ? " → " + m.BasePart.Name : ""), "prop_joint_" + i,
                () => LinkEdit(L("Удалить соединение"), () => part.Mates.Remove(m)));
            int at = Array.IndexOf(JointKinds, m.Type);
            var kinds = new ComboBox { ItemsSource = JointKinds.Select(k => AssemblyCommands.JointLabel(k)).ToList(), SelectedIndex = at, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(12, 2, 0, 2), Name = "prop_joint_kind_" + i };
            kinds.SelectionChanged += (_, _) =>
            {
                if (kinds.SelectedIndex < 0 || JointKinds[kinds.SelectedIndex] == m.Type) return;
                var t = JointKinds[kinds.SelectedIndex];
                LinkEdit(L("Соединение: {0}", AssemblyCommands.JointLabel(t)), () => { m.Type = t; m.Name = AssemblyCommands.JointLabel(t); });
            };
            box.Children.Add(kinds);
        }
        // связи других компонентов с этим
        foreach (var o in root.Children.OfType<Part>().Where(o => !ReferenceEquals(o, part)))
        {
            foreach (var c in o.Constraints.Where(c => ReferenceEquals(c.BasePart, part)).ToList())
                LinkRow((c.Name.Length > 0 ? c.Name : KindLabel(c.Kind)) + " ← " + o.Name, "prop_in_" + o.Name, () => LinkEdit(L("Удалить ограничение"), () => o.Constraints.Remove(c)));
            foreach (var m in o.Mates.Where(m => ReferenceEquals(m.BasePart, part)).ToList())
                LinkRow(m.Name + " ← " + o.Name, "prop_in_" + o.Name, () => LinkEdit(L("Удалить соединение"), () => o.Mates.Remove(m)));
        }
        if (part.Constraints.Count == 0 && part.Mates.All(m => m.Type == MateType.Free) && !root.Children.OfType<Part>().Any(o => o.Constraints.Any(c => ReferenceEquals(c.BasePart, part)) || o.Mates.Any(m => ReferenceEquals(m.BasePart, part))))
            PropsBody.Children.Add(new TextBlock { Text = L("Ограничений и соединений нет"), Classes = { "dim" }, Margin = new Thickness(0, 2) });

        // добавить связь: выбор ссылок (грани, рёбра, вершины) — затем ограничение или соединение
        var reg = CommandRegistry.Instance;
        var cmds = reg.All.Where(x => x.Id.StartsWith("mate.") || x.Id.StartsWith("joint.") && x.Id != "joint.gimbal").ToList();
        var add = new ComboBox { PlaceholderText = L("Добавить связь…"), ItemsSource = cmds.Select(x => L(x.Label)).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0), Name = "prop_link_add" };
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedIndex < 0) return;
            var cmd = cmds[add.SelectedIndex];
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { View.Selection.Clear(); if (cmd.Enabled || cmd.CanRun?.Invoke() != false) cmd.Execute(); });
        };
        PropsBody.Children.Add(add);
        PropsBody.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 6), Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)) });
    }

    /// <summary>Строка связи: имя и «×» (удалить); под ней — её поля. Возвращает панель полей.</summary>
    private StackPanel LinkRow(string text, string name, Action delete)
    {
        var x = new Button { Content = "×", Classes = { "tool" }, Padding = new Thickness(6, 0), MinHeight = 20, VerticalAlignment = VerticalAlignment.Center, Name = name + "_del" };
        ToolTip.SetTip(x, L("Удалить"));
        x.Click += (_, _) => delete();
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(t, text);
        var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        DockPanel.SetDock(x, Dock.Right);
        dp.Children.Add(x); dp.Children.Add(t);
        var sp = new StackPanel { Name = name };
        sp.Children.Add(dp);
        PropsBody.Children.Add(sp);
        return sp;
    }

    private Control ValueBox(string label, double value, string name, Action<double> set)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("98,*"), Margin = new Thickness(12, 1, 0, 1) };
        g.Children.Add(new TextBlock { Text = L(label), Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center });
        var tb = new TextBox { Text = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), MinHeight = 24, Padding = new Thickness(4, 2), Name = name };
        void Commit()
        {
            if (double.TryParse((tb.Text ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && Math.Abs(v - value) > 1e-12)
            { value = v; set(v); }
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        Grid.SetColumn(tb, 1); g.Children.Add(tb);
        return g;
    }
}
