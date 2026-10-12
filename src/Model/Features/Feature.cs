using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Core;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model.Features;

/// <summary>
/// Число, которое может быть либо константой, либо формулой из таблицы
/// параметров. Все размерные поля фич используют именно этот тип — иначе
/// параметризация остаётся только внутри эскизов.
/// </summary>
public struct ValueRef
{
    public double Value;
    public string? Formula;

    public ValueRef(double v) { Value = v; Formula = null; }
    public static implicit operator ValueRef(double v) => new ValueRef(v);

    /// <summary>
    /// ЗНАЧЕНИЕ ПО ФОРМУЛЕ. Неразрешимая формула — ОШИБКА, а не повод молча взять прежнее число.
    ///
    /// Прежде формула «missing_parameter+1» при значении 17 возвращала 17 без единого слова:
    /// пользователь видел параметрическую связь, а деталь строилась по другому числу. Такую
    /// подмену нельзя заметить ни на экране, ни в размерах — она обнаруживается только тогда,
    /// когда деталь изготовлена.
    ///
    /// Теперь непустая формула, которую не удалось вычислить, останавливает операцию с
    /// объяснением: что за выражение и почему не вышло. Значение без формулы, как и прежде,
    /// берётся как есть.
    /// </summary>
    public double Resolve(ParameterTable? t)
    {
        if (string.IsNullOrWhiteSpace(Formula)) return Value;

        if (t == null)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"формула «{Formula}» задана, но таблицы переменных нет");

        if (t.TryEvaluate(Formula!, out double v, out string? why))
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"формула «{Formula}» дала недопустимое значение");

            Value = v;
            return v;
        }

        throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"формула «{Formula}» не вычислена" +
                                   (string.IsNullOrEmpty(why) ? "" : $": {why}"));
    }

    public override string ToString() => Formula ?? Value.ToString("0.###");
}

/// <summary>Контекст перестроения детали: сюда фичи складывают результат.</summary>
public sealed class BuildContext
{
    public Part Part = null!;
    public ParameterTable? Parameters;
    public ISketchSolver Solver = new NumericSketchSolver();
    /// <summary>Тела, накопленные к текущему шагу дерева построения.</summary>
    public readonly List<TSolid> Bodies = new();
    public readonly List<string> Warnings = new();

    /// <summary>
    /// Вспомогательная геометрия, построенная к текущему шагу дерева: плоскости, оси и точки
    /// по ИМЕНАМ. Имя переживает перестроение, номер — нет, поэтому ссылки идут по именам.
    /// </summary>
    public readonly Dictionary<string, Frame3> Planes = new();
    public readonly Dictionary<string, (Vector3d At, Vector3d Dir)> Axes = new();
    public readonly Dictionary<string, Vector3d> Points = new();

    /// <summary>
    /// СНИМОК СОСТОЯНИЯ ПЕРЕД ОПЕРАЦИЕЙ. Операция может изменить тела и вспомогательную
    /// геометрию, а потом отказаться — например, проверив свои же размеры уже после первого
    /// выреза. Прежде такое изменение оставалось: объём детали менялся, хотя операция объявлена
    /// неудачной, и следующие операции шли по испорченному телу.
    ///
    /// Снимок позволяет вернуть всё как было. Списки копируются, а не разделяются: иначе возврат
    /// вернул бы ту же самую испорченную запись.
    /// </summary>
    public sealed class Snapshot
    {
        public readonly List<TSolid> Bodies;
        public readonly int Warnings;
        public readonly Dictionary<string, Frame3> Planes;
        public readonly Dictionary<string, (Vector3d At, Vector3d Dir)> Axes;
        public readonly Dictionary<string, Vector3d> Points;

        internal Snapshot(BuildContext c)
        {
            Bodies = new List<TSolid>(c.Bodies);
            Warnings = c.Warnings.Count;
            Planes = new Dictionary<string, Frame3>(c.Planes);
            Axes = new Dictionary<string, (Vector3d, Vector3d)>(c.Axes);
            Points = new Dictionary<string, Vector3d>(c.Points);
        }
    }

    /// <summary>Запомнить состояние до операции.</summary>
    public Snapshot Remember() => new(this);

    /// <summary>
    /// Вернуть состояние, запомненное до операции. Предупреждения, добавленные неудачной
    /// операцией, НЕ отбрасываются: они объясняют, почему не вышло, и нужны пользователю.
    /// </summary>
    public void RollBack(Snapshot s)
    {
        Bodies.Clear();
        Bodies.AddRange(s.Bodies);

        Planes.Clear();
        foreach (var kv in s.Planes) Planes[kv.Key] = kv.Value;

        Axes.Clear();
        foreach (var kv in s.Axes) Axes[kv.Key] = kv.Value;

        Points.Clear();
        foreach (var kv in s.Points) Points[kv.Key] = kv.Value;
    }

    /// <summary>Плоскость по имени; пустое имя означает XY детали.</summary>
    public Frame3 PlaneByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return Frame3.XY;
        if (Planes.TryGetValue(name, out var f)) return f;
        var d = Part.Children.OfType<DatumPlane>().FirstOrDefault(x => x.Name == name);
        if (d != null) return d.Frame;
        throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Плоскость «{name}» не найдена.");
    }

    /// <summary>Ось по имени.</summary>
    public (Vector3d At, Vector3d Dir) AxisByName(string name)
    {
        if (Axes.TryGetValue(name, out var a)) return a;
        var d = Part.Children.OfType<DatumAxis>().FirstOrDefault(x => x.Name == name);
        if (d != null) return (d.Origin, d.Direction);
        throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Ось «{name}» не найдена.");
    }

    public Sketch? FindSketch(uint id)
    {
        var sk = Part.FindById<Sketch>(id);
        // KRN-08: эскиз на грани переносится на текущую грань с его именем.
        if (sk != null) SketchFaceRef.Follow(sk, this);
        return sk;
    }
    public DatumPlane? FindPlane(uint id) => Part.FindById<DatumPlane>(id);
}

public abstract class Feature : ModelItem
{
    public override ItemKind Kind => ItemKind.Feature;

    /// <summary>Убирает материал вместо добавления.</summary>
    public bool Cut;

    /// <summary>Выполняет построение. Ошибки бросаются как RebuildException.</summary>
    public abstract void Execute(BuildContext ctx);

    /// <summary>
    /// K05. ПОСТОЯННЫЙ КЛЮЧ ОПЕРАЦИИ — основа устойчивых имён её граней. Номер Id выдаётся заново
    /// при каждом запуске программы, имя пользователь может поменять; ключ сохраняется в
    /// документе и не меняется. Грани, которые операция порождает, получают подпись
    /// «#ключ/имя», и две операции выдавливания больше не дают граням одинаковых имён.
    /// </summary>
    public string Key = NewKey();

    /// <summary>
    /// K02. Политика этой операции: null — как у детали. Позволяет разрешить приближённый путь
    /// одной операции, не ослабляя всё дерево; тело получит пометку «приближённое».
    /// </summary>
    public Kernel.OperationPolicy? Policy;

    /// <summary>
    /// Ключ операции для устойчивых имён. В документах — случайный (уникален между файлами).
    /// ДЕТЕРМИНИРОВАННЫЙ РЕЖИМ (только проверки): P3D_DETERMINISTIC_KEYS=1 или DeterministicKeys = true —
    /// ключи по счётчику, и журналы двух прогонов одной программы совпадают (прежде имена вида
    /// «#9dc4e5f257/extrude[0]:side[0.0]» менялись от запуска к запуску, и журналы K05 нельзя было сравнивать).
    /// Не включать в приложении: ключи документов, созданных в разных сеансах, совпали бы.
    /// </summary>
    public static string NewKey() => DeterministicKeys
        ? "d" + Interlocked.Increment(ref _keyCounter).ToString("x9")
        : Guid.NewGuid().ToString("N")[..10];

    private static bool _gDeterministicKeys = Environment.GetEnvironmentVariable("P3D_DETERMINISTIC_KEYS") == "1";
    /// <summary>Поставка 2: значение снимка модели в контексте (ModelSettings.DeterministicKeys), если он есть; иначе общее по умолчанию.</summary>
    internal static bool DeterministicKeys
    {
        get => Parametric3D.Model.ModelSettings.Current is { } ms ? ms.DeterministicKeys : _gDeterministicKeys;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gDeterministicKeys, value, "Feature.DeterministicKeys");
    }
    private static long _keyCounter;
    /// <summary>Сброс счётчика детерминированных ключей (проверки: одинаковая нумерация от начала прогона).</summary>
    internal static void ResetKeys() => Interlocked.Exchange(ref _keyCounter, 0);

    /// <summary>
    /// Эскизы, которые операция ИСПОЛЬЗУЕТ как исходные данные.
    ///
    /// Нужно, чтобы скрыть их после построения: эскиз, ушедший в тело,
    /// в трёхмерном виде только мешает — он лежит внутри геометрии
    /// и перечёркивает её линиями. Показать обратно всегда можно
    /// глазом в дереве.
    /// </summary>
    public virtual IEnumerable<uint> ConsumedSketchIds() => Enumerable.Empty<uint>();

    /// <summary>
    /// Грани, появившиеся при выполнении этой операции.
    ///
    /// Заполняется при перестройке: до выполнения операции запоминается
    /// набор граней, после — разница и есть её работа. Связь нужна дереву:
    /// иначе при наведении на операцию нечего подсветить, потому что грань
    /// сама по себе не помнит, кто её создал.
    /// </summary>
    public readonly List<uint> CreatedFaceIds = new();

    /// <summary>Короткое описание для дерева построения.</summary>
    public virtual string Summary => Name;
}
