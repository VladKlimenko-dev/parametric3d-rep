using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Threads;

namespace Parametric3D.Model.Features;

// Вырез резьбы: профиль впадины (profile_zr) в плоскости через ось и начало винта, протяжка по винту с шагом lead = P·N
// (ориентация профиля — постоянная относительно оси, не по Френе), N заходов — N вырезов с поворотом 360°/N. Конусная
// (1:16) — радиус растёт (наружная) или убывает (внутренняя) вдоль резьбы. Плавный выход — на последнем витке профиль
// плавно уходит по радиусу на глубину впадины (из материала).
public sealed partial class CosmeticThreadFeature
{
    /// <summary>Мутация P3D_THREADSTEPS: рамок на виток у многогранного запасного пути протяжки.</summary>
    static readonly int StepsPerTurn = int.TryParse(Environment.GetEnvironmentVariable("P3D_THREADSTEPS"), out var n) && n >= 8 ? n : 36;

    /// <summary>Резцы винтового выреза (по одному на заход).</summary>
    public static List<TSolid> CutTools(ThreadSpec spec, bool inner, Vector3d b0, Vector3d u, Vector3d e1, double len, bool left, bool smoothExit)
    {
        var zr = inner ? spec.IntZr : spec.ExtZr;
        if (zr.Length < 3) throw new RebuildException(OperationOutcome.InvalidInput, $"Thread: the type {spec.Designation} has no groove profile.");
        int starts = Math.Max(1, spec.Starts);
        double lead = spec.Lead > Tol.Weld ? spec.Lead : spec.P * starts;
        double rFace = inner ? spec.IntStartR : spec.ExtStartR;
        // глубина впадины от поверхности (и знак ухода из материала: наружная — от оси, внутренняя — к оси)
        double depth = zr.Max(p => Math.Abs(p.Y - rFace));
        double outSign = inner ? -1 : 1;
        double taper = spec.Taper ? Math.Tan(spec.HalfConeDeg * Math.PI / 180) * (inner ? -1 : 1) : 0;
        double exitLen = smoothExit ? Math.Min(lead, len / 2) : 0;
        double sg = left ? -1 : 1;
        int steps = Math.Max(16, (int)Math.Ceiling(StepsPerTurn * len / lead));

        var tools = new List<TSolid>(starts);
        for (int k = 0; k < starts; k++)
        {
            var ek = Rot(e1, u, 2 * Math.PI * k / starts);
            var plane = new Frame3(b0, u, ek);
            var curves = new List<ICurve3>();
            for (int i = 0; i < zr.Length; i++)
            {
                var a = zr[i]; var b = zr[(i + 1) % zr.Length];
                curves.Add(new LineCurve3(b0 + u * a.X + ek * a.Y, b0 + u * b.X + ek * b.Y));
            }
            var section = ProfileBuilder.Build(plane, curves);
            if (section.IsEmpty) throw new RebuildException(OperationOutcome.InvalidInput, $"Thread: the groove profile of {spec.Designation} is not closed.");
            (Vector3d O, Vector3d U, Vector3d V) Law(double s)
            {
                double z = len * s;
                var v = Rot(ek, u, sg * 2 * Math.PI * z / lead);
                double dr = taper * z;
                if (exitLen > 0 && z > len - exitLen)
                {
                    double t = (z - (len - exitLen)) / exitLen;
                    dr += outSign * depth * t * t * (3 - 2 * t);   // плавно: касательно в начале выхода
                }
                return (b0 + u * z + v * dr, u, v);
            }
            tools.Add(SweepOp.BuildByLaw(section, Law, steps));
        }
        return tools;
    }
}
