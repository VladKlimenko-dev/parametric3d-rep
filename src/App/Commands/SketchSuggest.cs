using Avalonia.Input;
using OpenTK.Mathematics;
using Parametric3D.App.Sketching;
using Parametric3D.App.Views;
using Parametric3D.Core;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// ПРЕДЛАГАЕМЫЕ ОПЕРАЦИИ ЭСКИЗА (полоса снизу вида, пп. 9–10): только то, что подходит к выбранному.
///
/// Выбор → предложения (по порядку важности):
///   * ничего — инструменты правки по щелчку: обрезка, разделение, касательная к двум дугам, рёбра модели, площадь;
///   * одна точка — разделить (снять совпадения), зафиксировать; точка и кривая — на кривой, середина (отрезок),
///     размер до отрезка; две точки — объединить, горизонтально/вертикально, расстояние; две точки и отрезок — симметрия;
///   * отрезок — горизонтально, вертикально, длина; два отрезка — скругление/фаска угла (общий конец),
///     параллельно, перпендикулярно, равенство, коллинеарно, угол/расстояние; три — симметрия;
///   * окружность/дуга — радиус, диаметр, длина дуги; две — концентрично, равенство, касание; отрезок и дуга — касание;
///   * кривые — смещение цепочки, зеркало (есть осевая), перенос/поворот, вспомогательная, обрезка, удалить;
///   * размер — радиус⇄диаметр, одна стрелка, стрелки снаружи, как размер (диаметр), сделать сильным, замок,
///     справочный, параметр, изменить значение; только подходящие к типу размера;
///   * связь — удалить.
/// </summary>
public static class SketchSuggest
{
    private static List<SkDimension> Dims(MainWindow w) => SketchCommands.PickedDimensions(w);
    private static bool OneTypeDims(MainWindow w, Func<SkDimension, bool> ok) { var d = Dims(w); return d.Count > 0 && d.All(ok); }
    private static bool Radial(SkDimension d) => d.Type is DimensionType.Radius or DimensionType.Diameter;

    /// <summary>Правка выбранных размеров с откатом, если эскиз перестал решаться.</summary>
    public static void EditDims(MainWindow w, Action<SkDimension> act, bool geometry = true)
    {
        var sk = w.ViewPort.ActiveSketch; var ds = Dims(w);
        if (sk == null || ds.Count == 0) { w.ShowToast(L("Выберите размер (подпись) в эскизе"), error: false); return; }
        var solver = w.Session.Document.Solver;
        if (!geometry)
        {
            // вид размера (стрелки, одна стрелка, как размер) — без решения и отката (k96, п. 8)
            foreach (var d in ds) act(d);
            sk.NotifyChanged(sk); w.ViewPort.MarkDirty(); w.RefreshToolStates(); w.RefreshContext();
            return;
        }
        // k96: откат — только при настоящем противоречии (избыточность согласных условий — не причина), с геометрией
        var geo = SketchEditing.Geometry.Take(sk);
        var st0 = sk.Solve(solver); geo.Restore();
        var before = ds.Select(d => (d, d.Value, d.Formula, d.Locked, d.IsDefault, d.Reference, d.UserLocked, d.Type)).ToList();
        foreach (var d in ds) act(d);
        var st = sk.Solve(solver);
        var fail = SketchEditing.FailureOf(st0, st, false);
        if (fail != SketchEditFailure.None)
        {
            var res = new SketchEditResult { Failure = fail, Status = st };
            geo.Restore();
            SketchEditing.Diagnose(sk, solver, geo, st0, false, ds.Cast<object>().ToArray(), res);
            foreach (var (d, v, f, lk, df, rf, ul, t) in before) { d.Value = v; d.Formula = f; d.Locked = lk; d.IsDefault = df; d.Reference = rf; d.UserLocked = ul; d.Type = t; }
            geo.Restore(); sk.Solve(solver); geo.Restore();
            w.ReportSketchConflict(sk, res, L("Правка размера"), null);
        }
        w.AfterSketchEdit(sk);
        w.RefreshContext();
    }

    public static void Register(CommandRegistry r, MainWindow w)
    {
        const string S = "Эскиз";
        bool In() => w.InSketch;

        // ---- размеры: только подходящие к выбранному размеру
        r.Add(new AppCommand { Id = "dim.to_radius", Label = "Сделать радиусом", Short = "Радиус", Group = S, Icon = "dim_radius_but.svg", Synonyms = "radius радиус",
            CanRun = () => In() && OneTypeDims(w, d => d.Type == DimensionType.Diameter && !d.UserLocked),
            Run = () => EditDims(w, d => { if (d.Type != DimensionType.Diameter) return; d.Type = DimensionType.Radius; d.Value /= 2; d.Formula = d.Formula == null ? null : $"({d.Formula})/2"; d.DiameterAsLinear = false; }) });
        r.Add(new AppCommand { Id = "dim.to_diameter", Label = "Сделать диаметром", Short = "Диаметр", Group = S, Icon = "dim_diameter_but.svg", Synonyms = "diameter диаметр",
            CanRun = () => In() && OneTypeDims(w, d => d.Type == DimensionType.Radius && !d.UserLocked),
            Run = () => EditDims(w, d => { if (d.Type != DimensionType.Radius) return; d.Type = DimensionType.Diameter; d.Value *= 2; d.Formula = d.Formula == null ? null : $"({d.Formula})*2"; }) });
        r.Add(new AppCommand { Id = "dim.single_arrow", Label = "Одна стрелка", Group = S, Synonyms = "single arrow одна стрелка",
            IconOf = () => Dims(w).FirstOrDefault()?.Type == DimensionType.Diameter ? "dim_diameter_arrow_ico.svg" : "dim_radius_arrow_ico.svg",
            CanRun = () => In() && OneTypeDims(w, Radial), IsChecked = () => Dims(w) is { Count: > 0 } ds && ds.All(d => d.SingleArrow),
            Run = () => { bool to = !Dims(w).All(d => d.SingleArrow); EditDims(w, d => d.SingleArrow = to, geometry: false); } });
        r.Add(new AppCommand { Id = "dim.outside", Label = "Стрелки снаружи", Group = S, Synonyms = "arrows outside стрелки снаружи наружный",
            IconOf = () => Dims(w).FirstOrDefault()?.Type switch
            {
                DimensionType.Diameter => "dim_diameter_outside_ico.svg", DimensionType.Radius => "dim_radius_outside_ico.svg",
                DimensionType.AngleLineLine => "dim_angle_outside_ico.svg", _ => "dim_arrows_outside_but.svg",
            },
            CanRun = () => In() && Dims(w).Count > 0, IsChecked = () => Dims(w) is { Count: > 0 } ds && ds.All(d => d.Arrows == ArrowPlacement.Outside),
            Run = () => { bool to = !Dims(w).All(d => d.Arrows == ArrowPlacement.Outside); EditDims(w, d => d.Arrows = to ? ArrowPlacement.Outside : ArrowPlacement.Auto, geometry: false); } });
        r.Add(new AppCommand { Id = "dim.as_linear", Label = "Диаметр как размер", Group = S, Icon = "dim_diameter_size_ico.svg", Synonyms = "diameter linear диаметр как размер",
            CanRun = () => In() && OneTypeDims(w, d => d.Type == DimensionType.Diameter), IsChecked = () => Dims(w) is { Count: > 0 } ds && ds.All(d => d.DiameterAsLinear),
            Run = () => { bool to = !Dims(w).All(d => d.DiameterAsLinear); EditDims(w, d => d.DiameterAsLinear = to, geometry: false); } });
        r.Add(new AppCommand { Id = "dim.make_strong", Label = "Сделать сильным", Short = "Сильный", Group = S, Icon = "dim_smart_but.svg", Synonyms = "strong driving сильный",
            CanRun = () => In() && Dims(w).Any(d => d.IsDefault || !d.Locked && !d.Reference),
            Run = () => EditDims(w, d => { d.IsDefault = false; d.Locked = true; d.Reference = false; }) });
        // k96 (пп. 2, 17): слабым — противоположность «Сделать сильным»; удалить размер (нужный эскизу — становится слабым)
        r.Add(new AppCommand { Id = "dim.make_weak", Label = "Сделать слабым", Short = "Слабый", Group = S, Icon = "dim_weak_but.svg", Synonyms = "weak слабый ослабить",
            CanRun = () => In() && Dims(w).Any(d => d.Locked && !d.IsDefault && !d.UserLocked && !d.Reference),
            Run = () => { var sk = w.ViewPort.ActiveSketch!; SketchEditing.MakeWeak(sk, Dims(w), w.Session.Document.Solver); w.AfterSketchEdit(sk); w.RefreshContext(); } });
        r.Add(new AppCommand { Id = "dim.delete", Label = "Удалить размер", Short = "Удалить", Group = S, Icon = "delete_but.svg", Synonyms = "delete dimension удалить размер",
            CanRun = () => In() && Dims(w).Any(d => !d.UserLocked),
            Run = () => DeleteDims(w, Dims(w)) });
        r.Add(new AppCommand { Id = "dim.lock_toggle", Label = "Замок размера", Short = "Замок", Group = S, Synonyms = "lock unlock замок заблокировать разблокировать",
            IconOf = () => Dims(w).Any(d => d.UserLocked) ? "dim_lock_ico.svg" : "dim_unlock_ico.svg",
            CanRun = () => In() && Dims(w).Any(d => !d.Reference), IsChecked = () => Dims(w) is { Count: > 0 } ds && ds.All(d => d.UserLocked),
            Run = () => { bool to = !Dims(w).All(d => d.UserLocked); EditDims(w, d => { if (d.Reference) return; d.UserLocked = to; if (to) { d.IsDefault = false; d.Locked = true; } }); } });
        r.Add(new AppCommand { Id = "dim.ref_toggle", Label = "Справочный размер", Short = "Справ.", Group = S, Icon = "dim_reference_but.svg", Synonyms = "reference справочный",
            CanRun = () => In() && Dims(w).Any(d => !d.UserLocked), IsChecked = () => Dims(w) is { Count: > 0 } ds && ds.All(d => d.Reference),
            Run = () => { bool to = !Dims(w).All(d => d.Reference); EditDims(w, d => { if (d.UserLocked) return; d.Reference = to; if (to) { d.Locked = false; d.IsDefault = false; } else d.Locked = true; }); } });
        r.Add(new AppCommand { Id = "dim.edit_value", Label = "Изменить значение", Short = "Значение…", Group = S, Icon = "tree_edit_dims_but.svg", Synonyms = "value значение",
            CanRun = () => In() && Dims(w) is { Count: 1 } ds && !ds[0].UserLocked, Run = () => w.EditDimensionValue(Dims(w)[0]) });

        // ---- точки
        r.Add(new AppCommand { Id = "sk.merge_points", Label = "Объединить точки", Short = "Объединить", Group = S, Icon = "con_coincident_but.svg", Synonyms = "merge join объединить слить",
            CanRun = () => In() && SketchCommands.Picked(w) is var p && p.Count >= 2 && p.All(e => e is SkPoint),
            Run = () => SketchCommands.Constrain(w, ConstraintType.Coincident) });
        r.Add(new AppCommand { Id = "sk.split_point", Label = "Разделить точку", Short = "Разделить", Group = S, Icon = "sk_split_but.svg", Synonyms = "split detach разделить разъединить",
            CanRun = () => In() && SketchCommands.Picked(w) is [SkPoint pt] && w.ViewPort.ActiveSketch is { } sk && Joined(sk, pt),
            Run = () => SplitPoint(w) });

        // ---- k109 (11:07): выбранные кривые — вспомогательные ↔ основные (пункт меню «Make construction» / «Make sketch»)
        r.Add(new AppCommand { Id = "sk.toggle_construction", Label = "Вспомогательная ↔ основная", Short = "Вспом.", Group = S, Icon = "sk_construction_but.svg",
            Synonyms = "make construction make sketch вспомогательная основная",
            CanRun = () => In() && ConstructionTargets(w).Count > 0, Run = () => ToggleConstructionSel(w) });

        // ---- удаление в эскизе (Delete)
        r.Add(new AppCommand { Id = "sk.delete", Label = "Удалить из эскиза", Short = "Удалить", Group = S, Icon = "delete_but.svg", Gesture = new KeyGesture(Key.Delete), Synonyms = "delete удалить",
            CanRun = () => In() && w.ViewPort.Selection.Items.Any(x => ReferenceEquals(x.Owner, w.ViewPort.ActiveSketch) && x.Target is SkEntity or SkConstraint or SkDimension),
            Run = () => Delete(w) });
    }

    /// <summary>k109: выбранные элементы эскиза, у которых меняется роль (кривые и точки; ссылки — нет).</summary>
    public static List<SkEntity> ConstructionTargets(MainWindow w) =>
        SketchCommands.Picked(w).Where(e => e is SkLine or SkArc or SkCircle or SkEllipse or SkSpline || e is SkPoint { Owner: null }).Where(e => e.Role != SkRole.Reference && !e.Builtin).ToList();

    /// <summary>Все выбранные — вспомогательные (пункт меню — «Make sketch»), иначе — «Make construction».</summary>
    public static bool AllConstruction(MainWindow w) => ConstructionTargets(w) is { Count: > 0 } t && t.All(e => e.Role == SkRole.Construction);

    /// <summary>k109: выбранные кривые — вспомогательные; если все уже вспомогательные — основные. Один шаг отмены эскиза.</summary>
    public static void ToggleConstructionSel(MainWindow w)
    {
        if (w.ViewPort.ActiveSketch is not { } sk) return;
        var t = ConstructionTargets(w);
        if (t.Count == 0) return;
        var to = t.All(e => e.Role == SkRole.Construction) ? SkRole.Object : SkRole.Construction;
        foreach (var e in t) e.Role = to;
        sk.BumpStructure();
        w.AfterSketchEdit(sk);
        w.ViewPort.MarkDirty();
    }

    /// <summary>Удалить размеры: нужные эскизу (сильные) становятся слабыми — об этом сообщение.</summary>
    public static void DeleteDims(MainWindow w, List<SkDimension> ds)
    {
        var sk = w.ViewPort.ActiveSketch; if (sk == null || ds.Count == 0) return;
        var (removed, weakened) = SketchEditing.DeleteDimensions(sk, ds.Where(d => !d.UserLocked), w.Session.Document.Solver);
        w.ViewPort.Selection.Clear();
        w.AfterSketchEdit(sk);
        if (weakened > 0) w.ShowToast(L("Удалено размеров: {0}; нужных эскизу (стали слабыми): {1}", removed, weakened), error: false);
        w.RefreshContext();
    }

    /// <summary>Точка связана с другой (совпадение) или общая у нескольких элементов.</summary>
    public static bool Joined(Sketch sk, SkPoint p) =>
        sk.Constraints.Any(c => c.Type == ConstraintType.Coincident && c.Items.Contains(p))
        || sk.Entities.Count(e => e.Handles.Contains(p)) > 1;

    /// <summary>Разделить точку: снять совпадения; общая точка нескольких элементов — каждому лишнему своя копия.</summary>
    public static void SplitPoint(MainWindow w)
    {
        var sk = w.ViewPort.ActiveSketch; if (sk == null || SketchCommands.Picked(w) is not [SkPoint p]) return;
        int n = sk.Constraints.RemoveAll(c => c.Type == ConstraintType.Coincident && c.Items.Contains(p));
        var users = sk.Entities.Where(e => e.Handles.Contains(p)).ToList();
        foreach (var e in users.Skip(1))
            if (e is SkLine l) { var q = new SkPoint(p.P) { Owner = l }; if (ReferenceEquals(l.A, p)) l.A = q; else if (ReferenceEquals(l.B, p)) l.B = q; n++; }
        sk.BumpStructure();
        sk.Solve(w.Session.Document.Solver);
        sk.NotifyChanged(sk); w.ViewPort.RefreshDefaultDimensions(); w.ViewPort.MarkDirty();
        w.ShowToast(L("Точка разделена"), error: false);
    }

    /// <summary>Удалить выбранное в эскизе: элементы (с их связями и размерами), связи, размеры.</summary>
    public static void Delete(MainWindow w)
    {
        var sk = w.ViewPort.ActiveSketch; if (sk == null) return;
        var sel = w.ViewPort.Selection.Items.Where(x => ReferenceEquals(x.Owner, sk)).Select(x => x.Target).ToList();
        foreach (var c in sel.OfType<SkConstraint>()) sk.Constraints.Remove(c);
        var dims = sel.OfType<SkDimension>().ToList();
        if (dims.Count > 0) { DeleteDims(w, dims); if (sel.All(x => x is SkDimension)) return; }
        foreach (var e in sel.OfType<SkEntity>())
        {
            if (e is SkPoint { Owner: not null } owned) { if (sk.Entities.Contains(owned.Owner!)) continue; }
            if (sk.Entities.Contains(e)) sk.RemoveEntity(e);
        }
        sk.BumpStructure();
        w.ViewPort.Selection.Clear();
        try { sk.Solve(w.Session.Document.Solver); } catch { }
        sk.NotifyChanged(sk); w.ViewPort.RefreshDefaultDimensions(); w.ViewPort.MarkDirty();
    }

    /// <summary>Подходящие к выбору операции эскиза (ид команд), по важности.</summary>
    public static List<string> For(MainWindow w)
    {
        var res = new List<string>();
        var sk = w.ViewPort.ActiveSketch;
        if (sk == null) return res;
        var sel = SketchCommands.Picked(w);
        var dims = Dims(w);
        bool cons = w.ViewPort.Selection.Items.Any(x => ReferenceEquals(x.Owner, sk) && x.Target is SkConstraint);

        if (dims.Count > 0)
        {
            res.AddRange(new[] { "dim.to_radius", "dim.to_diameter", "dim.single_arrow", "dim.outside", "dim.as_linear", "dim.make_strong", "dim.make_weak", "dim.lock_toggle", "dim.ref_toggle", "dim.link_param", "dim.edit_value", "dim.delete" });
        }
        if (sel.Count == 0 && dims.Count == 0)
        {
            if (cons) res.Add("sk.delete");
            else res.AddRange(new[] { "sk.trim", "sk.split", "sk.fillet", "sk.chamfer", "sk.line_tangent2", "sk.use_edge", "sk.offset_edge", "sk.thicken_edge", "dim.area" });
            return Usable(w, res);
        }
        if (sel.Count > 0)
        {
            var pts = sel.OfType<SkPoint>().ToList();
            var lines = sel.OfType<SkLine>().ToList();
            var round = sel.Where(e => e is SkCircle or SkArc).ToList();
            bool curves = sel.Any(e => e is not SkPoint);
            // точки
            if (pts.Count >= 2 && !curves) res.Add("sk.merge_points");
            if (pts.Count == 1 && sel.Count == 1) res.Add("sk.split_point");
            // связи — по плану (подходит ли выбор), в порядке частоты
            foreach (var (id, t, col) in new[]
            {
                ("con.horizontal", ConstraintType.Horizontal, false), ("con.vertical", ConstraintType.Vertical, false),
                ("con.point_on", ConstraintType.PointOnEntity, false), ("con.midpoint", ConstraintType.MidPoint, false),
                ("con.parallel", ConstraintType.Parallel, false), ("con.perpendicular", ConstraintType.Perpendicular, false),
                ("con.tangent", ConstraintType.Tangent, false), ("con.equal", ConstraintType.Equal, false),
                ("con.collinear", ConstraintType.Parallel, true), ("con.concentric", ConstraintType.Concentric, false),
                ("con.symmetric", ConstraintType.Symmetric, false),
            })
                if (SketchCommands.Plan(t, sel, col) is { Count: > 0 } plan && Plausible(sk, t, col, plan, lines)) res.Add(id);
            // угол двух отрезков с общим концом — скругление и фаска
            if (lines.Count == 2 && sel.Count == 2 && CommonCorner(lines[0], lines[1]) != null) res.InsertRange(0, new[] { "sk.fillet", "sk.chamfer" });
            // размеры
            foreach (var (id, k) in new[] { ("dim.linear", SketchCommands.DimKind.Linear), ("dim.radius", SketchCommands.DimKind.Radius), ("dim.diameter", SketchCommands.DimKind.Diameter), ("dim.angle", SketchCommands.DimKind.Angle), ("dim.arc_length", SketchCommands.DimKind.ArcLength) })
                if (SketchCommands.PlanDimension(k, sel) != null) res.Add(id);
            if (round.Count == 0 && SketchCommands.PlanDimension(SketchCommands.DimKind.Smart, sel) is { } sm && !res.Contains("dim.linear") && !res.Contains("dim.angle")) res.Add("dim.smart");
            // k130: две точки (отрезок) не на одной горизонтали / вертикали — размеры по осям
            if (SketchCommands.PlanDimension(SketchCommands.DimKind.Horizontal, sel) is { } hp && hp.Items[0] is SkPoint h0 && hp.Items[1] is SkPoint h1)
            {
                if (Math.Abs(h1.X - h0.X) > 1e-9) res.Add("dim.horizontal");
                if (Math.Abs(h1.Y - h0.Y) > 1e-9) res.Add("dim.vertical");
            }
            // кривые
            if (curves)
            {
                res.Add("sk.offset");
                if (sel.Any(e => e is SkLine { Role: SkRole.Centerline }) && sel.Count >= 2) res.Add("sk.mirror");
                res.AddRange(new[] { "sk.transform", "sk.toggle_construction", "sk.trim" });
            }
            res.Add("con.fix");
        }
        if (sel.Count > 0 || cons) res.Add("sk.delete");
        return Usable(w, res);
    }

    /// <summary>
    /// Предлагать ли связь: не повторять уже наложенную и не предлагать явно несбыточное — параллельность и
    /// коллинеарность почти перпендикулярных отрезков, перпендикулярность почти параллельных; горизонтальность или
    /// вертикальность отрезка, у которого уже есть одна из них.
    /// </summary>
    private static bool Plausible(Sketch sk, ConstraintType t, bool collinear, List<(ConstraintType T, SkEntity[] Items)> plan, List<SkLine> lines)
    {
        if (plan.All(p => sk.Constraints.Any(c => c.Enabled && c.Type == p.T && c.SameItems(p.Items)))) return false;
        if (t is ConstraintType.Horizontal or ConstraintType.Vertical && lines.Count > 0 &&
            lines.Any(l => sk.Constraints.Any(c => c.Enabled && c.Type is ConstraintType.Horizontal or ConstraintType.Vertical && c.Items.Count == 1 && ReferenceEquals(c.Items[0], l))))
            return false;
        // все отрезки уже горизонтальны/вертикальны — их взаимное направление задано, параллельность и перпендикулярность лишние
        bool Hv(SkLine l) => sk.Constraints.Any(c => c.Enabled && c.Type is ConstraintType.Horizontal or ConstraintType.Vertical && c.Items.Count == 1 && ReferenceEquals(c.Items[0], l));
        if (lines.Count >= 2 && t is ConstraintType.Parallel or ConstraintType.Perpendicular && lines.All(Hv)) return false;
        if (lines.Count >= 2 && t is ConstraintType.Parallel or ConstraintType.Perpendicular)
        {
            var d0 = lines[0].B.P - lines[0].A.P;
            foreach (var l in lines.Skip(1))
            {
                var d1 = l.B.P - l.A.P;
                if (d0.Length < 1e-12 || d1.Length < 1e-12) continue;
                double sin = Math.Abs(d0.X * d1.Y - d0.Y * d1.X) / (d0.Length * d1.Length);
                if (t == ConstraintType.Parallel && sin > 0.5) return false;          // > 30°
                if (t == ConstraintType.Perpendicular && sin < 0.866) return false;   // < 60°
            }
        }
        return true;
    }

    private static List<string> Usable(MainWindow w, List<string> ids) =>
        ids.Distinct().Where(id => CommandRegistry.Instance.Find(id) is { } c && c.Enabled).ToList();

    /// <summary>Общий конец двух отрезков (одна точка или совпадающие), null — нет.</summary>
    public static Vector2d? CommonCorner(SkLine a, SkLine b)
    {
        foreach (var ea in new[] { a.A, a.B })
            foreach (var eb in new[] { b.A, b.B })
                if ((ea.P - eb.P).Length < 1e-6) return ea.P;
        return null;
    }
}
