using Avalonia;
using Avalonia.Input;
using OpenTK.Mathematics;
using Parametric3D.Render.Engine;

namespace Parametric3D.App.Viewport;

/// <summary>MoveRotate (k103, ручки сечения): стрелка вдоль Z — перенос, кольца вокруг X и Y — поворот.</summary>
public enum GizmoMode { Move, Rotate, Scale, MoveRotate }

/// <summary>Часть гизмо: ось (0–2), плоскость поперёк оси (3–5: YZ, XZ, XY), угол / центр (6 — пропорционально, свободно).</summary>
public enum GizmoPart { None = -1, AxisX = 0, AxisY = 1, AxisZ = 2, PlaneYZ = 3, PlaneXZ = 4, PlaneXY = 5, Uniform = 6 }

/// <summary>Гизмо: вид, начало, оси рамки (единичные, взаимно перпендикулярные).</summary>
public sealed record GizmoSpec(GizmoMode Mode, Vector3d Origin, Vector3d X, Vector3d Y, Vector3d Z)
{
    /// <summary>k99: какие части видны и берутся (биты 1 &lt;&lt; GizmoPart); 0 — все.</summary>
    public int Mask { get; init; }
    public bool Has(GizmoPart p) => Mask == 0 || p != GizmoPart.None && (Mask & (1 << (int)p)) != 0;
}

/// <summary>
/// Перетаскивание гизмо — накопленное от нажатия: Delta — сдвиг в осях рамки (ось — одна составляющая, плоскость — две,
/// угол масштаба — вдоль диагонали (1,1,1)/√3, центр переноса — в плоскости вида), AngleDeg — поворот вокруг оси
/// (правило правой руки), Mods — SHIFT / CTRL.
/// </summary>
public readonly record struct GizmoDrag(GizmoPart Part, Vector3d Delta, double AngleDeg, KeyModifiers Mods);

/// <summary>
/// 13:09: ГИЗМО ПЕРЕНОСА, ПОВОРОТА И МАСШТАБА (FreeForm): стрелки осей X/Y/Z, квадраты плоскостей XY/XZ/YZ, угол —
/// пропорциональный масштаб (у переноса — центр: свободно в плоскости вида), у поворота — три кольца. Размер постоянный
/// на экране (≈ 90 пикселей). Нажали на часть и тянут: ось — по оси, плоскость — по плоскости, кольцо — угол вокруг
/// начала на экране. Операция (FFTransformOp) получает накопленное перемещение и сама пересчитывает параметры.
/// </summary>
public sealed partial class ModelViewport
{
    private GizmoSpec? _gizmo;
    private GizmoPart _gzHover = GizmoPart.None, _gzActive = GizmoPart.None;
    private bool _gzStarted;
    private Point _gzDown;
    private Vector2 _gzAxis;
    private double _gzWorldPerPx;
    private Vector3d _gzPlaneStart;
    private GizmoSpec? _gzStartSpec;

    public event Action<GizmoPart>? GizmoDragStarted;
    public event Action<GizmoDrag>? GizmoDragged;
    public event Action? GizmoDragEnded;

    public GizmoSpec? Gizmo
    {
        get => _gizmo;
        set { _gizmo = value; if (value == null) { _gzHover = GizmoPart.None; } _overlayDirty = true; Redraw(); }
    }
    public GizmoPart GizmoHover => _gzHover;
    public bool GizmoDragging => _gzActive != GizmoPart.None && _gzStarted;

    private double GizmoLen => 90 * PixelSize;

    private static Vector3 F(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    private Vector3d GzAxis(GizmoSpec g, int i) => i == 0 ? g.X : i == 1 ? g.Y : g.Z;
    /// <summary>Часть — кольцо поворота (у Rotate — все оси, у MoveRotate — X и Y).</summary>
    private static bool GzRot(GizmoSpec g, GizmoPart part) => g.Mode == GizmoMode.Rotate || g.Mode == GizmoMode.MoveRotate && part is GizmoPart.AxisX or GizmoPart.AxisY;
    /// <summary>Часть есть у гизмо (MoveRotate: кольца X, Y и стрелка Z).</summary>
    private static bool GzHas(GizmoSpec g, GizmoPart part) => g.Has(part) && (g.Mode != GizmoMode.MoveRotate || part is GizmoPart.AxisX or GizmoPart.AxisY or GizmoPart.AxisZ);

    /// <summary>Квадрат плоскости поперёк оси k: углы в мире.</summary>
    private Vector3d[] GzPlaneQuad(GizmoSpec g, int k)
    {
        double L = GizmoLen;
        var a = GzAxis(g, (k + 1) % 3); var b = GzAxis(g, (k + 2) % 3);
        double r0 = 0.28 * L, r1 = 0.48 * L;
        return new[] { g.Origin + a * r0 + b * r0, g.Origin + a * r1 + b * r0, g.Origin + a * r1 + b * r1, g.Origin + a * r0 + b * r1 };
    }

    private Vector3d GzCorner(GizmoSpec g) => g.Origin + (g.X + g.Y + g.Z) * (0.62 * GizmoLen);

    private List<Vector3d> GzRing(GizmoSpec g, int k)
    {
        var a = GzAxis(g, (k + 1) % 3); var b = GzAxis(g, (k + 2) % 3);
        double r = 0.82 * GizmoLen;
        var pts = new List<Vector3d>(65);
        for (int i = 0; i <= 64; i++) { double t = i * Math.PI * 2 / 64; pts.Add(g.Origin + a * (r * Math.Cos(t)) + b * (r * Math.Sin(t))); }
        return pts;
    }

    private static double SegDist(Point p, Point a, Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, l2 = vx * vx + vy * vy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / l2, 0, 1);
        double dx = a.X + vx * t - p.X, dy = a.Y + vy * t - p.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool InQuad(Point p, Point[] q)
    {
        // выпуклый четырёхугольник: знак векторного произведения у всех сторон одинаковый
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i]; var b = q[(i + 1) % 4];
            double c = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            int s = Math.Sign(c);
            if (s == 0) continue;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>Часть гизмо под курсором.</summary>
    public GizmoPart GizmoHit(Point p) => _gizmo is { } gm && GizmoHitAll(p) is var h && gm.Has(h) ? h : GizmoPart.None;

    private GizmoPart GizmoHitAll(Point p)
    {
        if (_gizmo is not { } g || ActiveSketch != null || Tool != null) return GizmoPart.None;
        double L = GizmoLen;
        if (ToScreen(F(g.Origin)) is not { } o) return GizmoPart.None;
        double Dist(Point a) => Math.Sqrt((a.X - p.X) * (a.X - p.X) + (a.Y - p.Y) * (a.Y - p.Y));
        if (g.Mode is GizmoMode.Rotate or GizmoMode.MoveRotate)
        {
            GizmoPart best = GizmoPart.None; double bd = 7;
            // MoveRotate: наконечник и стержень стрелки Z — перенос
            if (g.Mode == GizmoMode.MoveRotate && GzHas(g, GizmoPart.AxisZ) && ToScreen(F(g.Origin)) is { } zo && ToScreen(F(g.Origin + g.Z * L)) is { } zt)
                if (Dist(zt) < 10 || SegDist(p, zo, zt) < 6) return GizmoPart.AxisZ;
            for (int k = 0; k < 3; k++)
            {
                if (!g.Has((GizmoPart)k) || !GzRot(g, (GizmoPart)k)) continue;
                var ring = GzRing(g, k);
                Point? prev = null;
                foreach (var w in ring)
                {
                    if (ToScreen(F(w)) is not { } s) { prev = null; continue; }
                    if (prev is { } pv) { double d = SegDist(p, pv, s); if (d < bd) { bd = d; best = (GizmoPart)k; } }
                    prev = s;
                }
            }
            return best;
        }
        // угол (масштаб) / центр (перенос)
        if (g.Mode == GizmoMode.Scale) { if (ToScreen(F(GzCorner(g))) is { } c && Dist(c) < 9) return GizmoPart.Uniform; }
        else if (Dist(o) < 9) return GizmoPart.Uniform;
        // наконечники осей
        for (int i = 0; i < 3; i++)
            if (ToScreen(F(g.Origin + GzAxis(g, i) * L)) is { } tip && Dist(tip) < 10) return (GizmoPart)i;
        // квадраты плоскостей
        for (int k = 0; k < 3; k++)
        {
            var q = GzPlaneQuad(g, k).Select(w => ToScreen(F(w))).ToArray();
            if (q.All(x => x != null) && InQuad(p, q.Select(x => x!.Value).ToArray())) return (GizmoPart)(3 + k);
        }
        // стержни осей
        GizmoPart ba = GizmoPart.None; double bdist = 6;
        for (int i = 0; i < 3; i++)
            if (ToScreen(F(g.Origin + GzAxis(g, i) * (0.15 * L))) is { } a && ToScreen(F(g.Origin + GzAxis(g, i) * L)) is { } b)
            { double d = SegDist(p, a, b); if (d < bdist) { bdist = d; ba = (GizmoPart)i; } }
        return ba;
    }

    private bool UpdateGizmoHover(Point p)
    {
        if (_gizmo == null) return false;
        var h = GizmoHit(p);
        if (h != _gzHover) { _gzHover = h; _overlayDirty = true; Redraw(); }
        return h != GizmoPart.None;
    }

    private bool RayPlane(Point p, Vector3d at, Vector3d n, out Vector3d hit)
    {
        var px = Px(p);
        Camera.ScreenRay(px.X, px.Y, out var ro, out var rd);
        var o = new Vector3d(ro.X, ro.Y, ro.Z); var d = new Vector3d(rd.X, rd.Y, rd.Z);
        double den = Vector3d.Dot(d, n);
        hit = default;
        if (Math.Abs(den) < 1e-9 * Math.Max(1, d.Length)) return false;
        double t = Vector3d.Dot(at - o, n) / den;
        hit = o + d * t;
        return true;
    }

    private Vector3d GzPlaneNormal(GizmoSpec g, GizmoPart part)
    {
        if (part == GizmoPart.Uniform) { var f = Camera.Forward; return new Vector3d(f.X, f.Y, f.Z).Normalized(); }
        return GzAxis(g, (int)part - 3);
    }

    private void BeginGizmoDrag(Point p, GizmoPart part)
    {
        _gzActive = part; _gzStarted = false; _gzDown = p; _gzAxis = Vector2.Zero;
        _gzStartSpec = _gizmo;
        if (_gizmo is not { } g) return;
        double L = GizmoLen;
        Vector3d? dir = part switch
        {
            GizmoPart.AxisX or GizmoPart.AxisY or GizmoPart.AxisZ when !GzRot(g, part) => GzAxis(g, (int)part),
            GizmoPart.Uniform when g.Mode == GizmoMode.Scale => (g.X + g.Y + g.Z).Normalized(),
            _ => null,
        };
        if (dir is { } dv && ToScreen(F(g.Origin)) is { } a && ToScreen(F(g.Origin + dv * L)) is { } b)
        {
            var ax = new Vector2((float)(b.X - a.X), (float)(b.Y - a.Y));
            if (ax.Length > 1e-3f) { _gzWorldPerPx = L / ax.Length; _gzAxis = ax / ax.Length; }
        }
        if (!GzRot(g, part) && (part >= GizmoPart.PlaneYZ && part <= GizmoPart.PlaneXY || part == GizmoPart.Uniform && g.Mode == GizmoMode.Move))
            RayPlane(p, g.Origin, GzPlaneNormal(g, part), out _gzPlaneStart);
        _overlayDirty = true; Redraw();
    }

    /// <summary>Накопленное перетаскивание от нажатия до точки p.</summary>
    private GizmoDrag? GizmoDragAt(Point p, KeyModifiers mods)
    {
        if (_gzStartSpec is not { } g || _gzActive == GizmoPart.None) return null;
        var part = _gzActive;
        if (GzRot(g, part))
        {
            if (ToScreen(F(g.Origin)) is not { } o) return null;
            double a0 = Math.Atan2(-(_gzDown.Y - o.Y), _gzDown.X - o.X), a1 = Math.Atan2(-(p.Y - o.Y), p.X - o.X);
            double da = (a1 - a0) * 180 / Math.PI;
            while (da > 180) da -= 360;
            while (da < -180) da += 360;
            var f = Camera.Forward;
            var ax = GzAxis(g, (int)part);
            // ось смотрит на зрителя — поворот против часовой на экране положительный
            double toward = -Vector3d.Dot(ax, new Vector3d(f.X, f.Y, f.Z));
            if (toward < 0) da = -da;
            if (mods.HasFlag(KeyModifiers.Control)) da = Math.Round(da / 15) * 15;
            return new GizmoDrag(part, Vector3d.Zero, Math.Round(da, 2), mods);
        }
        if (part <= GizmoPart.AxisZ || part == GizmoPart.Uniform && g.Mode == GizmoMode.Scale)
        {
            if (_gzAxis == Vector2.Zero) return null;
            double moved = ((p.X - _gzDown.X) * _gzAxis.X + (p.Y - _gzDown.Y) * _gzAxis.Y) * _gzWorldPerPx;
            var d = part == GizmoPart.Uniform ? new Vector3d(moved, moved, moved) / Math.Sqrt(3)
                : part == GizmoPart.AxisX ? new Vector3d(moved, 0, 0) : part == GizmoPart.AxisY ? new Vector3d(0, moved, 0) : new Vector3d(0, 0, moved);
            return new GizmoDrag(part, d, 0, mods);
        }
        if (!RayPlane(p, g.Origin, GzPlaneNormal(g, part), out var hit)) return null;
        var w = hit - _gzPlaneStart;
        var fr = new Vector3d(Vector3d.Dot(w, g.X), Vector3d.Dot(w, g.Y), Vector3d.Dot(w, g.Z));
        if (part != GizmoPart.Uniform) { int k = (int)part - 3; if (k == 0) fr.X = 0; else if (k == 1) fr.Y = 0; else fr.Z = 0; }
        return new GizmoDrag(part, fr, 0, mods);
    }

    private void GizmoDragMove(Point p, KeyModifiers mods)
    {
        if (_gzActive == GizmoPart.None) return;
        if (!_gzStarted)
        {
            if (Math.Abs(p.X - _gzDown.X) + Math.Abs(p.Y - _gzDown.Y) < 3) return;
            _gzStarted = true;
            GizmoDragStarted?.Invoke(_gzActive);
        }
        if (GizmoDragAt(p, mods) is { } d) GizmoDragged?.Invoke(d);
    }

    private void EndGizmoDrag()
    {
        bool was = _gzStarted;
        _gzActive = GizmoPart.None; _gzStarted = false; _gzStartSpec = null;
        _overlayDirty = true; Redraw();
        if (was) GizmoDragEnded?.Invoke();
    }

    /// <summary>Потянуть гизмо (сценарии проверок): часть и накопленное перемещение; end=false — не отпускать.</summary>
    public void SimulateGizmoDrag(GizmoDrag d, bool end = true)
    {
        if (!_gzStarted) { _gzActive = d.Part; _gzStartSpec = _gizmo; _gzStarted = true; GizmoDragStarted?.Invoke(d.Part); }
        GizmoDragged?.Invoke(d);
        if (end) EndGizmoDrag();
    }

    private void AddGizmoOverlay(List<LineInst> lines, List<PointInst> pts)
    {
        if (_gizmo is not { } g || ActiveSketch != null) return;
        var th = _r.Theme;
        var hot = new Vector4(1f, 0.82f, 0.18f, 1);
        var cols = new[] { th.AxisX, th.AxisY, th.AxisZ };
        var on = _gzActive != GizmoPart.None ? _gzActive : _gzHover;
        Vector4 C(GizmoPart part, Vector4 c) => on == part ? hot : c;
        float L = (float)GizmoLen;
        var o = F(g.Origin);
        var view = Camera.Forward;
        if (g.Mode is GizmoMode.Rotate or GizmoMode.MoveRotate)
        {
            for (int k = 0; k < 3; k++)
            {
                if (!g.Has((GizmoPart)k) || !GzRot(g, (GizmoPart)k)) continue;
                var ring = GzRing(g, k);
                var c = C((GizmoPart)k, cols[k]);
                for (int i = 0; i + 1 < ring.Count; i++) lines.Add(Inst(F(ring[i]), F(ring[i + 1]), c, on == (GizmoPart)k ? 3f : 2f, 0));
            }
            if (g.Mode == GizmoMode.MoveRotate && GzHas(g, GizmoPart.AxisZ))
            {
                var ax = F(g.Z);
                var c = C(GizmoPart.AxisZ, cols[2]);
                var tip = o + ax * L;
                lines.Add(Inst(o + ax * (0.15f * L), tip, c, on == GizmoPart.AxisZ ? 3.2f : 2.4f, 0));
                var side = Vector3.Cross(ax, view);
                if (side.LengthSquared < 1e-8f) side = Vector3.Cross(ax, Vector3.UnitY);
                if (side.LengthSquared < 1e-8f) side = Vector3.Cross(ax, Vector3.UnitX);
                side = side.Normalized();
                float h = L * 0.2f;
                lines.Add(Inst(tip, tip - ax * h + side * (h * 0.45f), c, 2.4f, 0));
                lines.Add(Inst(tip, tip - ax * h - side * (h * 0.45f), c, 2.4f, 0));
                pts.Add(new PointInst { P = tip, Color = SceneData.Pack(c), Px = 7, Shape = 0 });
            }
            pts.Add(new PointInst { P = o, Color = SceneData.Pack(new Vector4(0.92f, 0.93f, 0.95f, 1)), Px = 6, Shape = 0 });
            return;
        }
        for (int i = 0; i < 3; i++)
        {
            if (!g.Has((GizmoPart)i)) continue;
            var ax = F(GzAxis(g, i));
            var c = C((GizmoPart)i, cols[i]);
            var tip = o + ax * L;
            lines.Add(Inst(o + ax * (0.15f * L), tip, c, on == (GizmoPart)i ? 3.2f : 2.4f, 0));
            if (g.Mode == GizmoMode.Move)
            {
                var side = Vector3.Cross(ax, view);
                if (side.LengthSquared < 1e-8f) side = Vector3.Cross(ax, Vector3.UnitY);
                if (side.LengthSquared < 1e-8f) side = Vector3.Cross(ax, Vector3.UnitX);
                side = side.Normalized();
                float h = L * 0.2f;
                lines.Add(Inst(tip, tip - ax * h + side * (h * 0.45f), c, 2.4f, 0));
                lines.Add(Inst(tip, tip - ax * h - side * (h * 0.45f), c, 2.4f, 0));
                pts.Add(new PointInst { P = tip, Color = SceneData.Pack(c), Px = 7, Shape = 0 });
            }
            else pts.Add(new PointInst { P = tip, Color = SceneData.Pack(c), Px = 11, Shape = 1 });
        }
        for (int k = 0; k < 3; k++)
        {
            if (!g.Has((GizmoPart)(3 + k))) continue;
            var q = GzPlaneQuad(g, k);
            var c = C((GizmoPart)(3 + k), cols[k]);
            for (int i = 0; i < 4; i++) lines.Add(Inst(F(q[i]), F(q[(i + 1) % 4]), c, on == (GizmoPart)(3 + k) ? 2.6f : 1.6f, 0));
            // заливка — диагонали (лёгкая штриховка), чтобы квадрат читался как площадка
            lines.Add(Inst(F(q[0]), F(q[2]), c, 1f, 4));
        }
        var white = new Vector4(0.92f, 0.93f, 0.95f, 1);
        if (g.Mode == GizmoMode.Scale)
        {
            var corner = F(GzCorner(g));
            lines.Add(Inst(o, corner, C(GizmoPart.Uniform, white), 1.2f, 5));
            pts.Add(new PointInst { P = corner, Color = SceneData.Pack(C(GizmoPart.Uniform, white)), Px = 13, Shape = 1 });
            pts.Add(new PointInst { P = o, Color = SceneData.Pack(white), Px = 5, Shape = 0 });
        }
        else if (g.Has(GizmoPart.Uniform)) pts.Add(new PointInst { P = o, Color = SceneData.Pack(C(GizmoPart.Uniform, white)), Px = 11, Shape = 0 });
    }
}
