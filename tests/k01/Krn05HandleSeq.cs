using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// КНОПКА «ТЕСТ», ПОСЛЕДОВАТЕЛЬНОСТЬ РУЧКАМИ ЦЕЛИКОМ (файл приложения seq_handles_k75.p3d, 2026-10-20): куб 100 → R10 →
    /// фаска 8 → скругление вершины R6 → выдавливание верхней грани 20 → скругление ребра стенки R4 → вырез верхней грани 5 →
    /// скругление R (в файле — R2; R2 здесь ровно съедает соседнее скругление R4 целиком — вырожденный случай, поэтому — R1.5).
    /// Прежде: (1) эскиз грани с дугой скругления вершины приходил ломаной из хорд (EdgeProjection), профиль на 5e-4 мм
    /// расходился с гранью, общая булева отказывала, выдавливание шло многогранным путём; (2) слитая стенка (стенка бруска и
    /// стенка выдавливания в одной плоскости) брала имя грани резца — ссылка скругления R4 на стенку бруска «no longer exists»
    /// (P3D_MERGETAGA=0); (3) прямое скругление брало сетку грани стенки у донора, а соседний шар размечался заново с другой
    /// разбивкой общей дуги (дробление у клюва) — 28 сторон без пары, скругление уходило в приближённый путь резца
    /// (P3D_DONOREDGES=0). Теперь все восемь операций — без ошибок, тело точное (ExactAnalytic), годное.
    /// </summary>
    static void Krn05HandleSeqChecks()
    {
        // ИМЯ СЛИТОЙ ГРАНИ — ОТ ПЕРВОГО ОПЕРАНДА (в обоих порядках операндов): брусок A и брусок B на нём, стенки в одних
        // плоскостях; у каждой слитой стенки — имя грани первого операнда. Мутация P3D_MERGETAGA=0 — в одном из порядков
        // стенки берут имя второго (по порядку граней оболочки).
        Check("KRN05-MERGE-TAG-FIRST-OPERAND", () =>
        {
            TSolid Tagged(TSolid s, string p) { var c = TopologyClone.Deep(s); foreach (var f in c.Faces) f.Tag = p + f.Tag; return c; }
            var a = Tagged(Box(0, 0, 0, 30, 20, 10), "A:"); var b = Tagged(Box(0, 0, 5, 30, 20, 10), "B:");
            int fromA0 = Parametric3D.Kernel.Analytic.GeneralBoolean.MergeTagFromA;
            foreach (var (x, y, first) in new[] { (a, b, "A:"), (b, a, "B:") })
            {
                var r = Parametric3D.Kernel.Analytic.GeneralBoolean.Run(x, y, Parametric3D.Kernel.Analytic.Bool2.Union, out var why);
                Require(r != null, $"{first} first: declined ({why})");
                var walls = r!.Faces.Where(f => Math.Abs(f.NormalAt(f.Loops[0].CoEdges[0].Start.P).Z) < 0.5).ToList();
                Log(new { test = "KRN05-MERGE-TAG-FIRST-OPERAND", first, faces = r.Faces.Count(), walls = walls.Select(f => f.Tag).ToList() });
                Require(walls.Count == 4, $"{first} first: {walls.Count} walls (merge expected)");
                Require(walls.All(f => f.Tag.StartsWith(first)), $"{first} first: walls {string.Join(", ", walls.Select(f => f.Tag))}");
            }
            Log(new { test = "KRN05-MERGE-TAG-FIRST-OPERAND", swapped = Parametric3D.Kernel.Analytic.GeneralBoolean.MergeTagFromA - fromA0 });
        });
        Check("KRN05-HANDLE-SEQUENCE", () =>
        {
            if (Environment.GetEnvironmentVariable("P3D_HSDBG") == "1") Parametric3D.Kernel.Ops.DirectRound.Debug = m => { if (m.Contains("declined") || m.Contains("invalid") || m.Contains("not meshed")) Console.WriteLine("   dr: " + m); };
            var (part, _) = DocumentFile.Load(File.ReadAllText(Fixture("seq_handles_k75.p3d")));
            ((RoundFeature)part.Features[7]).Radius = new ValueRef(1.5);
            part.StageIndex = part.Features.Count; part.Invalidate(); part.Rebuild(null);
            var b = part.Bodies.Single();
            if (Environment.GetEnvironmentVariable("P3D_HSDBG") == "1") foreach (var wq in part.Warnings) Console.WriteLine("   warn: " + wq);
            var errs = part.Features.Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error).ToList();
            var v = SolidValidator.Validate(b);
            Log(new { test = "KRN05-HANDLE-SEQUENCE", volume = part.Volume, guarantee = b.Guarantee.ToString(), errors = errs, valid = v.Summary(), mergeTagA = Parametric3D.Kernel.Analytic.GeneralBoolean.MergeTagFromA, donorEdgeMismatch = Parametric3D.Kernel.Export.StepImport.DonorEdgeMismatch });
            Require(errs.Count == 0, string.Join(" / ", errs));
            Require(b.Guarantee == GuaranteeLevel.ExactAnalytic, $"guarantee {b.Guarantee}");
            Require(v.IsValid, v.Summary());
            Require(b.Faces.All(f => f.Surface is not Parametric3D.Kernel.Surfaces.FacetedSurface fs || fs.Source != null), "a face without an exact source");
        });
    }
}
