using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model.Params;
using Parametric3D.Model.Threads;

namespace Parametric3D.Model.Features;

/// <summary>Как задана длина резьбы: на длину (поле) или до выбранного (грань, вершина, точка).</summary>
public enum ThreadLength { Blind, UpTo }

/// <summary>
/// РЕЗЬБА на круглой грани (наружной или в отверстии): косметическая (обозначение — винтовая линия в виде, тело не меняется)
/// или настоящая — вырез профиля впадины из таблицы резьб (threads_db.json) винтовой протяжкой. Начало — у выбранного
/// (без выбора — у торца грани), длина — числом (0 — вся грань) или до выбранного; плавный выход — профиль на последнем
/// витке уходит из материала; левая — винт в другую сторону. Диаметр и вид (наружная/внутренняя) — с грани.
/// </summary>
public sealed partial class CosmeticThreadFeature : Feature
{
    public readonly List<Vector3d> OnFaces = new();
    public readonly List<string?> OnFaceNames = new();
    /// <summary>Шаг косметической резьбы без типа из таблицы.</summary>
    public ValueRef Pitch = new(1.5);
    /// <summary>Длина резьбы «на длину»; 0 — во всю грань.</summary>
    public ValueRef Length = new(0);
    /// <summary>Косметическая (по умолчанию) или вырез.</summary>
    public bool Cosmetic = true;
    /// <summary>Тип из таблицы резьб (ThreadSpec.Id); null — без типа (только косметическая, шаг — Pitch).</summary>
    public string? TypeId;
    /// <summary>Начало резьбы: точка на торце, вершина или опорная точка (по оси); null — торец грани.</summary>
    public Vector3d? StartAt;
    public ThreadLength Mode = ThreadLength.Blind;
    /// <summary>Конец «до выбранного»: точка (по оси).</summary>
    public Vector3d? EndAt;
    public bool SmoothExit;
    public bool LeftHand;

    /// <summary>Найдено при перестроении: диаметр грани, внутренняя ли резьба, ось, длина грани и резьбы.</summary>
    public double Diameter { get; private set; }
    public bool Internal { get; private set; }
    public Vector3d AxisAt { get; private set; }
    public Vector3d AxisDir { get; private set; }
    public double FaceLength { get; private set; }
    public double ThreadLen { get; private set; }
    /// <summary>Отрезки винтовой линии (и контура профиля у начала) последнего построения — для вида.</summary>
    public IReadOnlyList<(Vector3d A, Vector3d B)>? Lines { get; private set; }
    /// <summary>Панель операции открыта: винтовая линия видна и у настоящей резьбы (предпросмотр).</summary>
    public bool ShowPreview;

    public CosmeticThreadFeature(string name = "Thread") { Name = name; }

    public ThreadSpec? Spec => ThreadDb.Find(TypeId);
    public override string Summary => Spec is { } s ? $"Thread {s.Designation}{(Cosmetic ? "" : " cut")}" : $"Thread {(Internal ? "M" : "")}{Diameter:0.##}x{Pitch}";
}
