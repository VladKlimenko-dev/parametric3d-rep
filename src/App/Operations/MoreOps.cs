using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// u13: ОПЕРАЦИИ КНОПОК ТАБЛИЦЫ ИКОНОК — те, что уже были в модели и ядре без панели: протяжка, переход по сечениям, уклон,
// ребро жёсткости, опорные ось и точка.

internal static class MoreFields
{
    public static RefField Sketches(string label, bool optional = false) => new()
    {
        Label = label, Filter = PickFilter.SketchCurves, Accept = Refs.IsSketch, Optional = optional,
        Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?"),
    };
    public static RefField Faces(string label, bool optional = false) => new() { Label = label, Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face, Optional = optional };
    public static RefField Points(string label, bool optional = true) => new()
    {
        Label = label, Filter = PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums,
        Accept = r => r.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint, Optional = optional,
    };
    public static RefField AnyEdge(string label, bool optional = true) => new()
    {
        Label = label, Filter = PickFilter.Edges | PickFilter.SketchCurves, Accept = r => r.Kind is PickKind.Edge or PickKind.SketchCurve, Single = true, Optional = optional,
    };

    /// <summary>Кривая ссылки: ребро тела или кривая эскиза (в мировых координатах).</summary>
    public static ICurve3? CurveOf(PickRef r)
    {
        if (r.Kind == PickKind.Edge && r.Owner is Part p && p.FindEdge(r.Id) is { Curve: { } c }) return c;
        if (r.Target is SkEntity se && r.Owner is Sketch sk && se is not SkPoint)
            try { return se.ToCurve3(sk.Plane); } catch { return null; }
        return null;
    }

    /// <summary>Грань-тело ссылки.</summary>
    public static TFace? FaceOf(PickRef r) => r.Kind == PickKind.Face && r.Owner is Part p ? p.FindFace(r.Id) : null;
}

// ====================================================================== протяжка

public sealed class SweepOp : FeatureOp<SweepFeature>
{
    private RefField _sec = null!, _path = null!;
    public SweepOp(SweepFeature? existing = null) : base(existing) { }
    public override string Title => "Протяжка";
    public override string Icon => "f_sweep_but.svg";
    protected override SweepFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _sec = p.Ref(new RefField { Label = "Сечение", Filter = PickFilter.SketchCurves, Accept = Refs.IsSketch, Single = true, Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?") });
        _path = p.Ref(new RefField { Label = "Траектория", Filter = PickFilter.SketchCurves | PickFilter.Datums, Accept = r => Refs.IsSketch(r) || r.Target is DatumCurve or Line3DItem, Single = true,
            Describe = r => r.Target is DatumCurve or Line3DItem ? L("Кривая «{0}»", ((ModelItem)r.Target!).Name) : L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?") });
        if (Editing) { _sec.Kept = F.SectionSketchId != 0 ? 1 : 0; _path.Kept = F.PathSketchId != 0 || F.PathLineId != 0 ? 1 : 0; }
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Number("Закрутка", () => F.Twist, v => F.Twist = v, "°");
        p.Number("Доля траектории", () => F.LengthFraction, v => F.LengthFraction = v, "");
        p.Check("Сечение без поворота", () => F.KeepOrientation, v => F.KeepOrientation = v);
        p.Note("Сечение — замкнутый эскиз; траектория — эскиз с незамкнутой цепочкой.");
    }

    protected override void SeedRefs()
    {
        // выбранные эскизы: первый — сечение, второй — траектория
        var sk = W.ViewPort.Selection.Items.Where(Refs.IsSketch).GroupBy(r => Refs.SketchOf(r)!.Id).Select(g => g.First()).ToList();
        if (sk.Count > 0) _sec.Take(new[] { sk[0] });
        if (sk.Count > 1) _path.Take(new[] { sk[1] });
        else if (W.ViewPort.Selection.Items.FirstOrDefault(r => r.Target is DatumCurve or Line3DItem) is { IsEmpty: false } dc) _path.Take(new[] { dc });
    }

    protected override void Push()
    {
        if (_sec.Items.Count > 0 && Refs.SketchOf(_sec.Items[0]) is { } s) F.SectionSketchId = s.Id;
        if (_path.Items.Count > 0 && _path.Items[0].Target is DatumCurve or Line3DItem) { F.PathLineId = ((ModelItem)_path.Items[0].Target!).Id; F.PathSketchId = 0; }
        else if (_path.Items.Count > 0 && Refs.SketchOf(_path.Items[0]) is { } t) { F.PathSketchId = t.Id; F.PathLineId = 0; }
    }
}

// ====================================================================== переход по сечениям

public sealed class LoftOp : FeatureOp<LoftFeature>
{
    private RefField _secs = null!, _guides = null!;
    public LoftOp(LoftFeature? existing = null) : base(existing) { }
    public override string Title => "Переход";
    public override string Icon => "f_blend_but.svg";
    protected override LoftFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _secs = p.Ref(MoreFields.Sketches("Сечения"));
        _guides = p.Ref(MoreFields.Sketches("Направляющие", optional: true));
        if (Editing) { _secs.Kept = F.SectionSketchIds.Count; _guides.Kept = F.GuideSketchIds.Count; }
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Check("Гладкий переход", () => F.Smooth, v => F.Smooth = v);
        p.Note("Сечения — по порядку выбора (не меньше двух).");
    }

    protected override void SeedRefs() => Seed(_secs);

    protected override void Push()
    {
        if (_secs.Touched)
        {
            F.SectionSketchIds.Clear();
            foreach (var r in _secs.Items) if (Refs.SketchOf(r) is { } s && !F.SectionSketchIds.Contains(s.Id)) F.SectionSketchIds.Add(s.Id);
        }
        if (_guides.Touched)
        {
            F.GuideSketchIds.Clear();
            foreach (var r in _guides.Items) if (Refs.SketchOf(r) is { } s && !F.GuideSketchIds.Contains(s.Id)) F.GuideSketchIds.Add(s.Id);
        }
    }

    protected override bool MissingRequiredExtra => F.SectionSketchIds.Count < 2;
}

// ====================================================================== уклон

public sealed class DraftOp : FeatureOp<DraftFeature>
{
    private RefField _faces = null!, _neutral = null!;
    private readonly RefCache<Vector3d> _cache = new();
    private bool _flip;
    public DraftOp(DraftFeature? existing = null) : base(existing) { }
    public override string Title => "Уклон";
    public override string Icon => "f_draft_but.svg";
    protected override DraftFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _faces = p.Ref(MoreFields.Faces("Грани"));
        _neutral = p.Ref(Fields.Plane("Нейтральная плоскость"));
        if (Editing) { _faces.Kept = F.OnFaces.Count; _neutral.Kept = 1; }
        p.Number("Угол", () => F.Angle, v => F.Angle = v, "°");
        p.Check("Обратное направление", () => _flip, v => _flip = v);
    }

    public override Action<double>? DragValue => v => F.Angle = new ValueRef(v);
    public override double? DragCurrent => F.Angle.Value;
    public override string DragLabel => "∠";

    protected override void SeedRefs() => Seed(_faces, _neutral);

    protected override void Push()
    {
        if (_faces.Touched)
        {
            F.OnFaces.Clear(); F.OnFaceNames.Clear();
            F.OnFaces.AddRange(_cache.Resolve(_faces, Refs.PointOf));
        }
        if (_neutral.Items.Count > 0 && Refs.PlaneOf(_neutral.Items[0]) is { } pl)
        {
            F.NeutralPoint = pl.Frame.Origin;
            F.NeutralNormal = pl.Frame.W.Normalized() * (_flip ? -1 : 1);
        }
    }
}

// ====================================================================== ребро жёсткости

public sealed class RibOp : FeatureOp<RibFeature>
{
    private RefField _sk = null!;
    private int _dir;    // 0 −V, 1 +V, 2 −U, 3 +U (оси плоскости эскиза)
    public RibOp(RibFeature? existing = null) : base(existing) { }
    public override string Title => "Ребро жёсткости";
    public override string Icon => "f_rib_but.svg";
    protected override RibFeature Create() => new();
    public static readonly string[] Dirs = { "−V эскиза", "+V эскиза", "−U эскиза", "+U эскиза" };

    public override void Build(OpPanel p)
    {
        _sk = p.Ref(Fields.Sketch());
        if (Editing && F.SketchId != 0) _sk.Kept = 1;
        p.Number("Толщина", () => F.Thickness, v => F.Thickness = v);
        p.Choice("Рост", Dirs, () => _dir, v => _dir = v);
        p.Note("Контур эскиза наращивается в плоскости эскиза до тела и утолщается на обе стороны.");
    }

    public override Action<double>? DragValue => v => F.Thickness = new ValueRef(Math.Abs(v));
    public override double? DragCurrent => F.Thickness.Value;
    public override string DragLabel => "T";

    protected override void SeedRefs() => Seed(_sk);

    protected override void Push()
    {
        if (_sk.Items.Count == 0 || Refs.SketchOf(_sk.Items[0]) is not { } sk) return;
        F.SketchId = sk.Id;
        var u = sk.Plane.U.Normalized(); var v = sk.Plane.V.Normalized();
        F.Grow = _dir switch { 0 => -v, 1 => v, 2 => -u, _ => u };
    }
}

// ====================================================================== опорная ось

public sealed class AxisOp : Operation
{
    private RefField _pts = null!, _pl = null!, _pl2 = null!, _face = null!, _edge = null!;
    private int _by;
    private DatumAxis? _ax;
    public int InitialMethod = -1;
    public override string Title => "Опорная ось";
    public override string Icon => "dat_axis_but.svg";
    public static readonly string[] Methods = { "Через две точки", "Нормально к плоскости через точку", "Пересечение двух плоскостей", "Ось грани вращения", "По прямому ребру", "Касательно к кривой в точке" };

    protected override bool Start() { W.Session.BeginEdit(L(Title)); if (InitialMethod >= 0) _by = InitialMethod; return true; }

    public override void Build(OpPanel p)
    {
        p.Choice("Способ", Methods, () => _by, v => _by = v);
        _pts = p.Ref(MoreFields.Points("Точки"));
        _pl = p.Ref(new RefField { Label = "Плоскость", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true });
        _pl2 = p.Ref(new RefField { Label = "Вторая плоскость", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true });
        _face = p.Ref(new RefField { Label = "Грань вращения", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face && !Refs.IsPlanar(r), Single = true, Optional = true });
        _edge = p.Ref(MoreFields.AnyEdge("Ребро или кривая"));
    }

    public override void Prime()
    {
        Seed(_pts, _pl, _face, _edge);
        if (InitialMethod < 0)
        {
            if (_face.Items.Count > 0) _by = 3;
            else if (_edge.Items.Count > 0) _by = _pts.Items.Count > 0 ? 5 : 4;
            else if (_pts.Items.Count >= 2) _by = 0;
            else if (_pl.Items.Count > 0 && _pts.Items.Count > 0) _by = 1;
        }
        foreach (var f in new[] { _pts, _pl, _pl2, _face, _edge }) if (f.Text != null) f.Text.Text = f.Summary;
        Panel?.Refresh(); Apply(); RefreshStatus();
    }

    private (Vector3d At, Vector3d Dir)? Compute()
    {
        var pts = _pts.Items.Select(Refs.PointOf).OfType<Vector3d>().ToList();
        var pl = _pl.Items.Count > 0 ? Refs.PlaneOf(_pl.Items[0]) : null;
        var pl2 = _pl2.Items.Count > 0 ? Refs.PlaneOf(_pl2.Items[0]) : null;
        try
        {
            switch (_by)
            {
                case 0 when pts.Count >= 2: return DatumBuilder.AxisThroughPoints(pts[0], pts[1]);
                case 1 when pl is { } b: return DatumBuilder.AxisNormalToPlane(b.Frame, pts.Count > 0 ? pts[0] : b.Frame.Origin);
                case 2 when pl is { } a && pl2 is { } b2: return DatumBuilder.AxisOfPlanes(a.Frame, b2.Frame);
                case 3 when _face.Items.Count > 0 && MoreFields.FaceOf(_face.Items[0]) is { } f: return DatumBuilder.AxisOfFace(f);
                case 4 when _edge.Items.Count > 0 && Refs.LineOf(_edge.Items[0]) is { } l: return l;
                case 5 when _edge.Items.Count > 0 && MoreFields.CurveOf(_edge.Items[0]) is { } c:
                {
                    var at = pts.Count > 0 ? pts[0] : c.PointAt(0.5 * (c.T0 + c.T1));
                    return DatumBuilder.AxisTangentToCurve(c, ClosestT(c, at));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { W.ShowToast(L(Title) + ": " + ex.Message); }
        return null;
    }

    internal static double ClosestT(ICurve3 c, Vector3d p)
    {
        double best = c.T0, bd = double.MaxValue;
        for (int i = 0; i <= 200; i++) { double t = c.T0 + (c.T1 - c.T0) * i / 200.0; double d = (c.PointAt(t) - p).LengthSquared; if (d < bd) { bd = d; best = t; } }
        double h = (c.T1 - c.T0) / 200.0;
        for (int it = 0; it < 40; it++) { foreach (var s in new[] { -h, h }) { double t = Math.Clamp(best + s, Math.Min(c.T0, c.T1), Math.Max(c.T0, c.T1)); double d = (c.PointAt(t) - p).LengthSquared; if (d < bd) { bd = d; best = t; } } h *= 0.5; }
        return best;
    }

    protected override void Apply()
    {
        if (Compute() is not { } a) return;
        if (_ax == null) { _ax = new DatumAxis(L("Ось{0}", Part.Children.OfType<DatumAxis>().Count() + 1), a.At, a.Dir.Normalized()); Part.Add(_ax); }
        else { _ax.Origin = a.At; _ax.Direction = a.Dir.Normalized(); _ax.NotifyChanged(_ax); }
        W.ViewPort.MarkDirty();
    }

    public override bool Commit()
    {
        if (_ax == null) { W.ShowToast(L("Выберите ссылки для способа «{0}»", L(Methods[_by]))); return false; }
        W.Session.CommitEdit(); return true;
    }

    public override void Cancel() { if (_ax != null) Part.Remove(_ax); W.Session.DiscardEdit(); }
}

// ====================================================================== опорная точка

public sealed class PointOp : Operation
{
    private RefField _pts = null!, _edge = null!, _pl = null!, _line = null!;
    private int _by;
    private DatumPoint? _pt;
    private ValueRef _ratio = new(0.5);
    public int InitialMethod = -1;
    public override string Title => "Опорная точка";
    public override string Icon => "dat_point_but.svg";
    public static readonly string[] Methods = { "В вершине (середина точек)", "В центре дуги", "На кривой (доля длины)", "Пересечение прямой и плоскости", "Пересечение трёх плоскостей" };

    protected override bool Start() { W.Session.BeginEdit(L(Title)); if (InitialMethod >= 0) _by = InitialMethod; return true; }

    public override void Build(OpPanel p)
    {
        p.Choice("Способ", Methods, () => _by, v => _by = v);
        _pts = p.Ref(MoreFields.Points("Точки"));
        _edge = p.Ref(MoreFields.AnyEdge("Ребро или кривая"));
        _line = p.Ref(Fields.Line("Прямая", optional: true));
        _pl = p.Ref(new RefField { Label = "Плоскости", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Optional = true });
        p.Number("Доля длины", () => _ratio, v => _ratio = v, "", visible: () => _by == 2);
    }

    public override void Prime()
    {
        Seed(_pts, _edge, _line, _pl);
        if (InitialMethod < 0)
        {
            if (_pl.Items.Count >= 3) _by = 4;
            else if (_line.Items.Count > 0 && _pl.Items.Count > 0) _by = 3;
            else if (_edge.Items.Count > 0 && MoreFields.CurveOf(_edge.Items[0]) is ArcCurve3) _by = 1;
            else if (_edge.Items.Count > 0) _by = 2;
        }
        foreach (var f in new[] { _pts, _edge, _line, _pl }) if (f.Text != null) f.Text.Text = f.Summary;
        Panel?.Refresh(); Apply(); RefreshStatus();
    }

    private Vector3d? Compute()
    {
        var pts = _pts.Items.Select(Refs.PointOf).OfType<Vector3d>().ToList();
        var pls = _pl.Items.Select(Refs.PlaneOf).Where(x => x != null).Select(x => x!.Value.Frame).ToList();
        try
        {
            switch (_by)
            {
                case 0 when pts.Count > 0: return pts.Aggregate(Vector3d.Zero, (a, b) => a + b) / pts.Count;
                case 1 when _edge.Items.Count > 0 && MoreFields.CurveOf(_edge.Items[0]) is { } c: return DatumBuilder.PointAtCentre(c);
                case 2 when _edge.Items.Count > 0 && MoreFields.CurveOf(_edge.Items[0]) is { } c2: return DatumBuilder.PointOnCurve(c2, _ratio.Value);
                case 3 when _line.Items.Count > 0 && Refs.LineOf(_line.Items[0]) is { } l && pls.Count > 0: return DatumBuilder.PointLineMeetsPlane(l.At, l.Dir, pls[0]);
                case 4 when pls.Count >= 3:
                {
                    var (at, dir) = DatumBuilder.AxisOfPlanes(pls[0], pls[1]);
                    return DatumBuilder.PointLineMeetsPlane(at, dir, pls[2]);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { W.ShowToast(L(Title) + ": " + ex.Message); }
        return null;
    }

    protected override void Apply()
    {
        if (Compute() is not { } q) return;
        if (_pt == null) { _pt = new DatumPoint(L("Точка{0}", Part.Children.OfType<DatumPoint>().Count() + 1), q); Part.Add(_pt); }
        else { _pt.P = q; _pt.NotifyChanged(_pt); }
        W.ViewPort.MarkDirty();
    }

    public override bool Commit()
    {
        if (_pt == null) { W.ShowToast(L("Выберите ссылки для способа «{0}»", L(Methods[_by]))); return false; }
        W.Session.CommitEdit(); return true;
    }

    public override void Cancel() { if (_pt != null) Part.Remove(_pt); W.Session.DiscardEdit(); }
}
