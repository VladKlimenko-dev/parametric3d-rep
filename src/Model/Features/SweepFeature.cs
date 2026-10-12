using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

/// <summary>Протягивание сечения по траектории.</summary>
public sealed class SweepFeature : Feature
{
    /// <summary>Эскиз сечения.</summary>
    public uint SectionSketchId;
    /// <summary>Эскиз траектории (берётся его первая незамкнутая цепочка).</summary>
    public uint PathSketchId;
    /// <summary>Либо трёхмерная линия как траектория.</summary>
    public uint PathLineId;

    /// <summary>Доля траектории от 0.001 до 1.0.</summary>
    public ValueRef LengthFraction = new(1.0);

    public SweepFeature(string name = "Sweep") { Name = name; }

    public override string Summary => $"{(Cut ? "Sweep cut" : "Sweep")} {LengthFraction}";

    public override void Execute(BuildContext ctx)
    {
        var section = ctx.FindSketch(SectionSketchId)
                      ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: no section sketch.");

        section.UpdateDimensionsFromParameters(ctx.Parameters!);
        section.Solve(ctx.Solver);
        section.RequireSolved(Name);

        Profile profile = section.ToProfile();
        if (profile.IsEmpty)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: the section is not closed.");

        ICurve3? path = ResolvePath(ctx);
        if (path == null)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: no path.");

        Kernel.Topology.TSolid? solid = null;
        double frac = LengthFraction.Resolve(ctx.Parameters);
        if (Math.Abs(frac - 1) < 1e-12 && ResolveChain(ctx) is { Count: > 1 } chain)
            solid = SweepOp.BuildChain(profile, chain, ctx.Warnings);
        solid ??= SweepOp.Build(profile, path, frac);

        if (Cut) BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
        else BooleanOp.Unite(ctx.Bodies, solid, ctx.Warnings);
    }

    private ICurve3? ResolvePath(BuildContext ctx)
    {
        if (PathLineId != 0)
        {
            var l = ctx.Part.FindById<Line3DItem>(PathLineId);
            if (l != null) return new LineCurve3(l.A, l.B);
        }
        if (PathSketchId != 0)
        {
            var ps = ctx.FindSketch(PathSketchId);
            if (ps != null)
            {
                var curves = ps.Entities
                    .Where(e => e.Role == Sketching.SkRole.Object && e is not Sketching.SkPoint)
                    .Select(e => e.ToCurve3(ps.Plane))
                    .ToList();
                if (curves.Count == 1) return curves[0];
                if (curves.Count > 1)
                {
                    // Сшиваем в одну ломаную по порядку следования.
                    var pts = new List<OpenTK.Mathematics.Vector3d>();
                    for (int i = 0; i < curves.Count; i++) curves[i].Tessellate(pts, i == 0);
                    return new PolylineCurve3(pts);
                }
            }
        }
        return null;
    }
    /// <summary>
    /// K09. Цепочка кривых траектории по СВЯЗНОСТИ концов (а не по порядку элементов эскиза),
    /// каждая — в направлении обхода. null, если кривые не складываются в одну незамкнутую цепь.
    /// </summary>
    private List<ICurve3>? ResolveChain(BuildContext ctx)
    {
        if (PathSketchId == 0) return null;
        var ps = ctx.FindSketch(PathSketchId);
        if (ps == null) return null;
        var left = ps.Entities.Where(e => e.Role == Sketching.SkRole.Object && e is not Sketching.SkPoint)
                              .Select(e => e.ToCurve3(ps.Plane)).ToList();
        if (left.Count < 2) return null;
        double tol = Tol.Weld * 10;
        bool Touch(OpenTK.Mathematics.Vector3d a, OpenTK.Mathematics.Vector3d b) => (a - b).Length < tol;
        // Начало — конец, которого не касается ни одна другая кривая.
        ICurve3? first = null;
        foreach (var c in left)
        {
            var s0 = c.PointAt(c.T0); var s1 = c.PointAt(c.T1);
            if (!left.Any(o => o != c && (Touch(o.PointAt(o.T0), s0) || Touch(o.PointAt(o.T1), s0)))) { first = c; break; }
            if (!left.Any(o => o != c && (Touch(o.PointAt(o.T0), s1) || Touch(o.PointAt(o.T1), s1)))) { first = c.Reversed(); left.Remove(c); left.Insert(0, first); break; }
        }
        if (first == null) return null;
        var chain = new List<ICurve3> { first };
        left.Remove(first); left.RemoveAll(c => ReferenceEquals(c, first));
        while (left.Count > 0)
        {
            var end = chain[^1].PointAt(chain[^1].T1);
            var next = left.FirstOrDefault(c => Touch(c.PointAt(c.T0), end) || Touch(c.PointAt(c.T1), end));
            if (next == null) return null;
            left.Remove(next);
            chain.Add(Touch(next.PointAt(next.T0), end) ? next : next.Reversed());
        }
        return chain;
    }

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SectionSketchId != 0) yield return SectionSketchId;
        if (PathSketchId != 0) yield return PathSketchId;
    }

}
