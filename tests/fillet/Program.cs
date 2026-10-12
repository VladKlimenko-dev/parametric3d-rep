using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel.Voxels;
using Parametric3D.Kernel.Exact;

static class Program
{
    static double XY=1;
    static double Z=1;
    static bool Remote;
    static bool DeepChecks;
    static Vector3d Origin=Vector3d.Zero,U=Vector3d.UnitX,V=Vector3d.UnitY,W=Vector3d.UnitZ;
    static Vector3d Map(double x,double y,double z)=>Origin+U*x+V*y+W*z;
    static int Passed,Failed;
    static void Require(bool ok,string why) {if(!ok)throw new Exception(why);}
    static void Check(string name,Action test) {
        try{test();Passed++;Log(new{test=name,ok=true});}
        catch(Exception ex){Failed++;Log(new{test=name,ok=false,error=ex.Message});}
    }
    static void Reset() {
        XY=Z=1;Remote=false;DeepChecks=false;Origin=Vector3d.Zero;U=Vector3d.UnitX;V=Vector3d.UnitY;W=Vector3d.UnitZ;
        BooleanOp.DeferMeshing=false;FilletOp.AppliedAddition=null;FilletOp.AppliedTool=null;FilletOp.BuiltTool=null;FilletOp.PathDebug=null;
    }
    static void Log(object o)=>Console.WriteLine(JsonSerializer.Serialize(o));
    static TSolid Box(double x=0,double y=0,double z=0,double w=60,double d=40,double h=20) {
        var f=new Frame3(Map(x,y,z),U,V);
        var p=new[]{f.ToWorld(0,0),f.ToWorld(w,0),f.ToWorld(w,d),f.ToWorld(0,d)};
        return ExtrudeOp.Build(ProfileBuilder.Build(f,Enumerable.Range(0,4)
            .Select(i=>(ICurve3)new LineCurve3(p[i],p[(i+1)%4])).ToList()),W*h);
    }
    static List<TSolid> Step(bool unite,List<string> warnings) {
        var bodies=new List<TSolid>{Box(w:60*XY,d:40*XY,h:(unite?10:20)*Z)};
        if(unite)BooleanOp.Unite(bodies,Box(0,0,10*Z,30*XY,40*XY,10*Z),warnings);
        else BooleanOp.Subtract(bodies,Box(30*XY,0,10*Z,30*XY,40*XY,10*Z),warnings);
        return bodies;
    }
    static TEdge Select(TSolid s)=>s.Edges.Single(e=>
        (e.V0.P-Map(30*XY,0,10*Z)).Length+(e.V1.P-Map(30*XY,40*XY,10*Z)).Length<1e-4 ||
        (e.V1.P-Map(30*XY,0,10*Z)).Length+(e.V0.P-Map(30*XY,40*XY,10*Z)).Length<1e-4);
    static double Volume(TSolid s)=>MassProps.Volume(Tessellator.Tessellate(s));
    static double ExactVolume(TSolid s) => s.Exact?.Solid.VolumeMm3().ToDouble() ?? Volume(s);
    // Аналитическое тело (маршрут P3D_ANALYTIC): его многогранная форма — ленивая и грубая (гранение с допуском хорды),
    // мера — аналитический объём по точным поверхностям.
    static bool IsAnalytic(TSolid s) => s.Guarantee == Parametric3D.Kernel.GuaranteeLevel.ExactAnalytic;
    static double AnalyticOrExact(TSolid s, bool analytic) => analytic ? (AnalyticVolume.Of(s) ?? ExactVolume(s)) : ExactVolume(s);
    static Parametric3D.Kernel.Exact.ExactSolid? ExactForm(TSolid s) =>
        s.Exact?.Solid ?? (s.LazyDerivedExact != null ? Parametric3D.Kernel.Exact.ExactAdapter.ToExact(s, new(), new()).Solid : null);
    static void CheckExactShell(Parametric3D.Kernel.Exact.ExactSolid solid) {
        Require(solid.UncancelledSegments()==0,"uncancelled exact segments");
        var rings=Parametric3D.Kernel.Exact.ExactAdapter.Rings(solid.Registry,solid.Polygons);
        var edges=new Dictionary<(int,int),(int Count,int Balance)>();
        var links=new Dictionary<int,List<(int,int)>>();
        foreach(var ring in rings)for(int i=0;i<ring.Count;i++){
            int a=ring[i],b=ring[(i+1)%ring.Count];
            var key=a<b?(a,b):(b,a);var e=edges.GetValueOrDefault(key);
            edges[key]=(e.Count+1,e.Balance+(a<b?1:-1));
            if(!links.TryGetValue(a,out var ls))links[a]=ls=new();
            ls.Add((ring[(i+ring.Count-1)%ring.Count],b));
        }
        Require(edges.Values.All(e=>e.Count==2&&e.Balance==0),"nonmanifold exact edge");
        foreach(var ls in links.Values){
            var degree=new Dictionary<int,int>();
            foreach(var (a,b) in ls){degree[a]=degree.GetValueOrDefault(a)+1;degree[b]=degree.GetValueOrDefault(b)+1;}
            Require(degree.Values.All(n=>n==2),"invalid exact vertex link degree");
            var seen=new HashSet<int>{ls[0].Item1};bool changed=true;
            while(changed){changed=false;foreach(var (a,b) in ls){
                if(seen.Contains(a)&&seen.Add(b))changed=true;
                if(seen.Contains(b)&&seen.Add(a))changed=true;
            }}
            Require(seen.Count==degree.Count,"disconnected exact vertex link");
        }
    }
    static string Hash(TSolid s) {
        var m=Tessellator.Tessellate(s,facets:true);using var stream=new MemoryStream();
        using(var w=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)){
            foreach(var v in m.Vertices){w.Write(v.P.X);w.Write(v.P.Y);w.Write(v.P.Z);}
            foreach(int i in m.Indices)w.Write(i);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    static void Scenario(string label,bool unite,double radius,bool chamfer=false,bool reverse=false,bool defer=false) {
        var warnings=new List<string>();BooleanOp.DeferMeshing=defer;
        var b=Step(unite,warnings);var s=b[0];var e=Select(s);double before=ExactVolume(s);
        if(DeepChecks){var fs=ExactForm(s);Require(fs!=null,"step fell back from Exact");CheckExactShell(fs!);}
        if(reverse){
            (e.V0,e.V1)=(e.V1,e.V0);e.Curve=e.Curve.Reversed();
            foreach(var ce in e.CoEdges)ce.Reversed=!ce.Reversed;
        }
        TSolid? other=null;string? otherHash=null;
        if(Remote){other=Box(100000,0,0);otherHash=Hash(other);b.Add(other);}
        Require(FilletOp.DescribeEdge(b,e).EndsWith("Convex=False"),"concave edge classified as convex");
        int added=0,subtracted=0;long direct0=Parametric3D.Kernel.Ops.DirectRound.Built;
        FilletOp.AppliedAddition=_=>added++;FilletOp.AppliedTool=_=>subtracted++;
        if(DeepChecks)FilletOp.BuiltTool=tool=>{
            var exact=Parametric3D.Kernel.Exact.ExactAdapter.ToExact(tool,new(),new()).Solid;
            CheckExactShell(exact);
            Require(exact.VolumeMm3().Sign>0,"input tool volume sign");
        };
        try {
            if(chamfer)FilletOp.ChamferEdges(b,new[]{EdgeRef.Of(e)},radius,radius,warnings);
            else FilletOp.RoundEdges(b,new[]{EdgeRef.Of(e)},radius,warnings);
        } catch (RebuildException ex) { throw new Exception(ex.Message+" "+string.Join(" | ",warnings)); }
        BooleanOp.DeferMeshing=false;
        if(defer)foreach(var body in b)BooleanOp.MeshExactlySolid(body,warnings);
        // KRN-06: прямой построитель (без резца) — тоже аддитивное вогнутое скругление; проверки объёма и топологии ниже те же
        bool direct=Parametric3D.Kernel.Ops.DirectRound.Built>direct0;
        Require((added==1&&subtracted==0)||(direct&&added==0&&subtracted==0),"not an additive fillet");
        bool analytic=IsAnalytic(s)||IsAnalytic(b[0]);                       // одна мера для «до» и «после»
        if(analytic)before=AnalyticOrExact(s,true);
        double after=AnalyticOrExact(b[0],analytic),gain=after-before;
        double expected=40*XY*radius*radius*(chamfer?.5:1-Math.PI/4);
        Require(gain>0,"volume did not increase");
        double error=Math.Max(1e-6,expected*.016+4e-5*40*XY*radius);
        Require(Math.Abs(gain-expected)<error,$"gain {gain}, expected {expected} +/- {error}");
        Require(TopologyCheck.Validate(b[0]).Count==0,"topology issues");
        if(DeepChecks){
            var fr=ExactForm(b[0]);Require(fr!=null,"result fell back from Exact");
            CheckExactShell(fr!);
        }
        var mesh=Tessellator.Tessellate(b[0],facets:true);Require(mesh.Whole,"broken result");
        var tris=Occupancy.Triangles(mesh);
        foreach(double y in new[]{.25,.5,.75}){
            Require(PointInSolid.Test(tris,Map(30*XY+radius*.15,40*XY*y,10*Z+radius*.15)),"missing added material");
            Require(!PointInSolid.Test(tris,Map(30*XY+radius*.8,40*XY*y,10*Z+radius*.8)),"filled intended void");
            Require(PointInSolid.Test(tris,Map(30*XY-radius*.5,40*XY*y,10*Z+radius*.5)),"old wall lost");
            Require(PointInSolid.Test(tris,Map(30*XY+radius*.5,40*XY*y,10*Z-radius*.5)),"old floor lost");
        }
        if(!chamfer){
            Require(b[0].Faces.Any(f=>{
                var src=f.Surface is FacetedSurface fs?fs.Source:f.Surface;
                return (src is ExtrudedSurface ex&&ex.Base is ArcCurve3 a&&Math.Abs(a.Radius-radius)<1e-7)||(src is CylinderSurface cy&&Math.Abs(cy.Radius-radius)<1e-7);   // KRN-06: прямой построитель — точный цилиндр
            }),"no cylindrical fillet with requested radius");
        }
        if(other!=null)Require(b.Count==2&&ReferenceEquals(b[1],other)&&Hash(b[1])==otherHash,"unrelated body changed");
        Log(new{scenario=label,before,after,gain,expected,error=Math.Abs(gain-expected),hash=Hash(b[0]),warnings});
    }
    static void Suite() {
        foreach(bool unite in new[]{false,true})
        foreach(bool defer in new[]{false,true})
        foreach(bool remote in new[]{false,true})
        foreach(double radius in new[]{1.0,2.0,4.0}){
            string name=$"step {(unite?"union":"cut")} r{radius} defer={defer} remote={remote}";
            Check(name,()=>{Reset();Remote=remote;Scenario(name,unite,radius,defer:defer);});
        }
        foreach(bool unite in new[]{false,true})
        foreach(double z in new[]{.005,.01,.1})
        foreach(bool reverse in new[]{false,true}){
            string name=$"thin z{z} union={unite} reversed={reverse}";
            Check(name,()=>{Reset();Z=z;Scenario(name,unite,2*Z,reverse:reverse);});
        }
        foreach(bool unite in new[]{false,true})
        foreach(double xy in new[]{200.0,1000.0}){
            string name=$"elongated xy{xy} union={unite}";
            Check(name,()=>{Reset();XY=xy;Scenario(name,unite,2);});
        }
        for(int rotation=0;rotation<4;rotation++)
        foreach(bool reverse in new[]{false,true}){
            int rr=rotation;string name=$"translated rotated={rr} reversed={reverse}";
            Check(name,()=>{
                Reset();Origin=new Vector3d(120,-80,30);
                if(rr==1){U=Vector3d.UnitY;V=Vector3d.UnitZ;W=Vector3d.UnitX;}
                if(rr==2){U=-Vector3d.UnitX;V=Vector3d.UnitY;W=-Vector3d.UnitZ;}
                if(rr==3){double a=.37;U=new(Math.Cos(a),Math.Sin(a),0);V=new(-Math.Sin(a),Math.Cos(a),0);W=Vector3d.UnitZ;}
                Remote=true;Scenario(name,false,2,reverse:reverse);
            });
        }
        foreach(bool remote in new[]{false,true})Check($"concave chamfer remote={remote}",()=>{
            Reset();Remote=remote;Scenario($"chamfer-{remote}",false,2,chamfer:true);
        });
        for(int i=0;i<12;i++){
            int at=i;Check($"convex cube edge {i}",()=>{
                Reset();var s=Box();var b=new List<TSolid>{s};var e=s.Edges.ToArray()[at];
                var warnings=new List<string>();int cut=0,add=0;
                FilletOp.AppliedTool=_=>cut++;FilletOp.AppliedAddition=_=>add++;
                Require(FilletOp.DescribeEdge(b,e).EndsWith("Convex=True"),"convex edge changed classification");
                double expected=e.Curve.Length*4*(1-Math.PI/4);
                long direct0=Parametric3D.Kernel.Ops.DirectRound.Built;
                FilletOp.RoundEdges(b,new[]{EdgeRef.Of(e)},2,warnings);
                bool direct=Parametric3D.Kernel.Ops.DirectRound.Built>direct0;   // KRN-06: прямой построитель — без резца
                double removed=48000-AnalyticOrExact(b[0],IsAnalytic(b[0]));   // аналитическое тело — аналитическим объёмом
                Require((cut==1&&add==0)||(direct&&cut==0&&add==0),"wrong operation");
                Require(Math.Abs(removed-expected)<expected*.016+.05,$"removed {removed}, expected {expected}");
                Require(TopologyCheck.Validate(b[0]).Count==0,"convex topology");
                Log(new{scenario=$"convex-{at}",hash=Hash(b[0]),volume=ExactVolume(b[0])});
            });
        }
        Check("radius too large remains refused without mutation",()=>{
            Reset();var w=new List<string>();var b=Step(false,w);var before=b[0];string hash=Hash(before);
            bool rejected=false;try{FilletOp.RoundEdges(b,new[]{EdgeRef.Of(Select(before))},12,w);}catch(RebuildException){rejected=true;}
            Require(rejected&&ReferenceEquals(before,b[0])&&Hash(b[0])==hash,"invalid operation not atomic");
        });
        Check("hole rim convex regression",()=>{
            Reset();var b=new List<TSolid>{Box()};var w=new List<string>();
            BooleanOp.Subtract(b,PrimitiveOp.Cylinder(new Vector3d(30,20,-2),8,24),w);
            var e=b[0].Edges.Where(e=>!e.Hidden&&e.Curve is ArcCurve3).OrderByDescending(e=>e.V0.P.Z).First();
            double before=ExactVolume(b[0]);
            FilletOp.RoundEdges(b,new[]{EdgeRef.Of(e)},2,w);
            Require(ExactVolume(b[0])<before,"hole rim round should remove material");
            Log(new{scenario="hole-rim",hash=Hash(b[0]),volume=ExactVolume(b[0])});
        });
        Reset();Log(new{passed=Passed,failed=Failed});Environment.ExitCode=Failed==0?0:1;
    }
    static void Probe(bool unite) {
        var warnings=new List<string>();var b=Step(unite,warnings);var s=b.Single();var e=Select(s);
        if(Remote)b.Add(Box(100000,0,0));
        Log(new{scene=unite?"union":"cut",volume=Volume(s),exact=s.ExactVolume(),faces=s.Faces.Count(),
            topology=TopologyCheck.Validate(s).Count,edge=FilletOp.DescribeEdge(b,e)});
        var p=e.Curve.PointAt(.5);
        Log(new{a=e.V0.P.ToString(),b=e.V1.P.ToString(),mid=p.ToString(),
            n1=e.FaceA!.NormalAt(p).ToString(),n2=e.FaceB!.NormalAt(p).ToString()});
        foreach(bool facets in new[]{false,true}){
            var m=Tessellator.Tessellate(s,facets);var tris=Occupancy.Triangles(m);
            foreach(int x in new[]{-1,1})foreach(int z in new[]{-1,1}){
                var q=p+new Vector3d(x*.05,0,z*.05);
                Log(new{facets,x,z,inside=PointInSolid.Test(tris,q),expected=x<0||z<0,triangles=tris.Count,whole=m.Whole});
            }
        }
        foreach(var face in new[]{e.FaceA,e.FaceB}){
            var fs=face!.Surface as FacetedSurface;
            Log(new{face=face.Id,reversed=face.Reversed,surface=face.Surface.GetType().Name,
                source=fs?.Source?.GetType().Name,normal=face.NormalAt(p).ToString(),facets=fs?.Facets.Count,
                triangles=fs?.Facets.Take(3).Select(t=>new[]{t.A.ToString(),t.B.ToString(),t.C.ToString()}).ToArray()});
        }
        FilletOp.PathDebug=msg=>Log(new{path=msg});
        FilletOp.BuiltTool=t=>Log(new{builtVolume=Volume(t)});
        FilletOp.AppliedAddition=t=>Log(new{addition=Volume(t)});
        FilletOp.AppliedTool=t=>Log(new{subtraction=Volume(t)});
        var sw=Stopwatch.StartNew();
        string? error=null;
        try{FilletOp.RoundEdges(b,new[]{EdgeRef.Of(e)},2*Z,warnings);}catch(Exception ex){error=ex.Message;}
        Log(new{resultVolume=Volume(b[0]),expected=36000*XY*XY*Z+40*XY*4*Z*Z*(1-Math.PI/4),
            elapsedMs=sw.Elapsed.TotalMilliseconds,error,warnings});
    }
    static void Main(string[] args) {
        BooleanOp.Threads=Tessellator.Threads=1;
        if(args.Contains("numerics")){
#if CONTACT_CHECKS
            Check("large homogeneous coordinates have finite approximations",()=>{
                var r=new PlaneRegistry();var d=System.Numerics.BigInteger.One<<1800;
                var a=r.Plane(d,0,0,-(5*d+1));var b=r.Plane(0,d,0,3*d-1);var c=r.Plane(0,0,d,-(7*d+1));
                int v=r.Vertex(a.Id,b.Id,c.Id);var q=r.Approx(v);
                Require(q==new Vector3d(5,-3,7),"overflow in approximate coordinates");
                Require(r.SideOfBox(q,q,r.Plane(1,0,0,-4))==1,"incorrect conservative box answer");
                Require(r.SideOfBox(new(double.NaN),q,a)==0,"NaN box classified");
                Require(r.SideOfBox(new(double.NegativeInfinity),q,a)==0,"infinite box classified");
            });
            foreach(int bits in new[]{600,1800})
                Check($"large plane normal {bits}",()=>{
                    var r=new PlaneRegistry();var d=System.Numerics.BigInteger.One<<bits;
                    var p=r.Plane(d+1,-2*d+1,3*d+1,1);var n=r.UnitNormal(p);
                    Require((new Vector3d(n.X,n.Y,n.Z)-new Vector3d(1,-2,3).Normalized()).Length<1e-14,"bad scaled normal");
                });
            foreach(double gap in new[]{.25/ExactGrid.UnitsPerMm,2/ExactGrid.UnitsPerMm})
                Check($"do not infer contact from a small gap {gap}",()=>{
                    Reset();var axis=new Vector3d(.2,.7,.4).Normalized();
                    U=RevolvedSurface.RotateAbout(Vector3d.UnitX,Vector3d.Zero,axis,.37);
                    V=RevolvedSurface.RotateAbout(Vector3d.UnitY,Vector3d.Zero,axis,.37);
                    W=RevolvedSurface.RotateAbout(Vector3d.UnitZ,Vector3d.Zero,axis,.37);
                    var r=new PlaneRegistry();var body=ExactAdapter.ToExact(Box(),r,new());
                    var tool=Box(gap,gap,20+gap,20,20,10);
                    int planes=r.PlaneCount,vertices=r.VertexCount;string hash=Hash(tool);
                    Require(!ExactContactInput.TryBuild(tool,body,out var fitted),"small gap was associated as contact");
                    Require(fitted==null && tool.Exact==null && Hash(tool)==hash,"decline modified operand");
                    Require(r.PlaneCount==planes && r.VertexCount==vertices,"decline modified body registry");
                });
            Check("successful association does not modify operands",()=>{
                Reset();var axis=new Vector3d(.2,.7,.4).Normalized();
                U=RevolvedSurface.RotateAbout(Vector3d.UnitX,Vector3d.Zero,axis,.37);
                V=RevolvedSurface.RotateAbout(Vector3d.UnitY,Vector3d.Zero,axis,.37);
                W=RevolvedSurface.RotateAbout(Vector3d.UnitZ,Vector3d.Zero,axis,.37);
                var r=new PlaneRegistry();var original=Box();var body=ExactAdapter.ToExact(original,r,new());
                var tool=Box(30,0,10,30,40,10);string hash=Hash(tool);
                int planes=r.PlaneCount,vertices=r.VertexCount;
                Require(ExactContactInput.TryBuild(tool,body,out var fitted),"coincident contact rejected");
                CheckExactShell(fitted!.Solid);
                Require(tool.Exact==null && original.Exact==null && Hash(tool)==hash,"association modified operand");
                Require(r.PlaneCount==planes && r.VertexCount==vertices,"association modified body registry");
            });
            Log(new{passed=Passed,failed=Failed});Environment.ExitCode=Failed==0?0:1;
#else
            Log(new{error="Rebuild with DefineConstants=CONTACT_CHECKS"});Environment.ExitCode=2;
#endif
            return;
        }
        if(args.Contains("filletbench")){
            foreach(string name in new[]{"Z37","Z10","axis1-90","axis2-90"})
                for(int i=0;i<7;i++){
                    Reset();Origin=new(120,-80,30);
                    double angle=name=="Z37"?.37:name=="Z10"?.1:.9;
                    var axis=name=="axis1-90"?new Vector3d(1,1,1).Normalized():
                        name=="axis2-90"?new Vector3d(.2,.7,.4).Normalized():Vector3d.UnitZ;
                    if(name.StartsWith("axis"))Origin=new(12,-8,3);
                    U=RevolvedSurface.RotateAbout(Vector3d.UnitX,Vector3d.Zero,axis,angle);
                    V=RevolvedSurface.RotateAbout(Vector3d.UnitY,Vector3d.Zero,axis,angle);
                    W=RevolvedSurface.RotateAbout(Vector3d.UnitZ,Vector3d.Zero,axis,angle);
                    var warnings=new List<string>();var sw=Stopwatch.StartNew();var bodies=Step(false,warnings);
                    double stepMs=sw.Elapsed.TotalMilliseconds;var edge=Select(bodies[0]);
                    long before=GC.GetAllocatedBytesForCurrentThread();sw.Restart();string? error=null;
                    try{FilletOp.RoundEdges(bodies,new[]{EdgeRef.Of(edge)},2,warnings);}
                    catch(RebuildException ex){error=ex.Message;}
                    sw.Stop();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
                    Log(new{scenario=name,iteration=i,warmup=i<2,stepMs,roundMs=sw.Elapsed.TotalMilliseconds,allocated,error,
                        volume=ExactVolume(bodies[0]),polygons=bodies[0].Exact?.Solid.Polygons.Count});
                }
            return;
        }
        if(args.Contains("target")){
            Parametric3D.Kernel.Exact.PlaneRegistry.VerifyEveryAnswer=true;
            foreach(bool unite in new[]{false,true})
            foreach(bool reverse in new[]{false,true}){
                string label=$"target angle=.37 union={unite} reverse={reverse}";
                Check(label,()=>{
                    Reset();DeepChecks=true;Remote=true;Origin=new(120,-80,30);
                    U=new(Math.Cos(.37),Math.Sin(.37),0);V=new(-Math.Sin(.37),Math.Cos(.37),0);
                    Scenario(label,unite,2,reverse:reverse);
                });
            }
            Log(new{passed=Passed,failed=Failed});Environment.ExitCode=Failed==0?0:1;return;
        }
        if(args.Contains("inputbench")){
            Reset();Origin=new(120,-80,30);double angle=.37;
            U=new(Math.Cos(angle),Math.Sin(angle),0);V=new(-Math.Sin(angle),Math.Cos(angle),0);
            if(args.Contains("spatial")){
                Origin=new(12,-8,3);var axis=args.Contains("axis1")?new Vector3d(1,1,1).Normalized():new Vector3d(.2,.7,.4).Normalized();
                U=RevolvedSurface.RotateAbout(Vector3d.UnitX,Vector3d.Zero,axis,angle);
                V=RevolvedSurface.RotateAbout(Vector3d.UnitY,Vector3d.Zero,axis,angle);
                W=RevolvedSurface.RotateAbout(Vector3d.UnitZ,Vector3d.Zero,axis,angle);
            }
            var warnings=new List<string>();var bodies=Step(false,warnings);
            TSolid? tool=null;FilletOp.BuiltTool=t=>tool=t;
            try{FilletOp.RoundEdges(bodies,new[]{EdgeRef.Of(Select(bodies[0]))},2,warnings);}
            catch(RebuildException){}
            Require(tool!=null,"no captured tool");
            for(int i=0;i<9;i++){
                var before=GC.GetAllocatedBytesForCurrentThread();var sw=Stopwatch.StartNew();
                var exact=Parametric3D.Kernel.Exact.ExactAdapter.ToExact(tool!,new(),new()).Solid;
                sw.Stop();var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
                Log(new{iteration=i,warmup=i<2,ms=sw.Elapsed.TotalMilliseconds,allocated,
                    polygons=exact.Polygons.Count,uncancelled=exact.UncancelledSegments(),
                    volume=exact.VolumeMm3().ToDouble()});
            }
            return;
        }
        if(args.Contains("rotations")){
            Parametric3D.Kernel.Exact.PlaneRegistry.VerifyEveryAnswer=true;
            foreach(double angle in new[]{.1,.37,.7,1.2,2.0,2.9,3.7,5.5})
            foreach(bool unite in new[]{false,true})
            foreach(bool reverse in new[]{false,true}){
                string label=$"Z angle={angle} union={unite} reverse={reverse}";
                Check(label,()=>{
                    Reset();DeepChecks=true;Remote=true;Origin=new(120,-80,30);
                    U=new(Math.Cos(angle),Math.Sin(angle),0);V=new(-Math.Sin(angle),Math.Cos(angle),0);
                    Scenario(label,unite,2,reverse:reverse);
                });
            }
            foreach(var axis in new[]{new Vector3d(1,1,1).Normalized(),new Vector3d(.2,.7,.4).Normalized(),Vector3d.UnitY})
            foreach(double angle in new[]{.37,.9})
            foreach(bool reverse in new[]{false,true}){
                string label=$"3D axis={axis} angle={angle} reverse={reverse}";
                Check(label,()=>{
                    Reset();DeepChecks=true;Origin=new(12,-8,3);
                    U=RevolvedSurface.RotateAbout(Vector3d.UnitX,Vector3d.Zero,axis,angle);
                    V=RevolvedSurface.RotateAbout(Vector3d.UnitY,Vector3d.Zero,axis,angle);
                    W=RevolvedSurface.RotateAbout(Vector3d.UnitZ,Vector3d.Zero,axis,angle);
                    Scenario(label,false,2,reverse:reverse);
                });
            }
            Log(new{passed=Passed,failed=Failed});Environment.ExitCode=Failed==0?0:1;return;
        }
#if DIAGNOSTIC_STAGES
        if(args.Contains("rotprobe")){
            Reset();Origin=new(120,-80,30);double angle=args.Length>1&&double.TryParse(args[1],out var supplied)?supplied:.37;
            U=new(Math.Cos(angle),Math.Sin(angle),0);V=new(-Math.Sin(angle),Math.Cos(angle),0);
            if(args.Contains("spatial")){
                Origin=new(12,-8,3);var axis=args.Contains("axis1")?new Vector3d(1,1,1).Normalized():new Vector3d(.2,.7,.4).Normalized();
                U=RevolvedSurface.RotateAbout(Vector3d.UnitX,Vector3d.Zero,axis,angle);
                V=RevolvedSurface.RotateAbout(Vector3d.UnitY,Vector3d.Zero,axis,angle);
                W=RevolvedSurface.RotateAbout(Vector3d.UnitZ,Vector3d.Zero,axis,angle);
            }
            var warnings=new List<string>();var bodies=Step(args.Contains("union"),warnings);
            var edge=Select(bodies[0]);TSolid? tool=null;
            if(args.Contains("reverse")){
                (edge.V0,edge.V1)=(edge.V1,edge.V0);edge.Curve=edge.Curve.Reversed();
                foreach(var ce in edge.CoEdges)ce.Reversed=!ce.Reversed;
            }
            BooleanOp.ExactStageDebug=(stage,s)=>{
                var rings=Parametric3D.Kernel.Exact.ExactAdapter.Rings(s.Registry,s.Polygons);
                var users=new Dictionary<(int,int),int>();
                foreach(var ring in rings)for(int i=0;i<ring.Count;i++){
                    int a=ring[i],b=ring[(i+1)%ring.Count];var key=a<b?(a,b):(b,a);
                    users[key]=users.GetValueOrDefault(key)+1;
                }
                var ids=rings.SelectMany(x=>x).Distinct().ToArray();
                var collapsed=ids.GroupBy(v=>s.Registry.ToMm(v)).Where(g=>g.Count()>1).Count();
                var links=new Dictionary<int,List<(int,int)>>();
                foreach(var ring in rings)for(int i=0;i<ring.Count;i++){
                    if(!links.TryGetValue(ring[i],out var ls))links[ring[i]]=ls=new();
                    ls.Add((ring[(i+ring.Count-1)%ring.Count],ring[(i+1)%ring.Count]));
                }
                int pinches=0;
                foreach(var ls in links.Values){
                    var seen=new HashSet<int>{ls[0].Item1};bool changed=true;
                    while(changed){changed=false;foreach(var (a,b) in ls){
                        if(seen.Contains(a)&&seen.Add(b))changed=true;
                        if(seen.Contains(b)&&seen.Add(a))changed=true;
                    }}
                    if(ls.Any(p=>!seen.Contains(p.Item1)||!seen.Contains(p.Item2)))pinches++;
                }
                Log(new{stage,polygons=s.Polygons.Count,volume=s.VolumeMm3().ToDouble(),
                    uncancelled=s.UncancelledSegments(),badEdges=users.Count(kv=>kv.Value!=2),collapsed,pinches,
                    examples=users.Where(kv=>kv.Value!=2).Take(4).Select(kv=>new{users=kv.Value,
                        a=s.Registry.ToMm(kv.Key.Item1).ToString(),b=s.Registry.ToMm(kv.Key.Item2).ToString()})});
            };
            FilletOp.BuiltTool=s=>{
                tool=s;
                string? open=(string?)typeof(FilletOp).GetMethod("OpenEdges",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{new List<TSolid>{s}});
                Log(new{toolOpen=open});
            };
            Parametric3D.Kernel.Exact.PlaneRegistry.VerifyEveryAnswer=true;
            FilletOp.PathDebug=s=>Log(new{fillet=s});
            try{FilletOp.RoundEdges(bodies,new[]{EdgeRef.Of(edge)},2,warnings);}catch(Exception ex){Log(new{error=ex.Message});}
            Log(new{warnings});
            if(tool!=null){
                BooleanOp.ExactStageDebug=null;var bad=new List<TSolid>{bodies[0]};
                warnings.Clear();BooleanOp.Unite(bad,tool,warnings);
                string? open=(string?)typeof(FilletOp).GetMethod("OpenEdges",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{bad});
                Log(new{rawResultVolume=ExactVolume(bad[0]),open,warnings,
                    facets=bad[0].Faces.Sum(f=>(f.Surface as FacetedSurface)?.Facets.Count??0)});
            }
            return;
        }
#endif
        if(args.Contains("suite")){Suite();return;}
        if(args.Contains("long"))XY=1000;
        if(args.Contains("thin"))Z=.005;
        Remote=args.Contains("remote");
        Probe(args.Contains("union"));
    }
}
