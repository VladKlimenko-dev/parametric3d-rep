using System.Security.Cryptography;
using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

static partial class Program
{
    static string Fingerprint(TSolid s)
    {
        var m = Tessellator.Tessellate(s, facets: true);
        using var st = new MemoryStream();
        using (var w = new BinaryWriter(st, System.Text.Encoding.UTF8, true))
        {
            w.Write(s.Faces.Count()); foreach (var f in s.Faces) w.Write(f.Id);
            w.Write(s.Exact?.Solid.Polygons.Count ?? -1); w.Write((int)s.Guarantee);
            foreach (var v in m.Vertices) { w.Write(v.P.X); w.Write(v.P.Y); w.Write(v.P.Z); }
            foreach (int i in m.Indices) w.Write(i);
        }
        return Convert.ToHexString(SHA256.HashData(st.ToArray()))[..16];
    }

    // Stages and the verdict each policy must give when that stage throws.
    static readonly (string Stage, OperationStatus Strict, OperationStatus Approx, string StrictCode)[] Faults =
    {
        // KRN-01 (g.10): исключение при разбиении грани входа — сбой расчёта (face-tessellation-failed, InternalFailure), а не
        // неполный вход (face-missing, InvalidInput); такой результат не окончателен — следующий расчёт пробует снова.
        ("boolean.input.face", OperationStatus.Failed, OperationStatus.Failed, OperationCodes.FaceTessellationFailed),
        ("boolean.exact",      OperationStatus.Failed, OperationStatus.SucceededApproximate, OperationCodes.FallbackRefused),
        ("boolean.topology",   OperationStatus.Failed, OperationStatus.Failed, OperationCodes.FallbackRefused),
    };

    static void Contract()
    {
        // Baseline: the exact route succeeds with a typed result.
        // KRN-01 (P0): «не проверялось» — не «годно». Пустой отчёт и отчёт без нужного представления не дают годности.
        Check("KRN01-UNCHECKED-IS-NOT-VALID", () =>
        {
            var empty = new ValidationReport();
            Require(!empty.IsValid, "an empty report (nothing checked) is reported valid");
            Require(!empty.IsValidFor(Representation.BRep), "an unchecked representation is reported valid");
            var brepOnly = SolidValidator.ValidateBRep(Box());
            Require(brepOnly.IsValidFor(Representation.BRep), "a checked valid B-rep is not reported valid");
            Require(!brepOnly.IsValidFor(Representation.Mesh), "the mesh was not checked but is reported valid");
        });
        // KRN-01: у каждого успеха — уровень гарантии; объединение с пустым набором берёт уровень самого тела.
        Check("KRN01-UNION-INTO-EMPTY-HAS-GUARANTEE", () =>
        {
            var r = BooleanOp.Evaluate(new List<TSolid>(), Box(), BooleanOp.BooleanKind.Unite, OperationPolicy.Strict);
            Require(r.Status == OperationStatus.Succeeded && r.Guarantee == GuaranteeLevel.ExactAnalytic, $"{r.Status} {r.Guarantee}");
        });
        // KRN-01: пустой результат — штатный исход Empty; отказы различаются по исходу.
        Check("KRN01-EMPTY-AND-FAILURE-OUTCOMES", () =>
        {
            var e1 = BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(100, 0, 0), BooleanOp.BooleanKind.Intersect, OperationPolicy.Strict);
            Require(e1.Accepted && e1.Outcome == OperationOutcome.Empty && e1.Bodies.Count == 0, $"intersect disjoint: {e1.Outcome} {e1.Bodies.Count}");
            var e2 = BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(-10, -10, -10, 80, 60, 40), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            Require(e2.Accepted && e2.Outcome == OperationOutcome.Empty, $"subtract by enclosing: {e2.Outcome}");
            var s1 = BooleanOp.Evaluate(new List<TSolid> { Box() }, Box(10, 10, 10, 10, 10, 30), BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            Require(s1.Outcome == OperationOutcome.Success && s1.Bodies.Count == 1, $"pocket: {s1.Outcome}");
            Require(s1.EffectiveSettings != null && s1.RequestedGeometryPolicy == s1.EffectiveSettings.GeometryPolicy && s1.InputRevisions.Count == 2,
                    "the result does not record its settings, geometry policy and input revisions");
            Require(OperationResult.OutcomeOf(OperationCodes.FallbackRefused) == OperationOutcome.Unsupported
                 && OperationResult.OutcomeOf(OperationCodes.InputApproximate) == OperationOutcome.InvalidInput
                 && OperationResult.OutcomeOf(OperationCodes.FaceMissing) == OperationOutcome.InvalidInput
                 && OperationResult.OutcomeOf(OperationCodes.Exception) == OperationOutcome.InternalFailure
                 && OperationResult.OutcomeOf(OperationCodes.InvalidResult) == OperationOutcome.InternalFailure, "failure code mapping");
        });
        // KRN-01 (P0): допуск хорды ОТОБРАЖЕНИЯ не меняет геометрию CAD-операции. Воспроизводитель: брусок с наклонным
        // отверстием (общая аналитическая булева) − тор (только многогранный путь; до g.14 — сфера, теперь она точная). Без политики вторая операция
        // зависела от хорды (45481.32359997 против 45481.32359743 при 0.02 и 0.005 мм) с уровнем ExactPolyhedral.
        Check("KRN01-DISPLAY-INDEPENDENCE", () =>
        {
            double? firstVol = null; Parametric3D.Kernel.Exact.BigRational? polyVol = null;
            foreach (double chord in Environment.GetEnvironmentVariable("DIREV") == "1" ? new[] { 0.005, 0.02 } : new[] { 0.02, 0.005 })
            foreach (var gp in new[] { GeometryPolicy.AllowBoundedFaceting, GeometryPolicy.PreserveBRep })
            {
                using var _c = new KernelContext(KernelSettings.Capture() with { Chord = chord, GeometryPolicy = gp }).Enter();
                TSolid tilt; using (BooleanOp.FacetingScope()) tilt = TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(30, 20, -20), 5, 60), TransformOp.Rotate(new Vector3d(30, 20, 10), Vector3d.UnitY, 17));
                var r1 = BooleanOp.Evaluate(new List<TSolid> { Box() }, tilt, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                Require(r1.Outcome == OperationOutcome.Success && r1.Guarantee == GuaranteeLevel.ExactAnalytic, $"op1 {gp} {chord}: {r1.Describe()}");
                double v1 = AnalyticVolume.Of(r1.Bodies[0]) ?? double.NaN;
                firstVol ??= v1;
                Require(v1 == firstVol, $"analytic result depends on the display chord: {v1:R} vs {firstVol:R}");
                if (Environment.GetEnvironmentVariable("DIDBG") == "1") Console.WriteLine($"DIDBG chord {chord} {gp}: op1 facets {r1.Bodies[0].Faces.Sum(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs ? fs.Facets.Count : -1000)} tilt facets {tilt.Faces.Sum(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs ? fs.Facets.Count : -1000)} exactPolys {r1.Bodies[0].Exact?.Solid.Polygons.Count}");
                // резец строится как в детали — с гранением CAD (операции детали идут в BooleanOp.FacetingScope)
                TSolid torus; using (BooleanOp.FacetingScope()) torus = PrimitiveOp.Torus(new Vector3d(55, 35, 20), 8, 3);
                var r2 = BooleanOp.Evaluate(r1.Bodies.ToList(), torus, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                if (gp == GeometryPolicy.PreserveBRep)
                {
                    Require(r2.Outcome == OperationOutcome.Unsupported && r2.FailureCode == OperationCodes.GeometryDowngradeRefused, $"PreserveBRep op2 {chord}: {r2.Describe()}");
                    Require((AnalyticVolume.Of(r1.Bodies[0]) ?? double.NaN) == v1, "the input body changed after a refused operation");
                }
                else
                {
                    Require(r2.Outcome == OperationOutcome.Success && r2.Diagnostics.Any(d => d.Code == OperationCodes.GeometryDowngrade),
                            $"AllowBoundedFaceting op2 {chord}: the downgrade to facets is not reported: {r2.Describe()}");
                    // g.04 (01.12): многогранная операция гранится допуском CAD (ToleranceContext.FacetChord), а не хордой
                    // отображения — её точная многогранная форма одна и та же при хорде экрана 0.02 и 0.005
                    var vp = r2.Bodies[0].Exact!.Solid.VolumeMm3();
                    polyVol ??= vp;
                    if (Environment.GetEnvironmentVariable("DIDBG") == "1") Console.WriteLine($"DIDBG poly volume chord {chord}: {vp.ToDouble():R}");
                    Require(vp.Equals(polyVol.Value), $"polyhedral result depends on the display chord: {vp.ToDouble():R} vs {polyVol.Value.ToDouble():R}");
                }
            }
        });
        Check("K02-SUCCESS-TYPED", () =>
        {
            using var __poly = PolyhedralPath();   // проверка МНОГОГРАННОГО пути (сферы с g.14 — в точном маршруте)
            var b = new List<TSolid> { Box() }; var tool = PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8);
            var r = BooleanOp.Evaluate(b, tool, BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
            Require(r.Status == OperationStatus.Succeeded && !r.FallbackUsed && r.Guarantee == GuaranteeLevel.ExactPolyhedral, r.Describe());
            Require(r.Bodies.Count == 1 && r.Bodies[0].Guarantee == GuaranteeLevel.ExactPolyhedral, "guarantee on body");
            Require(b.Count == 1 && b[0] != r.Bodies[0], "Evaluate must not modify the caller list");
        });
        Check("K02-QUADRIC-ROUTE", () =>
        {
            bool prev = BooleanOp.ExactCylinderCuts; BooleanOp.ExactCylinderCuts = true;
            try
            {
                var r = BooleanOp.Evaluate(new List<TSolid> { Box() }, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 8, 24),
                                           BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                Require(r.Status == OperationStatus.Succeeded, r.Describe());
                Log(new { test = "K02-QUADRIC-ROUTE", guarantee = r.Guarantee.ToString() });
            }
            finally { BooleanOp.ExactCylinderCuts = prev; }
        });

        foreach (var (stage, strictWant, approxWant, strictCode) in Faults)
            foreach (var policy in new[] { OperationPolicy.Strict, OperationPolicy.AllowApproximate })
            foreach (var kind in new[] { BooleanOp.BooleanKind.Subtract, BooleanOp.BooleanKind.Unite })
            {
                var want = policy == OperationPolicy.Strict ? strictWant : approxWant;
                Check($"K02-FAULT {stage} {policy} {kind}", () =>
                {
            using var __poly = PolyhedralPath();   // проверка МНОГОГРАННОГО пути (его этапы, формы, тождества)
                    var body = Box(); var tool = kind == BooleanOp.BooleanKind.Subtract
                        ? PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8) : Box(30, 20, 10);
                    var list = new List<TSolid> { body };
                    string before = Fingerprint(body), toolBefore = Fingerprint(tool);
                    var warn = new List<string>();
                    OperationResult r;
                    using (FaultInjection.Arm(stage))
                        r = BooleanOp.Evaluate(list, tool, kind, policy, warn);
                    Log(new { stage, policy = policy.ToString(), kind = kind.ToString(), result = r.Describe() });
                    Require(r.Status == want, $"status {r.Status}, want {want}: {r.Describe()}");
                    Require(list.Count == 1 && ReferenceEquals(list[0], body), "caller list changed");
                    Require(Fingerprint(body) == before && Fingerprint(tool) == toolBefore, "operand mutated");
                    if (want == OperationStatus.Failed)
                    {
                        Require(r.Bodies.Count == 0 && r.FailedStage != null, "failed result must carry no bodies and a stage");
                        if (policy == OperationPolicy.Strict) Require(r.FailureCode == strictCode, $"code {r.FailureCode}, want {strictCode}");
                        // Legacy void API: same refusal, caller state untouched.
                        using (FaultInjection.Arm(stage))
                        {
                            using var p = OperationPolicyScope.Use(policy);
                            bool threw = false;
                            try { BooleanOp.Subtract(list, tool, warn); } catch (OperationFailedException) { threw = true; }
                            if (kind == BooleanOp.BooleanKind.Subtract) Require(threw, "legacy call did not throw");
                        }
                        Require(list.Count == 1 && ReferenceEquals(list[0], body) && Fingerprint(body) == before, "legacy call changed state");
                    }
                    else
                    {
                        Require(r.FallbackUsed && r.Guarantee == GuaranteeLevel.Approximate, "approximate result not marked");
                        Require(r.Bodies.All(x => x.Guarantee == GuaranteeLevel.Approximate), "bodies not marked approximate");
                        // A strict follow-up refuses to build on an approximate body.
                        var next = BooleanOp.Evaluate(r.Bodies, PrimitiveOp.Cylinder(new Vector3d(10, 10, -2), 2, 24),
                                                      BooleanOp.BooleanKind.Subtract, OperationPolicy.Strict);
                        Require(next.Status == OperationStatus.Failed && next.FailureCode == OperationCodes.InputApproximate, next.Describe());
                        var next2 = BooleanOp.Evaluate(r.Bodies, PrimitiveOp.Cylinder(new Vector3d(10, 10, -2), 2, 24),
                                                       BooleanOp.BooleanKind.Subtract, OperationPolicy.AllowApproximate);
                        Require(next2.Status == OperationStatus.SucceededApproximate && next2.Bodies[0].Guarantee == GuaranteeLevel.Approximate,
                                "approximation must propagate: " + next2.Describe());
                    }
                });
            }

        Check("K02-FAULT-INPUT-FACE-SKIP", () =>
        {
            // The fault hits a later face, not the first: still a refusal, not a body with a hole.
            using var __poly = PolyhedralPath();   // проверка МНОГОГРАННОГО пути (сферы с g.14 — в точном маршруте)
            foreach (int skip in new[] { 1, 3, 5 })
            {
                var body = Box(); string fp = Fingerprint(body);
                OperationResult r;
                using (FaultInjection.Arm("boolean.input.face", skip))
                    r = BooleanOp.Evaluate(new List<TSolid> { body }, PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8),
                                           BooleanOp.BooleanKind.Subtract, OperationPolicy.AllowApproximate);
                Require(r.Status == OperationStatus.Failed && r.FailureCode == OperationCodes.FaceTessellationFailed && r.Outcome == OperationOutcome.InternalFailure, $"skip {skip}: {r.Describe()}");
                Require(Fingerprint(body) == fp, "body mutated");
            }
        });

        Check("K02-FAULT-FINAL-MESH", () =>
        {
            var w = new List<string>();
            var b = Scenario("S-HOLE", w);
            MeshData m;
            using (FaultInjection.Arm("mesh.face", 2)) m = Tessellator.Tessellate(b[0]);
            Require(!m.Whole, "broken face not reported in mesh");
            var rep = SolidValidator.Validate(b[0], m);
            Require(!rep.IsValidFor(Representation.Mesh) && rep.IsValidFor(Representation.BRep), rep.ToString());
            Require(rep.Issues.Any(i => i.Code == "face-missing"), "face-missing not reported");
        });

        Check("K02-FAULT-EXACT-MESHING-KEEPS-OLD", () =>
        {
            // Exact meshing of a face fails: the body keeps its previous (consistent) facets, and the
            // resulting mesh is still closed — a derived representation is not silently damaged.
            var w = new List<string>();
            var b = new List<TSolid> { Box() };
            BooleanOp.DeferMeshing = true;
            try { BooleanOp.Subtract(b, PrimitiveOp.Sphere(new Vector3d(30, 20, 20), 8), w); } finally { BooleanOp.DeferMeshing = false; }
            using (FaultInjection.Arm("mesh.face", 1)) BooleanOp.MeshExactlySolid(b[0], w);
            var rep = SolidValidator.Validate(b[0]);
            Require(rep.IsValid, rep.ToString());
        });

        // Part level: a feature whose Boolean is refused rolls back; later features do not see it.
        Check("K02-PART-ROLLBACK", () =>
        {
            using var __poly = PolyhedralPath();   // проверка МНОГОГРАННОГО пути (его этапы, формы, тождества)
            var part = new Part("k02");
            TestGeometry.AddCube(part, 60);
            part.AddFeature(new FaultFeature("boolean.exact", 0));
            part.AddFeature(new FaultFeature(null, 1));
            part.Rebuild(null);
            var f = part.Features.OfType<FaultFeature>().ToList();
            Log(new { test = "K02-PART-ROLLBACK", e0 = f[0].Error, e1 = f[1].Error, part.Error, part.Volume, part.Integrity, part.Approximate });
            Require(f[0].Error != null && f[0].Error!.Contains(OperationCodes.FallbackRefused), "strict part must refuse the fallback");
            Require(f[1].Error == null, "next feature must build on the rolled-back body");
            var reference = new Part("k02ref"); TestGeometry.AddCube(reference, 60);
            reference.AddFeature(new FaultFeature(null, 1)); reference.Rebuild(null);
            Require(Math.Abs(part.Volume - reference.Volume) < 1e-6 && part.Volume < 216000 - 1,
                    $"rolled-back tree volume {part.Volume} != reference {reference.Volume}");
            Require(part.Integrity == null && !part.Approximate, "integrity");
        });
        Check("K02-PART-APPROXIMATE-EXPLICIT", () =>
        {
            using var __poly = PolyhedralPath();   // проверка МНОГОГРАННОГО пути (его этапы, формы, тождества)
            var part = new Part("k02a") { Policy = OperationPolicy.AllowApproximate };
            TestGeometry.AddCube(part, 60);
            part.AddFeature(new FaultFeature("boolean.exact", 0));
            part.Rebuild(null);
            var f = part.Features.OfType<FaultFeature>().Single();
            Require(f.Error == null && part.Approximate, $"explicit approximate policy should build, marked: err={f.Error} approx={part.Approximate}");
        });
        Check("K02-PART-MESH-INTEGRITY", () =>
        {
            var part = new Part("k02m");
            TestGeometry.AddCube(part, 60);
            part.AddFeature(new FaultFeature(null, 0));
            BuildResultWithMeshFault(part);
        });
        Check("K02-MERGE-KEEPS-BROKEN-FACES", () =>
        {
            var part = new Part("k02merge");
            TestGeometry.AddCube(part, 60);
            part.AddFeature(new AddBodyFeature());
            Part.BuildResult r;
            using (FaultInjection.Arm("mesh.face", 3)) r = part.Compute(null);
            Require(r.Bodies.Count == 2, $"bodies {r.Bodies.Count}");
            Require(r.Mesh != null && !r.Mesh.Whole, "merged mesh lost BrokenFaces");
            Require(r.IntegrityError != null, "integrity error not set");
        });
    }

    static double VolumeCut(Part p) => 0;

    static void BuildResultWithMeshFault(Part part)
    {
        Part.BuildResult r;
        using (FaultInjection.Arm("mesh.face", 1)) r = part.Compute(null);
        Require(r.IntegrityError != null, "mesh fault not reported as integrity error");
        part.Apply(r);
        Require(part.Error != null && part.Integrity != null, "part error not set");
    }

    sealed class FaultFeature(string? stage, int i) : Feature
    {
        public override void Execute(BuildContext c)
        {
            var tool = PrimitiveOp.Cylinder(new Vector3d(-20 + i * 12, 0, -40), 3, 80);
            if (stage == null) { BooleanOp.Subtract(c.Bodies, tool, c.Warnings); return; }
            using (FaultInjection.Arm(stage)) BooleanOp.Subtract(c.Bodies, tool, c.Warnings);
        }
    }
    sealed class AddBodyFeature : Feature
    {
        public override void Execute(BuildContext c) => c.Bodies.Add(Box(200, 0, 0, 10, 10, 10));
    }
}
