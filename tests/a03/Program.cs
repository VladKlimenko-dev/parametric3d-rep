using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel;
using Parametric3D.Core;

static class Program
{
    static int passed,failed;
    static void Log(object o)=>Console.WriteLine(JsonSerializer.Serialize(o));
    static void Require(bool b,string why){if(!b)throw new Exception(why);}
    static void Check(string name,Action f){
        try{f();passed++;Log(new{test=name,ok=true});}
        catch(Exception e){failed++;Log(new{test=name,ok=false,error=e.ToString()});}
    }
    static readonly MethodInfo? Hint=typeof(PlaneRegistry).GetMethod("Vertex",
        BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(int),typeof(int),typeof(int),typeof(GridPoint)},null);
    static int Fast(PlaneRegistry r,int a,int b,int c,GridPoint p)=>(int)Hint!.Invoke(r,new object[]{a,b,c,p})!;
    static (OPlane, OPlane[]) Planes(PlaneRegistry r,GridPoint[] p){
        var s=r.PlaneThrough(p[0],p[1],p[2]);
        var b=Enumerable.Range(0,3).Select(i=>r.EdgePlane(s,p[i],p[(i+1)%3])).ToArray();
        return(s,b);
    }
    static void Hints(){
        Require(Hint!=null,"hint overload missing");
        for(int seed=0;seed<1000;seed++){
            int at=seed;Check($"hint triangle {at}",()=>{
                var rng=new Random(at);
                long shift=at%5==0?3_000_000_000_000_000_000L:at%2==0?1_000_000_000_000L:-100_000_000L;
                var p=new GridPoint[3];
                p[0]=new GridPoint(shift,-shift,shift/3);
                for(int i=1;i<3;i++)p[i]=new GridPoint(shift+rng.Next(-1000000,1000000),-shift+rng.Next(-1000000,1000000),shift/3+rng.Next(-1000000,1000000));
                if(at%7==0){p[1]=new(shift+1000000000,-shift+1000000000,shift/3);p[2]=new(shift+1000000001,-shift+1000000000,shift/3+1);}
                if(at%2==0)(p[1],p[2])=(p[2],p[1]);
                var slow=new PlaneRegistry();var fast=new PlaneRegistry();
                var(sa,ba)=Planes(slow,p);var(sb,bb)=Planes(fast,p);
                for(int i=0;i<3;i++){
                    int a=slow.Vertex(sa.Id,ba[(i+2)%3].Id,ba[i].Id);
                    var supplied=at%3==0?new GridPoint(p[i].X+17,p[i].Y-31,p[i].Z+11):p[i];
                    int b=Fast(fast,sb.Id,bb[(i+2)%3].Id,bb[i].Id,supplied);
                    Require(a==b&&slow.Homogeneous(a)==fast.Homogeneous(b),"different vertex or id");
                    Require(fast.Homogeneous(b)==((System.Numerics.BigInteger)p[i].X,(System.Numerics.BigInteger)p[i].Y,(System.Numerics.BigInteger)p[i].Z,System.Numerics.BigInteger.One),"not exact grid point");
                    int again=Fast(fast,bb[i].Id,sb.Id,bb[(i+2)%3].Id,new GridPoint(0,0,0));
                    Require(again==b,"triple cache changed");
                }
            });
        }
        Check("degenerate triple still throws",()=>{
            var r=new PlaneRegistry();
            var a=r.Plane(1,0,0,0);var b=r.Plane(0,1,0,0);var c=r.Plane(1,1,0,0);
            bool threw=false;try{Fast(r,a.Id,b.Id,c.Id,new GridPoint(0,0,0));}catch(TargetInvocationException ex) when(ex.InnerException is ArgumentException){threw=true;}
            Require(threw,"infinite intersection accepted");
        });
        Check("wrong hint falls back to rational vertex",()=>{
            var r=new PlaneRegistry();var a=r.Plane(2,0,0,-1);var b=r.Plane(0,3,0,-1);var c=r.Plane(0,0,5,-1);
            var v=Fast(r,a.Id,b.Id,c.Id,new GridPoint(0,0,0));
            Require(r.Homogeneous(v)==((System.Numerics.BigInteger)15,(System.Numerics.BigInteger)10,(System.Numerics.BigInteger)6,(System.Numerics.BigInteger)30),"fraction lost");
        });
    }
    static string Signature(ExactSolid s){
        var text=new StringBuilder();
        foreach(var p in s.Polygons){
            text.Append(s.Registry.Coefficients(p.Support.Id)).Append(':').Append(p.Support.Side).Append('|');
            foreach(var b in p.Bounds)text.Append(s.Registry.Coefficients(b.Id)).Append(':').Append(b.Side).Append(';');
            foreach(int v in p.Vertices(s.Registry))text.Append(s.Registry.Homogeneous(v)).Append(';');
            text.AppendLine();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    static void Corpus(){
        for(int j=0;j<16;j++){
            int at=j;Check($"input {at}",()=>{
                var pos=new Vector3d(at*13.75-50,at*3.25-40,-12+at);
                TSolid solid=(at%4) switch{
                    0=>PrimitiveOp.Cylinder(pos,2+at*.15,5+at),
                    1=>PrimitiveOp.Cone(pos,3+at*.1,1+at*.1,5+at),
                    2=>PrimitiveOp.Sphere(pos,2+at*.1),
                    _=>Box(pos,at)
                };
                var r=new PlaneRegistry();var w=new List<string>();
                var exact=ExactAdapter.ToExact(solid,r,w).Solid;
                string h=Signature(exact);
                var imported=exact.Import(new PlaneRegistry());
                Require(Signature(imported)==h,"registry import changed geometry/order");
                Log(new{scenario=$"input-{at}",hash=h,polygons=exact.Polygons.Count,volume=exact.VolumeMm3().ToString(),warnings=w});
            });
        }
    }
    static TSolid Box(Vector3d origin,int n){
        double a=.13*n;
        var u=new Vector3d(Math.Cos(a),Math.Sin(a),0);var v=new Vector3d(-Math.Sin(a),Math.Cos(a),0);
        var f=new Frame3(origin,u,v);
        var p=new[]{f.ToWorld(0,0),f.ToWorld(10,0),f.ToWorld(10,4),f.ToWorld(5,4),f.ToWorld(5,9),f.ToWorld(0,9)};
        return ExtrudeOp.Build(ProfileBuilder.Build(f,Enumerable.Range(0,p.Length).Select(i=>(ICurve3)new LineCurve3(p[i],p[(i+1)%p.Length])).ToList()),Vector3d.UnitZ*(n+3));
    }
    static void Main(string[] args){
        BooleanOp.Threads=Tessellator.Threads=1;
        PlaneRegistry.VerifyEveryAnswer=true;
        if(args.Contains("corpus"))Corpus();else Hints();
        Log(new{passed,failed});Environment.ExitCode=failed==0?0:1;
    }
}
