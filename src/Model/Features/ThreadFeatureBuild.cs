using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Threads;

namespace Parametric3D.Model.Features;

// Перестроение резьбы: грань → ось, торцы, начало и длина; вид; вырез.
public sealed partial class CosmeticThreadFeature
{
    /// <summary>
    /// k187: axis of a face a thread can go on (cylinder, circular extrude, revolve of a segment parallel to the axis);
    /// null — not such a face. One rule for the rebuild and for the face field of the thread panel.
    /// </summary>
    public static (Vector3d O, Vector3d W)? ThreadAxis(Kernel.Topology.TFace f) => GeometryAudit.AuthoritativeSurface(f) switch
    {
        Kernel.Surfaces.CylinderSurface cs => (cs.Axis.Origin, cs.Axis.W.Normalized()),
        Kernel.Surfaces.ExtrudedSurface { Base: Kernel.Curves.ArcCurve3 ba } es when Math.Abs(Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), es.Dir.Normalized())) - 1) < 1e-9 => (ba.Frame.Origin, es.Dir.Normalized()),
        // a revolve of a segment parallel to its axis is a cylinder (a revolved part: fishing neck etc.); P3D_THREADREVCYL=0 — refused
        Kernel.Surfaces.RevolvedSurface { Base: Kernel.Curves.LineCurve3 gl } rv when !DirectRound.RevCylOff && DirectRound.RevolvedCylinder(rv, gl) => (rv.Axis.Origin, rv.Axis.W.Normalized()),
        _ => null,
    };
    /// <summary>k187: the thread rebuild accepts this face (axis and radius).</summary>
    public static bool IsThreadFace(Kernel.Topology.TFace f) => ThreadAxis(f) != null && DirectEdit.RadiusAfter(f, 0) != null && DirectEdit.RadiusAfter(f, 1) != null;

    /// <summary>k188: a smooth run-out off the direct route goes through the general exact boolean (the same law sweep tool, whose root
    /// leaves the blank at a crossing angle: clearance P3D_RUNOUTCLEAR·P, default 0.06, instead of a degenerate tangency);
    /// P3D_THREADGENRUNOUT=0 — refused, as before.</summary>
    static readonly double GeneralRunoutClear = double.TryParse(Environment.GetEnvironmentVariable("P3D_RUNOUTCLEAR"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0.06;
    static readonly bool GeneralRunoutOff = Environment.GetEnvironmentVariable("P3D_THREADGENRUNOUT") == "0";
    public override void Execute(BuildContext ctx)
    {
        Lines = null;
        LastRoute = null;
        FaceId = 0; StartFaceId = 0;
        if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: no body.");
        if (OnFaces.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: no face selected.");
        var f = FacePick.Resolve(ctx.Bodies[0], OnFaces, OnFaceNames, "Thread")[0];
        var axis = ThreadAxis(f);
        if (axis == null || DirectEdit.RadiusAfter(f, 0) is not { } r0 || DirectEdit.RadiusAfter(f, 1) is not { } r1)
            throw new RebuildException(OperationOutcome.InvalidInput,
                $"Thread: the face is not cylindrical ({GeometryAudit.AuthoritativeSurface(f)?.GetType().Name ?? "no surface"}, face {f.Id}).");
        Diameter = 2 * r0;
        Internal = r1 < r0;
        var o = axis.Value.O; var w = axis.Value.W;
        AxisAt = o; AxisDir = w;

        // протяжённость грани вдоль оси — по точкам её рёбер
        var pts = new List<Vector3d>();
        foreach (var l in f.Loops) foreach (var ce in l.CoEdges) ce.Edge.Curve.Tessellate(pts, true);
        if (pts.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: the face has no edges.");
        double tMin = pts.Min(p => Vector3d.Dot(p - o, w)), tMax = pts.Max(p => Vector3d.Dot(p - o, w));
        FaceLength = tMax - tMin;
        if (!(FaceLength > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: the face has no length along its axis.");

        // начало (по оси) и направление — от начала к дальнему торцу грани
        double tol = 1e-6 * Math.Max(1, FaceLength);
        double s0 = StartAt is { } sp ? Math.Clamp(Vector3d.Dot(sp - o, w), tMin, tMax) : tMax;
        double dirSign = s0 - tMin >= tMax - s0 ? -1 : 1;
        var u = w * dirSign;
        FaceId = f.Id;
        // The start end face: planar, every boundary point on the start plane.
        StartFaceId = ctx.Bodies[0].Faces.FirstOrDefault(g => !ReferenceEquals(g, f)
            && GeometryAudit.AuthoritativeSurface(g) is Kernel.Surfaces.PlaneSurface
            && g.Loops.SelectMany(l => l.CoEdges).All(ce => { var q = new List<Vector3d>(); ce.Edge.Curve.Tessellate(q, true);
                return q.All(p => Math.Abs(Vector3d.Dot(p - o, w) - s0) < tol); }))?.Id ?? 0;
        double room = dirSign > 0 ? tMax - s0 : s0 - tMin;
        double len;
        if (Mode == ThreadLength.UpTo)
        {
            if (EndAt is not { } ep) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: select where the thread ends.");
            len = (Vector3d.Dot(ep - o, w) - s0) * dirSign;
        }
        else len = Length.Resolve(ctx.Parameters) is var lv && lv > Tol.Weld ? lv : room;
        if (!(len > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: the thread length must be positive (the end must lie along the face from the start).");
        if (len > room + tol) { ctx.Warnings.Add($"{Name}: the thread is limited to the face length {room:0.###}."); len = room; }
        ThreadLen = len;

        var spec = Spec;
        if (TypeId != null && spec == null)
            throw new RebuildException(OperationOutcome.InvalidInput, $"Thread: the type '{TypeId}' is not in the thread table{(ThreadDb.LoadError != null ? " (" + ThreadDb.LoadError + ")" : "")}.");
        double pitch = spec?.P ?? Pitch.Resolve(ctx.Parameters);
        if (!(pitch > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: the pitch must be positive.");
        int starts = Math.Max(1, spec?.Starts ?? 1);
        var b0 = o + w * s0;
        var e1 = Frame3.FromNormal(b0, u).U.Normalized();
        Lines = ViewLines(b0, u, e1, r0, pitch, starts, len, LeftHand, spec, Internal, SmoothExit);

        if (Cosmetic) { LastRoute = "cosmetic"; return; }
        if (spec == null) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: choose a thread type for a cut thread.");
        // вырез глубже грани (у вала — впадины не ниже поверхности) теряет смысл: отказ; номинал далеко от грани — предупреждение
        if (!ThreadDb.Candidates(Diameter, Internal).Contains(spec))
            throw new RebuildException(OperationOutcome.InvalidInput, $"Thread: {spec.Designation} is too large for a {(Internal ? "hole" : "shaft")} of diameter {Diameter:0.###}.");
        if (Internal ? Math.Abs(Diameter - (spec.TapDrill ?? spec.IntMinorD)) > 0.25 * spec.P + 0.1 : Math.Abs(Diameter - spec.DMajor) > 0.3 * spec.P + 0.05)
            ctx.Warnings.Add($"{Name}: {spec.Designation} is meant for a {(Internal ? "hole" : "shaft")} of diameter {(Internal ? spec.TapDrill ?? spec.IntMinorD : spec.DMajor):0.###}, the face is {Diameter:0.###}.");
        // This transient panel flag is NOT serialized. Preview is an explicitly labelled
        // helix/profile overlay, not an expensive boolean and not a committed solid.
        // Do not use KernelSettings.Preview alone: normal deferred validation also sets it.
        if (ShowPreview) { LastRoute = "overlay"; return; }
        using var exactScope = (KernelContext.Current ?? new KernelContext())
            .With(s => s with { GeometryPolicy = GeometryPolicy.PreserveBRep, Preview = false, LivePreview = false })
            .WithPolicy(OperationPolicy.Strict).Enter();
        var resultBodies = ctx.Bodies.ToList();
        if (ctx.Bodies.Count == 1 && DirectCylinder(ctx.Bodies[0], f, spec, Internal, b0, u, e1,
                len, FaceLength, r0, LeftHand, SmoothExit) is { } direct)
        {
            KernelContext.Checkpoint("thread.publish");
            ctx.Bodies[0] = direct;
            LastRoute = SmoothExit ? "direct-runout" : "direct-screw";
            return;
        }
        if (SmoothExit && GeneralRunoutOff)
            throw new RebuildException(OperationOutcome.Unsupported,
                "Thread: exact smooth run-out requires a single-start external trapezoid on an isolated cylinder. The original body is unchanged.");
        foreach (var tool in CutTools(spec, Internal, b0, u, e1, len, LeftHand, SmoothExit, r0, SmoothExit ? GeneralRunoutClear * spec.P : 0))
        {
            KernelContext.Checkpoint("thread.cut");
            BooleanOp.Subtract(resultBodies, tool, ctx.Warnings);
            if (resultBodies.Any(b => b.Guarantee != GuaranteeLevel.ExactAnalytic))
                throw new RebuildException(OperationOutcome.Unsupported, "Thread: the cut did not preserve exact surfaces.");
        }
        ctx.Bodies.Clear();
        ctx.Bodies.AddRange(resultBodies);
        LastRoute = "exact-boolean";
    }
}
