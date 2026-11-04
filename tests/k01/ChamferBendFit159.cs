using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// k159 (замечание 04.11, 23:51 — «в Sweep_Blend отчётливо видно неправильное построение фаски»): ФАСКА БОЛЬШЕ ИЗГИБА РЕБРА.
    /// Вогнутое ребро «стенка ∩ скругление R6.61» — дуга радиуса около 6.6 с центром на краю стенки; фаска 9.72 больше радиуса:
    /// линия фаски на стенке уходит за край стенки (ширина фаски падает до 1 мм), и заполнение выходило веером треугольников из
    /// конца дуги с вырожденными гранями (площадь 0). Норма: отказ операции с причиной «does not fit … contact line leaves the
    /// face»; тело — прежнее (до фаски: объём и годность — эталон перестроением без фаски). Мутация P3D_RAILBACK=0 — фаска
    /// строится веером (проверка падает). Фаска 4 на этом файле тоже отказывает (острие стенки у вершины): правильная форма угла — следующий шаг.
    /// </summary>
    static void ChamferBendFitChecks()
    {
        static (int Degenerate, double MinArea) Faces(TSolid b)
        {
            var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
            int deg = 0; double minA = double.MaxValue;
            foreach (var f in b.Faces)
            {
                if (!m.FaceRanges.TryGetValue(f.Id, out var r)) continue;
                double a = 0;
                for (int t = 0; t < r.IndexCount; t += 3)
                {
                    var p0 = m.Vertices[m.Indices[r.IndexStart + t]].P; var p1 = m.Vertices[m.Indices[r.IndexStart + t + 1]].P; var p2 = m.Vertices[m.Indices[r.IndexStart + t + 2]].P;
                    a += OpenTK.Mathematics.Vector3.Cross(p1 - p0, p2 - p0).Length / 2;
                }
                minA = Math.Min(minA, a);
                if (a < 0.05) deg++;
            }
            return (deg, minA);
        }
        Check("KRN06-CHAMFER-BENDFIT-K159", () =>
        {
            var dir = UserModelsDir() ?? throw new Exception("tests/ui/data not found");
            string text = File.ReadAllText(Path.Combine(dir, "user_k150_sweep_blend_c972.p3d"));
            // эталон: тело до фаски
            var (p0, pars0) = DocumentFile.Load(text);
            p0.Features.OfType<ChamferFeature>().Last().Suppressed = true; p0.Rebuild(pars0);
            double v0 = p0.Volume;
            var (p, pars) = DocumentFile.Load(text);
            p.Rebuild(pars);
            var ch = p.Features.OfType<ChamferFeature>().Last();
            var (deg, minA) = p.Bodies.Count == 1 ? Faces(p.Bodies[0]) : (-1, 0);
            Log(new { test = "KRN06-CHAMFER-BENDFIT-K159", size = ch.D1.Value, error = ch.Error?.Split('\n').Last(), volume = Math.Round(p.Volume, 3), before = Math.Round(v0, 3), degenerateFaces = deg, minFaceArea = minA, refusals = Parametric3D.Kernel.Ops.FilletOp.RailBackRefusals });
            Require(ch.Error != null && ch.Error.Contains("contact line leaves the face"), "the 9.72 chamfer was not refused with the reason: " + (ch.Error ?? $"built, degenerate faces {deg}"));
            Require(p.Bodies.Count == 1 && SolidValidator.Validate(p.Bodies[0]).IsValid && Math.Abs(p.Volume - v0) < 1e-6 * v0, $"the body before the chamfer is not kept: {p.Volume} vs {v0}");
        });
    }
}
