using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// k152: ФАСКА C6 ПО РЕБРУ БОКОВИН NURBS ЛОФТА У НАКЛОННОГО ТОРЦА (корпус s102, шаг 0). Прежде — резец (многогранно, снимал
    /// 1130.12 мм³): «no smooth parameter» / «the blend does not leave the end face». Нужны: продолжение граней NURBS за край
    /// области — касательное (P3D_SSITANEXT; многочлен крайнего промежутка загибался), стык таблицы ленты там, где точка касания
    /// переходит край области (P3D_DRTANBREAK), допуск нового ребра ленты по отходу p-кривой ≤ 1e-5 мм (P3D_DRTOLEDGE).
    /// Норма: точно и годно; снятый объём (точные объёмы до и после) — замер k152 до 1e-3 мм³ и формула сечений корпуса
    /// (1134.4783) до 0.05 %; допуски рёбер — не больше предельного.
    /// </summary>
    static void NurbsEndChecks()
    {
        Check("KRN06-SEQ-S102-NURBS-END-K152", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            string text = File.ReadAllText(Path.Combine(dir, "corpus_k152_s102_0.p3d"));
            var (p, pars) = DocumentFile.Load(text);
            int n = p.Features.Count;
            p.StageIndex = n - 1; p.MarkStageMoved(); p.Rebuild(pars);
            double before = AnalyticVolume.Of(p.Bodies[0]) ?? double.NaN;
            p.StageIndex = n; p.MarkStageMoved(); p.Rebuild(pars);
            var f = p.Features[n - 1];
            Require(f.Error == null, f.Error ?? "");
            Require(p.Bodies.Count == 1, $"bodies {p.Bodies.Count}");
            var b = p.Bodies[0];
            var v = SolidValidator.Validate(b);
            double after = AnalyticVolume.Of(b) ?? double.NaN, removed = before - after;
            double maxTol = b.Edges.Max(e => e.Tolerance);
            const double want = 1134.505650, formula = 1134.4783;
            Log(new { test = "KRN06-SEQ-S102-NURBS-END-K152", guarantee = b.Guarantee.ToString(), verdict = v.Summary(), removed = Math.Round(removed, 6), want, formula, maxEdgeTol = maxTol });
            Require(b.Guarantee == GuaranteeLevel.ExactAnalytic, "guarantee " + b.Guarantee);
            Require(v.IsValid, v.Summary());
            Require(Math.Abs(removed - want) <= 1e-3, $"removed {removed:R}, expected {want}");
            Require(Math.Abs(removed - formula) <= 5e-4 * formula, $"removed {removed:R}, section formula {formula}");
            Require(maxTol <= ToleranceContext.Current.MaxEntityTolerance, $"edge tolerance {maxTol:E2}");
        });
    }
}
