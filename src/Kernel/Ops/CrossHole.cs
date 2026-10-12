using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// ПОПЕРЕЧНОЕ ОТВЕРСТИЕ В ВАЛУ — первый случай, где отверстие встречает НЕ плоскость, а
/// цилиндрическую поверхность. Их стык — кривая четвёртой степени, и это тот самый случай,
/// ради которого мы измеряли погрешность хода кривой: она оказалась на уровне машинной
/// точности (10⁻¹³ мм), потому что каждая точка уточняется при обращении.
///
/// Здесь строится не всё тело, а то, что для него нужно: две кривые стыка (вход и выход) с
/// оценкой их погрешности. Строить по ним грани можно только тогда, когда известно, что кривые
/// годны, — поэтому оценка возвращается вместе с ними, а не прячется.
/// </summary>
public static class CrossHole
{
    /// <summary>
    /// ТЕЛО ВАЛА С ПОПЕРЕЧНЫМ ОТВЕРСТИЕМ. Грани: боковая поверхность вала с двумя вырезами по
    /// кривым стыка, два торца и стенка отверстия, ограниченная теми же кривыми.
    ///
    /// Новой математики здесь нет — вся трудность (кривая четвёртой степени) уже снята: её ход
    /// ведётся уточнением с машинной точностью. Осталась работа по топологии.
    ///
    /// ВНИМАНИЕ, ПОСТРОЕНИЕ ЕЩЁ НЕ ВЕРНО. Измерено на вале Ø40 длиной 60 с отверстием Ø16:
    /// объём выходит 69 953.5 при расчётном 67 519.7 — на 3.6% больше. Сами кривые стыка при
    /// этом точны (погрешность 3.5·10⁻¹³ мм), значит, дело в ГРАНЕНИИ построенных граней:
    /// боковая поверхность вала и стенка отверстия не обрезаются по этим кривым, а гранятся
    /// целиком.
    ///
    /// ИЗМЕРЕНО ДАЛЬШЕ, и это меняет оценку задачи. Построитель ТОЧНОЙ сетки
    /// (ExactFaceMesher.Mesh) эти грани ПРИНИМАЕТ: вал с двумя вырезами — 3036 треугольников,
    /// стенка отверстия — 96, торцы — по 69, ни одного отказа. То есть контуры из кривых
    /// пересечения он умеет, и переписывать его не нужно.
    ///
    /// Остаётся собрать тело из его сеток: при прямом сложении объём вышел 52 629.5 против
    /// расчётных 67 519.7, то есть расходится ориентация граней либо их учёт (у части граней
    /// обход выдаётся не в ту сторону, что ожидает сложение). Это уже частность сборки, а не
    /// нехватка построителя — и с неё стоит начинать следующий раз.
    ///
    /// Пока пользоваться этим построением нельзя; проверки покрывают только кривые (они верны),
    /// а не тело.
    /// </summary>
    public static TSolid? Cut(Vector3d shaftAt, Vector3d shaftDir, double shaftRadius, double shaftLength,
                              Vector3d holeAt, Vector3d holeDir, double holeRadius,
                              List<string>? warnings = null)
    {
        var rims = Rims(shaftAt, shaftDir, shaftRadius, holeAt, holeDir, holeRadius, warnings);
        if (rims.Count != 2)
        {
            warnings?.Add("CrossHole: the two rim curves were not found.");
            return null;
        }

        Vector3d sd = shaftDir.Normalized();
        Vector3d hd = holeDir.Normalized();

        var solid = new TSolid();
        var shell = new TShell { Solid = solid };
        solid.Shells.Add(shell);

        // Рёбра стыка: по одному на каждую кривую, замкнутые сами на себя.
        var rimEdges = new List<TEdge>();
        foreach (var rim in rims)
        {
            var v = new TVertex(rim.Curve.PointAt(rim.Curve.T0));
            var e = new TEdge { V0 = v, V1 = v, Curve = rim.Curve };

            // Погрешность кривой записывается в допуск ребра: она измерена, и прятать её нельзя.
            e.Tolerance = Math.Max(rim.Bound.Worst, Tol.Weld);
            rimEdges.Add(e);
        }

        TEdge lowEdge, highEdge;
        // Боковая поверхность вала: два выреза по кривым стыка.
        {
            var frame = Frame3.FromNormal(shaftAt, sd);
            var circle = new ArcCurve3(frame, shaftRadius, 0, 2 * Math.PI);
            var side = new TFace
            {
                Surface = new ExtrudedSurface(circle, sd * shaftLength),
                Tag = "cross:shaft"
            };

            var lowCircle = new ArcCurve3(frame, shaftRadius, 0, 2 * Math.PI);
            var highCircle = new ArcCurve3(Frame3.FromNormal(shaftAt + sd * shaftLength, sd), shaftRadius, 0, 2 * Math.PI);
            var vl = new TVertex(lowCircle.PointAt(0));
            var vh = new TVertex(highCircle.PointAt(0));

            // ПОСТАВКА 4: ТОРЦЕВЫЕ ОКРУЖНОСТИ — ОБЩИЕ РЁБРА с торцами (прежде у торцов были свои: 4 «ребра на краю»,
            // путь всегда отвергался проверкой — вскрыто трассой маршрутов отверстия, G8).
            lowEdge = new TEdge { V0 = vl, V1 = vl, Curve = lowCircle };
            highEdge = new TEdge { V0 = vh, V1 = vh, Curve = highCircle };
            var outer = side.AddLoop(true);
            outer.Add(lowEdge, false);
            outer.Add(highEdge, true);

            foreach (var e in rimEdges) side.AddLoop(false).Add(e, false);
            shell.Add(side);
        }

        // Торцы.
        foreach (var (at, flip) in new[] { (shaftAt, true), (shaftAt + sd * shaftLength, false) })
        {
            var frame = Frame3.FromNormal(at, sd);
            var cap = new TFace
            {
                Surface = new PlaneSurface(frame),
                Reversed = flip,
                Tag = flip ? "cross:cap-low" : "cross:cap-high"
            };
            // Обход навстречу боковой грани: нижний торец — назад по нижней окружности, верхний — вперёд по верхней.
            cap.AddLoop(true).Add(flip ? lowEdge : highEdge, flip);
            shell.Add(cap);
        }

        // Стенка отверстия: ограничена теми же кривыми стыка.
        {
            var frame = Frame3.FromNormal(holeAt, hd);
            var circle = new ArcCurve3(frame, holeRadius, 0, 2 * Math.PI);
            var wall = new TFace
            {
                Surface = new ExtrudedSurface(circle, hd * shaftRadius * 2),
                Reversed = true,
                Tag = "cross:hole"
            };

            // Обе кривые стыка боковая грань обходит вперёд — стенка обходит их навстречу (прежде первую — вперёд:
            // у общего ребра оба полуребра смотрели в одну сторону).
            var loop = wall.AddLoop(true);
            loop.Add(rimEdges[0], true);
            loop.Add(rimEdges[1], true);
            shell.Add(wall);
        }

        // ПОСТАВКА 4: грани гранятся ПО КОНТУРАМ (как в ExactCylinderCut): аналитическая поверхность, гранёная
        // целиком, вырезов не видит — сетка тела выходила незамкнутой (16 замечаний), хотя B-rep годно.
        var norm = TopologyConventions.Normalize(solid);
        if (solid.TopologyConvention == 0 && norm.Consistent && norm.Problems.Count == 0 && norm.Unanchored > 0)
        {
            TopologyConventions.SplitRegions(solid);
            solid.TopologyConvention = TopologyConventions.Version;
        }
        Export.StepImport.MeshFacesFromLoops(solid, warnings, "CrossHole");
        Export.StepImport.OrientShellPublic(solid);

        // Поставка 4: МНОГОГРАННАЯ ТОЧНАЯ ФОРМА — производная от готового B-rep (B-rep здесь истина): следующая
        // булева берёт её из тела, а не выводит заново, и проверка тела видит и её. Негодная — не кладётся.
        try
        {
            var exact = Exact.ExactAdapter.ToExact(solid, new Exact.PlaneRegistry(), warnings);
            if (exact.Solid.UncancelledSegments() == 0 && exact.Solid.VolumeMm3().Sign > 0) solid.AttachDerivedExact(exact);
        }
        catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { warnings?.Add("CrossHole: no exact polyhedral form (" + ex.Message + ")."); }

        warnings?.Add($"CrossHole: hole \u2300{2 * holeRadius:0.###} through a shaft \u2300{2 * shaftRadius:0.###}, " +
                      $"rim error up to {rims.Max(r => r.Bound.Worst):E2} mm; faces meshed from their loops.");
        return solid;
    }

    /// <summary>Кривая стыка и то, насколько она отходит от обеих поверхностей.</summary>
    public readonly struct Rim
    {
        public readonly IntersectionCurve3 Curve;
        public readonly CurveBound Bound;
        public Rim(IntersectionCurve3 curve, CurveBound bound) { Curve = curve; Bound = bound; }
    }

    /// <summary>
    /// Кривые стыка отверстия с валом: их две — вход и выход. Оси обеих поверхностей задаются
    /// точкой и направлением; отверстие должно быть уже вала, иначе стыка нет.
    /// </summary>
    public static List<Rim> Rims(Vector3d shaftAt, Vector3d shaftDir, double shaftRadius,
                                 Vector3d holeAt, Vector3d holeDir, double holeRadius,
                                 List<string>? warnings = null)
    {
        var res = new List<Rim>();

        if (holeRadius >= shaftRadius - Tol.Weld)
        {
            warnings?.Add("CrossHole: the hole must be narrower than the shaft.");
            return res;
        }

        Vector3d sd = shaftDir.Normalized();
        Vector3d hd = holeDir.Normalized();

        if (Math.Abs(Vector3d.Dot(sd, hd)) > 1 - 1e-9)
        {
            warnings?.Add("CrossHole: the hole runs along the shaft, there is no rim.");
            return res;
        }

        var shaft = new ExtrudedSurface(new ArcCurve3(Frame3.FromNormal(shaftAt, sd), shaftRadius, 0, 2 * Math.PI),
                                        sd * shaftRadius * 4);
        var hole = new ExtrudedSurface(new ArcCurve3(Frame3.FromNormal(holeAt, hd), holeRadius, 0, 2 * Math.PI),
                                       hd * shaftRadius * 4);

        // Затравка: обходим отверстие по его окружности и для каждой точки ищем, где она
        // встречает вал, — двигаясь вдоль оси отверстия. Обе стороны дают две кривые.
        foreach (int side in new[] { 1, -1 })
        {
            var seed = new List<Vector3d>();
            var frame = Frame3.FromNormal(holeAt, hd);

            for (int i = 0; i <= 48; i++)
            {
                double a = 2 * Math.PI * i / 48;
                Vector3d onHole = holeAt + (frame.U * Math.Cos(a) + frame.V * Math.Sin(a)) * holeRadius;

                // Ищем t, при котором точка (onHole + t·hd) лежит на валу.
                Vector3d rel = onHole - shaftAt;
                Vector3d perp = rel - sd * Vector3d.Dot(rel, sd);
                Vector3d step = hd - sd * Vector3d.Dot(hd, sd);

                double A = Vector3d.Dot(step, step);
                double B = 2 * Vector3d.Dot(perp, step);
                double C = Vector3d.Dot(perp, perp) - shaftRadius * shaftRadius;
                double disc = B * B - 4 * A * C;
                if (A <= 1e-18 || disc < 0) continue;

                double t = (-B + side * Math.Sqrt(disc)) / (2 * A);
                seed.Add(onHole + hd * t);
            }

            if (seed.Count < 3) continue;

            var built = CurveBoundEstimate.Build(shaft, hole, seed);
            if (built == null)
            {
                warnings?.Add("CrossHole: the rim curve did not converge.");
                continue;
            }

            res.Add(new Rim(built.Value.Curve, built.Value.Bound));
        }

        return res;
    }
}
