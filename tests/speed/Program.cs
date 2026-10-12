using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Diagnostics;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

class Program
{
    static TSolid Box(double x=0,double y=0,double z=0,double w=60,double d=40,double h=20)
    {
        var f=new Frame3(new Vector3d(x,y,z),Vector3d.UnitX,Vector3d.UnitY);
        var p=new[]{f.ToWorld(0,0),f.ToWorld(w,0),f.ToWorld(w,d),f.ToWorld(0,d)};
        return ExtrudeOp.Build(ProfileBuilder.Build(f,Enumerable.Range(0,4)
            .Select(i=>(ICurve3)new LineCurve3(p[i],p[(i+1)%4])).ToList()),new Vector3d(0,0,h));
    }
    static List<TSolid> Build(string scenario,List<string> warnings)
    {
        var b=new List<TSolid>{Box()};
        if(scenario=="box") return b;
        if(scenario=="disjoint") {BooleanOp.Subtract(b,Box(100,0),warnings);return b;}
        if(scenario=="overlap") {BooleanOp.Subtract(b,Box(30,0),warnings);return b;}
        if(scenario.StartsWith("holes"))
        {
            int n=int.Parse(scenario[5..]);
            for(int i=0;i<n;i++)
                BooleanOp.Subtract(b,PrimitiveOp.Cylinder(new Vector3d(8+12*(i%4),10+20*(i/4),-2),3,24),warnings);
            return b;
        }
        if(scenario=="sphere") {BooleanOp.Subtract(b,PrimitiveOp.Sphere(new Vector3d(30,20,20),8),warnings);return b;}
        if(scenario=="round" || scenario=="sequence")
        {
            var e=b[0].Edges.First(e=>(e.V0.P-new Vector3d(0,0,0)).Length<1e-8 &&
               (e.V1.P-new Vector3d(0,0,20)).Length<1e-8 ||
               (e.V1.P-new Vector3d(0,0,0)).Length<1e-8 &&
               (e.V0.P-new Vector3d(0,0,20)).Length<1e-8);
            FilletOp.RoundEdges(b,new[]{EdgeRef.Of(e)},3,warnings);
            if(scenario=="round") return b;
        }
        BooleanOp.Subtract(b,PrimitiveOp.Cylinder(new Vector3d(30,20,-2),8,24),warnings);
        if(scenario=="roundhole" || scenario=="sequence")
        {
            var e=b[0].Edges.Where(e=>!e.Hidden && e.Curve is ArcCurve3)
                .OrderByDescending(e=>e.V0.P.Z).First();
            FilletOp.RoundEdges(b,new[]{EdgeRef.Of(e)},2,warnings);
        }
        return b;
    }
    static object Quality(List<TSolid> bodies,string scenario)
    {
        var meshes=bodies.Select(b=>Tessellator.Tessellate(b)).ToList();
        double v=meshes.Sum(MassProps.Volume);
        double? expected=scenario switch {
            "box" or "disjoint"=>48000,
            "overlap"=>24000,
            "hole"=>48000-Math.PI*64*20,
            "round"=>48000-(9-Math.PI*9/4)*20,
            "sphere"=>48000-2.0/3*Math.PI*512,
            _=>scenario.StartsWith("holes")?48000-int.Parse(scenario[5..])*Math.PI*9*20:null};
        return new { v,expected,relativeError=expected.HasValue?Math.Abs(v-expected.Value)/expected.Value:(double?)null,
            faces=bodies.Sum(b=>b.Faces.Count()),edges=bodies.Sum(b=>b.Edges.Count()),
            triangles=meshes.Sum(m=>m.Indices.Length/3),
            issues=bodies.Sum(b=>TopologyCheck.Validate(b).Count),
            exactPolygons=bodies.Sum(b=>b.Exact?.Solid.Polygons.Count??0),
            registeredPlanes=bodies.Sum(b=>b.Exact?.Solid.Registry.PlaneCount??0),
            registeredVertices=bodies.Sum(b=>b.Exact?.Solid.Registry.VertexCount??0),
            meshHash=MeshHash(meshes),
            openMeshEdges=meshes.Sum(OpenEdges)};
    }
    static string MeshHash(List<MeshData> meshes)
    {
        using var s=new MemoryStream();
        using(var w=new BinaryWriter(s,System.Text.Encoding.UTF8,true))
        foreach(var m in meshes){
            w.Write(m.Vertices.Length); w.Write(m.Indices.Length);
            foreach(var v in m.Vertices){w.Write(v.P.X);w.Write(v.P.Y);w.Write(v.P.Z);}
            foreach(int i in m.Indices)w.Write(i);
        }
        return Convert.ToHexString(SHA256.HashData(s.ToArray()));
    }
    static int OpenEdges(MeshData mesh)
    {
        var ids=new Dictionary<(long,long,long),int>();
        int Id(int i) {
            var p=mesh.Vertices[i].P;
            var k=((long)Math.Round(p.X*1e5),(long)Math.Round(p.Y*1e5),(long)Math.Round(p.Z*1e5));
            if(!ids.TryGetValue(k,out int n)){n=ids.Count;ids[k]=n;}return n;
        }
        var edges=new Dictionary<(int,int),int>();
        for(int i=0;i<mesh.Indices.Length;i+=3)
            for(int j=0;j<3;j++) {
                int a=Id(mesh.Indices[i+j]),b=Id(mesh.Indices[i+(j+1)%3]);if(a==b)continue;
                var k=a<b?(a,b):(b,a); edges[k]=edges.GetValueOrDefault(k)+1;
            }
        return edges.Count(e=>e.Value!=2);
    }
    static void Bugs()
    {
        var arc=new ArcCurve3(Frame3.XY,5,Math.PI,2*Math.PI);
        var edge=new TEdge {Curve=arc,V0=new TVertex(arc.PointAt(arc.T0)),V1=new TVertex(arc.PointAt(arc.T1))};
        var r=EdgeRef.Of(edge);
        Console.WriteLine(JsonSerializer.Serialize(new {test="edgeRefMid",t0=arc.T0,t1=arc.T1,
            distance=(r.Mid!.Value-arc.PointAt((arc.T0+arc.T1)/2)).Length}));
        var vr=new ValueRef(17){Formula="missing_parameter+1"};
        Console.WriteLine(JsonSerializer.Serialize(new{test="invalidFormula",returned=vr.Resolve(new ParameterTable())}));
        var body=Box(); var face=body.Faces.First();face.Surface=new BrokenSurface();
        var mesh=Tessellator.Tessellate(body);
        Console.WriteLine(JsonSerializer.Serialize(new{test="swallowedFace",triangles=mesh.Indices.Length/3,
            volume=MassProps.Volume(mesh),openEdges=OpenEdges(mesh)}));
        var cylinder=PrimitiveOp.Cylinder(Vector3d.Zero,10,20);
        var e=cylinder.Edges.First(e=>e.Curve is ArcCurve3);
        Tol.Chord=0.5; Tol.ChordAngle=1.0;
        var first=ExactFaceMesher.EdgePoints(e);
        Tol.Chord=0.001; Tol.ChordAngle=0.03;
        var reused=ExactFaceMesher.EdgePoints(e);
        e.PolylineCache=null;
        var fresh=ExactFaceMesher.EdgePoints(e);
        Console.WriteLine(JsonSerializer.Serialize(new{test="edgeCacheQuality",coarse=first.Count,afterTolChange=reused.Count,fresh=fresh.Count}));
        var open=Box();
        var removed=open.Shells[0].Faces[0];
        foreach(var ce in removed.Loops.SelectMany(l=>l.CoEdges))ce.Edge.CoEdges.Remove(ce);
        open.Shells[0].Faces.RemoveAt(0);
        Console.WriteLine(JsonSerializer.Serialize(new{test="openSolidValidator",faces=open.Faces.Count(),
            oneSidedEdges=open.Edges.Count(e=>e.CoEdges.Count==1),issues=TopologyCheck.Validate(open).Count}));
        ExactFaceMesher.Remember=true;ExactFaceMesher.Forget();
        var cf=Box().Faces.First();
        var m1=ExactFaceMesher.Mesh(cf,out _);
        if(m1.HasValue){
            var before=m1.Value.P[0]; m1.Value.P[0]+=new Vector3d(123,0,0);
            var m2=ExactFaceMesher.Mesh(cf,out _);
            Console.WriteLine(JsonSerializer.Serialize(new{test="faceCacheAliasing",
                corruptedDistance=m2.HasValue?(m2.Value.P[0]-before).Length:-1}));
        }
        ExactFaceMesher.Remember=false;ExactFaceMesher.Forget();
        Tol.Chord=0.02;Tol.ChordAngle=7.5*Math.PI/180;
        var cb=PrimitiveOp.Cylinder(Vector3d.Zero,10,20);
        Console.WriteLine(JsonSerializer.Serialize(new{test="cylinderBounds",
            min=cb.Bounds.Min.ToString(),max=cb.Bounds.Max.ToString(),expected="(-10,-10,0)..(10,10,20)"}));
        var qw=new List<string>();
        var qb=QuadricCut.Cut(Box(),new Vector3d(30,20,20),3,qw,-Vector3d.UnitZ)!;
        var qm=Tessellator.Tessellate(qb);
        Console.WriteLine(JsonSerializer.Serialize(new{test="quadricMeshMetadata",faces=qb.Faces.Count(),
            faceRanges=qm.FaceRanges.Count,vertexFaceIds=qm.Vertices.Select(v=>v.FaceId).Distinct().Count(),
            edgePolylines=qm.EdgePolylines.Count,points=qm.Points.Count,
            min=qm.Bounds.Min.ToString(),max=qm.Bounds.Max.ToString()}));
        var ctx=new BuildContext{Part=new Part("transaction")};
        var original=Box();ctx.Bodies.Add(original);
        var h=new HoleFeature{Kind=HoleKind.Counterbore,Diameter=6,HeadDiameter=4,Depth=8,ThroughAll=false};
        h.Points.Add(new Vector3d(30,20,20));
        string? error=null;try{h.Execute(ctx);}catch(Exception ex){error=ex.Message;}
        Console.WriteLine(JsonSerializer.Serialize(new{test="failedHoleMutation",error,
            sameBody=ReferenceEquals(ctx.Bodies[0],original),volumeBefore=48000,
            volumeAfter=MassProps.Volume(Tessellator.Tessellate(ctx.Bodies[0]))}));
    }
    sealed class BrokenSurface:ISurface3
    {
        public bool IsPlanar=>true;
        public Vector3d PointAt(Vector2d uv)=>throw new Exception("injected");
        public Vector3d NormalAt(Vector2d uv)=>Vector3d.UnitZ;
        public Vector2d ProjectToUV(Vector3d p)=>Vector2d.Zero;
        public void TessellateFace(TFace face,MeshBuilder mesh)=>throw new Exception("injected");
    }
    static void Main(string[] args)
    {
        Console.WriteLine(JsonSerializer.Serialize(new{runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            cpu=Environment.ProcessorCount,scenario=args.FirstOrDefault(),chord=Tol.Chord}));
        BooleanOp.Threads=Tessellator.Threads=1;
        if(args.Contains("threads")) BooleanOp.Threads=Tessellator.Threads=4;
        if(args.Contains("full")) BooleanOp.LocalizeBooleans=false;
        if(args.Contains("qcut")) BooleanOp.ExactCylinderCuts=true;
        if(args.Contains("nomesh")) BooleanOp.ExactMeshing=false;
        if(args.Contains("verify")) PlaneRegistry.VerifyEveryAnswer=true;
        if(args[0]=="bugs"){Bugs();return;}
        if(args[0]=="tree"){
            var part=new Part("audit");TestGeometry.AddCube(part,60);
            for(int i=0;i<4;i++)part.AddFeature(new ProbeFeature(i));
            for(int i=0;i<4;i++){
                Prof.Enabled=i==3;Prof.Reset();ProbeFeature.Calls=0;
                var sw=Stopwatch.StartNew();part.Rebuild(null);sw.Stop();
                Console.WriteLine(JsonSerializer.Serialize(new{test="tree",run=i,ms=sw.Elapsed.TotalMilliseconds,
                    executed=ProbeFeature.Calls,dirty=part.IsDirty,volume=part.Volume}));
                if(i==3)Console.WriteLine(Prof.Report());
            }return;
        }
        int runs=int.TryParse(Environment.GetEnvironmentVariable("AUDIT_RUNS"),out int count)?count:4;
        for(int run=0;run<runs+1;run++)
        {
            bool profile=run==runs;Prof.Enabled=profile;Prof.Reset();
            ExactBsp.Splits=ExactBsp.Cuts=ExactBsp.BoxHits=ExactBsp.BoxMiss=0;ExactBsp.MaxDepth=0;
            PlaneRegistry.TotalTier1=PlaneRegistry.TotalTier2=PlaneRegistry.TotalTier3=PlaneRegistry.TotalShortcut=0;
            var warnings=new List<string>();BooleanOp.DeferMeshing=!args.Contains("eager");
            long alloc=GC.GetTotalAllocatedBytes(true);int gc0=GC.CollectionCount(0),gc2=GC.CollectionCount(2);
            var sw=Stopwatch.StartNew();
            var b=Build(args[0],warnings);
            double operations=sw.Elapsed.TotalMilliseconds;
            BooleanOp.DeferMeshing=false;
            if(BooleanOp.ExactMeshing&&!args.Contains("eager"))foreach(var s in b)BooleanOp.MeshExactlySolid(s,warnings);
            double meshing=sw.Elapsed.TotalMilliseconds-operations;
            foreach(var s in b)Tessellator.Tessellate(s);
            sw.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new{run,profile,ms=sw.Elapsed.TotalMilliseconds,operations,meshing,
                assembly=sw.Elapsed.TotalMilliseconds-operations-meshing,allocatedMB=(GC.GetTotalAllocatedBytes(true)-alloc)/1e6,
                gen0=GC.CollectionCount(0)-gc0,gen2=GC.CollectionCount(2)-gc2,
                splits=ExactBsp.Splits,cuts=ExactBsp.Cuts,depth=ExactBsp.MaxDepth,boxHits=ExactBsp.BoxHits,
                tier1=PlaneRegistry.TotalTier1,tier2=PlaneRegistry.TotalTier2,tier3=PlaneRegistry.TotalTier3,
                warnings}));
            if(profile){Console.WriteLine(Prof.Report());Prof.Enabled=false;Console.WriteLine(JsonSerializer.Serialize(Quality(b,args[0])));}
        }
    }
    sealed class ProbeFeature(int i):Feature
    {
        public static int Calls;
        public override void Execute(BuildContext c) {
            Calls++;BooleanOp.Subtract(c.Bodies,PrimitiveOp.Cylinder(new Vector3d(-20+i*12,0,-40),3,80),c.Warnings);
        }
    }
}
