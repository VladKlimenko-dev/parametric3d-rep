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

    /// <summary>Деталь документа (у документа сборки — первая деталь, если есть).</summary>
    public Part? Part => Document.AllParts().FirstOrDefault();

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
        var part = Part ?? throw new InvalidOperationException("the document has no part");
        DocumentStorage.Save(path, part, Document.Parameters);
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

    /// <summary>Правка отменена («Отмена»): модель — как до BeginEdit, шаг из журнала убирается, повтора нет.</summary>
    public void CancelEdit()
    {
        var part = Part;
        if (part == null || !_history.CanUndo) return;
        var r = _history.Undo(part, Document.Parameters);
        _history.ClearRedo();
        if (_undoNames.Count > 0) _undoNames.RemoveAt(_undoNames.Count - 1);
        if (r != null) ReplacePart(part, r.Value.Part, r.Value.Parameters);
        StateChanged?.Invoke();
    }

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public IReadOnlyList<string> UndoNames => _undoNames;
    public IReadOnlyList<string> RedoNames => _redoNames;

    public void Undo()
    {
        var part = Part;
        if (part == null || !_history.CanUndo) return;
        var r = _history.Undo(part, Document.Parameters);
        if (r == null) return;
        var name = _undoNames.Count > 0 ? _undoNames[^1] : "";
        if (_undoNames.Count > 0) _undoNames.RemoveAt(_undoNames.Count - 1);
        _redoNames.Add(name);
        ReplacePart(part, r.Value.Part, r.Value.Parameters);
    }

    public void Redo()
    {
        var part = Part;
        if (part == null || !_history.CanRedo) return;
        var r = _history.Redo(part, Document.Parameters);
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
