using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn09ImportHealChecks()
    {
        Check("KRN09-IMPORT-HEAL-REPORTED", () =>
        {
            // 09.5: импорт STEP лечится в бюджете (ToleranceContext.ImportHeal = 1e-4 мм) с отчётом — строка на тело. Брусок
            // 40×30×20 со сквозным отверстием R6, записанный нашим точным STEP и испорченный: вершина сдвинута на 2e-5 (лечится:
            // перенос в пересечение плоскостей), флаги стороны граней (читаются верно и без лечения), выпала плоская грань
            // (закрывается), вершина сдвинута на 5e-3 (больше бюджета — «NOT valid» с причиной), выпала стенка отверстия (не
            // плоская дыра — не закрывается, «NOT valid»). Объём здорового и вылеченного тела — точный (аналитический).
            var fails = new List<string>();
            double exact = 24000 - Math.PI * 36 * 20;
            foreach (var (name, text) in ImportHealCases())
            {
                var w = new List<string>();
                var p = Parametric3D.Model.Exchange.Read(text, out _, w);
                string? rep = w.FirstOrDefault(x => x.StartsWith("Import: body 1:"));
                double? v = p?.Bodies.Count == 1 ? AnalyticVolume.Of(p.Bodies[0]) : null;
                Log(new { test = "KRN09-IMPORT-HEAL", name, report = rep, volume = v });
                if (rep == null) { fails.Add($"{name}: no healing report"); continue; }
                bool valid = rep.EndsWith(" valid.");
                switch (name)
                {
                    case "as written" or "one face sense flipped" or "all faces flipped":
                        if (!valid || v is not double v0 || Math.Abs(v0 - exact) > 1e-9 * exact) fails.Add($"{name}: {rep} V {v}");
                        break;
                    case "vertex off by 2e-5":
                        if (!valid || !rep.Contains("vertex(es) snapped") || rep.Contains(" 0 vertex(es) snapped") || v is not double v1 || Math.Abs(v1 - exact) > 0.05) fails.Add($"{name}: {rep} V {v}");
                        break;
                    case "one face missing":
                        if (!valid || !rep.Contains("1 hole(s) filled") || v is not double v2 || Math.Abs(v2 - exact) > 1e-9 * exact) fails.Add($"{name}: {rep} V {v}");
                        break;
                    case "vertex off by 5e-3":
                        if (valid || !rep.Contains("budget")) fails.Add($"{name}: over-budget gap not reported: {rep}");
                        break;
                    case "hole wall missing":
                        if (valid || rep.Contains("1 hole(s) filled") || rep.Contains("2 hole(s) filled")) fails.Add($"{name}: a non-planar opening must not be filled: {rep}");
                        break;
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
