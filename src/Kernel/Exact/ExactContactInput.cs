using System.Numerics;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Exact;

internal static class ExactContactInput
{
    /// <summary>Трасса подбора контактных плоскостей (только для разбора; null — выключено).</summary>
    [ThreadStatic] internal static Action<string>? Trace;
    /// <summary>
    /// Своя точная плоскость каждой плоской грани резца без контакта — только в общем пути булевой
    /// (localSources == null). В пути резца скругления (TryAttach) её не дают: на 88 проверках FProbe
    /// это ломало 6 сценариев с объединением. Выключение для сравнения: P3D_OWNPLANES=0.
    /// </summary>
    private static bool _gOwnPlanesInGeneralPath = Environment.GetEnvironmentVariable("P3D_OWNPLANES") != "0";
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.OwnPlanesInGeneralPath), если он задан; иначе общее по умолчанию.</summary>
    internal static bool OwnPlanesInGeneralPath
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.OwnPlanesInGeneralPath : _gOwnPlanesInGeneralPath;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gOwnPlanesInGeneralPath, value, "ExactContactInput.OwnPlanesInGeneralPath");
    }
    // Contact construction, not a general proximity weld. Fillets supply the
    // sources incident to their selected edge; other operands must have
    // analytically coincident planar source surfaces before quantization.
    private readonly record struct P(BigRational X, BigRational Y, BigRational Z)
    {
        public static P operator +(P a, P b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static P operator -(P a, P b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static P operator *(P a, BigRational k) => new(a.X*k,a.Y*k,a.Z*k);
        public BigRational Dot(P b) => X*b.X+Y*b.Y+Z*b.Z;
        public P Cross(P b) => new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        public Vector3d Mm => new(X.ToDouble()/ExactGrid.UnitsPerMm,Y.ToDouble()/ExactGrid.UnitsPerMm,Z.ToDouble()/ExactGrid.UnitsPerMm);
    }

    internal static bool TryAttach(TSolid tool, TSolid body, TEdge edge)
    {
        if(body.Exact is not {} cached) return false;
        var localSources=new HashSet<ISurface3>();
        foreach(var e in edge.V0.Edges.Concat(edge.V1.Edges).Append(edge))
            foreach(var face in new[]{e.FaceA,e.FaceB})
                if(face!=null && Source(face.Surface) is {} source)localSources.Add(source);
        double roundoff=64*2.2204460492503131e-16*Math.Max(1,Math.Max(edge.V0.P.Length,edge.V1.P.Length));
        // По кольцам со вставленными T-стыками, а не по углам многоугольников: конец ребра может лежать
        // посреди стороны выпуклого куска соседней грани (так раскладывает слияние), и по углам эта грань
        // выпала бы из местных — торец резца на ней не прижался бы (FProbe Z angle=0.1 union=True).
        var rings=ExactAdapter.Rings(cached.Solid.Registry,cached.Solid.Polygons);
        for(int k=0;k<rings.Length;k++)
            foreach(int v in rings[k])
            {
                var p=cached.Solid.Registry.ToMm(v);var q=new Vector3d(p.X,p.Y,p.Z);
                if((q-edge.V0.P).Length<=roundoff||(q-edge.V1.P).Length<=roundoff)
                    if(cached.Sources.TryGetValue(cached.Solid.Polygons[k].Tag,out var s)&&Source(s) is {} source)
                    {localSources.Add(source);break;}
            }
        if(Trace!=null)
        {
            Trace($"attach edge ({edge.V0.P.X:R},{edge.V0.P.Y:R},{edge.V0.P.Z:R})-({edge.V1.P.X:R},{edge.V1.P.Y:R},{edge.V1.P.Z:R}); topology faces at V0 {edge.V0.Edges.Count()} edges, V1 {edge.V1.Edges.Count()} edges; local {localSources.Count}");
            foreach(var vtx in new[]{edge.V0.P,edge.V1.P})
            {
                double best=double.MaxValue;
                foreach(var polygon in cached.Solid.Polygons)
                    foreach(int v in polygon.Vertices(cached.Solid.Registry))
                    {var p=cached.Solid.Registry.ToMm(v);best=Math.Min(best,(new Vector3d(p.X,p.Y,p.Z)-vtx).Length);}
                Trace($"  nearest exact vertex to end: {best:E2} (roundoff {roundoff:E1}); polygons there: "+string.Join(" ",cached.Solid.Polygons.Where(pg=>pg.Vertices(cached.Solid.Registry).ToArray().Any(v=>{var p=cached.Solid.Registry.ToMm(v);return (new Vector3d(p.X,p.Y,p.Z)-vtx).Length<=roundoff;})).Select(pg=>$"{pg.Tag}/p{pg.Support.Id}")));
            }
        }
        if(!TryBuild(tool,cached,out var fitted,localSources))return false;
        tool.AttachDerivedExact(fitted!);  // поставка 2: точная форма подогнана к B-rep резца — производна от него
        return true;
    }

    private static ISurface3? Source(ISurface3? surface) =>
        surface is FacetedSurface fs ? Source(fs.Source) : surface;

    private static bool SameSourcePlane(ISurface3 a, ISurface3 b)
    {
        if(ReferenceEquals(a,b))return true;
        if(!a.IsPlanar||!b.IsPlanar)return false;
        var p=a.PointAt(Vector2d.Zero);var q=b.PointAt(Vector2d.Zero);
        var na=a.NormalAt(Vector2d.Zero).Normalized();var nb=b.NormalAt(Vector2d.Zero).Normalized();
        double scale=Math.Max(1,Math.Max(p.Length,q.Length));
        return Math.Abs(Vector3d.Dot(na,nb))>1-1e-14 &&
            Math.Abs(Vector3d.Dot(p-q,nb))<=64*2.2204460492503131e-16*scale;
    }
    internal static bool TryBuild(TSolid tool, ExactBody cached, out ExactBody? fitted,
                                  ISet<ISurface3>? localSources=null)
    {
        fitted=null;
        // Isolated registry: an unsuccessful construction must not alter the body.
        var r=new PlaneRegistry();
        var solid=cached.Solid.Import(r);
        var tris=BooleanOp.ToPolygonsInternal(tool,out var sources,null);
        var planes=solid.Polygons.Where(p=>cached.Sources.TryGetValue(p.Tag,out var s)&&s?.IsPlanar==true)
            .GroupBy(p=>(p.Support.Id,p.Tag)).Select(g=>g.First()).ToArray();
        // Upper bound on the correction of an already associated contact.
        // It is NOT used to infer contact between unrelated analytic planes.
        double limit=2*Math.Sqrt(3)/ExactGrid.UnitsPerMm;
        var constraints=new Dictionary<uint,int>();
        foreach(var group in tris.GroupBy(p=>p.Tag))
        {
            if(!sources.TryGetValue(group.Key,out var surface)||surface?.IsPlanar!=true)continue;
            var samples=group.SelectMany(p=>p.V).ToArray();
            if(samples.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||!double.IsFinite(p.Z)))return false;
            var normal=group.First().N.Normalized();
            double best=limit;int selected=-1;
            foreach(var polygon in planes)
            {
                int plane=polygon.Support.Id;
                var source=Source(cached.Sources[polygon.Tag]);
                if(source==null)continue;
                if(localSources!=null)
                {
                    if(!localSources.Any(s=>SameSourcePlane(s,source)))
                    {
                        if(Trace!=null&&source.IsPlanar)
                        {
                            var pn=source.NormalAt(Vector2d.Zero).Normalized();
                            if(Math.Abs(Vector3d.Dot(pn,normal))>1-1e-8)
                                Trace($"  candidate plane {plane} tag {polygon.Tag}: not a local source; local: {string.Join("; ",localSources.Select(ls=>$"{ls.GetType().Name} n·={Vector3d.Dot(ls.NormalAt(Vector2d.Zero).Normalized(),pn):R} off {Vector3d.Dot(ls.PointAt(Vector2d.Zero)-source.PointAt(Vector2d.Zero),pn):E2}"))}");
                        }
                        continue;
                    }
                }
                else
                {
                    // Do not erase a user-specified small gap just because it
                    // happens to be smaller than the quantization step.
                    if(source is not PlaneSurface && source is not ExtrudedSurface {Base:LineCurve3})
                    {
                        Trace?.Invoke($"  candidate plane {plane} tag {polygon.Tag}: source {source.GetType().Name}/{(source as ExtrudedSurface)?.Base.GetType().Name} not a plain plane");
                        continue;
                    }
                    double scale=samples.Max(p=>Math.Max(1,Math.Max(Math.Abs(p.X),Math.Max(Math.Abs(p.Y),Math.Abs(p.Z)))));
                    double roundoff=64*2.2204460492503131e-16*scale;
                    if(samples.Any(p=>(source.PointAt(source.ProjectToUV(p))-p).Length>roundoff))
                    {
                        Trace?.Invoke($"  candidate plane {plane} tag {polygon.Tag}: off source {source.GetType().Name} by {samples.Max(p=>(source.PointAt(source.ProjectToUV(p))-p).Length):E2} (roundoff {roundoff:E1})");
                        continue;
                    }
                }
                var (x,y,z)=r.UnitNormal(new OPlane(plane,1));var n=new Vector3d(x,y,z);
                if(Math.Abs(Vector3d.Dot(n,normal))<1-1e-8)continue;
                var origin=r.ToMm(polygon.Vertices(r)[0]);var point=new Vector3d(origin.X,origin.Y,origin.Z);
                double error=samples.Max(p=>Math.Abs(Vector3d.Dot(n,p-point)));
                if(error<best){best=error;selected=plane;}
            }
            if(selected>=0)constraints[group.Key]=selected;
            // Отказ контактного пути при почти совпадающих плоскостях тела пробовался: такие плоскости
            // встречаются часто, а общий путь их не выдерживает (16 из 88 проверок) — отменено.
            if(Trace!=null)
            {
                // Ближайшая плоскость тела к этой грани резца — даже если она отвергнута.
                double bestAny=double.MaxValue,bestDot=0;uint bestTag=0;
                foreach(var polygon in planes)
                {
                    var (x,y,z)=r.UnitNormal(new OPlane(polygon.Support.Id,1));var nn=new Vector3d(x,y,z);
                    double dd=Math.Abs(Vector3d.Dot(nn,normal));if(dd<0.999)continue;
                    var o0=r.ToMm(polygon.Vertices(r)[0]);var pt=new Vector3d(o0.X,o0.Y,o0.Z);
                    double err=samples.Max(p=>Math.Abs(Vector3d.Dot(nn,p-pt)));
                    if(err<bestAny){bestAny=err;bestDot=dd;bestTag=polygon.Tag;}
                }
                string sel="";
                if(selected>=0)
                {
                    var (sx,sy,sz)=r.UnitNormal(new OPlane(selected,1));
                    var pol=planes.First(pp=>pp.Support.Id==selected);var o1=r.ToMm(pol.Vertices(r)[0]);
                    sel=$" → plane {selected} (body tags {string.Join(",",planes.Where(pp=>pp.Support.Id==selected).Select(pp=>pp.Tag))}) n ({sx:0.######},{sy:0.######},{sz:0.######}) w {sx*o1.X+sy*o1.Y+sz*o1.Z:0.######}";
                }
                var tn=normal; var tp=samples[0];
                Trace($"tool face {group.Key} n ({tn.X:0.######},{tn.Y:0.######},{tn.Z:0.######}) w {Vector3d.Dot(tn,tp):0.######}: {(selected>=0?"matched":"NOT matched")}{sel}; nearest body plane: distance {bestAny:E2} mm, tag {bestTag} {(cached.Sources.TryGetValue(bestTag,out var bs)?Source(bs)?.GetType().Name:"-")}, 1-|cos| {1-bestDot:E1}");
            }
        }
        Trace?.Invoke($"constraints {constraints.Count}");
        if(constraints.Count<2)return false;

        // КЛАСС 2 ПОВЁРНУТОГО СКРУГЛЕНИЯ (закрыт в r4). Плоская грань без контакта прежде переводилась
        // треугольник за треугольником по округлённым вершинам и выходила несколькими почти совпадающими
        // плоскостями; следующая операция прижималась к одной, и оставался клин. Теперь у такой грани
        // одна своя плоскость (ниже), а грани самого тела — одна плоскость на грань (ExactPlanarInput).

        var pointsDouble=new List<Vector3d>();
        var points=new List<P>();
        var at=new List<HashSet<int>>();
        var triangles=new List<(int A,int B,int C,uint Tag)>();
        double tau=.5/ExactGrid.UnitsPerMm;
        int Index(Vector3d p)
        {
            for(int i=0;i<pointsDouble.Count;i++)
                if((pointsDouble[i]-p).LengthSquared<=tau*tau)return i;
            var g=GridPoint.FromMm(p.X,p.Y,p.Z);
            pointsDouble.Add(p);points.Add(new P(g.X,g.Y,g.Z));at.Add(new());
            return points.Count-1;
        }
        var __weld=Diagnostics.Prof.Time("контакт: склейка точек");
        foreach(var tri in tris)
        {
            int a=Index(tri.V[0]),b=Index(tri.V[1]),c=Index(tri.V[2]);
            triangles.Add((a,b,c,tri.Tag));
            if(constraints.TryGetValue(tri.Tag,out int plane))
                {at[a].Add(plane);at[b].Add(plane);at[c].Add(plane);}
        }
        __weld.Dispose();
        Diagnostics.Prof.Count("контакт: точек", points.Count);
        // УЗКИЙ ВАРИАНТ «ОДНА ПЛОСКОСТЬ НА ГРАНЬ БЕЗ КОНТАКТА»: вершины такой плоской грани резца, не
        // лежащие ни на одной плоскости тела, ставятся на общую плоскость этой грани. Тогда все её
        // треугольники без вершин контакта лежат в ОДНОЙ точной плоскости (прежде — каждый в своей,
        // по округлённым вершинам), и следующая операция не прижимается к одной из нескольких копий.
        // Вершинам, у которых уже три плоскости тела, своя не добавляется (широкий вариант, ставивший
        // её всем, давал несовместные четвёрки плоскостей и ломал 6 сценариев из 88).
        // Своя точная плоскость каждой плоской грани резца без контакта с телом.
        var ownAt=new List<List<int>>();for(int i=0;i<points.Count;i++)ownAt.Add(new());
        if(OwnPlanesInGeneralPath&&localSources==null)
            foreach(var group in tris.GroupBy(p=>p.Tag))
            {
                if(constraints.ContainsKey(group.Key))continue;
                if(!sources.TryGetValue(group.Key,out var surface)||surface?.IsPlanar!=true)continue;
                var nd=Vector3d.Zero;foreach(var poly in group)nd+=Vector3d.Cross(poly.V[1]-poly.V[0],poly.V[2]-poly.V[0]);
                if(nd.LengthSquared<1e-24)continue;
                nd=nd.Normalized();
                const double S=1<<30;
                var a=new BigInteger(Math.Round(nd.X*S));var b=new BigInteger(Math.Round(nd.Y*S));var c=new BigInteger(Math.Round(nd.Z*S));
                if(a.IsZero&&b.IsZero&&c.IsZero)continue;
                var pts=group.SelectMany(p=>p.V).ToList();
                var centre=pts.Aggregate(Vector3d.Zero,(acc,q)=>acc+q)/pts.Count;
                var g=GridPoint.FromMm(centre.X,centre.Y,centre.Z);
                int own=r.Plane(a,b,c,-(a*g.X+b*g.Y+c*g.Z)).Id;
                var (ux,uy,uz)=r.UnitNormal(new OPlane(own,1));var un=new Vector3d(ux,uy,uz);
                var cm=new Vector3d(g.X,g.Y,g.Z)/ExactGrid.UnitsPerMm;
                if(pts.Any(q=>Math.Abs(Vector3d.Dot(un,q-cm))>limit)){Trace?.Invoke($"own plane {group.Key}: not flat");continue;}
                foreach(var tri in group)
                    foreach(var v in tri.V)
                    {
                        int idx=Index(v);
                        if(!ownAt[idx].Contains(own))ownAt[idx].Add(own);
                    }
            }
        for(int i=0;i<points.Count;i++)
        {
            if(at[i].Count==0&&ownAt[i].Count==0)continue;
            var selected=new List<int>();P n0=default, direction=default;
            foreach(int plane in at[i].Order().Concat(ownAt[i]))
            {
                var (a,b,c,_)=r.Coefficients(plane);var n=new P(a,b,c);
                if(selected.Count==0){selected.Add(plane);n0=n;continue;}
                if(selected.Count==1)
                {
                    var cross=n0.Cross(n);
                    if(cross.Dot(cross).Sign!=0){selected.Add(plane);direction=cross;}
                }
                else if(direction.Dot(n).Sign!=0){selected.Add(plane);break;}
            }
            // Keep the grid coordinate(s) in the best-conditioned free axis.
            // Cramer's rule in the registry has much smaller denominators than
            // successive orthogonal projections of rational coordinates.
            int Largest(P p)
            {
                var a=BigInteger.Abs(p.X.Num);var b=BigInteger.Abs(p.Y.Num);var c=BigInteger.Abs(p.Z.Num);
                return a>=b&&a>=c?0:b>=c?1:2;
            }
            void Coordinate(int axis)
            {
                var p=points[i];var value=axis==0?p.X:axis==1?p.Y:p.Z;
                selected.Add(r.Plane(axis==0?1:0,axis==1?1:0,axis==2?1:0,-value.Num).Id);
            }
            if(selected.Count==1)
            {
                int pivot=Largest(n0);
                for(int axis=0;axis<3;axis++)if(axis!=pivot)Coordinate(axis);
            }
            else if(selected.Count==2)Coordinate(Largest(direction));
            int vertex=r.Vertex(selected[0],selected[1],selected[2]);
            if(at[i].Any(plane=>r.Side(vertex,plane)!=0)){Trace?.Invoke($"vertex {i}: off contact plane");return false;}
            if(Trace!=null&&ownAt[i].Any(plane=>r.Side(vertex,plane)!=0))Trace($"vertex {i}: off own plane ({at[i].Count} contact, {ownAt[i].Count} own)");
            var (x,y,z,w)=r.Homogeneous(vertex);
            var q=new P(new(x,w),new(y,w),new(z,w));
            if((q.Mm-pointsDouble[i]).Length>limit){Trace?.Invoke($"vertex {i}: moved {(q.Mm-pointsDouble[i]).Length:E2}");return false;}
            points[i]=q;
        }
        OPlane Plane(P n,BigRational d)
        {
            BigInteger scale=1;
            foreach(var v in new[]{n.X,n.Y,n.Z,d})
                scale=scale/BigInteger.GreatestCommonDivisor(scale,v.Den)*v.Den;
            return r.Plane(n.X.Num*(scale/n.X.Den),n.Y.Num*(scale/n.Y.Den),n.Z.Num*(scale/n.Z.Den),d.Num*(scale/d.Den));
        }
        var polys=new List<ExactPolygon>();
        foreach(var (ia,ib,ic,tag) in triangles)
        {
            var a=points[ia];var b=points[ib];var c=points[ic];var n=(b-a).Cross(c-a);
            if(n.Dot(n).Sign==0)continue;
            var support=Plane(n,-n.Dot(a));
            OPlane Bound(P u,P v,P inside)
            {
                var en=n.Cross(v-u);var d=-en.Dot(u);var plane=Plane(en,d);
                return (en.Dot(inside)+d).Sign<0?plane:plane.Flipped;
            }
            polys.Add(new ExactPolygon(support,new[]{Bound(a,b,c),Bound(b,c,a),Bound(c,a,b)},tag));
        }
        var exact=new ExactSolid(r,ExactMerge.Fragments(r,polys));
        if(exact.UncancelledSegments()!=0||exact.VolumeMm3().Sign<=0)return false;
        fitted=new ExactBody(exact,sources);
        return true;
    }
}
