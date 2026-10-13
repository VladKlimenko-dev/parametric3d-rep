using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Sketching;

static partial class Program
{
    static Sketch PolySketch(Part p, Frame3 f, params (double X, double Y)[] pts)
    {
        var s = new Sketch($"S{p.Sketches.Count() + 1}", f); p.Add(s);
        var sp = pts.Select(q => new SkPoint(q.X, q.Y)).ToList();
        for (int i = 0; i < sp.Count; i++) s.AddEntity(new SkLine(sp[i], sp[(i + 1) % sp.Count]));
        return s;
    }

    /// <summary>
    /// K09. МАТРИЦА ОПЕРАЦИЙ: каждая операция на простом входе с заранее известным ответом.
    /// Ожидание выводится формулой, а не прогоном кода; проверяются отсутствие ошибки, годность
    /// тела и объём (точный путь — 1e-6, иначе сетка — 2e-3 относительно).
    /// </summary>
    static void K09Matrix()
    {
        var cases = K09Cases();
        K09MatrixRun(cases);
    }

    /// <summary>K09: каталог операций с известным ответом (используется и проверкой ворот g.10).</summary>
    internal static List<(string Name, Func<Part> Build, double Expected)> K09Cases()
    {
        var cases = new List<(string Name, Func<Part> Build, double Expected)>();
        Frame3 XY(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        Frame3 XZ = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ);

        cases.Add(("shell cube 60, t=2, open top", () =>
        {
            var p = new Part("shell"); TestGeometry.AddCube(p, 60);
            var sh = new ShellFeature { Thickness = new ValueRef(2) }; sh.OpenFaces.Add(new Vector3d(0, 0, 30)); p.AddFeature(sh); return p;
        }, 216000 - 56.0 * 56 * 58));
        cases.Add(("revolve ring 360°", () =>
        {
            var p = new Part("rev"); var s = PolySketch(p, XZ, (10, 0), (20, 0), (20, 30), (10, 30));
            var axis = new SkLine(new SkPoint(0, 0), new SkPoint(0, 30)) { Role = SkRole.Centerline }; s.AddEntity(axis);
            p.AddFeature(new RevolveFeature { SketchId = s.Id, AxisEntityId = axis.Id }); return p;
        }, Math.PI * (400 - 100) * 30));
        cases.Add(("revolve ring 90°", () =>
        {
            var p = new Part("rev90"); var s = PolySketch(p, XZ, (10, 0), (20, 0), (20, 30), (10, 30));
            var axis = new SkLine(new SkPoint(0, 0), new SkPoint(0, 30)) { Role = SkRole.Centerline }; s.AddEntity(axis);
            p.AddFeature(new RevolveFeature { SketchId = s.Id, AxisEntityId = axis.Id, EndAngle = new ValueRef(90) }); return p;
        }, Math.PI * (400 - 100) * 30 / 4));
        cases.Add(("sweep circle r5 along 40", () =>
        {
            var p = new Part("sweep"); var sec = CircleSketch(p, 0, 0, 0, 5);
            var path = new Sketch("path", XZ); p.Add(path); path.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, 40)));
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return p;
        }, Math.PI * 25 * 40));
        cases.Add(("loft square 20 → 10 over 30", () =>
        {
            var p = new Part("loft"); var a = PolySketch(p, XY(0), (-10, -10), (10, -10), (10, 10), (-10, 10)); var b = PolySketch(p, XY(30), (-5, -5), (5, -5), (5, 5), (-5, 5));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return p;
        }, 30.0 / 3 * (400 + 100 + 200)));
        cases.Add(("draft 5° on the sides of a cube 60 (neutral bottom)", () =>
        {
            var p = new Part("draft"); TestGeometry.AddCube(p, 60);
            var df = new DraftFeature { NeutralPoint = new Vector3d(0, 0, -30), NeutralNormal = Vector3d.UnitZ, Angle = new ValueRef(5) };
            foreach (var q in new[] { new Vector3d(30, 0, 0), new Vector3d(-30, 0, 0), new Vector3d(0, 30, 0), new Vector3d(0, -30, 0) }) df.OnFaces.Add(q);
            p.AddFeature(df); return p;
        }, Frustum(60, 60 - 2 * 60 * Math.Tan(5 * Math.PI / 180), 60)));
        cases.Add(("mirror box across x=0", () =>
        {
            var p = new Part("mirror"); var s = PolySketch(p, XY(0), (10, 0), (30, 0), (30, 20), (10, 20));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
            p.AddFeature(new MirrorFeature { At = Vector3d.Zero, Normal = Vector3d.UnitX }); return p;
        }, 2 * 20 * 20 * 10));
        cases.Add(("linear pattern 3 × 20", () =>
        {
            var p = new Part("lin"); var s = PolySketch(p, XY(0), (0, 0), (10, 0), (10, 10), (0, 10));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
            p.AddFeature(new LinearPatternFeature { Step = new Vector3d(20, 0, 0), Count = new ValueRef(3) }); return p;
        }, 3 * 1000));
        cases.Add(("circular pattern 4", () =>
        {
            var p = new Part("circ"); var s = PolySketch(p, XY(0), (20, -5), (30, -5), (30, 5), (20, 5));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(10); p.AddFeature(ex);
            p.AddFeature(new CircularPatternFeature { At = Vector3d.Zero, Axis = Vector3d.UnitZ, Count = new ValueRef(4) }); return p;
        }, 4 * 1000));

        cases.Add(("shell cylinder r10 h30, t=2, open top", () =>
        {
            var p = new Part("shellcyl"); var c = CircleSketch(p, 0, 0, 0, 10);
            var ex = new ExtrudeFeature { SketchId = c.Id }; ex.Side1.Distance = new ValueRef(30); p.AddFeature(ex);
            var sh = new ShellFeature { Thickness = new ValueRef(2) }; sh.OpenFaces.Add(new Vector3d(0, 0, 30)); p.AddFeature(sh); return p;
        }, Math.PI * 100 * 30 - Math.PI * 64 * 28));
        cases.Add(("shell too thick (cube 10, t=6) must be refused", () =>
        {
            var p = new Part("shellbad"); TestGeometry.AddCube(p, 10);
            var sh = new ShellFeature { Thickness = new ValueRef(6) }; sh.OpenFaces.Add(new Vector3d(0, 0, 5)); p.AddFeature(sh); return p;
        }, double.NaN));
        cases.Add(("sweep circle r2 along a quarter arc R20", () =>
        {
            var p = new Part("sweeparc");
            var sec = new Sketch("sec", new Frame3(new Vector3d(20, 0, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(sec);
            sec.AddEntity(new SkCircle(new SkPoint(0, 0), 2));
            var path = new Sketch("path", XY(0)); p.Add(path);
            path.AddEntity(new SkArc(new SkPoint(0, 0), new SkPoint(20, 0), new SkPoint(0, 20)) { CCW = true });
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return p;
        }, Math.PI * 4 * (Math.PI / 2 * 20)));
        cases.Add(("revolve circle 90° (torus segment)", () =>
        {
            var p = new Part("revc"); var s = new Sketch("s", XZ); p.Add(s);
            s.AddEntity(new SkCircle(new SkPoint(20, 0), 2));
            var axis = new SkLine(new SkPoint(0, -5), new SkPoint(0, 5)) { Role = SkRole.Centerline }; s.AddEntity(axis);
            p.AddFeature(new RevolveFeature { SketchId = s.Id, AxisEntityId = axis.Id, EndAngle = new ValueRef(90) }); return p;
        }, Math.PI * 4 * 2 * Math.PI * 20 / 4));
        cases.Add(("revolve cut groove in a cylinder", () =>
        {
            var p = new Part("groove"); var c = CircleSketch(p, 0, 0, 0, 10);
            var ex = new ExtrudeFeature { SketchId = c.Id }; ex.Side1.Distance = new ValueRef(30); p.AddFeature(ex);
            var s = PolySketch(p, XZ, (8, 10), (12, 10), (12, 20), (8, 20));
            var axis = new SkLine(new SkPoint(0, 0), new SkPoint(0, 30)) { Role = SkRole.Centerline }; s.AddEntity(axis);
            p.AddFeature(new RevolveFeature { SketchId = s.Id, AxisEntityId = axis.Id, Cut = true }); return p;
        }, Math.PI * 100 * 30 - Math.PI * (100 - 64) * 10));
        cases.Add(("extrude symmetric two-sided", () =>
        {
            var p = new Part("sym"); var s = PolySketch(p, XY(0), (0, 0), (10, 0), (10, 10), (0, 10));
            var ex = new ExtrudeFeature { SketchId = s.Id, Symmetric = true }; ex.Side1.Distance = new ValueRef(30); p.AddFeature(ex); return p;
        }, 3000));

        cases.Add(("loft circle r10 → r5 over 30 (cone frustum)", () =>
        {
            var p = new Part("loftc"); var a = CircleSketch(p, 0, 0, 0, 10); var b = CircleSketch(p, 30, 0, 0, 5);
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return p;
        }, Math.PI * 30 / 3 * (100 + 50 + 25)));
        cases.Add(("rib in an L-bracket (triangle 30×30, t=3)", () =>
        {
            var p = new Part("rib");
            var s1 = PolySketch(p, XY(0), (0, 0), (40, 0), (40, 20), (0, 20));
            var e1 = new ExtrudeFeature { SketchId = s1.Id }; e1.Side1.Distance = new ValueRef(5); p.AddFeature(e1);
            var s2 = PolySketch(p, XY(5), (0, 0), (5, 0), (5, 20), (0, 20));
            var e2 = new ExtrudeFeature { SketchId = s2.Id }; e2.Side1.Distance = new ValueRef(35); p.AddFeature(e2);
            var rs = new Sketch("rib", new Frame3(new Vector3d(0, 10, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(rs);
            rs.AddEntity(new SkLine(new SkPoint(5, 35), new SkPoint(35, 5)));
            p.AddFeature(new RibFeature { SketchId = rs.Id, Thickness = new ValueRef(3), Grow = new Vector3d(-1, 0, -1).Normalized() }); return p;
        }, 4000 + 3500 + 450 * 3));

        cases.Add(("sweep circle r2 along a line 20 + tangent arc R20 (90°)", () =>
        {
            var p = new Part("sweepchain"); var sec = CircleSketch(p, 0, 0, 0, 2);
            var path = new Sketch("path", XZ); p.Add(path);
            // Entities deliberately out of order and the line reversed: the chain is found by connectivity.
            path.AddEntity(new SkArc(new SkPoint(20, 20), new SkPoint(20, 40), new SkPoint(0, 20), true));
            path.AddEntity(new SkLine(new SkPoint(0, 20), new SkPoint(0, 0)));
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return p;
        }, Math.PI * 4 * (20 + Math.PI / 2 * 20)));

        cases.Add(("sweep circle r3 along an L-shaped polyline 40 + 30 (mitred corner)", () =>
        {
            var p = new Part("sweepL"); var sec = CircleSketch(p, 0, 0, 0, 3);
            var path = new Sketch("path", XZ); p.Add(path);
            path.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, 40)));
            path.AddEntity(new SkLine(new SkPoint(0, 40), new SkPoint(30, 40)));
            p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); return p;
        }, Math.PI * 9 * 70));
        return cases;
    }

    static void K09MatrixRun(List<(string Name, Func<Part> Build, double Expected)> cases)
    {
        var report = new List<string>();
        foreach (var (name, build, expected) in cases)
        {
            string verdict; double vol = double.NaN; bool analytic = false; string? err = null;
            try
            {
                var p = build(); var r = p.Compute(null); p.Apply(r);
                err = string.Join("; ", p.Features.Where(f => f.Error != null).Select(f => $"{f.Name}: {f.Error}"));
                vol = p.Volume; analytic = !r.MassFromMesh;
                verdict = string.Join(" | ", p.Bodies.Select(b => SolidValidator.Validate(b).Summary()));
            }
            catch (Exception ex) { verdict = "exception: " + ex.Message; }
            double rel = Math.Abs(vol - expected) / expected;
            bool ok = double.IsNaN(expected)
                ? !string.IsNullOrEmpty(err)                         // ожидается честный отказ
                : string.IsNullOrEmpty(err) && !verdict.Contains("error") && !verdict.StartsWith("exception") && rel < (analytic ? 1e-6 : 2e-3);
            if (double.IsNaN(rel)) rel = -1;
            if (double.IsNaN(vol)) vol = -1;
            Log(new { k09 = name, ok, expected = double.IsNaN(expected) ? -1 : expected, vol, rel, analytic, err, verdict });
            report.Add($"{(ok ? "OK  " : "FAIL")} {name}");
        }
        Console.WriteLine(string.Join("\n", report));
        Check("K09-OPERATION-MATRIX", () => Require(report.All(r => r.StartsWith("OK")), string.Join("; ", report.Where(r => !r.StartsWith("OK")))));
    }

    static double Frustum(double a, double b, double h) => h / 3 * (a * a + b * b + a * b);
}
static partial class Program
{
    static void SweepProbe()
    {
        var p = new Part("sweep"); var sec = CircleSketch(p, 0, 0, 0, 5);
        var path = new Sketch("path", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ)); p.Add(path); path.AddEntity(new SkLine(new SkPoint(0, 0), new SkPoint(0, 40)));
        p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id }); p.Rebuild(null);
        var b = p.Bodies[0];
        {
            var p2 = new Part("cylx"); var s2 = CircleSketch(p2, 0, 0, 0, 5);
            var ex2 = new ExtrudeFeature { SketchId = s2.Id }; ex2.Side1.Distance = new ValueRef(40); p2.AddFeature(ex2); p2.Rebuild(null);
            Log(new { extrudeCylinder = SolidValidator.Validate(p2.Bodies[0]).Summary(), conv = p2.Bodies[0].TopologyConvention });
            var raw = Parametric3D.Kernel.Ops.PrimitiveOp.Cylinder(Vector3d.Zero, 5, 40);
            Log(new { primitiveRaw = SolidValidator.Validate(raw).Summary() });
            var before = raw.Faces.Select(f => (f.Id, f.Reversed, string.Join(",", f.Loops.Select(l => string.Join("", l.CoEdges.Select(c => (c.Edge.Curve is Parametric3D.Kernel.Curves.ArcCurve3 ? "a" : "l") + (c.Reversed ? "-" : "+"))))))).ToList();
            var rep = TopologyConventions.Normalize(raw);
            var after = raw.Faces.Select(f => (f.Id, f.Reversed, string.Join(",", f.Loops.Select(l => string.Join("", l.CoEdges.Select(c => (c.Edge.Curve is Parametric3D.Kernel.Curves.ArcCurve3 ? "a" : "l") + (c.Reversed ? "-" : "+"))))))).ToList();
            Log(new { cylBefore = before.Select(x => x.ToString()), cylAfter = after.Select(x => x.ToString()), rep = rep.ToString() });
            var m = Parametric3D.Kernel.Tess.Tessellator.Tessellate(raw);
            foreach (var f in raw.Faces)
            {
                var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb); var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
                Log(new { faceMesh = f.Id, surf = f.Surface.GetType().Name, verts = md.Vertices.Length, tris = md.Indices.Length / 3, area = Parametric3D.Kernel.Tess.MassProps.Area(md) });
            }
            Log(new { primitiveNormalized = SolidValidator.Validate(raw).Summary(), loops = raw.Faces.Select(f => f.Loops.Count) });
        }
        var v = SolidValidator.Validate(b);
        Log(new { sweep = b.Faces.Select(f => $"{f.Surface.GetType().Name}/{GeometryAudit.AuthoritativeSurface(f)?.GetType().Name} loops {f.Loops.Count}"),
                  edges = b.Edges.Select(e => e.Curve.GetType().Name).GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}"),
                  issues = v.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(4).Select(i => i.ToString()) });
    }
}
static partial class Program
{
    static void ShellCrash()
    {
        var p = new Part("shellbad"); TestGeometry.AddCube(p, 10);
        var sh = new ShellFeature { Thickness = new ValueRef(6) }; sh.OpenFaces.Add(new Vector3d(0, 0, 5)); p.AddFeature(sh);
        Console.Error.WriteLine("computing");
        var r = p.Compute(null); p.Apply(r);
        Console.Error.WriteLine("done: " + sh.Error);
    }
}
static partial class Program
{
    static void SweepArcCrash()
    {
        var p = new Part("sweeparc");
        var sec = new Sketch("sec", new Frame3(new Vector3d(20, 0, 0), Vector3d.UnitX, Vector3d.UnitZ)); p.Add(sec);
        sec.AddEntity(new SkCircle(new SkPoint(0, 0), 2));
        var path = new Sketch("path", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY)); p.Add(path);
        path.AddEntity(new SkArc(new SkPoint(0, 0), new SkPoint(20, 0), new SkPoint(0, 20)) { CCW = true });
        p.AddFeature(new SweepFeature { SectionSketchId = sec.Id, PathSketchId = path.Id });
        Console.Error.WriteLine("computing");
        var r = p.Compute(null); p.Apply(r);
        Console.Error.WriteLine("done: " + p.Features[0].Error + " vol " + p.Volume);
        Console.Error.WriteLine("validate: " + SolidValidator.Validate(p.Bodies[0]).Summary());
        foreach (var i in SolidValidator.ValidateBRep(p.Bodies[0]).Issues.Take(3)) Console.Error.WriteLine("  " + i);
        foreach (var f in p.Bodies[0].Faces) Console.Error.WriteLine($"  face {f.Surface.GetType().Name} base={(f.Surface is Parametric3D.Kernel.Surfaces.RevolvedSurface rs ? rs.Base.GetType().Name + " sweep " + (rs.A1 - rs.A0) : "")} loops={f.Loops.Count}");
        var secProfile = sec.ToProfile();
        foreach (var c in secProfile.Loops.SelectMany(l => l.Curves)) Console.Error.WriteLine($"  profile curve {c.GetType().Name} W·planeW={(c is Parametric3D.Kernel.Curves.ArcCurve3 a ? Vector3d.Dot(a.Frame.W.Normalized(), secProfile.Plane.W.Normalized()) : 0)}");
    }
}
static partial class Program
{
    static void RevolveCircleProbe()
    {
        foreach (double deg in new[] { 90.0, 180.0, 360.0 })
        {
            var p = new Part("revc");
            var s = new Sketch("s", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ)); p.Add(s);
            s.AddEntity(new SkCircle(new SkPoint(20, 0), 2));
            var axis = new SkLine(new SkPoint(0, -5), new SkPoint(0, 5)) { Role = SkRole.Centerline }; s.AddEntity(axis);
            p.AddFeature(new RevolveFeature { SketchId = s.Id, AxisEntityId = axis.Id, EndAngle = new ValueRef(deg) });
            var r = p.Compute(null); p.Apply(r);
            double exact = Math.PI * 4 * 2 * Math.PI * 20 * deg / 360;
            var b = p.Bodies.FirstOrDefault();
            if (b != null && deg == 90) foreach (var i in SolidValidator.ValidateMesh(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b)).Issues.Take(4)) Console.WriteLine("ISSUE " + i);
            if (b != null && deg == 90) { var raw = Parametric3D.Kernel.Ops.RevolveOp.Build(Parametric3D.Kernel.Ops.ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), new List<Parametric3D.Kernel.Curves.ICurve3> { new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(20, 0, 0), Vector3d.UnitX, Vector3d.UnitZ), 2, 0, 2 * Math.PI) }), new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 0, Math.PI / 2);
                Console.WriteLine("RAW " + SolidValidator.Validate(raw).Summary()); TopologyConventions.Normalize(raw); Console.WriteLine("RAWNORM " + SolidValidator.Validate(raw).Summary());
                foreach (var f in b.Faces)
                {
                    var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb); var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
                    var near = md.Vertices.Select(v => v.P).Where(q => Math.Abs(q.X) < 1e-3 && Math.Abs(q.Y - 18) < 0.05).Select(q => $"({q.X:R},{q.Y:R},{q.Z:R})").Distinct().Take(6);
                    Console.WriteLine($"FACE {f.Id} {f.Surface.GetType().Name} rev={f.Reversed} loops={string.Join(",", f.Loops.Select(l => string.Join("", l.CoEdges.Select(c => c.Reversed ? "-" : "+"))))} near18: {string.Join(" ", near)}");
                }
            }
            Log(new { revolveCircle = deg, err = p.Features[0].Error, vol = p.Volume, exact, analytic = !r.MassFromMesh,
                      verdict = b == null ? "none" : SolidValidator.Validate(b).Summary(), faces = b?.Faces.Select(f => f.Surface.GetType().Name + ":" + f.Loops.Count) });
        }
    }
}
static partial class Program
{
    /// <summary>
    /// K09. РЕШАТЕЛЬ ЭСКИЗОВ: ранг, степени свободы, избыточность, противоречие, масштаб, ветвь.
    /// Прямоугольник из четырёх отрезков с общими углами; ожидания задаются числом степеней
    /// свободы, выведенным вручную.
    /// </summary>
    static (Sketch S, SkPoint A, SkPoint B, SkPoint C, SkPoint D, SkLine Ab, SkLine Bc, SkLine Cd, SkLine Da) Rect(double k)
    {
        var s = new Sketch("r", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        var a = new SkPoint(0, 0); var b = new SkPoint(10.3 * k, 0.4 * k); var c = new SkPoint(9.8 * k, 6.1 * k); var d = new SkPoint(-0.2 * k, 5.7 * k);
        var ab = new SkLine(a, b); var bc = new SkLine(b, c); var cd = new SkLine(c, d); var da = new SkLine(d, a);
        foreach (var e in new SkEntity[] { a, b, c, d, ab, bc, cd, da }) s.AddEntity(e);
        return (s, a, b, c, d, ab, bc, cd, da);
    }

    static void K09Solver()
    {
        var solver = new NumericSketchSolver();
        var rows = new List<string>();
        void Case(string name, double scale, Action<Sketch, SkPoint, SkPoint, SkPoint, SkPoint, SkLine, SkLine, SkLine, SkLine> setup,
                  SolveStatus want, int wantDof, Func<SkPoint, SkPoint, SkPoint, SkPoint, bool>? geometry = null)
        {
            var (s, a, b, c, d, ab, bc, cd, da) = Rect(scale);
            setup(s, a, b, c, d, ab, bc, cd, da);
            var st = solver.Solve(s, null, out int dof);
            bool geo = geometry == null || geometry(a, b, c, d);
            bool ok = st == want && dof == wantDof && geo;
            Log(new { solver = name, scale, status = st.ToString(), dof, want = want.ToString(), wantDof, geo, ok,
                      a = $"({a.X:0.####},{a.Y:0.####})", c = $"({c.X:0.####},{c.Y:0.####})" });
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name} ×{scale}: {st}/{dof} (want {want}/{wantDof}){(geo ? "" : " geometry wrong")}");
        }
        foreach (double k in new[] { 1.0, 1000.0, 0.001 })
        {
            // Полностью определён: угол закреплён, стороны горизонтальны/вертикальны, два размера.
            Case("fully constrained", k, (s, a, b, c, d, ab, bc, cd, da) =>
            {
                s.AddConstraint(ConstraintType.Fix, a);
                s.AddConstraint(ConstraintType.Horizontal, ab); s.AddConstraint(ConstraintType.Horizontal, cd);
                s.AddConstraint(ConstraintType.Vertical, bc); s.AddConstraint(ConstraintType.Vertical, da);
                s.AddDimension(DimensionType.DistancePointPoint, 10 * k, a, b).Locked = true;
                s.AddDimension(DimensionType.DistancePointPoint, 6 * k, b, c).Locked = true;
            }, SolveStatus.Ok, 0, (a, b, c, d) => Math.Abs(c.X - 10 * k) < 1e-6 * k && Math.Abs(c.Y - 6 * k) < 1e-6 * k);
            // Не хватает высоты: одна степень свободы.
            Case("one dimension missing", k, (s, a, b, c, d, ab, bc, cd, da) =>
            {
                s.AddConstraint(ConstraintType.Fix, a);
                s.AddConstraint(ConstraintType.Horizontal, ab); s.AddConstraint(ConstraintType.Horizontal, cd);
                s.AddConstraint(ConstraintType.Vertical, bc); s.AddConstraint(ConstraintType.Vertical, da);
                s.AddDimension(DimensionType.DistancePointPoint, 10 * k, a, b).Locked = true;
            }, SolveStatus.Underconstrained, 1);
            // Противоречие: ширина 10, но диагональ 5.
            Case("conflicting dimensions", k, (s, a, b, c, d, ab, bc, cd, da) =>
            {
                s.AddConstraint(ConstraintType.Fix, a);
                s.AddConstraint(ConstraintType.Horizontal, ab); s.AddConstraint(ConstraintType.Horizontal, cd);
                s.AddConstraint(ConstraintType.Vertical, bc); s.AddConstraint(ConstraintType.Vertical, da);
                s.AddDimension(DimensionType.DistancePointPoint, 10 * k, a, b).Locked = true;
                s.AddDimension(DimensionType.DistancePointPoint, 6 * k, b, c).Locked = true;
                s.AddDimension(DimensionType.DistancePointPoint, 5 * k, a, c).Locked = true;
            }, SolveStatus.Inconsistent, 0);
            // Избыточное, но согласное условие: параллельность сторон, уже следующая из горизонталей.
            Case("redundant but consistent", k, (s, a, b, c, d, ab, bc, cd, da) =>
            {
                s.AddConstraint(ConstraintType.Fix, a);
                s.AddConstraint(ConstraintType.Horizontal, ab); s.AddConstraint(ConstraintType.Horizontal, cd);
                s.AddConstraint(ConstraintType.Vertical, bc); s.AddConstraint(ConstraintType.Vertical, da);
                s.AddConstraint(ConstraintType.Parallel, ab, cd);
                s.AddDimension(DimensionType.DistancePointPoint, 10 * k, a, b).Locked = true;
                s.AddDimension(DimensionType.DistancePointPoint, 6 * k, b, c).Locked = true;
            }, SolveStatus.Overconstrained, 0, (a, b, c, d) => Math.Abs(c.X - 10 * k) < 1e-6 * k);
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("K09-SKETCH-SOLVER", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
static partial class Program
{
    static void K09Solver2()
    {
        var solver = new NumericSketchSolver();
        var rows = new List<string>();
        // 1. ВЕТВЬ: треугольник с закреплённым основанием и двумя размерами сторон; меняем размер —
        //    вершина обязана остаться по ту же сторону основания.
        {
            var s = new Sketch("tri", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
            var a = new SkPoint(0, 0); var b = new SkPoint(10, 0); var c = new SkPoint(4, 6);
            foreach (var e in new SkEntity[] { a, b, c, new SkLine(a, b), new SkLine(b, c), new SkLine(c, a) }) s.AddEntity(e);
            s.AddConstraint(ConstraintType.Fix, a); s.AddConstraint(ConstraintType.Fix, b);
            var dac = s.AddDimension(DimensionType.DistancePointPoint, 7, a, c); dac.Locked = true;
            var dbc = s.AddDimension(DimensionType.DistancePointPoint, 8, b, c); dbc.Locked = true;
            var flips = new List<string>();
            foreach (var (x, y) in new[] { (7.0, 8.0), (9, 6), (5.5, 5.5), (12, 3.5), (6, 9), (5.01, 5.01) })
            {
                dac.Value = x; dbc.Value = y;
                var st = solver.Solve(s, null, out int dof);
                if (c.Y <= 0) flips.Add($"{x}/{y}: c=({c.X:0.###},{c.Y:0.###}) {st}");
                if (st != SolveStatus.Ok) flips.Add($"{x}/{y}: status {st}");
            }
            Log(new { solver2 = "branch", flips });
            rows.Add($"{(flips.Count == 0 ? "OK  " : "FAIL")} triangle keeps its branch: {string.Join("; ", flips)}");
        }
        // 2. КАСАНИЕ: отрезок касается окружности радиуса 5; расстояние от центра до прямой = 5.
        {
            var s = new Sketch("tan", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
            var ctr = new SkPoint(0, 0); var circ = new SkCircle(ctr, 4.3);
            var p = new SkPoint(-10, 6.2); var q = new SkPoint(10, 5.1); var ln = new SkLine(p, q);
            foreach (var e in new SkEntity[] { ctr, circ, p, q, ln }) s.AddEntity(e);
            s.AddConstraint(ConstraintType.Fix, ctr); s.AddConstraint(ConstraintType.Tangent, ln, circ);
            s.AddConstraint(ConstraintType.Horizontal, ln);
            var r = s.AddDimension(DimensionType.Radius, 5, circ); r.Locked = true;
            var st = solver.Solve(s, null, out int dof);
            double dist = Math.Abs(p.Y);   // horizontal line y = const, centre at origin
            bool ok = st is SolveStatus.Ok or SolveStatus.Underconstrained && Math.Abs(dist - 5) < 1e-7 && Math.Abs(p.Y - q.Y) < 1e-9 && Math.Abs(circ.Radius - 5) < 1e-9;
            Log(new { solver2 = "tangent", status = st.ToString(), dof, dist, radius = circ.Radius });
            rows.Add($"{(ok ? "OK  " : "FAIL")} line tangent to a circle: distance {dist:0.#########}, status {st}, dof {dof}");
        }
        // 3. БОЛЬШОЙ ЭСКИЗ: N полностью определённых прямоугольников; время и статус.
        foreach (int n in new[] { 5, 20, 50 })
        {
            var s = new Sketch("big", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
            for (int i = 0; i < n; i++)
            {
                double ox = (i % 10) * 20, oy = (i / 10) * 20;
                var a = new SkPoint(ox + 0.1, oy - 0.2); var b = new SkPoint(ox + 10.3, oy + 0.4); var c = new SkPoint(ox + 9.8, oy + 6.1); var d = new SkPoint(ox - 0.2, oy + 5.7);
                var ab = new SkLine(a, b); var bc = new SkLine(b, c); var cd = new SkLine(c, d); var da = new SkLine(d, a);
                foreach (var e in new SkEntity[] { a, b, c, d, ab, bc, cd, da }) s.AddEntity(e);
                var fx = s.AddDimension(DimensionType.CoordX, ox, a); fx.Locked = true;
                var fy = s.AddDimension(DimensionType.CoordY, oy, a); fy.Locked = true;
                s.AddConstraint(ConstraintType.Horizontal, ab); s.AddConstraint(ConstraintType.Horizontal, cd);
                s.AddConstraint(ConstraintType.Vertical, bc); s.AddConstraint(ConstraintType.Vertical, da);
                s.AddDimension(DimensionType.DistancePointPoint, 10, a, b).Locked = true;
                s.AddDimension(DimensionType.DistancePointPoint, 6, b, c).Locked = true;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var st = solver.Solve(s, null, out int dof);
            double ms = sw.Elapsed.TotalMilliseconds;
            bool ok = st == SolveStatus.Ok && dof == 0 && ms < 2000;
            Log(new { solver2 = "big", rectangles = n, variables = 8 * n, status = st.ToString(), dof, ms });
            rows.Add($"{(ok ? "OK  " : "FAIL")} {n} rectangles ({8 * n} variables): {st}/{dof} in {ms:0} ms");
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("K09-SKETCH-SOLVER-2", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
static partial class Program
{
    static void GridSolve() => GridSolveSizes(new[] { 4, 8, 12 });

    /// <summary>Сетка N×N клеток: горизонтальные/вертикальные связи, размеры по нижнему и левому краю, угол закреплён;
    /// dropOne — без одного размера (одна степень свободы).</summary>
    static (Sketch S, SkPoint Corner) GridSketch(int N, bool dropOne = false)
    {
        var s = new Sketch("grid", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        var P = new SkPoint[N + 1, N + 1];
        var rnd = new Random(7);
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) { P[i, j] = new SkPoint(i * 10 + rnd.NextDouble(), j * 10 + rnd.NextDouble()); s.AddEntity(P[i, j]); }
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
            {
                if (i < N) { var l = new SkLine(P[i, j], P[i + 1, j]); s.AddEntity(l); s.AddConstraint(ConstraintType.Horizontal, l); if (j == 0 && !(dropOne && i == N - 1)) s.AddDimension(DimensionType.DistancePointPoint, 10, P[i, j], P[i + 1, j]).Locked = true; }
                if (j < N) { var l = new SkLine(P[i, j], P[i, j + 1]); s.AddEntity(l); s.AddConstraint(ConstraintType.Vertical, l); if (i == 0) s.AddDimension(DimensionType.DistancePointPoint, 10, P[i, j], P[i, j + 1]).Locked = true; }
            }
        s.AddDimension(DimensionType.CoordX, 0, P[0, 0]).Locked = true; s.AddDimension(DimensionType.CoordY, 0, P[0, 0]).Locked = true;
        return (s, P[N, N]);
    }

    static void SketchSparseChecks()
    {
        Check("KRN10-SKETCH-SPARSE", () =>
        {
            // 10.8: связный большой эскиз — разреженная алгебра (J^T J в профиле после RCM, ранг по разреженным строкам,
            // доводка разреженной J·Jᵀ). Сетка 31×31 (1922 переменные): решение верно (угол (300, 300)), Ok/0, время ≤ 4 с
            // (плотно — 4.7…8 с на том же стенде); сетка 13×13 с одним снятым размером — 1 степень свободы, как у плотного;
            // сетка 21×21 — то же решение, что у плотного (до 1e-9).
            var fails = new List<string>(); var solver = new NumericSketchSolver();
            long s0 = NumericSketchSolver.SparseSteps;
            var (g30, c30) = GridSketch(30);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var st = solver.Solve(g30, null, out int dof); double ms = sw.Elapsed.TotalMilliseconds;
            Log(new { test = "KRN10-SKETCH-SPARSE", grid = 30, status = st.ToString(), dof, ms, corner = $"({c30.X:R},{c30.Y:R})" });
            if (st != SolveStatus.Ok || dof != 0 || Math.Abs(c30.X - 300) > 1e-6 || Math.Abs(c30.Y - 300) > 1e-6) fails.Add($"grid 31×31: {st}/{dof} corner ({c30.X},{c30.Y})");
            if (ms > 4000) fails.Add($"grid 31×31: {ms:0} ms");
            if (NumericSketchSolver.SparseSteps == s0) fails.Add("no sparse steps");
            foreach (bool dense in new[] { false, true })
            {
                NumericSketchSolver.SparseOffForTests = dense;
                try
                {
                    var (g, _) = GridSketch(12, dropOne: true);
                    var st1 = solver.Solve(g, null, out int dof1);
                    Log(new { test = "KRN10-SKETCH-SPARSE", grid = 12, dropOne = true, dense, status = st1.ToString(), dof = dof1 });
                    if (st1 != SolveStatus.Underconstrained || dof1 != 1) fails.Add($"grid 13×13 minus one dimension ({(dense ? "dense" : "sparse")}): {st1}/{dof1}");
                }
                finally { NumericSketchSolver.SparseOffForTests = false; }
            }
            {
                var (ga, ca) = GridSketch(20); var (gb, cb) = GridSketch(20);
                solver.Solve(ga, null, out _);
                NumericSketchSolver.SparseOffForTests = true;
                try { solver.Solve(gb, null, out _); } finally { NumericSketchSolver.SparseOffForTests = false; }
                var pa = ga.Entities.OfType<SkPoint>().ToList(); var pb = gb.Entities.OfType<SkPoint>().ToList();
                double worst = pa.Zip(pb).Max(t => Math.Max(Math.Abs(t.First.X - t.Second.X), Math.Abs(t.First.Y - t.Second.Y)));
                if (worst > 1e-9) fails.Add($"grid 21×21: sparse vs dense differ by {worst:G3}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
    static void GridSolveSizes(int[] sizes)
    {
        var solver = new NumericSketchSolver();
        foreach (int N in sizes)
        {
            var s = new Sketch("grid", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
            var P = new SkPoint[N + 1, N + 1];
            var rnd = new Random(7);
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) { P[i, j] = new SkPoint(i * 10 + rnd.NextDouble(), j * 10 + rnd.NextDouble()); s.AddEntity(P[i, j]); }
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                {
                    if (i < N) { var l = new SkLine(P[i, j], P[i + 1, j]); s.AddEntity(l); s.AddConstraint(ConstraintType.Horizontal, l); if (j == 0) s.AddDimension(DimensionType.DistancePointPoint, 10, P[i, j], P[i + 1, j]).Locked = true; }
                    if (j < N) { var l = new SkLine(P[i, j], P[i, j + 1]); s.AddEntity(l); s.AddConstraint(ConstraintType.Vertical, l); if (i == 0) s.AddDimension(DimensionType.DistancePointPoint, 10, P[i, j], P[i, j + 1]).Locked = true; }
                }
            s.AddDimension(DimensionType.CoordX, 0, P[0, 0]).Locked = true; s.AddDimension(DimensionType.CoordY, 0, P[0, 0]).Locked = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var st = solver.Solve(s, null, out int dof);
            double ms = sw.Elapsed.TotalMilliseconds;
            Log(new { grid = N, variables = 2 * (N + 1) * (N + 1), status = st.ToString(), dof, ms, corner = $"({P[N, N].X:0.######},{P[N, N].Y:0.######})" });
            if (N == 12)
                Check("K09-SKETCH-SOLVER-CONNECTED-GRID", () => Require(st == SolveStatus.Ok && dof == 0 && Math.Abs(P[N, N].X - 120) < 1e-6 && Math.Abs(P[N, N].Y - 120) < 1e-6 && ms < 1000,
                    $"grid 13×13: {st}/{dof} in {ms:0} ms, corner ({P[N, N].X},{P[N, N].Y})"));
        }
    }
}
static partial class Program
{
    static double RoundedBox(double a, double b, double c, double r) =>
        (a - 2 * r) * (b - 2 * r) * (c - 2 * r) + 2 * r * ((a - 2 * r) * (b - 2 * r) + (b - 2 * r) * (c - 2 * r) + (a - 2 * r) * (c - 2 * r))
        + Math.PI * r * r * ((a - 2 * r) + (b - 2 * r) + (c - 2 * r)) + 4.0 / 3 * Math.PI * r * r * r;

    static void FilletMatrix()
    {
        var rows = new List<string>();
        void Case(string name, Func<Part, IEnumerable<TEdge>> pick, double expected, bool allowRefusal = false)
        {
            var p = new Part("fm"); var s = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (0, 0), (60, 0), (60, 40), (0, 40));
            var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(20); p.AddFeature(ex); p.Rebuild(null);
            var rf = new RoundFeature { Radius = new ValueRef(3) };
            foreach (var e in pick(p)) rf.Edges.Add(EdgeRef.Of(e));
            p.AddFeature(rf);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = p.Compute(null); p.Apply(r);
            double ms = sw.Elapsed.TotalMilliseconds;
            string verdict = string.Join("|", p.Bodies.Select(b => SolidValidator.Validate(b).Summary()));
            double rel = Math.Abs(p.Volume - expected) / expected;
            bool ok = rf.Error == null && !verdict.Contains("error") && rel < 2e-3;
            Log(new { fillet = name, edges = rf.Edges.Count, err = rf.Error, vol = p.Volume, expected, rel, verdict, ms, analytic = !r.MassFromMesh });
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {(rf.Error ?? $"vol {p.Volume:0.###} vs {expected:0.###} (rel {rel:E1})")} {verdict}");
        }
        IEnumerable<TEdge> Vert(Part p) => p.Bodies[0].Edges.Where(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(e.V0.P.X - e.V1.P.X) < 1e-9 && Math.Abs(e.V0.P.Y - e.V1.P.Y) < 1e-9);
        IEnumerable<TEdge> Top(Part p) => p.Bodies[0].Edges.Where(e => Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9);
        double r = 3, lossPerLen = r * r * (1 - Math.PI / 4);
        Case("one vertical edge", p => Vert(p).Take(1), 48000 - lossPerLen * 20);
        Case("four vertical edges", p => Vert(p), 48000 - 4 * lossPerLen * 20);
        // Mitred corners of a closed loop: the two edge removals overlap by ∫₀ʳ w(z)² dz = r³(5/3 − π/2) per corner.
        Case("four top edges (closed loop)", p => Top(p), 48000 - lossPerLen * 2 * (60 + 40) + 4 * r * r * r * (5.0 / 3 - Math.PI / 2));
        Case("all twelve edges (corner blends)", p => p.Bodies[0].Edges, RoundedBox(60, 40, 20, r));
        {
            // Variable radius 2 → 4 along a vertical edge of height 20: removed (1 − π/4)∫(2 + z/10)² dz.
            var p = new Part("var"); var sk = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (0, 0), (60, 0), (60, 40), (0, 40));
            var ex = new ExtrudeFeature { SketchId = sk.Id }; ex.Side1.Distance = new ValueRef(20); p.AddFeature(ex); p.Rebuild(null);
            var e = Vert(p).First(x => Math.Abs(x.V0.P.X - 60) < 1e-9 && Math.Abs(x.V0.P.Y - 40) < 1e-9);
            var w = new List<string>(); var bodies = p.Bodies.ToList();
            bool upward = e.V0.P.Z < e.V1.P.Z;
            Parametric3D.Kernel.Ops.FilletOp.RoundEdgesVariable(bodies, new List<EdgeRef> { EdgeRef.Of(e) }, upward ? 2 : 4, upward ? 4 : 2, w);
            double vol = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bodies[0]));
            double expect = 48000 - (1 - Math.PI / 4) * 10.0 / 3 * (64 - 8);
            var verdict = SolidValidator.Validate(bodies[0]).Summary();
            bool ok = Math.Abs(vol - expect) / expect < 2e-3 && !verdict.Contains("error");
            Log(new { fillet = "variable 2→4", vol, expect, verdict, warnings = w.Take(3) });
            rows.Add($"{(ok ? "OK  " : "FAIL")} variable radius 2→4: vol {vol:0.###} vs {expect:0.###} {verdict}");
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("K09-FILLET-MATRIX", () => Require(rows.All(x => x.StartsWith("OK")), string.Join("; ", rows.Where(x => !x.StartsWith("OK")))));
    }
}
static partial class Program
{
    static void LoftProbe()
    {
        Frame3 XYz(double z) => new(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        var cases = new List<(string, Func<Part>, double Lo, double Hi)>();
        cases.Add(("circle r10 → square 20", () =>
        {
            var p = new Part("l1"); var a = CircleSketch(p, 0, 0, 0, 10); var b = PolySketch(p, XYz(30), (-10, -10), (10, -10), (10, 10), (-10, 10));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return p;
        }, Math.PI * 100 * 30, 400 * 30));
        cases.Add(("square 20 → square 20 rotated 45°", () =>
        {
            var p = new Part("l2"); var a = PolySketch(p, XYz(0), (-10, -10), (10, -10), (10, 10), (-10, 10));
            double q = 10 * Math.Sqrt(2); var b = PolySketch(p, XYz(30), (q, 0), (0, q), (-q, 0), (0, -q));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return p;
        }, 0.7 * 400 * 30, 400 * 30));
        cases.Add(("three sections: square 20 → circle r5 → square 20", () =>
        {
            var p = new Part("l3"); var a = PolySketch(p, XYz(0), (-10, -10), (10, -10), (10, 10), (-10, 10)); var m = CircleSketch(p, 15, 0, 0, 5);
            var b = PolySketch(p, XYz(30), (-10, -10), (10, -10), (10, 10), (-10, 10));
            var lf = new LoftFeature(); lf.SectionSketchIds.Add(a.Id); lf.SectionSketchIds.Add(m.Id); lf.SectionSketchIds.Add(b.Id); p.AddFeature(lf); return p;
        }, Math.PI * 25 * 30, 400 * 30));
        foreach (var (name, build, lo, hi) in cases)
        {
            var p = build(); var r = p.Compute(null); p.Apply(r);
            var b = p.Bodies.FirstOrDefault();
            var verdict = b == null ? "no body" : SolidValidator.Validate(b, null, new ValidationOptions { CheckSelfIntersections = true }).Summary();
            Log(new { loft = name, err = p.Features[0].Error, vol = p.Volume, lo, hi, inRange = p.Volume >= lo && p.Volume <= hi, verdict,
                      faces = b?.Faces.Select(f => f.Surface.GetType().Name).GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}") });
        }
    }
}
static partial class Program
{
    /// <summary>Rounded equilateral prism (side a, height h, all edges and corners rolled with r): Steiner formula.</summary>
    static double RoundedPrism(double a, double h, double r)
    {
        double ai = a - 2 * Math.Sqrt(3) * r, hi = h - 2 * r;
        double area = Math.Sqrt(3) / 4 * ai * ai, perim = 3 * ai;
        double V = area * hi, S = 2 * area + perim * hi;
        double M = 0.5 * (3 * hi * (Math.PI - Math.PI / 3) + 6 * ai * (Math.PI - Math.PI / 2));
        return V + S * r + M * r * r + 4.0 / 3 * Math.PI * r * r * r;
    }

    static void PrismCorners()
    {
        double a = 40, h = 20, r = 3;
        var p = new Part("prism");
        var s = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (0, 0), (a, 0), (a / 2, a * Math.Sqrt(3) / 2));
        var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(h); p.AddFeature(ex); p.Rebuild(null);
        var rf = new RoundFeature { Radius = new ValueRef(r) };
        foreach (var e in p.Bodies[0].Edges) rf.Edges.Add(EdgeRef.Of(e));
        p.AddFeature(rf);
        var res = p.Compute(null); p.Apply(res);
        double expect = RoundedPrism(a, h, r);
        if (p.Bodies.Count > 0)
        {
            var vb = SolidValidator.Validate(p.Bodies[0]);
            Log(new { prismDetail = "all", analytic = AnalyticVolume.Of(p.Bodies[0]), massFromMesh = res.MassFromMesh, audit = GeometryAudit.Of(p.Bodies[0]).Summary(),
                      issues = vb.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3).Select(i => i.ToString()) });
        }
        var verdict = p.Bodies.Count > 0 ? SolidValidator.Validate(p.Bodies[0]).Summary() : "none";
        Log(new { prism = "all edges", err = rf.Error, vol = p.Volume, expect, diff = p.Volume - expect, rel = Math.Abs(p.Volume - expect) / expect, verdict,
                  warnings = res.Warnings.Where(w => w.Contains("corner")).Take(2) });
        foreach (var (which, pick, exp) in new (string, Func<TEdge, bool>, double)[]
        {
            ("vertical only", e => Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1e-9, (Math.Sqrt(3) / 4 * a * a - 3 * r * r * (Math.Sqrt(3) - Math.PI / 3)) * h),
            ("top three only", e => Math.Abs(e.V0.P.Z - h) < 1e-9 && Math.Abs(e.V1.P.Z - h) < 1e-9, double.NaN),
        })
        {
            var q = new Part("prism2");
            var s2 = PolySketch(q, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (0, 0), (a, 0), (a / 2, a * Math.Sqrt(3) / 2));
            var ex2 = new ExtrudeFeature { SketchId = s2.Id }; ex2.Side1.Distance = new ValueRef(h); q.AddFeature(ex2); q.Rebuild(null);
            var rf2 = new RoundFeature { Radius = new ValueRef(r) };
            foreach (var e in q.Bodies[0].Edges.Where(pick)) rf2.Edges.Add(EdgeRef.Of(e));
            q.AddFeature(rf2); q.Rebuild(null);
            Log(new { prism = which, vol = q.Volume, exp = double.IsNaN(exp) ? -1 : exp, verdict = SolidValidator.Validate(q.Bodies[0]).Summary() });
        }
        // Known limitation: corners that are not right angles stay as three-cylinder corners (valid body,
        // 2.45 mm³ more material per corner at r = 3). The check documents that the body is valid.
        Check("K09-PRISM-ROUND-ALL-EDGES-VALID", () => Require(rf.Error == null && !verdict.Contains("error"),
            $"rounded prism {p.Volume} vs Steiner {expect}: {verdict} {rf.Error}"));
    }
}
static partial class Program
{
    /// <summary>K09. Каждое условие решателя на маленьком эскизе с проверкой получившейся геометрии.</summary>
    static void K09Constraints()
    {
        var solver = new NumericSketchSolver();
        var rows = new List<string>();
        Sketch NewS() => new("c", new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY));
        void Row(string name, Func<(SolveStatus St, bool Geo, string Info)> run)
        {
            try
            {
                var (st, geo, info) = run();
                bool ok = st is SolveStatus.Ok or SolveStatus.Underconstrained && geo;
                rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {st} {info}");
            }
            catch (Exception ex) { rows.Add($"FAIL {name}: {ex.GetType().Name} {ex.Message}"); }
        }
        const double E = 1e-9;
        Row("coincident", () => { var s = NewS(); var a = new SkPoint(0, 0); var b = new SkPoint(1.3, 0.7); s.AddEntity(a); s.AddEntity(b); s.AddConstraint(ConstraintType.Fix, a); s.AddConstraint(ConstraintType.Coincident, a, b); var st = solver.Solve(s, null, out _); return (st, (b.P - a.P).Length < E, $"{b.P}"); });
        Row("point on line", () => { var s = NewS(); var l = new SkLine(new SkPoint(0, 0), new SkPoint(10, 0)); var p = new SkPoint(4, 2.5); s.AddEntity(l); s.AddEntity(p); s.AddConstraint(ConstraintType.Fix, l.A); s.AddConstraint(ConstraintType.Fix, l.B); s.AddConstraint(ConstraintType.PointOnEntity, p, l); var st = solver.Solve(s, null, out _); return (st, Math.Abs(p.Y) < E, $"{p.P}"); });
        Row("point on circle", () => { var s = NewS(); var c = new SkCircle(new SkPoint(0, 0), 5); var p = new SkPoint(3, 1); s.AddEntity(c); s.AddEntity(p); s.AddConstraint(ConstraintType.Fix, c.Center); s.AddDimension(DimensionType.Radius, 5, c).Locked = true; s.AddConstraint(ConstraintType.PointOnEntity, p, c); var st = solver.Solve(s, null, out _); return (st, Math.Abs(p.P.Length - 5) < E, $"|p| {p.P.Length:0.#########}"); });
        Row("parallel", () => { var s = NewS(); var a = new SkLine(new SkPoint(0, 0), new SkPoint(10, 1)); var b = new SkLine(new SkPoint(0, 5), new SkPoint(8, 7)); s.AddEntity(a); s.AddEntity(b); s.AddConstraint(ConstraintType.Fix, a.A); s.AddConstraint(ConstraintType.Fix, a.B); s.AddConstraint(ConstraintType.Parallel, a, b); var st = solver.Solve(s, null, out _); var da = a.B.P - a.A.P; var db = b.B.P - b.A.P; return (st, Math.Abs(da.X * db.Y - da.Y * db.X) < E * da.Length * db.Length, ""); });
        Row("perpendicular", () => { var s = NewS(); var a = new SkLine(new SkPoint(0, 0), new SkPoint(10, 1)); var b = new SkLine(new SkPoint(2, 1), new SkPoint(3, 7)); s.AddEntity(a); s.AddEntity(b); s.AddConstraint(ConstraintType.Fix, a.A); s.AddConstraint(ConstraintType.Fix, a.B); s.AddConstraint(ConstraintType.Perpendicular, a, b); var st = solver.Solve(s, null, out _); var da = a.B.P - a.A.P; var db = b.B.P - b.A.P; return (st, Math.Abs(Vector2d.Dot(da, db)) < E * da.Length * db.Length, ""); });
        Row("equal lengths", () => { var s = NewS(); var a = new SkLine(new SkPoint(0, 0), new SkPoint(10, 0)); var b = new SkLine(new SkPoint(0, 5), new SkPoint(6, 5)); s.AddEntity(a); s.AddEntity(b); s.AddConstraint(ConstraintType.Fix, a.A); s.AddConstraint(ConstraintType.Fix, a.B); s.AddConstraint(ConstraintType.Equal, a, b); var st = solver.Solve(s, null, out _); return (st, Math.Abs((b.B.P - b.A.P).Length - 10) < E, $"{(b.B.P - b.A.P).Length:0.#########}"); });
        Row("equal radii", () => { var s = NewS(); var a = new SkCircle(new SkPoint(0, 0), 4); var b = new SkCircle(new SkPoint(20, 0), 7); s.AddEntity(a); s.AddEntity(b); s.AddDimension(DimensionType.Radius, 4, a).Locked = true; s.AddConstraint(ConstraintType.Equal, a, b); var st = solver.Solve(s, null, out _); return (st, Math.Abs(b.Radius - 4) < E, $"{b.Radius:0.#########}"); });
        Row("symmetric about a line", () => { var s = NewS(); var ax = new SkLine(new SkPoint(0, -5), new SkPoint(0, 5)); var p = new SkPoint(-3, 1); var q = new SkPoint(2.5, 1.4); s.AddEntity(ax); s.AddEntity(p); s.AddEntity(q); s.AddConstraint(ConstraintType.Fix, ax.A); s.AddConstraint(ConstraintType.Fix, ax.B); s.AddConstraint(ConstraintType.Fix, p); s.AddConstraint(ConstraintType.Symmetric, p, q, ax); var st = solver.Solve(s, null, out _); return (st, Math.Abs(q.X - 3) < E && Math.Abs(q.Y - 1) < E, $"{q.P}"); });
        Row("midpoint", () => { var s = NewS(); var l = new SkLine(new SkPoint(0, 0), new SkPoint(10, 4)); var m = new SkPoint(3, 3); s.AddEntity(l); s.AddEntity(m); s.AddConstraint(ConstraintType.Fix, l.A); s.AddConstraint(ConstraintType.Fix, l.B); s.AddConstraint(ConstraintType.MidPoint, m, l); var st = solver.Solve(s, null, out _); return (st, (m.P - new Vector2d(5, 2)).Length < E, $"{m.P}"); });
        Row("concentric", () => { var s = NewS(); var a = new SkCircle(new SkPoint(1, 1), 3); var b = new SkCircle(new SkPoint(4, 2), 5); s.AddEntity(a); s.AddEntity(b); s.AddConstraint(ConstraintType.Fix, a.Center); s.AddConstraint(ConstraintType.Concentric, a, b); var st = solver.Solve(s, null, out _); return (st, (b.Center.P - a.Center.P).Length < E, $"{b.Center.P}"); });
        Row("tangent circle–circle", () => { var s = NewS(); var a = new SkCircle(new SkPoint(0, 0), 5); var b = new SkCircle(new SkPoint(12, 1), 3); s.AddEntity(a); s.AddEntity(b); s.AddConstraint(ConstraintType.Fix, a.Center); s.AddDimension(DimensionType.Radius, 5, a).Locked = true; s.AddDimension(DimensionType.Radius, 3, b).Locked = true; s.AddConstraint(ConstraintType.Tangent, a, b); var st = solver.Solve(s, null, out _); double d = (b.Center.P - a.Center.P).Length; return (st, Math.Abs(d - 8) < E || Math.Abs(d - 2) < E, $"centre distance {d:0.#########}"); });
        Row("tangent line–arc", () => { var s = NewS(); var arc = new SkArc(new SkPoint(0, 0), new SkPoint(5, 0), new SkPoint(0, 5)); var l = new SkLine(new SkPoint(-8, 6.3), new SkPoint(8, 4.7)); s.AddEntity(arc); s.AddEntity(l); s.AddConstraint(ConstraintType.Fix, arc.Center); s.AddDimension(DimensionType.Radius, 5, arc).Locked = true; s.AddConstraint(ConstraintType.Horizontal, l); s.AddConstraint(ConstraintType.Tangent, l, arc); var st = solver.Solve(s, null, out _); return (st, Math.Abs(Math.Abs(l.A.Y) - 5) < E, $"line y {l.A.Y:0.#########}"); });
        Row("angle 30° between lines", () => { var s = NewS(); var a = new SkLine(new SkPoint(0, 0), new SkPoint(10, 0)); var b = new SkLine(new SkPoint(0, 0), new SkPoint(8, 3)); s.AddEntity(a); s.AddEntity(b); s.AddConstraint(ConstraintType.Fix, a.A); s.AddConstraint(ConstraintType.Fix, a.B); s.AddConstraint(ConstraintType.Coincident, a.A, b.A); s.AddDimension(DimensionType.AngleLineLine, 30, a, b).Locked = true; var st = solver.Solve(s, null, out _); var db = b.B.P - b.A.P; double ang = Math.Atan2(Math.Abs(db.Y), db.X) * 180 / Math.PI; return (st, Math.Abs(ang - 30) < 1e-5, $"{ang:0.######}°"); });
        Row("distance point–line", () => { var s = NewS(); var l = new SkLine(new SkPoint(0, 0), new SkPoint(10, 0)); var p = new SkPoint(3, 2); s.AddEntity(l); s.AddEntity(p); s.AddConstraint(ConstraintType.Fix, l.A); s.AddConstraint(ConstraintType.Fix, l.B); s.AddDimension(DimensionType.DistancePointLine, 7, p, l).Locked = true; var st = solver.Solve(s, null, out _); return (st, Math.Abs(Math.Abs(p.Y) - 7) < E, $"{p.P}"); });
        Row("ellipse semi-axes", () => { var s = NewS(); var e = new SkEllipse(new SkPoint(0, 0), 8, 3); s.AddEntity(e); s.AddConstraint(ConstraintType.Fix, e.Center); s.AddDimension(DimensionType.EllipseRadiusU, 12, e).Locked = true; s.AddDimension(DimensionType.EllipseRadiusV, 4, e).Locked = true; var st = solver.Solve(s, null, out _); return (st, Math.Abs(e.RU - 12) < E && Math.Abs(e.RV - 4) < E, $"{e.RU:0.###}×{e.RV:0.###}"); });
        Row("point on ellipse", () => { var s = NewS(); var e = new SkEllipse(new SkPoint(0, 0), 8, 3); var p = new SkPoint(5, 3); s.AddEntity(e); s.AddEntity(p); s.AddConstraint(ConstraintType.Fix, e.Center); s.AddDimension(DimensionType.EllipseRadiusU, 8, e).Locked = true; s.AddDimension(DimensionType.EllipseRadiusV, 3, e).Locked = true; s.AddConstraint(ConstraintType.PointOnEntity, p, e); var st = solver.Solve(s, null, out _); double f = p.X * p.X / 64 + p.Y * p.Y / 9; return (st, Math.Abs(f - 1) < 1e-6, $"x²/a²+y²/b² = {f:0.#########}"); });
        Row("tangent line–ellipse (rotated 20°)", () => { var s = NewS(); var e = new SkEllipse(new SkPoint(0, 0), 8, 3, 20 * Math.PI / 180); var l = new SkLine(new SkPoint(-10, 6.2), new SkPoint(10, 5.6)); s.AddEntity(e); s.AddEntity(l); s.AddConstraint(ConstraintType.Fix, e.Center); s.AddDimension(DimensionType.EllipseRadiusU, 8, e).Locked = true; s.AddDimension(DimensionType.EllipseRadiusV, 3, e).Locked = true; s.AddConstraint(ConstraintType.Horizontal, l); s.AddConstraint(ConstraintType.Tangent, l, e);
            var st = solver.Solve(s, null, out _);
            // Exact: the topmost point of the rotated ellipse, y_max = sqrt(a² sin²θ + b² cos²θ).
            double th = 20 * Math.PI / 180, ymax = Math.Sqrt(64 * Math.Sin(th) * Math.Sin(th) + 9 * Math.Cos(th) * Math.Cos(th));
            return (st, Math.Abs(Math.Abs(l.A.Y) - ymax) < E, $"line y {l.A.Y:0.#########} vs {ymax:0.#########}"); });
        {
            // An unsupported combination is reported, not silently dropped (tangent ellipse–ellipse).
            var s = NewS(); var e = new SkEllipse(new SkPoint(0, 0), 8, 3); var e2 = new SkEllipse(new SkPoint(20, 0), 5, 2);
            s.AddEntity(e); s.AddEntity(e2); s.AddConstraint(ConstraintType.Tangent, e, e2);
            solver.Solve(s, null, out _);
            bool rep = NumericSketchSolver.Unsupported.Any(u => u.StartsWith("Tangent"));
            rows.Add($"{(rep ? "OK  " : "FAIL")} unsupported combination reported: {string.Join(", ", NumericSketchSolver.Unsupported)}");
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("K09-SOLVER-CONSTRAINTS", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
static partial class Program
{
    /// <summary>Removed volume of a vertex round R at a right-angle corner, by 2-D quadrature (independent of the kernel).</summary>
    static double VertexRoundRemoved(double R)
    {
        // Right-angle corner at the origin, body in the positive octant, sphere tangent to the three
        // edges: centre (s, s, s), s = R/√2; removed = cube [0,s]³ outside the sphere.
        double s = R / Math.Sqrt(2); int N = 3000; double h = s / N, sum = 0;
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                double x = (i + 0.5) * h, y = (j + 0.5) * h;
                double q = R * R - (x - s) * (x - s) - (y - s) * (y - s);
                double hh = q > 0 ? Math.Sqrt(q) : 0;
                sum += (s - Math.Min(s, hh)) * h * h;
            }
        return sum;
    }

    static void VertexOps()
    {
        var rows = new List<string>();
        Part Cube() { var p = new Part("v"); TestGeometry.AddCube(p, 60); p.Rebuild(null); return p; }
        var corner = new Vector3d(30, 30, 30);
        void Row(string name, Part p, Feature f, double expected, Func<Part, string?>? extra = null, bool exactRequired = true)
        {
            p.AddFeature(f); var r = p.Compute(null); p.Apply(r);
            var b = p.Bodies.FirstOrDefault();
            string verdict = b == null ? "no body" : SolidValidator.Validate(b).Summary();
            double vol = p.Volume; double rel = Math.Abs(vol - expected) / expected;
            string? why = extra?.Invoke(p);
            double tol = !r.MassFromMesh ? 1e-7 : 2e-4;
            bool ok = f.Error == null && !verdict.Contains("error") && rel < tol && why == null;
            Log(new { vertexOp = name, vol, expected, rel, analytic = !r.MassFromMesh, err = f.Error, verdict, why });
            rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {(f.Error ?? $"vol {vol:0.######} vs {expected:0.######} (rel {rel:E1}, {(r.MassFromMesh ? "mesh" : "exact")})")} {verdict} {why}");
        }
        double tolV = 2e-5;
        string? HasVertices(Part p, params Vector3d[] pts) =>
            pts.All(q => p.Bodies[0].Edges.Any(e => (e.V0.P - q).Length < tolV || (e.V1.P - q).Length < tolV)) ? null
            : "expected vertices missing: " + string.Join(", ", pts.Select(q => p.Bodies[0].Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).Min(v => (v - q).Length).ToString("E1")));

        // 1. Uniform chamfer d = 6 at a right-angle corner: tetrahedron d³/6.
        Row("chamfer uniform d6", Cube(), new VertexChamferFeature { Vertex = corner, Distance = new ValueRef(6) }, 216000 - 216.0 / 6,
            p => HasVertices(p, new Vector3d(24, 30, 30), new Vector3d(30, 24, 30), new Vector3d(30, 30, 24)));
        // 2. Per-edge distances 4 / 6 / 9 picked by points on the edges: d1·d2·d3/6.
        {
            var f = new VertexChamferFeature { Vertex = corner };
            f.PerEdge.Add((new Vector3d(28, 30, 30), new ValueRef(4)));
            f.PerEdge.Add((new Vector3d(30, 28, 30), new ValueRef(6)));
            f.PerEdge.Add((new Vector3d(30, 30, 28), new ValueRef(9)));
            Row("chamfer per edge 4/6/9", Cube(), f, 216000 - 4.0 * 6 * 9 / 6,
                p => HasVertices(p, new Vector3d(26, 30, 30), new Vector3d(30, 24, 30), new Vector3d(30, 30, 21)));
        }
        // 3. Round R = 6 at a right-angle corner: removed volume by quadrature.
        double removed = VertexRoundRemoved(6);
        double sb = 6 / Math.Sqrt(2);
        // A vertex round touches the edges with a 2e-5·R margin (robust transversal contact): the
        // edge ends ≈ R·√(4e-5) ≈ 0.04 mm before the exact tangent point.
        tolV = 0.06;
        Row("round R6", Cube(), new VertexRoundFeature { Vertex = corner, Radius = new ValueRef(6) }, 216000 - removed,
            p => HasVertices(p, new Vector3d(30 - sb, 30, 30), new Vector3d(30, 30 - sb, 30), new Vector3d(30, 30, 30 - sb)));
        tolV = 2e-5;
        // 4. Prism corner (60° between the side faces): uniform chamfer d4 removes d³·sin60°/6.
        Part Prism() { var p = new Part("pr"); var s = PolySketch(p, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), (0, 0), (40, 0), (20, 20 * Math.Sqrt(3))); var ex = new ExtrudeFeature { SketchId = s.Id }; ex.Side1.Distance = new ValueRef(20); p.AddFeature(ex); p.Rebuild(null); return p; }
        double prismV = Math.Sqrt(3) / 4 * 1600 * 20;
        Row("chamfer uniform d4 at a 60° corner", Prism(), new VertexChamferFeature { Vertex = new Vector3d(0, 0, 20), Distance = new ValueRef(4) }, prismV - 64 * Math.Sin(Math.PI / 3) / 6);
        // 5. Round R4 at the 60° corner: validity, the cut points on the edges.
        {
            var p = Prism(); var f = new VertexRoundFeature { Vertex = new Vector3d(0, 0, 20), Radius = new ValueRef(4) };
            p.AddFeature(f); p.Rebuild(null);
            var b = p.Bodies[0]; var v = SolidValidator.Validate(b);
            // Tangent points: equal setback along the three edges (computed by the same closed formula
            // independently here: a·eᵢ = k for all edges).
            var d1 = new Vector3d(1, 0, 0); var d2 = new Vector3d(0.5, Math.Sqrt(3) / 2, 0); var d3 = new Vector3d(0, 0, -1);
            double det = Vector3d.Dot(d1, Vector3d.Cross(d2, d3));
            var a = ((Vector3d.Cross(d2, d3) + Vector3d.Cross(d3, d1) + Vector3d.Cross(d1, d2)) / det).Normalized();
            double k = Math.Abs(Vector3d.Dot(a, d1)), sb4 = 4 / Math.Sqrt(1 - k * k) * k;
            var o = new Vector3d(0, 0, 20);
            tolV = 0.06;
            string? why = HasVertices(p, o + d1 * sb4, o + d2 * sb4, o + d3 * sb4);
            tolV = 2e-5;
            bool ok = f.Error == null && v.IsValid && why == null && p.Volume < prismV;
            rows.Add($"{(ok ? "OK  " : "FAIL")} round R4 at a 60° corner: {f.Error ?? $"vol {p.Volume:0.###} (< {prismV:0.###})"} {v.Summary()} {why}");
        }
        // 6. Too large: refused, body unchanged.
        {
            var p = Cube(); var f = new VertexRoundFeature { Vertex = corner, Radius = new ValueRef(90) };
            p.AddFeature(f); p.Rebuild(null);
            bool ok = f.Error != null && Math.Abs(p.Volume - 216000) < 1e-9;
            rows.Add($"{(ok ? "OK  " : "FAIL")} too large a radius is refused: {f.Error}");
        }
        // 7. Not a vertex: refused.
        {
            var p = Cube(); var f = new VertexChamferFeature { Vertex = new Vector3d(30, 30, 0), Distance = new ValueRef(3) };
            p.AddFeature(f); p.Rebuild(null);
            rows.Add($"{(f.Error != null ? "OK  " : "FAIL")} a point that is not a vertex is refused: {f.Error}");
        }
        // 8. Saved and loaded with the document.
        {
            var p = Cube();
            var vc = new VertexChamferFeature { Vertex = corner };
            vc.PerEdge.Add((new Vector3d(28, 30, 30), new ValueRef(4))); vc.PerEdge.Add((new Vector3d(30, 28, 30), new ValueRef(6))); vc.PerEdge.Add((new Vector3d(30, 30, 28), new ValueRef(9)));
            p.AddFeature(vc);
            p.AddFeature(new VertexRoundFeature { Vertex = new Vector3d(-30, -30, -30), Radius = new ValueRef(5) });
            p.Rebuild(null);
            var (q, t) = DocumentFile.Load(DocumentFile.Save(p)); q.Rebuild(t);
            bool ok = q.Features.All(f => f.Error == null) && Math.Abs(q.Volume - p.Volume) < 1e-9 && p.Features.All(f => f.Error == null);
            rows.Add($"{(ok ? "OK  " : "FAIL")} document round trip: {q.Volume:0.######} vs {p.Volume:0.######} {string.Join(";", p.Features.Select(f => f.Error))}");
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("K09-VERTEX-CHAMFER-AND-ROUND", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
static partial class Program
{
    static void VertexDbg()
    {
        var p = new Part("v"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
        p.AddFeature(new VertexRoundFeature { Vertex = new Vector3d(30, 30, 30), Radius = new ValueRef(6) }); p.Rebuild(null);
        foreach (var v in p.Bodies[0].Edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).Distinct().Where(q => (q - new Vector3d(30, 30, 30)).Length < 12))
            Console.WriteLine($"VDBG {v.X:0.######},{v.Y:0.######},{v.Z:0.######}");
        foreach (var e in p.Bodies[0].Edges.Where(e => (e.V0.P - new Vector3d(30, 30, 30)).Length < 12 || (e.V1.P - new Vector3d(30, 30, 30)).Length < 12))
            Console.WriteLine($"EDBG {e.Curve.GetType().Name} {e.V0.P} -> {e.V1.P}");
    }
}
static partial class Program
{
    static void VertexDbg2()
    {
        foreach (var (pt, r) in new[] { (new Vector3d(-30, -30, -30), 5.0), (new Vector3d(30, 30, 30), 5.0), (new Vector3d(-30, -30, -30), 6.0), (new Vector3d(30, -30, 30), 5.0) })
        {
            var p = new Part("v"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
            var f = new VertexRoundFeature { Vertex = pt, Radius = new ValueRef(r) }; p.AddFeature(f); p.Rebuild(null);
            Console.WriteLine($"VDBG2 {pt} R{r}: {f.Error ?? "ok " + p.Volume.ToString("0.######")}");
            if (f.Error != null && Parametric3D.Kernel.Ops.BooleanOp.LastInvalidResult is { } bad0)
                foreach (var i in SolidValidator.ValidateBRep(bad0).Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(1))
                {
                    var face = bad0.Faces.FirstOrDefault(x => x.Id == i.Element);
                    Console.WriteLine($"VDBG2 issue0 {i} face {face?.Tag} " + (face == null ? "" : string.Join(" | ", face.Loops.Select(l => string.Join(" ", l.CoEdges.Select(ce => $"{ce.Edge.Curve.GetType().Name[0]}{ce.Start.P:0.####}->{ce.End.P:0.####}"))))));
                }
        }
        foreach (var first in new Feature[] { new VertexChamferFeature { Vertex = new Vector3d(30, 30, 30), Distance = new ValueRef(6) }, new VertexRoundFeature { Vertex = new Vector3d(30, 30, 30), Radius = new ValueRef(6) }, new CutCyl(0, 0, 3) })
            foreach (var r in new[] { 5.0, 6.0 })
            {
                var p = new Part("v"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
                p.AddFeature<Feature>(first is VertexChamferFeature ? new VertexChamferFeature { Vertex = new Vector3d(30, 30, 30), Distance = new ValueRef(6) } : first is VertexRoundFeature ? new VertexRoundFeature { Vertex = new Vector3d(30, 30, 30), Radius = new ValueRef(6) } : new CutCyl(0, 0, 3));
                var f = new VertexRoundFeature { Vertex = new Vector3d(-30, -30, -30), Radius = new ValueRef(r) }; p.AddFeature(f); p.Rebuild(null);
                if (f.Error != null && Parametric3D.Kernel.Ops.BooleanOp.LastInvalidResult is { } bad)
                {
                    var v = SolidValidator.ValidateBRep(bad);
                    foreach (var i in v.Issues.Where(x => x.Severity == ValidationSeverity.Error).Take(2))
                    {
                        var face = bad.Faces.FirstOrDefault(x => x.Id == i.Element);
                        Console.WriteLine($"VDBG2 issue {i} face {face?.Tag} loops {face?.Loops.Count} " + (face == null ? "" : string.Join(" | ", face.Loops.Select(l => string.Join(" ", l.CoEdges.Select(ce => $"{ce.Start.P:0.##}->{ce.End.P:0.##}"))))));
                    }
                }
                Console.WriteLine($"VDBG2 after {first.GetType().Name} R{r}: {(f.Error ?? "ok").Substring(0, Math.Min(150, (f.Error ?? "ok").Length))}");
            }
    }
}
static partial class Program
{
    static void VertexMargin()
    {
        foreach (var m in new[] { 2e-5, 2e-6, 2e-7 })
        {
            Parametric3D.Kernel.Ops.VertexOp.BallMargin = m;
            var res = new List<string>();
            foreach (var (pt, r, first) in new[] { (new Vector3d(30, 30, 30), 6.0, false), (new Vector3d(30, -30, 30), 5.0, false), (new Vector3d(-30, -30, -30), 5.0, true) })
            {
                var p = new Part("v"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
                if (first) p.AddFeature(new VertexChamferFeature { Vertex = new Vector3d(30, 30, 30), Distance = new ValueRef(6) });
                var f = new VertexRoundFeature { Vertex = pt, Radius = new ValueRef(r) }; p.AddFeature(f); p.Rebuild(null);
                res.Add(f.Error == null ? $"{p.Volume:0.######}" : "FAIL");
            }
            Console.WriteLine($"MARGIN {m}: {string.Join(" ", res)}");
        }
        Parametric3D.Kernel.Ops.VertexOp.BallMargin = 2e-6;
    }
}
static partial class Program
{
    /// <summary>Правильная N-угольная пирамида: основание радиуса rb на z = 0, вершина (0, 0, h).</summary>
    static TSolid Pyramid(int n, double rb, double h)
    {
        var apex = new Vector3d(0, 0, h);
        var hs = new List<(Vector3d At, Vector3d Inward)> { (Vector3d.Zero, Vector3d.UnitZ) };
        for (int i = 0; i < n; i++)
        {
            double a0 = 2 * Math.PI * i / n, a1 = 2 * Math.PI * (i + 1) / n;
            var p0 = new Vector3d(rb * Math.Cos(a0), rb * Math.Sin(a0), 0); var p1 = new Vector3d(rb * Math.Cos(a1), rb * Math.Sin(a1), 0);
            var nrm = Vector3d.Cross(p1 - p0, apex - p0).Normalized();      // outward
            hs.Add((p0, -nrm));
        }
        var w = new List<string>();
        var body = Parametric3D.Kernel.Ops.FilletOp.HalfspaceCell(new Vector3d(0, 0, h / 2), new Vector3d(0, 0, h / 2), Math.Max(rb, h), hs, w)!;
        TopologyConventions.Normalize(body);
        return body;
    }

    static void VertexOpsN()
    {
        var rows = new List<string>();
        void Add(string name, bool ok, string info) => rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {info}");
        double Vol(TSolid b) => AnalyticVolume.Of(b) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
        bool HasV(TSolid b, Vector3d q, double tol) => b.Edges.Any(e => (e.V0.P - q).Length < tol || (e.V1.P - q).Length < tol);
        foreach (int n in new[] { 4, 5, 6 })
        {
            double rb = 20, h = 20, L = Math.Sqrt(rb * rb + h * h);
            double vPyr = n / 2.0 * rb * rb * Math.Sin(2 * Math.PI / n) * h / 3;
            var apex = new Vector3d(0, 0, h);
            // Uniform chamfer d: the cut points are coplanar (regular pyramid) — a similar small pyramid is removed.
            {
                var b = new List<TSolid> { Pyramid(n, rb, h) }; var w = new List<string>();
                double v0 = Vol(b[0]); double d = 6;
                string? err = null;
                try { Parametric3D.Kernel.Ops.VertexOp.Chamfer(b, apex, new[] { d }, w); } catch (Exception ex) { err = ex.Message; }
                double removed = err == null ? v0 - Vol(b[0]) : -1, expect = vPyr * Math.Pow(d / L, 3);
                var verdict = err == null ? SolidValidator.Validate(b[0]).Summary() : err;
                Add($"N={n} chamfer uniform d6", err == null && !verdict.Contains("error") && Math.Abs(removed - expect) < 1e-6 * vPyr,
                    $"removed {removed:0.######} vs {expect:0.######}; pyramid volume {v0:0.###} vs {vPyr:0.###}; {verdict}");
            }
            // Per-edge distances whose cut points lie on a tilted plane: removed = (1/3)·area(Q)·dist(apex, plane).
            {
                var b = new List<TSolid> { Pyramid(n, rb, h) }; var w = new List<string>();
                double v0 = Vol(b[0]);
                var pn = new Vector3d(0.15, 0.1, 1).Normalized(); double pc = Vector3d.Dot(pn, apex) - 4;
                var corner = Parametric3D.Kernel.Ops.VertexOp.Find(b[0], apex);
                var hints = new List<(Vector3d, double)>(); var q = new List<Vector3d>();
                foreach (var e in corner.Dirs)
                {
                    double t = (pc - Vector3d.Dot(pn, apex)) / Vector3d.Dot(pn, e);
                    hints.Add((apex + e * t * 0.5, t)); q.Add(apex + e * t);
                }
                var d = Parametric3D.Kernel.Ops.VertexOp.MatchDistances(corner, hints);
                string? err = null;
                try { Parametric3D.Kernel.Ops.VertexOp.Chamfer(b, apex, d, w); } catch (Exception ex) { err = ex.Message; }
                // Planar polygon area of the cut points (ordered by angle around the axis).
                var ordered = q.OrderBy(x => Math.Atan2(x.Y, x.X)).ToList();
                var nsum = Vector3d.Zero; for (int i = 0; i < ordered.Count; i++) nsum += Vector3d.Cross(ordered[i], ordered[(i + 1) % ordered.Count]);
                double area = Math.Abs(Vector3d.Dot(nsum, pn)) / 2, dist = Math.Abs(Vector3d.Dot(pn, apex) - pc);
                double expect = area * dist / 3, removed = err == null ? v0 - Vol(b[0]) : -1;
                var verdict = err == null ? SolidValidator.Validate(b[0]).Summary() : err;
                Add($"N={n} chamfer per edge (tilted plane)", err == null && !verdict.Contains("error") && Math.Abs(removed - expect) < 1e-6 * vPyr,
                    $"removed {removed:0.######} vs {expect:0.######}; {verdict}");
            }
            // Per-edge distances NOT coplanar: several planar cut faces; valid body, each edge cut at its own distance.
            {
                var b = new List<TSolid> { Pyramid(n, rb, h) }; var w = new List<string>();
                var corner = Parametric3D.Kernel.Ops.VertexOp.Find(b[0], apex);
                var dist = corner.Dirs.Select((e, i) => 3.0 + 2.0 * (i % 3)).ToArray();
                string? err = null;
                try { Parametric3D.Kernel.Ops.VertexOp.Chamfer(b, apex, dist, w); } catch (Exception ex) { err = ex.Message; }
                var verdict = err == null ? SolidValidator.Validate(b[0]).Summary() : err;
                bool cuts = err == null && corner.Dirs.Select((e, i) => apex + e * dist[i]).All(x => HasV(b[0], x, 1e-4));
                Add($"N={n} chamfer per edge (non-coplanar)", err == null && !verdict.Contains("error") && cuts, $"{verdict}; cut points on the edges: {cuts}");
            }
            // Round R: the edges lie on a circular cone — the sphere touches all of them.
            {
                var b = new List<TSolid> { Pyramid(n, rb, h) }; var w = new List<string>();
                double v0 = Vol(b[0]); double R = 4;
                string? err = null;
                try { Parametric3D.Kernel.Ops.VertexOp.Round(b, apex, R, w); } catch (Exception ex) { err = ex.Message; }
                double k = h / L, lambda = R / Math.Sqrt(1 - k * k), s = lambda * k;
                var cz = h - lambda;
                // Independent removed volume: grid quadrature over the pyramid "apex + tangent points" outside the sphere.
                var corner0 = Parametric3D.Kernel.Ops.VertexOp.Find(Pyramid(n, rb, h), apex);
                var tpts = corner0.Dirs.Select(e => apex + e * s).OrderBy(x => Math.Atan2(x.Y, x.X)).ToArray();
                double zT = tpts[0].Z, rT = Math.Sqrt(tpts[0].X * tpts[0].X + tpts[0].Y * tpts[0].Y);
                int G = 160; double removedQ = 0; double dz = (h - zT) / G, dxy = 2 * rT / G;
                var faces = new List<(Vector3d P, Vector3d N)>();
                for (int i = 0; i < n; i++) { var a = tpts[i]; var c2 = tpts[(i + 1) % n]; var nn = Vector3d.Cross(c2 - apex, a - apex); faces.Add((apex, nn)); }
                // orient face normals inward (towards the axis point below the apex)
                for (int i = 0; i < faces.Count; i++) if (Vector3d.Dot(faces[i].N, new Vector3d(0, 0, zT) - apex) < 0) faces[i] = (faces[i].P, -faces[i].N);
                for (int iz = 0; iz < G; iz++)
                {
                    double z = zT + (iz + 0.5) * dz;
                    for (int ix = 0; ix < G; ix++)
                        for (int iy = 0; iy < G; iy++)
                        {
                            var x = new Vector3d(-rT + (ix + 0.5) * dxy, -rT + (iy + 0.5) * dxy, z);
                            if (faces.Any(f => Vector3d.Dot(f.N, x - f.P) < 0)) continue;
                            if ((x - new Vector3d(0, 0, cz)).LengthSquared > R * R) removedQ += dz * dxy * dxy;
                        }
                }
                double removed = err == null ? v0 - Vol(b[0]) : -1;
                var verdict = err == null ? SolidValidator.Validate(b[0]).Summary() : err;
                bool tangent = err == null && tpts.All(x => HasV(b[0], x, 0.06));
                if (err == null && Environment.GetEnvironmentVariable("VOPSDBG") == "1")
                {
                    foreach (var iss in SolidValidator.Validate(b[0]).Issues) Console.WriteLine($"  N={n} issue: {iss}");
                    var md = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b[0]); var sw2 = new StringWriter();
                    foreach (var v in md.Vertices) sw2.WriteLine(FormattableString.Invariant($"v {v.P.X:R} {v.P.Y:R} {v.P.Z:R}"));
                    for (int t = 0; t + 2 < md.Indices.Length; t += 3) sw2.WriteLine($"f {md.Indices[t] + 1} {md.Indices[t + 1] + 1} {md.Indices[t + 2] + 1}");
                    File.WriteAllText($"/tmp/claude-0/vops_{n}.obj", sw2.ToString());
                    if (b[0].Exact != null)
                    {
                        var RR = b[0].Exact.Solid.Registry; var sw = new StringWriter(); var vid = new Dictionary<int, int>();
                        foreach (var pg in b[0].Exact.Solid.Polygons)
                        {
                            var ids = pg.Vertices(RR).ToArray();
                            foreach (var q in ids) if (!vid.ContainsKey(q)) { vid[q] = vid.Count + 1; var (x, y, z) = RR.ToMm(q); sw.WriteLine(FormattableString.Invariant($"v {x:R} {y:R} {z:R}")); }
                            sw.WriteLine("f " + string.Join(" ", ids.Select(q => vid[q])));
                        }
                        File.WriteAllText($"/tmp/claude-0/vops_{n}_exact.obj", sw.ToString());
                    }
                }
                Add($"N={n} round R4", err == null && !verdict.Contains("error") && Math.Abs(removed - removedQ) < 0.02 * removedQ && tangent,
                    $"removed {removed:0.####} vs quadrature {removedQ:0.####}; tangent points {tangent}; {verdict}");
            }
        }
        // An irregular 4-edge corner (edges not on a circular cone): the sphere touches the farthest edges and cuts the others.
        {
            var hs = new List<(Vector3d At, Vector3d Inward)> { (Vector3d.Zero, Vector3d.UnitZ) };
            var apex = new Vector3d(2, -1, 18);
            var basePts = new[] { new Vector3d(20, 0, 0), new Vector3d(-5, 18, 0), new Vector3d(-16, -4, 0), new Vector3d(3, -19, 0) };
            for (int i = 0; i < 4; i++) { var p0 = basePts[i]; var p1 = basePts[(i + 1) % 4]; var nrm = Vector3d.Cross(p1 - p0, apex - p0).Normalized(); hs.Add((p0, -nrm)); }
            var w = new List<string>();
            var b = new List<TSolid> { Parametric3D.Kernel.Ops.FilletOp.HalfspaceCell(new Vector3d(0, 0, 9), new Vector3d(0, 0, 9), 20, hs, w)! };
            TopologyConventions.Normalize(b[0]);
            double v0 = Vol(b[0]);
            string? err = null;
            try { Parametric3D.Kernel.Ops.VertexOp.Round(b, apex, 3, w); } catch (Exception ex) { err = ex.Message; }
            var verdict = err == null ? SolidValidator.Validate(b[0]).Summary() : err;
            Add("irregular 4-edge corner round R3", err == null && !verdict.Contains("error") && Vol(b[0]) < v0, $"{verdict}; removed {(err == null ? v0 - Vol(b[0]) : -1):0.####}");
            b = new List<TSolid> { Parametric3D.Kernel.Ops.FilletOp.HalfspaceCell(new Vector3d(0, 0, 9), new Vector3d(0, 0, 9), 20, hs, w)! };
            TopologyConventions.Normalize(b[0]);
            err = null;
            try { Parametric3D.Kernel.Ops.VertexOp.Chamfer(b, apex, new[] { 4.0 }, w); } catch (Exception ex) { err = ex.Message; }
            verdict = err == null ? SolidValidator.Validate(b[0]).Summary() : err;
            Add("irregular 4-edge corner chamfer d4", err == null && !verdict.Contains("error"), verdict);
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("K09-VERTEX-N-EDGES", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
static partial class Program
{
    static void PyrDbg()
    {
        var b = Pyramid(4, 20, 20); var apex = new Vector3d(0, 0, 20);
        var v = b.Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().OrderBy(x => (x.P - apex).Length).First();
        var edges = b.Edges.Where(e => e.V0 == v || e.V1 == v).ToList();
        Console.WriteLine($"PYR vertex {v.P} edges {edges.Count} conv {b.TopologyConvention} valid {SolidValidator.Validate(b).Summary()}");
        var dirs = edges.Select(e => ((e.V0 == v ? e.V1.P : e.V0.P) - v.P).Normalized()).ToList();
        foreach (var f in edges.SelectMany(e => e.CoEdges).Select(c => c.Loop?.Face).OfType<TFace>().Distinct())
            Console.WriteLine($"PYR face {f.Tag} n {f.NormalAt(v.P)} maxdot {dirs.Max(d => Vector3d.Dot(f.NormalAt(v.P).Normalized(), d)):E2}");
    }
}
static partial class Program
{
    static void VertexConcave()
    {
        var rows = new List<string>();
        void Add(string name, bool ok, string info) => rows.Add($"{(ok ? "OK  " : "FAIL")} {name}: {info}");
        double Vol(TSolid b) => AnalyticVolume.Of(b) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b));
        bool HasV(TSolid b, Vector3d q, double tol) => b.Edges.Any(e => (e.V0.P - q).Length < tol || (e.V1.P - q).Length < tol);
        // Cube with an octant notch: (0,0,0) is a concave corner (the empty space is the octant x,y,z ≥ 0).
        List<TSolid> Notched()
        {
            var b = new List<TSolid> { Box(-30, -30, -30, 60, 60, 60) }; var w = new List<string>();
            Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, Box(0, 0, 0, 40, 40, 40), w);
            return b;
        }
        var o = Vector3d.Zero;
        string Run(Action a) { try { a(); return null!; } catch (Exception ex) { return ex.Message; } }
        {
            var b = Notched(); double v0 = Vol(b[0]); var w = new List<string>();
            var err = Run(() => Parametric3D.Kernel.Ops.VertexOp.Chamfer(b, o, new[] { 6.0 }, w));
            double added = err == null ? Vol(b[0]) - v0 : double.NaN;
            var verdict = err ?? SolidValidator.Validate(b[0]).Summary();
            Add("concave corner chamfer d6 (adds d³/6)", err == null && !verdict.Contains("error") && Math.Abs(added - 36) < 1e-6 && HasV(b[0], new Vector3d(6, 0, 0), 1e-5),
                $"added {added:0.######} vs 36; {verdict}");
        }
        {
            var b = Notched(); double v0 = Vol(b[0]); var w = new List<string>();
            var c = Parametric3D.Kernel.Ops.VertexOp.Find(b[0], o);
            var d = Parametric3D.Kernel.Ops.VertexOp.MatchDistances(c, new List<(Vector3d, double)> { (new Vector3d(2, 0, 0), 4), (new Vector3d(0, 2, 0), 6), (new Vector3d(0, 0, 2), 9) });
            var err = Run(() => Parametric3D.Kernel.Ops.VertexOp.Chamfer(b, o, d, w));
            double added = err == null ? Vol(b[0]) - v0 : double.NaN;
            var verdict = err ?? SolidValidator.Validate(b[0]).Summary();
            Add("concave corner chamfer 4/6/9", err == null && !verdict.Contains("error") && Math.Abs(added - 36) < 1e-6 && HasV(b[0], new Vector3d(0, 0, 9), 1e-5),
                $"added {added:0.######} vs 36; {verdict}");
        }
        {
            var b = Notched(); double v0 = Vol(b[0]); var w = new List<string>();
            var err = Run(() => Parametric3D.Kernel.Ops.VertexOp.Round(b, o, 6, w));
            double added = err == null ? Vol(b[0]) - v0 : double.NaN, expect = VertexRoundRemoved(6);
            var verdict = err ?? SolidValidator.Validate(b[0]).Summary();
            double sb = 6 / Math.Sqrt(2);
            Add("concave corner round R6 (adds the cube corner outside the sphere)", err == null && !verdict.Contains("error") && Math.Abs(added - expect) < 0.01 * expect && HasV(b[0], new Vector3d(sb, 0, 0), 0.06),
                $"added {added:0.######} vs {expect:0.######}; {verdict}");
        }
        // Inverted square pyramid pocket: its apex is a concave corner with four edges.
        {
            List<TSolid> Pocket()
            {
                var pyr = Pyramid(4, 20, 20);
                var flipped = Parametric3D.Kernel.Ops.TransformOp.Copy(pyr, Parametric3D.Kernel.Ops.TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitX, 180));
                var moved = Parametric3D.Kernel.Ops.TransformOp.Copy(flipped, Parametric3D.Kernel.Ops.TransformOp.Move(new Vector3d(0, 0, 30.5)));
                var b = new List<TSolid> { Box(-30, -30, -30, 60, 60, 60) }; var w = new List<string>();
                Parametric3D.Kernel.Ops.BooleanOp.Subtract(b, moved, w);
                return b;
            }
            var apex = new Vector3d(0, 0, 10.5);
            double L = Math.Sqrt(800), vPyr = 4 / 2.0 * 400 * Math.Sin(Math.PI / 2) * 20 / 3;
            var b = Pocket(); double v0 = Vol(b[0]); var w = new List<string>();
            var c = Parametric3D.Kernel.Ops.VertexOp.Find(b[0], apex);
            var err = Run(() => Parametric3D.Kernel.Ops.VertexOp.Chamfer(b, apex, new[] { 6.0 }, w));
            double added = err == null ? Vol(b[0]) - v0 : double.NaN, expect = vPyr * Math.Pow(6 / L, 3);
            var verdict = err ?? SolidValidator.Validate(b[0]).Summary();
            Add("concave 4-edge corner chamfer d6", err == null && c.Concave && c.Dirs.Length == 4 && !verdict.Contains("error") && Math.Abs(added - expect) < 1e-5 * vPyr,
                $"edges {c.Dirs.Length}, concave {c.Concave}; added {added:0.######} vs {expect:0.######}; {verdict}");
            b = Pocket(); v0 = Vol(b[0]);
            err = Run(() => Parametric3D.Kernel.Ops.VertexOp.Round(b, apex, 4, w));
            verdict = err ?? SolidValidator.Validate(b[0]).Summary();
            double addedR = err == null ? Vol(b[0]) - v0 : double.NaN;
            Add("concave 4-edge corner round R4", err == null && !verdict.Contains("error") && addedR > 0, $"added {addedR:0.####}; {verdict}");
        }
        // A saddle vertex (convex and concave edges together) is refused with a clear reason.
        {
            var b = Notched(); var w = new List<string>();
            var err = Run(() => Parametric3D.Kernel.Ops.VertexOp.Round(b, new Vector3d(0, 0, 30), 3, w));
            Add("saddle vertex refused", err != null && err.Contains("saddle"), err ?? "accepted");
        }
        // Through the feature tree and the document.
        {
            var p = new Part("concave"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
            var s = PolySketch(p, new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), (0, 0), (40, 0), (40, 40), (0, 40));
            var cut = new ExtrudeFeature { SketchId = s.Id, Cut = true }; cut.Side1.Distance = new ValueRef(30); cut.Side1.Flip = true; p.AddFeature(cut);
            p.Rebuild(null);
            double v0 = p.Volume;
            var f = new VertexRoundFeature { Vertex = Vector3d.Zero, Radius = new ValueRef(5) }; p.AddFeature(f); p.Rebuild(null);
            var (q, t) = DocumentFile.Load(DocumentFile.Save(p)); q.Rebuild(t);
            Add("concave round in the tree and the document", f.Error == null && p.Volume > v0 && Math.Abs(q.Volume - p.Volume) < 1e-9, $"{f.Error} {v0:0.###} → {p.Volume:0.######}, reloaded {q.Volume:0.######}");
        }
        Console.WriteLine(string.Join("\n", rows));
        Check("K09-VERTEX-CONCAVE", () => Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK")))));
    }
}
