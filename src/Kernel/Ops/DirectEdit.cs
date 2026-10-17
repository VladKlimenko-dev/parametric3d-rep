using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// u13: ПРЯМАЯ ПРАВКА ГРАНЕЙ — кнопки «смещение граней», «перенос грани», «новый радиус», «удаление граней». Смещение —
/// тем же путём, что оболочка (DirectRound.OffsetSolid: грани по своим поверхностям, соседи продлеваются до встречи);
/// удаление сопряжений — заострением (полоса убирается, соседи продлеваются до общего ребра).
/// </summary>
public static class DirectEdit
{
    /// <summary>Тело со смещёнными гранями (offset(f) по нормали грани; 0 — на месте); null — не вышло (why).</summary>
    public static TSolid? OffsetFaces(TSolid body, Func<TFace, double> offset, ref string? why)
        => DirectRound.OffsetSolid(body, offset, "direct:offset", ref why);

    /// <summary>Радиус грани после смещения на d (цилиндр, тор, шар, круговое выдавливание); null — у грани нет радиуса.</summary>
    public static double? RadiusAfter(TFace f, double d) => DirectRound.OffsetRadiusOf(f, d);

    /// <summary>Смещение, которое даёт грани радиус r; null — у грани нет радиуса.</summary>
    public static double? OffsetForRadius(TFace f, double r)
    {
        if (RadiusAfter(f, 0) is not { } r0 || RadiusAfter(f, 1) is not { } r1) return null;
        double s = r1 - r0; // ±1
        return Math.Abs(s) < 0.5 ? null : (r - r0) / s;
    }

    /// <summary>Тело без сопряжений vanish (скругления, касательные полосы между двумя гранями); null — не вышло (why).</summary>
    public static TSolid? RemoveBlends(TSolid body, IEnumerable<TFace> vanish, ref string? why)
    {
        double sc = 1; foreach (var v in body.Vertices) sc = Math.Max(sc, v.P.Length);
        return DirectRound.SharpenFaces(body, vanish.ToHashSet(), 1e-9 * sc, ref why);
    }
}
