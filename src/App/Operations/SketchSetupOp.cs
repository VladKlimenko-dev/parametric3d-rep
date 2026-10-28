using Avalonia;
using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

/// <summary>
/// НАСТРОЙКА ЭСКИЗА (особый режим, как в прежнем приложении пользователя и в Creo «Sketch Setup»). Вид отъезжает на общий
/// вид детали: видны эскиз, плоскость-основа (рамка), стрелка нормали взгляда в центре эскиза и стрелка «верха». Панель:
/// плоскость-основа (щелчок по другой плоской грани или плоскости — новая основа), основа нулевой точки (вершина, точка) и
/// смещение нуля от неё, чем задан «верх» (ребро, грань или плоскость, точка). Щелчок по стрелке нормали — взгляд с
/// обратной стороны (стрелка переворачивается). «ОК» — один шаг отмены; после — снова правка эскиза (если начинали из неё).
/// </summary>
public sealed class SketchSetupOp : Operation
{
    private readonly uint _sketchId;
    private readonly bool _backToSketch;
    private Sketch _sk = null!;
    private RefField _plane = null!, _origin = null!, _up = null!;
    private double _dx, _dy;

    public SketchSetupOp(Sketch sk, bool backToSketch) { _sketchId = sk.Id; _backToSketch = backToSketch; }
    /// <summary>k131: эскиз был новым (пустой — удалится при «Готово»), и что начать после него.</summary>
    public bool WasNew;
    public Func<Sketch, Operation>? Then;

    public override string Title => "Настройка эскиза";
    public override string Icon => "sk_setup_but.svg";
    public Sketch Sketch => _sk;

    protected override bool Start()
    {
        if (Part.Sketches.FirstOrDefault(s => s.Id == _sketchId) is not { } sk) { W.ShowToast(L("Эскиз не найден")); return false; }
        _sk = sk;
        _dx = sk.OriginOffset.X; _dy = sk.OriginOffset.Y;
        W.Session.BeginEdit(L("Настройка эскиза «{0}»", sk.Name));
        // общий вид детали: изометрия, вписать всё
        var v = W.ViewPort;
        v.AnimateTo(Parametric3D.Render.ViewOrientation.Isometric);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => v.ZoomToFit(), Avalonia.Threading.DispatcherPriority.Background);
        v.ClickInterceptor = ArrowClick;
        v.HoverInterceptor = Hover;
        ShowArrows();
        return true;
    }

    public override void Build(OpPanel p)
    {
        p.Header(L("Эскиз «{0}»", _sk.Name));
        _plane = p.Ref(new RefField
        {
            Label = "Плоскость-основа", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true,
            Describe = r => Refs.PlaneOf(r)?.Name ?? "?",
        });
        // k99 (замечание 1): поля показывают то, что уже задано (основа — и выбором в виде), а не «не задано»
        if (!KeptOff)
        {
            _plane.KeptText = L(_sk.PlaneDescription);
            if (BaseRef() is { } br) { _plane.Items.Add(br); _plane.Touched = false; }
        }
        p.Note(L("Сейчас: {0}. Щелчок по другой плоской грани или плоскости — новая основа.", L(_sk.PlaneDescription)));
        _origin = p.Ref(new RefField
        {
            Label = "Основа нулевой точки (Origin)", Filter = PickFilter.Vertices | PickFilter.Datums | PickFilter.SketchPoints, Accept = r => Refs.PointOf(r) != null, Single = true, Optional = true,
        });
        if (!KeptOff) _origin.KeptText = L(_sk.OriginDescription);
        p.Note(L("Сейчас: {0}.", L(_sk.OriginDescription)));
        p.Number("Смещение нуля по X эскиза", () => new ValueRef(_dx), v => { _dx = v.Value; _sk.SetOriginOffset(new Vector2d(_dx, _dy)); ShowArrows(); });
        p.Number("Смещение нуля по Y эскиза", () => new ValueRef(_dy), v => { _dy = v.Value; _sk.SetOriginOffset(new Vector2d(_dx, _dy)); ShowArrows(); });
        // k101 (замечание 5): «верх» — ОДНО ИЗ РЁБЕР ОСНОВЫ: сторона рамки основы в виде или прямое ребро грани-основы;
        // эскиз поворачивается так, что выбранное ребро — сверху (как ориентация «Top» в Creo)
        _up = p.Ref(new RefField
        {
            Label = "Верх эскиза", Filter = PickFilter.Edges, Accept = UpCandidate, Single = true, Optional = true,
            Prefer = PreferOff ? null : UpCandidate,
        });
        if (!KeptOff) _up.KeptText = _sk.UpDescription.Length > 0 ? L(_sk.UpDescription) : L("ось плоскости");
        p.Note(L("Щёлкните сторону рамки основы или прямое ребро грани-основы — оно станет верхом эскиза. Сейчас: {0}.",
            _sk.UpDescription.Length > 0 ? L(_sk.UpDescription) : L("ось плоскости")));
        p.Check("Взгляд с обратной стороны (или щелчок по стрелке нормали)", () => _sk.PlaneDescription.EndsWith(" (flipped)"), _ => { _sk.Flip(); ShowArrows(); });
    }

    static readonly bool KeptOff = Environment.GetEnvironmentVariable("P3D_SKSETUPKEPT") == "0";
    static readonly bool PreferOff = Environment.GetEnvironmentVariable("P3D_SKSETUPUP") == "0";

    /// <summary>Основа эскиза ссылкой вида: опорная плоскость по номеру или грань по имени (номеру).</summary>
    public PickRef? BaseRef()
    {
        if (_sk.BasePlaneId != 0 && Part.FindById<DatumPlane>(_sk.BasePlaneId) is { } dp) return new PickRef(dp.Id, PickKind.DatumPlane, Part, dp);
        var faces = Part.Bodies.SelectMany(b => b.Faces);
        var f = (!string.IsNullOrEmpty(_sk.BaseFaceTag) ? faces.FirstOrDefault(x => x.Tag == _sk.BaseFaceTag) : null)
                ?? (_sk.BasePlaneId != 0 ? faces.FirstOrDefault(x => x.Id == _sk.BasePlaneId) : null);
        return f != null ? new PickRef(f.Id, PickKind.Face, Part, null) : null;
    }

    /// <summary>Годится ли под курсором для «верха»: прямое ребро, лежащее в плоскости основы (ребро грани-основы).</summary>
    public bool UpCandidate(PickRef r)
    {
        if (r.Kind != PickKind.Edge || Refs.LineOf(r) is not { } ln) return false;
        var n = _sk.Plane.W.Normalized(); var o = _sk.Plane.Origin;
        double tol = Math.Max(1e-6, Len() * 1e-6);
        return Math.Abs(Vector3d.Dot(ln.Dir.Normalized(), n)) < 1e-6 && Math.Abs(Vector3d.Dot(ln.At - o, n)) <= tol;
    }

    /// <summary>«Верх» — к прямой (ребру) в плоскости эскиза: перпендикуляр от центра эскиза к ней.</summary>
    public bool SetUpTowardLine(Vector3d at, Vector3d dir, string description)
    {
        var c = Center(); var n = _sk.Plane.W.Normalized();
        var d = dir - n * Vector3d.Dot(dir, n);
        if (d.Length < 1e-12) return false;
        d = d.Normalized();
        var foot = at + d * Vector3d.Dot(c - at, d);
        var up = foot - c; up -= n * Vector3d.Dot(up, n);
        if (up.Length < 1e-9 * Math.Max(1, Len())) up = d;   // ребро через центр — вдоль него
        return _sk.SetUpDirection(up, description);
    }

    /// <summary>Стороны рамки основы (мир): начало, конец; 0 — нижняя, 1 — правая, 2 — верхняя, 3 — левая.</summary>
    public (Vector3d A, Vector3d B)[] FrameSides()
    {
        var pl = _sk.Plane; var c = Center(); double h = Len() * 0.8;
        var q = new[] { c - pl.U * h - pl.V * h, c + pl.U * h - pl.V * h, c + pl.U * h + pl.V * h, c - pl.U * h + pl.V * h };
        return Enumerable.Range(0, 4).Select(i => (q[i], q[(i + 1) % 4])).ToArray();
    }

    static readonly string[] SideNames = { "нижняя сторона рамки", "правая сторона рамки", "верхняя сторона рамки", "левая сторона рамки" };

    /// <summary>Подсвечено под курсором: 0 — ничего, 1 — стрелка нормали, 2 — стрелка «верха», 10 + i — сторона рамки i.</summary>
    public int Hot { get; private set; }

    private int HitAt(Point p)
    {
        var v = W.ViewPort;
        double Seg(Vector3d a, Vector3d b)
        {
            if (v.Project(a) is not { } pa || v.Project(b) is not { } pb) return double.MaxValue;
            double dx = pb.X - pa.X, dy = pb.Y - pa.Y, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((p.X - pa.X) * dx + (p.Y - pa.Y) * dy) / l2, 0, 1);
            double ex = pa.X + dx * t - p.X, ey = pa.Y + dy * t - p.Y;
            return Math.Sqrt(ex * ex + ey * ey);
        }
        var c = Center(); double L = Len(); var pl = _sk.Plane;
        if (Seg(c + pl.W.Normalized() * L * 0.3, c + pl.W.Normalized() * L) <= 8) return 1;
        if (Seg(c + pl.V.Normalized() * L * 0.2, c + pl.V.Normalized() * L * 0.7) <= 8) return 2;
        var sides = FrameSides();
        int best = 0; double bd = 8;
        for (int i = 0; i < 4; i++) { double d = Seg(sides[i].A, sides[i].B); if (d < bd) { bd = d; best = 10 + i; } }
        return best;
    }

    private void Hover(Point p)
    {
        int h = HitAt(p);
        if (h == Hot) return;
        Hot = h;
        ShowArrows();
    }

    protected override void Apply()
    {
        // плоскость-основа
        if (_plane.Items.Count > 0 && _plane.Touched && Refs.PlaneOf(_plane.Items[0]) is { } pl)
        {
            var frame = SketchOp.SketchFrame(_plane.Items[0], pl.Frame);
            _sk.SetBasePlane(frame, pl.SourceId, pl.Name);
            SketchFaceRef.AttachById(_sk, Part.Bodies, pl.SourceId);
            _sk.OriginBase = null; _sk.OriginOffset = Vector2d.Zero; _dx = _dy = 0; _sk.UpDescription = "";
            _plane.Touched = false;
        }
        // основа нулевой точки
        if (_origin.Items.Count > 0 && _origin.Touched && Refs.PointOf(_origin.Items[0]) is { } op)
        {
            _sk.SetOrigin(op, Describe(_origin.Items[0]));
            _origin.Touched = false;
        }
        // верх
        if (_up.Items.Count > 0 && _up.Touched)
        {
            var r = _up.Items[0];
            if (Refs.LineOf(r) is { } ln && !SetUpTowardLine(ln.At, ln.Dir, Describe(r))) W.ShowToast(L("Это направление перпендикулярно плоскости эскиза — «верх» им не задать"));
            _up.Touched = false;
            if (_up.Text != null) _up.Text.Text = L(_sk.UpDescription);
        }

        W.ViewPort.MarkDirty();
        ShowArrows();
    }

    private string Describe(PickRef r) => r.Target is ModelItem mi ? mi.Name : r.Kind switch
    {
        PickKind.Vertex => L("Вершина"), PickKind.Edge => L("Ребро"), PickKind.Face => L("Грань"), _ => r.Kind.ToString(),
    } + (r.Kind is PickKind.Vertex or PickKind.Edge or PickKind.Face ? $" #{r.Id}" : "");

    // ---------------------------------------------------------------- стрелки в виде

    private Vector3d Center()
    {
        var pts = _sk.AllPoints().Select(q => q.P).ToList();
        var c2 = pts.Count == 0 ? Vector2d.Zero : pts.Aggregate(Vector2d.Zero, (a, b) => a + b) / pts.Count;
        return _sk.Plane.ToWorld(c2);
    }

    private double Len()
    {
        var b = Part.Bounds;
        double d = b.IsEmpty ? 50 : (b.Max - b.Min).Length;
        return Math.Max(10, d * 0.25);
    }

    /// <summary>Стрелка нормали (оранжевая), «верх» (зелёная), рамка плоскости-основы (синяя).</summary>
    public void ShowArrows()
    {
        var pl = _sk.Plane; var c = Center(); double L = Len();
        var lines = new List<(Vector3d, Vector3d, Vector4)>();
        void Arrow(Vector3d from, Vector3d dir, double len, Vector4 col)
        {
            var tip = from + dir * len;
            lines.Add((from, tip, col));
            var side = Vector3d.Cross(dir, Math.Abs(Vector3d.Dot(dir, pl.U)) < 0.9 ? pl.U : pl.V).Normalized();
            var side2 = Vector3d.Cross(dir, side).Normalized();
            foreach (var s in new[] { side, -side, side2, -side2 }) lines.Add((tip, tip - dir * len * 0.15 + s * len * 0.07, col));
        }
        var orange = new Vector4(1f, 0.55f, 0.1f, 1f); var green = new Vector4(0.3f, 0.85f, 0.4f, 1f); var blue = new Vector4(0.35f, 0.6f, 1f, 0.9f);
        var hot = new Vector4(1f, 0.92f, 0.3f, 1f);
        // k101 (замечание 1): стрелка или сторона рамки под курсором — подсвечена (сторона рамки — щелчком «верх»)
        Arrow(c, pl.W.Normalized(), L, Hot == 1 ? hot : orange);
        Arrow(c, pl.V.Normalized(), L * 0.7, Hot == 2 ? hot : green);
        var sides = FrameSides();
        for (int i = 0; i < 4; i++)
        {
            lines.Add((sides[i].A, sides[i].B, Hot == 10 + i ? hot : blue));
            if (Hot == 10 + i) { var off = (sides[i].A + sides[i].B) / 2 - c; lines.Add((sides[i].A + off * 0.005, sides[i].B + off * 0.005, hot)); }
        }
        // нулевая точка эскиза — крестик
        double k = L * 0.08;
        lines.Add((pl.Origin - pl.U * k, pl.Origin + pl.U * k, green)); lines.Add((pl.Origin - pl.V * k, pl.Origin + pl.V * k, green));
        W.ViewPort.SetAnnotations(lines);
    }

    /// <summary>Щелчок рядом со стрелкой нормали — перевернуть взгляд.</summary>
    private bool ArrowClick(Point p)
    {
        // k101: щелчок по стороне рамки основы — она становится верхом эскиза
        int h = HitAt(p);
        if (h >= 10)
        {
            var (sa, sb) = FrameSides()[h - 10];
            if (SetUpTowardLine(sa, sb - sa, SideNames[h - 10])) { _up.Items.Clear(); _up.Touched = false; if (_up.Text != null) _up.Text.Text = L(_sk.UpDescription); }
            Hot = 0;
            W.ViewPort.MarkDirty(); ShowArrows(); Panel?.Refresh();
            return true;
        }
        var v = W.ViewPort;
        var c = Center(); var tip = c + _sk.Plane.W.Normalized() * Len();
        if (v.Project(c) is not { } a || v.Project(tip) is not { } b) return false;
        var ab = b - a; double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / Math.Max(1e-9, ab.X * ab.X + ab.Y * ab.Y), 0.3, 1);
        double dx = a.X + ab.X * t - p.X, dy = a.Y + ab.Y * t - p.Y;
        if (dx * dx + dy * dy > 10 * 10) return false;
        FlipView();
        return true;
    }

    /// <summary>Взгляд с обратной стороны (стрелка нормали — в обратную сторону).</summary>
    public void FlipView()
    {
        _sk.Flip();

        W.ViewPort.MarkDirty();
        ShowArrows();
        Panel?.Refresh();
    }

    private void Leave()
    {
        W.ViewPort.ClickInterceptor = null;
        W.ViewPort.HoverInterceptor = null;
        W.ViewPort.SetAnnotations(Array.Empty<(Vector3d, Vector3d, Vector4)>());
    }

    public override bool Commit()
    {
        Leave();
        W.Session.CommitEdit();
        if (_backToSketch) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (W.Session.Part?.Sketches.FirstOrDefault(s => s.Id == _sketchId) is { } sk) W.ResumeSketchAfterSetup(sk, WasNew, Then); });
        return true;
    }

    public override void Cancel()
    {
        Leave();
        W.Session.CancelEdit();
        if (_backToSketch) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (W.Session.Part?.Sketches.FirstOrDefault(s => s.Id == _sketchId) is { } sk) W.ResumeSketchAfterSetup(sk, WasNew, Then); });
    }
}
