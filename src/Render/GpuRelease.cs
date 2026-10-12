using OpenTK.Graphics.OpenGL4;

namespace Parametric3D.Render;

/// <summary>
/// ОТДАТЬ ОБЪЕКТ OPENGL — СНАЧАЛА ЗАБЫТЬ НОМЕР, ПОТОМ УДАЛЯТЬ.
///
/// Везде стояло «GL.Delete…(id); id = -1;». Когда контекст уже мёртв
/// (сброс драйвера, пересоздание окна), удаление бросает исключение —
/// и номер остаётся. Новый контекст получает объект, которого в нём нет:
/// буфер, программу шейдеров. Ничего не рисуется, и ошибки при этом нет.
///
/// Здесь номер сбрасывается ДО удаления, а исключение удаления гасится:
/// освобождать в мёртвом контексте нечего, а объект обязан быть создан
/// заново в новом.
/// </summary>
internal static class GpuRelease
{
    public static void Buffer(ref int id)       { int x = id; id = -1; if (x >= 0) Try(() => GL.DeleteBuffer(x)); }
    public static void VertexArray(ref int id)  { int x = id; id = -1; if (x >= 0) Try(() => GL.DeleteVertexArray(x)); }
    public static void Renderbuffer(ref int id) { int x = id; id = -1; if (x >= 0) Try(() => GL.DeleteRenderbuffer(x)); }
    public static void Framebuffer(ref int id)  { int x = id; id = -1; if (x >= 0) Try(() => GL.DeleteFramebuffer(x)); }

    /// <summary>Общая программа шейдеров: поле обнуляется до удаления.</summary>
    public static void Shader(ref ShaderProgram? shader)
    {
        var s = shader;
        shader = null;
        if (s != null) Try(s.Dispose);
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch { /* контекст мог умереть раньше нас */ }
    }
}
