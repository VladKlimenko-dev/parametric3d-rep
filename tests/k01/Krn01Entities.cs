using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;
using Parametric3D.Model.Params;

static partial class Program
{
    static void Krn01EntitiesChecks()
    {
        Check("KRN01-DIAGNOSTICS-NAME-ENTITIES", () =>
        {
            // 01.11: ОТКАЗ НАЗЫВАЕТ ЗАТРОНУТЫЕ СУЩНОСТИ устойчивыми именами (грань — имя «#ключ/роль», ребро — имена двух граней):
            // (1) скругление ребра куба радиусом больше куба — ребро; (2) PreserveBRep и гранёный результат — гранёные грани;
            // (3) операция ядра, давшая открытое тело (внесённая ошибка: удалена грань), — рёбра на краю.
            var fails = new List<string>();
            {
                var p = new Part("ent1"); var ex = TestGeometry.AddCube(p, 60); p.Rebuild(null);
                var e = CornerEdge(p, 30, 30);
                var rf = new RoundFeature("R") { Radius = new ValueRef(80) }; rf.Edges.Add(EdgeRef.Of(e)); p.AddFeature(rf);
                var r = p.Compute(null); p.Apply(r);
                var ents = r.ErrorEntities.GetValueOrDefault(rf);
                string want = EntityNames.Edge(e);
                if (r.Errors.GetValueOrDefault(rf) == null) fails.Add("round R80: no refusal");
                else if (ents == null || !ents.Contains(want)) fails.Add($"round R80: entities [{string.Join("; ", ents ?? Array.Empty<string>())}], want {want} ({r.Errors[rf]})");
                if (!want.Contains("#" + ex.Key + "/")) fails.Add($"edge name without the face names: {want}");
            }
            {
                using var _c = new KernelContext(KernelSettings.Capture() with { GeometryPolicy = GeometryPolicy.PreserveBRep }).Enter();
                var p = new Part("ent2"); TestGeometry.AddCube(p, 60); p.Rebuild(null);
                var f = p.AddFeature(new PolicyBypassFeature()); var r = p.Compute(null); p.Apply(r);
                var ents = r.ErrorEntities.GetValueOrDefault(f);
                if (r.Errors.GetValueOrDefault(f) == null || ents == null || ents.Count == 0) fails.Add($"PreserveBRep refusal names no entities ({r.Errors.GetValueOrDefault(f)})");
            }
            {
                var box = Box(0, 0, 0, 10, 10, 10);
                var res = OperationContract.Run("BreakForTest", new[] { box }, work =>
                {
                    var c = TopologyClone.Deep(work[0]);
                    var victim = c.Shells[0].Faces[0];
                    foreach (var ce in victim.Loops.SelectMany(l => l.CoEdges).ToList()) ce.Edge.CoEdges.Remove(ce);
                    c.Shells[0].Faces.Remove(victim);
                    work[0] = c;
                });
                var d = res.Diagnostics.FirstOrDefault(x => x.Code == OperationCodes.InvalidResult);
                if (d == null || d.Entities is not { Count: > 0 } es || !es.Any(x => x.StartsWith("edge["))) fails.Add($"invalid result names no edges: {res.Describe()} {d}");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
        });
    }
}
