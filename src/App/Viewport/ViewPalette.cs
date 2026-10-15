using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.App.Viewport;

/// <summary>
/// ПАЛИТРА ВИДА (UI_VIEW_PALETTE.md): цвета сцены для тёмной и светлой темы — те же смысловые цвета, что в оболочке
/// (выбор — синий, наведение — янтарный, ошибка — красный, оси X/Y/Z). Пишутся в настройки документа, из которых
/// рендер берёт стили (RenderStyles.Reload).
/// </summary>
public static class ViewPalette
{
    private static Vector4 C(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    public static void Apply(Settings s, bool dark)
    {
        if (dark)
        {
            s.BackgroundTop = C(41, 46, 54); s.BackgroundBottom = C(18, 20, 25);
            s.HoverColor = C(255, 176, 32); s.SelectColor = C(47, 140, 255);
            s.EdgeColor = C(20, 22, 26); s.HiddenEdgeColor = C(140, 147, 160, 0.7f); s.VertexColor = C(20, 22, 26);
            s.DatumPlaneColor = C(62, 123, 250, 0.16f); s.DatumBorderColor = C(110, 160, 255, 0.85f);
            s.DatumAxisColor = C(110, 160, 255, 0.85f); s.OriginColor = C(255, 196, 61);
            s.SketchObjectColor = C(255, 214, 74); s.SketchPointColor = C(255, 214, 74);
            s.SketchConstructionColor = C(140, 170, 220, 0.9f); s.SketchCenterlineColor = C(255, 255, 255, 0.95f);
            s.SketchReferenceColor = C(96, 205, 255, 0.9f); s.SketchFillColor = C(110, 120, 255, 0.18f);
            s.SketchCenterColor = C(255, 128, 200);
            s.GridMinorColor = C(255, 255, 255, 0.12f); s.GridMajorColor = C(255, 255, 255, 0.22f);
            s.GridAxisXColor = C(229, 72, 77, 0.85f); s.GridAxisYColor = C(48, 164, 108, 0.85f);
            s.DimensionColor = C(74, 222, 128); s.DimensionLockedColor = C(74, 222, 128); s.DimensionReferenceColor = C(190, 140, 255);
            s.ConstraintGlyphColor = C(140, 215, 255, 0.95f); s.ConflictColor = C(239, 68, 68); s.SketchErrorColor = C(239, 68, 68);
        }
        else
        {
            s.BackgroundTop = C(236, 239, 243); s.BackgroundBottom = C(206, 212, 221);
            s.HoverColor = C(230, 145, 0); s.SelectColor = C(23, 106, 230);
            s.EdgeColor = C(28, 30, 34); s.HiddenEdgeColor = C(120, 126, 138, 0.7f); s.VertexColor = C(28, 30, 34);
            s.DatumPlaneColor = C(37, 86, 214, 0.14f); s.DatumBorderColor = C(37, 86, 214, 0.85f);
            s.DatumAxisColor = C(37, 86, 214, 0.85f); s.OriginColor = C(214, 146, 0);
            s.SketchObjectColor = C(31, 95, 214); s.SketchPointColor = C(31, 95, 214);
            s.SketchConstructionColor = C(90, 115, 160, 0.9f); s.SketchCenterlineColor = C(60, 64, 72, 0.95f);
            s.SketchReferenceColor = C(0, 140, 200, 0.9f); s.SketchFillColor = C(31, 95, 214, 0.14f);
            s.SketchCenterColor = C(200, 40, 140);
            s.GridMinorColor = C(0, 0, 0, 0.10f); s.GridMajorColor = C(0, 0, 0, 0.18f);
            s.GridAxisXColor = C(205, 43, 49, 0.85f); s.GridAxisYColor = C(24, 121, 78, 0.85f);
            s.DimensionColor = C(22, 140, 70); s.DimensionLockedColor = C(22, 140, 70); s.DimensionReferenceColor = C(120, 70, 200);
            s.ConstraintGlyphColor = C(40, 120, 200, 0.95f); s.ConflictColor = C(209, 36, 47); s.SketchErrorColor = C(209, 36, 47);
        }
    }
}
