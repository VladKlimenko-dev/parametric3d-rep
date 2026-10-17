using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// u13h: операции FreeForm — заготовки, булевы между телами, разрез, перенос/поворот/масштаб, зеркало и симметрия,
// массивы, выравнивание грани. Тела выбираются щелчком по их граням; имена тел запоминаются при выборе (после
// предпросмотра номера граней другие).

internal static class FFFields
{
    public static RefField Bodies(string label, bool single = false) => new()
    {
        Label = label, Filter = PickFilter.Faces, Single = single,
        Accept = r => r.Kind == PickKind.Face && r.Owner is Part p && p.BodyNameOf(r.Id) != null,
        Describe = r => r.Owner is Part p && p.BodyNameOf(r.Id) is { } n ? L("Тело «{0}»", n) : "?",
    };

    public static RefField Curve(string label, bool optional = false) => new()
    {
        Label = label, Filter = PickFilter.Datums | PickFilter.SketchCurves, Single = true, Optional = optional,
        Accept = r => r.Target is DatumCurve, Describe = r => (r.Target as DatumCurve)?.Name ?? "?",
    };

    public static RefField Plane(string label) => new()
    {
        Label = label, Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true,
    };

    public static RefField Point(string label) => new()
    {
        Label = label, Filter = PickFilter.Vertices | PickFilter.Datums, Single = true, Optional = true,
        Accept = r => Refs.PointOf(r) != null && r.Kind != PickKind.Face && r.Kind != PickKind.Edge,
    };
}

/// <summary>Имена тел по номерам выбранных граней — снимаются один раз, при выборе.</summary>
internal sealed class BodyNameCache
{
    private readonly Dictionary<uint, string> _map = new();
    public List<string> Resolve(RefField f)
    {
        var res = new List<string>();
        foreach (var r in f.Items)
        {
            if (!_map.TryGetValue(r.Id, out var n))
            {
                if (r.Owner is not Part p || p.BodyNameOf(r.Id) is not { } bn) continue;
                _map[r.Id] = n = bn;
            }
            if (!res.Contains(n)) res.Add(n);
        }
        return res;
    }
}

/// <summary>Плоскость построения FreeForm детали (по умолчанию XY).</summary>
internal static class FFPlane
{
    public static Frame3 Of(Part p) => p.ConstructionPlane;
}

public sealed class FFPrimitiveOp : FeatureOp<FFPrimitiveFeature>
{
    public FFPrimitive Shape;
    private RefField _at = null!;
    public FFPrimitiveOp(FFPrimitiveFeature? existing = null) : base(existing) { if (existing != null) Shape = existing.Shape; }
    public override string Title => Shape switch { FFPrimitive.Cylinder => "Цилиндр", FFPrimitive.Cone => "Конус", FFPrimitive.Sphere => "Шар", FFPrimitive.Torus => "Тор", _ => "Брусок" };
    public override string Icon => Shape switch { FFPrimitive.Cylinder => "ff_cylinder_but.svg", FFPrimitive.Cone => "ff_cone_but.svg", FFPrimitive.Sphere => "ff_sphere_but.svg", FFPrimitive.Torus => "ff_torus_but.svg", _ => "ff_box_but.svg" };

    protected override FFPrimitiveFeature Create()
    {
        var fr = FFPlane.Of(Part);
        var f = new FFPrimitiveFeature { Shape = Shape, Origin = fr.Origin, U = fr.U, V = fr.V };
        switch (Shape)
        {
            case FFPrimitive.Cylinder: f.A = new(15); f.B = new(40); break;
            case FFPrimitive.Cone: f.A = new(20); f.B = new(8); f.C = new(30); break;
            case FFPrimitive.Sphere: f.A = new(20); break;
            case FFPrimitive.Torus: f.A = new(25); f.B = new(6); break;
        }
        return f;
    }

    public override void Build(OpPanel p)
    {
        switch (Shape)
        {
            case FFPrimitive.Box:
                p.Number("Ширина", () => F.A, v => F.A = v); p.Number("Глубина", () => F.B, v => F.B = v); p.Number("Высота", () => F.C, v => F.C = v); break;
            case FFPrimitive.Cylinder:
                p.Number("Радиус", () => F.A, v => F.A = v); p.Number("Высота", () => F.B, v => F.B = v); break;
            case FFPrimitive.Cone:
                p.Number("Радиус основания", () => F.A, v => F.A = v); p.Number("Радиус вершины", () => F.B, v => F.B = v); p.Number("Высота", () => F.C, v => F.C = v); break;
            case FFPrimitive.Sphere:
                p.Number("Радиус", () => F.A, v => F.A = v); break;
            default:
                p.Number("Радиус кольца", () => F.A, v => F.A = v); p.Number("Радиус трубы", () => F.B, v => F.B = v); break;
        }
        _at = p.Ref(FFFields.Point("Центр (точка; иначе — начало плоскости построения)"));
        if (Editing) _at.Kept = 1;
        p.Check("Вырезать из тел", () => F.Cut, v => F.Cut = v);
        p.Note("Заготовка ставится на плоскость построения FreeForm — новым телом.");
    }

    protected override void SeedRefs() => Seed(_at);
    public override Action<double>? DragValue => v => F.A = new ValueRef(v);
    public override double? DragCurrent => F.A.Value;
    public override string DragLabel => Shape is FFPrimitive.Box ? "W" : "R";

    protected override void Push()
    {
        if (_at.Touched)
        {
            var fr = FFPlane.Of(Part);
            F.Origin = _at.Items.Count > 0 && Refs.PointOf(_at.Items[0]) is { } p ? p : fr.Origin;
        }
    }
}

public sealed class FFBooleanOp : FeatureOp<FFBooleanFeature>
{
    public FFBool Kind;
    private RefField _target = null!, _tools = null!;
    private readonly BodyNameCache _names = new();
    public FFBooleanOp(FFBooleanFeature? existing = null) : base(existing) { if (existing != null) Kind = existing.Op; }
    public override string Title => Kind switch { FFBool.Subtract => "Вычесть тела", FFBool.Intersect => "Пересечь тела", _ => "Объединить тела" };
    public override string Icon => Kind switch { FFBool.Subtract => "ff_subtract_but.svg", FFBool.Intersect => "ff_intersect_but.svg", _ => "ff_union_but.svg" };
    protected override FFBooleanFeature Create() => new() { Op = Kind };

    public override void Build(OpPanel p)
    {
        _target = p.Ref(FFFields.Bodies("Целевое тело", single: true));
        _tools = p.Ref(FFFields.Bodies(Kind == FFBool.Subtract ? "Вычитаемые тела" : "Тела-инструменты"));
        if (Editing) { _target.Kept = 1; _tools.Kept = F.Tools.Count; }
        p.Check("Сохранить тела-инструменты", () => F.KeepTools, v => F.KeepTools = v);
    }

    protected override void SeedRefs()
    {
        // первое выбранное тело — цель, остальные — инструменты
        var sel = W.ViewPort.Selection.Items.Where(r => _target.Accept(r)).ToList();
        if (sel.Count > 0) _target.Take(new[] { sel[0] });
        if (sel.Count > 1) _tools.Take(sel.Skip(1).ToList());
    }

    protected override void Push()
    {
        if (_target.Touched) F.Target = _names.Resolve(_target).FirstOrDefault() ?? "";
        if (_tools.Touched) { F.Tools.Clear(); F.Tools.AddRange(_names.Resolve(_tools).Where(n => n != F.Target)); }
    }
}

public sealed class FFSplitOp : FeatureOp<FFSplitFeature>
{
    private RefField _body = null!, _plane = null!;
    private readonly BodyNameCache _names = new();
    public FFSplitOp(FFSplitFeature? existing = null) : base(existing) { }
    public override string Title => "Разрез тела";
    public override string Icon => "ff_split_but.svg";
    protected override FFSplitFeature Create()
    {
        var fr = FFPlane.Of(Part);
        return new() { PlaneAt = fr.Origin, Normal = fr.W };
    }

    public override void Build(OpPanel p)
    {
        _body = p.Ref(FFFields.Bodies("Тело", single: true));
        _plane = p.Ref(FFFields.Plane("Плоскость разреза (иначе — плоскость построения)"));
        if (Editing) { _body.Kept = 1; _plane.Kept = 1; }
        p.Note("Тело делится плоскостью на два тела.");
    }

    protected override void SeedRefs()
    {
        var sel = W.ViewPort.Selection.Items.ToList();
        if (sel.FirstOrDefault(r => r.Kind == PickKind.DatumPlane) is { } dp) _plane.Take(new[] { dp });
        if (sel.FirstOrDefault(_body.Accept) is { } b) _body.Take(new[] { b });
    }

    protected override void Push()
    {
        if (_body.Touched) F.Body = _names.Resolve(_body).FirstOrDefault() ?? "";
        if (_plane.Touched)
        {
            if (_plane.Items.Count > 0 && Refs.PlaneOf(_plane.Items[0]) is { } pl) { F.PlaneAt = pl.Frame.Origin; F.Normal = pl.Frame.W; }
            else { var fr = FFPlane.Of(Part); F.PlaneAt = fr.Origin; F.Normal = fr.W; }
        }
    }
}

public sealed class FFTransformOp : FeatureOp<FFTransformFeature>
{
    public FFXform Mode;
    private RefField _bodies = null!, _axis = null!, _center = null!;
    private readonly BodyNameCache _names = new();
    public FFTransformOp(FFTransformFeature? existing = null) : base(existing) { if (existing != null) Mode = existing.Mode; }
    public override string Title => Mode switch { FFXform.Rotate => "Поворот тел", FFXform.Scale => "Масштаб тел", _ => "Перенос тел" };
    public override string Icon => Mode switch { FFXform.Rotate => "ff_rotate_but.svg", FFXform.Scale => "ff_scale_but.svg", _ => "ff_move_but.svg" };
    protected override FFTransformFeature Create()
    {
        var fr = FFPlane.Of(Part);
        var f = new FFTransformFeature { Mode = Mode, AxisAt = fr.Origin, AxisDir = fr.W };
        if (Mode == FFXform.Move) f.Dx = new(20);
        if (Mode == FFXform.Rotate) f.Angle = new(45);
        if (Mode == FFXform.Scale) f.Factor = new(1.5);
        return f;
    }

    public override void Build(OpPanel p)
    {
        _bodies = p.Ref(FFFields.Bodies("Тела"));
        if (Editing) _bodies.Kept = F.BodyNames.Count;
        _axis = new RefField { Label = "", Optional = true }; _center = new RefField { Label = "", Optional = true };
        switch (Mode)
        {
            case FFXform.Rotate:
                _axis = p.Ref(Fields.Line("Ось (иначе — нормаль плоскости построения)", optional: true));
                if (Editing) _axis.Kept = 1;
                p.Number("Угол", () => F.Angle, v => F.Angle = v, "°");
                break;
            case FFXform.Scale:
                _center = p.Ref(FFFields.Point("Центр (иначе — начало плоскости построения)"));
                if (Editing) _center.Kept = 1;
                p.Number("Коэффициент", () => F.Factor, v => F.Factor = v, "");
                break;
            default:
                p.Number("Сдвиг X", () => F.Dx, v => F.Dx = v);
                p.Number("Сдвиг Y", () => F.Dy, v => F.Dy = v);
                p.Number("Сдвиг Z", () => F.Dz, v => F.Dz = v);
                break;
        }
        p.Check("Копия (новое тело)", () => F.Copy, v => F.Copy = v);
    }

    protected override void SeedRefs() => Seed(_bodies, _axis, _center);
    public override Action<double>? DragValue => Mode switch
    {
        FFXform.Rotate => v => F.Angle = new ValueRef(v),
        FFXform.Scale => null,
        _ => v => F.Dx = new ValueRef(v),
    };
    public override double? DragCurrent => Mode == FFXform.Rotate ? F.Angle.Value : F.Dx.Value;
    public override string DragLabel => Mode == FFXform.Rotate ? "°" : "X";

    protected override void Push()
    {
        if (_bodies.Touched) { F.BodyNames.Clear(); F.BodyNames.AddRange(_names.Resolve(_bodies)); }
        if (_axis.Touched && _axis.Items.Count > 0 && Refs.LineOf(_axis.Items[0]) is { } ln) { F.AxisAt = ln.At; F.AxisDir = ln.Dir; }
        if (_center.Touched && _center.Items.Count > 0 && Refs.PointOf(_center.Items[0]) is { } c) F.AxisAt = c;
    }
}

public sealed class FFMirrorOp : FeatureOp<FFMirrorFeature>
{
    public FFMirrorMode Mode;
    private RefField _bodies = null!, _plane = null!;
    private readonly BodyNameCache _names = new();
    public FFMirrorOp(FFMirrorFeature? existing = null) : base(existing) { if (existing != null) Mode = existing.Mode; }
    public override string Title => Mode == FFMirrorMode.Symmetry ? "Симметрия" : "Зеркало тел";
    public override string Icon => Mode == FFMirrorMode.Symmetry ? "ff_symmetry_but.svg" : "ff_mirror_but.svg";
    protected override FFMirrorFeature Create()
    {
        var fr = FFPlane.Of(Part);
        return new() { Mode = Mode, PlaneAt = fr.Origin, Normal = fr.U };
    }

    public override void Build(OpPanel p)
    {
        _bodies = p.Ref(FFFields.Bodies("Тела"));
        _plane = p.Ref(FFFields.Plane("Плоскость (иначе — поперёк оси X построения)"));
        if (Editing) { _bodies.Kept = F.BodyNames.Count; _plane.Kept = 1; }
        p.Check("Обратить сторону", () => _flip, v => { _flip = v; F.Normal = -F.Normal; });
        p.Note(Mode == FFMirrorMode.Symmetry
            ? "Остаётся половина тела по нормали плоскости, к ней пристраивается её отражение."
            : "Отражение — новым телом.");
    }
    private bool _flip;

    protected override void SeedRefs()
    {
        var sel = W.ViewPort.Selection.Items.ToList();
        if (sel.FirstOrDefault(r => r.Kind == PickKind.DatumPlane) is { } dp) _plane.Take(new[] { dp });
        var b = sel.Where(_bodies.Accept).ToList();
        if (b.Count > 0) _bodies.Take(b);
    }

    protected override void Push()
    {
        if (_bodies.Touched) { F.BodyNames.Clear(); F.BodyNames.AddRange(_names.Resolve(_bodies)); }
        if (_plane.Touched && _plane.Items.Count > 0 && Refs.PlaneOf(_plane.Items[0]) is { } pl)
        { F.PlaneAt = pl.Frame.Origin; F.Normal = _flip ? -pl.Frame.W : pl.Frame.W; }
    }
}

public sealed class FFArrayOp : FeatureOp<FFArrayFeature>
{
    public FFArrayKind Kind;
    private RefField _bodies = null!, _axis = null!, _curve = null!;
    private readonly BodyNameCache _names = new();
    private ValueRef _sx = new(30), _sy = new(0), _sz = new(0), _tx = new(0), _ty = new(30), _tz = new(0);
    public FFArrayOp(FFArrayFeature? existing = null) : base(existing)
    {
        if (existing == null) return;
        Kind = existing.Kind;
        _sx = new(existing.Step.X); _sy = new(existing.Step.Y); _sz = new(existing.Step.Z);
        _tx = new(existing.Step2.X); _ty = new(existing.Step2.Y); _tz = new(existing.Step2.Z);
    }
    public override string Title => Kind switch { FFArrayKind.Circular => "Круговой массив тел", FFArrayKind.Curve => "Массив тел вдоль кривой", _ => "Прямоугольный массив тел" };
    public override string Icon => Kind switch { FFArrayKind.Circular => "ff_array_circular_but.svg", FFArrayKind.Curve => "ff_array_curve_but.svg", _ => "ff_array_linear_but.svg" };
    protected override FFArrayFeature Create()
    {
        var fr = FFPlane.Of(Part);
        return new() { Kind = Kind, AxisAt = fr.Origin, AxisDir = fr.W, Count = new(Kind == FFArrayKind.Circular ? 6 : 3), Step = fr.U * 30, Step2 = fr.V * 30 };
    }

    public override void Build(OpPanel p)
    {
        _bodies = p.Ref(FFFields.Bodies("Тела"));
        if (Editing) _bodies.Kept = F.BodyNames.Count;
        _axis = new RefField { Label = "", Optional = true }; _curve = new RefField { Label = "", Optional = true };
        p.Number("Количество", () => F.Count, v => F.Count = v, "");
        switch (Kind)
        {
            case FFArrayKind.Circular:
                _axis = p.Ref(Fields.Line("Ось (иначе — нормаль плоскости построения)", optional: true));
                if (Editing) _axis.Kept = 1;
                p.Number("Угол", () => F.Angle, v => F.Angle = v, "°");
                break;
            case FFArrayKind.Curve:
                _curve = p.Ref(FFFields.Curve("Кривая"));
                if (Editing) _curve.Kept = 1;
                p.Check("Поворачивать по касательной", () => F.Align, v => F.Align = v);
                break;
            default:
                p.Number("Шаг X", () => _sx, v => _sx = v); p.Number("Шаг Y", () => _sy, v => _sy = v); p.Number("Шаг Z", () => _sz, v => _sz = v);
                p.Number("Количество 2", () => F.Count2, v => F.Count2 = v, "");
                p.Number("Шаг 2 X", () => _tx, v => _tx = v); p.Number("Шаг 2 Y", () => _ty, v => _ty = v); p.Number("Шаг 2 Z", () => _tz, v => _tz = v);
                break;
        }
    }

    protected override void SeedRefs() => Seed(_bodies, _axis, _curve);
    protected override bool MissingRequiredExtra => Kind == FFArrayKind.Curve && F.CurveName.Length == 0 && _curve.Items.Count == 0;

    protected override void Push()
    {
        if (_bodies.Touched) { F.BodyNames.Clear(); F.BodyNames.AddRange(_names.Resolve(_bodies)); }
        if (_axis.Touched && _axis.Items.Count > 0 && Refs.LineOf(_axis.Items[0]) is { } ln) { F.AxisAt = ln.At; F.AxisDir = ln.Dir; }
        if (_curve.Touched && _curve.Items.Count > 0 && _curve.Items[0].Target is DatumCurve dc) F.CurveName = dc.Name;
        if (Kind == FFArrayKind.Linear)
        {
            var prm = Parameters;
            F.Step = new Vector3d(_sx.Resolve(prm), _sy.Resolve(prm), _sz.Resolve(prm));
            F.Step2 = new Vector3d(_tx.Resolve(prm), _ty.Resolve(prm), _tz.Resolve(prm));
        }
    }
}

public sealed class FFAlignFaceOp : FeatureOp<FFAlignFaceFeature>
{
    private RefField _faces = null!, _plane = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public FFAlignFaceOp(FFAlignFaceFeature? existing = null) : base(existing) { }
    public override string Title => "Выровнять грань";
    public override string Icon => "ff_align_face_but.svg";
    protected override FFAlignFaceFeature Create()
    {
        var fr = FFPlane.Of(Part);
        return new() { PlaneAt = fr.Origin, Normal = fr.W };
    }

    public override void Build(OpPanel p)
    {
        _faces = p.Ref(new RefField { Label = "Грани", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face && Refs.IsPlanar(r) });
        _plane = p.Ref(Fields.Plane("К плоскости (грань, опорная)"));
        if (Editing) { _faces.Kept = F.OnFaces.Count; _plane.Kept = 1; }
        p.Number("Смещение", () => F.Offset, v => F.Offset = v);
        p.Note("Плоские грани переходят в выбранную плоскость; соседние грани продлеваются до неё.");
    }

    protected override void SeedRefs()
    {
        // последняя выбранная плоская грань — цель, прочие — выравниваемые
        var sel = W.ViewPort.Selection.Items.Where(Refs.IsPlanar).ToList();
        if (sel.Count >= 2) { _plane.Take(new[] { sel[^1] }); _faces.Take(sel.Take(sel.Count - 1).ToList()); }
        else Seed(_faces);
    }

    public override Action<double>? DragValue => v => F.Offset = new ValueRef(v);
    public override double? DragCurrent => F.Offset.Value;
    public override string DragLabel => "d";

    protected override void Push()
    {
        if (_faces.Touched) { F.OnFaces.Clear(); F.OnFaceNames.Clear(); F.OnFaces.AddRange(_cache.Resolve(_faces, Refs.PointOf)); }
        if (_plane.Touched && _plane.Items.Count > 0 && Refs.PlaneOf(_plane.Items[0]) is { } pl) { F.PlaneAt = pl.Frame.Origin; F.Normal = pl.Frame.W; }
    }
}
