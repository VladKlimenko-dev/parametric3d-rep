using System.Globalization;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model.Features;

/// <summary>Что замерено: точка (вершина), прямая (ребро) или плоскость (грань) — с именем для поиска после перестроения.</summary>
public sealed class MeasureRef
{
    public int Type;               // 0 — точка, 1 — прямая, 2 — плоскость
    public Vector3d P, D;          // точка; направление прямой или нормаль плоскости
    public string Key = "";        // вершина — ключ граней вокруг; ребро — метки двух граней; грань — метка грани
    public MeasureRef Clone() => (MeasureRef)MemberwiseClone();

    public string Pack() => string.Join(";", Type.ToString(CultureInfo.InvariantCulture), V(P), V(D), Key.Replace(';', '\u001f'));
    public static MeasureRef? Unpack(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var a = s.Split(';');
        if (a.Length < 4) return null;
        return new MeasureRef { Type = int.Parse(a[0], CultureInfo.InvariantCulture), P = P3(a[1]), D = P3(a[2]), Key = a[3].Replace('\u001f', ';') };
    }
    static string V(Vector3d v) => string.Join(",", new[] { v.X, v.Y, v.Z }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
    static Vector3d P3(string s) { var p = s.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new Vector3d(p[0], p[1], p[2]); }
}

/// <summary>
/// ЗАМЕР — ОПЕРАЦИЯ ДЕРЕВА (k120, замечание 15:06, п. 17). Расстояние между двумя точками, параллельными прямыми или
/// параллельными плоскостями (и от точки до прямой или плоскости). Тела не меняет: при перестроении находит свои элементы
/// по именам (вершина — по меткам граней вокруг, ребро — по меткам двух граней, грань — по своей метке; не нашла — прежняя
/// геометрия) и пересчитывает значение. На виде — размер (выносные, размерная линия, значение); подпись тянется мышью.
/// </summary>
public sealed class MeasureFeature : Feature
{
    public MeasureRef RefA = new(), RefB = new();
    /// <summary>Концы размера (мир) и значение — итог последнего перестроения.</summary>
    public Vector3d A, B;
    public double Value;
    /// <summary>Сторона выноса размера (единичная) и вынос в единицах модели (тянется мышью).</summary>
    public Vector3d Side = Vector3d.UnitZ;
    public double Offset = 10;
    /// <summary>Элементы не найдены после перестроения — показана прежняя геометрия.</summary>
    public bool Stale;

    public MeasureFeature(string name = "Measure") { Name = name; }
    public override string Summary => $"{Value.ToString("0.####", CultureInfo.InvariantCulture)}";

    /// <summary>Посчитать замер по двум ссылкам; null — пара не измеряется (непараллельные прямые или плоскости).</summary>
    public static (Vector3d A, Vector3d B)? Solve(MeasureRef a, MeasureRef b)
    {
        if (a.Type > b.Type) { var r = Solve(b, a); return r is { } x ? (x.B, x.A) : null; }
        Vector3d FootLine(Vector3d p, MeasureRef l) { var d = l.D.Normalized(); return l.P + d * Vector3d.Dot(p - l.P, d); }
        Vector3d FootPlane(Vector3d p, MeasureRef q) { var n = q.D.Normalized(); return p - n * Vector3d.Dot(p - q.P, n); }
        bool Parallel(Vector3d u, Vector3d v) => Vector3d.Cross(u.Normalized(), v.Normalized()).Length < 1e-6;
        switch ((a.Type, b.Type))
        {
            case (0, 0): return (a.P, b.P);
            case (0, 1): return (a.P, FootLine(a.P, b));
            case (0, 2): return (a.P, FootPlane(a.P, b));
            case (1, 1): return Parallel(a.D, b.D) ? (a.P, FootLine(a.P, b)) : null;
            case (1, 2): return Math.Abs(Vector3d.Dot(a.D.Normalized(), b.D.Normalized())) < 1e-6 ? (a.P, FootPlane(a.P, b)) : null;
            case (2, 2): return Parallel(a.D, b.D) ? (a.P, FootPlane(a.P, b)) : null;
        }
        return null;
    }

    public override void Execute(BuildContext ctx)
    {
        // элемент без имени (грань без метки, точка эскиза) — неподвижная геометрия: искать нечего, это не «потерян»
        bool okA = RefA.Key.Length == 0 || Resolve(RefA, ctx.Bodies), okB = RefB.Key.Length == 0 || Resolve(RefB, ctx.Bodies);
        Stale = !(okA && okB);
        if (Solve(RefA, RefB) is { } r) { A = r.A; B = r.B; Value = (B - A).Length; }
        else Stale = true;
        if (Stale) ctx.Warnings.Add($"{Name}: the measured elements were not found after the rebuild; the previous geometry is shown.");
    }

    /// <summary>Найти элемент по имени в телах; true — найден (ссылка обновлена).</summary>
    public static bool Resolve(MeasureRef r, IEnumerable<TSolid> bodies)
    {
        if (r.Key.Length == 0) return false;
        switch (r.Type)
        {
            case 0:
            {
                TVertex? best = null; double bd = double.MaxValue;
                foreach (var b in bodies) foreach (var v in b.Vertices)
                    if (EdgeRef.EndKey(v) == r.Key && (v.P - r.P).LengthSquared is var d && d < bd) { bd = d; best = v; }
                if (best == null) return false;
                r.P = best.P; return true;
            }
            case 1:
            {
                TEdge? best = null; double bd = double.MaxValue;
                foreach (var b in bodies) foreach (var e in b.Edges)
                {
                    if (e.Curve is not LineCurve3 || EdgeKey(e) != r.Key) continue;
                    double d = (e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2) - r.P).LengthSquared;
                    if (d < bd) { bd = d; best = e; }
                }
                if (best == null) return false;
                r.P = best.Curve.PointAt((best.Curve.T0 + best.Curve.T1) / 2); r.D = (best.V1.P - best.V0.P).Normalized(); return true;
            }
            default:
            {
                // метка грани не всегда единственна (все внутренние грани оболочки — «shell:inner»): из граней с меткой —
                // параллельная прежней нормали и ближайшая к прежней плоскости
                PlaneSurface? best = null; double bd = double.MaxValue;
                var d0 = r.D.LengthSquared > 1e-18 ? r.D.Normalized() : Vector3d.Zero;
                foreach (var b in bodies) foreach (var f in b.Faces)
                {
                    if (f.Tag != r.Key || GeometryAudit.AuthoritativeSurface(f) is not PlaneSurface ps) continue;
                    var n = ps.F.W.Normalized();
                    double score = Math.Abs(Vector3d.Dot(r.P - ps.F.Origin, n)) + (d0 == Vector3d.Zero ? 0 : (1 - Math.Abs(Vector3d.Dot(n, d0))) * 1e6);
                    if (score < bd) { bd = score; best = ps; }
                }
                if (best == null) return false;
                var nb = best.F.W.Normalized();
                r.P = r.P - nb * Vector3d.Dot(r.P - best.F.Origin, nb); r.D = nb; return true;
            }
        }
    }

    public static string EdgeKey(TEdge e)
    {
        var a = e.FaceA?.Tag ?? ""; var b = e.FaceB?.Tag ?? "";
        return string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
    }
}
