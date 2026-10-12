using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    // ПОСТАВКА 4. КРИВАЯ ГРАНЬ ИЗ НЕСКОЛЬКИХ ОБЛАСТЕЙ делится на грани (TopologyConventions.SplitCurvedRegions) — по
    // связности треугольников разбиения. Прежде делились только плоские; у кривой оставалась одна грань с контурами всех
    // областей (STEP допускает у грани одну внешнюю границу, история считала куски по числу внешних колец).
    static TSolid Grooved(List<string> w)
    {
        var shaft = new List<TSolid> { PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60) };
        var ring = new List<TSolid> { PrimitiveOp.Cylinder(new Vector3d(0, 0, 28), 12, 4) };
        BooleanOp.Subtract(ring, PrimitiveOp.Cylinder(new Vector3d(0, 0, 27), 8, 6), w);
        BooleanOp.Subtract(shaft, ring[0], w);
        return shaft[0];
    }
    static TSolid Slotted(List<string> w)
    {
        var shaft = new List<TSolid> { PrimitiveOp.Cylinder(Vector3d.Zero, 10, 60) };
        BooleanOp.Subtract(shaft, Box(8, -2, -1, 4, 4, 62), w);
        BooleanOp.Subtract(shaft, Box(-12, -2, -1, 4, 4, 62), w);
        return shaft[0];
    }

    static void CurvedRegions()
    {
        // Пояса: проточка R8 высотой 4 на вале R10×60. Объём — по формуле.
        CurvedCase("G9-CURVED-REGIONS-BANDS", Grooved, Math.PI * 100 * 60 - Math.PI * (100 - 64) * 4, wallPieces: 2, loopsPerPiece: 2);
        // Не периодические области: два сквозных продольных паза 4×4 на x = ±(8…12). Объём сегментов вычисляется:
        // из круга R10 убирается часть полосы |y| ≤ 2 при |x| ≥ 8 с обеих сторон.
        double r = 10, x0 = 8, half = 2;
        // Площадь части круга при |y| ≤ 2, x ≥ 8: ∫_{-2}^{2} (sqrt(r² − y²) − 8) dy.
        double F(double y) => 0.5 * (y * Math.Sqrt(r * r - y * y) + r * r * Math.Asin(y / r)) - x0 * y;
        double cap = F(half) - F(-half);
        CurvedCase("G9-CURVED-REGIONS-SLOTS", Slotted, (Math.PI * r * r - 2 * cap) * 60, wallPieces: 2, loopsPerPiece: 1);
    }

    static void CurvedCase(string name, Func<List<string>, TSolid> build, double exact, int wallPieces, int loopsPerPiece)
    {
        Check(name, () =>
        {
            using var __poly = PolyhedralPath();   // предпосылка — многогранный вход
            var w = new List<string>();
            var b = build(w);
            BooleanOp.MeshExactlySolid(b, w);
            var v = SolidValidator.Validate(b);
            Require(v.IsValid, v.Summary());
            // Стенка вала — грани на цилиндре R10 (дно проточки R8 и стенки пазов — не она).
            var wall = b.Faces.Where(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface { Source: Parametric3D.Kernel.Surfaces.ExtrudedSurface { Base: Parametric3D.Kernel.Curves.ArcCurve3 { Radius: 10 } } }).ToList();
            int facesOn = b.Faces.Count();
            double vol = AnalyticVolume.Of(b) ?? double.NaN;
            // То же без деления кривых граней — для сравнения.
            int facesOff; double volOff;
            using (new KernelContext(KernelSettings.Capture() with { SplitCurvedRegions = false }).Enter())
            { var b2 = build(new List<string>()); facesOff = b2.Faces.Count(); volOff = AnalyticVolume.Of(b2) ?? double.NaN; }
            // Обмен STEP: грань с одной внешней границей; объём после чтения.
            var sw = new StringWriter();
            Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, name, new List<string>());
            var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(), new List<string>());
            var vrBack = back.Count == 1 ? SolidValidator.Validate(back[0]) : null;
            double? J(double x) => double.IsFinite(x) ? x : null;
            double vBackMesh = back.Count == 1 ? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(back[0])) : double.NaN;
            Log(new { test = name, facesOn, facesOff, wallPieces = wall.Count, loops = wall.Select(f => f.Loops.Count), volume = J(vol), volumeWithoutSplit = J(volOff),
                      exact, err = J(vol - exact), stepBack = back.Count, stepFaces = back.Count == 1 ? back[0].Faces.Count() : -1,
                      stepVerdict = vrBack?.Summary(), stepMeshVolume = J(vBackMesh) });
            Require(wall.Count == wallPieces, $"the wall is {wall.Count} face(s), expected {wallPieces}");
            Require(wall.All(f => f.Loops.Count == loopsPerPiece), "loops per piece: " + string.Join(",", wall.Select(f => f.Loops.Count)));
            Require(facesOn == facesOff + wallPieces - 1, $"faces {facesOn} with splitting vs {facesOff} without");
            // Деление не меняет объём (до порядка суммирования), а сам объём — по формуле.
            Require(Math.Abs(vol - volOff) < 1e-9 * exact, $"volume {vol} with splitting vs {volOff} without");
            Require(Math.Abs(vol - exact) < 1e-6 * exact, $"volume {vol} vs exact {exact}");
            Require(back.Count == 1 && vrBack!.IsValid && back[0].Faces.Count() == facesOn, $"STEP round trip: {back.Count} body(ies), {vrBack?.Summary()}, faces {(back.Count == 1 ? back[0].Faces.Count() : -1)} vs {facesOn}");
        });
    }

    /// <summary>
    /// Поставка 4: ушная триангуляция с мостами (Triangulator) — отверстия, мосты которых сходятся в одну вершину
    /// контура. Прямоугольник 100×60 и квадраты 10×10 друг над другом: луч из каждого бьёт в вертикальную правую
    /// сторону, мосты идут в её конец, и после первого моста у этой вершины дубликаты. Площадь треугольников — область
    /// без отверстий, вывернутых треугольников нет. Прежде (первый найденный экземпляр вершины) — перекрытие.
    /// </summary>
    static void TriangulatorBridges()
    {
        Check("G10-TRIANGULATOR-DEGENERATE-COLLINEAR", () =>
        {
            // Вырожденная раскладка (r22): прямоугольник 100×60, отверстия 10×7 в целых координатах — два столбца на одних
            // прямых: луч моста идёт вдоль сторон соседних отверстий, диагонали ушей проходят через их вершины.
            var outer = new List<Vector2d> { new(0, 0), new(100, 0), new(100, 60), new(0, 60) };
            var holes = new List<IReadOnlyList<Vector2d>>();
            foreach (double y in new[] { 5.0, 20, 35, 48 })
                foreach (double x in new[] { 60.0, 30 })
                    holes.Add(new List<Vector2d> { new(x, y), new(x, y + 7), new(x + 10, y + 7), new(x + 10, y) });
            var tri = Parametric3D.Kernel.Tess.Triangulator.Triangulate(outer, holes, out var pts);
            double area = 0; int flipped = 0;
            for (int i = 0; i + 2 < tri.Count; i += 3)
            {
                var a = pts[tri[i]]; var b = pts[tri[i + 1]]; var c = pts[tri[i + 2]];
                double s2 = ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
                area += Math.Abs(s2); if (s2 < -1e-12) flipped++;
            }
            Log(new { test = "G10-TRIANGULATOR-DEGENERATE-COLLINEAR", triangles = tri.Count / 3, area, expected = 5440.0, flipped });
            Require(flipped == 0 && Math.Abs(area - 5440) < 1e-9 * 5440, $"area {area} vs 5440, flipped {flipped}");
        });

        Check("G10-TRIANGULATOR-BRIDGE-DUPLICATES", () =>
        {
            var outer = new List<Vector2d> { new(0, 0), new(100, 0), new(100, 60), new(0, 60) };
            var holes = new List<IReadOnlyList<Vector2d>>();
            // Правый столбец: мосты всех четырёх — в угол (100, 60), у него дубликаты. Координаты общего положения (сдвиги на
            // иррациональные доли): без них в раскладке много точек на одной прямой — диагональ уха проходит через вершину
            // отверстия, и ушная триангуляция ошибается иначе (вырождение, KNOWN_LIMITATIONS обновления 22).
            int k = 0;
            foreach (double y in new[] { 5.0, 20, 35, 48 })
                foreach (var (x, dy) in new[] { (60.0, 0.0), (30.0, 3.0) })
                {
                    double ox = 0.1 * Math.Sqrt(2 + k), oy = 0.1 * Math.Sqrt(3 + 2 * k); k++;
                    double x0 = x + ox, y0 = y + dy + oy;
                    holes.Add(new List<Vector2d> { new(x0, y0), new(x0 + 0.013 * k, y0 + 7), new(x0 + 10, y0 + 7 + 0.017 * k), new(x0 + 10 - 0.011 * k, y0) });   // по часовой
                }
            var tri = Parametric3D.Kernel.Tess.Triangulator.Triangulate(outer, holes, out var pts);
            double area = 0; int flipped = 0;
            for (int i = 0; i + 2 < tri.Count; i += 3)
            {
                var a = pts[tri[i]]; var b = pts[tri[i + 1]]; var c = pts[tri[i + 2]];
                double s2 = ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
                area += Math.Abs(s2); if (s2 < -1e-12) flipped++;
            }
            static double Ar(IReadOnlyList<Vector2d> p) { double a = 0; for (int i = 0; i < p.Count; i++) { var u = p[i]; var v = p[(i + 1) % p.Count]; a += u.X * v.Y - v.X * u.Y; } return Math.Abs(a) / 2; }
            double expected = 100 * 60 - holes.Sum(Ar);
            Log(new { test = "G10-TRIANGULATOR-BRIDGE-DUPLICATES", triangles = tri.Count / 3, area, expected, flipped });
            Require(flipped == 0 && Math.Abs(area - expected) < 1e-9 * expected, $"area {area} vs {expected}, flipped {flipped}");
        });
    }
}
