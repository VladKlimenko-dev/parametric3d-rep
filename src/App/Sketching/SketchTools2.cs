using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Model.Sketching;
using Parametric3D.Kernel;

namespace Parametric3D.App.Sketching;

// u13: ИНСТРУМЕНТЫ ЭСКИЗА ПО ТАБЛИЦЕ ИКОНОК — рисование (прямоугольник от центра и по трём точкам, окружность по двум
// точкам, касательная дуга, эллипс по пяти точкам, сплайн через точки, многоугольник, паз, текст, система координат
// эскиза) и правка (обрезка, разделение, скругление и фаска угла, отрезок касательный к двум дугам).

/// <summary>Геометрия кривых эскиза: пересечения, параметры, куски.</summary>
public static class SkGeom
{
    static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Окружность-носитель дуги или окружности.</summary>
    public static (Vector2d C, double R)? CircleOf(SkEntity e) => e switch { SkCircle c => (c.Center.P, c.Radius), SkArc a => (a.Center.P, a.Radius), _ => null };

    /// <summary>Параметр точки на кривой: отрезок — t ∈ [0, 1]; дуга — доля обхода ∈ [0, 1]; окружность — угол ∈ [0, 2π).</summary>
    public static double Param(SkEntity e, Vector2d p)
    {
        switch (e)
        {
            case SkLine l: { var d = l.B.P - l.A.P; return Vector2d.Dot(p - l.A.P, d) / Math.Max(d.LengthSquared, 1e-30); }
            case SkCircle c: { double a = Math.Atan2(p.Y - c.Center.Y, p.X - c.Center.X); return a < 0 ? a + 2 * Math.PI : a; }
            case SkArc ar:
            {
                double a = Math.Atan2(p.Y - ar.Center.Y, p.X - ar.Center.X), sw = ar.Sweep;
                double rel = sw > 0 ? GeomUtil.Norm2Pi(a - ar.A0) : -GeomUtil.Norm2Pi(ar.A0 - a);
                return rel / sw;
            }
        }
        return double.NaN;
    }

    public static Vector2d PointAt(SkEntity e, double t) => e switch
    {
        SkLine l => l.A.P + (l.B.P - l.A.P) * t,
        SkCircle c => c.Center.P + new Vector2d(Math.Cos(t), Math.Sin(t)) * c.Radius,
        SkArc a => a.Center.P + new Vector2d(Math.Cos(a.A0 + a.Sweep * t), Math.Sin(a.A0 + a.Sweep * t)) * a.Radius,
        _ => Vector2d.Zero,
    };

    static bool OnPiece(SkEntity e, Vector2d p)
    {
        double t = Param(e, p);
        return e switch { SkLine => t >= -1e-9 && t <= 1 + 1e-9, SkArc => t >= -1e-9 && t <= 1 + 1e-9, _ => true };
    }

    /// <summary>Точки пересечения двух кривых (отрезки, дуги, окружности) в пределах самих кривых.</summary>
    public static List<Vector2d> Intersect(SkEntity a, SkEntity b)
    {
        var res = new List<Vector2d>();
        if (a is SkLine la && b is SkLine lb)
        {
            var r = la.B.P - la.A.P; var s = lb.B.P - lb.A.P; double den = Cross(r, s);
            if (Math.Abs(den) < 1e-14) return res;
            double t = Cross(lb.A.P - la.A.P, s) / den, u = Cross(lb.A.P - la.A.P, r) / den;
            if (t >= -1e-9 && t <= 1 + 1e-9 && u >= -1e-9 && u <= 1 + 1e-9) res.Add(la.A.P + r * t);
            return res;
        }
        if (a is SkLine l && CircleOf(b) is { } cb) { foreach (var p in LineCircle(l.A.P, l.B.P, cb.C, cb.R)) if (OnPiece(l, p) && OnPiece(b, p)) res.Add(p); return res; }
        if (b is SkLine l2 && CircleOf(a) is { } ca) return Intersect(b, a);
        if (CircleOf(a) is { } c1 && CircleOf(b) is { } c2) { foreach (var p in CircleCircle(c1.C, c1.R, c2.C, c2.R)) if (OnPiece(a, p) && OnPiece(b, p)) res.Add(p); return res; }
        return res;
    }

    static IEnumerable<Vector2d> LineCircle(Vector2d a, Vector2d b, Vector2d c, double r)
    {
        var d = b - a; var f = a - c;
        double A = Vector2d.Dot(d, d), B = 2 * Vector2d.Dot(f, d), C = Vector2d.Dot(f, f) - r * r, disc = B * B - 4 * A * C;
        if (A < 1e-30 || disc < -1e-12) yield break;
        disc = Math.Sqrt(Math.Max(0, disc));
        double t1 = (-B - disc) / (2 * A), t2 = (-B + disc) / (2 * A);
        yield return a + d * t1;
        if (disc > 1e-12) yield return a + d * t2;
    }

    static IEnumerable<Vector2d> CircleCircle(Vector2d c1, double r1, Vector2d c2, double r2)
    {
        var d = c2 - c1; double L = d.Length;
        if (L < 1e-12 || L > r1 + r2 + 1e-12 || L < Math.Abs(r1 - r2) - 1e-12) yield break;
        double x = (L * L + r1 * r1 - r2 * r2) / (2 * L), h = Math.Sqrt(Math.Max(0, r1 * r1 - x * x));
        var u = d / L; var n = new Vector2d(-u.Y, u.X); var m = c1 + u * x;
        yield return m + n * h;
        if (h > 1e-12) yield return m - n * h;
    }
}

// ------------------------------------------------------------ прямоугольник от центра и по трём точкам

public sealed class RectCenterTool : SketchToolBase
{
    public override string Title => "Прямоугольник от центра";
    public override string Hint => Pts.Count == 0 ? "Щёлкните центр" : "Щёлкните угол";
    public override ISketchTool Restart() => Restarted(new RectCenterTool());

    public override bool Click(SketchInput i)
    {
        if (Pts.Count == 0) { Pts.Add(i.P); Snaps.Add(i.Snap); return false; }
        var c = Pts[0]; var h = i.P - c;
        if (Math.Abs(h.X) < Tol.Weld || Math.Abs(h.Y) < Tol.Weld) return false;
        var lines = Rect3PTool.Quad(i.Sketch, new[] { c - h, c + new Vector2d(h.X, -h.Y), c + h, c + new Vector2d(-h.X, h.Y) }, Role, axisAligned: true);
        // центр — середина диагонали: вспомогательная диагональ и точка центра на её середине
        var diag = i.Sketch.AddEntity(new SkLine(new SkPoint(c - h), new SkPoint(c + h)) { Role = SkRole.Construction });
        i.Sketch.AddConstraint(ConstraintType.Coincident, diag.A, lines[0].A);
        i.Sketch.AddConstraint(ConstraintType.Coincident, diag.B, lines[2].A);
        var cp = i.Sketch.AddEntity(new SkPoint(c) { Role = SkRole.Construction });
        i.Sketch.AddConstraint(ConstraintType.MidPoint, cp, diag);
        Link(i.Sketch, cp, Snaps[0]);
        Finished = true; return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); if (Pts.Count == 0) return;
        var c = Pts[0]; var h = Cursor - c;
        lines.Add(new[] { c - h, c + new Vector2d(h.X, -h.Y), c + h, c + new Vector2d(-h.X, h.Y), c - h });
    }
}

public sealed class Rect3PTool : SketchToolBase
{
    public override string Title => "Прямоугольник по трём точкам";
    public override string Hint => Pts.Count switch { 0 => "Щёлкните первый угол", 1 => "Щёлкните конец первой стороны", _ => "Щёлкните ширину" };
    public override ISketchTool Restart() => Restarted(new Rect3PTool());

    /// <summary>Четырёхугольник по углам: стороны, общие углы; прямоугольник — две пары параллельных и прямой угол.</summary>
    public static SkLine[] Quad(Sketch sk, Vector2d[] c, SkRole role, bool axisAligned)
    {
        var p = c.Select(x => new SkPoint(x)).ToArray();
        var l = new SkLine[4];
        for (int k = 0; k < 4; k++) l[k] = sk.AddEntity(new SkLine(k == 0 ? p[0] : new SkPoint(c[k]), new SkPoint(c[(k + 1) % 4])) { Role = role });
        for (int k = 0; k < 4; k++) sk.AddConstraint(ConstraintType.Coincident, l[k].B, l[(k + 1) % 4].A);
        if (axisAligned)
        {
            sk.AddConstraint(ConstraintType.Horizontal, l[0]); sk.AddConstraint(ConstraintType.Horizontal, l[2]);
            sk.AddConstraint(ConstraintType.Vertical, l[1]); sk.AddConstraint(ConstraintType.Vertical, l[3]);
        }
        else
        {
            sk.AddConstraint(ConstraintType.Parallel, l[0], l[2]); sk.AddConstraint(ConstraintType.Parallel, l[1], l[3]);
            sk.AddConstraint(ConstraintType.Perpendicular, l[0], l[1]);
        }
        return l;
    }

    Vector2d[]? Corners(Vector2d third)
    {
        var a = Pts[0]; var b = Pts[1]; var d = b - a; if (d.Length < Tol.Weld) return null;
        var n = new Vector2d(-d.Y, d.X).Normalized(); double w = Vector2d.Dot(third - b, n);
        if (Math.Abs(w) < Tol.Weld) return null;
        return new[] { a, b, b + n * w, a + n * w };
    }

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P); Snaps.Add(i.Snap);
        if (Pts.Count < 3) return false;
        Finished = true;
        if (Corners(Pts[2]) is not { } c) return false;
        var l = Quad(i.Sketch, c, Role, axisAligned: false);
        Link(i.Sketch, l[0].A, Snaps[0]); Link(i.Sketch, l[0].B, Snaps[1]);
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); if (Pts.Count == 0) return;
        if (Pts.Count == 1) { lines.Add(new[] { Pts[0], Cursor }); return; }
        if (Corners(Cursor) is { } c) lines.Add(new[] { c[0], c[1], c[2], c[3], c[0] });
    }
}

// ------------------------------------------------------------ окружность по двум точкам (диаметр)

public sealed class Circle2PTool : SketchToolBase
{
    public override string Title => "Окружность по двум точкам";
    public override string Hint => Pts.Count == 0 ? "Щёлкните первую точку диаметра" : "Щёлкните вторую точку диаметра";
    public override ISketchTool Restart() => Restarted(new Circle2PTool());

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P); Snaps.Add(i.Snap);
        if (Pts.Count < 2) return false;
        Finished = true;
        double r = (Pts[1] - Pts[0]).Length / 2; if (r <= Tol.Weld) return false;
        var c = i.Sketch.AddEntity(new SkCircle(new SkPoint((Pts[0] + Pts[1]) / 2), r) { Role = Role }, autoConstrain: true);
        foreach (var (p, s) in new[] { (Pts[0], Snaps[0]), (Pts[1], Snaps[1]) })
            if (s != null) i.Sketch.AddConstraint(ConstraintType.PointOnEntity, s, c);
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); if (Pts.Count == 0) return;
        lines.Add(Circle((Pts[0] + Cursor) / 2, (Cursor - Pts[0]).Length / 2));
    }
}

// ------------------------------------------------------------ дуга касательная к концу

public sealed class ArcTangentTool : SketchToolBase
{
    private SkPoint? _start; private Vector2d _tan;
    public override string Title => "Дуга касательная";
    public override string Hint => _start == null ? "Щёлкните конец отрезка или дуги" : "Щёлкните конец дуги";
    public override ISketchTool Restart() => Restarted(new ArcTangentTool());

    /// <summary>Касательное направление в конце кривой (наружу от неё).</summary>
    public static Vector2d? TangentAtEnd(SkPoint p)
    {
        switch (p.Owner)
        {
            case SkLine l when ReferenceEquals(l.B, p): return (l.B.P - l.A.P).Normalized();
            case SkLine l2 when ReferenceEquals(l2.A, p): return (l2.A.P - l2.B.P).Normalized();
            case SkArc a when ReferenceEquals(a.End, p): { var r = a.End.P - a.Center.P; var t = new Vector2d(-r.Y, r.X).Normalized(); return a.CCW ? t : -t; }
            case SkArc a2 when ReferenceEquals(a2.Start, p): { var r = a2.Start.P - a2.Center.P; var t = new Vector2d(-r.Y, r.X).Normalized(); return a2.CCW ? -t : t; }
        }
        return null;
    }

    public static SkArc? Make(Vector2d s, Vector2d t, Vector2d e)
    {
        var d = e - s; var n = new Vector2d(-t.Y, t.X); double dn = Vector2d.Dot(n, d);
        if (Math.Abs(dn) < 1e-12 || d.Length < Tol.Weld) return null;
        double r = d.LengthSquared / (2 * dn);
        var c = s + n * r;
        bool ccw = r > 0;
        return new SkArc(new SkPoint(c), new SkPoint(s), new SkPoint(e), ccw);
    }

    public override bool Click(SketchInput i)
    {
        if (_start == null)
        {
            if (i.Snap is not { } sp || TangentAtEnd(sp) is not { } t) return false;
            _start = sp; _tan = t; Pts.Add(sp.P); return false;
        }
        Finished = true;
        if (Make(_start.P, _tan, i.P) is not { } arc) return false;
        arc.Role = Role;
        i.Sketch.AddEntity(arc);
        i.Sketch.AddConstraint(ConstraintType.Coincident, arc.Start, _start);
        if (_start.Owner is { } own) i.Sketch.AddConstraint(ConstraintType.Tangent, own, arc);
        Link(i.Sketch, arc.End, i.Snap);
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        if (_start != null && Make(_start.P, _tan, Cursor) is { } a) lines.Add(Tess(a));
    }
}

// ------------------------------------------------------------ эллипс по пяти точкам

public sealed class Ellipse5PTool : SketchToolBase
{
    public override string Title => "Эллипс по пяти точкам";
    public override string Hint => Loc.L("Щёлкните точку {0} из 5", Pts.Count + 1);
    public override ISketchTool Restart() => Restarted(new Ellipse5PTool());

    /// <summary>Коника через пять точек (Ax² + Bxy + Cy² + Dx + Ey + F = 0) — эллипс: центр, полуоси, поворот; иначе null.</summary>
    public static SkEllipse? Fit(IReadOnlyList<Vector2d> p)
    {
        if (p.Count < 5) return null;
        var o = p.Aggregate(Vector2d.Zero, (a, b) => a + b) / p.Count;
        var m = new double[5, 6];
        for (int i = 0; i < 5; i++) { double x = p[i].X - o.X, y = p[i].Y - o.Y; m[i, 0] = x * x; m[i, 1] = x * y; m[i, 2] = y * y; m[i, 3] = x; m[i, 4] = y; m[i, 5] = 1; }
        // F = −1 (эллипс не через начало — центр масс точек внутри): 5×5 система
        var a = new double[5, 5]; var rhs = new double[5];
        for (int i = 0; i < 5; i++) { for (int j = 0; j < 5; j++) a[i, j] = m[i, j]; rhs[i] = 1; }
        if (!Solve(a, rhs)) return null;
        double A = rhs[0], B = rhs[1], C = rhs[2], D = rhs[3], E = rhs[4], F = -1;
        double det = 4 * A * C - B * B;
        if (det <= 1e-18) return null;   // не эллипс
        double cx = (B * E - 2 * C * D) / det, cy = (B * D - 2 * A * E) / det;
        double F0 = A * cx * cx + B * cx * cy + C * cy * cy + D * cx + E * cy + F;
        double th = 0.5 * Math.Atan2(B, A - C);
        double ct = Math.Cos(th), st = Math.Sin(th);
        double a1 = A * ct * ct + B * ct * st + C * st * st, c1 = A * st * st - B * ct * st + C * ct * ct;
        if (-F0 / a1 <= 0 || -F0 / c1 <= 0) return null;
        double ru = Math.Sqrt(-F0 / a1), rv = Math.Sqrt(-F0 / c1);
        if (ru < rv) { (ru, rv) = (rv, ru); th += Math.PI / 2; }
        return new SkEllipse(new SkPoint(new Vector2d(cx + o.X, cy + o.Y)), ru, rv, th);
    }

    static bool Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-14) return false;
            if (piv != c) { for (int k = 0; k < n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]); (b[c], b[piv]) = (b[piv], b[c]); }
            for (int r = 0; r < n; r++) { if (r == c) continue; double f = a[r, c] / a[c, c]; for (int k = c; k < n; k++) a[r, k] -= f * a[c, k]; b[r] -= f * b[c]; }
        }
        for (int c = 0; c < n; c++) b[c] /= a[c, c];
        return true;
    }

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P); if (Pts.Count < 5) return false;
        Finished = true;
        if (Fit(Pts) is not { } e) return false;
        e.Role = Role; i.Sketch.AddEntity(e, autoConstrain: true);
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); points.AddRange(Pts);
        if (Pts.Count == 4 && Fit(Pts.Append(Cursor).ToList()) is { } e) lines.Add(Tess(e));
    }
}

// ------------------------------------------------------------ сплайн через точки

public sealed class SplineThroughTool : SketchToolBase
{
    public override string Title => "Сплайн через точки";
    public override string Hint => "Щёлкайте точки, через которые пройдёт сплайн; Enter или правая кнопка — готово";
    public override ISketchTool Restart() => Restarted(new SplineThroughTool());
    public override bool Click(SketchInput i) { Pts.Add(i.P); return false; }

    /// <summary>Управляющие точки кривой Безье степени n−1 через точки (параметры — по длине хорд).</summary>
    public static List<Vector2d>? Interpolate(IReadOnlyList<Vector2d> q)
    {
        int n = q.Count; if (n < 2) return null; if (n == 2) return q.ToList();
        var t = new double[n]; for (int i = 1; i < n; i++) t[i] = t[i - 1] + (q[i] - q[i - 1]).Length;
        if (t[n - 1] < 1e-12) return null;
        for (int i = 0; i < n; i++) t[i] /= t[n - 1];
        var M = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) M[i, j] = Binom(n - 1, j) * Math.Pow(t[i], j) * Math.Pow(1 - t[i], n - 1 - j);
        var bx = q.Select(p => p.X).ToArray(); var by = q.Select(p => p.Y).ToArray();
        var M2 = (double[,])M.Clone();
        if (!Gauss(M, bx) || !Gauss(M2, by)) return null;
        return Enumerable.Range(0, n).Select(i => new Vector2d(bx[i], by[i])).ToList();
    }
    static double Binom(int n, int k) { double r = 1; for (int i = 1; i <= k; i++) r = r * (n - k + i) / i; return r; }
    static bool Gauss(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-15) return false;
            if (piv != c) { for (int k = 0; k < n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]); (b[c], b[piv]) = (b[piv], b[c]); }
            for (int r = c + 1; r < n; r++) { double f = a[r, c] / a[c, c]; for (int k = c; k < n; k++) a[r, k] -= f * a[c, k]; b[r] -= f * b[c]; }
        }
        for (int c = n - 1; c >= 0; c--) { double s = b[c]; for (int k = c + 1; k < n; k++) s -= a[c, k] * b[k]; b[c] = s / a[c, c]; }
        return true;
    }

    public override bool Finish(Sketch sk)
    {
        bool made = false;
        if (Pts.Count >= 2 && Pts.Count <= 12 && Interpolate(Pts) is { } cv)
        {
            sk.AddEntity(new SkSpline(cv.Select(p => new SkPoint(p))) { Role = Role }, autoConstrain: true);
            made = true;
        }
        Pts.Clear(); Finished = true; return made;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); points.AddRange(Pts);
        var all = Pts.Append(Cursor).ToList();
        if (all.Count >= 2 && all.Count <= 12 && Interpolate(all) is { } cv) lines.Add(Tess(new SkSpline(cv.Select(p => new SkPoint(p)))));
    }
}

// ------------------------------------------------------------ многоугольник

public sealed class PolygonTool : SketchToolBase
{
    public static int Sides = 6;
    private readonly int _n;
    public PolygonTool(int sides = 0) { _n = sides > 2 ? sides : Sides; }
    public override string Title => "Многоугольник";
    public override string Hint => Pts.Count == 0 ? Loc.L("Щёлкните центр ({0} сторон)", _n) : "Щёлкните вершину";
    public override ISketchTool Restart() => Restarted(new PolygonTool(_n));

    Vector2d[] Corners(Vector2d v) { var c = Pts[0]; var r = v - c; double a0 = Math.Atan2(r.Y, r.X), R = r.Length; return Enumerable.Range(0, _n).Select(k => c + new Vector2d(Math.Cos(a0 + 2 * Math.PI * k / _n), Math.Sin(a0 + 2 * Math.PI * k / _n)) * R).ToArray(); }

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P); Snaps.Add(i.Snap); if (Pts.Count < 2) return false;
        Finished = true;
        if ((Pts[1] - Pts[0]).Length < Tol.Weld) return false;
        var sk = i.Sketch; var c = Corners(Pts[1]);
        // правильный многоугольник: вершины на вспомогательной окружности, стороны равны
        var circ = sk.AddEntity(new SkCircle(new SkPoint(Pts[0]), (Pts[1] - Pts[0]).Length) { Role = SkRole.Construction });
        Link(sk, circ.Center, Snaps[0]);
        var ls = new List<SkLine>();
        for (int k = 0; k < _n; k++) ls.Add(sk.AddEntity(new SkLine(new SkPoint(c[k]), new SkPoint(c[(k + 1) % _n])) { Role = Role }));
        for (int k = 0; k < _n; k++)
        {
            sk.AddConstraint(ConstraintType.Coincident, ls[k].B, ls[(k + 1) % _n].A);
            sk.AddConstraint(ConstraintType.PointOnEntity, ls[k].A, circ);
            if (k > 0) sk.AddConstraint(ConstraintType.Equal, ls[0], ls[k]);
        }
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); if (Pts.Count == 0) return;
        var c = Corners(Cursor); lines.Add(c.Append(c[0]).ToArray());
    }
}

// ------------------------------------------------------------ паз

public sealed class SlotTool : SketchToolBase
{
    public override string Title => "Паз";
    public override string Hint => Pts.Count switch { 0 => "Щёлкните центр первой дуги", 1 => "Щёлкните центр второй дуги", _ => "Щёлкните ширину" };
    public override ISketchTool Restart() => Restarted(new SlotTool());

    (Vector2d A, Vector2d B, double R)? Shape(Vector2d w)
    {
        var a = Pts[0]; var b = Pts[1]; var d = b - a; if (d.Length < Tol.Weld) return null;
        var n = new Vector2d(-d.Y, d.X).Normalized(); double r = Math.Abs(Vector2d.Dot(w - a, n));
        return r < Tol.Weld ? null : (a, b, r);
    }

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P); Snaps.Add(i.Snap); if (Pts.Count < 3) return false;
        Finished = true;
        if (Shape(Pts[2]) is not { } s) return false;
        var sk = i.Sketch; var d = (s.B - s.A).Normalized(); var n = new Vector2d(-d.Y, d.X) * s.R;
        var l1 = sk.AddEntity(new SkLine(new SkPoint(s.A + n), new SkPoint(s.B + n)) { Role = Role });
        var l2 = sk.AddEntity(new SkLine(new SkPoint(s.B - n), new SkPoint(s.A - n)) { Role = Role });
        var a1 = sk.AddEntity(new SkArc(new SkPoint(s.B), new SkPoint(s.B + n), new SkPoint(s.B - n), ccw: false) { Role = Role });
        var a2 = sk.AddEntity(new SkArc(new SkPoint(s.A), new SkPoint(s.A - n), new SkPoint(s.A + n), ccw: false) { Role = Role });
        sk.AddConstraint(ConstraintType.Coincident, l1.B, a1.Start); sk.AddConstraint(ConstraintType.Coincident, a1.End, l2.A);
        sk.AddConstraint(ConstraintType.Coincident, l2.B, a2.Start); sk.AddConstraint(ConstraintType.Coincident, a2.End, l1.A);
        sk.AddConstraint(ConstraintType.Tangent, l1, a1); sk.AddConstraint(ConstraintType.Tangent, l2, a1);
        sk.AddConstraint(ConstraintType.Tangent, l1, a2); sk.AddConstraint(ConstraintType.Tangent, l2, a2);
        sk.AddConstraint(ConstraintType.Equal, a1, a2);
        Link(sk, a2.Center, Snaps[0]); Link(sk, a1.Center, Snaps[1]);
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); if (Pts.Count == 0) return;
        if (Pts.Count == 1) { lines.Add(new[] { Pts[0], Cursor }); return; }
        if (Shape(Cursor) is not { } s) return;
        var d = (s.B - s.A).Normalized(); var n = new Vector2d(-d.Y, d.X) * s.R;
        lines.Add(new[] { s.A + n, s.B + n }); lines.Add(new[] { s.B - n, s.A - n });
        var c1 = Circle(s.A, s.R); var c2 = Circle(s.B, s.R); lines.Add(c1); lines.Add(c2);
    }
}

// ------------------------------------------------------------ текст (штриховой шрифт — отрезки)

public sealed class TextTool : SketchToolBase
{
    public static string Text = "ABC";
    public static double Height = 10;
    public override string Title => "Текст";
    public override string Hint => Loc.L("Щёлкните начало текста «{0}»", Text);
    public override bool Continuous => false;
    public override ISketchTool Restart() => Restarted(new TextTool());

    public static List<(Vector2d A, Vector2d B)> Strokes(string text, Vector2d at, double h)
    {
        var res = new List<(Vector2d, Vector2d)>();
        var polys = new List<List<Vector2d>>();
        Parametric3D.Render.StrokeFont.Layout(text, at, Vector2d.UnitX, Vector2d.UnitY, h, polys);
        foreach (var poly in polys)
            for (int k = 1; k < poly.Count; k++) res.Add((poly[k - 1], poly[k]));
        return res;
    }

    public override bool Click(SketchInput i)
    {
        Finished = true;
        var st = Strokes(Text, i.P, Height); if (st.Count == 0) return false;
        // общие концы штрихов — совпадение (контур буквы не рвётся)
        var ends = new List<SkPoint>();
        foreach (var (a, b) in st)
        {
            if ((b - a).Length < 1e-9) continue;
            var l = i.Sketch.AddEntity(new SkLine(new SkPoint(a), new SkPoint(b)) { Role = Role });
            foreach (var p in new[] { l.A, l.B })
            {
                var same = ends.FirstOrDefault(e => (e.P - p.P).Length < 1e-9);
                if (same != null) i.Sketch.AddConstraint(ConstraintType.Coincident, p, same); else ends.Add(p);
            }
        }
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        foreach (var (a, b) in Strokes(Text, Cursor, Height)) lines.Add(new[] { a, b });
    }
}

// ------------------------------------------------------------ система координат эскиза

public sealed class SketchCsysTool : SketchToolBase
{
    public override string Title => "Система координат эскиза";
    public override string Hint => Pts.Count == 0 ? "Щёлкните новое начало координат" : "Щёлкните точку на оси X";
    public override bool Continuous => false;
    public override ISketchTool Restart() => Restarted(new SketchCsysTool());

    public override bool Click(SketchInput i)
    {
        Pts.Add(i.P); if (Pts.Count < 2) return false;
        Finished = true;
        var sk = i.Sketch; var o = Pts[0]; var x = Pts[1] - o; if (x.Length < Tol.Weld) return false;
        // новое начало и направление «вверх» — перпендикуляр к новой оси X; геометрия эскиза переносится вместе
        var wo = sk.Plane.ToWorld(o);
        var up = new Vector2d(-x.Y, x.X).Normalized();
        sk.SetOrigin(wo, Loc.L("Точка эскиза"));
        sk.SetUpVector(up);
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor); if (Pts.Count == 0) return;
        lines.Add(new[] { Pts[0], Cursor });
    }
}

// ------------------------------------------------------------ обрезка и разделение

public sealed class TrimTool : SketchToolBase, IStrokeTool
{
    private readonly bool _split;
    public TrimTool(bool split = false) { _split = split; }
    public override string Title => _split ? "Разделить" : "Обрезка";
    public override string Hint => _split ? "Щёлкните точку на кривой, где разделить" : "Щёлкните кусок кривой, который убрать (до пересечений и точек на ней), или проведите мышью по кривым";
    public override ISketchTool Restart() => Restarted(new TrimTool(_split));

    public override bool Click(SketchInput i)
    {
        var sk = i.Sketch;
        var e = sk.Entities.Where(x => x is SkLine or SkArc or SkCircle).OrderBy(x => x.DistanceTo(i.P)).FirstOrDefault();
        if (e == null || e.DistanceTo(i.P) > Math.Max(sk.AutoTolerance * 4, 1e-3 * Extent(sk))) return false;
        return _split ? Split(sk, e, i.P) : Trim(sk, e, i.P);
    }

    static double Extent(Sketch sk) { var pts = sk.AllPoints().Select(p => p.P).ToList(); if (pts.Count == 0) return 100; return Math.Max(pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y)); }

    /// <summary>
    /// Параметры точек, делящих кривую e (по порядку): пересечения с прочими кривыми и (k105, как в Creo) точки эскиза,
    /// лежащие на ней — концы других кривых, отдельные точки (свои концы e не в счёт). Мутация P3D_TRIMPOINTS=0 — только
    /// пересечения.
    /// </summary>
    static List<double> Cuts(Sketch sk, SkEntity e)
    {
        var ts = new List<double>();
        foreach (var o in sk.Entities)
        {
            if (ReferenceEquals(o, e) || o is SkPoint || o.Role == SkRole.Construction && e.Role != SkRole.Construction) continue;
            foreach (var p in SkGeom.Intersect(e, o)) ts.Add(SkGeom.Param(e, p));
        }
        if (!TrimPointsOff)
        {
            double tol = Math.Max(1e-7, 1e-7 * Extent(sk));
            var own = e.Handles.ToHashSet();
            foreach (var pt in sk.AllPoints())
            {
                if (own.Contains(pt) || pt.Role == SkRole.Construction && e.Role != SkRole.Construction) continue;
                if (e is SkCircle or SkArc && SkGeom.CircleOf(e) is { } cc && (pt.P - cc.C).Length < tol) continue;   // центр
                if (e.DistanceTo(pt.P) <= tol) ts.Add(SkGeom.Param(e, pt.P));
            }
        }
        ts.Sort();
        return ts;
    }
    static readonly bool TrimPointsOff = Environment.GetEnvironmentVariable("P3D_TRIMPOINTS") == "0";
    static readonly bool TrimStrokeOff = Environment.GetEnvironmentVariable("P3D_TRIMSTROKE") == "0";
    static readonly bool CircleDimKeepOff = Environment.GetEnvironmentVariable("P3D_TRIMCIRCLEDIM") == "0";

    // ---------------------------------------------------------- k105 (замечание 1): обрезка протяжкой

    private readonly List<Vector2d> _stroke = new();
    public bool StrokeEnabled => !_split && !TrimStrokeOff;
    public void StrokeMove(Vector2d p) { if (_stroke.Count == 0 || (_stroke[^1] - p).LengthSquared > 1e-18) _stroke.Add(p); }
    public void StrokeCancel() => _stroke.Clear();

    /// <summary>Протяжка кончилась: убрать куски кривых, которые пересекла линия протяжки (каждый — до пересечений и точек).</summary>
    public bool StrokeEnd(Sketch sk)
    {
        var path = _stroke.ToList(); _stroke.Clear();
        return StrokeEnabled && TrimAlong(sk, path);
    }

    /// <summary>Куски кривых, которые пересекает ломаная path, — убрать (по порядку пересечений вдоль ломаной).</summary>
    public static bool TrimAlong(Sketch sk, IReadOnlyList<Vector2d> path)
    {
        if (path.Count < 2) return false;
        var hits = new List<(double S, Vector2d P)>();
        double run = 0;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var seg = new SkLine(new SkPoint(path[i]), new SkPoint(path[i + 1]));
            double len = (path[i + 1] - path[i]).Length;
            foreach (var e in sk.Entities.Where(x => x is SkLine or SkArc or SkCircle && x.Role != SkRole.Reference))
                foreach (var p in SkGeom.Intersect(seg, e)) hits.Add((run + (p - path[i]).Length, p));
            run += len;
        }
        double tol = Math.Max(sk.AutoTolerance, 1e-6 * Extent(sk));
        bool changed = false;
        foreach (var (_, p) in hits.OrderBy(h => h.S))
        {
            // после прежних обрезок кривые другие: кусок, на котором лежит точка пересечения (если он ещё есть)
            var e = sk.Entities.Where(x => x is SkLine or SkArc or SkCircle && x.Role != SkRole.Reference).OrderBy(x => x.DistanceTo(p)).FirstOrDefault();
            if (e == null || e.DistanceTo(p) > tol) continue;
            changed |= Trim(sk, e, p);
        }
        return changed;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        base.Preview(lines, points);
        for (int i = 0; i + 1 < _stroke.Count; i++) lines.Add(new[] { _stroke[i], _stroke[i + 1] });
    }

    /// <summary>Точка выпала из эскиза (конец убранного куска) — связи и размеры с ней снимаются.</summary>
    static void Drop(Sketch sk, SkPoint p)
    {
        sk.Constraints.RemoveAll(c => c.Items.Contains(p));
        sk.Dimensions.RemoveAll(d => d.Items.Contains(p));
    }

    /// <summary>
    /// Обрезка: кусок кривой между соседними пересечениями вокруг точки щелчка убирается. Остающиеся концы — прежние точки
    /// (их связи с соседями сохраняются); у отрезка, разбитого на два, второй кусок — на той же прямой (параллельно и точкой
    /// на первом), у дуги — концентрично.
    /// </summary>
    public static bool Trim(Sketch sk, SkEntity e, Vector2d at)
    {
        double t = SkGeom.Param(e, at);
        var cuts = Cuts(sk, e);
        switch (e)
        {
            case SkLine l:
            {
                var inner = cuts.Where(c => c > 1e-9 && c < 1 - 1e-9).Distinct().ToList();
                double lo = inner.Where(c => c < t).DefaultIfEmpty(0).Max(), hi = inner.Where(c => c > t).DefaultIfEmpty(1).Min();
                if (lo <= 1e-9 && hi >= 1 - 1e-9) { sk.RemoveEntity(e); return true; }
                var pa = SkGeom.PointAt(l, lo); var pb = SkGeom.PointAt(l, hi);
                if (lo <= 1e-9) { var oldA = l.A; Drop(sk, oldA); l.A = new SkPoint(pb) { Owner = l }; }
                else if (hi >= 1 - 1e-9) { var oldB = l.B; Drop(sk, oldB); l.B = new SkPoint(pa) { Owner = l }; }
                else
                {
                    var b = l.B; l.B = new SkPoint(pa) { Owner = l };
                    var l2 = sk.AddEntity(new SkLine(new SkPoint(pb), b) { Role = l.Role });   // b — с прежними связями
                    sk.AddConstraint(ConstraintType.Parallel, l, l2); sk.AddConstraint(ConstraintType.PointOnEntity, l2.A, l);
                }
                sk.BumpStructure(); sk.NotifyChanged(sk);
                return true;
            }
            case SkArc ar:
            {
                var inner = cuts.Where(c => c > 1e-9 && c < 1 - 1e-9).Distinct().ToList();
                double lo = inner.Where(c => c < t).DefaultIfEmpty(0).Max(), hi = inner.Where(c => c > t).DefaultIfEmpty(1).Min();
                if (lo <= 1e-9 && hi >= 1 - 1e-9) { sk.RemoveEntity(e); return true; }
                var plo = SkGeom.PointAt(ar, lo); var phi = SkGeom.PointAt(ar, hi);
                if (lo <= 1e-9) { var os = ar.Start; Drop(sk, os); ar.Start = new SkPoint(phi) { Owner = ar }; }
                else if (hi >= 1 - 1e-9) { var oe = ar.End; Drop(sk, oe); ar.End = new SkPoint(plo) { Owner = ar }; }
                else
                {
                    var en = ar.End; ar.End = new SkPoint(plo) { Owner = ar };
                    var a2 = sk.AddEntity(new SkArc(new SkPoint(ar.Center.P), new SkPoint(phi), en, ar.CCW) { Role = ar.Role });
                    sk.AddConstraint(ConstraintType.Concentric, ar, a2); sk.AddConstraint(ConstraintType.Equal, ar, a2);
                }
                sk.BumpStructure(); sk.NotifyChanged(sk);
                return true;
            }
            case SkCircle c:
            {
                var ang = cuts.Distinct().ToList();
                if (ang.Count < 2) { sk.RemoveEntity(e); return true; }
                // убрать дугу между соседними пересечениями вокруг t; остаётся дуга от hi до lo (против часовой)
                double lo = ang.Where(x => x <= t).DefaultIfEmpty(ang[^1] - 2 * Math.PI).Max(), hi = ang.Where(x => x > t).DefaultIfEmpty(ang[0] + 2 * Math.PI).Min();
                var cc = c.Center.P; double r = c.Radius;
                if (CircleDimKeepOff)
                {
                    sk.RemoveEntity(e);
                    sk.AddEntity(new SkArc(new SkPoint(cc), new SkPoint(cc + new Vector2d(Math.Cos(hi), Math.Sin(hi)) * r), new SkPoint(cc + new Vector2d(Math.Cos(lo), Math.Sin(lo)) * r), ccw: true) { Role = c.Role });
                    return true;
                }
                // k105 (замечание 3): дуга — на том же центре (его связи остаются), размеры и связи окружности (радиус,
                // диаметр — тот же размер, тот же идентификатор dN и формула) переходят на дугу
                var arc = sk.AddEntity(new SkArc(c.Center, new SkPoint(cc + new Vector2d(Math.Cos(hi), Math.Sin(hi)) * r), new SkPoint(cc + new Vector2d(Math.Cos(lo), Math.Sin(lo)) * r), ccw: true) { Role = c.Role });
                foreach (var dm in sk.Dimensions) for (int k = 0; k < dm.Items.Count; k++) if (ReferenceEquals(dm.Items[k], c)) dm.Items[k] = arc;
                foreach (var cn in sk.Constraints) for (int k = 0; k < cn.Items.Count; k++) if (ReferenceEquals(cn.Items[k], c)) cn.Items[k] = arc;
                sk.RemoveEntity(e);
                return true;
            }
        }
        return false;
    }

    /// <summary>Разделить кривую в точке: два куска с общей точкой (прежние концы и их связи сохраняются).</summary>
    public static bool Split(Sketch sk, SkEntity e, Vector2d at)
    {
        switch (e)
        {
            case SkLine l:
            {
                double t = SkGeom.Param(l, at); if (t <= 1e-6 || t >= 1 - 1e-6) return false;
                var m = SkGeom.PointAt(l, t); var b = l.B;
                l.B = new SkPoint(m) { Owner = l };
                var l2 = sk.AddEntity(new SkLine(new SkPoint(m), b) { Role = l.Role });
                sk.AddConstraint(ConstraintType.Coincident, l.B, l2.A);
                sk.AddConstraint(ConstraintType.Parallel, l, l2);
                sk.BumpStructure(); sk.NotifyChanged(sk);
                return true;
            }
            case SkArc ar:
            {
                double t = SkGeom.Param(ar, at); if (t <= 1e-6 || t >= 1 - 1e-6) return false;
                var m = SkGeom.PointAt(ar, t); var en = ar.End;
                ar.End = new SkPoint(m) { Owner = ar };
                var a2 = sk.AddEntity(new SkArc(new SkPoint(ar.Center.P), new SkPoint(m), en, ar.CCW) { Role = ar.Role });
                sk.AddConstraint(ConstraintType.Coincident, ar.End, a2.Start); sk.AddConstraint(ConstraintType.Concentric, ar, a2); sk.AddConstraint(ConstraintType.Equal, ar, a2);
                sk.BumpStructure(); sk.NotifyChanged(sk);
                return true;
            }
            case SkCircle c:
            {
                // окружность в точке — две полуокружности с общими концами
                double a0 = SkGeom.Param(c, at); var cc = c.Center.P; double r = c.Radius;
                var p0 = cc + new Vector2d(Math.Cos(a0), Math.Sin(a0)) * r; var p1 = cc - (p0 - cc);
                sk.RemoveEntity(e);
                var x1 = sk.AddEntity(new SkArc(new SkPoint(cc), new SkPoint(p0), new SkPoint(p1), true) { Role = c.Role });
                var x2 = sk.AddEntity(new SkArc(new SkPoint(cc), new SkPoint(p1), new SkPoint(p0), true) { Role = c.Role });
                sk.AddConstraint(ConstraintType.Coincident, x1.End, x2.Start); sk.AddConstraint(ConstraintType.Coincident, x2.End, x1.Start);
                sk.AddConstraint(ConstraintType.Concentric, x1, x2);
                return true;
            }
        }
        return false;
    }
}

// ------------------------------------------------------------ скругление и фаска угла

public sealed class CornerTool : SketchToolBase
{
    public static double Radius = 5, Distance = 5;
    private readonly bool _chamfer;
    public CornerTool(bool chamfer = false) { _chamfer = chamfer; }
    public override string Title => _chamfer ? "Фаска угла" : "Скругление угла";
    public override string Hint => Loc.L(_chamfer ? "Щёлкните угол двух отрезков (фаска {0})" : "Щёлкните угол двух отрезков (радиус {0})", _chamfer ? Distance : Radius);
    public override ISketchTool Restart() => Restarted(new CornerTool(_chamfer));

    public override bool Click(SketchInput i) => Apply(i.Sketch, i.P, _chamfer, _chamfer ? Distance : Radius);

    /// <summary>Угол — общий конец двух отрезков у точки щелчка: отрезки укорачиваются, между ними дуга (касательная) или фаска.</summary>
    public static readonly bool ChamferDimsOff = Environment.GetEnvironmentVariable("P3D_SKCHAMFERDIMS") == "0";
    public static readonly bool CornerSplitOff = Environment.GetEnvironmentVariable("P3D_SKCORNERSPLIT") == "0";

    public static bool Apply(Sketch sk, Vector2d at, bool chamfer, double size)
    {
        var lines = sk.Entities.OfType<SkLine>().ToList();
        SkLine? la = null, lb = null; SkPoint? pa = null, pb = null; double best = double.MaxValue;
        foreach (var a in lines)
            foreach (var b in lines)
            {
                if (ReferenceEquals(a, b)) continue;
                foreach (var ea in new[] { a.A, a.B })
                    foreach (var eb in new[] { b.A, b.B })
                    {
                        if ((ea.P - eb.P).Length > 1e-6) continue;
                        double d = (ea.P - at).Length;
                        if (d < best) { best = d; la = a; lb = b; pa = ea; pb = eb; }
                    }
            }
        if (la == null || lb == null || pa == null || pb == null) return false;
        var v = pa.P; var oa = ReferenceEquals(pa, la.A) ? la.B : la.A; var ob = ReferenceEquals(pb, lb.A) ? lb.B : lb.A;
        var ua = (oa.P - v).Normalized(); var ub = (ob.P - v).Normalized();
        double phi = Math.Acos(Math.Clamp(Vector2d.Dot(ua, ub), -1, 1));
        if (phi < 1e-6 || phi > Math.PI - 1e-6) return false;
        double dist = chamfer ? size : size / Math.Tan(phi / 2);
        if (dist >= (oa.P - v).Length - 1e-9 || dist >= (ob.P - v).Length - 1e-9) return false;
        var qa = v + ua * dist; var qb = v + ub * dist;
        // k97 (замечание 12): у ломаной соседние отрезки делят ОДНУ точку угла — прежде обе точки касания писались в неё
        // же (сначала qa, затем qb), дуга выходила из точки в ту же точку: «скругление нулевого радиуса». Точка угла
        // разделяется: второй отрезок получает свой конец. P3D_SKCORNERSPLIT=0 — прежнее поведение.
        if (ReferenceEquals(pa, pb) && !CornerSplitOff)
        {
            var np = new SkPoint(pb.P) { Owner = lb };
            if (ReferenceEquals(lb.A, pb)) lb.A = np; else lb.B = np;
            pa.Owner = la; pb = np;
            sk.BumpStructure();
        }
        // угла больше нет: связи и размеры, приложенные к точке угла, снимаются (иначе они тянут точку касания обратно в угол)
        if (!CornerSplitOff)
        {
            sk.Constraints.RemoveAll(c => c.Items.Any(x => ReferenceEquals(x, pa) || ReferenceEquals(x, pb)));
            sk.Dimensions.RemoveAll(d => d.Items.Any(x => ReferenceEquals(x, pa) || ReferenceEquals(x, pb)));
        }
        // концы отрезков у угла переезжают в точки касания; совпадение угла снимается
        sk.Constraints.RemoveAll(c => c.Type == ConstraintType.Coincident && c.Items.Contains(pa) && c.Items.Contains(pb));
        pa.P = qa; pb.P = qb;
        if (chamfer)
        {
            var ch = sk.AddEntity(new SkLine(new SkPoint(qa), new SkPoint(qb)) { Role = la.Role });
            sk.AddConstraint(ConstraintType.Coincident, ch.A, pa); sk.AddConstraint(ConstraintType.Coincident, ch.B, pb);
            // k98 (замечание 6): как в Creo — два размера катетов фаски (у прямого угла — горизонтальный и вертикальный):
            // расстояние от конца фаски на одном отрезке до прямой другого. P3D_SKCHAMFERDIMS=0 — без них.
            if (!ChamferDimsOff)
            {
                static double DistToLine(Vector2d p, SkLine l) { var d = l.B.P - l.A.P; double n = d.Length; return n < 1e-12 ? (p - l.A.P).Length : Math.Abs(d.X * (p.Y - l.A.P.Y) - d.Y * (p.X - l.A.P.X)) / n; }
                var d1 = sk.AddDimension(DimensionType.DistancePointLine, DistToLine(qb, la), pb, la);
                var d2 = sk.AddDimension(DimensionType.DistancePointLine, DistToLine(qa, lb), pa, lb);
                foreach (var dd in new[] { d1, d2 }) { dd.Locked = true; dd.IsDefault = false; }
            }
        }
        else
        {
            var bis = (ua + ub).Normalized(); double r = size; var c = v + bis * (r / Math.Sin(phi / 2));
            bool ccw = (qa.X - c.X) * (qb.Y - c.Y) - (qa.Y - c.Y) * (qb.X - c.X) > 0;
            var arc = sk.AddEntity(new SkArc(new SkPoint(c), new SkPoint(qa), new SkPoint(qb), ccw) { Role = la.Role });
            sk.AddConstraint(ConstraintType.Coincident, arc.Start, pa); sk.AddConstraint(ConstraintType.Coincident, arc.End, pb);
            sk.AddConstraint(ConstraintType.Tangent, la, arc); sk.AddConstraint(ConstraintType.Tangent, lb, arc);
        }
        return true;
    }
}

// ------------------------------------------------------------ отрезок касательный к двум дугам

public sealed class LineTangent2Tool : SketchToolBase
{
    private SkEntity? _first; private Vector2d _p1;
    public override string Title => "Отрезок касательный к двум дугам";
    public override string Hint => _first == null ? "Щёлкните первую дугу или окружность (у точки касания)" : "Щёлкните вторую дугу или окружность";
    public override ISketchTool Restart() => Restarted(new LineTangent2Tool());

    static SkEntity? Round(Sketch sk, Vector2d p) => sk.Entities.Where(e => e is SkArc or SkCircle).OrderBy(e => e.DistanceTo(p)).FirstOrDefault();

    /// <summary>Общая касательная двух окружностей, концы которой ближе всего к точкам щелчка.</summary>
    public static (Vector2d A, Vector2d B)? Tangent((Vector2d C, double R) c1, (Vector2d C, double R) c2, Vector2d near1, Vector2d near2)
    {
        var d = c2.C - c1.C; double L = d.Length; if (L < 1e-12) return null;
        (Vector2d, Vector2d)? best = null; double bd = double.MaxValue;
        foreach (int s1 in new[] { 1, -1 })   // внешние (+) и внутренние (−) касательные
        {
            double r2 = s1 * c2.R, cosA = (c1.R - r2) / L;
            if (Math.Abs(cosA) > 1) continue;
            double a = Math.Acos(cosA), base0 = Math.Atan2(d.Y, d.X);
            foreach (int s2 in new[] { 1, -1 })
            {
                double th = base0 + s2 * a; var n = new Vector2d(Math.Cos(th), Math.Sin(th));
                var p1 = c1.C + n * c1.R; var p2 = c2.C + n * r2;
                double score = (p1 - near1).Length + (p2 - near2).Length;
                if (score < bd) { bd = score; best = (p1, p2); }
            }
        }
        return best;
    }

    public override bool Click(SketchInput i)
    {
        var e = Round(i.Sketch, i.P); if (e == null) return false;
        if (_first == null) { _first = e; _p1 = i.P; return false; }
        Finished = true;
        if (ReferenceEquals(e, _first) || SkGeom.CircleOf(_first) is not { } c1 || SkGeom.CircleOf(e) is not { } c2) return false;
        if (Tangent(c1, c2, _p1, i.P) is not { } t) return false;
        var l = i.Sketch.AddEntity(new SkLine(new SkPoint(t.A), new SkPoint(t.B)) { Role = Role });
        i.Sketch.AddConstraint(ConstraintType.Tangent, l, _first); i.Sketch.AddConstraint(ConstraintType.Tangent, l, e);
        i.Sketch.AddConstraint(ConstraintType.PointOnEntity, l.A, _first); i.Sketch.AddConstraint(ConstraintType.PointOnEntity, l.B, e);
        return true;
    }

    public override void Preview(List<Vector2d[]> lines, List<Vector2d> points)
    {
        points.Add(Cursor);
        if (_first != null && SkGeom.CircleOf(_first) is { } c1 && Round(SketchOf(_first), Cursor) is { } e && !ReferenceEquals(e, _first) && SkGeom.CircleOf(e) is { } c2 && Tangent(c1, c2, _p1, Cursor) is { } t)
            lines.Add(new[] { t.A, t.B });
    }
    private Sketch? _sk;
    public override void Move(SketchInput i) { base.Move(i); _sk = i.Sketch; }
    private Sketch SketchOf(SkEntity _) => _sk!;
}
