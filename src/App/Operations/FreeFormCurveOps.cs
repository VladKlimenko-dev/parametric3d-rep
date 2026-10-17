using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// u13i: КРИВЫЕ FREEFORM — NURBS по опорным точкам и через точки, спираль, эквидистанта, скругление углов, соединение,
// продление, мостик G1/G2, перестроение, кривая из рёбер, изопараметрическая, обрезка, правка опорных точек, вес, узел.
// Кривая — опорная кривая детали (DatumCurve) без параметров: шаг фиксируется сразу (как в Plasticity); исходную опорную
// кривую команды «скругление», «продление», «обрезка», «перестроение» и правки точек меняют на месте.

public enum FFCurveMode { Nurbs, Spiral, Offset, Fillet, Join, Extend, Bridge, Rebuild, Extract, Isoparm, Trim, CvEdit, CvWeight, InsertKnot }

internal static class CurveRefs
{
    public static bool IsCurve(PickRef r) =>
        r.Target is DatumCurve or Line3DItem || (r.Target is SkEntity se && se is not SkPoint) || r.Kind == PickKind.Edge || (r.Kind == PickKind.SketchCurve && Refs.SketchOf(r) != null);

    public static RefField Curve(string label, bool single = true, bool optional = false) => new()
    {
        Label = label, Filter = PickFilter.Datums | PickFilter.SketchCurves | PickFilter.Edges, Accept = IsCurve, Single = single, Optional = optional,
        Describe = r => r.Target is DatumCurve dc ? dc.Name : r.Kind == PickKind.Edge ? L("Ребро #{0}", r.Id) : r.Target is SkEntity ? L("Кривая эскиза") : r.Target is Line3DItem l ? l.Name : "?",
    };

    /// <summary>Точки кривой ссылки в мировых координатах (опорная кривая, линия, кривая эскиза, ребро).</summary>
    public static List<Vector3d>? PointsOf(PickRef r)
    {
        switch (r.Target)
        {
            case DatumCurve dc: return dc.Points.ToList();
            case Line3DItem ln: return new List<Vector3d> { ln.A, ln.B };
            case SkEntity se when se is not SkPoint && Refs.SketchOf(r) is { } sk:
                try { var l = new List<Vector3d>(); se.ToCurve3(sk.Plane).Tessellate(l, true); return l; } catch { return null; }
        }
        if (r.Kind == PickKind.Edge && r.Owner is Part p && p.FindEdge(r.Id) is { } e)
        {
            var l = new List<Vector3d>();
            if (e.CoEdges.Count > 0) e.CoEdges[0].Tessellate(l, true);
            else l.AddRange(FFCurves.Sample(e.Curve, 64));
            return l;
        }
        if (Refs.SketchOf(r) is { } s2)
        {
            var parts = s2.Entities.Where(x => x is not SkPoint && x.Role == SkRole.Object)
                .Select(x => { var l = new List<Vector3d>(); try { x.ToCurve3(s2.Plane).Tessellate(l, true); } catch { } return (IReadOnlyList<Vector3d>)l; })
                .Where(l => l.Count >= 2).ToList();
            if (parts.Count == 1) return parts[0].ToList();
            if (parts.Count > 1) try { return FFCurves.Join(parts, 1e-4); } catch { return parts[0].ToList(); }
        }
        return null;
    }

    /// <summary>Нормаль плоскости кривой: эскиза — его плоскость; иначе — по точкам (если плоская), иначе null.</summary>
    public static Vector3d? PlaneNormal(PickRef r, IReadOnlyList<Vector3d> pts)
    {
        if (Refs.SketchOf(r) is { } sk) return sk.Plane.W;
        if (pts.Count < 3) return null;
        var c = pts.Aggregate(Vector3d.Zero, (a, b) => a + b) / pts.Count;
        Vector3d n = Vector3d.Zero;
        for (int i = 1; i < pts.Count; i++) n += Vector3d.Cross(pts[i - 1] - c, pts[i] - c);
        if (n.LengthSquared < 1e-18) return null;
        n.Normalize();
        return pts.All(p => Math.Abs(Vector3d.Dot(p - c, n)) < 1e-6 * Math.Max(1, (p - c).Length) + 1e-6) ? n : null;
    }
}

public sealed class FFCurveOp : Operation
{
    public FFCurveMode Mode;
    private RefField _a = null!, _b = null!;
    private ValueRef _v1 = new(5), _v2 = new(0), _v3 = new(0), _v4 = new(0), _deg = new(3), _idx = new(0);
    private bool _closed, _through, _arc, _left, _g2, _replace = true;
    private int _ends = 2, _dir, _keep;
    private DatumCurve? _dc;
    private DatumCurve? _edited;
    private (List<Vector3d> Pts, List<Vector3d>? Cv, List<double>? W, List<double>? K, int Deg)? _backup;
    private readonly List<(DatumCurve C, bool Visible)> _hidden = new();
    private string? _error;
    private string _info = "";

    public override string Title => Mode switch
    {
        FFCurveMode.Spiral => "Спираль", FFCurveMode.Offset => "Смещение кривой", FFCurveMode.Fillet => "Скругление углов кривой",
        FFCurveMode.Join => "Соединить кривые", FFCurveMode.Extend => "Продление кривой", FFCurveMode.Bridge => "Мостик между кривыми",
        FFCurveMode.Rebuild => "Перестроение кривой", FFCurveMode.Extract => "Кривая из рёбер", FFCurveMode.Isoparm => "Изопараметрическая кривая",
        FFCurveMode.Trim => "Обрезка кривой", FFCurveMode.CvEdit => "Правка опорных точек", FFCurveMode.CvWeight => "Вес опорной точки",
        FFCurveMode.InsertKnot => "Вставить узел", _ => "NURBS-кривая",
    };
    public override string Icon => Mode switch
    {
        FFCurveMode.Spiral => "ff_curve_spiral_but.svg", FFCurveMode.Offset => "ff_curve_offset_but.svg", FFCurveMode.Fillet => "ff_curve_fillet_but.svg",
        FFCurveMode.Join => "ff_curve_join_but.svg", FFCurveMode.Extend => "ff_curve_extend_but.svg", FFCurveMode.Bridge => "ff_curve_bridge_but.svg",
        FFCurveMode.Rebuild => "ff_curve_rebuild_but.svg", FFCurveMode.Extract => "ff_curve_extract_but.svg", FFCurveMode.Isoparm => "ff_curve_isoparm_but.svg",
        FFCurveMode.Trim => "ff_trim_curve_but.svg", FFCurveMode.CvEdit => "ff_cv_edit_but.svg", FFCurveMode.CvWeight => "ff_cv_weight_but.svg",
        FFCurveMode.InsertKnot => "ff_insert_knot_but.svg", _ => "ff_curve_nurbs_but.svg",
    };

    /// <summary>Режимы, меняющие опорную кривую на месте.</summary>
    bool InPlace => Mode is FFCurveMode.Fillet or FFCurveMode.Extend or FFCurveMode.Trim or FFCurveMode.Rebuild or FFCurveMode.CvEdit or FFCurveMode.CvWeight or FFCurveMode.InsertKnot;

    protected override bool Start() { W.Session.BeginEdit(L(Title)); return true; }

    public override void Build(OpPanel p)
    {
        _b = new RefField { Label = "", Optional = true };
        switch (Mode)
        {
            case FFCurveMode.Nurbs:
                _a = p.Ref(MoreFields.Points("Точки по порядку", optional: false));
                p.Check("Через точки (интерполяция)", () => _through, v => _through = v);
                p.Number("Степень", () => _deg, v => _deg = v, "");
                p.Check("Замкнутая", () => _closed, v => _closed = v);
                p.Note("Без «через точки» точки — опорные (кривая их не проходит, кроме концов).");
                break;
            case FFCurveMode.Spiral:
                _a = p.Ref(FFFields.Point("Центр (иначе — начало плоскости построения)"));
                _v1 = new(20); _v2 = new(20); _v3 = new(10); _v4 = new(3);
                p.Number("Радиус начала", () => _v1, v => _v1 = v); p.Number("Радиус конца", () => _v2, v => _v2 = v);
                p.Number("Шаг", () => _v3, v => _v3 = v); p.Number("Витков", () => _v4, v => _v4 = v, "");
                p.Check("Левая", () => _left, v => _left = v);
                p.Note("Ось — нормаль плоскости построения; шаг 0 — плоская спираль.");
                break;
            case FFCurveMode.Offset:
                _a = p.Ref(CurveRefs.Curve("Кривая"));
                _v1 = new(5);
                p.Number("Расстояние", () => _v1, v => _v1 = v);
                p.Note("В плоскости кривой (иначе — плоскости построения); знак — сторона.");
                break;
            case FFCurveMode.Fillet:
                _a = p.Ref(CurveRefs.Curve("Кривая"));
                _v1 = new(5);
                p.Number("Радиус", () => _v1, v => _v1 = v);
                break;
            case FFCurveMode.Join:
                _a = p.Ref(CurveRefs.Curve("Кривые", single: false));
                _v1 = new(0.01);
                p.Number("Допуск зазора", () => _v1, v => _v1 = v);
                p.Check("Скрыть исходные опорные кривые", () => _replace, v => _replace = v);
                break;
            case FFCurveMode.Extend:
                _a = p.Ref(CurveRefs.Curve("Кривая"));
                _v1 = new(10);
                p.Number("Длина", () => _v1, v => _v1 = v);
                p.Segments("Концы", new[] { L("Начало"), L("Конец"), L("Оба") }, () => _ends, v => _ends = v);
                p.Check("Дугой (по кривизне)", () => _arc, v => _arc = v);
                break;
            case FFCurveMode.Bridge:
                _a = p.Ref(CurveRefs.Curve("Первая кривая"));
                _b = p.Ref(CurveRefs.Curve("Вторая кривая"));
                _v1 = new(1);
                p.Segments("Непрерывность", new[] { "G1", "G2" }, () => _g2 ? 1 : 0, v => _g2 = v == 1);
                p.Number("Натяжение", () => _v1, v => _v1 = v, "");
                break;
            case FFCurveMode.Rebuild:
                _a = p.Ref(CurveRefs.Curve("Кривая"));
                _v1 = new(8);
                p.Number("Опорных точек", () => _v1, v => _v1 = v, "");
                p.Number("Степень", () => _deg, v => _deg = v, "");
                break;
            case FFCurveMode.Extract:
                _a = p.Ref(new RefField { Label = "Рёбра", Filter = PickFilter.Edges, Accept = r => r.Kind == PickKind.Edge });
                p.Note("Рёбра, касающиеся концами, сшиваются в одну кривую.");
                break;
            case FFCurveMode.Isoparm:
                _a = p.Ref(new RefField { Label = "Грань", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face, Single = true });
                _v1 = new(0.5);
                p.Segments("Направление", new[] { "U", "V" }, () => _dir, v => _dir = v);
                p.Number("Положение (0…1)", () => _v1, v => _v1 = v, "");
                break;
            case FFCurveMode.Trim:
                _a = p.Ref(CurveRefs.Curve("Кривая"));
                _b = p.Ref(new RefField { Label = "Режущая кривая или плоскость", Filter = PickFilter.Datums | PickFilter.SketchCurves | PickFilter.Edges | PickFilter.Faces, Accept = r => CurveRefs.IsCurve(r) || Refs.IsPlanar(r), Single = true });
                p.Segments("Оставить", new[] { L("Начало"), L("Конец") }, () => _keep, v => _keep = v);
                break;
            case FFCurveMode.CvEdit:
                _a = p.Ref(CurveRefs.Curve("Кривая (опорная)"));
                _v1 = new(5);
                p.Number("Номер опорной точки", () => _idx, v => _idx = v, "");
                p.Number("Сдвиг X", () => _v1, v => _v1 = v); p.Number("Сдвиг Y", () => _v2, v => _v2 = v); p.Number("Сдвиг Z", () => _v3, v => _v3 = v);
                p.Note("Кривая без опорных точек сначала перестраивается (8 точек).");
                break;
            case FFCurveMode.CvWeight:
                _a = p.Ref(CurveRefs.Curve("Кривая (опорная)"));
                _v1 = new(2);
                p.Number("Номер опорной точки", () => _idx, v => _idx = v, "");
                p.Number("Вес", () => _v1, v => _v1 = v, "");
                break;
            case FFCurveMode.InsertKnot:
                _a = p.Ref(CurveRefs.Curve("Кривая (опорная)"));
                _v1 = new(0.5); _v2 = new(1);
                p.Number("Параметр (0…1)", () => _v1, v => _v1 = v, "");
                p.Number("Сколько раз", () => _v2, v => _v2 = v, "");
                break;
        }
    }

    public override void Prime()
    {
        if (Mode == FFCurveMode.Bridge)
        {
            var sel = W.ViewPort.Selection.Items.Where(CurveRefs.IsCurve).ToList();
            if (sel.Count > 0) _a.Take(new[] { sel[0] });
            if (sel.Count > 1) _b.Take(new[] { sel[1] });
        }
        else Seed(_a, _b);
        foreach (var f in new[] { _a, _b }) if (f.Text != null) f.Text.Text = f.Summary;
        Panel?.Refresh(); Apply(); RefreshStatus();
    }

    protected override string? BuildError => _error;

    List<Vector3d> Src(RefField f)
    {
        // правка на месте — от исходной формы (до предпросмотра)
        if (ReferenceEquals(f, _a) && _edited != null && _backup is { } b) return b.Pts.ToList();
        return f.Items.Count > 0 && CurveRefs.PointsOf(f.Items[0]) is { Count: >= 2 } pts ? pts : throw new RebuildException(OperationOutcome.InvalidInput, L("Выберите: {0}", L(f.Label).ToLowerInvariant()));
    }

    /// <summary>Опорная кривая первого поля (для правки на месте), иначе null.</summary>
    DatumCurve? SrcCurve => _a.Items.Count > 0 ? _a.Items[0].Target as DatumCurve : null;

    /// <summary>Построить: (точки, NURBS или null).</summary>
    (List<Vector3d> Pts, SplineCurve3? Spline, string Source) Compute()
    {
        var prm = Parameters;
        switch (Mode)
        {
            case FFCurveMode.Nurbs:
            {
                var q = _a.Items.Select(Refs.PointOf).OfType<Vector3d>().ToList();
                if (q.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, L("Выберите: {0}", L("точки по порядку")));
                int deg = Math.Clamp((int)Math.Round(_deg.Resolve(prm)), 1, 5);
                if (_closed) return (CurveOp.ThroughPoints(q, true, true), null, "closed through points");
                var sp = _through ? FFCurves.Interpolate(q, deg) : new SplineCurve3(q, Math.Min(deg, q.Count - 1));
                return (FFCurves.Sample(sp, Math.Max(64, q.Count * 24)), sp, _through ? "NURBS through points" : "NURBS by control points");
            }
            case FFCurveMode.Spiral:
            {
                var fr = Part.ConstructionPlane;
                if (_a.Items.Count > 0 && Refs.PointOf(_a.Items[0]) is { } c) fr = new Frame3(c, fr.U, fr.V);
                return (FFCurves.Spiral(fr, _v1.Resolve(prm), _v2.Resolve(prm), _v3.Resolve(prm), _v4.Resolve(prm), _left), null, "spiral");
            }
            case FFCurveMode.Offset:
            {
                var s = Src(_a);
                var n = CurveRefs.PlaneNormal(_a.Items[0], s) ?? Part.ConstructionPlane.W;
                return (FFCurves.Offset(s, n, _v1.Resolve(prm)), null, "offset");
            }
            case FFCurveMode.Fillet:
            {
                var pts = FFCurves.FilletCorners(Src(_a), _v1.Resolve(prm), out int k);
                if (k == 0) throw new RebuildException(OperationOutcome.InvalidInput, L("Нет углов, куда помещается радиус"));
                _info = L("Скруглено углов: {0}", k);
                return (pts, null, "fillet corners");
            }
            case FFCurveMode.Join:
            {
                var parts = _a.Items.Select(CurveRefs.PointsOf).OfType<List<Vector3d>>().Cast<IReadOnlyList<Vector3d>>().ToList();
                return (FFCurves.Join(parts, Math.Max(1e-6, _v1.Resolve(prm))), null, "join");
            }
            case FFCurveMode.Extend:
            {
                var s = Src(_a);
                return (FFCurves.Extend(s, _v1.Resolve(prm), _ends != 1, _ends != 0, _arc), null, "extend");
            }
            case FFCurveMode.Bridge:
                return (FFCurves.Bridge(Src(_a), Src(_b), _g2, _v1.Resolve(prm)), null, _g2 ? "bridge G2" : "bridge G1");
            case FFCurveMode.Rebuild:
            {
                var s = Src(_a);
                var sp = FFCurves.Fit(s, Math.Max(2, (int)Math.Round(_v1.Resolve(prm))), Math.Clamp((int)Math.Round(_deg.Resolve(prm)), 1, 5));
                var pts = FFCurves.Sample(sp, Math.Max(64, sp.ControlPoints.Count * 24));
                _info = L("Отклонение от исходной: {0} мм", FFCurves.Deviation(s, pts).ToString("G3"));
                return (pts, sp, "rebuilt");
            }
            case FFCurveMode.Extract:
            {
                var parts = _a.Items.Select(CurveRefs.PointsOf).OfType<List<Vector3d>>().Cast<IReadOnlyList<Vector3d>>().ToList();
                if (parts.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, L("Выберите: {0}", L("рёбра")));
                var pts = parts.Count == 1 ? parts[0].ToList() : FFCurves.Join(parts, 1e-4);
                // одно ребро-сплайн — с его опорными точками
                SplineCurve3? sp = _a.Items.Count == 1 && _a.Items[0].Owner is Part ep && ep.FindEdge(_a.Items[0].Id)?.Curve is SplineCurve3 es ? es : null;
                return (pts, sp, "extracted edges");
            }
            case FFCurveMode.Isoparm:
            {
                if (_a.Items.Count == 0 || _a.Items[0].Owner is not Part fp || fp.FindFace(_a.Items[0].Id) is not { } face)
                    throw new RebuildException(OperationOutcome.InvalidInput, L("Выберите: {0}", L("грань")));
                return (FFCurves.Isoparm(face, _dir == 1, Math.Clamp(_v1.Resolve(prm), 0, 1)), null, _dir == 1 ? "isoparm V" : "isoparm U");
            }
            case FFCurveMode.Trim:
            {
                var s = Src(_a);
                if (_b.Items.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, L("Выберите: {0}", L("режущая кривая или плоскость")));
                var cut = _b.Items[0];
                if (CurveRefs.IsCurve(cut) && cut.Kind != PickKind.Face && CurveRefs.PointsOf(cut) is { Count: >= 2 } cp)
                {
                    var bb = Bounds3.Empty; foreach (var q in s) bb.Add(q);
                    return (FFCurves.TrimByCurve(s, cp, _keep == 0, Math.Max(1e-3, 1e-3 * (bb.Max - bb.Min).Length)), null, "trimmed");
                }
                if (Refs.PlaneOf(cut) is { } pl) return (FFCurves.TrimByPlane(s, pl.Frame.Origin, pl.Frame.W, _keep == 0), null, "trimmed");
                throw new RebuildException(OperationOutcome.InvalidInput, L("Режущая ссылка — не кривая и не плоскость"));
            }
            default:
            {
                // правки опорных точек: исходная (или перестроенная) NURBS
                var src = SrcCurve ?? throw new RebuildException(OperationOutcome.InvalidInput, L("Выберите опорную кривую"));
                var (bp, bcv, bw, bk, bd) = _backup!.Value;
                var sp = bcv is { Count: >= 2 } ? new SplineCurve3(bcv, bd, bw, bk) : FFCurves.Fit(bp, Math.Min(8, Math.Max(2, bp.Count)), 3);
                switch (Mode)
                {
                    case FFCurveMode.CvEdit:
                    {
                        int i = (int)Math.Round(_idx.Resolve(prm));
                        if (i < 0 || i >= sp.ControlPoints.Count) throw new RebuildException(OperationOutcome.InvalidInput, L("Номер опорной точки: 0…{0}", sp.ControlPoints.Count - 1));
                        var cps = sp.ControlPoints.ToList(); cps[i] += new Vector3d(_v1.Resolve(prm), _v2.Resolve(prm), _v3.Resolve(prm));
                        sp = new SplineCurve3(cps, sp.Degree, sp.Weights, sp.Knots);
                        _info = L("Опорных точек: {0}", cps.Count);
                        break;
                    }
                    case FFCurveMode.CvWeight:
                    {
                        int i = (int)Math.Round(_idx.Resolve(prm));
                        if (i < 0 || i >= sp.ControlPoints.Count) throw new RebuildException(OperationOutcome.InvalidInput, L("Номер опорной точки: 0…{0}", sp.ControlPoints.Count - 1));
                        double wv = _v1.Resolve(prm);
                        if (!(wv > 1e-6)) throw new RebuildException(OperationOutcome.InvalidInput, L("Вес должен быть больше нуля"));
                        var ws = sp.Weights.ToList(); ws[i] = wv;
                        sp = new SplineCurve3(sp.ControlPoints, sp.Degree, ws, sp.Knots);
                        break;
                    }
                    default:
                    {
                        double t = _v1.Resolve(prm); int times = Math.Clamp((int)Math.Round(_v2.Resolve(prm)), 1, sp.Degree);
                        var u = sp.WithUnitKnots();
                        for (int k = 0; k < times; k++) u = u.InsertKnot(t);
                        sp = u;
                        _info = L("Опорных точек: {0}", sp.ControlPoints.Count);
                        break;
                    }
                }
                return (FFCurves.Sample(sp, Math.Max(64, sp.ControlPoints.Count * 24)), sp, src.Source);
            }
        }
    }

    protected override void Apply()
    {
        _error = null; _info = "";
        // правка на месте: запомнить исходную опорную кривую (для «Отмена»)
        var src = InPlace ? SrcCurve : null;
        if (!ReferenceEquals(src, _edited))
        {
            RestoreEdited();
            _edited = src;
            if (src != null) _backup = (src.Points.ToList(), src.Control?.ToList(), src.Weights?.ToList(), src.Knots?.ToList(), src.Degree);
        }
        if (Mode is FFCurveMode.CvEdit or FFCurveMode.CvWeight or FFCurveMode.InsertKnot && _edited == null)
        {
            _error = L("Выберите опорную кривую (правка точек — только у опорных кривых)");
            return;
        }
        try
        {
            var (pts, sp, source) = Compute();
            if (pts.Count < 2) throw new RebuildException(OperationOutcome.InvalidInput, L("Кривая пуста"));
            var target = _edited ?? _dc;
            if (target == null)
            {
                string baseName = L("Кривая{0}", Part.Children.OfType<DatumCurve>().Count() + 1);
                _dc = target = new DatumCurve(baseName, pts, source);
                if (sp != null) _dc.SetSpline(sp, pts.Count - 1);
                Part.Add(_dc);
            }
            else
            {
                target.Points.Clear(); target.Points.AddRange(pts); target.Source = source;
                if (sp != null) target.SetSpline(sp, pts.Count - 1); else { target.Control = null; target.Weights = null; target.Knots = null; }
                target.NotifyChanged(target);
            }
            if (Mode == FFCurveMode.Join && _replace) HideSources();
            // опорные точки правки — ломаной поверх модели
            if (Mode is FFCurveMode.CvEdit or FFCurveMode.CvWeight or FFCurveMode.InsertKnot or FFCurveMode.Rebuild or FFCurveMode.Nurbs && target.Control is { Count: >= 2 } cvs)
            {
                var col = new Vector4(0.95f, 0.55f, 0.15f, 1);
                W.ViewPort.SetAnnotations(Enumerable.Range(1, target.Control!.Count - 1).Select(i => (target.Control[i - 1], target.Control[i], col)));
            }
        }
        catch (RebuildException ex) { _error = ex.Message; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _error = ex.Message; }
        if (Panel != null && _info.Length > 0 && _error == null) Panel.Status.Text = _info;
        W.ViewPort.MarkDirty();
    }

    public override void RefreshStatus()
    {
        base.RefreshStatus();
        if (Panel != null && _error == null && _info.Length > 0 && string.IsNullOrEmpty(Panel.Status.Text)) Panel.Status.Text = _info;
    }

    void HideSources()
    {
        foreach (var r in _a.Items)
            if (r.Target is DatumCurve c && !ReferenceEquals(c, _dc) && !_hidden.Any(h => ReferenceEquals(h.C, c)))
            { _hidden.Add((c, c.Visible)); c.Visible = false; }
    }

    void RestoreEdited()
    {
        if (_edited == null || _backup is not { } b) return;
        _edited.Points.Clear(); _edited.Points.AddRange(b.Pts);
        _edited.Control = b.Cv; _edited.Weights = b.W; _edited.Knots = b.K; _edited.Degree = b.Deg;
        _edited.NotifyChanged(_edited);
    }

    /// <summary>Готовая кривая (для проверок).</summary>
    public DatumCurve? Result => _edited ?? _dc;

    public override bool Commit()
    {
        if (_error != null || Result == null) { W.ShowToast(_error ?? L("Кривая не построена — проверьте ссылки")); return false; }
        W.ViewPort.SetAnnotations(Array.Empty<(Vector3d, Vector3d, Vector4)>());
        W.Session.CommitEdit(); return true;
    }

    public override void Cancel()
    {
        W.ViewPort.SetAnnotations(Array.Empty<(Vector3d, Vector3d, Vector4)>());
        RestoreEdited();
        foreach (var (c, v) in _hidden) c.Visible = v;
        if (_dc != null) Part.Remove(_dc);
        W.Session.DiscardEdit();
        W.ViewPort.MarkDirty();
    }
}

public sealed class FFPipeOp : FeatureOp<FFPipeFeature>
{
    private RefField _curve = null!;
    public FFPipeOp(FFPipeFeature? existing = null) : base(existing) { }
    public override string Title => "Труба вдоль кривой";
    public override string Icon => "ff_pipe_but.svg";
    protected override FFPipeFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _curve = p.Ref(new RefField { Label = "Кривая (опорная или эскиз)", Filter = PickFilter.Datums | PickFilter.SketchCurves, Single = true,
            Accept = r => r.Target is DatumCurve || Refs.IsSketch(r), Describe = r => r.Target is DatumCurve dc ? dc.Name : L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?") });
        if (Editing) _curve.Kept = 1;
        p.Number("Радиус", () => F.Radius, v => F.Radius = v);
        p.Number("Внутренний радиус (0 — сплошная)", () => F.Inner, v => F.Inner = v);
        p.Check("Вырез", () => F.Cut, v => F.Cut = v);
    }

    protected override void SeedRefs() => Seed(_curve);
    public override Action<double>? DragValue => v => F.Radius = new ValueRef(v);
    public override double? DragCurrent => F.Radius.Value;
    public override string DragLabel => "R";

    protected override void Push()
    {
        if (!_curve.Touched || _curve.Items.Count == 0) return;
        var r = _curve.Items[0];
        if (r.Target is DatumCurve dc) { F.CurveName = dc.Name; F.SketchId = 0; }
        else if (Refs.SketchOf(r) is { } sk) { F.SketchId = sk.Id; F.CurveName = ""; }
    }
}

public sealed class FFImprintOp : FeatureOp<FFImprintFeature>
{
    private RefField _face = null!, _curve = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public FFImprintOp(FFImprintFeature? existing = null) : base(existing) { }
    public override string Title => "Отпечаток кривой";
    public override string Icon => "ff_imprint_but.svg";
    protected override FFImprintFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _face = p.Ref(new RefField { Label = "Плоская грань", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face && Refs.IsPlanar(r), Single = true });
        _curve = p.Ref(new RefField { Label = "Кривая (опорная или эскиз)", Filter = PickFilter.Datums | PickFilter.SketchCurves, Single = true,
            Accept = r => r.Target is DatumCurve || Refs.IsSketch(r), Describe = r => r.Target is DatumCurve dc ? dc.Name : L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?") });
        if (Editing) { _face.Kept = 1; _curve.Kept = 1; }
        p.Note("Грань делится по кривой (форма тела не меняется). Незамкнутая кривая должна пересекать грань от края до края.");
    }

    protected override void SeedRefs() => Seed(_face, _curve);

    protected override void Push()
    {
        if (_face.Touched) { F.OnFaces.Clear(); F.OnFaceNames.Clear(); F.OnFaces.AddRange(_cache.Resolve(_face, Refs.PointOf)); }
        if (_curve.Touched && _curve.Items.Count > 0)
        {
            var r = _curve.Items[0];
            if (r.Target is DatumCurve dc) { F.CurveName = dc.Name; F.SketchId = 0; }
            else if (Refs.SketchOf(r) is { } sk) { F.SketchId = sk.Id; F.CurveName = ""; }
        }
    }
}
