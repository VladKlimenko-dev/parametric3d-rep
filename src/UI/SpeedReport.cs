using System.Diagnostics;
using System.Text;
using Parametric3D.Kernel.Diagnostics;
using Parametric3D.Model;
using Parametric3D.Model.Params;

namespace Parametric3D.UI;

/// <summary>
/// ЗАМЕР СКОРОСТИ НА ЖИВОЙ МОДЕЛИ. Скорость — слабое место ядра, и мерить её надо там, где она
/// важна: на настоящей детали и на настоящей машине.
///
/// ЧТО ПОПРАВЛЕНО ПО ЗАМЕЧАНИЯМ К САМОМУ ЗАМЕРУ:
///   — ПРОГРЕВ ИСКЛЮЧЁН. Первое перестроение считает ещё и подготовку кода, и прежде оно шло в
///     общий счёт: по нему выходило, что худший прогон вчетверо хуже лучшего, хотя дело было в
///     разогреве, а не в работе.
///   — ВРЕМЯ МЕРЯЕТСЯ БЕЗ ПРОФИЛИРОВАНИЯ. Засечки стоят денег; прежде они были включены во всех
///     прогонах, и объявленное время включало стоимость собственного измерения. Разложение по
///     этапам снимается отдельным прогоном, уже после замера времени.
///   — ПОРЯДОК ЧЕРЕДУЕТСЯ. Один поток всегда шёл первым, и ему доставался холодный кэш; теперь
///     прогоны идут вперемежку.
///   — ПОКАЗЫВАЕТСЯ СЕРЕДИННОЕ И ВЕРХНЯЯ ГРАНИЦА, а не только лучшее: по одному лучшему времени
///     нельзя судить о том, чего ждать в работе.
/// </summary>
public static class SpeedReport
{
    /// <summary>Замерить перестроение детали и вернуть отчёт.</summary>
    public static string Measure(Part part, ParameterTable? parameters, int runs = 5)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Деталь: {part.Name}");
        sb.AppendLine($"Тел: {part.Bodies.Count}, операций в дереве: {part.Features.Count()}");
        sb.AppendLine($"Ядер у машины: {Environment.ProcessorCount}");
        sb.AppendLine($"Прогонов: {runs} после прогрева, время без засечек");
        sb.AppendLine();

        int wasMesh = Kernel.Tess.Tessellator.Threads;
        int wasBool = Kernel.Ops.BooleanOp.Threads;
        bool wasProf = Prof.Enabled;

        try
        {
            Prof.Enabled = false;

            // ПРОГРЕВ: один прогон на каждом числе потоков, в счёт не идёт.
            One(part, parameters, 1);
            One(part, parameters, Environment.ProcessorCount);

            var single = new List<double>();
            var many = new List<double>();

            // ЧЕРЕДОВАНИЕ: иначе первому достаётся холодный кэш, и сравнение выходит нечестным.
            for (int i = 0; i < runs; i++)
            {
                single.Add(One(part, parameters, 1));
                many.Add(One(part, parameters, Environment.ProcessorCount));
            }

            single.Sort();
            many.Sort();

            double Median(List<double> x) => x[x.Count / 2];
            double High(List<double> x) => x[Math.Min(x.Count - 1, (int)(x.Count * 0.9))];

            sb.AppendLine("Полное перестроение, мс:");
            sb.AppendLine("                 лучшее   серединное   верхняя граница");
            sb.AppendLine($"  один поток    {single[0],7:0.#} {Median(single),12:0.#} {High(single),16:0.#}");
            sb.AppendLine($"  все ядра      {many[0],7:0.#} {Median(many),12:0.#} {High(many),16:0.#}");
            sb.AppendLine();

            double gain = Median(many) > 0 ? Median(single) / Median(many) : 1;
            sb.AppendLine($"Выигрыш от раскладки по потокам: в {gain:0.##} раза.");
            sb.AppendLine(gain < 1.2
                ? "  То есть его почти нет: время уходит туда, где раскладка не применяется."
                : "  Раскладка окупается: стоит расширять её на новые места.");
            sb.AppendLine();

            // РАЗЛОЖЕНИЕ — ОТДЕЛЬНЫМ ПРОГОНОМ, и его время с замером выше не сравнивают.
            Kernel.Tess.Tessellator.Threads = 1;
            Kernel.Ops.BooleanOp.Threads = 1;

            Prof.Enabled = true;
            Prof.ResetAll();

            part.Invalidate();
            part.Rebuild(parameters);

            string profile = Prof.Report();
            Prof.Enabled = false;

            sb.AppendLine("Время по этапам (отдельный прогон с засечками, один поток —");
            sb.AppendLine("засечки сами стоят времени, поэтому с числами выше их не сравнивают):");
            sb.AppendLine(profile);
        }
        finally
        {
            Kernel.Tess.Tessellator.Threads = wasMesh;
            Kernel.Ops.BooleanOp.Threads = wasBool;
            Prof.Enabled = wasProf;
        }

        return sb.ToString();
    }

    /// <summary>Одно перестроение на заданном числе потоков; возвращает время в миллисекундах.</summary>
    private static double One(Part part, ParameterTable? parameters, int threads)
    {
        // Число потоков ставится на время одного прогона и возвращается само.
        using var options = new Kernel.KernelOptions { Threads = threads }.Apply();

        part.Invalidate();

        var sw = Stopwatch.StartNew();
        part.Rebuild(parameters);
        sw.Stop();

        return sw.Elapsed.TotalMilliseconds;
    }
}
