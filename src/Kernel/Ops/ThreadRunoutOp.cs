using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// A single-start external trapezoidal groove, with optional radial tool withdrawal, on a round blank.
/// Repeated axial cells and a separately constructed nonperiodic ending; no whole-length Boolean.
/// </summary>
public static class ThreadRunoutOp
{
    public static TSolid Build(Frame3 frame, double lead, double height, double length,
        double radius, IReadOnlyList<Vector2d> profile, bool left, bool smoothExit = true)
    {
        var law = new ThreadRunoutLaw(frame, lead, height, length, radius, profile, left, smoothExit);
        bool partial = length < height - 1e-9;
        // Abrupt partial ending: copied cells stay strictly below the stop plane z = length.
        int count = Math.Max(0, smoothExit ? (int)Math.Floor((law.Start + law.LeftBase) / lead + 1e-10)
            : partial ? (int)Math.Ceiling(length / lead - 1e-10) - 1 : (int)Math.Floor(height / lead + 1e-10));
        double prefix = count * lead;
        // The same phase partition in both builders makes the seam an identical edge cycle.
        // Past full withdrawal (z = End + Q1 = length) the blank stays one plain cylinder face;
        // without the exit the groove stops at that plane with a flat step face.
        double plainFrom = partial ? length : double.NaN;
        var phases = law.Phases(prefix, plainFrom);
        TSolid Cell(double a, double b, bool full)
        {
            KernelContext.Checkpoint("thread.cell");
            string? why = null;
            return DirectRound.ThreadCell(law, a, b, full, phases, ref why, plainFrom)
                ?? throw new RebuildException(OperationOutcome.Unsupported, "Thread run-out: " + why);
        }
        if (count == 0) return Cell(0, height, !smoothExit);
        var seed = Cell(0, lead, true);
        var measure = AnalyticVolume.OfWithError(seed)
            ?? throw new RebuildException(OperationOutcome.Unsupported, "Thread run-out: no seed volume.");
        var result = TopologyClone.Deep(seed);
        result.BeginEdit("thread run-out assembly", EditKind.Shape);
        double volume = count * measure.Volume, error = count * measure.QuadratureError;
        for (int i = 1; i < count; i++)
        {
            KernelContext.Checkpoint("thread.repeat");
            var copy = TopologyTransform.TranslateCell(seed, frame.W * (i * lead))
                ?? throw new RebuildException(OperationOutcome.Unsupported, "Thread run-out: translation refused.");
            PeriodicSweepOp.Join(result, copy);
        }
        if (height - prefix > 1e-10)
        {
            var end = Cell(prefix, height, !smoothExit);
            var endMeasure = AnalyticVolume.OfWithError(end)
                ?? throw new RebuildException(OperationOutcome.Unsupported, "Thread run-out: no ending volume.");
            volume += endMeasure.Volume; error += endMeasure.QuadratureError;
            PeriodicSweepOp.Join(result, end);
        }
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
            throw new RebuildException(OperationOutcome.Unsupported, "Thread run-out: invalid seam: " + report);
        if (BooleanOp.VolumeVsFacets(result) is { Agree: false })
            throw new RebuildException(OperationOutcome.Unsupported, "Thread run-out: display/boundary volume mismatch.");
        return result;
    }
}

/// <summary>Tool parameter t, axial profile coordinate q: z=t+q, r=rProfile(q)+D(t).</summary>
internal sealed class ThreadRunoutLaw
{
    internal readonly Frame3 Frame;
    internal readonly double Lead, Height, Radius, Root, Q0, Q1, ML, MR, Depth, Start, End, Span, Omega;
    internal double LeftBase => Q0 + Depth / ML;
    internal double RightBase => Q1 + Depth / MR;
    internal readonly bool Left, SmoothExit;

    internal ThreadRunoutLaw(Frame3 frame, double lead, double height, double length, double radius,
        IReadOnlyList<Vector2d> profile, bool left, bool smoothExit = true)
    {
        if (!(lead > 0 && height >= length && length > 0 && radius > 0)
            || !double.IsFinite(lead + height + length + radius) || height / lead > 10000
            || profile.Count != 4 || profile.Any(p => !double.IsFinite(p.X + p.Y)))
            throw new RebuildException(OperationOutcome.InvalidInput, "Thread run-out: invalid blank or trapezoid.");
        Frame = frame; Lead = lead; Height = height; Radius = radius; Left = left; SmoothExit = smoothExit;
        var p = profile.OrderBy(x => x.Y).ThenBy(x => x.X).ToArray();
        Root = p[0].Y; Q0 = p[0].X; Q1 = p[1].X;
        if (Math.Abs(p[1].Y - Root) > 1e-12 || Math.Abs(p[2].Y - p[3].Y) > 1e-12
            || !(Q0 < Q1 && p[2].X < Q0 && p[3].X > Q1 && Root > 0 && Root < radius && p[2].Y >= radius))
            throw new RebuildException(OperationOutcome.Unsupported, "Thread run-out: a flat-root trapezoid is required.");
        ML = (p[2].Y - Root) / (p[2].X - Q0);
        MR = (p[3].Y - Root) / (p[3].X - Q1);
        Depth = radius - Root;
        if (RightBase - LeftBase >= lead - 1e-9)
            throw new RebuildException(OperationOutcome.InvalidInput, "Thread run-out: adjacent grooves overlap.");
        End = length - Q1;
        Span = Math.Min(lead, length + LeftBase - Q1);
        // This bound proves t+qRight(t) is strictly monotone throughout the cubic withdrawal.
        // Together with nonoverlapping turns it rules out folds and permits bracketed cap roots.
        if (smoothExit && Span <= 1.5 * Depth / MR * (1 + 1e-8))
            throw new RebuildException(OperationOutcome.InvalidInput, "Thread run-out: length is too short for a nonfolding smooth exit.");
        Start = End - Span;
        Omega = (left ? -1 : 1) * 2 * Math.PI / lead;
    }

    internal (double D, double Derivative) Offset(double t, bool full)
    {
        if (!SmoothExit || full || t <= Start) return (0, 0);
        if (t >= End) return (Depth, 0);
        double s = (t - Start) / Span;
        return (Depth * s * s * (3 - 2 * s), Depth * 6 * s * (1 - s) / Span);
    }

    // 0: left intersection with the blank, 1/2: root corners, 3: right intersection,
    // 4: left intersection of the NEXT turn, the other boundary of the cylindrical crest.
    internal (double Q, double Derivative) Boundary(int id, double t, bool full)
    {
        var d = Offset(t + (id == 4 ? Lead : 0), full);
        return id switch
        {
            0 => (Q0 + (Depth - d.D) / ML, -d.Derivative / ML),
            1 => (Q0, 0),
            2 => (Q1, 0),
            3 => (Q1 + (Depth - d.D) / MR, -d.Derivative / MR),
            _ => (Lead + Q0 + (Depth - d.D) / ML, -d.Derivative / ML)
        };
    }

    internal double[] Phases(double prefix, double plainFrom = double.NaN)
    {
        var values = new List<double> { 0, Lead / 4, Lead / 2, 3 * Lead / 4 };
        if (SmoothExit) { values.Add(Start); values.Add(End); }
        foreach (double z in new[] { 0.0, Lead, prefix, Height, plainFrom }.Where(double.IsFinite))
            foreach (bool full in new[] { true, false })
                for (int id = 0; id < 5; id++)
                {
                    double a = -3 * Lead, b = Height + 3 * Lead;
                    // Monotonicity is proved in the constructor, not assumed from point samples.
                    for (int i = 0; i < 60; i++)
                    {
                        double m = (a + b) / 2;
                        if (m + Boundary(id, m, full).Q < z) a = m; else b = m;
                    }
                    values.Add((a + b) / 2);
                }
        var sorted = values.Select(t => t - Lead * Math.Floor(t / Lead)).Order().ToArray();
        var result = new List<double> { 0 };
        foreach (double t in sorted)
            if (t - result[^1] > 1e-9 * Lead && Lead - t > 1e-9 * Lead) result.Add(t);
        return result.ToArray();
    }
}
