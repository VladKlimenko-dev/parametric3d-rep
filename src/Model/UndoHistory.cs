using Parametric3D.Model.Params;

namespace Parametric3D.Model;

/// <summary>
/// KRN-08 (08.7): ОТМЕНА И ПОВТОР ПО СНИМКАМ ДОКУМЕНТА. Перед каждой правкой записывается снимок (тот же формат, что при
/// сохранении файла: замысел — операции, параметры, ссылки, без геометрии); отмена восстанавливает предыдущий снимок и
/// перестраивает деталь, повтор — снимок, снятый при отмене. Новая правка после отмены очищает повтор. Снимок, а не обратная
/// команда: восстанавливается ровно то, что было записано, без ручных обратных операций для каждого вида правки.
/// </summary>
public sealed class UndoHistory
{
    readonly List<string> _undo = new(), _redo = new();

    /// <summary>Сколько шагов отмены хранится (старые отбрасываются).</summary>
    public int Limit { get; init; } = 200;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>Записать состояние ПЕРЕД правкой.</summary>
    public void Record(Part part, ParameterTable? parameters = null)
    {
        _undo.Add(DocumentFile.Save(part, parameters));
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Отменить: текущее состояние уходит в повтор, возвращается деталь предыдущего снимка (перестроенная).</summary>
    public (Part Part, ParameterTable Parameters)? Undo(Part current, ParameterTable? parameters = null, bool rebuild = true)
    {
        if (_undo.Count == 0) return null;
        _redo.Add(DocumentFile.Save(current, parameters));
        var snap = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        return Restore(snap, rebuild);
    }

    /// <summary>Повторить отменённое.</summary>
    public (Part Part, ParameterTable Parameters)? Redo(Part current, ParameterTable? parameters = null, bool rebuild = true)
    {
        if (_redo.Count == 0) return null;
        _undo.Add(DocumentFile.Save(current, parameters));
        var snap = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        return Restore(snap, rebuild);
    }

    /// <summary>UI: забыть последний снимок (правка отменена до того, как что-то изменила, — «Отмена» в панели операции).</summary>
    public void DropLast() { if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1); }

    /// <summary>UI: очистить повтор (отменённая через «Отмена» правка не должна возвращаться повтором).</summary>
    public void ClearRedo() => _redo.Clear();

    /// <summary>rebuild: false — деталь возвращается «грязной», перестраивает её вызывающий (приложение — в фоне, без остановки окна).</summary>
    static (Part Part, ParameterTable Parameters) Restore(string snap, bool rebuild)
    {
        var (p, t) = DocumentFile.Load(snap);
        if (p.LocalParameters.Count > 0) { p.LocalParameters.Parent = t; p.LocalParameters.Recalculate(); }
        if (rebuild) p.Rebuild(p.LocalParameters.Count > 0 ? p.LocalParameters : t);
        return (p, t);
    }
}
