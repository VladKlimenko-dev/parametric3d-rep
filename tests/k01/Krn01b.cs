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
            {   // уклон (замена плоскости, конус): происхождение — каждая грань результата из своей грани, наклонённая — изменена
                foreach (var (name, make, probe) in new (string, Func<TSolid>, Vector3d)[] {
                    ("draft plane", () => Box(0, 0, 0, 40, 30, 20), new Vector3d(40, 15, 10)),
                    ("draft cylinder", () => { var bl = new List<TSolid> { Box(0, 0, 0, 40, 30, 20) }; BooleanOp.Unite(bl, PrimitiveOp.Cylinder(new Vector3d(20, 15, 20), 6, 10), new List<string>()); return bl[0]; }, new Vector3d(26, 15, 25)) })
                {
                    var b0 = make();
                    var r = OperationContract.Run("Draft", new List<TSolid> { b0 }, l => DraftOp.Apply(l, new[] { probe }, new Vector3d(0, 0, name.EndsWith("cylinder") ? 20 : 0), Vector3d.UnitZ, 4, new List<string>()));
                    var pm = r.ProvenanceMap;
                    if (!r.Accepted || pm == null) { fails.Add($"{name}: {r.Outcome}, provenance {(pm == null ? "missing" : "ok")}"); continue; }
                    int mod = pm.Faces.Count(x => x.Kind == FaceOrigin.Modified), all = r.Bodies[0].Faces.Count();
                    if (pm.Faces.Count != all || mod == 0 || mod == all || pm.Faces.Any(x => x.Source == null)) fails.Add($"{name}: provenance {pm.Faces.Count} of {all}, modified {mod}");
                }
            }
            {   // KRN-01 (01.5): прочие операции — происхождение по тождеству поверхностей (если построитель карту не пишет):
                // карта есть, покрывает все грани результата, источники — грани входа, есть сохранённые/изменённые и новые
                var cases = new (string Name, Func<TSolid> Make, Action<List<TSolid>> Op, bool NeedNew)[]
                {
                    ("shell with a hole", () => { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24), new List<string>()); return bl[0]; },
                        l => ShellOp.Hollow(l, 2, new List<Vector3d> { new(5, 5, 20) }, new List<string>()), false),
                    ("linear pattern", () => Box(0, 0, 0, 10, 10, 10), l => TransformOp.LinearPattern(l, new Vector3d(5, 0, 0), 3, new List<string>()), false),
                    ("mirror", () => Box(-3, 0, 0, 13, 10, 10), l => TransformOp.MirrorBody(l, Vector3d.Zero, Vector3d.UnitX, new List<string>()), false),
                    ("vertex round", () => Box(0, 0, 0, 40, 30, 20), l => VertexOp.Round(l, new Vector3d(40, 30, 20), 5, new List<string>()), true),
                    ("chamfer 2/3 (cutter path)", () => Box(0, 0, 0, 40, 30, 20), l =>
                    {
                        var e = l[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - 20) < 1e-9 && Math.Abs(x.V1.P.Z - 20) < 1e-9);
                        FilletOp.ChamferEdges(l, new[] { EdgeRef.Of(e) }, 2, 3, new List<string>());
                    }, true),
                };
                foreach (var (name, make, op, needNew) in cases)
                {
                    var b0 = make(); var inFaces = new HashSet<TFace>(b0.Faces, ReferenceEqualityComparer.Instance);
                    var r = OperationContract.Run(name, new List<TSolid> { b0 }, op);
                    var pm = r.ProvenanceMap;
                    if (!r.Accepted || pm == null) { fails.Add($"{name}: {r.Outcome}, provenance {(pm == null ? "missing" : "ok")}"); continue; }
                    int all = r.Bodies.Sum(b => b.Faces.Count());
                    int fromInput = pm.Faces.Count(x => x.Source != null), gen = pm.Faces.Count(x => x.Kind == FaceOrigin.Generated);
                    Log(new { test = "KRN01-PROVENANCE", name, op = pm.Operation, faces = all, mapped = pm.Faces.Count, fromInput, generated = gen, deleted = pm.Deleted.Count });
                    if (pm.Faces.Count != all) fails.Add($"{name}: provenance covers {pm.Faces.Count} of {all} faces");
                    if (pm.Faces.Any(x => x.Source != null && !inFaces.Contains(x.Source))) fails.Add($"{name}: a source is not an input face");
                    if (fromInput == 0) fails.Add($"{name}: no face traced to the input");
                    if (needNew && gen == 0) fails.Add($"{name}: no generated face");
                    if (pm.Faces.All(x => x.Kind == FaceOrigin.Kept)) fails.Add($"{name}: every face is 'kept' — the operation changed nothing?");
                    // массив, зеркало, оболочка сдвигом не создают граней без источника: копии и стенки полости — из граней входа
                    if (!needNew && gen > 0) fails.Add($"{name}: {gen} face(s) without a source (copies must trace to the input)");
                }
            }
            {   // особые маршруты отверстия (мимо контракта операций): карта у тела детали — стенка новая, прочие из граней входа
                foreach (var (name, build) in HoleContractCases().Where(c => c.Name is "quadric-through" or "exact-through (tilted)" or "cross-hole"))
                {
                    var (p, h) = build();
                    var pm = p.Bodies.Count == 1 ? p.Bodies[0].Provenance : null;
                    if (pm == null) { fails.Add($"hole {name}: provenance missing ({Trace(h)})"); continue; }
                    int all = p.Bodies[0].Faces.Count(), gen = pm.Faces.Count(x => x.Kind == FaceOrigin.Generated), src = pm.Faces.Count(x => x.Source != null);
                    if (pm.Faces.Count != all || gen == 0 || src == 0) fails.Add($"hole {name}: provenance {pm.Faces.Count} of {all}, generated {gen}, from input {src}");
                }
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
