using OpenTK.Mathematics;

namespace Parametric3D.Render;

public enum ProjectionMode { Perspective = 0, Orthographic = 1 }

public enum ViewOrientation
{
    Isometric = 0, Front, Back, Left, Right, Top, Bottom, Custom
}

/// <summary>
/// Состояние камеры — простая интерполируемая структура.
///
/// В старом коде ViewSettings тащил на себе и матрицы, и режим редактирования,
/// и разрешение, и копировался вручную полем за полем в CopyTo/CopyFrom.
/// Здесь состояние — это struct: копирование бесплатно, интерполяция — одна
/// функция, режим редактирования сюда не лезет вообще.
/// </summary>
public struct CameraState
{
    public Quaternion Rotation;     // ориентация мира относительно камеры
    public Vector3 Target;          // точка, вокруг которой вращаемся
    public float Distance;          // расстояние камеры до Target (перспектива)
    public float OrthoHeight;       // высота окна ортогональной проекции
    public float Fovy;              // рад
    public ProjectionMode Mode;

    public static CameraState Default => new CameraState
    {
        Rotation = Quaternion.FromAxisAngle(Vector3.UnitX, -MathHelper.PiOver6) *
                   Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.PiOver4),
        Target = Vector3.Zero,
        Distance = 400f,
        OrthoHeight = 300f,
        Fovy = MathHelper.DegreesToRadians(35f),
        Mode = ProjectionMode.Perspective
    };

    public static CameraState Lerp(CameraState a, CameraState b, float t)
    {
        return new CameraState
        {
            Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t),
            Target = Vector3.Lerp(a.Target, b.Target, t),
            Distance = a.Distance + (b.Distance - a.Distance) * t,
            OrthoHeight = a.OrthoHeight + (b.OrthoHeight - a.OrthoHeight) * t,
            Fovy = a.Fovy + (b.Fovy - a.Fovy) * t,
            // Режим проекции переключается в середине перехода, чтобы не ломать матрицу.
            Mode = t < 0.5f ? a.Mode : b.Mode
        };
    }
}

/// <summary>Камера: превращает CameraState в матрицы и умеет бросать луч из пикселя.</summary>
public sealed class Camera
{
    public CameraState State = CameraState.Default;

    /// <summary>
    /// Ближняя и дальняя плоскости отсечения подбираются по расстоянию до
    /// точки взгляда.
    ///
    /// Раньше стояли константы 0.1 и 100000. Отношение 1 : 1 000 000 съедает
    /// почти всю точность буфера глубины: у перспективной проекции она падает
    /// как квадрат расстояния, и на удалении разница между гранью и её ребром
    /// перестаёт различаться. Отсюда и рёбра, проступающие сквозь тело.
    /// Плавающий диапазон держит отношение около 1 : 50 000 на любом масштабе.
    /// </summary>
    public float ZNear => MathF.Max(0.01f, State.Distance * 0.002f);
    public float ZFar => MathF.Max(1000f, State.Distance * 100f);
    public int ViewportWidth = 1024;
    public int ViewportHeight = 768;

    public float Aspect => ViewportHeight <= 0 ? 1f : ViewportWidth / (float)ViewportHeight;

    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Quaternion.Invert(State.Rotation));
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Quaternion.Invert(State.Rotation));
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Quaternion.Invert(State.Rotation));
    public Vector3 Position => State.Target - Forward * State.Distance;

    public Matrix4 ViewMatrix => Matrix4.LookAt(Position, State.Target, Up);

    public Matrix4 ProjMatrix
    {
        get
        {
            if (State.Mode == ProjectionMode.Orthographic)
            {
                float h = Math.Max(1e-4f, State.OrthoHeight);
                return Matrix4.CreateOrthographic(h * Aspect, h, -ZFar, ZFar);
            }
            return Matrix4.CreatePerspectiveFieldOfView(State.Fovy, Aspect, ZNear, ZFar);
        }
    }

    /// <summary>Пикселей экрана на одну модельную единицу в плоскости Target.</summary>
    public float WorldToPx
    {
        get
        {
            if (State.Mode == ProjectionMode.Orthographic)
                return ViewportHeight / Math.Max(1e-4f, State.OrthoHeight);
            float visible = 2f * State.Distance * MathF.Tan(State.Fovy * 0.5f);
            return ViewportHeight / Math.Max(1e-4f, visible);
        }
    }

    // ------------------------------------------------------------- навигация

    public void Orbit(float dxPixels, float dyPixels)
    {
        const float k = 0.006f;
        Quaternion qy = Quaternion.FromAxisAngle(Vector3.UnitY, dxPixels * k);
        Quaternion qx = Quaternion.FromAxisAngle(Vector3.UnitX, dyPixels * k);
        State.Rotation = qx * State.Rotation * qy;
        State.Rotation.Normalize();
    }

    public void Pan(float dxPixels, float dyPixels)
    {
        float s = 1f / Math.Max(1e-4f, WorldToPx);
        State.Target += (-Right * dxPixels + Up * dyPixels) * s;
    }

    /// <summary>Зум «к курсору»: точка под мышью остаётся на месте.</summary>
    public void Zoom(float delta, int mouseX, int mouseY)
    {
        float factor = MathF.Pow(1.12f, -delta);
        Vector3 before = ScreenToWorldOnTargetPlane(mouseX, mouseY);

        if (State.Mode == ProjectionMode.Orthographic)
            State.OrthoHeight = Math.Clamp(State.OrthoHeight * factor, 1e-3f, 1e7f);
        else
            State.Distance = Math.Clamp(State.Distance * factor, 1e-3f, 1e7f);

        Vector3 after = ScreenToWorldOnTargetPlane(mouseX, mouseY);
        State.Target += before - after;
    }

    public void ZoomToFit(Vector3 min, Vector3 max, float margin = 1.25f)
    {
        Vector3 c = (min + max) * 0.5f;
        float d = (max - min).Length;
        if (d < 1e-4f) d = 100f;
        State.Target = c;
        State.OrthoHeight = d * margin;
        State.Distance = d * margin / MathF.Max(1e-3f, MathF.Tan(State.Fovy * 0.5f)) * 0.5f;
    }

    public static Quaternion OrientationQuat(ViewOrientation o) => o switch
    {
        ViewOrientation.Front => Quaternion.Identity,
        ViewOrientation.Back => Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.Pi),
        ViewOrientation.Right => Quaternion.FromAxisAngle(Vector3.UnitY, -MathHelper.PiOver2),
        ViewOrientation.Left => Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.PiOver2),
        ViewOrientation.Top => Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.PiOver2),
        ViewOrientation.Bottom => Quaternion.FromAxisAngle(Vector3.UnitX, -MathHelper.PiOver2),
        ViewOrientation.Isometric => Quaternion.FromAxisAngle(Vector3.UnitX, -MathHelper.PiOver6) *
                                     Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.PiOver4),
        _ => Quaternion.Identity
    };

    // --------------------------------------------------------------- лучи

    /// <summary>Луч из пикселя экрана в мир.</summary>
    public void ScreenRay(int px, int py, out Vector3 origin, out Vector3 dir)
    {
        float nx = 2f * px / Math.Max(1, ViewportWidth) - 1f;
        float ny = 1f - 2f * py / Math.Max(1, ViewportHeight);

        Matrix4 inv = ViewMatrix * ProjMatrix;
        inv.Invert();

        Vector4 pNear = new Vector4(nx, ny, -1f, 1f) * inv;
        Vector4 pFar = new Vector4(nx, ny, 1f, 1f) * inv;
        Vector3 a = pNear.Xyz / pNear.W;
        Vector3 b = pFar.Xyz / pFar.W;

        origin = a;
        dir = (b - a).Normalized();
    }

    /// <summary>Точка на плоскости, проходящей через Target перпендикулярно взгляду.</summary>
    public Vector3 ScreenToWorldOnTargetPlane(int px, int py)
        => ScreenToWorldOnPlane(px, py, State.Target, -Forward);

    public Vector3 ScreenToWorldOnPlane(int px, int py, Vector3 planeOrigin, Vector3 planeNormal)
    {
        ScreenRay(px, py, out Vector3 ro, out Vector3 rd);
        float den = Vector3.Dot(rd, planeNormal);
        if (MathF.Abs(den) < 1e-9f) return planeOrigin;
        float t = Vector3.Dot(planeOrigin - ro, planeNormal) / den;
        return ro + rd * t;
    }
}
