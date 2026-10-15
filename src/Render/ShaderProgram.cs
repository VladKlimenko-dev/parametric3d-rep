using Parametric3D.Render.Gl;
using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Render;

/// <summary>
/// Обёртка над программой шейдера с кэшем локаций униформов.
///
/// Отличие от старой версии: исходники шейдеров — строки в сборке
/// (Shaders.cs), а не файлы по абсолютному пути вида
/// "C:\Users\...\Shaders\". Приложение больше не зависит от машины сборки.
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    public int Handle { get; private set; }
    private readonly Dictionary<string, int> _loc = new();
    public readonly string Name;

    public ShaderProgram(string name, string vertexSrc, string fragmentSrc, string? geometrySrc = null)
    {
        Name = name;
        int vs = Compile(ShaderType.VertexShader, vertexSrc, name + ".vert");
        int fs = Compile(ShaderType.FragmentShader, fragmentSrc, name + ".frag");
        int gs = geometrySrc == null ? 0 : Compile(ShaderType.GeometryShader, geometrySrc, name + ".geom");

        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vs);
        if (gs != 0) GL.AttachShader(Handle, gs);
        GL.AttachShader(Handle, fs);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            throw new P3DException($"Failed to link program '{name}':\n{GL.GetProgramInfoLog(Handle)}");

        GL.DetachShader(Handle, vs); GL.DeleteShader(vs);
        if (gs != 0) { GL.DetachShader(Handle, gs); GL.DeleteShader(gs); }
        GL.DetachShader(Handle, fs); GL.DeleteShader(fs);
    }

    /// <summary>
    /// Заменяет любой символ вне ASCII на '?'.
    ///
    /// Строка уходит в драйвер как UTF-8, и один русский символ занимает
    /// два байта. Если длина считается в символах, а данные — в байтах,
    /// исходник обрезается с конца ровно на число «лишних» байт: драйвер
    /// сообщает про «pre-mature EOF» и про обрубленный идентификатор.
    /// Отладить такое тяжело, потому что видимый текст шейдера корректен,
    /// а ломается только то, что стоит в конце файла.
    ///
    /// Все шейдеры в Shaders.cs и так написаны на ASCII. Эта замена —
    /// страховка: если кто-то допишет комментарий по-русски, шейдер
    /// скомпилируется с «?????» в комментарии, а не рухнет.
    /// </summary>
    private static string ToAscii(string src)
    {
        bool clean = true;
        foreach (char c in src)
            if (c > '\u007F') { clean = false; break; }
        if (clean) return src;

        var sb = new System.Text.StringBuilder(src.Length);
        foreach (char c in src) sb.Append(c > '\u007F' ? '?' : c);
        return sb.ToString();
    }

    /// <summary>
    /// Шейдеры написаны для настольного OpenGL 3.3 («#version 330 core») в подмножестве, общем с OpenGL ES 3.0. В контексте
    /// ES (ANGLE под Windows, EGL) заголовок меняется на «#version 300 es» с точностью highp — остальной текст тот же.
    /// </summary>
    internal static string ForContext(string src)
    {
        if (!GL.IsEs) return src;
        return src.Replace("#version 330 core", "#version 300 es\nprecision highp float;\nprecision highp int;");
    }

    private static int Compile(ShaderType type, string src, string label)
    {
        int h = GL.CreateShader(type);
        GL.ShaderSource(h, ForContext(ToAscii(src)));
        GL.CompileShader(h);
        GL.GetShader(h, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new P3DException($"Failed to compile shader '{label}':\n{GL.GetShaderInfoLog(h)}");
        return h;
    }

    public void Use() => GL.UseProgram(Handle);

    public int Loc(string name)
    {
        if (_loc.TryGetValue(name, out int l)) return l;
        l = GL.GetUniformLocation(Handle, name);
        _loc[name] = l;
        return l;
    }

    public void Set(string n, int v) { int l = Loc(n); if (l >= 0) GL.Uniform1(l, v); }
    public void Set(string n, float v) { int l = Loc(n); if (l >= 0) GL.Uniform1(l, v); }
    public void Set(string n, Vector2 v) { int l = Loc(n); if (l >= 0) GL.Uniform2(l, v); }
    public void Set(string n, Vector3 v) { int l = Loc(n); if (l >= 0) GL.Uniform3(l, v); }
    public void Set(string n, Vector4 v) { int l = Loc(n); if (l >= 0) GL.Uniform4(l, v); }

    public void Set(string n, Matrix4 m)
    {
        int l = Loc(n);
        if (l >= 0) GL.UniformMatrix4(l, false, ref m);
    }

    public void SetArray(string n, float[] v) { int l = Loc(n); if (l >= 0) GL.Uniform1(l, v.Length, v); }
    public void SetArray(string n, int[] v) { int l = Loc(n); if (l >= 0) GL.Uniform1(l, v.Length, v); }
    /// <summary>Массив vec4 передаётся плоским float[] длиной count*4.</summary>
    public void SetArray4(string n, float[] v) { int l = Loc(n); if (l >= 0) GL.Uniform4(l, v.Length / 4, v); }

    public void Dispose()
    {
        // Номер забывается до удаления: в мёртвом контексте удаление бросает,
        // и программа с прежним номером досталась бы новому контексту.
        int h = Handle;
        Handle = 0;
        if (h != 0)
        {
            try { GL.DeleteProgram(h); }
            catch { /* контекст мог умереть раньше нас */ }
        }
    }
}
