using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    // Поставка 2, часть 2: неизменяемая публикация, ревизии B-rep и состояния представлений (B-rep, Exact, mesh).
    static TSolid ExactBoxMinusCyl()
    {
        var b = new List<TSolid> { Box() }; var w = new List<string>();
        BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), 6, 24), w);
        return b[0];
    }
    static bool Throws(Action a)
    {
        try { a(); return false; } catch (PublishedBodyMutationException) { return true; }
    }

    static void Representations()
    {
        Check("G6-PUBLISH-FREEZES-FORM", () =>
        {
            var s = ExactBoxMinusCyl();
            var v = s.Vertices.First(); var e = s.Edges.First(); var f = s.Faces.First();
            // До публикации — правка разрешена.
            v.P = v.P; e.Curve = e.Curve; f.Tag = f.Tag;
            s.Publish();
            Require(s.IsPublished && s.Representations.Published, "not published");
            foreach (var (what, act) in new (string, Action)[]
            {
                ("TVertex.P", () => v.P += new Vector3d(1, 0, 0)), ("TEdge.V0", () => e.V0 = e.V1), ("TEdge.Curve", () => e.Curve = e.Curve),
                ("TFace.Surface", () => f.Surface = f.Surface), ("TFace.Reversed", () => f.Reversed = !f.Reversed),
                ("TFace.Tag", () => f.Tag = "x"), ("TSolid.Exact", () => s.Exact = null),
                ("BeginEdit", () => s.BeginEdit("test", EditKind.Layout)), ("TopologyHeal.Heal", () => TopologyHeal.Heal(s)),
                ("VertexSnap.Apply", () => VertexSnap.Apply(s)),
                // Списки топологии и полурёбра (TopoList): добавление и удаление тоже запрещены.
                ("TSolid.Shells", () => s.Shells.Clear()), ("TShell.Faces", () => s.Shells[0].Faces.Remove(f)),
                ("TFace.Loops", () => f.Loops.Clear()), ("TLoop.CoEdges", () => f.Loops[0].CoEdges.RemoveAt(0)),
                ("TEdge.CoEdges", () => e.CoEdges.Add(e.CoEdges[0])), ("TVertex.Edges", () => v.Edges.Clear()),
                ("TCoEdge.Reversed", () => f.Loops[0].CoEdges[0].Reversed = !f.Loops[0].CoEdges[0].Reversed),
                ("FacetedSurface.Facets", () => s.Faces.Select(x => x.Surface).OfType<Parametric3D.Kernel.Surfaces.FacetedSurface>().First().Facets.Clear()),
            })
                Require(Throws(act), what + " changed a published body");
            // Производные данные — можно (кэши, не форма).
            v.Tolerance = v.Tolerance; e.PolylineCache = e.PolylineCache;
            // Точная форма — готовая или ленивая (у аналитического тела строится по требованию).
            Require((s.Exact != null || s.LazyDerivedExact != null) && s.Vertices.First().P == v.P && s.Shells.Count == 1 && f.Loops.Count > 0 &&
                    SolidValidator.ValidateBRep(s).IsValid, "form changed by refused writes");
        });

        Check("G6-EXACT-STATE-BY-EDIT-KIND", () =>
        {
            using var __poly = PolyhedralPath();   // проверка МНОГОГРАННОГО пути (его этапы, формы, тождества)
            // Точная форма — истина тела после точной булевой: доводка B-rep (Derive) и раскладка (Layout)
            // её не старят, правка формы (Shape) — старит.
            var s = ExactBoxMinusCyl();
            var r0 = s.Representations;
            Require(r0.HasExact && r0.ExactCurrent && s.ExactAuthoritative, $"boolean result: {r0}");
            s.BeginEdit("derive", EditKind.Derive); Require(s.ExactIsCurrent, "authoritative exact staled by Derive");
            s.BeginEdit("layout", EditKind.Layout); Require(s.ExactIsCurrent, "exact staled by Layout");
            s.BeginEdit("shape", EditKind.Shape); Require(!s.ExactIsCurrent && s.Representations.BRepRevision == r0.BRepRevision + 3,
                $"shape edit did not stale exact: {s.Representations}");
            // Точная форма, производная от B-rep (подогнанный резец): стареет уже от доводки B-rep.
            var t = ExactBoxMinusCyl(); t.AttachDerivedExact(t.Exact!);
            Require(!t.ExactAuthoritative && t.ExactIsCurrent, "derived exact state");
            t.BeginEdit("derive", EditKind.Derive); Require(!t.ExactIsCurrent, "derived exact survived a B-rep edit");
            t.BeginEdit("layout2", EditKind.Layout); Require(!t.ExactIsCurrent, "stale exact became current again");
        });

        Check("G6-MESH-REVISION", () =>
        {
            // Видимая сетка — производное ревизии B-rep: после правки формы та же настройка сетки размечает заново.
            var s = ExactBoxMinusCyl(); var w = new List<string>();
            BooleanOp.MeshExactlySolid(s, w);
            var r1 = s.Representations; Require(r1.MeshCurrent && r1.MeshRevision == r1.BRepRevision, $"after meshing: {r1}");
            s.BeginEdit("shape", EditKind.Shape);
            Require(!s.Representations.MeshCurrent, "mesh still current after a shape edit");
            BooleanOp.MeshExactlySolid(s, w);
            var r2 = s.Representations; Require(r2.MeshCurrent && r2.MeshRevision == r2.BRepRevision, $"not remeshed: {r2}");
            // Опубликованное тело можно доразметить (производное), но не править.
            s.Publish(); s.BeginEditSafe();
        });

        Check("G6-CLONE-FAITHFUL", () =>
        {
            // Структурная копия: та же форма (отпечаток), годный B-rep, ни одной общей сущности, не опубликована,
            // правка копии не трогает оригинал. Состояние точной формы переносится.
            var p = RealisticPart(8001, 20, out _); p.Rebuild(null);
            foreach (var orig in new[] { ExactBoxMinusCyl(), p.Bodies[0] })
            {
                var c = TopologyClone.Deep(orig);
                // Отпечаток формы без номеров (у копии номера сущностей новые — Authoritative законно различается).
                Require(SolidFingerprint.Geometry(c) == SolidFingerprint.Geometry(orig), "geometry fingerprint differs");
                Require(c.Faces.Select(x => x.Tag).SequenceEqual(orig.Faces.Select(x => x.Tag)) &&
                        c.Edges.Count() == orig.Edges.Count() && c.Vertices.Count() == orig.Vertices.Count(), "topology or names differ");
                Require(SolidValidator.ValidateBRep(c).IsValid, "clone B-rep invalid: " + SolidValidator.ValidateBRep(c).Summary());
                var ids = new HashSet<uint>(orig.Faces.Select(x => x.Id).Concat(orig.Edges.Select(x => x.Id)).Concat(orig.Vertices.Select(x => x.Id)));
                Require(!c.Faces.Any(x => ids.Contains(x.Id)) && !c.Edges.Any(x => ids.Contains(x.Id)) && !c.Vertices.Any(x => ids.Contains(x.Id)), "shared entities");
                Require(!c.IsPublished && c.Representations.HasExact == orig.Representations.HasExact &&
                        c.ExactIsCurrent == orig.ExactIsCurrent, $"state not carried: {c.Representations} vs {orig.Representations}");
                var before = SolidFingerprint.Authoritative(orig);
                c.BeginEdit("test", EditKind.Shape); c.Vertices.First().P += new Vector3d(0.25, 0, 0);
                Require(SolidFingerprint.Authoritative(orig) == before, "editing the clone changed the original");
                Require(!c.ExactIsCurrent || !orig.Representations.HasExact, "clone exact stayed current after a shape edit");
            }
        });

        Check("G6-STEP-EXACT-EDGES-APPLIED", () =>
        {
            // Точные рёбра в STEP реально пишутся у настоящей детали. Прежде копия для записи пересобиралась из
            // треугольников, не проходила проверку B-rep, и экспорт молча писал исходные рёбра (0 из 6 записей).
            var p = RealisticPart(8001, 20, out _); p.Rebuild(null);
            var w = new List<string>(); var sw = new StringWriter();
            Parametric3D.Kernel.Export.StepExact.Write(sw, p.Bodies, "g6", w);
            var line = w.FirstOrDefault(x => x.Contains("written as exact intersection curves"));
            Log(new { test = "G6-STEP-EXACT-EDGES-APPLIED", warning = line });
            if (p.Bodies.All(b => b.Guarantee == GuaranteeLevel.ExactAnalytic))
            {
                // Аналитическое тело: рёбра точные изначально, заменять нечего — STEP пишет их точными сущностями напрямую
                // (по окружности на каждую дугу, ни одного сплайна).
                var txt = sw.ToString();
                int arcs = p.Bodies.Sum(b => b.Edges.Count(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3));
                int circles = System.Text.RegularExpressions.Regex.Matches(txt, @"CIRCLE\(").Count, splines = System.Text.RegularExpressions.Regex.Matches(txt, "B_SPLINE_CURVE").Count;
                Require(p.Bodies.All(b => b.Edges.All(e => e.Curve is Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3)) && circles >= arcs && splines == 0,
                        $"analytic body: {arcs} arcs, {circles} circles, {splines} splines in STEP");
            }
            else
                Require(line != null && int.Parse(line.Split(':')[1].Trim().Split(' ')[0]) > 0, "no exact edges written: " + string.Join(" | ", w));
            Require(p.Bodies.All(b => b.IsPublished), "export touched publication");
        });

        Check("G6-STALE-EXACT-NOT-USED", () =>
        {
            // Точная форма, производная от B-rep, после правки формы устаревает, и вход в точное ядро её не берёт.
            // Нарочно неверная форма (сплошной брусок у тела с отверстием): взятая из кэша, она дала бы 48 000 мм³.
            var s = ExactBoxMinusCyl();
            var wrong = ExactAdapter.ToExact(Box(), new PlaneRegistry(), new());
            s.AttachDerivedExact(wrong);
            double cachedV = ExactAdapter.ToExact(s, new PlaneRegistry(), new()).Solid.VolumeMm3().ToDouble();
            Require(Math.Abs(cachedV - 48000) < 1e-6, $"current derived exact should be used as is: {cachedV}");
            long before = ExactAdapter.StaleExactUses;
            s.BeginEdit("test", EditKind.Derive);
            double v = ExactAdapter.ToExact(s, new PlaneRegistry(), new()).Solid.VolumeMm3().ToDouble();
            Log(new { test = "G6-STALE-EXACT-NOT-USED", cachedV, rebuiltV = v });
            Require(ExactAdapter.StaleExactUses == before + 1, "stale exact not detected");
            Require(Math.Abs(v - 48000) > 100, $"stale exact form was used: {v}");
        });

        Check("G6-PUBLISHED-RESULT-STATES", () =>
        {
            var p = TreeVariant(2); p.Rebuild(null);
            foreach (var b in p.Bodies)
            {
                var r = b.Representations;
                Require(r.Published, "result body not published");
                Require(!r.HasExact || r.ExactCurrent, $"published body with a stale exact form: {r}, last edit {b.LastEdit}");
                Require(r.MeshCurrent, $"published body without a current mesh: {r}");
            }
        });
    }

    /// <summary>Правка опубликованного тела обязана бросать — вспомогательное для G6-MESH-REVISION.</summary>
    static void BeginEditSafe(this TSolid s) => Require(Throws(() => s.BeginEdit("after publish", EditKind.Shape)), "published body editable");
}
