using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// KRN-03. ЛОКАЛЬНАЯ КАРТА СФЕРЫ ДЛЯ РАЗМЕТКИ: стереографическая проекция из полюса P, лежащего ВНЕ грани. Конформна,
/// окружности сферы переходят в окружности плоскости, особенность (полюс) — вне области: грань в такой карте — обычная
/// ограниченная область без шва и полюса. Используется разметчиком вместо параметризации поверхности грани, так что сетка
/// не зависит от того, какой картой (осью) описана сферическая грань в B-rep. Геометрия грани не меняется.
/// </summary>
public sealed class StereoChart : ISurface3
{
    public readonly Vector3d C, P, E1, E2;
    public readonly double R;
    public StereoChart(Vector3d centre, double radius, Vector3d pole)
    {
        C = centre; R = radius; P = pole.Normalized();
        var u0 = Math.Abs(P.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
        E1 = (u0 - P * Vector3d.Dot(u0, P)).Normalized(); E2 = Vector3d.Cross(P, E1);
    }
    public bool IsPlanar => false;
    public Vector3d PointAt(Vector2d q)
    {
        double q2 = q.LengthSquared; var w = E1 * q.X + E2 * q.Y;
        return C + (w * 2 + P * (q2 - 1)) / (q2 + 1) * R;
    }
    public Vector3d NormalAt(Vector2d q) => (PointAt(q) - C).Normalized();
    public Vector2d ProjectToUV(Vector3d x)
    {
        var u = (x - C).Normalized(); double den = 1 - Vector3d.Dot(u, P);
        // полюс карты по построению вне грани (SurfaceDomain.SpherePoleOutside): точка в полюсе — «бесконечность»
        if (den <= 0) return new Vector2d(double.PositiveInfinity, double.PositiveInfinity);
        return new Vector2d(Vector3d.Dot(u, E1) / den, Vector3d.Dot(u, E2) / den);
    }
    public void TessellateFace(TFace face, MeshBuilder mb) => throw new NotSupportedException("StereoChart — карта разметчика, не поверхность грани");
}
