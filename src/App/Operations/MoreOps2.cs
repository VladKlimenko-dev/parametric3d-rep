using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// u13c: операции кнопок таблицы иконок — винтовая протяжка, перенос/копия, прямая правка граней, массивы по точкам,
// по кривой и заполнением, косметическая резьба.

public sealed class HelicalSweepOp : FeatureOp<HelicalSweepFeature>
{
    private RefField _sk = null!, _axis = null!;
    public HelicalSweepOp(HelicalSweepFeature? existing = null) : base(existing) { }
    public override string Title => "Винтовая протяжка";
    public override string Icon => "f_helical_sweep_but.svg";
    protected override HelicalSweepFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _sk = p.Ref(new RefField { Label = "Сечение", Filter = PickFilter.SketchCurves, Accept = Refs.IsSketch, Single = true, Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?") });
        if (Editing && F.SketchId != 0) _sk.Kept = 1;
        _axis = p.Ref(new RefField
        {
            Label = "Ось", Optional = true, Single = true,
            Filter = PickFilter.Datums | PickFilter.SketchCurves,
            Accept = r => r.Target is DatumAxis || r.Target is SkLine,
        });
        if (Editing && (F.AxisId != 0 || F.AxisEntityId != 0)) _axis.Kept = 1;
        p.Note("Без оси — осевая линия эскиза сечения. Винтовая линия идёт от центра сечения.");
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Number("Шаг", () => F.Pitch, v => F.Pitch = v);
        p.Number("Витков", () => F.Turns, v => F.Turns = v, "");
        p.Check("Левая", () => F.LeftHand, v => F.LeftHand = v);
    }

    protected override void SeedRefs() => Seed(_sk, _axis);

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

public sealed class MoveCopyOp : FeatureOp<MoveCopyFeature>
{
    private RefField _axis = null!;
    public bool StartCopy;
    public MoveCopyOp(MoveCopyFeature? existing = null) : base(existing) { }
    public override string Title => "Перенос и поворот";
    public override string Icon => "f_move_copy_but.svg";
    protected override MoveCopyFeature Create() => new() { Copy = StartCopy, Dx = new ValueRef(20) };

    public override void Build(OpPanel p)
    {
        p.Number("Сдвиг X", () => F.Dx, v => F.Dx = v);
        p.Number("Сдвиг Y", () => F.Dy, v => F.Dy = v);
        p.Number("Сдвиг Z", () => F.Dz, v => F.Dz = v);
        _axis = p.Ref(Fields.Line("Ось поворота", optional: true));
        if (Editing) _axis.Kept = 1;
        p.Number("Угол", () => F.Angle, v => F.Angle = v, "°");
        p.Check("Копия (объединить с исходным)", () => F.Copy, v => F.Copy = v);
    }

    protected override void SeedRefs() => Seed(_axis);
    public override Action<double>? DragValue => v => F.Dx = new ValueRef(v);
    public override double? DragCurrent => F.Dx.Value;
    public override string DragLabel => "X";

    protected override void Push()
    {
        if (_axis.Touched && _axis.Items.Count > 0 && Refs.LineOf(_axis.Items[0]) is { } ln) { F.AxisAt = ln.At; F.AxisDir = ln.Dir; }
    }
}

public sealed class FaceEditOp : FeatureOp<FaceEditFeature>
{
    private RefField _faces = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public FaceEditMode StartMode;
    public bool MoveFace;
    public FaceEditOp(FaceEditFeature? existing = null) : base(existing) { if (existing != null) StartMode = existing.Mode; }
    public override string Title => StartMode switch { FaceEditMode.Radius => "Новый радиус", FaceEditMode.Delete => "Удаление граней", _ => MoveFace ? "Перенос грани" : "Смещение граней" };
    public override string Icon => StartMode switch { FaceEditMode.Radius => "dir_new_radius_but.svg", FaceEditMode.Delete => "dir_delete_face_but.svg", _ => MoveFace ? "dir_move_face_but.svg" : "dir_offset_face_but.svg" };
    protected override FaceEditFeature Create() => new(L(Title)) { Mode = StartMode };

    public override void Build(OpPanel p)
    {
        _faces = p.Ref(MoreFields.Faces(StartMode == FaceEditMode.Delete ? "Скругления (грани)" : "Грани"));
        if (Editing) _faces.Kept = F.OnFaces.Count;
        if (StartMode == FaceEditMode.Offset) p.Number(MoveFace ? "Перенос" : "Смещение", () => F.Distance, v => F.Distance = v);
        if (StartMode == FaceEditMode.Radius) p.Number("Радиус", () => F.Radius, v => F.Radius = v);
        p.Note(StartMode switch
        {
            FaceEditMode.Delete => "Удаляются сопряжения (скругления); соседние грани сходятся в ребро.",
            FaceEditMode.Radius => "Круглые грани (цилиндр, скругление) получают новый радиус.",
            _ => "Грани смещаются по нормали; соседние продлеваются до встречи.",
        });
    }

    public override Action<double>? DragValue => StartMode switch
    {
        FaceEditMode.Offset => v => F.Distance = new ValueRef(v),
        FaceEditMode.Radius => v => F.Radius = new ValueRef(v),
        _ => null,
    };
    public override double? DragCurrent => StartMode == FaceEditMode.Radius ? F.Radius.Value : F.Distance.Value;
    public override string DragLabel => StartMode == FaceEditMode.Radius ? "R" : "d";

    protected override void SeedRefs() => Seed(_faces);

    protected override void Push()
    {
        if (_faces.Touched)
        {
            F.OnFaces.Clear(); F.OnFaceNames.Clear();
            F.OnFaces.AddRange(_cache.Resolve(_faces, Refs.PointOf));
        }
    }
}

public sealed class PointsPatternOp : FeatureOp<PointsPatternFeature>
{
    private RefField _src = null!, _sk = null!;
    public PointsPatternMode StartMode;
    public PointsPatternOp(PointsPatternFeature? existing = null) : base(existing) { if (existing != null) StartMode = existing.Mode; }
    public override string Title => StartMode switch { PointsPatternMode.Curve => "Массив по кривой", PointsPatternMode.Fill => "Массив заполнением", _ => "Массив по точкам" };
    public override string Icon => StartMode switch { PointsPatternMode.Curve => "f_pattern_curve_but.svg", PointsPatternMode.Fill => "f_pattern_fill_but.svg", _ => "f_pattern_points_but.svg" };
    protected override PointsPatternFeature Create() => new(L(Title)) { Mode = StartMode };

    public override void Build(OpPanel p)
    {
        _src = p.Ref(Fields.Features());
        if (Editing) _src.Kept = F.Sources.Count;
        _sk = p.Ref(new RefField
        {
            Label = StartMode switch { PointsPatternMode.Curve => "Кривая (эскиз)", PointsPatternMode.Fill => "Область (эскиз)", _ => "Точки (эскиз)" },
            Filter = PickFilter.SketchCurves | PickFilter.SketchPoints, Accept = Refs.IsSketch, Single = true,
            Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?"),
        });
        if (Editing && F.SketchId != 0) _sk.Kept = 1;
        if (StartMode == PointsPatternMode.Curve) p.Number("Количество", () => F.Count, v => F.Count = v, "шт");
        if (StartMode == PointsPatternMode.Fill)
        {
            p.Number("Шаг", () => F.Spacing, v => F.Spacing = v);
            p.Number("Отступ от края", () => F.Margin, v => F.Margin = v);
        }
        p.Note(StartMode switch
        {
            PointsPatternMode.Curve => "Копии — через равные доли длины кривой от её начала.",
            PointsPatternMode.Fill => "Сетка внутри замкнутой области от первой точки эскиза (или центра области).",
            _ => "Первая точка эскиза — место исходной операции, копии — в остальных точках.",
        });
    }

    protected override void SeedRefs() => Seed(_src, _sk);

    protected override void Push()
    {
        Fields.PushSources(_src, F.Sources);
        if (_sk.Items.Count > 0 && Refs.SketchOf(_sk.Items[0]) is { } sk) F.SketchId = sk.Id;
    }
}

public sealed class CosmeticThreadOp : FeatureOp<CosmeticThreadFeature>
{
    private RefField _faces = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public CosmeticThreadOp(CosmeticThreadFeature? existing = null) : base(existing) { }
    public override string Title => "Косметическая резьба";
    public override string Icon => "f_thread_but.svg";
    protected override CosmeticThreadFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _faces = p.Ref(new RefField { Label = "Цилиндрическая грань", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face, Single = true });
        if (Editing) _faces.Kept = F.OnFaces.Count;
        p.Number("Шаг", () => F.Pitch, v => F.Pitch = v);
        p.Number("Длина (0 — вся грань)", () => F.Length, v => F.Length = v);
        p.Note("Резьба — обозначение: тело не меняется; диаметр и вид берутся с грани.");
    }

    protected override void SeedRefs() => Seed(_faces);

    protected override void Push()
    {
        if (_faces.Touched)
        {
            F.OnFaces.Clear(); F.OnFaceNames.Clear();
            F.OnFaces.AddRange(_cache.Resolve(_faces, Refs.PointOf));
        }
    }
}
