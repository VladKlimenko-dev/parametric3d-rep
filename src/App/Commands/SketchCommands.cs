using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.App.Views;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// u13: СВЯЗИ И РАЗМЕРЫ ЭСКИЗА КНОПКАМИ — по выбранным элементам активного эскиза. Связь, после которой эскиз не решается
/// (переопределён, противоречив), снимается с сообщением; размер ставится «сильным» (управляющим) с текущим значением.
/// </summary>
public static class SketchCommands
{
    public static List<SkEntity> Picked(MainWindow w)
    {
        var sk = w.ViewPort.ActiveSketch;
        if (sk == null) return new();
        return w.ViewPort.Selection.Items.Where(r => ReferenceEquals(r.Owner, sk) && r.Target is SkEntity).Select(r => (SkEntity)r.Target!).Distinct().ToList();
    }

    static SkPoint? CenterOf(SkEntity e) => e switch { SkCircle c => c.Center, SkArc a => a.Center, SkEllipse el => el.Center, _ => null };
    static bool Curve(SkEntity e) => e is SkLine or SkCircle or SkArc or SkEllipse;

    /// <summary>Связи, которые получатся из выбранного для типа t (несколько — по парам); null — выбор не подходит.</summary>
    public static List<(ConstraintType T, SkEntity[] Items)>? Plan(ConstraintType t, List<SkEntity> sel, bool collinear = false)
    {
        var pts = sel.OfType<SkPoint>().ToList(); var lines = sel.OfType<SkLine>().ToList();
        var round = sel.Where(e => e is SkCircle or SkArc).ToList();
        var res = new List<(ConstraintType, SkEntity[])>();
        if (collinear)
        {
            if (lines.Count < 2) return null;
            for (int i = 1; i < lines.Count; i++) { res.Add((ConstraintType.Parallel, new SkEntity[] { lines[0], lines[i] })); res.Add((ConstraintType.PointOnEntity, new SkEntity[] { lines[i].A, lines[0] })); }
            return res;
        }
        switch (t)
        {
            case ConstraintType.Coincident:
                if (pts.Count >= 2) { for (int i = 1; i < pts.Count; i++) res.Add((t, new SkEntity[] { pts[0], pts[i] })); return res; }
                if (pts.Count == 1 && sel.FirstOrDefault(Curve) is { } cv) { res.Add((ConstraintType.PointOnEntity, new SkEntity[] { pts[0], cv })); return res; }
                return null;
            case ConstraintType.PointOnEntity:
                if (pts.Count >= 1 && sel.FirstOrDefault(Curve) is { } c2) { foreach (var p in pts) res.Add((t, new SkEntity[] { p, c2 })); return res; }
                return null;
            case ConstraintType.MidPoint:
                if (pts.Count == 1 && lines.Count == 1) { res.Add((t, new SkEntity[] { pts[0], lines[0] })); return res; }
                return null;
            case ConstraintType.Horizontal or ConstraintType.Vertical:
                if (lines.Count > 0 && pts.Count == 0) { foreach (var l in lines) res.Add((t, new SkEntity[] { l })); return res; }
                if (pts.Count == 2 && lines.Count == 0) { res.Add((t, new SkEntity[] { pts[0], pts[1] })); return res; }
                return null;
            case ConstraintType.Parallel or ConstraintType.Perpendicular:
                if (lines.Count >= 2) { for (int i = 1; i < lines.Count; i++) res.Add((t, new SkEntity[] { lines[0], lines[i] })); return res; }
                return null;
            case ConstraintType.Tangent:
                if (sel.Count == 2 && sel.All(Curve) && sel.Any(e => e is not SkLine)) { res.Add((t, new[] { sel[0], sel[1] })); return res; }
                return null;
            case ConstraintType.Equal:
                if (lines.Count >= 2 && round.Count == 0) { for (int i = 1; i < lines.Count; i++) res.Add((t, new SkEntity[] { lines[0], lines[i] })); return res; }
                if (round.Count >= 2 && lines.Count == 0) { for (int i = 1; i < round.Count; i++) res.Add((t, new[] { round[0], round[i] })); return res; }
                return null;
            case ConstraintType.Concentric:
                var withC = sel.Where(e => CenterOf(e) != null).ToList();
                if (withC.Count >= 2) { for (int i = 1; i < withC.Count; i++) res.Add((t, new[] { withC[0], withC[i] })); return res; }
                if (withC.Count == 1 && pts.Count == 1) { res.Add((ConstraintType.Coincident, new SkEntity[] { pts[0], CenterOf(withC[0])! })); return res; }
                return null;
            case ConstraintType.Symmetric:
                if (pts.Count == 2 && lines.Count == 1) { res.Add((t, new SkEntity[] { pts[0], pts[1], lines[0] })); return res; }
                if (lines.Count == 3 && pts.Count == 0)
                {
                    // два отрезка относительно третьего (оси — осевой, если есть): концы попарно
                    var axis = lines.FirstOrDefault(l => l.Role == SkRole.Centerline) ?? lines[2];
                    var two = lines.Where(l => l != axis).ToList();
                    res.Add((t, new SkEntity[] { two[0].A, two[1].A, axis })); res.Add((t, new SkEntity[] { two[0].B, two[1].B, axis }));
                    return res;
                }
                return null;
            case ConstraintType.Fix:
                if (sel.Count > 0) { res.Add((t, sel.ToArray())); return res; }
                return null;
        }
        return null;
    }

    public static readonly Dictionary<ConstraintType, string> Hints = new()
    {
        [ConstraintType.Coincident] = "Выберите две точки (или точку и кривую)",
        [ConstraintType.PointOnEntity] = "Выберите точку и кривую",
        [ConstraintType.MidPoint] = "Выберите точку и отрезок",
        [ConstraintType.Horizontal] = "Выберите отрезки или две точки",
        [ConstraintType.Vertical] = "Выберите отрезки или две точки",
        [ConstraintType.Parallel] = "Выберите два отрезка",
        [ConstraintType.Perpendicular] = "Выберите два отрезка",
        [ConstraintType.Tangent] = "Выберите отрезок и дугу (окружность) или две дуги",
        [ConstraintType.Equal] = "Выберите два отрезка или две дуги (окружности)",
        [ConstraintType.Concentric] = "Выберите две дуги или окружности",
        [ConstraintType.Symmetric] = "Выберите две точки и ось (или два отрезка и ось)",
        [ConstraintType.Fix] = "Выберите элементы эскиза",
    };

    /// <summary>Наложить связь на выбранное; отказ решателя — откат.</summary>
    public static bool Constrain(MainWindow w, ConstraintType t, bool collinear = false)
    {
        var sk = w.ViewPort.ActiveSketch; if (sk == null) return false;
        var plan = Plan(t, Picked(w), collinear);
        if (plan == null || plan.Count == 0) { w.ShowToast(L(collinear ? "Выберите два отрезка" : Hints[t]), error: false); return false; }
        // k96: противоречие, вырождение или избыточность — отказ с участниками (красным) и повтором для «Разрешить эскиз»
        var solver = w.Session.Document.Solver;
        w.ClearConflictFlags(sk);
        Func<SketchEditResult> run = () => SketchEditing.AddConstraints(sk, plan, solver);
        var res = run();
        w.AfterSketchEdit(sk);
        if (!res.Ok)
        {
            w.ReportSketchConflict(sk, res, L("Связь «{0}»", Parametric3D.App.Views.MainWindow.ConstraintName(t)), res.Failure == SketchEditFailure.Redundant ? null : run);
            return false;
        }
        return true;
    }

    static bool Bad(SolveStatus st) => st is SolveStatus.Overconstrained or SolveStatus.Inconsistent or SolveStatus.DidNotConverge or SolveStatus.Singular;

    public enum DimKind { Smart, Linear, Radius, Diameter, Angle, ArcLength }

    /// <summary>Размер по выбранному: тип по виду (умный) или заданный.</summary>
    public static (DimensionType T, SkEntity[] Items)? PlanDimension(DimKind k, List<SkEntity> sel)
    {
        var pts = sel.OfType<SkPoint>().ToList(); var lines = sel.OfType<SkLine>().ToList();
        var round = sel.Where(e => e is SkCircle or SkArc).ToList();
        switch (k)
        {
            case DimKind.Radius: return round.Count == 1 ? (DimensionType.Radius, new[] { round[0] }) : null;
            case DimKind.Diameter: return round.Count == 1 ? (DimensionType.Diameter, new[] { round[0] }) : null;
            case DimKind.ArcLength: return sel.Count == 1 && sel[0] is SkArc ar ? (DimensionType.ArcLength, new SkEntity[] { ar }) : null;
            case DimKind.Angle: return lines.Count == 2 ? (DimensionType.AngleLineLine, new SkEntity[] { lines[0], lines[1] }) : null;
        }
        // линейный и умный
        if (pts.Count == 2 && sel.Count == 2) return (DimensionType.DistancePointPoint, new SkEntity[] { pts[0], pts[1] });
        if (pts.Count == 1 && lines.Count == 1 && sel.Count == 2) return (DimensionType.DistancePointLine, new SkEntity[] { pts[0], lines[0] });
        if (lines.Count == 1 && sel.Count == 1) return (DimensionType.DistancePointPoint, new SkEntity[] { lines[0].A, lines[0].B });
        if (lines.Count == 2 && sel.Count == 2)
        {
            double cr = Math.Abs(lines[0].Direction.X * lines[1].Direction.Y - lines[0].Direction.Y * lines[1].Direction.X);
            if (cr < 1e-6) return (DimensionType.DistanceLineLine, new SkEntity[] { lines[0], lines[1] });
            return k == DimKind.Smart ? (DimensionType.AngleLineLine, new SkEntity[] { lines[0], lines[1] }) : null;
        }
        if (k == DimKind.Smart && round.Count == 1 && sel.Count == 1) return (round[0] is SkCircle ? DimensionType.Diameter : DimensionType.Radius, new[] { round[0] });
        if (k == DimKind.Smart && sel.Count == 1 && sel[0] is SkEllipse el) return (DimensionType.EllipseRadiusU, new SkEntity[] { el });
        return null;
    }

    public static SkDimension? Dimension(MainWindow w, DimKind k)
    {
        var sk = w.ViewPort.ActiveSketch; if (sk == null) return null;
        var plan = PlanDimension(k, Picked(w));
        if (plan == null)
        {
            w.ShowToast(L(k switch
            {
                DimKind.Radius or DimKind.Diameter => "Выберите дугу или окружность",
                DimKind.ArcLength => "Выберите дугу",
                DimKind.Angle => "Выберите два отрезка",
                _ => "Выберите отрезок, две точки, точку и отрезок, два отрезка или дугу",
            }), error: false);
            return null;
        }
        var (t, items) = plan.Value;
        // тот же размер слабым уже есть — становится сильным
        var st0 = sk.Solve(w.Session.Document.Solver);
        var d = sk.Dimensions.FirstOrDefault(x => x.Type == t && x.Items.Count == items.Length && x.Items.Zip(items).All(z => ReferenceEquals(z.First, z.Second)));
        if (d == null)
        {
            var probe = new SkDimension(t, 0, items);
            double v = DimensionMeasure.Value(probe);
            if (double.IsNaN(v)) { w.ShowToast(L("Размер не измеряется")); return null; }
            d = sk.AddDimension(t, v, items);
        }
        d.IsDefault = false; d.Locked = true; d.Reference = false; d.UserPlaced = true;
        var st = sk.Solve(w.Session.Document.Solver);
        if ((Bad(st) && !Bad(st0)) || d.Conflict)
        {
            // противоречит связям — справочным (измеряет, не управляет)
            d.Locked = false; d.Reference = true;
            sk.Solve(w.Session.Document.Solver);
            w.ShowToast(L("Размер избыточен — поставлен справочным"), error: false);
        }
        sk.BumpStructure(); sk.NotifyChanged(sk); w.ViewPort.RefreshDefaultDimensions(); w.ViewPort.MarkDirty();
        return d;
    }

    /// <summary>Выбранные размеры активного эскиза (подписи).</summary>
    public static List<SkDimension> PickedDimensions(MainWindow w)
    {
        var sk = w.ViewPort.ActiveSketch;
        return sk == null ? new() : w.ViewPort.Selection.Items.Where(r => ReferenceEquals(r.Owner, sk) && r.Target is SkDimension).Select(r => (SkDimension)r.Target!).Distinct().ToList();
    }

    /// <summary>Действие над выбранными размерами (справочный, стрелки, замок…).</summary>
    public static void OnDimensions(MainWindow w, Action<SkDimension> act)
    {
        var sk = w.ViewPort.ActiveSketch; var ds = PickedDimensions(w);
        if (sk == null || ds.Count == 0) { w.ShowToast(L("Выберите размер (подпись) в эскизе"), error: false); return; }
        foreach (var d in ds) act(d);
        sk.Solve(w.Session.Document.Solver); sk.BumpStructure(); sk.NotifyChanged(sk); w.ViewPort.MarkDirty();
    }

    /// <summary>Справочный ⇄ управляющий.</summary>
    public static void ToggleReference(MainWindow w) => OnDimensions(w, d =>
    {
        if (d.UserLocked) return;
        d.Reference = !d.Reference;
        if (d.Reference) { d.Locked = false; d.IsDefault = false; } else d.Locked = true;
    });

    /// <summary>Связать с параметром: значение размера — формула (имя параметра или выражение).</summary>
    public static async void LinkParam(MainWindow w)
    {
        var sk = w.ViewPort.ActiveSketch; var ds = PickedDimensions(w);
        if (sk == null || ds.Count != 1) { w.ShowToast(L("Выберите один размер (подпись) в эскизе"), error: false); return; }
        var d = ds[0];
        var part = w.Session.Part;
        var table = part != null ? w.Session.Document.ScopeFor(part) : w.Session.Document.Parameters;
        var names = new List<string>(); for (var tt = table; tt != null; tt = tt.Parent) names.AddRange(tt.All.Select(x => x.Name));
        string? text = await Dialogs.Prompt(w, L("Связать с параметром"), L("Параметр или формула (например, W/2):") + (names.Count > 0 ? "\n" + L("Параметры: {0}", string.Join(", ", names)) : ""), d.Formula ?? names.FirstOrDefault() ?? "");
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!table.TryEvaluate(text.Trim(), out double v, out var err)) { w.ShowToast(L("Не формула: {0}", err ?? text)); return; }
        var res = SketchEditing.SetDimensionValue(sk, d, v, text.Trim(), w.Session.Document.Solver);
        w.AfterSketchEdit(sk);
        if (!res.Ok) w.ReportSketchConflict(sk, res, L("Размер = {0}", text.Trim()), () => SketchEditing.SetDimensionValue(sk, d, v, text.Trim(), w.Session.Document.Solver));
    }

    /// <summary>Площадь замкнутых областей эскиза (внешние контуры минус отверстия), мм²; null — замкнутых нет.</summary>
    public static (double Area, int Regions)? Area(Sketch sk)
    {
        Parametric3D.Kernel.Ops.Profile prof;
        try { prof = sk.ToProfile(); } catch { return null; }
        if (prof.IsEmpty || prof.HasSelfIntersections) return null;
        // площадь контура по точкам кривых: хорды N и 2N на каждой кривой, уточнение Ричардсона (ошибка ~N⁻⁴)
        double LoopArea(Parametric3D.Kernel.Ops.ProfileLoop l)
        {
            double Poly(int n)
            {
                double acc = 0;
                foreach (var c in l.Curves)
                {
                    bool straight = c is Parametric3D.Kernel.Curves.LineCurve3; int m = straight ? 1 : n;
                    var prev = prof.Plane.ToLocal2(c.PointAt(c.T0));
                    for (int k = 1; k <= m; k++) { var q = prof.Plane.ToLocal2(c.PointAt(c.T0 + (c.T1 - c.T0) * k / m)); acc += prev.X * q.Y - prev.Y * q.X; prev = q; }
                }
                return Math.Abs(acc / 2);
            }
            double a1 = Poly(512), a2 = Poly(1024);
            return (4 * a2 - a1) / 3;
        }
        double a = prof.Outers.Sum(LoopArea) - prof.Inners.Sum(LoopArea);
        return (a, prof.Outers.Count());
    }

    public static void ShowArea(MainWindow w)
    {
        var sk = w.ViewPort.ActiveSketch; if (sk == null) return;
        if (Area(sk) is not { } r) { w.ShowToast(L("В эскизе нет замкнутых областей"), error: false); return; }
        w.ShowToast(L("Площадь: {0} мм² (областей: {1})", r.Area.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), r.Regions), error: false);
    }
}
