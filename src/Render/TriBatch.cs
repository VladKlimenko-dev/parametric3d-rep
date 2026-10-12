using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;

namespace Parametric3D.Render;

/// <summary>
/// Пакет треугольников одного тела (или вспомогательной поверхности).
/// Данные приходят прямо из тесселятора — MeshVertex совпадает с раскладкой
/// вершинных атрибутов, поэтому загрузка идёт без перепаковки.
///
/// Подсветка грани — точечная запись Flags в диапазон вершин этой грани
/// (MeshData.FaceRanges), а не перестроение всей сетки.
/// </summary>
public sealed class TriBatch : IDisposable
{
    private static ShaderProgram? _shader;
    public static ShaderProgram Shader =>
        _shader ??= new ShaderProgram("tri", Shaders.TriVert, Shaders.TriFrag);

    private int _vao = -1, _vbo = -1, _ebo = -1;
    private MeshVertex[] _verts = Array.Empty<MeshVertex>();
    private int _indexCount;
    private Dictionary<uint, FaceRange> _ranges = new();

    public Vector4 BaseColor = new(0.72f, 0.74f, 0.78f, 1f);
    public Vector4 HoverColor = new(0.85f, 0.88f, 0.95f, 1f);
    public Vector4 SelectColor = new(0.35f, 0.62f, 0.95f, 1f);
    public bool Unlit;
    public float Specular = 0.25f;
    public float Shininess = 48f;

    public bool IsEmpty => _indexCount == 0;

    public void Upload(MeshData md)
    {
        _verts = md.Vertices;
        _indexCount = md.Indices.Length;
        _ranges = new Dictionary<uint, FaceRange>(md.FaceRanges);

        if (_vao < 0)
        {
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            _ebo = GL.GenBuffer();
        }

        GL.BindVertexArray(_vao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _verts.Length * MeshVertex.Size, _verts, BufferUsageHint.DynamicDraw);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, md.Indices.Length * sizeof(int), md.Indices, BufferUsageHint.StaticDraw);

        const int S = MeshVertex.Size;
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, S, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, S, 12);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribIPointer(2, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)24);
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribIPointer(3, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)28);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
    }

    /// <summary>
    /// Ставит флаги подсветки на одну грань. Перезаливается только её
    /// диапазон вершин.
    /// </summary>
    /// <returns>
    /// Найден ли такой id в этом пакете — именно найден, а НЕ «изменился».
    /// Разница принципиальная: вызывающий по этому значению решает, куда
    /// смотреть дальше, и «нашли, но флаги уже такие» — это тоже успех.
    /// </returns>
    public bool SetFaceFlags(uint faceId, uint flags)
    {
        if (!_ranges.TryGetValue(faceId, out var r) || r.VertexCount == 0) return false;
        bool changed = false;
        for (int i = r.VertexStart; i < r.VertexStart + r.VertexCount; i++)
            if (_verts[i].Flags != flags) { _verts[i].Flags = flags; changed = true; }
        if (!changed || _vbo < 0) return true;

        var slice = new MeshVertex[r.VertexCount];
        Array.Copy(_verts, r.VertexStart, slice, 0, r.VertexCount);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr)(r.VertexStart * MeshVertex.Size),
                         r.VertexCount * MeshVertex.Size, slice);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        return true;
    }

    public void ClearAllFlags() => SetAllFlags(0);

    /// <summary>Одинаковые флаги на все вершины — подсветка тела целиком.</summary>
    public void SetAllFlags(uint flags)
    {
        bool changed = false;
        for (int i = 0; i < _verts.Length; i++)
            if (_verts[i].Flags != flags) { _verts[i].Flags = flags; changed = true; }
        if (!changed || _vbo < 0) return;
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, _verts.Length * MeshVertex.Size, _verts);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    public IEnumerable<uint> FaceIds => _ranges.Keys;

    public void Draw(RenderContext ctx)
    {
        if (_indexCount == 0) return;
        var sh = Shader;
        sh.Use();
        sh.Set("uModel", ctx.Model);
        sh.Set("uViewProj", ctx.ViewProj);
        int nl = sh.Loc("uNormalMat");
        if (nl >= 0)
        {
            Matrix3 nm = ctx.NormalMatrix;
            GL.UniformMatrix3(nl, false, ref nm);
        }
        sh.Set("uCamPos", ctx.CamPos);
        sh.Set("uBaseColor", BaseColor);
        sh.Set("uHoverColor", HoverColor);
        sh.Set("uSelectColor", SelectColor);
        sh.Set("uOpacity", ctx.Opacity);
        sh.Set("uUnlit", Unlit ? 1 : 0);
        sh.Set("uSpecular", Specular);
        sh.Set("uShininess", Shininess);

        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        GpuRelease.VertexArray(ref _vao);
        GpuRelease.Buffer(ref _vbo);
        GpuRelease.Buffer(ref _ebo);
        _indexCount = 0;
    }

    public static void DisposeShared() => GpuRelease.Shader(ref _shader);
}
