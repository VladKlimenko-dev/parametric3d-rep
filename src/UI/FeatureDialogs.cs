using Parametric3D.Model;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>Мелкие помощники для однотипных диалогов настроек фич.</summary>
internal static class Ui
{
    public static Label Lab(string text, int x, int y, int w = 130) =>
        new() { Text = text, Left = x, Top = y + 3, Width = w, AutoSize = false };

    public static TextBox Box(string text, int x, int y, int w = 150) =>
        new() { Text = text, Left = x, Top = y, Width = w };

    public static ComboBox Combo(int x, int y, int w, params string[] items)
    {
        var c = new ComboBox { Left = x, Top = y, Width = w, DropDownStyle = ComboBoxStyle.DropDownList };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        return c;
    }

    public static CheckBox Check(string text, int x, int y, int w = 180) =>
        new() { Text = text, Left = x, Top = y, Width = w };

    /// <summary>Поле принимает и число, и формулу — так параметризация доступна везде.</summary>
    public static ValueRef ReadValue(TextBox box, ValueRef current, ParameterTable table)
    {
        string s = box.Text.Trim();
        if (double.TryParse(s.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
            return new ValueRef(d);

        var v = new ValueRef(current.Value) { Formula = s };
        v.Resolve(table);
        return v;
    }

    public static void Buttons(Form f, Action onOk)
    {
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        ok.Click += (_, _) => onOk();
        var p = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        p.Controls.Add(cancel);
        p.Controls.Add(ok);
        f.Controls.Add(p);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
    }
}




/// <summary>Настройки скругления.</summary>
public sealed class RoundDialog : Form
{
    public RoundDialog(RoundFeature f, ParameterTable pars, IEnumerable<EdgeRef> selectedEdges)
    {
        Text = "Round";
        Size = new Size(360, 200);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;

        var edges = selectedEdges.ToList();
        var info = Ui.Lab($"Selected edges: {(edges.Count > 0 ? edges.Count : f.Edges.Count)}", 12, 15, 300);
        var r = Ui.Box(f.Radius.ToString(), 150, 45, 150);

        Controls.AddRange(new Control[] { info, Ui.Lab("Radius:", 12, 45), r });

        Ui.Buttons(this, () =>
        {
            if (edges.Count > 0) { f.Edges.Clear(); f.Edges.AddRange(edges); }
            f.Radius = Ui.ReadValue(r, f.Radius, pars);
        });
    }
}

/// <summary>Настройки фаски.</summary>
public sealed class ChamferDialog : Form
{
    public ChamferDialog(ChamferFeature f, ParameterTable pars, IEnumerable<EdgeRef> selectedEdges)
    {
        Text = "Chamfer";
        Size = new Size(360, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;

        var edges = selectedEdges.ToList();
        var info = Ui.Lab($"Selected edges: {(edges.Count > 0 ? edges.Count : f.Edges.Count)}", 12, 15, 300);
        var mode = Ui.Combo(150, 45, 180, "Two distances", "Distance and angle");
        mode.SelectedIndex = (int)f.Mode;

        var d1 = Ui.Box(f.D1.ToString(), 150, 78, 150);
        var d2 = Ui.Box(f.D2.ToString(), 150, 108, 150);
        var ang = Ui.Box(f.Angle.ToString(), 150, 138, 150);

        var lab2 = Ui.Lab("Distance 2:", 12, 108);
        var lab3 = Ui.Lab("Angle, \u00b0:", 12, 138);

        void Sync()
        {
            bool two = mode.SelectedIndex == 0;
            d2.Enabled = lab2.Enabled = two;
            ang.Enabled = lab3.Enabled = !two;
        }
        mode.SelectedIndexChanged += (_, _) => Sync();
        Sync();

        Controls.AddRange(new Control[]
        {
            info, Ui.Lab("Type:", 12, 45), mode,
            Ui.Lab("Distance 1:", 12, 78), d1, lab2, d2, lab3, ang
        });

        Ui.Buttons(this, () =>
        {
            if (edges.Count > 0) { f.Edges.Clear(); f.Edges.AddRange(edges); }
            f.Mode = (ChamferKind)mode.SelectedIndex;
            f.D1 = Ui.ReadValue(d1, f.D1, pars);
            f.D2 = Ui.ReadValue(d2, f.D2, pars);
            f.Angle = Ui.ReadValue(ang, f.Angle, pars);
        });
    }
}

/// <summary>Настройки эскиза (Setup из тулбара эскиза).</summary>
/// <summary>Ввод одного числа — для скругления и фаски в эскизе.</summary>
public sealed class ValueInputForm : Form
{
    public double Value1 { get; private set; }
    public double Value2 { get; private set; }

    public ValueInputForm(string title, string label1, double v1, string? label2 = null, double v2 = 0)
    {
        Text = title;
        Size = new Size(320, label2 == null ? 160 : 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        Value1 = v1; Value2 = v2;

        var b1 = Ui.Box(v1.ToString("0.###"), 130, 18, 140);
        Controls.AddRange(new Control[] { Ui.Lab(label1, 12, 18, 110), b1 });

        TextBox? b2 = null;
        if (label2 != null)
        {
            b2 = Ui.Box(v2.ToString("0.###"), 130, 50, 140);
            Controls.AddRange(new Control[] { Ui.Lab(label2, 12, 50, 110), b2 });
        }

        Ui.Buttons(this, () =>
        {
            Value1 = Parse(b1.Text, Value1);
            if (b2 != null) Value2 = Parse(b2.Text, Value2);
        });
    }

    private static double Parse(string s, double fallback) =>
        double.TryParse(s.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : fallback;
}
