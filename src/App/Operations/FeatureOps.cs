using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// ====================================================================== общие поля

internal static class Fields
{
    public static RefField Sketch() => new()
    {
        Label = "Эскиз", Filter = PickFilter.SketchCurves, Accept = Refs.IsSketch, Single = true,
        Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?"),
    };

    public static RefField Edges() => new() { Label = "Рёбра", Filter = PickFilter.Edges, Accept = r => r.Kind == PickKind.Edge };

    public static RefField Vertex() => new() { Label = "Вершина", Filter = PickFilter.Vertices, Accept = r => r.Kind == PickKind.Vertex, Single = true };

    public static RefField Plane(string label = "Плоскость") => new()
    {
        Label = label, Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true,
    };

    public static RefField Line(string label, bool optional = false) => new()
    {
        Label = label, Filter = PickFilter.Edges | PickFilter.Datums | PickFilter.SketchCurves, Accept = Refs.IsLinear, Single = true, Optional = optional,
    };

    public static PickRef SketchRef(Sketch sk) => new(sk.Id, PickKind.SketchCurve, sk.Parent, sk);
}

/// <summary>Кэш ссылок по номеру выбора: рёбра и грани запоминаются при выборе (после перестроения их номеров может не быть).</summary>
internal sealed class RefCache<TV> where TV : struct
{
    private readonly Dictionary<uint, TV> _map = new();
    public IEnumerable<TV> Resolve(RefField f, Func<PickRef, TV?> make)
    {
        foreach (var r in f.Items)
        {
            if (!_map.TryGetValue(r.Id, out var v))
            {
                if (make(r) is not { } m) continue;
                _map[r.Id] = v = m;
            }
            yield return v;
        }
    }
}

// ====================================================================== выдавливание и вращение

public sealed class ExtrudeOp : FeatureOp<ExtrudeFeature>
{
    public Sketch? SeedSketch;
    public bool StartCut;
    private RefField _sk = null!;
    public ExtrudeOp(ExtrudeFeature? existing = null) : base(existing) { }
    public override string Title => StartCut ? "Вырез выдавливанием" : "Выдавливание";
    public override string Icon => "f_extrude_but.svg";
    protected override ExtrudeFeature Create() => new() { Cut = StartCut };

    public override void Build(OpPanel p)
    {
        _sk = p.Ref(Fields.Sketch());
        if (Editing && F.SketchId != 0) _sk.Kept = 1;
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Choice("Глубина до", new[] { "На расстояние", "Насквозь" }, () => F.Side1.End == EndType.ThroughAll ? 1 : 0,
            v => F.Side1.End = v == 1 ? EndType.ThroughAll : EndType.Distance);
        p.Number("Глубина", () => F.Side1.Distance, v => F.Side1.Distance = v);
        p.Check("Обратное направление", () => F.Side1.Flip, v => F.Side1.Flip = v);
        p.Check("Симметрично", () => F.Symmetric, v => F.Symmetric = v);
    }

    protected override void SeedRefs()
    {
        if (SeedSketch != null) _sk.Take(new[] { Fields.SketchRef(SeedSketch) });
        else Seed(_sk);
    }

    protected override void Push()
    {
        if (_sk.Items.Count > 0 && Refs.SketchOf(_sk.Items[0]) is { } sk) F.SketchId = sk.Id;
    }
}

public sealed class RevolveOp : FeatureOp<RevolveFeature>
{
    public Sketch? SeedSketch;
    private RefField _sk = null!, _axis = null!;
    public RevolveOp(RevolveFeature? existing = null) : base(existing) { }
    public override string Title => "Вращение";
    public override string Icon => "f_revolve_but.svg";
    protected override RevolveFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _sk = p.Ref(Fields.Sketch());
        if (Editing && F.SketchId != 0) _sk.Kept = 1;
        _axis = p.Ref(new RefField
        {
            Label = "Ось", Optional = true, Single = true,
            Filter = PickFilter.Datums | PickFilter.SketchCurves,
            Accept = r => r.Target is DatumAxis || r.Target is SkLine,
        });
        if (Editing && (F.AxisId != 0 || F.AxisEntityId != 0)) _axis.Kept = 1;
        p.Note("Без оси — осевая линия эскиза.");
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Number("Угол", () => F.EndAngle, v => F.EndAngle = v, "°");
    }

    protected override void SeedRefs()
    {
        if (SeedSketch != null) _sk.Take(new[] { Fields.SketchRef(SeedSketch) });
        else Seed(_sk);
    }

    protected override void Push()
    {
        if (_sk.Items.Count > 0 && Refs.SketchOf(_sk.Items[0]) is { } sk) F.SketchId = sk.Id;
        if (_axis.Touched)
        {
            F.AxisId = 0; F.AxisEntityId = 0;
            if (_axis.Items.Count > 0)
            {
                var r = _axis.Items[0];
                if (r.Target is DatumAxis ax) F.AxisId = ax.Id;
                else if (r.Target is SkLine ln) F.AxisEntityId = ln.Id;
            }
        }
    }
}

// ====================================================================== скругление и фаска

public sealed class RoundOp : FeatureOp<RoundFeature>
{
    private RefField _edges = null!;
    private readonly RefCache<EdgeRef> _cache = new();
    public RoundOp(RoundFeature? existing = null) : base(existing) { }
    public override string Title => "Скругление";
    public override string Icon => "f_round_but.svg";
    protected override RoundFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _edges = p.Ref(Fields.Edges());
        if (Editing) _edges.Kept = F.Edges.Count + F.Selectors.Count;
        p.Number("Радиус", () => F.Radius, v => F.Radius = v);
    }

    protected override void SeedRefs() => Seed(_edges);

    protected override void Push()
    {
        if (!_edges.Touched) return;
        var list = _cache.Resolve(_edges, r => Part.FindEdge(r.Id) is { } e ? EdgeRef.Of(e) : (EdgeRef?)null).ToList();
        F.Edges.Clear(); F.Edges.AddRange(list);
        F.Selectors.Clear();
    }
}

public sealed class ChamferOp : FeatureOp<ChamferFeature>
{
    private RefField _edges = null!;
    private readonly RefCache<EdgeRef> _cache = new();
    public ChamferOp(ChamferFeature? existing = null) : base(existing) { }
    public override string Title => "Фаска";
    public override string Icon => "f_chamfer_but.svg";
    protected override ChamferFeature Create() => new() { Mode = ChamferKind.TwoDistances };

    public override void Build(OpPanel p)
    {
        _edges = p.Ref(Fields.Edges());
        if (Editing) _edges.Kept = F.Edges.Count + F.Selectors.Count;
        p.Number("Размер", () => F.D1, v => { F.D1 = v; F.D2 = v; });
    }

    protected override void SeedRefs() => Seed(_edges);

    protected override void Push()
    {
        if (!_edges.Touched) return;
        var list = _cache.Resolve(_edges, r => Part.FindEdge(r.Id) is { } e ? EdgeRef.Of(e) : (EdgeRef?)null).ToList();
        F.Edges.Clear(); F.Edges.AddRange(list);
        F.Selectors.Clear();
    }
}

public sealed class VertexRoundOp : FeatureOp<VertexRoundFeature>
{
    private RefField _v = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public VertexRoundOp(VertexRoundFeature? existing = null) : base(existing) { }
    public override string Title => "Скругление вершины";
    public override string Icon => "f_round_vertex_but.svg";
    protected override VertexRoundFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _v = p.Ref(Fields.Vertex());
        if (Editing) _v.Kept = 1;
        p.Number("Радиус", () => F.Radius, v => F.Radius = v);
    }

    protected override void SeedRefs() => Seed(_v);

    protected override void Push()
    {
        if (!_v.Touched) return;
        foreach (var p in _cache.Resolve(_v, Refs.PointOf)) { F.Vertex = p; F.VertexKey = null; }
    }
}

public sealed class VertexChamferOp : FeatureOp<VertexChamferFeature>
{
    private RefField _v = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public VertexChamferOp(VertexChamferFeature? existing = null) : base(existing) { }
    public override string Title => "Фаска угла";
    public override string Icon => "f_chamfer_vertex_but.svg";
    protected override VertexChamferFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _v = p.Ref(Fields.Vertex());
        if (Editing) _v.Kept = 1;
        p.Number("Размер", () => F.Distance, v => F.Distance = v);
    }

    protected override void SeedRefs() => Seed(_v);

    protected override void Push()
    {
        if (!_v.Touched) return;
        foreach (var p in _cache.Resolve(_v, Refs.PointOf)) { F.Vertex = p; F.VertexKey = null; }
    }
}

// ====================================================================== оболочка и отверстие

public sealed class ShellOp : FeatureOp<ShellFeature>
{
    private RefField _faces = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public ShellOp(ShellFeature? existing = null) : base(existing) { }
    public override string Title => "Оболочка";
    public override string Icon => "f_shell_but.svg";
    protected override ShellFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _faces = p.Ref(new RefField { Label = "Открытые грани", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face, Optional = true });
        if (Editing) _faces.Kept = F.OpenFaces.Count;
        p.Number("Толщина", () => F.Thickness, v => F.Thickness = v);
        p.Note("Отрицательная толщина — стенка наружу.");
    }

    protected override void SeedRefs() => Seed(_faces);

    protected override void Push()
    {
        if (!_faces.Touched) return;
        F.OpenFaces.Clear();
        F.OpenFaceNames.Clear();
        F.OpenFaces.AddRange(_cache.Resolve(_faces, Refs.PointOf));
    }
}

public sealed class HoleOp : FeatureOp<HoleFeature>
{
    private RefField _face = null!, _pts = null!;
    private readonly RefCache<Vector3d> _cache = new();
    private Vector3d? _faceCentre, _faceNormal;
    public HoleOp(HoleFeature? existing = null) : base(existing) { }
    public override string Title => "Отверстие";
    public override string Icon => "f_hole_but.svg";
    protected override HoleFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _face = p.Ref(Fields.Plane("Грань входа"));
        _pts = p.Ref(new RefField
        {
            Label = "Точки", Optional = true,
            Filter = PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums,
            Accept = r => r.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint,
        });
        if (Editing) { _face.Kept = 1; _pts.Kept = F.Points.Count; }
        p.Note("Без точек — отверстие в середине грани.");
        p.Choice("Вид", new[] { "Простое", "С цекованием", "С зенковкой" }, () => (int)F.Kind, v => F.Kind = (HoleKind)v);
        p.Number("Диаметр", () => F.Diameter, v => F.Diameter = v);
        p.Number("Глубина", () => F.Depth, v => F.Depth = v);
        p.Check("Насквозь", () => F.ThroughAll, v => F.ThroughAll = v);
        p.Number("Диаметр головки", () => F.HeadDiameter, v => F.HeadDiameter = v);
        p.Number("Глубина головки", () => F.HeadDepth, v => F.HeadDepth = v);
    }

    protected override void SeedRefs() => Seed(_face, _pts);

    protected override void Push()
    {
        if (_face.Touched)
        {
            _faceCentre = null; _faceNormal = null;
            if (_face.Items.Count > 0 && Refs.PlaneOf(_face.Items[0]) is { } pl)
            {
                _faceCentre = pl.Frame.Origin;
                // грань тела — сверлить внутрь (против нормали); опорная плоскость — против её нормали
                _faceNormal = pl.Frame.W;
            }
        }
        if (!_face.Touched && !_pts.Touched) return;
        if (_faceNormal is { } n) F.Direction = -n;
        F.Points.Clear();
        F.PointFaces.Clear();
        F.Points.AddRange(_cache.Resolve(_pts, Refs.PointOf));
        if (F.Points.Count == 0 && _faceCentre is { } c) F.Points.Add(c);
    }
}

// ====================================================================== зеркало и массивы

public sealed class MirrorOp : FeatureOp<MirrorFeature>
{
    private RefField _pl = null!;
    public MirrorOp(MirrorFeature? existing = null) : base(existing) { }
    public override string Title => "Зеркало";
    public override string Icon => "f_mirror_but.svg";
    protected override MirrorFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _pl = p.Ref(Fields.Plane("Плоскость зеркала"));
        if (Editing) _pl.Kept = 1;
    }

    protected override void SeedRefs() => Seed(_pl);

    protected override void Push()
    {
        if (!_pl.Touched || _pl.Items.Count == 0 || Refs.PlaneOf(_pl.Items[0]) is not { } pl) return;
        F.At = pl.Frame.Origin;
        F.Normal = pl.Frame.W;
    }
}

public sealed class LinearPatternOp : FeatureOp<LinearPatternFeature>
{
    private RefField _dir = null!;
    private ValueRef _spacing = new(20);
    private bool _flip;
    public LinearPatternOp(LinearPatternFeature? existing = null) : base(existing)
    {
        if (existing != null) _spacing = new ValueRef(existing.Step.Length);
    }
    public override string Title => "Линейный массив";
    public override string Icon => "f_pattern_linear_but.svg";
    protected override LinearPatternFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _dir = p.Ref(Fields.Line("Направление"));
        if (Editing) _dir.Kept = 1;
        p.Number("Шаг", () => _spacing, v => _spacing = v);
        p.Number("Количество", () => F.Count, v => F.Count = v, "шт");
        p.Check("Обратное направление", () => _flip, v => _flip = v);
    }

    protected override void SeedRefs() => Seed(_dir);

    protected override void Push()
    {
        Vector3d d = F.Step.LengthSquared > 1e-18 ? F.Step.Normalized() : Vector3d.UnitX;
        if (_dir.Touched && _dir.Items.Count > 0 && Refs.LineOf(_dir.Items[0]) is { } ln) d = ln.Dir;
        if (_flip) { d = -d; _flip = false; }
        F.Step = d * _spacing.Value;
    }
}

public sealed class CircularPatternOp : FeatureOp<CircularPatternFeature>
{
    private RefField _axis = null!;
    public CircularPatternOp(CircularPatternFeature? existing = null) : base(existing) { }
    public override string Title => "Круговой массив";
    public override string Icon => "f_pattern_circular_but.svg";
    protected override CircularPatternFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _axis = p.Ref(Fields.Line("Ось", optional: true));
        if (Editing) _axis.Kept = 1;
        p.Note("Без оси — ось Z через начало координат.");
        p.Number("Количество", () => F.Count, v => F.Count = v, "шт");
    }

    protected override void SeedRefs() => Seed(_axis);

    protected override void Push()
    {
        if (!_axis.Touched) return;
        if (_axis.Items.Count > 0 && Refs.LineOf(_axis.Items[0]) is { } ln) { F.At = ln.At; F.Axis = ln.Dir; }
        else { F.At = Vector3d.Zero; F.Axis = Vector3d.UnitZ; }
    }
}

// ====================================================================== опорная плоскость (элемент, не операция)

public sealed class PlaneOp : Operation
{
    private RefField _base = null!;
    private ValueRef _offset = new(20);
    private bool _flip;
    private DatumPlane? _dp;
    public override string Title => "Плоскость";
    public override string Icon => "dat_plane_but.svg";

    protected override bool Start()
    {
        W.Session.BeginEdit(L(Title));
        return true;
    }

    public override void Build(OpPanel p)
    {
        _base = p.Ref(Fields.Plane("От плоскости"));
        p.Number("Смещение", () => _offset, v => _offset = v);
        p.Check("Обратное направление", () => _flip, v => _flip = v);
    }

    public override void Prime()
    {
        Seed(_base);
        if (_base.Text != null) _base.Text.Text = _base.Summary;
        Apply();
        RefreshStatus();
    }

    protected override void Apply()
    {
        if (_base.Items.Count == 0 || Refs.PlaneOf(_base.Items[0]) is not { } pl) return;
        double d = _offset.Value * (_flip ? -1 : 1);
        var frame = pl.Frame.OffsetAlongNormal(d);
        if (_dp == null)
        {
            _dp = new DatumPlane(L("Плоскость{0}", Part.Planes.Count() + 1), frame) { Mode = DatumPlaneMode.OffsetFromFace, Offset = d };
            _dp.References.Add(pl.SourceId);
            Part.Add(_dp);
        }
        else
        {
            _dp.Frame = frame;
            _dp.Offset = d;
            _dp.References.Clear();
            _dp.References.Add(pl.SourceId);
            _dp.NotifyChanged(_dp);
        }
        W.ViewPort.MarkDirty();
    }

    public override bool Commit()
    {
        if (_dp == null) { W.ShowToast(L("Выберите плоскость или плоскую грань")); return false; }
        W.Session.CommitEdit();
        return true;
    }

    public override void Cancel() => W.Session.CancelEdit();
}

// ====================================================================== эскиз

/// <summary>Новый эскиз: плоскость (опорная или плоская грань) — и сразу в режим эскиза.</summary>
public sealed class SketchOp : Operation
{
    private RefField _pl = null!;
    /// <summary>Что начать после эскиза (эскиз → выдавливание за один заход).</summary>
    public Func<Sketch, Operation>? Then;
    public override string Title => "Эскиз";
    public override string Icon => "sketch_but.svg";
    public override string OkLabel => "Начать эскиз";

    protected override bool Start()
    {
        // плоскость уже выбрана — эскиз начинается без панели
        var sel = W.ViewPort.Selection.Items.FirstOrDefault(Refs.IsPlanar);
        if (!sel.IsEmpty && Refs.PlaneOf(sel) is { } pl)
        {
            W.BeginNewSketch(Part, pl.Frame, pl.SourceId, pl.Name, Then);
            return false;
        }
        return true;
    }

    public override void Build(OpPanel p)
    {
        _pl = p.Ref(Fields.Plane());
        p.Note("Выберите опорную плоскость (в виде или в дереве) или плоскую грань тела.");
    }

    protected override void Apply() { }

    public override bool Commit()
    {
        if (_pl.Items.Count == 0 || Refs.PlaneOf(_pl.Items[0]) is not { } pl)
        {
            W.ShowToast(L("Выберите плоскость или плоскую грань"));
            return false;
        }
        W.BeginNewSketch(Part, pl.Frame, pl.SourceId, pl.Name, Then);
        return true;
    }

    public override void Cancel() { }
}

/// <summary>Операция для правки готовой (двойной щелчок в дереве, «Изменить»): null — правка этой операции панелью не поддерживается.</summary>
public static class OperationFactory
{
    public static Operation? ForEdit(Feature f) => f switch
    {
        ExtrudeFeature x => new ExtrudeOp(x),
        RevolveFeature x => new RevolveOp(x),
        RoundFeature x => new RoundOp(x),
        ChamferFeature x => new ChamferOp(x),
        VertexRoundFeature x => new VertexRoundOp(x),
        VertexChamferFeature x => new VertexChamferOp(x),
        ShellFeature x => new ShellOp(x),
        HoleFeature x => new HoleOp(x),
        MirrorFeature x => new MirrorOp(x),
        LinearPatternFeature x => new LinearPatternOp(x),
        CircularPatternFeature x => new CircularPatternOp(x),
        _ => null,
    };
}
