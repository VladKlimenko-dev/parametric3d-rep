using OpenTK.Mathematics;
using Parametric3D.Model.Sketching;

namespace Parametric3D.Model.Features;

/// <summary>
/// k116 (снимок 21:45): ПРОЕКЦИИ РЁБЕР И ВЕРШИН В ЭСКИЗЕ СЛЕДУЮТ ЗА ТЕЛОМ. Точка-ссылка («Ребро #…», «Вершина #…») — проекция
/// вершины тела на плоскость эскиза в момент привязки; номера рёбер между расчётами не постоянны, и точка оставалась прежней.
/// Тело с тех пор сдвинулось на 1.6·10⁻⁶ мм (уточнение точных путей) — привязанная к ней вершина резца легла рядом с ребром
/// кармана, но не на него: точная булева не собирала такую почти-совпадающую геометрию, деталь с этой операции уходила на
/// многогранный путь (двойные стенки, огранённые поверхности, расчёт вчетверо дольше). Перед использованием эскиза точка-ссылка
/// ближе 10⁻⁴ мм к проекции вершины тела ставится в неё. Мутация P3D_EDGEREFFOLLOW=0.
/// </summary>
public static class SketchEdgeRefs
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_EDGEREFFOLLOW") == "0";
    public const double Reach = 1e-4;
    /// <summary>Точек-ссылок, поставленных в проекцию вершины (счётчик разбора).</summary>
    public static long Followed;

    public static void Follow(Sketch sk, BuildContext ctx)
    {
        if (Off || ctx.Bodies.Count == 0) return;
        List<Vector2d>? proj = null;
        foreach (var p in sk.AllPoints())
        {
            if (p.Role != SkRole.Reference) continue;
            var src = p.ReferenceSource ?? "";
            if (!(src.StartsWith("Ребро #", StringComparison.Ordinal) || src.StartsWith("Вершина #", StringComparison.Ordinal))) continue;
            proj ??= ctx.Bodies.SelectMany(b => b.Vertices).Select(v => sk.Plane.ToLocal2(v.P)).ToList();
            double best = Reach; Vector2d? at = null;
            foreach (var q in proj) { double d = (q - p.P).Length; if (d < best) { best = d; at = q; } }
            if (at is { } a && best > 0) { p.X = a.X; p.Y = a.Y; Interlocked.Increment(ref Followed); }
        }
    }
}
