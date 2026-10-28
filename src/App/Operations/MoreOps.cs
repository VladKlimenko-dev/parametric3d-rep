using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Operations;

// u13: ОПЕРАЦИИ КНОПОК ТАБЛИЦЫ ИКОНОК — те, что уже были в модели и ядре без панели: протяжка, переход по сечениям, уклон,
// ребро жёсткости, опорные ось и точка.

internal static class MoreFields
{
    public static RefField Sketches(string label, bool optional = false) => new()
    {
        Label = label, Filter = PickFilter.SketchCurves, Accept = Refs.IsSketch, Optional = optional,
        Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?"),
    };
    public static RefField Faces(string label, bool optional = false) => new() { Label = label, Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face, Optional = optional };
    public static RefField Points(string label, bool optional = true) => new()
    {
        Label = label, Filter = PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums,
        Accept = r => r.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint, Optional = optional,
    };
    public static RefField AnyEdge(string label, bool optional = true) => new()
    {
        Label = label, Filter = PickFilter.Edges | PickFilter.SketchCurves | PickFilter.Datums,
        // k132: и опорная ось (ось по оси, касательная к ней)
        Accept = r => r.Kind is PickKind.Edge or PickKind.SketchCurve || r.Kind == PickKind.DatumAxis && r.Target is DatumAxis, Single = true, Optional = optional,
    };

    /// <summary>Кривая ссылки: ребро тела или кривая эскиза (в мировых координатах).</summary>
    public static ICurve3? CurveOf(PickRef r)
    {
        if (r.Kind == PickKind.Edge && r.Owner is Part p && p.FindEdge(r.Id) is { Curve: { } c }) return c;
        if (r.Target is SkEntity se && r.Owner is Sketch sk && se is not SkPoint)
            try { return se.ToCurve3(sk.Plane); } catch { return null; }
        return null;
    }

    /// <summary>Грань-тело ссылки.</summary>
    public static TFace? FaceOf(PickRef r) => r.Kind == PickKind.Face && r.Owner is Part p ? p.FindFace(r.Id) : null;
}

// ====================================================================== протяжка

public sealed class SweepOp : FeatureOp<SweepFeature>
{
    /// <summary>
    /// k107: состояние протяжки между её эскизами (как в Creo: из панели протяжки начать эскиз траектории или сечения и
    /// вернуться в неё с тем же выбором и значениями).
    /// </summary>
    public sealed class State
    {
        public uint Section, Path, PathLine;
        public bool Cut, Keep, Reverse;
        public ValueRef Twist = new(0), Fraction = new(1);
    }
    public State? Seed;

    private RefField _sec = null!, _path = null!, _secPoint = null!, _pathPlane = null!;
    private bool _pickSecPoint, _pickPathPlane, _leaving;
    public SweepOp(SweepFeature? existing = null) : base(existing) { }
    public override string Title => "Протяжка";
    public override string Icon => "f_sweep_but.svg";
    protected override SweepFeature Create()
    {
        var f = new SweepFeature();
        if (Seed is { } s) { f.Cut = s.Cut; f.KeepOrientation = s.Keep; f.Reverse = s.Reverse; f.Twist = s.Twist; f.LengthFraction = s.Fraction; }
        return f;
    }

    /// <summary>Разбор: идёт выбор точки начала сечения / плоскости траектории.</summary>
    public bool PickingSectionPoint => _pickSecPoint;
    public bool PickingPathPlane => _pickPathPlane;
    public RefField SectionPointField => _secPoint;
    public RefField PathPlaneField => _pathPlane;

    public override void Build(OpPanel p)
    {
        _sec = p.Ref(new RefField { Label = "Сечение", Filter = PickFilter.SketchCurves, Accept = Refs.IsSketch, Single = true, Describe = r => L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?") });
        // k107: эскиз сечения — в начале траектории, в плоскости, перпендикулярной ей (точка — у начала или конца пути)
        p.Button("Создать эскиз сечения", StartSectionPick, () => HasPath && !Editing,
            "Щёлкните конец траектории: там создаётся плоскость, перпендикулярная траектории, и начинается эскиз сечения");
        _secPoint = p.Ref(new RefField
        {
            Label = "Точка на траектории", Filter = PickFilter.SketchPoints | PickFilter.Vertices, Accept = r => Refs.PointOf(r) != null,
            // k108: точки траектории важнее начала координат детали, лежащего под ними
            Prefer = r => r.Kind is PickKind.SketchPoint or PickKind.Vertex,
            Single = true, Optional = true,
        }, () => _pickSecPoint);
        _path = p.Ref(new RefField { Label = "Траектория", Filter = PickFilter.SketchCurves | PickFilter.Datums, Accept = r => Refs.IsSketch(r) || r.Target is DatumCurve or Line3DItem, Single = true,
            Describe = r => r.Target is DatumCurve or Line3DItem ? L("Кривая «{0}»", ((ModelItem)r.Target!).Name) : L("Эскиз «{0}»", Refs.SketchOf(r)?.Name ?? "?") });
        // k107: эскиз траектории — на выбранной плоскости
        p.Button("Создать эскиз траектории", StartPathPick, () => !Editing,
            "Выберите плоскость или плоскую грань — на ней начнётся эскиз траектории");
        _pathPlane = p.Ref(new RefField
        {
            Label = "Плоскость траектории", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true,
        }, () => _pickPathPlane);
        if (Editing) { _sec.Kept = F.SectionSketchId != 0 ? 1 : 0; _path.Kept = F.PathSketchId != 0 || F.PathLineId != 0 ? 1 : 0; }
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Check("Обратное направление", () => F.Reverse, v => F.Reverse = v);
        p.Number("Закрутка", () => F.Twist, v => F.Twist = v, "°");
        p.Number("Доля траектории", () => F.LengthFraction, v => F.LengthFraction = v, "");
        p.Check("Сечение без поворота", () => F.KeepOrientation, v => F.KeepOrientation = v);
        p.Note("Сечение — замкнутый эскиз в начале траектории; траектория — эскиз с незамкнутой цепочкой. Стрелка на траектории — направление протяжки (щелчок — развернуть).");
    }

    /// <summary>Доступность кнопок зависит от выбора — обновить вместе с состоянием.</summary>
    public override void RefreshStatus() { base.RefreshStatus(); Panel?.Refresh(); }

    bool HasPath => _path != null && (_path.Items.Count > 0 || F.PathSketchId != 0 || F.PathLineId != 0);

    void StartSectionPick()
    {
        if (!HasPath) { W.ShowToast(L("Сначала выберите траекторию"), error: false); return; }
        _pickSecPoint = true; _pickPathPlane = false;
        // k108: концы и узлы траектории — видны и выбираются (вне правки эскиза точки эскиза не ловились)
        if (F.PathSketchId != 0) W.ViewPort.SetPointSketches(new[] { F.PathSketchId });
        Panel?.Refresh();
        Activate(_secPoint);
        W.ShowHint(L("Щёлкните конец траектории — там начнётся эскиз сечения"));
    }

    void StartPathPick()
    {
        _pickPathPlane = true; _pickSecPoint = false;
        Panel?.Refresh();
        Activate(_pathPlane);
        W.ShowHint(L("Выберите плоскость эскиза траектории"));
    }

    protected override void SeedRefs()
    {
        if (Seed is { } st)
        {
            if (Part.Sketches.FirstOrDefault(k => k.Id == st.Section) is { } ss) _sec.Take(new[] { Fields.SketchRef(ss) });
            if (Part.Sketches.FirstOrDefault(k => k.Id == st.Path) is { } ps) _path.Take(new[] { Fields.SketchRef(ps) });
            else if (st.PathLine != 0 && Part.Children.FirstOrDefault(c => c.Id == st.PathLine && c is DatumCurve or Line3DItem) is { } pl)
                _path.Take(new[] { new PickRef(pl.Id, PickKind.DatumAxis, Part, pl) });
            return;
        }
        // выбранные эскизы: первый — сечение, второй — траектория
        var sk = W.ViewPort.Selection.Items.Where(Refs.IsSketch).GroupBy(r => Refs.SketchOf(r)!.Id).Select(g => g.First()).ToList();
        if (sk.Count > 0) _sec.Take(new[] { sk[0] });
        if (sk.Count > 1) _path.Take(new[] { sk[1] });
        else if (W.ViewPort.Selection.Items.FirstOrDefault(r => r.Target is DatumCurve or Line3DItem) is { IsEmpty: false } dc) _path.Take(new[] { dc });
        // k107: один эскиз — незамкнутый (траектория) или замкнутый (сечение)
        if (sk.Count == 1 && Refs.SketchOf(sk[0]) is { } one && !IsClosed(one))
        { _sec.Items.Clear(); _path.Take(new[] { sk[0] }); }
    }

    static bool IsClosed(Sketch sk)
    {
        try { return !sk.ToProfile().IsEmpty; } catch { return false; }
    }

    State Snapshot() => new()
    {
        Section = F.SectionSketchId, Path = F.PathSketchId, PathLine = F.PathLineId,
        Cut = F.Cut, Keep = F.KeepOrientation, Reverse = F.Reverse, Twist = F.Twist, Fraction = F.LengthFraction,
    };

    protected override void Push()
    {
        if (_sec.Items.Count > 0 && Refs.SketchOf(_sec.Items[0]) is { } s) F.SectionSketchId = s.Id;
        else if (_sec.Touched) F.SectionSketchId = 0;
        if (_path.Items.Count > 0 && _path.Items[0].Target is DatumCurve or Line3DItem) { F.PathLineId = ((ModelItem)_path.Items[0].Target!).Id; F.PathSketchId = 0; }
        else if (_path.Items.Count > 0 && Refs.SketchOf(_path.Items[0]) is { } t) { F.PathSketchId = t.Id; F.PathLineId = 0; }
        if (_leaving) return;
        if (_pickPathPlane && _pathPlane.Items.Count > 0 && Refs.PlaneOf(_pathPlane.Items[0]) is { } pl) BeginPathSketch(_pathPlane.Items[0], pl);
        else if (_pickSecPoint && _secPoint.Items.Count > 0 && Refs.PointOf(_secPoint.Items[0]) is { } q) BeginSectionSketch(q);
    }

    /// <summary>Эскиз траектории на плоскости; «Готово» — назад в протяжку с ним, «Отмена» — назад без него.</summary>
    void BeginPathSketch(PickRef at, (Parametric3D.Kernel.Frame3 Frame, uint SourceId, string Name) pl)
    {
        _leaving = true;
        var st = Snapshot(); var w = W; var part = Part;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(w.CurrentOperation, this)) return;
            w.CancelOperation();
            w.BeginNewSketch(part, SketchOp.SketchFrame(at, pl.Frame), pl.SourceId, pl.Name, sk => { st.Path = sk.Id; st.PathLine = 0; return new SweepOp { Seed = st }; });
            w.AfterSketchCancel = () => new SweepOp { Seed = st };
        });
    }

    /// <summary>
    /// Эскиз сечения в начале траектории: точка у конца траектории — тот конец становится началом (направление протяжки
    /// разворачивается); плоскость — перпендикулярно траектории, ось V — по нормали плоскости траектории.
    /// </summary>
    void BeginSectionSketch(Vector3d q)
    {
        if (F.StartFrame(Part) is not { } sf) { W.ShowToast(L("Траектория не найдена")); return; }
        W.ViewPort.SetPointSketches(null);
        if ((q - sf.End).Length < (q - sf.P).Length) { F.Reverse = !F.Reverse; sf = F.StartFrame(Part)!.Value; }
        var frame = SectionFrame(sf.P, sf.T, sf.N);
        _leaving = true;
        var st = Snapshot(); var w = W; var part = Part;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(w.CurrentOperation, this)) return;
            w.CancelOperation();
            w.BeginNewSketch(part, frame, 0, L("Нормаль к траектории"), sk => { st.Section = sk.Id; return new SweepOp { Seed = st }; });
            w.AfterSketchCancel = () => new SweepOp { Seed = st };
        });
    }

    /// <summary>Плоскость сечения: начало — точка траектории, нормаль — касательная, V — нормаль плоскости траектории.</summary>
    public static Parametric3D.Kernel.Frame3 SectionFrame(Vector3d p, Vector3d t, Vector3d n)
    {
        t = t.Normalized();
        var v = n - t * Vector3d.Dot(n, t);
        if (v.LengthSquared < 1e-12) v = Math.Abs(t.Z) < 0.9 ? Vector3d.UnitZ - t * t.Z : Vector3d.UnitX - t * t.X;
        v = v.Normalized();
        var u = Vector3d.Cross(v, t).Normalized();
        return new Parametric3D.Kernel.Frame3(p, u, v);
    }

    /// <summary>k107: стрелка направления — у начала траектории, по ходу протяжки.</summary>
    public override IEnumerable<(Vector3d At, Vector3d Dir)> ViewArrows()
    {
        if (ArrowsOff || Part == null || _pickSecPoint) yield break;   // k108: при выборе точки начала стрелка не мешает
        (Vector3d P, Vector3d T, Vector3d N, Vector3d End)? sf = null;
        try { sf = F.StartFrame(Part); } catch { }
        if (sf is { } s) yield return (s.P, s.T);
    }

    public override void ArrowClicked(int index)
    {
        F.Reverse = !F.Reverse;
        FieldChanged();
    }

    static readonly bool ArrowsOff = Environment.GetEnvironmentVariable("P3D_SWEEPARROW") == "0";
}

// ====================================================================== переход по сечениям

public sealed class LoftOp : FeatureOp<LoftFeature>
{
    private RefField _secs = null!, _guides = null!;
    public LoftOp(LoftFeature? existing = null) : base(existing) { }
    public override string Title => "Переход";
    public override string Icon => "f_blend_but.svg";
    protected override LoftFeature Create() => new();

    public override void Build(OpPanel p)
    {
        _secs = p.Ref(MoreFields.Sketches("Сечения"));
        _guides = p.Ref(MoreFields.Sketches("Направляющие", optional: true));
        if (Editing) { _secs.Kept = F.SectionSketchIds.Count; _guides.Kept = F.GuideSketchIds.Count; }
        p.Segments("Тело", new[] { "Добавить", "Вырезать" }, () => F.Cut ? 1 : 0, v => F.Cut = v == 1);
        p.Check("Гладкий переход", () => F.Smooth, v => F.Smooth = v);
        p.Note("Сечения — по порядку выбора (не меньше двух).");
    }

    protected override void SeedRefs() => Seed(_secs);

    protected override void Push()
    {
        if (_secs.Touched)
        {
            F.SectionSketchIds.Clear();
            foreach (var r in _secs.Items) if (Refs.SketchOf(r) is { } s && !F.SectionSketchIds.Contains(s.Id)) F.SectionSketchIds.Add(s.Id);
        }
        if (_guides.Touched)
        {
            F.GuideSketchIds.Clear();
            foreach (var r in _guides.Items) if (Refs.SketchOf(r) is { } s && !F.GuideSketchIds.Contains(s.Id)) F.GuideSketchIds.Add(s.Id);
        }
    }

    protected override bool MissingRequiredExtra => F.SectionSketchIds.Count < 2;
}

// ====================================================================== уклон

public sealed class DraftOp : FeatureOp<DraftFeature>, IHandleOp
{
    private RefField _faces = null!, _neutral = null!, _moving = null!, _refPlane = null!;
    private readonly RefCache<Vector3d> _cache = new();
    private readonly RefCache<EdgeRef> _hingeCache = new(), _moveCache = new();
    private bool _flip;
    public DraftOp(DraftFeature? existing = null) : base(existing) { }
    public override string Title => "Уклон";
    public override string Icon => "f_draft_but.svg";
    protected override DraftFeature Create() => new();

    /// <summary>Мутация P3D_DRAFTHANDLE=0: у уклона по ребру нет ручки и стрелки.</summary>
    static readonly bool HandleOff = Environment.GetEnvironmentVariable("P3D_DRAFTHANDLE") == "0";

    /// <summary>k112: шарнир — ребро (а не нейтральная плоскость).</summary>
    public bool ByEdge => _neutral != null && (_neutral.Items.Count > 0 ? _neutral.Items[0].Kind == PickKind.Edge : !_neutral.Touched && F?.Hinge != null);

    public override void Build(OpPanel p)
    {
        _faces = p.Ref(MoreFields.Faces("Грани"));
        // k112: неподвижное — ребро выбранной грани (шарнир) или нейтральная плоскость
        _neutral = p.Ref(new RefField
        {
            Label = "Неподвижное: ребро или плоскость", Filter = PickFilter.Edges | PickFilter.Faces | PickFilter.Datums, Single = true,
            Accept = r => r.Kind == PickKind.Edge ? !Topo.IsSeam(r) : Refs.IsPlanar(r),
            Describe = r => r.Kind == PickKind.Edge ? L("Ребро (шарнир)") : Viewport.Selection.Label(r),
        });
        _moving = p.Ref(new RefField
        {
            Label = "Смещаемое ребро", Filter = PickFilter.Edges, Single = true, Optional = true,
            Accept = r => r.Kind == PickKind.Edge && !Topo.IsSeam(r) && !_neutral.Items.Any(x => x.Kind == PickKind.Edge && x.Id == r.Id),
        }, () => ByEdge);
        _refPlane = p.Ref(new RefField
        {
            Label = "Угол к плоскости", Filter = PickFilter.Faces | PickFilter.Datums, Single = true, Optional = true,
            Accept = r => r.Kind != PickKind.Edge && Refs.IsPlanar(r),
        }, () => ByEdge);
        if (Editing)
        {
            _faces.Kept = F.OnFaces.Count; _neutral.Kept = 1;
            if (F.Moving != null) _moving.Kept = 1;
            if (F.RefNormal != null) _refPlane.Kept = 1;
        }
        p.Choice("Угол задан", new[] { "От текущего (приращение)", "Итоговый между гранями" }, () => F.Total ? 1 : 0, SetTotal, visible: () => ByEdge);
        p.Number("Угол", () => F.Angle, v => F.Angle = v, "°");
        p.Check("Обратное направление", () => ByEdge ? F.Reverse : _flip, v => { if (ByEdge) F.Reverse = v; else _flip = v; }, visible: () => !(ByEdge && F.Total));
        p.Info(() => AngleInfo(), () => ByEdge);
    }

    /// <summary>k112: «к соседней грани: было → станет» (по последнему расчёту).</summary>
    public string AngleInfo()
    {
        if (F.LastBefore is not { } b) return F.LastHinge != null ? L("Угол между гранями не определён — выберите плоскость угла") : "";
        string to = F.RefNormal != null ? L("к плоскости") : L("к соседней грани");
        return F.LastAfter is { } a ? L("Угол {0}: {1}° → {2}°", to, Fmt(b), Fmt(a)) : L("Угол {0}: {1}°", to, Fmt(b));
    }
    static string Fmt(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>k112: шарнир-ребро выбран — дальше смещаемое ребро (если не задано).</summary>
    protected override RefField? NextAfter(RefField f) =>
        ReferenceEquals(f, _neutral) && ByEdge && !_moving.IsSet && _faces.IsSet ? _moving : null;

    public void SetTotalMode(bool total) { SetTotal(total ? 1 : 0); FieldChanged(); }

    /// <summary>Смена способа задания угла — геометрия та же: итоговый = нынешний результат, приращение = применённый поворот.</summary>
    void SetTotal(int v)
    {
        bool total = v == 1;
        if (total == F.Total) return;
        if (total && F.LastAfter is { } a) F.Angle = new ValueRef(Math.Round(a, 3));
        else if (!total && F.LastApplied is { } d && Math.Abs(d) > 1e-9) { F.Angle = new ValueRef(Math.Round(Math.Abs(d), 3)); F.Reverse = d < 0; }
        F.Total = total;
    }

    // ---------------------------------------------------------------- k112: ручка и стрелка у смещаемого ребра

    public override bool SignedDrag => ByEdge;
    public override string DragUnit => "°";
    public override Action<double>? DragValue => v =>
    {
        if (!ByEdge) { F.Angle = new ValueRef(v); return; }
        if (F.Total && F.LastHinge?.AngleTo(v, F.RefNormal) is { } t) { F.Angle = new ValueRef(Math.Round(t, 2)); return; }
        F.Angle = new ValueRef(Math.Abs(v)); F.Reverse = v < 0;
    };
    public override double? DragCurrent => ByEdge ? F.LastApplied ?? F.Angle.Value * (F.Reverse ? -1 : 1) : F.Angle.Value;
    public override string DragLabel => "∠";

    /// <summary>Ручка — у середины смещаемого ребра, вдоль его хода (положительный угол — внутрь); величина ручки — плечо·tg угла.</summary>
    public (Vector3d At, Vector3d Out)? HandleAt() => !HandleOff && ByEdge && F.LastHinge is { } h ? (h.MoveAt, h.MoveDir) : null;
    public override double HandleToValue(double d) => ByEdge && F.LastHinge is { Lever: > 0 } h ? Math.Atan(d / h.Lever) * 180 / Math.PI : d;
    public override double ValueToHandle(double v) => ByEdge && F.LastHinge is { Lever: > 0 } h ? h.Lever * Math.Tan(Math.Clamp(v, -89, 89) * Math.PI / 180) : v;

    /// <summary>Контур при перетаскивании: шарнир и смещённое ребро (у плоской грани — четырёхугольник).</summary>
    public Func<double, List<(Vector3d A, Vector3d B)>>? HandleGhost()
    {
        if (F.LastHinge is not { } h) return null;
        return d =>
        {
            double deg = HandleToValue(d);
            var m = h.MAt(deg); double len = h.Lever / Math.Max(1e-6, Math.Cos(deg * Math.PI / 180));
            if (h.Cone) return new() { (h.HingeP, h.HingeP + m * len) };
            var a = h.HingeA; var b = h.HingeB;
            return new() { (a, b), (b, b + m * len), (b + m * len, a + m * len), (a + m * len, a) };
        };
    }

    public override IEnumerable<(Vector3d At, Vector3d Dir)> ViewArrows()
    {
        if (HandleOff || !ByEdge || F.LastHinge is not { } h) yield break;
        double sgn = F.Total ? Math.Sign(F.LastApplied ?? 1) : F.Reverse ? -1 : 1;
        if (sgn == 0) sgn = 1;
        yield return (h.MoveAt, h.MoveDir * sgn);
    }

    public override void ArrowClicked(int index)
    {
        if (F.Total) return;
        F.Reverse = !F.Reverse;
        FieldChanged();
    }

    public override void RefreshStatus()
    {
        base.RefreshStatus();
        Panel?.Refresh();
        W.RefreshPlaneHandle();
    }

    protected override void SeedRefs()
    {
        Seed(_faces, _neutral);
    }

    protected override void Push()
    {
        if (_faces.Touched)
        {
            F.OnFaces.Clear(); F.OnFaceNames.Clear();
            F.OnFaces.AddRange(_cache.Resolve(_faces, Refs.PointOf));
        }
        if (_neutral.Items.Count > 0 && _neutral.Items[0].Kind == PickKind.Edge)
        {
            var he = _hingeCache.Resolve(_neutral, r => Part.FindEdge(r.Id) is { } e ? EdgeRef.Of(e) : (EdgeRef?)null).Select(x => (EdgeRef?)x).FirstOrDefault();
            if (he != null) F.Hinge = he;
        }
        else if (_neutral.Items.Count > 0 && Refs.PlaneOf(_neutral.Items[0]) is { } pl)
        {
            F.Hinge = null;
            F.NeutralPoint = pl.Frame.Origin;
            F.NeutralNormal = pl.Frame.W.Normalized() * (_flip ? -1 : 1);
        }
        else if (_neutral.Touched) F.Hinge = null;
        if (_moving.Touched)
            F.Moving = _moving.Items.Count > 0 ? _moveCache.Resolve(_moving, r => Part.FindEdge(r.Id) is { } e ? EdgeRef.Of(e) : (EdgeRef?)null).Select(x => (EdgeRef?)x).FirstOrDefault() : null;
        if (_refPlane.Touched)
            F.RefNormal = _refPlane.Items.Count > 0 && Refs.PlaneOf(_refPlane.Items[0]) is { } rp ? rp.Frame.W.Normalized() : null;
    }
}

// ====================================================================== ребро жёсткости

public sealed class RibOp : FeatureOp<RibFeature>
{
    private RefField _sk = null!;
    private int _dir;    // 0 −V, 1 +V, 2 −U, 3 +U (оси плоскости эскиза)
    public RibOp(RibFeature? existing = null) : base(existing) { }
    public override string Title => "Ребро жёсткости";
    public override string Icon => "f_rib_but.svg";
    protected override RibFeature Create() => new();
    public static readonly string[] Dirs = { "−V эскиза", "+V эскиза", "−U эскиза", "+U эскиза" };

    public override void Build(OpPanel p)
    {
        _sk = p.Ref(Fields.Sketch());
        if (Editing && F.SketchId != 0) _sk.Kept = 1;
        p.Number("Толщина", () => F.Thickness, v => F.Thickness = v);
        p.Choice("Рост", Dirs, () => _dir, v => _dir = v);
        p.Note("Контур эскиза наращивается в плоскости эскиза до тела и утолщается на обе стороны.");
    }

    public override Action<double>? DragValue => v => F.Thickness = new ValueRef(Math.Abs(v));
    public override double? DragCurrent => F.Thickness.Value;
    public override string DragLabel => "T";

    protected override void SeedRefs() => Seed(_sk);

    protected override void Push()
    {
        if (_sk.Items.Count == 0 || Refs.SketchOf(_sk.Items[0]) is not { } sk) return;
        F.SketchId = sk.Id;
        var u = sk.Plane.U.Normalized(); var v = sk.Plane.V.Normalized();
        F.Grow = _dir switch { 0 => -v, 1 => v, 2 => -u, _ => u };
    }
}

// ====================================================================== опорная ось

public sealed class AxisOp : Operation
{
    public static readonly bool DatumSeedOff = Environment.GetEnvironmentVariable("P3D_DATUMSEED") == "0";
    private RefField _pts = null!, _pl = null!, _pl2 = null!, _face = null!, _edge = null!;
    private int _by;
    private DatumAxis? _ax;
    public int InitialMethod = -1;
    public override string Title => "Опорная ось";
    public override string Icon => "dat_axis_but.svg";
    public static readonly string[] Methods = { "Через две точки", "Нормально к плоскости через точку", "Пересечение двух плоскостей", "Ось грани вращения", "По прямому ребру", "Касательно к кривой в точке" };

    /// <summary>
    /// k132 (замечание 28.10, 23:42, п. 2): ПРАВКА ГОТОВОЙ ОСИ (двойной щелчок, «Изменить»): способ и ссылки — из оси; «Отмена»
    /// — ось прежняя. P3D_DATUMEDIT=0 — правки нет.
    /// </summary>
    public DatumAxis? EditTarget;
    /// <summary>Разбор (k132): способ и число ссылок в полях.</summary>
    public int Method => _by;
    public int RefCount => new[] { _pts, _pl, _pl2, _face, _edge }.Sum(f => f?.Items.Count ?? 0);
    public static readonly bool EditOff = Environment.GetEnvironmentVariable("P3D_DATUMEDIT") == "0";
    private (Vector3d O, Vector3d D, int M, List<uint> R)? _orig;

    protected override bool Start()
    {
        if (EditTarget is { } ax)
        {
            W.Session.BeginEdit(L("Изменить «{0}»", ax.Name));
            _ax = ax; _by = ax.Method >= 0 ? ax.Method : 0;
            _orig = (ax.Origin, ax.Direction, ax.Method, ax.References.ToList());
            return true;
        }
        W.Session.BeginEdit(L(Title)); if (InitialMethod >= 0) _by = InitialMethod; return true;
    }

    public override void Build(OpPanel p)
    {
        p.Choice("Способ", Methods, () => _by, v => _by = v);
        _pts = p.Ref(MoreFields.Points("Точки"));
        _pl = p.Ref(new RefField { Label = "Плоскость", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true });
        _pl2 = p.Ref(new RefField { Label = "Вторая плоскость", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Single = true, Optional = true });
        _face = p.Ref(new RefField { Label = "Грань вращения", Filter = PickFilter.Faces, Accept = r => r.Kind == PickKind.Face && !Refs.IsPlanar(r), Single = true, Optional = true });
        _edge = p.Ref(MoreFields.AnyEdge("Ребро или кривая"));
    }

    public override void Prime()
    {
        if (EditTarget is { } eax)
        {
            SeedFrom(RefsOfIds(eax.References), _pts, _pl, _pl2, _face, _edge);
            foreach (var f in new[] { _pts, _pl, _pl2, _face, _edge }) if (f.Text != null) f.Text.Text = f.Summary;
            Panel?.Refresh(); RefreshStatus();
            return;
        }
        // k132 (замечание 28.10, 23:42, п. 1): способ — по выбранному до команды: две плоскости — пересечение, грань вращения —
        // её ось, прямое ребро или опорная ось — по ней, кривая и точка — касательная, две точки — через них, плоскость
        // (и точка) — нормально к плоскости. P3D_DATUMSEED=0 — без второй плоскости и без плоскости без точки.
        if (DatumSeedOff) Seed(_pts, _pl, _face, _edge); else Seed(_pts, _pl, _pl2, _face, _edge);
        if (InitialMethod < 0)
        {
            if (!DatumSeedOff && _pl.Items.Count > 0 && _pl2.Items.Count > 0) _by = 2;
            else if (_face.Items.Count > 0) _by = 3;
            else if (_edge.Items.Count > 0) _by = _pts.Items.Count > 0 && _edge.Items[0].Kind != PickKind.DatumAxis ? 5 : 4;
            else if (_pts.Items.Count >= 2) _by = 0;
            else if (_pl.Items.Count > 0 && (_pts.Items.Count > 0 || !DatumSeedOff)) _by = 1;
        }
        foreach (var f in new[] { _pts, _pl, _pl2, _face, _edge }) if (f.Text != null) f.Text.Text = f.Summary;
        Panel?.Refresh(); Apply(); RefreshStatus();
    }

    private (Vector3d At, Vector3d Dir)? Compute()
    {
        var pts = _pts.Items.Select(Refs.PointOf).OfType<Vector3d>().ToList();
        var pl = _pl.Items.Count > 0 ? Refs.PlaneOf(_pl.Items[0]) : null;
        var pl2 = _pl2.Items.Count > 0 ? Refs.PlaneOf(_pl2.Items[0]) : null;
        try
        {
            switch (_by)
            {
                case 0 when pts.Count >= 2: return DatumBuilder.AxisThroughPoints(pts[0], pts[1]);
                case 1 when pl is { } b: return DatumBuilder.AxisNormalToPlane(b.Frame, pts.Count > 0 ? pts[0] : b.Frame.Origin);
                case 2 when pl is { } a && pl2 is { } b2: return DatumBuilder.AxisOfPlanes(a.Frame, b2.Frame);
                case 3 when _face.Items.Count > 0 && MoreFields.FaceOf(_face.Items[0]) is { } f: return DatumBuilder.AxisOfFace(f);
                case 4 when _edge.Items.Count > 0 && Refs.LineOf(_edge.Items[0]) is { } l: return l;
                case 5 when _edge.Items.Count > 0 && MoreFields.CurveOf(_edge.Items[0]) is { } c:
                {
                    var at = pts.Count > 0 ? pts[0] : c.PointAt(0.5 * (c.T0 + c.T1));
                    return DatumBuilder.AxisTangentToCurve(c, ClosestT(c, at));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { W.ShowToast(L(Title) + ": " + ex.Message); }
        return null;
    }

    internal static double ClosestT(ICurve3 c, Vector3d p)
    {
        double best = c.T0, bd = double.MaxValue;
        for (int i = 0; i <= 200; i++) { double t = c.T0 + (c.T1 - c.T0) * i / 200.0; double d = (c.PointAt(t) - p).LengthSquared; if (d < bd) { bd = d; best = t; } }
        double h = (c.T1 - c.T0) / 200.0;
        for (int it = 0; it < 40; it++) { foreach (var s in new[] { -h, h }) { double t = Math.Clamp(best + s, Math.Min(c.T0, c.T1), Math.Max(c.T0, c.T1)); double d = (c.PointAt(t) - p).LengthSquared; if (d < bd) { bd = d; best = t; } } h *= 0.5; }
        return best;
    }

    protected override void Apply()
    {
        if (Compute() is not { } a) return;
        if (_ax == null) { _ax = new DatumAxis(L("Ось{0}", Part.Children.OfType<DatumAxis>().Count() + 1), a.At, a.Dir.Normalized()); Part.AddAtStage(_ax); }
        else { _ax.Origin = a.At; _ax.Direction = a.Dir.Normalized(); _ax.NotifyChanged(_ax); }
        // k132: способ и ссылки — для правки
        _ax.Method = _by;
        _ax.References.Clear();
        foreach (var f in new[] { _pts, _pl, _pl2, _face, _edge }) foreach (var r in f.Items) _ax.References.Add(r.Id);
        W.ViewPort.MarkDirty();
    }

    public override bool Commit()
    {
        if (_ax == null) { W.ShowToast(L("Выберите ссылки для способа «{0}»", L(Methods[_by]))); return false; }
        W.Session.CommitEdit(); return true;
    }

    public override void Cancel()
    {
        if (EditTarget is { } ax && _orig is { } o)
        {
            ax.Origin = o.O; ax.Direction = o.D; ax.Method = o.M; ax.References.Clear(); ax.References.AddRange(o.R); ax.NotifyChanged(ax);
            W.Session.DiscardEdit(); W.ViewPort.MarkDirty(); return;
        }
        if (_ax != null) Part.Remove(_ax); W.Session.DiscardEdit();
    }
}

// ====================================================================== опорная точка

public sealed class PointOp : Operation
{
    private RefField _pts = null!, _edge = null!, _pl = null!, _line = null!;
    private int _by;
    private DatumPoint? _pt;
    private ValueRef _ratio = new(0.5);
    public int InitialMethod = -1;
    public override string Title => "Опорная точка";
    public override string Icon => "dat_point_but.svg";
    public static readonly string[] Methods = { "В вершине (середина точек)", "В центре дуги", "На кривой (доля длины)", "Пересечение прямой и плоскости", "Пересечение трёх плоскостей" };

    /// <summary>k132: правка готовой точки (как оси).</summary>
    public DatumPoint? EditTarget;
    private (Vector3d P, int M, List<uint> R)? _orig;
    public int Method => _by;

    protected override bool Start()
    {
        if (EditTarget is { } pt)
        {
            W.Session.BeginEdit(L("Изменить «{0}»", pt.Name));
            _pt = pt; _by = pt.Method >= 0 ? pt.Method : 0;
            _orig = (pt.P, pt.Method, pt.References.ToList());
            return true;
        }
        W.Session.BeginEdit(L(Title)); if (InitialMethod >= 0) _by = InitialMethod; return true;
    }

    public override void Build(OpPanel p)
    {
        p.Choice("Способ", Methods, () => _by, v => _by = v);
        _pts = p.Ref(MoreFields.Points("Точки"));
        _edge = p.Ref(MoreFields.AnyEdge("Ребро или кривая"));
        _line = p.Ref(Fields.Line("Прямая", optional: true));
        _pl = p.Ref(new RefField { Label = "Плоскости", Filter = PickFilter.Faces | PickFilter.Datums, Accept = Refs.IsPlanar, Optional = true });
        p.Number("Доля длины", () => _ratio, v => _ratio = v, "", visible: () => _by == 2);
    }

    public override void Prime()
    {
        if (EditTarget is { } ept)
        {
            SeedFrom(RefsOfIds(ept.References), _pts, _line, _edge, _pl);
            foreach (var f in new[] { _pts, _edge, _line, _pl }) if (f.Text != null) f.Text.Text = f.Summary;
            Panel?.Refresh(); RefreshStatus();
            return;
        }
        // k132: прямая (ребро, опорная ось) — сначала в поле прямой, а не кривой: ось и плоскость — точка пересечения
        if (AxisOp.DatumSeedOff) Seed(_pts, _edge, _line, _pl); else Seed(_pts, _line, _edge, _pl);
        if (InitialMethod < 0)
        {
            if (_pl.Items.Count >= 3) _by = 4;
            else if (_line.Items.Count > 0 && _pl.Items.Count > 0) _by = 3;
            else if (!AxisOp.DatumSeedOff && _pts.Items.Count > 0 && _edge.Items.Count == 0) _by = 0;
            else if (!AxisOp.DatumSeedOff && _edge.Items.Count == 0 && _line.Items.Count > 0 && _line.Items[0].Kind == PickKind.Edge) { _edge.Take(_line.Items); _line.Items.Clear(); _by = MoreFields.CurveOf(_edge.Items[0]) is ArcCurve3 ? 1 : 2; }
            else if (_edge.Items.Count > 0 && MoreFields.CurveOf(_edge.Items[0]) is ArcCurve3) _by = 1;
            else if (_edge.Items.Count > 0) _by = 2;
        }
        foreach (var f in new[] { _pts, _edge, _line, _pl }) if (f.Text != null) f.Text.Text = f.Summary;
        Panel?.Refresh(); Apply(); RefreshStatus();
    }

    private Vector3d? Compute()
    {
        var pts = _pts.Items.Select(Refs.PointOf).OfType<Vector3d>().ToList();
        var pls = _pl.Items.Select(Refs.PlaneOf).Where(x => x != null).Select(x => x!.Value.Frame).ToList();
        try
        {
            switch (_by)
            {
                case 0 when pts.Count > 0: return pts.Aggregate(Vector3d.Zero, (a, b) => a + b) / pts.Count;
                case 1 when _edge.Items.Count > 0 && MoreFields.CurveOf(_edge.Items[0]) is { } c: return DatumBuilder.PointAtCentre(c);
                case 2 when _edge.Items.Count > 0 && MoreFields.CurveOf(_edge.Items[0]) is { } c2: return DatumBuilder.PointOnCurve(c2, _ratio.Value);
                case 3 when _line.Items.Count > 0 && Refs.LineOf(_line.Items[0]) is { } l && pls.Count > 0: return DatumBuilder.PointLineMeetsPlane(l.At, l.Dir, pls[0]);
                case 4 when pls.Count >= 3:
                {
                    var (at, dir) = DatumBuilder.AxisOfPlanes(pls[0], pls[1]);
                    return DatumBuilder.PointLineMeetsPlane(at, dir, pls[2]);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { W.ShowToast(L(Title) + ": " + ex.Message); }
        return null;
    }

    protected override void Apply()
    {
        if (Compute() is not { } q) return;
        if (_pt == null) { _pt = new DatumPoint(L("Точка{0}", Part.Children.OfType<DatumPoint>().Count() + 1), q); Part.AddAtStage(_pt); }
        else { _pt.P = q; _pt.NotifyChanged(_pt); }
        _pt.Method = _by;
        _pt.References.Clear();
        foreach (var f in new[] { _pts, _edge, _line, _pl }) foreach (var r in f.Items) _pt.References.Add(r.Id);
        W.ViewPort.MarkDirty();
    }

    public override bool Commit()
    {
        if (_pt == null) { W.ShowToast(L("Выберите ссылки для способа «{0}»", L(Methods[_by]))); return false; }
        W.Session.CommitEdit(); return true;
    }

    public override void Cancel()
    {
        if (EditTarget is { } pt && _orig is { } o)
        {
            pt.P = o.P; pt.Method = o.M; pt.References.Clear(); pt.References.AddRange(o.R); pt.NotifyChanged(pt);
            W.Session.DiscardEdit(); W.ViewPort.MarkDirty(); return;
        }
        if (_pt != null) Part.Remove(_pt); W.Session.DiscardEdit();
    }
}
