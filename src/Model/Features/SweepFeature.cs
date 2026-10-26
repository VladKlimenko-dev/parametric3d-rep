using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;

namespace Parametric3D.Model.Features;

/// <summary>Протягивание сечения по траектории.</summary>
public sealed class SweepFeature : Feature
{
    public override bool InstantPreviewSafe => true;
    public override bool InstantReplaySafe => true;
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
    /// <summary>
    /// k107: НАПРАВЛЕНИЕ ПРОТЯЖКИ (стрелка на траектории, как в Creo): true — траектория проходится от другого конца; сечение
    /// встаёт в её начало.
    /// </summary>
    public bool Reverse;

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

        // k111: цепочка траектории по связности (и замкнутая), с началом у сечения и направлением (Reverse)
        var chain = OrderedChain(ctx.FindSketch, section);
        ICurve3? path = chain is { Count: > 1 } ? Polyline(chain) : chain is { Count: 1 } ? chain[0] : ResolvePath(ctx);
        if (path == null)
            throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Sweep: no path.");
        if (Reverse && chain == null) path = path.Reversed();

        Kernel.Topology.TSolid? solid = null;
        double frac = LengthFraction.Resolve(ctx.Parameters);
        double twist = Twist.Resolve(ctx.Parameters);
        if (Math.Abs(frac - 1) < 1e-12 && Math.Abs(twist) < 1e-12 && !KeepOrientation && chain is { Count: > 1 })
            solid = SweepOp.BuildChain(profile, chain, ctx.Warnings);
        solid ??= SweepOp.Build(profile, path, frac, twist, KeepOrientation);
        // k116 (замечание 16:00, п. 3): зеркало и массивы протяжки — тело протяжки под преобразованием
        if (_x is { } xf)
            solid = Kernel.Topology.TopologyTransform.Apply(solid, xf) ?? Kernel.Ops.TransformOp.Copy(solid, xf);

        if (Cut) BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
        else BooleanOp.Unite(ctx.Bodies, solid, ctx.Warnings);
    }

    private Kernel.Ops.TransformOp.Xform? _x;
    /// <summary>k116: мутация P3D_SWEEPMIRROR=0 — протяжка не повторяется зеркалом/массивом (прежний отказ).</summary>
    static readonly bool MirrorOff = Environment.GetEnvironmentVariable("P3D_SWEEPMIRROR") == "0";
    public override void ExecuteTransformed(BuildContext ctx, Kernel.Ops.TransformOp.Xform x)
    {
        if (MirrorOff) { base.ExecuteTransformed(ctx, x); return; }
        _x = x;
        try { Execute(ctx); } finally { _x = null; }
    }

    private ICurve3? ResolvePath(BuildContext ctx) => PathOf(ctx.Part, ctx.FindSketch);

    static ICurve3 Polyline(List<ICurve3> chain)
    {
        var pts = new List<Vector3d>();
        for (int i = 0; i < chain.Count; i++) chain[i].Tessellate(pts, i == 0);
        return new PolylineCurve3(pts);
    }

    static readonly bool ClosedChainOff = Environment.GetEnvironmentVariable("P3D_SWEEPCLOSED") == "0";

    /// <summary>
    /// k111 (снимок 19:13): ЦЕПОЧКА ТРАЕКТОРИИ ЭСКИЗА — по связности концов, в том числе ЗАМКНУТАЯ (контур вокруг детали:
    /// прежде замкнутая цепь не складывалась, и кривые сшивались ломаной по порядку элементов эскиза — с перескоками). У
    /// замкнутой начало — узел, ближайший к началу эскиза сечения, обход — по нормали сечения (как сечение нарисовано); Reverse
    /// разворачивает. null — траектория не из эскиза (или не цепь).
    /// </summary>
    List<ICurve3>? OrderedChain(Func<uint, Sketching.Sketch?> find, Sketching.Sketch? section)
    {
        if (PathSketchId == 0 || find(PathSketchId) is not { } ps) return null;
        var chain = ChainOf(ps, out bool closed);
        if (chain == null) return null;
        if (closed && section != null)
        {
            var o = section.Plane.Origin;
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < chain.Count; i++) { double d = (chain[i].PointAt(chain[i].T0) - o).Length; if (d < bd) { bd = d; best = i; } }
            chain = chain.Skip(best).Concat(chain.Take(best)).ToList();
            if (Vector3d.Dot(chain[0].TangentAt(chain[0].T0), section.Plane.W) < 0) chain = Rev(chain);
        }
        if (Reverse) chain = Rev(chain);
        return chain;
    }
    static List<ICurve3> Rev(List<ICurve3> c) => Enumerable.Reverse(c).Select(x => x.Reversed()).ToList();

    /// <summary>
    /// k107: начало протяжки — точка, касательная (по ходу, с учётом Reverse) и нормаль плоскости траектории (для «верха»
    /// эскиза сечения); null — траектории нет.
    /// </summary>
    public (Vector3d P, Vector3d T, Vector3d N, Vector3d End)? StartFrame(Part part)
    {
        Sketching.Sketch? Find(uint id) => part.Sketches.FirstOrDefault(k => k.Id == id);
        var path = PathOf(part, Find);
        if (path == null) return null;
        var chain = OrderedChain(Find, SectionSketchId != 0 ? Find(SectionSketchId) : null);
        Vector3d n = PathSketchId != 0 && Find(PathSketchId) is { } pk ? pk.Plane.W : Vector3d.UnitZ;
        if (chain is { Count: > 0 })
        {
            // уже по ходу протяжки (Reverse учтён)
            var c0 = chain[0]; var c1 = chain[^1];
            var t0 = c0.TangentAt(c0.T0);
            if (t0.LengthSquared < 1e-24) return null;
            return (c0.PointAt(c0.T0), t0.Normalized(), n, c1.PointAt(c1.T1));
        }
        ICurve3 first = path, last = path;
        Vector3d a = first.PointAt(first.T0), b = last.PointAt(last.T1);
        Vector3d ta = first.TangentAt(first.T0), tb = -last.TangentAt(last.T1);
        if (ta.LengthSquared < 1e-24 || tb.LengthSquared < 1e-24) return null;
        return Reverse ? (b, tb.Normalized(), n, a) : (a, ta.Normalized(), n, b);
    }

    private ICurve3? PathOf(Part part, Func<uint, Sketching.Sketch?> find)
    {
        if (PathLineId != 0)
        {
            var l = part.FindById<Line3DItem>(PathLineId);
            if (l != null) return new LineCurve3(l.A, l.B);
            // u13: опорная кривая (через точки, по уравнению, проекция)
            if (part.FindById<DatumCurve>(PathLineId) is { Points.Count: >= 2 } dc) return new PolylineCurve3(dc.Points);
        }
        if (PathSketchId != 0)
        {
            var ps = find(PathSketchId);
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
    private static List<ICurve3>? ChainOf(Sketching.Sketch ps, out bool closed)
    {
        closed = false;
        var left = ps.Entities.Where(e => e.Role == Sketching.SkRole.Object && e is not Sketching.SkPoint)
                              .Select(e => e.ToCurve3(ps.Plane)).ToList();
        if (left.Count == 1) { closed = left[0].IsClosed; return left; }
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
        // k111: свободного конца нет — замкнутая цепь: начало — первая кривая
        if (first == null)
        {
            if (ClosedChainOff) return null;
            first = left[0]; closed = true;
        }
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
        if (closed && !Touch(chain[^1].PointAt(chain[^1].T1), chain[0].PointAt(chain[0].T0))) return null;
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
