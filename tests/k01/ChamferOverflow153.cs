using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// k153 (замечание 04.11, 10:34, п. 2; файл пользователя user_k153_tube_chamfer.p3d): ФАСКА ШИРЕ СОСЕДНЕЙ ГРАНИ. Грань ребра —
    /// линза (29.7 мм посередине, сужается к концам); фаска 54.19 прежде строилась резцом — тело годно по проверке, но это уже не
    /// фаска ребра (резец съедал грань целиком и резал соседние элементы). Норма: отказ операции с причиной «шире грани» и
    /// наибольшим размером; тело — прежнее (точное, годное); фаска 20 — строится, тело годно. Мутация P3D_CHAMFEROVERFLOW=0 —
    /// фаска 54.19 строится без отказа (проверка падает).
    /// </summary>
    static void ChamferOverflowChecks()
    {
        Check("KRN06-CHAMFER-OVERFLOW-K153", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            string text = File.ReadAllText(Path.Combine(dir, "user_k153_tube_chamfer.p3d"));
            var (p, pars) = DocumentFile.Load(text);
            p.Rebuild(pars);
            var ch = p.Features.OfType<ChamferFeature>().Last();
            Log(new { test = "KRN06-CHAMFER-OVERFLOW-K153", size = ch.D1.Value, error = ch.Error, g = p.Bodies.Select(b => b.Guarantee.ToString()) });
            Require(ch.Error != null && ch.Error.Contains("wider than the face"), "the 54.19 chamfer was not refused: " + (ch.Error ?? "built"));
            var m = System.Text.RegularExpressions.Regex.Match(ch.Error!, @"about ([0-9.]+) mm");
            Require(m.Success && double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is > 20 and < 30, "largest chamfer: " + ch.Error);
            Require(p.Bodies.Count == 1 && p.Bodies[0].Guarantee == GuaranteeLevel.ExactAnalytic && SolidValidator.Validate(p.Bodies[0]).IsValid, "the body before the chamfer is not kept");
            ch.D1 = new ValueRef(20); ch.D2 = new ValueRef(20); p.MarkDirtyFrom(ch); p.Rebuild(pars);
            Log(new { test = "KRN06-CHAMFER-OVERFLOW-K153", size = 20, error = ch.Error, g = p.Bodies.Select(b => b.Guarantee.ToString()) });
            Require(ch.Error == null && p.Bodies.Count == 1 && SolidValidator.Validate(p.Bodies[0]).IsValid, "the 20 mm chamfer: " + (ch.Error ?? "invalid body"));
        });
    }
}
