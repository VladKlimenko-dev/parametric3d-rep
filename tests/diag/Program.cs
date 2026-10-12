using System.Text.Json;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

static class P
{
    static TSolid Box()
    {
        var f=new Frame3(Vector3d.Zero,Vector3d.UnitX,Vector3d.UnitY);
        var p=new[]{f.ToWorld(0,0),f.ToWorld(60,0),f.ToWorld(60,40),f.ToWorld(0,40)};
        return ExtrudeOp.Build(ProfileBuilder.Build(f,Enumerable.Range(0,4)
            .Select(i=>(ICurve3)new LineCurve3(p[i],p[(i+1)%4])).ToList()),new Vector3d(0,0,20));
    }
    // generic triangle-soup edge analysis, vertex identity by key
    static (int bad,int nonOpposed,List<string> samples) Soup<T>(IEnumerable<(T a,T b,T c,uint face)> tris) where T:notnull
    {
        var und=new Dictionary<(T,T),List<(bool fwd,uint face)>>();
        var cmp=Comparer<string>.Default;
        foreach(var (a,b,c,f) in tris){
            if(a.Equals(b)||b.Equals(c)||c.Equals(a)) continue;
            foreach(var (u,v) in new[]{(a,b),(b,c),(c,a)}){
                bool fwd=string.CompareOrdinal(u.ToString(),v.ToString())<0;
                var k=fwd?(u,v):(v,u);
                if(!und.TryGetValue(k,out var l))und[k]=l=new();
                l.Add((fwd,f));
            }
        }
        int bad=0,nonOp=0;var s=new List<string>();
        foreach(var kv in und){
            if(kv.Value.Count!=2){bad++; if(s.Count<40)s.Add($"{kv.Key.Item1}->{kv.Key.Item2} n={kv.Value.Count} faces=[{string.Join(",",kv.Value.Select(x=>(x.fwd?"+":"-")+x.face))}]");}
            else if(kv.Value[0].fwd==kv.Value[1].fwd){nonOp++; if(s.Count<40)s.Add($"NONOPP {kv.Key.Item1}->{kv.Key.Item2} faces=[{string.Join(",",kv.Value.Select(x=>(x.fwd?"+":"-")+x.face))}]");}
        }
        return (bad,nonOp,s);
    }
    static void Main(string[] args)
    {
        BooleanOp.Threads=Tessellator.Threads=1;
        var w=new List<string>();
        var b=new List<TSolid>{Box()};
        BooleanOp.DeferMeshing=true;
        BooleanOp.Subtract(b,PrimitiveOp.Sphere(new Vector3d(30,20,20),8),w);
        BooleanOp.DeferMeshing=false;
        var s=b[0];
        var faces=s.Faces.ToList();
        Console.WriteLine($"faces={faces.Count} [{string.Join(", ",faces.Select(f=>f.Id+":"+(f.Surface is FacetedSurface fs?"Fac("+fs.Source?.GetType().Name+") facets="+fs.Facets.Count+" disp="+(fs.Display?.Count??-1):f.Surface.GetType().Name)+" loops="+f.Loops.Count))}]");
        Console.WriteLine("warnings: "+string.Join(" | ",w));
        // Stage 1 exact
        if(s.Exact!=null){
            var ex=s.Exact.Solid;
            Console.WriteLine($"S1 exact: polys={ex.Polygons.Count} uncancelled={ex.UncancelledSegments()}");
        } else Console.WriteLine("S1 exact: none");
        // Stage 2 topology
        var iss=TopologyCheck.Validate(s);
        Console.WriteLine($"S2 topo: issues={iss.Count} edges={s.Edges.Count()} {string.Join("; ",iss.Take(5))}");
        // Stage 3 facets (pre MeshExactly)
        Report("S3 facets(pre)",faces,f=>((FacetedSurface)f.Surface).Facets);
        BooleanOp.MeshExactlySolid(s,w);
        foreach(var f in faces){var fs=(FacetedSurface)f.Surface; Console.WriteLine($"  face {f.Id} facets={fs.Facets.Count} display={(fs.Display==null?"null":fs.Display.Count.ToString())}");}
        BooleanOp.MeshDebug = m => Console.WriteLine("MESHDEBUG " + m);
        var sw0=System.Diagnostics.Stopwatch.StartNew(); BooleanOp.MeshExactlySolid(s,w); Console.WriteLine($"second mesh call ms={sw0.Elapsed.TotalMilliseconds}");
        Report("S3b facets(post)",faces,f=>((FacetedSurface)f.Surface).Facets);
        Report("S4 display(double)",faces,f=>((FacetedSurface)f.Surface).Display??((FacetedSurface)f.Surface).Facets);
        foreach(var f in faces){var fs=(FacetedSurface)f.Surface; var L=fs.Display??fs.Facets;
          foreach(var (A,B,C) in L){
            Vector3d F(Vector3d q)=>new Vector3d((float)q.X,(float)q.Y,(float)q.Z);
            var qd=new TriangleQuality(A,B,C); var qf=new TriangleQuality(F(A),F(B),F(C));
            bool drop=qf.Area<=0||qf.MinAngle<=1e-9; bool fdup=F(A)==F(B)||F(B)==F(C)||F(C)==F(A);
            if(drop||fdup) Console.WriteLine($"  DROP face {f.Id}: A={A:R} B={B:R} C={C:R} areaD={qd.Area:E3} minAngD={qd.MinAngle:E3} areaF={qf.Area:E3} minAngF={qf.MinAngle:E3} floatDup={fdup}");
          }}
        var m=Tessellator.Tessellate(s);
        var vt=m.Vertices; var ix=m.Indices;
        IEnumerable<(string,string,string,uint)> FT(Func<Vector3,string> key){
            for(int i=0;i+2<ix.Length;i+=3) yield return (key(vt[ix[i]].P),key(vt[ix[i+1]].P),key(vt[ix[i+2]].P),vt[ix[i]].FaceId);
        }
        string Bits(Vector3 p)=>$"{BitConverter.SingleToInt32Bits(p.X)}:{BitConverter.SingleToInt32Bits(p.Y)}:{BitConverter.SingleToInt32Bits(p.Z)}";
        string R5(Vector3 p)=>$"{Math.Round(p.X*1e5)}:{Math.Round(p.Y*1e5)}:{Math.Round(p.Z*1e5)}";
        var r1=Soup(FT(Bits)); Console.WriteLine($"S5 float mesh exact-bits weld: bad={r1.bad} nonOpposed={r1.nonOpposed} tris={ix.Length/3}");
        foreach(var x in r1.samples.Take(12))Console.WriteLine("   "+x);
        var r2=Soup(FT(R5)); Console.WriteLine($"S5 float mesh 1e-5 rounding weld (speed test): bad={r2.bad} nonOpposed={r2.nonOpposed}");
        foreach(var x in r2.samples.Take(12))Console.WriteLine("   "+x);
    }
    static void Report(string name,List<TFace> faces,Func<TFace,List<(Vector3d A,Vector3d B,Vector3d C)>> get)
    {
        string K(Vector3d p)=>$"{p.X:R}:{p.Y:R}:{p.Z:R}";
        var tris=faces.SelectMany(f=>get(f).Select(t=>(K(t.A),K(t.B),K(t.C),f.Id))).ToList();
        var r=Soup(tris);
        Console.WriteLine($"{name}: tris={tris.Count} bad={r.bad} nonOpposed={r.nonOpposed}");
        foreach(var x in r.samples.Take(12))Console.WriteLine("   "+x);
    }
}
