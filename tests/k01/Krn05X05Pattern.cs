using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// k136 (модель пользователя X-218795-05 после правки Sketch1, 29.10): КРУГОВОЙ МАССИВ ВЫРЕЗА НА ТЕЛЕ ВРАЩЕНИЯ — ТОЧНО.
    /// Массив берёт операцию заново и строит 6 копий; копия 5 падала («ребро 5.46e-8 мм»), потому что:
    /// — сам вырез уходил в многогранный путь: удостоверение событий требовало кривую пересечения через точку ребра тонкого
    ///   тора на ПОВЕРХНОСТИ цилиндра выреза вне его грани (P3D_CERTOUTBOX=0);
    /// — у копии замыкающее звено ломаной тор × цилиндр было длиннее шара единственности ветви (P3D_TRACECHORD=0);
    /// — у копии через шов контур грани вращения подходил к себе на 1.3e-6 мм, и клин между этими точками отвергал разметку
    ///   грани (P3D_PINCHTRI=0).
    /// Норма: все операции без ошибок, тело точное аналитическое, объём массива = объём вращения − 6 × объём одного выреза
    /// (до 1e-9).
    /// </summary>
    static void Krn05X05PatternChecks()
    {
        Check("KRN05-USER-X05-PATTERN-EXACT", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var text = File.ReadAllText(Path.Combine(dir, "user_k136_x05.p3d"));
            double V(int stage)
            {
                var (p, pars) = DocumentFile.Load(text);
                p.StageIndex = stage; p.MarkStageMoved(); p.Rebuild(pars);
                var bad = p.Features.Take(stage).Where(f => f.Error != null).Select(f => f.Name + ": " + f.Error!.Split('\n')[0]).ToList();
                Require(bad.Count == 0, string.Join("; ", bad));
                Require(p.Bodies.Count == 1 && p.Bodies[0].Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic,
                        $"stage {stage}: guarantee {string.Join(",", p.Bodies.Select(b => b.Guarantee))}");
                return p.Volume;
            }
            double v1 = V(1), v2 = V(2), v3 = V(3);
            double want = v1 - 6 * (v1 - v2);
            Log(new { revolve = v1, cut = v2, pattern = v3, want, rel = Math.Abs(v3 - want) / want });
            Require(Math.Abs(v3 - want) <= 1e-9 * want, $"pattern volume {v3} vs {want}");
        });
    }
}
