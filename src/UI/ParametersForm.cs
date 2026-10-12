using Parametric3D.Model;
using Parametric3D.Model.Params;

namespace Parametric3D.UI;

/// <summary>
/// Таблица параметров документа.
///
/// Формулы вычисляются в порядке зависимостей; цикл A→B→A показывается
/// как ошибка в строке, а не как зависание.
/// </summary>
public sealed class ParametersForm : Form
{
    private readonly Document _doc;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();

    public ParametersForm(Document doc)
    {
        _doc = doc;
        Text = "Parameters";
        Size = new Size(680, 460);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Name = "name", Width = 150 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Type",
            Name = "kind",
            Width = 100,
            DataSource = new[] { "Real", "Integer", "String", "Formula", "Boolean" }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value / formula", Name = "value", Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", Name = "result", Width = 90, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Unit", Name = "unit", Width = 60 });

        _grid.CellEndEdit += (_, e) => Commit(e.RowIndex);

        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight };
        var bAdd = new Button { Text = "Add", Width = 100 };
        var bDel = new Button { Text = "Delete", Width = 100 };
        var bClose = new Button { Text = "Close", Width = 100 };
        bAdd.Click += (_, _) => { _doc.Parameters.Add(UniqueName(), 0); Reload(); };
        bDel.Click += (_, _) => DeleteSelected();
        bClose.Click += (_, _) => Close();
        panel.Controls.AddRange(new Control[] { bAdd, bDel, bClose });

        _status.Dock = DockStyle.Bottom;
        _status.Height = 22;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(panel);

        Reload();
    }

    private string UniqueName()
    {
        int i = 1;
        while (_doc.Parameters.Find($"Param{i}") != null) i++;
        return $"Param{i}";
    }

    private void Reload()
    {
        _doc.Parameters.Recalculate();
        _grid.Rows.Clear();
        foreach (var p in _doc.Parameters.All)
        {
            int r = _grid.Rows.Add(
                p.Name,
                KindText(p.Kind),
                p.Kind == ParamKind.Formula ? p.Formula : ValueText(p),
                p.Error ?? p.Value.ToString("0.####"),
                p.Unit);
            if (p.Error != null) _grid.Rows[r].DefaultCellStyle.ForeColor = Color.Firebrick;
            _grid.Rows[r].Tag = p;
        }
        int bad = _doc.Parameters.All.Count(p => p.Error != null);
        _status.Text = bad == 0
            ? $"Parameters: {_doc.Parameters.All.Count}"
            : $"Parameters: {_doc.Parameters.All.Count}, with errors: {bad}";
    }

    private static string ValueText(Parameter p) => p.Kind switch
    {
        ParamKind.String => p.Text,
        ParamKind.Integer => ((long)Math.Round(p.Value)).ToString(),
        ParamKind.Boolean => p.Flag ? "true" : "false",
        _ => p.Value.ToString("0.####")
    };

    private static string KindText(ParamKind k) => k switch
    {
        ParamKind.Integer => "Integer",
        ParamKind.String => "String",
        ParamKind.Formula => "Formula",
        ParamKind.Boolean => "Boolean",
        _ => "Real"
    };

    private static ParamKind KindFrom(string s) => s switch
    {
        "Integer" => ParamKind.Integer,
        "String" => ParamKind.String,
        "Formula" => ParamKind.Formula,
        "Boolean" => ParamKind.Boolean,
        _ => ParamKind.Real
    };

    private void Commit(int row)
    {
        if (row < 0 || _grid.Rows[row].Tag is not Parameter p) return;

        string name = Convert.ToString(_grid.Rows[row].Cells["name"].Value) ?? p.Name;
        string kind = Convert.ToString(_grid.Rows[row].Cells["kind"].Value) ?? KindText(p.Kind);
        string val = Convert.ToString(_grid.Rows[row].Cells["value"].Value) ?? "";
        string unit = Convert.ToString(_grid.Rows[row].Cells["unit"].Value) ?? "";

        if (name != p.Name) _doc.Parameters.Rename(p, name);
        p.Kind = KindFrom(kind);
        p.Unit = unit;

        if (p.Kind == ParamKind.Formula) p.Formula = val;
        else if (p.Kind == ParamKind.String) p.Text = val;
        else if (p.Kind == ParamKind.Boolean)
            p.Flag = val.Trim().ToLowerInvariant() is "true" or "1" or "yes";
        else if (double.TryParse(val.Replace(',', '.'),
                 System.Globalization.NumberStyles.Float,
                 System.Globalization.CultureInfo.InvariantCulture, out double d)) p.Number = d;

        _doc.Parameters.Recalculate();
        _doc.ParametersChanged();
        Reload();
    }

    private void DeleteSelected()
    {
        if (_grid.CurrentRow?.Tag is not Parameter p) return;
        var users = _doc.Parameters.UsagesOf(p);
        if (users.Count > 0)
        {
            MessageBox.Show(this,
                "This parameter is used by formulas: " + string.Join(", ", users.Select(u => u.Name)),
                "Cannot delete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _doc.Parameters.Remove(p.Name);
        _doc.ParametersChanged();
        Reload();
    }
}
