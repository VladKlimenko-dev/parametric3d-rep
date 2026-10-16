using OpenTK.Mathematics;

namespace Parametric3D.Render.Engine;

/// <summary>
/// ЦВЕТА СЦЕНЫ по UI_VIEW_PALETTE.md (тёмная и светлая темы). Цвет — sRGB 0..1 с прозрачностью; линии и точки — в
/// логических пикселях (на экране умножаются на масштаб DPI).
/// </summary>
public sealed class RenderTheme
{
    public Vector4 BackgroundTop, BackgroundBottom;
    public Vector4 Body, Edge, EdgeHidden, Silhouette, Vertex;
    /// <summary>Рёбра в стилях без граней (каркас, невидимые линии): светлые на тёмном фоне, тёмные на светлом.</summary>
    public Vector4 EdgeWire;
    /// <summary>Рёбра тела в режиме эскиза (кандидаты в ссылки).</summary>
    public Vector4 SketchRefEdge;
    /// <summary>Размеры эскиза: по умолчанию (серые), управляющие, справочные, конфликт.</summary>
    public Vector4 DimDefault, DimLocked, DimReference;
    /// <summary>Ручки операций у выбранного (как в Plasticity).</summary>
    public Vector4 Handle, HandleAlt;
    public Vector4 Hover, Select, Error;
    public Vector4 DatumPlane, DatumBorder, DatumAxis, Origin, DatumPoint;
    public Vector4 SketchCurve, SketchConstruction, SketchCenterline, SketchReference, SketchPoint;
    public Vector4 PreviewAdd, PreviewAddEdge, PreviewCut, PreviewCutEdge;
    public float EdgeWidth = 1.5f, SilhouetteWidth = 1.5f, SketchWidth = 1.8f, DatumWidth = 1.2f, HighlightBonus = 1.4f;
    public float VertexSize = 6f, VertexHighlightSize = 9f, OriginSize = 9f;
    public bool Dark;

    private static Vector4 C(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    public static RenderTheme DarkTheme() => new()
    {
        Dark = true,
        BackgroundTop = C(41, 46, 54), BackgroundBottom = C(18, 20, 25),
        Body = C(184, 190, 200), Edge = C(20, 22, 26), EdgeWire = C(208, 214, 224), SketchRefEdge = C(150, 160, 176, 0.7f), DimDefault = C(150, 156, 168), DimLocked = C(96, 200, 255), DimReference = C(190, 140, 255), Handle = C(255, 176, 32), HandleAlt = C(96, 200, 255), EdgeHidden = C(140, 147, 160, 0.7f), Silhouette = C(20, 22, 26), Vertex = C(20, 22, 26),
        Hover = C(255, 176, 32), Select = C(47, 140, 255), Error = C(239, 68, 68),
        DatumPlane = C(62, 123, 250, 0.09f), DatumBorder = C(110, 160, 255, 0.85f), DatumAxis = C(110, 160, 255, 0.85f),
        Origin = C(255, 196, 61), DatumPoint = C(110, 160, 255),
        SketchCurve = C(255, 214, 74), SketchConstruction = C(140, 170, 220, 0.9f), SketchCenterline = C(255, 255, 255, 0.95f),
        SketchReference = C(96, 205, 255, 0.9f), SketchPoint = C(255, 214, 74),
        PreviewAdd = C(255, 122, 26, 0.4f), PreviewAddEdge = C(255, 122, 26, 0.95f), PreviewCut = C(239, 68, 68, 0.35f), PreviewCutEdge = C(239, 68, 68, 0.95f),
    };

    public static RenderTheme LightTheme() => new()
    {
        Dark = false,
        BackgroundTop = C(236, 239, 243), BackgroundBottom = C(206, 212, 221),
        Body = C(196, 202, 212), Edge = C(28, 30, 34), EdgeWire = C(36, 40, 48), SketchRefEdge = C(90, 98, 112, 0.75f), DimDefault = C(110, 116, 128), DimLocked = C(0, 110, 200), DimReference = C(130, 70, 200), Handle = C(220, 120, 0), HandleAlt = C(0, 110, 200), EdgeHidden = C(120, 126, 138, 0.7f), Silhouette = C(28, 30, 34), Vertex = C(28, 30, 34),
        Hover = C(230, 145, 0), Select = C(23, 106, 230), Error = C(209, 36, 47),
        DatumPlane = C(37, 86, 214, 0.08f), DatumBorder = C(37, 86, 214, 0.85f), DatumAxis = C(37, 86, 214, 0.85f),
        Origin = C(214, 146, 0), DatumPoint = C(37, 86, 214),
        SketchCurve = C(31, 95, 214), SketchConstruction = C(90, 115, 160, 0.9f), SketchCenterline = C(60, 64, 72, 0.95f),
        SketchReference = C(0, 140, 200, 0.9f), SketchPoint = C(31, 95, 214),
        PreviewAdd = C(234, 88, 12, 0.35f), PreviewAddEdge = C(234, 88, 12, 0.95f), PreviewCut = C(209, 36, 47, 0.3f), PreviewCutEdge = C(209, 36, 47, 0.95f),
    };
}
