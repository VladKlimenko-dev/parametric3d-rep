using OpenTK.Mathematics;
using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>
/// Настройки эскиза: имя, плоскость-базис, нулевая точка, сетка.
///
/// Окно НЕМОДАЛЬНОЕ и во время выбора плоскости или нулевой точки
/// остаётся открытым.
///
/// Модальное окно перехватывает все события мыши, и вид не получал бы ни
/// наведения, ни щелчков. Прошлый вариант обходил это закрытием окна на
/// время выбора — но тогда набранное приходилось переносить в эскиз
/// заранее, а нажать OK было уже нельзя. Немодальное окно снимает вопрос:
/// вид работает, а поля обновляются на месте, по событию выбора.
/// </summary>
public partial class SketchSetupForm : Form
{
    /// <summary>Пользователь нажал «выбрать плоскость».</summary>
    public event Action<Sketch>? PlanePickRequested;

    /// <summary>Пользователь нажал «выбрать нулевую точку».</summary>
    public event Action<Sketch>? OriginPickRequested;

    /// <summary>Настройки применены и окно закрывается.</summary>
    public event Action<Sketch>? Applied;

    /// <summary>Окно закрылось — вызывающему пора убрать за собой.</summary>
    public event Action? Dismissed;

    /// <summary>Эскиз перевёрнут кнопкой Flip: вид надо перерисовать.</summary>
    public event Action<Sketch>? Flipped;

    /// <summary>Изменено направление «вверх»: оси эскиза повёрнуты.</summary>
    public event Action<Sketch>? UpChanged;

    private readonly Sketch _sketch;

    public SketchSetupForm(Sketch sketch)
    {
        _sketch = sketch;
        InitializeComponent();

        nameBox.Text = sketch.Name;
        snapCheck.Checked = sketch.SnapToGrid;
        stepBox.Text = sketch.GridStep.ToString("0.###",
            System.Globalization.CultureInfo.InvariantCulture);

        planeValue.Text = sketch.PlaneDescription;
        originValue.Text = sketch.OriginDescription;

        upXBox.Value = ToDecimal(sketch.UpVector.X);
        upYBox.Value = ToDecimal(sketch.UpVector.Y);
    }

    private static decimal ToDecimal(double v) =>
        (decimal)Math.Clamp(Math.Round(v, 3), -1000, 1000);

    /// <summary>Переносит набранное в эскиз. Вызывается и при OK, и перед выбором.</summary>
    private void Apply()
    {
        string name = nameBox.Text.Trim();
        if (name.Length > 0) _sketch.Name = name;

        _sketch.SnapToGrid = snapCheck.Checked;

        if (double.TryParse(stepBox.Text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double step) && step > 0)
            _sketch.GridStep = step;

        // Направление «вверх» применяется, только если оно изменилось:
        // иначе каждое OK пересчитывало бы оси заново без нужды.
        var up = new Vector2d((double)upXBox.Value, (double)upYBox.Value);
        if (up.LengthSquared > 1e-12 && (up - _sketch.UpVector).Length > 1e-9)
        {
            _sketch.SetUpVector(up);
            UpChanged?.Invoke(_sketch);
        }
    }

    /// <summary>Обновляет поля после выбора в трёхмерном виде.</summary>
    public void RefreshFromSketch()
    {
        planeValue.Text = _sketch.PlaneDescription;
        originValue.Text = _sketch.OriginDescription;
        upXBox.Value = ToDecimal(_sketch.UpVector.X);
        upYBox.Value = ToDecimal(_sketch.UpVector.Y);
    }

    private void okButton_Click(object? sender, EventArgs e)
    {
        Apply();
        Applied?.Invoke(_sketch);
        Close();
    }

    private void cancelButton_Click(object? sender, EventArgs e) => Close();

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Dismissed?.Invoke();
    }

    private void planeSelectButton_Click(object? sender, EventArgs e)
    {
        Apply();
        PlanePickRequested?.Invoke(_sketch);
    }

    private void originSelectButton_Click(object? sender, EventArgs e)
    {
        Apply();
        OriginPickRequested?.Invoke(_sketch);
    }

    /// <summary>
    /// Переворот эскиза: взгляд с обратной стороны плоскости-базиса.
    ///
    /// Меняется знак нормали, а вместе с ним и одна из осей в плоскости —
    /// иначе система координат стала бы левой, и все построения оказались
    /// бы зеркальными.
    /// </summary>
    private void flipButton_Click(object? sender, EventArgs e)
    {
        Apply();
        _sketch.Flip();
        RefreshFromSketch();
        Flipped?.Invoke(_sketch);
    }
}
