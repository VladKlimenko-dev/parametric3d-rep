using OpenTK.Mathematics;
using Parametric3D.Model.Threads;

namespace Parametric3D.Model.Features;

// Вид резьбы: винтовая линия и контур профиля у начала.
public sealed partial class CosmeticThreadFeature
{
    /// <summary>Поворот d вокруг единичной оси a на угол th (против часовой, если смотреть с конца a).</summary>
    private static Vector3d Rot(Vector3d d, Vector3d a, double th)
    {
        double c = Math.Cos(th), sn = Math.Sin(th);
        return d * c + Vector3d.Cross(a, d) * sn + a * (Vector3d.Dot(a, d) * (1 - c));
    }

    /// <summary>
    /// Вид резьбы: винтовая линия по поверхности грани (чуть снаружи материала — не прячется в теле) на каждый заход и
    /// контур профиля впадины выбранного типа у начала.
    /// </summary>
    public static List<(Vector3d A, Vector3d B)> ViewLines(Vector3d b0, Vector3d u, Vector3d e1, double r, double pitch, int starts, double len,
        bool left, ThreadSpec? spec, bool inner, bool smoothExit = false)
    {
        var res = new List<(Vector3d, Vector3d)>();
        double lead = spec?.Lead > 0 ? spec.Lead : pitch * starts, turns = len / lead, sg = left ? -1 : 1;
        double rv = r * (inner ? 0.997 : 1.003);
        // A hard total budget, even for very long/fine-pitch threads.
        int n = Math.Clamp((int)Math.Min(int.MaxValue, Math.Ceiling(48 * turns)), 8, Math.Max(8, 6000 / starts));
        for (int k = 0; k < starts; k++)
        {
            var ek = Rot(e1, u, 2 * Math.PI * k / starts);
            Vector3d At(double z)
            {
                if (spec != null && smoothExit)
                {
                    var frame = ToolFrame(spec, inner, b0, u, ek, z, len, left, true);
                    return frame.O + frame.V * rv;
                }
                return b0 + u * z + Rot(ek, u, sg * 2 * Math.PI * z / lead) * rv;
            }
            var prev = At(0);
            for (int i = 1; i <= n; i++) { var p = At(len * i / n); res.Add((prev, p)); prev = p; }
        }
        if (spec != null)
        {
            // Preview must also work for profiles whose exact cut is unsupported.
            var zr = inner ? spec.IntZr : spec.ExtZr;
            double entry = LeadInDistance(spec, inner);
            var first = ToolFrame(spec, inner, b0, u, e1, -entry, len, left, false);
            for (int i = 0; i < zr.Length; i++)
            {
                var a = zr[i]; var b = zr[(i + 1) % zr.Length];
                res.Add((first.O + first.U * a.X + first.V * a.Y, first.O + first.U * b.X + first.V * b.Y));
            }
            var previous = first.O + first.V * rv;
            for (int i = 1; i <= 48; i++)
            {
                var f = ToolFrame(spec, inner, b0, u, e1, -entry + entry * i / 48, len, left, false);
                var point = f.O + f.V * rv;
                res.Add((previous, point)); previous = point;
            }
        }
        return res;
    }
}
