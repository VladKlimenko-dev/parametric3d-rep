using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Params;

namespace Parametric3D.Model.Features;

// u13: операции кнопок таблицы иконок — перенос/копия тела, прямая правка граней, массивы по точкам, по кривой и
// заполнением, косметическая резьба.

/// <summary>Грани операции по точкам и устойчивым именам (как у уклона): точка → грань тела.</summary>
internal static class FacePick
{
    public static List<TFace> Resolve(TSolid body, List<Vector3d> points, List<string?> names, string what)
    {
        var pts = FaceRefs.Resolve(body, points, names, what);
        var res = new List<TFace>();
        foreach (var p in pts)
        {
            var f = body.Faces.OrderBy(x => FaceRefs.Distance(x, p)).FirstOrDefault();
            if (f == null || FaceRefs.Distance(f, p) > 1e-3) throw new RebuildException(OperationOutcome.InvalidInput, $"{what}: a selected face is not found.");
            if (!res.Contains(f)) res.Add(f);
        }
        return res;
    }
}

/// <summary>ПЕРЕНОС И ПОВОРОТ ТЕЛА (копия с переносом): сдвиг dx, dy, dz и поворот вокруг оси; копия — объединяется с телом.</summary>
public sealed class MoveCopyFeature : Feature
{
    public ValueRef Dx = new(0), Dy = new(0), Dz = new(0), Angle = new(0);
    public Vector3d AxisAt;
    public Vector3d AxisDir = Vector3d.UnitZ;
    public bool Copy;
    public MoveCopyFeature(string name = "Move") { Name = name; }
    public override string Summary => $"{(Copy ? "Copy" : "Move")} ({Dx}, {Dy}, {Dz}) {Angle}°";

    public TransformOp.Xform Xform(ParameterTable? prm)
    {
        var by = new Vector3d(Dx.Resolve(prm), Dy.Resolve(prm), Dz.Resolve(prm));
        double deg = Angle.Resolve(prm);
        var m = TransformOp.Move(by);
        if (Math.Abs(deg) < 1e-12 || AxisDir.LengthSquared < 1e-18) return m;
        var r = TransformOp.Rotate(AxisAt, AxisDir.Normalized(), deg);
        return new TransformOp.Xform(p => m.Point(r.Point(p)), d => m.Direction(r.Direction(d)), r.Flips ^ m.Flips);
    }

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Move: no body.");
        var x = Xform(ctx.Parameters);
        var by = new Vector3d(Dx.Resolve(ctx.Parameters), Dy.Resolve(ctx.Parameters), Dz.Resolve(ctx.Parameters));
        if (by.Length < Tol.Weld && Math.Abs(Angle.Resolve(ctx.Parameters)) < 1e-9)
            throw new RebuildException(OperationOutcome.InvalidInput, "Move: the shift and the angle are both zero.");
        var src = ctx.Bodies.ToList();
        for (int i = 0; i < src.Count; i++)
        {
            var moved = TopologyTransform.Apply(src[i], x) ?? TransformOp.Copy(src[i], x);
            if (Copy) BooleanOp.Unite(ctx.Bodies, moved, ctx.Warnings);
            else ctx.Bodies[ctx.Bodies.IndexOf(src[i])] = moved;
        }
    }
}

public enum FaceEditMode { Offset = 0, Radius = 1, Delete = 2 }

/// <summary>
/// ПРЯМАЯ ПРАВКА ГРАНЕЙ: смещение (и перенос плоской грани по нормали) — соседи продлеваются до встречи; новый радиус
/// круглой грани; удаление сопряжений (скругление убирается, соседи сходятся в ребро).
/// </summary>
public sealed class FaceEditFeature : Feature
{
    public FaceEditMode Mode;
    public readonly List<Vector3d> OnFaces = new();
    public readonly List<string?> OnFaceNames = new();
    public ValueRef Distance = new(2);
    public ValueRef Radius = new(5);
    public FaceEditFeature(string name = "Edit faces") { Name = name; }
    public override string Summary => Mode switch
    {
        FaceEditMode.Radius => $"New radius {Radius} ({OnFaces.Count} faces)",
        FaceEditMode.Delete => $"Delete faces ({OnFaces.Count})",
        _ => $"Offset faces {Distance} ({OnFaces.Count} faces)",
    };

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no body.");
        if (OnFaces.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no faces selected.");
        var body = ctx.Bodies[0];
        var faces = FacePick.Resolve(body, OnFaces, OnFaceNames, Name);
        string? why = null;
        TSolid? res;
        switch (Mode)
        {
            case FaceEditMode.Delete:
                res = DirectEdit.RemoveBlends(body, faces, ref why);
                break;
            case FaceEditMode.Radius:
            {
                double r = Radius.Resolve(ctx.Parameters);
                if (!(r > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the radius must be positive.");
                var off = new Dictionary<TFace, double>();
                foreach (var f in faces)
                    off[f] = DirectEdit.OffsetForRadius(f, r) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: a selected face has no radius (not a cylinder, cone, sphere or torus).");
                res = DirectEdit.OffsetFaces(body, f => off.TryGetValue(f, out var d) ? d : 0, ref why);
                // сопряжение (скругление между двумя гранями): смещение одной полосы не касается соседей — полоса
                // убирается (соседи сходятся в ребро) и ребро скругляется заново новым радиусом
                if (res == null) res = Reblend(body, faces, r, ctx.Warnings, ref why);
                break;
            }
            default:
            {
                double d = Distance.Resolve(ctx.Parameters);
                if (Math.Abs(d) < Tol.Weld) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the distance must not be zero.");
                var set = faces.ToHashSet();
                res = DirectEdit.OffsetFaces(body, f => set.Contains(f) ? d : 0, ref why);
                break;
            }
        }
        if (res == null) throw new RebuildException(OperationOutcome.Unsupported, $"{Name}: the faces could not be changed ({why}).");
        ctx.Bodies[0] = res;
    }

    static TSolid? Reblend(TSolid body, List<TFace> faces, double r, List<string> warnings, ref string? why)
    {
        string? w2 = null;
        var sharp = DirectEdit.RemoveBlends(body, faces, ref w2);
        if (sharp == null) { why = $"{why}; reblend: {w2}"; return null; }
        var old = body.Vertices.Select(v => v.P).ToList();
        bool Known(Vector3d p) => old.Any(q => (q - p).LengthSquared < 1e-12);
        var fresh = sharp.Edges.Where(e => !Known(e.V0.P) && !Known(e.V1.P))
            .Where(e => faces.Any(f => FaceRefs.Distance(f, (e.V0.P + e.V1.P) / 2) < 4 * r + 1e-6)).ToList();
        if (fresh.Count == 0) { why = $"{why}; reblend: the sharp edge is not found"; return null; }
        var list = new List<TSolid> { sharp };
        try { FilletOp.RoundEdges(list, fresh.Select(EdgeRef.Of).ToList(), r, warnings); }
        catch (RebuildException ex) { why = $"{why}; reblend: {ex.Message}"; return null; }
        return list.Count == 1 ? list[0] : null;
    }
}

public enum PointsPatternMode { Points = 0, Curve = 1, Fill = 2 }

/// <summary>
/// МАССИВЫ ПО ТОЧКАМ, ПО КРИВОЙ И ЗАПОЛНЕНИЕМ. Повторяются выбранные операции (как у массивов u12) или всё тело. Точки —
/// отдельные точки эскиза (первая — исходное место, копии — в остальных); кривая — цепочка эскиза, копии через равные
/// доли длины от её начала; заполнение — сетка с шагом внутри замкнутой области эскиза (от первой точки эскиза или от
/// центра области), отступ от края — Margin.
/// </summary>
public sealed class PointsPatternFeature : Feature
{
    public PointsPatternMode Mode;
    public uint SketchId;
    public ValueRef Count = new(4);
    public ValueRef Spacing = new(10);
    public ValueRef Margin = new(0);
    public readonly List<string> Sources = new();
    public PointsPatternFeature(string name = "Pattern") { Name = name; }
    public override string Summary => Mode switch { PointsPatternMode.Curve => $"Curve pattern ×{Count}", PointsPatternMode.Fill => $"Fill pattern {Spacing}", _ => "Point pattern" };

    public List<Vector3d> Shifts(BuildContext ctx)
    {
        var sk = ctx.FindSketch(SketchId) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the sketch is not found.");
        sk.UpdateDimensionsFromParameters(ctx.Parameters!);
        sk.Solve(ctx.Solver);
        var plane = sk.Plane;
        var free = sk.Entities.OfType<Sketching.SkPoint>().Where(p => p.Owner == null && p.Role == Sketching.SkRole.Object).Select(p => plane.ToWorld(p.P)).ToList();
        var res = new List<Vector3d>();
        switch (Mode)
        {
            case PointsPatternMode.Points:
            {
                if (free.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the sketch needs at least two points (the first one is the original).");
                for (int i = 1; i < free.Count; i++) res.Add(free[i] - free[0]);
                break;
            }
            case PointsPatternMode.Curve:
            {
                var curves = sk.Entities.Where(e => e.Role == Sketching.SkRole.Object && e is not Sketching.SkPoint).Select(e => e.ToCurve3(plane)).ToList();
                if (curves.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the sketch has no curve.");
                var pts = new List<Vector3d>();
                for (int i = 0; i < curves.Count; i++) curves[i].Tessellate(pts, i == 0);
                var poly = new Kernel.Curves.PolylineCurve3(pts);
                int n = (int)Math.Round(Count.Resolve(ctx.Parameters));
                if (n < 2) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the count must be at least two.");
                bool closed = poly.IsClosed;
                var p0 = poly.PointAt(0);
                for (int i = 1; i < n; i++) res.Add(poly.PointAt(closed ? (double)i / n : (double)i / (n - 1)) - p0);
                break;
            }
            default:
            {
                double s = Spacing.Resolve(ctx.Parameters), margin = Margin.Resolve(ctx.Parameters);
                if (!(s > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the spacing must be positive.");
                Profile prof;
                try { prof = sk.ToProfile(); } catch (Exception ex) { throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: {ex.Message}"); }
                if (prof.IsEmpty) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the sketch has no closed region.");
                var loops = prof.Loops.Select(l => l.Sample(plane)).Where(l => l.Count >= 3).ToList();
                var all = loops.SelectMany(l => l).ToList();
                Vector2d c0 = free.Count > 0 ? plane.ToLocal2(free[0]) : all.Aggregate(Vector2d.Zero, (a, b) => a + b) / all.Count;
                double x0 = all.Min(p => p.X), x1 = all.Max(p => p.X), y0 = all.Min(p => p.Y), y1 = all.Max(p => p.Y);
                int i0 = (int)Math.Floor((x0 - c0.X) / s) - 1, i1 = (int)Math.Ceiling((x1 - c0.X) / s) + 1;
                int j0 = (int)Math.Floor((y0 - c0.Y) / s) - 1, j1 = (int)Math.Ceiling((y1 - c0.Y) / s) + 1;
                if ((long)(i1 - i0) * (j1 - j0) > LinearPatternFeature.MaxCopies * 4L) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: too many copies; increase the spacing.");
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                    {
                        if (i == 0 && j == 0) continue;
                        var q = c0 + new Vector2d(i * s, j * s);
                        if (!Inside(loops, q) || Edge(loops, q) < margin) continue;
                        res.Add(plane.ToWorld(q) - plane.ToWorld(c0));
                    }
                if (res.Count > LinearPatternFeature.MaxCopies) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: too many copies; increase the spacing.");
                break;
            }
        }
        return res;
    }

    static bool Inside(List<List<Vector2d>> loops, Vector2d q)
    {
        bool inside = false;
        foreach (var l in loops)
            for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                if ((l[i].Y > q.Y) != (l[j].Y > q.Y) && q.X < (l[j].X - l[i].X) * (q.Y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X) inside = !inside;
        return inside;
    }

    static double Edge(List<List<Vector2d>> loops, Vector2d q)
    {
        double best = double.MaxValue;
        foreach (var l in loops)
            for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
            {
                var a = l[j]; var ab = l[i] - a; double t = ab.LengthSquared < 1e-24 ? 0 : Math.Clamp(Vector2d.Dot(q - a, ab) / ab.LengthSquared, 0, 1);
                best = Math.Min(best, (a + ab * t - q).Length);
            }
        return best;
    }

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no body.");
        var shifts = Shifts(ctx);
        if (shifts.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no copies fit.");
        if (Sources.Count > 0) { FeatureReplay.Run(ctx, this, Sources, shifts.Select(TransformOp.Move).ToList(), Name); return; }
        var src = ctx.Bodies.ToList();
        foreach (var b in src)
            foreach (var d in shifts)
            {
                KernelContext.Checkpoint("pattern.copy");
                var x = TransformOp.Move(d);
                BooleanOp.Unite(ctx.Bodies, TopologyTransform.Apply(b, x) ?? TransformOp.Copy(b, x), ctx.Warnings);
            }
    }

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SketchId != 0) yield return SketchId;
    }
}

/// <summary>
/// КОСМЕТИЧЕСКАЯ РЕЗЬБА: обозначение резьбы на круглой грани (наружной или в отверстии) — диаметр, шаг, длина; тело не
/// меняется. Проверяется, что грань цилиндрическая; диаметр и вид (наружная/внутренняя) берутся с грани.
/// </summary>
public sealed class CosmeticThreadFeature : Feature
{
    public readonly List<Vector3d> OnFaces = new();
    public readonly List<string?> OnFaceNames = new();
    public ValueRef Pitch = new(1.5);
    /// <summary>Длина резьбы; 0 — во всю грань.</summary>
    public ValueRef Length = new(0);
    /// <summary>Найдено при перестроении: диаметр грани, внутренняя ли резьба, ось.</summary>
    public double Diameter { get; private set; }
    public bool Internal { get; private set; }
    public Vector3d AxisAt { get; private set; }
    public Vector3d AxisDir { get; private set; }
    public CosmeticThreadFeature(string name = "Thread") { Name = name; }
    public override string Summary => $"Thread {(Internal ? "M" : "")}{Diameter:0.##}x{Pitch}";

    public override void Execute(BuildContext ctx)
    {
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
        if (!(Pitch.Resolve(ctx.Parameters) > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Thread: the pitch must be positive.");
        Diameter = 2 * r0;
        AxisAt = axis.Value.O; AxisDir = axis.Value.W;
        Internal = r1 < r0;
    }
}

/// <summary>
/// ПОЛНОЕ СКРУГЛЕНИЕ: средняя грань между двумя параллельными боковыми заменяется полуцилиндром, касательным к обеим
/// (радиус — половина расстояния между ними). Грани: боковая, средняя, боковая. Делается вырезом полосы толщиной w/2 под
/// средней гранью и добавлением цилиндра радиуса w/2 по длине средней грани.
/// </summary>
public sealed class FullRoundFeature : Feature
{
    public readonly List<Vector3d> OnFaces = new();
    public readonly List<string?> OnFaceNames = new();
    public FullRoundFeature(string name = "Full round") { Name = name; }
    public override string Summary => "Full round";

    public override void Execute(BuildContext ctx)
    {
        if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Full round: no body.");
        if (OnFaces.Count != 3) throw new RebuildException(OperationOutcome.InvalidInput, "Full round: select a side face, the middle face and the other side face.");
        var body = ctx.Bodies[0];
        var fs = FacePick.Resolve(body, OnFaces, OnFaceNames, "Full round");
        if (fs.Count != 3) throw new RebuildException(OperationOutcome.InvalidInput, "Full round: three different faces are needed.");
        (Vector3d P, Vector3d N) Plane(TFace f)
        {
            var src = GeometryAudit.AuthoritativeSurface(f);
            if (src is not { IsPlanar: true }) throw new RebuildException(OperationOutcome.InvalidInput, "Full round: the faces must be planar.");
            var p = f.Loops[0].CoEdges[0].Start.P; return (p, f.NormalAt(p).Normalized());
        }
        var a = Plane(fs[0]); var m = Plane(fs[1]); var b = Plane(fs[2]);
        if (Vector3d.Dot(a.N, b.N) > -1 + 1e-9) throw new RebuildException(OperationOutcome.InvalidInput, "Full round: the side faces must be parallel and face away from each other.");
        double w = Math.Abs(Vector3d.Dot(b.P - a.P, a.N));
        if (w < Tol.Weld) throw new RebuildException(OperationOutcome.InvalidInput, "Full round: the side faces coincide.");
        if (Math.Abs(Vector3d.Dot(a.N, m.N)) > 1e-6) throw new RebuildException(OperationOutcome.InvalidInput, "Full round: the middle face must be perpendicular to the side faces.");
        var e = Vector3d.Cross(a.N, m.N).Normalized();
        var mv = fs[1].Loops.SelectMany(l => l.CoEdges).Select(c => c.Start.P).ToList();
        double s0 = mv.Min(v => Vector3d.Dot(v, e)), s1 = mv.Max(v => Vector3d.Dot(v, e));
        // точка на средней грани посередине между боковыми, в начале длины
        var q = m.P - a.N * (Vector3d.Dot(m.P - a.P, a.N) + w / 2);
        q += e * (s0 - Vector3d.Dot(q, e));
        double r = w / 2, len = s1 - s0, eps = 1e-3 * Math.Max(1, w);
        // вырез: прямоугольник поперёк (по ширине — от A до B, по высоте — от M вниз на r), длина — по средней грани
        var fr = new Frame3(q, a.N, m.N);
        Vector3d C(double x, double y) => q + a.N * x + m.N * y;
        var rect = new List<Kernel.Curves.ICurve3>
        {
            new Kernel.Curves.LineCurve3(C(-r, -r), C(r, -r)), new Kernel.Curves.LineCurve3(C(r, -r), C(r, eps)),
            new Kernel.Curves.LineCurve3(C(r, eps), C(-r, eps)), new Kernel.Curves.LineCurve3(C(-r, eps), C(-r, -r)),
        };
        var cutter = ExtrudeOp.Build(ProfileBuilder.Build(fr, rect), e * len);
        var axisAt = q - m.N * r;
        var circFrame = new Frame3(axisAt, a.N, m.N);
        var circle = new List<Kernel.Curves.ICurve3> { new Kernel.Curves.ArcCurve3(circFrame, r, 0, 2 * Math.PI) };
        var cyl = ExtrudeOp.Build(ProfileBuilder.Build(circFrame, circle), e * len);
        BooleanOp.Subtract(ctx.Bodies, cutter, ctx.Warnings);
        BooleanOp.Unite(ctx.Bodies, cyl, ctx.Warnings);
    }
}
