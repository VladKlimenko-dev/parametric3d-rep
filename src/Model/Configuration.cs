using Parametric3D.Model.Params;

namespace Parametric3D.Model;

/// <summary>
/// КОНФИГУРАЦИЯ (исполнение) — именованный набор отличий от основной модели. Одна деталь или
/// узел часто существуют в нескольких исполнениях: длиннее и короче, с отверстиями и без, с
/// установленной крышкой и снятой. Прежде под каждое приходилось держать отдельный документ,
/// и правка общей части расходилась по копиям.
///
/// Конфигурация хранит ТОЛЬКО отличия, а не полную модель: значения переменных, гашение
/// операций, скрытие деталей и свободные координаты связей. Всё остальное берётся из основной
/// модели, поэтому её правка сразу отражается во всех исполнениях — ради этого исполнения и
/// заводят.
/// </summary>
public sealed class Configuration
{
    public string Name;
    public string Comment = "";

    /// <summary>Значения переменных: имя → значение.</summary>
    public readonly Dictionary<string, double> Parameters = new();

    /// <summary>Операции, погашенные в этом исполнении (по именам).</summary>
    public readonly HashSet<string> SuppressedFeatures = new();

    /// <summary>Детали, скрытые в этом исполнении (по именам).</summary>
    public readonly HashSet<string> HiddenParts = new();

    /// <summary>Свободные координаты связей: имя связи → сдвиг и угол.</summary>
    public readonly Dictionary<string, (double Offset, double Angle)> MateValues = new();

    public Configuration(string name) { Name = name; }

    public override string ToString() =>
        $"{Name}: переменных {Parameters.Count}, погашено {SuppressedFeatures.Count}, скрыто {HiddenParts.Count}";

    /// <summary>
    /// Применить к детали: подменить переменные и погасить операции. Возвращает, что не нашлось,
    /// — молча пропустить чужое имя значит показать не то исполнение, о котором просили.
    /// </summary>
    public List<string> ApplyTo(Part part, ParameterTable? table)
    {
        var missing = new List<string>();

        if (table != null)
            foreach (var (name, value) in Parameters)
            {
                var p = table.Find(name);
                if (p == null) { missing.Add($"переменная «{name}» не найдена"); continue; }
                p.Number = value;
            }
        table?.Recalculate();

        foreach (var f in part.Features) f.Suppressed = false;
        foreach (string name in SuppressedFeatures)
        {
            var f = part.Features.FirstOrDefault(x => x.Name == name);
            if (f == null) { missing.Add($"операция «{name}» не найдена"); continue; }
            f.Suppressed = true;
        }

        return missing;
    }

    /// <summary>Применить к узлу: скрыть детали, выставить свободные координаты связей.</summary>
    public List<string> ApplyTo(Assembly asm, ParameterTable? table)
    {
        var missing = new List<string>();
        var parts = asm.Parts.ToList();

        if (table != null)
            foreach (var (name, value) in Parameters)
            {
                var p = table.Find(name);
                if (p == null) { missing.Add($"переменная «{name}» не найдена"); continue; }
                p.Number = value;
            }
        table?.Recalculate();

        foreach (var p in parts) p.Visible = true;
        foreach (string name in HiddenParts)
        {
            var p = parts.FirstOrDefault(x => x.Name == name);
            if (p == null) { missing.Add($"деталь «{name}» не найдена"); continue; }
            p.Visible = false;
        }

        foreach (var (name, v) in MateValues)
        {
            var m = parts.SelectMany(x => x.Mates).FirstOrDefault(x => x.Name == name);
            if (m == null) { missing.Add($"связь «{name}» не найдена"); continue; }
            m.Offset = v.Offset;
            m.Angle = v.Angle;
        }

        return missing;
    }
}

/// <summary>
/// u13: ПРИМЕНЕНИЕ ИСПОЛНЕНИЙ. При первом переходе с основной модели запоминается её состояние (значения переменных-чисел и
/// погашенные операции); исполнение = основная модель + свои значения и погашения; «основная» возвращает запомненное.
/// </summary>
public static class Configurations
{
    sealed class Snapshot { public readonly Dictionary<string, double> Values = new(); public readonly HashSet<string> Suppressed = new(); }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Part, Snapshot> Base = new();

    /// <summary>Исполнение из текущего состояния: значения всех числовых переменных (без формул) и погашенные операции.</summary>
    public static Configuration Capture(Part part, Params.ParameterTable table, string name)
    {
        var cfg = new Configuration(name);
        foreach (var p in table.All) if (string.IsNullOrEmpty(p.Formula) && p.Kind == Params.ParamKind.Real) cfg.Parameters[p.Name] = p.Number;
        foreach (var f in part.Features) if (f.Suppressed) cfg.SuppressedFeatures.Add(f.Name);
        return cfg;
    }

    public static void Apply(Part part, Params.ParameterTable table, string name)
    {
        if (part.ActiveConfiguration == "" && !Base.TryGetValue(part, out _))
        {
            var snap = new Snapshot();
            foreach (var p in table.All) if (string.IsNullOrEmpty(p.Formula) && p.Kind == Params.ParamKind.Real) snap.Values[p.Name] = p.Number;
            foreach (var f in part.Features) if (f.Suppressed) snap.Suppressed.Add(f.Name);
            Base.AddOrUpdate(part, snap);
        }
        Base.TryGetValue(part, out var b);
        var cfg = name == "" ? null : part.Configurations.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"no configuration {name}");
        foreach (var p in table.All)
        {
            if (!string.IsNullOrEmpty(p.Formula) || p.Kind != Params.ParamKind.Real) continue;
            if (cfg != null && cfg.Parameters.TryGetValue(p.Name, out var v)) p.Number = v;
            else if (b != null && b.Values.TryGetValue(p.Name, out var bv)) p.Number = bv;
        }
        foreach (var f in part.Features)
            f.Suppressed = (b?.Suppressed.Contains(f.Name) ?? f.Suppressed && cfg == null) || (cfg?.SuppressedFeatures.Contains(f.Name) ?? false);
        part.ActiveConfiguration = name;
        if (name == "") Base.Remove(part);
        table.Recalculate();
        part.Invalidate();
    }
}

