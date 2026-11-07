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
    public bool IsAssembly { get => Document.IsAssembly; private set => Document.IsAssembly = value; }
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
    /// <summary>
    /// k119: ИМПОРТ STL — новая деталь с операцией «Импорт STL» (треугольники файла и настройки перевода хранятся в ней;
    /// тело — точная геометрия или плоские грани — строится перестроением).
    /// </summary>
    public static DocumentSession ImportMesh(string path, bool exact, bool snap, double tolScale, double scale)
    {
        var tris = Parametric3D.Kernel.MeshFit.StlFile.ReadFile(path);
        if (tris.Count == 0) throw new InvalidDataException(Loc.L("В файле нет треугольников."));
        string name = Path.GetFileNameWithoutExtension(path);
        var part = new Part(name);
        part.AddFeature(new Parametric3D.Model.Features.MeshImportFeature(Loc.L("Импорт STL «{0}»", name))
        {
            SourceName = Path.GetFileName(path), MeshData = Parametric3D.Model.Features.MeshImportFeature.Pack(tris),
            Exact = exact, Snap = snap, TolScale = tolScale, Scale = scale,
        });
        var doc = new Document(name);
        doc.Root.Add(part);
        return new DocumentSession(doc, null) { Modified = true };
    }

    /// <summary>
    /// k148g: ДЕТАЛЬ ИЗ ФАЙЛА STEP/IGES — тела файла операцией «Импорт» в дереве (текст файла хранится в ней, тела — отдельные).
    /// Прежде тела клались в деталь без операции, и первое же перестроение истории их стирало: открывалась пустая деталь
    /// (замечание 02.11, 23:10); то же — у компонента сборки из STEP.
    /// </summary>
    public static Part ImportPart(string path, List<string> warnings)
    {
        string text = ReadImportText(path, warnings);
        var read = Exchange.Read(text, out _, warnings) ?? throw new InvalidDataException(string.Join("\n", warnings));
        var part = new Part(Path.GetFileNameWithoutExtension(path));
        var imp = new Parametric3D.Model.Features.InsertBodyFeature(Loc.L("Импорт «{0}»", Path.GetFileName(path)))
        {
            Step = text, SourceName = Path.GetFileName(path), Separate = true,
        };
        imp.Prime(read.Bodies.ToList(), read.BodyNameList);
        part.AddFeature(imp);
        // k149 (замечание 03.11, 06:41, п. 3): каждая деталь файла (замкнутая оболочка) — свой узел FreeForm внутри операции
        // импорта (правка — в окружении FreeForm, тела деталей не объединяются)
        foreach (var nm in imp.PartNames(warnings))
            part.AddFeature(new Parametric3D.Model.Features.FreeFormFeature(nm) { ImportOf = imp.Key, FFBody = nm });
        return part;
    }

    /// <summary>
    /// k149 (замечания 03.11, 08:17–08:19): ФАЙЛ С НЕСКОЛЬКИМИ ДЕТАЛЯМИ — СБОРКА. Каждая деталь файла (замкнутая оболочка,
    /// имя — изделие STEP) — компонент сборки: в нём операция «Импорт» с телом только этой детали и узел FreeForm для правки;
    /// у компонента — связь «шесть степеней свободы» (6 DOF: тип соединения в файле не задан), он не зафиксирован — его можно
    /// двигать и вращать. Положение — как в файле. Файл с одной деталью — документ детали, как прежде. P3D_IMPORTASM=0 —
    /// всегда документ детали.
    /// </summary>
    public static List<Part>? ImportComponents(string path, List<string> warnings)
    {
        if (Environment.GetEnvironmentVariable("P3D_IMPORTASM") == "0") return null;
        string text = ReadImportText(path, warnings);
        // k149 (замечание 03.11, 10:32): структура изделий STEP — детали на своих местах (размещения сборки)
        if (Exchange.Guess(text) == Exchange.Format.Step && !(Environment.GetEnvironmentVariable("P3D_STEPPLACE") == "0"))
        {
            List<Kernel.Export.StepOccurrence>? occ = null;
            try { occ = Kernel.Export.StepAssembly.Read(text, warnings); } catch (Exception ex) { warnings.Add("STEP assembly: " + ex.Message); }
            if (occ is { Count: >= 2 }) return FromOccurrences(path, occ, warnings);
        }
        var read = Exchange.Read(text, out var fmt, warnings) ?? throw new InvalidDataException(string.Join("\n", warnings));
        var bodies = Parametric3D.Model.Features.InsertBodyFeature.Separated(read.Bodies.ToList(), read.BodyNameList, out var names);
        if (bodies.Count < 2) return null;
        string file = Path.GetFileName(path);
        var list = new List<Part>();
        for (int i = 0; i < bodies.Count; i++)
        {
            var one = new Part(names[i]); one.Bodies.Add(bodies[i]);
            string step;
            try { step = Exchange.Write(one, Exchange.Format.Step, warnings); }
            catch (Exception ex) { warnings.Add($"{names[i]}: {ex.Message}"); continue; }
            var comp = new Part(names[i]);
            var imp = new Parametric3D.Model.Features.InsertBodyFeature(Loc.L("Импорт «{0}»", file)) { Step = step, SourceName = file, Separate = true };
            imp.Prime(new List<Kernel.Topology.TSolid> { bodies[i] }, new[] { names[i] });
            comp.AddFeature(imp);
            foreach (var nm in imp.PartNames(warnings))
                comp.AddFeature(new Parametric3D.Model.Features.FreeFormFeature(nm) { ImportOf = imp.Key, FFBody = nm });
            comp.Mates.Add(new Parametric3D.Model.Mates.Mate(Parametric3D.Model.Mates.MateType.Free, Loc.L("Шесть степеней")));
            list.Add(comp);
        }
        return list.Count < 2 ? null : list;
    }

    /// <summary>Файл с несколькими деталями — документ сборки из компонентов (ImportComponents); иначе null.</summary>
    public static DocumentSession? ImportAssembly(string path, List<string> warnings)
    {
        if (ImportComponents(path, warnings) is not { } comps) return null;
        var doc = new Document(Path.GetFileNameWithoutExtension(path));
        var s = new DocumentSession(doc, null) { IsAssembly = true, Modified = true };
        foreach (var c in comps) doc.Root.Add(c);
        return s;
    }

    /// <summary>
    /// k149: сборка из вхождений деталей STEP. Компонент — вхождение: тела изделия в его координатах (операция «Импорт» и
    /// узел FreeForm на тело), размещение — Translation/Rotation компонента, связь — «шесть степеней»; подсборка файла —
    /// группа компонентов (Part.SubAssembly). Повторные вхождения одного изделия — одинаковое содержимое, свои размещения.
    /// </summary>
    static List<Part> FromOccurrences(string path, List<Kernel.Export.StepOccurrence> occ, List<string> warnings)
    {
        string file = Path.GetFileName(path);
        var list = new List<Part>();
        var stepOf = new Dictionary<int, (string Step, List<Kernel.Topology.TSolid> Bodies, List<string> Names)>();
        var used = new HashSet<string>();
        string Unique(string n) { string u = n; for (int k = 2; !used.Add(u); k++) u = $"{n} ({k})"; return u; }
        int anon = 0;
        foreach (var o in occ)
        {
            int key = o.ProductDef >= 0 ? o.ProductDef : -(++anon);
            if (!stepOf.TryGetValue(key, out var st))
            {
                if (!Kernel.Export.ImportHeal.Off) for (int i = 0; i < o.Bodies.Count; i++) Kernel.Export.ImportHeal.Heal(o.Bodies[i], warnings, $"{o.Product} {i + 1}");
                var bodies = Parametric3D.Model.Features.InsertBodyFeature.Separated(o.Bodies, o.BodyNames.Select(n => n ?? (o.Bodies.Count == 1 ? o.Product : "")).ToList(), out var names);
                for (int i = 0; i < names.Count; i++) if (names[i].StartsWith("Деталь ") && o.Bodies.Count > 1) names[i] = $"{o.Product}.{i + 1}";
                var one = new Part(o.Product); one.Bodies.AddRange(bodies);
                one.BodyNameList.AddRange(names);
                string text;
                try { text = Exchange.Write(one, Exchange.Format.Step, warnings); }
                catch (Exception ex) { warnings.Add($"{o.Product}: {ex.Message}"); continue; }
                stepOf[key] = st = (text, bodies, names);
            }
            var comp = new Part(Unique(o.Product)) { SubAssembly = o.Parent };
            var imp = new Parametric3D.Model.Features.InsertBodyFeature(Loc.L("Импорт «{0}»", file)) { Step = st.Step, SourceName = file, Separate = true };
            imp.Prime(st.Bodies.Select(b => Kernel.Topology.TopologyClone.Deep(b)).ToList(), st.Names);
            comp.AddFeature(imp);
            foreach (var nm in imp.PartNames(warnings))
                comp.AddFeature(new Parametric3D.Model.Features.FreeFormFeature(nm) { ImportOf = imp.Key, FFBody = nm });
            var pl = o.Placement;
            comp.Translation = new OpenTK.Mathematics.Vector3((float)pl.O.X, (float)pl.O.Y, (float)pl.O.Z);
            comp.Rotation = QuatOf(pl.X, pl.Y, pl.Z);
            comp.Mates.Add(new Parametric3D.Model.Mates.Mate(Parametric3D.Model.Mates.MateType.Free, Loc.L("Шесть степеней")));
            list.Add(comp);
        }
        return list;
    }

    /// <summary>Кватернион поворота, переводящего оси X, Y, Z в x, y, z (столбцы матрицы поворота).</summary>
    public static OpenTK.Mathematics.Quaternion QuatOf(OpenTK.Mathematics.Vector3d x, OpenTK.Mathematics.Vector3d y, OpenTK.Mathematics.Vector3d z)
    {
        double m00 = x.X, m10 = x.Y, m20 = x.Z, m01 = y.X, m11 = y.Y, m21 = y.Z, m02 = z.X, m12 = z.Y, m22 = z.Z;
        double tr = m00 + m11 + m22, qw, qx, qy, qz;
        if (tr > 0) { double S = Math.Sqrt(tr + 1) * 2; qw = 0.25 * S; qx = (m21 - m12) / S; qy = (m02 - m20) / S; qz = (m10 - m01) / S; }
        else if (m00 > m11 && m00 > m22) { double S = Math.Sqrt(1 + m00 - m11 - m22) * 2; qw = (m21 - m12) / S; qx = 0.25 * S; qy = (m01 + m10) / S; qz = (m02 + m20) / S; }
        else if (m11 > m22) { double S = Math.Sqrt(1 + m11 - m00 - m22) * 2; qw = (m02 - m20) / S; qx = (m01 + m10) / S; qy = 0.25 * S; qz = (m12 + m21) / S; }
        else { double S = Math.Sqrt(1 + m22 - m00 - m11) * 2; qw = (m10 - m01) / S; qx = (m02 + m20) / S; qy = (m12 + m21) / S; qz = 0.25 * S; }
        return OpenTK.Mathematics.Quaternion.Normalize(new OpenTK.Mathematics.Quaternion((float)qx, (float)qy, (float)qz, (float)qw));
    }

    /// <summary>
    /// k182: текст для импорта: STEP/IGES — как есть; SolidWorks (.sldprt) и Parasolid (.x_b) — переводом тел Parasolid в STEP
    /// (Kernel.Export.Parasolid). Перевод кэшируется по пути: импорт сначала пробует сборку, потом деталь.
    /// </summary>
    static string ReadImportText(string path, List<string> warnings)
    {
        if (!Kernel.Export.Parasolid.ParasolidImport.Handles(path)) return File.ReadAllText(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_importCache is { } c && c.Path == path && c.Stamp == stamp) return c.Text;
        var text = Kernel.Export.Parasolid.ParasolidImport.StepTextOf(path, warnings);
        _importCache = (path, stamp, text);
        return text;
    }
    static (string Path, DateTime Stamp, string Text)? _importCache;

    public static DocumentSession Import(string path, List<string> warnings)
    {
        if (ImportAssembly(path, warnings) is { } asm) return asm;
        var part = ImportPart(path, warnings);
        var doc = new Document(part.Name);
        doc.Root.Add(part);
        return new DocumentSession(doc, null) { Modified = true };
    }

    public void Save() => SaveAs(FilePath ?? throw new InvalidOperationException("path is not set"));

    public void SaveAs(string path)
    {
        if (IsAssembly) DocumentStorage.WriteAtomic(path, AssemblyFile.Save(Document.Root, Document.Parameters, withDisplay: true));
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
        if (Environment.GetEnvironmentVariable("P3D_LOADDBG") == "1") Console.WriteLine($"LOAD cancel-edit: adopted {_history.LastAdopted} of {part.Features.Count}");
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
