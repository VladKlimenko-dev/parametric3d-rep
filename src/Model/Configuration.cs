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
