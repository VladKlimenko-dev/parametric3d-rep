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

    // u12: кромка (граница касательных граней) не берётся — скруглять и срезать там нечего
    public static RefField Edges() => new() { Label = "Рёбра", Filter = PickFilter.Edges, Accept = r => r.Kind == PickKind.Edge && !Topo.IsSeam(r) };

    public static RefField Vertex() => new() { Label = "Вершина", Filter = PickFilter.Vertices, Accept = r => r.Kind == PickKind.Vertex, Single = true };

    public static RefField Plane(string label = "Плоскость") => new()
    {
        Label = label, Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true,
    };

    public static RefField Line(string label, bool optional = false) => new()
    {
        Label = label, Filter = PickFilter.Edges | PickFilter.Datums | PickFilter.SketchCurves, Accept = r => Refs.IsLinear(r) && !Topo.IsSeam(r), Single = true, Optional = optional,
    };

    /// <summary>
    /// u12 (замечание 10): «Плоскость» перед «Эскизом» у выдавливания и вращения: эскиза ещё нет — выбирают грань или
    /// опорную плоскость, приложение создаёт на ней эскиз и переходит в его правку; по завершении эскиза — снова эта
    /// операция с этим эскизом.
    /// </summary>
    public static RefField SketchPlane() => new()
    {
        Label = "Плоскость", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true,
    };
    public static void SketchOnPlane(Operation op, RefField? plane, RefField sk, Func<Sketch, Operation> then)
    {
        if (plane == null || plane.Items.Count == 0 || sk.Items.Count > 0) return;
        if (Refs.PlaneOf(plane.Items[0]) is not { } pl) return;
        var at = plane.Items[0];
        var w = op.Window; var part = w.Session.Part;
        if (part == null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(w.CurrentOperation, op)) return;
            w.CancelOperation();
            w.BeginNewSketch(part, SketchOp.SketchFrame(at, pl.Frame), pl.SourceId, pl.Name, then);
        });
    }

    /// <summary>u12: операции дерева для зеркала и массивов операций (выбор в дереве).</summary>
    public static RefField Features() => new()
    {
        Label = "Операции", Filter = PickFilter.None, Optional = true,
        Accept = r => r.Kind == PickKind.Feature && r.Target is Feature f && f is not (MirrorFeature or LinearPatternFeature or CircularPatternFeature),
        Describe = r => (r.Target as Feature)?.Name ?? "?",
    };
    public static void PushSources(RefField f, List<string> sources)
    {
        if (!f.Touched) return;
        sources.Clear();
        foreach (var r in f.Items) if (r.Target is Feature ft && !sources.Contains(ft.Key)) sources.Add(ft.Key);
    }

    public static PickRef SketchRef(Sketch sk) => new(sk.Id, PickKind.SketchCurve, sk.Parent, sk);
}

/// <summary>Кэш ссылок по номеру выбора: рёбра и грани запоминаются при выборе (после перестроения их номеров может не быть).</summary>
internal sealed class RefCache<TV> where TV : struct
{
    private readonly Dictionary<uint, TV> _map = new();
    /// <summary>u09: заранее снятые значения (ручка: рёбра и точки до операции — после предпросмотра их номеров нет).</summary>
    public Dictionary<uint, TV>? Preset;
    public IEnumerable<TV> Resolve(RefField f, Func<PickRef, TV?> make)
    {
        foreach (var r in f.Items)
        {
            if (!_map.TryGetValue(r.Id, out var v))
            {
                if (Preset != null && Preset.TryGetValue(r.Id, out var pv)) { _map[r.Id] = pv; yield return pv; continue; }
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
    /// <summary>Эскиз, создаваемый вместе с операцией (контур грани ручкой): входит в тот же шаг отмены.</summary>
    public Func<Part, Sketch?>? PreSketch;
    private Sketch? _created;
    private RefField _sk = null!;
    public ExtrudeOp(ExtrudeFeature? existing = null) : base(existing) { }
    public override string Title => StartThin ? "Тонкое выдавливание" : StartCut ? "Вырез выдавливанием" : "Выдавливание";
    public override string Icon => "f_extrude_but.svg";
    protected override ExtrudeFeature Create()
    {
        var f = new ExtrudeFeature { Cut = StartCut };
        if (FlipCut) f.Side1.Flip = true;
        if (StartThin) f.Thin = new ValueRef(2);
        return f;
    }

    public override void Build(OpPanel p)
    {
        if (!Editing) _plane = p.Ref(Fields.SketchPlane());
        _sk = p.Ref(Fields.Sketch());
        if (Editing && F.SketchId != 0) _sk.Kept = 1;
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Choice("Глубина до", new[] { "На расстояние", "Насквозь", "До следующей грани" }, () => EndIndex(F.Side1.End),
            v => F.Side1.End = EndOf(v));
        p.Number("Глубина", () => F.Side1.Distance, v => F.Side1.Distance = v, visible: () => F.Side1.End == EndType.Distance);
        p.Check("Обратное направление", () => F.Side1.Flip, v => F.Side1.Flip = v);
        p.Check("Симметрично", () => F.Symmetric, v => { F.Symmetric = v; if (v) F.TwoSided = false; });
        p.Check("Вторая сторона", () => F.TwoSided, v => { F.TwoSided = v; if (v) F.Symmetric = false; }, visible: () => !F.Symmetric);
        p.Choice("Вторая: до", new[] { "На расстояние", "Насквозь", "До следующей грани" }, () => EndIndex(F.Side2.End),
            v => F.Side2.End = EndOf(v), visible: () => F.TwoSided);
        p.Number("Глубина 2", () => F.Side2.Distance, v => F.Side2.Distance = v, visible: () => F.TwoSided && F.Side2.End == EndType.Distance);
        p.Check("Тонкое (стенка)", () => F.Thin != null, v => F.Thin = v ? (F.Thin ?? new ValueRef(2)) : null);
        p.Number("Толщина стенки", () => F.Thin ?? new ValueRef(2), v => F.Thin = v, visible: () => F.Thin != null);
        p.Check("Стенка наружу", () => F.ThinOutside, v => F.ThinOutside = v, visible: () => F.Thin != null);
    }
    /// <summary>u13: кнопка «тонкое выдавливание» — сразу со стенкой.</summary>
    public bool StartThin;

    /// <summary>
    /// k102 (замечание 7): глубина на виде — от плоскости эскиза вдоль нормали, у края контура (в сторону оси U эскиза);
    /// симметрично — общий размер; вторая сторона — свой размер с другой стороны контура.
    /// </summary>
    public override IEnumerable<ViewDim> ViewDims()
    {
        if (F == null || F.SketchId == 0 || Part.Sketches.FirstOrDefault(s => s.Id == F.SketchId) is not { } sk) yield break;
        var pts = sk.AllPoints().Where(p => p.Owner?.Role != SkRole.Reference && p.Owner?.Role != SkRole.Construction).Select(p => p.P).ToList();
        if (pts.Count == 0) yield break;
        double xmax = pts.Max(p => p.X), xmin = pts.Min(p => p.X), ymid = (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2;
        var n = sk.Plane.W.Normalized(); var u = sk.Plane.U.Normalized();
        var right = sk.Plane.ToWorld(new Vector2d(xmax, ymid)); var left = sk.Plane.ToWorld(new Vector2d(xmin, ymid));
        string Id(ExtrudeSide side) => Parametric3D.Model.Params.ModelRefs.PathOf(F, Parametric3D.Model.Params.FeatureDims.IndexOf(F, side, "Distance"));
        static string V(double v) => Core.Settings.Current.FormatLength(Math.Abs(v), false);
        if (F.Side1.End != EndType.Distance) yield break;
        double d1 = Math.Abs(F.Side1.Distance.Value) * (F.Side1.Flip ? -1 : 1);
        if (F.Symmetric)
        {
            yield return new ViewDim(right - n * (d1 / 2), right + n * (d1 / 2), u, V(d1), Id(F.Side1));
            yield break;
        }
        yield return new ViewDim(right, right + n * d1, u, V(d1), Id(F.Side1));
        if (F.TwoSided && F.Side2.End == EndType.Distance)
        {
            double d2 = Math.Abs(F.Side2.Distance.Value) * (F.Side2.Flip ? -1 : 1);
            yield return new ViewDim(left, left - n * d2, -u, V(d2), Id(F.Side2));
        }
    }

    private static int EndIndex(EndType e) => e switch { EndType.ThroughAll => 1, EndType.ToNext => 2, _ => 0 };
    private static EndType EndOf(int i) => i switch { 1 => EndType.ThroughAll, 2 => EndType.ToNext, _ => EndType.Distance };

    /// <summary>Выдавливание контура грани ручкой: вырез — внутрь (против нормали грани).</summary>
    public bool FlipCut;
    public override Action<double>? DragValue => v => F.Side1.Distance = new ValueRef(v);
    public override double? DragCurrent => F.Side1.Distance.Value;
    public override string DragLabel => "H";

    protected override void SeedRefs()
    {
        if (PreSketch != null && PreSketch(Part) is { } made) { Part.Add(made); _created = made; SeedSketch = made; }
        if (SeedSketch != null) _sk.Take(new[] { Fields.SketchRef(SeedSketch) });
        else Seed(_sk);
    }

    private RefField? _plane;
    protected override void Push()
    {
        if (_sk.Items.Count > 0 && Refs.SketchOf(_sk.Items[0]) is { } sk) F.SketchId = sk.Id;
        bool cut = F.Cut, thin = F.Thin != null;
        Fields.SketchOnPlane(this, _plane, _sk, sk => new ExtrudeOp { SeedSketch = sk, StartCut = cut, StartThin = thin });
    }

    public override void Cancel()
    {
        base.Cancel();
        if (_created != null) Part.Remove(_created);
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
        if (!Editing) _plane = p.Ref(Fields.SketchPlane());
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
        p.Number("Начальный угол", () => F.StartAngle, v => F.StartAngle = v, "°");
    }

    protected override void SeedRefs()
    {
        if (SeedSketch != null) _sk.Take(new[] { Fields.SketchRef(SeedSketch) });
        else Seed(_sk);
    }

    private RefField? _plane;
    protected override void Push()
    {
        if (_sk.Items.Count > 0 && Refs.SketchOf(_sk.Items[0]) is { } sk) F.SketchId = sk.Id;
        Fields.SketchOnPlane(this, _plane, _sk, s2 => new RevolveOp { SeedSketch = s2 });
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
    /// <summary>u12: выбранные рёбра поля (ручка операции, начатой командой).</summary>
    public IReadOnlyList<PickRef> EdgeRefs => _edges?.Items ?? (IReadOnlyList<PickRef>)Array.Empty<PickRef>();
    public Dictionary<uint, EdgeRef>? PresetEdges { set => _cache.Preset = value; }
    public override string Title => Variable ? "Переменное скругление" : "Скругление";
    public override string Icon => Variable ? "f_round_variable_but.svg" : "f_round_but.svg";
    /// <summary>u13: кнопка «Переменное скругление» — радиус в конце сразу включён (вдвое больше начального).</summary>
    public bool Variable;
    protected override RoundFeature Create() { var f = new RoundFeature(); if (Variable) f.RadiusEnd = new ValueRef(f.Radius.Value * 2); return f; }

    public override void Build(OpPanel p)
    {
        _edges = p.Ref(Fields.Edges());
        if (Editing) _edges.Kept = F.Edges.Count + F.Selectors.Count;
        p.Number("Радиус", () => F.Radius, v => F.Radius = v);
        p.Check("Переменный радиус", () => F.RadiusEnd != null, v => F.RadiusEnd = v ? F.Radius : null);
        p.Number("Радиус в конце", () => F.RadiusEnd ?? F.Radius, v => F.RadiusEnd = v, visible: () => F.RadiusEnd != null);
        p.Note("Касательно продолжающие рёбра выбираются вместе (Alt — одно ребро).");
    }

    public override Action<double>? DragValue => v => F.Radius = new ValueRef(v);
    public override double? DragCurrent => F.Radius.Value;
    public override string DragLabel => "R";

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
    public IReadOnlyList<PickRef> EdgeRefs => _edges?.Items ?? (IReadOnlyList<PickRef>)Array.Empty<PickRef>();
    public Dictionary<uint, EdgeRef>? PresetEdges { set => _cache.Preset = value; }
    public override string Title => "Фаска";
    public override string Icon => "f_chamfer_but.svg";
    protected override ChamferFeature Create() => new() { Mode = ChamferKind.TwoDistances };

    public override void Build(OpPanel p)
    {
        _edges = p.Ref(Fields.Edges());
        if (Editing) _edges.Kept = F.Edges.Count + F.Selectors.Count;
        _equal = !Editing || (F.Mode == ChamferKind.TwoDistances && Math.Abs(F.D1.Value - F.D2.Value) < 1e-12 && F.D1.Formula == F.D2.Formula);
        p.Choice("Тип фаски", new[] { "Равные катеты (D × D)", "Два катета (D1 × D2)", "Катет и угол (D × A)" },
            () => F.Mode == ChamferKind.DistanceAngle ? 2 : _equal ? 0 : 1,
            v => { F.Mode = v == 2 ? ChamferKind.DistanceAngle : ChamferKind.TwoDistances; _equal = v == 0; if (_equal) F.D2 = F.D1; else F.SizeEnd = null; });
        p.Number("Катет D1", () => F.D1, v => { F.D1 = v; if (_equal) F.D2 = v; });
        p.Number("Катет D2", () => F.D2, v => F.D2 = v, visible: () => !_equal && F.Mode == ChamferKind.TwoDistances);
        p.Number("Угол", () => F.Angle, v => F.Angle = v, "°", visible: () => F.Mode == ChamferKind.DistanceAngle);
        p.Check("Поменять катеты местами", () => false, v => { if (v) (F.D1, F.D2) = (F.D2, F.D1); }, visible: () => !_equal && F.Mode == ChamferKind.TwoDistances);
        // u12: переменная фаска — равные катеты от D1 в начале цепочки до размера в конце, плавно по длине
        p.Check("Переменный размер", () => F.SizeEnd != null, v => { F.SizeEnd = v ? F.D1 : null; if (v) { _equal = true; F.Mode = ChamferKind.TwoDistances; F.D2 = F.D1; } }, visible: () => _equal && F.Mode == ChamferKind.TwoDistances);
        p.Number("Размер в конце", () => F.SizeEnd ?? F.D1, v => F.SizeEnd = v, visible: () => F.SizeEnd != null);
        p.Note("Касательно продолжающие рёбра выбираются вместе (Alt — одно ребро).");
    }

    private bool _equal = true;

    public override Action<double>? DragValue => v => { F.D1 = new ValueRef(v); if (_equal || F.Mode == ChamferKind.DistanceAngle) F.D2 = new ValueRef(v); };
    public override double? DragCurrent => F.D1.Value;
    public override string DragLabel => "D";

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
    public Dictionary<uint, Vector3d>? PresetPoints { set => _cache.Preset = value; }
    public override string Title => "Скругление вершины";
    public override string Icon => "f_round_vertex_but.svg";
    protected override VertexRoundFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _v = p.Ref(Fields.Vertex());
        if (Editing) _v.Kept = 1;
        p.Number("Радиус", () => F.Radius, v => F.Radius = v);
    }

    public override Action<double>? DragValue => v => F.Radius = new ValueRef(v);
    public override double? DragCurrent => F.Radius.Value;
    public override string DragLabel => "R";

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
    public Dictionary<uint, Vector3d>? PresetPoints { set => _cache.Preset = value; }
    public override string Title => "Фаска угла";
    public override string Icon => "f_chamfer_vertex_but.svg";
    protected override VertexChamferFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _v = p.Ref(Fields.Vertex());
        if (Editing) _v.Kept = 1;
        p.Number("Размер", () => F.Distance, v => F.Distance = v);
    }

    public override Action<double>? DragValue => v => F.Distance = new ValueRef(v);
    public override double? DragCurrent => F.Distance.Value;
    public override string DragLabel => "D";

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

    public override Action<double>? DragValue => v => F.Thickness = new ValueRef(v);
    public override double? DragCurrent => F.Thickness.Value;
    public override string DragLabel => "T";
    /// <summary>Ручка оболочки: внутрь тела — положительная толщина, наружу — отрицательная (стенка наружу).</summary>
    public override bool SignedDrag => true;
    /// <summary>Открытые грани, выбранные в поле (для ручки).</summary>
    public IReadOnlyList<PickRef> FaceRefs => _faces?.Items ?? (IReadOnlyList<PickRef>)Array.Empty<PickRef>();

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
        p.Check("Насквозь", () => F.ThroughAll, v => F.ThroughAll = v);
        p.Number("Глубина", () => F.Depth, v => F.Depth = v, visible: () => !F.ThroughAll);
        p.Number("Диаметр головки", () => F.HeadDiameter, v => F.HeadDiameter = v, visible: () => F.Kind != HoleKind.Simple);
        p.Number("Глубина головки", () => F.HeadDepth, v => F.HeadDepth = v, visible: () => F.Kind == HoleKind.Counterbore);
        p.Number("Угол зенковки", () => F.HeadAngle, v => F.HeadAngle = v, "°", visible: () => F.Kind == HoleKind.Countersink);
    }

    protected override void SeedRefs() => Seed(_face, _pts);

    protected override void Push()
    {
        if (_face.Touched)
        {
            // u12: грань не найдена по номеру (предпросмотр перестроил деталь) — прежние центр и нормаль остаются
            if (_face.Items.Count == 0) { _faceCentre = null; _faceNormal = null; }
            if (_face.Items.Count > 0 && Refs.PlaneOf(_face.Items[0]) is { } pl)
            {
                _faceCentre = _face.Items[0] is { Kind: PickKind.Face, Owner: Part fp } fr && Refs.FaceCentre(fp, fr.Id) is { } fc ? fc : pl.Frame.Origin;
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
        _src = p.Ref(Fields.Features());
        if (Editing) _src.Kept = F.Sources.Count;
        _pl = p.Ref(Fields.Plane("Плоскость зеркала"));
        if (Editing) _pl.Kept = 1;
        p.Note("Операции (выбор в дереве) — отражаются они; без них — отражается и объединяется всё тело.");
    }
    private RefField _src = null!;

    protected override void SeedRefs() => Seed(_src, _pl);

    protected override void Push()
    {
        Fields.PushSources(_src, F.Sources);
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
        _src = p.Ref(Fields.Features());
        if (Editing) _src.Kept = F.Sources.Count;
        _dir = p.Ref(Fields.Line("Направление"));
        if (Editing) _dir.Kept = 1;
        p.Number("Шаг", () => _spacing, v => _spacing = v);
        p.Number("Количество", () => F.Count, v => F.Count = v, "шт");
        p.Check("Обратное направление", () => _flip, v => _flip = v);
    }

    private RefField _src = null!;
    protected override void SeedRefs() => Seed(_src, _dir);

    protected override void Push()
    {
        Fields.PushSources(_src, F.Sources);
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
        _src = p.Ref(Fields.Features());
        if (Editing) _src.Kept = F.Sources.Count;
        _axis = p.Ref(Fields.Line("Ось", optional: true));
        if (Editing) _axis.Kept = 1;
        p.Note("Без оси — ось Z через начало координат.");
        p.Number("Количество", () => F.Count, v => F.Count = v, "шт");
    }

    private RefField _src = null!;
    protected override void SeedRefs() => Seed(_src, _axis);

    protected override void Push()
    {
        Fields.PushSources(_src, F.Sources);
        if (!_axis.Touched) return;
        if (_axis.Items.Count > 0 && Refs.LineOf(_axis.Items[0]) is { } ln) { F.At = ln.At; F.Axis = ln.Dir; }
        else { F.At = Vector3d.Zero; F.Axis = Vector3d.UnitZ; }
    }
}

// ====================================================================== опорная плоскость (элемент, не операция)

public sealed class PlaneOp : Operation
{
    // u12 (замечание 14): СПОСОБ ПОСТРОЕНИЯ ПЛОСКОСТИ — выбор в панели (прежде только смещение от плоскости); у способов со
    // смещением и углом — ручка (тянут — смещение), у построенных по точкам — без ручки.
    private RefField _base = null!, _pts = null!, _line = null!;
    private ValueRef _offset = new(20), _angle = new(30);
    private bool _flip;
    private int _by;      // 0 смещение, 1 три точки, 2 прямая и точка, 3 параллельно через точку, 4 под углом, 5 перпендикулярно прямой
    private DatumPlane? _dp;
    public override string Title => "Плоскость";
    public override string Icon => "dat_plane_but.svg";
    public override bool SignedDrag => true;
    /// <summary>Ручка есть у способов со смещением (смещение) и углом (угол).</summary>
    public bool HasHandle => _by is 0 or 4;
    public static readonly string[] Methods = { "Смещение от плоскости", "Через три точки", "Через прямую и точку", "Параллельно через точку", "Под углом к плоскости", "Перпендикулярно прямой или кривой", "Посередине между плоскостями", "Касательно к грани в точке" };
    /// <summary>u13: способ, заданный кнопкой (плоскость посередине, касательная, нормально к кривой…); −1 — по выбору.</summary>
    public int InitialMethod = -1;
    private RefField _base2 = null!, _face = null!;

    protected override bool Start()
    {
        W.Session.BeginEdit(L(Title));
        if (InitialMethod >= 0) _by = InitialMethod;
        return true;
    }

    public override void Build(OpPanel p)
    {
        p.Choice("Способ", Methods, () => _by, v => { _by = v; });
        _base = p.Ref(new RefField { Label = "Плоскость", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true });
        _base2 = p.Ref(new RefField { Label = "Вторая плоскость", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true });
        _line = p.Ref(new RefField { Label = "Прямая (ось) или кривая", Filter = PickFilter.Edges | PickFilter.Datums | PickFilter.SketchCurves, Accept = r => Refs.IsLinear(r) || r.Kind is PickKind.Edge or PickKind.SketchCurve, Single = true, Optional = true });
        _face = p.Ref(new RefField { Label = "Кривая грань", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face && !Refs.IsPlanar(r), Single = true, Optional = true });
        _pts = p.Ref(new RefField { Label = "Точки", Filter = PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums, Accept = r => r.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint, Optional = true });
        p.Number("Смещение", () => _offset, v => _offset = v, visible: () => _by == 0);
        p.Number("Угол", () => _angle, v => _angle = v, "°", visible: () => _by == 4);
        p.Check("Обратное направление", () => _flip, v => _flip = v, visible: () => _by is 0 or 4);
        p.Note("Смещение — плоскость; три точки — точки; прямая и точка — прямая и точка; параллельно — плоскость и точка; под углом — плоскость и прямая в ней; перпендикулярно — прямая или кривая (и точка); посередине — две плоскости; касательно — кривая грань (и точка).");
    }

    public override void Prime()
    {
        // вторая плоскость — второй выбранной плоской ссылкой (выбор снят раскладкой по полям — взять до неё)
        var planes = W.ViewPort.Selection.Items.Where(Refs.IsPlanar).ToList();
        Seed(_base, _line, _pts, _face);
        if (planes.Count >= 2 && _base.Items.Count > 0 && planes.FirstOrDefault(x => x.Id != _base.Items[0].Id) is { IsEmpty: false } other) _base2.Take(new[] { other });
        // способ — по выбранному: три точки, прямая и точка, плоскость и точка
        if (InitialMethod >= 0) { }
        else if (_pts.Items.Count >= 3) _by = 1;
        else if (planes.Count >= 2) _by = 6;
        else if (_face.Items.Count > 0) _by = 7;
        else if (_line.Items.Count > 0 && _pts.Items.Count > 0) _by = Refs.IsLinear(_line.Items[0]) ? 2 : 5;
        else if (_base.Items.Count > 0 && _pts.Items.Count > 0) _by = 3;
        else if (_base.Items.Count > 0 && _line.Items.Count > 0) _by = 4;
        else if (_line.Items.Count > 0) _by = 5;
        foreach (var f in new[] { _base, _base2, _line, _pts, _face }) if (f.Text != null) f.Text.Text = f.Summary;
        Panel?.Refresh();
        Apply();
        RefreshStatus();
    }

    private (Frame3 F, DatumPlaneMode M, uint Src)? Compute()
    {
        var pts = _pts.Items.Select(Refs.PointOf).OfType<Vector3d>().ToList();
        var ln = _line.Items.Count > 0 ? Refs.LineOf(_line.Items[0]) : null;
        var pl = _base.Items.Count > 0 ? Refs.PlaneOf(_base.Items[0]) : null;
        double sgn = _flip ? -1 : 1;
        try
        {
            switch (_by)
            {
                case 0 when pl is { } b: return (b.Frame.OffsetAlongNormal(_offset.Value * sgn), DatumPlaneMode.OffsetFromFace, b.SourceId);
                case 1 when pts.Count >= 3: return (DatumBuilder.PlaneThroughPoints(pts[0], pts[1], pts[2]), DatumPlaneMode.ThreePoints, 0);
                case 2 when ln is { } l && pts.Count >= 1: return (DatumBuilder.PlaneThroughLineAndPoint(l.At, l.Dir, pts[0]), DatumPlaneMode.LineAndPoint, 0);
                case 3 when pl is { } b && pts.Count >= 1: return (DatumBuilder.PlaneParallelThroughPoint(b.Frame, pts[0]), DatumPlaneMode.ParallelThroughPoint, b.SourceId);
                case 4 when pl is { } b && ln is { } l: return (DatumBuilder.PlaneAtAngle(b.Frame, l.At, l.Dir, _angle.Value * sgn), DatumPlaneMode.AngleToFace, b.SourceId);
                case 5 when ln is { } l && Refs.IsLinear(_line.Items[0]): return (DatumBuilder.PlaneNormalToLine(l.Dir, pts.Count > 0 ? pts[0] : l.At), DatumPlaneMode.NormalToLine, 0);
                case 5 when _line.Items.Count > 0 && MoreFields.CurveOf(_line.Items[0]) is { } cv:
                {
                    // нормально к кривой: в точке (или в середине кривой), нормаль — касательная кривой
                    var at = pts.Count > 0 ? pts[0] : cv.PointAt(0.5 * (cv.T0 + cv.T1));
                    double t = AxisOp.ClosestT(cv, at);
                    return (DatumBuilder.PlaneNormalToLine(cv.TangentAt(t), cv.PointAt(t)), DatumPlaneMode.NormalToLine, 0);
                }
                case 6 when pl is { } a && _base2.Items.Count > 0 && Refs.PlaneOf(_base2.Items[0]) is { } b2:
                {
                    // посередине: параллельные — на полпути по нормали; пересекающиеся — биссектор через линию пересечения
                    var na = a.Frame.W.Normalized(); var nb = b2.Frame.W.Normalized();
                    if (Math.Abs(Math.Abs(Vector3d.Dot(na, nb)) - 1) < 1e-9)
                        return (Frame3.FromNormal(a.Frame.Origin + na * (Vector3d.Dot(b2.Frame.Origin - a.Frame.Origin, na) / 2), na), DatumPlaneMode.ParallelThroughPoint, a.SourceId);
                    var (at, _) = DatumBuilder.AxisOfPlanes(a.Frame, b2.Frame);
                    var n = (na - nb).Normalized();
                    return (Frame3.FromNormal(at, n), DatumPlaneMode.AngleToFace, a.SourceId);
                }
                case 7 when _face.Items.Count > 0 && MoreFields.FaceOf(_face.Items[0]) is { } fc:
                {
                    // касательно к грани: в выбранной точке (или в середине грани)
                    var src = GeometryAudit.AuthoritativeSurface(fc);
                    var p0 = pts.Count > 0 ? pts[0] : Refs.PointOf(_face.Items[0]) ?? fc.Loops[0].CoEdges[0].Start.P;
                    var q = src != null ? src.PointAt(src.ProjectToUV(p0)) : p0;
                    return (Frame3.FromNormal(q, fc.NormalAt(q).Normalized()), DatumPlaneMode.ParallelThroughPoint, _face.Items[0].Id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { W.ShowToast(L(Title) + ": " + ex.Message); }
        return null;
    }

    protected override void Apply()
    {
        if (Compute() is not { } c) return;
        if (_dp == null)
        {
            _dp = new DatumPlane(L("Плоскость{0}", Part.Planes.Count() + 1), c.F) { Mode = c.M, Offset = _by == 0 ? _offset.Value * (_flip ? -1 : 1) : 0 };
            if (c.Src != 0) _dp.References.Add(c.Src);
            Part.Add(_dp);
        }
        else
        {
            _dp.Frame = c.F; _dp.Mode = c.M;
            _dp.Offset = _by == 0 ? _offset.Value * (_flip ? -1 : 1) : 0;
            _dp.References.Clear();
            if (c.Src != 0) _dp.References.Add(c.Src);
            _dp.NotifyChanged(_dp);
        }
        W.ViewPort.MarkDirty();
        W.RefreshPlaneHandle();
    }

    /// <summary>Ручка: начало и нормаль базовой плоскости (смещение) — тянут вдоль нормали.</summary>
    public (Vector3d At, Vector3d Out)? HandleAt()
    {
        if (!HasHandle || _base.Items.Count == 0 || Refs.PlaneOf(_base.Items[0]) is not { } b) return null;
        var at = _base.Items[0] is { Kind: PickKind.Face, Owner: Part fp } fr && Refs.FaceCentre(fp, fr.Id) is { } fc ? fc : b.Frame.Origin;
        return (at, b.Frame.W.Normalized());
    }

    public override bool Commit()
    {
        if (_dp == null) { W.ShowToast(L("Выберите ссылки для способа «{0}»", L(Methods[_by]))); return false; }
        W.Session.CommitEdit();
        return true;
    }

    public override void Cancel()
    {
        // новая плоскость — убрать её; деталь не заменяется и не перестраивается целиком
        if (_dp != null) Part.Remove(_dp);
        W.Session.DiscardEdit();
    }

    public override Action<double>? DragValue => v =>
    {
        if (_by == 4) { _angle = new ValueRef(v); return; }
        _flip = false; _offset = new ValueRef(v);
    };
    public override double? DragCurrent => _by == 4 ? _angle.Value : _offset.Value * (_flip ? -1 : 1);
    public override string DragLabel => _by == 4 ? "∠" : "d";
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
            W.BeginNewSketch(Part, SketchFrame(sel, pl.Frame), pl.SourceId, pl.Name, Then);
            return false;
        }
        // u12 (замечание 13): выбрана цепочка рёбер (кромок) в одной плоскости — эскиз в этой плоскости
        if (Topo.CoplanarFrame(W.ViewPort.Selection.Items) is { } cf)
        {
            W.BeginNewSketch(Part, cf, 0, L("по рёбрам"), Then);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Плоскость эскиза на грани: начало — проекция начала координат детали на плоскость грани, чтобы размеры до нуля
    /// эскиза были размерами от начала детали (как в Creo).
    /// </summary>
    public static Frame3 SketchFrame(PickRef r, Frame3 f)
    {
        if (r.Kind != PickKind.Face) return f;
        var n = f.W.Normalized();
        return Frame3.FromNormal(n * Vector3d.Dot(f.Origin, n), n);
    }

    public override void Build(OpPanel p)
    {
        _pl = p.Ref(Fields.Plane());
        p.Note("Выберите опорную плоскость (в виде или в дереве) или плоскую грань тела.");
    }

    /// <summary>u12 (замечание 13): плоскость выбрана — сразу в эскиз, без кнопки «Начать эскиз».</summary>
    protected override void Apply()
    {
        if (_pl.Items.Count > 0 && Refs.PlaneOf(_pl.Items[0]) is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (ReferenceEquals(W.CurrentOperation, this)) W.CommitOperation(); });
    }

    public override bool Commit()
    {
        if (_pl.Items.Count == 0 || Refs.PlaneOf(_pl.Items[0]) is not { } pl)
        {
            W.ShowToast(L("Выберите плоскость или плоскую грань"));
            return false;
        }
        W.BeginNewSketch(Part, SketchFrame(_pl.Items[0], pl.Frame), pl.SourceId, pl.Name, Then);
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
        SweepFeature x => new SweepOp(x),
        LoftFeature x => new LoftOp(x),
        DraftFeature x => new DraftOp(x),
        RibFeature x => new RibOp(x),
        HelicalSweepFeature x => new HelicalSweepOp(x),
        MoveCopyFeature x => new MoveCopyOp(x),
        FaceEditFeature x => new FaceEditOp(x),
        PointsPatternFeature x => new PointsPatternOp(x),
        CosmeticThreadFeature x => new CosmeticThreadOp(x),
        FullRoundFeature x => new FullRoundOp(x),
        SurfaceFeature x => new SurfaceOp(x),
        QuiltEditFeature x => new QuiltEditOp(x),
        QuiltSolidFeature x => new QuiltSolidOp(x),
        BlendFeature x => new BlendOp(x),
        InsertBodyFeature x => new InsertBodyOp(x),
        FFPrimitiveFeature x => new FFPrimitiveOp(x),
        FFBooleanFeature x => new FFBooleanOp(x),
        FFSplitFeature x => new FFSplitOp(x),
        FFTransformFeature x => new FFTransformOp(x),
        FFMirrorFeature x => new FFMirrorOp(x),
        FFArrayFeature x => new FFArrayOp(x),
        FFAlignFaceFeature x => new FFAlignFaceOp(x),
        FFPipeFeature x => new FFPipeOp(x),
        FFImprintFeature x => new FFImprintOp(x),
        FFNurbsQuiltFeature x => new FFSurfaceOp(x),
        FFPlanarQuiltFeature x => new FFPlanarOp(x),
        FFStitchFeature x => new FFStitchOp(x),
        FFSubDFeature x => new SubDOp(x),
        _ => null,
    };
}
