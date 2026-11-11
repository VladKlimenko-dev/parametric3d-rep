using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Threads;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

/// <summary>
/// РЕЗЬБА: круглая грань; косметическая (по умолчанию — обозначение, тело не меняется) или вырез профиля впадины из таблицы
/// резьб винтовой протяжкой; тип — из таблицы (с выбранной гранью — только типы, чей малый диаметр меньше диаметра грани,
/// лучшие первыми); начало (без выбора — торец грани); длина — на длину (0 — вся грань) или до выбранного; плавный выход;
/// левая. Пока панель открыта, винтовая линия и профиль выбранного типа видны и у выреза (предпросмотр).
/// </summary>
public sealed partial class CosmeticThreadOp : FeatureOp<CosmeticThreadFeature>
{
    private RefField _face = null!, _start = null!, _end = null!;
    private readonly RefCache<Vector3d> _faceCache = new(), _startCache = new(), _endCache = new();
    public CosmeticThreadOp(CosmeticThreadFeature? existing = null) : base(existing) { }
    public override string Title => "Резьба";
    public override string Icon => "f_thread_but.svg";
    protected override CosmeticThreadFeature Create() => new();

    private static RefField PointField(string label, Func<bool>? enabled = null) => new()
    {
        Label = label, Optional = true, Single = true, Enabled = enabled,
        Filter = PickFilter.Faces | PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums,
        Accept = r => r.Kind is PickKind.Face or PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint,
    };

    public override void Build(OpPanel p)
    {
        F.ShowPreview = true;
        _face = p.Ref(new RefField { Label = "Цилиндрическая грань", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face, Single = true });
        if (Editing) _face.Kept = F.OnFaces.Count;
        p.Check("Косметическая", () => F.Cosmetic, v => F.Cosmetic = v, tip: "Обозначение: тело не меняется. Снять — вырез профиля из таблицы резьб.");
        p.List("Тип резьбы", () => Types().Select(t => t.Display).ToList(), () => Types().FindIndex(t => t.Id == F.TypeId),
            i => { var ts = Types(); if (i < ts.Count) { F.TypeId = ts[i].Id; F.LeftHand = ts[i].StdLeft; } },
            visible: () => ThreadDb.All.Count > 0);
        p.Info(TypeInfo);
        p.Number("Шаг", () => F.Pitch, v => F.Pitch = v, visible: () => F.Spec == null);
        _start = p.Ref(PointField("Начало резьбы"));
        if (Editing && F.StartAt != null) _start.Kept = 1;
        p.Choice("Длина", new[] { "На длину", "До выбранного" }, () => (int)F.Mode, v => F.Mode = (ThreadLength)v);
        _end = p.Ref(PointField("Конец резьбы", () => F.Mode == ThreadLength.UpTo));
        if (Editing && F.EndAt != null) _end.Kept = 1;
        p.Number("Длина резьбы", ShownLength, SetLength, enabled: () => F.Mode == ThreadLength.Blind, scrub: 1);
        p.Check("Плавный выход", () => F.SmoothExit, v => F.SmoothExit = v, enabled: () => !F.Cosmetic,
            tip: "Точный плавный выход наружной однозаходной трапециевидной резьбы на отдельном цилиндре. Длина включает выход; остальные случаи отклоняются без изменения детали.");
        p.Check("Левая", () => F.LeftHand, v => F.LeftHand = v);
        p.Note("Предпросмотр — винтовая линия и профиль, без выреза тела. Точный вырез — после ОК.");
        p.Note("Резец заходит до начала участка с плавным врезанием. Длина 0 — вся грань.");
    }

    public override void RefreshStatus() { base.RefreshStatus(); Panel?.Refresh(); }

    protected override void SeedRefs() => Seed(_face);

    protected override void Push()
    {
        if (_face.Touched)
        {
            F.OnFaces.Clear(); F.OnFaceNames.Clear();
            F.OnFaces.AddRange(_faceCache.Resolve(_face, Refs.PointOf));
            // новая грань: тип, которого нет среди подходящих, — на лучший подходящий
            if (FaceDia() is { } fd && ThreadDb.Candidates(fd.D, fd.Inner) is { Count: > 0 } c && (F.Spec == null || !c.Contains(F.Spec)))
            { F.TypeId = c[0].Id; F.LeftHand = c[0].StdLeft; }
        }
        if (_start.Touched) F.StartAt = _startCache.Resolve(_start, Refs.PointOf).Select(v => (Vector3d?)v).FirstOrDefault();
        if (_end.Touched) F.EndAt = _endCache.Resolve(_end, Refs.PointOf).Select(v => (Vector3d?)v).FirstOrDefault();
    }

    public override bool Commit()
    {
        F.ShowPreview = false;
        if (base.Commit()) return true;
        F.ShowPreview = true;
        return false;
    }

    public override void Cancel()
    {
        if (F != null) F.ShowPreview = false;
        base.Cancel();
    }
}
