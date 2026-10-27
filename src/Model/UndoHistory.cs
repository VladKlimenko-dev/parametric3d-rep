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
    /// <summary>
    /// k122: расчёт детали, снятый вместе со снимком (ключ — сам снимок): отмена и повтор берут сохранённые состояния
    /// операций отсюда и не пересчитывают ничего. Держатся для последних StashLimit снимков (память — тела состояний).
    /// </summary>
    readonly Dictionary<string, Part.BuildStash> _stash = new(ReferenceEqualityComparer.Instance);
    readonly List<string> _stashOrder = new();
    public int StashLimit { get; init; } = 8;
    /// <summary>Разбор: последнее восстановление взяло расчёт из снимка (с какой операции пойдёт пересчёт; −1 — нет).</summary>
    public int LastStashFrom { get; private set; } = -1;

    void Keep(string json, Part part)
    {
        if (part.StashBuild() is not { } st0) return;
        var st = st0.Detached();
        _stash[json] = st; _stashOrder.Add(json);
        while (_stashOrder.Count > StashLimit) { _stash.Remove(_stashOrder[0]); _stashOrder.RemoveAt(0); }
    }

    /// <summary>Сколько шагов отмены хранится (старые отбрасываются).</summary>
    public int Limit { get; init; } = 200;

    /// <summary>Разбор: сколько операций начала дерева перенято при последнем восстановлении (−1 — не перенято).</summary>
    public int LastAdopted { get; private set; } = -1;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>Записать состояние ПЕРЕД правкой.</summary>
    public void Record(Part part, ParameterTable? parameters = null)
    {
        var json = DocumentFile.Save(part, parameters);
        _undo.Add(json);
        Keep(json, part);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Отменить: текущее состояние уходит в повтор, возвращается деталь предыдущего снимка (перестроенная).</summary>
    public (Part Part, ParameterTable Parameters)? Undo(Part current, ParameterTable? parameters = null, bool rebuild = true)
    {
        if (_undo.Count == 0) return null;
        var now = DocumentFile.Save(current, parameters);
        _redo.Add(now); Keep(now, current);
        var snap = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        var r = Restore(snap, rebuild, current, now, out int k); LastAdopted = k; return r;
    }

    /// <summary>Повторить отменённое.</summary>
    public (Part Part, ParameterTable Parameters)? Redo(Part current, ParameterTable? parameters = null, bool rebuild = true)
    {
        if (_redo.Count == 0) return null;
        var now = DocumentFile.Save(current, parameters);
        _undo.Add(now); Keep(now, current);
        var snap = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        var r = Restore(snap, rebuild, current, now, out int k); LastAdopted = k; return r;
    }

    /// <summary>UI: забыть последний снимок (правка отменена до того, как что-то изменила, — «Отмена» в панели операции).</summary>
    public void DropLast() { if (_undo.Count > 0) { _stash.Remove(_undo[^1]); _undo.RemoveAt(_undo.Count - 1); } }

    /// <summary>UI: очистить повтор (отменённая через «Отмена» правка не должна возвращаться повтором).</summary>
    public void ClearRedo() { foreach (var r in _redo) _stash.Remove(r); _redo.Clear(); }

    /// <summary>rebuild: false — деталь возвращается «грязной», перестраивает её вызывающий (приложение — в фоне, без остановки окна).</summary>
    (Part Part, ParameterTable Parameters) Restore(string snap, bool rebuild, Part? current, string? currentJson, out int adopted)
    {
        var (p, t) = DocumentFile.Load(snap);
        // k122: расчёт, снятый вместе с этим снимком, — без пересчёта операций
        LastStashFrom = _stash.TryGetValue(snap, out var st) ? p.AdoptStash(st) : -1;
        // u-нагрузка: общая с текущей деталью начальная часть дерева не пересчитывается (сохранённые состояния переносятся)
        adopted = LastStashFrom >= 0 ? LastStashFrom : current != null && currentJson != null ? p.AdoptBuildState(current, currentJson, snap) : -1;
        if (p.LocalParameters.Count > 0) { p.LocalParameters.Parent = t; p.LocalParameters.Recalculate(); }
        if (rebuild) p.Rebuild(p.LocalParameters.Count > 0 ? p.LocalParameters : t);
        return (p, t);
    }
}
