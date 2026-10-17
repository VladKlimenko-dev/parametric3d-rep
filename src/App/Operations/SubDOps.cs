using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// u13k: SUBD FREEFORM — клетка-брусок (новое тело), выдавить грани клетки, петля рёбер, складка, уровень сглаживания,
// в NURBS. Грани и рёбра тела SubD указывают на грани и рёбра клетки (подписи «subd{k}»).

public enum SubDMode { Box, Extrude, EdgeLoop, Crease, Level, ToNurbs }

internal static class SubDRefs
{
    /// <summary>Тело SubD и клетка по выбранной грани или ребру тела.</summary>
    public static (string Body, SubDCage Cage)? CageOf(PickRef r)
    {
        if (r.Owner is not Part p || p.BodyNameOf(r.Id) is not { } n || !p.CagesByBody.TryGetValue(n, out var c)) return null;
        return (n, c);
    }

    public static bool IsSubD(PickRef r) => r.Kind is PickKind.Face or PickKind.Edge && CageOf(r) != null;

    /// <summary>Грань клетки по грани тела.</summary>
    public static int CageFace(PickRef r) => r.Owner is Part p && p.FindFace(r.Id) is { } f ? SubDCage.FaceOfTag(f.Tag) : -1;

    /// <summary>Ребро клетки по ребру тела (между двумя гранями клетки).</summary>
    public static (int, int)? CageEdge(PickRef r, SubDCage cage)
    {
        if (r.Owner is not Part p || p.FindEdge(r.Id) is not { } e || e.FaceA is not { } fa || e.FaceB is not { } fb) return null;
        int k1 = SubDCage.FaceOfTag(fa.Tag), k2 = SubDCage.FaceOfTag(fb.Tag);
        return k1 == k2 ? null : cage.SharedEdge(k1, k2);
    }

    public static RefField Faces(string label) => new()
    {
        Label = label, Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face && IsSubD(r),
        Describe = r => L("Грань клетки {0}", CageFace(r)),
    };
    public static RefField Edges(string label, bool single) => new()
    {
        Label = label, Filter = PickFilter.Edges, Single = single, Accept = r => r.Kind == PickKind.Edge && CageOf(r) is { } c && CageEdge(r, c.Cage) != null,
        Describe = r => CageOf(r) is { } c && CageEdge(r, c.Cage) is { } e ? L("Ребро клетки {0}–{1}", e.Item1, e.Item2) : "?",
    };
    public static RefField Body(string label) => new()
    {
        Label = label, Filter = PickFilter.Faces, Single = true, Accept = r => r.Kind == PickKind.Face && IsSubD(r),
        Describe = r => CageOf(r) is { } c ? L("Тело «{0}»", c.Body) : "?",
    };
}

public sealed class SubDOp : FeatureOp<FFSubDFeature>
{
    public SubDMode Mode;
    private RefField _a = null!;
    private ValueRef _w = new(40), _d = new(30), _h = new(20), _lvl = new(2), _dist = new(10), _t = new(0.5);
    private int _crease;            // 0 — поставить складку, 1 — убрать
    private (string Body, SubDCage Cage)? _base;
    private readonly Dictionary<uint, int> _faceIdx = new();
    private readonly Dictionary<uint, (int, int)> _edgeIdx = new();
    private string? _error;

    public SubDOp(FFSubDFeature? existing = null) : base(existing) { }
    public override string Title => Mode switch
    {
        SubDMode.Extrude => "Выдавить грани клетки", SubDMode.EdgeLoop => "Петля рёбер", SubDMode.Crease => "Складка",
        SubDMode.Level => "Уровень сглаживания", SubDMode.ToNurbs => "SubD в NURBS", _ => "SubD-клетка",
    };
    public override string Icon => Mode switch
    {
        SubDMode.Extrude => "subd_extrude_but.svg", SubDMode.EdgeLoop => "subd_edge_loop_but.svg", SubDMode.Crease => "subd_crease_but.svg",
        SubDMode.Level => "subd_smooth_level_but.svg", SubDMode.ToNurbs => "subd_to_nurbs_but.svg", _ => "subd_box_but.svg",
    };

    protected override FFSubDFeature Create()
    {
        var f = new FFSubDFeature { ToNurbs = Mode == SubDMode.ToNurbs };
        if (Mode == SubDMode.Box) f.Cage = SubDCage.Box(Part.ConstructionPlane, 40, 30, 20);
        return f;
    }

    public override void Build(OpPanel p)
    {
        switch (Mode)
        {
            case SubDMode.Box:
                _a = new RefField { Label = "", Optional = true };
                p.Number("Ширина", () => _w, v => _w = v); p.Number("Глубина", () => _d, v => _d = v); p.Number("Высота", () => _h, v => _h = v);
                p.Number("Уровень сглаживания", () => _lvl, v => _lvl = v, "");
                p.Note("Клетка — брусок на плоскости построения; тело — её подразделение (Катмулл — Кларк).");
                break;
            case SubDMode.Extrude:
                _a = p.Ref(SubDRefs.Faces("Грани клетки"));
                p.Number("Расстояние", () => _dist, v => _dist = v);
                break;
            case SubDMode.EdgeLoop:
                _a = p.Ref(SubDRefs.Edges("Ребро (петля — поперёк)", single: true));
                p.Number("Положение (0…1)", () => _t, v => _t = v, "");
                break;
            case SubDMode.Crease:
                _a = p.Ref(SubDRefs.Edges("Рёбра клетки", single: false));
                p.Segments("Складка", new[] { L("Поставить"), L("Убрать") }, () => _crease, v => _crease = v);
                break;
            case SubDMode.Level:
                _a = p.Ref(SubDRefs.Body("Тело SubD"));
                p.Number("Уровень сглаживания", () => _lvl, v => _lvl = v, "");
                break;
            default:
                _a = p.Ref(SubDRefs.Body("Тело SubD"));
                p.Note("Каждый четырёхугольник клетки — NURBS-лоскут через точки подразделения; клетка больше не правится.");
                break;
        }
    }

    protected override void SeedRefs() { if (Mode != SubDMode.Box) Seed(_a); }
    protected override bool MissingRequiredExtra => _error != null && Mode != SubDMode.Box;
    protected override string? BuildError => _error ?? F?.Error;

    public override Action<double>? DragValue => Mode switch
    {
        SubDMode.Extrude => v => _dist = new ValueRef(v),
        SubDMode.Box => v => _w = new ValueRef(v),
        _ => null,
    };
    public override double? DragCurrent => Mode == SubDMode.Extrude ? _dist.Value : _w.Value;
    public override string DragLabel => Mode == SubDMode.Extrude ? "d" : "W";

    protected override void Push()
    {
        _error = null;
        var prm = Parameters;
        try
        {
            if (Mode == SubDMode.Box)
            {
                var c = SubDCage.Box(Part.ConstructionPlane, _w.Resolve(prm), _d.Resolve(prm), _h.Resolve(prm));
                if (!(_w.Resolve(prm) > Tol.Weld && _d.Resolve(prm) > Tol.Weld && _h.Resolve(prm) > Tol.Weld)) { _error = L("Размеры должны быть больше нуля"); return; }
                c.Level = Math.Clamp((int)Math.Round(_lvl.Resolve(prm)), 0, 5);
                F.Cage = c; F.Body = null;
                return;
            }
            // исходная клетка тела — при первом выборе (до предпросмотра)
            if (_a.Items.Count > 0 && _base == null && SubDRefs.CageOf(_a.Items[0]) is { } b0) _base = b0;
            if (_base is not { } bs) { _error = L("Выберите тело SubD"); return; }
            foreach (var r in _a.Items)
            {
                if (!_faceIdx.ContainsKey(r.Id) && r.Kind == PickKind.Face) _faceIdx[r.Id] = SubDRefs.CageFace(r);
                if (!_edgeIdx.ContainsKey(r.Id) && r.Kind == PickKind.Edge && SubDRefs.CageEdge(r, bs.Cage) is { } e) _edgeIdx[r.Id] = e;
            }
            F.Body = bs.Body;
            var cage = bs.Cage;
            switch (Mode)
            {
                case SubDMode.Extrude:
                {
                    var faces = _a.Items.Select(r => _faceIdx.GetValueOrDefault(r.Id, -1)).Where(i => i >= 0).Distinct().ToList();
                    if (faces.Count == 0) { _error = L("Выберите грани клетки"); return; }
                    F.Cage = cage.Extrude(faces, _dist.Resolve(prm));
                    break;
                }
                case SubDMode.EdgeLoop:
                {
                    if (_a.Items.Count == 0 || !_edgeIdx.TryGetValue(_a.Items[0].Id, out var e)) { _error = L("Выберите ребро клетки"); return; }
                    F.Cage = cage.EdgeLoop(e.Item1, e.Item2, _t.Resolve(prm));
                    break;
                }
                case SubDMode.Crease:
                {
                    var c = cage.Clone();
                    foreach (var r in _a.Items) if (_edgeIdx.TryGetValue(r.Id, out var e)) { if (_crease == 0) c.Creases.Add(e); else c.Creases.Remove(e); }
                    F.Cage = c;
                    break;
                }
                case SubDMode.Level:
                {
                    var c = cage.Clone(); c.Level = Math.Clamp((int)Math.Round(_lvl.Resolve(prm)), 0, 5);
                    F.Cage = c;
                    break;
                }
                default:
                    F.Cage = cage.Clone(); F.ToNurbs = true;
                    break;
            }
            ShowCage(F.Cage);
        }
        catch (RebuildException ex) { _error = ex.Message; }
    }

    void ShowCage(SubDCage c)
    {
        var col = new Vector4(0.35f, 0.75f, 1f, 1);
        var lines = new List<(Vector3d, Vector3d, Vector4)>();
        foreach (var (a, b) in c.EdgeFaces().Keys) lines.Add((c.V[a], c.V[b], c.Creases.Contains((a, b)) ? new Vector4(1f, 0.4f, 0.3f, 1) : col));
        W.ViewPort.SetAnnotations(lines);
    }

    public override bool Commit()
    {
        if (_error != null) { W.ShowToast(_error); return false; }
        W.ViewPort.SetAnnotations(Array.Empty<(Vector3d, Vector3d, Vector4)>());
        return base.Commit();
    }

    public override void Cancel()
    {
        W.ViewPort.SetAnnotations(Array.Empty<(Vector3d, Vector3d, Vector4)>());
        base.Cancel();
    }
}
