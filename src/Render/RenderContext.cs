using OpenTK.Mathematics;

namespace Parametric3D.Render;

/// <summary>Всё, что нужно любому батчу для одного вызова отрисовки.</summary>
public sealed class RenderContext
{
    public Matrix4 Model = Matrix4.Identity;
    public Matrix4 View = Matrix4.Identity;
    public Matrix4 Proj = Matrix4.Identity;
    public Matrix4 ViewProj => View * Proj;
    public Matrix4 MVP => Model * View * Proj;

    public Vector2 Viewport = new(1024, 768);
    public Vector3 CamPos = Vector3.UnitZ * 10;

    /// <summary>Сколько экранных пикселей в одной модельной единице (для штриховки).</summary>
    public float WorldToPx = 1.0f;

    public float Opacity = 1.0f;
    /// <summary>
    /// Сдвиг линий к камере в координатах отсечения. Основную работу делает
    /// glPolygonOffset на гранях (см. RenderScene.Draw); здесь остаётся лишь
    /// крошечная добавка против дрожания на совпадающих поверхностях.
    /// </summary>
    public float DepthBias = 0.000002f;
    public float LineWidthScale = 1.0f;

    public RenderStyles Styles = null!;

    public Matrix3 NormalMatrix
    {
        get
        {
            Matrix4 m = Model;
            m.Invert();
            m.Transpose();
            return new Matrix3(m);
        }
    }
}
