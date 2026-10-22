using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Render;

/// <summary>
/// Раскладка значков ограничений на эскизе (перенесено из прежнего
/// ConstraintRenderer, проход 1 — без привязки к способу рисования).
///
/// Правила размещения:
///   * ограничения ЛИНИИ собираются в строку, параллельную ей, у её
///     середины и вынесены СНАРУЖИ замкнутого контура;
///   * ограничения УЗЛА (или центра дуги, окружности, эллипса) идут
///     строкой ниже узла; узлы в одной точке (совпадение) дают одну строку;
///   * шаг строки — по фактическому размеру значка, строка центрируется.
///
/// Результат — координаты в плоскости эскиза; они же пишутся в
/// <see cref="SkConstraint.GlyphPositions"/> (по ним ловит мышь).
/// </summary>
public static class ConstraintLayout
{
    private static Settings S => Settings.Current;

    /// <summary>Значки рисуются картинками (иконки con_*), а не векторными штрихами.</summary>
    public static bool IconMode = true;

    /// <summary>Сторона значка на экране, пикселей (DIP).</summary>
    public static double SizePx(ConstraintType t) =>
        IconMode ? S.ConstraintIconSizePx : S.ConstraintGlyphSizePx * ConstraintGlyphs.Scale(t);

    /// <summary>Файл иконки ограничения.</summary>
    public static string IconOf(ConstraintType t) => t switch
    {
        ConstraintType.Coincident => "con_coincident_but.svg",
        ConstraintType.PointOnEntity => "con_point_on_but.svg",
        ConstraintType.Horizontal => "con_horizontal_but.svg",
        ConstraintType.Vertical => "con_vertical_but.svg",
        ConstraintType.Parallel => "con_parallel_but.svg",
        ConstraintType.Perpendicular => "con_perpendicular_but.svg",
        ConstraintType.Tangent => "con_tangent_but.svg",
        ConstraintType.Equal => "con_equal_but.svg",
        ConstraintType.Symmetric => "con_symmetric_but.svg",
        ConstraintType.MidPoint => "con_midpoint_but.svg",
        ConstraintType.Concentric => "con_concentric_but.svg",
        ConstraintType.Fix => "con_fix_but.svg",
        _ => "",
    };

    public readonly record struct Placement(SkConstraint Constraint, Vector2d Center);

    private sealed class Row
    {
        public Vector2d Position;
        public Vector2d Direction;
        public bool AlongLine;
        public readonly List<SkConstraint> Items = new();
    }

    /// <summary>Сколько раз считались замкнутые контуры (кэш по геометрии эскиза).</summary>
    public static int LoopBuilds;
    private static Sketch? _loopSketch;
    private static double _loopKey = double.NaN;
    private static List<List<Vector2d>> _loops = new();

    /// <summary>Раскладка для эскиза при размере пикселя px (единиц эскиза на пиксель).</summary>
    public static List<Placement> Build(Sketch sk, double px)
    {
        var result = new List<Placement>();
        foreach (var c in sk.Constraints) c.GlyphPositions.Clear();
        if (!S.ShowConstraintGlyphs || px <= 0 || double.IsNaN(px)) return result;

        // ---- строки по объектам
        var rows = new List<Row>();
        var byEntity = new Dictionary<SkEntity, Row>();
        double merge = 0.5 * px;

        foreach (var c in sk.Constraints)
        {
            if (!c.Enabled) continue;
            if (IconMode ? IconOf(c.Type).Length == 0 : ConstraintGlyphs.Get(c.Type).Length == 0) continue;

            var seen = new HashSet<Row>();
            foreach (var item in c.Items)
            {
                if (!TryAnchor(item, out Vector2d pos, out Vector2d dir, out bool alongLine)) continue;
                if (!byEntity.TryGetValue(item, out var row))
                {
                    // узлы в одной точке — одна строка (у совпадения иначе два одинаковых значка друг на друге)
                    if (!alongLine)
                        row = rows.FirstOrDefault(r => !r.AlongLine && (r.Position - pos).Length <= merge);
                    if (row == null)
                    {
                        row = new Row { Position = pos, Direction = dir, AlongLine = alongLine };
                        rows.Add(row);
                    }
                    byEntity[item] = row;
                }
                if (seen.Add(row)) row.Items.Add(c);
            }
        }
        if (rows.Count == 0) return result;

        var loops = ClosedLoops(sk);
        double away = S.ConstraintGlyphOffsetPx * px;
        double gap = S.ConstraintGlyphSpacingPx * px;

        foreach (var row in rows)
        {
            Vector2d dir = row.Direction, offset;
            if (row.AlongLine)
            {
                // наружу контура: внутрь замкнутого — переходим на другую сторону
                Vector2d nrm = new(-dir.Y, dir.X);
                if (InsideAny(loops, row.Position + nrm * away)) nrm = -nrm;
                offset = nrm * away;
            }
            else
            {
                dir = new Vector2d(1, 0);
                offset = new Vector2d(0, -away);
            }

            var steps = new double[row.Items.Count];
            double total = 0;
            for (int i = 0; i < row.Items.Count; i++)
            {
                steps[i] = SizePx(row.Items[i].Type) * px + gap;
                total += steps[i];
            }
            total -= gap;

            // строка, наехавшая на уже поставленные значки (короткая сторона, угол рядом с серединой),
            // отодвигается дальше от своего объекта — не более трёх шагов
            Vector2d push = offset.Length > 0 ? offset.Normalized() : new Vector2d(0, -1);
            var centers = new Vector2d[row.Items.Count];
            for (int attempt = 0; ; attempt++)
            {
                Vector2d start = row.Position + offset + push * (attempt * (S.ConstraintGlyphSpacingPx * 2 + SizePx(row.Items[0].Type)) * px) - dir * (total * 0.5);
                double pen = 0;
                bool clash = false;
                for (int i = 0; i < row.Items.Count; i++)
                {
                    centers[i] = start + dir * (pen + (steps[i] - gap) * 0.5);
                    pen += steps[i];
                    double need = (SizePx(row.Items[i].Type) + 2) * px;
                    foreach (var q in result)
                        if (Math.Abs(q.Center.X - centers[i].X) < need && Math.Abs(q.Center.Y - centers[i].Y) < need) { clash = true; break; }
                }
                if (!clash || attempt >= 3) break;
            }
            for (int i = 0; i < row.Items.Count; i++)
            {
                row.Items[i].GlyphPositions.Add(centers[i]);
                result.Add(new Placement(row.Items[i], centers[i]));
            }
        }
        return result;
    }

    /// <summary>Точка и направление строки значков для участника ограничения.</summary>
    public static bool TryAnchor(SkEntity item, out Vector2d pos, out Vector2d dir, out bool alongLine)
    {
        pos = Vector2d.Zero;
        dir = new Vector2d(1, 0);
        alongLine = false;
        if (item.Builtin) return false;   // k96: нуль и оси эскиза — значок у другого участника
        switch (item)
        {
            case SkLine l:
                {
                    Vector2d d = l.B.P - l.A.P;
                    if (d.Length < 1e-9) return false;
                    pos = (l.A.P + l.B.P) * 0.5;
                    dir = d.Normalized();
                    // строка читается слева направо
                    if (dir.X < 0 || (Math.Abs(dir.X) < 1e-9 && dir.Y < 0)) dir = -dir;
                    alongLine = true;
                    return true;
                }
            case SkPoint p: pos = p.P; return true;
            // у окружности, дуги и эллипса — под центром
            case SkCircle c: pos = c.Center.P; return true;
            case SkArc arc: pos = arc.Center.P; return true;
            case SkEllipse el: pos = el.Center.P; return true;
            default: return false;
        }
    }

    /// <summary>Замкнутые контуры эскиза в координатах его плоскости (кэш по положению точек).</summary>
    private static List<List<Vector2d>> ClosedLoops(Sketch sk)
    {
        double key = sk.StructureVersion * 7919.0;
        int n = 0;
        foreach (var p in sk.AllPoints()) { n++; key += (p.P.X * 1.618 + p.P.Y * 2.718) * n; }
        key += n;
        if (ReferenceEquals(sk, _loopSketch) && key == _loopKey) return _loops;

        var result = new List<List<Vector2d>>();
        try
        {
            Profile pr = sk.ToProfile();
            foreach (var loop in pr.Loops)
            {
                if (loop.SelfIntersects) continue;
                var pts = loop.Sample(pr.Plane);
                if (pts.Count >= 3) result.Add(pts);
            }
        }
        catch
        {
            // недостроенный эскиз просто не даёт контуров
        }
        LoopBuilds++;
        _loopSketch = sk; _loopKey = key; _loops = result;
        return result;
    }

    private static bool InsideAny(List<List<Vector2d>> loops, Vector2d p)
    {
        foreach (var loop in loops)
            if (GeomUtil.PointInPolygon(loop, p)) return true;
        return false;
    }
}
