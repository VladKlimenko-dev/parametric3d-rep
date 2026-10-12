using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Render;

/// <summary>
/// Значки ограничений на эскизе.
///
/// Правила размещения:
///   * ограничения ЛИНИИ собираются в строку, параллельную ей, у её
///     середины и вынесены СНАРУЖИ замкнутого контура;
///   * ограничения УЗЛА (или центра дуги, окружности, эллипса) идут
///     строкой ниже узла.
///
/// Работа идёт в два прохода. Сначала считается раскладка: значки
/// группируются по объекту, к которому приписаны, и получают координаты.
/// Затем они рисуются, сгруппированные ПО ОГРАНИЧЕНИЮ.
///
/// Порядок именно такой из-за подсветки. Одно ограничение — например,
/// параллельность — рисуется значком на КАЖДОЙ из двух линий, и при
/// наведении должны загораться оба. Пакет линий хранит один диапазон
/// подсветки на идентификатор, поэтому все ломаные одного ограничения
/// должны лечь в буфер подряд.
/// </summary>
public static class ConstraintRenderer
{
    private static Settings S => Settings.Current;

    /// <summary>Один значок: тип, место и направление строки.</summary>
    private readonly struct Placement
    {
        public readonly SkConstraint Constraint;
        public readonly Vector2d Center;

        public Placement(SkConstraint c, Vector2d center)
        {
            Constraint = c; Center = center;
        }
    }

    /// <summary>Строка значков у одного объекта эскиза.</summary>
    private sealed class Row
    {
        public Vector2d Position;
        public Vector2d Direction;
        public bool AlongLine;
        public readonly List<SkConstraint> Items = new();
    }

    public static void Build(Sketch sk, LineBatch batch, double px)
    {
        if (!S.ShowConstraintGlyphs || px <= 0) return;

        // ---- проход 1: раскладка

        var rows = new Dictionary<object, Row>();

        foreach (var c in sk.Constraints)
        {
            c.GlyphPositions.Clear();
            if (!c.Enabled) continue;
            if (ConstraintGlyphs.Get(c.Type).Length == 0) continue;

            foreach (var item in c.Items)
            {
                if (!TryAnchor(item, out Vector2d pos, out Vector2d dir, out bool alongLine)) continue;

                if (!rows.TryGetValue(item, out var row))
                {
                    row = new Row { Position = pos, Direction = dir, AlongLine = alongLine };
                    rows[item] = row;
                }
                row.Items.Add(c);
            }
        }

        if (rows.Count == 0) return;

        List<List<Vector2d>> loops = ClosedLoops(sk);

        double away = S.ConstraintGlyphOffsetPx * px;

        var placements = new Dictionary<SkConstraint, List<Placement>>();

        foreach (var row in rows.Values)
        {
            Vector2d dir = row.Direction;
            Vector2d offset;

            if (row.AlongLine)
            {
                // Наружу контура: пробуем одну сторону и переходим на другую,
                // если попали внутрь замкнутого контура. У незамкнутого
                // контура обе стороны равноправны.
                Vector2d nrm = new(-dir.Y, dir.X);
                if (InsideAny(loops, row.Position + nrm * away)) nrm = -nrm;
                offset = nrm * away;
            }
            else
            {
                dir = new Vector2d(1, 0);
                offset = new Vector2d(0, -away);
            }

            // Шаг считается по фактическому размеру знака: у совпадения
            // он в полтора раза больше, и постоянный шаг наложил бы его
            // на соседей.
            var steps = new double[row.Items.Count];
            double total = 0;
            for (int i = 0; i < row.Items.Count; i++)
            {
                double w = (S.ConstraintGlyphSizePx * ConstraintGlyphs.Scale(row.Items[i].Type)
                            + S.ConstraintGlyphSpacingPx) * px;
                steps[i] = w;
                total += w;
            }
            total -= S.ConstraintGlyphSpacingPx * px;

            Vector2d start = row.Position + offset - dir * (total * 0.5);
            double pen = 0;

            for (int i = 0; i < row.Items.Count; i++)
            {
                var c = row.Items[i];
                Vector2d center = start + dir * (pen + steps[i] * 0.5
                                                - S.ConstraintGlyphSpacingPx * px * 0.5);
                pen += steps[i];

                if (!placements.TryGetValue(c, out var list))
                    placements[c] = list = new List<Placement>();

                list.Add(new Placement(c, center));
                c.GlyphPositions.Add(center);
            }
        }

        // ---- проход 2: отрисовка, ограничение за ограничением

        Frame3 plane = sk.Plane;
        double size = S.ConstraintGlyphSizePx * px;

        foreach (var (c, list) in placements)
        {
            LineStyle style = c.Conflict ? LineStyle.ConstraintConflict : LineStyle.ConstraintGlyph;

            CurrentStyle = style;
            CurrentPickId = c.Id;

            batch.BeginGroup(c.Id);
            foreach (var p in list)
                DrawGlyph(batch, plane, c.Type, p.Center, size * ConstraintGlyphs.Scale(c.Type));
            batch.EndGroup();
        }
    }

    /// <summary>Точка и направление строки значков для участника ограничения.</summary>
    private static bool TryAnchor(SkEntity item, out Vector2d pos, out Vector2d dir, out bool alongLine)
    {
        pos = Vector2d.Zero;
        dir = new Vector2d(1, 0);
        alongLine = false;

        switch (item)
        {
            case SkLine l:
                {
                    Vector2d d = l.B.P - l.A.P;
                    if (d.Length < 1e-9) return false;
                    pos = (l.A.P + l.B.P) * 0.5;
                    dir = d.Normalized();

                    // Строка читается слева направо: направление, смотрящее
                    // влево, разворачивается.
                    if (dir.X < 0 || (Math.Abs(dir.X) < 1e-9 && dir.Y < 0)) dir = -dir;
                    alongLine = true;
                    return true;
                }

            case SkPoint p:
                pos = p.P;
                return true;

            // У окружности, дуги и эллипса значки ставятся под центром:
            // это единственная точка, одинаково понятная для всех трёх.
            case SkCircle c: pos = c.Center.P; return true;
            case SkArc arc: pos = arc.Center.P; return true;
            case SkEllipse el: pos = el.Center.P; return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Рисует знак ВСЕГДА в осях эскиза, без поворота по линии.
    ///
    /// Строка значков по-прежнему идёт вдоль линии — так их удобно читать
    /// и они не наезжают на чертёж. А вот сами знаки поворачивать нельзя:
    /// вертикальная черта, повёрнутая вместе с горизонтальной линией,
    /// становится горизонтальной и означает ровно противоположное.
    /// </summary>
    private static void DrawGlyph(LineBatch batch, Frame3 plane, ConstraintType type,
                                  Vector2d center, double size)
    {
        Vector2d dir = new(1, 0);
        Vector2d up = new(0, 1);
        Vector2d origin = center - dir * (size * 0.5) - up * (size * 0.5);

        foreach (var stroke in ConstraintGlyphs.Get(type))
        {
            for (int i = 0; i + 3 < stroke.Length; i += 2)
            {
                Vector2d a = origin + dir * (stroke[i] * size) + up * (stroke[i + 1] * size);
                Vector2d b = origin + dir * (stroke[i + 2] * size) + up * (stroke[i + 3] * size);

                Vector3d wa = plane.ToWorld(a), wb = plane.ToWorld(b);
                batch.AddSegment(
                    new Vector3((float)wa.X, (float)wa.Y, (float)wa.Z),
                    new Vector3((float)wb.X, (float)wb.Y, (float)wb.Z),
                    CurrentStyle, CurrentPickId);
            }
        }
    }

    // Стиль и идентификатор текущего ограничения — их читает DrawGlyph.
    private static LineStyle CurrentStyle;
    private static uint CurrentPickId;

    /// <summary>Замкнутые контуры эскиза в координатах его плоскости.</summary>
    private static List<List<Vector2d>> ClosedLoops(Sketch sk)
    {
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
            // Недостроенный эскиз просто не даёт контуров — значки встанут
            // с любой стороны, и это не ошибка.
        }
        return result;
    }

    private static bool InsideAny(List<List<Vector2d>> loops, Vector2d p)
    {
        foreach (var loop in loops)
            if (GeomUtil.PointInPolygon(loop, p)) return true;
        return false;
    }
}
