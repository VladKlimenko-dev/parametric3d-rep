using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Threads;

namespace Parametric3D.Model.Features;

public sealed partial class CosmeticThreadFeature
{
    /// <summary>Whole section starts outside the nominal entrance, plus one lead of approach.</summary>
    public static double LeadInDistance(ThreadSpec spec, bool inner)
    {
        var zr = inner ? spec.IntZr : spec.ExtZr;
        return (spec.Lead > 0 ? spec.Lead : spec.P * Math.Max(1, spec.Starts))
            + Math.Max(0, zr.Max(p => p.X));
    }

    /// <summary>Trim ONLY the air-side overcut: adjacent turns must not overlap as solids.</summary>
    public static Vector2d[] NonOverlappingProfile(ThreadSpec spec, bool inner, double? blankRadius = null)
    {
        var polygon = (inner ? spec.IntZr : spec.ExtZr).ToList();
        double radius = blankRadius ?? (inner ? spec.IntStartR : spec.ExtStartR);
        double half = spec.P * 0.499;
        foreach (double sign in new[] { -1.0, 1.0 })
        {
            var clipped = new List<Vector2d>();
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
                bool insideA = sign * a.X <= half, insideB = sign * b.X <= half;
                if (!insideA && (inner ? a.Y > radius : a.Y < radius))
                    throw new RebuildException(OperationOutcome.InvalidInput, "Thread: the material-side profile overlaps adjacent pitches.");
                if (insideA) clipped.Add(a);
                if (insideA != insideB)
                {
                    var p = a + (b - a) * ((sign * half - a.X) / (b.X - a.X));
                    if (inner ? p.Y > radius : p.Y < radius)
                        throw new RebuildException(OperationOutcome.InvalidInput, "Thread: the profile cannot be separated outside the material.");
                    clipped.Add(p);
                }
            }
            polygon = clipped;
        }
        return polygon.ToArray();
    }

    /// <summary>Analytic frame law with C1 radial entry, optional exit; z=0 is the requested start.</summary>
    public static (Vector3d O, Vector3d U, Vector3d V) ToolFrame(ThreadSpec spec, bool inner,
        Vector3d b0, Vector3d u, Vector3d radial, double z, double len, bool left, bool smoothExit,
        double? blankRadius = null)
    {
        var zr = inner ? spec.IntZr : spec.ExtZr;
        double lead = spec.Lead > Tol.Weld ? spec.Lead : spec.P * Math.Max(1, spec.Starts);
        double rFace = blankRadius ?? (inner ? spec.IntStartR : spec.ExtStartR);
        double depth = zr.Max(p => Math.Abs(p.Y - rFace)) + 0.15 * spec.P;
        double outSign = inner ? -1 : 1;
        var v = Rot(radial, u, (left ? -1 : 1) * 2 * Math.PI * z / lead);
        double dr = spec.Taper ? Math.Tan(spec.HalfConeDeg * Math.PI / 180) * (inner ? -1 : 1) * z : 0;
        if (z < 0)
        {
            // Full depth before even the leading section point reaches the entrance.
            double t = Math.Clamp((-z - Math.Max(0, zr.Max(p => p.X))) / lead, 0, 1);
            dr += outSign * depth * t * t * (3 - 2 * t);
        }
        if (smoothExit)
        {
            double root = inner ? zr.Max(p => p.Y) : zr.Min(p => p.Y);
            double rootRight = zr.Where(p => Math.Abs(p.Y - root) < 1e-12).Max(p => p.X);
            double first = double.PositiveInfinity;
            for (int i = 0; i < zr.Length; i++)
            {
                var a = zr[i]; var b = zr[(i + 1) % zr.Length];
                if (Math.Abs(b.Y - a.Y) < 1e-14 || (a.Y - rFace) * (b.Y - rFace) > 0) continue;
                first = Math.Min(first, a.X + (b.X - a.X) * (rFace - a.Y) / (b.Y - a.Y));
            }
            double end = len - rootRight, exitLen = Math.Min(lead, len + first - rootRight);
            // Unsupported short/atypical profiles still receive a finite cosmetic overlay.
            // Committing their exact body is separately rejected by the direct builder.
            if (!double.IsFinite(exitLen) || exitLen <= 0) { end = len; exitLen = len / 2; }
            if (exitLen > 0)
            {
                double t = Math.Clamp((z - end + exitLen) / exitLen, 0, 1);
                // Exactly to the blank: extra clearance here would cross it with nonzero velocity.
                dr += outSign * Math.Abs(root - rFace) * t * t * (3 - 2 * t);
            }
        }
        return (b0 + u * z + v * dr, u, v);
    }

    public static List<TSolid> CutTools(ThreadSpec spec, bool inner, Vector3d b0, Vector3d u,
        Vector3d e1, double len, bool left, bool smoothExit, double? blankRadius = null)
    {
        if (!(len > 0) || !double.IsFinite(len) || !(spec.P > 0))
            throw new RebuildException(OperationOutcome.InvalidInput, "Thread: invalid length or pitch.");
        var zr = NonOverlappingProfile(spec, inner, blankRadius);
        if (zr.Length < 3) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: no closed groove profile.");
        int starts = Math.Max(1, spec.Starts);
        double lead = spec.Lead > Tol.Weld ? spec.Lead : spec.P * starts;
        double entry = LeadInDistance(spec, inner), travel = len + entry;
        int spans = Math.Max(1, (int)Math.Ceiling(2 * travel / lead));
        var tools = new List<TSolid>(starts);
        for (int k = 0; k < starts; k++)
        {
            KernelContext.Checkpoint("thread.tool");
            var radial = Rot(e1, u, 2 * Math.PI * k / starts);
            (Vector3d O, Vector3d U, Vector3d V) Law(double s) =>
                ToolFrame(spec, inner, b0, u, radial, -entry + travel * s, len, left, smoothExit, blankRadius);
            var first = Law(0);
            var plane = new Frame3(first.O, first.U, first.V);
            var curves = Enumerable.Range(0, zr.Length).Select(i => (ICurve3)new LineCurve3(
                plane.ToWorld(zr[i]), plane.ToWorld(zr[(i + 1) % zr.Length]))).ToList();
            var section = ProfileBuilder.Build(plane, curves);
            tools.Add(SweepOp.BuildByLaw(section, Law, 36 * spans, requireExact: true, lawSpans: spans));
        }
        return tools;
    }
}
