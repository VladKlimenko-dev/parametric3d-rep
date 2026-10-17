using Parametric3D.Model;
using Parametric3D.Model.Params;

namespace Parametric3D.App.Services;

/// <summary>
/// ОТКРЫТЫЙ ДОКУМЕНТ: модель (Model.Document), путь к файлу, признак изменений, журнал отмены.
///
/// Документ детали — одна деталь в корневой сборке (UI-43: деталь — одно тело). Отмена и повтор — снимками детали
/// (Model.UndoHistory, формат файла): каждая правка модели идёт через Edit(имя, действие) — снимок до правки
/// записывается сам, и одна правка — один шаг отмены (UI-22). Журнал хранит имена шагов (UI-37).
/// </summary>
public sealed class DocumentSession
{
    public Document Document { get; private set; }
    public string? FilePath { get; private set; }
    public bool Modified { get; private set; }

    private readonly UndoHistory _history = new();
    private readonly List<string> _undoNames = new(), _redoNames = new();

    /// <summary>Модель заменена целиком (открытие, отмена, повтор) — вид и дерево перестраиваются.</summary>
    public event Action? Replaced;
    /// <summary>Изменилось что-то, что видно в заголовке, журнале или кнопках отмены.</summary>
    public event Action? StateChanged;

    private DocumentSession(Document doc, string? path)
    {
        Document = doc;
        FilePath = path;
        Hook();
    }

    private void Hook()
    {
        Document.Root.Changed += _ => { if (!Modified) { Modified = true; StateChanged?.Invoke(); } };
    }

    /// <summary>Изменение без шага отмены (видимость глазом в дереве): только отметка «изменён».</summary>
    public void MarkModified()
    {
        if (Modified) return;
        Modified = true;
        StateChanged?.Invoke();
    }

    public string Title => FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : Document.Title;

    /// <summary>Деталь документа (у документа сборки — компонент, правимый в контексте, иначе первая деталь).</summary>
    public Part? Part => ActivePart != null && Document.AllParts().Contains(ActivePart) ? ActivePart : (IsAssembly ? null : Document.AllParts().FirstOrDefault());

    /// <summary>u13: документ сборки (компоненты — детали корня).</summary>
    public bool IsAssembly { get; private set; }
    public Assembly RootAssembly => Document.Root;
    /// <summary>u13: компонент, правимый в контексте сборки (null — режим сборки).</summary>
    public Part? ActivePart { get; set; }
    public const string AssemblyExtension = ".p3a";

    /// <summary>u13: новая сборка — пустой корень с плоскостями.</summary>
    public static DocumentSession NewAssembly()
    {
        string name = Loc.L("Сборка") + (++_counter);
        var doc = new Document(name);
        return new DocumentSession(doc, null) { IsAssembly = true };
    }

    /// <summary>u13: открыть сборку (.p3a): компоненты — в корень документа.</summary>
    public static DocumentSession OpenAssembly(string path)
    {
        var (asm, parameters) = AssemblyFile.Load(File.ReadAllText(path));
        var doc = new Document(Path.GetFileNameWithoutExtension(path));
        MoveInto(asm, doc.Root);
        CopyParameters(parameters, doc.Parameters);
        doc.FilePath = path;
        return new DocumentSession(doc, path) { IsAssembly = true, Modified = false };
    }

    static void MoveInto(Assembly from, Assembly to)
    {
        foreach (var c in from.Children.Where(c => c is Parametric3D.Model.Part or Parametric3D.Model.Assembly).ToList()) { from.Children.Remove(c); c.Parent = null; to.Add(c); }
        to.ExplodedViews.Clear(); to.ExplodedViews.AddRange(from.ExplodedViews);
    }

    // ================================================================ создание и файлы

    private static int _counter;

    /// <summary>Новая деталь: три плоскости и начало координат (Д-01).</summary>
    public static DocumentSession NewPart()
    {
        string name = Loc.L("Деталь") + (++_counter);
        var doc = new Document(name);
        doc.Root.AddPart(name);
        return new DocumentSession(doc, null);
    }

    /// <summary>Документ из готовой детали (кнопка «Тест»).</summary>
    public static DocumentSession FromPart(Part part, string title)
    {
        var doc = new Document(title);
        doc.Root.Add(part);
        return new DocumentSession(doc, null);
    }

    /// <summary>Пробная деталь — куб 100 (для показа и проверок вида).</summary>
    public static DocumentSession Sample()
    {
        var s = NewPart();
        TestGeometry.AddCube(s.Part!, 100);
        s.Modified = false;
        return s;
    }

    public const string PartExtension = ".p3d";

    public static DocumentSession Open(string path)
    {
        if (path.EndsWith(AssemblyExtension, StringComparison.OrdinalIgnoreCase)) return OpenAssembly(path);
        var (part, parameters) = DocumentStorage.Load(path);
        var doc = new Document(Path.GetFileNameWithoutExtension(path));
        doc.Root.Add(part);
        CopyParameters(parameters, doc.Parameters);
        doc.FilePath = path;
        var s = new DocumentSession(doc, path);
        s.Modified = false;
        return s;
    }

    /// <summary>Импорт STEP/IGES: тела без истории — новая деталь (позже — FreeForm-тело, раздел 24).</summary>
    public static DocumentSession Import(string path, List<string> warnings)
    {
        string text = File.ReadAllText(path);
        var part = Exchange.Read(text, out _, warnings) ?? throw new InvalidDataException(string.Join("\n", warnings));
        part.Name = Path.GetFileNameWithoutExtension(path);
        var doc = new Document(part.Name);
        doc.Root.Add(part);
        return new DocumentSession(doc, null) { Modified = true };
    }

    public void Save() => SaveAs(FilePath ?? throw new InvalidOperationException("path is not set"));

    public void SaveAs(string path)
    {
        if (IsAssembly) DocumentStorage.WriteAtomic(path, AssemblyFile.Save(Document.Root, Document.Parameters));
        else
        {
            var part = Part ?? throw new InvalidOperationException("the document has no part");
            DocumentStorage.Save(path, part, Document.Parameters);
        }
        FilePath = path;
        Document.FilePath = path;
        Modified = false;
        StateChanged?.Invoke();
    }

    // ================================================================ правка и отмена

    /// <summary>
    /// Правка модели одним шагом отмены: снимок до правки, действие, отметка «изменён». Исключение действия откатывает
    /// снимок (модель возвращается как была) и уходит дальше.
    /// </summary>
    public void Edit(string name, Action action)
    {
        var part = Part;
        BeginEdit(name);
        try { action(); }
        catch
        {
            // правка упала на полпути — модель возвращается к снимку до неё
            if (part != null) CancelEdit();
            throw;
        }
        Modified = true;
        StateChanged?.Invoke();
    }

    /// <summary>Начать правку, которая длится (панель операции, эскиз): снимок до неё — один шаг отмены.</summary>
    public void BeginEdit(string name)
    {
        if (IsAssembly)
        {
            _asmUndo.Add(AssemblyFile.Save(Document.Root, Document.Parameters)); _asmRedo.Clear();
            _undoNames.Add(name); _redoNames.Clear();
            return;
        }
        var part = Part;
        if (part == null) return;
        _history.Record(part, Document.Parameters);
        _undoNames.Add(name);
        _redoNames.Clear();
    }

    /// <summary>Правка закончена («ОК»): имя шага можно уточнить.</summary>
    public void CommitEdit(string? name = null)
    {
        if (name != null && _undoNames.Count > 0) _undoNames[^1] = name;
        Modified = true;
        StateChanged?.Invoke();
    }

    /// <summary>Правка отменена без замены детали (вызывающий сам вернул модель): снимок и имя шага забываются.</summary>
    public void DiscardEdit()
    {
        if (IsAssembly) { if (_asmUndo.Count > 0) _asmUndo.RemoveAt(_asmUndo.Count - 1); if (_undoNames.Count > 0) _undoNames.RemoveAt(_undoNames.Count - 1); StateChanged?.Invoke(); return; }
        _history.DropLast();
        if (_undoNames.Count > 0) _undoNames.RemoveAt(_undoNames.Count - 1);
        StateChanged?.Invoke();
    }

    /// <summary>Правка отменена («Отмена»): модель — как до BeginEdit, шаг из журнала убирается, повтора нет.</summary>
    public void CancelEdit()
    {
        if (IsAssembly)
        {
            if (_asmUndo.Count == 0) return;
            var snap = _asmUndo[^1]; _asmUndo.RemoveAt(_asmUndo.Count - 1);
            if (_undoNames.Count > 0) _undoNames.RemoveAt(_undoNames.Count - 1);
            RestoreAssembly(snap);
            StateChanged?.Invoke();
            return;
        }
        var part = Part;
        if (part == null || !_history.CanUndo) return;
        var r = _history.Undo(part, Document.Parameters, rebuild: false);
        _history.ClearRedo();
        if (_undoNames.Count > 0) _undoNames.RemoveAt(_undoNames.Count - 1);
        if (r != null) ReplacePart(part, r.Value.Part, r.Value.Parameters);
        StateChanged?.Invoke();
    }

    public bool CanUndo => IsAssembly ? _asmUndo.Count > 0 : _history.CanUndo;
    public bool CanRedo => IsAssembly ? _asmRedo.Count > 0 : _history.CanRedo;
    private readonly List<string> _asmUndo = new(), _asmRedo = new();

    /// <summary>u13: сборка из снимка — компоненты заменяются; правимый в контексте — тот же по номеру.</summary>
    private void RestoreAssembly(string json)
    {
        int active = ActivePart == null ? -1 : Document.AllParts().ToList().IndexOf(ActivePart);
        var (asm, parameters) = AssemblyFile.Load(json);
        foreach (var c in Document.Root.Children.Where(c => c is Parametric3D.Model.Part or Parametric3D.Model.Assembly).ToList()) Document.Root.Remove(c);
        MoveInto(asm, Document.Root);
        CopyParameters(parameters, Document.Parameters);
        ActivePart = active >= 0 ? Document.AllParts().ElementAtOrDefault(active) : null;
        Modified = true;
        Replaced?.Invoke();
    }
    public IReadOnlyList<string> UndoNames => _undoNames;
    public IReadOnlyList<string> RedoNames => _redoNames;

    public void Undo()
    {
        if (IsAssembly)
        {
            if (_asmUndo.Count == 0) return;
            _asmRedo.Add(AssemblyFile.Save(Document.Root, Document.Parameters));
            var snap = _asmUndo[^1]; _asmUndo.RemoveAt(_asmUndo.Count - 1);
            var nm = _undoNames.Count > 0 ? _undoNames[^1] : ""; if (_undoNames.Count > 0) _undoNames.RemoveAt(_undoNames.Count - 1); _redoNames.Add(nm);
            RestoreAssembly(snap); StateChanged?.Invoke();
            return;
        }
        var part = Part;
        if (part == null || !_history.CanUndo) return;
        var r = _history.Undo(part, Document.Parameters, rebuild: false);
        if (r == null) return;
        var name = _undoNames.Count > 0 ? _undoNames[^1] : "";
        if (_undoNames.Count > 0) _undoNames.RemoveAt(_undoNames.Count - 1);
        _redoNames.Add(name);
        ReplacePart(part, r.Value.Part, r.Value.Parameters);
    }

    public void Redo()
    {
        if (IsAssembly)
        {
            if (_asmRedo.Count == 0) return;
            _asmUndo.Add(AssemblyFile.Save(Document.Root, Document.Parameters));
            var snap = _asmRedo[^1]; _asmRedo.RemoveAt(_asmRedo.Count - 1);
            var nm = _redoNames.Count > 0 ? _redoNames[^1] : ""; if (_redoNames.Count > 0) _redoNames.RemoveAt(_redoNames.Count - 1); _undoNames.Add(nm);
            RestoreAssembly(snap); StateChanged?.Invoke();
            return;
        }
        var part = Part;
        if (part == null || !_history.CanRedo) return;
        var r = _history.Redo(part, Document.Parameters, rebuild: false);
        if (r == null) return;
        var name = _redoNames.Count > 0 ? _redoNames[^1] : "";
        if (_redoNames.Count > 0) _redoNames.RemoveAt(_redoNames.Count - 1);
        _undoNames.Add(name);
        ReplacePart(part, r.Value.Part, r.Value.Parameters);
    }

    /// <summary>Отменить до шага журнала (0 — до первого): несколько отмен подряд.</summary>
    public void UndoTo(int count)
    {
        for (int i = 0; i < count && CanUndo; i++) Undo();
    }

    private void ReplacePart(Part old, Part fresh, ParameterTable parameters)
    {
        var root = Document.Root;
        int i = root.Children.IndexOf(old);
        root.Remove(old);
        root.Add(fresh);
        if (i >= 0 && i < root.Children.Count - 1)
        {
            root.Children.Remove(fresh);
            root.Children.Insert(i, fresh);
        }
        CopyParameters(parameters, Document.Parameters);
        Modified = true;
        Replaced?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>Перенести параметры (таблица документа — поле только для чтения).</summary>
    public static void CopyParameters(ParameterTable from, ParameterTable to)
    {
        foreach (var p in to.All.ToList()) to.Remove(p.Name);
        foreach (var p in from.All)
        {
            var q = p.Kind == ParamKind.Formula ? to.AddFormula(p.Name, p.Formula, p.Unit) : to.Add(p.Name, p.Number, p.Kind, p.Unit);
            q.Text = p.Text; q.Flag = p.Flag; q.Comment = p.Comment;
        }
        to.Recalculate();
    }
}
