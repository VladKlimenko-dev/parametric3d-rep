namespace Parametric3D.Kernel.Topology;

/// <summary>Происхождение грани результата операции.</summary>
public enum FaceOrigin
{
    /// <summary>Грань операнда целиком, геометрия не изменилась.</summary>
    Kept,
    /// <summary>Грань операнда, обрезанная или продолженная (та же поверхность, другие контуры).</summary>
    Modified,
    /// <summary>Одна из нескольких граней результата из одной грани операнда.</summary>
    Split,
    /// <summary>Новая грань (лента скругления, угол, фаска).</summary>
    Generated,
}

/// <summary>Грань результата и её источник (null — новая).</summary>
public sealed record FaceProvenance(TFace Result, TFace? Source, FaceOrigin Kind, string? Note = null);

/// <summary>
/// KRN-08 (08.2): ПРОИСХОЖДЕНИЕ КАК ВЫХОД ОПЕРАЦИИ. Построитель записывает, из какой грани операнда получена каждая грань
/// результата и как (сохранена, изменена, разделена, новая), какие грани операндов исчезли и какие слились с совпадающей
/// гранью другого операнда. Это знание построителя в момент сборки, а не сопоставление после факта (по именам или
/// геометрии). Годно для той ревизии тела, при которой записано (дальнейшая правка тела делает его недоступным).
/// </summary>
public sealed class Provenance
{
    public readonly List<FaceProvenance> Faces = new();
    /// <summary>Грани операндов, от которых в результате ничего не осталось.</summary>
    public readonly List<TFace> Deleted = new();
    /// <summary>Грани операндов, слившиеся с совпадающей гранью другого операнда (в результате — одна грань).</summary>
    public readonly List<(TFace Source, TFace Into)> Merged = new();
    public string Operation = "";

    public FaceProvenance? Of(TFace result) => Faces.FirstOrDefault(p => ReferenceEquals(p.Result, result));
    public IEnumerable<TFace> ResultsOf(TFace source) => Faces.Where(p => ReferenceEquals(p.Source, source)).Select(p => p.Result);
}
