using OpenTK.Mathematics;
using Parametric3D.App.Sketching;
using Parametric3D.App.Views;
using Parametric3D.Model.Sketching;
using static Parametric3D.App.Loc;

namespace Parametric3D.App.Commands;

/// <summary>
/// u13: ПРАВКА ЭСКИЗА ПО ВЫБРАННОМУ — зеркало относительно осевой, перенос/поворот/масштаб, смещение цепочки; рёбра модели
/// в эскиз (использовать, со смещением, утолщением); показ конфликтующих связей; параметры инструментов (стороны
/// многоугольника, текст, радиус и фаска угла).
/// </summary>
public static class SketchEditCommands
{
    static Sketch? Sk(MainWindow w) => w.ViewPort.ActiveSketch;
    static double Num(string s) => double.Parse(s.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
    static void Done(MainWindow w, Sketch sk)
    {
        sk.BumpStructure();
        try { sk.Solve(w.Session.Document.Solver); } catch (Exception ex) when (ex is not OperationCanceledException) { Core.AppLog.Write("sketch solve", ex); }
        sk.NotifyChanged(sk); w.ViewPort.RefreshDefaultDimensions(); w.ViewPort.MarkDirty();
    }

    /// <summary>Копия элемента с отображением точек (точки копии — новые).</summary>
    public static SkEntity? Copy(SkEntity e, Func<Vector2d, Vector2d> map, bool mirror, Dictionary<SkPoint, SkPoint> pmap)
    {
        SkPoint P(SkPoint p) { if (!pmap.TryGetValue(p, out var q)) pmap[p] = q = new SkPoint(map(p.P)); return q; }
        switch (e)
        {
            case SkLine l: return new SkLine(P(l.A), P(l.B)) { Role = l.Role };
            case SkCircle c: { var cc = P(c.Center); double r = (map(c.Center.P + new Vector2d(c.Radius, 0)) - cc.P).Length; return new SkCircle(cc, r) { Role = c.Role }; }
            case SkArc a: return new SkArc(P(a.Center), P(a.Start), P(a.End), mirror ? !a.CCW : a.CCW) { Role = a.Role };
            case SkPoint pt when pt.Owner == null: return P(pt);
            case SkSpline sp: return new SkSpline(sp.Ctrl.Select(P)) { Role = sp.Role };
            case SkEllipse el:
            {
                var c = P(el.Center); var ax = map(el.Center.P + new Vector2d(Math.Cos(el.Rotation), Math.Sin(el.Rotation)) * el.RU) - c.P;
                double sc = ax.Length / el.RU;
                return new SkEllipse(c, el.RU * sc, el.RV * sc, Math.Atan2(ax.Y, ax.X)) { Role = el.Role, A0 = el.A0, A1 = el.A1 };
            }
        }
        return null;
    }

    /// <summary>Зеркало выбранных относительно осевой (или последнего выбранного отрезка): копии и симметрия точек.</summary>
    public static void Mirror(MainWindow w)
    {
        var sk = Sk(w); if (sk == null) return;
        var sel = SketchCommands.Picked(w).Where(e => e is not SkPoint p || p.Owner == null).ToList();
        var axis = sel.OfType<SkLine>().FirstOrDefault(l => l.Role == SkRole.Centerline) ?? sel.OfType<SkLine>().LastOrDefault();
        if (axis == null || sel.Count < 2) { w.ShowToast(L("Выберите элементы и осевую линию (ось — последним отрезком)"), error: false); return; }
        sel.Remove(axis);
        var a = axis.A.P; var d = (axis.B.P - axis.A.P).Normalized();
        Vector2d M(Vector2d p) { var r = p - a; var along = d * Vector2d.Dot(r, d); return a + along - (r - along); }
        var pmap = new Dictionary<SkPoint, SkPoint>();
        foreach (var e in sel) if (Copy(e, M, true, pmap) is { } c) sk.AddEntity(c);
        foreach (var (src, dst) in pmap)
        {
            if ((src.P - dst.P).Length < 1e-9) sk.AddConstraint(ConstraintType.PointOnEntity, src, axis);
            else sk.AddConstraint(ConstraintType.Symmetric, src, dst, axis);
        }
        Done(w, sk);
    }

    /// <summary>Перенос, поворот (вокруг центра выбранного), масштаб выбранных элементов: «dx dy угол масштаб».</summary>
    public static async void Transform(MainWindow w)
    {
        var sk = Sk(w); if (sk == null) return;
        var sel = SketchCommands.Picked(w);
        if (sel.Count == 0) { w.ShowToast(L("Выберите элементы эскиза"), error: false); return; }
        // k128: сдвиги — в единице детали (число без единицы), «4.5in» — в указанной
        var text = await Dialogs.Prompt(w, L("Перенести, повернуть, масштаб"), L("Сдвиг X, сдвиг Y ({0}), угол (°), масштаб — через пробел; копия — добавьте «копия»:", DisplayUnits.LengthUnit), "10 0 0 1");
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text.Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        bool copy = parts.Any(x => x.StartsWith("коп", StringComparison.OrdinalIgnoreCase) || x.StartsWith("copy", StringComparison.OrdinalIgnoreCase));
        var vals = parts.Where(x => !x.StartsWith("коп", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("copy", StringComparison.OrdinalIgnoreCase)).ToList();
        double V(int i, Parametric3D.Core.Dimension dim, double dflt) => i < vals.Count && DisplayUnits.TryValue(vals[i], dim, out var v) ? v : dflt;
        double dx = V(0, Parametric3D.Core.Dimension.Length, 0), dy = V(1, Parametric3D.Core.Dimension.Length, 0), ang = V(2, Parametric3D.Core.Dimension.Angle, 0) * Math.PI / 180, sc = V(3, Parametric3D.Core.Dimension.None, 1);
        if (!(sc > 0)) { w.ShowToast(L("Масштаб должен быть больше нуля")); return; }
        var pts = sel.SelectMany(e => e.Handles).Distinct().ToList();
        var ctr = pts.Aggregate(Vector2d.Zero, (s, p) => s + p.P) / Math.Max(1, pts.Count);
        Vector2d Map(Vector2d p) { var r = (p - ctr) * sc; r = new Vector2d(r.X * Math.Cos(ang) - r.Y * Math.Sin(ang), r.X * Math.Sin(ang) + r.Y * Math.Cos(ang)); return ctr + r + new Vector2d(dx, dy); }
        if (copy)
        {
            var pmap = new Dictionary<SkPoint, SkPoint>();
            foreach (var e in sel.Where(e => e is not SkPoint p || p.Owner == null)) if (Copy(e, Map, false, pmap) is { } c) sk.AddEntity(c);
        }
        else
        {
            // сами точки; размеры и связи, мешающие движению (закрепление), сняты с выбранного
            sk.Constraints.RemoveAll(c => c.Type == ConstraintType.Fix && c.Items.Any(sel.Contains));
            foreach (var p in pts) p.P = Map(p.P);
            foreach (var c in sel.OfType<SkCircle>()) c.Radius *= sc;
            foreach (var el in sel.OfType<SkEllipse>()) { el.RU *= sc; el.RV *= sc; el.Rotation += ang; }
            sk.Dimensions.RemoveAll(d => d.Locked && !d.UserLocked && d.Items.Any(i => sel.Contains(i) || pts.Contains(i as SkPoint ?? new SkPoint(0, 0))));
        }
        Done(w, sk);
    }

    /// <summary>Смещённая копия кривой на d (влево по обходу у отрезка; у дуги и окружности — радиус ± d).</summary>
    public static SkEntity? OffsetOf(SkEntity e, double d)
    {
        switch (e)
        {
            case SkLine l: { var t = (l.B.P - l.A.P).Normalized(); var n = new Vector2d(-t.Y, t.X) * d; return new SkLine(new SkPoint(l.A.P + n), new SkPoint(l.B.P + n)) { Role = l.Role }; }
            case SkCircle c: return c.Radius + d > 1e-9 ? new SkCircle(new SkPoint(c.Center.P), c.Radius + d) { Role = c.Role } : null;
            case SkArc a:
            {
                double r = a.Radius + (a.CCW ? -d : d); if (!(r > 1e-9)) return null;
                Vector2d P(Vector2d q) => a.Center.P + (q - a.Center.P).Normalized() * r;
                return new SkArc(new SkPoint(a.Center.P), new SkPoint(P(a.Start.P)), new SkPoint(P(a.End.P)), a.CCW) { Role = a.Role };
            }
        }
        return null;
    }

    static (Vector2d A, Vector2d B)? Ends(SkEntity e) => e switch { SkLine l => (l.A.P, l.B.P), SkArc a => (a.Start.P, a.End.P), _ => null };
    static SkPoint? EndPt(SkEntity e, bool start) => e switch { SkLine l => start ? l.A : l.B, SkArc a => start ? a.Start : a.End, _ => null };

    /// <summary>Смещение выбранной цепочки на d: куски сдвигаются, соседние отрезки сходятся в новом углу, концы — совпадение.</summary>
    public static async void Offset(MainWindow w)
    {
        var sk = Sk(w); if (sk == null) return;
        var sel = SketchCommands.Picked(w).Where(e => e is SkLine or SkArc or SkCircle).ToList();
        if (sel.Count == 0) { w.ShowToast(L("Выберите отрезки, дуги или окружности"), error: false); return; }
        var text = await Dialogs.Prompt(w, L("Смещение цепочки"), L("Расстояние, {0} (знак — сторона):", DisplayUnits.LengthUnit), DisplayUnits.ShowText(5));
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!DisplayUnits.TryLength(text, out double d)) { w.ShowToast(L("Не число: {0}", text)); return; }
        OffsetChain(sk, sel, d);
        Done(w, sk);
    }

    public static List<SkEntity> OffsetChain(Sketch sk, List<SkEntity> sel, double d)
    {
        var made = new List<(SkEntity Src, SkEntity Off)>();
        foreach (var e in sel) if (OffsetOf(e, d) is { } o) made.Add((e, o));
        // общие концы исходных — у смещённых: отрезок с отрезком — пересечение прямых; иначе — совпадение
        for (int i = 0; i < made.Count; i++)
            for (int j = i + 1; j < made.Count; j++)
            {
                foreach (bool si in new[] { true, false })
                    foreach (bool sj in new[] { true, false })
                    {
                        var pi = EndPt(made[i].Src, si); var pj = EndPt(made[j].Src, sj);
                        if (pi == null || pj == null || (pi.P - pj.P).Length > 1e-6) continue;
                        var qi = EndPt(made[i].Off, si)!; var qj = EndPt(made[j].Off, sj)!;
                        if (made[i].Off is SkLine li && made[j].Off is SkLine lj)
                        {
                            var r = li.B.P - li.A.P; var s2 = lj.B.P - lj.A.P; double den = r.X * s2.Y - r.Y * s2.X;
                            if (Math.Abs(den) > 1e-12) { double t = ((lj.A.P - li.A.P).X * s2.Y - (lj.A.P - li.A.P).Y * s2.X) / den; var x = li.A.P + r * t; qi.P = x; qj.P = x; }
                        }
                        made[i] = made[i]; // концы — совпадение
                        sk.Constraints.Add(new SkConstraint(ConstraintType.Coincident, qi, qj));
                    }
            }
        foreach (var (_, o) in made) sk.AddEntity(o);
        return made.Select(m => m.Off).ToList();
    }

    // -------------------------------------------------------------- рёбра модели в эскиз

    public enum EdgeMode { None, Use, Offset, Thicken }
    public static EdgeMode Mode = EdgeMode.None;
    public static double ModeDistance = 5;

    /// <summary>Включить режим «щелчок по ребру модели»: использовать (проекция основной геометрией), со смещением, утолщением.</summary>
    public static async void StartEdgeMode(MainWindow w, EdgeMode m)
    {
        if (Sk(w) == null) return;
        if (m != EdgeMode.Use)
        {
            var text = await Dialogs.Prompt(w, L(m == EdgeMode.Offset ? "Смещение ребра" : "Утолщение ребра"), L(m == EdgeMode.Offset ? "Смещение, {0} (знак — сторона):" : "Толщина, {0}:", DisplayUnits.LengthUnit), DisplayUnits.ShowText(ModeDistance));
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!DisplayUnits.TryLength(text, out double md)) { w.ShowToast(L("Не число: {0}", text)); return; }
            ModeDistance = md;
        }
        Mode = m;
        w.ViewPort.Tool = null;
        w.ShowToast(L("Щёлкайте рёбра модели; Esc — закончить"), error: false);
    }

    /// <summary>Ребро модели в эскиз по режиму. true — обработано режимом (иначе — обычная ссылка).</summary>
    public static bool OnModelEdge(MainWindow w, Sketch sk, Parametric3D.Model.Part part, Parametric3D.Core.PickRef r)
    {
        if (Mode == EdgeMode.None || r.Kind != Parametric3D.Core.PickKind.Edge || part.FindEdge(r.Id) is not { Curve: { } c }) return false;
        var pool = new EdgeProjection.PointPool(SkRole.Object, "");
        var proj = EdgeProjection.Project(sk, c, pool, SkRole.Object, "");
        foreach (var e in proj) e.ReferenceSource = "";
        var curves = proj.Where(e => e is not SkPoint).ToList();
        if (Mode == EdgeMode.Use)
        {
            foreach (var e in proj) sk.AddEntity(e);
            foreach (var e in curves) sk.AddConstraint(ConstraintType.Fix, e);
        }
        else if (Mode == EdgeMode.Offset)
        {
            var off = curves.Select(e => OffsetOf(e, ModeDistance)).OfType<SkEntity>().ToList();
            foreach (var e in off) sk.AddEntity(e);
        }
        else
        {
            // утолщение: две кривые на ±t/2 и торцы-отрезки между ними (замкнутый контур)
            double h = ModeDistance / 2;
            foreach (var e in curves)
            {
                var a = OffsetOf(e, h); var b = OffsetOf(e, -h);
                if (a == null || b == null) continue;
                sk.AddEntity(a); sk.AddEntity(b);
                if (Ends(a) is { } ea && Ends(b) is { } eb)
                {
                    var c1 = sk.AddEntity(new SkLine(new SkPoint(ea.A), new SkPoint(eb.A)));
                    var c2 = sk.AddEntity(new SkLine(new SkPoint(ea.B), new SkPoint(eb.B)));
                    sk.AddConstraint(ConstraintType.Coincident, c1.A, EndPt(a, true)!); sk.AddConstraint(ConstraintType.Coincident, c1.B, EndPt(b, true)!);
                    sk.AddConstraint(ConstraintType.Coincident, c2.A, EndPt(a, false)!); sk.AddConstraint(ConstraintType.Coincident, c2.B, EndPt(b, false)!);
                }
            }
        }
        Done(w, sk);
        return true;
    }

    // -------------------------------------------------------------- конфликтующие связи

    /// <summary>Показать связи, мешающие решению: каждая проверяется снятием — без неё эскиз решается.</summary>
    public static void ShowConflicts(MainWindow w)
    {
        // k96: панель «Разрешить эскиз» — участники противоречия с действиями
        if (Sk(w) == null) return;
        if (!w.ResolvePanelOpen) w.ToggleResolvePanel();
    }

    // -------------------------------------------------------------- параметры инструментов

    public static async void Polygon(MainWindow w)
    {
        var text = await Dialogs.Prompt(w, L("Многоугольник"), L("Число сторон (3–64):"), PolygonTool.Sides.ToString());
        if (string.IsNullOrWhiteSpace(text) || !int.TryParse(text.Trim(), out int n) || n < 3 || n > 64) return;
        PolygonTool.Sides = n; w.SetTool(new PolygonTool(n));
    }

    public static async void Text(MainWindow w)
    {
        var text = await Dialogs.Prompt(w, L("Текст"), L("Текст и высота, {0} (через «;», например: ABC;10):", DisplayUnits.LengthUnit), $"{TextTool.Text};{DisplayUnits.ShowText(TextTool.Height)}");
        if (string.IsNullOrWhiteSpace(text)) return;
        var p = text.Split(';');
        TextTool.Text = p[0];
        if (p.Length > 1 && DisplayUnits.TryLength(p[1], out double th)) TextTool.Height = Math.Max(0.1, th);
        w.SetTool(new TextTool());
    }

    public static async void Corner(MainWindow w, bool chamfer)
    {
        var text = await Dialogs.Prompt(w, L(chamfer ? "Фаска угла" : "Скругление угла"), L(chamfer ? "Фаска, {0}:" : "Радиус, {0}:", DisplayUnits.LengthUnit), DisplayUnits.ShowText(chamfer ? CornerTool.Distance : CornerTool.Radius));
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!DisplayUnits.TryLength(text, out double v)) return;
        if (!(v > 0)) return;
        if (chamfer) CornerTool.Distance = v; else CornerTool.Radius = v;
        // выбраны два отрезка с общим концом — угол обрабатывается сразу (без щелчка по углу)
        if (w.ViewPort.ActiveSketch is { } sk && SketchCommands.Picked(w) is [SkLine a, SkLine b] && SketchSuggest.CommonCorner(a, b) is { } at)
        {
            if (CornerTool.Apply(sk, at, chamfer, v))
            {
                w.ViewPort.Selection.Clear();
                sk.Solve(w.Session.Document.Solver); sk.NotifyChanged(sk); w.ViewPort.RefreshDefaultDimensions(); w.ViewPort.MarkDirty();
            }
            else w.ShowToast(L(chamfer ? "Фаска не помещается на отрезках" : "Скругление не помещается на отрезках"));
            return;
        }
        w.SetTool(new CornerTool(chamfer));
    }

    /// <summary>Настройка эскиза: перевернуть плоскость, начало и верх — в исходное.</summary>
    /// <summary>
    /// Настройка эскиза — особый режим (SketchSetupOp): общий вид детали, стрелки нормали и «верха», плоскость-основа,
    /// основа и смещение нулевой точки, «верх» по ребру, грани, плоскости или точке. Из правки эскиза — правка
    /// завершается и после «ОК»/«Отмены» возобновляется.
    /// </summary>
    public static void Setup(MainWindow w)
    {
        var sk = Sk(w) ?? w.SelectedItems().OfType<Sketch>().FirstOrDefault();
        if (sk == null) { w.ShowToast(L("Выберите эскиз"), error: false); return; }
        bool back = w.InSketch;
        // k131: новый пустой эскиз на время настройки не удаляется и после неё остаётся новым
        (bool WasNew, Func<Sketch, Parametric3D.App.Operations.Operation>? Then) st = (false, null);
        if (back) st = w.FinishSketchForSetup();
        if (w.Session.Part?.Sketches.FirstOrDefault(s => s.Id == sk.Id) is not { } live) return;
        w.StartOperation(new Parametric3D.App.Operations.SketchSetupOp(live, back) { WasNew = st.WasNew, Then = st.Then });
    }
}
