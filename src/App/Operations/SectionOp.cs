using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Model.Features;
using Parametric3D.App.Viewport;
using Parametric3D.Model.Params;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

/// <summary>
/// k99 (замечания 3, 4): НАСТРОЙКА ПЛОСКОСТИ СЕЧЕНИЯ — основа (плоскость или плоская грань), начало (начало основы, центр
/// основы или указанная точка), смещение вдоль нормали основы, углы A и B (вокруг первой и второй осей основы). Ручки в виде
/// — по полю, в котором курсор: смещение — стрелка вдоль нормали, угол A — кольцо вокруг первой оси, B — вокруг второй.
/// Модель не меняется: «ОК» оставляет сечение, «Отмена» возвращает прежнее.
/// </summary>
public sealed class SectionOp : Operation
{
    private SectionState _s = null!;
    private SectionState? _before;
    private RefField _base = null!, _point = null!;
    /// <summary>Поле, к которому сейчас ручка: 0 — смещение, 1 — угол A, 2 — угол B.</summary>
    public int GizmoField { get; private set; }
    private double _g0;

    public override string Title => "Сечение";
    public override string Icon => "section_view_but.svg";
    public SectionState State => _s;

    protected override bool Start()
    {
        var v = W.ViewPort;
        _before = v.Section?.Clone();
        _s = v.Section?.Clone() ?? Default();
        v.ApplySection(_s);
        v.SectionHot = true;
        ShowGizmo();
        return true;
    }

    /// <summary>Сечение по умолчанию: основа — выбранная плоскость или плоскость XZ детали, начало — центр основы.</summary>
    public SectionState Default()
    {
        var s = new SectionState();
        var sel = W.ViewPort.Selection.Items.FirstOrDefault(Refs.IsPlanar);
        if (!sel.IsEmpty) s.Base = sel;
        else s.Base = new PickRef(Part.PlaneXZ.Id, PickKind.DatumPlane, Part, Part.PlaneXZ);
        return s;
    }

    public override void Build(OpPanel p)
    {
        _base = p.Ref(new RefField
        {
            Label = "Основа (Base plane)", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true,
            Describe = r => Refs.PlaneOf(r)?.Name ?? "?",
        });
        if (_s.Base is { } b) { _base.Items.Add(b); _base.Touched = false; }
        p.Check("Начало — начало основы (Origin – base plane)", () => _s.Origin == SectionState.OriginKind.BasePlane,
            on => _s.Origin = on ? SectionState.OriginKind.BasePlane : SectionState.OriginKind.MidPlane);
        p.Check("Начало — центр основы (Origin – mid-plane)", () => _s.Origin == SectionState.OriginKind.MidPlane,
            on => _s.Origin = on ? SectionState.OriginKind.MidPlane : SectionState.OriginKind.Point,
            enabled: () => _s.Origin != SectionState.OriginKind.BasePlane);
        _point = p.Ref(new RefField
        {
            Label = "Точка начала (Origin point)", Filter = PickFilter.Vertices | PickFilter.Datums | PickFilter.SketchPoints, Accept = r => Refs.PointOf(r) != null,
            Single = true, Optional = true, Enabled = () => _s.Origin == SectionState.OriginKind.Point,
        });
        p.Number("Смещение (Offset)", () => new ValueRef(_s.Offset), v => _s.Offset = v.Value, focused: () => SetGizmoField(0));
        p.Number("Угол A", () => new ValueRef(_s.AngleA), v => _s.AngleA = v.Value, unit: "°", focused: () => SetGizmoField(1));
        p.Number("Угол B", () => new ValueRef(_s.AngleB), v => _s.AngleB = v.Value, unit: "°", focused: () => SetGizmoField(2));
        p.Note(L("Угол A — поворот вокруг первой оси основы, угол B — вокруг второй. Ручка в виде — у поля, в котором курсор."));
    }

    protected override void Apply()
    {
        if (_base.Touched && _base.Items.Count > 0) { _s.Base = _base.Items[0]; _base.Touched = false; }
        if (_point.Touched && _point.Items.Count > 0 && Refs.PointOf(_point.Items[0]) is { } q) { _s.OriginPoint = q; _s.Origin = SectionState.OriginKind.Point; _point.Touched = false; }
        W.ViewPort.ApplySection(_s);
        ShowGizmo();
    }

    protected override void RequestLive() { W.ViewPort.ApplySection(_s); ShowGizmo(); }

    public void SetGizmoField(int k) { GizmoField = k; ShowGizmo(); }

    /// <summary>Ручка по полю: смещение — стрелка вдоль нормали основы; углы — кольцо вокруг оси основы.</summary>
    private void ShowGizmo()
    {
        var v = W.ViewPort;
        if (v.SectionPlane is not { } sp) { v.Gizmo = null; return; }
        var f = _s.BaseFrame();
        Vector3d u = f.U.Normalized(), vv = f.V.Normalized(), w = f.W.Normalized();
        v.Gizmo = GizmoField switch
        {
            1 => new GizmoSpec(GizmoMode.Rotate, sp.At, u, vv, w) { Mask = 1 << (int)GizmoPart.AxisX },
            2 => new GizmoSpec(GizmoMode.Rotate, sp.At, u, vv, w) { Mask = 1 << (int)GizmoPart.AxisY },
            _ => new GizmoSpec(GizmoMode.Move, sp.At, u, vv, w) { Mask = 1 << (int)GizmoPart.AxisZ },
        };
    }

    public override void GizmoStart(GizmoPart part) => _g0 = GizmoField switch { 1 => _s.AngleA, 2 => _s.AngleB, _ => _s.Offset };

    public override void GizmoMove(GizmoDrag d)
    {
        switch (GizmoField)
        {
            case 1: _s.AngleA = Math.Round(_g0 + d.AngleDeg, 2); break;
            case 2: _s.AngleB = Math.Round(_g0 + d.AngleDeg, 2); break;
            default: _s.Offset = Math.Round(_g0 + d.Delta.Z, 3); break;
        }
        GizmoValuesChanged();
        W.ViewPort.ApplySection(_s);
    }

    public override void GizmoEnd() => FieldChanged();

    private void Leave() { var v = W.ViewPort; v.Gizmo = null; v.SectionHot = false; }

    public override bool Commit() { Leave(); return true; }

    public override void Cancel()
    {
        Leave();
        if (_before != null) W.ViewPort.ApplySection(_before); else W.ViewPort.SetSection(null);
    }
}
