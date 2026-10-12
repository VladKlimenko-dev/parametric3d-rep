using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>
/// Разбор конфликта ограничений.
///
/// Показывает, что именно спорит с только что наложенным ограничением,
/// и предлагает удалить одно из них. Если пользователь ничего не выбрал,
/// спорящие остаются на месте, помечаются как конфликтующие, рисуются
/// красным и исключаются из решателя.
///
/// Молча отменить наложение или молча наложить поверх — оба варианта
/// хуже: в первом случае непонятно, почему команда «не сработала»,
/// во втором эскиз тихо разъезжается.
/// </summary>
public sealed class ConstraintConflictForm : Form
{
    private readonly ListBox _list = new();

    /// <summary>Что пользователь выбрал удалить. null — ничего.</summary>
    public object? ToRemove { get; private set; }

    public ConstraintConflictForm(SkConstraint added, List<Conflict> conflicts)
    {
        Text = "Constraint conflict";
        Size = new Size(560, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(10, 8, 10, 0),
            Text = $"The new constraint {added.Type} cannot hold together with the items below.\n" +
                   "Choose one to remove, or press OK to keep everything and mark it as conflicting."
        };

        _list.Dock = DockStyle.Fill;
        _list.IntegralHeight = false;
        _list.Margin = new Padding(10);

        // Первым пунктом — само новое ограничение: чаще всего убрать хотят
        // именно его, раз оно только что всё сломало.
        _list.Items.Add(new Entry(added, $"[new]  {added.Type}"));
        foreach (var c in conflicts)
        {
            object? target = (object?)c.Constraint ?? c.Dimension;
            if (target == null) continue;
            _list.Items.Add(new Entry(target, c.Description));
        }
        _list.SelectedIndex = 0;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };

        var ok = new Button { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
        var remove = new Button { Text = "Remove selected", Width = 140, DialogResult = DialogResult.OK };
        var keep = new Button { Text = "Keep all", Width = 90, DialogResult = DialogResult.Cancel };

        remove.Click += (_, _) => ToRemove = (_list.SelectedItem as Entry)?.Target;
        ok.Click += (_, _) => ToRemove = null;

        buttons.Controls.Add(ok);
        buttons.Controls.Add(remove);
        buttons.Controls.Add(keep);

        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(header);

        AcceptButton = ok;
        CancelButton = keep;
    }

    private sealed class Entry
    {
        public readonly object Target;
        private readonly string _text;

        public Entry(object target, string text) { Target = target; _text = text; }
        public override string ToString() => _text;
    }
}
