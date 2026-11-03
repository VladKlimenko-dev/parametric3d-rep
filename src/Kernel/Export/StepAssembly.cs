using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>Жёсткое размещение: p ↦ O + X·p.x + Y·p.y + Z·p.z (оси — единичные, взаимно перпендикулярные).</summary>
public readonly record struct RigidPlacement(Vector3d O, Vector3d X, Vector3d Y, Vector3d Z)
{
    public static readonly RigidPlacement Identity = new(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ);
    public Vector3d Dir(Vector3d v) => X * v.X + Y * v.Y + Z * v.Z;
    public Vector3d Apply(Vector3d p) => O + Dir(p);
    /// <summary>this ∘ b: сначала b, затем this.</summary>
    public RigidPlacement Then(RigidPlacement inner) => new(Apply(inner.O), Dir(inner.X), Dir(inner.Y), Dir(inner.Z));
    public RigidPlacement Inverse()
    {
        // обратная к ортогональной — транспонированная
        var ix = new Vector3d(X.X, Y.X, Z.X); var iy = new Vector3d(X.Y, Y.Y, Z.Y); var iz = new Vector3d(X.Z, Y.Z, Z.Z);
        var r = new RigidPlacement(Vector3d.Zero, ix, iy, iz);
        return new RigidPlacement(-r.Dir(O), ix, iy, iz);
    }
    public static RigidPlacement Of(Frame3 f) => new(f.Origin, f.U, f.V, f.W);
    public bool IsIdentity => (O.Length < 1e-12) && (X - Vector3d.UnitX).Length < 1e-12 && (Y - Vector3d.UnitY).Length < 1e-12;
}

/// <summary>Вхождение детали в сборку файла: изделие, его тела (в координатах изделия), размещение в сборке, родитель.</summary>
public sealed class StepOccurrence
{
    public required string Product;
    public required int ProductDef;
    public required List<TSolid> Bodies;
    public required List<string?> BodyNames;
    public RigidPlacement Placement = RigidPlacement.Identity;
    /// <summary>Подсборка, в которую входит деталь (имя изделия родителя; null — корень).</summary>
    public string? Parent;
}

/// <summary>
/// k149 (замечание 03.11, 10:32): СБОРКА STEP — ДЕТАЛИ НА СВОИХ МЕСТАХ. Структура изделий AP203/AP214:
/// PRODUCT_DEFINITION детали и сборки; вхождение — NEXT_ASSEMBLY_USAGE_OCCURRENCE (родитель, ребёнок); его размещение —
/// CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(связь представлений с преобразованием, PRODUCT_DEFINITION_SHAPE вхождения), где
/// ITEM_DEFINED_TRANSFORMATION(система 1, система 2) переводит систему 1 в систему 2 (как OCCT STEPControl_ActorRead:
/// rep1 — представление ребёнка, иначе преобразование обращается). Тела изделия — из его представления формы и связанных с ним
/// (SHAPE_REPRESENTATION_RELATIONSHIP без преобразования); MAPPED_ITEM пока не разбирается.
/// Обход от корней (изделий, которые никуда не входят): размещение вхождения — произведение размещений по пути. Деталь,
/// входящая несколько раз, даёт несколько вхождений (тела — общие, размещения — свои).
/// </summary>
public static class StepAssembly
{
    public static List<StepOccurrence>? Read(string text, List<string>? warnings)
    {
        var parser = StepParser.Parse(text);
        var E = parser.Entities;
        static int R(string a) => int.TryParse(a.Trim().TrimStart('#'), out int id) ? id : -1;
        IEnumerable<int> Refs(string a) => StepParser.SplitArgs(a.Trim().TrimStart('(').TrimEnd(')')).Select(R).Where(x => x > 0);
        bool Is(int id, string type) => E.TryGetValue(id, out var e) && e.Type == type;

        if (!E.Values.Any(e => e.Type == "NEXT_ASSEMBLY_USAGE_OCCURRENCE")) return null;
        var solids = StepImport.ReadExactSolidsWithShells(parser, warnings, out var solidOfShell);
        if (solids.Count == 0) return null;

        // изделие: PRODUCT_DEFINITION → имя
        string NameOfPd(int pd)
        {
            if (!E.TryGetValue(pd, out var pdE) || pdE.Args.Count < 3) return $"#{pd}";
            if (!E.TryGetValue(R(pdE.Args[2]), out var pdf) || pdf.Args.Count < 3) return $"#{pd}";
            if (!E.TryGetValue(R(pdf.Args[2]), out var pr) || pr.Type != "PRODUCT" || pr.Args.Count < 2) return $"#{pd}";
            string n = StepImport.Unquote(pr.Args[1]); if (n.Length == 0) n = StepImport.Unquote(pr.Args[0]);
            return n.Length > 0 ? n : $"#{pd}";
        }
        // представления изделия (SDR) и связи представлений без преобразования
        var repsOfPd = new Dictionary<int, List<int>>(); var pdOfRep = new Dictionary<int, int>();
        foreach (var (_, e) in E)
        {
            if (e.Type != "SHAPE_DEFINITION_REPRESENTATION" || e.Args.Count < 2) continue;
            int pds = R(e.Args[0]), rep = R(e.Args[1]);
            if (!E.TryGetValue(pds, out var pdsE) || pdsE.Type != "PRODUCT_DEFINITION_SHAPE" || pdsE.Args.Count < 3) continue;
            int pd = R(pdsE.Args[2]);
            if (!E.TryGetValue(pd, out var pdE) || !pdE.Type.StartsWith("PRODUCT_DEFINITION")) continue;   // у вхождения definition — NAUO
            if (!repsOfPd.TryGetValue(pd, out var l)) repsOfPd[pd] = l = new();
            l.Add(rep); pdOfRep[rep] = pd;
        }
        var linked = new Dictionary<int, List<int>>();
        void Link(int a, int b) { if (!linked.TryGetValue(a, out var l)) linked[a] = l = new(); l.Add(b); }
        foreach (var (_, e) in E)
            if (e.Type == "SHAPE_REPRESENTATION_RELATIONSHIP" && e.Args.Count >= 4) { int a = R(e.Args[2]), b = R(e.Args[3]); if (a > 0 && b > 0) { Link(a, b); Link(b, a); } }
        HashSet<int> RepClosure(IEnumerable<int> reps)
        {
            var seen = new HashSet<int>(); var q = new Queue<int>(reps);
            while (q.Count > 0) { int r = q.Dequeue(); if (!seen.Add(r)) continue; if (linked.TryGetValue(r, out var l)) foreach (int x in l) q.Enqueue(x); }
            return seen;
        }
        // тела представлений
        var nameOfBrep = new Dictionary<int, string?>();
        List<(TSolid S, string? Name)> BodiesOfReps(IEnumerable<int> reps)
        {
            var res = new List<(TSolid, string?)>();
            foreach (int rep in reps)
            {
                if (!E.TryGetValue(rep, out var re) || re.Args.Count < 2) continue;
                foreach (int it in Refs(re.Args[1]))
                {
                    if (!E.TryGetValue(it, out var ie)) continue;
                    if (ie.Type is "MANIFOLD_SOLID_BREP" or "BREP_WITH_VOIDS" && ie.Args.Count >= 2 && solidOfShell.TryGetValue(R(ie.Args[1]), out var so) && solids.Contains(so))
                        res.Add((so, StepImport.Unquote(ie.Args[0]) is { Length: > 0 } bn ? bn : null));
                    else if (ie.Type == "SHELL_BASED_SURFACE_MODEL" && ie.Args.Count >= 2)
                        foreach (int sh in Refs(ie.Args[1])) if (solidOfShell.TryGetValue(sh, out var so2) && solids.Contains(so2)) res.Add((so2, null));
                }
            }
            return res;
        }
        // вхождения: родитель → (ребёнок, размещение ребёнка в родителе)
        var children = new Dictionary<int, List<(int Child, RigidPlacement P)>>();
        var isChild = new HashSet<int>();
        var nauoPds = new Dictionary<int, int>();   // NAUO → PRODUCT_DEFINITION_SHAPE вхождения
        foreach (var (id, e) in E)
            if (e.Type == "PRODUCT_DEFINITION_SHAPE" && e.Args.Count >= 3 && Is(R(e.Args[2]), "NEXT_ASSEMBLY_USAGE_OCCURRENCE")) nauoPds[R(e.Args[2])] = id;
        var cdsrOf = new Dictionary<int, int>();    // PDS вхождения → связь с преобразованием
        foreach (var (_, e) in E)
            if (e.Type == "CONTEXT_DEPENDENT_SHAPE_REPRESENTATION" && e.Args.Count >= 2) cdsrOf[R(e.Args[1])] = R(e.Args[0]);
        RigidPlacement? Transform(int rrId, int childPd)
        {
            if (!E.TryGetValue(rrId, out var rr) || rr.Type != "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION" || rr.Args.Count < 5) return null;
            if (!E.TryGetValue(R(rr.Args[4]), out var tr)) return null;
            RigidPlacement t;
            if (tr.Type == "ITEM_DEFINED_TRANSFORMATION" && tr.Args.Count >= 4)
            {
                var a1 = RigidPlacement.Of(parser.Placement(tr.Args[2])); var a2 = RigidPlacement.Of(parser.Placement(tr.Args[3]));
                t = a2.Then(a1.Inverse());
            }
            else if (tr.Type == "CARTESIAN_TRANSFORMATION_OPERATOR_3D" && tr.Args.Count >= 5)
            {
                Vector3d D(string a, Vector3d dflt) => a.Trim() == "$" ? dflt : parser.Direction(a).Normalized();
                var x = D(tr.Args[2], Vector3d.UnitX); var o = parser.Point(tr.Args[4]);
                var z = tr.Args.Count > 6 ? D(tr.Args[6], Vector3d.UnitZ) : Vector3d.UnitZ;
                x = (x - z * Vector3d.Dot(x, z)).Normalized();
                t = new RigidPlacement(o, x, Vector3d.Cross(z, x), z);
            }
            else return null;
            // rep1 — представление ребёнка; иначе преобразование — обратное
            int rep1 = R(rr.Args[2]);
            var childReps = repsOfPd.TryGetValue(childPd, out var cr) ? RepClosure(cr) : new HashSet<int>();
            return childReps.Contains(rep1) ? t : t.Inverse();
        }
        foreach (var (id, e) in E)
        {
            if (e.Type != "NEXT_ASSEMBLY_USAGE_OCCURRENCE" || e.Args.Count < 5) continue;
            int parent = R(e.Args[3]), child = R(e.Args[4]);
            if (parent <= 0 || child <= 0) continue;
            RigidPlacement place = RigidPlacement.Identity;
            if (nauoPds.TryGetValue(id, out int ipds) && cdsrOf.TryGetValue(ipds, out int rr))
            {
                try { if (Transform(rr, child) is { } t) place = t; else warnings?.Add($"STEP: placement of «{NameOfPd(child)}» not read (#{rr})."); }
                catch (Exception ex) { warnings?.Add($"STEP: placement of «{NameOfPd(child)}»: {ex.Message}"); }
            }
            if (!children.TryGetValue(parent, out var l)) children[parent] = l = new();
            l.Add((child, place)); isChild.Add(child);
        }
        // обход от корней
        var occ = new List<StepOccurrence>();
        var bodiesCache = new Dictionary<int, List<(TSolid S, string? Name)>>();
        List<(TSolid S, string? Name)> BodiesOf(int pd)
        {
            if (bodiesCache.TryGetValue(pd, out var b)) return b;
            b = repsOfPd.TryGetValue(pd, out var reps) ? BodiesOfReps(RepClosure(reps)) : new();
            // тела, которые принадлежат дочерним изделиям (через общие представления), у родителя не повторяются
            return bodiesCache[pd] = b;
        }
        void Walk(int pd, RigidPlacement w, string? parentName, int depth)
        {
            if (depth > 32) return;
            var own = BodiesOf(pd);
            bool hasKids = children.TryGetValue(pd, out var kids);
            if (own.Count > 0 && !(hasKids && kids!.Any(k => BodiesOf(k.Child).Any(cb => own.Any(o => ReferenceEquals(o.S, cb.S))))))
                occ.Add(new StepOccurrence { Product = NameOfPd(pd), ProductDef = pd, Bodies = own.Select(x => x.S).ToList(), BodyNames = own.Select(x => x.Name).ToList(), Placement = w, Parent = parentName });
            if (hasKids)
                foreach (var (c, p) in kids!) Walk(c, w.Then(p), depth == 0 ? null : NameOfPd(pd), depth + 1);
        }
        var roots = repsOfPd.Keys.Concat(children.Keys).Distinct().Where(pd => !isChild.Contains(pd)).ToList();
        foreach (int r in roots) Walk(r, RigidPlacement.Identity, null, 0);
        if (occ.Count == 0) return null;
        // тела файла, не попавшие ни в одно изделие, — отдельными вхождениями на месте
        var used = new HashSet<TSolid>(occ.SelectMany(o => o.Bodies), ReferenceEqualityComparer.Instance);
        int k = 0;
        foreach (var s in solids)
            if (!used.Contains(s)) occ.Add(new StepOccurrence { Product = $"Body {++k}", ProductDef = -1, Bodies = new() { s }, BodyNames = new() { null } });
        return occ;
    }
}
