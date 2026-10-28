using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Parametric3D.Render.Gl;

namespace Parametric3D.Render.Engine;

/// <summary>
/// ДВИЖОК ОТРИСОВКИ ВИДА (2026-10-16, заменил прежний RenderScene).
///
/// Кадр: многовыборочный (MSAA до 8) буфер → фон-градиент → тела (студийный свет, гамма) с контуром силуэта
/// («вывернутая оболочка»: задние грани, раздвинутые по нормали на постоянное число пикселей) → рёбра постоянной ширины
/// со сглаживанием → прозрачные плоскости → линии и точки опорных и эскизов → вершины тела (наведённые и выбранные) →
/// разрешение в буфер окна. Стили: тонированный, с рёбрами, каркас, невидимые линии штрихом, прозрачный.
///
/// Выбор под курсором — проходом выбора (Pick): тот же набор объектов рисуется номерами в отдельный буфер без
/// сглаживания, рёбра и линии — полосой допуска, точки — квадратом допуска; читается один пиксель. Поэтому выбор
/// одинаков во всех стилях, учитывает заслонение и не зависит от числа треугольников.
///
/// Подсветка — текстура признаков по номерам выбора (SetFlags): меняется одна строка текстуры, сетки не трогаются.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    public RenderTheme Theme = RenderTheme.DarkTheme();
    public DisplayStyle Style = DisplayStyle.ShadedWithEdges;
    public bool ShowAllVertices;
    /// <summary>
    /// Режим эскиза: тела не рисуются и не заслоняют эскиз — их рёбра и вершины видны тонко, поверх, как кандидаты в
    /// ссылки эскиза (наведение подсвечивает, выбор — тоже без заслонения).
    /// </summary>
    public bool SketchMode;

    private ShaderProgram? _mesh, _line, _point, _fill, _bg, _floor;
    /// <summary>k132: габарит тел сцены (мир), без предпросмотра; пусто — нет тел.</summary>
    private Vector3 _bMin, _bMax; private bool _hasBounds;
    /// <summary>Разбор (k132): пол последнего кадра — нарисован, его высота, центр и полуразмеры; проходы отражения и тени.</summary>
    public (bool Drawn, float Y, Vector3 C, Vector2 H, bool Reflection, bool Shadow) LastFloor;
    /// <summary>Разбор (k133): шаг сетки пола последнего кадра (мм) и число промежуточных на основную; 0 — сетки нет.</summary>
    public (double MinorMm, int PerMajor) LastGrid;
    public static readonly bool FloorOff = Environment.GetEnvironmentVariable("P3D_FLOOR") == "0";

    /// <summary>
    /// k132 (замечание 28.10, 23:42, п. 8): ПОЛ ПОД ДЕТАЛЬЮ (как в SolidWorks): плоскость, параллельная XZ, на зазоре от нижней
    /// точки тел; края мягко исчезают. k133 (замечание 29.10, 08:55, п. 3): РАЗМЕР — множитель габарита тел size (1 — по
    /// габариту, 10 — в 10 раз больше; по умолчанию 2.62 — вдвое больше прежнего пола); ФОРМА: прямоугольник, эллипс
    /// (описан около прямоугольника — √2), круг (радиус — полудиагональ), квадрат (сторона — большая сторона). Плоская по
    /// стороне деталь — не меньше 10 % наибольшего размера. Null — тел нет.
    /// </summary>
    public static (float Y, Vector3 C, Vector2 H)? FloorGeometry(Vector3 min, Vector3 max, float gap, float size = 2.62f, FloorShapeKind shape = FloorShapeKind.Rectangle)
    {
        if (FloorOff) return null;
        var d = max - min;
        float big = Math.Max(d.X, Math.Max(d.Y, d.Z));
        if (!(big > 0) || float.IsNaN(big)) return null;
        float y = min.Y - Math.Max(0, gap);
        float k = FloorSizeOff ? 1.3f : Math.Clamp(size, 1f, 10f);
        var h = new Vector2(Math.Max(d.X, 0.1f * big) * 0.5f * k, Math.Max(d.Z, 0.1f * big) * 0.5f * k);
        if (!FloorSizeOff)
            switch (shape)
            {
                case FloorShapeKind.Ellipse: h *= MathF.Sqrt(2); break;
                case FloorShapeKind.Circle: { float r = h.Length; h = new Vector2(r, r); break; }
                case FloorShapeKind.Square: { float m = Math.Max(h.X, h.Y); h = new Vector2(m, m); break; }
            }
        var c = new Vector3((min.X + max.X) * 0.5f, y, (min.Z + max.Z) * 0.5f);
        return (y, c, h);
    }
    public static readonly bool FloorSizeOff = Environment.GetEnvironmentVariable("P3D_FLOORSIZE") == "0";
    public static readonly bool FloorGridOff = Environment.GetEnvironmentVariable("P3D_FLOORGRID") == "0";

    /// <summary>
    /// k133: ШАГ СЕТКИ ПОЛА в единицах документа: основной шаг — «круглое» число 1, 2, 5 × 10ⁿ единиц, при котором на большую
    /// сторону пола приходится около 10 основных клеток; промежуточных на основную — 10 (у 1), 4 (у 2), 5 (у 5). Возврат —
    /// шаг промежуточных линий в мм и их число на основную.
    /// </summary>
    /// <summary>k133: сетка пола темы для пола с полуразмерами h (единица — документа); (0, 0) — сетки нет.</summary>
    public static (double MinorMm, int PerMajor) GridFor(RenderTheme th, Vector2 h)
    {
        if (!th.FloorGrid || FloorGridOff) return (0, 0);
        double unit = Parametric3D.Core.Settings.DisplayUnitMm > 0 ? Parametric3D.Core.Settings.DisplayUnitMm : th.GridUnitMm;
        return GridSteps(2 * Math.Max(h.X, h.Y), unit);
    }

    public static (double MinorMm, int PerMajor) GridSteps(double floorSideMm, double unitMm)
    {
        if (!(unitMm > 0)) unitMm = 1;
        double raw = Math.Max(floorSideMm, 1e-6) / unitMm / 10;
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double m = raw / p;
        int digit = m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10;
        if (digit == 10) { digit = 1; p *= 10; }
        int per = digit == 1 ? 10 : digit == 2 ? 4 : 5;
        double majorMm = digit * p * unitMm;
        return (majorMm / per, per);
    }

    private int _emptyVao = -1;

    private sealed class MeshGpu { public int Vao = -1, Pos = -1, Nrm = -1, Pick = -1, Ibo = -1, Count; public Matrix4 Model; public Vector4 Color, MatColor; public float MatSpec, MatShine, MatMetal; public int Preview; public uint PartId; }
    private readonly List<MeshGpu> _meshes = new();
    private sealed class InstGpu { public int Vao = -1, Vbo = -1, Count; }
    private readonly InstGpu _bodyEdges = new(), _lines = new(), _points = new(), _bodyVerts = new();
    // u12: сетка вида / сетка FEA — линии треугольников, строятся по требованию (стиль, новая сцена, размер элемента)
    private readonly InstGpu _meshLines = new();
    private readonly List<MeshItem> _meshCpu = new();
    private (DisplayStyle Style, float H, int Version) _meshLinesKey = (DisplayStyle.Shaded, 0, -1);
    private int _sceneVersion;
    /// <summary>u12: размер элемента сетки FEA, доля диагонали габарита (0 — по умолчанию 1/30).</summary>
    public float FeaElementFraction = 1f / 15;
    // надстройка: сетка эскиза (с глубиной), построение инструмента и привязки (поверх всего) — меняются без пересборки сцены
    private readonly InstGpu _ovGrid = new(), _ovLines = new(), _ovPoints = new();
    private int _planeVao = -1, _planeVbo = -1;
    private readonly List<(uint Pick, Vector4 Fill)> _planes = new();

    private int _flagsTex = -1, _flagsW = 1024, _flagsH = 1;
    private byte[] _flags = new byte[1024 * 4];
    private bool _flagsDirty = true;

    // кадровые буферы
    private int _fbo = -1, _colorRb = -1, _depthRb = -1, _w, _h, _samples = 1;
    private int _pickFbo = -1, _pickColor = -1, _pickDepth = -1;
    // разрешение сглаживания — в свой буфер того же формата: в OpenGL ES перенос из многовыборочного буфера в буфер
    // другого формата (окно Avalonia под ANGLE может быть BGRA) запрещён, и кадр был бы пустым
    private int _resolveFbo = -1, _resolveColor = -1;
    public int Samples => _samples;

    public int PickCount { get; private set; }

    // ================================================================ загрузка сцены

    private void EnsureShaders()
    {
        if (_mesh != null) return;
        _mesh = new ShaderProgram("mesh", EngineShaders.MeshVert, EngineShaders.MeshFrag);
        _line = new ShaderProgram("line", EngineShaders.LineVert, EngineShaders.LineFrag);
        _point = new ShaderProgram("point", EngineShaders.PointVert, EngineShaders.PointFrag);
        _fill = new ShaderProgram("fill", EngineShaders.FillVert, EngineShaders.FillFrag);
        _bg = new ShaderProgram("background", EngineShaders.BackgroundVert, EngineShaders.BackgroundFrag);
        _cap = new ShaderProgram("cap", EngineShaders.CapVert, EngineShaders.CapFrag);
        _floor = new ShaderProgram("floor", EngineShaders.FloorVert, EngineShaders.FloorFrag);
        _emptyVao = GL.GenVertexArray();
    }

    public void Upload(SceneData s)
    {
        EnsureShaders();
        ReleaseScene();
        _meshCpu.Clear(); _meshCpu.AddRange(s.Meshes.Where(m => m.Preview == 0)); _sceneVersion++;
        // k132: габарит тел — для пола
        _hasBounds = false; _bMin = new Vector3(float.MaxValue); _bMax = new Vector3(float.MinValue);
        foreach (var m in s.Meshes)
        {
            if (m.Preview != 0) continue;
            foreach (var pp in m.Pos)
            {
                var w = Vector3.TransformPosition(pp, m.Model);
                _bMin = Vector3.ComponentMin(_bMin, w); _bMax = Vector3.ComponentMax(_bMax, w); _hasBounds = true;
            }
        }
        foreach (var m in s.Meshes)
        {
            var g = new MeshGpu { Model = m.Model, Color = m.Color, MatColor = m.MatColor, MatSpec = m.MatSpec, MatShine = m.MatShine, MatMetal = m.MatMetal, Count = m.Idx.Length, Preview = m.Preview, PartId = m.PartId };
            g.Vao = GL.GenVertexArray();
            GL.BindVertexArray(g.Vao);
            g.Pos = Buffer(m.Pos, 12); GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
            g.Nrm = Buffer(m.Nrm, 12); GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 12, 0);
            g.Pick = Buffer(m.Pick, 4); GL.EnableVertexAttribArray(2); GL.VertexAttribIPointer(2, 1, VertexAttribIntegerType.UnsignedInt, 4, IntPtr.Zero);
            g.Ibo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, g.Ibo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, m.Idx.Length * 4, m.Idx, BufferUsageHint.StaticDraw);
            GL.BindVertexArray(0);
            _meshes.Add(g);
        }
        UploadLines(_bodyEdges, s.BodyEdges);
        UploadLines(_lines, s.Lines);
        UploadPoints(_points, s.Points);
        UploadPoints(_bodyVerts, s.BodyVertices);

        _planes.Clear();
        if (s.Planes.Count > 0)
        {
            var v = new float[s.Planes.Count * 12];
            for (int i = 0; i < s.Planes.Count; i++)
            {
                var p = s.Planes[i];
                // порядок полосы треугольников: c0, c1, c3, c2
                Put(v, i * 12 + 0, p.C0); Put(v, i * 12 + 3, p.C1); Put(v, i * 12 + 6, p.C3); Put(v, i * 12 + 9, p.C2);
                _planes.Add((p.Pick, p.Fill));
            }
            _planeVao = GL.GenVertexArray();
            GL.BindVertexArray(_planeVao);
            _planeVbo = Buffer(v, 4);
            GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
            GL.BindVertexArray(0);
        }

        PickCount = s.PickTable.Count;
        int need = Math.Max(1, (PickCount + _flagsW - 1) / _flagsW);
        if (_flagsTex < 0 || need != _flagsH)
        {
            _flagsH = need;
            _flags = new byte[_flagsW * _flagsH * 4];
            if (_flagsTex < 0) _flagsTex = GL.GenTexture();
            GL.BindTexture2D(_flagsTex);
            GL.TexImageRgba8(_flagsW, _flagsH, _flags);
        }
        else Array.Clear(_flags);
        _flagsDirty = true;
    }

    private static void Put(float[] v, int at, Vector3 p) { v[at] = p.X; v[at + 1] = p.Y; v[at + 2] = p.Z; }

    private static int Buffer<T>(T[] data, int stride) where T : unmanaged
    {
        int b = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, b);
        GL.BufferData(BufferTarget.ArrayBuffer, Math.Max(1, data.Length) * Marshal.SizeOf<T>(), data.Length == 0 ? new T[1] : data, BufferUsageHint.StaticDraw);
        return b;
    }

    private static void UploadLines(InstGpu g, List<LineInst> l)
    {
        g.Count = l.Count;
        if (l.Count == 0) return;
        g.Vao = GL.GenVertexArray();
        GL.BindVertexArray(g.Vao);
        g.Vbo = Buffer(CollectionsMarshal.AsSpan(l).ToArray(), LineInst.Size);
        const int S = LineInst.Size;
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, S, 0); GL.VertexAttribDivisor(0, 1);
        GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, S, 12); GL.VertexAttribDivisor(1, 1);
        GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, S, 24); GL.VertexAttribDivisor(2, 1);
        GL.EnableVertexAttribArray(3); GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, S, 28); GL.VertexAttribDivisor(3, 1);
        GL.EnableVertexAttribArray(4); GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, S, 32); GL.VertexAttribDivisor(4, 1);
        GL.EnableVertexAttribArray(5); GL.VertexAttribIPointer(5, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)36); GL.VertexAttribDivisor(5, 1);
        GL.BindVertexArray(0);
    }

    private static void UploadPoints(InstGpu g, List<PointInst> l)
    {
        g.Count = l.Count;
        if (l.Count == 0) return;
        g.Vao = GL.GenVertexArray();
        GL.BindVertexArray(g.Vao);
        g.Vbo = Buffer(CollectionsMarshal.AsSpan(l).ToArray(), PointInst.Size);
        const int S = PointInst.Size;
        GL.EnableVertexAttribArray(0); GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, S, 0); GL.VertexAttribDivisor(0, 1);
        GL.EnableVertexAttribArray(1); GL.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, S, 12); GL.VertexAttribDivisor(1, 1);
        GL.EnableVertexAttribArray(2); GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, S, 16); GL.VertexAttribDivisor(2, 1);
        GL.EnableVertexAttribArray(3); GL.VertexAttribIPointer(3, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)20); GL.VertexAttribDivisor(3, 1);
        GL.EnableVertexAttribArray(4); GL.VertexAttribIPointer(4, 1, VertexAttribIntegerType.UnsignedInt, S, (IntPtr)24); GL.VertexAttribDivisor(4, 1);
        GL.BindVertexArray(0);
    }

    // ================================================================ надстройка

    /// <summary>
    /// Надстройка кадра (вызывать в потоке OpenGL): grid — линии с проверкой глубины (сетка эскиза), lines и points —
    /// поверх всего (резиновая нить инструмента, точка привязки). Пустые списки — надстройки нет.
    /// </summary>
    public void SetOverlay(List<LineInst> grid, List<LineInst> lines, List<PointInst> points)
    {
        EnsureShaders();
        ReleaseInst(_ovGrid); ReleaseInst(_ovLines); ReleaseInst(_ovPoints);
        UploadLines(_ovGrid, grid);
        UploadLines(_ovLines, lines);
        UploadPoints(_ovPoints, points);
    }

    private static void ReleaseInst(InstGpu g)
    {
        Del(ref g.Vbo);
        if (g.Vao >= 0) { GL.DeleteVertexArray(g.Vao); g.Vao = -1; }
        g.Count = 0;
    }

    // ================================================================ подсветка

    /// <summary>Признаки объекта по номеру выбора: 1 — наведение, 2 — выбор, 8 — ошибка.</summary>
    public void ClearFlags() { Array.Clear(_flags); _flagsDirty = true; }

    public void SetFlags(int pick, byte flags)
    {
        if (pick <= 0 || pick >= PickCount) return;
        _flags[pick * 4] |= flags;
        _flagsDirty = true;
    }

    private void UploadFlags()
    {
        if (!_flagsDirty || _flagsTex < 0) return;
        GL.BindTexture2D(_flagsTex);
        GL.TexSubImageRgba8(0, 0, _flagsW, _flagsH, _flags, 0);
        _flagsDirty = false;
    }

    // ================================================================ кадровые буферы

    public void Resize(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == _w && h == _h && _fbo >= 0) return;
        _w = w; _h = h;
        ReleaseTargets();
        GL.GetInteger(GetPName.MaxSamples, out int max);
        _samples = Math.Clamp(8, 1, Math.Max(1, max));
        for (; ; )
        {
            _fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _colorRb = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _colorRb);
            if (_samples > 1) GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, _samples, RenderbufferStorage.Rgba8, w, h);
            else GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _colorRb);
            _depthRb = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRb);
            if (_samples > 1) GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, _samples, RenderbufferStorage.Depth24Stencil8, w, h);
            else GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthRb);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete || _samples == 1) break;
            ReleaseTargets();
            _samples = _samples > 4 ? 4 : 1;      // меньше выборок — или без сглаживания
        }

        if (_samples > 1)
        {
            _resolveFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveFbo);
            _resolveColor = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _resolveColor);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _resolveColor);
        }

        _pickFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _pickFbo);
        _pickColor = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _pickColor);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _pickColor);
        _pickDepth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _pickDepth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, w, h);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _pickDepth);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // ================================================================ кадр

    public sealed class FrameParams
    {
        public Matrix4 View, Proj;
        public float Near;          // > 0 — перспектива
        public float Scale = 1;     // физических пикселей на логический
    }

    /// <summary>Нарисовать кадр и перенести его в буфер окна target.</summary>
    public void Draw(FrameParams f, int target)
    {
        EnsureShaders();
        UploadFlags();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        GL.Viewport(0, 0, _w, _h);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        if (_samples > 1) GL.Enable(EnableCap.Multisample);
        GL.ColorMask(true, true, true, true);
        GL.DepthMask(true);
        GL.ClearColor(0, 0, 0, 1);
        GL.ClearDepth(1.0);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

        // фон
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        _bg!.Use();
        _bg.Set("uTop", Theme.BackgroundTop);
        _bg.Set("uBottom", Theme.BackgroundBottom);
        GL.BindVertexArray(_emptyVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        BindFlags();

        bool meshStyle = Style is DisplayStyle.ViewMesh or DisplayStyle.FeaMesh;
        bool faces = Style is DisplayStyle.Shaded or DisplayStyle.ShadedWithEdges or DisplayStyle.Material || meshStyle;
        bool edges = Style is not (DisplayStyle.Shaded or DisplayStyle.Material);
        bool hidden = Style is DisplayStyle.HiddenLines or DisplayStyle.Transparent;

        if (SketchMode)
        {
            GL.Disable(EnableCap.DepthTest);
            DrawLines(_bodyEdges, f, 0f, colorOverride: Theme.SketchRefEdge);
            // k118 (п.2): потенциальные опорные точки тела — мельче и бледнее выбранных опорных точек эскиза
            // (масштаб 0,65 к размеру и прозрачности; подсвеченная/выбранная — в полном размере). P3D_CANDDIM=0 — как прежде.
            _pointDim = CandDimOff ? 0f : 0.65f;
            if (_pointDim > 0) DimmedCandidateDraws++;
            DrawPoints(_bodyVerts, f, 0f, onlyFlagged: false);
            _pointDim = 0f;
            GL.Enable(EnableCap.DepthTest);
            goto afterBodies;
        }

        // k132: пол (отражение, сам пол, тень) — под телами
        LastFloor = default;
        if (faces && !FloorOff) DrawFloor(f);

        // тела: тонированные — освещённые грани и контур; иначе — только глубина (для заслонения рёбер)
        if (faces) DrawMeshes(f, lit: true, opacity: 1f);
        else DrawMeshDepthOnly(f);
        if (faces) DrawCaps(f);
        if (faces) DrawOutline(f);
        // k118 (замечание 08:54, п. 3): КОНТУР ТЕЛА И В СТИЛЯХ БЕЗ ГРАНЕЙ. Очерк кривых поверхностей (цилиндр сбоку) — не ребро:
        // в «скрытых линиях» и каркасе тело без него теряло форму. Тот же проход контура — цветом рёбер, шириной ребра.
        // Мутация P3D_WIREOUTLINE=0.
        else if (!meshStyle && !WireOutlineOff && Style is DisplayStyle.HiddenLines or DisplayStyle.Wireframe or DisplayStyle.Transparent)
            DrawOutline(f, Style == DisplayStyle.Transparent ? Theme.Edge : Theme.EdgeWire, Theme.EdgeWidth * 0.6f);
        if (meshStyle)
        {
            EnsureMeshLines();
            DrawLines(_meshLines, f, 1e-5f, colorOverride: Style == DisplayStyle.FeaMesh ? new Vector4(0.10f, 0.30f, 0.75f, 0.9f) : new Vector4(0.25f, 0.27f, 0.32f, 0.9f));
        }

        // рёбра: невидимые — штрихом под гранями
        if (hidden && _bodyEdges.Count > 0)
        {
            GL.DepthFunc(DepthFunction.Greater);
            GL.DepthMask(false);
            DrawLines(_bodyEdges, f, 0f, colorOverride: Theme.EdgeHidden, dashOverride: 7f);
            GL.DepthMask(true);
            GL.DepthFunc(DepthFunction.Lequal);
        }
        // без граней рёбра — светлые на тёмном фоне (тёмные на светлом), с гранями — тёмные
        if (edges) DrawEdges(_bodyEdges, f, colorOverride: faces || Style == DisplayStyle.Transparent ? default : Theme.EdgeWire);
        if (Style == DisplayStyle.Transparent)
        {
            GL.DepthMask(false);
            DrawMeshes(f, lit: true, opacity: 0.35f);
            GL.DepthMask(true);
        }
        // наведённые и выбранные рёбра видны и в тонированном стиле
        if (!edges) DrawLinesFlaggedOnly(_bodyEdges, f);

    afterBodies:
        // сетка эскиза — под плоскостями и линиями, с глубиной
        GL.DepthMask(false);   // сетка не заслоняет линии эскиза, лежащие на ней
        DrawLines(_ovGrid, f, 3e-4f);
        GL.DepthMask(true);

        // опорные плоскости — прозрачные, после тел
        DrawPlanes(f, pick: false);

        // линии и точки опорных элементов и эскизов
        // в эскизе его линии и точки не заслоняются ничем (плоскость эскиза может совпадать с гранью или опорной плоскостью)
        if (SketchMode) GL.Disable(EnableCap.DepthTest);
        DrawLines(_lines, f, 4e-5f);
        DrawPoints(_points, f, 6e-5f, onlyFlagged: false);
        if (SketchMode) GL.Enable(EnableCap.DepthTest);
        if (!SketchMode) DrawPoints(_bodyVerts, f, 8e-5f, onlyFlagged: !ShowAllVertices);

        // построение инструмента — поверх всего
        if (_ovLines.Count > 0 || _ovPoints.Count > 0)
        {
            GL.Disable(EnableCap.DepthTest);
            DrawLines(_ovLines, f, 0f);
            DrawPoints(_ovPoints, f, 0f, onlyFlagged: false);
            GL.Enable(EnableCap.DepthTest);
        }

        GL.BindVertexArray(0);
        GL.UseProgram(0);

        // перенос в буфер окна: многовыборочный — сначала в свой обычный буфер того же формата
        int src = _fbo;
        if (_samples > 1 && _resolveFbo >= 0)
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            src = _resolveFbo;
        }
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, src);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, target);
        GL.Viewport(0, 0, _w, _h);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(true);
    }

    /// <summary>
    /// Проход выбора: номер объекта под пикселем (x, y — физические пиксели от левого верхнего угла). tolPx — допуск
    /// (логические пиксели) для линий; точки ловятся с допуском на треть больше.
    /// </summary>
    public uint Pick(FrameParams f, int x, int y, float tolPx)
    {
        if (_pickFbo < 0 || x < 0 || y < 0 || x >= _w || y >= _h) return 0;
        EnsureShaders();
        UploadFlags();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _pickFbo);
        GL.Viewport(0, 0, _w, _h);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.Disable(EnableCap.Multisample);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        // рисуется только окрестность курсора
        GL.Enable(EnableCap.ScissorTest);
        int r = (int)MathF.Ceiling(tolPx * f.Scale * 2) + 2;
        int py = _h - 1 - y;
        GL.Scissor(Math.Max(0, x - r), Math.Max(0, py - r), 2 * r + 1, 2 * r + 1);
        GL.ColorMask(true, true, true, true);
        GL.DepthMask(true);
        GL.ClearColor(0, 0, 0, 0);
        GL.ClearDepth(1.0);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        BindFlags();

        // поверхности плоскостей — первыми и без глубины: тело поверх них всегда главнее
        GL.DepthMask(false);
        GL.Disable(EnableCap.DepthTest);
        DrawPlanes(f, pick: true);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);

        // тела: номера граней
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1.5f, 6f);
        _mesh!.Use();
        SetCamera(_mesh, f);
        _mesh.Set("uMode", 2);
        _mesh.Set("uOutlinePx", 0f);
        if (!SketchMode)
            foreach (var m in _meshes)
            {
                if (m.Preview != 0) continue;
                _mesh.Set("uModel", m.Model);
                GL.BindVertexArray(m.Vao);
                GL.DrawElements(PrimitiveType.Triangles, m.Count, DrawElementsType.UnsignedInt, 0);
            }
        GL.Disable(EnableCap.PolygonOffsetFill);
        // в эскизе тела не заслоняют: рёбра и вершины ловятся сквозь
        if (SketchMode) GL.Disable(EnableCap.DepthTest);

        // рёбра и линии — полосой допуска; точки и вершины — квадратом
        _line!.Use();
        _line.Set("uPickMode", 1);
        DrawLinesRaw(_bodyEdges, f, 6e-4f, widthOverride: tolPx * 2, colorOverride: default, dashOverride: 0);
        DrawLinesRaw(_lines, f, 6e-4f, widthOverride: tolPx * 2, colorOverride: default, dashOverride: 0);
        _line.Set("uPickMode", 0);
        _point!.Use();
        _point.Set("uPickMode", 1);
        if (SketchMode)
        {
            // точки эскиза главнее вершин тела
            DrawPointsRaw(_bodyVerts, f, 8e-4f, onlyFlagged: false, sizeOverride: tolPx * 2.6f);
            DrawPointsRaw(_points, f, 8e-4f, onlyFlagged: false, sizeOverride: tolPx * 2.6f);
            GL.Enable(EnableCap.DepthTest);
        }
        else
        {
            DrawPointsRaw(_points, f, 8e-4f, onlyFlagged: false, sizeOverride: tolPx * 2.6f);
            DrawPointsRaw(_bodyVerts, f, 8e-4f, onlyFlagged: false, sizeOverride: tolPx * 2.6f);
        }
        _point.Set("uPickMode", 0);

        var px = new byte[4];
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _pickFbo);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.ReadPixelsRgba(x, py, 1, 1, px);
        GL.Disable(EnableCap.ScissorTest);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        uint id = px[3] == 0 ? 0u : (uint)(px[0] | px[1] << 8 | px[2] << 16);
        return id < PickCount ? id : 0;
    }

    // ================================================================ проходы

    private void BindFlags()
    {
        GL.ActiveTexture(0);
        GL.BindTexture2D(_flagsTex);
    }

    private void SetCommon(ShaderProgram p, FrameParams f)
    {
        p.Set("uFlags", 0);
        p.Set("uFlagsW", _flagsW);
        p.Set("uHover", Theme.Hover);
        p.Set("uSelect", Theme.Select);
    }

    /// <summary>u13: плоскость сечения (n, d) — отсекается dot(n, p) + d > 0; нулевая — без сечения.</summary>
    public Vector4 ClipPlane;
    /// <summary>u13: зебра на гранях.</summary>
    public bool Zebra;

    private void SetCamera(ShaderProgram p, FrameParams f)
    {
        p.Set("uClip", ClipPlane);
        p.Set("uView", f.View);
        p.Set("uProj", f.Proj);
        p.Set("uViewport", new Vector2(_w, _h));
        SetCommon(p, f);
        // k132: свет из настроек вида модели
        var th = Theme;
        Vector3 Dir(Vector3 pos, Vector3 to) { var d = pos - to; return d.LengthSquared > 1e-12f ? d.Normalized() : new Vector3(0, 0, 1); }
        p.Set("uL1", Dir(th.Light1Pos, th.Light1Target)); p.Set("uL1C", th.Light1Color * th.Light1Power);
        p.Set("uL2", Dir(th.Light2Pos, th.Light2Target)); p.Set("uL2C", th.Light2Color * th.Light2Power);
        p.Set("uAmb", th.AmbientColor * th.AmbientPower);
    }

    /// <summary>
    /// k132: пол — трафарет пола, отражение тел (зеркально относительно пола, только на полу), пол поверх отражения
    /// (частично прозрачный — отражение видно слабо), тень — тела, сплющенные на пол (по одному разу на пиксель).
    /// Снизу пол не виден.
    /// </summary>
    private void DrawFloor(FrameParams f)
    {
        var th = Theme;
        if (!th.FloorOn || SketchMode || !_hasBounds || _floor == null || _meshes.All(m => m.Preview != 0)) return;
        if (FloorGeometry(_bMin, _bMax, th.FloorGap, th.FloorSize, th.FloorShape) is not { } fg) return;
        var inv = f.View.Inverted();
        var eye = inv.Row3.Xyz; var fwd = -inv.Row2.Xyz;
        bool above = f.Near > 0 ? eye.Y > fg.Y : fwd.Y < -0.02f;
        if (!above) return;
        float big = Math.Max(_bMax.X - _bMin.X, Math.Max(_bMax.Y - _bMin.Y, _bMax.Z - _bMin.Z));
        var (gStep, gPer) = GridFor(th, fg.H);
        bool round = th.FloorShape is FloorShapeKind.Ellipse or FloorShapeKind.Circle;
        void Quad(Vector4 color, bool overRefl = false, bool stencil = false)
        {
            _floor!.Use();
            _floor.Set("uShape", round && !FloorSizeOff ? 1 : 0); _floor.Set("uStencil", stencil ? 1 : 0);
            _floor.Set("uGridOn", gStep > 0 ? 1 : 0); _floor.Set("uGridStep", (float)gStep); _floor.Set("uGridMajor", (float)gPer);
            _floor.Set("uGridO", Vector2.Zero); _floor.Set("uGridColor", th.FloorGridColor); _floor.Set("uGridPx", th.FloorGridPx);
            _floor.Set("uOverRefl", overRefl ? 1 : 0); _floor.Set("uTop", th.BackgroundTop); _floor.Set("uBottom", th.BackgroundBottom);
            _floor.Set("uVpH", (float)_h);
            _floor.Set("uView", f.View); _floor.Set("uProj", f.Proj);
            _floor.Set("uC", fg.C); _floor.Set("uH", fg.H); _floor.Set("uColor", color); _floor.Set("uEdge", 0.18f);
            GL.BindVertexArray(_emptyVao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }
        GL.Enable(EnableCap.StencilTest);
        GL.StencilMask(0xFF);
        // 1) трафарет пола
        GL.ColorMask(false, false, false, false); GL.DepthMask(false);
        GL.StencilFunc(StencilFunction.Always, 1, 0xFF);
        GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);
        Quad(new Vector4(1, 1, 1, 1), stencil: true);
        GL.ColorMask(true, true, true, true); GL.DepthMask(true);
        GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        GL.StencilFunc(StencilFunction.Equal, 1, 0xFF);
        int dbg = int.TryParse(Environment.GetEnvironmentVariable("P3D_FLOORDBG"), out var dd) ? dd : 0;
        bool refl = th.FloorReflection && ClipPlane == Vector4.Zero && (dbg & 1) == 0;
        // 2) отражение
        if (refl)
        {
            var R = Matrix4.CreateScale(1, -1, 1) * Matrix4.CreateTranslation(0, 2 * fg.Y, 0);
            _mesh!.Use();
            SetCamera(_mesh, f);
            _mesh.Set("uClip", new Vector4(0, 1, 0, -fg.Y - 1e-4f * big));
            _mesh.Set("uMode", 0); _mesh.Set("uOutlinePx", 0f); _mesh.Set("uOpacity", 1f); _mesh.Set("uError", Theme.Error);
            _mesh.Set("uZebra", 0);
            GL.Disable(EnableCap.CullFace);
            foreach (var m in _meshes)
            {
                if (m.Preview != 0) continue;
                _mesh.Set("uModel", m.Model * R);
                bool mat = Style == DisplayStyle.Material;
                _mesh.Set("uBase", mat ? m.MatColor : m.Color);
                _mesh.Set("uMat", mat ? new Vector4(m.MatSpec, m.MatShine, m.MatMetal, 1) : Vector4.Zero);
                GL.BindVertexArray(m.Vao);
                GL.DrawElements(PrimitiveType.Triangles, m.Count, DrawElementsType.UnsignedInt, 0);
            }
            _mesh.Set("uMat", Vector4.Zero);
            _mesh.Set("uClip", ClipPlane);
        }
        // 3) пол поверх отражения (без отражения — плотнее)
        var fc = th.FloorColor;
        if ((dbg & 2) == 0) Quad(new Vector4(fc.X, fc.Y, fc.Z, refl ? Math.Clamp(1 - th.FloorReflect, 0.3f, 0.97f) : fc.W), refl);
        // 4) тень: тела, сплющенные на пол; каждый пиксель — один раз
        if (th.FloorShadow && (dbg & 4) == 0)
        {
            var S = Matrix4.CreateScale(1, 0, 1) * Matrix4.CreateTranslation(0, fg.Y + 1e-4f * big, 0);
            GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Incr);
            GL.DepthMask(false);
            _mesh!.Use();
            SetCamera(_mesh, f);
            _mesh.Set("uMode", 1); _mesh.Set("uOutlinePx", 0f);
            _mesh.Set("uFlat", new Vector4(0, 0, 0, th.Dark ? 0.45f : 0.22f));
            GL.Disable(EnableCap.CullFace);
            foreach (var m in _meshes)
            {
                if (m.Preview != 0) continue;
                _mesh.Set("uModel", m.Model * S);
                GL.BindVertexArray(m.Vao);
                GL.DrawElements(PrimitiveType.Triangles, m.Count, DrawElementsType.UnsignedInt, 0);
            }
            _mesh.Set("uMode", 0);
            GL.DepthMask(true);
        }
        GL.StencilFunc(StencilFunction.Always, 0, 0xFF);
        GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        GL.Clear(ClearBufferMask.StencilBufferBit);
        GL.Disable(EnableCap.StencilTest);
        LastFloor = (true, fg.Y, fg.C, fg.H, refl, th.FloorShadow);
        LastGrid = (gStep, gPer);
        FloorFrames++;
    }
    public static long FloorFrames;

    private void DrawMeshes(FrameParams f, bool lit, float opacity)
    {
        if (_meshes.Count == 0) return;
        _mesh!.Use();
        SetCamera(_mesh, f);
        _mesh.Set("uMode", 0);
        _mesh.Set("uOutlinePx", 0f);
        _mesh.Set("uOpacity", opacity);
        _mesh.Set("uError", Theme.Error);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1.5f, 6f);
        if (opacity >= 1f && ClipPlane == Vector4.Zero) { GL.Enable(EnableCap.CullFace); GL.CullFace(TriangleFace.Back); }
        _mesh.Set("uZebra", Zebra ? 1 : 0);
        foreach (var m in _meshes)
        {
            if (m.Preview != 0) continue;
            _mesh.Set("uModel", m.Model);
            bool mat = Style == DisplayStyle.Material;
            _mesh.Set("uBase", mat ? m.MatColor : m.Color);
            _mesh.Set("uMat", mat ? new Vector4(m.MatSpec, m.MatShine, m.MatMetal, 1) : Vector4.Zero);
            GL.BindVertexArray(m.Vao);
            GL.DrawElements(PrimitiveType.Triangles, m.Count, DrawElementsType.UnsignedInt, 0);
        }
        _mesh.Set("uMat", Vector4.Zero);
        _mesh.Set("uZebra", 0);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.PolygonOffsetFill);
        DrawPreviews(f);
    }

    /// <summary>Предпросмотр операции: добавляемое — оранжевым, вырезаемое — красным, полупрозрачно поверх.</summary>
    private void DrawPreviews(FrameParams f)
    {
        bool any = false;
        foreach (var m in _meshes) if (m.Preview != 0) { any = true; break; }
        if (!any) return;
        _mesh!.Use();
        _mesh.Set("uMode", 1);
        GL.DepthMask(false);
        foreach (var m in _meshes)
        {
            if (m.Preview == 0) continue;
            _mesh.Set("uModel", m.Model);
            _mesh.Set("uFlat", m.Preview > 0 ? Theme.PreviewAdd : Theme.PreviewCut);
            GL.BindVertexArray(m.Vao);
            GL.DrawElements(PrimitiveType.Triangles, m.Count, DrawElementsType.UnsignedInt, 0);
        }
        GL.DepthMask(true);
        _mesh.Set("uMode", 0);
    }

    private void DrawMeshDepthOnly(FrameParams f)
    {
        if (_meshes.Count == 0) return;
        _mesh!.Use();
        SetCamera(_mesh, f);
        _mesh.Set("uMode", 1);
        _mesh.Set("uFlat", new Vector4(0, 0, 0, 1));
        _mesh.Set("uOutlinePx", 0f);
        GL.ColorMask(false, false, false, false);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1.5f, 6f);
        foreach (var m in _meshes)
        {
            if (m.Preview != 0) continue;
            _mesh.Set("uModel", m.Model);
            GL.BindVertexArray(m.Vao);
            GL.DrawElements(PrimitiveType.Triangles, m.Count, DrawElementsType.UnsignedInt, 0);
        }
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.ColorMask(true, true, true, true);
    }

    /// <summary>Контур силуэта: задние грани, раздвинутые по нормали на постоянную ширину в пикселях.</summary>
    /// <summary>
    /// k99 (замечание 5): КРЫШКИ СЕЧЕНИЯ. Плоскость сечения задана (ClipPlane), CapFrame — её рамка (центр, оси, полуразмер).
    /// Для каждой детали: её оставшиеся за плоскостью грани (обе стороны, без глубины) переворачивают бит трафарета — где
    /// счёт нечётный, точка плоскости внутри тела; там рисуется четырёхугольник плоскости цветом материала со штриховкой
    /// (металл — одна штриховка, неметалл — сетка; соседние детали — другой шаг и направление). P3D_SECTIONCAPS=0 — без них.
    /// </summary>
    public (Vector3 O, Vector3 U, Vector3 V, float Half)? CapFrame;
    /// <summary>Шаг штриховки крышек (мир) у первой детали.</summary>
    public float HatchStep = 2.5f;
    public static readonly bool CapsOff = Environment.GetEnvironmentVariable("P3D_SECTIONCAPS") == "0";
    /// <summary>Разбор: сколько крышек нарисовано в последнем кадре.</summary>
    public int CapsDrawn { get; private set; }
    private ShaderProgram? _cap;

    /// <summary>Штриховка детали по её порядку: (шаг, угол, крест).</summary>
    public static (float Step, float Angle, bool Cross) HatchOf(int partIndex, bool metal, float baseStep)
    {
        float[] k = { 1f, 1.45f, 0.75f, 1.9f };
        float step = baseStep * k[partIndex % k.Length];
        float ang = (partIndex % 2 == 0 ? 45f : 135f) * MathF.PI / 180f;
        return (step, ang, !metal);
    }

    public static readonly bool CapColorsOff = Environment.GetEnvironmentVariable("P3D_CAPCOLORS") == "0";

    /// <summary>
    /// k103 (замечание 5): ЦВЕТА КРЫШКИ СЕЧЕНИЯ по настройкам: заливка — своя или по материалу (темнее), штриховка — своя,
    /// по материалу или выключена (цвет заливки — линий не видно). Мутация P3D_CAPCOLORS=0 — настройки не действуют.
    /// </summary>
    public static (Vector4 Fill, Vector4 Hatch) CapColors(RenderTheme th, Vector4 material)
    {
        var fill = new Vector4(material.X * 0.82f, material.Y * 0.82f, material.Z * 0.82f, 1);
        var hatch = new Vector4(material.X * 0.28f, material.Y * 0.28f, material.Z * 0.30f, 1);
        if (CapColorsOff) return (fill, hatch);
        if (th.SectionFillColor.W > 0) fill = new Vector4(th.SectionFillColor.Xyz, 1);
        if (!th.SectionHatch) hatch = fill;
        else if (th.SectionHatchColor.W > 0) hatch = new Vector4(th.SectionHatchColor.Xyz, 1);
        return (fill, hatch);
    }

    private void DrawCaps(FrameParams f)
    {
        CapsDrawn = 0;
        if (CapsOff || ClipPlane == Vector4.Zero || CapFrame is not { } cf || _meshes.Count == 0 || _cap == null) return;
        var parts = _meshes.Where(m => m.Preview == 0).Select(m => m.PartId).Distinct().ToList();
        GL.Enable(EnableCap.StencilTest);
        GL.Disable(EnableCap.CullFace);
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var ms = _meshes.Where(m => m.Preview == 0 && m.PartId == parts[pi]).ToList();
            // 1) чётность пересечений — в трафарет
            GL.ColorMask(false, false, false, false);
            GL.DepthMask(false);
            GL.Disable(EnableCap.DepthTest);
            GL.StencilMask(0xFF);
            GL.StencilFunc(StencilFunction.Always, 0, 0xFF);
            GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Invert);
            _mesh!.Use();
            SetCamera(_mesh, f);
            _mesh.Set("uMode", 2);
            _mesh.Set("uOutlinePx", 0f);
            foreach (var m in ms)
            {
                _mesh.Set("uModel", m.Model);
                GL.BindVertexArray(m.Vao);
                GL.DrawElements(PrimitiveType.Triangles, m.Count, DrawElementsType.UnsignedInt, 0);
            }
            // 2) крышка там, где счёт нечётный; трафарет обнуляется
            GL.ColorMask(true, true, true, true);
            GL.DepthMask(true);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.StencilFunc(StencilFunction.Notequal, 0, 0xFF);
            GL.StencilOp(StencilOp.Zero, StencilOp.Zero, StencilOp.Zero);
            var m0 = ms[0];
            var (fillC, hatchC) = CapColors(Theme, m0.MatColor.W > 0 ? m0.MatColor : m0.Color);
            var (step, ang, cross) = HatchOf(pi, m0.MatMetal > 0.5f || m0.MatColor.W <= 0, HatchStep);
            _cap.Use();
            _cap.Set("uView", f.View);
            _cap.Set("uProj", f.Proj);
            _cap.Set("uO", cf.O); _cap.Set("uU", cf.U); _cap.Set("uV", cf.V); _cap.Set("uHalf", cf.Half);
            _cap.Set("uFill", fillC); _cap.Set("uHatch", hatchC);
            _cap.Set("uStep", step); _cap.Set("uAngle", ang); _cap.Set("uCross", cross ? 1 : 0);
            GL.BindVertexArray(_emptyVao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            CapsDrawn++;
        }
        GL.StencilFunc(StencilFunction.Always, 0, 0xFF);
        GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        GL.Disable(EnableCap.StencilTest);
        GL.BindVertexArray(0);
    }

    /// <summary>
    /// k116 (замечание 16:00, п. 1): КОНТУР — ЗА ГРАНЯМИ. Грани рисуются со сдвигом глубины назад (PolygonOffset 1.5/6, чтобы
    /// рёбра были поверх), а проход контура (обратные стороны, раздутые по нормали на N пикселей) — без сдвига. Тонкий
    /// треугольник-щепка (вдоль длинной трубы в огранённом теле — тысячи) при раздутии выворачивается, перестаёт
    /// отсекаться как лицевой и на той же глубине оказывался ВПЕРЕДИ своей грани: тёмные точки и штрихи по поверхности
    /// («вершины и пунктир»). Контур теперь сдвинут дальше граней. Мутация P3D_OUTLINEOFFSET=0.
    /// </summary>
    static readonly bool OutlineOffsetOff = Environment.GetEnvironmentVariable("P3D_OUTLINEOFFSET") == "0";
    public const float OutlineOffsetFactor = 6f, OutlineOffsetUnits = 40f;
    public static readonly bool WireOutlineOff = Environment.GetEnvironmentVariable("P3D_WIREOUTLINE") == "0";
    /// <summary>Разбор: проходов контура в стилях без граней (накопительно).</summary>
    public int WireOutlines { get; private set; }
    private void DrawOutline(FrameParams f, Vector4? color = null, float? width = null)
    {
        if (_meshes.Count == 0) return;
        if (color != null) WireOutlines++;
        if (!OutlineOffsetOff) { GL.Enable(EnableCap.PolygonOffsetFill); GL.PolygonOffset(OutlineOffsetFactor, OutlineOffsetUnits); }
        _mesh!.Use();
        _mesh.Set("uMode", 1);
        _mesh.Set("uFlat", color ?? Theme.Silhouette);
        _mesh.Set("uOutlinePx", (width ?? Theme.SilhouetteWidth) * f.Scale);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Front);
        foreach (var m in _meshes)
        {
            if (m.Preview != 0) continue;
            _mesh.Set("uModel", m.Model);
            GL.BindVertexArray(m.Vao);
            GL.DrawElements(PrimitiveType.Triangles, m.Count, DrawElementsType.UnsignedInt, 0);
        }
        GL.CullFace(TriangleFace.Back);
        GL.Disable(EnableCap.CullFace);
        if (!OutlineOffsetOff) GL.Disable(EnableCap.PolygonOffsetFill);
        _mesh.Set("uOutlinePx", 0f);
        _mesh.Set("uMode", 0);
    }

    private void DrawPlanes(FrameParams f, bool pick)
    {
        if (_planes.Count == 0 || _planeVao < 0) return;
        _fill!.Use();
        _fill.Set("uView", f.View);
        _fill.Set("uProj", f.Proj);
        SetCommon(_fill, f);
        _fill.Set("uPickMode", pick ? 1 : 0);
        if (!pick) GL.DepthMask(false);
        GL.BindVertexArray(_planeVao);
        for (int i = 0; i < _planes.Count; i++)
        {
            _fill.Set("uPick", _planes[i].Pick);
            _fill.Set("uColor", _planes[i].Fill);
            GL.DrawArrays(PrimitiveType.TriangleStrip, i * 4, 4);
        }
        if (!pick) GL.DepthMask(true);
    }

    private void DrawLines(InstGpu g, FrameParams f, float bias, Vector4 colorOverride = default, float dashOverride = 0)
    {
        if (g.Count == 0) return;
        _line!.Use();
        _line.Set("uPickMode", 0);
        DrawLinesRaw(g, f, bias, 0, colorOverride, dashOverride);
    }

    private void DrawLinesFlaggedOnly(InstGpu g, FrameParams f)
    {
        // в тонированном стиле рёбра не рисуются, но наведённое и выбранное — видны: дешёвый путь — прозрачные
        // неподсвеченные (альфа 0) и подсвеченные цветом подсветки
        if (g.Count == 0) return;
        _line!.Use();
        _line.Set("uPickMode", 0);
        GL.DepthMask(false);
        _biasPx = EdgeBiasPxOff ? 0 : EdgeBiasPx;
        try { DrawLinesRaw(g, f, 2e-5f, 0, new Vector4(0, 0, 0, 0.0001f), 0); } finally { _biasPx = 0; }
        GL.DepthMask(true);
    }

    /// <summary>
    /// k98 (замечание 15): РЁБРА ТЕЛА — СДВИГ К ГЛАЗУ В ПИКСЕЛЯХ. Постоянный сдвиг глубины 2e-5 (в нормированных координатах)
    /// в мире растёт с удалением (у перспективы — как квадрат расстояния): при уменьшении масштаба он доходил до миллиметров,
    /// и ребро внутреннего выреза проступало сквозь стенку трубы. Теперь — доля пикселя на глубине точки (не зависит от
    /// масштаба). P3D_EDGEBIASPX=0 — прежний сдвиг.
    /// </summary>
    public static readonly bool EdgeBiasPxOff = Environment.GetEnvironmentVariable("P3D_EDGEBIASPX") == "0";
    public const float EdgeBiasPx = 0.8f;
    private float _biasPx;
    /// <summary>
    /// k102 (замечание 1): СДВИГ РЁБЕР — НЕ БОЛЬШЕ ДОПУСКА РАЗБИЕНИЯ В МИРЕ. Толщина линии на глубину не влияет (полоса
    /// строится на экране, глубина — у самого ребра); ребро выреза проступало сквозь стенку, потому что сдвиг к глазу в
    /// 0.8 пикселя на мелком масштабе превышал толщину стенки. Ребру нужен сдвиг лишь на прогиб хорды разбиения (рёбра на
    /// точной поверхности, грани — хордами); грани и так отодвинуты (смещение полигонов). Сдвиг — минимум из 0.8 пикселя и
    /// EdgeBiasWorld (задаёт вид: ~5·10⁻⁴ габарита модели). P3D_EDGEBIASWORLD=0 — только пиксели (k98).
    /// </summary>
    public float EdgeBiasWorld;
    /// <summary>Сдвиг рёбер к глазу (мир) на глубине при данном размере пикселя: минимум из пикселей и мирового предела.</summary>
    public float EdgeBiasAt(float pixelWorld) => EdgeBiasPxOff ? 0 : EdgeBiasWorld > 0 && !EdgeBiasWorldOff ? MathF.Min(EdgeBiasWorld, EdgeBiasPx * pixelWorld) : EdgeBiasPx * pixelWorld;
    public static readonly bool EdgeBiasWorldOff = Environment.GetEnvironmentVariable("P3D_EDGEBIASWORLD") == "0";

    private void DrawEdges(InstGpu g, FrameParams f, Vector4 colorOverride = default, float dashOverride = 0)
    {
        _biasPx = EdgeBiasPxOff ? 0 : EdgeBiasPx;
        try { DrawLines(g, f, 2e-5f, colorOverride, dashOverride); } finally { _biasPx = 0; }
    }

    private void DrawLinesRaw(InstGpu g, FrameParams f, float bias, float widthOverride, Vector4 colorOverride, float dashOverride)
    {
        if (g.Count == 0) return;
        SetCamera(_line!, f);
        _line!.Set("uScale", f.Scale);
        _line.Set("uNear", f.Near);
        _line.Set("uDepthBias", bias);
        _line.Set("uBiasPx", _biasPx);
        _line.Set("uBiasWorld", _biasPx > 0 && !EdgeBiasWorldOff ? EdgeBiasWorld : 0f);
        _line.Set("uBonus", Theme.HighlightBonus);
        _line.Set("uWidthOverride", widthOverride);
        _line.Set("uColorOverride", colorOverride);
        _line.Set("uDashOverride", dashOverride);
        GL.BindVertexArray(g.Vao);
        GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, g.Count);
    }

    private float _pointDim;
    public static readonly bool CandDimOff = Environment.GetEnvironmentVariable("P3D_CANDDIM") == "0";
    /// <summary>k118: сколько раз потенциальные опорные точки рисовались приглушёнными (для проверки).</summary>
    public int DimmedCandidateDraws { get; private set; }

    private void DrawPoints(InstGpu g, FrameParams f, float bias, bool onlyFlagged)
    {
        if (g.Count == 0) return;
        _point!.Use();
        _point.Set("uPickMode", 0);
        DrawPointsRaw(g, f, bias, onlyFlagged, 0);
    }

    private void DrawPointsRaw(InstGpu g, FrameParams f, float bias, bool onlyFlagged, float sizeOverride)
    {
        if (g.Count == 0) return;
        SetCamera(_point!, f);
        _point!.Set("uScale", f.Scale);
        _point.Set("uDepthBias", bias);
        _point.Set("uBonus", Theme.VertexHighlightSize - Theme.VertexSize);
        _point.Set("uSizeOverride", sizeOverride);
        _point.Set("uOnlyFlagged", onlyFlagged ? 1 : 0);
        _point.Set("uDim", _pointDim);
        GL.BindVertexArray(g.Vao);
        GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, g.Count);
    }

    // ================================================================ освобождение

    /// <summary>
    /// u12: линии сетки. Вид — стороны треугольников показа (каждая один раз). FEA — каждый треугольник делится на n²
    /// равных (n — по длиннейшей стороне и размеру элемента): равномерная сетка заданного шага поверх граней.
    /// </summary>
    private void EnsureMeshLines()
    {
        float diag = 0;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var m in _meshCpu) foreach (var p0 in m.Pos) { var p = Vector3.TransformPosition(p0, m.Model); lo = Vector3.ComponentMin(lo, p); hi = Vector3.ComponentMax(hi, p); }
        if (_meshCpu.Count > 0) diag = (hi - lo).Length;
        float h = Math.Max(1e-6f, diag * (FeaElementFraction > 0 ? FeaElementFraction : 1f / 15));
        var key = (Style, Style == DisplayStyle.FeaMesh ? h : 0f, _sceneVersion);
        // k96 (самотест 17:56: окно стояло 648 мс на первом кадре стиля «Сетка FEA»): сетка FEA строится в фоне — кадр
        // рисуется сразу (пока без сетки), готовые линии загружаются следующим кадром. P3D_FEAASYNC=0 — в кадре, как прежде.
        if (_meshLinesTask is { IsCompleted: true } done && _meshLinesTaskKey == key)
        {
            _meshLinesTask = null;
            if (done.Status == TaskStatus.RanToCompletion)
            {
                Del(ref _meshLines.Vbo);
                if (_meshLines.Vao >= 0) { GL.DeleteVertexArray(_meshLines.Vao); _meshLines.Vao = -1; }
                UploadLines(_meshLines, done.Result);
                _meshLinesKey = key;
            }
            return;
        }
        if (key == _meshLinesKey && _meshLines.Vao >= 0) return;
        if (_meshLinesTask != null && _meshLinesTaskKey == key) return;   // уже считается
        var style = Style;
        var items = _meshCpu.Select(m => (m.Pos, m.Idx, m.Pick, m.Model)).ToList();
        List<LineInst> Build()
        {
            var lines = new List<LineInst>();
            uint col = SceneData.Pack(new Vector4(0, 0, 0, 1));
            void Add(Vector3 a, Vector3 b) { if (lines.Count < 2_000_000) lines.Add(new LineInst { A = a, B = b, Color = col, Width = 1f }); }
            foreach (var (pos, idx, pick, model) in items)
            {
                Vector3 P(int i) => Vector3.TransformPosition(pos[i], model);
                if (style == DisplayStyle.ViewMesh)
                {
                    var seen = new HashSet<(int, int)>();
                    for (int t = 0; t + 2 < idx.Length; t += 3)
                        for (int k = 0; k < 3; k++)
                        {
                            int a = idx[t + k], b = idx[t + (k + 1) % 3];
                            if (seen.Add(a < b ? (a, b) : (b, a))) Add(P(a), P(b));
                        }
                    continue;
                }
                var wp = pos.Select(q => Vector3.TransformPosition(q, model)).ToArray();
                foreach (var (a, b2) in FeaRemesh.Lines(wp, idx, pick, h)) Add(a, b2);
            }
            return lines;
        }
        if (style == DisplayStyle.FeaMesh && !FeaAsyncOff)
        {
            _meshLinesTaskKey = key;
            var cb = MeshLinesReady;
            _meshLinesTask = Task.Run(() => { var r = Build(); cb?.Invoke(); return r; });
            // прежние линии (другой стиль или шаг) не показываются
            Del(ref _meshLines.Vbo);
            if (_meshLines.Vao >= 0) { GL.DeleteVertexArray(_meshLines.Vao); _meshLines.Vao = -1; }
            _meshLines.Count = 0;
            return;
        }
        _meshLinesKey = key;
        Del(ref _meshLines.Vbo);
        if (_meshLines.Vao >= 0) { GL.DeleteVertexArray(_meshLines.Vao); _meshLines.Vao = -1; }
        UploadLines(_meshLines, Build());
    }

    private Task<List<LineInst>>? _meshLinesTask;
    private (DisplayStyle Style, float H, int Version) _meshLinesTaskKey;
    static readonly bool FeaAsyncOff = Environment.GetEnvironmentVariable("P3D_FEAASYNC") == "0";
    /// <summary>Сетка FEA построена в фоне — нужен кадр (вызывается из фонового потока).</summary>
    public Action? MeshLinesReady;

    private void ReleaseScene()
    {
        foreach (var m in _meshes)
        {
            Del(ref m.Pos); Del(ref m.Nrm); Del(ref m.Pick); Del(ref m.Ibo);
            if (m.Vao >= 0) GL.DeleteVertexArray(m.Vao);
        }
        _meshes.Clear();
        foreach (var g in new[] { _bodyEdges, _lines, _points, _bodyVerts })
        {
            Del(ref g.Vbo);
            if (g.Vao >= 0) { GL.DeleteVertexArray(g.Vao); g.Vao = -1; }
            g.Count = 0;
        }
        Del(ref _planeVbo);
        if (_planeVao >= 0) { GL.DeleteVertexArray(_planeVao); _planeVao = -1; }
    }

    private static void Del(ref int b) { if (b >= 0) GL.DeleteBuffer(b); b = -1; }

    private void ReleaseTargets()
    {
        void Rb(ref int r) { if (r >= 0) GL.DeleteRenderbuffer(r); r = -1; }
        void Fb(ref int r) { if (r >= 0) GL.DeleteFramebuffer(r); r = -1; }
        Rb(ref _colorRb); Rb(ref _depthRb); Fb(ref _fbo);
        Rb(ref _pickColor); Rb(ref _pickDepth); Fb(ref _pickFbo);
        Rb(ref _resolveColor); Fb(ref _resolveFbo);
    }

    public void Dispose()
    {
        try
        {
            ReleaseScene();
            ReleaseInst(_ovGrid); ReleaseInst(_ovLines); ReleaseInst(_ovPoints);
            ReleaseTargets();
            if (_flagsTex >= 0) GL.DeleteTexture(_flagsTex);
            _flagsTex = -1;
            foreach (var p in new[] { _mesh, _line, _point, _fill, _bg }) p?.Dispose();
            _mesh = _line = _point = _fill = _bg = null;
            if (_emptyVao >= 0) GL.DeleteVertexArray(_emptyVao);
            _emptyVao = -1;
        }
        catch { /* контекст мог умереть раньше */ }
    }
}
