using OpenTK.Mathematics;

namespace Parametric3D.Kernel;

/// <summary>Осевой габаритный ящик. Используется для zoom-to-fit, отсечения и подбора масштаба сетки.</summary>
public struct Bounds3
{
    public Vector3d Min;
    public Vector3d Max;
    public bool IsEmpty;

    public static Bounds3 Empty => new Bounds3
    {
        Min = new Vector3d(double.MaxValue),
        Max = new Vector3d(double.MinValue),
        IsEmpty = true
    };

    public void Add(Vector3d p)
    {
        if (IsEmpty) { Min = p; Max = p; IsEmpty = false; return; }
        Min = Vector3d.ComponentMin(Min, p);
        Max = Vector3d.ComponentMax(Max, p);
    }

    public void Add(Bounds3 b)
    {
        if (b.IsEmpty) return;
        Add(b.Min); Add(b.Max);
    }

    public Vector3d Center => IsEmpty ? Vector3d.Zero : (Min + Max) * 0.5;
    public Vector3d Size => IsEmpty ? Vector3d.Zero : (Max - Min);
    public double Diagonal => IsEmpty ? 0.0 : (Max - Min).Length;

    public Bounds3 Expanded(double d)
    {
        if (IsEmpty) return this;
        return new Bounds3 { Min = Min - new Vector3d(d), Max = Max + new Vector3d(d), IsEmpty = false };
    }
}
