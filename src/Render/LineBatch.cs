using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Parametric3D.Render;

[StructLayout(LayoutKind.Sequential)]
public struct LineInstance
{
    public Vector3 P0;    //  0
    public Vector3 P1;    // 12
    public float S0;      // 24  накопленная длина ломаной в точке P0
    public int Style;     // 28
    public uint PickId;   // 32
    public uint Flags;    // 36

    /// <summary>
    /// Толщина в пикселях, перекрывающая таблицу стилей. 0 — брать из стиля.
    /// Нужна наконечникам стрелок: их ширина не связана с толщиной линий.
    /// </summary>
    public float WidthPx; // 40

    public const int Size = 44;
}

/// <summary>
/// Пакет линий.
///
/// ВСЯ линейная графика проекта (рёбра тел, эскиз, сетка, размеры, оси,
/// «резиновая» линия построения) складывается в такие пакеты и рисуется
/// одним instanced-вызовом на пакет. Толщина в пикселях и штриховка живут
/// в шейдере — GL_LINE_WIDTH и GL_LINE_STIPPLE не используются вообще
/// (в core-профиле они и не работают толком).
///
/// Подсветка и выделение меняются точечным BufferSubData по диапазону
/// инстансов конкретного объекта — перезаливать весь буфер не нужно.
/// </summary>
public sealed class LineBatch : IDisposable
{
    private static int _quadVbo = -1;
    private static ShaderProgram? _shader;

    private int _vao = -1, _instVbo = -1;
    private LineInstance[] _data = new LineInstance[256];
    private int _count;
    private int _capacityOnGpu;
    private bool _dirty;

    private readonly Dictionary<uint, (int Start, int Count)> _ranges = new();

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public static ShaderProgram Shader =>
        _shader ??= new ShaderProgram("line", Shaders.LineVert, Shaders.LineFrag);

    private static int QuadVbo
    {
        get
        {
            if (_quadVbo >= 0) return _quadVbo;
            float[] corners = { 0f, -1f, 0f, 1f, 1f, -1f, 1f, 1f };
            _quadVbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, corners.Length * sizeof(float), corners, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            return _quadVbo;
        }
    }

    // ------------------------------------------------------------ наполнение

    public void Begin()
    {
        _count = 0;
        _ranges.Clear();
    }

    private void Ensure(int extra)
    {
        if (_count + extra <= _data.Length) return;
        int n = Math.Max(_data.Length * 2, _count + extra);
        Array.Resize(ref _data, n);
    }

    /// <summary>Признак в Flags: полоса сужается к нулю у второго конца.</summary>
    public const uint FlagTaper = 4;

    /// <summary>Начало / конец отрезка — стык внутри ломаной (шейдер выносит там торец).</summary>
    public const uint FlagJoinStart = 8, FlagJoinEnd = 16;

    /// <summary>
    /// Биты, которыми распоряжается подсветка: наведение и выделение.
    ///
    /// Всё, что выше, принадлежит самой линии и переживает подсветку.
    /// Раньше подсветка писала поле флагов ЦЕЛИКОМ и стирала признак
    /// сужения — наконечники стрелок превращались в прямоугольники после
    /// первого же движения мыши.
    /// </summary>
    private const uint HighlightMask = 3;

    public void AddSegment(Vector3 a, Vector3 b, LineStyle style, uint pickId, uint flags = 0)
    {
        Ensure(1);
        _data[_count++] = new LineInstance
        { P0 = a, P1 = b, S0 = 0f, Style = (int)style, PickId = pickId, Flags = flags };
        _dirty = true;
    }

    /// <summary>
    /// Полоса заданной ширины, сужающаяся к точке в конце b.
    ///
    /// Одна такая полоса — это готовый треугольник со сглаженными краями:
    /// сглаживание считает тот же шейдер, что и для линий, по покрытию.
    /// Прежний способ набирал наконечник двумя десятками поперечных полос,
    /// их края накладывались, и стрелка выходила размытой и шире, чем надо.
    /// </summary>
    public void AddTaperedSegment(Vector3 a, Vector3 b, LineStyle style, float widthPx, uint pickId)
    {
        Ensure(1);
        _data[_count++] = new LineInstance
        {
            P0 = a, P1 = b, S0 = 0f, Style = (int)style,
            PickId = pickId, Flags = FlagTaper, WidthPx = widthPx
        };
        _dirty = true;
    }

    /// <summary>Добавляет ломаную. Фаза штриха накапливается вдоль неё, поэтому
    /// пунктир не «сбрасывается» на каждом изломе.</summary>
    public void AddPolyline(IReadOnlyList<Vector3> pts, LineStyle style, uint pickId,
                            bool closed = false, uint flags = 0)
    {
        int n = pts.Count;
        if (n < 2) return;
        int segs = closed ? n : n - 1;
        Ensure(segs);

        int start = _count;
        float s = 0f;
        for (int i = 0; i < segs; i++)
        {
            Vector3 a = pts[i];
            Vector3 b = pts[(i + 1) % n];

            // Стыки внутри ломаной (у замкнутой — все концы): только там шейдер
            // выносит торец отрезка, чтобы изломы не расходились. Настоящие концы
            // обрываются ровно в вершине и не вылезают за угол тела.
            uint joins = 0;
            if (closed || i > 0) joins |= FlagJoinStart;
            if (closed || i < segs - 1) joins |= FlagJoinEnd;

            _data[_count++] = new LineInstance
            { P0 = a, P1 = b, S0 = s, Style = (int)style, PickId = pickId, Flags = flags | joins };
            s += (b - a).Length;
        }

        if (pickId != 0)
        {
            if (_ranges.TryGetValue(pickId, out var r))
                _ranges[pickId] = (r.Start, r.Count + segs);
            else
                _ranges[pickId] = (start, segs);
        }
        _dirty = true;
    }

    public void AddPolyline(Vector3[] pts, LineStyle style, uint pickId, bool closed = false, uint flags = 0)
        => AddPolyline((IReadOnlyList<Vector3>)pts, style, pickId, closed, flags);

    /// <summary>Заканчивает наполнение и заливает данные в GPU.</summary>
    public void End()
    {
        EnsureGpu();
        if (_count == 0) { _dirty = false; return; }

        GL.BindBuffer(BufferTarget.ArrayBuffer, _instVbo);
        if (_count > _capacityOnGpu)
        {
            _capacityOnGpu = Math.Max(_count, 256);
            GL.BufferData(BufferTarget.ArrayBuffer, _capacityOnGpu * LineInstance.Size, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        }
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, _count * LineInstance.Size, _data);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        _dirty = false;
    }

    private uint _groupId;
    private int _groupStart;

    /// <summary>
    /// Начинает группу: всё добавленное до EndGroup получит один диапазон
    /// подсветки под указанным id.
    ///
    /// Нужно значкам ограничений: одно ограничение рисуется НЕСКОЛЬКИМИ
    /// ломаными и, более того, в нескольких местах эскиза — по значку
    /// на каждый участник. Обычная запись диапазона в AddPolyline хранит
    /// по одному отрезку на id, и подсветилась бы только последняя ломаная.
    /// </summary>
    public void BeginGroup(uint pickId)
    {
        _groupId = pickId;
        _groupStart = _count;
    }

    public void EndGroup()
    {
        if (_groupId != 0 && _count > _groupStart)
            _ranges[_groupId] = (_groupStart, _count - _groupStart);
        _groupId = 0;
    }

    /// <summary>Меняет флаги (наведение/выделение) только у одного объекта.</summary>
    /// <returns>Найден ли id в этом пакете (не «изменился ли»).</returns>
    public bool SetFlags(uint pickId, uint flags)
    {
        if (!_ranges.TryGetValue(pickId, out var r)) return false;
        bool changed = false;
        for (int i = r.Start; i < r.Start + r.Count; i++)
        {
            uint next = (_data[i].Flags & ~HighlightMask) | (flags & HighlightMask);
            if (_data[i].Flags != next) { _data[i].Flags = next; changed = true; }
        }
        if (!changed || _instVbo < 0) return true;

        GL.BindBuffer(BufferTarget.ArrayBuffer, _instVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer,
                         (IntPtr)(r.Start * LineInstance.Size),
                         r.Count * LineInstance.Size,
                         new ArraySegment<LineInstance>(_data, r.Start, r.Count).ToArray());
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        return true;
    }

    public void ClearAllFlags() => SetAllFlags(0);

    /// <summary>Одинаковые флаги на все инстансы батча — подсветка объекта целиком.</summary>
    public void SetAllFlags(uint flags)
    {
        bool changed = false;
        for (int i = 0; i < _count; i++)
        {
            uint next = (_data[i].Flags & ~HighlightMask) | (flags & HighlightMask);
            if (_data[i].Flags != next) { _data[i].Flags = next; changed = true; }
        }
        if (changed) End();
    }

    // ------------------------------------------------------------ отрисовка

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
        _capacityOnGpu = Math.Max(_data.Length, 256);
        GL.BufferData(BufferTarget.ArrayBuffer, _capacityOnGpu * LineInstance.Size, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        const int S = LineInstance.Size;
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, S, 0);
        GL.VertexAttribDivisor(1, 1);

        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, S, 12);
        GL.VertexAttribDivisor(2, 1);

        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, S, 24);
        GL.VertexAttribDivisor(3, 1);

        GL.EnableVertexAttribArray(4);
        GL.VertexAttribIPointer(4, 1, VertexAttribIntegerType.Int, S, (IntPtr)28);
        GL.VertexAttribDivisor(4, 1);

        GL.EnableVertexAttribArray(5);
        GL.VertexAttribIPointer(5, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)32);
        GL.VertexAttribDivisor(5, 1);

        GL.EnableVertexAttribArray(6);
        GL.VertexAttribIPointer(6, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)36);
        GL.VertexAttribDivisor(6, 1);

        GL.EnableVertexAttribArray(7);
        GL.VertexAttribPointer(7, 1, VertexAttribPointerType.Float, false, S, 40);
        GL.VertexAttribDivisor(7, 1);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    /// <param name="styleOverride">Принудительный стиль для всего пакета
    /// (например, EdgeHidden при отрисовке невидимых рёбер прозрачного тела). -1 = как есть.</param>
    public void Draw(RenderContext ctx, int styleOverride = -1)
    {
        if (_count == 0) return;
        if (_dirty) End();

        var sh = Shader;
        sh.Use();
        sh.Set("uMVP", ctx.MVP);
        sh.Set("uViewport", ctx.Viewport);
        sh.Set("uDepthBias", ctx.DepthBias);
        sh.Set("uWorldToPx", ctx.WorldToPx);
        sh.Set("uOpacity", ctx.Opacity);
        sh.Set("uStyleOverride", styleOverride);
        sh.Set("uWidthScale", ctx.LineWidthScale);
        ctx.Styles.UploadLine(sh);

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
