using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// u13f: поверхности (квилты), правка квилтов, утолщение/отверждение/замена грани, переходы, вставка тела.

internal static class QuiltFields
{
    public static bool IsQuiltFace(PickRef r) => r.Kind == PickKind.Face && r.Owner is Part p && p.QuiltOfFace(r.Id) != null;
    public static bool IsBodyFace(PickRef r) => r.Kind == PickKind.Face && r.Owner is Part p && p.QuiltOfFace(r.Id) == null;
    public static string? KeyOf(PickRef r) => r.Owner is Part p ? p.QuiltOfFace(r.Id)?.Key : null;
    public static RefField Quilts(string label, bool single) => new()
    {
        Label = label, Filter = PickFilter.Faces, Accept = IsQuiltFace, Single = single,
        Describe = r => L("Поверхность «{0}»", r.Owner is Part p ? p.QuiltOfFace(r.Id)?.Name ?? "?" : "?"),
    };
    public static RefField Sketch(string label, bool optional = false) => new()
    {
        Label = label, Filter = PickFilter.SketchCurves, Accept = Refs.IsSketch, Single = true, Optional = optional,
        Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?"),
    };
}

public sealed class SurfaceOp : FeatureOp<SurfaceFeature>
{
    public SurfaceKind StartMode;
    private RefField _sk = null!, _faces = null!, _axis = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public SurfaceOp(SurfaceFeature? existing = null) : base(existing) { if (existing != null) StartMode = existing.Mode; }
    public override string Title => StartMode switch { SurfaceKind.Revolve => "Поверхность вращением", SurfaceKind.Fill => "Заливка (плоская поверхность)", SurfaceKind.Copy => "Копия поверхности", _ => "Поверхность выдавливанием" };
    public override string Icon => StartMode switch { SurfaceKind.Fill => "f_fill_but.svg", SurfaceKind.Copy => "surf_copy_but.svg", _ => "f_surface_extrude_but.svg" };
    protected override SurfaceFeature Create() => new(L(Title)) { Mode = StartMode };

    public override void Build(OpPanel p)
    {
        if (StartMode == SurfaceKind.Copy)
        {
            _faces = p.Ref(new RefField { Label = "Грани тела", Filter = PickFilter.Faces, Accept = QuiltFields.IsBodyFace });
            if (Editing) _faces.Kept = F.OnFaces.Count;
            _sk = _axis = new RefField { Label = "", Optional = true };
            return;
        }
        _sk = p.Ref(QuiltFields.Sketch(StartMode == SurfaceKind.Fill ? "Замкнутый эскиз" : "Эскиз (цепочка кривых)"));
        if (Editing && F.SketchId != 0) _sk.Kept = 1;
        _faces = new RefField { Label = "", Optional = true };
        if (StartMode == SurfaceKind.Revolve)
        {
            _axis = p.Ref(new RefField { Label = "Ось (отрезок эскиза)", Filter = PickFilter.SketchCurves, Accept = r => r.Target is SkLine, Single = true, Optional = true });
            if (Editing && F.AxisEntityId != 0) _axis.Kept = 1;
            p.Number("Угол", () => F.Angle, v => F.Angle = v, "°");
            p.Note("Без оси — осевая линия эскиза.");
        }
        else _axis = new RefField { Label = "", Optional = true };
        if (StartMode == SurfaceKind.Extrude) p.Number("Глубина", () => F.Depth, v => F.Depth = v);
        p.Check(StartMode == SurfaceKind.Fill ? "Основа с другой стороны" : "Обратное направление", () => F.Flip, v => F.Flip = v);
    }

    public override Action<double>? DragValue => StartMode == SurfaceKind.Extrude ? v => F.Depth = new ValueRef(v) : null;
    public override double? DragCurrent => F.Depth.Value;
    public override string DragLabel => "H";

    protected override void SeedRefs() => Seed(_sk, _faces, _axis);

    protected override void Push()
    {
        if (StartMode == SurfaceKind.Copy)
        {
            if (_faces.Touched) { F.OnFaces.Clear(); F.OnFaceNames.Clear(); F.OnFaces.AddRange(_cache.Resolve(_faces, Refs.PointOf)); }
            return;
        }
        if (_sk.Items.Count > 0 && Refs.SketchOf(_sk.Items[0]) is { } sk) F.SketchId = sk.Id;
        if (_axis.Touched) F.AxisEntityId = _axis.Items.Count > 0 && _axis.Items[0].Target is SkLine ln ? ln.Id : 0;
    }
}

public sealed class QuiltEditOp : FeatureOp<QuiltEditFeature>
{
    public QuiltEditKind StartMode;
    private RefField _src = null!, _pl = null!;
    public QuiltEditOp(QuiltEditFeature? existing = null) : base(existing) { if (existing != null) StartMode = existing.Mode; }
    public override string Title => StartMode switch { QuiltEditKind.Merge => "Объединение квилтов", QuiltEditKind.Trim => "Обрезка квилта", QuiltEditKind.Extend => "Продление квилта", _ => "Смещение поверхности" };
    public override string Icon => StartMode switch { QuiltEditKind.Merge => "surf_merge_but.svg", QuiltEditKind.Trim => "surf_trim_but.svg", QuiltEditKind.Extend => "surf_extend_but.svg", _ => "surf_offset_but.svg" };
    protected override QuiltEditFeature Create() => new(L(Title)) { Mode = StartMode };

    public override void Build(OpPanel p)
    {
        _src = p.Ref(QuiltFields.Quilts(StartMode == QuiltEditKind.Merge ? "Поверхности (две и больше)" : "Поверхность", single: StartMode != QuiltEditKind.Merge));
        if (Editing) _src.Kept = F.Sources.Count;
        if (StartMode == QuiltEditKind.Trim)
        {
            _pl = p.Ref(Fields.Plane("Плоскость обрезки"));
            if (Editing) _pl.Kept = 1;
            p.Check("Оставить другую сторону", () => F.Flip, v => F.Flip = v);
        }
        else _pl = new RefField { Label = "", Optional = true };
        if (StartMode is QuiltEditKind.Offset or QuiltEditKind.Extend) p.Number(StartMode == QuiltEditKind.Extend ? "Продление" : "Смещение", () => F.Distance, v => F.Distance = v);
    }

    public override Action<double>? DragValue => StartMode is QuiltEditKind.Offset or QuiltEditKind.Extend ? v => F.Distance = new ValueRef(v) : null;
    public override double? DragCurrent => F.Distance.Value;
    public override string DragLabel => "d";
    protected override bool MissingRequiredExtra => StartMode == QuiltEditKind.Merge && _src.Items.Select(QuiltFields.KeyOf).Distinct().Count() < 2 && F.Sources.Count < 2;

    protected override void SeedRefs() => Seed(_src, _pl);

    protected override void Push()
    {
        if (_src.Touched) { F.Sources.Clear(); foreach (var k in _src.Items.Select(QuiltFields.KeyOf).OfType<string>().Distinct()) F.Sources.Add(k); }
        if (_pl.Items.Count > 0 && Refs.PlaneOf(_pl.Items[0]) is { } pl) { F.PlaneAt = pl.Frame.Origin; F.PlaneNormal = pl.Frame.W; }
    }
}

public sealed class QuiltSolidOp : FeatureOp<QuiltSolidFeature>
{
    public QuiltSolidKind StartMode;
    private RefField _src = null!, _face = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public QuiltSolidOp(QuiltSolidFeature? existing = null) : base(existing) { if (existing != null) StartMode = existing.Mode; }
    public override string Title => StartMode switch { QuiltSolidKind.Solidify => "Отверждение квилта", QuiltSolidKind.ReplaceFace => "Замена грани квилтом", _ => "Утолщение квилта" };
    public override string Icon => StartMode switch { QuiltSolidKind.Solidify => "surf_solidify_but.svg", QuiltSolidKind.ReplaceFace => "dir_replace_face_but.svg", _ => "f_thicken_but.svg" };
    protected override QuiltSolidFeature Create() => new(L(Title)) { Mode = StartMode };

    public override void Build(OpPanel p)
    {
        if (StartMode == QuiltSolidKind.ReplaceFace)
        {
            _face = p.Ref(new RefField { Label = "Заменяемая грань", Filter = PickFilter.Faces, Accept = r => QuiltFields.IsBodyFace(r) && Refs.IsPlanar(r), Single = true });
            if (Editing) _face.Kept = F.OnFaces.Count;
        }
        else _face = new RefField { Label = "", Optional = true };
        _src = p.Ref(QuiltFields.Quilts("Поверхность", single: true));
        if (Editing && F.Source.Length > 0) _src.Kept = 1;
        if (StartMode == QuiltSolidKind.Thicken) p.Number("Толщина", () => F.Thickness, v => F.Thickness = v);
        if (StartMode != QuiltSolidKind.ReplaceFace) p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Check(StartMode == QuiltSolidKind.Thicken ? "Другая сторона" : StartMode == QuiltSolidKind.ReplaceFace ? "Другая сторона поверхности" : "—", () => F.Flip, v => F.Flip = v, visible: () => StartMode != QuiltSolidKind.Solidify);
    }

    public override Action<double>? DragValue => StartMode == QuiltSolidKind.Thicken ? v => F.Thickness = new ValueRef(v) : null;
    public override double? DragCurrent => F.Thickness.Value;
    public override string DragLabel => "t";

    protected override void SeedRefs() => Seed(_face, _src);

    protected override void Push()
    {
        if (_src.Items.Count > 0 && QuiltFields.KeyOf(_src.Items[0]) is { } k) F.Source = k;
        if (_face.Touched) { F.OnFaces.Clear(); F.OnFaceNames.Clear(); F.OnFaces.AddRange(_cache.Resolve(_face, Refs.PointOf)); }
    }
}

public sealed class BlendOp : FeatureOp<BlendFeature>
{
    public BlendKind StartMode;
    private RefField _secs = null!, _path = null!, _axis = null!;
    public BlendOp(BlendFeature? existing = null) : base(existing) { if (existing != null) StartMode = existing.Mode; }
    public override string Title => StartMode switch { BlendKind.Swept => "Протяжка с переходом", BlendKind.Rotational => "Переход вращением", _ => "Граничный переход" };
    public override string Icon => StartMode switch { BlendKind.Swept => "f_swept_blend_but.svg", BlendKind.Rotational => "f_rotational_blend_but.svg", _ => "f_boundary_blend_but.svg" };
    protected override BlendFeature Create() => new(L(Title)) { Mode = StartMode, AsSurface = StartMode == BlendKind.Boundary };

    public override void Build(OpPanel p)
    {
        _secs = p.Ref(new RefField { Label = "Сечения по порядку", Filter = PickFilter.SketchCurves, Accept = Refs.IsSketch, Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?") });
        if (Editing) _secs.Kept = F.SectionSketchIds.Count;
        _path = StartMode == BlendKind.Swept ? p.Ref(QuiltFields.Sketch("Траектория")) : new RefField { Label = "", Optional = true };
        if (Editing && F.PathSketchId != 0) _path.Kept = 1;
        _axis = StartMode == BlendKind.Rotational ? p.Ref(new RefField { Label = "Ось (отрезок эскиза)", Filter = PickFilter.SketchCurves, Accept = r => r.Target is SkLine, Single = true, Optional = true }) : new RefField { Label = "", Optional = true };
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Check("Поверхность (без торцов)", () => F.AsSurface, v => F.AsSurface = v);
        p.Note(StartMode switch
        {
            BlendKind.Swept => "Сечения — у точек траектории; форма между ними смешивается вдоль траектории.",
            BlendKind.Rotational => "Сечения — в плоскостях через ось (осевая линия в эскизе сечения); промежуточные — поворотом.",
            _ => "Переход через замкнутые сечения; три и больше — гладко.",
        });
    }

    protected override bool MissingRequiredExtra => Refs2() < 2 && F.SectionSketchIds.Count < 2;
    int Refs2() => _secs.Items.Select(Refs.SketchOf).OfType<Sketch>().Select(s => s.Id).Distinct().Count();

    protected override void SeedRefs()
    {
        var sk = W.ViewPort.Selection.Items.Where(Refs.IsSketch).GroupBy(r => Refs.SketchOf(r)!.Id).Select(g => g.First()).ToList();
        if (StartMode == BlendKind.Swept && sk.Count > 2) { _secs.Take(sk.Take(sk.Count - 1).ToArray()); _path.Take(new[] { sk[^1] }); }
        else if (sk.Count > 0) _secs.Take(sk.ToArray());
    }

    protected override void Push()
    {
        if (_secs.Touched) { F.SectionSketchIds.Clear(); foreach (var id in _secs.Items.Select(Refs.SketchOf).OfType<Sketch>().Select(s => s.Id).Distinct()) F.SectionSketchIds.Add(id); }
        if (_path.Items.Count > 0 && Refs.SketchOf(_path.Items[0]) is { } ps) F.PathSketchId = ps.Id;
        if (_axis.Touched) F.AxisEntityId = _axis.Items.Count > 0 && _axis.Items[0].Target is SkLine ln ? ln.Id : 0;
    }
}

public sealed class InsertBodyOp : FeatureOp<InsertBodyFeature>
{
    public string Step = "", Source = "";
    public InsertBodyOp(InsertBodyFeature? existing = null) : base(existing) { }
    public override string Title => "Вставить тело (FreeForm, STEP)";
    public override string Icon => "f_insert_freeform_but.svg";
    protected override InsertBodyFeature Create() => new(L("Вставка «{0}»", Source)) { Step = Step, SourceName = Source };
    private ValueRef _x = new(0), _y = new(0), _z = new(0);

    public override void Build(OpPanel p)
    {
        if (Editing) { _x = new ValueRef(F.Offset.X); _y = new ValueRef(F.Offset.Y); _z = new ValueRef(F.Offset.Z); }
        p.Note(L("Тело из «{0}» (B-rep хранится в документе).", F.SourceName));
        p.Number("Сдвиг X", () => _x, v => _x = v);
        p.Number("Сдвиг Y", () => _y, v => _y = v);
        p.Number("Сдвиг Z", () => _z, v => _z = v);
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
    }

    protected override void SeedRefs() { }
    protected override void Push() { F.Offset = new Vector3d(_x.Value, _y.Value, _z.Value); }
}
