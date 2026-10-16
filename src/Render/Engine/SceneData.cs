using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Render.Engine;

/// <summary>Отрезок для отрисовки (экземпляр): концы в мире, цвет RGBA8, ширина в логических пикселях, штрих, номер выбора.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LineInst
{
    public Vector3 A, B;
    public uint Color;
    public float Width;
    /// <summary>Период штриха в пикселях (0 — сплошная).</summary>
    public float Dash;
    /// <summary>Номер в таблице выбора (0 — не выбирается).</summary>
    public uint Pick;
    public const int Size = 40;
}

/// <summary>Точка для отрисовки: положение, цвет, размер (px), форма (0 круг, 1 квадрат, 2 ромб, 3 крест), номер выбора.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PointInst
{
    public Vector3 P;
    public uint Color;
    public float Px;
    public uint Shape;
    public uint Pick;
    public const int Size = 28;
}

/// <summary>Сетка тела детали: вершины с нормалями и номером выбора грани.</summary>
public sealed class MeshItem
{
    public Vector3[] Pos = Array.Empty<Vector3>();
    public Vector3[] Nrm = Array.Empty<Vector3>();
    public uint[] Pick = Array.Empty<uint>();
    public int[] Idx = Array.Empty<int>();
    public Matrix4 Model = Matrix4.Identity;
    public Vector4 Color;
    /// <summary>u10: стиль «Материал» — цвет материала, сила и резкость блика, металл (0/1).</summary>
    public Vector4 MatColor;
    public float MatSpec = 0.3f, MatShine = 48, MatMetal;
    public uint PartId;
    /// <summary>Предпросмотр операции (рисуется полупрозрачным цветом добавления или выреза).</summary>
    public int Preview;
}

/// <summary>Прямоугольник опорной плоскости.</summary>
public struct PlaneItem
{
    public Vector3 C0, C1, C2, C3;
    public uint Pick;
    public Vector4 Fill;
}

/// <summary>Запись таблицы выбора: что под номером и чья это часть (деталь, эскиз).</summary>
public struct PickEntry
{
    public PickRef Ref;
    public uint PartId;
    public uint SketchId;
    /// <summary>Класс для приоритета под курсором: 3 точка, 2 линия, 1 поверхность.</summary>
    public int Priority;
}

/// <summary>
/// СЦЕНА — снимок документа для отрисовки и выбора, построенный на процессоре (SceneBuilder). Видеокарта получает его
/// целиком (SceneRenderer.Upload); подсветка меняет только текстуру признаков по номерам выбора.
/// </summary>
public sealed class SceneData
{
    public readonly List<MeshItem> Meshes = new();
    public readonly List<LineInst> BodyEdges = new();
    public readonly List<LineInst> Lines = new();
    public readonly List<PointInst> Points = new();
    /// <summary>u10: начала координат деталей (точка и оси в мире) — стрелки X/Y/Z рисуются поверх, постоянной длины на экране.</summary>
    public readonly List<(Vector3 P, Vector3 X, Vector3 Y, Vector3 Z, uint Pick)> Origins = new();
    /// <summary>Вершины тел: видны только наведённые и выбранные (или все — по настройке показа).</summary>
    public readonly List<PointInst> BodyVertices = new();
    public readonly List<PlaneItem> Planes = new();
    /// <summary>Таблица выбора: номер (с 1) → объект. Номер 0 — пусто.</summary>
    public readonly List<PickEntry> PickTable = new() { default };
    public readonly Dictionary<uint, List<int>> ById = new();
    public Kernel.Bounds3 Bounds = Kernel.Bounds3.Empty;

    public uint AddPick(PickRef r, int priority, uint partId = 0, uint sketchId = 0)
    {
        uint i = (uint)PickTable.Count;
        PickTable.Add(new PickEntry { Ref = r, PartId = partId, SketchId = sketchId, Priority = priority });
        if (!ById.TryGetValue(r.Id, out var l)) ById[r.Id] = l = new List<int>(1);
        l.Add((int)i);
        return i;
    }

    public static uint Pack(Vector4 c) =>
        (uint)Math.Clamp((int)MathF.Round(c.X * 255), 0, 255)
        | (uint)Math.Clamp((int)MathF.Round(c.Y * 255), 0, 255) << 8
        | (uint)Math.Clamp((int)MathF.Round(c.Z * 255), 0, 255) << 16
        | (uint)Math.Clamp((int)MathF.Round(c.W * 255), 0, 255) << 24;
}

/// <summary>Что показывать в виде (кнопка «Опорные» и её список).</summary>
public sealed class ShowOptions
{
    public bool Planes = true, Axes = true, Origin = true, Points = true, Lines = true, Vertices;
    public bool Centerlines = true, Sketches = true, Dimensions = true, Annotations = true, Measurements = true;

    public ShowOptions Clone() => (ShowOptions)MemberwiseClone();
}

/// <summary>Стиль отображения тел.</summary>
public enum DisplayStyle
{
    Shaded,
    ShadedWithEdges,
    /// <summary>Только рёбра, невидимые скрыты.</summary>
    Wireframe,
    /// <summary>Рёбра; невидимые — штрихом.</summary>
    HiddenLines,
    /// <summary>Полупрозрачные тела, невидимые рёбра — штрихом.</summary>
    Transparent,
    /// <summary>u10: тонированный цветом и блеском материала детали (металлы — с отражением окружения).</summary>
    Material,
}
