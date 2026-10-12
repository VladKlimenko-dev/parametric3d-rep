using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

static class Program
{
    static int passed, failed;
    static void Log(object o) => Console.WriteLine(JsonSerializer.Serialize(o));
    static void Check(string name, Action action) {
        try { action(); passed++; Log(new {test=name,ok=true}); }
        catch(Exception e) {failed++;Log(new{test=name,ok=false,error=e.ToString()});}
    }
    static void Require(bool b,string s="assertion failed") {if(!b)throw new Exception(s);}
    static void Near(double a,double b,double tol) => Require(Math.Abs(a-b)<=tol,$"{a} != {b} (tol {tol})");
    static Vector3d D(Vector3 p)=>new(p.X,p.Y,p.Z);
    static TSolid Box(double x=0,double y=0,double z=0,double w=60,double d=40,double h=20) {
        var f=new Frame3(new Vector3d(x,y,z),Vector3d.UnitX,Vector3d.UnitY);
        var p=new[]{f.ToWorld(0,0),f.ToWorld(w,0),f.ToWorld(w,d),f.ToWorld(0,d)};
        return ExtrudeOp.Build(ProfileBuilder.Build(f,Enumerable.Range(0,4)
            .Select(i=>(ICurve3)new LineCurve3(p[i],p[(i+1)%4])).ToList()),new Vector3d(0,0,h));
    }
    static TSolid Sheet(TFace f) {var s=new TSolid();var sh=s.AddShell();sh.IsClosed=false;sh.Add(f);return s;}
    static TFace PolyFace(ISurface3 s, Vector2d[] uv, int reverseMask=0, bool shuffle=false) {
        var f=new TFace {Surface=s};var l=f.AddLoop();
        var v=uv.Select(q=>new TVertex(s.PointAt(q))).ToArray();
        for(int i=0;i<v.Length;i++){
            bool rev=(reverseMask&(1<<i))!=0;
            var a=v[i];var b=v[(i+1)%v.Length];
            var c=new ParamCurve(s,uv[i],uv[(i+1)%v.Length]);
            var e=new TEdge{V0=rev?b:a,V1=rev?a:b,Curve=rev?c.Reversed():c};
            l.Add(e,rev);
        }
        if(shuffle&&l.CoEdges.Count==4)(l.CoEdges[1],l.CoEdges[3])=(l.CoEdges[3],l.CoEdges[1]);
        return f;
    }
    static Vector2d[] Rect(double u0=0,double v0=0,double u1=20,double v1=10)=>
        new[]{new Vector2d(u0,v0),new Vector2d(u1,v0),new Vector2d(u1,v1),new Vector2d(u0,v1)};
    static double Area(MeshData m)=>MeshQuality.Measure(m).Sum(q=>q.Area);
    static void Winding(MeshData m) {
        for(int i=0;i<m.Indices.Length;i+=3){
            var a=m.Vertices[m.Indices[i]];var b=m.Vertices[m.Indices[i+1]];var c=m.Vertices[m.Indices[i+2]];
            Require(Vector3d.Dot(Vector3d.Cross(D(b.P)-D(a.P),D(c.P)-D(a.P)),D(a.N)+D(b.N)+D(c.N))>0,
                "triangle disagrees with normals");
        }
    }
    static void ValidMesh(MeshData m) {
        Require(m.Whole,"broken mesh");Require(m.Indices.Length>0,"empty mesh");
        Require(m.Indices.All(i=>i>=0&&i<m.Vertices.Length),"index out of range");
        Require(m.Vertices.All(v=>float.IsFinite(v.P.X)&&float.IsFinite(v.P.Y)&&float.IsFinite(v.P.Z)),"nonfinite");
        Require(MeshQuality.Of(m).Degenerate==0,"degenerate triangles");
    }
    static void Metadata(MeshData m,HashSet<uint> ids) {
        Require(m.FaceRanges.Keys.ToHashSet().SetEquals(ids),"missing ranges");
        foreach(var kv in m.FaceRanges){
            var r=kv.Value;Require(r.IndexStart>=0&&r.IndexStart+r.IndexCount<=m.Indices.Length,"range");
            for(int j=r.IndexStart;j<r.IndexStart+r.IndexCount;j++)
                Require(m.Vertices[m.Indices[j]].FaceId==kv.Key,"wrong face id");
        }
        foreach(int i in m.Indices){var p=D(m.Vertices[i].P);
            Require(p.X>=m.Bounds.Min.X-1e-6&&p.X<=m.Bounds.Max.X+1e-6&&
                    p.Y>=m.Bounds.Min.Y-1e-6&&p.Y<=m.Bounds.Max.Y+1e-6&&
                    p.Z>=m.Bounds.Min.Z-1e-6&&p.Z<=m.Bounds.Max.Z+1e-6,"bounds");}
    }
    static string Hash(TSolid s) {
        var m=Tessellator.Tessellate(s);using var stream=new MemoryStream();
        using(var w=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)){
            foreach(var v in m.Vertices){w.Write(v.P.X);w.Write(v.P.Y);w.Write(v.P.Z);}
            foreach(int i in m.Indices)w.Write(i);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    static SectionSurface Section(bool procedural=false) {
        SweepSection At(double u)=>SweepSection.Line(new Vector3d(5*u,0,0),new Vector3d(5*u,10,0));
        if(procedural)return new SectionSurface(At,new double[]{2,3,4,5,6},
            new[]{At(2),At(3),At(4),At(5),At(6)},false,2,6);
        return new SectionSurface(new[]{At(0),At(1),At(2),At(3),At(4)},false);
    }
    static void Tests() {
        Check("plane metadata",()=>{var f=PolyFace(new PlaneSurface(Frame3.XY),Rect());var m=AnalysisMesher.BuildFace(f,2,out bool exact);
            Require(exact);ValidMesh(m);Metadata(m,new(){f.Id});Near(Area(m),200,1e-4);});
        for(int mask=0;mask<16;mask++){
            int k=mask;Check($"plane oriented ring {k}",()=>{var f=PolyFace(new PlaneSurface(Frame3.XY),Rect(),k,true);
                var m=AnalysisMesher.BuildFace(f,2,out bool exact);Require(exact,"unexpected fallback");ValidMesh(m);Near(Area(m),200,1e-3);});
        }
        Check("two-edge circle",()=>{
            var f=new TFace{Surface=new PlaneSurface(Frame3.XY)};var l=f.AddLoop();
            var a=new TVertex(new Vector3d(10,0,0));var b=new TVertex(new Vector3d(-10,0,0));
            l.Add(new TEdge{V0=a,V1=b,Curve=new ArcCurve3(Frame3.XY,10,0,Math.PI)},false);
            l.Add(new TEdge{V0=b,V1=a,Curve=new ArcCurve3(Frame3.XY,10,Math.PI,2*Math.PI)},false);
            var m=AnalysisMesher.BuildFace(f,1,out bool exact);Require(exact,"two curves are a valid ring");
            ValidMesh(m);Near(Area(m),Math.PI*100,1);});
        Check("closed circle coarse size",()=>{
            var f=new TFace{Surface=new PlaneSurface(Frame3.XY)};var l=f.AddLoop();var a=new TVertex(new Vector3d(1,0,0));
            l.Add(new TEdge{V0=a,V1=a,Curve=ArcCurve3.FullCircle(Frame3.XY,1)},false);
            var m=AnalysisMesher.BuildFace(f,20,out bool exact);Require(exact);ValidMesh(m);});
        Check("open residual ring rejected",()=>{
            var f=PolyFace(new PlaneSurface(Frame3.XY),Rect());
            var l=f.Loops[0];l.Add(new TEdge{V0=new TVertex(new(3,3,0)),V1=new TVertex(new(4,4,0)),
                Curve=new LineCurve3(new(3,3,0),new(4,4,0))},false);
            AnalysisMesher.BuildFace(f,2,out bool exact);Require(!exact,"open residual ignored");});
        foreach(bool proc in new[]{false,true})Check($"section domain procedural={proc}",()=>{
            var s=Section(proc);var f=PolyFace(s,Rect(s.UMin,0,s.UMax,1),5,true);
            var m=AnalysisMesher.BuildFace(f,2,out bool exact);Require(exact,"Section fallback");
            ValidMesh(m);Near(Area(m),200,1e-3);});
        Check("curved section",()=>{
            SweepSection A(double z)=>SweepSection.Arc(new Vector3d(0,0,z),Vector3d.UnitX,Vector3d.UnitY,10,Math.PI/2);
            var s=new SectionSurface(new[]{A(0),A(5),A(10),A(15),A(20)},false);
            var f=PolyFace(s,Rect(0,0,4,1),3,true);
            var m=AnalysisMesher.BuildFace(f,1.5,out bool exact);Require(exact,"Section fallback");ValidMesh(m);
            Near(Area(m),Math.PI*100,2);
            foreach(int i in m.Indices){var p=D(m.Vertices[i].P);Near(Math.Sqrt(p.X*p.X+p.Y*p.Y),10,2e-6);}
        });
        Check("analysis preserves Boolean mesh",()=>{
            var s=Box();string h=Hash(s);var md=AnalysisMesher.Build(s,5);ValidMesh(md);Require(Hash(s)==h);
            Metadata(md,s.Faces.Select(f=>f.Id).ToHashSet());});
        Check("broken fallback is visible and atomic",()=>{
            var f=PolyFace(new BadSurface(),Rect());var md=AnalysisMesher.Build(Sheet(f),2);
            Require(!md.Whole,"failure swallowed");Require(md.BrokenFaces.Any(x=>x.Face==f.Id));
            Require(md.Indices.Length==0,"partial face leaked");
        });
        Check("empty fallback is broken",()=>{
            var f=PolyFace(new EmptySurface(),Rect());var md=AnalysisMesher.Build(Sheet(f),2);
            Require(!md.Whole,"empty fallback considered whole");
        });
        Check("empty mesh not fit",()=>Require(!MeshQuality.Of(new MeshData()).FitForAnalysis));
        Check("TetMesher rejects broken source",()=>{
            var m=Tessellator.Tessellate(Box());m.BrokenFaces.Add((1,"injected"));
            var w=new List<string>();var t=TetMesher.Build(m,100,w);
            Require(t.Tets.Count==0&&w.Count>0,"broken source accepted");
        });
        Check("size NaN rejected",()=>{
            bool ok=false;try{AnalysisMesher.Build(Box(),double.NaN);}catch(ArgumentOutOfRangeException){ok=true;}
            Require(ok);
        });
        Check("size infinity rejected",()=>{
            bool ok=false;try{AnalysisMesher.Build(Box(),double.PositiveInfinity);}catch(ArgumentOutOfRangeException){ok=true;}
            Require(ok);
        });
        foreach(double size in new[]{0.0,-1.0}) {
            if(Environment.GetEnvironmentVariable("FIX_BASELINE")=="1") {
                Log(new{test=$"invalid size {size}",skipped=true,reason="baseline can loop forever"});continue;
            }
            Check($"invalid size {size}",()=>{
            bool ok=false;try{AnalysisMesher.Build(Box(),size);}catch(ArgumentOutOfRangeException){ok=true;}Require(ok);
            });
        }
        Check("cylinder closed analysis",()=>{
            var s=PrimitiveOp.Cylinder(Vector3d.Zero,10,20);var w=new List<string>();
            var m=AnalysisMesher.Build(s,2,w);ValidMesh(m);Winding(m);Metadata(m,s.Faces.Select(f=>f.Id).ToHashSet());
            Require(w.Count==0,string.Join(";",w));Near(Area(m),600*Math.PI,25);
        });
        foreach(bool reverse in new[]{false,true})Check($"partial cylinder reverse={reverse}",()=>{
            var arc=new ArcCurve3(Frame3.XY,10,reverse?Math.PI:0,reverse?0:Math.PI);
            var s=new ExtrudedSurface(arc,new Vector3d(0,0,20));var f=PolyFace(s,Rect(0,0,1,1),9,true);
            var m=AnalysisMesher.BuildFace(f,2,out bool exact);Require(exact);ValidMesh(m);Winding(m);
            foreach(int i in m.Indices){var v=m.Vertices[i];Require(Vector3d.Dot(D(v.N),f.NormalAt(D(v.P)))>0.99,"normal differs from surface");}
            Near(Area(m),Math.PI*200,8);
        });
        Check("cylinder hole not erased by rectangle shortcut",()=>{
            var solid=PrimitiveOp.Cylinder(Vector3d.Zero,10,20);
            var face=solid.Faces.First(f=>f.Surface is ExtrudedSurface);
            var extra=PolyFace(face.Surface,Rect(.25,.4,.35,.6));
            var hole=extra.Loops[0];hole.Face=face;hole.IsOuter=false;face.Loops.Add(hole);
            AnalysisMesher.BuildFace(face,2,out bool exact);Require(!exact,"shortcut silently erased hole");
        });
        Check("planar hole area",()=>{
            var f=PolyFace(new PlaneSurface(Frame3.XY),Rect(0,0,40,30),3,true);
            var c=new Vector3d(20,15,0);var a=new TVertex(c+new Vector3d(5,0,0));
            f.AddLoop(false).Add(new TEdge{V0=a,V1=a,Curve=ArcCurve3.FullCircle(new Frame3(c,Vector3d.UnitX,Vector3d.UnitY),5)},false);
            var m=AnalysisMesher.BuildFace(f,1,out bool exact);Require(exact);ValidMesh(m);Winding(m);
            Near(Area(m),1200-Math.PI*25,0.6);
        });
        Check("CDT boundary and area preserved",()=>{
            for(int k=0;k<30;k++){
                var rand=new Random(8100+k);
                var outer=new[]{new Vector2d(0,0),new Vector2d(20,0),new Vector2d(20,15),new Vector2d(0,15)};
                var inner=Enumerable.Range(0,40).Select(_=>new Vector2d(0.1+rand.NextDouble()*19.8,0.1+rand.NextDouble()*14.8)).ToArray();
                var t=Cdt.Triangulate(outer,Array.Empty<IReadOnlyList<Vector2d>>(),out var p,inner)!;
                double area=0;var edges=new Dictionary<(int,int),int>();
                for(int i=0;i<t.Count;i+=3){var a=p[t[i]];var b=p[t[i+1]];var c=p[t[i+2]];
                    double cross=(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);Require(cross>0);area+=cross/2;
                    for(int j=0;j<3;j++){int u=t[i+j],v=t[i+(j+1)%3];var e=u<v?(u,v):(v,u);edges[e]=edges.GetValueOrDefault(e)+1;}}
                Near(area,300,1e-7);Require(edges.Values.All(n=>n==1||n==2));
                Require(edges.Count(e=>e.Value==1)==4,"changed boundary");
            }
        });
        Check("concave plane",()=>{
            var s=new PlaneSurface(Frame3.XY);
            var uv=new[]{new Vector2d(0,0),new Vector2d(30,0),new Vector2d(30,10),new Vector2d(10,10),new Vector2d(10,30),new Vector2d(0,30)};
            var f=PolyFace(s,uv,37);var m=AnalysisMesher.BuildFace(f,2,out bool exact);
            Require(exact);ValidMesh(m);Winding(m);Near(Area(m),500,1e-4);
        });
    }
    static void Corpus() {
        for(int i=0;i<100;i++){
            int k=i;Check($"surface corpus {k}",()=>{
                var r=new Random(2718+k);double w=10+r.NextDouble()*30,h=10+r.NextDouble()*20;
                TFace f;double expected;
                if(k%4==0){
                    var s=Section(k%8==0);f=PolyFace(s,Rect(s.UMin,0,s.UMax,1),k%16,true);expected=200;
                }else if(k%4==1){
                    var s=new PlaneSurface(new Frame3(new Vector3d(k*0.2,3,-7),Vector3d.UnitX,Vector3d.UnitY));
                    f=PolyFace(s,Rect(0,0,w,h),k%16,true);expected=w*h;
                }else if(k%4==2){
                    var s=new PlaneSurface(Frame3.XY);
                    var uv=new[]{new Vector2d(0,0),new Vector2d(w,0),new Vector2d(w,h*0.4),
                        new Vector2d(w*0.4,h*0.4),new Vector2d(w*0.4,h),new Vector2d(0,h)};
                    f=PolyFace(s,uv,k%64);expected=w*h*0.64;
                }else{
                    var s=new PlaneSurface(Frame3.XY);f=PolyFace(s,Rect(0,0,w,h),k%16,true);
                    var center=new Vector3d(w/2,h/2,0);double radius=Math.Min(w,h)*0.15;
                    var a=new TVertex(center+new Vector3d(radius,0,0));
                    f.AddLoop(false).Add(new TEdge{V0=a,V1=a,Curve=ArcCurve3.FullCircle(new Frame3(center,Vector3d.UnitX,Vector3d.UnitY),radius)},false);
                    // The boundary consists of chords with n=ceil(length/size), not an exact circle.
                    // Compare to its exact polygon area; don't misdiagnose discretization as lost area.
                    int n=Math.Max(8,(int)Math.Ceiling(2*Math.PI*radius/(Math.Min(w,h)/12)));
                    expected=w*h-n*radius*radius*Math.Sin(2*Math.PI/n)/2;
                }
                var m=AnalysisMesher.BuildFace(f,Math.Min(w,h)/12,out bool exact);Require(exact,"fallback");
                ValidMesh(m);Winding(m);Metadata(m,new(){f.Id});Near(Area(m),expected,0.005);
            });
        }
    }
    static void Sequences() {
        // New independently reproducible corpus, NOT the missing original 450-sequence suite.
        for(int i=0;i<450;i++){
            int k=i;Check($"new sequence {k}",()=>{
                var r=new Random(91000+k);double w=50+r.NextDouble()*15,d=35+r.NextDouble()*10,h=12+r.NextDouble()*10;
                var b=new List<TSolid>{Box(w:w,d:d,h:h)};var warn=new List<string>();double expected=w*d*h;
                BooleanOp.DeferMeshing=true;
                int count=1+k%3;
                try{
                    for(int j=0;j<count;j++){
                        double radius=1.5+r.NextDouble();
                        BooleanOp.Subtract(b,PrimitiveOp.Cylinder(new Vector3d(10+j*14,12,-2),radius,h+4),warn);
                        expected-=Math.PI*radius*radius*h;
                    }
                    BooleanOp.Subtract(b,Box(w+100,0,0,5,5,h),warn);
                }finally{BooleanOp.DeferMeshing=false;}
                foreach(var s in b)BooleanOp.MeshExactlySolid(s,warn);
                var md=Tessellator.Tessellate(b.Single());ValidMesh(md);Near(MassProps.Volume(md),expected,expected*0.002);
                Log(new{sequence=k,displayHash=Hash(b.Single()),volume=MassProps.Volume(md),triangles=md.Indices.Length/3});
            });
        }
    }
    static void Bench() {
        foreach(double size in new[]{4.0,2.0,1.0}){
            var f=PolyFace(new PlaneSurface(Frame3.XY),Rect(0,0,60,40));
            for(int run=0;run<6;run++){
                long a=GC.GetTotalAllocatedBytes(true);var sw=Stopwatch.StartNew();
                var m=AnalysisMesher.BuildFace(f,size,out bool exact);sw.Stop();
                Log(new{bench="plane60x40",size,run,ms=sw.Elapsed.TotalMilliseconds,
                    allocatedMB=(GC.GetTotalAllocatedBytes(true)-a)/1e6,exact,triangles=m.Indices.Length/3,
                    area=Area(m),quality=MeshQuality.Of(m).ToString()});
            }
        }
    }
    static void Main(string[] args) {
        Log(new{runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,cpu=Environment.ProcessorCount});
        BooleanOp.Threads=Tessellator.Threads=1;
        if(args.Contains("bench"))Bench();
        else if(args.Contains("corpus"))Corpus();
        else if(args.Contains("sequences"))Sequences();
        else Tests();
        Log(new{passed,failed});Environment.ExitCode=failed==0?0:1;
    }
    sealed class ParamCurve(ISurface3 s,Vector2d a,Vector2d b):ICurve3 {
        public double T0=>0;public double T1=>1;public bool IsClosed=>a==b;
        public Vector3d PointAt(double t)=>s.PointAt(a+(b-a)*t);
        public Vector3d TangentAt(double t)=>(PointAt(t+1e-5)-PointAt(t-1e-5)).Normalized();
        public double Length {get {double n=0;var p=PointAt(0);for(int i=1;i<=64;i++){var q=PointAt(i/64.0);n+=(q-p).Length;p=q;}return n;}}
        public Bounds3 Bounds {get {var b=Bounds3.Empty;for(int i=0;i<=64;i++)b.Add(PointAt(i/64.0));return b;}}
        public ICurve3 Reversed()=>new ParamCurve(s,b,a);
        public void Tessellate(List<Vector3d> dst,bool includeFirstPoint){for(int i=includeFirstPoint?0:1;i<=32;i++)dst.Add(PointAt(i/32.0));}
    }
    class EmptySurface:ISurface3 {
        public bool IsPlanar=>false;
        public Vector3d PointAt(Vector2d q)=>new(q.X,q.Y,0);
        public Vector3d NormalAt(Vector2d q)=>Vector3d.UnitZ;
        public Vector2d ProjectToUV(Vector3d p)=>new(p.X,p.Y);
        public virtual void TessellateFace(TFace f,MeshBuilder mb){}
    }
    sealed class BadSurface:EmptySurface {
        public override void TessellateFace(TFace f,MeshBuilder mb){
            int a=mb.AddVertex(new(0,0,0),Vector3d.UnitZ);
            int b=mb.AddVertex(new(1,0,0),Vector3d.UnitZ);
            int c=mb.AddVertex(new(0,1,0),Vector3d.UnitZ);mb.AddTriangle(a,b,c);throw new InvalidOperationException("injected");
        }
    }
}
