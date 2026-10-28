using Avalonia.Controls;
using Avalonia.Input;
using OpenTK.Mathematics;
using Parametric3D.App.Operations;
using Parametric3D.App.Viewport;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Render;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// u13d: КНОПКИ ТАБЛИЦЫ ИКОНОК — ВИД, ВЫБОР, ИЗМЕРЕНИЕ И АНАЛИЗ: стандартные виды, вид по нормали, вписать выбранное,
/// сечение, сетка; режимы выбора (вершина, ребро, грань, тело, операция), фильтр, рамка и аркан, касательная цепочка,
/// контур грани; измерить, площадь, периметр, массовые свойства, зебра, гребёнка кривизны, проверка тела, залечивание.
/// </summary>
public static class ViewSelectCommands
{
    public static void Register(CommandRegistry r, MainWindow w)
    {
        var vp = w.ViewPort;
        KeyGesture K(Key k, KeyModifiers m = KeyModifiers.None) => new(k, m);
        bool HasPart() => w.Session?.Part != null;
        const string V = "Вид", S = "Выбор", An = "Анализ";
        void View(string id, string label, string icon, ViewOrientation o, string syn, KeyGesture g) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = V, Icon = icon, Synonyms = syn, Gesture = g, Run = () => Orient(w, o) });
        View("view.front", "Вид спереди", "view_front_but.svg", ViewOrientation.Front, "front спереди", K(Key.D1, KeyModifiers.Control));
        View("view.back", "Вид сзади", "view_back_but.svg", ViewOrientation.Back, "back сзади", K(Key.D2, KeyModifiers.Control));
        View("view.left", "Вид слева", "view_left_but.svg", ViewOrientation.Left, "left слева", K(Key.D3, KeyModifiers.Control));
        View("view.right", "Вид справа", "view_right_but.svg", ViewOrientation.Right, "right справа", K(Key.D4, KeyModifiers.Control));
        View("view.top", "Вид сверху", "view_top_but.svg", ViewOrientation.Top, "top сверху", K(Key.D5, KeyModifiers.Control));
        View("view.bottom", "Вид снизу", "view_bottom_but.svg", ViewOrientation.Bottom, "bottom снизу", K(Key.D6, KeyModifiers.Control));
        r.Add(new AppCommand { Id = "view.normal", Label = "Вид по нормали", Group = V, Icon = "view_normal_but.svg", Synonyms = "normal to look at по нормали", Gesture = K(Key.N),
            Run = () => NormalTo(w) });
        r.Add(new AppCommand { Id = "view.zoom_sel", Label = "Вписать выбранное", Group = V, Icon = "view_zoom_sel_but.svg", Synonyms = "zoom selection fit selected вписать выбранное", Gesture = K(Key.Z),
            Run = () => ZoomSelection(w) });
        r.Add(new AppCommand { Id = "view.section", Label = "Сечение модели плоскостью", Group = V, Icon = "section_view_but.svg", Synonyms = "section clip cut view сечение разрез",
            IsChecked = () => vp.SectionPlane != null, Run = () => ToggleSection(w) });
        r.Add(new AppCommand { Id = "view.grid", Label = "Сетка", Group = V, Icon = "grid_but.svg", Synonyms = "grid сетка",
            IsChecked = () => vp.ModelGrid, Run = () => vp.ModelGrid = !vp.ModelGrid });
        // выбор
        void Mode(string id, string label, string icon, SelMode m, string syn, Key k) =>
            r.Add(new AppCommand { Id = id, Label = label, Group = S, Icon = icon, Synonyms = syn, Gesture = K(k), IsChecked = () => ViewSelectCommands.Mode == m, Run = () => SetMode(w, m) });
        Mode("sel.vertex", "Выбор: вершины и точки", "sel_vertex_but.svg", SelMode.Vertex, "select vertex point вершина точка", Key.D1);
        Mode("sel.edge", "Выбор: рёбра и кривые", "sel_edge_but.svg", SelMode.Edge, "select edge curve ребро кривая", Key.D2);
        Mode("sel.face", "Выбор: грани", "sel_face_but.svg", SelMode.Face, "select face грань", Key.D3);
        Mode("sel.body", "Выбор: тела", "sel_body_but.svg", SelMode.Body, "select body part тело деталь", Key.D4);
        Mode("sel.feature", "Выбор: операции", "sel_feature_but.svg", SelMode.Feature, "select feature операция", Key.D5);
        r.Add(new AppCommand { Id = "sel.filter", Label = "Фильтр типов выбора", Group = S, Icon = "sel_filter_but.svg", Synonyms = "selection filter фильтр выбора",
            Run = () => FilterMenu(w, null) });
        r.Add(new AppCommand { Id = "sel.box", Label = "Выбор рамкой", Group = S, Icon = "sel_box_but.svg", Synonyms = "box select window рамка",
            IsChecked = () => vp.RegionSelect && vp.RegionMode == ModelViewport.RegionKind.Box,
            Run = () => { if (vp.RegionSelect && vp.RegionMode == ModelViewport.RegionKind.Box) vp.RegionSelect = false; else { vp.RegionSelect = true; vp.RegionMode = ModelViewport.RegionKind.Box; } w.RefreshToolStates(); } });
        r.Add(new AppCommand { Id = "sel.lasso", Label = "Выбор арканом", Group = S, Icon = "sel_lasso_but.svg", Synonyms = "lasso select аркан",
            IsChecked = () => vp.RegionSelect && vp.RegionMode == ModelViewport.RegionKind.Lasso,
            Run = () => { if (vp.RegionSelect && vp.RegionMode == ModelViewport.RegionKind.Lasso) vp.RegionMode = ModelViewport.RegionKind.Box; else { vp.RegionSelect = true; vp.RegionMode = ModelViewport.RegionKind.Lasso; } w.RefreshToolStates(); } });
        r.Add(new AppCommand { Id = "sel.tangent_chain", Label = "Касательная цепочка рёбер", Group = S, Icon = "sel_tangent_chain_but.svg", Synonyms = "tangent chain касательная цепочка",
            IsChecked = () => vp.AutoChain,
            Run = () => { if (vp.Selection.Items.Any(x => x.Kind == PickKind.Edge)) { int n = ExpandChains(w); w.ShowToast(L("Добавлено рёбер: {0}", n), error: false); } else { vp.AutoChain = !vp.AutoChain; w.RefreshToolStates(); } } });
        r.Add(new AppCommand { Id = "sel.face_loop", Label = "Контур грани", Group = S, Icon = "sel_face_loop_but.svg", Synonyms = "face loop edges контур грани рёбра",
            CanRun = HasPart, Run = () => FaceLoop(w) });
        // анализ
        r.Add(new AppCommand { Id = "an.measure", Label = "Измерить", Group = An, Icon = Parametric3D.App.Icons.IconProvider.Exists("dim_size_but.svg") ? "dim_size_but.svg" : "dim_linear_but.svg", Synonyms = "measure distance angle radius измерить расстояние угол радиус", Gesture = K(Key.M),
            CanRun = HasPart, Run = async () => { if (!MeasureOff && CreateMeasure(w) != null) return; await Dialogs.Info(w, L("Измерение"), string.Join("\n", Measure(w))); } });
        r.Add(new AppCommand { Id = "an.area", Label = "Площадь", Group = An, Icon = "dim_area_but.svg", Synonyms = "area площадь",
            CanRun = HasPart, Run = () => w.ShowToast(L("Площадь выбранных граней: {0} мм²", F(SelectedArea(w))), error: false) });
        r.Add(new AppCommand { Id = "an.perimeter", Label = "Периметр / длина", Group = An, Icon = "dim_perimeter_but.svg", Synonyms = "perimeter length периметр длина",
            CanRun = HasPart, Run = () => w.ShowToast(L("Длина: {0} мм", F(SelectedLength(w))), error: false) });
        r.Add(new AppCommand { Id = "an.mass", Label = "Массовые свойства", Group = An, Icon = "mass_but.svg", Synonyms = "mass properties inertia масса центр инерция",
            CanRun = HasPart, Run = async () => await Dialogs.Info(w, L("Массовые свойства"), MassText(w.Session.Part!)) });
        r.Add(new AppCommand { Id = "an.zebra", Label = "Зебра", Group = An, Icon = "zebra.svg", Synonyms = "zebra stripes reflection зебра",
            IsChecked = () => vp.Zebra, Run = () => vp.Zebra = !vp.Zebra });
        r.Add(new AppCommand { Id = "an.curvature", Label = "Кривизна (гребёнка)", Group = An, Icon = "an_curvature_but.svg", Synonyms = "curvature comb кривизна гребёнка",
            IsChecked = () => vp.Annotations.Count > 0,
            Run = () => { if (vp.Annotations.Count > 0) vp.SetAnnotations(Array.Empty<(Vector3d, Vector3d, Vector4)>()); else { var c = CurvatureComb(w); if (c.Count == 0) w.ShowToast(L("Выберите рёбра или кривые эскиза"), error: false); vp.SetAnnotations(c); } } });
        r.Add(new AppCommand { Id = "an.check_body", Label = "Проверка тела", Group = An, Icon = "an_check_body_but.svg", Synonyms = "check body validate проверка тела",
            CanRun = HasPart, Run = async () => await Dialogs.Info(w, L("Проверка тела"), string.Join("\n", CheckBodies(w.Session.Part!))) });
        r.Add(new AppCommand { Id = "an.heal", Label = "Залечить", Group = An, Icon = "an_heal_but.svg", Synonyms = "heal repair import залечить",
            CanRun = HasPart, Run = () => Heal(w) });
    }

    public static string MassText(Part part)
    {
        var m = MassSummary.Of(part);
        return string.Join("\n", new[]
        {
            L("Материал: {0} ({1} кг/м³)", part.Material?.Name ?? "—", F(part.Material?.Density ?? 0)),
            L("Объём: {0} мм³", F(part.Volume)), L("Масса: {0} кг", (part.Mass).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)),
            L("Площадь поверхности: {0} мм²", F(part.Bodies.Sum(b => b.Faces.Sum(f => FaceArea(part, f) ?? 0)))),
            L("Центр масс: {0}", Pt(m.Centre)),
            L("Моменты инерции (кг·мм²): Ixx {0}, Iyy {1}, Izz {2}", F(m.Ixx), F(m.Iyy), F(m.Izz)),
            L("Центробежные: Ixy {0}, Ixz {1}, Iyz {2}", F(m.Ixy), F(m.Ixz), F(m.Iyz)),
        });
    }

    /// <summary>Залечить: тела без операций (импорт) — сшивка зазоров, мелкие рёбра; деталь с операциями — отказ.</summary>
    public static void Heal(MainWindow w)
    {
        var part = w.Session.Part!;
        if (part.Features.Count > 0) { w.ShowToast(L("Залечивание — для импортированных тел (деталь без операций)"), error: false); return; }
        var warnings = new List<string>();
        int fixedN = 0;
        w.Session.Edit(L("Залечить"), () =>
        {
            for (int i = 0; i < part.Bodies.Count; i++)
            {
                var rep = Kernel.Export.ImportHeal.Heal(part.Bodies[i], warnings, $"body {i + 1}");
                if (TopologyHeal.Heal(part.Bodies[i], 0, warnings)) fixedN++;
            }
            part.Invalidate();
        });
        w.ViewPort.MarkDirty();
        w.ShowToast(warnings.Count == 0 ? L("Тела в порядке — залечивать нечего") : L("Залечено: {0}", string.Join("; ", warnings.Take(4))), error: false);
    }

    // ---------------------------------------------------------------- вид

    public static void Orient(MainWindow w, ViewOrientation o) { w.ViewPort.AnimateTo(o); }

    /// <summary>Нормаль вида: плоскость активного эскиза, выбранная плоская грань или опорная плоскость.</summary>
    public static (Vector3d At, Vector3d N)? PlaneOfSelection(MainWindow w)
    {
        foreach (var r in w.ViewPort.Selection.Items)
            if (Refs.PlaneOf(r) is { } pl) return (pl.Frame.Origin, pl.Frame.W.Normalized());
        return null;
    }

    public static void NormalTo(MainWindow w)
    {
        Vector3d n, up;
        if (w.ViewPort.ActiveSketch is { } sk) { n = sk.Plane.W; up = sk.Plane.V; }
        else if (PlaneOfSelection(w) is { } p) { n = p.N; up = Math.Abs(n.Z) < 0.9 ? Vector3d.UnitZ : Vector3d.UnitY; }
        else { w.ShowToast(L("Выберите плоскую грань или плоскость"), error: false); return; }
        w.ViewPort.AnimateTo(ViewMath.LookFrom(new Vector3((float)n.X, (float)n.Y, (float)n.Z), new Vector3((float)up.X, (float)up.Y, (float)up.Z)));
    }

    /// <summary>Точки выбранного (мир): вершины граней и рёбер по сетке показа, точки эскиза и опорные.</summary>
    public static List<Vector3d> PointsOf(MainWindow w, IEnumerable<PickRef> refs)
    {
        var pts = new List<Vector3d>();
        foreach (var r in refs)
        {
            if (r.Owner is Part p && p.Mesh is { } md)
            {
                if (r.Kind == PickKind.Face) pts.AddRange(md.Vertices.Where(v => v.FaceId == r.Id).Select(v => new Vector3d(v.P.X, v.P.Y, v.P.Z)));
                else if (r.Kind == PickKind.Edge && md.EdgePolylines.FirstOrDefault(e => e.Item1 == r.Id).Item2 is { } pl) pts.AddRange(pl.Select(v => new Vector3d(v.X, v.Y, v.Z)));
                else if (r.Kind == PickKind.Vertex && p.FindVertex(r.Id) is { } tv) pts.Add(tv.P);
                else if (r.Kind == PickKind.Body) pts.AddRange(md.Vertices.Select(v => new Vector3d(v.P.X, v.P.Y, v.P.Z)));
            }
            if (Refs.PointOf(r) is { } q && r.Kind is PickKind.SketchPoint or PickKind.DatumPoint) pts.Add(q);
            if (r.Target is Feature f && f.Parent is Part fp && fp.Mesh is { } fmd)
                pts.AddRange(fmd.Vertices.Where(v => fp.FindFace(v.FaceId)?.Tag.StartsWith("#" + f.Key + "/", StringComparison.Ordinal) == true).Select(v => new Vector3d(v.P.X, v.P.Y, v.P.Z)));
        }
        return pts;
    }

    public static void ZoomSelection(MainWindow w)
    {
        var pts = PointsOf(w, w.ViewPort.Selection.Items);
        if (pts.Count == 0) { w.ViewPort.ZoomToFit(); return; }
        var mn = new Vector3((float)pts.Min(p => p.X), (float)pts.Min(p => p.Y), (float)pts.Min(p => p.Z));
        var mx = new Vector3((float)pts.Max(p => p.X), (float)pts.Max(p => p.Y), (float)pts.Max(p => p.Z));
        var vp = w.ViewPort;
        var cam = new Camera { State = vp.Camera.State, ViewportWidth = vp.Camera.ViewportWidth, ViewportHeight = vp.Camera.ViewportHeight };
        cam.ZoomToFit(mn, mx);
        vp.AnimateTo(cam.State);
    }

    /// <summary>Сечение: включить по выбранной плоскости (без выбора — через центр детали поперёк взгляда) или выключить.</summary>
    public static void ToggleSection(MainWindow w)
    {
        var vp = w.ViewPort;
        if (w.CurrentOperation is Parametric3D.App.Operations.SectionOp) { w.CancelOperation(); vp.SetSection(null); return; }
        if (vp.SectionPlane != null) { vp.SetSection(null); return; }
        // k99 (замечание 3): сечение — настраиваемая плоскость: по умолчанию основа — выбранная плоскость или XZ детали,
        // начало — центр основы; сразу открываются её настройки (дальше — щелчок по контуру плоскости)
        if (w.Session.Part != null && !w.InSketch && !SectionOldOff) { w.StartOperation(new Parametric3D.App.Operations.SectionOp()); return; }
        if (PlaneOfSelection(w) is { } p) { vp.SetSection(p); return; }
        var b = w.Session.Document.Bounds;
        var c = b.IsEmpty ? Vector3d.Zero : (b.Min + b.Max) / 2;
        var f = vp.Camera.Forward;
        vp.SetSection((c, -new Vector3d(f.X, f.Y, f.Z)));
    }
    static readonly bool SectionOldOff = Environment.GetEnvironmentVariable("P3D_SECTIONOP") == "0";

    // ---------------------------------------------------------------- выбор

    public enum SelMode { All, Vertex, Edge, Face, Body, Feature }
    public static SelMode Mode = SelMode.All;

    public static void SetMode(MainWindow w, SelMode m)
    {
        Mode = Mode == m ? SelMode.All : m;
        var vp = w.ViewPort;
        if (w.CurrentOperation != null || w.InSketch) { w.ShowToast(L("Режим выбора — вне операции и эскиза"), error: false); }
        vp.Selection.Filter = Mode switch
        {
            SelMode.Vertex => PickFilter.Vertices | PickFilter.SketchPoints | PickFilter.Datums,
            SelMode.Edge => PickFilter.Edges | PickFilter.SketchCurves,
            SelMode.Face => PickFilter.Faces,
            SelMode.Body or SelMode.Feature => PickFilter.Faces,
            _ => PickFilter.All,
        };
        vp.PickMap = Mode switch
        {
            SelMode.Body => r => r.Kind == PickKind.Face && r.Owner is Part p ? new PickRef(p.Id, PickKind.Body, p.Parent, p) : r,
            SelMode.Feature => r => r.Kind == PickKind.Face && r.Owner is Part p && FeatureOfFace(p, r.Id) is { } f ? new PickRef(f.Id, PickKind.Feature, f.Parent, f) : PickRef.Empty,
            _ => null,
        };
        w.RefreshToolStates();
    }

    /// <summary>Операция, создавшая грань (по устойчивому имени «#ключ/роль»).</summary>
    public static Feature? FeatureOfFace(Part p, uint faceId)
    {
        var f = p.FindFace(faceId); if (f == null || !f.Tag.StartsWith("#")) return null;
        int slash = f.Tag.IndexOf('/'); var key = slash > 1 ? f.Tag[1..slash] : f.Tag[1..];
        return p.Features.LastOrDefault(x => x.Key == key);
    }

    /// <summary>Фильтр типов: флажки по видам выбираемого.</summary>
    public static void FilterMenu(MainWindow w, Control? anchor)
    {
        var vp = w.ViewPort;
        var panel = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(6) };
        void Box(string label, PickFilter f)
        {
            var cb = new CheckBox { Content = L(label), IsChecked = vp.Selection.Filter.HasFlag(f) };
            cb.IsCheckedChanged += (_, _) => { vp.Selection.Filter = cb.IsChecked == true ? vp.Selection.Filter | f : vp.Selection.Filter & ~f; Mode = SelMode.All; vp.PickMap = null; };
            panel.Children.Add(cb);
        }
        Box("Грани", PickFilter.Faces); Box("Рёбра", PickFilter.Edges); Box("Вершины", PickFilter.Vertices);
        Box("Кривые эскиза", PickFilter.SketchCurves); Box("Точки эскиза", PickFilter.SketchPoints); Box("Опорные", PickFilter.Datums); Box("Детали", PickFilter.Parts);
        var fl = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        if (anchor != null) fl.ShowAt(anchor); else fl.ShowAt(w.ViewPort, showAtPointer: true);
    }

    /// <summary>Касательная цепочка: выбранные рёбра дополняются касательно продолжающимися.</summary>
    public static int ExpandChains(MainWindow w)
    {
        int n = 0;
        foreach (var r in w.ViewPort.Selection.Items.Where(x => x.Kind == PickKind.Edge && x.Owner is Part).ToList())
            foreach (var c in Topo.ChainRefs((Part)r.Owner!, r))
                if (!w.ViewPort.Selection.Contains(c.Id)) { w.ViewPort.Selection.Select(c, additive: true); n++; }
        return n;
    }

    /// <summary>Контур грани: рёбра внешнего контура выбранной грани (и внутренних — со второго нажатия).</summary>
    public static int FaceLoop(MainWindow w)
    {
        var faces = w.ViewPort.Selection.Items.Where(x => x.Kind == PickKind.Face && x.Owner is Part).ToList();
        if (faces.Count == 0) { w.ShowToast(L("Выберите грань"), error: false); return 0; }
        var add = new List<PickRef>();
        foreach (var r in faces)
        {
            var p = (Part)r.Owner!; var f = p.FindFace(r.Id); if (f == null) continue;
            foreach (var e in f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge).Distinct())
                if (e != null) add.Add(new PickRef(e.Id, PickKind.Edge, p, null));
        }
        w.ViewPort.Selection.Clear();
        foreach (var a in add) w.ViewPort.Selection.Select(a, additive: true);
        return add.Count;
    }

    // ---------------------------------------------------------------- измерение

    static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    static string Pt(Vector3d p) => $"({F(p.X)}; {F(p.Y)}; {F(p.Z)})";

    public static double? FaceArea(Part p, TFace f)
    {
        var a = AnalyticVolume.FaceArea(f);
        if (a is { } x && x > 0) return x;
        return p.Mesh is { } md ? Kernel.Tess.MassProps.FaceArea(md, f.Id) : null;
    }

    /// <summary>Измерение выбранного: координаты, длины, радиусы, площади; для пары — расстояние и угол.</summary>
    public static readonly bool MeasureOff = Environment.GetEnvironmentVariable("P3D_MEASUREFEATURE") == "0";

    /// <summary>Ссылка замера из выбранного: вершина, точка, прямое ребро, плоская грань, опорная плоскость; null — не годится.</summary>
    public static MeasureRef? MeasureRefOf(PickRef r)
    {
        if (r.Owner is Part p)
        {
            if (r.Kind == PickKind.Vertex && p.FindVertex(r.Id) is { } v) return new MeasureRef { Type = 0, P = v.P, Key = EdgeRef.EndKey(v) };
            if (r.Kind == PickKind.Edge && p.FindEdge(r.Id) is { Curve: LineCurve3 } e)
                return new MeasureRef { Type = 1, P = e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2), D = (e.V1.P - e.V0.P).Normalized(), Key = MeasureFeature.EdgeKey(e) };
            if (r.Kind == PickKind.Face && p.FindFace(r.Id) is { } f && GeometryAudit.AuthoritativeSurface(f) is Kernel.Surfaces.PlaneSurface ps)
            {
                var pts = f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge.V0.P).ToList();
                var c0 = pts.Count > 0 ? pts.Aggregate(Vector3d.Zero, (a, b) => a + b) / pts.Count : ps.F.Origin;
                var n = ps.F.W.Normalized();
                return new MeasureRef { Type = 2, P = c0 - n * Vector3d.Dot(c0 - ps.F.Origin, n), D = n, Key = f.Tag ?? "" };
            }
        }
        if (PointOnly(r) is { } q) return new MeasureRef { Type = 0, P = q };
        if (r.Kind is PickKind.DatumPlane && Refs.PlaneOf(r) is { } pl) return new MeasureRef { Type = 2, P = pl.Frame.Origin, D = pl.Frame.W.Normalized() };
        if (r.Kind is PickKind.DatumAxis && Refs.LineOf(r) is { } ln) return new MeasureRef { Type = 1, P = ln.At, D = ln.Dir.Normalized() };
        return null;
    }

    /// <summary>
    /// k120 (замечание 15:06, п. 17): «Измерить» по двум выбранным — точкам, параллельным прямым, параллельным плоскостям
    /// (и от точки до прямой или плоскости) — ЗАМЕР-ОПЕРАЦИЯ в дереве с размером на виде (подпись тянется мышью); иначе —
    /// прежнее окно со сведениями. P3D_MEASUREFEATURE=0 — только окно.
    /// </summary>
    public static MeasureFeature? CreateMeasure(MainWindow w)
    {
        var part = w.Session.Part;
        var sel = w.ViewPort.Selection.Items;
        if (part == null || sel.Count != 2) return null;
        if (MeasureRefOf(sel[0]) is not { } ra || MeasureRefOf(sel[1]) is not { } rb) return null;
        if (MeasureFeature.Solve(ra, rb) is not { } ab || (ab.B - ab.A).Length < 1e-9) return null;
        var ms = new MeasureFeature(UniqueName(part, L("Замер"))) { RefA = ra, RefB = rb, A = ab.A, B = ab.B, Value = (ab.B - ab.A).Length };
        var d = (ab.B - ab.A).Normalized();
        var f = w.ViewPort.Camera.Forward; var side = Vector3d.Cross(d, new Vector3d(f.X, f.Y, f.Z));
        if (side.LengthSquared < 1e-12) side = Vector3d.Cross(d, Math.Abs(d.Z) < 0.9 ? Vector3d.UnitZ : Vector3d.UnitX);
        ms.Side = side.Normalized();
        ms.Offset = Math.Max(ms.Value * 0.25, 30 * w.ViewPort.PixelSize);
        w.Session.Edit(L("Замер"), () => part.AddFeature(ms));
        w.Session.Document.RebuildDirty();
        w.ViewPort.MarkDirty(); w.RebuildTree();
        w.ShowToast(L("Замер: {0}", DisplayUnits.ToShow(ms.Value).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + L(DisplayUnits.LengthUnit)), error: false);
        return ms;
    }

    static string UniqueName(Part part, string baseName)
    {
        for (int i = 1; ; i++) { string n = $"{baseName} {i}"; if (!part.Features.Any(x => x.Name == n)) return n; }
    }

    public static List<string> Measure(MainWindow w)
    {
        var res = new List<string>();
        var sel = w.ViewPort.Selection.Items.ToList();
        foreach (var r in sel)
        {
            if (r.Owner is Part p)
            {
                if (r.Kind == PickKind.Edge && p.FindEdge(r.Id) is { } e)
                {
                    string s = L("Ребро: длина {0}", F(e.Curve.Length));
                    if (e.Curve is ArcCurve3 a) s += L(", радиус {0}, диаметр {1}", F(a.Radius), F(2 * a.Radius));
                    res.Add(s);
                }
                else if (r.Kind == PickKind.Face && p.FindFace(r.Id) is { } f)
                {
                    string s = L("Грань: площадь {0}", FaceArea(p, f) is { } ar ? F(ar) : "?");
                    if (DirectEditRadius(f) is { } rr) s += L(", радиус {0}, диаметр {1}", F(rr), F(2 * rr));
                    res.Add(s);
                }
                else if (r.Kind == PickKind.Vertex && p.FindVertex(r.Id) is { } v) res.Add(L("Вершина: {0}", Pt(v.P)));
            }
            else if (r.Kind is PickKind.SketchPoint or PickKind.DatumPoint && Refs.PointOf(r) is { } q) res.Add(L("Точка: {0}", Pt(q)));
        }
        if (sel.Count == 2)
        {
            var a = sel[0]; var b = sel[1];
            Vector3d? pa = PointOnly(a), pb = PointOnly(b);
            var la = Refs.LineOf(a); var lb = Refs.LineOf(b);
            var qa = Refs.PlaneOf(a); var qb = Refs.PlaneOf(b);
            if (pa is { } x && pb is { } y)
            {
                var d = y - x;
                res.Add(L("Расстояние: {0} (dX {1}, dY {2}, dZ {3})", F(d.Length), F(d.X), F(d.Y), F(d.Z)));
            }
            else if (qa is { } qa1 && qb is { } qb1)
            {
                var fa = qa1.Frame; var fb = qb1.Frame;
                double c = Math.Abs(Vector3d.Dot(fa.W.Normalized(), fb.W.Normalized()));
                double ang = Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI;
                res.Add(L("Угол между плоскостями: {0}°", F(ang)));
                if (ang < 1e-6) res.Add(L("Расстояние между плоскостями: {0}", F(Math.Abs(Vector3d.Dot(fb.Origin - fa.Origin, fa.W.Normalized())))));
            }
            else if (la is { } l1 && lb is { } l2)
            {
                double c = Math.Abs(Vector3d.Dot(l1.Dir.Normalized(), l2.Dir.Normalized()));
                res.Add(L("Угол между прямыми: {0}°", F(Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI)));
                var n = Vector3d.Cross(l1.Dir, l2.Dir);
                double dist = n.Length < 1e-12 ? (Vector3d.Cross(l2.At - l1.At, l1.Dir.Normalized())).Length : Math.Abs(Vector3d.Dot(l2.At - l1.At, n.Normalized()));
                res.Add(L("Расстояние между прямыми: {0}", F(dist)));
            }
            else if ((pa ?? pb) is { } pt && (qa ?? qb) is { } pl)
                res.Add(L("Расстояние от точки до плоскости: {0}", F(Math.Abs(Vector3d.Dot(pt - pl.Frame.Origin, pl.Frame.W.Normalized())))));
            else
            {
                // наименьшее расстояние между точками сеток показа
                var A = PointsOf(w, new[] { a }); var B = PointsOf(w, new[] { b });
                if (A.Count > 0 && B.Count > 0 && (long)A.Count * B.Count < 4_000_000)
                    res.Add(L("Наименьшее расстояние (по сетке): {0}", F(A.Min(x => B.Min(y => (x - y).Length)))));
            }
        }
        if (res.Count == 0) res.Add(L("Выберите вершины, рёбра, грани или точки (одну или две)"));
        return res;
    }

    static Vector3d? PointOnly(PickRef r) => r.Kind is PickKind.Vertex or PickKind.SketchPoint or PickKind.DatumPoint ? Refs.PointOf(r) : null;
    static double? DirectEditRadius(TFace f) => Kernel.Ops.DirectEdit.RadiusAfter(f, 0);

    public static double SelectedArea(MainWindow w) => w.ViewPort.Selection.Items.Where(r => r.Kind == PickKind.Face && r.Owner is Part)
        .Sum(r => ((Part)r.Owner!).FindFace(r.Id) is { } f ? FaceArea((Part)r.Owner!, f) ?? 0 : 0);

    /// <summary>Периметр: длина выбранных рёбер; у выбранных граней — длина их контуров.</summary>
    public static double SelectedLength(MainWindow w)
    {
        double s = 0;
        foreach (var r in w.ViewPort.Selection.Items)
        {
            if (r.Owner is not Part p) continue;
            if (r.Kind == PickKind.Edge && p.FindEdge(r.Id) is { } e) s += e.Curve.Length;
            if (r.Kind == PickKind.Face && p.FindFace(r.Id) is { } f) s += f.Loops.SelectMany(l => l.CoEdges).Select(c => c.Edge).Distinct().Sum(e2 => e2?.Curve.Length ?? 0);
            if (r.Target is Model.Sketching.SkEntity se && r.Owner is Model.Sketching.Sketch sk && se is not Model.Sketching.SkPoint)
                try { s += se.ToCurve3(sk.Plane).Length; } catch { }
        }
        return s;
    }

    /// <summary>Гребёнка кривизны выбранных рёбер и кривых эскиза: нормали длиной k·кривизна.</summary>
    public static List<(Vector3d A, Vector3d B, Vector4 C)> CurvatureComb(MainWindow w, double scale = 0)
    {
        var curves = new List<ICurve3>();
        foreach (var r in w.ViewPort.Selection.Items)
        {
            if (r.Owner is Part p && r.Kind == PickKind.Edge && p.FindEdge(r.Id) is { } e) curves.Add(e.Curve);
            if (r.Target is Model.Sketching.SkEntity se && r.Owner is Model.Sketching.Sketch sk && se is not Model.Sketching.SkPoint)
                try { curves.Add(se.ToCurve3(sk.Plane)); } catch { }
        }
        var res = new List<(Vector3d, Vector3d, Vector4)>();
        var col = new Vector4(0.95f, 0.45f, 0.2f, 0.9f);
        foreach (var c in curves)
        {
            int n = 48; double t0 = c.T0, t1 = c.T1, h = (t1 - t0) * 2e-3;
            var samples = new List<(Vector3d P, Vector3d K)>();
            for (int i = 0; i <= n; i++)
            {
                double t = t0 + (t1 - t0) * i / n;
                if (c is ArcCurve3 arc)
                {
                    var pa = c.PointAt(t); var toC = arc.Frame.Origin - pa;
                    samples.Add((pa, toC.Normalized() / arc.Radius * -1));
                    continue;
                }
                double tc = Math.Clamp(t, t0 + h, t1 - h); double ta = tc - h, tb = tc + h;
                var p0 = c.PointAt(ta); var p1 = c.PointAt(tc); var p2 = c.PointAt(tb);
                // кривизна по трём точкам: k = 4·S / (|a||b||c|), направление — к центру
                var a = p1 - p0; var b = p2 - p1; var cc = p2 - p0;
                var cr = Vector3d.Cross(a, b); double den = a.Length * b.Length * cc.Length;
                var pt = c.PointAt(t);
                if (den < 1e-30) { samples.Add((pt, Vector3d.Zero)); continue; }
                double k = 2 * cr.Length / den;
                var nrm = Vector3d.Cross(cr, cc).Normalized();   // к центру кривизны
                samples.Add((pt, -nrm * k));
            }
            double kmax = samples.Max(s => s.K.Length);
            double len = c.Length;
            double sc = scale > 0 ? scale : (kmax > 1e-12 ? 0.25 * len / kmax : 0);
            for (int i = 0; i <= n; i++)
            {
                var tip = samples[i].P + samples[i].K * sc;
                res.Add((samples[i].P, tip, col));
                if (i > 0) res.Add((samples[i - 1].P + samples[i - 1].K * sc, tip, col));
            }
        }
        return res;
    }

    /// <summary>Проверка тел детали: топология и геометрия.</summary>
    public static List<string> CheckBodies(Part part)
    {
        var res = new List<string>();
        int i = 0;
        foreach (var b in part.Bodies)
        {
            i++;
            var issues = TopologyCheck.Validate(b);
            var geo = Tolerances.Check(b);
            res.Add(issues.Count == 0 && geo.Count == 0
                ? L("Тело {0}: годно — граней {1}, рёбер {2}, вершин {3}", i, b.Faces.Count(), b.Edges.Count(), b.Vertices.Count())
                : L("Тело {0}: замечаний {1}", i, issues.Count + geo.Count));
            res.AddRange(issues.Take(20).Select(x => "  • " + x));
            res.AddRange(geo.Take(20).Select(x => "  • " + x));
        }
        if (i == 0) res.Add(L("В детали нет тел"));
        return res;
    }
}
