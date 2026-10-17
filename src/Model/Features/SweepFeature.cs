using OpenTK.Mathematics;
using Parametric3D.Kernel;
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
    /// <summary>KRN-07 (07.3): закрутка сечения вокруг касательной, градусы (линейно по траектории; 0 — без закрутки).</summary>
    public ValueRef Twist = new(0.0);
    /// <summary>KRN-07 (07.3): закон ориентации — сечение не поворачивается (параллельные копии); иначе — рамка без вращения.</summary>
    public bool KeepOrientation;

    public SweepFeature(string name = "Sweep") { Name = name; }

    public override string Summary => $"{(Cut ? "Sweep cut" : "Sweep")} {LengthFraction}";

    public override void Execute(BuildContext ctx)
    {
        var section = ctx.FindSketch(SectionSketchId)
                      ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: no section sketch.");

        section.UpdateDimensionsFromParameters(ctx.Parameters!);
        section.Solve(ctx.Solver);
        section.RequireSolved(Name);

        Profile profile = section.ToSolidProfile("Sweep");
        if (profile.IsEmpty)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: the section is not closed.");

        ICurve3? path = ResolvePath(ctx);
        if (path == null)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: no path.");

        Kernel.Topology.TSolid? solid = null;
        double frac = LengthFraction.Resolve(ctx.Parameters);
        double twist = Twist.Resolve(ctx.Parameters);
        if (Math.Abs(frac - 1) < 1e-12 && Math.Abs(twist) < 1e-12 && !KeepOrientation && ResolveChain(ctx) is { Count: > 1 } chain)
            solid = SweepOp.BuildChain(profile, chain, ctx.Warnings);
        solid ??= SweepOp.Build(profile, path, frac, twist, KeepOrientation);

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

/// <summary>
/// u13: ВИНТОВАЯ ПРОТЯЖКА — сечение (замкнутый контур эскиза) по винтовой линии вокруг оси: шаг, число витков, левая или
/// правая. Винтовая линия начинается в центре сечения; её радиус — расстояние от центра сечения до оси. Ось — опорная ось
/// или отрезок эскиза сечения (без него — осевая линия эскиза).
/// </summary>
public sealed class HelicalSweepFeature : Feature
{
    public uint SketchId;
    public uint AxisId;
    public uint AxisEntityId;
    public ValueRef Pitch = new(5);
    public ValueRef Turns = new(3);
    public bool LeftHand;
    /// <summary>Точек ломаной винтовой линии на виток.</summary>
    public int StepsPerTurn = 72;

    public HelicalSweepFeature(string name = "Helical sweep") { Name = name; }
    public override string Summary => $"{(Cut ? "Helical cut" : "Helical sweep")} {Pitch} x {Turns}";

    /// <summary>Точки винтовой линии: начало c, ось (o, a), шаг, витки, левая.</summary>
    public static List<Vector3d> Helix(Vector3d c, Vector3d o, Vector3d a, double pitch, double turns, bool left, int perTurn)
    {
        a = a.Normalized();
        var foot = o + a * Vector3d.Dot(c - o, a);
        var r = c - foot; double rad = r.Length;
        if (rad < Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Helical sweep: the section centre lies on the axis.");
        var u = r / rad; var v = Vector3d.Cross(a, u);
        int n = Math.Max(8, (int)Math.Ceiling(perTurn * Math.Abs(turns)));
        var pts = new List<Vector3d>(n + 1);
        double sgn = left ? -1 : 1;
        for (int k = 0; k <= n; k++)
        {
            double th = 2 * Math.PI * turns * k / n;
            pts.Add(foot + rad * (u * Math.Cos(th) + v * (sgn * Math.Sin(th))) + a * (pitch * turns * k / n));
        }
        return pts;
    }

    public override void Execute(BuildContext ctx)
    {
        var sketch = ctx.FindSketch(SketchId)
                     ?? throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Helical sweep: no section sketch.");
        sketch.UpdateDimensionsFromParameters(ctx.Parameters!);
        sketch.Solve(ctx.Solver);
        sketch.RequireSolved(Name);
        Profile profile = sketch.ToSolidProfile("Helical sweep");
        if (profile.IsEmpty)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Helical sweep: the section is not closed.");
        Frame3? axis = null;
        if (AxisId != 0 && ctx.Part.FindById<DatumAxis>(AxisId) is { } da) axis = Frame3.FromNormal(da.Origin, da.Direction);
        if (axis == null && AxisEntityId != 0) axis = sketch.AxisFromEntity(AxisEntityId);
        axis ??= sketch.RevolutionAxis();
        if (axis == null)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Helical sweep: no axis. Draw a centerline in the sketch or pick an axis.");
        double pitch = Pitch.Resolve(ctx.Parameters), turns = Turns.Resolve(ctx.Parameters);
        if (!(Math.Abs(pitch) > Tol.Weld) || !(turns > 1e-6))
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Helical sweep: the pitch and the number of turns must be positive.");
        // центр сечения — среднее точек внешних контуров
        var samples = profile.Outers.SelectMany(l => { var p3 = new List<Vector3d>(); foreach (var c in l.Curves) c.Tessellate(p3, true); return p3; }).ToList();
        var centre = samples.Aggregate(Vector3d.Zero, (s, p) => s + p) / samples.Count;
        // начало координат сечения — в центр: протяжка переносит начало плоскости сечения в начало траектории
        var shifted = new Profile { Plane = new Frame3(centre, profile.Plane.U, profile.Plane.V) };
        foreach (var l in profile.Loops) shifted.Loops.Add(l);
        var ax = axis.Value;
        var pts = Helix(centre, ax.Origin, ax.W, pitch, turns, LeftHand, StepsPerTurn);
        var solid = SweepOp.Build(shifted, new PolylineCurve3(pts));
        if (Cut) BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
        else BooleanOp.Unite(ctx.Bodies, solid, ctx.Warnings);
    }

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SketchId != 0) yield return SketchId;
    }
}
