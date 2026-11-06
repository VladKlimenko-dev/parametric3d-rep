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
        foreach (var f in new[] { _pt, _pl, _x }) f.Refresh();
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
        if (_cs == null) { _cs = new DatumCsys(L("СК{0}", Part.Children.OfType<DatumCsys>().Count() + 1), f); Part.AddAtStage(_cs); }
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
    /// <summary>k137 (замечание 29.10, 18:13, п. 7): правка существующей кривой (двойной щелчок в дереве, «Изменить»).</summary>
    public DatumCurve? EditTarget;
    public static readonly bool EditOff = Environment.GetEnvironmentVariable("P3D_CURVEEDIT") == "0";
    static readonly bool AccumulateOff = Environment.GetEnvironmentVariable("P3D_PICKORDER") == "0";
    private (List<Vector3d> Pts, string Src, int Mode, List<Vector3d> In, bool Closed, bool Smooth, List<Vector3d>? Cv, List<double>? Wt, List<double>? Kn, int Deg, List<CurvePointSpec> Specs)? _orig;
    // k177: условия касательных по точкам (через точки)
    private readonly List<CurvePointSpec> _specs = new();
    private readonly List<List<uint>> _cands = new();
    private List<Vector3d> _specPts = new(), _tan = new();
    private int _cur;
    private Avalonia.Controls.ComboBox? _curBox;
    private bool _syncBox;
    private RefField _face = null!;
    private SplineCurve3? _spline;
    public static readonly bool TangentsOff = Environment.GetEnvironmentVariable("P3D_CURVETANGENTS") == "0";
    /// <summary>k178: мутация — у новой точки эскиза «По нормали эскиза» не включается само.</summary>
    public static readonly bool NormalDefaultOff = Environment.GetEnvironmentVariable("P3D_CURVENORMALDEFAULT") == "0";
    CurvePointSpec? Cur => _cur >= 0 && _cur < _specs.Count ? _specs[_cur] : null;
    List<uint> CurCands => _cur >= 0 && _cur < _cands.Count ? _cands[_cur] : new();
    /// <summary>Разбор (проверки): условия точек и выбор точки панели.</summary>
    public IReadOnlyList<CurvePointSpec> PointSpecs => _specs;
    public IReadOnlyList<uint> CandidateFaces(int i) => i >= 0 && i < _cands.Count ? _cands[i] : Array.Empty<uint>();
    public int CurrentPoint { get => _cur; set { _cur = Math.Clamp(value, 0, Math.Max(0, _specs.Count - 1)); SyncFace(); Panel?.Refresh(); UpdatePointBox(); } }
    /// <summary>Способ по кривой файла до k137 (без записанного способа): по описанию построения.</summary>
    public static int ModeOf(DatumCurve dc) => dc.Mode >= 0 ? dc.Mode : dc.Source.StartsWith("x=") ? 1 : dc.Source == "projection" ? 2 : 0;
    public override string Title => Mode switch { 1 => "Кривая по уравнению", 2 => "Проекция кривой на грань", _ => "Кривая через точки" };
    public override string Icon => Mode switch { 1 => "dat_curve_equation_but.svg", 2 => "dat_curve_project_but.svg", _ => "dat_curve_points_but.svg" };

    protected override bool Start()
    {
        if (EditTarget is { } dc)
        {
            W.Session.BeginEdit(L("Изменить «{0}»", dc.Name));
            _dc = dc; Mode = ModeOf(dc); _closed = dc.Closed; _smooth = dc.Smooth;
            _orig = (dc.Points.ToList(), dc.Source, dc.Mode, dc.Inputs.ToList(), dc.Closed, dc.Smooth, dc.Control?.ToList(), dc.Weights?.ToList(), dc.Knots?.ToList(), dc.Degree, dc.Specs.Select(x => x.Copy()).ToList());
            _specs.AddRange(dc.Specs.Select(x => x.Copy())); _specPts = dc.Inputs.ToList();
            // уравнение — из описания построения «x=…; y=…; z=…; t=a..b»
            if (Mode == 1)
            {
                var m = System.Text.RegularExpressions.Regex.Match(dc.Source, @"^x=(.*); y=(.*); z=(.*); t=([-0-9.eE+]+)\.\.([-0-9.eE+]+)$");
                if (m.Success)
                {
                    _fx = m.Groups[1].Value; _fy = m.Groups[2].Value; _fz = m.Groups[3].Value;
                    _t0 = new ValueRef(double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture));
                    _t1 = new ValueRef(double.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture));
                    _n = new ValueRef(Math.Max(2, dc.Points.Count - 1));
                }
            }
            return true;
        }
        W.Session.BeginEdit(L(Title)); return true;
    }

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
                _pts = p.Ref(new RefField
                {
                    Label = "Точки по порядку", Filter = PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums,
                    Accept = r => r.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint, Accumulate = !AccumulateOff,
                    Tip = "Щелчками по порядку: вершины тела, точки эскизов (концы и центры кривых видимых эскизов), опорные точки. Ctrl — убрать точку.",
                });
                if (EditTarget is { } edc && edc.Inputs.Count >= 2) _pts.Kept = edc.Inputs.Count;   // k137: без нового выбора — прежние точки
                p.Check("Гладкая", () => _smooth, v => _smooth = v);
                p.Check("Замкнутая", () => _closed, v => _closed = v);
                _src = _faces = _dir = new RefField { Label = "", Optional = true };
                if (!TangentsOff) BuildTangents(p);
                break;
        }
    }

    static void Text(OpPanel p, string label, Func<string> get, Action<string> set) => p.Text(label, get, set);

    /// <summary>
    /// k177: КАСАТЕЛЬНЫЕ В ТОЧКАХ. Точка выбирается списком (или щелчком по стрелке касательной на виде — обращение); у каждой
    /// точки: «По нормали эскиза» (точка эскиза — касательная по нормали его плоскости), «По нормали поверхности» (грань,
    /// сходящаяся в точке: одна — сама, несколько — та, что ближе всех к перпендикуляру хода кривой; другую — щелчком на виде),
    /// «Обратить направление», вес касательной. Условие одной точки не трогает соседние; точка без условия — касательная по
    /// соседям. У ломаной (не гладкой) условий нет. Мутация P3D_CURVETANGENTS=0 — без раздела.
    /// </summary>
    void BuildTangents(OpPanel p)
    {
        p.Header("Касательные в точках");
        _curBox = new Avalonia.Controls.ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Name = "curve_point" };
        _curBox.SelectionChanged += (_, _) => { if (_syncBox || _curBox.SelectedIndex < 0) return; _cur = _curBox.SelectedIndex; SyncFace(); Panel?.Refresh(); RefreshStatus(); };
        var g = new Avalonia.Controls.Grid { ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("108,*"), Margin = new Avalonia.Thickness(0, 1) };
        g.Children.Add(new Avalonia.Controls.TextBlock { Text = L("Точка"), Classes = { "dim" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        // k178: выбор точки — список и кнопки «предыдущая / следующая»
        var dock = new Avalonia.Controls.DockPanel();
        Avalonia.Controls.Button Step(string text, int d, string tip)
        {
            var b = new Avalonia.Controls.Button { Content = text, Classes = { "tool" }, Padding = new Avalonia.Thickness(6, 0), MinHeight = 22, Name = d < 0 ? "curve_point_prev" : "curve_point_next" };
            Avalonia.Controls.ToolTip.SetTip(b, L(tip));
            b.Click += (_, _) => { if (_specs.Count == 0) return; CurrentPoint = (_cur + d + _specs.Count) % _specs.Count; RefreshStatus(); };
            Avalonia.Controls.DockPanel.SetDock(b, Avalonia.Controls.Dock.Right);
            return b;
        }
        dock.Children.Add(Step("▶", 1, "Следующая точка"));
        dock.Children.Add(Step("◀", -1, "Предыдущая точка"));
        dock.Children.Add(_curBox);
        Avalonia.Controls.Grid.SetColumn(dock, 1); g.Children.Add(dock);
        p.Host.Children.Add(g);
        bool Smooth() => _smooth && Cur != null;
        p.Check("По нормали эскиза", () => Cur?.NormalToSketch ?? false, v => { if (Cur is { } c) { c.NormalToSketch = v && c.FromSketch; if (c.NormalToSketch) c.NormalToSurface = false; } },
                enabled: () => Smooth() && Cur!.FromSketch,
                tip: "Касательная в точке — по нормали плоскости эскиза, на котором построена точка. Доступно у точек эскиза.");
        p.Check("По нормали поверхности", () => Cur?.NormalToSurface ?? false, v => { if (Cur is { } c) { c.NormalToSurface = v && CurCands.Count > 0 && !c.NormalToSketch; if (c.NormalToSurface && (c.FaceAuto || !CurCands.Contains(c.FaceId))) { c.FaceId = DefaultFace(_cur); c.FaceAuto = true; } SyncFace(); } },
                enabled: () => Smooth() && !Cur!.NormalToSketch && CurCands.Count > 0,
                tip: "Касательная в точке — по нормали поверхности, сходящейся в этой точке. Доступно, если точка лежит на грани тела; при «По нормали эскиза» — нет.");
        _face = p.Ref(new RefField
        {
            Label = "Поверхность", Filter = PickFilter.Faces, Single = true, Optional = true,
            Accept = r => r.Kind == PickKind.Face && CurCands.Contains(r.Id), Prefer = r => r.Kind == PickKind.Face && CurCands.Contains(r.Id),
            Enabled = () => Smooth() && Cur!.NormalToSurface && !Cur.NormalToSketch && CurCands.Count > 0,
            Describe = r => L("Грань #{0}", r.Id),
            Tip = "Грань, по нормали которой идёт касательная в точке. Щелчок по виду — другая из граней, сходящихся в точке. По умолчанию: одна грань — она; несколько — та, что ближе к перпендикуляру хода кривой.",
        });
        p.Check("Обратить направление", () => Cur?.Flip ?? false, v => { if (Cur is { } c) c.Flip = v; }, enabled: () => Smooth() && Cur!.Constrained,
                tip: "Касательная — в обратную сторону (по умолчанию — по ходу кривой). То же — щелчок по стрелке точки на виде.");
        p.Number("Вес касательной", () => new ValueRef(Cur?.Weight ?? 1), v => { if (Cur is { } c && v.Value > 0) c.Weight = Math.Clamp(v.Value, 0.05, 10); }, unit: "", scrub: 0.1);
        p.Button("Всем точкам эскизов — по нормали эскиза", () => { foreach (var c in _specs) if (c.FromSketch) { c.NormalToSketch = true; c.NormalToSurface = false; } SyncFace(); FieldChanged(); }, enabled: () => _smooth && _specs.Any(c => c.FromSketch));
        p.Button("Снять условия у всех точек", () => { foreach (var c in _specs) { c.NormalToSketch = c.NormalToSurface = c.Flip = false; c.Weight = 1; } SyncFace(); FieldChanged(); }, enabled: () => _specs.Any(c => c.NormalToSketch || c.NormalToSurface || c.Flip || Math.Abs(c.Weight - 1) > 1e-12));
        p.Info(() => TangentSummary());
        p.Note("Стрелки на виде — касательные точек с условием; щелчок по стрелке обращает её. Вес — длина касательной в долях хорды участка (1 — обычная).");
    }

    string SourceName(int i)
    {
        var c = _specs[i];
        if (c.SketchId != 0 && Part.Sketches.FirstOrDefault(s => s.Id == c.SketchId) is { } sk) return L("эскиз «{0}»", sk.Name);
        if (c.FromSketch) return L("точка эскиза");
        if (c.VertexId != 0) return L("вершина");
        return L("точка");
    }
    string CondName(CurvePointSpec c) => c.NormalToSketch && c.FromSketch ? L("по нормали эскиза") : c.NormalToSurface && c.FaceNormal.LengthSquared > 0 ? L("по нормали грани #{0}", c.FaceId) : L("свободно");
    string TangentSummary()
    {
        if (!_smooth) return L("Ломаная: касательных нет.");
        if (_specs.Count == 0) return L("Выберите точки.");
        return string.Join("; ", _specs.Select((c, i) => $"{i + 1}: {CondName(c)}{(c.Constrained && c.Flip ? " ⇄" : "")}{(Math.Abs(c.Weight - 1) > 1e-12 ? $" ×{c.Weight:0.##}" : "")}"));
    }

    void UpdatePointBox()
    {
        if (_curBox == null) return;
        _syncBox = true;
        try
        {
            _curBox.ItemsSource = _specs.Select((c, i) => $"{i + 1} — {SourceName(i)} ({CondName(c)})").ToList();
            if (_specs.Count > 0) _curBox.SelectedIndex = Math.Clamp(_cur, 0, _specs.Count - 1);
        }
        finally { _syncBox = false; }
    }

    /// <summary>Поле «Поверхность» — грань текущей точки (без отметки «тронуто»: правка самой точки — по щелчку).</summary>
    void SyncFace()
    {
        if (_face == null) return;
        _face.Items.Clear();
        if (Cur is { NormalToSurface: true, FaceId: > 0 } c) _face.Items.Add(new PickRef(c.FaceId, PickKind.Face, Part, null));
        _face.Refresh();
    }

    IEnumerable<Parametric3D.Kernel.Topology.TFace> AllFaces() => Part.Bodies.SelectMany(b => b.Faces).Concat(Part.Quilts.SelectMany(q => q.Faces));

    Dictionary<uint, List<(Vector3d A, Vector3d B, Vector3d C)>>? _faceTris;
    object? _faceTrisFor;
    /// <summary>Грани тела, сходящиеся в точке: у вершины — грани её рёбер; иначе — грани, на поверхности и внутри контура которых точка.</summary>
    List<uint> FacesAt(Vector3d q, PickRef? r)
    {
        var res = new List<uint>();
        Parametric3D.Kernel.Topology.TVertex? vx = r is { Kind: PickKind.Vertex } vr ? Part.FindVertex(vr.Id) : null;
        vx ??= Part.Bodies.SelectMany(b => b.Vertices).FirstOrDefault(v => (v.P - q).Length <= 1e-7 * Math.Max(1, q.Length));
        if (vx != null)
        {
            foreach (var e in vx.Edges) foreach (var f in new[] { e.FaceA, e.FaceB }) if (f != null && !res.Contains(f.Id)) res.Add(f.Id);
            if (res.Count > 0) return res;
        }
        if (!ReferenceEquals(_faceTrisFor, Part.Mesh))
        {
            _faceTrisFor = Part.Mesh; _faceTris = new();
            if (Part.Mesh is { } md)
                for (int i = 0; i + 2 < md.Indices.Length; i += 3)
                {
                    var v0 = md.Vertices[md.Indices[i]]; var v1 = md.Vertices[md.Indices[i + 1]]; var v2 = md.Vertices[md.Indices[i + 2]];
                    if (!_faceTris.TryGetValue(v0.FaceId, out var l)) _faceTris[v0.FaceId] = l = new();
                    l.Add((new Vector3d(v0.P.X, v0.P.Y, v0.P.Z), new Vector3d(v1.P.X, v1.P.Y, v1.P.Z), new Vector3d(v2.P.X, v2.P.Y, v2.P.Z)));
                }
        }
        double tolMesh = Math.Max(1e-2, 3 * Tol.Chord);
        foreach (var f in AllFaces())
        {
            if (res.Contains(f.Id)) continue;
            var su = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) ?? f.Surface;
            try { if ((su.PointAt(su.ProjectToUV(q)) - q).Length > Math.Max(1e-4, 1e-6 * q.Length)) continue; } catch { continue; }
            if (_faceTris == null || !_faceTris.TryGetValue(f.Id, out var tris)) continue;
            if (tris.Any(t => DistTri(q, t.A, t.B, t.C) <= tolMesh)) res.Add(f.Id);
        }
        return res;
    }

    static double DistTri(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        // ближайшая точка треугольника (Эриксон, «Real-Time Collision Detection», 5.1.5)
        var ab = b - a; var ac = c - a; var ap = p - a;
        double d1 = Vector3d.Dot(ab, ap), d2 = Vector3d.Dot(ac, ap); if (d1 <= 0 && d2 <= 0) return (p - a).Length;
        var bp = p - b; double d3 = Vector3d.Dot(ab, bp), d4 = Vector3d.Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return (p - b).Length;
        double vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + ab * (d1 / (d1 - d3)))).Length;
        var cp = p - c; double d5 = Vector3d.Dot(ab, cp), d6 = Vector3d.Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return (p - c).Length;
        double vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + ac * (d2 / (d2 - d6)))).Length;
        double va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).Length;
        double den = 1 / (va + vb + vc); return (p - (a + ab * (vb * den) + ac * (vc * den))).Length;
    }

    /// <summary>Грань по умолчанию: одна — она; несколько — та, чья нормаль ближе всех к ходу кривой (грань — ближе к перпендикуляру кривой).</summary>
    uint DefaultFace(int i)
    {
        var cands = i < _cands.Count ? _cands[i] : new();
        if (cands.Count == 0) return 0;
        if (cands.Count == 1) return cands[0];
        var free = _specPts.Count >= 2 ? CurveThrough.Tangents(_specPts, _specPts.Select(_ => (Vector3d?)null).ToList(), _specPts.Select(_ => false).ToList(), _closed) : null;
        var t = free != null && i < free.Length ? free[i] : Vector3d.UnitZ;
        uint best = cands[0]; double bd = -1;
        foreach (var id in cands)
            if (Part.FindFace(id) is { } f)
            {
                double d; try { d = Math.Abs(Vector3d.Dot(f.NormalAt(_specPts[i]).Normalized(), t)); } catch { continue; }
                if (d > bd) { bd = d; best = id; }
            }
        return best;
    }

    /// <summary>Условия точек — по новому списку точек: у прежней точки (то же место) — прежние, у новой — по ссылке; грани — заново.</summary>
    void SyncSpecs(List<Vector3d> q, List<PickRef>? refs)
    {
        var old = _specs.Select((c, i) => (C: c, P: i < _specPts.Count ? _specPts[i] : new Vector3d(double.NaN))).ToList();
        var used = new bool[old.Count];
        _specs.Clear(); _cands.Clear();
        var oldPts = _specPts;
        _specPts = q.ToList();
        for (int i = 0; i < q.Count; i++)
        {
            CurvePointSpec? c = null;
            for (int k = 0; k < old.Count; k++) if (!used[k] && (old[k].P - q[i]).Length <= 1e-9 * Math.Max(1, q[i].Length)) { used[k] = true; c = old[k].C; break; }
            bool fresh = c == null;
            c ??= new CurvePointSpec();
            var r = refs != null && i < refs.Count ? refs[i] : (PickRef?)null;
            if (r is { Kind: PickKind.SketchPoint } sr && Refs.SketchOf(sr) is { } sk) { c.SketchId = sk.Id; c.SketchPointId = sr.Id; c.SketchNormal = sk.Plane.W.Normalized(); }
            else if (r is { Kind: PickKind.Vertex } vr) { c.VertexId = vr.Id; }
            if (!c.FromSketch)
                // точка эскиза — и без ссылки (правка, кривая до k177): эскиз, у которого есть точка в этом месте
                foreach (var sk2 in Part.Sketches)
                {
                    var hit = sk2.Entities.SelectMany(e => e.Handles)
                        .FirstOrDefault(pt => (sk2.Plane.ToWorld(pt.P) - q[i]).Length <= 1e-7 * Math.Max(1, q[i].Length));
                    if (hit != null) { c.SketchId = sk2.Id; c.SketchPointId = hit.Id; c.SketchNormal = sk2.Plane.W.Normalized(); break; }
                }
            if (!c.FromSketch) c.NormalToSketch = false;
            // k178: у новой точки эскиза — «По нормали эскиза» по умолчанию (если не задана нормаль поверхности)
            else if (fresh && !c.NormalToSurface && !NormalDefaultOff) c.NormalToSketch = true;
            _specs.Add(c);
            _cands.Add(FacesAt(q[i], r));
        }
        for (int i = 0; i < _specs.Count; i++)
        {
            var c = _specs[i]; var cands = _cands[i];
            if (cands.Count == 0) { c.NormalToSurface = false; c.FaceNormal = Vector3d.Zero; continue; }
            if (!c.NormalToSurface) continue;   // грань — при включении (по умолчанию — по ходу кривой с теми точками, что есть тогда)
            if (c.FaceAuto) c.FaceId = DefaultFace(i);
            else if (!cands.Contains(c.FaceId))
                c.FaceId = c.FaceTag != null && cands.FirstOrDefault(id => Part.FindFace(id)?.Tag == c.FaceTag) is var byTag && byTag != 0 ? byTag : DefaultFace(i);
            if (Part.FindFace(c.FaceId) is { } f) { c.FaceTag = f.Tag; try { c.FaceNormal = f.NormalAt(q[i]).Normalized(); } catch { c.FaceNormal = Vector3d.Zero; } }
        }
        _cur = Math.Clamp(_cur, 0, Math.Max(0, _specs.Count - 1));
        UpdatePointBox();
        SyncFace();
    }

    public override IEnumerable<(Vector3d At, Vector3d Dir)> ViewArrows()
    {
        if (Mode != 0 || !_smooth || TangentsOff) yield break;
        for (int i = 0; i < _specs.Count && i < _tan.Count && i < _specPts.Count; i++)
            if (_specs[i].Constrained) yield return (_specPts[i], _tan[i]);
    }

    public override void ArrowClicked(int index)
    {
        int k = -1;
        for (int i = 0; i < _specs.Count; i++) if (_specs[i].Constrained && ++k == index) { _specs[i].Flip = !_specs[i].Flip; _cur = i; UpdatePointBox(); FieldChanged(); return; }
    }

    public override void Prime()
    {
        if (EditTarget != null) { }   // правка: выбор до команды — не ссылки кривой
        else if (Mode == 0) Seed(_pts); else if (Mode == 2) Seed(_src, _faces, _dir);
        foreach (var f in new[] { _pts, _src, _faces, _dir }) f.Refresh();
        // k178: состояние флажков точек — после построения (при правке точки известны только после Apply; прежде флажки
        // оставались недоступными до первой правки поля)
        Apply(); Panel?.Refresh(); RefreshStatus();
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
                var refs = _pts.Items.Where(r => Refs.PointOf(r) != null).ToList();
                var q = refs.Select(r => Refs.PointOf(r)!.Value).ToList();
                bool kept = false;
                if (q.Count == 0 && !_pts.Touched && EditTarget is { } edc && edc.Inputs.Count >= 2) { q = edc.Inputs.ToList(); kept = true; }
                _lastInputs = q;
                _spline = null;
                if (TangentsOff) return q.Count >= 2 ? ThroughPoints(q, _closed, _smooth) : null;
                // k177: условия касательных; гладкая — кубические участки (точно через точки, касательные по условиям)
                if (_face != null && Cur is { NormalToSurface: true } fc && _face.Items.Count > 0 && _face.Items[0].Id != fc.FaceId) { fc.FaceId = _face.Items[0].Id; fc.FaceAuto = false; }
                SyncSpecs(q, kept ? null : refs);
                if (q.Count < 2) return null;
                if (!_smooth) { _tan = new(); return ThroughPoints(q, _closed, false); }
                var dirs = _specs.Select(c => c.Direction).ToList(); var flips = _specs.Select(c => c.Flip).ToList(); var wts = _specs.Select(c => c.Weight).ToList();
                _spline = CurveThrough.Build(q, dirs, flips, wts, _closed);
                if (_spline == null) return null;
                _tan = CurveThrough.Tangents(q, dirs, flips, _closed && q.Count >= 3).ToList();
                int ns = Math.Max(64, 24 * q.Count);
                return Enumerable.Range(0, ns + 1).Select(i => _spline.PointAt(_spline.T0 + (_spline.T1 - _spline.T0) * i / ns)).ToList();
            }
        }
    }

    protected override void Apply()
    {
        if (Compute() is not { Count: >= 2 } pts) return;
        string src = Mode switch { 1 => $"x={_fx}; y={_fy}; z={_fz}; t={_t0.Value}..{_t1.Value}", 2 => "projection", _ => "through points" };
        if (_dc == null) { _dc = new DatumCurve(L("Кривая{0}", Part.Children.OfType<DatumCurve>().Count() + 1), pts, src); Part.AddAtStage(_dc); }
        else { _dc.Points.Clear(); _dc.Points.AddRange(pts); _dc.Source = src; _dc.NotifyChanged(_dc); }
        // k137: построение — для правки
        _dc.Mode = Mode; _dc.Closed = _closed; _dc.Smooth = _smooth;
        _dc.Inputs.Clear(); if (Mode == 0) _dc.Inputs.AddRange(_lastInputs);
        // k177: гладкая через точки — сплайн (точки кривой — по нему); условия касательных — для правки
        if (Mode == 0 && _spline != null) _dc.SetSpline(_spline);
        else { _dc.Control = null; _dc.Weights = null; _dc.Knots = null; }
        _dc.Specs.Clear(); if (Mode == 0 && !TangentsOff) _dc.Specs.AddRange(_specs.Select(c => c.Copy()));
        _dc.NotifyChanged(_dc);
        W.ViewPort.MarkDirty();
    }
    private List<Vector3d> _lastInputs = new();

    public override bool Commit()
    {
        if (_dc == null) { W.ShowToast(L("Кривая не построена — проверьте ссылки")); return false; }
        W.Session.CommitEdit(); return true;
    }

    public override void Cancel()
    {
        if (EditTarget is { } dc && _orig is { } o)
        {
            dc.Points.Clear(); dc.Points.AddRange(o.Pts); dc.Source = o.Src; dc.Mode = o.Mode; dc.Inputs.Clear(); dc.Inputs.AddRange(o.In);
            dc.Closed = o.Closed; dc.Smooth = o.Smooth;
            dc.Control = o.Cv; dc.Weights = o.Wt; dc.Knots = o.Kn; dc.Degree = o.Deg; dc.Specs.Clear(); dc.Specs.AddRange(o.Specs);
            dc.NotifyChanged(dc);
            W.Session.DiscardEdit(); W.ViewPort.MarkDirty(); return;
        }
        if (_dc != null) Part.Remove(_dc); W.Session.DiscardEdit();
    }
}

/// <summary>Соосное отверстие (на оси цилиндрической грани, ребра-окружности или опорной оси) и радиальное (в стенке цилиндра к оси).</summary>
public sealed class AxialHoleOp : FeatureOp<HoleFeature>, IHandleOp
{
    public bool Radial;
    private RefField _axis = null!, _face = null!, _pt = null!, _end = null!;
    public AxialHoleOp(HoleFeature? existing = null) : base(existing) { if (existing?.RadFace != null) Radial = true; }
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

    /// <summary>k153: типы радиального отверстия — низ (насквозь, плоское дно, острие сверла).</summary>
    static readonly (string Icon, string Tip, int Bottom)[] RadTypes =
    {
        ("hole_all_through_ico.svg", "Простое насквозь", 0),
        ("hole_straight_bottom_square_ico.svg", "Глухое, плоское дно", 1),
        ("hole_straight_bottom_chamfer_ico.svg", "Глухое, острие сверла", 2),
    };

    public override void Build(OpPanel p)
    {
        _axis = p.Ref(new RefField { Label = Radial ? "Цилиндрическая грань" : "Ось (цилиндр, окружность, ось)", Filter = PickFilter.Faces | PickFilter.Edges | PickFilter.Datums, Accept = r => AxisOf(r) != null && !(r.Kind == PickKind.Face && Refs.IsPlanar(r)), Single = true });
        if (Radial) _pt = p.Ref(new RefField { Label = "Положение (точка)", Filter = PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums, Accept = r => r.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint, Single = true, Optional = true });
        else _face = p.Ref(new RefField { Label = "Грань входа", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true });
        _pt ??= new RefField { Label = "", Optional = true }; _face ??= new RefField { Label = "", Optional = true };
        if (Editing && Radial) _axis.Kept = 1;
        p.Note(Radial ? "Без точки — в середине грани; сверлится к оси. Гизмо: стрелка — вдоль оси, кольцо — вокруг оси; ручка — диаметр." : "Центр — пересечение оси с гранью входа.");
        if (Radial)
        {
            p.IconSegments("Тип отверстия", RadTypes.Select(t => (t.Icon, t.Tip)).ToArray(),
                () => F.ThroughAll ? 0 : F.DrillPoint ? 2 : 1, i => { F.ThroughAll = RadTypes[i].Bottom == 0; F.DrillPoint = RadTypes[i].Bottom == 2; });
            // k153 (замечание 04.11, 10:34, п. 3): положение — вдоль оси от торца и угол вокруг оси
            p.Header("Положение");
            _end = p.Ref(new RefField { Label = "Торец (отсчёт вдоль оси)", Filter = PickFilter.Faces, Accept = Refs.IsPlanar, Single = true, Optional = true });
            if (Editing) _end.Kept = F.RadRefFace != null ? 1 : 0;
            p.Number("Расстояние от торца", () => F.RadAxial, v => F.RadAxial = v, visible: () => F.RadFace != null);
            p.Number("Угол вокруг оси", () => F.RadAngle, v => F.RadAngle = v, "°", visible: () => F.RadFace != null);
        }
        else _end = new RefField { Label = "", Optional = true };
        p.Number("Диаметр", () => F.Diameter, v => F.Diameter = v);
        if (!Radial) p.Check("Насквозь", () => F.ThroughAll, v => F.ThroughAll = v);
        p.Number("Глубина", () => F.Depth, v => F.Depth = v, visible: () => !F.ThroughAll);
        if (Radial) p.Number("Угол острия", () => F.DrillAngle, v => F.DrillAngle = v, "°", visible: () => !F.ThroughAll && F.DrillPoint);
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
            return;
        }
        // k153: радиальная привязка — грань цилиндра по имени, торец, расстояние вдоль оси и угол (P3D_HOLEPLACE=0 — точка, как прежде)
        var cylFace = MoreFields.FaceOf(_axis.Items[0]);
        // k153: положение — заново только при новом выборе грани или точки (не при каждом пересчёте панели — гизмо)
        string sig = string.Join(",", _axis.Items.Select(r => r.Id)) + "|" + string.Join(",", _pt.Items.Select(r => r.Id));
        bool fresh = (_axis.Touched || _pt.Touched) && (sig != _placedSig || F.Points.Count == 0);
        if (fresh) _placedSig = sig;
        if (fresh)
        {
            Vector3d p0 = _pt.Items.Count > 0 && Refs.PointOf(_pt.Items[0]) is { } pp ? pp
                : _axis.Items[0] is { Kind: PickKind.Face, Owner: Part fp } fr && Refs.FacePoint(fp, fr.Id) is { } fc ? fc : ax.At;
            var foot = ax.At + d * Vector3d.Dot(p0 - ax.At, d);
            var radial = p0 - foot;
            if (radial.LengthSquared < 1e-12) { W.ShowToast(L("Точка на оси — укажите точку на стенке")); return; }
            double R = cylFace is { } f && Kernel.Ops.DirectEdit.RadiusAfter(f, 0) is { } rr ? rr : radial.Length;
            var on = foot + radial.Normalized() * R;
            F.Points.Clear(); F.PointFaces.Clear(); F.Points.Add(on); F.Direction = -radial.Normalized();
            if (!HoleFeature.PlaceOff && cylFace != null && cylFace.Tag.Length > 0)
            {
                F.RadFace = cylFace.Tag;
                // ноль угла — мировая X в плоскости поперёк оси (Y, если ось вдоль X); торец — ближайшая плоская грань поперёк оси
                var z = Vector3d.UnitX - d * d.X; if (z.LengthSquared < 1e-6) z = Vector3d.UnitY - d * d.Y;
                F.RadZeroDir = z.Normalized();
                var y = Vector3d.Cross(d, F.RadZeroDir);
                var rn = radial.Normalized();
                F.RadAngle = new ValueRef(Math.Round(Math.Atan2(Vector3d.Dot(rn, y), Vector3d.Dot(rn, F.RadZeroDir)) * 180 / Math.PI, 3));
                if (F.RadRefFace == null && _end.Items.Count == 0 && EndFace(cylFace, d) is { } ef) F.RadRefFace = ef.Tag;
                SyncAxial(on);
            }
        }
        if (_end.Touched && !HoleFeature.PlaceOff)
        {
            F.RadRefFace = _end.Items.Count > 0 && MoreFields.FaceOf(_end.Items[0]) is { } ef ? ef.Tag : null;
            if (F.Points.Count > 0) SyncAxial(F.Points[0]);
        }
    }

    private string? _placedSig;
    /// <summary>Плоская грань поперёк оси, соседняя с гранью цилиндра (ближайший торец).</summary>
    static Parametric3D.Kernel.Topology.TFace? EndFace(Parametric3D.Kernel.Topology.TFace cyl, Vector3d axis)
    {
        var near = cyl.Edges.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<Parametric3D.Kernel.Topology.TFace>().Distinct().Where(f => f != cyl);
        return near.FirstOrDefault(f => SketchFaceRef.PlaneOf(f) is { } pl && Math.Abs(Math.Abs(Vector3d.Dot(pl.N.Normalized(), axis)) - 1) < 1e-6 && f.Tag.Length > 0);
    }

    /// <summary>Расстояние вдоль оси от торца (или от начала отсчёта) до точки.</summary>
    void SyncAxial(Vector3d at)
    {
        if (F.RadFace == null || HoleFeature.CylinderByName(Part.Bodies, F.RadFace) is not { } cyl) return;
        double t0;
        var pl = F.RadRefFace == null ? null : Part.Bodies.SelectMany(b => b.Faces).Where(f => f.Tag == F.RadRefFace).Select(SketchFaceRef.PlaneOf).FirstOrDefault(x => x != null);
        if (pl is { } p) t0 = Vector3d.Dot(p.P - cyl.At, cyl.Axis);
        else { F.RadOrigin = cyl.At; t0 = 0; }
        F.RadAxial = new ValueRef(Math.Round(Vector3d.Dot(at - cyl.At, cyl.Axis) - t0, 3));
    }

    // ---------------------------------------------------------------- k153: ручка диаметра и гизмо (вдоль оси, вокруг оси)

    public override Action<double>? DragValue => v => F.Diameter = new ValueRef(Math.Abs(v));
    public override double? DragCurrent => F.Diameter.Value;
    public override string DragLabel => "Ø";
    public override double HandleToValue(double d) => 2 * d;
    public override double ValueToHandle(double v) => v / 2;

    (Vector3d P, Vector3d Axis, Vector3d Radial)? Place()
    {
        if (!Radial || F == null) return null;
        if (F.RadialPlacement(Part.Bodies, null) is { } rp && F.RadFace != null && HoleFeature.CylinderByName(Part.Bodies, F.RadFace) is { } cyl)
            return (rp.P, cyl.Axis, -rp.Dir);
        return null;
    }
    public (Vector3d At, Vector3d Out)? HandleAt() => Place() is { } pl ? (pl.P, pl.Axis) : null;

    public override void RefreshGizmo()
    {
        if (Place() is not { } pl) { W.ViewPort.Gizmo = null; return; }
        var x = pl.Radial; var z = pl.Axis; var y = Vector3d.Cross(z, x);
        W.ViewPort.Gizmo = new GizmoSpec(GizmoMode.Full, pl.P, x, y, z) { Mask = (1 << (int)GizmoPart.AxisZ) | (1 << (int)GizmoPart.RingZ) };
    }
    private GizmoSpec? _g0;
    private double _ax0, _an0;
    public override void GizmoStart(GizmoPart part) { _g0 = W.ViewPort.Gizmo; _ax0 = F.RadAxial.Value; _an0 = F.RadAngle.Value; }
    public override void GizmoMove(GizmoDrag d)
    {
        if (_g0 is not { } g) return;
        if (d.Part == GizmoPart.AxisZ) F.RadAxial = new ValueRef(Math.Round(_ax0 + d.Delta.Z, 3));
        else if (d.Part == GizmoPart.RingZ) F.RadAngle = new ValueRef(Math.Round(_an0 + d.AngleDeg, 3));
        if (Place() is { } pl) { F.Points.Clear(); F.Points.Add(pl.P); F.Direction = -pl.Radial; W.ViewPort.Gizmo = g with { Origin = pl.P, X = pl.Radial, Y = Vector3d.Cross(pl.Axis, pl.Radial) }; }
        GizmoValuesChanged();
    }
    public override void GizmoEnd() => _g0 = null;
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
