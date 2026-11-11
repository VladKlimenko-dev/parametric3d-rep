using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Kernel.Ops;

/// <summary>Repeat a full-pitch screw cell by exact translations, sewing shared end edges without Boolean.</summary>
public static class PeriodicSweepOp
{
    public static TSolid Build(Profile section, double lead, double length, bool left)
    {
        if (section.IsEmpty || section.HasSelfIntersections)
            throw new RebuildException(OperationOutcome.InvalidInput, "Periodic sweep: section must be closed and non-self-intersecting.");
        if (!(lead > 0 && length > 0) || !double.IsFinite(lead + length) || length / lead > 10000)
            throw new RebuildException(OperationOutcome.InvalidInput, "Periodic sweep: invalid length, lead or repeat count.");
        var frame = section.Plane; var axis = frame.W.Normalized();
        TSolid Cell(double h)
        {
            (Vector3d O, Vector3d U, Vector3d V) Law(double t)
            {
                double a = (left ? -1 : 1) * 2 * Math.PI * h * t / lead;
                var u = frame.U * Math.Cos(a) + frame.V * Math.Sin(a);
                return (frame.Origin + axis * (h * t), u, Vector3d.Cross(axis, u));
            }
            int spans = Math.Max(1, (int)Math.Ceiling(4 * h / lead));
            Vector3d Velocity(double t, Vector3d p) => axis * h
                + Vector3d.Cross(axis, p - frame.Origin) * ((left ? -1 : 1) * 2 * Math.PI * h / lead);
            return SweepOp.BuildByLaw(section, Law, 36 * spans, requireExact: true, lawSpans: spans, pointVelocity: Velocity);
        }
        if (length <= lead + 1e-10) return Cell(length);
        int count = (int)Math.Floor(length / lead);
        double tail = length - count * lead;
        var seed = Cell(lead);
        var measure = AnalyticVolume.OfWithError(seed)
            ?? throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: seed has no exact volume.");
        var result = TopologyClone.Deep(seed);
        result.BeginEdit("periodic sweep assembly", EditKind.Shape);
        double volume = count * measure.Volume, error = count * measure.QuadratureError;
        for (int i = 1; i < count; i++)
        {
            KernelContext.Checkpoint("sweep.repeat");
            var copy = TopologyTransform.TranslateCell(seed, axis * (i * lead))
                ?? throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: exact translation refused.");
            Join(result, copy);
        }
        if (tail > 1e-10)
        {
            var end = Cell(tail);
            var endMeasure = AnalyticVolume.OfWithError(end)
                ?? throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: tail has no exact volume.");
            volume += endMeasure.Volume; error += endMeasure.QuadratureError;
            var moved = TopologyTransform.TranslateCell(end, axis * (count * lead))
                ?? throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: exact tail translation refused.");
            Join(result, moved);
        }
        // The cells occupy consecutive disjoint axial slabs and the seam curves were
        // matched below. Volume is additive; carry the measured quadrature error too.
        result.Guarantee = GuaranteeLevel.ExactAnalytic;
        result.TopologyConvention = TopologyConventions.Version;
        foreach (var v in result.Vertices) v.Edges.Clear();
        foreach (var e in result.Edges)
        {
            e.V0.Edges.Add(e);
            if (!ReferenceEquals(e.V0, e.V1)) e.V1.Edges.Add(e);
        }
        AnalyticVolume.RememberComposition(result, volume, error);
        var report = SolidValidator.Validate(result);
        if (!report.IsValid)
            throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: sewn solid invalid: " + report.Summary());
        if (BooleanOp.VolumeVsFacets(result) is { } check && !check.Agree)
            throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: boundary volume disagrees with display mesh.");
        return result;
    }

    internal static void Join(TSolid left, TSolid right)
    {
        if (left.Shells.Count != 1 || right.Shells.Count != 1)
            throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: cell must have one shell.");
        var a = left.Faces.Single(f => f.Tag == "cap:end");
        var b = right.Faces.Single(f => f.Tag == "cap:start");
        var ae = a.Edges.ToArray(); var be = b.Edges.ToArray();
        if (ae.Length != be.Length) throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: unequal seam edges.");
        var edges = new Dictionary<TEdge, (TEdge Edge, bool Reverse)>();
        var vertices = new Dictionary<TVertex, TVertex>();
        var used = new HashSet<TEdge>();
        var remesh = new HashSet<TFace>();
        bool Near(Vector3d p, Vector3d q) => (p - q).Length <= 1e-7;
        foreach (var e in be)
        {
            var match = ae.FirstOrDefault(q => !used.Contains(q)
                && (Near(e.V0.P, q.V0.P) && Near(e.V1.P, q.V1.P) || Near(e.V0.P, q.V1.P) && Near(e.V1.P, q.V0.P))
                && Near(e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2), q.Curve.PointAt((q.Curve.T0 + q.Curve.T1) / 2)));
            if (match == null) throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: unmatched seam curve.");
            bool reverse = !Near(e.V0.P, match.V0.P);
            for (int sample = 0; sample <= 16; sample++)
            {
                double t = sample / 16.0;
                var p = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * t);
                var q = match.Curve.PointAt(match.Curve.T0 + (match.Curve.T1 - match.Curve.T0) * (reverse ? 1 - t : t));
                if (!Near(p, q)) throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: incompatible seam parameterization.");
            }
            edges.Add(e, (match, reverse)); used.Add(match);
            var oldSamples = ExactFaceMesher.EdgePoints(e);
            var newSamples = ExactFaceMesher.EdgePoints(match);
            bool SameDisplayPoint(Vector3d p, Vector3d q) =>
                (float)p.X == (float)q.X && (float)p.Y == (float)q.Y && (float)p.Z == (float)q.Z;
            bool sameSamples = oldSamples.Count == newSamples.Count
                && oldSamples.Select((p, i) => SameDisplayPoint(p, newSamples[reverse ? newSamples.Count - 1 - i : i])).All(same => same);
            if (!sameSamples)
                foreach (var ce in e.CoEdges)
                    if (ce.Loop?.Face is { } face && !ReferenceEquals(face, b)) remesh.Add(face);
            vertices[e.V0] = reverse ? match.V1 : match.V0;
            vertices[e.V1] = reverse ? match.V0 : match.V1;
        }
        void RemoveCap(TFace f)
        {
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) ce.Edge.CoEdges.Remove(ce);
            f.Shell!.Faces.Remove(f);
        }
        RemoveCap(a); RemoveCap(b);
        foreach (var e in right.Edges)
        {
            if (vertices.TryGetValue(e.V0, out var v0)) e.V0 = v0;
            if (vertices.TryGetValue(e.V1, out var v1)) e.V1 = v1;
            if (e.PolylineCache is { Count: >= 2 } samples)
            {
                samples[0] = e.V0.P; samples[^1] = e.V1.P;
                e.PolylineSource = (e.Curve, e.V0.P, e.V1.P);
            }
        }
        foreach (var f in right.Faces.ToArray())
        {
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
                if (edges.TryGetValue(ce.Edge, out var replacement))
                {
                    ce.Edge.CoEdges.Remove(ce); ce.Edge = replacement.Edge;
                    ce.Reversed ^= replacement.Reverse; ce.Edge.CoEdges.Add(ce);
                }
            left.Shells[0].Add(f);
        }
        foreach (var face in remesh)
            if (!ExactFaceMesher.RefreshSweepDisplay(face))
                throw new RebuildException(OperationOutcome.Unsupported, "Periodic sweep: seam display could not be reconciled.");
        left.DisplayMeshKey = null;
    }
}
