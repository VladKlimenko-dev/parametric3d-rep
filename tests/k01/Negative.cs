using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    // Synthetic triangle meshes: closed tetrahedra and boxes in arbitrary placement.
    static MeshData Mesh(params (Vector3d[] P, int[] T)[] parts)
    {
        var mb = new MeshBuilder(); uint face = 1;
        foreach (var (P, T) in parts)
        {
            mb.BeginFace(face);
            int s = mb.VertexCount;
            foreach (var p in P) mb.AddVertex(p, Vector3d.UnitZ);
            for (int i = 0; i < T.Length; i += 3) mb.AddTriangle(s + T[i], s + T[i + 1], s + T[i + 2]);
            mb.EndFace(); face++;
        }
        var md = new MeshData(); mb.FillInto(md); return md;
    }
    static (Vector3d[], int[]) Tet(Vector3d o, double k = 1, bool inverted = false)
    {
        var P = new[] { o, o + new Vector3d(k, 0, 0), o + new Vector3d(0, k, 0), o + new Vector3d(0, 0, k) };
        var T = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
        if (inverted) for (int i = 0; i < T.Length; i += 3) (T[i + 1], T[i + 2]) = (T[i + 2], T[i + 1]);
        return (P, T);
    }
    static (Vector3d[], int[]) Cube(Vector3d o, double k, bool inverted = false)
    {
        var P = new Vector3d[8];
        for (int i = 0; i < 8; i++) P[i] = o + new Vector3d((i & 1) * k, ((i >> 1) & 1) * k, ((i >> 2) & 1) * k);
        var T = new[] { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4, 2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5 };
        if (inverted) for (int i = 0; i < T.Length; i += 3) (T[i + 1], T[i + 2]) = (T[i + 2], T[i + 1]);
        return (P, T);
    }
    static ValidationReport VM(MeshData m, bool selfx = false) =>
        SolidValidator.ValidateMesh(m, new ValidationOptions { CheckSelfIntersections = selfx });
    static void Expect(ValidationReport r, string code)
    {
        Require(!r.IsValid, "damaged representation accepted: " + r.Summary());
        Require(r.Issues.Any(i => i.Code == code && i.Severity == ValidationSeverity.Error),
            $"expected {code}, got: {string.Join(" | ", r.Issues.Select(i => i.Code))}");
    }

    static void Negative()
    {
        Check("K01-NEG-CONTROL-TET", () => Require(VM(Mesh(Tet(Vector3d.Zero))).IsValid, VM(Mesh(Tet(Vector3d.Zero))).ToString()));
        Check("K01-NEG-CONTROL-CUBE", () => { var r = VM(Mesh(Cube(Vector3d.Zero, 2)), true); Require(r.IsValid, r.ToString()); });
        Check("K01-NEG-MISSING-TRIANGLE", () =>
        {
            var (P, T) = Tet(Vector3d.Zero); Expect(VM(Mesh((P, T[..9]))), "open-edge");
        });
        Check("K01-NEG-FLIPPED-TRIANGLE", () =>
        {
            var (P, T) = Tet(Vector3d.Zero); (T[1], T[2]) = (T[2], T[1]); Expect(VM(Mesh((P, T))), "orientation");
        });
        Check("K01-NEG-NONMANIFOLD-EDGE", () =>
        {
            // Two tets glued along one edge plus a third sharing it: edge used 4+ times → nonmanifold.
            var a = Tet(Vector3d.Zero); var b = Tet(Vector3d.Zero); for (int i = 0; i < 4; i++) b.Item1[i] = new Vector3d(-b.Item1[i].X, -b.Item1[i].Y, b.Item1[i].Z);
            // b reflected: shares edge (0,0,0)-(0,0,1); reflection flips orientation, re-invert
            for (int i = 0; i < b.Item2.Length; i += 3) (b.Item2[i + 1], b.Item2[i + 2]) = (b.Item2[i + 2], b.Item2[i + 1]);
            var c = Tet(Vector3d.Zero); for (int i = 0; i < 4; i++) c.Item1[i] = new Vector3d(c.Item1[i].X, -c.Item1[i].Y, c.Item1[i].Z) + new Vector3d(0, 0, 0);
            for (int i = 0; i < c.Item2.Length; i += 3) (c.Item2[i + 1], c.Item2[i + 2]) = (c.Item2[i + 2], c.Item2[i + 1]);
            var r = VM(Mesh(a, b, c));
            Require(!r.IsValid, "accepted");
            Require(r.Issues.Any(i => i.Code is "nonmanifold-edge" or "vertex-link" or "open-edge"), string.Join(",", r.Issues.Select(i => i.Code)));
        });
        Check("K01-NEG-PINCH-VERTEX", () =>
        {
            // Two tets touching only at the origin: every edge is fine, the vertex is not.
            var a = Tet(Vector3d.Zero); var b = Tet(Vector3d.Zero, -1);
            for (int i = 0; i < b.Item2.Length; i += 3) (b.Item2[i + 1], b.Item2[i + 2]) = (b.Item2[i + 2], b.Item2[i + 1]);
            var r = VM(Mesh(a, b));
            Require(r.Metrics["mesh.openEdges"] == 0 && r.Metrics["mesh.sameDirectionEdges"] == 0, "edges should look clean: " + r);
            Expect(r, "vertex-pinch");
        });
        Check("K01-NEG-INSIDE-OUT", () => Expect(VM(Mesh(Tet(Vector3d.Zero, 1, inverted: true))), "inside-out"));
        Check("K01-POS-CAVITY", () =>
        {
            var r = VM(Mesh(Cube(Vector3d.Zero, 10), Cube(new Vector3d(3, 3, 3), 2, inverted: true)), true);
            Require(r.IsValid, r.ToString()); Require(r.Metrics["mesh.cavities"] == 1 && r.Metrics["mesh.components"] == 2, "cavity counted");
        });
        Check("K01-POS-TWO-COMPONENTS", () =>
        {
            var r = VM(Mesh(Cube(Vector3d.Zero, 2), Cube(new Vector3d(5, 0, 0), 2)), true); Require(r.IsValid, r.ToString());
        });
        Check("K01-NEG-ORPHAN-CAVITY", () => Expect(VM(Mesh(Cube(Vector3d.Zero, 2), Cube(new Vector3d(5, 0, 0), 2, inverted: true))), "orphan-cavity"));
        Check("K01-NEG-SELF-INTERSECTION", () =>
        {
            var r = VM(Mesh(Cube(Vector3d.Zero, 2), Cube(new Vector3d(0.7, 0.6, 0.55), 2)), true);
            Require(r.Metrics["mesh.openEdges"] == 0, "combinatorially clean");
            Expect(r, "self-intersection");
        });
        Check("K01-NEG-BROKEN-FACE", () =>
        {
            var m = Mesh(Tet(Vector3d.Zero)); m.BrokenFaces.Add((7, "injected")); Expect(VM(m), "face-missing");
        });
        Check("K01-NEG-BREP-MISSING-FACE", () =>
        {
            var s = Box(); var removed = s.Shells[0].Faces[0];
            foreach (var ce in removed.Loops.SelectMany(l => l.CoEdges)) ce.Edge.CoEdges.Remove(ce);
            s.Shells[0].Faces.RemoveAt(0);
            var r = SolidValidator.Validate(s, null, new ValidationOptions());
            Require(!r.IsValidFor(Representation.BRep), "brep accepted");
            Require(!r.IsValidFor(Representation.Mesh), "mesh accepted");
        });
        Check("K01-NEG-EXACT-DROPPED-POLYGON", () =>
        {
            using var __poly = PolyhedralPath();   // порча многогранной точной формы тела
            var w = new List<string>();
            var b = Scenario("S-HOLE", w);
            var ex = b[0].Exact!.Solid;
            Require(SolidValidator.ValidateExact(ex).IsValid, "control invalid");
            var damaged = new ExactSolid(ex.Registry, ex.Polygons.Skip(1).ToList());
            var r = SolidValidator.ValidateExact(damaged);
            Require(!r.IsValid, "dropped polygon accepted");
        });
        Check("K01-NEG-EXACT-DUPLICATED-POLYGON", () =>
        {
            using var __poly = PolyhedralPath();   // порча многогранной точной формы тела
            var w = new List<string>();
            var b = Scenario("S-HOLE", w);
            var ex = b[0].Exact!.Solid;
            var damaged = new ExactSolid(ex.Registry, ex.Polygons.Concat(new[] { ex.Polygons[0] }).ToList());
            Require(!SolidValidator.ValidateExact(damaged).IsValid, "duplicated polygon accepted");
        });
        Check("K01-NEEDLE-QUALITY", () =>
        {
            // The sphere needle: real area 1.8e-7 mm², angle ~3e-7°. Must not be "degenerate".
            var q = new TriangleQuality(new Vector3d(38.00022857142857, 19.930910034873676, 20),
                                        new Vector3d(38, 28.0016, 20), new Vector3d(38, 28, 20));
            Require(q.Area > 0 && q.MinAngle > 1e-9, $"min angle {q.MinAngle}");
        });
        Check("K01-WELD-KEEPS-CLOSURE", () =>
        {
            // A face whose triangulation contains a collinear (zero-area) triangle with three distinct
            // vertices: welding must not open the mesh. Square split with a midpoint on the diagonal.
            var mb = new MeshBuilder(); mb.BeginFace(1);
            var P = new[] { new Vector3d(0,0,0), new Vector3d(1,0,0), new Vector3d(1,1,0), new Vector3d(0,1,0), new Vector3d(0.5,0.5,0) };
            foreach (var p in P) mb.AddVertex(p, Vector3d.UnitZ);
            // (0,2,4) is collinear: 4 lies on diagonal 0-2.
            mb.AddTriangle(0, 1, 2); mb.AddTriangle(0, 2, 4); mb.AddTriangle(4, 2, 3); mb.AddTriangle(0, 4, 3);
            mb.EndFace();
            var md = new MeshData();
            var fill = typeof(MeshBuilder).GetMethod("FillInto")!; fill.Invoke(mb, new object[] { md, true, false });
            var r = SolidValidator.ValidateMesh(md, new ValidationOptions { Closed = false });
            // Open sheet: boundary edges are legal; check no interior edge lost its partner by
            // counting edges used once: a closed square boundary has exactly 4.
            Require(r.Metrics["mesh.zeroAreaTriangles"] == 0, "collinear triangle should be flipped away");
            var edges = new Dictionary<(int,int),int>();
            for (int i = 0; i < md.Indices.Length; i += 3) for (int j = 0; j < 3; j++) {
                int a = md.Indices[i + j], b = md.Indices[i + (j + 1) % 3]; var k = a < b ? (a, b) : (b, a); edges[k] = edges.GetValueOrDefault(k) + 1; }
            Require(edges.Count(e => e.Value == 1) == 4, $"boundary edges {edges.Count(e => e.Value == 1)}");
            Require(Math.Abs(MassProps.Area(md) - 1) < 1e-6, "area");
        });
    }
}
