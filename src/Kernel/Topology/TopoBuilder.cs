using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// Сборщик топологии: гарантирует, что совпадающие вершины и рёбра
/// переиспользуются, а не дублируются. Без этого нельзя ни корректно
/// отрисовать каркас, ни сделать скругление по ребру.
/// </summary>
public sealed class TopoBuilder
{
    private readonly List<TVertex> _verts = new();
    private readonly List<TEdge> _edges = new();
    private readonly double _weld;

    public TopoBuilder(double weld = Tol.Weld) { _weld = weld; }

    public TVertex Vertex(Vector3d p)
    {
        double w2 = _weld * _weld;
        for (int i = 0; i < _verts.Count; i++)
            if ((_verts[i].P - p).LengthSquared <= w2) return _verts[i];
        var v = new TVertex(p);
        _verts.Add(v);
        return v;
    }

    /// <summary>
    /// Ищет уже существующее ребро между теми же вершинами с геометрически
    /// совпадающей кривой; иначе создаёт новое. Возвращает ребро и признак
    /// того, что его нужно обходить в обратную сторону.
    /// </summary>
    public TEdge Edge(TVertex a, TVertex b, ICurve3 curve, out bool reversed)
    {
        foreach (var e in _edges)
        {
            if (e.V0 == a && e.V1 == b && SameCurve(e.Curve, curve, false)) { reversed = false; return e; }
            if (e.V0 == b && e.V1 == a && SameCurve(e.Curve, curve, true)) { reversed = true; return e; }
        }
        var ne = new TEdge { V0 = a, V1 = b, Curve = curve };
        a.Edges.Add(ne);
        if (b != a) b.Edges.Add(ne);
        _edges.Add(ne);
        reversed = false;
        return ne;
    }

    private static bool SameCurve(ICurve3 x, ICurve3 y, bool yReversed)
    {
        double w2 = Tol.Weld * Tol.Weld * 100.0;
        for (int i = 1; i < 4; i++)
        {
            double t = i / 4.0;
            Vector3d px = x.PointAt(t);
            Vector3d py = y.PointAt(yReversed ? 1.0 - t : t);
            if ((px - py).LengthSquared > w2) return false;
        }
        return true;
    }

    public IReadOnlyList<TEdge> AllEdges => _edges;
    public IReadOnlyList<TVertex> AllVertices => _verts;
}
