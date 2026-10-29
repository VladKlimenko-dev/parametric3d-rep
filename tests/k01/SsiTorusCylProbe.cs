using OpenTK.Mathematics;
using Parametric3D.Kernel.Geometry;

static partial class Program
{
    /// <summary>Разбор: тор × цилиндр X-218795-05 (тонкая трубка r 1.5 у кольца R 187, цилиндр R 50 поперёк).</summary>
    static void SsiTorusCylProbe()
    {
        {
            var t2 = new Ssi.Torus(new Vector3d(0, 0, -692.8866), new Vector3d(0, 0, -1), 173.00368077337882, 1.5240000000001146);
            var c2 = new Ssi.Cylinder(new Vector3d(77.28977967257181, 184.66982529869853, -667.6887579470026), new Vector3d(43.99409051224948, -25.400000000000006, 0), 50.23764985739308);
            var r2 = Ssi.Intersect(t2, c2, new Vector3d(0, 5.0710722626598255, -692.8866), 396.1771587966419, (new Vector3d(99.28682492869655, 145.1452125345737, -667.6887579470026), 113.52353092257498));
            Console.WriteLine($"copy torus: kind {r2.Kind} curves {r2.Curves.Count} uncert {r2.Uncertified}");
            var r3 = Ssi.Intersect(t2, c2, new Vector3d(0, 5.0710722626598255, -692.8866), 396.1771587966419, (new Vector3d(99.28682492869655, 145.1452125345737, -667.6887579470026), 113.0));
            Console.WriteLine($"copy torus ball 113.0: kind {r3.Kind} curves {r3.Curves.Count} uncert {r3.Uncertified}");
            Ssi.TransversalDbg = (c, rb, la, lb, cr) => Console.WriteLine($"  TRANS c {c:0.####} rb {rb:G4} la {la:G4} lb {lb:G4} cross {cr:G4}");
            Ssi.Intersect(t2, c2, new Vector3d(0, 5.0710722626598255, -692.8866), 396.1771587966419, (new Vector3d(99.28682492869655, 145.1452125345737, -667.6887579470026), 113.52353092257498));
            Ssi.TransversalDbg = null;
        }
        var t = new Ssi.Torus(new Vector3d(0, 0, -684.7215542594209), new Vector3d(0, 0, -1), 186.87300220067044, 1.5123542594208175);
        var c = new Ssi.Cylinder(new Vector3d(198.57364985739304, 25.4, -667.6887579470026), new Vector3d(0, -50.8, 0), 50.23764985739308);
        var q = new Vector3d(150.7935562861831, 110.37582314101903, -683.2092);
        Console.WriteLine($"q on torus F {Ssi.Implicit(t, q).F:G4}, on cyl F {Ssi.Implicit(c, q).F:G4}");
        foreach (var (bc, br) in new[] { (q, 5.0), (q, 30.0), (new Vector3d(150, 60, -680), 80.0), (Vector3d.Zero + new Vector3d(0, 0, -684), 260.0) })
            foreach (var (a, b, n) in new[] { ((Ssi.Shape)t, (Ssi.Shape)c, "t×c"), (c, t, "c×t") })
            {
                var r = Ssi.Intersect(a, b, bc, br);
                Console.WriteLine($"ball {br}: {n} kind {r.Kind} curves {r.Curves.Count} uncert {r.Uncertified}");
            }
    }
}
