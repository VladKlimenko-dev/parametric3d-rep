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
