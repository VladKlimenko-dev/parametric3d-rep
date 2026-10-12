using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    // Самопересечения по классам (SolidValidator.ClassifyPairs / TrianglePairs.Classify): на каждый класс —
    // пары, разобранные напрямую, и целые сетки через ValidateMesh. Отрицательные fixtures: прежняя проверка
    // (только несмежные пары, строгие знаки в double) не видела компланарных наложений, касаний, дубликатов и
    // складок через общую вершину.
    static Vector3d V(double x, double y, double z) => new(x, y, z);
    static PairClass Pair(Vector3d[] a, int[] ia, Vector3d[] b, int[] ib) => TrianglePairs.Classify(a, ia, b, ib);
    static void ExpectPair(string name, PairClass want, Vector3d[] a, int[] ia, Vector3d[] b, int[] ib) =>
        Check(name, () =>
        {
            var got = Pair(a, ia, b, ib);
            Require(got == want, $"expected {want}, got {got}");
            // Классификация симметрична: перестановка треугольников и циклический сдвиг вершин не меняют класса.
            var back = Pair(b, ib, a, ia);
            Require(back == want, $"swapped: expected {want}, got {back}");
            var rot = Pair(new[] { a[1], a[2], a[0] }, new[] { ia[1], ia[2], ia[0] }, b, ib);
            Require(rot == want, $"rotated: expected {want}, got {rot}");
        });

    static void SelfXChecks()
    {
        int[] I = { 0, 1, 2 }, J = { 3, 4, 5 };
        var T0 = new[] { V(0, 0, 0), V(4, 0, 0), V(0, 4, 0) };

        // ---- несмежные и разрешённая смежность
        ExpectPair("SX-DISJOINT-FAR", PairClass.Disjoint, T0, I, new[] { V(10, 0, 1), V(11, 0, 1), V(10, 1, 1) }, J);
        ExpectPair("SX-DISJOINT-ABOVE", PairClass.Disjoint, T0, I, new[] { V(1, 1, 1), V(2, 1, 1), V(1, 2, 2) }, J);
        ExpectPair("SX-DISJOINT-COPLANAR", PairClass.Disjoint, T0, I, new[] { V(5, 5, 0), V(6, 5, 0), V(5, 6, 0) }, J);
        ExpectPair("SX-ADJ-EDGE-BENT", PairClass.Adjacent, T0, I, new[] { V(4, 0, 0), V(0, 0, 0), V(2, -1, 3) }, new[] { 1, 0, 5 });
        ExpectPair("SX-ADJ-EDGE-FLAT", PairClass.Adjacent, T0, I, new[] { V(4, 0, 0), V(0, 0, 0), V(2, -3, 0) }, new[] { 1, 0, 5 });
        ExpectPair("SX-ADJ-VERTEX", PairClass.Adjacent, T0, I, new[] { V(0, 0, 0), V(-3, 0, 1), V(0, -3, 2) }, new[] { 0, 4, 5 });

        // ---- трансверсальное пересечение
        ExpectPair("SX-TRANSVERSAL", PairClass.Transversal, T0, I, new[] { V(1, 1, -1), V(1, 1, 1), V(3, -1, 0) }, J);
        // Складка через общую вершину: прежняя проверка такие пары пропускала целиком.
        ExpectPair("SX-TRANSVERSAL-SHARED-VERTEX", PairClass.Transversal, T0, I,
            new[] { V(0, 0, 0), V(3, 3, -1), V(3, 3, 1) }, new[] { 0, 4, 5 });

        // Вырожденное пересечение: отрезок пересечения идёт по диагонали одного и стороне другого (целые
        // координаты, кубы [0,2]³ и [1,3]³) — ни одно ребро не проходит строго внутри; прежде не находилось.
        ExpectPair("SX-TRANSVERSAL-DEGENERATE", PairClass.Transversal,
            new[] { V(2, 2, 0), V(2, 2, 2), V(2, 0, 2) }, I, new[] { V(1, 1, 1), V(3, 1, 1), V(1, 1, 3) }, J);

        // ---- компланарное наложение
        ExpectPair("SX-COPLANAR-OVERLAP", PairClass.CoplanarOverlap, T0, I, new[] { V(1, 1, 0), V(5, 1, 0), V(1, 5, 0) }, J);
        ExpectPair("SX-COPLANAR-CONTAINED", PairClass.CoplanarOverlap, T0, I, new[] { V(0.5, 0.5, 0), V(1, 0.5, 0), V(0.5, 1, 0) }, J);
        // Складка по общему ребру: третьи вершины по одну сторону ребра в общей плоскости.
        ExpectPair("SX-COPLANAR-FOLD-EDGE", PairClass.CoplanarOverlap, T0, I, new[] { V(4, 0, 0), V(0, 0, 0), V(1, 1, 0) }, new[] { 1, 0, 5 });

        // ---- T-стык и касание
        ExpectPair("SX-TJUNCTION-COPLANAR", PairClass.TJunction, T0, I, new[] { V(2, 0, 0), V(3, -2, 0), V(1, -2, 0) }, J);
        ExpectPair("SX-TOUCH-FACE", PairClass.TJunction, T0, I, new[] { V(1, 1, 0), V(2, 1, 3), V(1, 2, 3) }, J);
        // Ребро B проходит через внутреннюю точку (1,0,0) ребра A; внутренности не пересекаются.
        ExpectPair("SX-TOUCH-EDGE-EDGE", PairClass.TJunction, T0, I, new[] { V(1, -1, 1), V(1, 1, -1), V(1, -3, 0) }, J);

        // ---- дубликат
        ExpectPair("SX-DUPLICATE", PairClass.Duplicate, T0, I, new[] { V(4, 0, 0), V(0, 4, 0), V(0, 0, 0) }, J);
        ExpectPair("SX-DUPLICATE-FILM", PairClass.Duplicate, T0, I, new[] { V(0, 4, 0), V(4, 0, 0), V(0, 0, 0) }, J);
        ExpectPair("SX-DUPLICATE-SAME-INDICES", PairClass.Duplicate, T0, I, T0, I);

        // ---- точность предиката: точки строго на плоскости z = x
        Check("SX-EXACT-ORIENT", () =>
        {
            Vector3d a = V(0.1, 0.7, 0.1), b = V(0.3, 0.2, 0.3), c = V(0.9, 0.55, 0.9), d = V(0.123, 0.456, 0.123);
            Require(ExactPredicates.Orient3D(a, b, c, d) == 0, "coplanar points classified as off-plane");
            Require(ExactPredicates.Orient3D(a, b, c, d + V(0, 0, 1e-15)) != 0, "point off plane by 1e-15 classified as on");
            // Компланарное наложение на наклонной плоскости — в «неудобных» координатах.
            var s = new[] { a, b, c }; var t = new[] { (a + b) / 2 + V(0.01, 0.01, 0.01), b, c };
            var got = Pair(s, I, t, J);
            Require(got == PairClass.CoplanarOverlap, $"tilted coplanar overlap: {got}");
        });

        // ---- целые сетки
        var opts = new ValidationOptions { CheckSelfIntersections = true };
        ValidationReport VX(MeshData m) => SolidValidator.ValidateMesh(m, opts);
        double M(ValidationReport r, string k) => r.Metrics.TryGetValue(k, out var v) ? v : -1;
        Check("SX-MESH-CONTROL-CUBES", () =>
        {
            var r = VX(Mesh(Cube(V(0, 0, 0), 2), Cube(V(5, 0, 0), 2)));
            Require(r.IsValid && M(r, "geometry.selfIntersections") == 0 && M(r, "geometry.pairs.tJunction") == 0, r.ToString());
        });
        Check("SX-MESH-TRANSVERSAL", () =>
        {
            var r = VX(Mesh(Cube(V(0, 0, 0), 2), Cube(V(1, 1, 1), 2)));
            Expect(r, "self-intersection");
            Require(M(r, "geometry.pairs.transversal") > 0, r.ToString());
        });
        Check("SX-MESH-COPLANAR-OVERLAP", () =>
        {
            // Куб на кубе со сдвигом: верх нижнего и низ верхнего наложены (вершины разные) — прежде не виделось.
            var r = VX(Mesh(Cube(V(0, 0, 0), 2), Cube(V(1, 0.5, 2), 2)));
            Expect(r, "coplanar-overlap");
            Require(M(r, "geometry.pairs.transversal") == 0, "no transversal expected: " + r);
        });
        Check("SX-MESH-DUPLICATE", () =>
        {
            var r = VX(Mesh(Cube(V(0, 0, 0), 2), Cube(V(0, 0, 0), 2)));
            Expect(r, "duplicate-face");
        });
        Check("SX-MESH-TJUNCTION", () =>
        {
            // Тетраэдр стоит вершиной на верхней грани куба: касание без общей вершины.
            var tet = (new[] { V(1, 1, 2), V(0.5, 0.5, 3), V(1.5, 0.5, 3), V(1, 1.5, 3) }, new[] { 0, 1, 2, 0, 2, 3, 0, 3, 1, 1, 3, 2 });
            var r = VX(Mesh(Cube(V(0, 0, 0), 2), tet));
            Require(M(r, "geometry.pairs.tJunction") > 0, r.ToString());
            Require(r.Issues.Any(i => i.Code == "t-junction"), r.ToString());
            Require(M(r, "geometry.selfIntersections") == 0, "touch counted as intersection: " + r);
            Expect(r, "t-junction");
        });
    }
}
