using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// u13j: ПОВЕРХНОСТИ FREEFORM — линейчатая, сетка кривых, заплатка, мостик между краями, продление, обращение нормали,
// правка опорных точек, вес, узел (NURBS-поверхность шага), плоская поверхность, сшивка в тело.

public enum FFSurfMode { Ruled, Network, Patch, Blend, Extend, Flip, CvEdit, CvWeight, Knot }

public sealed class FFSurfaceOp : FeatureOp<FFNurbsQuiltFeature>
{
    public FFSurfMode Mode;
    private RefField _a = null!, _b = null!;
    private ValueRef _v1 = new(1), _v2 = new(0), _v3 = new(0), _i = new(0), _j = new(0);
    private int _side, _dir;
    private readonly Dictionary<string, NurbsSurface> _base = new();
    private string? _baseKey;
    private string _info = "";
    private string? _error;

    public FFSurfaceOp(FFNurbsQuiltFeature? existing = null) : base(existing) { }
    public override string Title => Mode switch
    {
        FFSurfMode.Network => "Сетка кривых", FFSurfMode.Patch => "Заплатка", FFSurfMode.Blend => "Мостик между краями",
        FFSurfMode.Extend => "Продление поверхности", FFSurfMode.Flip => "Обратить нормаль", FFSurfMode.CvEdit => "Правка опорных точек поверхности",
        FFSurfMode.CvWeight => "Вес опорной точки поверхности", FFSurfMode.Knot => "Вставить узел поверхности", _ => "Линейчатая поверхность",
    };
    public override string Icon => Mode switch
    {
        FFSurfMode.Network => "ff_surf_network_but.svg", FFSurfMode.Patch => "ff_surf_patch_but.svg", FFSurfMode.Blend => "ff_surf_blend_but.svg",
        FFSurfMode.Extend => "ff_surf_extend_but.svg", FFSurfMode.Flip => "ff_surf_flip_but.svg", FFSurfMode.CvEdit => "ff_cv_edit_but.svg",
        FFSurfMode.CvWeight => "ff_cv_weight_but.svg", FFSurfMode.Knot => "ff_insert_knot_but.svg", _ => "ff_surf_ruled_but.svg",
    };
    bool Edit => Mode is FFSurfMode.Extend or FFSurfMode.Flip or FFSurfMode.CvEdit or FFSurfMode.CvWeight or FFSurfMode.Knot;

    protected override FFNurbsQuiltFeature Create() => new() { QuiltName = L("Поверхность {0}", Part.Quilts.Count + 1) };

    public override void Build(OpPanel p)
    {
        _b = new RefField { Label = "", Optional = true };
        switch (Mode)
        {
            case FFSurfMode.Ruled:
                _a = p.Ref(CurveRefs.Curve("Первая кривая")); _b = p.Ref(CurveRefs.Curve("Вторая кривая"));
                break;
            case FFSurfMode.Network:
                _a = p.Ref(CurveRefs.Curve("Кривые U", single: false)); _b = p.Ref(CurveRefs.Curve("Кривые V", single: false));
                p.Note("Не меньше двух кривых в каждом направлении; поверхность проходит через сетку (Гордон).");
                break;
            case FFSurfMode.Patch:
                _a = p.Ref(CurveRefs.Curve("Контур (кривые)", single: false));
                p.Note("Одна замкнутая кривая или кривые, замыкающие контур; четыре кривые — стороны.");
                break;
            case FFSurfMode.Blend:
                _a = p.Ref(new RefField { Label = "Первый край (ребро)", Filter = PickFilter.Edges, Accept = r => r.Kind == PickKind.Edge, Single = true });
                _b = p.Ref(new RefField { Label = "Второй край (ребро)", Filter = PickFilter.Edges, Accept = r => r.Kind == PickKind.Edge, Single = true });
                _v1 = new(1);
                p.Number("Натяжение", () => _v1, v => _v1 = v, "");
                p.Note("Касание (G1) к граням краёв.");
                break;
            default:
                _a = p.Ref(QuiltFields.Quilts("Поверхность (NURBS)", single: true));
                break;
        }
        switch (Mode)
        {
            case FFSurfMode.Extend:
                _v1 = new(10);
                p.Segments("Край", new[] { "U0", "U1", "V0", "V1" }, () => _side, v => _side = v);
                p.Number("Длина", () => _v1, v => _v1 = v);
                break;
            case FFSurfMode.CvEdit:
                _v1 = new(5);
                p.Number("Опорная точка i", () => _i, v => _i = v, ""); p.Number("Опорная точка j", () => _j, v => _j = v, "");
                p.Number("Сдвиг X", () => _v2, v => _v2 = v); p.Number("Сдвиг Y", () => _v3, v => _v3 = v); p.Number("Сдвиг Z", () => _v1, v => _v1 = v);
                break;
            case FFSurfMode.CvWeight:
                _v1 = new(2);
                p.Number("Опорная точка i", () => _i, v => _i = v, ""); p.Number("Опорная точка j", () => _j, v => _j = v, "");
                p.Number("Вес", () => _v1, v => _v1 = v, "");
                break;
            case FFSurfMode.Knot:
                _v1 = new(0.5);
                p.Segments("Направление", new[] { "U", "V" }, () => _dir, v => _dir = v);
                p.Number("Параметр (0…1)", () => _v1, v => _v1 = v, "");
                break;
        }
        if (Editing) { _a.Kept = 1; _b.Kept = 1; }
    }

    protected override void SeedRefs()
    {
        var sel = W.ViewPort.Selection.Items.ToList();
        if (Mode is FFSurfMode.Ruled or FFSurfMode.Blend)
        {
            var c = sel.Where(_a.Accept).ToList();
            if (c.Count > 0) _a.Take(new[] { c[0] });
            if (c.Count > 1) _b.Take(new[] { c[1] });
        }
        else Seed(_a, _b);
    }

    protected override bool MissingRequiredExtra => F.Def.Nu < 2;

    protected override string? BuildError => _error ?? F?.Error;

    public override void RefreshStatus()
    {
        base.RefreshStatus();
        if (Panel != null && string.IsNullOrEmpty(Panel.Status.Text) && _info.Length > 0) Panel.Status.Text = _info;
    }

    /// <summary>Исходная NURBS выбранного квилта (снимается при первом выборе — до предпросмотра правки).</summary>
    NurbsSurface? BaseSurface()
    {
        if (_a.Items.Count == 0) return _baseKey != null && _base.TryGetValue(_baseKey, out var kept) ? kept : null;
        if (QuiltFields.KeyOf(_a.Items[0]) is not { } key || _a.Items[0].Owner is not Part p) return null;
        if (!_base.ContainsKey(key) && p.QuiltOfFace(_a.Items[0].Id) is { } q)
        {
            if (q.Nurbs == null) throw new RebuildException(OperationOutcome.Unsupported, L("Поверхность не NURBS (у неё нет опорных точек)"));
            _base[key] = q.Nurbs;
        }
        _baseKey = key;
        return _base.GetValueOrDefault(key);
    }

    List<IReadOnlyList<Vector3d>> Curves(RefField f) => f.Items.Select(CurveRefs.PointsOf).OfType<List<Vector3d>>().Cast<IReadOnlyList<Vector3d>>().ToList();

    protected override void Push()
    {
        _error = null; _info = "";
        try
        {
            NurbsSurface? s = null;
            var prm = Parameters;
            switch (Mode)
            {
                case FFSurfMode.Ruled:
                {
                    var c = Curves(_a).Concat(Curves(_b)).ToList();
                    if (c.Count < 2) { _error = L("Выберите две кривые"); return; }
                    s = FFSurfaces.Ruled(c[0], c[1]);
                    break;
                }
                case FFSurfMode.Network:
                {
                    var u = Curves(_a); var v = Curves(_b);
                    if (u.Count < 2 || v.Count < 2) { _error = L("Нужно не меньше двух кривых U и двух кривых V"); return; }
                    s = FFSurfaces.Network(u, v);
                    double dev = FFSurfaces.Deviation(s, u.Concat(v).SelectMany(c => FFSurfaces.Resample(c, 16)));
                    _info = L("Отклонение от кривых: {0} мм", dev.ToString("G3"));
                    break;
                }
                case FFSurfMode.Patch:
                {
                    var c = Curves(_a);
                    if (c.Count == 0) { _error = L("Выберите контур"); return; }
                    s = FFSurfaces.Patch(c);
                    break;
                }
                case FFSurfMode.Blend:
                {
                    if (_a.Items.Count == 0 || _b.Items.Count == 0) { _error = L("Выберите два края"); return; }
                    var (ea, fa, pa) = EdgeOf(_a.Items[0]); var (eb, fb, pb) = EdgeOf(_b.Items[0]);
                    var faA = Model.FFEdges.PickFace(ea, f => pa.QuiltOfFace(f.Id) != null, (eb.V0.P + eb.V1.P) / 2);
                    var faB = Model.FFEdges.PickFace(eb, f => pb.QuiltOfFace(f.Id) != null, (ea.V0.P + ea.V1.P) / 2);
                    var (ptsA, awayA) = Model.FFEdges.Away(ea, faA);
                    var (ptsB, awayB) = Model.FFEdges.Away(eb, faB);
                    s = FFSurfaces.Blend(ptsA, awayA, ptsB, awayB, _v1.Resolve(prm));
                    break;
                }
                default:
                {
                    var b0 = BaseSurface();
                    if (b0 == null) { _error = L("Выберите поверхность FreeForm (NURBS)"); return; }
                    F.ReplacesKey = _baseKey;
                    var d = NurbsDef.Of(b0);
                    switch (Mode)
                    {
                        case FFSurfMode.Extend: s = FFSurfaces.Extend(b0, _side, _v1.Resolve(prm)); break;
                        case FFSurfMode.Flip: s = FFSurfaces.Flip(d).Surface; break;
                        case FFSurfMode.CvEdit:
                        case FFSurfMode.CvWeight:
                        {
                            int i = (int)Math.Round(_i.Resolve(prm)), j = (int)Math.Round(_j.Resolve(prm));
                            if (i < 0 || i >= d.Nu || j < 0 || j >= d.Nv) { _error = L("Опорная точка: i 0…{0}, j 0…{1}", d.Nu - 1, d.Nv - 1); return; }
                            if (Mode == FFSurfMode.CvEdit) d.Cp[i, j] += new Vector3d(_v2.Resolve(prm), _v3.Resolve(prm), _v1.Resolve(prm));
                            else
                            {
                                double wv = _v1.Resolve(prm);
                                if (!(wv > 1e-6)) { _error = L("Вес должен быть больше нуля"); return; }
                                d.W[i, j] = wv;
                            }
                            s = d.Surface;
                            _info = L("Опорных точек: {0}×{1}", d.Nu, d.Nv);
                            ShowNet(d);
                            break;
                        }
                        default:
                        {
                            double t = _v1.Resolve(prm);
                            if (!(t > 1e-6 && t < 1 - 1e-6)) { _error = L("Параметр — между 0 и 1"); return; }
                            s = _dir == 0 ? b0.InsertKnotU(t) : b0.InsertKnotV(t);
                            _info = L("Опорных точек: {0}×{1}", s.CountU, s.CountV);
                            break;
                        }
                    }
                    break;
                }
            }
            if (s != null) { F.Def = NurbsDef.Of(s); if (_info.Length == 0) _info = L("Опорных точек: {0}×{1}", s.CountU, s.CountV); }
        }
        catch (RebuildException ex) { _error = ex.Message; }
        catch (ArgumentException ex) { _error = ex.Message; }
    }

    (Kernel.Topology.TEdge E, Kernel.Topology.TFace? F, Part P) EdgeOf(PickRef r)
    {
        if (r.Owner is Part p && p.FindEdge(r.Id) is { } e) return (e, null, p);
        throw new RebuildException(OperationOutcome.InvalidInput, L("Край не найден"));
    }

    void ShowNet(NurbsDef d)
    {
        var lines = new List<(Vector3d, Vector3d, Vector4)>();
        var c = new Vector4(0.95f, 0.55f, 0.15f, 1);
        for (int i = 0; i < d.Nu; i++) for (int j = 0; j < d.Nv; j++)
        {
            if (i + 1 < d.Nu) lines.Add((d.Cp[i, j], d.Cp[i + 1, j], c));
            if (j + 1 < d.Nv) lines.Add((d.Cp[i, j], d.Cp[i, j + 1], c));
        }
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

public sealed class FFPlanarOp : FeatureOp<FFPlanarQuiltFeature>
{
    private RefField _curve = null!;
    public FFPlanarOp(FFPlanarQuiltFeature? existing = null) : base(existing) { }
    public override string Title => "Плоская поверхность";
    public override string Icon => "ff_surf_planar_but.svg";
    protected override FFPlanarQuiltFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _curve = p.Ref(CurveRefs.Curve("Замкнутая плоская кривая"));
        if (Editing) _curve.Kept = 1;
    }

    protected override void SeedRefs() => Seed(_curve);

    protected override void Push()
    {
        if (!_curve.Touched || _curve.Items.Count == 0) return;
        if (CurveRefs.PointsOf(_curve.Items[0]) is { Count: >= 3 } pts) { F.Boundary.Clear(); F.Boundary.AddRange(pts); }
    }
}

public sealed class FFStitchOp : FeatureOp<FFStitchFeature>
{
    private RefField _q = null!;
    public FFStitchOp(FFStitchFeature? existing = null) : base(existing) { }
    public override string Title => "Сшить в тело";
    public override string Icon => "ff_surf_stitch_but.svg";
    protected override FFStitchFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _q = p.Ref(QuiltFields.Quilts("Поверхности", single: false));
        if (Editing) _q.Kept = F.Sources.Count;
        p.Note("Поверхности должны замыкать объём (края совпадают).");
    }

    protected override void SeedRefs() => Seed(_q);

    protected override void Push()
    {
        if (!_q.Touched) return;
        F.Sources.Clear();
        foreach (var r in _q.Items) if (QuiltFields.KeyOf(r) is { } k && !F.Sources.Contains(k)) F.Sources.Add(k);
    }
}
