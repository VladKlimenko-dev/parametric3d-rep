using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Ops;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model.Features;

/// <summary>
/// УКЛОН — операция дерева. В ядре она была, но из модели ею воспользоваться было нельзя.
///
/// Грани задаются точками на них, нейтральная плоскость — точкой и нормалью: числами, а не
/// номерами граней, которые меняются при перестроении.
/// </summary>
public sealed class DraftFeature : Feature
{
    /// <summary>Точки на гранях, которые наклоняются.</summary>
    public readonly List<Vector3d> OnFaces = new();

    /// <summary>K05: устойчивые имена граней уклона (запоминаются при первом выборе по точке).</summary>
    public readonly List<string?> OnFaceNames = new();

    /// <summary>KRN-08 (08.3): грани уклона по роли — разрешаются при каждой перестройке.</summary>
    public readonly List<FaceSelector> OnSelectors = new();

    /// <summary>Нейтральная плоскость: сечение, которое остаётся на месте.</summary>
    public Vector3d NeutralPoint;
    public Vector3d NeutralNormal = Vector3d.UnitZ;

    public ValueRef Angle = new(3);

    /// <summary>k112: неподвижное ребро выбранной грани (шарнир). Задано — нейтральная плоскость не нужна.</summary>
    public Parametric3D.Kernel.Topology.EdgeRef? Hinge;
    /// <summary>k112: смещаемое ребро (кривая) той же грани — задаёт, в какую сторону от шарнира уходит грань.</summary>
    public Parametric3D.Kernel.Topology.EdgeRef? Moving;
    /// <summary>k112: Angle — итоговый угол к соседней грани (опорной плоскости), а не приращение от текущего.</summary>
    public bool Total;
    /// <summary>k112: смещаемое ребро — наружу (материал добавляется) вместо внутрь.</summary>
    public bool Reverse;
    /// <summary>k112: опорная плоскость для угла (null — соседняя грань через шарнир).</summary>
    public Vector3d? RefNormal;

    /// <summary>Мутация P3D_DRAFTTOTAL=0: «итоговый угол» не учитывается (угол — всегда приращение).</summary>
    public static readonly bool TotalOff = Environment.GetEnvironmentVariable("P3D_DRAFTTOTAL") == "0";

    /// <summary>k112: по последнему расчёту — шарнир, угол до и после, применённый поворот (для панели и ручки).</summary>
    public Parametric3D.Kernel.Ops.DraftOp.HingeInfo? LastHinge;
    public double? LastBefore, LastAfter, LastApplied;

    public bool ByEdge => Hinge != null && !Parametric3D.Kernel.Ops.DraftOp.HingeOff;

    public DraftFeature(string name = "Draft") { Name = name; }

    public override string Summary => $"Draft {Angle}\u00b0 ({OnFaces.Count} faces)";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: no body.");
        if (OnFaces.Count == 0 && OnSelectors.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: no faces selected.");
        LastHinge = null; LastBefore = LastAfter = LastApplied = null;
        if (ByEdge) { ExecuteHinged(ctx); return; }
        if (NeutralNormal.LengthSquared < 1e-18)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: the neutral plane is not set.");

        double deg = Angle.Resolve(ctx.Parameters);
        if (Math.Abs(deg) < 1e-9) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: the angle must not be zero.");

        bool drafted = false;
        var faces = OnFaces.Count > 0 ? FaceRefs.Resolve(ctx.Bodies[0], OnFaces, OnFaceNames, "Draft") : new List<Vector3d>();
        foreach (var sel in OnSelectors)
        {
            var fs = sel.Resolve(ctx.Bodies);
            if (fs.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Draft: the selector {sel} selects no faces.");
            faces.AddRange(fs.Select(FaceRefs.Inside));
        }
        Kernel.OperationContract.Apply("Draft", ctx.Bodies, b => drafted = DraftOp.Apply(b, faces, NeutralPoint, NeutralNormal.Normalized(), deg, ctx.Warnings));
        if (!drafted)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Draft: the faces could not be drafted.");
    }

    /// <summary>
    /// k112: УКЛОН ПО РЕБРУ. Грань поворачивается вокруг неподвижного ребра (у цилиндра — окружности) так, что смещаемое
    /// ребро уходит внутрь (или наружу — Reverse) на угол Angle от текущего положения; Total — Angle есть итоговый угол
    /// между гранью и соседней гранью через шарнир (или опорной плоскостью), поворот считается от текущего угла.
    /// </summary>
    void ExecuteHinged(BuildContext ctx)
    {
        var faces = OnFaces.Count > 0 ? FaceRefs.Resolve(ctx.Bodies[0], OnFaces, OnFaceNames, "Draft") : new List<Vector3d>();
        foreach (var sel in OnSelectors) faces.AddRange(sel.Resolve(ctx.Bodies).Select(FaceRefs.Inside));
        if (faces.Count != 1)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: a draft about a fixed edge takes exactly one face.");
        var info = Parametric3D.Kernel.Ops.DraftOp.Hinge(ctx.Bodies, faces[0], Hinge!.Value, Moving, out var why)
                   ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: " + why + ".");
        LastHinge = info;
        double value = Angle.Resolve(ctx.Parameters);
        LastBefore = info.AngleTo(0, RefNormal);
        double deg;
        if (Total && !TotalOff)
        {
            if (LastBefore == null) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: there is no neighbouring face or reference plane to measure the angle from.");
            deg = info.Solve(value, RefNormal)
                  ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, $"Draft: the angle {value:0.###}° cannot be reached (now {LastBefore:0.###}°).");
            if (Math.Abs(deg) < 1e-9) { LastApplied = 0; LastAfter = LastBefore; return; }   // уже такой угол — тело не меняется
        }
        else
        {
            if (Math.Abs(value) < 1e-9) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Draft: the angle must not be zero.");
            deg = Reverse ? -value : value;
        }
        LastApplied = deg;
        LastAfter = info.AngleTo(deg, RefNormal);
        bool drafted = false;
        Kernel.OperationContract.Apply("Draft", ctx.Bodies, b => drafted = Parametric3D.Kernel.Ops.DraftOp.ApplyHinged(b, info, deg, ctx.Warnings));
        if (!drafted)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Draft: " + (ctx.Warnings.LastOrDefault()?.Replace("Draft: ", "") ?? "the face could not be drafted."));
    }
}

/// <summary>
/// РЕБРО ЖЁСТКОСТИ по эскизу: незамкнутый контур наращивается в заданную сторону до встречи с
/// телом и утолщается.
/// </summary>
public sealed class RibFeature : Feature
{
    public uint SketchId;

    /// <summary>Направление наращивания (обычно вниз, к телу).</summary>
    public Vector3d Grow = -Vector3d.UnitZ;

    public ValueRef Thickness = new(3);

    /// <summary>Предел наращивания, чтобы ребро не уходило в бесконечность.</summary>
    public ValueRef MaxLength = new(1000);

    public RibFeature(string name = "Rib") { Name = name; }

    public override string Summary => $"Rib {Thickness}";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Rib: no body.");

        var sketch = ctx.FindSketch(SketchId)
                     ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Rib: the sketch is not found.");

        double t = Thickness.Resolve(ctx.Parameters);
        if (t <= Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Rib: the thickness must be greater than zero.");
        if (Grow.LengthSquared < 1e-18) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Rib: the direction is not set.");

        var profile = sketch.ToProfile();
        bool built = false;
        if (profile.IsEmpty)
        {
            // K09: открытый эскиз (отрезок, дуга) — свободная кромка ребра.
            var edge = sketch.Entities.Where(e => e.Role == SkRole.Object && e is not SkPoint).Select(e => e.ToCurve3(sketch.Plane)).ToList();
            Kernel.OperationContract.Apply("Rib", ctx.Bodies, b => built = RibOp.BuildOpen(b, sketch.Plane, edge, Grow.Normalized(), t, MaxLength.Resolve(ctx.Parameters), ctx.Warnings));
        }
        else
            Kernel.OperationContract.Apply("Rib", ctx.Bodies, b => built = RibOp.Build(b, profile, Grow.Normalized(), t, MaxLength.Resolve(ctx.Parameters), ctx.Warnings));
        if (!built)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.Unsupported, "Rib: the rib could not be built. " + string.Join(" ", ctx.Warnings.Where(w => w.StartsWith("Rib:")).TakeLast(1)));
    }

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SketchId != 0) yield return SketchId;
    }
}
