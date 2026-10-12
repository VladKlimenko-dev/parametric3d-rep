using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;
using Parametric3D.Model;
using Parametric3D.Model.Features;

/// <summary>
/// KRN-08 (ворота g.09): история и устойчивые ссылки.
/// </summary>
static partial class Program
{
    static void Krn08Checks()
    {
        // 08.4: неоднозначная ссылка — исход Ambiguous с кандидатами (запрос выбора), а не похожая грань/кромка.
        Check("KRN08-VERTEX-REFERENCE", () =>
        {
            // ссылка на вершину — по имени (грани вокруг): правка высоты куба переносит вершину — скругление вершины идёт за
            // ней и равно сборке с нуля в новой точке; копии массива с тем же именем — по точке, иначе неоднозначность
            Parametric3D.Model.Part Make(double h, Vector3d at, int copies, out Parametric3D.Model.Features.ExtrudeFeature ex, out Parametric3D.Model.Features.VertexRoundFeature vr)
            {
                var p = new Parametric3D.Model.Part("vx"); ex = Parametric3D.Model.TestGeometry.AddCube(p, 100); ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(h);
                if (copies > 1) p.AddFeature(new Parametric3D.Model.Features.LinearPatternFeature("P1") { Step = new Vector3d(250, 0, 0), Count = new Parametric3D.Model.Features.ValueRef(copies) });
                vr = new Parametric3D.Model.Features.VertexRoundFeature("V1") { Vertex = at, Radius = new Parametric3D.Model.Features.ValueRef(10) };
                p.AddFeature(vr); p.Rebuild(null); return p;
            }
            var a = Make(100, new Vector3d(50, 50, 50), 1, out var exA, out var vrA);
            Require(vrA.Error == null && vrA.VertexKey != null, "first build: " + vrA.Error);
            exA.Side1.Distance = new Parametric3D.Model.Features.ValueRef(70); a.MarkDirtyFrom(exA); a.Rebuild(null);
            Require(vrA.Error == null, "after the height edit the vertex round lost its vertex: " + vrA.Error);
            var fresh = Make(70, new Vector3d(50, 50, 35), 1, out _, out _);
            Require(PartGeometry(a) == PartGeometry(fresh), $"edited {a.Volume:R} differs from a fresh build {fresh.Volume:R}");
            // копии массива: у каждой свои имена (KRN-07, 07.7) — вершина исходной копии после правки высоты находится по
            // имени (прежде — неоднозначность с двумя кандидатами), результат равен сборке с нуля
            var b = Make(100, new Vector3d(50, 50, 50), 2, out var exB, out var vrB);
            Require(vrB.Error == null, "pattern: " + vrB.Error);
            exB.Side1.Distance = new Parametric3D.Model.Features.ValueRef(70); b.MarkDirtyFrom(exB);
            var rr = b.Compute(null); b.Apply(rr);
            Require(rr.Outcomes.TryGetValue(vrB, out var o) && o == OperationOutcome.Success, $"pattern after the edit: {o} {vrB.Error}");
            var freshB = Make(70, new Vector3d(50, 50, 35), 2, out _, out _);
            Require(PartGeometry(b) == PartGeometry(freshB), $"pattern: edited {b.Volume:R} differs from a fresh build {freshB.Volume:R}");
            // документ хранит имя
            var (q, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(a)); q.Rebuild(null);
            Require(q.Features.OfType<Parametric3D.Model.Features.VertexRoundFeature>().Single().VertexKey == vrA.VertexKey && PartGeometry(q) == PartGeometry(a), "save/load lost the vertex name");
        });
        Check("KRN08-SKETCH-ON-FACE", () =>
        {
            // эскиз на верхней грани куба, вырез окружности r=10 на 20 вниз; правка высоты 100→70 — эскиз идёт за гранью:
            // объём = сборке с нуля (эскиз на z=35) = 70·10^4 − π·100·20; прежде вырез оставался на z=50 (почти в воздухе)
            Parametric3D.Model.Part Make(double h, bool attach, out Parametric3D.Model.Features.ExtrudeFeature ex, out Parametric3D.Model.Sketching.Sketch sk)
            {
                var p = new Parametric3D.Model.Part("skf"); ex = Parametric3D.Model.TestGeometry.AddCube(p, 100);
                ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(h); p.Rebuild(null);
                sk = new Parametric3D.Model.Sketching.Sketch("OnTop", new Parametric3D.Kernel.Frame3(new Vector3d(0, 0, h / 2), Vector3d.UnitX, Vector3d.UnitY));
                if (attach)
                {
                    var top = p.Bodies[0].Faces.Single(f => Parametric3D.Model.Features.SketchFaceRef.PlaneOf(f) is { } pl && pl.N.Z > 0.5 && Math.Abs(pl.P.Z - h / 2) < 1e-9);
                    Require(Parametric3D.Model.Features.SketchFaceRef.AttachById(sk, p.Bodies, top.Id), "attach by the picked face id");
                    Require(sk.BaseFaceTag != null && sk.BaseFaceTag.StartsWith("#"), "the top face has no stable name: " + top.Tag);
                }
                p.Add(sk);
                var c = sk.AddEntity(new Parametric3D.Model.Sketching.SkPoint(5, -3));
                sk.AddEntity(new Parametric3D.Model.Sketching.SkCircle((Parametric3D.Model.Sketching.SkPoint)c, 10));
                var cut = new Parametric3D.Model.Features.ExtrudeFeature("Pocket") { SketchId = sk.Id, Cut = true };
                cut.Side1.End = Parametric3D.Model.Features.EndType.Distance; cut.Side1.Distance = new Parametric3D.Model.Features.ValueRef(20); cut.Side1.Flip = true;
                p.AddFeature(cut); p.Rebuild(null);
                Require(cut.Error == null, "pocket: " + cut.Error);
                return p;
            }
            double Want(double h) => h * 1e4 - Math.PI * 100 * 20;
            var a = Make(100, true, out var exA, out var skA);
            Require(Math.Abs(a.Volume - Want(100)) < 1e-6 * Want(100), $"first build {a.Volume:R} vs {Want(100):R}");
            exA.Side1.Distance = new Parametric3D.Model.Features.ValueRef(70); a.MarkDirtyFrom(exA); a.Rebuild(null);
            Require(a.Features.All(f => f.Error == null), "after the edit: " + string.Join(";", a.Features.Select(f => f.Error)));
            Require(Math.Abs(skA.Plane.Origin.Z - 35) < 1e-9, $"the sketch stayed at z={skA.Plane.Origin.Z:R}");
            Require(Math.Abs(a.Volume - Want(70)) < 1e-6 * Want(70), $"edited {a.Volume:R} vs expected {Want(70):R}");
            var fresh = Make(70, false, out _, out _);
            Require(Math.Abs(a.Volume - fresh.Volume) < 1e-6 * fresh.Volume, $"edited {a.Volume:R} differs from a fresh build {fresh.Volume:R}");
            // документ хранит имя грани: после загрузки правка снова переносит эскиз
            var (q, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(a)); q.Rebuild(null);
            var qs = q.Sketches.Single(s => s.Name == "OnTop");
            Require(qs.BaseFaceTag == skA.BaseFaceTag, "save/load lost the face name");
            var qex = q.Features.OfType<Parametric3D.Model.Features.ExtrudeFeature>().First(f => !f.Cut);
            qex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(90); q.MarkDirtyFrom(qex); q.Rebuild(null);
            Require(Math.Abs(q.Volume - Want(90)) < 1e-6 * Want(90), $"loaded+edited {q.Volume:R} vs {Want(90):R}");
            // грань исчезла (имя не найдено) — явная ошибка, а не старая плоскость
            skA.BaseFaceTag = "#nosuch/cap+"; a.MarkDirtyFrom(exA); a.Rebuild(null);
            var pocket = a.Features.OfType<Parametric3D.Model.Features.ExtrudeFeature>().Single(f => f.Cut);
            Require(pocket.Error != null && pocket.Error.Contains("no longer exists"), "missing face must be reported: " + pocket.Error);
            Log(new { test = "KRN08-SKETCH-ON-FACE", edited = a.Volume, tag = skA.BaseFaceTag });
        });
        Check("KRN08-HOLE-ON-FACE", () =>
        {
            // глухое отверстие d10 на 20 от точки на верхней грани; правка высоты 100→70 — точка идёт за гранью (имя грани
            // запомнено при первом построении): объём = сборке с нуля с точкой на z=35; прежде отверстие начиналось на z=50
            Parametric3D.Model.Part Make(double h, double z, out Parametric3D.Model.Features.ExtrudeFeature ex, out Parametric3D.Model.Features.HoleFeature hole)
            {
                var p = new Parametric3D.Model.Part("hf"); ex = Parametric3D.Model.TestGeometry.AddCube(p, 100);
                ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(h);
                hole = new Parametric3D.Model.Features.HoleFeature("H1") { Direction = -Vector3d.UnitZ, ThroughAll = false,
                    Diameter = new Parametric3D.Model.Features.ValueRef(10), Depth = new Parametric3D.Model.Features.ValueRef(20) };
                hole.Points.Add(new Vector3d(10, -7, z)); p.AddFeature(hole); p.Rebuild(null);
                Require(hole.Error == null, "hole: " + hole.Error);
                return p;
            }
            double Want(double h) => h * 1e4 - Math.PI * 25 * 20;
            var a = Make(100, 50, out var exA, out var hA);
            Require(hA.PointFaces.Count == 1 && hA.PointFaces[0] != null && hA.PointFaces[0]!.StartsWith("#"), "the entry face was not named");
            Require(Math.Abs(a.Volume - Want(100)) < 1e-6 * Want(100), $"first build {a.Volume:R} vs {Want(100):R}");
            exA.Side1.Distance = new Parametric3D.Model.Features.ValueRef(70); a.MarkDirtyFrom(exA); a.Rebuild(null);
            Require(hA.Error == null, "after the edit: " + hA.Error);
            var fresh = Make(70, 35, out _, out _);
            Require(Math.Abs(a.Volume - fresh.Volume) < 1e-6 * fresh.Volume && Math.Abs(a.Volume - Want(70)) < 1e-6 * Want(70),
                $"edited {a.Volume:R}, fresh {fresh.Volume:R}, expected {Want(70):R}");
            var (q, _) = Parametric3D.Model.DocumentFile.Load(Parametric3D.Model.DocumentFile.Save(a)); q.Rebuild(null);
            var qh = q.Features.OfType<Parametric3D.Model.Features.HoleFeature>().Single();
            Require(qh.PointFaces.Count == 1 && qh.PointFaces[0] == hA.PointFaces[0], "save/load lost the entry face name");
            hA.PointFaces[0] = "#nosuch/cap+"; a.MarkDirtyFrom(exA); a.Rebuild(null);
            Require(hA.Error != null && hA.Error.Contains("no longer exists"), "missing entry face must be reported: " + hA.Error);
        });
        Check("KRN08-SKETCH-STATES", () =>
        {
            // 08.9: состояния эскиза и их действие на операцию. Полный, недоопределённый, избыточный-согласный — выдавливание
            // строится (объём по сторонам 10×6); противоречивый и выродившийся (особый) — явная ошибка операции, тела нет.
            // Прежде статус решения не проверялся: противоречивый прямоугольник выдавливался по компромиссу наименьших квадратов.
            var rows = new List<string>();
            void Case(string name, Action<Parametric3D.Model.Sketching.Sketch, Parametric3D.Model.Sketching.SkPoint, Parametric3D.Model.Sketching.SkPoint,
                      Parametric3D.Model.Sketching.SkLine, Parametric3D.Model.Sketching.SkLine, Parametric3D.Model.Sketching.SkLine> setup,
                      Parametric3D.Model.Sketching.SolveStatus want, bool builds, double? volume, Parametric3D.Model.Sketching.ISketchSolver? solver = null)
            {
                var (s, a, b, c, d, ab, bc, cd, da) = Rect(1);
                setup(s, a, b, ab, bc, cd);
                var p = new Parametric3D.Model.Part("st"); p.Add(s);
                var ex = new Parametric3D.Model.Features.ExtrudeFeature("E") { SketchId = s.Id };
                ex.Side1.End = Parametric3D.Model.Features.EndType.Distance; ex.Side1.Distance = new Parametric3D.Model.Features.ValueRef(5);
                p.AddFeature(ex); p.Rebuild(null, solver);
                bool built = ex.Error == null && p.Bodies.Count == 1;
                bool okVol = volume == null || (built && Math.Abs(p.Volume - volume.Value) < 1e-6 * volume.Value);
                bool ok = s.LastSolve == want && built == builds && okVol && (builds || (ex.Error ?? "").Contains("cannot be used"));
                rows.Add($"{(ok ? "OK" : "FAIL")} {name}: {s.LastSolve} (want {want}), built {built} (want {builds}), V={(p.Bodies.Count > 0 ? p.Volume : 0):R}, degenerate {s.DegenerateEntities.Count}, conflicts {s.Constraints.Count(x => x.Conflict) + s.Dimensions.Count(x => x.Conflict)}, driving {s.Dimensions.Count(x => x.IsDriving)}, lens {string.Join(",", s.Entities.OfType<Parametric3D.Model.Sketching.SkLine>().Select(l => l.Length.ToString("0.###")))}; {ex.Error}");
            }
            void Base(Parametric3D.Model.Sketching.Sketch s, Parametric3D.Model.Sketching.SkPoint a, Parametric3D.Model.Sketching.SkLine ab, Parametric3D.Model.Sketching.SkLine bc, Parametric3D.Model.Sketching.SkLine cd)
            {
                s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Fix, a);
                s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Horizontal, ab); s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Horizontal, cd);
                s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Vertical, bc);
                s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Vertical, s.Entities.OfType<Parametric3D.Model.Sketching.SkLine>().Last());
            }
            Case("fully", (s, a, b, ab, bc, cd) => { Base(s, a, ab, bc, cd);
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 10, a, ab.B).Locked = true;
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 6, bc.A, bc.B).Locked = true; },
                Parametric3D.Model.Sketching.SolveStatus.Ok, true, 300);
            Case("under", (s, a, b, ab, bc, cd) => { Base(s, a, ab, bc, cd);
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 10, a, ab.B).Locked = true; },
                Parametric3D.Model.Sketching.SolveStatus.Underconstrained, true, null);
            Case("redundant", (s, a, b, ab, bc, cd) => { Base(s, a, ab, bc, cd);
                s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Parallel, ab, cd);
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 10, a, ab.B).Locked = true;
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 6, bc.A, bc.B).Locked = true; },
                Parametric3D.Model.Sketching.SolveStatus.Overconstrained, true, 300);
            Case("conflicting", (s, a, b, ab, bc, cd) => { Base(s, a, ab, bc, cd);
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 10, a, ab.B).Locked = true;
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 6, bc.A, bc.B).Locked = true;
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 5, a, bc.B).Locked = true; },
                Parametric3D.Model.Sketching.SolveStatus.Inconsistent, false, null);
            Case("singular", (s, a, b, ab, bc, cd) => {
                s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Fix, a);
                s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Horizontal, ab);
                s.AddConstraint(Parametric3D.Model.Sketching.ConstraintType.Vertical, bc);
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 10, a, ab.B).Locked = true;
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 0, bc.A, bc.B).Locked = true; },
                Parametric3D.Model.Sketching.SolveStatus.Singular, false, null);
            // решатель не справился (DidNotConverge) — подставной решатель: численно решает, но сообщает неудачу
            Case("failed", (s, a, b, ab, bc, cd) => { Base(s, a, ab, bc, cd);
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 10, a, ab.B).Locked = true;
                s.AddDimension(Parametric3D.Model.Sketching.DimensionType.DistancePointPoint, 6, bc.A, bc.B).Locked = true; },
                Parametric3D.Model.Sketching.SolveStatus.DidNotConverge, false, null, new FailingSolver());
            Console.WriteLine(string.Join("\n", rows));
            Require(rows.All(r => r.StartsWith("OK")), string.Join("; ", rows.Where(r => !r.StartsWith("OK"))));
        });
        Check("KRN08-STALE-REVISIONS", () =>
        {
            // 08.6: результат, посчитанный до правки, не применяется после неё — какая бы правка ни была: операция (порядок
            // прихода результатов обратный), полоска расчёта, политика, значение параметра. После отказа деталь прежняя, а
            // новый расчёт даёт то же, что сборка с нуля. Без правки результат применяется.
            var fails = new List<string>();
            {   // без правки — применяется
                var (p, t, _, _, _) = ParamTree(); var r = p.Compute(t);
                if (!p.Apply(r)) fails.Add("an up-to-date result was rejected");
            }
            {   // обратный порядок прихода: новый применён, старый пришёл позже
                var (p, t, _, _, last) = ParamTree();
                last.R = new Parametric3D.Model.Features.ValueRef(5); p.MarkDirtyFrom(last); var rA = p.Compute(t);
                last.R = new Parametric3D.Model.Features.ValueRef(7); p.MarkDirtyFrom(last); var rB = p.Compute(t);
                if (!p.Apply(rB)) fails.Add("order: the newest result was rejected");
                string g = PartGeometry(p);
                if (p.Apply(rA)) fails.Add("order: the older result replaced the newer one");
                if (PartGeometry(p) != g) fails.Add("order: the part changed");
            }
            {   // полоска расчёта сдвинута прямым присваиванием (так её двигает дерево)
                var (p, t, _, _, last) = ParamTree();
                last.R = new Parametric3D.Model.Features.ValueRef(5); p.MarkDirtyFrom(last); var r = p.Compute(t);
                string before = PartGeometry(p);
                p.StageIndex = 1;
                if (p.Apply(r)) fails.Add("stage: a result of the full tree was applied after the stage moved to 1");
                if (PartGeometry(p) != before) fails.Add("stage: the part changed");
                p.Rebuild(t);
                var (q, tq, _, _, _) = ParamTree(); q.StageIndex = 1; q.Invalidate(); q.Rebuild(tq);
                if (PartGeometry(p) != PartGeometry(q)) fails.Add("stage: the rebuild differs from a fresh build at stage 1");
            }
            {   // политика
                var (p, t, _, _, last) = ParamTree();
                last.R = new Parametric3D.Model.Features.ValueRef(5); p.MarkDirtyFrom(last); var r = p.Compute(t);
                p.Policy = Parametric3D.Kernel.OperationPolicy.AllowApproximate;
                if (p.Apply(r)) fails.Add("policy: a result computed under Strict was applied after the policy changed");
            }
            {   // значение параметра изменено в таблице без уведомления детали
                var (p, t, _, _, _) = ParamTree(); var r = p.Compute(t);
                t.Find("H")!.Number = 50; t.Recalculate();
                if (p.Apply(r)) fails.Add("parameter: a result for H=60 was applied after H became 50");
                p.Rebuild(t);
                string fresh = FullBuild(ParamTree, tt => tt.Find("H")!.Number = 50);
                if (PartGeometry(p) != fresh) fails.Add("parameter: the rebuild differs from a fresh build with H=50");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN08-FACE-SELECTORS", () =>
        {
            // 08.3: грани по роли — уклон всех боковых граней куба селектором Faces(E:side) = уклону по точкам (K09);
            // правка размера куба — селектор находит новые грани; документ хранит селектор; пустой селектор — ошибка;
            // оболочка с открытым верхом селектором Faces(E:top) — объём по формуле
            var fails = new List<string>();
            double tan5 = Math.Tan(5 * Math.PI / 180);
            var p = new Part("fs"); var ex = TestGeometry.AddCube(p, 60);
            var df = new DraftFeature("D") { NeutralPoint = new Vector3d(0, 0, -30), NeutralNormal = Vector3d.UnitZ, Angle = new ValueRef(5) };
            df.OnSelectors.Add(new FaceSelector(FaceSelectorKind.Role, ex.Key, "side"));
            p.AddFeature(df); p.Rebuild(null);
            double want = Frustum(60, 60 - 2 * 60 * tan5, 60);
            if (df.Error != null || Math.Abs(p.Volume - want) > 2e-3 * want) fails.Add($"draft by selector: {df.Error} V={p.Volume:R} vs {want:R}");
            var q = new Part("fp"); TestGeometry.AddCube(q, 60);
            var dq = new DraftFeature("D") { NeutralPoint = new Vector3d(0, 0, -30), NeutralNormal = Vector3d.UnitZ, Angle = new ValueRef(5) };
            foreach (var pt in new[] { new Vector3d(30, 0, 0), new Vector3d(-30, 0, 0), new Vector3d(0, 30, 0), new Vector3d(0, -30, 0) }) dq.OnFaces.Add(pt);
            q.AddFeature(dq); q.Rebuild(null);
            if (Math.Abs(p.Volume - q.Volume) > 1e-6 * q.Volume || p.Bodies.Sum(b => b.Faces.Count()) != q.Bodies.Sum(b => b.Faces.Count())) fails.Add($"draft by selector differs from draft by points: {p.Volume:R} vs {q.Volume:R}");   // многогранный уклон: порядок граней меняет округление (1e-9)
            ex.Side1.Distance = new ValueRef(40); p.MarkDirtyFrom(ex); p.Rebuild(null);
            double want40 = Frustum(60 - 2 * 10 * tan5, 60 - 2 * 50 * tan5, 40);   // симметричный куб: z от −20 до 20, нейтраль на z = −30
            if (df.Error != null || Math.Abs(p.Volume - want40) > 2e-3 * want40) fails.Add($"after the height edit: {df.Error} V={p.Volume:R} vs {want40:R}");
            var (l, _) = DocumentFile.Load(DocumentFile.Save(p)); l.Rebuild(null);
            var ld = l.Features.OfType<DraftFeature>().Single();
            if (ld.OnSelectors.Count != 1 || ld.OnSelectors[0] != df.OnSelectors[0] || PartGeometry(l) != PartGeometry(p)) fails.Add("save/load lost the face selector");
            df.OnSelectors[0] = new FaceSelector(FaceSelectorKind.Role, ex.Key, "nosuchrole"); p.MarkDirtyFrom(df); p.Rebuild(null);
            if (df.Error == null || !df.Error.Contains("selects no faces")) fails.Add("an empty selector must be an error: " + df.Error);
            // оболочка: открыть верх куба селектором
            var s = new Part("sh"); var es = TestGeometry.AddCube(s, 60);
            var sh = new ShellFeature("S") { Thickness = new ValueRef(2) }; sh.OpenSelectors.Add(new FaceSelector(FaceSelectorKind.Role, es.Key, "top"));
            s.AddFeature(sh); s.Rebuild(null);
            double wantS = 60.0 * 60 * 60 - 56.0 * 56 * 58;
            if (sh.Error != null || Math.Abs(s.Volume - wantS) > 1e-6 * wantS) fails.Add($"shell open top by selector: {sh.Error} V={s.Volume:R} vs {wantS:R}");
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN08-AMBIGUOUS-AXIS-AND-MATE", () =>
        {
            // 08.4: ось по грани и сопряжение по имени грани — одноимённые грани с разной опорой (стенки двух отверстий одной
            // операции; копии массива — уже нет: у экземпляров свои имена, KRN-07) дают неоднозначность с кандидатами, а не
            // первую попавшуюся; одно отверстие — ссылка разрешается
            var p = new Parametric3D.Model.Part("ax"); Parametric3D.Model.TestGeometry.AddCube(p, 100);
            var h = new Parametric3D.Model.Features.HoleFeature("H1") { Direction = -Vector3d.UnitZ, ThroughAll = true, Diameter = new Parametric3D.Model.Features.ValueRef(10) };
            h.Points.Add(new Vector3d(0, 0, 50)); p.AddFeature(h);
            p.Rebuild(null);
            var wall = p.Bodies[0].Faces.First(f => f.Tag.StartsWith("#" + h.Key + "/") && GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface).Tag;
            var ax = new Parametric3D.Model.Features.DatumAxisFeature("A1") { By = Parametric3D.Model.Features.AxisBy.FaceAxis, FaceTag = wall };
            p.AddFeature(ax);
            var r1 = p.Compute(null); p.Apply(r1);
            Require(ax.Error == null, "one copy: the axis must resolve: " + ax.Error);
            var mate = new Parametric3D.Model.Mates.Mate(Parametric3D.Model.Mates.MateType.Cylinder, "M1") { PartFaceTag = wall };
            Require(mate.Resolve(p) && mate.Ambiguity == null, "one copy: the mate must resolve");
            h.Points.Add(new Vector3d(25, 25, 50)); p.MarkDirtyFrom(h);
            var r2 = p.Compute(null); p.Apply(r2);
            Require(r2.Outcomes.TryGetValue(ax, out var o2) && o2 == OperationOutcome.Ambiguous, $"two copies: axis outcome {o2}, expected Ambiguous; {ax.Error}");
            Require(r2.Ambiguities.TryGetValue(ax, out var a2) && a2.Candidates.Count == 2, "two copies: 2 candidates expected");
            Require(!mate.Resolve(p) && mate.Ambiguity != null, "two copies: the mate must report the ambiguity");
            Log(new { test = "KRN08-AMBIGUOUS-AXIS-AND-MATE", axis = ax.Error, mate = mate.Ambiguity });
        });
        Check("KRN08-AMBIGUOUS-REFERENCE", () =>
        {
            // (а) кромка скругления разделена надрезом, точка ссылки — в надрезе: два куска, ни один не держит точку
            var (p, rf, _) = RoundTree();
            var notch = new CutBox(25, 25, -3, 10, 10, 6);
            p.AddFeature(notch); p.MoveFeature(notch, p.Features.IndexOf(rf));
            var rr = p.Compute(null); p.Apply(rr);
            Require(rr.Outcomes.TryGetValue(rf, out var o1) && o1 == OperationOutcome.Ambiguous, $"round: outcome {o1}, expected Ambiguous; {rf.Error}");
            Require(rr.Ambiguities.TryGetValue(rf, out var a1) && a1.Candidates.Count >= 2, "round: candidates expected");
            Require(rf.Error != null && rf.Error.Contains("conflict"), "round: message must name the conflict: " + rf.Error);
            // (б) грань, открытая оболочкой, разделена пазом поперёк; точка ссылки — в пазу
            var q = new Part("k08amb"); TestGeometry.AddCube(q, 60);
            var sh = new Parametric3D.Model.Features.ShellFeature { Thickness = new Parametric3D.Model.Features.ValueRef(2) };
            sh.OpenFaces.Add(new Vector3d(0, 0, 30)); q.AddFeature(sh); q.Rebuild(null);
            Require(sh.Error == null && sh.OpenFaceNames[0] != null, "shell must bind the top face: " + sh.Error);
            double vOne = q.Volume;
            var slot = new CutBox(-40, -2, 25, 80, 4, 10);
            q.AddFeature(slot); q.MoveFeature(slot, q.Features.IndexOf(sh));
            var r2 = q.Compute(null); q.Apply(r2);
            Require(r2.Outcomes.TryGetValue(sh, out var o2) && o2 == OperationOutcome.Ambiguous, $"shell: outcome {o2}, expected Ambiguous; {sh.Error}");
            Require(r2.Ambiguities.TryGetValue(sh, out var a2) && a2.Candidates.Count == 2, $"shell: 2 candidates expected, got {a2?.Candidates.Count}");
            // разрешение: выбор пользователя — точка кандидата становится точкой ссылки
            sh.OpenFaces[0] = a2!.Candidates[0].At; q.MarkDirtyFrom(sh);
            var r3 = q.Compute(null); q.Apply(r3);
            Log(new { test = "KRN08-AMBIGUOUS-REFERENCE", round = rf.Error, shell = a2.Message, candidates = a2.Candidates.Select(c => c.Name), resolved = r3.Outcomes.GetValueOrDefault(sh).ToString(), vOne, v = q.Volume });
            // после выбора ссылка однозначна (сама оболочка паза может не строиться — это другой отказ, Unsupported)
            Require(r3.Outcomes.TryGetValue(sh, out var o3) && o3 != OperationOutcome.Ambiguous && !r3.Ambiguities.ContainsKey(sh), $"after the choice the reference must resolve: {o3} {sh.Error}");
            var names = sh.OpenFaceNames.ToList();
            var bodyBefore = Checkpoints(q).Where(c => c.After < q.Features.IndexOf(sh)).OrderByDescending(c => c.After).First().Bodies[0];
            var pts = FaceRefs.Resolve(bodyBefore, sh.OpenFaces, names, "check");
            Require(pts.Count == 1 && (pts[0] - a2.Candidates[0].At).Length < 1e-9, "the chosen candidate must be the resolved face point");
        });

        // 08.8: совпадение площади и первых моментов — не доказательство равенства областей. Карман 4×4 и карман 2×8 с общим
        // центром в верхней грани бруска: площадь и центр верхней грани (и дна кармана) одни и те же, области — разные.
        Check("KRN08-SAMEREGION-MOMENTS-NOT-PROOF", () =>
        {
            var w = new List<string>();
            TSolid Notched(double nx, double ny)
            {
                var pocket = Box(30 - nx / 2, 20 - ny / 2, 18, nx, ny, 2); foreach (var f in pocket.Faces) f.Tag = "pocket/" + f.Tag;
                var l = new List<TSolid> { Box() }; BooleanOp.Subtract(l, pocket, w);
                Require(l.Count == 1, "one body expected");
                return l[0];
            }
            var a = Notched(4, 4); var b = Notched(2, 8); var a2 = Notched(4, 4);
            static bool HasExact(TSolid t) => t.Exact != null || t.LazyDerivedExact != null;
            Require(HasExact(a) && HasExact(b), "exact form expected");
            string top = a.Faces.Where(f => f.Loops.Count == 2).Select(f => f.Tag).Single();
            var hb = FeatureHistory.Between(new List<TSolid> { a }, new List<TSolid> { b });
            var ha = FeatureHistory.Between(new List<TSolid> { a }, new List<TSolid> { a2 });
            Log(new { test = "KRN08-SAMEREGION-MOMENTS-NOT-PROOF", top, modified = hb.Modified, split = hb.Split, sameModified = ha.Modified });
            Require(hb.Modified.Contains(top), $"top face '{top}' (hole 4×4 → 2×8, same area and centroid) must be modified: " + string.Join(",", hb.Modified));
            Require(ha.Modified.Count == 0 && ha.Split.Count == 0 && ha.Generated.Count == 0 && ha.Deleted.Count == 0,
                "identical rebuild must report nothing: " + string.Join(",", ha.Modified.Concat(ha.Split)));
            // прямое сравнение точных областей: те же суммы, разные границы (точная форма — ленивая производная; на копии
            // она становится описанием тела, чтобы сравнивались именно многоугольники)
            TSolid WithExact(TSolid t)
            {
                var c = TopologyClone.Deep(t);
                if (c.Exact == null && t.LazyDerivedExact is { } lz)
                {
                    c.Exact = lz.Form.Value;
                    // многоугольники производной формы помечены номерами граней исходного тела (порядок граней копии — тот же)
                    var tags = c.Exact.Solid.Polygons.Select(q => q.Tag).Distinct().OrderBy(x => x).ToList(); var dst = c.Faces.ToList();
                    Require(tags.Count == dst.Count, $"derived form: {tags.Count} tags for {dst.Count} faces");
                    for (int i = 0; i < dst.Count; i++) dst[i].SourceTag = tags[i];
                }
                return c;
            }
            var ea = WithExact(a); var eb = WithExact(b); var ea2 = WithExact(a2);
            Require(ea.Exact != null && ea.Faces.All(f => f.SourceTag != 0), "exact polygons with source tags expected");
            var fa = ea.Faces.Where(f => f.Tag == top).ToList(); var fb = eb.Faces.Where(f => f.Tag == top).ToList();
            foreach (var (body, fs) in new[] { (ea, fa), (eb, fb) })
            {
                var R = body.Exact!.Solid.Registry;
                var zs = body.Exact.Solid.Polygons.Where(q => q.Tag == fs[0].SourceTag).SelectMany(q => q.Vertices(R).ToArray()).Select(v => R.ToMm(v).Z).Distinct().ToList();
                Require(zs.Count == 1 && Math.Abs(zs[0] - 20) < 1e-9, "top-face polygons must lie at z = 20: " + string.Join(",", zs));
            }
            if (Environment.GetEnvironmentVariable("K08DBG") == "1")
                foreach (var (nm, body, fs) in new[] { ("a", ea, fa), ("b", eb, fb) })
                {
                    var R = body.Exact!.Solid.Registry;
                    Console.WriteLine($"{nm}: top SourceTag {fs[0].SourceTag}; polygon tags {string.Join(",", body.Exact.Solid.Polygons.Select(q => q.Tag).Distinct())}; face ids {string.Join(",", body.Faces.Select(f => f.SourceTag))}");
                    foreach (var q in body.Exact.Solid.Polygons.Where(q => q.Tag == fs[0].SourceTag)) Console.WriteLine("   poly " + string.Join(" ", q.Vertices(R).ToArray().Select(v => { var m = R.ToMm(v); return $"({m.X:0.###},{m.Y:0.###},{m.Z:0.###})"; })));
                }
            var r = FeatureHistory.SameRegionExact(fa, fb);
            Require(r == false, $"SameRegionExact(4×4 hole, 2×8 hole) = {r?.ToString() ?? "null"}, expected false");
            var r2 = FeatureHistory.SameRegionExact(fa, ea2.Faces.Where(f => f.Tag == top).ToList());
            Require(r2 == true, $"same region must compare equal: {r2?.ToString() ?? "null"}");
        });
    }
}

/// <summary>KRN08-SKETCH-STATES: решатель, сообщающий неудачу (состояние «failed» эскиза).</summary>
sealed class FailingSolver : Parametric3D.Model.Sketching.ISketchSolver
{
    public Parametric3D.Model.Sketching.SolveStatus Solve(Parametric3D.Model.Sketching.Sketch sketch, Parametric3D.Model.Sketching.SkPoint? dragged, out int dof)
    {
        new Parametric3D.Model.Sketching.NumericSketchSolver().Solve(sketch, dragged, out dof);
        return Parametric3D.Model.Sketching.SolveStatus.DidNotConverge;
    }
}
