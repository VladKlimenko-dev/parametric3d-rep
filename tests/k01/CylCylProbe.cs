using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    static void CylCylProbe()
    {
        if (Environment.GetEnvironmentVariable("P3D_DRDBG") == "1") DirectRound.Debug = m => Console.WriteLine("   [dr] " + m);
        TSolid Shaft() => TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 0, -50), 20, 100), TransformOp.Rotate(Vector3d.Zero, Vector3d.UnitY, 90));   // ось X, R20, x ∈ [−50, 50]
        void Case(string name, Func<List<TSolid>> make, Func<TEdge, bool> pick, double r)
        {
            var bl = make();
            var b = bl[0];
            var edges = b.Edges.Where(pick).ToList();
            Console.WriteLine($"{name}: base V={AnalyticVolume.Of(b) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(b)):R} edges picked {edges.Count} ({string.Join(",", edges.Select(e => e.Curve.GetType().Name))})");
            var w = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { FilletOp.RoundEdges(bl, edges.Select(EdgeRef.Of).ToList(), r, w); }
            catch (Exception e) { Console.WriteLine($"   EXCEPTION {e.GetType().Name}: {e.Message[..Math.Min(300, e.Message.Length)]}"); return; }
            finally { Console.WriteLine($"   estimate {FilletOp.EstimateBlendVolume(edges, r, r, true):G6}, last {FilletOp.LastEstimate}"); }
            var res = bl[0];
            var fac = GeometryAudit.Faceted(res);
            double v = AnalyticVolume.Of(res) ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(res));
            double vm = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(res));
            double? vx = null; try { vx = res.Exact?.Solid.VolumeMm3().ToDouble(); } catch { }
            Console.WriteLine($"   volumes: analytic-integral {AnalyticVolume.Of(res):R}, mesh {vm:R}, exact polyhedron {vx:R}");
            Console.WriteLine($"   rounded r={r}: V={v:R} guarantee {res.Guarantee} faceted {fac.Faces}/{fac.Edges} valid {SolidValidator.Validate(res).IsValid} {sw.ElapsedMilliseconds} ms; {string.Join(" | ", w.Take(3))}");
        }
        // C: усечённый конус (вращение трапеции) — верхняя кромка плоскость/конус, выпуклое скругление r1
        Case("cone frustum top rim", () =>
        {
            var prof = ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), new List<Parametric3D.Kernel.Curves.ICurve3> {
                new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(0, 0, 0), new Vector3d(20, 0, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(20, 0, 0), new Vector3d(12, 0, 20)),
                new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(12, 0, 20), new Vector3d(0, 0, 20)), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(0, 0, 20), new Vector3d(0, 0, 0)) });
            var t = RevolveOp.Build(prof, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
            TopologyConventions.Normalize(t);
            return new List<TSolid> { t };
        }, e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 && e.V0.P.Z > 19, 1);
        // A: бобышка R8 вдоль Z на валу R20 вдоль X — объединение; стык (седловая кривая), вогнутое скругление r3
        Case("boss r8 on shaft R20, junction round", () =>
        {
            var b = new List<TSolid> { Shaft() };
            BooleanOp.Unite(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, 0), 8, 40), new List<string>());
            return b;
        }, e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.V0.P.Z > 10, 3);
        // B: сквозное отверстие d8 поперёк вала — кромка отверстия на цилиндре (выпуклое скругление r1)
        Case("hole d8 across shaft R20, rim round", () =>
        {
            var b = new List<TSolid> { Shaft() };
            BooleanOp.Subtract(b, PrimitiveOp.Cylinder(new Vector3d(0, 0, -30), 4, 60), new List<string>());
            return b;
        }, e => e.Curve is not (Parametric3D.Kernel.Curves.LineCurve3 or Parametric3D.Kernel.Curves.ArcCurve3) && e.V0.P.Z > 10, 1);
    }
}
static partial class Program
{
    static void Krn06FallbackGuardChecks()
    {
        Check("KRN06-FALLBACK-ROUND-ESTIMATE-GUARD", () =>
        {
            // обходной путь (протянутый резец; кромка плоскость/конус) сверяется с независимой оценкой объёма полосы:
            // верный резец — принят (объём по Паппу), резец, построенный втрое больше (внесённая ошибка), — отказ с откатом
            var fails = new List<string>();
            TSolid Frustum()
            {
                var prof = ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitZ), new List<Parametric3D.Kernel.Curves.ICurve3> {
                    new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(0, 0, 0), new Vector3d(20, 0, 0)), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(20, 0, 0), new Vector3d(12, 0, 20)),
                    new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(12, 0, 20), new Vector3d(0, 0, 20)), new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(0, 0, 20), new Vector3d(0, 0, 0)) });
                var t = RevolveOp.Build(prof, new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 0, 2 * Math.PI);
                TopologyConventions.Normalize(t); return t;
            }
            // Папп: сечение полосы у кромки с внутренним углом θ (тангенс наклона стенки 8/20) — площадь и центроид
            double th = Math.PI / 2 + Math.Atan(8.0 / 20);       // угол между верхом и стенкой внутри материала
            double r = 1, A = r * r * (1 / Math.Tan(th / 2) - (Math.PI - th) / 2);
            foreach (double scale in new[] { 1.0, 3.0 })
            {
                var bl = new List<TSolid> { Frustum() };
                double v0 = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bl[0]));
                var e = bl[0].Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3 && x.V0.P.Z > 19);
                FilletOp.ToolScaleForTest = scale; DirectRound.OffForTest = true;   // проверяется обходной путь: кромку усечённого конуса теперь строит прямой построитель
                try
                {
                    FilletOp.RoundEdges(bl, new[] { EdgeRef.Of(e) }, r, new List<string>());
                    var est = FilletOp.LastEstimate;
                    if (scale != 1) fails.Add($"tool ×{scale}: accepted (changed {est?.Changed:G4}, estimate {est?.Estimate:G4})");
                    else if (est is not { } es || Math.Abs(es.Changed - 2 * Math.PI * 12 * A) > 0.15 * 2 * Math.PI * 12 * A)
                        fails.Add($"tool ×1: changed {est?.Changed:G6} vs ~{2 * Math.PI * 12 * A:G6} (Pappus, centroid at R12)");
                }
                catch (Parametric3D.Core.RebuildException ex)
                {
                    if (scale == 1) fails.Add("tool ×1 refused: " + ex.Message);
                    else if (!ex.Message.Contains("could not be trusted") || bl.Count != 1 || Math.Abs(Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bl[0])) - v0) > 1e-6 * v0)
                        fails.Add($"tool ×{scale}: refusal without the reason or without rollback: {ex.Message}");
                }
                finally { FilletOp.ToolScaleForTest = 0; DirectRound.OffForTest = false; }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
