using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Model;

/// <summary>
/// Массовые характеристики детали или узла: масса, центр тяжести и моменты инерции
/// относительно центра тяжести.
/// </summary>
public readonly struct MassReport
{
    public readonly double Volume;        // мм³
    public readonly double Mass;          // кг
    public readonly Vector3d Centre;      // мм, в координатах сборки
    public readonly double Ixx, Iyy, Izz, Ixy, Ixz, Iyz;   // кг·мм²

    public MassReport(double volume, double mass, Vector3d centre,
                      double ixx, double iyy, double izz, double ixy, double ixz, double iyz)
    {
        Volume = volume; Mass = mass; Centre = centre;
        Ixx = ixx; Iyy = iyy; Izz = izz; Ixy = ixy; Ixz = ixz; Iyz = iyz;
    }

    public override string ToString() =>
        $"масса {Mass:0.###} кг, центр ({Centre.X:0.##}, {Centre.Y:0.##}, {Centre.Z:0.##})";
}

/// <summary>
/// СВОДКА ПО МАССЕ ДЛЯ ДЕТАЛИ И УЗЛА. Деталь знала только массу; центра тяжести и моментов не
/// было, а для сборки не было и массы.
///
/// Моменты узла складываются НЕ НАПРЯМУЮ: у каждой детали они посчитаны относительно её
/// собственного центра, и перед сложением переносятся в общий центр (теорема Гюйгенса —
/// Штейнера). Простое сложение дало бы заниженные значения — тем сильнее, чем дальше детали
/// разнесены.
/// </summary>
public static class MassSummary
{
    public static MassReport Of(Part part)
    {
        double vol = 0, mass = 0;
        Vector3d acc = Vector3d.Zero;
        double ixx = 0, iyy = 0, izz = 0, ixy = 0, ixz = 0, iyz = 0;
        double rho = part.Material.Density;

        var bodies = new List<(Vector3d Centre, double Mass, (double Ixx, double Iyy, double Izz, double Ixy, double Ixz, double Iyz) I)>();

        foreach (var b in part.Bodies)
        {
            var md = Tessellator.Tessellate(b);
            double v = MassProps.Volume(md);
            if (v <= 0) continue;

            double m = v * 1e-9 * rho;
            var c = MassProps.Centroid(md);
            var inertia = MassProps.Inertia(md, rho);

            vol += v; mass += m; acc += c * m;
            bodies.Add((c, m, inertia));
        }

        if (mass <= 0) return new MassReport(vol, 0, Vector3d.Zero, 0, 0, 0, 0, 0, 0);
        Vector3d centre = acc / mass;

        foreach (var (c, m, i) in bodies)
        {
            Vector3d d = c - centre;
            ixx += i.Ixx + m * (d.Y * d.Y + d.Z * d.Z);
            iyy += i.Iyy + m * (d.X * d.X + d.Z * d.Z);
            izz += i.Izz + m * (d.X * d.X + d.Y * d.Y);
            ixy += i.Ixy - m * d.X * d.Y;
            ixz += i.Ixz - m * d.X * d.Z;
            iyz += i.Iyz - m * d.Y * d.Z;
        }

        return new MassReport(vol, mass, centre, ixx, iyy, izz, ixy, ixz, iyz);
    }

    /// <summary>Сводка по узлу: детали берутся в их положении в сборке.</summary>
    public static MassReport Of(Assembly asm)
    {
        double vol = 0, mass = 0;
        Vector3d acc = Vector3d.Zero;
        var parts = new List<(Vector3d Centre, double Mass, MassReport R)>();

        foreach (var p in asm.Parts)
        {
            var r = Of(p);
            if (r.Mass <= 0) continue;

            // Центр детали переводится в координаты сборки её положением.
            Vector3 c = Vector3.Transform(new Vector3((float)r.Centre.X, (float)r.Centre.Y, (float)r.Centre.Z),
                                          p.Rotation) + p.Translation;
            Vector3d world = new(c.X, c.Y, c.Z);

            vol += r.Volume; mass += r.Mass; acc += world * r.Mass;
            parts.Add((world, r.Mass, r));
        }

        if (mass <= 0) return new MassReport(vol, 0, Vector3d.Zero, 0, 0, 0, 0, 0, 0);
        Vector3d centre = acc / mass;

        double ixx = 0, iyy = 0, izz = 0, ixy = 0, ixz = 0, iyz = 0;
        foreach (var (c, m, r) in parts)
        {
            // Повороты деталей здесь не разворачивают матрицу инерции: пока учитывается только
            // перенос. Для повёрнутых деталей это приближение, и оно оговорено.
            Vector3d d = c - centre;
            ixx += r.Ixx + m * (d.Y * d.Y + d.Z * d.Z);
            iyy += r.Iyy + m * (d.X * d.X + d.Z * d.Z);
            izz += r.Izz + m * (d.X * d.X + d.Y * d.Y);
            ixy += r.Ixy - m * d.X * d.Y;
            ixz += r.Ixz - m * d.X * d.Z;
            iyz += r.Iyz - m * d.Y * d.Z;
        }

        return new MassReport(vol, mass, centre, ixx, iyy, izz, ixy, ixz, iyz);
    }
}
