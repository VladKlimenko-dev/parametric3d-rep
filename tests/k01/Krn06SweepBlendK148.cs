using Parametric3D.Model;

static partial class Program
{
    /// <summary>
    /// k148d (замечание пользователя 01.11, 15:02, модель Sweep_Blend): ФАСКА 7.21 ПО РЕБРУ СО СМЕНОЙ ВЫПУКЛОСТИ. Ребро стенки окна
    /// лофта после скругления меняется вдоль себя из выпуклого в вогнутое; точный путь законно отказывает, резец даёт негодное
    /// тело (ребро кратности 3). Норма: лофт, скругление и первая фаска — точно; вторая фаска — отказ, и в тексте ошибки —
    /// причина (смена выпуклости), а не только «результат резца не прошёл проверку»; тело — как до фаски (точное).
    /// </summary>
    static void Krn06SweepBlendK148Checks()
    {
        Check("KRN06-USER-SWEEPBLEND-MIXED-CONVEXITY", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            var (p, pars) = DocumentFile.Load(File.ReadAllText(Path.Combine(dir, "user_k148_sweep_blend.p3d")));
            p.Rebuild(pars);
            var fs = p.Features;
            Require(fs.Count >= 4, $"features {fs.Count}");
            for (int i = 0; i < 3; i++) Require(fs[i].Error == null, $"{fs[i].Name}: {fs[i].Error}");
            var e = fs[3].Error ?? throw new Exception("the mixed-convexity chamfer was applied");
            Log(new { test = "KRN06-USER-SWEEPBLEND-MIXED-CONVEXITY", error = e.Split('\n')[0] });
            Require(e.Contains("changes from convex to concave"), "no mixed-convexity reason in: " + e);
            Require(p.Bodies.Count == 1 && p.Bodies[0].Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic, "body after the refused chamfer is not exact");
        });
    }
}
