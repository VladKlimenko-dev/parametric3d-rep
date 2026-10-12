using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;

namespace Parametric3D.Model.Mates;

/// <summary>Вид ограничения сборки (неподвижная привязка, в отличие от кинематических соединений).</summary>
public enum ConstraintKind
{
    /// <summary>По умолчанию: базовые плоскости детали совмещаются с плоскостями сборки.</summary>
    Default,
    /// <summary>Совмещение: две плоскости, нормали НАВСТРЕЧУ (поверхности прижаты друг к другу).</summary>
    Mate,
    /// <summary>Выравнивание: две плоскости, нормали В ОДНУ сторону.</summary>
    Align,
    /// <summary>Ориентация: плоскости параллельны или под углом, без привязки по расстоянию.</summary>
    Orient,
    /// <summary>Вставка: оси двух цилиндров совмещаются (вал в отверстии).</summary>
    Insert,
    /// <summary>Цилиндрическое: оси выровнены, вращение и осевое смещение свободны.</summary>
    Cylindrical,
    /// <summary>Точка на линии.</summary>
    PointOnLine,
    /// <summary>Точка на поверхности (плоскости).</summary>
    PointOnSurface,
    /// <summary>Касание плоскости и цилиндра (или сферы).</summary>
    Tangent,
    /// <summary>Совмещение двух систем координат.</summary>
    CoordinateSystem
}

/// <summary>
/// ОГРАНИЧЕНИЕ СБОРКИ — неподвижная привязка детали, в отличие от кинематического соединения,
/// которое оставляет степени свободы. Прежде в ядре были только соединения (жёсткое, шарнир,
/// ползун, цилиндр, плоское, шар), и обычную сборку «прижать плоскостью, вставить вал» выразить
/// было нечем.
///
/// Геометрия задаётся так же, как у соединений: точка и направление на детали и на сборке
/// (или на базовой детали). Для плоскости направление — её нормаль, для цилиндра — ось.
///
/// Каждое ограничение умеет считать НЕВЯЗКУ — насколько оно не выполнено при текущем положении
/// детали. Это то же мерило, которым пользуется решатель связей, поэтому ограничения и
/// соединения решаются вместе.
/// </summary>
public sealed class AssemblyConstraint
{
    public ConstraintKind Kind;
    public string Name = "";

    /// <summary>Если задана — геометрия сборки берётся в координатах этой детали.</summary>
    public Part? BasePart;

    public Vector3d AssemblyOrigin;
    public Vector3d AssemblyAxis = Vector3d.UnitZ;
    public Vector3d PartOrigin;
    public Vector3d PartAxis = Vector3d.UnitZ;

    /// <summary>Зазор (для совмещения и выравнивания) или радиус (для касания).</summary>
    public double Offset;

    /// <summary>Угол в градусах — для ориентации.</summary>
    public double Angle;

    public bool Enabled = true;

    public AssemblyConstraint(ConstraintKind kind, string name = "")
    {
        Kind = kind;
        Name = string.IsNullOrEmpty(name) ? kind.ToString() : name;
    }

    /// <summary>
    /// Сколько степеней свободы ограничение забирает. Нужно, чтобы понимать, определена ли
    /// деталь: сумма по всем её ограничениям должна дойти до шести.
    /// </summary>
    public int Removes => Kind switch
    {
        ConstraintKind.Default => 6,
        ConstraintKind.CoordinateSystem => 6,
        ConstraintKind.Insert => 4,          // ось совмещена: остаются вращение и сдвиг вдоль неё
        ConstraintKind.Cylindrical => 4,
        ConstraintKind.Mate => 3,            // плоскость к плоскости: сдвиг по нормали и два поворота
        ConstraintKind.Align => 3,
        ConstraintKind.Orient => 2,          // только направление
        ConstraintKind.PointOnLine => 2,
        ConstraintKind.PointOnSurface => 1,
        ConstraintKind.Tangent => 1,
        _ => 0
    };

    /// <summary>
    /// НЕВЯЗКА: расстояние и угол, на которые ограничение не выполнено. Ноль — выполнено.
    /// У каждого вида своя мерка, и она отвечает именно его смыслу: у ориентации расстояние не
    /// считается вовсе, у точки на плоскости — только расстояние.
    /// </summary>
    public (double Distance, double AngleDeg) Residual(Part part)
    {
        var (baseOrigin, baseAxis) = InBase();
        var (partOrigin, partAxis) = InPart(part);

        Vector3d bn = baseAxis.LengthSquared < 1e-18 ? Vector3d.UnitZ : baseAxis.Normalized();
        Vector3d pn = partAxis.LengthSquared < 1e-18 ? Vector3d.UnitZ : partAxis.Normalized();
        Vector3d delta = partOrigin - baseOrigin;

        switch (Kind)
        {
            case ConstraintKind.Default:
            case ConstraintKind.CoordinateSystem:
                return (delta.Length, Deg(Vector3d.Dot(pn, bn)));

            case ConstraintKind.Mate:
                // Нормали навстречу: их скалярное произведение должно быть −1, а зазор по нормали
                // равен заданному.
                return (Math.Abs(Vector3d.Dot(delta, bn) - Offset), Deg(-Vector3d.Dot(pn, bn)));

            case ConstraintKind.Align:
                return (Math.Abs(Vector3d.Dot(delta, bn) - Offset), Deg(Vector3d.Dot(pn, bn)));

            case ConstraintKind.Orient:
            {
                double want = Math.Cos(Angle * Math.PI / 180);
                return (0, Math.Abs(Math.Acos(Math.Clamp(Vector3d.Dot(pn, bn), -1, 1)) * 180 / Math.PI
                                    - Math.Acos(Math.Clamp(want, -1, 1)) * 180 / Math.PI));
            }

            case ConstraintKind.Insert:
            case ConstraintKind.Cylindrical:
            {
                // Оси совпадают: точка детали лежит на оси сборки, направления сонаправлены.
                Vector3d off = delta - bn * Vector3d.Dot(delta, bn);
                return (off.Length, Deg(Math.Abs(Vector3d.Dot(pn, bn))));
            }

            case ConstraintKind.PointOnLine:
            {
                Vector3d off = delta - bn * Vector3d.Dot(delta, bn);
                return (off.Length, 0);
            }

            case ConstraintKind.PointOnSurface:
                return (Math.Abs(Vector3d.Dot(delta, bn) - Offset), 0);

            case ConstraintKind.Tangent:
                // Плоскость касается цилиндра или сферы: расстояние от оси (или центра) до
                // плоскости равно радиусу.
                return (Math.Abs(Math.Abs(Vector3d.Dot(delta, bn)) - Offset), 0);
        }

        return (0, 0);
    }

    /// <summary>Выполнено ли ограничение в пределах допусков сборки.</summary>
    public bool Satisfied(Part part, double tolerance = 0)
    {
        double tol = tolerance > 0 ? tolerance : Tol.Weld * 100;
        var (d, a) = Residual(part);
        return d <= tol && a <= 1e-3;
    }

    private static double Deg(double cos) =>
        Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;

    private (Vector3d Origin, Vector3d Axis) InBase()
    {
        if (BasePart == null) return (AssemblyOrigin, AssemblyAxis);

        Vector3 o = Vector3.Transform(ToF(AssemblyOrigin), BasePart.Rotation) + BasePart.Translation;
        Vector3 a = Vector3.Transform(ToF(AssemblyAxis), BasePart.Rotation);
        return (new Vector3d(o.X, o.Y, o.Z), new Vector3d(a.X, a.Y, a.Z));
    }

    private (Vector3d Origin, Vector3d Axis) InPart(Part part)
    {
        Vector3 o = Vector3.Transform(ToF(PartOrigin), part.Rotation) + part.Translation;
        Vector3 a = Vector3.Transform(ToF(PartAxis), part.Rotation);
        return (new Vector3d(o.X, o.Y, o.Z), new Vector3d(a.X, a.Y, a.Z));
    }

    private static Vector3 ToF(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
