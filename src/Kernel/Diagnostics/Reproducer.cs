using System.Text.Json;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Diagnostics;

/// <summary>
/// K10. ВОСПРОИЗВОДИТЕЛЬ ОТКАЗА ОПЕРАЦИИ.
///
/// Отказ булевой описывается одним текстом: вид операции, политика, настройки ядра, стадия и код
/// отказа, версия формата и сами операнды — точной записью STEP (геометрия, а не сетка). По
/// такому тексту отказ повторяется без документа пользователя: Replay читает операнды и
/// выполняет ту же операцию с теми же настройками.
///
/// Ничего не отправляется: текст создаётся по запросу и остаётся у вызывающего. В нём —
/// геометрия только этих операндов, без дерева построения, имён файлов и параметров документа.
/// Ограничение: операнды проходят через STEP, поэтому отказ, вызванный кэшем точной формы, а не
/// геометрией, может не повториться — Replay тогда честно сообщит «не воспроизведено».
/// </summary>
public static class Reproducer
{
    public const int FormatVersion = 1;

    public sealed class Record
    {
        public int Format { get; set; } = FormatVersion;
        public string Operation { get; set; } = "";
        public string Policy { get; set; } = "";
        public string? FailedStage { get; set; }
        public string? FailureCode { get; set; }
        public string? Message { get; set; }
        public string? InjectedFault { get; set; }
        public Dictionary<string, string> Settings { get; set; } = new();
        public List<string> Bodies { get; set; } = new();
        public string Tool { get; set; } = "";
    }

    /// <summary>Записать отказ (или любой результат) булевой вместе с операндами.</summary>
    public static string Capture(IReadOnlyList<TSolid> bodies, TSolid tool, BooleanOp.BooleanKind kind,
                                 OperationPolicy policy, OperationResult result, string? injectedFault = null)
    {
        string Step(TSolid s, string name)
        {
            var sw = new StringWriter();
            if (!Export.StepExact.Write(sw, new[] { s }, name)) throw new InvalidOperationException($"operand {name} cannot be written exactly");
            return sw.ToString();
        }
        var settings = KernelSettings.Capture();
        var rec = new Record
        {
            Operation = kind.ToString(),
            Policy = policy.ToString(),
            FailedStage = result.FailedStage,
            FailureCode = result.FailureCode,
            Message = result.Describe(),
            InjectedFault = injectedFault,
            Settings = typeof(KernelSettings).GetProperties()
                .Where(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(double) || p.PropertyType == typeof(string))
                .ToDictionary(p => p.Name, p => Convert.ToString(p.GetValue(settings), System.Globalization.CultureInfo.InvariantCulture) ?? ""),
            Bodies = bodies.Select((b, i) => Step(b, $"body{i}")).ToList(),
            Tool = Step(tool, "tool")
        };
        return JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Повторить записанную операцию. Reproduced — совпали ли стадия и код отказа.</summary>
    public static (OperationResult Result, bool Reproduced) Replay(string text)
    {
        var rec = JsonSerializer.Deserialize<Record>(text) ?? throw new InvalidDataException("reproducer is empty");
        if (rec.Format > FormatVersion) throw new InvalidDataException($"reproducer format {rec.Format} is newer than {FormatVersion}");
        var bodies = rec.Bodies.SelectMany(t => Export.StepImport.ReadExactSolids(t)).ToList();
        var tool = Export.StepImport.ReadExactSolids(rec.Tool).Single();
        foreach (var b in bodies.Append(tool)) TopologyConventions.Normalize(b);
        var kind = Enum.Parse<BooleanOp.BooleanKind>(rec.Operation);
        var policy = Enum.Parse<OperationPolicy>(rec.Policy);

        // Настройки — те же, что при отказе (числовые и логические поля KernelSettings).
        var s = KernelSettings.Capture();
        foreach (var (name, value) in rec.Settings)
        {
            var prop = typeof(KernelSettings).GetProperty(name);
            if (prop == null || !prop.CanWrite && prop.GetSetMethod(true) == null) continue;
            object? v = prop.PropertyType == typeof(double) ? double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                      : prop.PropertyType == typeof(int) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                      : prop.PropertyType == typeof(bool) ? bool.Parse(value)
                      : prop.PropertyType == typeof(string) ? value : null;
            if (v != null) s = With(s, prop, v);
        }

        OperationResult r;
        using (new KernelContext(s).Enter())
        using (rec.InjectedFault != null ? FaultInjection.Arm(rec.InjectedFault) : null)
            r = BooleanOp.Evaluate(bodies, tool, kind, policy);
        bool same = rec.FailureCode == null ? r.Accepted : r.FailureCode == rec.FailureCode && r.FailedStage == rec.FailedStage;
        return (r, same);
    }

    /// <summary>Копия записи настроек с одним изменённым свойством (через with-клонирование).</summary>
    private static KernelSettings With(KernelSettings s, System.Reflection.PropertyInfo p, object value)
    {
        var clone = (KernelSettings)typeof(KernelSettings).GetMethod("<Clone>$")!.Invoke(s, null)!;
        p.SetValue(clone, value);
        return clone;
    }
}
