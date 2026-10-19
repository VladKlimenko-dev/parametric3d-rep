using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    /// <summary>
    /// КНОПКА «ТЕСТ» (2026-10-20), ПОСЛЕДОВАТЕЛЬНОСТЬ РУЧКАМИ: куб 100 → скругление ребра R10 → фаска 8 → скругление вершины
    /// R6 → ВЫДАВЛИВАНИЕ ВЕРХНЕЙ ГРАНИ на 20 (файл приложения seq_handles_k74.p3d). Общий точный маршрут отказывал: (1) стенка
    /// резца (цилиндр от дуги профиля) и боковая грань тела — поверхности касаются по прямой, но ГРАНИ не встречаются (стенка
    /// выше грани тела); (2) дно резца и цилиндр скругления R10 касаются по РЕБРУ, общему для обеих граней. Операция шла
    /// многогранным путём: объём с ошибкой 9e-4 мм³, тело ExactPolyhedral (в приложении — отказ «vertex-pinch»). Теперь —
    /// точно: объём = объём тела до операции + точный объём резца (касание без общего объёма; до 1e-9). Мутации
    /// P3D_TANGENTCLIP=0 (оба случая — снова отказ точного маршрута).
    /// </summary>
    static void Krn05TangentFacesChecks()
    {
        Check("KRN05-TANGENT-CONTACT-FACES", () =>
        {
            var (part, _) = DocumentFile.Load(File.ReadAllText(Fixture("seq_handles_k74.p3d")));
            part.StageIndex = 4; part.Rebuild(null);
            double v4 = part.Volume;
            var xf = (ExtrudeFeature)part.Features[4];
            var sk = part.Sketches.First(k => k.Id == xf.SketchId);
            var tool = Parametric3D.Kernel.Ops.ExtrudeOp.Build(sk.ToProfile(), sk.Plane.W.Normalized() * xf.Side1.Distance.Value);
            double vt = OpVolume(tool);
            part.StageIndex = 5; part.Invalidate(); part.Rebuild(null);
            var b = part.Bodies.Single();
            double v5 = part.Volume;
            Log(new { test = "KRN05-TANGENT-CONTACT-FACES", v4, tool = vt, v5, want = v4 + vt, guarantee = b.Guarantee.ToString(), tangentMiss = Parametric3D.Kernel.Analytic.GeneralBoolean.TangentMissFaces, tangentBoth = Parametric3D.Kernel.Analytic.GeneralBoolean.TangentOnBothEdges });
            Require(xf.Error == null, "extrude: " + xf.Error);
            Require(b.Guarantee == GuaranteeLevel.ExactAnalytic, $"guarantee {b.Guarantee}");
            Require(SolidValidator.Validate(b).IsValid, SolidValidator.Validate(b).Summary());
            Require(Math.Abs(v5 - (v4 + vt)) <= 1e-9 * v5, $"volume {v5:R} vs {v4 + vt:R}");
        });
    }
}
