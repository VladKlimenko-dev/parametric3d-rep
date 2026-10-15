using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Mathematics;

namespace Parametric3D.Render.Gl;

// Собственные привязки OpenGL для рендера.
//
// Прежде рендер брал их из OpenTK.Graphics (пакет OpenTK) и жил в окне
// OpenTK.GLControl под WinForms. Новое приложение (Avalonia) само создаёт
// контекст — настольный OpenGL (WGL, GLX) или OpenGL ES 3 (ANGLE, EGL) — и даёт
// только адреса функций. Рендеру нужно около шестидесяти функций; их имена и
// перечисления здесь те же, что в OpenTK, чтобы код рендера не переписывать.
// Привязки грузятся один раз на контекст: GL.Load(адрес по имени, ES ли это).

public enum BufferTarget : uint { ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893 }
public enum BufferUsageHint : uint { StaticDraw = 0x88E4, DynamicDraw = 0x88E8, StreamDraw = 0x88E0 }
public enum VertexAttribPointerType : uint { Float = 0x1406 }
public enum VertexAttribIntegerType : uint { Int = 0x1404, UnsignedInt = 0x1405 }
public enum PrimitiveType : uint { Points = 0, Lines = 1, LineStrip = 3, Triangles = 4, TriangleStrip = 5 }
public enum DrawElementsType : uint { UnsignedInt = 0x1405 }
public enum ShaderType : uint { FragmentShader = 0x8B30, VertexShader = 0x8B31, GeometryShader = 0x8DD9 }
public enum ShaderParameter : uint { CompileStatus = 0x8B81, InfoLogLength = 0x8B84 }
public enum GetProgramParameterName : uint { LinkStatus = 0x8B82, InfoLogLength = 0x8B84 }
public enum EnableCap : uint
{
    CullFace = 0x0B44, DepthTest = 0x0B71, Blend = 0x0BE2, ScissorTest = 0x0C11,
    PolygonOffsetFill = 0x8037, Multisample = 0x809D,
}
public enum BlendingFactor : uint { Zero = 0, One = 1, SrcAlpha = 0x0302, OneMinusSrcAlpha = 0x0303 }
public enum DepthFunction : uint { Never = 0x0200, Less = 0x0201, Equal = 0x0202, Lequal = 0x0203, Greater = 0x0204, Always = 0x0207 }
public enum FramebufferTarget : uint { Framebuffer = 0x8D40, ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9 }
public enum RenderbufferTarget : uint { Renderbuffer = 0x8D41 }
public enum RenderbufferStorage : uint { Rgba8 = 0x8058, Depth24Stencil8 = 0x88F0 }
public enum FramebufferAttachment : uint { ColorAttachment0 = 0x8CE0, DepthStencilAttachment = 0x821A }
public enum FramebufferErrorCode : uint { FramebufferComplete = 0x8CD5 }
public enum DrawBufferMode : uint { None = 0, Back = 0x0405, ColorAttachment0 = 0x8CE0 }
public enum ReadBufferMode : uint { Back = 0x0405, ColorAttachment0 = 0x8CE0 }
public enum CullFaceMode : uint { Front = 0x0404, Back = 0x0405 }
public enum TriangleFace : uint { Front = 0x0404, Back = 0x0405 }
public enum BlitFramebufferFilter : uint { Nearest = 0x2600, Linear = 0x2601 }
public enum StringName : uint { Vendor = 0x1F00, Renderer = 0x1F01, Version = 0x1F02, ShadingLanguageVersion = 0x8B8C }
public enum GetPName : uint { MaxSamples = 0x8D57, FramebufferBinding = 0x8CA6, ReadFramebufferBinding = 0x8CAA, Viewport = 0x0BA2 }
public enum ErrorCode : uint { NoError = 0 }
[Flags] public enum ClearBufferMask : uint { DepthBufferBit = 0x100, StencilBufferBit = 0x400, ColorBufferBit = 0x4000 }

public static unsafe class GL
{
    /// <summary>Контекст — OpenGL ES (ANGLE, EGL): шейдеры переводятся на «#version 300 es».</summary>
    public static bool IsEs { get; private set; }
    public static bool Loaded { get; private set; }

    private static delegate* unmanaged<uint, uint, void> _bindBuffer, _bindFramebuffer, _bindRenderbuffer, _attachShader, _detachShader, _vertexAttribDivisor, _blendFunc;
    private static delegate* unmanaged<uint, void> _enableVertexAttribArray, _bindVertexArray, _enable, _disable, _useProgram, _depthFunc, _compileShader, _linkProgram, _deleteShader, _deleteProgram, _clear, _readBuffer, _cullFace;
    private static delegate* unmanaged<byte, void> _depthMask;
    private static delegate* unmanaged<uint, int, uint, byte, int, IntPtr, void> _vertexAttribPointer;
    private static delegate* unmanaged<uint, int, uint, int, IntPtr, void> _vertexAttribIPointer;
    private static delegate* unmanaged<uint, IntPtr, void*, uint, void> _bufferData;
    private static delegate* unmanaged<uint, IntPtr, IntPtr, void*, void> _bufferSubData;
    private static delegate* unmanaged<int, uint*, void> _genBuffers, _genVertexArrays, _genRenderbuffers, _genFramebuffers, _deleteBuffers, _deleteVertexArrays, _deleteRenderbuffers, _deleteFramebuffers, _drawBuffers;
    private static delegate* unmanaged<int, int, int, int, void> _viewport;
    private static delegate* unmanaged<int, int, void> _uniform1i;
    private static delegate* unmanaged<int, float, void> _uniform1f;
    private static delegate* unmanaged<int, float, float, void> _uniform2f;
    private static delegate* unmanaged<int, float, float, float, void> _uniform3f;
    private static delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
    private static delegate* unmanaged<int, int, float*, void> _uniform1fv, _uniform4fv;
    private static delegate* unmanaged<int, int, int*, void> _uniform1iv;
    private static delegate* unmanaged<int, int, byte, float*, void> _uniformMatrix4fv, _uniformMatrix3fv;
    private static delegate* unmanaged<uint, byte*> _getString;
    private static delegate* unmanaged<uint, uint> _createShader, _checkFramebufferStatus;
    private static delegate* unmanaged<uint> _createProgram, _getError;
    private static delegate* unmanaged<uint, int, byte**, int*, void> _shaderSource;
    private static delegate* unmanaged<uint, uint, int*, void> _getShaderiv, _getProgramiv;
    private static delegate* unmanaged<uint, int, int*, byte*, void> _getShaderInfoLog, _getProgramInfoLog;
    private static delegate* unmanaged<uint, byte*, int> _getUniformLocation;
    private static delegate* unmanaged<uint, int, uint, int, int, void> _renderbufferStorageMultisample;
    private static delegate* unmanaged<uint, uint, int, int, void> _renderbufferStorage;
    private static delegate* unmanaged<uint, uint, uint, uint, void> _framebufferRenderbuffer;
    private static delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void> _blitFramebuffer;
    private static delegate* unmanaged<uint, int, int, int, void> _drawArraysInstanced;
    private static delegate* unmanaged<uint, int, int, void> _drawArrays;
    private static delegate* unmanaged<uint, int, uint, IntPtr, void> _drawElements;
    private static delegate* unmanaged<byte, byte, byte, byte, void> _colorMask;
    private static delegate* unmanaged<float, float, float, float, void> _clearColor;
    private static delegate* unmanaged<double, void> _clearDepth;
    private static delegate* unmanaged<float, void> _clearDepthf;
    private static delegate* unmanaged<float, float, void> _polygonOffset;
    private static delegate* unmanaged<uint, int*, void> _getIntegerv;
    private static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> _readPixels;
    private static delegate* unmanaged<int, int, int, int, void> _scissor;

    /// <summary>Загрузить адреса функций текущего контекста.</summary>
    public static void Load(Func<string, IntPtr> proc, bool isEs)
    {
        IsEs = isEs;
        IntPtr P(string n)
        {
            var p = proc(n);
            if (p == IntPtr.Zero) throw new InvalidOperationException($"OpenGL function {n} is not available");
            return p;
        }
        IntPtr Opt(string n) => proc(n);

        _bindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        _bindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        _bindRenderbuffer = (delegate* unmanaged<uint, uint, void>)P("glBindRenderbuffer");
        _attachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        _detachShader = (delegate* unmanaged<uint, uint, void>)P("glDetachShader");
        _vertexAttribDivisor = (delegate* unmanaged<uint, uint, void>)P("glVertexAttribDivisor");
        _blendFunc = (delegate* unmanaged<uint, uint, void>)P("glBlendFunc");
        _enableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        _bindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        _enable = (delegate* unmanaged<uint, void>)P("glEnable");
        _disable = (delegate* unmanaged<uint, void>)P("glDisable");
        _useProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        _depthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
        _compileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        _linkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        _deleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        _deleteProgram = (delegate* unmanaged<uint, void>)P("glDeleteProgram");
        _clear = (delegate* unmanaged<uint, void>)P("glClear");
        _readBuffer = (delegate* unmanaged<uint, void>)P("glReadBuffer");
        _cullFace = (delegate* unmanaged<uint, void>)P("glCullFace");
        _depthMask = (delegate* unmanaged<byte, void>)P("glDepthMask");
        _vertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, IntPtr, void>)P("glVertexAttribPointer");
        _vertexAttribIPointer = (delegate* unmanaged<uint, int, uint, int, IntPtr, void>)P("glVertexAttribIPointer");
        _bufferData = (delegate* unmanaged<uint, IntPtr, void*, uint, void>)P("glBufferData");
        _bufferSubData = (delegate* unmanaged<uint, IntPtr, IntPtr, void*, void>)P("glBufferSubData");
        _genBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        _genVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        _genRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glGenRenderbuffers");
        _genFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        _deleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
        _deleteVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glDeleteVertexArrays");
        _deleteRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteRenderbuffers");
        _deleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        _drawBuffers = (delegate* unmanaged<int, uint*, void>)P("glDrawBuffers");
        _viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        _scissor = (delegate* unmanaged<int, int, int, int, void>)P("glScissor");
        _uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        _uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        _uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
        _uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
        _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        _uniform1fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform1fv");
        _uniform4fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform4fv");
        _uniform1iv = (delegate* unmanaged<int, int, int*, void>)P("glUniform1iv");
        _uniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        _uniformMatrix3fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix3fv");
        _getString = (delegate* unmanaged<uint, byte*>)P("glGetString");
        _createShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        _checkFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        _createProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        _getError = (delegate* unmanaged<uint>)P("glGetError");
        _shaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        _getShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        _getProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        _getShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        _getProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        _getUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        _renderbufferStorageMultisample = (delegate* unmanaged<uint, int, uint, int, int, void>)P("glRenderbufferStorageMultisample");
        _renderbufferStorage = (delegate* unmanaged<uint, uint, int, int, void>)P("glRenderbufferStorage");
        _framebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glFramebufferRenderbuffer");
        _blitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void>)P("glBlitFramebuffer");
        _drawArraysInstanced = (delegate* unmanaged<uint, int, int, int, void>)P("glDrawArraysInstanced");
        _drawArrays = (delegate* unmanaged<uint, int, int, void>)P("glDrawArrays");
        _drawElements = (delegate* unmanaged<uint, int, uint, IntPtr, void>)P("glDrawElements");
        _colorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)P("glColorMask");
        _clearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        _clearDepth = (delegate* unmanaged<double, void>)Opt("glClearDepth");
        _clearDepthf = (delegate* unmanaged<float, void>)Opt("glClearDepthf");
        _polygonOffset = (delegate* unmanaged<float, float, void>)P("glPolygonOffset");
        _getIntegerv = (delegate* unmanaged<uint, int*, void>)P("glGetIntegerv");
        _readPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
        Loaded = true;
    }

    // ------------------------------------------------------------------ буферы
    public static int GenBuffer() { uint h; _genBuffers(1, &h); return (int)h; }
    public static int GenVertexArray() { uint h; _genVertexArrays(1, &h); return (int)h; }
    public static int GenRenderbuffer() { uint h; _genRenderbuffers(1, &h); return (int)h; }
    public static int GenFramebuffer() { uint h; _genFramebuffers(1, &h); return (int)h; }
    public static void DeleteBuffer(int h) { uint x = (uint)h; _deleteBuffers(1, &x); }
    public static void DeleteVertexArray(int h) { uint x = (uint)h; _deleteVertexArrays(1, &x); }
    public static void DeleteRenderbuffer(int h) { uint x = (uint)h; _deleteRenderbuffers(1, &x); }
    public static void DeleteFramebuffer(int h) { uint x = (uint)h; _deleteFramebuffers(1, &x); }

    public static void BindBuffer(BufferTarget t, int h) => _bindBuffer((uint)t, (uint)h);
    public static void BindVertexArray(int h) => _bindVertexArray((uint)h);
    public static void BindFramebuffer(FramebufferTarget t, int h) => _bindFramebuffer((uint)t, (uint)h);
    public static void BindRenderbuffer(RenderbufferTarget t, int h) => _bindRenderbuffer((uint)t, (uint)h);

    public static void BufferData(BufferTarget t, int size, IntPtr data, BufferUsageHint u) => _bufferData((uint)t, size, (void*)data, (uint)u);
    public static void BufferData<T>(BufferTarget t, int size, T[] data, BufferUsageHint u) where T : unmanaged
    {
        fixed (T* p = data) _bufferData((uint)t, size, p, (uint)u);
    }
    public static void BufferSubData(BufferTarget t, IntPtr offset, int size, IntPtr data) => _bufferSubData((uint)t, offset, size, (void*)data);
    public static void BufferSubData<T>(BufferTarget t, IntPtr offset, int size, T[] data) where T : unmanaged
    {
        fixed (T* p = data) _bufferSubData((uint)t, offset, size, p);
    }
    public static void BufferSubData<T>(BufferTarget t, IntPtr offset, int size, ref T data) where T : unmanaged
    {
        fixed (T* p = &data) _bufferSubData((uint)t, offset, size, p);
    }

    public static void EnableVertexAttribArray(int i) => _enableVertexAttribArray((uint)i);
    public static void VertexAttribDivisor(int i, int d) => _vertexAttribDivisor((uint)i, (uint)d);
    public static void VertexAttribPointer(int i, int size, VertexAttribPointerType t, bool norm, int stride, int offset)
        => _vertexAttribPointer((uint)i, size, (uint)t, (byte)(norm ? 1 : 0), stride, offset);
    public static void VertexAttribPointer(int i, int size, VertexAttribPointerType t, bool norm, int stride, IntPtr offset)
        => _vertexAttribPointer((uint)i, size, (uint)t, (byte)(norm ? 1 : 0), stride, offset);
    public static void VertexAttribIPointer(int i, int size, VertexAttribIntegerType t, int stride, IntPtr offset)
        => _vertexAttribIPointer((uint)i, size, (uint)t, stride, offset);

    // ------------------------------------------------------------------ состояние
    public static void Enable(EnableCap c) { if (c == EnableCap.Multisample && IsEs) return; _enable((uint)c); }
    public static void Disable(EnableCap c) { if (c == EnableCap.Multisample && IsEs) return; _disable((uint)c); }
    public static void DepthMask(bool f) => _depthMask((byte)(f ? 1 : 0));
    public static void DepthFunc(DepthFunction f) => _depthFunc((uint)f);
    public static void BlendFunc(BlendingFactor s, BlendingFactor d) => _blendFunc((uint)s, (uint)d);
    public static void ColorMask(bool r, bool g, bool b, bool a) => _colorMask((byte)(r ? 1 : 0), (byte)(g ? 1 : 0), (byte)(b ? 1 : 0), (byte)(a ? 1 : 0));
    public static void CullFace(CullFaceMode m) => _cullFace((uint)m);
    public static void CullFace(TriangleFace m) => _cullFace((uint)m);
    public static void PolygonOffset(float factor, float units) => _polygonOffset(factor, units);
    public static void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
    public static void Scissor(int x, int y, int w, int h) => _scissor(x, y, w, h);
    public static void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
    public static void ClearDepth(double d)
    {
        if (_clearDepthf != null) _clearDepthf((float)d);
        else _clearDepth(d);
    }
    public static void Clear(ClearBufferMask m) => _clear((uint)m);
    public static ErrorCode GetError() => (ErrorCode)_getError();
    public static void GetInteger(GetPName p, out int v) { int x; _getIntegerv((uint)p, &x); v = x; }
    public static void GetInteger(GetPName p, int[] v) { fixed (int* x = v) _getIntegerv((uint)p, x); }
    public static string GetString(StringName n)
    {
        var p = _getString((uint)n);
        return p == null ? "" : Marshal.PtrToStringAnsi((IntPtr)p) ?? "";
    }

    // ------------------------------------------------------------------ кадровые буферы
    public static void RenderbufferStorage(RenderbufferTarget t, RenderbufferStorage f, int w, int h) => _renderbufferStorage((uint)t, (uint)f, w, h);
    public static void RenderbufferStorageMultisample(RenderbufferTarget t, int samples, RenderbufferStorage f, int w, int h)
        => _renderbufferStorageMultisample((uint)t, samples, (uint)f, w, h);
    public static void FramebufferRenderbuffer(FramebufferTarget t, FramebufferAttachment a, RenderbufferTarget rt, int h)
        => _framebufferRenderbuffer((uint)t, (uint)a, (uint)rt, (uint)h);
    public static FramebufferErrorCode CheckFramebufferStatus(FramebufferTarget t) => (FramebufferErrorCode)_checkFramebufferStatus((uint)t);
    public static void DrawBuffer(DrawBufferMode m) { uint x = (uint)m; _drawBuffers(1, &x); }
    public static void ReadBuffer(ReadBufferMode m) => _readBuffer((uint)m);
    public static void BlitFramebuffer(int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, ClearBufferMask mask, BlitFramebufferFilter f)
        => _blitFramebuffer(sx0, sy0, sx1, sy1, dx0, dy0, dx1, dy1, (uint)mask, (uint)f);
    /// <summary>Пиксели RGBA8 (снизу вверх, как в OpenGL).</summary>
    public static void ReadPixelsRgba(int x, int y, int w, int h, byte[] dst)
    {
        fixed (byte* p = dst) _readPixels(x, y, w, h, 0x1908, 0x1401, p);
    }

    // ------------------------------------------------------------------ рисование
    public static void DrawArrays(PrimitiveType p, int first, int count) => _drawArrays((uint)p, first, count);
    public static void DrawArraysInstanced(PrimitiveType p, int first, int count, int inst) => _drawArraysInstanced((uint)p, first, count, inst);
    public static void DrawElements(PrimitiveType p, int count, DrawElementsType t, int offset) => _drawElements((uint)p, count, (uint)t, offset);

    // ------------------------------------------------------------------ шейдеры
    public static int CreateShader(ShaderType t) => (int)_createShader((uint)t);
    public static int CreateProgram() => (int)_createProgram();
    public static void ShaderSource(int h, string src)
    {
        var bytes = Encoding.ASCII.GetBytes(src);
        fixed (byte* b = bytes)
        {
            byte* s = b; int len = bytes.Length;
            _shaderSource((uint)h, 1, &s, &len);
        }
    }
    public static void CompileShader(int h) => _compileShader((uint)h);
    public static void AttachShader(int p, int s) => _attachShader((uint)p, (uint)s);
    public static void DetachShader(int p, int s) => _detachShader((uint)p, (uint)s);
    public static void DeleteShader(int h) => _deleteShader((uint)h);
    public static void LinkProgram(int h) => _linkProgram((uint)h);
    public static void DeleteProgram(int h) => _deleteProgram((uint)h);
    public static void UseProgram(int h) => _useProgram((uint)h);
    public static void GetShader(int h, ShaderParameter p, out int v) { int x; _getShaderiv((uint)h, (uint)p, &x); v = x; }
    public static void GetProgram(int h, GetProgramParameterName p, out int v) { int x; _getProgramiv((uint)h, (uint)p, &x); v = x; }
    public static string GetShaderInfoLog(int h)
    {
        GetShader(h, ShaderParameter.InfoLogLength, out int n);
        if (n <= 0) return "";
        var buf = new byte[n]; int got;
        fixed (byte* b = buf) _getShaderInfoLog((uint)h, n, &got, b);
        return Encoding.ASCII.GetString(buf, 0, Math.Max(0, got));
    }
    public static string GetProgramInfoLog(int h)
    {
        GetProgram(h, GetProgramParameterName.InfoLogLength, out int n);
        if (n <= 0) return "";
        var buf = new byte[n]; int got;
        fixed (byte* b = buf) _getProgramInfoLog((uint)h, n, &got, b);
        return Encoding.ASCII.GetString(buf, 0, Math.Max(0, got));
    }
    public static int GetUniformLocation(int h, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* b = bytes) return _getUniformLocation((uint)h, b);
    }

    public static void Uniform1(int l, int v) => _uniform1i(l, v);
    public static void Uniform1(int l, float v) => _uniform1f(l, v);
    public static void Uniform2(int l, Vector2 v) => _uniform2f(l, v.X, v.Y);
    public static void Uniform3(int l, Vector3 v) => _uniform3f(l, v.X, v.Y, v.Z);
    public static void Uniform4(int l, Vector4 v) => _uniform4f(l, v.X, v.Y, v.Z, v.W);
    public static void Uniform1(int l, int count, float[] v) { fixed (float* p = v) _uniform1fv(l, count, p); }
    public static void Uniform1(int l, int count, int[] v) { fixed (int* p = v) _uniform1iv(l, count, p); }
    public static void Uniform4(int l, int count, float[] v) { fixed (float* p = v) _uniform4fv(l, count, p); }
    public static void UniformMatrix4(int l, bool transpose, ref Matrix4 m)
    {
        fixed (Matrix4* p = &m) _uniformMatrix4fv(l, 1, (byte)(transpose ? 1 : 0), (float*)p);
    }
    public static void UniformMatrix3(int l, bool transpose, ref Matrix3 m)
    {
        fixed (Matrix3* p = &m) _uniformMatrix3fv(l, 1, (byte)(transpose ? 1 : 0), (float*)p);
    }
}
