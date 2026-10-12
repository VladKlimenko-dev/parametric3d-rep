using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Render;

/// <summary>
/// Кадровый буфер сцены: одно цветовое вложение RGBA8 с многократной
/// выборкой (MSAA) и буфер глубины.
///
/// ИСТОРИЯ. Сначала здесь было ВТОРОЕ вложение формата R32UI, куда каждый
/// фрагментный шейдер писал идентификатор объекта: выбор мышью читался
/// оттуда одним glReadPixels. Схема не заработала — чтение всегда давало
/// ноль, и вместе с этим переставала действовать прозрачность в основном
/// вложении. Симптомы появились и исчезли одновременно, что указывает на
/// драйвер, отключающий смешивание для ВСЕГО набора буферов вывода, если
/// хотя бы один из них целочисленный. Спецификация требует иного —
/// смешивание должно пропускаться только для самого целочисленного
/// буфера, — но полагаться на это не стоит.
///
/// Выбор объекта теперь считается геометрически на процессоре
/// (см. Views/ScenePicker.cs), поэтому целочисленное вложение не нужно
/// вовсе. А раз кадровый буфер стал обычным, сглаживание делается
/// нормальным MSAA вместо суперсэмплинга: та же картинка при вчетверо
/// меньшем числе пикселей, и вывод на экран — одним glBlitFramebuffer,
/// без шейдера даунсэмплинга.
/// </summary>
public sealed class SceneFramebuffer : IDisposable
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Желаемое число выборок MSAA. 1 — сглаживание выключено.</summary>
    public int Samples
    {
        get => _samples;
        set
        {
            int v = Math.Max(1, value);
            if (v == _samples) return;
            _samples = v;
            Invalidate();
        }
    }

    /// <summary>Фактическое число выборок, которое дала видеокарта.</summary>
    public int ActualSamples { get; private set; } = 1;

    private int _samples = 4;
    private int _fbo = -1, _colorRbo = -1, _depthRbo = -1;
    private bool _needRebuild = true;

    private void Invalidate() => _needRebuild = true;

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (!_needRebuild && _fbo >= 0 && width == Width && height == Height) return;

        Width = width;
        Height = height;
        _needRebuild = false;

        Dispose();

        // Число выборок ограничено возможностями видеокарты.
        GL.GetInteger(GetPName.MaxSamples, out int maxSamples);
        ActualSamples = Math.Clamp(_samples, 1, Math.Max(1, maxSamples));

        _fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorRbo = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _colorRbo);
        if (ActualSamples > 1)
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, ActualSamples,
                                              RenderbufferStorage.Rgba8, width, height);
        else
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                   RenderbufferTarget.Renderbuffer, _colorRbo);

        _depthRbo = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
        if (ActualSamples > 1)
            GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, ActualSamples,
                                              RenderbufferStorage.Depth24Stencil8, width, height);
        else
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                                   RenderbufferTarget.Renderbuffer, _depthRbo);

        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);

        var st = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (st != FramebufferErrorCode.FramebufferComplete)
        {
            // Не все конфигурации поддерживают запрошенное число выборок.
            // Откатываемся на буфер без сглаживания, а не падаем.
            if (ActualSamples > 1)
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                _samples = 1;
                _needRebuild = true;
                Resize(width, height);
                return;
            }
            throw new P3DException($"Scene framebuffer is incomplete: {st}");
        }

        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Bind()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        GL.Viewport(0, 0, Width, Height);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        if (ActualSamples > 1) GL.Enable(EnableCap.Multisample);
    }

    public void Clear(Vector4 color)
    {
        GL.ClearColor(color.X, color.Y, color.Z, color.W);
        GL.ClearDepth(1.0);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
    }

    /// <summary>
    /// Переносит картинку на экран. Разрешение MSAA выполняет сам
    /// glBlitFramebuffer — отдельный проход с шейдером не нужен.
    /// </summary>
    public void ResolveToScreen(int screenW, int screenH)
    {
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.DrawBuffer(DrawBufferMode.Back);

        GL.BlitFramebuffer(0, 0, Width, Height,
                           0, 0, screenW, screenH,
                           ClearBufferMask.ColorBufferBit,
                           BlitFramebufferFilter.Nearest);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, screenW, screenH);
    }

    public void Dispose()
    {
        GpuRelease.Renderbuffer(ref _colorRbo);
        GpuRelease.Renderbuffer(ref _depthRbo);
        GpuRelease.Framebuffer(ref _fbo);
        _needRebuild = true;
    }

    public static void DisposeShared() { }
}
