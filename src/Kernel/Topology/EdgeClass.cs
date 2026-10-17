using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// u12: РЕБРО ИЛИ КРОМКА. Кромка — граница двух касательных друг к другу граней (край скругления, стык плоскости с
/// цилиндром по касательной): излома там нет, скруглять, срезать и тянуть массив по ней нечего. Ядро различает их по
/// нормалям обеих граней в пяти точках ребра; приложение по этому признаку гасит недоступные операции. На кромке
/// остаются операции, которым излом не нужен: паз вдоль неё, плоскость эскиза по ней.
/// </summary>
public static class EdgeClass
{
    /// <summary>Косинус угла между нормалями граней, выше которого стык считается касательным (≈ 1.8°).</summary>
    public const double SmoothCos = 0.9995;

    public static bool IsTangentSeam(TEdge e)
    {
        var fa = e.FaceA; var fb = e.FaceB;
        if (fa == null || fb == null || ReferenceEquals(fa, fb) || e.Curve == null) return false;
        var c = e.Curve;
        for (int i = 0; i <= 4; i++)
        {
            double t = c.T0 + (c.T1 - c.T0) * (0.1 + 0.8 * i / 4.0);
            var p = c.PointAt(t);
            Vector3d na, nb;
            try { na = N(fa, p); nb = N(fb, p); }
            catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { return false; }
            if (!(na.LengthSquared > 1e-18 && nb.LengthSquared > 1e-18)) return false;
            if (Vector3d.Dot(na.Normalized(), nb.Normalized()) < SmoothCos) return false;
        }
        return true;
    }

    /// <summary>Нормаль грани по её точной поверхности (у гранёной грани — по источнику, не по фасетам).</summary>
    static Vector3d N(TFace f, Vector3d p)
    {
        var s = GeometryAudit.AuthoritativeSurface(f) ?? f.Surface;
        var n = s.NormalAt(s.ProjectToUV(p));
        if (f.Reversed) n = -n;
        // знак — как у нормали самой грани (гранёная поверхность несёт сторону грани)
        var nf = f.NormalAt(p);
        return Vector3d.Dot(n, nf) < 0 ? -n : n;
    }
}
