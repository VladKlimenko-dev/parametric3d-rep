using OpenTK.Mathematics;

namespace Parametric3D.Model;

/// <summary>
/// РАЗНЕСЁННЫЙ ВИД — показ узла в разобранном состоянии: детали отодвинуты по направлениям
/// сборки, чтобы было видно, что куда ставится. Нужен для сборочных чертежей и указаний по
/// сборке.
///
/// Разнесение НЕ меняет саму сборку: положения деталей остаются прежними, а вид хранит лишь
/// смещения для показа. Иначе после просмотра разнесённого вида сборка оказалась бы
/// разобранной, а связи — нарушенными.
///
/// Смещения задаются в координатах сборки. Порядок шагов важен: по нему строится
/// последовательность сборки, поэтому шаги хранятся списком, а не набором.
/// </summary>
public sealed class ExplodedView
{
    public string Name;

    /// <summary>Шаг разнесения: какая деталь, куда и на каком шаге.</summary>
    public sealed class Step
    {
        public string PartName = "";
        public Vector3d Direction = Vector3d.UnitZ;
        public double Distance;
        /// <summary>Номер шага: детали одного номера отодвигаются вместе.</summary>
        public int Order;

        public Vector3d Shift => Direction.LengthSquared < 1e-18
            ? Vector3d.Zero
            : Direction.Normalized() * Distance;
    }

    public readonly List<Step> Steps = new();

    public ExplodedView(string name) { Name = name; }

    /// <summary>Сколько всего шагов (различных номеров) — столько приёмов в сборке.</summary>
    public int StageCount => Steps.Count == 0 ? 0 : Steps.Select(s => s.Order).Distinct().Count();

    /// <summary>
    /// Смещения для показа: имя детали → сдвиг. Доля от 0 до 1 позволяет показать разнесение
    /// движением, а не скачком; шаги при этом идут по порядку, а не разом.
    /// </summary>
    public Dictionary<string, Vector3d> Offsets(double fraction = 1.0)
    {
        var res = new Dictionary<string, Vector3d>();
        if (Steps.Count == 0) return res;

        double f = Math.Clamp(fraction, 0, 1);
        var orders = Steps.Select(s => s.Order).Distinct().OrderBy(x => x).ToList();

        // Каждому шагу — своя доля общего хода: первый начинает, последний заканчивает.
        for (int i = 0; i < orders.Count; i++)
        {
            double from = (double)i / orders.Count;
            double to = (double)(i + 1) / orders.Count;
            double local = f <= from ? 0 : f >= to ? 1 : (f - from) / (to - from);

            foreach (var s in Steps.Where(x => x.Order == orders[i]))
            {
                var shift = s.Shift * local;
                res[s.PartName] = res.TryGetValue(s.PartName, out var had) ? had + shift : shift;
            }
        }

        return res;
    }

    /// <summary>
    /// Проверка на бессмыслицу: деталь, отодвинутая на ноль, и деталь, названная дважды на одном
    /// шаге, — обычноописки, и лучше сказать о них сразу.
    /// </summary>
    public List<string> Check(Assembly asm)
    {
        var problems = new List<string>();
        var names = asm.Parts.Select(p => p.Name).ToHashSet();

        foreach (var s in Steps)
        {
            if (!names.Contains(s.PartName)) problems.Add($"деталь «{s.PartName}» не найдена в узле");
            if (Math.Abs(s.Distance) < 1e-9) problems.Add($"деталь «{s.PartName}»: разнесение на ноль");
            if (s.Direction.LengthSquared < 1e-18) problems.Add($"деталь «{s.PartName}»: направление не задано");
        }

        foreach (var g in Steps.GroupBy(s => (s.PartName, s.Order)).Where(g => g.Count() > 1))
            problems.Add($"деталь «{g.Key.PartName}» названа дважды на шаге {g.Key.Order}");

        return problems;
    }
}
