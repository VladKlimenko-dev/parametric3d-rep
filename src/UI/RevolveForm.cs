using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>
/// Настройка вращения.
///
/// Ось выбирается не здесь, а в трёхмерном виде: окно закрывается,
/// вид переходит в режим указания сегмента, после щелчка окно
/// открывается снова с заполненным полем. Модальная форма не отдала бы
/// виду события мыши, а сегменты надо подсвечивать под курсором.
/// </summary>
public partial class RevolveForm : Form
{
    private readonly RevolveFeature _f;
    private readonly ParameterTable _params;
    private readonly List<Sketch> _sketches;

    /// <summary>Пользователь попросил указать ось на эскизе.</summary>
    public bool PickAxis { get; private set; }

    /// <summary>Пользователь попросил новый эскиз под эту операцию.</summary>
    public bool NewSketchRequested { get; private set; }

    /// <summary>Эскиз, выбранный в окне на момент закрытия.</summary>
    public Sketch? ChosenSketch =>
        sketchCombo.SelectedIndex >= 0 && sketchCombo.SelectedIndex < _sketches.Count
            ? _sketches[sketchCombo.SelectedIndex]
            : null;

    public RevolveForm(RevolveFeature f, Part part, ParameterTable pars)
    {
        _f = f;
        _params = pars;
        _sketches = part.Sketches.ToList();

        InitializeComponent();

        foreach (var s in _sketches) sketchCombo.Items.Add(s.Name);
        if (_sketches.Count == 0) sketchCombo.Items.Add("- no sketches -");

        int idx = _sketches.FindIndex(s => s.Id == f.SketchId);
        sketchCombo.SelectedIndex = idx >= 0 ? idx : 0;

        cutCheck.Checked = f.Cut;
        startBox.Text = f.StartAngle.ToString();
        endBox.Text = f.EndAngle.ToString();
        axisValue.Text = DescribeAxis(f, part);
    }

    private static string DescribeAxis(RevolveFeature f, Part part)
    {
        if (f.AxisEntityId == 0) return "sketch centerline";

        foreach (var sk in part.Sketches)
            foreach (var e in sk.Entities)
                if (e.Id == f.AxisEntityId) return $"segment #{e.Id}";

        return $"#{f.AxisEntityId}";
    }

    /// <summary>Переносит набранное в операцию. Нужно и при OK, и перед выбором оси.</summary>
    private void Apply()
    {
        if (_sketches.Count > 0 && sketchCombo.SelectedIndex >= 0
            && sketchCombo.SelectedIndex < _sketches.Count)
            _f.SketchId = _sketches[sketchCombo.SelectedIndex].Id;

        _f.Cut = cutCheck.Checked;
        _f.StartAngle = Ui.ReadValue(startBox, _f.StartAngle, _params);
        _f.EndAngle = Ui.ReadValue(endBox, _f.EndAngle, _params);
    }

    private void okButton_Click(object? sender, EventArgs e) => Apply();

    private void newSketchButton_Click(object? sender, EventArgs e)
    {
        Apply();
        NewSketchRequested = true;
        DialogResult = DialogResult.Retry;
    }

    private void axisSelectButton_Click(object? sender, EventArgs e)
    {
        Apply();
        PickAxis = true;
        DialogResult = DialogResult.Retry;
    }
}
