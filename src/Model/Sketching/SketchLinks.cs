using OpenTK.Mathematics;

namespace Parametric3D.Model.Sketching;

/// <summary>
/// k110 (отзыв 12:24): АССОЦИАТИВНЫЕ ССЫЛКИ НА ДРУГОЙ ЭСКИЗ. Кривая или точка эскиза на параллельной плоскости, взятая в
/// ссылки правимого эскиза, — ссылочная геометрия с источником «Эскиз #номер эскиза:устойчивый номер элемента». При любом
/// изменении эскиза-источника его ссылки в других эскизах перестраиваются на месте (те же точки — связи и размеры к ним
/// остаются), эскиз-получатель решается заново и его операция пересчитывается (цепочки эскизов — тоже). Номер эскиза при
/// чтении файла переводится на новый (как у операций). Мутация P3D_SKLINKS=0 — ссылка — снимок (прежде).
/// </summary>
public static class SketchLinks
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_SKLINKS") == "0";
    public const string Prefix = "Эскиз #";

    public static string SourceOf(Sketch other, SkEntity e) => $"{Prefix}{other.Id}:{e.StableId}";

    public static bool TryParse(string? src, out uint sketch, out uint entity)
    {
        sketch = entity = 0;
        if (src == null || !src.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = src[Prefix.Length..];
        int c = rest.IndexOf(':');
        return c > 0 && uint.TryParse(rest[..c], out sketch) && uint.TryParse(rest[(c + 1)..], out entity);
    }

    static Vector2d Map(Sketch sk, Sketch other, Vector2d p) => sk.Plane.ToLocal2(other.Plane.ToWorld(p));
    static bool SameSide(Sketch sk, Sketch other) => Vector3d.Dot(sk.Plane.W, other.Plane.W) > 0;

    /// <summary>Ссылочная копия элемента e эскиза other в плоскости sk (точки — те же, по порядку ручек).</summary>
    public static SkEntity? Make(Sketch sk, Sketch other, SkEntity e)
    {
        string src = SourceOf(other, e);
        SkPoint P(SkPoint q) => new(Map(sk, other, q.P)) { Role = SkRole.Reference, ReferenceSource = src };
        SkEntity? made = e switch
        {
            SkPoint q => P(q),
            SkLine l => new SkLine(P(l.A), P(l.B)),
            SkCircle c => new SkCircle(P(c.Center), c.Radius),
            SkArc a => new SkArc(P(a.Center), P(a.Start), P(a.End), SameSide(sk, other) ? a.CCW : !a.CCW),
            SkEllipse el => new SkEllipse(P(el.Center), el.RU, el.RV, 0),
            SkSpline sp => new SkSpline(sp.Ctrl.Select(P).ToList()),
            _ => null,
        };
        if (made == null) return null;
        made.Role = SkRole.Reference; made.ReferenceSource = src;
        Update(sk, other, e, made);
        return made;
    }

    /// <summary>Перенести положение источника в ссылку; true — что-то сдвинулось.</summary>
    public static bool Update(Sketch sk, Sketch other, SkEntity e, SkEntity r)
    {
        bool moved = false;
        void Set(SkPoint to, SkPoint from)
        {
            var p = Map(sk, other, from.P);
            if ((to.P - p).LengthSquared > 1e-24) { to.P = p; moved = true; }
        }
        switch (e, r)
        {
            case (SkPoint q, SkPoint rq): Set(rq, q); break;
            case (SkLine l, SkLine rl): Set(rl.A, l.A); Set(rl.B, l.B); break;
            case (SkCircle c, SkCircle rc):
                Set(rc.Center, c.Center);
                if (Math.Abs(rc.Radius - c.Radius) > 1e-12) { rc.Radius = c.Radius; moved = true; }
                break;
            case (SkArc a, SkArc ra): Set(ra.Center, a.Center); Set(ra.Start, a.Start); Set(ra.End, a.End); break;
            case (SkEllipse el, SkEllipse rel):
            {
                Set(rel.Center, el.Center);
                var ax = Map(sk, other, el.Center.P + new Vector2d(Math.Cos(el.Rotation), Math.Sin(el.Rotation))) - Map(sk, other, el.Center.P);
                double rot = Math.Atan2(ax.Y, ax.X);
                bool same = SameSide(sk, other);
                double a0 = same ? el.A0 : -el.A1, a1 = same ? el.A1 : -el.A0;
                if (Math.Abs(rel.RU - el.RU) + Math.Abs(rel.RV - el.RV) + Math.Abs(rel.Rotation - rot) + Math.Abs(rel.A0 - a0) + Math.Abs(rel.A1 - a1) > 1e-12)
                { rel.RU = el.RU; rel.RV = el.RV; rel.Rotation = rot; rel.A0 = a0; rel.A1 = a1; moved = true; }
                break;
            }
            case (SkSpline sp, SkSpline rs) when sp.Ctrl.Count == rs.Ctrl.Count:
                for (int i = 0; i < sp.Ctrl.Count; i++) Set(rs.Ctrl[i], sp.Ctrl[i]);
                break;
        }
        return moved;
    }

    /// <summary>Эскизы детали, у которых есть ссылки на эскиз src.</summary>
    public static IEnumerable<Sketch> Dependents(Part part, Sketch src) =>
        part.Sketches.Where(s => !ReferenceEquals(s, src) && s.Entities.Any(e => TryParse(e.ReferenceSource, out var id, out _) && id == src.Id));

    /// <summary>Эскиз src изменился — его ссылки в других эскизах перестроить; изменившиеся эскизы решить и известить.</summary>
    public static void Propagate(Part part, Sketch src, HashSet<Sketch>? seen = null)
    {
        if (Off) return;
        seen ??= new HashSet<Sketch>(ReferenceEqualityComparer.Instance) { src };
        foreach (var dep in Dependents(part, src).ToList())
        {
            if (!seen.Add(dep)) continue;
            bool moved = false;
            foreach (var r in dep.Entities.Where(e => e is not SkPoint || ((SkPoint)e).Owner == null).ToList())
            {
                if (!TryParse(r.ReferenceSource, out var sid, out var eid) || sid != src.Id) continue;
                var e = src.Entities.FirstOrDefault(x => x.StableId == eid);
                if (e != null) moved |= Update(dep, src, e, r);
            }
            if (!moved) continue;
            try { dep.Solve(new NumericSketchSolver()); } catch { }
            dep.BumpStructure();
            dep.NotifyChanged(dep);            // операция эскиза пересчитывается; его зависимые — дальше по цепочке
            Propagate(part, dep, seen);
        }
    }
}
