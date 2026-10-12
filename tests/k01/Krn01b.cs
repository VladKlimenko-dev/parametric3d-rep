using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void Krn01bChecks()
    {
        Check("KRN01-ACCURACY-AND-PROVENANCE-IN-RESULT", () =>
        {
            // 01.3: отчёт о точности — у всех операций (не только булевой); 01.5: карта происхождения — в результате операции
            var fails = new List<string>();
            {   // булева (слоистый маршрут): точность и происхождение
                var r = BooleanOp.Evaluate(new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }, PrimitiveOp.Cylinder(new Vector3d(20, 15, -5), 6, 30), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, new List<string>());
                if (r.Accuracy is not { Basis: "exact B-rep" }) fails.Add($"boolean: accuracy {r.Accuracy}");
                if (r.ProvenanceMap == null || r.ProvenanceMap.Faces.Count != r.Bodies[0].Faces.Count()) fails.Add($"boolean: provenance {(r.ProvenanceMap == null ? "missing" : r.ProvenanceMap.Faces.Count + " of " + r.Bodies[0].Faces.Count())}");
            }
            {   // скругление прямым построителем через контракт операций
                var b = Box(0, 0, 0, 40, 30, 20);
                var e = b.Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - 20) < 1e-9 && Math.Abs(x.V1.P.Z - 20) < 1e-9);
                var r = OperationContract.Run("Round", new List<TSolid> { b }, l => FilletOp.RoundEdges(l, new[] { EdgeRef.Of(e) }, 3, new List<string>()));
                if (!r.Accepted || r.Accuracy is not { Basis: "exact B-rep", Measured: { } m } || m > r.Accuracy.Budget) fails.Add($"round: {r.Outcome} accuracy {r.Accuracy}");
                if (r.ProvenanceMap == null) fails.Add("round: provenance missing");
            }
            {   // оболочка
                var r = OperationContract.Run("Shell", new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }, l => ShellOp.Hollow(l, 2, new List<Vector3d> { new(20, 15, 20) }, new List<string>()));
                if (!r.Accepted || r.Accuracy == null) fails.Add($"shell: {r.Outcome} accuracy {r.Accuracy}");
            }
            {   // лофт (многогранный): отклонение фасет измерено и в бюджете
                var a = ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), new List<Parametric3D.Kernel.Curves.ICurve3> {
                    new Parametric3D.Kernel.Curves.LineCurve3(new(-10, -10, 0), new(10, -10, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(10, -10, 0), new(10, 10, 0)),
                    new Parametric3D.Kernel.Curves.LineCurve3(new(10, 10, 0), new(-10, 10, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new(-10, 10, 0), new(-10, -10, 0)) });
                var c = ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), new List<Parametric3D.Kernel.Curves.ICurve3> {
                    new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(new Vector3d(0, 0, 30), Vector3d.UnitX, Vector3d.UnitY), 8, 0, 2 * Math.PI) });
                var r = OperationContract.Run("Loft", new List<TSolid>(), l => l.Add(LoftOp.Build(new[] { a, c })));
                if (!r.Accepted || r.Accuracy is not { Basis: "faceting (measured)", Measured: { } m } || m > r.Accuracy.Budget) fails.Add($"loft: {r.Outcome} accuracy {r.Accuracy}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
static partial class Program
{
    static void Krn09DisplayFaultChecks()
    {
        Check("KRN09-DISPLAY-FAULT-ISOLATED", () =>
        {
            // 09.2: сбой разметки сетки показа не повреждает B-rep: отпечаток тела и точный STEP те же; сетка со сбоем сообщает
            // неразмеченные грани (а не молча теряет их); следующая разметка без сбоя — целая и та же, что до сбоя (сбой не
            // остаётся в кэше); следующая CAD-операция даёт то же, что без сбоя
            var fails = new List<string>();
            TSolid Make() => BooleanOp.Evaluate(new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }, PrimitiveOp.Cylinder(new Vector3d(20, 15, -5), 6, 30), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, new List<string>()).Bodies[0];
            var b = Make();
            string fp0 = SolidFingerprint.Authoritative(b);
            string Step(TSolid s) { var sw = new StringWriter(); Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, "p", new List<string>()); var t = sw.ToString(); return string.Join("\n", t.Split('\n').Where(l => !l.Contains("FILE_NAME"))); }
            string step0 = Step(b);
            var md0 = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
            Parametric3D.Kernel.Tess.MeshData mdF;
            using (FaultInjection.Arm("mesh.face", 1)) mdF = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
            if (mdF.BrokenFaces.Count == 0) fails.Add("the injected display fault was not reported (no broken faces)");
            if (SolidFingerprint.Authoritative(b) != fp0) fails.Add("the B-rep changed after a display fault");
            if (Step(b) != step0) fails.Add("the exact STEP changed after a display fault");
            var md1 = Parametric3D.Kernel.Tess.Tessellator.Tessellate(b);
            if (md1.BrokenFaces.Count != 0 || md1.Indices.Length != md0.Indices.Length || !md1.Vertices.SequenceEqual(md0.Vertices)) fails.Add($"the next display mesh differs from the one before the fault (broken {md1.BrokenFaces.Count}, triangles {md1.Indices.Length / 3} vs {md0.Indices.Length / 3})");
            var tool = PrimitiveOp.Cylinder(new Vector3d(8, 8, -5), 3, 30);
            var after = BooleanOp.Evaluate(new List<TSolid> { b }, tool, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, new List<string>());
            var clean = BooleanOp.Evaluate(new List<TSolid> { Make() }, PrimitiveOp.Cylinder(new Vector3d(8, 8, -5), 3, 30), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict, new List<string>());
            if (!after.Accepted || !clean.Accepted || SolidFingerprint.Geometry(after.Bodies[0]) != SolidFingerprint.Geometry(clean.Bodies[0]))
                fails.Add($"the next CAD operation differs after a display fault ({after.Outcome}/{clean.Outcome})");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
