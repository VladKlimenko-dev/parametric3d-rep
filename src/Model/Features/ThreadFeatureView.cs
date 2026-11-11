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
        bool left, ThreadSpec? spec, bool inner)
    {
        var res = new List<(Vector3d, Vector3d)>();
        double lead = pitch * starts, turns = len / lead, sg = left ? -1 : 1;
        double rv = r * (inner ? 0.997 : 1.003);
        int per = Math.Clamp((int)(6000 / Math.Max(1, turns * starts)), 12, 48);
        int n = Math.Max(8, (int)Math.Ceiling(per * turns));
        for (int k = 0; k < starts; k++)
        {
            var ek = Rot(e1, u, 2 * Math.PI * k / starts);
            Vector3d At(double z) => b0 + u * z + Rot(ek, u, sg * 2 * Math.PI * z / lead) * rv;
            var prev = At(0);
            for (int i = 1; i <= n; i++) { var p = At(len * i / n); res.Add((prev, p)); prev = p; }
        }
        if (spec != null)
        {
            var zr = inner ? spec.IntZr : spec.ExtZr;
            for (int i = 0; i < zr.Length; i++)
            {
                var a = zr[i]; var b = zr[(i + 1) % zr.Length];
                res.Add((b0 + u * a.X + e1 * a.Y, b0 + u * b.X + e1 * b.Y));
            }
        }
        return res;
    }
}
