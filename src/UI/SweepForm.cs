using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>Настройка протягивания: сечение, путь, доля длины пути.</summary>
public partial class SweepForm : Form
{
    private readonly SweepFeature _f;
    private readonly ParameterTable _params;
    private readonly List<Sketch> _sketches;

    /// <summary>Пользователь попросил новый эскиз под сечение.</summary>
    public bool NewSketchRequested { get; private set; }

    /// <summary>Пользователь попросил указать путь щелчком в трёхмерном виде.</summary>
    public bool PickPath { get; private set; }

    public SweepForm(SweepFeature f, Part part, ParameterTable pars)
    {
        _f = f;
        _params = pars;
        _sketches = part.Sketches.ToList();

        InitializeComponent();

        foreach (var s in _sketches)
        {
            sectionCombo.Items.Add(s.Name);
            pathCombo.Items.Add(s.Name);
        }
        if (_sketches.Count == 0)
        {
            sectionCombo.Items.Add("- no sketches -");
            pathCombo.Items.Add("- no sketches -");
        }

        int i1 = _sketches.FindIndex(s => s.Id == f.SectionSketchId);
        int i2 = _sketches.FindIndex(s => s.Id == f.PathSketchId);
        sectionCombo.SelectedIndex = i1 >= 0 ? i1 : 0;
        pathCombo.SelectedIndex = i2 >= 0 ? i2 : 0;

        cutCheck.Checked = f.Cut;
        lengthBox.Text = f.LengthFraction.ToString();
    }

    private void okButton_Click(object? sender, EventArgs e) => Apply();

    private void newSectionButton_Click(object? sender, EventArgs e)
    {
        Apply();
        NewSketchRequested = true;
        DialogResult = DialogResult.Retry;
    }

    /// <summary>
    /// Путь выбирается щелчком по эскизу в трёхмерном виде: в списке
    /// эскизы легко перепутать, а на модели видно, какой из них где.
    /// </summary>
    private void pathSelectButton_Click(object? sender, EventArgs e)
    {
        Apply();
        PickPath = true;
        DialogResult = DialogResult.Retry;
    }

    private void Apply()
    {
        if (_sketches.Count == 0) return;

        if (sectionCombo.SelectedIndex >= 0 && sectionCombo.SelectedIndex < _sketches.Count)
            _f.SectionSketchId = _sketches[sectionCombo.SelectedIndex].Id;

        if (pathCombo.SelectedIndex >= 0 && pathCombo.SelectedIndex < _sketches.Count)
            _f.PathSketchId = _sketches[pathCombo.SelectedIndex].Id;

        _f.Cut = cutCheck.Checked;
        _f.LengthFraction = Ui.ReadValue(lengthBox, _f.LengthFraction, _params);
    }
}
