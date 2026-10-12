using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel;
static class P {
    static TSolid Box(double x=0,double y=0,double z=0,double w=60,double d=40,double h=20){
        var f=new Frame3(new Vector3d(x,y,z),Vector3d.UnitX,Vector3d.UnitY);
        var p=new[]{f.ToWorld(0,0),f.ToWorld(w,0),f.ToWorld(w,d),f.ToWorld(0,d)};
        return ExtrudeOp.Build(ProfileBuilder.Build(f,Enumerable.Range(0,4).Select(i=>(ICurve3)new LineCurve3(p[i],p[(i+1)%4])).ToList()),new Vector3d(0,0,h));}
    static void Main(){
        BooleanOp.Threads=Tessellator.Threads=1;
        foreach(var id in new[]{"holes4","sphere"}){
            var w=new List<string>(); var b=new List<TSolid>{Box()}; BooleanOp.DeferMeshing=true;
            if(id=="holes4") for(int i=0;i<4;i++) BooleanOp.Subtract(b,PrimitiveOp.Cylinder(new Vector3d(8+12*i,10,-2),3,24),w);
            else BooleanOp.Subtract(b,PrimitiveOp.Sphere(new Vector3d(30,20,20),8),w);
            BooleanOp.DeferMeshing=false; BooleanOp.MeshExactlySolid(b[0],w);
            var sw=new StringWriter(); bool ok=Parametric3D.Kernel.Export.StepExact.Write(sw,new[]{b[0]},"p",w);
            var back=Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(),w);
            Console.WriteLine($"{id} ok={ok} expect={MassProps.Volume(Tessellator.Tessellate(b[0])):0.###} got={back.Sum(s=>MassProps.Volume(Tessellator.Tessellate(s))):0.###} loops={sw.ToString().Split("EDGE_LOOP(").Length-1}");
        }
    }
}
