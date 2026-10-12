using System.Text.Json;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel.Tess;
using Parametric3D.Core;

static class Program
{
    static int passed, failed, accepted, declined;
    static void Require(bool b,string s){if(!b)throw new Exception(s);}
    static void Log(object o)=>Console.WriteLine(JsonSerializer.Serialize(o));
    static void Check(string name,Action a){
        try{a();passed++;Log(new{test=name,ok=true});}
        catch(Exception e){failed++;Log(new{test=name,ok=false,error=e.Message});}
    }
    static string Signature(ExactSolid s){
        return string.Join("\n",s.Polygons.Select(p=>{
            var v=p.Vertices(s.Registry).ToArray().Select(i=>s.Registry.Homogeneous(i).ToString()).ToArray();
            int start=Enumerable.Range(0,v.Length).OrderBy(i=>v[i],StringComparer.Ordinal).First();
            return $"{p.Tag}:{s.Registry.Coefficients(p.Support.Id)}:{p.Support.Side}:"+
                string.Join("|",Enumerable.Range(0,v.Length).Select(i=>v[(i+start)%v.Length]));
        }).OrderBy(x=>x,StringComparer.Ordinal));
    }
    static void EqualInput(TSolid s){
        var fastR=new PlaneRegistry();
        var old=ExactAdapter.ToExact(s,new PlaneRegistry(),new());
        if(!ExactCylinderInput.TryBuild(s,fastR,out var fast)){
            declined++;
            Require(fastR.PlaneCount==0&&fastR.VertexCount==0,"fallback polluted registry");
            Environment.SetEnvironmentVariable("P3D_CYLINDERINPUT","1");
            try{Require(Signature(ExactAdapter.ToExact(s,new PlaneRegistry(),new()).Solid)==Signature(old.Solid),"fallback changed result");}
            finally{Environment.SetEnvironmentVariable("P3D_CYLINDERINPUT","0");}
            return;
        }
        accepted++;
        if(Signature(old.Solid)!=Signature(fast!.Solid)){
            Log(new{diagnostic="regions",oldPolys=old.Solid.Polygons.Count,newPolys=fast.Solid.Polygons.Count,
                oldVolume=old.Solid.VolumeMm3().ToString(),newVolume=fast.Solid.VolumeMm3().ToString(),
                onlyOld=Signature(old.Solid).Split('\n').Except(Signature(fast.Solid).Split('\n')).Take(1).ToArray(),
                onlyNew=Signature(fast.Solid).Split('\n').Except(Signature(old.Solid).Split('\n')).Take(1).ToArray()});
            throw new Exception("exact oriented polygon regions differ");
        }
        Require(old.Solid.VolumeMm3()==fast.Solid.VolumeMm3(),"exact volumes differ");
        Require(old.Solid.UncancelledSegments()==0&&fast.Solid.UncancelledSegments()==0,"uncancelled edges");
        Require(old.Sources.Count==fast.Sources.Count&&old.Sources.All(kv=>ReferenceEquals(kv.Value,fast.Sources[kv.Key])),"sources differ");
    }
    static TSolid CustomCylinder(Vector3d at,Vector3d axis,bool reverse=false){
        var u=axis==Vector3d.UnitX?Vector3d.UnitY:Vector3d.UnitX;
        var f=new Frame3(at,u,Vector3d.Cross(axis,u));
        ICurve3 a=new ArcCurve3(f,3,reverse?Math.PI*2:0,reverse?0:Math.PI*2);
        return ExtrudeOp.Build(ProfileBuilder.Build(f,new List<ICurve3>{a}),axis*12);
    }
    static void Rejected(string name,TSolid s)=>Check(name,()=>{
        var r=new PlaneRegistry();
        Require(!ExactCylinderInput.TryBuild(s,r,out _),"unsupported input accepted");
        Require(r.PlaneCount==0&&r.VertexCount==0,"rejected input polluted registry");
    });
    static void Main(){
        BooleanOp.Threads=Tessellator.Threads=1;
        Environment.SetEnvironmentVariable("P3D_CYLINDERINPUT","0");
        PlaneRegistry.VerifyEveryAnswer=true;
        double oldChord=Tol.Chord;
        for(int i=0;i<96;i++){
            int at=i;
            Check($"cylinder {at}",()=>{
                double[] radii={.001,.03,.5,3,8,20};
                double[] heights={.01,.5,10,24};
                Tol.Chord=new[]{.005,.02,.05,.01}[at/24];
                Vector3d origin=(at%4) switch{
                    0=>new(0,0,0),
                    1=>new(13.1234567,-20.3333333,17.8123456),
                    2=>new(60000,-40000,20000),
                    _=>new(.000005,-.000015,.000025)
                };
                EqualInput(PrimitiveOp.Cylinder(origin,radii[at%6]*(1+.07*(at/24)),heights[(at/6)%4]*(1+.03*(at/24))));
            });
        }
        Tol.Chord=oldChord;
        Rejected("tilted cylinder",CustomCylinder(new(0,0,0),new Vector3d(0,.6,.8)));
        Rejected("X cylinder",CustomCylinder(new(0,0,0),Vector3d.UnitX));
        Rejected("negative Z cylinder",CustomCylinder(new(0,0,0),-Vector3d.UnitZ));
        Rejected("cone",PrimitiveOp.Cone(Vector3d.Zero,4,2,10));
        Rejected("sphere",PrimitiveOp.Sphere(Vector3d.Zero,3));
        var missing=PrimitiveOp.Cylinder(Vector3d.Zero,3,10);missing.Shells[0].Faces.RemoveAt(1);
        Rejected("missing face",missing);
        var trim=PrimitiveOp.Cylinder(Vector3d.Zero,3,10);trim.Faces.First().AddLoop(false);
        Rejected("trimmed side",trim);
        var flipped=PrimitiveOp.Cylinder(Vector3d.Zero,3,10);flipped.Faces.First().Reversed=true;
        Rejected("flipped side",flipped);
        Log(new{passed,failed,accepted,declined});Environment.ExitCode=failed==0?0:1;
    }
}
