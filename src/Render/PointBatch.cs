using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Parametric3D.Render;

[StructLayout(LayoutKind.Sequential)]
public struct PointInstance
{
    public Vector3 P;     //  0
    public int Style;     // 12
    public uint PickId;   // 16
    public uint Flags;    // 20
    public const int Size = 24;
}

/// <summary>Пакет точек: вершины тел, точки эскиза, снапы. Одна программа на все.</summary>
public sealed class PointBatch : IDisposable
{
    private static int _quadVbo = -1;
    private static ShaderProgram? _shader;

    private int _vao = -1, _instVbo = -1;
    private PointInstance[] _data = new PointInstance[128];
    private int _count, _capacityOnGpu;
    private bool _dirty;
    private readonly Dictionary<uint, int> _index = new();

    public int Count => _count;

    public static ShaderProgram Shader =>
        _shader ??= new ShaderProgram("point", Shaders.PointVert, Shaders.PointFrag);

    private static int QuadVbo
    {
        get
        {
            if (_quadVbo >= 0) return _quadVbo;
            float[] c = { -1f, -1f, -1f, 1f, 1f, -1f, 1f, 1f };
            _quadVbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, c.Length * sizeof(float), c, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            return _quadVbo;
        }
    }

    public void Begin() { _count = 0; _index.Clear(); }

    public void Add(Vector3 p, PointStyle style, uint pickId, uint flags = 0)
    {
        if (_count == _data.Length) Array.Resize(ref _data, _data.Length * 2);
        if (pickId != 0) _index[pickId] = _count;
        _data[_count++] = new PointInstance { P = p, Style = (int)style, PickId = pickId, Flags = flags };
        _dirty = true;
    }

    public void End()
    {
        EnsureGpu();
        if (_count == 0) { _dirty = false; return; }
        GL.BindBuffer(BufferTarget.ArrayBuffer, _instVbo);
        if (_count > _capacityOnGpu)
        {
            _capacityOnGpu = Math.Max(_count, 128);
            GL.BufferData(BufferTarget.ArrayBuffer, _capacityOnGpu * PointInstance.Size, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        }
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, _count * PointInstance.Size, _data);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        _dirty = false;
    }

    /// <returns>Найден ли id в этом пакете (не «изменился ли»).</returns>
    public bool SetFlags(uint pickId, uint flags)
    {
        if (!_index.TryGetValue(pickId, out int i)) return false;
        if (_data[i].Flags == flags) return true;
        _data[i].Flags = flags;
        if (_instVbo >= 0)
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, _instVbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr)(i * PointInstance.Size),
                             PointInstance.Size, new[] { _data[i] });
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }
        return true;
    }

    public void ClearAllFlags() => SetAllFlags(0);

    public void SetAllFlags(uint flags)
    {
        bool changed = false;
        for (int i = 0; i < _count; i++)
            if (_data[i].Flags != flags) { _data[i].Flags = flags; changed = true; }
        if (changed) End();
    }

    private void EnsureGpu()
    {
        if (_vao >= 0) return;
        _vao = GL.GenVertexArray();
        _instVbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, QuadVbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _instVbo);
        _capacityOnGpu = Math.Max(_data.Length, 128);
        GL.BufferData(BufferTarget.ArrayBuffer, _capacityOnGpu * PointInstance.Size, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        const int S = PointInstance.Size;
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, S, 0);
        GL.VertexAttribDivisor(1, 1);

        GL.EnableVertexAttribArray(2);
        GL.VertexAttribIPointer(2, 1, VertexAttribIntegerType.Int, S, (IntPtr)12);
        GL.VertexAttribDivisor(2, 1);

        GL.EnableVertexAttribArray(3);
        GL.VertexAttribIPointer(3, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)16);
        GL.VertexAttribDivisor(3, 1);

        GL.EnableVertexAttribArray(4);
        GL.VertexAttribIPointer(4, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)20);
        GL.VertexAttribDivisor(4, 1);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    /// <summary>
    /// Рисовать только точки под курсором и выделенные. Вершины тела в виде
    /// «Shaded + Edges» не нужны в покое, но выбирать их можно, и подсвеченная
    /// вершина обязана быть видна.
    /// </summary>
    public bool OnlyFlagged;

    public void Draw(RenderContext ctx)
    {
        if (_count == 0) return;
        if (_dirty) End();

        var sh = Shader;
        sh.Use();
        // Программа общая у всех пакетов точек, поэтому значение ставится
        // при каждом рисовании, а не один раз.
        sh.Set("uOnlyFlagged", OnlyFlagged ? 1 : 0);
        sh.Set("uMVP", ctx.MVP);
        sh.Set("uViewport", ctx.Viewport);
        sh.Set("uDepthBias", ctx.DepthBias * 2f);
        sh.Set("uOpacity", ctx.Opacity);
        ctx.Styles.UploadPoint(sh);

        GL.BindVertexArray(_vao);
        GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, _count);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        GpuRelease.VertexArray(ref _vao);
        GpuRelease.Buffer(ref _instVbo);
    }

    public static void DisposeShared()
    {
        GpuRelease.Shader(ref _shader);
        GpuRelease.Buffer(ref _quadVbo);
    }
}
