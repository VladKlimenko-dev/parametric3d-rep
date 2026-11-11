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
    public override void Execute(BuildContext ctx)
    {
        Lines = null;
        if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: no body.");
        if (OnFaces.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: no face selected.");
        var f = FacePick.Resolve(ctx.Bodies[0], OnFaces, OnFaceNames, "Thread")[0];
        var srf = GeometryAudit.AuthoritativeSurface(f);
        (Vector3d O, Vector3d W)? axis = srf switch
        {
            Kernel.Surfaces.CylinderSurface cs => (cs.Axis.Origin, cs.Axis.W.Normalized()),
            Kernel.Surfaces.ExtrudedSurface { Base: Kernel.Curves.ArcCurve3 ba } es when Math.Abs(Math.Abs(Vector3d.Dot(ba.Frame.W.Normalized(), es.Dir.Normalized())) - 1) < 1e-9 => (ba.Frame.Origin, es.Dir.Normalized()),
            _ => null,
        };
        if (axis == null || DirectEdit.RadiusAfter(f, 0) is not { } r0 || DirectEdit.RadiusAfter(f, 1) is not { } r1)
            throw new RebuildException(OperationOutcome.InvalidInput, "Thread: the face is not cylindrical.");
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
        Lines = ViewLines(b0, u, e1, r0, pitch, starts, len, LeftHand, spec, Internal);

        if (Cosmetic) return;
        if (spec == null) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: choose a thread type for a cut thread.");
        // вырез глубже грани (у вала — впадины не ниже поверхности) теряет смысл: отказ; номинал далеко от грани — предупреждение
        if (!ThreadDb.Candidates(Diameter, Internal).Contains(spec))
            throw new RebuildException(OperationOutcome.InvalidInput, $"Thread: {spec.Designation} is too large for a {(Internal ? "hole" : "shaft")} of diameter {Diameter:0.###}.");
        if (Internal ? Math.Abs(Diameter - (spec.TapDrill ?? spec.IntMinorD)) > 0.25 * spec.P + 0.1 : Math.Abs(Diameter - spec.DMajor) > 0.3 * spec.P + 0.05)
            ctx.Warnings.Add($"{Name}: {spec.Designation} is meant for a {(Internal ? "hole" : "shaft")} of diameter {(Internal ? spec.TapDrill ?? spec.IntMinorD : spec.DMajor):0.###}, the face is {Diameter:0.###}.");
        foreach (var tool in CutTools(spec, Internal, b0, u, e1, len, LeftHand, SmoothExit))
        {
            KernelContext.Checkpoint("thread.cut");
            BooleanOp.Subtract(ctx.Bodies, tool, ctx.Warnings);
        }
    }
}
