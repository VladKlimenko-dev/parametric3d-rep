using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Exact;

// A04. Прямой ввод целого цилиндра вдоль +Z с узкой проверкой применимости.
// Не аналитический Boolean: результат остаётся многогранным цилиндром.
internal static class ExactCylinderInput
{
    internal static bool TryBuild(TSolid solid, PlaneRegistry target, out ExactBody? body)
    {
        body = null;
        if (solid.Exact != null || solid.QExact != null || solid.Shells.Count != 1 ||
            !solid.Shells[0].IsClosed) return false;
        var faces = solid.Faces.ToArray();
        if (faces.Length != 3 || faces.Count(f => f.Surface is ExtrudedSurface) != 1) return false;
        var side = faces.FirstOrDefault(f => f.Surface is ExtrudedSurface);
        if (side == null || side.Reversed || side.Surface is not ExtrudedSurface ex ||
            ex.Base is not ArcCurve3 arc || !Circle(arc) ||
            ex.Dir.X != 0 || ex.Dir.Y != 0 || !double.IsFinite(ex.Dir.Z) || ex.Dir.Z <= 1e-4)
            return false;
        var origin = arc.Frame.Origin;
        var end = origin + ex.Dir;
        if (!Finite(origin) || !Finite(end) || end.Z <= origin.Z) return false;
        // K07: торец — горизонтальная плоскость на уровне окружности; начало её системы
        // координат может стоять где угодно в этой плоскости (у выдавливания эскиза — в начале
        // эскиза, а не в центре окружности). Многоугольники торцов строятся из округлённых точек
        // окружности, а не из системы координат плоскости, поэтому доказательство A04 не меняется.
        var bottom = faces.FirstOrDefault(f => f.Surface is PlaneSurface ps && SamePlane(ps.F, origin));
        var top = faces.FirstOrDefault(f => f.Surface is PlaneSurface ps && SamePlane(ps.F, end));
        if (bottom == null || top == null || ReferenceEquals(bottom,top) || !bottom.Reversed || top.Reversed ||
            !Cap(bottom, out var eb) || !Cap(top, out var et)) return false;
        if (!ReferenceEquals(eb!.Curve, arc) || et!.Curve is not ArcCurve3 upper ||
            !Circle(upper) || upper.Radius != arc.Radius || upper.Frame.Origin != end) return false;
        if (side.Loops.Count != 1 || !side.Loops[0].IsOuter || side.Loops[0].CoEdges.Count != 4)
            return false;
        var edges = side.Loops[0].CoEdges.Select(c => c.Edge).ToArray();
        if (edges.Count(e => ReferenceEquals(e, eb)) != 1 || edges.Count(e => ReferenceEquals(e, et)) != 1)
            return false;
        var seams = edges.Where(e => !ReferenceEquals(e, eb) && !ReferenceEquals(e, et)).ToArray();
        if (seams.Length != 2 || !ReferenceEquals(seams[0], seams[1]) ||
            seams[0].Curve is not LineCurve3 seam ||
            seam.A != arc.PointAt(0) || seam.B != arc.PointAt(0) + ex.Dir ||
            !ReferenceEquals(eb.V0, eb.V1) || !ReferenceEquals(et.V0, et.V1) ||
            eb.V0.P != seam.A || et.V0.P != seam.B) return false;

        var samples = new List<Vector3d>();
        arc.Tessellate(samples, true);
        int n = samples.Count - 1;
        if (n < 3) return false;
        var lo = samples.Take(n).Select(p => GridPoint.FromMm(p.X, p.Y, p.Z)).ToArray();
        var hi = samples.Take(n).Select(p => GridPoint.FromMm(p.X, p.Y, p.Z + ex.Dir.Z)).ToArray();
        if (lo.Distinct().Count() != n || hi.Distinct().Count() != n ||
            lo.Any(p => p.Z != lo[0].Z) || hi.Any(p => p.Z != hi[0].Z) || hi[0].Z <= lo[0].Z)
            return false;
        // После округления диск должен остаться строго выпуклым.
        for (int i = 0; i < n; i++)
        {
            var a = lo[i]; var b = lo[(i + 1) % n]; var c = lo[(i + 2) % n];
            System.Numerics.BigInteger cross =
                (System.Numerics.BigInteger)(b.X-a.X)*(c.Y-b.Y) -
                (System.Numerics.BigInteger)(b.Y-a.Y)*(c.X-b.X);
            if (cross.Sign <= 0) return false;
        }

        // Проверяем в отдельном реестре: отказ fast path не меняет target.
        var r = new PlaneRegistry();
        var polys = new List<ExactPolygon>(n+2);
        for (int i = 0; i < n; i++)
            if (!Polygon(new[]{lo[i],lo[(i+1)%n],hi[(i+1)%n],hi[i]},side.Id,r,polys)) return false;
        if (!Polygon(Enumerable.Reverse(lo).ToArray(),bottom.Id,r,polys) ||
            !Polygon(hi,top.Id,r,polys)) return false;
        var sources = faces.ToDictionary(f => f.Id, f => (ISurface3?)f.Surface);
        body = new ExactBody(new ExactSolid(r,polys).Import(target),sources);
        foreach(var f in faces) BooleanOp.RememberInputTag(f);
        Diagnostics.Prof.Count("direct cylinder input",1);
        return true;
    }

    private static bool Polygon(GridPoint[] p,uint tag,PlaneRegistry r,List<ExactPolygon> output)
    {
        var s=r.PlaneThrough(p[0],p[1],p[2]);
        var bounds=new OPlane[p.Length];
        for(int i=0;i<p.Length;i++)
        {
            var e=r.EdgePlane(s,p[i],p[(i+1)%p.Length]);
            int sign=0;
            foreach(var v in p)
            {
                int next=r.Side(v,e);
                if(next!=0){if(sign!=0&&sign!=next)return false;sign=next;}
            }
            if(sign==0)return false;
            bounds[i]=sign>0?e.Flipped:e;
        }
        output.Add(new ExactPolygon(s,bounds,tag));
        return true;
    }
    private static bool Finite(Vector3d p)=>double.IsFinite(p.X)&&double.IsFinite(p.Y)&&double.IsFinite(p.Z);
    private static bool SamePlane(Frame3 f,Vector3d point)=>f.Origin.Z==point.Z&&f.U==Vector3d.UnitX&&f.V==Vector3d.UnitY&&f.W==Vector3d.UnitZ;
    private static bool SameFrame(Frame3 f,Vector3d origin)=>f.Origin==origin&&f.U==Vector3d.UnitX&&f.V==Vector3d.UnitY&&f.W==Vector3d.UnitZ;
    private static bool Circle(ArcCurve3 a)=>double.IsFinite(a.Radius)&&a.Radius>1e-4&&a.A0==0&&a.A1==2*Math.PI&&SameFrame(a.Frame,a.Frame.Origin);
    private static bool Cap(TFace f,out TEdge? e)
    {
        e=null;
        if(f.Loops.Count!=1||!f.Loops[0].IsOuter||f.Loops[0].CoEdges.Count!=1)return false;
        e=f.Loops[0].CoEdges[0].Edge;return true;
    }
}
