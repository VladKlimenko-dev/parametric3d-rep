using Parametric3D.Render.Gl;
using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Render;

/// <summary>
/// Триэдр X/Y/Z (RGB) в левом нижнем углу вида.
///
/// Рисуется в собственном маленьком вьюпорте с очищенным буфером глубины,
/// поэтому не конфликтует с моделью. Наконечники осей кликабельны — по клику
/// вид разворачивается по соответствующей оси (событие AxisClicked).
/// </summary>
public sealed class CsysGizmo : IDisposable
{
    private readonly LineBatch _lines = new();
    private readonly PointBatch _tips = new();
    private readonly RenderContext _ctx = new();
    private bool _built;

    public int SizePx = 92;
    public int MarginPx = 12;

    public readonly uint IdX = Ids.New();
    public readonly uint IdY = Ids.New();
    public readonly uint IdZ = Ids.New();
    public readonly uint IdXNeg = Ids.New();
    public readonly uint IdYNeg = Ids.New();
    public readonly uint IdZNeg = Ids.New();

    public event Action<ViewOrientation>? AxisClicked;

    private void Build(RenderStyles styles)
    {
        if (_built) return;

        // Стили с фиксированными цветами осей.
        styles.SetLine(LineStyle.Gizmo, new Vector4(1, 1, 1, 1), 2.6f, 0xFFFF, 0);

        _lines.Begin();
        _lines.AddSegment(Vector3.Zero, Vector3.UnitX, LineStyle.GridAxisX, IdX);
        _lines.AddSegment(Vector3.Zero, Vector3.UnitY, LineStyle.GridAxisY, IdY);
        _lines.AddSegment(Vector3.Zero, Vector3.UnitZ, LineStyle.Gizmo, IdZ);
        _lines.End();

        _tips.Begin();
        _tips.Add(Vector3.UnitX, PointStyle.Snap, IdX);
        _tips.Add(Vector3.UnitY, PointStyle.Snap, IdY);
        _tips.Add(Vector3.UnitZ, PointStyle.Snap, IdZ);
        _tips.Add(-Vector3.UnitX, PointStyle.Reference, IdXNeg);
        _tips.Add(-Vector3.UnitY, PointStyle.Reference, IdYNeg);
        _tips.Add(-Vector3.UnitZ, PointStyle.Reference, IdZNeg);
        _tips.End();

        _built = true;
    }

    /// <summary>
    /// Попадание по наконечнику оси, считается на процессоре.
    ///
    /// Гизмо рисуется в собственном маленьком вьюпорте в углу, поэтому
    /// его проекция известна точно: ортогональная камера с окном 2.8
    /// и ориентацией сцены. Проще спроецировать шесть концов осей, чем
    /// читать их из буфера выбора.
    /// </summary>
    /// <param name="mouse">Позиция курсора в координатах контрола (Y вниз).</param>
    /// <param name="controlHeight">Высота контрола в пикселях.</param>
    public bool HitTest(Camera cam, OpenTK.Mathematics.Vector2i mouse, int controlHeight)
    {
        // Прямоугольник гизмо в координатах контрола.
        int left = MarginPx;
        int bottom = controlHeight - MarginPx;
        int top = bottom - SizePx;
        if (mouse.X < left || mouse.X > left + SizePx || mouse.Y < top || mouse.Y > bottom)
            return false;

        Span<(Vector3 Dir, ViewOrientation View)> axes = stackalloc (Vector3, ViewOrientation)[6]
        {
            (Vector3.UnitX, ViewOrientation.Right),  (-Vector3.UnitX, ViewOrientation.Left),
            (Vector3.UnitY, ViewOrientation.Top),    (-Vector3.UnitY, ViewOrientation.Bottom),
            (Vector3.UnitZ, ViewOrientation.Front),  (-Vector3.UnitZ, ViewOrientation.Back)
        };

        const float half = 1.4f;      // половина окна ортогональной проекции гизмо
        float best = 12f;             // допуск захвата, пиксели
        ViewOrientation? hit = null;
        float bestDepth = float.MinValue;

        foreach (var (dir, view) in axes)
        {
            Vector3 p = Vector3.Transform(dir, cam.State.Rotation);

            float sx = left + (p.X / (half * 2f) + 0.5f) * SizePx;
            float sy = bottom - (p.Y / (half * 2f) + 0.5f) * SizePx;

            float d = MathF.Sqrt((sx - mouse.X) * (sx - mouse.X) + (sy - mouse.Y) * (sy - mouse.Y));
            if (d > best) continue;

            // При совпадении по экрану выигрывает ось, направленная к зрителю.
            if (hit != null && p.Z <= bestDepth) continue;
            hit = view;
            bestDepth = p.Z;
        }

        if (hit == null) return false;
        AxisClicked?.Invoke(hit.Value);
        return true;
    }


    /// <param name="scale">Множитель разрешения кадрового буфера (суперсэмплинг).</param>
    public void Draw(Camera cam, RenderStyles styles, int viewportW, int viewportH, int scale)
    {
        Build(styles);

        int s = SizePx * scale;
        int m = MarginPx * scale;
        GL.Viewport(m, m, s, s);
        GL.Clear(ClearBufferMask.DepthBufferBit);

        _ctx.Styles = styles;
        _ctx.Viewport = new Vector2(s, s);
        _ctx.Model = Matrix4.Identity;
        _ctx.View = Matrix4.CreateFromQuaternion(cam.State.Rotation) * Matrix4.CreateTranslation(0, 0, -4f);
        _ctx.Proj = Matrix4.CreateOrthographic(2.8f, 2.8f, -10f, 10f);
        _ctx.CamPos = Vector3.UnitZ * 4;
        _ctx.WorldToPx = s / 2.8f;
        _ctx.Opacity = 1f;
        _ctx.DepthBias = 0f;
        _ctx.LineWidthScale = scale;

        _lines.Draw(_ctx);
        _tips.Draw(_ctx);

        GL.Viewport(0, 0, viewportW, viewportH);
    }

    /// <summary>
    /// Отдать буферы. Флаг «построено» сбрасывается: иначе в новом контексте
    /// гизмо считало себя готовым и рисовало буферами мёртвого.
    /// </summary>
    public void Dispose() { _built = false; _lines.Dispose(); _tips.Dispose(); }
}
