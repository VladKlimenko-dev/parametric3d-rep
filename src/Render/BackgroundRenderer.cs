using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Parametric3D.Render;

/// <summary>
/// Градиентный фон. Рисуется полноэкранным треугольником без вершинного
/// буфера (позиции считаются из gl_VertexID), поэтому не требует ни VBO,
/// ни очистки цвета отдельным вызовом.
/// </summary>
public sealed class BackgroundRenderer : IDisposable
{
    private static ShaderProgram? _shader;
    private static int _vao = -1;

    public Vector4 Top = new(0.196f, 0.220f, 0.259f, 1f);
    public Vector4 Bottom = new(0.086f, 0.098f, 0.118f, 1f);

    public void Draw()
    {
        _shader ??= new ShaderProgram("background", Shaders.FullscreenVert, Shaders.BackgroundFrag);
        if (_vao < 0) _vao = GL.GenVertexArray();

        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.Blend);

        _shader.Use();
        _shader.Set("uTop", Top);
        _shader.Set("uBottom", Bottom);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        GL.UseProgram(0);

        // Восстанавливаем ВСЁ, что выключали.
        //
        // Раньше здесь возвращались только глубина и маска, а смешивание
        // оставалось выключенным. Следующей рисовалась сетка — и её
        // альфа-канал просто игнорировался: линии выходили одинаково
        // яркими независимо от прозрачности и одинаково жирными, потому
        // что сглаживание тоже работает через альфу. Тела и эскиз этого
        // не замечали: RenderScene.Draw включает смешивание сам.
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    public void Dispose() { }

    public static void DisposeShared()
    {
        GpuRelease.Shader(ref _shader);
        GpuRelease.VertexArray(ref _vao);
    }
}
