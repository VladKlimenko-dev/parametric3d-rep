using Parametric3D.Model.Sketching;

namespace Parametric3D.UI;

/// <summary>
/// Ссылочная геометрия эскиза: список того, на что эскиз опирается,
/// с возможностью удалить лишнее.
///
/// В списке ДВА рода строк.
///
/// Основа — плоскость-базис и нулевая точка. Без них эскиза не существует:
/// плоскость задаёт, где он лежит, ноль — откуда отсчитываются координаты.
/// Поэтому их удаление не запрещено, но выйти из окна с неполной основой
/// нельзя: кнопка Close выключается, а причина написана прямо в окне.
///
/// Проекции — элементы с ролью Reference. Отдельного хранилища для них нет
/// намеренно: ссылка это элемент эскиза, и второй список рядом означал бы
/// два источника правды.
/// </summary>
public partial class SketchReferencesForm : Form
{
    private readonly Sketch _sketch;

    /// <summary>Что-то изменено — вызывающему надо перестроить вид.</summary>
    public bool Changed { get; private set; }

    // Убранная основа. Хранится, чтобы её можно было вернуть: иначе окно
    // превратилось бы в ловушку — Close выключен, а восстановить нечем.
    private bool _planeRemoved;
    private bool _originRemoved;
    private string _savedPlane = "";
    private string _savedOrigin = "";
    private uint _savedPlaneId;

    public SketchReferencesForm(Sketch sketch)
    {
        _sketch = sketch;
        InitializeComponent();
        Reload();
    }

    /// <summary>Строка списка: либо основа, либо ссылочный элемент.</summary>
    private sealed class Row
    {
        public bool IsPlane;
        public bool IsOrigin;
        public SkEntity? Entity;
    }

    private void Reload()
    {
        list.BeginUpdate();
        list.Items.Clear();

        if (!_planeRemoved) Add("Base plane", _sketch.PlaneDescription, _sketch.BasePlaneId,
                                new Row { IsPlane = true });

        if (!_originRemoved) Add("Origin", _sketch.OriginDescription, 0,
                                 new Row { IsOrigin = true });

        foreach (var e in _sketch.Entities)
        {
            if (e.Role != SkRole.Reference) continue;
            Add(e.Kind.ToString(),
                e.ReferenceSource.Length > 0 ? e.ReferenceSource : "(manual)",
                e.Id,
                new Row { Entity = e });
        }

        list.EndUpdate();
        UpdateState();

        void Add(string kind, string source, uint id, Row tag)
        {
            var row = new ListViewItem(kind);
            row.SubItems.Add(source);
            row.SubItems.Add(id == 0 ? "" : id.ToString());
            row.Tag = tag;

            // Основа выделена начертанием: её нельзя убирать просто так.
            if (tag.Entity == null) row.Font = new Font(list.Font, FontStyle.Bold);

            list.Items.Add(row);
        }
    }

    /// <summary>Основа неполна — эскиз в таком виде не существует.</summary>
    private bool BasisIncomplete => _planeRemoved || _originRemoved;

    private void UpdateState()
    {
        deleteButton.Enabled = list.SelectedItems.Count > 0;
        restoreButton.Enabled = BasisIncomplete;
        closeButton.Enabled = !BasisIncomplete;

        if (BasisIncomplete)
        {
            hintLabel.ForeColor = Color.FromArgb(190, 55, 45);
            hintLabel.Text = "Sketch needs minimum basis elements - plane and origin";
            return;
        }

        hintLabel.ForeColor = SystemColors.GrayText;

        int refs = 0;
        foreach (var e in _sketch.Entities) if (e.Role == SkRole.Reference) refs++;

        hintLabel.Text = refs == 0
            ? "Basis only. Hover other geometry in the sketch and click to project it."
            : $"Basis plus {refs} projected item(s).";
    }

    private void list_SelectedIndexChanged(object? sender, EventArgs e) => UpdateState();

    private void deleteButton_Click(object? sender, EventArgs e)
    {
        // Собираем заранее: удаление меняет и эскиз, и сам список.
        var entities = new List<SkEntity>();
        bool plane = false, origin = false;

        foreach (ListViewItem row in list.SelectedItems)
        {
            if (row.Tag is not Row r) continue;
            if (r.IsPlane) plane = true;
            else if (r.IsOrigin) origin = true;
            else if (r.Entity != null) entities.Add(r.Entity);
        }

        if (plane)
        {
            _savedPlane = _sketch.PlaneDescription;
            _savedPlaneId = _sketch.BasePlaneId;
            _planeRemoved = true;
        }

        if (origin)
        {
            _savedOrigin = _sketch.OriginDescription;
            _originRemoved = true;
        }

        foreach (var se in entities) _sketch.RemoveEntity(se);

        if (plane || origin || entities.Count > 0) Changed = true;
        Reload();
    }

    /// <summary>Возвращает убранную основу.</summary>
    private void restoreButton_Click(object? sender, EventArgs e)
    {
        if (_planeRemoved)
        {
            _sketch.PlaneDescription = _savedPlane;
            _sketch.BasePlaneId = _savedPlaneId;
            _planeRemoved = false;
        }

        if (_originRemoved)
        {
            _sketch.OriginDescription = _savedOrigin;
            _originRemoved = false;
        }

        Reload();
    }

    private void closeButton_Click(object? sender, EventArgs e)
    {
        if (BasisIncomplete) return;      // страховка: кнопка и так выключена
        Close();
    }
}
