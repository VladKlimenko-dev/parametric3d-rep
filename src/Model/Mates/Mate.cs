using OpenTK.Mathematics;

namespace Parametric3D.Model.Mates;

/// <summary>Тип связи детали со сборкой (степени свободы, которые она оставляет).</summary>
public enum MateType
{
    /// <summary>Вращение вокруг одной оси (шарнир).</summary>
    Pin,
    /// <summary>Линейное перемещение вдоль одной оси.</summary>
    Slider,
    /// <summary>Вращение и перемещение вдоль одной оси.</summary>
    Cylinder,
    /// <summary>Перемещение в плоскости + вращение вокруг нормали.</summary>
    Planar,
    /// <summary>Свободное вращение вокруг точки (шарнир-шар).</summary>
    Ball,
    /// <summary>Жёсткая фиксация.</summary>
    Weld,
    /// <summary>u13: шар в цилиндре — свободное вращение и сдвиг вдоль оси.</summary>
    BallCylinder,
    /// <summary>u13: паз — сдвиг вдоль оси паза и вращение вокруг нормали плоскости паза.</summary>
    Slot,
    /// <summary>u13: шесть степеней свободы (связь только записана).</summary>
    Free
}

/// <summary>
/// Связь детали с родительской сборкой.
///
/// Хранит опорную геометрию (ось/плоскость/точку) в координатах сборки и в
/// координатах детали, плюс текущие значения свободных координат. Решение
/// кинематики сборки — отдельная задача; здесь зафиксирована структура данных
/// и ограничение степеней свободы.
/// </summary>
public sealed class Mate : ModelItem
{
    public override ItemKind Kind => ItemKind.Mate;

    public MateType Type = MateType.Weld;

    /// <summary>
    /// БАЗОВАЯ ДЕТАЛЬ. Пусто — опорная геометрия задана в координатах сборки; иначе — в
    /// координатах этой детали, и сопряжение образует цепочку: деталь ставится относительно
    /// базовой, а та — относительно своей базы. Без этого цепочка «Б к А, А к сборке» не
    /// выражалась вовсе: сопряжение знало только сборку.
    /// </summary>
    public Part? BasePart;

    /// <summary>Опорная точка/ось/плоскость в системе координат сборки (или базовой детали).</summary>
    public Vector3d AssemblyOrigin;
    public Vector3d AssemblyAxis = Vector3d.UnitZ;

    /// <summary>То же самое в системе координат детали.</summary>
    public Vector3d PartOrigin;
    public Vector3d PartAxis = Vector3d.UnitZ;

    /// <summary>
    /// ССЫЛКИ НА ГЕОМЕТРИЮ ПО УСТОЙЧИВЫМ ИМЕНАМ. Прежде здесь лежали номера граней; номера
    /// выдаются заново при каждом перестроении, поэтому сборка разваливалась бы при первом же
    /// изменении детали — ровно так, как раньше разваливались скругления. Имя грани («чем она
    /// создана») переживает изменение размеров.
    /// </summary>
    public string? PartFaceTag, BaseFaceTag;

    /// <summary>
    /// Пересчитывает опорную геометрию по именам граней: у плоской грани берутся точка и
    /// нормаль, у цилиндрической — ось. Возвращает, что удалось найти.
    /// </summary>
    public bool Resolve(Part part)
    {
        Ambiguity = null;
        bool okPart = PartFaceTag == null || FromFace(part, PartFaceTag, ref PartOrigin, ref PartAxis, ref Ambiguity);
        bool okBase = BaseFaceTag == null || BasePart == null ||
                      FromFace(BasePart, BaseFaceTag, ref AssemblyOrigin, ref AssemblyAxis, ref Ambiguity);
        return okPart && okBase;
    }

    /// <summary>KRN-08 (08.4): одноимённые грани с разной опорой (разные плоскости или оси) — неоднозначность: сопряжение не
    /// разрешается, причина здесь (прежде бралась первая попавшаяся грань).</summary>
    public string? Ambiguity;
    static readonly bool AmbiguityOff = Environment.GetEnvironmentVariable("P3D_MATEAMBIGUITY") == "0";

    private static bool FromFace(Part part, string tag, ref Vector3d origin, ref Vector3d axis, ref string? ambiguity)
    {
        var found = new List<(Vector3d O, Vector3d A, bool Plane)>();
        foreach (var body in part.Bodies)
            foreach (var f in body.Faces)
            {
                if (f.Tag != tag && !(tag != null && !tag.StartsWith("#") && Kernel.Topology.EdgeRef.Bare(f.Tag) == tag)) continue;
                var src = f.Surface is Kernel.Surfaces.FacetedSurface fs ? fs.Source : f.Surface;
                switch (src)
                {
                    case Kernel.Surfaces.PlaneSurface pl: found.Add((pl.F.Origin, pl.F.W.Normalized(), true)); break;
                    case Kernel.Surfaces.ExtrudedSurface ex when ex.Base is Kernel.Curves.ArcCurve3 arc: found.Add((arc.Frame.Origin, ex.Dir.Normalized(), false)); break;
                }
                if (AmbiguityOff && found.Count > 0) break;
            }
        if (found.Count == 0) return false;
        bool Same((Vector3d O, Vector3d A, bool Plane) a, (Vector3d O, Vector3d A, bool Plane) b)
        {
            if (a.Plane != b.Plane || Math.Abs(Math.Abs(Vector3d.Dot(a.A, b.A)) - 1) > 1e-9) return false;
            var d = b.O - a.O;
            return a.Plane ? Math.Abs(Vector3d.Dot(d, a.A)) <= 1e-6 : (d - a.A * Vector3d.Dot(d, a.A)).Length <= 1e-6;
        }
        if (!AmbiguityOff && found.Any(x => !Same(found[0], x)))
        {
            ambiguity = $"{found.Count} faces named '{Kernel.Topology.EdgeRef.Bare(tag)}' lie on different {(found[0].Plane ? "planes" : "axes")}";
            return false;
        }
        origin = found[0].O; axis = found[0].A;
        return true;
    }

    // Текущие значения свободных координат
    public double Angle;          // Pin, Cylinder
    public double Offset;         // Slider, Cylinder
    public Vector2d PlanarOffset; // Planar
    public double PlanarAngle;    // Planar
    public Quaternion BallRotation = Quaternion.Identity;
    /// <summary>u13: нормаль плоскости паза (соединение «паз»).</summary>
    public Vector3d SlotNormal;

    // Пределы
    public bool LimitEnabled;
    public double MinValue = double.NegativeInfinity;
    public double MaxValue = double.PositiveInfinity;

    public Mate(MateType type, string name) { Type = type; Name = name; }

    public int DegreesOfFreedom => Type switch
    {
        MateType.Pin => 1,
        MateType.Slider => 1,
        MateType.Cylinder => 2,
        MateType.Planar => 3,
        MateType.Ball => 3,
        MateType.BallCylinder => 4,
        MateType.Slot => 2,
        MateType.Free => 6,
        _ => 0
    };

    /// <summary>Итоговое положение детали относительно сборки.</summary>
    /// <summary>
    /// Свободная координата в пределах: у шарнира и ползуна это угол и сдвиг, у цилиндра —
    /// обе. Пределы хранились, но не применялись, и шарнир проворачивался за свой упор.
    /// </summary>
    public double Clamp(double v) => LimitEnabled && (GuardOff || MinValue <= MaxValue) ? Math.Clamp(v, MinValue, MaxValue) : v;

    /// <summary>Приводит свободные координаты к пределам (на случай задания их извне).</summary>
    public void ClampFree()
    {
        switch (Type)
        {
            case MateType.Pin: Angle = Clamp(Angle); break;
            case MateType.Slider: Offset = Clamp(Offset); break;
            case MateType.Cylinder: Angle = Clamp(Angle); Offset = Clamp(Offset); break;
            case MateType.Planar: PlanarAngle = Clamp(PlanarAngle); break;
        }
    }

    /// <summary>
    /// KRN-11 (11.2): НЕДОПУСТИМОЕ СОПРЯЖЕНИЕ — причина или null. Нулевая или нечисловая ось, нечисловая точка, нечисловые
    /// свободные координаты, пределы «от» больше «до», сопряжение детали с собой. Такое сопряжение не применяется (прежде:
    /// NaN в положении детали — деталь пропадала с экрана — или исключение Math.Clamp). Мутация P3D_MATEGUARD=0.
    /// </summary>
    public string? Invalid(Part part)
    {
        if (GuardOff) return null;
        static bool F(Vector3d v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
        if (ReferenceEquals(BasePart, part)) return "the part is mated to itself";
        if (!F(AssemblyAxis) || AssemblyAxis.LengthSquared < 1e-24) return "assembly axis is zero or not a number";
        if (!F(PartAxis) || PartAxis.LengthSquared < 1e-24) return "part axis is zero or not a number";
        if (!F(AssemblyOrigin) || !F(PartOrigin)) return "reference point is not a number";
        if (!double.IsFinite(Angle) || !double.IsFinite(Offset) || !double.IsFinite(PlanarAngle) || !double.IsFinite(PlanarOffset.X) || !double.IsFinite(PlanarOffset.Y)) return "free coordinate is not a number";
        if (Type is MateType.Slot && !F(SlotNormal)) return "slot normal is not a number";
        if (LimitEnabled && (double.IsNaN(MinValue) || double.IsNaN(MaxValue) || MinValue > MaxValue)) return $"limits from {MinValue} to {MaxValue}";
        var q = BallRotation; if (Type is MateType.Ball or MateType.BallCylinder && !(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) && q.LengthSquared > 1e-12f)) return "ball rotation is not a rotation";
        return null;
    }
    static readonly bool GuardOff = Environment.GetEnvironmentVariable("P3D_MATEGUARD") == "0";

    public void ApplyTo(Part part)
    {
        if (Type == MateType.Free) return;
        if (Invalid(part) != null) return;
        ClampFree();
        // Опорная геометрия базовой детали переводится в координаты сборки её текущим
        // положением: так цепочка считается от звена к звену.
        Vector3d baseOrigin = AssemblyOrigin, baseAxis = AssemblyAxis;
        if (BasePart != null)
        {
            Vector3 o = Vector3.Transform(ToF(AssemblyOrigin), BasePart.Rotation) + BasePart.Translation;
            Vector3 a = Vector3.Transform(ToF(AssemblyAxis), BasePart.Rotation);
            baseOrigin = new Vector3d(o.X, o.Y, o.Z);
            baseAxis = new Vector3d(a.X, a.Y, a.Z);
        }

        Vector3d aAxis = baseAxis.Normalized();

        // Совмещаем ось детали с осью сборки.
        Quaternion align = FromTo(PartAxis.Normalized(), aAxis);
        Quaternion rot = align;
        Vector3d pos = baseOrigin;

        switch (Type)
        {
            case MateType.Pin:
                rot = Quaternion.FromAxisAngle(ToF(aAxis), (float)Angle) * align;
                break;
            case MateType.Slider:
                pos += aAxis * Offset;
                break;
            case MateType.Cylinder:
                rot = Quaternion.FromAxisAngle(ToF(aAxis), (float)Angle) * align;
                pos += aAxis * Offset;
                break;
            case MateType.Planar:
                {
                    var f = Kernel.Frame3.FromNormal(baseOrigin, aAxis);
                    pos = f.ToWorld(PlanarOffset);
                    rot = Quaternion.FromAxisAngle(ToF(aAxis), (float)PlanarAngle) * align;
                    break;
                }
            case MateType.Ball:
                rot = BallRotation;
                break;
            case MateType.BallCylinder:
                rot = BallRotation;
                pos += aAxis * Offset;
                break;
            case MateType.Slot:
                {
                    // ось паза — AssemblyAxis; нормаль плоскости паза — PlanarNormal (в координатах базы)
                    var n = SlotNormal.LengthSquared < 1e-18 ? Kernel.Frame3.FromNormal(Vector3d.Zero, aAxis).U : SlotNormal.Normalized();
                    pos += aAxis * Offset;
                    rot = Quaternion.FromAxisAngle(ToF(n), (float)Angle) * align;
                    break;
                }
        }

        Vector3d offsetInPart = -PartOrigin;
        Vector3 rotated = Vector3.Transform(ToF(offsetInPart), rot);

        part.Rotation = rot;
        part.Translation = ToF(pos) + rotated;
    }

    /// <summary>
    /// НЕВЯЗКА: насколько связь НЕ выполнена при текущем положении детали. Расстояние между
    /// опорной точкой связи (в координатах сборки) и той же точкой детали, плюс угол между
    /// осями в градусах. Ноль — связь выполнена.
    /// </summary>
    public (double Distance, double AngleDeg) Residual(Part part)
    {
        Vector3d baseOrigin = AssemblyOrigin, baseAxis = AssemblyAxis;
        if (BasePart != null)
        {
            Vector3 o = Vector3.Transform(ToF(AssemblyOrigin), BasePart.Rotation) + BasePart.Translation;
            Vector3 a = Vector3.Transform(ToF(AssemblyAxis), BasePart.Rotation);
            baseOrigin = new Vector3d(o.X, o.Y, o.Z);
            baseAxis = new Vector3d(a.X, a.Y, a.Z);
        }

        Vector3 p = Vector3.Transform(ToF(PartOrigin), part.Rotation) + part.Translation;
        Vector3 d = Vector3.Transform(ToF(PartAxis), part.Rotation);
        Vector3d at = new(p.X, p.Y, p.Z), dir = new Vector3d(d.X, d.Y, d.Z).Normalized();

        // У ползуна и цилиндра сдвиг вдоль оси — свобода, а не невязка.
        Vector3d delta = at - baseOrigin;
        if (Type is MateType.Slider or MateType.Cylinder or MateType.BallCylinder or MateType.Slot)
            delta -= baseAxis.Normalized() * Vector3d.Dot(delta, baseAxis.Normalized());
        if (Type == MateType.Free) return (0, 0);

        double cos = Math.Clamp(Vector3d.Dot(dir, baseAxis.Normalized()), -1, 1);
        double angle = Type is MateType.Ball or MateType.BallCylinder or MateType.Slot ? 0 : Math.Acos(Math.Abs(cos)) * 180 / Math.PI;
        return (delta.Length, angle);
    }

    private static Vector3 ToF(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static Quaternion FromTo(Vector3d a, Vector3d b)
    {
        double d = Vector3d.Dot(a, b);
        if (d > 1 - 1e-12) return Quaternion.Identity;
        if (d < -1 + 1e-12)
        {
            Vector3d ortho = Math.Abs(a.Z) < 0.9 ? Vector3d.UnitZ : Vector3d.UnitX;
            Vector3d ax = Vector3d.Cross(a, ortho).Normalized();
            return Quaternion.FromAxisAngle(ToF(ax), MathF.PI);
        }
        Vector3d axis = Vector3d.Cross(a, b);
        return Quaternion.FromAxisAngle(ToF(axis.Normalized()), (float)Math.Acos(Math.Clamp(d, -1, 1)));
    }
}
