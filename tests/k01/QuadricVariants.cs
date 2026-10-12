using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>Тело вращения профиля в плоскости XZ вокруг оси Z (отрезки и дуги).</summary>
    static TSolid RevolveProfile(List<ICurve3> prof)
    {
        var p = ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), prof);
        var t = RevolveOp.Build(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
        TopologyConventions.Normalize(t);
        return t;
    }
    static List<ICurve3> Poly(params (double X, double Z)[] pts)
    {
        var l = new List<ICurve3>();
        for (int i = 0; i < pts.Length; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Length]; l.Add(new LineCurve3(new Vector3d(a.X, 0, a.Z), new Vector3d(b.X, 0, b.Z))); }
        return l;
    }

    /// <summary>Замкнутые рёбра между квадриками: прямое построение (или отказ), изменение объёма, время.</summary>
    static void QuadricVariants()
    {
        string? why = null;
        DirectRound.Debug = m => { if ((m.StartsWith("face ") || m.StartsWith("   flipped")) && Environment.GetEnvironmentVariable("P3D_QVDBG") == "1") Console.WriteLine("   " + m); if (m.StartsWith("declined") || m.StartsWith("cyl/cyl: sigma")) why = (why == null ? "" : why + " | ") + m; };
        void Run(string name, Func<TSolid> make, Func<TEdge, bool> pick, double r)
        {
            var b = new List<TSolid> { make() };
            if (Environment.GetEnvironmentVariable("P3D_QVDUMP") == name.Split(' ')[0]) TopoDump(b[0]);
            double v0 = AnalyticVolume.Of(b[0]) ?? double.NaN;
            var edges = b[0].Edges.Where(pick).ToList();
            why = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string res;
            try
            {
                FilletOp.RoundEdges(b, edges.Select(EdgeRef.Of).ToList(), r, new List<string>());
                double v1 = AnalyticVolume.Of(b[0]) ?? double.NaN;
                res = $"dV {v1 - v0:R} guarantee {b[0].Guarantee} valid {SolidValidator.Validate(b[0]).IsValid} faceted {GeometryAudit.Faceted(b[0])}";
            }
            catch (Exception e) { res = "EXCEPTION " + e.Message[..Math.Min(120, e.Message.Length)]; }
            Console.WriteLine($"QV {name}: edges {edges.Count} ({string.Join(",", edges.Select(x => x.Curve.GetType().Name))}) r {r}: {res}; {sw.ElapsedMilliseconds} ms; {why}");
        }
        bool At(TEdge e, double rr, double z) { var p = e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2); return Math.Abs(new Vector2d(p.X, p.Y).Length - rr) < 1e-6 && Math.Abs(p.Z - z) < 1e-6; }
        // усечённый конус: верхняя кромка плоскость/конус (выпуклая)
        Run("frustum top rim (plane/cone)", () => RevolveProfile(Poly((0, 0), (20, 0), (12, 20), (0, 20))), e => At(e, 12, 20), 1);
        // цилиндр, затем конус: стык цилиндр/конус (выпуклый) и конус/плоскость
        Run("cyl/cone convex", () => RevolveProfile(Poly((0, 0), (20, 0), (20, 10), (12, 25), (0, 25))), e => At(e, 20, 10), 2);
        // конус сужается к цилиндру меньшего радиуса: стык конус/цилиндр вогнутый
        Run("cone/cyl concave", () => RevolveProfile(Poly((0, 0), (20, 0), (20, 10), (10, 15), (10, 30), (0, 30))), e => At(e, 10, 15), 2);
        // цилиндр и шар (центр на оси ниже стыка): стык цилиндр/шар выпуклый
        Run("cyl/sphere convex", () =>
        {
            double zc = 20 - Math.Sqrt(225 - 100);
            var prof = new List<ICurve3> {
                new LineCurve3(new Vector3d(0, 0, 0), new Vector3d(10, 0, 0)), new LineCurve3(new Vector3d(10, 0, 0), new Vector3d(10, 0, 20)),
                new ArcCurve3(new Frame3(new Vector3d(0, 0, zc), Vector3d.UnitX, Vector3d.UnitZ), 15, Math.Atan2(20 - zc, 10), Math.PI / 2),
                new LineCurve3(new Vector3d(0, 0, zc + 15), new Vector3d(0, 0, 0)) };
            return RevolveProfile(prof);
        }, e => At(e, 10, 20), 1.5);
        // отверстие не по оси шара: кромка шар/цилиндр (кривая пересечения)
        Run("sphere with off-axis hole rim", () =>
        {
            var b = new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 20) };
            BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(6, 0, -30), 4, 60), new List<string>());
            return b[0];
        }, e => e.Curve is not (LineCurve3 or ArcCurve3) && e.Curve.PointAt(0.5).Z > 5, 1);
        // бобышка-цилиндр на шаре: стык вогнутый
        Run("cylinder boss on sphere", () =>
        {
            var b = new List<TSolid> { PrimitiveOp.Sphere(Vector3d.Zero, 20) };
            BooleanOp.Unite(b, PrimitiveOp.Cylinder(new Vector3d(5, 0, 0), 4, 30), new List<string>());
            return b[0];
        }, e => e.Curve is not (LineCurve3 or ArcCurve3) && e.Curve.PointAt(0.5).Z > 5, 1);
        DirectRound.Debug = null;
    }
}
