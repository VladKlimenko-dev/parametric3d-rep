using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// РЕБРО ЖЁСТКОСТИ: плоский профиль наращивается в стороны на толщину и растёт в заданном
/// направлении, пока не упрётся в тело; получившийся брусок объединяется с телом.
///
/// Длина роста считается лучами из точек профиля: берётся ближайшее попадание в тело — так
/// ребро доходит до стенки и не торчит за ней. Если ни один луч в тело не попал, задаётся
/// предельная длина.
/// </summary>
public static class RibOp
{
    public static bool Build(List<TSolid> bodies, Profile sketch, Vector3d grow, double thickness,
                             double maxLength, List<string> warnings)
    {
        if (bodies.Count == 0) { warnings.Add("Rib: no body."); return false; }
        if (sketch.IsEmpty) { warnings.Add("Rib: the sketch is empty or not closed."); return false; }
        if (thickness <= Tol.Weld) { warnings.Add("Rib: the thickness must be greater than zero."); return false; }
        if (grow.LengthSquared < 1e-18) { warnings.Add("Rib: no direction to grow."); return false; }

        Vector3d d = grow.Normalized();

        // Точки профиля — начала лучей.
        var starts = new List<Vector3d>();
        foreach (var loop in sketch.Loops)
            foreach (var c in loop.Curves)
            {
                var pts = new List<Vector3d>();
                c.Tessellate(pts, true);
                starts.AddRange(pts);
            }
        if (starts.Count == 0) { warnings.Add("Rib: the sketch has no points."); return false; }

        var tris = Voxels.Occupancy.Triangles(Tess.Tessellator.Tessellate(bodies[0], facets: true));
        double reach = maxLength;
        foreach (Vector3d s in starts)
        {
            double t = FirstHit(tris, s, d, maxLength);
            if (t > Tol.Weld) reach = Math.Min(reach, t);
        }
        if (reach <= Tol.Weld) { warnings.Add("Rib: the sketch already touches the body."); return false; }

        // ПРОФИЛЬ УДЛИНЯЕТСЯ В СВОЕЙ ПЛОСКОСТИ, А ВЫДАВЛИВАЕТСЯ ПОПЕРЁК НЕЁ. Сначала я
        // выдавливал профиль вдоль роста, то есть в его же плоскости, и получал плёнку нулевой
        // толщины: ребро не прирастало вовсе.
        var plane2 = sketch.Plane;
        Vector2d v2 = new(Vector3d.Dot(d * reach, plane2.U), Vector3d.Dot(d * reach, plane2.V));
        var flat = new List<Vector2d>();
        foreach (var loop in sketch.Loops)
            foreach (var c in loop.Curves)
            {
                var pts = new List<Vector3d>();
                c.Tessellate(pts, true);
                foreach (var q in pts) { var l = plane2.ToLocal2(q); flat.Add(l); flat.Add(l + v2); }
            }
        var hull = ConvexHull(flat);
        if (hull.Count < 3) { warnings.Add("Rib: the sketch is degenerate."); return false; }

        var curves = new List<ICurve3>();
        for (int i = 0; i < hull.Count; i++)
            curves.Add(new LineCurve3(plane2.ToWorld(hull[i]), plane2.ToWorld(hull[(i + 1) % hull.Count])));
        var grown = ProfileBuilder.Build(plane2, curves);

        Vector3d across = plane2.W.Normalized();
        TSolid plate = ExtrudeOp.Build(grown, across * thickness);
        TSolid thick = TransformOp.Copy(plate, TransformOp.Move(-across * (thickness / 2)));

        BooleanOp.Unite(bodies, thick, warnings);
        warnings.Add($"Rib: thickness {thickness:0.###}, grown {reach:0.###} up to the body.");
        return true;
    }

    /// <summary>Выпуклая оболочка точек в плоскости (обход Эндрю).</summary>
    private static List<Vector2d> ConvexHull(List<Vector2d> pts)
    {
        var p = pts.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross(Vector2d o, Vector2d a, Vector2d b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<Vector2d>();
        foreach (var q in p)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], q) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(q);
        }
        var upper = new List<Vector2d>();
        for (int i = p.Count - 1; i >= 0; i--)
        {
            var q = p[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], q) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(q);
        }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    /// <summary>Утолщение: объединение двух копий, сдвинутых на ±половину толщины.</summary>
    private static TSolid Thicken(TSolid slab, Vector3d across, double thickness)
    {
        var list = new List<TSolid> { TransformOp.Copy(slab, TransformOp.Move(across * (thickness / 2))) };
        var w = new List<string>();
        BooleanOp.Unite(list, TransformOp.Copy(slab, TransformOp.Move(-across * (thickness / 2))), w);
        return list[0];
    }

    /// <summary>
    /// K09. РЕБРО ЖЁСТКОСТИ ПО ОТКРЫТОМУ ЭСКИЗУ — обычный случай: отрезок задаёт свободную
    /// кромку, ребро заполняет плоскость эскиза от неё до тела вдоль направления роста.
    /// Прежний путь требовал замкнутый контур, а «досягаемость» брал наименьшей по всем точкам:
    /// у косого ребра в угловом кронштейне концы отрезка лежат на теле, и ребро не строилось.
    ///
    /// Здесь каждая точка кромки (плотная выборка) идёт по направлению роста до первого попадания
    /// в тело; область ребра — многоугольник «точки кромки → точки попадания». Если соседние
    /// попадания легли на разные плоские грани, между ними вставляется точка пересечения этих
    /// граней в плоскости эскиза: внутренний угол тела не срезается хордой.
    /// </summary>
    public static bool BuildOpen(List<TSolid> bodies, Frame3 plane, IReadOnlyList<ICurve3> edge, Vector3d grow,
                                 double thickness, double maxLength, List<string> warnings)
    {
        if (bodies.Count == 0 || edge.Count == 0) { warnings.Add("Rib: no body or no edge."); return false; }
        Vector3d d = grow.Normalized();
        // Направление роста — в плоскости эскиза.
        d = (d - plane.W.Normalized() * Vector3d.Dot(d, plane.W.Normalized()));
        if (d.LengthSquared < 1e-18) { warnings.Add("Rib: the direction lies across the sketch plane."); return false; }
        d = d.Normalized();
        var tris = Voxels.Occupancy.Triangles(Tess.Tessellator.Tessellate(bodies[0], facets: true));

        var line = new List<Vector3d>();
        foreach (var c in edge)
        {
            int n = 64;
            for (int i = 0; i <= n; i++)
            {
                var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / n);
                if (line.Count == 0 || (line[^1] - q).LengthSquared > 1e-18) line.Add(q);
            }
        }
        var hits = new List<(Vector3d P, Vector3d N)>();
        foreach (var q in line)
        {
            var (t, nrm) = Hit(tris, q, d, maxLength);
            if (t >= maxLength) { warnings.Add("Rib: the edge does not reach the body in the grow direction."); return false; }
            hits.Add((q + d * t, nrm));
        }
        // Дальняя граница: попадания с углами между разными гранями.
        var far = new List<Vector3d> { hits[0].P };
        for (int i = 1; i < hits.Count; i++)
        {
            var (pa, na) = hits[i - 1]; var (pb, nb) = hits[i];
            if (Vector3d.Dot(na, nb) < 1 - 1e-9 && Corner(plane, pa, na, pb, nb) is { } corner) far.Add(corner);
            far.Add(pb);
        }
        var poly = new List<Vector2d>();
        foreach (var q in line) poly.Add(plane.ToLocal2(q));
        for (int i = far.Count - 1; i >= 0; i--) poly.Add(plane.ToLocal2(far[i]));
        // Повторы точек (концы кромки на теле) убираются.
        var clean = new List<Vector2d>();
        foreach (var q in poly) if (clean.Count == 0 || (clean[^1] - q).LengthSquared > 1e-16) clean.Add(q);
        if (clean.Count > 2 && (clean[0] - clean[^1]).LengthSquared < 1e-16) clean.RemoveAt(clean.Count - 1);
        if (clean.Count < 3) { warnings.Add("Rib: the rib region is degenerate."); return false; }

        var curves = new List<ICurve3>();
        for (int i = 0; i < clean.Count; i++)
            curves.Add(new LineCurve3(plane.ToWorld(clean[i]), plane.ToWorld(clean[(i + 1) % clean.Count])));
        var region = ProfileBuilder.Build(plane, curves);
        if (region.IsEmpty) { warnings.Add("Rib: the rib region is not a simple polygon."); return false; }
        Vector3d across = plane.W.Normalized();
        TSolid plate = ExtrudeOp.Build(region, across * thickness);
        TSolid thick = TransformOp.Copy(plate, TransformOp.Move(-across * (thickness / 2)));
        BooleanOp.Unite(bodies, thick, warnings);
        warnings.Add($"Rib: thickness {thickness:0.###}, grown along the sketch edge to the body.");
        return true;
    }

    /// <summary>Пересечение следов двух плоских граней в плоскости эскиза.</summary>
    private static Vector3d? Corner(Frame3 plane, Vector3d pa, Vector3d na, Vector3d pb, Vector3d nb)
    {
        // След грани в плоскости: точки x плоскости с n·(x − p) = 0. В координатах плоскости —
        // прямая a·u + b·v = c.
        Vector2d A = new(Vector3d.Dot(na, plane.U), Vector3d.Dot(na, plane.V));
        Vector2d B = new(Vector3d.Dot(nb, plane.U), Vector3d.Dot(nb, plane.V));
        double ca = Vector3d.Dot(na, pa - plane.Origin), cb = Vector3d.Dot(nb, pb - plane.Origin);
        double det = A.X * B.Y - A.Y * B.X;
        if (Math.Abs(det) < 1e-12) return null;
        double u = (ca * B.Y - A.Y * cb) / det, v = (A.X * cb - ca * B.X) / det;
        var q = plane.ToWorld(new Vector2d(u, v));
        // Угол должен лежать рядом с обоими попаданиями (не дальше расстояния между ними).
        double span = (pa - pb).Length;
        return (q - pa).Length <= 2 * span + 1e-9 && (q - pb).Length <= 2 * span + 1e-9 ? q : null;
    }

    /// <summary>Первое попадание луча в треугольники (без скользящих) и нормаль попавшего треугольника.</summary>
    private static (double T, Vector3d N) Hit(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris, Vector3d q, Vector3d dir, double limit)
    {
        double best = limit; Vector3d bn = Vector3d.Zero;
        foreach (var (a, b, c) in tris)
        {
            Vector3d nt = Vector3d.Cross(b - a, c - a);
            if (nt.LengthSquared < 1e-24 || Math.Abs(Vector3d.Dot(dir, nt.Normalized())) < 1e-6) continue;
            Vector3d e1 = b - a, e2 = c - a, p = Vector3d.Cross(dir, e2);
            double det = Vector3d.Dot(e1, p);
            if (Math.Abs(det) < 1e-14) continue;
            double inv = 1 / det; Vector3d s = q - a;
            double u = Vector3d.Dot(s, p) * inv;
            if (u < -1e-12 || u > 1 + 1e-12) continue;
            Vector3d qv = Vector3d.Cross(s, e1);
            double v = Vector3d.Dot(dir, qv) * inv;
            if (v < -1e-12 || u + v > 1 + 1e-12) continue;
            double t = Vector3d.Dot(e2, qv) * inv;
            if (t >= -1e-9 && t < best) { best = Math.Max(0, t); bn = nt.Normalized(); }
        }
        return (best, bn);
    }

    private static double FirstHit(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris,
                                   Vector3d q, Vector3d dir, double limit)
    {
        double best = limit;
        foreach (var (a, b, c) in tris)
        {
            // Скользящие попадания не считаются: луч от нижнего края профиля идёт вдоль грани
            // тела и «упирался» в неё сразу — ребро не вырастало вовсе.
            Vector3d nt = Vector3d.Cross(b - a, c - a);
            if (nt.LengthSquared < 1e-24 || Math.Abs(Vector3d.Dot(dir, nt.Normalized())) < 1e-6) continue;

            Vector3d e1 = b - a, e2 = c - a;
            Vector3d p = Vector3d.Cross(dir, e2);
            double det = Vector3d.Dot(e1, p);
            if (Math.Abs(det) < 1e-14) continue;
            double inv = 1 / det;
            Vector3d s = q - a;
            double u = Vector3d.Dot(s, p) * inv;
            if (u < -1e-12 || u > 1 + 1e-12) continue;
            Vector3d qv = Vector3d.Cross(s, e1);
            double v = Vector3d.Dot(dir, qv) * inv;
            if (v < -1e-12 || u + v > 1 + 1e-12) continue;
            double t = Vector3d.Dot(e2, qv) * inv;
            if (t > 1e-9 && t < best) best = t;
        }
        return best;
    }
}
