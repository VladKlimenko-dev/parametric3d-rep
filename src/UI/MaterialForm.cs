using OpenTK.Mathematics;
using Parametric3D.Model;

namespace Parametric3D.UI;

/// <summary>
/// Материал детали: плотность влияет на массу в статусной строке,
/// цвет и блеск — на отрисовку граней.
/// </summary>
public sealed class MaterialForm : Form
{
    public MaterialForm(Part part, IReadOnlyList<Material> library)
    {
        Text = "Material";
        Size = new Size(380, 260);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;

        var list = Ui.Combo(140, 18, 200, library.Select(m => m.Name).ToArray());
        int idx = library.ToList().FindIndex(m => m.Name == part.Material.Name);
        if (idx >= 0) list.SelectedIndex = idx;

        var density = Ui.Box(part.Material.Density.ToString("0.##"), 140, 52, 120);
        var colorBtn = new Button { Text = "Choose...", Left = 140, Top = 86, Width = 120 };
        var preview = new Panel
        {
            Left = 270, Top = 86, Width = 60, Height = 24, BorderStyle = BorderStyle.FixedSingle,
            BackColor = ToColor(part.Material.Color)
        };

        colorBtn.Click += (_, _) =>
        {
            using var cd = new ColorDialog { Color = preview.BackColor };
            if (cd.ShowDialog(this) == DialogResult.OK) preview.BackColor = cd.Color;
        };

        list.SelectedIndexChanged += (_, _) =>
        {
            var m = library[list.SelectedIndex];
            density.Text = m.Density.ToString("0.##");
            preview.BackColor = ToColor(m.Color);
        };

        var info = Ui.Lab($"Volume: {part.Volume / 1000.0:0.##} cm\u00b3", 12, 124, 320);

        Controls.AddRange(new Control[]
        {
            Ui.Lab("Material:", 12, 18), list,
            Ui.Lab("Density, kg/m\u00b3:", 12, 52), density,
            Ui.Lab("Color:", 12, 86), colorBtn, preview,
            info
        });

        Ui.Buttons(this, () =>
        {
            var chosen = library[Math.Max(0, list.SelectedIndex)];

            // Копия, а не ссылка: правка цвета одной детали не должна
            // менять материал в библиотеке и во всех остальных деталях.
            var m = new Material(chosen.Name)
            {
                Density = chosen.Density,
                Specular = chosen.Specular,
                Shininess = chosen.Shininess,
                Opacity = chosen.Opacity,
                Color = ToVec(preview.BackColor)
            };

            if (double.TryParse(density.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0)
                m.Density = d;

            part.Material = m;
            // K06: материал меняет массу и вид, но не геометрию — ядро не запускается.
            part.AppearanceChanged();
        });
    }

    private static Color ToColor(Vector4 c) => Color.FromArgb(
        (int)Math.Clamp(c.X * 255f, 0, 255),
        (int)Math.Clamp(c.Y * 255f, 0, 255),
        (int)Math.Clamp(c.Z * 255f, 0, 255));

    private static Vector4 ToVec(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
}
