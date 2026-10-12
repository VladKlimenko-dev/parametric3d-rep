using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>Настройка выдавливания.</summary>
public partial class ExtrudeForm : Form
{
    private static readonly string[] EndNames =
        { "To distance", "Through all", "To next", "To selected" };

    private readonly ExtrudeFeature _f;
    private readonly ParameterTable _params;
    private readonly List<Sketch> _sketches;

    /// <summary>Пользователь попросил новый эскиз под эту операцию.</summary>
    public bool NewSketchRequested { get; private set; }

    public ExtrudeForm(ExtrudeFeature f, Part part, ParameterTable pars)
    {
        _f = f;
        _params = pars;
        _sketches = part.Sketches.ToList();

        InitializeComponent();

        foreach (var s in _sketches) sketchCombo.Items.Add(s.Name);
        if (_sketches.Count == 0) sketchCombo.Items.Add("- no sketches -");

        int idx = _sketches.FindIndex(s => s.Id == f.SketchId);
        sketchCombo.SelectedIndex = idx >= 0 ? idx : 0;

        end1Combo.Items.AddRange(EndNames);
        end2Combo.Items.AddRange(EndNames);
        end1Combo.SelectedIndex = (int)f.Side1.End;
        end2Combo.SelectedIndex = (int)f.Side2.End;

        cutCheck.Checked = f.Cut;
        twoSidedCheck.Checked = f.TwoSided;
        symmetricCheck.Checked = f.Symmetric;

        depth1Box.Text = f.Side1.Distance.ToString();
        depth2Box.Text = f.Side2.Distance.ToString();
        flip1Check.Checked = f.Side1.Flip;
        flip2Check.Checked = f.Side2.Flip;

        UpdateSecondSide();
    }

    /// <summary>
    /// Вторая сторона имеет смысл только при двустороннем выдавливании
    /// и не имеет — при симметричном: там она равна первой.
    /// </summary>
    private void UpdateSecondSide() =>
        side2Group.Enabled = twoSidedCheck.Checked && !symmetricCheck.Checked;

    private void sidesChanged(object? sender, EventArgs e) => UpdateSecondSide();

    /// <summary>
    /// Окно закрывается, приложение создаёт эскиз и открывает его
    /// на черчение. Настройки операции сохраняются: они уже перенесены
    /// в саму операцию, а не живут в полях окна.
    /// </summary>
    private void newSketchButton_Click(object? sender, EventArgs e)
    {
        Apply();
        NewSketchRequested = true;
        DialogResult = DialogResult.Retry;
    }

    private void okButton_Click(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        if (_sketches.Count > 0 && sketchCombo.SelectedIndex >= 0
            && sketchCombo.SelectedIndex < _sketches.Count)
            _f.SketchId = _sketches[sketchCombo.SelectedIndex].Id;

        _f.Cut = cutCheck.Checked;
        _f.TwoSided = twoSidedCheck.Checked;
        _f.Symmetric = symmetricCheck.Checked;

        _f.Side1.End = (EndType)Math.Max(0, end1Combo.SelectedIndex);
        _f.Side2.End = (EndType)Math.Max(0, end2Combo.SelectedIndex);

        _f.Side1.Distance = Ui.ReadValue(depth1Box, _f.Side1.Distance, _params);
        _f.Side2.Distance = Ui.ReadValue(depth2Box, _f.Side2.Distance, _params);

        _f.Side1.Flip = flip1Check.Checked;
        _f.Side2.Flip = flip2Check.Checked;
    }
}
