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

// u13e: опорные система координат и кривые (через точки, по уравнению, проекция на грань), соосное и радиальное отверстия.

public sealed class CsysOp : Operation
{
    private RefField _pt = null!, _pl = null!, _x = null!;
    private int _by;
    private ValueRef _dx = new(0), _dy = new(0), _dz = new(0), _rot = new(0);
    private DatumCsys? _cs;
    public override string Title => "Система координат";
    public override string Icon => "dat_csys_but.svg";
    public static readonly string[] Methods = { "В точке (оси детали)", "На плоскости или грани", "Три плоскости" };

    protected override bool Start() { W.Session.BeginEdit(L(Title)); return true; }

    public override void Build(OpPanel p)
    {
        p.Choice("Способ", Methods, () => _by, v => _by = v);
        _pt = p.Ref(MoreFields.Points("Начало (точка)"));
        _pl = p.Ref(new RefField { Label = "Плоскости", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Optional = true });
        _x = p.Ref(new RefField { Label = "Направление X", Filter = PickFilter.Edges | PickFilter.Datums | PickFilter.SketchCurves, Accept = Refs.IsLinear, Single = true, Optional = true });
        p.Number("Сдвиг X", () => _dx, v => _dx = v);
        p.Number("Сдвиг Y", () => _dy, v => _dy = v);
        p.Number("Сдвиг Z", () => _dz, v => _dz = v);
        p.Number("Поворот вокруг Z", () => _rot, v => _rot = v, "°");
    }

    public override void Prime()
    {
        Seed(_pt, _pl, _x);
        if (_pl.Items.Count >= 3) _by = 2; else if (_pl.Items.Count > 0) _by = 1;
        foreach (var f in new[] { _pt, _pl, _x }) if (f.Text != null) f.Text.Text = f.Summary;
        Panel?.Refresh(); Apply(); RefreshStatus();
    }

    public Frame3? Compute()
    {
        var pts = _pt.Items.Select(Refs.PointOf).OfType<Vector3d>().ToList();
        var pls = _pl.Items.Select(Refs.PlaneOf).Where(x => x != null).Select(x => x!.Value.Frame).ToList();
        Vector3d o, u, w;
        switch (_by)
        {
            case 1 when pls.Count > 0:
                o = pts.Count > 0 ? pts[0] - pls[0].W * Vector3d.Dot(pts[0] - pls[0].Origin, pls[0].W) : pls[0].Origin;
                if (_pl.Items[0] is { Kind: PickKind.Face, Owner: Part fp } fr && pts.Count == 0 && Refs.FaceCentre(fp, fr.Id) is { } fc) o = fc;
                w = pls[0].W; u = pls[0].U;
                break;
            case 2 when pls.Count >= 3:
            {
                var (n1, n2, n3) = (pls[0].W, pls[1].W, pls[2].W);
                double det = Vector3d.Dot(n1, Vector3d.Cross(n2, n3));
                if (Math.Abs(det) < 1e-9) { W.ShowToast(L("Плоскости не пересекаются в точке")); return null; }
                double d1 = Vector3d.Dot(n1, pls[0].Origin), d2 = Vector3d.Dot(n2, pls[1].Origin), d3 = Vector3d.Dot(n3, pls[2].Origin);
                o = (d1 * Vector3d.Cross(n2, n3) + d2 * Vector3d.Cross(n3, n1) + d3 * Vector3d.Cross(n1, n2)) / det;
                w = n3; u = n1 - w * Vector3d.Dot(n1, w);
                if (u.LengthSquared < 1e-12) u = Math.Abs(w.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
                break;
            }
            default:
                if (pts.Count == 0 && pls.Count == 0) return null;
                o = pts.Count > 0 ? pts[0] : pls[0].Origin; w = Vector3d.UnitZ; u = Vector3d.UnitX;
                break;
        }
        if (_x.Items.Count > 0 && Refs.LineOf(_x.Items[0]) is { } xl)
        {
            var xu = xl.Dir - w * Vector3d.Dot(xl.Dir, w);
            if (xu.LengthSquared > 1e-12) u = xu;
        }
        w = w.Normalized(); u = (u - w * Vector3d.Dot(u, w)).Normalized();
        double a = _rot.Value * Math.PI / 180;
        var v0 = Vector3d.Cross(w, u);
        var ur = u * Math.Cos(a) + v0 * Math.Sin(a);
        var vr = Vector3d.Cross(w, ur);
        o += ur * _dx.Value + vr * _dy.Value + w * _dz.Value;
        return new Frame3(o, ur, vr);
    }

    protected override void Apply()
    {
        if (Compute() is not { } f) return;
        if (_cs == null) { _cs = new DatumCsys(L("СК{0}", Part.Children.OfType<DatumCsys>().Count() + 1), f); Part.Add(_cs); }
        else { _cs.Frame = f; _cs.NotifyChanged(_cs); }
        W.ViewPort.MarkDirty();
    }

    public override bool Commit()
    {
        if (_cs == null) { W.ShowToast(L("Выберите точку или плоскости")); return false; }
        W.Session.CommitEdit(); return true;
    }

    public override void Cancel() { if (_cs != null) Part.Remove(_cs); W.Session.DiscardEdit(); }
}

/// <summary>Опорная кривая: через точки (гладко, центростремительный Кэтмулл–Ром), по уравнению x(t), y(t), z(t), проекция на грани.</summary>
public sealed class CurveOp : Operation
{
    public int Mode;   // 0 через точки, 1 уравнение, 2 проекция
    private RefField _pts = null!, _src = null!, _faces = null!, _dir = null!;
    private string _fx = "50*cos(t)", _fy = "50*sin(t)", _fz = "t/10";
    private ValueRef _t0 = new(0), _t1 = new(720), _n = new(200);
    private bool _closed, _smooth = true;
    private DatumCurve? _dc;
    public override string Title => Mode switch { 1 => "Кривая по уравнению", 2 => "Проекция кривой на грань", _ => "Кривая через точки" };
    public override string Icon => Mode switch { 1 => "dat_curve_equation_but.svg", 2 => "dat_curve_project_but.svg", _ => "dat_curve_points_but.svg" };

    protected override bool Start() { W.Session.BeginEdit(L(Title)); return true; }

    public override void Build(OpPanel p)
    {
        switch (Mode)
        {
            case 1:
                Text(p, "x(t) =", () => _fx, v => _fx = v);
                Text(p, "y(t) =", () => _fy, v => _fy = v);
                Text(p, "z(t) =", () => _fz, v => _fz = v);
                p.Number("t от", () => _t0, v => _t0 = v, "");
                p.Number("t до", () => _t1, v => _t1 = v, "");
                p.Number("Точек", () => _n, v => _n = v, "");
                p.Note("Тригонометрия — в градусах (sin(t), cos(t)); можно параметры детали.");
                _pts = _src = _faces = _dir = new RefField { Label = "", Optional = true };
                break;
            case 2:
                _src = p.Ref(new RefField { Label = "Кривая (эскиз или опорная)", Filter = PickFilter.SketchCurves | PickFilter.Datums, Accept = r => Refs.IsSketch(r) || r.Target is DatumCurve or Line3DItem });
                _faces = p.Ref(MoreFields.Faces("Грани"));
                _dir = p.Ref(new RefField { Label = "Направление", Filter = PickFilter.Edges | PickFilter.Datums | PickFilter.Faces, Accept = r => Refs.IsLinear(r) || Refs.IsPlanar(r), Single = true, Optional = true });
                p.Note("Без направления — по нормали плоскости эскиза.");
                _pts = new RefField { Label = "", Optional = true };
                break;
            default:
                _pts = p.Ref(MoreFields.Points("Точки по порядку", optional: false));
                p.Check("Гладкая", () => _smooth, v => _smooth = v);
                p.Check("Замкнутая", () => _closed, v => _closed = v);
                _src = _faces = _dir = new RefField { Label = "", Optional = true };
                break;
        }
    }

    static void Text(OpPanel p, string label, Func<string> get, Action<string> set) => p.Text(label, get, set);

    public override void Prime()
    {
        if (Mode == 0) Seed(_pts); else if (Mode == 2) Seed(_src, _faces, _dir);
        foreach (var f in new[] { _pts, _src, _faces, _dir }) if (f.Text != null) f.Text.Text = f.Summary;
        Panel?.Refresh(); Apply(); RefreshStatus();
    }

    /// <summary>Гладкая кривая через точки: центростремительный Кэтмулл–Ром (без петель), k точек на участок.</summary>
    public static List<Vector3d> ThroughPoints(IReadOnlyList<Vector3d> q, bool closed, bool smooth, int k = 16)
    {
        var res = new List<Vector3d>();
        int n = q.Count; if (n < 2) return res;
        if (!smooth) { res.AddRange(q); if (closed) res.Add(q[0]); return res; }
        Vector3d P(int i) => closed ? q[((i % n) + n) % n] : q[Math.Clamp(i, 0, n - 1)];
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var p0 = P(i - 1); var p1 = P(i); var p2 = P(i + 1); var p3 = P(i + 2);
            if (!closed && i == 0) p0 = p1 - (p2 - p1);
            if (!closed && i == segs - 1) p3 = p2 + (p2 - p1);
            double T(Vector3d a, Vector3d b) => Math.Max(1e-9, Math.Sqrt((b - a).Length));
            double t0 = 0, t1 = t0 + T(p0, p1), t2 = t1 + T(p1, p2), t3 = t2 + T(p2, p3);
            for (int j = (i == 0 ? 0 : 1); j <= k; j++)
            {
                double t = t1 + (t2 - t1) * j / k;
                var a1 = (t1 - t) / (t1 - t0) * p0 + (t - t0) / (t1 - t0) * p1;
                var a2 = (t2 - t) / (t2 - t1) * p1 + (t - t1) / (t2 - t1) * p2;
                var a3 = (t3 - t) / (t3 - t2) * p2 + (t - t2) / (t3 - t2) * p3;
                var b1 = (t2 - t) / (t2 - t0) * a1 + (t - t0) / (t2 - t0) * a2;
                var b2 = (t3 - t) / (t3 - t1) * a2 + (t - t1) / (t3 - t1) * a3;
                res.Add((t2 - t) / (t2 - t1) * b1 + (t - t1) / (t2 - t1) * b2);
            }
        }
        return res;
    }

    /// <summary>Точки кривой по уравнению; null — формула не вычисляется (ошибка в why).</summary>
    public static List<Vector3d>? Equation(ParameterTable? scope, string fx, string fy, string fz, double t0, double t1, int n, out string? why)
    {
        why = null;
        var tt = new ParameterTable { Parent = scope, Units = scope?.Units ?? Parametric3D.Core.UnitSystem.Metric };
        var tp = tt.Add("t", t0);
        var res = new List<Vector3d>();
        n = Math.Clamp(n, 2, 20000);
        for (int i = 0; i <= n; i++)
        {
            tp.Number = t0 + (t1 - t0) * i / n;
            // k128: координаты — длины: число без единицы — в единице детали
            var L = Parametric3D.Core.Dimension.Length;
            if (!tt.TryEvaluateAs(fx, L, out var x, out why) || !tt.TryEvaluateAs(fy, L, out var y, out why) || !tt.TryEvaluateAs(fz, L, out var z, out why)) return null;
            res.Add(new Vector3d(x, y, z));
        }
        return res;
    }

    /// <summary>Проекция точек вдоль направления на треугольники граней (ближайшее попадание в обе стороны).</summary>
    public static List<Vector3d> Project(IEnumerable<Vector3d> pts, Vector3d dir, List<(Vector3d A, Vector3d B, Vector3d C)> tris)
    {
        dir = dir.Normalized();
        var res = new List<Vector3d>();
        foreach (var p in pts)
        {
            double best = double.MaxValue; Vector3d? hit = null;
            foreach (var (a, b, c) in tris)
            {
                var e1 = b - a; var e2 = c - a; var h = Vector3d.Cross(dir, e2); double det = Vector3d.Dot(e1, h);
                if (Math.Abs(det) < 1e-14) continue;
                var s = p - a; double u = Vector3d.Dot(s, h) / det; if (u < -1e-9 || u > 1 + 1e-9) continue;
                var q = Vector3d.Cross(s, e1); double v = Vector3d.Dot(dir, q) / det; if (v < -1e-9 || u + v > 1 + 1e-9) continue;
                double t = Vector3d.Dot(e2, q) / det;
                if (Math.Abs(t) < best) { best = Math.Abs(t); hit = p + dir * t; }
            }
            if (hit is { } hp) res.Add(hp);
        }
        return res;
    }

    public List<Vector3d>? Compute()
    {
        switch (Mode)
        {
            case 1:
            {
                var r = Equation(Parameters, _fx, _fy, _fz, _t0.Value, _t1.Value, (int)Math.Round(_n.Value), out var why);
                if (r == null) W.ShowToast(L("Не формула: {0}", why ?? ""));
                return r;
            }
            case 2:
            {
                if (_src.Items.Count == 0 || _faces.Items.Count == 0) return null;
                var pts = new List<Vector3d>(); Vector3d? n0 = null;
                foreach (var r in _src.Items)
                {
                    if (r.Target is DatumCurve dc) { pts.AddRange(dc.Points); continue; }
                    if (r.Target is Line3DItem ln) { for (int i = 0; i <= 50; i++) pts.Add(ln.A + (ln.B - ln.A) * (i / 50.0)); continue; }
                    if (Refs.SketchOf(r) is { } sk)
                    {
                        n0 ??= sk.Plane.W;
                        var ents = r.Target is SkEntity se && se is not SkPoint ? new[] { se } : sk.Entities.Where(e => e is not SkPoint && e.Role == SkRole.Object).ToArray();
                        foreach (var e in ents) { var l = new List<Vector3d>(); try { e.ToCurve3(sk.Plane).Tessellate(l, true); } catch { continue; } pts.AddRange(l); }
                    }
                }
                Vector3d dir = n0 ?? Vector3d.UnitZ;
                if (_dir.Items.Count > 0) { if (Refs.LineOf(_dir.Items[0]) is { } l) dir = l.Dir; else if (Refs.PlaneOf(_dir.Items[0]) is { } pl) dir = pl.Frame.W; }
                var tris = new List<(Vector3d, Vector3d, Vector3d)>();
                foreach (var fr in _faces.Items)
                    if (fr.Owner is Part p && p.Mesh is { } md)
                    {
                        var v = md.Vertices; var ix = md.Indices;
                        for (int i = 0; i + 2 < ix.Length; i += 3)
                            if (v[ix[i]].FaceId == fr.Id)
                                tris.Add((new Vector3d(v[ix[i]].P.X, v[ix[i]].P.Y, v[ix[i]].P.Z), new Vector3d(v[ix[i + 1]].P.X, v[ix[i + 1]].P.Y, v[ix[i + 1]].P.Z), new Vector3d(v[ix[i + 2]].P.X, v[ix[i + 2]].P.Y, v[ix[i + 2]].P.Z)));
                    }
                var res = Project(pts, dir, tris);
                return res.Count >= 2 ? res : null;
            }
            default:
            {
                var q = _pts.Items.Select(Refs.PointOf).OfType<Vector3d>().ToList();
                return q.Count >= 2 ? ThroughPoints(q, _closed, _smooth) : null;
            }
        }
    }

    protected override void Apply()
    {
        if (Compute() is not { Count: >= 2 } pts) return;
        string src = Mode switch { 1 => $"x={_fx}; y={_fy}; z={_fz}; t={_t0.Value}..{_t1.Value}", 2 => "projection", _ => "through points" };
        if (_dc == null) { _dc = new DatumCurve(L("Кривая{0}", Part.Children.OfType<DatumCurve>().Count() + 1), pts, src); Part.Add(_dc); }
        else { _dc.Points.Clear(); _dc.Points.AddRange(pts); _dc.Source = src; _dc.NotifyChanged(_dc); }
        W.ViewPort.MarkDirty();
    }

    public override bool Commit()
    {
        if (_dc == null) { W.ShowToast(L("Кривая не построена — проверьте ссылки")); return false; }
        W.Session.CommitEdit(); return true;
    }

    public override void Cancel() { if (_dc != null) Part.Remove(_dc); W.Session.DiscardEdit(); }
}

/// <summary>Соосное отверстие (на оси цилиндрической грани, ребра-окружности или опорной оси) и радиальное (в стенке цилиндра к оси).</summary>
public sealed class AxialHoleOp : FeatureOp<HoleFeature>
{
    public bool Radial;
    private RefField _axis = null!, _face = null!, _pt = null!;
    public AxialHoleOp(HoleFeature? existing = null) : base(existing) { }
    public override string Title => Radial ? "Отверстие радиальное" : "Отверстие соосное";
    public override string Icon => Radial ? "f_hole_radial_but.svg" : "f_hole_coaxial_but.svg";
    protected override HoleFeature Create() => new(L(Title)) { Diameter = new ValueRef(6) };

    public static (Vector3d At, Vector3d Dir)? AxisOf(PickRef r)
    {
        if (Refs.LineOf(r) is { } l && r.Kind != PickKind.Edge) return l;
        if (r.Kind == PickKind.Face && MoreFields.FaceOf(r) is { } f) { try { return DatumBuilder.AxisOfFace(f); } catch { return null; } }
        if (r.Kind == PickKind.Edge && r.Owner is Part p && p.FindEdge(r.Id)?.Curve is ArcCurve3 a) return (a.Frame.Origin, a.Frame.W.Normalized());
        return Refs.LineOf(r);
    }

    public override void Build(OpPanel p)
    {
        _axis = p.Ref(new RefField { Label = Radial ? "Цилиндрическая грань" : "Ось (цилиндр, окружность, ось)", Filter = PickFilter.Faces | PickFilter.Edges | PickFilter.Datums, Accept = r => AxisOf(r) != null && !(r.Kind == PickKind.Face && Refs.IsPlanar(r)), Single = true });
        if (Radial) _pt = p.Ref(new RefField { Label = "Положение (точка)", Filter = PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums, Accept = r => r.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint, Single = true, Optional = true });
        else _face = p.Ref(new RefField { Label = "Грань входа", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true });
        _pt ??= new RefField { Label = "", Optional = true }; _face ??= new RefField { Label = "", Optional = true };
        p.Note(Radial ? "Без точки — в середине грани; сверлится к оси." : "Центр — пересечение оси с гранью входа.");
        p.Number("Диаметр", () => F.Diameter, v => F.Diameter = v);
        p.Check("Насквозь", () => F.ThroughAll, v => F.ThroughAll = v);
        p.Number("Глубина", () => F.Depth, v => F.Depth = v, visible: () => !F.ThroughAll);
    }

    protected override void SeedRefs() { if (Radial) Seed(_axis, _pt); else Seed(_axis, _face); }

    protected override void Push()
    {
        if (_axis.Items.Count == 0 || AxisOf(_axis.Items[0]) is not { } ax) return;
        var d = ax.Dir.Normalized();
        if (!Radial)
        {
            if (_face.Items.Count == 0 || Refs.PlaneOf(_face.Items[0]) is not { } pl) return;
            var n = pl.Frame.W.Normalized(); double den = Vector3d.Dot(d, n);
            if (Math.Abs(den) < 1e-9) { W.ShowToast(L("Ось параллельна грани входа")); return; }
            var at = ax.At + d * (Vector3d.Dot(pl.Frame.Origin - ax.At, n) / den);
            F.Points.Clear(); F.PointFaces.Clear(); F.Points.Add(at); F.Direction = -n;
        }
        else
        {
            Vector3d p0 = _pt.Items.Count > 0 && Refs.PointOf(_pt.Items[0]) is { } pp ? pp
                : _axis.Items[0] is { Kind: PickKind.Face, Owner: Part fp } fr && Refs.FacePoint(fp, fr.Id) is { } fc ? fc : ax.At;
            var foot = ax.At + d * Vector3d.Dot(p0 - ax.At, d);
            var radial = p0 - foot;
            if (radial.LengthSquared < 1e-12) { W.ShowToast(L("Точка на оси — укажите точку на стенке")); return; }
            double R = MoreFields.FaceOf(_axis.Items[0]) is { } f && Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr ? rr : radial.Length;
            var on = foot + radial.Normalized() * R;
            F.Points.Clear(); F.PointFaces.Clear(); F.Points.Add(on); F.Direction = -radial.Normalized();
        }
    }
}

public sealed class FullRoundOp : FeatureOp<FullRoundFeature>
{
    private RefField _faces = null!;
    private readonly RefCache<Vector3d> _cache = new();
    public FullRoundOp(FullRoundFeature? existing = null) : base(existing) { }
    public override string Title => "Полное скругление";
    public override string Icon => "f_round_full_but.svg";
    protected override FullRoundFeature Create() => new(L(Title));

    public override void Build(OpPanel p)
    {
        _faces = p.Ref(MoreFields.Faces("Боковая, средняя, боковая грань"));
        if (Editing) _faces.Kept = F.OnFaces.Count;
        p.Note("Средняя грань заменяется полуцилиндром, касательным к боковым (радиус — половина ширины).");
    }

    protected override void SeedRefs() => Seed(_faces);
    protected override bool MissingRequiredExtra => _faces.Items.Count != 3 && F.OnFaces.Count != 3;

    protected override void Push()
    {
        if (_faces.Touched)
        {
            F.OnFaces.Clear(); F.OnFaceNames.Clear();
            F.OnFaces.AddRange(_cache.Resolve(_faces, Refs.PointOf));
        }
    }
}
