using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Features;

// u13f: ПОВЕРХНОСТИ (квилты) и переходы — кнопки таблицы иконок. Квилт = тело-основа + выбранные его грани (Model/Quilt.cs).

internal static class QuiltUtil
{
    /// <summary>Точка внутри грани и наружная нормаль в ней.</summary>
    public static (Vector3d P, Vector3d N) Probe(TFace f)
    {
        var p = FaceRefs.Inside(f);
        var src = GeometryAudit.AuthoritativeSurface(f);
        if (src != null) p = src.PointAt(src.ProjectToUV(p));
        return (p, f.NormalAt(p).Normalized());
    }

    /// <summary>Грани тела, лежащие на поверхностях граней old (по точке внутри новой грани).</summary>
    public static HashSet<TFace> OnSurfacesOf(TSolid solid, IEnumerable<TFace> old, double tol = 1e-6)
    {
        var surfs = old.Select(GeometryAudit.AuthoritativeSurface).Where(s => s != null).ToList();
        var res = new HashSet<TFace>();
        foreach (var f in solid.Faces)
        {
            Vector3d p;
            try { p = FaceRefs.Inside(f); } catch { continue; }
            foreach (var s in surfs)
                if ((s!.PointAt(s.ProjectToUV(p)) - p).Length <= tol * Math.Max(1, p.Length)) { res.Add(f); break; }
        }
        return res;
    }

    /// <summary>Грани тела, на которых лежат точки (каждой точке — ближайшая грань в пределах допуска).</summary>
    public static HashSet<TFace> At(TSolid solid, IEnumerable<Vector3d> pts, double tol = 1e-5)
    {
        var res = new HashSet<TFace>();
        foreach (var p in pts)
        {
            var best = solid.Faces.Select(f => (F: f, D: FaceRefs.Distance(f, p))).OrderBy(x => x.D).FirstOrDefault();
            if (best.F != null && best.D <= tol * Math.Max(1, p.Length)) res.Add(best.F);
        }
        return res;
    }

    public static double Size(TSolid s)
    {
        var b = Bounds3.Empty; foreach (var v in s.Vertices) b.Add(v.P);
        return b.IsEmpty ? 100 : Math.Max(1, (b.Max - b.Min).Length);
    }

    /// <summary>Большой кирпич по одну сторону плоскости (полупространство в пределах size).</summary>
    public static TSolid HalfSpace(Vector3d at, Vector3d n, double size)
    {
        n = n.Normalized();
        var fr = Frame3.FromNormal(at, n);
        double h = size;
        var rect = new List<ICurve3>
        {
            new LineCurve3(fr.ToWorld(-h, -h), fr.ToWorld(h, -h)), new LineCurve3(fr.ToWorld(h, -h), fr.ToWorld(h, h)),
            new LineCurve3(fr.ToWorld(h, h), fr.ToWorld(-h, h)), new LineCurve3(fr.ToWorld(-h, h), fr.ToWorld(-h, -h)),
        };
        return ExtrudeOp.Build(ProfileBuilder.Build(fr, rect), n * h);
    }

    public static Quilt Require(BuildContext ctx, string key, string what)
        => ctx.QuiltByKey(key) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{what}: the source surface is not found (build it before this operation).");

    /// <summary>Объединённый квилт — список простых.</summary>
    public static IEnumerable<Quilt> Simple(Quilt q) => q.Parts.Count > 0 ? q.Parts.SelectMany(Simple) : new[] { q };
}

public enum SurfaceKind { Extrude = 0, Revolve = 1, Fill = 2, Copy = 3 }

/// <summary>
/// ПОВЕРХНОСТЬ ВЫДАВЛИВАНИЕМ / ВРАЩЕНИЕМ, ЗАЛИВКА, КОПИЯ ГРАНЕЙ. Выдавливание и вращение незамкнутой цепочки: цепочка
/// замыкается вспомогательными отрезками (к вершине в стороне или к оси), строится тело, квилт — грани от самой цепочки.
/// Заливка — плоская грань замкнутого эскиза (основа — толстая плита). Копия — выбранные грани тела.
/// </summary>
public sealed class SurfaceFeature : Feature
{
    public SurfaceKind Mode;
    public uint SketchId;
    public ValueRef Depth = new(20);
    public ValueRef Angle = new(360);
    public uint AxisEntityId;
    public bool Flip;
    public readonly List<Vector3d> OnFaces = new();
    public readonly List<string?> OnFaceNames = new();
    public SurfaceFeature(string name = "Surface") { Name = name; }
    public override string Summary => Mode switch { SurfaceKind.Revolve => $"Surface revolve {Angle}°", SurfaceKind.Fill => "Fill", SurfaceKind.Copy => $"Surface copy ({OnFaces.Count})", _ => $"Surface extrude {Depth}" };

    public override void Execute(BuildContext ctx)
    {
        if (Mode == SurfaceKind.Copy)
        {
            if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Surface copy: no body.");
            var fs = FacePick.Resolve(ctx.Bodies[0], OnFaces, OnFaceNames, "Surface copy");
            ctx.Quilts.Add(new Quilt(Key, Name, ctx.Bodies[0], fs));
            return;
        }
        var sk = ctx.FindSketch(SketchId) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the sketch is not found.");
        sk.UpdateDimensionsFromParameters(ctx.Parameters!);
        sk.Solve(ctx.Solver);
        var plane = sk.Plane;
        var curves = sk.Entities.Where(e => e.Role == Sketching.SkRole.Object && e is not Sketching.SkPoint).Select(e => e.ToCurve3(plane)).ToList();
        if (curves.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the sketch has no curves.");
        if (Mode == SurfaceKind.Fill)
        {
            var prof = sk.ToSolidProfile(Name);
            if (prof.IsEmpty) throw new RebuildException(OperationOutcome.InvalidInput, "Fill: the sketch has no closed loop.");
            double size = Math.Max(10, prof.Loops.SelectMany(l => l.Sample(plane)).Select(p => p.Length).DefaultIfEmpty(10).Max() * 2);
            var slab = ExtrudeOp.Build(prof, plane.W * (Flip ? size : -size));
            var cap = slab.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is { IsPlanar: true } && Math.Abs(Vector3d.Dot(QuiltUtil.Probe(f).P - plane.Origin, plane.W)) < 1e-9).ToList();
            if (cap.Count == 0) throw new RebuildException(OperationOutcome.InternalFailure, "Fill: the face is not found.");
            ctx.Quilts.Add(new Quilt(Key, Name, slab, cap));
            return;
        }
        // цепочка: замкнутая — как есть; незамкнутая — замыкается вспомогательными отрезками
        var prof0 = ProfileBuilder.Build(plane, curves);
        var chainMids = curves.Select(c => c.PointAt(0.5 * (c.T0 + c.T1))).ToList();
        Profile profile;
        if (!prof0.IsEmpty && prof0.Loops.Sum(l => l.Curves.Count) == curves.Count) profile = prof0;
        else
        {
            var ends = Ends(curves);
            if (ends == null) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the curves must form one chain.");
            var (a, b) = ends.Value;
            var extra = new List<ICurve3>();
            if (Mode == SurfaceKind.Revolve)
            {
                var ax = AxisOf(sk) ?? throw new RebuildException(OperationOutcome.InvalidInput, "Surface revolve: no axis (draw a centerline).");
                var fa = Foot(a, ax); var fb = Foot(b, ax);
                if ((b - fb).Length > 1e-9) extra.Add(new LineCurve3(b, fb));
                if ((fb - fa).Length > 1e-9) extra.Add(new LineCurve3(fb, fa));
                if ((fa - a).Length > 1e-9) extra.Add(new LineCurve3(fa, a));
            }
            else
            {
                // вершина в стороне от хорды, противоположной цепочке
                var mid = (a + b) / 2; var chord = b - a; double L = Math.Max(chord.Length, curves.Sum(c => c.Length));
                var side = Vector3d.Cross(plane.W, chord.LengthSquared > 1e-18 ? chord.Normalized() : plane.U);
                var cen = chainMids.Aggregate(Vector3d.Zero, (s, p) => s + p) / chainMids.Count;
                if (Vector3d.Dot(cen - mid, side) > 0) side = -side;
                var apex = mid + side * L;
                extra.Add(new LineCurve3(b, apex)); extra.Add(new LineCurve3(apex, a));
            }
            profile = ProfileBuilder.Build(plane, curves.Concat(extra));
            if (profile.IsEmpty) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: the chain could not be closed.");
        }
        TSolid solid; Func<Vector3d, Vector3d> mapMid;
        if (Mode == SurfaceKind.Revolve)
        {
            var ax = AxisOf(sk) ?? throw new RebuildException(OperationOutcome.InvalidInput, "Surface revolve: no axis (draw a centerline).");
            double ang = Angle.Resolve(ctx.Parameters) * (Flip ? -1 : 1);
            solid = RevolveOp.Build(profile, ax, 0, ang * Math.PI / 180);
            var rot = TransformOp.Rotate(ax.Origin, ax.W.Normalized(), ang / 2);
            mapMid = rot.Point;
        }
        else
        {
            double d = Depth.Resolve(ctx.Parameters) * (Flip ? -1 : 1);
            solid = ExtrudeOp.Build(profile, plane.W * d);
            mapMid = p => p + plane.W * (d / 2);
        }
        var faces = QuiltUtil.At(solid, chainMids.Select(mapMid));
        if (faces.Count == 0) throw new RebuildException(OperationOutcome.InternalFailure, $"{Name}: the surface faces are not found.");
        var quilt = new Quilt(Key, Name, solid, faces);
        // точное утолщение: полоса между цепочкой и её эквидистантой — тем же выдавливанием/вращением
        if (Ordered(curves) is { } chain)
        {
            bool rev = Mode == SurfaceKind.Revolve; var axF = rev ? AxisOf(sk) : null;
            double dep = Depth.Resolve(ctx.Parameters) * (Flip ? -1 : 1), ang = Angle.Resolve(ctx.Parameters) * (Flip ? -1 : 1);
            var refPt = rev && axF is { } af ? (Vector3d?)af.Origin : null;
            quilt.Thicken = (t, other) =>
            {
                var band = Band(chain, plane, t, other, refPt, rev && axF is { } a2 ? a2 : null);
                if (band == null) return null;
                var pr = ProfileBuilder.Build(plane, band);
                if (pr.IsEmpty) return null;
                return rev && axF is { } a3 ? RevolveOp.Build(pr, a3, 0, ang * Math.PI / 180) : ExtrudeOp.Build(pr, plane.W * dep);
            };
        }
        ctx.Quilts.Add(quilt);
    }

    /// <summary>Цепочка по порядку и направлению (от свободного конца); null — не одна незамкнутая цепочка.</summary>
    static List<ICurve3>? Ordered(List<ICurve3> cs)
    {
        var left = cs.ToList();
        bool Touch(Vector3d a, Vector3d b) => (a - b).Length < 1e-6;
        ICurve3? first = null;
        foreach (var c in left)
        {
            var s0 = c.PointAt(c.T0); var s1 = c.PointAt(c.T1);
            if (!left.Any(o => o != c && (Touch(o.PointAt(o.T0), s0) || Touch(o.PointAt(o.T1), s0)))) { first = c; break; }
            if (!left.Any(o => o != c && (Touch(o.PointAt(o.T0), s1) || Touch(o.PointAt(o.T1), s1)))) { first = c.Reversed(); left.Remove(c); left.Insert(0, first); break; }
        }
        if (first == null) return null;
        var chain = new List<ICurve3> { first };
        left.RemoveAt(0);
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

    /// <summary>
    /// Полоса толщиной t у цепочки (в плоскости): цепочка, конец → эквидистанта (обратно) → начало. Сторона — слева по ходу
    /// (other — справа); у вращения — к оси (other — от оси). Отрезки — сдвигом и стыковкой прямых, дуги — радиусом ± t.
    /// </summary>
    static List<ICurve3>? Band(List<ICurve3> chain, Frame3 plane, double t, bool other, Vector3d? towards, Frame3? axis)
    {
        Vector3d Left(ICurve3 c, double u) => Vector3d.Cross(plane.W, c.TangentAt(u).Normalized()).Normalized();
        double sgn = other ? -1 : 1;
        if (axis is { } ax)
        {
            // сторона к оси: знак по средней точке цепочки
            var m = chain[0].PointAt(0.5 * (chain[0].T0 + chain[0].T1));
            var foot = ax.Origin + ax.W * Vector3d.Dot(m - ax.Origin, ax.W);
            sgn = Vector3d.Dot(Left(chain[0], 0.5 * (chain[0].T0 + chain[0].T1)), foot - m) > 0 ? 1 : -1;
            if (other) sgn = -sgn;
        }
        var off = new List<ICurve3>();
        foreach (var c in chain)
        {
            switch (c)
            {
                case LineCurve3 l: { var n = Left(c, 0) * (sgn * t); off.Add(new LineCurve3(l.A + n, l.B + n)); break; }
                case ArcCurve3 a:
                {
                    double um = 0.5 * (a.T0 + a.T1); var pm = a.PointAt(um); var n = Left(c, um) * sgn;
                    double r = a.Radius + (Vector3d.Dot(n, pm - a.Frame.Origin) > 0 ? t : -t);
                    if (!(r > Tol.Weld)) return null;
                    off.Add(new ArcCurve3(a.Frame, r, a.A0, a.A1));
                    if (!((off[^1].PointAt(off[^1].T0) - (a.PointAt(a.T0) + Left(c, a.T0) * (sgn * t))).Length < 1e-6)) off[^1] = off[^1].Reversed();
                    break;
                }
                default: return null;
            }
        }
        // стыки соседних смещённых отрезков — пересечением прямых
        for (int i = 0; i + 1 < off.Count; i++)
            if (off[i] is LineCurve3 l1 && off[i + 1] is LineCurve3 l2)
            {
                var d1 = l1.B - l1.A; var d2 = l2.B - l2.A; var cr = Vector3d.Cross(d1, d2);
                if (cr.LengthSquared < 1e-18) continue;
                double s = Vector3d.Dot(Vector3d.Cross(l2.A - l1.A, d2), cr) / cr.LengthSquared;
                var x = l1.A + d1 * s;
                off[i] = new LineCurve3(l1.A, x); off[i + 1] = new LineCurve3(x, l2.B);
            }
        var res = new List<ICurve3>(chain);
        var a0 = chain[0].PointAt(chain[0].T0); var a1 = chain[^1].PointAt(chain[^1].T1);
        var o0 = off[0].PointAt(off[0].T0); var o1 = off[^1].PointAt(off[^1].T1);
        res.Add(new LineCurve3(a1, o1));
        for (int i = off.Count - 1; i >= 0; i--) res.Add(off[i].Reversed());
        res.Add(new LineCurve3(o0, a0));
        return res;
    }

    static Vector3d Foot(Vector3d p, Frame3 ax) => ax.Origin + ax.W * Vector3d.Dot(p - ax.Origin, ax.W);

    Frame3? AxisOf(Sketching.Sketch sk) => AxisEntityId != 0 ? sk.AxisFromEntity(AxisEntityId) : sk.RevolutionAxis();

    /// <summary>Концы незамкнутой цепочки (по связности концов); null — не одна цепочка.</summary>
    static (Vector3d A, Vector3d B)? Ends(List<ICurve3> cs)
    {
        var pts = new List<Vector3d>();
        foreach (var c in cs) { pts.Add(c.PointAt(c.T0)); pts.Add(c.PointAt(c.T1)); }
        var free = new List<Vector3d>();
        for (int i = 0; i < pts.Count; i++)
        {
            int n = pts.Count(q => (q - pts[i]).Length < 1e-6);
            if (n == 1) free.Add(pts[i]);
        }
        return free.Count == 2 ? (free[0], free[1]) : null;
    }

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        if (SketchId != 0) yield return SketchId;
    }
}

public enum QuiltEditKind { Offset = 0, Merge = 1, Trim = 2, Extend = 3 }

/// <summary>Правка квилтов: смещение, объединение, обрезка плоскостью, продление сквозь граничные грани.</summary>
public sealed class QuiltEditFeature : Feature
{
    public QuiltEditKind Mode;
    public readonly List<string> Sources = new();
    public ValueRef Distance = new(5);
    public Vector3d PlaneAt;
    public Vector3d PlaneNormal = Vector3d.UnitZ;
    public bool Flip;
    public QuiltEditFeature(string name = "Surface edit") { Name = name; }
    public override string Summary => Mode switch { QuiltEditKind.Merge => $"Merge ({Sources.Count})", QuiltEditKind.Trim => "Trim", QuiltEditKind.Extend => $"Extend {Distance}", _ => $"Surface offset {Distance}" };

    public override void Execute(BuildContext ctx)
    {
        if (Sources.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: no surface selected.");
        var qs = Sources.Select(k => QuiltUtil.Require(ctx, k, Name)).ToList();
        if (Mode == QuiltEditKind.Merge)
        {
            var all = qs.SelectMany(QuiltUtil.Simple).ToList();
            var m = new Quilt(Key, Name, all[0].Parent, all[0].Faces);
            m.Parts.AddRange(all);
            foreach (var q in qs) ctx.Quilts.Remove(q);
            ctx.Quilts.Add(m);
            return;
        }
        var res = new List<Quilt>();
        foreach (var q in qs.SelectMany(QuiltUtil.Simple))
        {
            string? why = null; TSolid? s;
            HashSet<TFace> faces;
            switch (Mode)
            {
                case QuiltEditKind.Trim:
                {
                    var n = PlaneNormal.Normalized() * (Flip ? -1 : 1);
                    var list = new List<TSolid> { q.Parent };
                    BooleanOp.Subtract(list, QuiltUtil.HalfSpace(PlaneAt, n, 4 * QuiltUtil.Size(q.Parent)), ctx.Warnings);
                    if (list.Count == 0) throw new RebuildException(OperationOutcome.Empty, "Trim: nothing is left of the surface.");
                    s = list[0];
                    faces = QuiltUtil.OnSurfacesOf(s, q.Faces);
                    // грани среза (лежат в плоскости) — не квилт
                    faces.RemoveWhere(f => GeometryAudit.AuthoritativeSurface(f) is { IsPlanar: true } && Math.Abs(Vector3d.Dot(QuiltUtil.Probe(f).P - PlaneAt, PlaneNormal.Normalized())) < 1e-7 && !q.Faces.Any(o => Math.Abs(Vector3d.Dot(QuiltUtil.Probe(o).P - PlaneAt, PlaneNormal.Normalized())) < 1e-7));
                    break;
                }
                case QuiltEditKind.Extend:
                {
                    double h = Distance.Resolve(ctx.Parameters);
                    if (!(h > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Extend: the distance must be positive.");
                    // граничные (неквилтовые) грани основы отодвигаются наружу на h — грани квилта продлеваются до них
                    var others = q.Others.ToHashSet();
                    s = DirectEdit.OffsetFaces(q.Parent, f => others.Contains(f) ? h : 0, ref why);
                    if (s == null) s = DirectRound.ExtendThroughOpen(q.Parent, others, h, ref why);
                    if (s == null) throw new RebuildException(OperationOutcome.Unsupported, $"Extend: the surface could not be extended ({why}).");
                    faces = QuiltUtil.OnSurfacesOf(s, q.Faces);
                    break;
                }
                default:
                {
                    double d = Distance.Resolve(ctx.Parameters);
                    s = DirectEdit.OffsetFaces(q.Parent, f => q.Faces.Contains(f) ? d : 0, ref why);
                    if (s == null) throw new RebuildException(OperationOutcome.Unsupported, $"Surface offset: {why}.");
                    var probes = q.Faces.Select(QuiltUtil.Probe).Select(x => x.P + x.N * d).ToList();
                    faces = QuiltUtil.At(s, probes, 1e-4);
                    break;
                }
            }
            if (faces.Count == 0) throw new RebuildException(OperationOutcome.InternalFailure, $"{Name}: the surface faces are lost.");
            res.Add(new Quilt(Key, Name, s, faces));
        }
        foreach (var q in qs) ctx.Quilts.Remove(q);
        if (res.Count == 1) ctx.Quilts.Add(res[0]);
        else { var m = new Quilt(Key, Name, res[0].Parent, res[0].Faces); m.Parts.AddRange(res); ctx.Quilts.Add(m); }
    }
}

public enum QuiltSolidKind { Thicken = 0, Solidify = 1, ReplaceFace = 2 }

/// <summary>
/// УТОЛЩЕНИЕ, ОТВЕРЖДЕНИЕ квилта и ЗАМЕНА ГРАНИ квилтом. Утолщение — полость основы с открытыми неквилтовыми гранями
/// (стенка по квилту внутрь основы; «другая сторона» — наружу). Отверждение — основа как тело (добавить или вырезать).
/// Замена грани — столб над гранью тела (её контур вдоль нормали) заменяется частью основы квилта в этом столбе.
/// </summary>
public sealed class QuiltSolidFeature : Feature
{
    public QuiltSolidKind Mode;
    public string Source = "";
    public ValueRef Thickness = new(2);
    public bool Flip;
    public readonly List<Vector3d> OnFaces = new();
    public readonly List<string?> OnFaceNames = new();
    public QuiltSolidFeature(string name = "Thicken") { Name = name; }
    public override string Summary => Mode switch { QuiltSolidKind.Solidify => "Solidify" + (Cut ? " cut" : ""), QuiltSolidKind.ReplaceFace => "Replace face", _ => $"Thicken {Thickness}" };

    public override void Execute(BuildContext ctx)
    {
        var q = QuiltUtil.Require(ctx, Source, Name);
        var tools = new List<TSolid>();
        switch (Mode)
        {
            case QuiltSolidKind.Thicken:
            {
                double t = Thickness.Resolve(ctx.Parameters);
                if (!(t > Tol.Weld)) throw new RebuildException(OperationOutcome.InvalidInput, "Thicken: the thickness must be positive.");
                foreach (var part in QuiltUtil.Simple(q))
                {
                    if (part.Thicken?.Invoke(t, Flip) is { } exact) { tools.Add(exact); continue; }
                    var open = part.Others.Select(f => FaceRefs.Inside(f)).ToList();
                    var list = new List<TSolid> { part.Parent };
                    int w0 = ctx.Warnings.Count;
                    if (ShellOp.Hollow(list, Flip ? -t : t, open, ctx.Warnings)) { tools.Add(list[0]); continue; }
                    ctx.Warnings.RemoveRange(w0, ctx.Warnings.Count - w0);
                    // иначе: стенка = основа без основы, у которой грани квилта сдвинуты внутрь на t (наружу — наоборот)
                    string? why = null;
                    var moved = DirectEdit.OffsetFaces(part.Parent, f => part.Faces.Contains(f) ? (Flip ? t : -t) : 0, ref why)
                        ?? throw new RebuildException(OperationOutcome.Unsupported, $"Thicken: the wall could not be built ({why}).");
                    var wall = new List<TSolid> { Flip ? moved : part.Parent };
                    BooleanOp.Subtract(wall, Flip ? part.Parent : moved, ctx.Warnings);
                    if (wall.Count == 0) throw new RebuildException(OperationOutcome.Unsupported, "Thicken: the wall is empty.");
                    tools.AddRange(wall);
                }
                break;
            }
            case QuiltSolidKind.Solidify:
                tools.AddRange(QuiltUtil.Simple(q).Select(p => p.Parent));
                break;
            case QuiltSolidKind.ReplaceFace:
            {
                if (ctx.Bodies.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Replace face: no body.");
                var face = FacePick.Resolve(ctx.Bodies[0], OnFaces, OnFaceNames, "Replace face").FirstOrDefault()
                           ?? throw new RebuildException(OperationOutcome.InvalidInput, "Replace face: select the face to replace.");
                if (GeometryAudit.AuthoritativeSurface(face) is not { IsPlanar: true })
                    throw new RebuildException(OperationOutcome.InvalidInput, "Replace face: the replaced face must be planar.");
                var (fp, fn) = QuiltUtil.Probe(face);
                double size = 4 * Math.Max(QuiltUtil.Size(ctx.Bodies[0]), QuiltUtil.Size(q.Parent));
                // столб: контур грани вдоль нормали — от глубины под самой низкой точкой квилта до size вверх
                var outer = face.Loops[0].CoEdges.Select(c => c.Edge?.Curve).OfType<ICurve3>().ToList();
                var fr = Frame3.FromNormal(fp, fn);
                var qpts = QuiltUtil.Simple(q).SelectMany(p => p.Faces).SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)).Select(c => Vector3d.Dot(c.Start.P - fp, fn)).ToList();
                double low = Math.Min(0, qpts.Min()), high = Math.Max(0, qpts.Max());
                double gap = 0.05 * Math.Max(1, high - low);
                var prof = ProfileBuilder.Build(fr, outer);
                if (prof.IsEmpty) throw new RebuildException(OperationOutcome.InvalidInput, "Replace face: the face outline is not closed.");
                var shifted = new Profile { Plane = prof.Plane.Translated(fn * (low - gap)) };
                foreach (var l in prof.Loops) { var nl = new ProfileLoop { IsOuter = l.IsOuter, Area = l.Area }; foreach (var c in l.Curves) nl.Curves.Add(ExtrudeOp.Translate(c, fn * (low - gap))); shifted.Loops.Add(nl); }
                var column = ExtrudeOp.Build(shifted, fn * (high - low + 2 * gap));
                // новое содержимое столба: часть основы квилта (или дополнение — «другая сторона»)
                var fill = new List<TSolid> { column };
                foreach (var p in QuiltUtil.Simple(q))
                {
                    if (Flip) BooleanOp.Subtract(fill, p.Parent, ctx.Warnings);
                    else BooleanOp.Intersect(fill, p.Parent, ctx.Warnings);
                }
                BooleanOp.Subtract(ctx.Bodies, column, ctx.Warnings);
                foreach (var f in fill) BooleanOp.Unite(ctx.Bodies, f, ctx.Warnings);
                return;
            }
        }
        foreach (var t in tools)
        {
            if (Cut) BooleanOp.Subtract(ctx.Bodies, t, ctx.Warnings);
            else if (ctx.Bodies.Count == 0) ctx.Bodies.Add(t);
            else BooleanOp.Unite(ctx.Bodies, t, ctx.Warnings);
        }
        // квилт израсходован
        ctx.Quilts.Remove(q);
    }
}

public enum BlendKind { Boundary = 0, Swept = 1, Rotational = 2 }

/// <summary>
/// ПЕРЕХОДЫ: граничный (тело или поверхность по сечениям — гладкий лофт), с протяжкой (сечения ведутся вдоль траектории:
/// промежуточные сечения — перенос и смешение формы по рамкам траектории) и вращением (сечения в плоскостях через ось —
/// промежуточные поворотом). Поверхность — боковые грани без торцов.
/// </summary>
public sealed class BlendFeature : Feature
{
    public BlendKind Mode;
    public readonly List<uint> SectionSketchIds = new();
    public uint PathSketchId;
    public uint AxisEntityId;
    public bool AsSurface;
    public int Steps = 16, Points = 48;
    public BlendFeature(string name = "Blend") { Name = name; }
    public override string Summary => $"{Mode} blend ({SectionSketchIds.Count} sections)" + (AsSurface ? " surface" : "") + (Cut ? " cut" : "");

    public override void Execute(BuildContext ctx)
    {
        if (SectionSketchIds.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: at least two sections are needed.");
        var sks = SectionSketchIds.Select(id => ctx.FindSketch(id) ?? throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: a section sketch is not found.")).ToList();
        var profs = new List<Profile>();
        foreach (var sk in sks)
        {
            if (ctx.Parameters != null) sk.UpdateDimensionsFromParameters(ctx.Parameters);
            sk.Solve(ctx.Solver); sk.RequireSolved(Name);
            var p = sk.ToSolidProfile(Name);
            if (p.IsEmpty) throw new RebuildException(OperationOutcome.InvalidInput, $"{Name}: a section is not closed.");
            profs.Add(p);
        }
        TSolid solid = Mode switch
        {
            BlendKind.Boundary => LoftOp.Build(profs, 64, smooth: profs.Count > 2),
            _ => Morph(ctx, sks, profs),
        };
        if (AsSurface)
        {
            var caps = new HashSet<TFace>(solid.Faces.Where(f => profs.Any(pr => GeometryAudit.AuthoritativeSurface(f) is { IsPlanar: true } && Math.Abs(Vector3d.Dot(QuiltUtil.Probe(f).P - pr.Plane.Origin, pr.Plane.W)) < 1e-6 && Math.Abs(Math.Abs(Vector3d.Dot(QuiltUtil.Probe(f).N, pr.Plane.W)) - 1) < 1e-6)));
            ctx.Quilts.Add(new Quilt(Key, Name, solid, solid.Faces.Where(f => !caps.Contains(f))));
            return;
        }
        if (Cut) BooleanOp.Subtract(ctx.Bodies, solid, ctx.Warnings);
        else if (ctx.Bodies.Count == 0) ctx.Bodies.Add(solid);
        else BooleanOp.Unite(ctx.Bodies, solid, ctx.Warnings);
    }

    /// <summary>Внешний контур сечения: N точек по длине от точки с наибольшим U, против часовой.</summary>
    static List<Vector2d> Resample(Profile p, int n)
    {
        var loop = p.Loops.Where(l => l.IsOuter).OrderByDescending(l => Math.Abs(l.Area)).First();
        var pts = loop.Sample(p.Plane);
        double area = 0; for (int i = 0; i < pts.Count; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Count]; area += a.X * b.Y - a.Y * b.X; }
        if (area < 0) pts.Reverse();
        var c = pts.Aggregate(Vector2d.Zero, (s, q) => s + q) / pts.Count;
        int start = 0; double best = double.MinValue;
        for (int i = 0; i < pts.Count; i++) { double ang = Math.Atan2(pts[i].Y - c.Y, pts[i].X - c.X); double sc = -Math.Abs(ang); if (sc > best) { best = sc; start = i; } }
        pts = pts.Skip(start).Concat(pts.Take(start)).ToList();
        var acc = new List<double> { 0 };
        for (int i = 1; i <= pts.Count; i++) acc.Add(acc[^1] + (pts[i % pts.Count] - pts[i - 1]).Length);
        double L = acc[^1]; var res = new List<Vector2d>();
        int j = 0;
        for (int k = 0; k < n; k++)
        {
            double s = L * k / n;
            while (j < pts.Count - 1 && acc[j + 1] < s) j++;
            double seg = acc[j + 1] - acc[j]; double u = seg < 1e-15 ? 0 : (s - acc[j]) / seg;
            res.Add(pts[j] + (pts[(j + 1) % pts.Count] - pts[j]) * u);
        }
        return res;
    }

    TSolid Morph(BuildContext ctx, List<Sketching.Sketch> sks, List<Profile> profs)
    {
        int n = Math.Clamp(Points, 8, 256), steps = Math.Clamp(Steps, 2, 128);
        var shapes = profs.Select(p => Resample(p, n)).ToList();
        var frames = profs.Select(p => p.Plane).ToList();
        // центры сечений — в начале их рамок (форма — относительно центра)
        for (int i = 0; i < shapes.Count; i++)
        {
            var c = shapes[i].Aggregate(Vector2d.Zero, (s, q) => s + q) / n;
            shapes[i] = shapes[i].Select(q => q - c).ToList();
            frames[i] = new Frame3(frames[i].ToWorld(c), frames[i].U, frames[i].V);
        }
        var stations = new List<Frame3>(); var forms = new List<List<Vector2d>>();
        if (Mode == BlendKind.Rotational)
        {
            var ax = (AxisEntityId != 0 ? sks[0].AxisFromEntity(AxisEntityId) : sks.Select(s => s.RevolutionAxis()).FirstOrDefault(a => a != null))
                     ?? throw new RebuildException(OperationOutcome.InvalidInput, "Rotational blend: no axis (draw a centerline in a section).");
            var w = ax.W.Normalized();
            double Ang(Frame3 f) { var r = f.Origin - ax.Origin; r -= w * Vector3d.Dot(r, w); var r0 = frames[0].Origin - ax.Origin; r0 -= w * Vector3d.Dot(r0, w); return Math.Atan2(Vector3d.Dot(Vector3d.Cross(r0, r), w), Vector3d.Dot(r0, r)); }
            var angs = frames.Select(Ang).ToList();
            for (int i = 1; i < angs.Count; i++) while (angs[i] <= angs[i - 1] + 1e-9) angs[i] += 2 * Math.PI;
            for (int i = 0; i + 1 < frames.Count; i++)
                for (int k = (i == 0 ? 0 : 1); k <= steps; k++)
                {
                    double t = (double)k / steps;
                    var rot = TransformOp.Rotate(ax.Origin, w, (angs[i] + (angs[i + 1] - angs[i]) * t - angs[i]) * 180 / Math.PI);
                    var f0 = frames[i];
                    stations.Add(new Frame3(rot.Point(f0.Origin), rot.Direction(f0.U), rot.Direction(f0.V)));
                    forms.Add(Lerp(shapes[i], shapes[i + 1], t, frames[i], frames[i + 1], stations[^1]));
                }
        }
        else
        {
            var ps = ctx.FindSketch(PathSketchId) ?? throw new RebuildException(OperationOutcome.InvalidInput, "Swept blend: the path sketch is not found.");
            var pts = new List<Vector3d>();
            var pc = ps.Entities.Where(e => e.Role == Sketching.SkRole.Object && e is not Sketching.SkPoint).Select(e => e.ToCurve3(ps.Plane)).ToList();
            for (int i = 0; i < pc.Count; i++) pc[i].Tessellate(pts, i == 0);
            if (pts.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, "Swept blend: the path is empty.");
            var path = new PolylineCurve3(pts);
            // параметры станций сечений — ближайшие точки траектории
            double T(Vector3d x) { double best = 0, bd = double.MaxValue; for (int i = 0; i <= 400; i++) { double t = i / 400.0; double d = (path.PointAt(t) - x).LengthSquared; if (d < bd) { bd = d; best = t; } } return best; }
            var ts = frames.Select(f => T(f.Origin)).ToList();
            for (int i = 0; i + 1 < frames.Count; i++)
            {
                var f0 = frames[i];
                for (int k = (i == 0 ? 0 : 1); k <= steps; k++)
                {
                    double u = (double)k / steps, t = ts[i] + (ts[i + 1] - ts[i]) * u;
                    var tan0 = path.TangentAt(ts[i]); var tan = path.TangentAt(t);
                    // рамка: поворот рамки сечения вслед касательной траектории
                    var axis = Vector3d.Cross(tan0, tan); double s = axis.Length, c = Vector3d.Dot(tan0, tan);
                    Vector3d U = f0.U, V = f0.V;
                    if (s > 1e-12) { var rot = TransformOp.Rotate(Vector3d.Zero, axis / s, Math.Atan2(s, c) * 180 / Math.PI); U = rot.Direction(U); V = rot.Direction(V); }
                    var origin = path.PointAt(t) + (f0.Origin - path.PointAt(ts[i]));
                    if (k == steps) { stations.Add(frames[i + 1]); forms.Add(shapes[i + 1]); continue; }
                    stations.Add(new Frame3(origin, U, V));
                    forms.Add(Lerp(shapes[i], shapes[i + 1], u, frames[i], frames[i + 1], stations[^1]));
                }
            }
        }
        var sections = new List<Profile>();
        for (int i = 0; i < stations.Count; i++)
        {
            var fr = stations[i]; var f = forms[i];
            var cs = new List<ICurve3>();
            for (int k = 0; k < f.Count; k++) cs.Add(new LineCurve3(fr.ToWorld(f[k]), fr.ToWorld(f[(k + 1) % f.Count])));
            sections.Add(ProfileBuilder.Build(fr, cs));
        }
        return LoftOp.Build(sections, 64, smooth: false);
    }

    /// <summary>Форма между сечениями a и b (в 2D своих рамок): точки сечения b переводятся в рамку a по направлениям U/V.</summary>
    static List<Vector2d> Lerp(List<Vector2d> a, List<Vector2d> b, double t, Frame3 fa, Frame3 fb, Frame3 at)
    {
        var res = new List<Vector2d>(a.Count);
        for (int i = 0; i < a.Count; i++) res.Add(a[i] * (1 - t) + b[i] * t);
        return res;
    }

    public override IEnumerable<uint> ConsumedSketchIds()
    {
        foreach (var id in SectionSketchIds) if (id != 0) yield return id;
        if (PathSketchId != 0) yield return PathSketchId;
    }
}

/// <summary>
/// ВСТАВИТЬ ТЕЛО (FreeForm, STEP, деталь): замороженное B-rep, хранится в документе текстом STEP; при перестроении читается
/// один раз и объединяется с телом детали (пересекающиеся — в одно, как решено для встраивания FreeForm).
/// </summary>
public sealed class InsertBodyFeature : Feature
{
    public string Step = "";
    public string SourceName = "";
    public Vector3d Offset;
    private List<TSolid>? _cache;
    private string? _cacheText;
    public InsertBodyFeature(string name = "Insert body") { Name = name; }
    public override string Summary => $"Inserted body «{SourceName}»";

    public List<TSolid> Bodies(List<string> warnings)
    {
        if (_cache != null && ReferenceEquals(_cacheText, Step)) return _cache;
        var part = Exchange.Read(Step, out _, warnings) ?? throw new RebuildException(OperationOutcome.InvalidInput, "Insert body: the stored body cannot be read.");
        _cache = part.Bodies.ToList(); _cacheText = Step;
        return _cache;
    }

    public override void Execute(BuildContext ctx)
    {
        if (string.IsNullOrEmpty(Step)) throw new RebuildException(OperationOutcome.InvalidInput, "Insert body: no body.");
        foreach (var b0 in Bodies(ctx.Warnings))
        {
            var b = Offset.LengthSquared > 0 ? (TopologyTransform.Apply(b0, TransformOp.Move(Offset)) ?? TransformOp.Copy(b0, TransformOp.Move(Offset))) : b0;
            if (Cut) BooleanOp.Subtract(ctx.Bodies, b, ctx.Warnings);
            else if (ctx.Bodies.Count == 0) ctx.Bodies.Add(b);
            else BooleanOp.Unite(ctx.Bodies, b, ctx.Warnings);
        }
    }
}

/// <summary>Для проверок: площадь квилта (с частями).</summary>
public static class QuiltUtilT
{
    public static double Area(Quilt q) => QuiltUtil.Simple(q).Sum(p => p.Area);
}
