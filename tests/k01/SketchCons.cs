using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Model.Sketching;

static partial class Program
{
    /// <summary>Многоугольник ломаной (отдельные точки у концов, совпадения — как у инструмента «Отрезок»), автопривязка.</summary>
    static (Sketch Sk, List<SkLine> Lines) SkPoly(params (double X, double Y)[] pts)
    {
        var sk = new Sketch("probe", Frame3.XY);
        var lines = new List<SkLine>();
        SkPoint? prev = null, first = null;
        for (int i = 0; i < pts.Length; i++)
        {
            var a = prev ?? new SkPoint(new Vector2d(pts[i].X, pts[i].Y));
            var bxy = pts[(i + 1) % pts.Length];
            SkPoint b = i == pts.Length - 1 ? new SkPoint(new Vector2d(bxy.X, bxy.Y)) : new SkPoint(new Vector2d(bxy.X, bxy.Y));
            var l = sk.AddEntity(new SkLine(a, b), autoConstrain: true);
            first ??= a;
            lines.Add(l);
            prev = b;
        }
        return (sk, lines);
    }

    static void SketchProbe()
    {
        var solver = new NumericSketchSolver();
        // снимок 2 (17:55): (0,0) → (85.22,0) → (85.22,74.73) → (132.36,74.73) → (53.67,40) → (0,40) → (0,0)
        var (sk, L) = SkPoly((0, 0), (85.22, 0), (85.22, 74.73), (132.36, 74.73), (53.67, 40), (0, 40));
        Console.WriteLine("constraints: " + string.Join(", ", sk.Constraints.Select(c => c.Type)));
        sk.Solve(solver);
        Console.WriteLine($"solve {sk.LastSolve} dof {sk.DegreesOfFreedom}");
        DefaultDimensions.Refresh(sk, solver);
        foreach (var d in sk.Dimensions) Console.WriteLine($"  dim {d.Type} {d.Value:0.##} default {d.IsDefault} locked {d.Locked}");
        Console.WriteLine($"after defaults: solve {sk.Solve(solver)} dof {sk.DegreesOfFreedom}");
        var top = L[2];
        var dTop = sk.Dimensions.FirstOrDefault(d => d.Type == DimensionType.DistancePointPoint && d.Items.Contains(top.A) && d.Items.Contains(top.B))
                   ?? sk.AddDimension(DimensionType.DistancePointPoint, 47.14, top.A, top.B);
        void Dump(string t) { Console.WriteLine(t + ": " + string.Join(" ", L.Select(l => $"({l.A.X:0.##},{l.A.Y:0.##})"))); }
        Dump("before");
        var r = SketchEditing.SetDimensionValue(sk, dTop, 10, null, solver);
        Console.WriteLine($"set 10: ok {r.Ok} held {r.HeldWeak} {r.Status} fail {r.Failure} cons [{string.Join(",", r.Constraints.Select(c => c.Type))}] dims [{string.Join(",", r.Dimensions.Select(d => d.Value.ToString("0.##")))}]");
        DefaultDimensions.Refresh(sk, solver);
        foreach (var d in sk.Dimensions) Console.WriteLine($"  dim {d.Type} {d.Value:0.##} default {d.IsDefault} locked {d.Locked}");
        Dump("after");
    }

    static (double X, double Y)[] StepPoly => new[] { (0.0, 0.0), (85.22, 0.0), (85.22, 74.73), (132.36, 74.73), (53.67, 40.0), (0.0, 40.0) };

    /// <summary>k96 (отзыв 17:55): система связей эскиза — правка размеров, противоречия, нуль и оси, удаление размеров.</summary>
    static void SketchConstraintChecks()
    {
        var solver = new NumericSketchSolver();
        Check("KRN-PARAM-K102", () =>
        {
            // k102: функции, условия, сравнения; идентификаторы размеров эскиза (d0…), пути s"…".dN, f"…".dN, p"…".x
            var t = new Parametric3D.Model.Params.ParameterTable();
            void Eq(string f, double want)
            {
                Require(t.TryEvaluate(f, out double v, out var err), $"{f}: {err}");
                Require(Math.Abs(v - want) < 1e-9, $"{f} = {v}, want {want}");
            }
            Eq("if(2 > 1, 10, 1/0)", 10); Eq("if(1 >= 2, 1, 7)", 7); Eq("3 == 3 && 2 != 3", 1); Eq("0 || 0", 0);
            Eq("sinh(0) + cosh(0)", 1); Eq("log(1000)", 3); Eq("ln(exp(2))", 2); Eq("pow(2, 10)", 1024); Eq("sign(-3)", -1);
            Eq("mod(7, 3)", 1); Eq("bound(15, 0, 10)", 10); Eq("max(1, 5, 3)", 5); Eq("trunc(-2.7)", -2);
            var doc = new Parametric3D.Model.Document();
            var part = new Parametric3D.Model.Part("Деталь1"); doc.Root.Add(part);
            var sk = part.AddSketch("Эскиз1", Frame3.XY, part.PlaneXY.Id);
            var l = new SkLine(new SkPoint(0, 0), new SkPoint(30, 0)); sk.AddEntity(l);
            var d0 = sk.AddDimension(DimensionType.DistancePointPoint, 30, l.A, l.B); d0.Locked = true;
            var d1 = sk.AddDimension(DimensionType.DistancePointPoint, 0, l.A, l.B); d1.Reference = true;
            Require(d0.Index == 0 && d1.Index == 1 && d0.Name == "d0", $"dimension ids {d0.Name} {d1.Name}");
            Require(sk.TryEvaluate("d0 * 2 + 1", null, out double sv, out var se) && Math.Abs(sv - 61) < 1e-9, $"sketch scope: {sv} {se}");
            var ex = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(12); part.AddFeature(ex);
            part.LocalParameters.Add("w", 5);
            var scope = doc.ScopeFor(part);
            Require(scope.TryEvaluate("s\"Эскиз1\".d0 + f\"" + ex.Name + "\".d0", out double pv, out var pe) && Math.Abs(pv - 42) < 1e-9, $"part paths: {pv} {pe}");
            Require(doc.Parameters.TryEvaluate("p\"Деталь1\".s\"Эскиз1\".d0 + p\"Деталь1\".w", out double dv, out var de) && Math.Abs(dv - 35) < 1e-9, $"document paths: {dv} {de}");
            Require(doc.Parameters.TryEvaluate("a\"" + doc.Root.Name + "\".p\"Деталь1\".s\"Эскиз1\".d0", out double av, out var ae) && Math.Abs(av - 30) < 1e-9, $"assembly path: {av} {ae}");
            Require(!scope.TryEvaluate("s\"Нет\".d0", out _, out _), "unknown path evaluated");
            // параметр-формула со ссылкой на размер
            var pf = part.LocalParameters.AddFormula("twice", "s\"Эскиз1\".d0 * 2");
            Require(pf.Error == null && Math.Abs(pf.Value - 60) < 1e-9, $"parameter from a dimension: {pf.Value} {pf.Error}");
            // идентификаторы сохраняются в файле; удалённый размер не сдвигает номера
            sk.Dimensions.Remove(d0);
            var d2 = sk.AddDimension(DimensionType.DistancePointPoint, 30, l.A, l.B);
            Require(d2.Index == 2, $"new dimension after delete: {d2.Name}");
            var (p2, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(part, null));
            var ids = p2.Sketches.First().Dimensions.Select(x => x.Index).OrderBy(x => x).ToList();
            Require(ids.SequenceEqual(new[] { 1, 2 }), "ids after reload: " + string.Join(",", ids));
        });
        Check("KRN-SK-CONFLICTS-K106", () =>
        {
            // k106 (снимок 13:13): совпадение концов кривых, уже касающихся друг друга (и прочие обычные сочетания), — не
            // «избыточно» и не «противоречиво»: касание по расстоянию (|d − r| = 0) вместе с общей точкой давало вырожденный
            // якобиан, решатель называл эскиз переопределённым, и связь отвергалась
            var solver = new NumericSketchSolver();
            var bad = new List<string>();
            void Case(string name, Func<Sketch, (ConstraintType, SkEntity[])> build)
            {
                var part = new Parametric3D.Model.Part("c");
                var sk = part.AddSketch("S", Frame3.XY, part.PlaneXY.Id);
                var (t, items) = build(sk);
                sk.Solve(solver);
                var r = SketchEditing.AddConstraints(sk, new[] { (t, items) }, solver);
                if (!r.Ok) bad.Add($"{name}: {r.Failure} [{string.Join(", ", r.Constraints.Select(c => c.Type))}]");
            }
            SkArc Arc(Sketch sk, double cx, double cy, double r, double a0, double a1)
                => sk.AddEntity(new SkArc(new SkPoint(cx, cy), new SkPoint(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)), new SkPoint(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)), true));
            // отрезок касается дуги, концы рядом — совпадение
            Case("line–arc", sk =>
            {
                var l = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, -30)));
                var a = Arc(sk, 20, -35, 20, Math.PI * 0.95, Math.PI * 1.5);
                sk.AddConstraint(ConstraintType.Tangent, l, a);
                return (ConstraintType.Coincident, new SkEntity[] { l.B, a.Start });
            });
            // дуга касается дуги, концы рядом — совпадение
            Case("arc–arc", sk =>
            {
                var a = Arc(sk, 0, 0, 10, Math.PI * 0.5, Math.PI * 0.02);
                a.CCW = false;
                var b = Arc(sk, 25, 0, 15, Math.PI * 0.95, Math.PI * 0.5);
                b.CCW = false;
                sk.AddConstraint(ConstraintType.Tangent, a, b);
                return (ConstraintType.Coincident, new SkEntity[] { a.End, b.Start });
            });
            // касание после совпадения (обратный порядок)
            Case("arc–arc tangent after coincident", sk =>
            {
                var a = Arc(sk, 0, 0, 10, Math.PI * 0.5, 0); a.CCW = false;
                var b = Arc(sk, 25.3, 0.4, 15.2, Math.PI, Math.PI * 0.5); b.CCW = false;
                sk.AddConstraint(ConstraintType.Coincident, a.End, b.Start);
                return (ConstraintType.Tangent, new SkEntity[] { a, b });
            });
            // конец отрезка на окружности, затем касание
            Case("point-on + tangent", sk =>
            {
                var c = sk.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
                var l = sk.AddEntity(new SkLine(new SkPoint(10, 0), new SkPoint(10.5, 20)));
                sk.AddConstraint(ConstraintType.PointOnEntity, l.A, c);
                return (ConstraintType.Tangent, new SkEntity[] { l, c });
            });
            // совпадение через третью точку
            Case("coincident chain", sk =>
            {
                var l = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, -30)));
                var a = Arc(sk, 20, -35, 20, Math.PI * 0.95, Math.PI * 1.5);
                var m = sk.AddEntity(new SkLine(new SkPoint(0.2, -31), new SkPoint(-10, -40)));
                sk.AddConstraint(ConstraintType.Tangent, l, a);
                sk.AddConstraint(ConstraintType.Coincident, m.A, a.Start);
                return (ConstraintType.Coincident, new SkEntity[] { l.B, m.A });
            });
            // контур: два отрезка касаются двух дуг, замыкание последнего угла
            Case("closed slot", sk =>
            {
                var top = Arc(sk, 0, 20, 10, 0, Math.PI);
                var bot = Arc(sk, 0, -20, 10, Math.PI, 2 * Math.PI);
                var l1 = sk.AddEntity(new SkLine(new SkPoint(-10, 20), new SkPoint(-10, -20)));
                var l2 = sk.AddEntity(new SkLine(new SkPoint(10.3, -19.6), new SkPoint(10, 20)));
                sk.AddConstraint(ConstraintType.Coincident, top.End, l1.A); sk.AddConstraint(ConstraintType.Coincident, l1.B, bot.Start);
                sk.AddConstraint(ConstraintType.Coincident, l2.B, top.Start);
                sk.AddConstraint(ConstraintType.Tangent, top, l1); sk.AddConstraint(ConstraintType.Tangent, l1, bot);
                sk.AddConstraint(ConstraintType.Tangent, bot, l2); sk.AddConstraint(ConstraintType.Tangent, l2, top);
                return (ConstraintType.Coincident, new SkEntity[] { bot.End, l2.A });
            });
            // касание отрезка и окружности, затем конец отрезка на окружности
            Case("tangent + point-on", sk =>
            {
                var c = sk.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
                var l = sk.AddEntity(new SkLine(new SkPoint(10, 1), new SkPoint(10, 20)));
                sk.AddConstraint(ConstraintType.Tangent, l, c);
                return (ConstraintType.PointOnEntity, new SkEntity[] { l.A, c });
            });
            // эллипс: конец отрезка на эллипсе, затем касание
            Case("ellipse point-on + tangent", sk =>
            {
                var el = sk.AddEntity(new SkEllipse(new SkPoint(0, 0), 20, 10));
                var l = sk.AddEntity(new SkLine(new SkPoint(20, 0), new SkPoint(21, 15)));
                sk.AddConstraint(ConstraintType.PointOnEntity, l.A, el);
                return (ConstraintType.Tangent, new SkEntity[] { l, el });
            });
            // две окружности касаются, точка касания связана с обеими
            Case("circles tangent + common point", sk =>
            {
                var c1 = sk.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
                var c2 = sk.AddEntity(new SkCircle(new SkPoint(25, 0), 15));
                var pt = sk.AddEntity(new SkPoint(10, 0.5));
                sk.AddConstraint(ConstraintType.Tangent, c1, c2);
                sk.AddConstraint(ConstraintType.PointOnEntity, pt, c1);
                return (ConstraintType.PointOnEntity, new SkEntity[] { pt, c2 });
            });
            // защита правки сама по себе (касание записано расстоянием — вырождено при общей точке): повтор и проверка
            // «следует ли из прочих»
            NumericSketchSolver.ForceDistanceTangent = true;
            try
            {
                Case("distance tangent + point-on (guard)", sk =>
                {
                    var c = sk.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
                    var l = sk.AddEntity(new SkLine(new SkPoint(10, 1), new SkPoint(10, 20)));
                    sk.AddConstraint(ConstraintType.Tangent, l, c);
                    return (ConstraintType.PointOnEntity, new SkEntity[] { l.A, c });
                });
                Case("distance tangent arcs + coincident (guard)", sk =>
                {
                    var a = Arc(sk, 0, 0, 10, Math.PI * 0.5, Math.PI * 0.02); a.CCW = false;
                    var b = Arc(sk, 25, 0, 15, Math.PI * 0.95, Math.PI * 0.5); b.CCW = false;
                    sk.AddConstraint(ConstraintType.Tangent, a, b);
                    return (ConstraintType.Coincident, new SkEntity[] { a.End, b.Start });
                });
                // точки уже на месте: по рангу «избыточно», но совпадение не следует из касания (проверка возмущением)
                Case("distance tangent arcs + coincident in place (guard)", sk =>
                {
                    var a = Arc(sk, 0, 0, 10, Math.PI * 0.5, 0); a.CCW = false;
                    var b = Arc(sk, 25, 0, 15, Math.PI, Math.PI * 0.5); b.CCW = false;
                    sk.AddConstraint(ConstraintType.Tangent, a, b);
                    return (ConstraintType.Coincident, new SkEntity[] { a.End, b.Start });
                });
            }
            finally { NumericSketchSolver.ForceDistanceTangent = false; }
            // точка касания по связям: эскиз с касанием и общей точкой — не «переопределён»
            {
                var part = new Parametric3D.Model.Part("k"); var sk = part.AddSketch("S", Frame3.XY, part.PlaneXY.Id);
                var a = Arc(sk, 0, 0, 10, Math.PI * 0.5, 0); a.CCW = false;
                var b = Arc(sk, 25, 0, 15, Math.PI, Math.PI * 0.5); b.CCW = false;
                sk.AddConstraint(ConstraintType.Coincident, a.End, b.Start); sk.AddConstraint(ConstraintType.Tangent, a, b);
                var st = sk.Solve(solver);
                if (st == SolveStatus.Overconstrained) bad.Add("arc–arc tangent at a common point: overconstrained");
            }
            // настоящая избыточность и противоречие по-прежнему отвергаются
            {
                var part = new Parametric3D.Model.Part("r"); var sk = part.AddSketch("S", Frame3.XY, part.PlaneXY.Id);
                var l = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(10, 0)));
                sk.AddConstraint(ConstraintType.Horizontal, l); sk.Solve(solver);
                var r = SketchEditing.AddConstraints(sk, new[] { (ConstraintType.Vertical, new SkEntity[] { l }) }, solver);
                if (r.Ok) bad.Add("horizontal + vertical accepted");
                var l2 = sk.AddEntity(new SkLine(new SkPoint(0, 5), new SkPoint(10, 5)));
                sk.AddConstraint(ConstraintType.Horizontal, l2); sk.Solve(solver);
                var r2 = SketchEditing.AddConstraints(sk, new[] { (ConstraintType.Parallel, new SkEntity[] { l, l2 }) }, solver);
                if (r2.Ok) bad.Add("parallel of two horizontals accepted (redundant)");
            }
            Require(bad.Count == 0, string.Join(" | ", bad));
        });
        Check("KRN-SK-LINKS-K110", () =>
        {
            // k110: ссылка на эскиз параллельной плоскости следует за источником — эскиз выдавливания (окружность, равная
            // ссылочной) и объём меняются вместе с исходным эскизом; после чтения файла — тоже. Мутация P3D_SKLINKS=0.
            var part = new Parametric3D.Model.Part("links");
            var doc = new Parametric3D.Model.Document(); doc.Root.Add(part);
            var src = part.AddSketch("Src", new Frame3(new Vector3d(0, 0, -10), Vector3d.UnitX, Vector3d.UnitY));
            var sc = src.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
            var t = part.AddSketch("T", Frame3.XY, part.PlaneXY.Id);
            var rc = (SkCircle)SketchLinks.Make(t, src, sc)!; t.AddEntity(rc);
            var own = t.AddEntity(new SkCircle(new SkPoint(1, 1), 8));
            t.AddConstraint(ConstraintType.Concentric, own, rc); t.AddConstraint(ConstraintType.Equal, own, rc);
            t.Solve(new NumericSketchSolver());
            var ex = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = t.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(5); part.AddFeature(ex);
            doc.RebuildDirty();
            Require(Math.Abs(part.Volume - Math.PI * 100 * 5) < 0.01 * part.Volume, $"initial volume {part.Volume}");
            sc.Radius = 15; src.NotifyChanged(src);
            doc.RebuildDirty();
            Require(Math.Abs(rc.Radius - 15) < 1e-9 && Math.Abs(own.Radius - 15) < 1e-6, $"reference {rc.Radius}, own {own.Radius}");
            Require(Math.Abs(part.Volume - Math.PI * 225 * 5) < 0.01 * part.Volume, $"volume did not follow: {part.Volume}");
            var (p2, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(part));
            var d2 = new Parametric3D.Model.Document(); d2.Root.Add(p2); d2.RebuildDirty();
            var s2 = p2.Sketches.First(k => k.Name == "Src"); s2.Entities.OfType<SkCircle>().First().Radius = 12; s2.NotifyChanged(s2);
            d2.RebuildDirty();
            Require(Math.Abs(p2.Volume - Math.PI * 144 * 5) < 0.01 * p2.Volume, $"after reload: volume {p2.Volume}");
        });
        Check("KRN-SK-FORMULA-LIVE", () =>
        {
            // k103 (замечание 2): размер d1 = d0 + 5 следует за слабым размером d0 при перетаскивании точки (решатель держит
            // формулу по текущей геометрии). Мутация P3D_SKFORMULALIVE=0.
            var part = new Parametric3D.Model.Part("live");
            var sk = part.AddSketch("S", Frame3.XY, part.PlaneXY.Id);
            var a = new SkLine(new SkPoint(0, 0), new SkPoint(30, 0)); sk.AddEntity(a);
            var b = new SkLine(new SkPoint(0, 10), new SkPoint(20, 10)); sk.AddEntity(b);
            a.A.Fixed = true; b.A.Fixed = true;
            sk.AddConstraint(ConstraintType.Horizontal, a); sk.AddConstraint(ConstraintType.Horizontal, b);
            var d0 = sk.AddDimension(DimensionType.DistancePointPoint, 30, a.A, a.B); d0.IsDefault = true; d0.Locked = false;
            var d1 = sk.AddDimension(DimensionType.DistancePointPoint, 35, b.A, b.B); d1.Locked = true; d1.Formula = "d0 + 5";
            var solver = new NumericSketchSolver();
            sk.Solve(solver);
            Require(Math.Abs(b.Length - 35) < 1e-6, $"formula dimension: {b.Length}");
            a.B.P = new Vector2d(50, 0);
            sk.Solve(solver, a.B);
            Require(Math.Abs(a.Length - 50) < 0.5, $"dragged line: {a.Length}");
            Require(Math.Abs(b.Length - a.Length - 5) < 1e-6 && Math.Abs(d1.Value - b.Length) < 1e-6, $"d1 did not follow d0 while dragging: {b.Length}, d1 {d1.Value}");
        });
        Check("KRN-REF-ROLES", () =>
        {
            // k104: фаска на кромке отверстия, вырезанного точным путём (подпись стенки «quadric:hole», ссылка — «hole:wall»);
            // скругление кромки бобышки после перестановки (точный путь ↔ общий). Мутации P3D_TAGROLES=0, P3D_REFLOOP=0.
            var p = new Parametric3D.Model.Part("roles");
            var doc = new Parametric3D.Model.Document(); doc.Root.Add(p);
            var s1 = PolySketch(p, Frame3.XY, (0, 0), (80, 0), (80, 50), (0, 50));
            var ex = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = s1.Id }; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(6); p.AddFeature(ex);
            var s2 = p.AddSketch("H", Frame3.XY, p.PlaneXY.Id); s2.AddEntity(new SkCircle(new SkPoint(20, 25), 5));
            var cut = new Parametric3D.Model.Features.ExtrudeFeature { SketchId = s2.Id, Cut = true }; cut.Side1.Distance = new Parametric3D.Model.Features.ValueRef(10); p.AddFeature(cut);
            doc.RebuildDirty();
            var hole = p.Bodies.SelectMany(b => b.Edges).Where(e => e.Curve is not Parametric3D.Kernel.Curves.LineCurve3 && e.V0.P.Z > 5.9).ToList();
            Require(hole.Count > 0, "hole edges");
            // ссылка на окружность целиком, на теле — четыре дуги: берутся все
            var whole = new Parametric3D.Kernel.Topology.EdgeRef(new Vector3d(25, 25, 6), new Vector3d(25, 25, 6), new Vector3d(15, 25, 6), hole[0].FaceA!.Tag, hole[0].FaceB!.Tag);
            var norm = Parametric3D.Kernel.Ops.FilletOp.NormalizeLoopRefs(p.Bodies.ToList(), new[] { whole });
            Require(norm.Count == hole.Count || hole.Count == 1, $"whole-loop reference: {norm.Count} of {hole.Count}");
            var ch = new Parametric3D.Model.Features.ChamferFeature { D1 = new Parametric3D.Model.Features.ValueRef(1), D2 = new Parametric3D.Model.Features.ValueRef(1) };
            ch.Edges.AddRange(hole.Select(Parametric3D.Kernel.Topology.EdgeRef.Of)); p.AddFeature(ch);
            doc.RebuildDirty();
            Require(ch.Error == null, "chamfer of an exact hole: " + ch.Error);
            Require(Parametric3D.Kernel.Topology.EdgeRef.Role("#k/quadric:boss-top") == Parametric3D.Kernel.Topology.EdgeRef.Role("#k/extrude[0]:top")
                    && Parametric3D.Kernel.Topology.EdgeRef.Role("#k/quadric:hole") != Parametric3D.Kernel.Topology.EdgeRef.Role("#j/extrude[0]:side[0.0]"), "face roles");
        });
        Check("KRN-REV-MIRROR", () =>
        {
            // k97 (замечание 2): зеркало операции «Вращение» — кольцо у x = 50 отражается к x = −50 (прежде отказ
            // «this operation cannot be repeated by a mirror or pattern»). Мутация P3D_REVMIRROR=0.
            var p = new Parametric3D.Model.Part("revmirror");
            var XZ = new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);
            var s = PolySketch(p, XZ, (55, 0), (65, 0), (65, 10), (55, 10));
            var ax = new SkLine(new SkPoint(50, 0), new SkPoint(50, 10)) { Role = SkRole.Centerline }; s.AddEntity(ax);
            var rv = new Parametric3D.Model.Features.RevolveFeature { SketchId = s.Id, AxisEntityId = ax.Id };
            p.AddFeature(rv);
            var m = new Parametric3D.Model.Features.MirrorFeature { At = Vector3d.Zero, Normal = Vector3d.UnitX }; m.Sources.Add(rv.Key);
            p.AddFeature(m);
            p.Rebuild(null);
            double ring = Math.PI * (15 * 15 - 5 * 5) * 10;
            Require(p.Features.All(f => f.Error == null), "error: " + string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => f.Error)));
            Require(Math.Abs(p.Volume - 2 * ring) < 1e-6 * ring, $"volume {p.Volume:0.###} vs {2 * ring:0.###}");
            var xs = p.Bodies.SelectMany(b => b.Vertices).Select(v => v.P.X).ToList();
            Require(xs.Min() < -60 && xs.Max() > 60, $"mirrored ring is not at x = −50: x {xs.Min():0.#}..{xs.Max():0.#}");
        });
        Check("KRN-SK-WEAK-DOF", () =>
        {
            // k97 (замечание 7): эскиз, образмеренный слабыми размерами, — без «свободных» степеней (значок дерева);
            // DegreesOfFreedom по-прежнему без них. Мутация P3D_SKWEAKDOF=0.
            var (sk, _) = SkPoly(StepPoly);
            sk.Solve(solver);
            int dof = sk.DegreesOfFreedom;
            Require(dof > 0 && sk.FreeDegrees == dof, $"dof {dof} free {sk.FreeDegrees} before weak dims");
            DefaultDimensions.Refresh(sk, solver);
            sk.Solve(solver);
            int weak = sk.Dimensions.Count(d => d.IsDefault);
            Require(weak > 0 && sk.DegreesOfFreedom == dof, $"weak {weak} dof {sk.DegreesOfFreedom}");
            Require(sk.FreeDegrees == Math.Max(0, dof - weak), $"free {sk.FreeDegrees}, dof {dof}, weak {weak}");
        });
        Check("KRN-SK-NO-TRIVIAL", () =>
        {
            // ломаная (общие точки соседних отрезков): автопривязка не ставит «конец на своём отрезке», эскиз не переопределён
            var (sk, _) = SkPoly(StepPoly);
            Require(!sk.Constraints.Any(Sketch.IsTrivial), "trivial constraints: " + string.Join(",", sk.Constraints.Where(Sketch.IsTrivial).Select(c => c.Type)));
            var st = sk.Solve(solver);
            Require(st == SolveStatus.Underconstrained, $"status {st}");
            // уже наложенные пустые связи (старые эскизы) убираются при решении
            var (sk2, L2) = SkPoly(StepPoly);
            sk2.Constraints.Add(new SkConstraint(ConstraintType.PointOnEntity, L2[1].A, L2[0]));
            Require(sk2.Solve(solver) == SolveStatus.Underconstrained && !sk2.Constraints.Any(Sketch.IsTrivial), "an old trivial constraint was not removed");
        });
        Check("KRN-SK-EDIT-STEP", () =>
        {
            // снимок 2: верх 47.14 → 10 — сдвигается только правый конец верха (наклонный отрезок укорачивается)
            var (sk, L) = SkPoly(StepPoly);
            sk.Solve(solver); DefaultDimensions.Refresh(sk, solver);
            var top = L[2];
            var d = sk.Dimensions.First(x => x.Type == DimensionType.DistancePointPoint && x.Items.Contains(top.A) && x.Items.Contains(top.B));
            var before = L.Select(l => l.A.P).ToList();
            var r = SketchEditing.SetDimensionValue(sk, d, 10, null, solver);
            Require(r.Ok && r.HeldWeak, $"edit failed: {r.Failure} {r.Status}");
            Require(Math.Abs(top.Length - 10) < 1e-7, $"top length {top.Length}");
            for (int i = 0; i < L.Count; i++)
            {
                double moved = (L[i].A.P - before[i]).Length;
                if (i == 3) Require(Math.Abs(L[i].A.P.X - 95.22) < 1e-4 && Math.Abs(L[i].A.P.Y - 74.73) < 1e-4, $"top right at {L[i].A.P}");
                else Require(moved < 1e-4, $"vertex {i} moved by {moved:G3}");
            }
        });
        Check("KRN-SK-EDIT-REJECT", () =>
        {
            // прямоугольный треугольник 30-40-50 сильными размерами; катет 45 — противоречие: участники — две другие стороны и
            // прямой угол (горизонталь/вертикаль катетов), геометрия и значение — как были
            var (sk, L) = SkPoly((0, 0), (40, 0), (0, 30));
            var dims = L.Select(l => sk.AddDimension(DimensionType.DistancePointPoint, l.Length, l.A, l.B)).ToList();
            foreach (var x in dims) x.Locked = true;
            sk.Solve(solver);
            var before = L.Select(l => l.A.P).ToList();
            var r = SketchEditing.SetDimensionValue(sk, dims[2], 45, null, solver);
            Require(!r.Ok && r.Failure == SketchEditFailure.Inconsistent, $"expected a rejection, got ok {r.Ok} {r.Failure}");
            Require(r.Dimensions.Contains(dims[0]) && r.Dimensions.Contains(dims[1]) && r.Constraints.Count > 0,
                $"participants: {string.Join(",", r.Dimensions.Select(x => x.Value))} {string.Join(",", r.Constraints.Select(c => c.Type))}");
            Require(Math.Abs(dims[2].Value - 30) < 1e-9 && dims[2].Locked, $"value {dims[2].Value}");
            for (int i = 0; i < L.Count; i++) Require((L[i].A.P - before[i]).Length < 1e-9, $"vertex {i} moved after the rejection");
            // недоопределённый эскиз: хорда окружности R3 длиной 5 → 10 (невозможно) — хорда остаётся, где была
            var s2 = new Sketch("chord", Frame3.XY);
            var c = s2.AddEntity(new SkCircle(new SkPoint(0, 0), 3));
            var ch = s2.AddEntity(new SkLine(new SkPoint(-2.5, Math.Sqrt(9 - 6.25)), new SkPoint(2.5, Math.Sqrt(9 - 6.25))));
            s2.AddConstraint(ConstraintType.PointOnEntity, ch.A, c); s2.AddConstraint(ConstraintType.PointOnEntity, ch.B, c);
            s2.AddConstraint(ConstraintType.Fix, c.Center);
            var dr = s2.AddDimension(DimensionType.Radius, 3, c); dr.Locked = true;
            var dl = s2.AddDimension(DimensionType.DistancePointPoint, 5, ch.A, ch.B); dl.Locked = true;
            s2.Solve(solver);
            var a0 = ch.A.P; var b0 = ch.B.P;
            var r2 = SketchEditing.SetDimensionValue(s2, dl, 10, null, solver);
            Require(!r2.Ok && r2.Dimensions.Contains(dr), $"chord 10 in R3: ok {r2.Ok}, participants {r2.Dimensions.Count}");
            Require((ch.A.P - a0).Length < 1e-9 && (ch.B.P - b0).Length < 1e-9, $"the chord moved after the rejection: {ch.A.P} {ch.B.P}");
        });
        Check("KRN-SK-ZERO-DIM", () =>
        {
            // размер 0: точка на прямой (точка — прямая) и совпадение центров двух окружностей (точка — точка)
            var sk = new Sketch("z", Frame3.XY);
            var l = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(50, 0)));
            var p = sk.AddEntity(new SkPoint(20, 7));
            var c1 = sk.AddEntity(new SkCircle(new SkPoint(80, 10), 5));
            var c2 = sk.AddEntity(new SkCircle(new SkPoint(90, 14), 8));
            var dpl = sk.AddDimension(DimensionType.DistancePointLine, 7, p, l);
            var dpp = sk.AddDimension(DimensionType.DistancePointPoint, (c1.Center.P - c2.Center.P).Length, c1.Center, c2.Center);
            var r1 = SketchEditing.SetDimensionValue(sk, dpl, 0, null, solver);
            double dl = Math.Abs((l.B.P - l.A.P).X * (p.P - l.A.P).Y - (l.B.P - l.A.P).Y * (p.P - l.A.P).X) / l.Length;
            Require(r1.Ok && dl < 1e-9, $"point-line 0: ok {r1.Ok} {r1.Failure}, distance {dl}");
            var r2 = SketchEditing.SetDimensionValue(sk, dpp, 0, null, solver);
            Require(r2.Ok && (c1.Center.P - c2.Center.P).Length < 1e-9, $"point-point 0: ok {r2.Ok} {r2.Failure}, gap {(c1.Center.P - c2.Center.P).Length}");
        });
        Check("KRN-SK-SIDE", () =>
        {
            // точка под прямой: размер 10 → 5 — точка остаётся под прямой (прежде знаковое уравнение перебрасывало её)
            var sk = new Sketch("s", Frame3.XY);
            var l = sk.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(50, 0)));
            sk.AddConstraint(ConstraintType.Fix, l);
            var p = sk.AddEntity(new SkPoint(20, -10));
            var d = sk.AddDimension(DimensionType.DistancePointLine, 10, p, l);
            var r = SketchEditing.SetDimensionValue(sk, d, 5, null, solver);
            Require(r.Ok && Math.Abs(p.Y + 5) < 1e-7, $"point at {p.P} (expected y = −5)");
        });
        Check("KRN-SK-BUILTIN", () =>
        {
            // п. 1: связи к нулю и осям эскиза; в файле — роли элементов и встроенные ссылки
            var part = new Parametric3D.Model.Part("p");
            var sk = new Sketch("b", Frame3.XY); part.Children.Add(sk);
            var l = sk.AddEntity(new SkLine(new SkPoint(3, 4), new SkPoint(30, 9)));
            var cl = sk.AddEntity(new SkLine(new SkPoint(-10, -10), new SkPoint(-10, 20)) { Role = SkRole.Construction });
            var r = SketchEditing.AddConstraints(sk, new[] { (ConstraintType.Coincident, new SkEntity[] { l.A, sk.OriginPoint }), (ConstraintType.PointOnEntity, new SkEntity[] { l.B, sk.AxisH }) }, solver);
            Require(r.Ok, $"constraints to the origin and the axis: {r.Failure}");
            Require(l.A.P.Length < 1e-9 && Math.Abs(l.B.Y) < 1e-9, $"line at {l.A.P} {l.B.P}");
            var json = Parametric3D.Model.DocumentFile.Save(part);
            var (p2, _) = Parametric3D.Model.DocumentFile.Load(json);
            var s2 = p2.Sketches.Single();
            Require(s2.Constraints.Any(c => c.Items.Contains(s2.OriginPoint)) && s2.Constraints.Any(c => c.Items.Contains(s2.AxisH)), "builtin references lost in the file");
            Require(s2.Entities.OfType<SkLine>().Count(x => x.Role == SkRole.Construction) == 1, "construction role lost in the file");
        });
        Check("KRN-SK-DELETE-DIMS", () =>
        {
            // п. 17: удаление сильного размера, нужного эскизу, — он становится слабым; справочный — удаляется
            var (sk, L) = SkPoly((0, 0), (40, 0), (40, 20), (0, 20));
            sk.Solve(solver); DefaultDimensions.Refresh(sk, solver);
            var w = sk.Dimensions.First(x => x.Items.Contains(L[0].A) && x.Items.Contains(L[0].B));
            var r = SketchEditing.SetDimensionValue(sk, w, 50, null, solver);
            Require(r.Ok && !w.IsDefault && w.Locked, "width did not become strong");
            var refd = sk.AddDimension(DimensionType.DistancePointPoint, L[1].Length, L[1].A, L[1].B); refd.Reference = true;
            var (removed, weakened) = SketchEditing.DeleteDimensions(sk, new[] { w, refd }, solver);
            Require(!sk.Dimensions.Contains(refd), "the reference dimension stayed");
            Require(sk.Dimensions.Any(x => x.IsDefault && x.Items.Contains(L[0].A) && x.Items.Contains(L[0].B) && Math.Abs(x.Value - 50) < 1e-6), "the needed width did not stay as a weak dimension");
            Require(Math.Abs(L[0].Length - 50) < 1e-6, $"geometry changed: width {L[0].Length}");
        });
        Check("KRN-SK-KEEP-WEAK", () =>
        {
            // п. 8: вид слабого размера («одна стрелка») переживает пересборку слабых размеров — тот же объект
            var sk = new Sketch("k", Frame3.XY);
            var a = sk.AddEntity(new SkCircle(new SkPoint(0, 0), 10));
            var b = sk.AddEntity(new SkCircle(new SkPoint(0, 0), 20));
            sk.AddConstraint(ConstraintType.Concentric, a, b);
            sk.Solve(solver); DefaultDimensions.Refresh(sk, solver);
            var da = sk.Dimensions.First(x => x.Items.Contains(a)); var db = sk.Dimensions.First(x => x.Items.Contains(b));
            da.SingleArrow = true;
            DefaultDimensions.Refresh(sk, solver);
            Require(sk.Dimensions.Contains(da) && da.SingleArrow && sk.Dimensions.Contains(db) && !db.SingleArrow, "weak dimension display settings were not kept");
        });
        Check("KRN-SK-REDUNDANT", () =>
        {
            // связь, уже следующая из других (оба конца на горизонтальной оси + «горизонтально»), — отказ с участниками
            var sk = new Sketch("r", Frame3.XY);
            var l = sk.AddEntity(new SkLine(new SkPoint(5, 0), new SkPoint(25, 0)));
            sk.AddConstraint(ConstraintType.PointOnEntity, l.A, sk.AxisH);
            sk.AddConstraint(ConstraintType.PointOnEntity, l.B, sk.AxisH);
            var r = SketchEditing.AddConstraints(sk, new[] { (ConstraintType.Horizontal, new SkEntity[] { l }) }, solver);
            Require(!r.Ok && r.Failure == SketchEditFailure.Redundant && r.Constraints.Count >= 1, $"redundant constraint: ok {r.Ok} {r.Failure}, participants {r.Constraints.Count}");
            Require(!sk.Constraints.Any(c => c.Type == ConstraintType.Horizontal), "the redundant constraint stayed");
        });
    }
}
