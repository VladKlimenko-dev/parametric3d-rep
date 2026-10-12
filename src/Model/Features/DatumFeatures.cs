using Parametric3D.Core;
using OpenTK.Mathematics;
using Parametric3D.Kernel;

namespace Parametric3D.Model.Features;

/// <summary>Способ построения вспомогательной плоскости.</summary>
public enum PlaneBy
{
    ThreePoints,
    OffsetFromPlane,
    AngleToPlane,
    NormalToAxis
}

/// <summary>
/// ВСПОМОГАТЕЛЬНАЯ ПЛОСКОСТЬ КАК ОПЕРАЦИЯ ДЕРЕВА. Прежде плоскость умела только хранить своё
/// положение: задать её можно было числами, и при изменении размеров она оставалась на месте.
/// Здесь хранится СПОСОБ и привязки, а положение считается заново при каждом перестроении —
/// поэтому смещение можно задать переменной, и плоскость поедет вслед за моделью.
///
/// Привязки на другие вспомогательные элементы делаются по имени: имя переживает перестроение,
/// а номер выдаётся заново.
/// </summary>
public sealed class DatumPlaneFeature : Feature
{
    public PlaneBy By = PlaneBy.OffsetFromPlane;

    /// <summary>Имя исходной плоскости (для смещения, угла) — пусто означает XY детали.</summary>
    public string BasePlane = "";

    /// <summary>Имя оси (для угла и для перпендикулярности).</summary>
    public string BaseAxis = "";

    public ValueRef Offset = new(20);
    public ValueRef Angle = new(30);

    /// <summary>Точки для построения по трём точкам.</summary>
    public readonly List<Vector3d> Points = new();

    public DatumPlaneFeature(string name = "Плоскость") { Name = name; }

    public override string Summary => By switch
    {
        PlaneBy.ThreePoints => "Плоскость по трём точкам",
        PlaneBy.OffsetFromPlane => $"Плоскость со смещением {Offset}",
        PlaneBy.AngleToPlane => $"Плоскость под углом {Angle}\u00b0",
        _ => "Плоскость перпендикулярно оси"
    };

    public override void Execute(BuildContext ctx)
    {
        Frame3 frame;
        switch (By)
        {
            case PlaneBy.ThreePoints:
                if (Points.Count < 3) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Плоскость: нужны три точки.");
                frame = DatumBuilder.PlaneThroughPoints(Points[0], Points[1], Points[2]);
                break;

            case PlaneBy.OffsetFromPlane:
                frame = DatumBuilder.PlaneOffset(ctx.PlaneByName(BasePlane), Offset.Resolve(ctx.Parameters));
                break;

            case PlaneBy.AngleToPlane:
            {
                var (at, dir) = ctx.AxisByName(BaseAxis);
                frame = DatumBuilder.PlaneAtAngle(ctx.PlaneByName(BasePlane), at, dir, Angle.Resolve(ctx.Parameters));
                break;
            }

            default:
            {
                var (at, dir) = ctx.AxisByName(BaseAxis);
                Vector3d through = Points.Count > 0 ? Points[0] : at;
                frame = DatumBuilder.PlaneNormalToLine(dir, through);
                break;
            }
        }

        ctx.Planes[Name] = frame;
    }
}

/// <summary>Способ построения вспомогательной оси.</summary>
public enum AxisBy
{
    TwoPoints,
    NormalToPlane,
    PlaneIntersection,
    FaceAxis
}

/// <summary>
/// ВСПОМОГАТЕЛЬНАЯ ОСЬ КАК ОПЕРАЦИЯ ДЕРЕВА. Ось по грани ищется по УСТОЙЧИВОМУ ИМЕНИ грани —
/// тому самому, что переживает изменение размеров.
/// </summary>
public sealed class DatumAxisFeature : Feature
{
    static readonly bool AxisAmbiguityOff = Environment.GetEnvironmentVariable("P3D_AXISAMBIGUITY") == "0";
    public AxisBy By = AxisBy.TwoPoints;

    public readonly List<Vector3d> Points = new();
    public string BasePlane = "";
    public string SecondPlane = "";

    /// <summary>Подпись цилиндрической или конической грани (устойчивое имя).</summary>
    public string FaceTag = "";

    public DatumAxisFeature(string name = "Ось") { Name = name; }

    public override string Summary => By switch
    {
        AxisBy.TwoPoints => "Ось через две точки",
        AxisBy.NormalToPlane => "Ось перпендикулярно плоскости",
        AxisBy.PlaneIntersection => "Ось по пересечению плоскостей",
        _ => "Ось грани"
    };

    public override void Execute(BuildContext ctx)
    {
        (Vector3d At, Vector3d Dir) axis;
        switch (By)
        {
            case AxisBy.TwoPoints:
                if (Points.Count < 2) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Ось: нужны две точки.");
                axis = DatumBuilder.AxisThroughPoints(Points[0], Points[1]);
                break;

            case AxisBy.NormalToPlane:
                axis = DatumBuilder.AxisNormalToPlane(ctx.PlaneByName(BasePlane),
                                                     Points.Count > 0 ? Points[0] : ctx.PlaneByName(BasePlane).Origin);
                break;

            case AxisBy.PlaneIntersection:
                axis = DatumBuilder.AxisOfPlanes(ctx.PlaneByName(BasePlane), ctx.PlaneByName(SecondPlane));
                break;

            default:
            {
                // Среди одноимённых граней берётся та, у которой ось вообще есть: подписи у
                // граней резца повторяются, и первой попадалась плоская.
                var same = ctx.Bodies.SelectMany(b => b.Faces).Where(f => f.Tag == FaceTag || (FaceTag != null && !FaceTag.StartsWith("#") && Kernel.Topology.EdgeRef.Bare(f.Tag) == FaceTag)).ToList();
                if (same.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Ось грани: грань «{FaceTag}» не найдена.");

                var withAxis = same.Where(f =>
                {
                    var src = f.Surface is Kernel.Surfaces.FacetedSurface fs ? fs.Source : f.Surface;
                    return src is Kernel.Surfaces.ExtrudedSurface ex && ex.Base is Kernel.Curves.ArcCurve3
                           || src is Kernel.Surfaces.RevolvedSurface;
                }).ToList();
                var with = withAxis.FirstOrDefault() ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Ось грани: среди граней «{FaceTag}» нет цилиндрической или конической.");
                axis = DatumBuilder.AxisOfFace(with);
                // KRN-08 (08.4): одноимённые грани с РАЗНЫМИ осями (копии массива, разделившееся отверстие) — неоднозначность,
                // а не первая попавшаяся; одна ось у всех кусков — та же ось. Мутация P3D_AXISAMBIGUITY=0.
                if (!AxisAmbiguityOff && withAxis.Count > 1)
                {
                    var axes = withAxis.Select(f => (F: f, A: DatumBuilder.AxisOfFace(f))).ToList();
                    bool Coaxial((Vector3d At, Vector3d Dir) a, (Vector3d At, Vector3d Dir) b)
                    {
                        var wa = a.Dir.Normalized(); var wb = b.Dir.Normalized();
                        if (Math.Abs(Math.Abs(Vector3d.Dot(wa, wb)) - 1) > 1e-9) return false;
                        var d = b.At - a.At; return (d - wa * Vector3d.Dot(d, wa)).Length <= 1e-6;
                    }
                    if (axes.Any(x => !Coaxial(axes[0].A, x.A)))
                        throw new AmbiguousReferenceException($"Datum axis «{Name}»", axes.Select(x => (x.F.Tag, x.A.At)).ToList(),
                            $"{axes.Count} faces named '{Kernel.Topology.EdgeRef.Bare(FaceTag ?? "")}' have different axes");
                }
                break;
            }
        }

        ctx.Axes[Name] = axis;
    }
}

/// <summary>Вспомогательная точка как операция дерева.</summary>
public sealed class DatumPointFeature : Feature
{
    /// <summary>Пересечение оси с плоскостью; пусто — точка задана прямо.</summary>
    public string BaseAxis = "";
    public string BasePlane = "";
    public Vector3d At;

    public DatumPointFeature(string name = "Точка") { Name = name; }

    public override string Summary =>
        string.IsNullOrEmpty(BaseAxis) ? "Точка по координатам" : "Точка на пересечении оси и плоскости";

    public override void Execute(BuildContext ctx)
    {
        if (string.IsNullOrEmpty(BaseAxis)) { ctx.Points[Name] = At; return; }

        var (at, dir) = ctx.AxisByName(BaseAxis);
        ctx.Points[Name] = DatumBuilder.PointLineMeetsPlane(at, dir, ctx.PlaneByName(BasePlane));
    }
}
