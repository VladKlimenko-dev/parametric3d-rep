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
        _up = p.Ref(new RefField
        {
            Label = "Верх эскиза", Filter = PickFilter.Edges | PickFilter.Faces | PickFilter.Datums | PickFilter.Vertices,
            Accept = r => Refs.LineOf(r) != null || Refs.IsPlanar(r) || Refs.PointOf(r) != null, Single = true, Optional = true,
            // k99 (замечание 2): под курсором — только то, что задаёт «верх»: ребро не поперёк эскиза, грань или плоскость,
            // перпендикулярная ему (прежде ловились любые плоскости — большие опорные перекрывали грань основы)
            Prefer = PreferOff ? null : UpCandidate,
        });
        if (!KeptOff) _up.KeptText = _sk.UpDescription.Length > 0 ? _sk.UpDescription : L("ось плоскости");
        p.Note(L("Ребро — вдоль него; грань или плоскость, перпендикулярная эскизу, — по её нормали; точка — от нуля к ней. Сейчас: {0}.",
            _sk.UpDescription.Length > 0 ? _sk.UpDescription : L("ось плоскости")));
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

    /// <summary>Годится ли под курсором для «верха»: ребро не поперёк эскиза, плоскость/грань — поперёк, точка.</summary>
    public bool UpCandidate(PickRef r)
    {
        var n = _sk.Plane.W.Normalized();
        if (Refs.LineOf(r) is { } ln) return Math.Abs(Vector3d.Dot(ln.Dir.Normalized(), n)) < 1 - 1e-6;
        if (Refs.PlaneOf(r) is { } pl) return Math.Abs(Vector3d.Dot(pl.Frame.W.Normalized(), n)) < 1e-6;
        if (r.Kind == PickKind.Face) return false;   // кривая грань — «верха» не задаёт
        return Refs.PointOf(r) != null;
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
            Vector3d? dir = Refs.LineOf(r) is { } ln ? ln.Dir
                : Refs.PlaneOf(r) is { } upl ? upl.Frame.W
                : Refs.PointOf(r) is { } pt ? pt - _sk.Plane.Origin : null;
            if (dir is { } d && !_sk.SetUpDirection(d, Describe(r))) W.ShowToast(L("Это направление перпендикулярно плоскости эскиза — «верх» им не задать"));
            _up.Touched = false;
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
        Arrow(c, pl.W.Normalized(), L, orange);
        Arrow(c, pl.V.Normalized(), L * 0.7, green);
        double h = L * 0.8;
        var q = new[] { c - pl.U * h - pl.V * h, c + pl.U * h - pl.V * h, c + pl.U * h + pl.V * h, c - pl.U * h + pl.V * h };
        for (int i = 0; i < 4; i++) lines.Add((q[i], q[(i + 1) % 4], blue));
        // нулевая точка эскиза — крестик
        double k = L * 0.08;
        lines.Add((pl.Origin - pl.U * k, pl.Origin + pl.U * k, green)); lines.Add((pl.Origin - pl.V * k, pl.Origin + pl.V * k, green));
        W.ViewPort.SetAnnotations(lines);
    }

    /// <summary>Щелчок рядом со стрелкой нормали — перевернуть взгляд.</summary>
    private bool ArrowClick(Point p)
    {
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
        W.ViewPort.SetAnnotations(Array.Empty<(Vector3d, Vector3d, Vector4)>());
    }

    public override bool Commit()
    {
        Leave();
        W.Session.CommitEdit();
        if (_backToSketch) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (W.Session.Part?.Sketches.FirstOrDefault(s => s.Id == _sketchId) is { } sk) W.EditSketch(sk); });
        return true;
    }

    public override void Cancel()
    {
        Leave();
        W.Session.CancelEdit();
        if (_backToSketch) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (W.Session.Part?.Sketches.FirstOrDefault(s => s.Id == _sketchId) is { } sk) W.EditSketch(sk); });
    }
}
