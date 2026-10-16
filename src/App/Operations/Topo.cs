using OpenTK.Mathematics;
using Parametric3D.App.Sketching;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Sketching;

namespace Parametric3D.App.Operations;

/// <summary>Топология для выбора и ручек: касательные цепочки рёбер, нормали, контур грани в эскиз.</summary>
public static class Topo
{
    /// <summary>Касание по углу: рёбра в общей вершине идут одним направлением (до 1°).</summary>
    private const double TangentCos = 0.99985;

    /// <summary>Направление ребра в его вершине (единичное), наружу от вершины не важно — сравнивается по модулю.</summary>
    private static Vector3d? DirAt(TEdge e, TVertex v)
    {
        var c = e.Curve;
        if (c == null) return null;
        double t = (c.PointAt(c.T0) - v.P).LengthSquared <= (c.PointAt(c.T1) - v.P).LengthSquared ? c.T0 : c.T1;
        var d = c.TangentAt(t);
        return d.LengthSquared > 1e-24 ? d.Normalized() : null;
    }

    /// <summary>П. 4: ребро и все рёбра, касательно продолжающие его через вершины (цепочка G1).</summary>
    public static List<TEdge> TangentChain(TEdge start)
    {
        var res = new List<TEdge> { start };
        var seen = new HashSet<TEdge> { start };
        var queue = new Queue<TEdge>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (v == null || DirAt(e, v) is not { } d) continue;
                foreach (var o in v.Edges)
                {
                    if (seen.Contains(o) || o.V0 == null || o.V1 == null) continue;
                    if (DirAt(o, v) is not { } d2) continue;
                    if (Math.Abs(Vector3d.Dot(d, d2)) < TangentCos) continue;
                    seen.Add(o); res.Add(o); queue.Enqueue(o);
                }
            }
        }
        return res;
    }

    public static List<PickRef> ChainRefs(Part part, PickRef r)
    {
        if (part.FindEdge(r.Id) is not { } e) return new() { r };
        var chain = TangentChain(e);
        var list = new List<PickRef> { r };
        list.AddRange(chain.Where(x => x != e).Select(x => new PickRef(x.Id, PickKind.Edge, part, null)));
        return list;
    }

    /// <summary>Грани ребра.</summary>
    public static IEnumerable<TFace> FacesOf(Part p, TEdge e) =>
        p.Bodies.SelectMany(b => b.Faces).Where(f => f.Edges.Contains(e));

    /// <summary>Внешняя нормаль грани около точки (по сетке, если грань не задана точно).</summary>
    public static Vector3d FaceNormal(Part p, TFace f, Vector3d near)
    {
        var n = f.NormalAt(near);
        if (n.LengthSquared > 1e-18) return n.Normalized();
        return Refs.FacePlane(p, f.Id)?.N ?? Vector3d.UnitZ;
    }

    /// <summary>Середина ребра и направление «наружу» — биссектриса нормалей смежных граней.</summary>
    public static (Vector3d At, Vector3d Out)? EdgeHandle(Part p, uint edgeId)
    {
        if (p.FindEdge(edgeId) is not { Curve: { } c } e) return null;
        var mid = c.PointAt(0.5 * (c.T0 + c.T1));
        var n = Vector3d.Zero;
        foreach (var f in FacesOf(p, e)) n += FaceNormal(p, f, mid);
        if (n.LengthSquared < 1e-12) return null;
        return (mid, n.Normalized());
    }

    /// <summary>Вершина и направление «наружу» — сумма нормалей граней вершины.</summary>
    public static (Vector3d At, Vector3d Out)? VertexHandle(Part p, uint vertexId)
    {
        if (p.FindVertex(vertexId) is not { } v) return null;
        var faces = v.Edges.SelectMany(e => FacesOf(p, e)).Distinct().ToList();
        var n = Vector3d.Zero;
        foreach (var f in faces) n += FaceNormal(p, f, v.P);
        if (n.LengthSquared < 1e-12) return null;
        return (v.P, n.Normalized());
    }

    /// <summary>Точка грани и её нормаль; плоская ли.</summary>
    public static (Vector3d At, Vector3d Out, bool Planar)? FaceHandle(Part p, uint faceId)
    {
        if (Refs.FacePlane(p, faceId) is { } pl) return (pl.P, pl.N, true);
        var c = Refs.FacePoint(p, faceId);
        if (c == null || p.FindFace(faceId) is not { } f) return null;
        return (c.Value, FaceNormal(p, f, c.Value), false);
    }

    /// <summary>
    /// Эскиз с контуром плоской грани (внешний и внутренние контуры — отрезками, дугами и окружностями, общие точки
    /// сшиты): основа выдавливания грани ручкой. null — грань не плоская или контур не получился.
    /// </summary>
    public static Sketch? FaceOutlineSketch(Part p, uint faceId, string name)
    {
        if (p.FindFace(faceId) is not { } f || Refs.FacePlane(p, faceId) is not { } pl) return null;
        var frame = Frame3.FromNormal(pl.P, pl.N);
        var sk = new Sketch(name, frame);
        sk.SetBasePlane(frame, faceId, Loc.L("Грань #{0}", faceId));
        var pool = new EdgeProjection.PointPool(SkRole.Object, "");
        foreach (var e in f.Edges)
        {
            if (e.Curve == null) continue;
            foreach (var ent in EdgeProjection.Project(sk, e.Curve, pool, SkRole.Object, ""))
                if (ent is not SkPoint) sk.AddEntity(ent);
        }
        if (sk.Entities.Count == 0) return null;
        // контур — зафиксирован (это граница грани, а не свободный чертёж)
        foreach (var ent in sk.Entities) ent.Fixed = true;
        return sk;
    }
}
