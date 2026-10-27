using Avalonia;
using OpenTK.Mathematics;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Render;

namespace Parametric3D.App.Viewport;

/// <summary>
/// k120 (замечание 15:06, п. 17): ЗАМЕРЫ НА ВИДЕ. Каждая операция «Замер» детали рисуется размером: выносные линии от
/// замеренных точек, размерная линия со стрелками на выносе Offset (единицы модели) в сторону Side, значение — в
/// единицах системы детали. Подпись тянется мышью: точка курсора ложится на плоскость размера (через A, натянутую на AB
/// и Side), вынос — её проекция на Side; отпустили — документ изменён (MeasureMoved). Показ — флажок «Показывать:
/// Измерения». Мутации: P3D_MEASUREVIEW=0 — замеры не рисуются; P3D_MEASUREDRAG=0 — подпись не тянется.
/// </summary>
public sealed partial class ModelViewport
{
    public static readonly bool MeasureViewOff = Environment.GetEnvironmentVariable("P3D_MEASUREVIEW") == "0";
    public static readonly bool MeasureDragOff = Environment.GetEnvironmentVariable("P3D_MEASUREDRAG") == "0";

    /// <summary>Подпись замера отпущена после перетаскивания.</summary>
    public event Action<Part, MeasureFeature>? MeasureMoved;
    /// <summary>Нарисовано замеров в последнем кадре (проверки).</summary>
    public int MeasuresDrawn { get; private set; }

    readonly List<(Part Part, MeasureFeature M, Vector3d Mid, double HalfW)> _measureLabels = new();
    (Part Part, MeasureFeature M)? _measureDrag;

    static Matrix4 WorldOf(ModelItem? it)
    {
        var m = Matrix4.Identity;
        for (; it != null; it = it.Parent)
            m = m * (it switch { Part p => p.LocalTransform, Assembly a => a.LocalTransform, _ => Matrix4.Identity });
        return m;
    }
    static Vector3d Xf(Matrix4 m, Vector3d v)
    {
        var r = new Vector4((float)v.X, (float)v.Y, (float)v.Z, 1f) * m;
        return new Vector3d(r.X, r.Y, r.Z);
    }
    static Vector3d XfDir(Matrix4 m, Vector3d v)
    {
        var r = new Vector4((float)v.X, (float)v.Y, (float)v.Z, 0f) * m;
        return new Vector3d(r.X, r.Y, r.Z);
    }

    /// <summary>Замеры видимых деталей (не погашенные, до точки останова).</summary>
    public IEnumerable<(Part Part, MeasureFeature M)> VisibleMeasures()
    {
        if (_doc == null || !Show.Measurements) yield break;
        foreach (var part in Parts(_doc.Root))
        {
            if (!part.Visible) continue;
            int stage = part.EffectiveStage;
            for (int i = 0; i < stage && i < part.Features.Count; i++)
                if (part.Features[i] is MeasureFeature m && !m.Suppressed && m.Value > 0) yield return (part, m);
        }
    }

    private void AddMeasureDims(List<Render.Engine.LineInst> lines)
    {
        _measureLabels.Clear();
        MeasuresDrawn = 0;
        if (MeasureViewOff || ActiveSketch != null) return;
        static Vector3 F(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
        double px = PixelSize;
        var camR = Camera.Right; var camU = Camera.Up;
        var R = new Vector3d(camR.X, camR.Y, camR.Z).Normalized(); var U = new Vector3d(camU.X, camU.Y, camU.Z).Normalized();
        foreach (var (part, ms) in VisibleMeasures())
        {
            var w = WorldOf(part);
            var A = Xf(w, ms.A); var B = Xf(w, ms.B);
            var side = XfDir(w, ms.Side); side = side.LengthSquared > 1e-18 ? side.Normalized() : R;
            bool hot = _measureDrag is { } d && ReferenceEquals(d.M, ms);
            var col = ms.Stale ? new Vector4(0.9f, 0.45f, 0.2f, 1f) : hot ? _r.Theme.Select : new Vector4(0.15f, 0.55f, 0.95f, 1f);
            col.W = 1;
            double off = ms.Offset;
            var a1 = A + side * off; var b1 = B + side * off;
            double over = 6 * px * Math.Sign(off == 0 ? 1 : off);
            lines.Add(Inst(F(A), F(a1 + side * over), col, 1.1f, 0));
            lines.Add(Inst(F(B), F(b1 + side * over), col, 1.1f, 0));
            lines.Add(Inst(F(a1), F(b1), col, 1.2f, 0));
            var ab = b1 - a1; double len = ab.Length;
            if (len > 1e-12)
            {
                var t = ab / len;
                double ah = Math.Min(10 * px, len * 0.3), aw = ah * 0.35;
                var n = Vector3d.Cross(t, Vector3d.Cross(R, U)); if (n.LengthSquared < 1e-18) n = side; n = n.Normalized();
                foreach (var (tip, dir) in new[] { (a1, t), (b1, -t) })
                {
                    lines.Add(Inst(F(tip), F(tip + dir * ah + n * aw), col, 1.3f, 0));
                    lines.Add(Inst(F(tip), F(tip + dir * ah - n * aw), col, 1.3f, 0));
                }
            }
            string text = Commands.DisplayUnits.Format(ms.Value, Core.Dimension.Length, part.Units, "0.###");
            double h = SketchDimensions.TextHeightPx * px;
            var mid = (a1 + b1) / 2;
            var strokes = new List<List<Vector2d>>();
            double tw = StrokeFont.Width(text) * h;
            StrokeFont.LayoutFill(text, new Vector2d(-tw / 2, 3 * px), Vector2d.UnitX, Vector2d.UnitY, h, 0.7 / SketchDimensions.TextHeightPx, px * 0.4, strokes);
            if (strokes.Count == 0) StrokeFont.Layout(text, new Vector2d(-tw / 2, 3 * px), Vector2d.UnitX, Vector2d.UnitY, h, strokes);
            foreach (var pl in strokes)
                for (int i = 1; i < pl.Count; i++)
                    lines.Add(Inst(F(mid + R * pl[i - 1].X + U * pl[i - 1].Y), F(mid + R * pl[i].X + U * pl[i].Y), col, Core.Settings.Current.DimensionTextOutlineWidth, 0));
            _measureLabels.Add((part, ms, mid + U * (3 * px + h / 2), tw / px / 2));
            MeasuresDrawn++;
        }
    }

    /// <summary>Замер, чья подпись (или размерная линия) под точкой экрана p.</summary>
    public (Part Part, MeasureFeature M)? MeasureLabelAt(Point p)
    {
        foreach (var (part, m, mid, halfW) in _measureLabels)
            if (Project(mid) is { } q && Math.Abs(q.X - p.X) <= halfW + 6 && Math.Abs(q.Y - p.Y) <= SketchDimensions.TextHeightPx * 0.5 + 6)
                return (part, m);
        return null;
    }

    /// <summary>Экранная точка подписи замера (проверки).</summary>
    public Point? MeasureLabelPoint(MeasureFeature m)
    {
        foreach (var l in _measureLabels) if (ReferenceEquals(l.M, m)) return Project(l.Mid);
        return null;
    }

    private bool BeginMeasureDrag(Point p)
    {
        if (MeasureDragOff || ActiveSketch != null || Tool != null) return false;
        if (MeasureLabelAt(p) is not { } hit) return false;
        _measureDrag = hit;
        return true;
    }

    /// <summary>Перетаскивание подписи замера: курсор → плоскость размера → вынос вдоль Side (единицы модели).</summary>
    public void MeasureDragMove(Point p)
    {
        if (_measureDrag is not { } d) return;
        var w = WorldOf(d.Part);
        var A = Xf(w, d.M.A); var B = Xf(w, d.M.B);
        var side = XfDir(w, d.M.Side).Normalized();
        var nrm = Vector3d.Cross(B - A, side);
        if (nrm.LengthSquared < 1e-18) { var f = Camera.Forward; nrm = new Vector3d(f.X, f.Y, f.Z); }
        var px = Px(p);
        var q = Camera.ScreenToWorldOnPlane(px.X, px.Y, new Vector3((float)A.X, (float)A.Y, (float)A.Z), new Vector3((float)nrm.X, (float)nrm.Y, (float)nrm.Z));
        double off = Vector3d.Dot(new Vector3d(q.X, q.Y, q.Z) - A, side);
        // вынос — в единицах модели; масштаб детали в сборке не меняется (поворот и сдвиг)
        if (double.IsFinite(off)) d.M.Offset = off;
        _overlayDirty = true; Redraw();
    }

    private bool EndMeasureDrag()
    {
        if (_measureDrag is not { } d) return false;
        _measureDrag = null;
        bool moved = Math.Abs(_last.X - _down.X) + Math.Abs(_last.Y - _down.Y) > 3;
        _overlayDirty = true; Redraw();
        if (moved) MeasureMoved?.Invoke(d.Part, d.M);
        return moved;
    }
}
