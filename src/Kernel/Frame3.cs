using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Kernel;

/// <summary>
/// Правая система координат в пространстве: начало + орты U,V,W (W = U x V).
///
/// Это ЕДИНСТВЕННЫЙ способ описать «плоскость» в проекте. В старом коде плоскость
/// была GLItem-ом с кватернионом, буфером вершин и шейдером одновременно —
/// геометрия, топология и отрисовка были слиты в один класс. Здесь Frame3 —
/// чистые данные (struct), не знающие ни про OpenGL, ни про дерево модели.
/// </summary>
public readonly struct Frame3
{
    public readonly Vector3d Origin;
    public readonly Vector3d U;   // локальная ось X
    public readonly Vector3d V;   // локальная ось Y
    public readonly Vector3d W;   // нормаль (U x V)

    public Frame3(Vector3d origin, Vector3d u, Vector3d v)
    {
        Origin = origin;
        U = u.Normalized();
        Vector3d w = Vector3d.Cross(U, v);
        if (w.LengthSquared < Tol.Linear)
            throw new P3DException("Frame3: axes U and V are collinear.");
        W = w.Normalized();
        V = Vector3d.Cross(W, U).Normalized();
    }

    private Frame3(Vector3d origin, Vector3d u, Vector3d v, Vector3d w)
    { Origin = origin; U = u; V = v; W = w; }

    public static readonly Frame3 XY = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ);
    public static readonly Frame3 XZ = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ, -Vector3d.UnitY);
    public static readonly Frame3 YZ = new Frame3(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitZ, Vector3d.UnitX);

    /// <summary>Строит фрейм по точке и нормали, ось U подбирается автоматически.</summary>
    public static Frame3 FromNormal(Vector3d origin, Vector3d normal)
    {
        Vector3d n = normal.Normalized();
        Vector3d a = Math.Abs(n.Z) < 0.9 ? Vector3d.UnitZ : Vector3d.UnitX;
        Vector3d u = Vector3d.Cross(a, n).Normalized();
        Vector3d v = Vector3d.Cross(n, u);
        return new Frame3(origin, u, v, n);
    }

    /// <summary>Локальные 2D-координаты -> мир.</summary>
    public Vector3d ToWorld(Vector2d p) => Origin + U * p.X + V * p.Y;
    public Vector3d ToWorld(double x, double y) => Origin + U * x + V * y;
    public Vector3d ToWorld(Vector3d p) => Origin + U * p.X + V * p.Y + W * p.Z;
    public Vector3d DirToWorld(Vector2d d) => U * d.X + V * d.Y;

    /// <summary>Мир -> локальные 2D (проекция на плоскость фрейма).</summary>
    public Vector2d ToLocal2(Vector3d p)
    {
        Vector3d d = p - Origin;
        return new Vector2d(Vector3d.Dot(d, U), Vector3d.Dot(d, V));
    }

    public Vector3d ToLocal3(Vector3d p)
    {
        Vector3d d = p - Origin;
        return new Vector3d(Vector3d.Dot(d, U), Vector3d.Dot(d, V), Vector3d.Dot(d, W));
    }

    /// <summary>Знаковое расстояние от точки до плоскости фрейма.</summary>
    public double SignedDistance(Vector3d p) => Vector3d.Dot(p - Origin, W);

    public Frame3 Translated(Vector3d delta) => new Frame3(Origin + delta, U, V, W);
    public Frame3 OffsetAlongNormal(double d) => new Frame3(Origin + W * d, U, V, W);
    public Frame3 Flipped() => new Frame3(Origin, V, U, -W);

    /// <summary>Матрица «локальные -> мировые» для передачи в рендер.</summary>
    public Matrix4d ToMatrix()
    {
        return new Matrix4d(
            U.X, U.Y, U.Z, 0,
            V.X, V.Y, V.Z, 0,
            W.X, W.Y, W.Z, 0,
            Origin.X, Origin.Y, Origin.Z, 1);
    }

    public override string ToString() => $"Frame3(O={Origin}, W={W})";
}
