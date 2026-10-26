using OpenTK.Mathematics;
using Parametric3D.App.Viewport;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Views;

/// <summary>
/// k115 (замечание 12:39, пп. 4–5): КОНТУРЫ ЭЛЕМЕНТОВ ДЕРЕВА ДЛЯ ВИДА. Операция — что она добавила (зелёным) и убрала
/// (красным): рёбра, появившиеся и исчезнувшие при ней (Part.MaterialDiff). Скрытый элемент — его очертание пунктиром
/// (эскиз — кривые, деталь — рёбра, плоскость — рамка, ось — отрезок, точка — крестик, операция — рёбра её граней).
/// </summary>
public static class TreeContours
{
    public static readonly Vector4 Added = new(0.25f, 0.80f, 0.35f, 1f), Removed = new(0.95f, 0.30f, 0.30f, 1f), Hidden = new(0.35f, 0.62f, 1f, 1f);

    public static ModelViewport.Contour? MaterialOf(ModelItem? it)
    {
        if (it is not Feature f || f.Parent is not Part p) return null;
        if (p.MaterialDiff(f) is not { } d || d.Lines.Count == 0) return null;
        return new ModelViewport.Contour(d.Lines, d.Adds ? Added : Removed);
    }

    static void Curve(List<(Vector3d, Vector3d)> l, Parametric3D.Kernel.Curves.ICurve3 c, int n)
    {
        var prev = c.PointAt(c.T0);
        for (int i = 1; i <= n; i++) { var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / n); l.Add((prev, q)); prev = q; }
    }

    public static ModelViewport.Contour? OutlineOf(ModelItem it)
    {
        var l = new List<(Vector3d A, Vector3d B)>();
        switch (it)
        {
            case Sketch sk:
                foreach (var e in sk.Entities)
                {
                    if (e is SkPoint) continue;
                    try { var c = e.ToCurve3(sk.Plane); Curve(l, c, c is Parametric3D.Kernel.Curves.LineCurve3 ? 1 : 48); } catch { }
                }
                break;
            case Part p when p.Mesh is { } md:
                foreach (var (_, pts) in md.EdgePolylines)
                    for (int i = 1; i < pts.Length; i++) l.Add((new Vector3d(pts[i - 1].X, pts[i - 1].Y, pts[i - 1].Z), new Vector3d(pts[i].X, pts[i].Y, pts[i].Z)));
                break;
            case DatumPlane dp:
            {
                double s = 40;
                var o = dp.Frame.Origin; var u = dp.Frame.U.Normalized() * s; var v = dp.Frame.V.Normalized() * s;
                var c = new[] { o - u - v, o + u - v, o + u + v, o - u + v };
                for (int i = 0; i < 4; i++) l.Add((c[i], c[(i + 1) % 4]));
                break;
            }
            case DatumAxis ax:
            {
                var d = ax.Direction.Normalized() * (ax.DisplayLength / 2);
                l.Add((ax.Origin - d, ax.Origin + d));
                break;
            }
            case DatumPoint pt:
                foreach (var d in new[] { Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ }) l.Add((pt.P - d * 3, pt.P + d * 3));
                break;
            case Feature f when f.Parent is Part fp:
            {
                var ids = f.CreatedFaceIds.ToHashSet();
                foreach (var e in fp.Bodies.SelectMany(b => b.Edges))
                    if (!e.Hidden && ((e.FaceA != null && ids.Contains(e.FaceA.Id)) || (e.FaceB != null && ids.Contains(e.FaceB.Id))))
                        Curve(l, e.Curve, e.Curve is Parametric3D.Kernel.Curves.LineCurve3 ? 1 : 32);
                break;
            }
        }
        return l.Count == 0 ? null : new ModelViewport.Contour(l, Hidden, 6);
    }
}
