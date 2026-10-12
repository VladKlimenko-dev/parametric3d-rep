using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

/// <summary>KRN-06 (06.9): прямое скругление плоскость/плоскость — объём по формуле, точный маршрут, годность.</summary>
static partial class Program
{
    static TEdge EdgeAt(TSolid b, Vector3d a, Vector3d c) =>
        b.Edges.First(e => ((e.V0.P - a).Length < 1e-6 && (e.V1.P - c).Length < 1e-6) || ((e.V0.P - c).Length < 1e-6 && (e.V1.P - a).Length < 1e-6));

    internal static List<(string Name, TSolid Body, List<EdgeRef> Edges, double R, double Expected)> DirectRoundCases()
    {
        var list = new List<(string, TSolid, List<EdgeRef>, double, double)>();
        double k = 1 - Math.PI / 4;
        {   // вертикальное ребро бруска
            var b = Box(0, 0, 0, 60, 40, 20);
            list.Add(("box vertical edge R5", b, new() { EdgeRef.Of(EdgeAt(b, new(60, 40, 0), new(60, 40, 20))) }, 5, 60 * 40 * 20 - k * 25 * 20));
        }
        {   // два непересекающихся ребра (верхнее по X и вертикальное в противоположном углу)
            var b = Box(0, 0, 0, 60, 40, 20);
            list.Add(("box two edges R3", b, new() { EdgeRef.Of(EdgeAt(b, new(0, 40, 20), new(60, 40, 20))), EdgeRef.Of(EdgeAt(b, new(0, 0, 0), new(0, 0, 20))) }, 3, 60 * 40 * 20 - k * 9 * 60 - k * 9 * 20));
        }
        {   // повёрнутый брусок
            var rot = TransformOp.Rotate(new Vector3d(5, 5, 5), new Vector3d(0.3, 0.7, 0.2).Normalized(), 37);
            var b0 = Box(0, 0, 0, 60, 40, 20); var b = TransformOp.Copy(b0, rot);
            list.Add(("rotated box edge R4", b, new() { EdgeRef.Of(EdgeAt(b, rot.Point(new(60, 40, 0)), rot.Point(new(60, 40, 20)))) }, 4, 60 * 40 * 20 - k * 16 * 20));
        }
        double m = 5.0 / 3 - Math.PI / 2;   // объём пересечения двух удалённых четвертей у стыка цепочки: r³(5/3 − π/2)
        {   // цепочка: два верхних ребра с общей вершиной (стык — эллипс в биссекторной плоскости)
            var b = Box(0, 0, 0, 60, 40, 20);
            list.Add(("box chain 2 top edges R4", b, new() { EdgeRef.Of(EdgeAt(b, new(0, 0, 20), new(60, 0, 20))), EdgeRef.Of(EdgeAt(b, new(60, 0, 20), new(60, 40, 20))) }, 4,
                60 * 40 * 20 - k * 16 * 60 - k * 16 * 40 + 64 * m));
        }
        {   // открытая цепочка из трёх верхних рёбер (два стыка)
            var b = Box(0, 0, 0, 60, 40, 20);
            list.Add(("box chain 3 top edges R3", b, new() { EdgeRef.Of(EdgeAt(b, new(0, 0, 20), new(60, 0, 20))), EdgeRef.Of(EdgeAt(b, new(60, 0, 20), new(60, 40, 20))), EdgeRef.Of(EdgeAt(b, new(60, 40, 20), new(0, 40, 20))) }, 3,
                60 * 40 * 20 - k * 9 * (60 + 40 + 60) + 2 * 27 * m));
        }
        {   // вогнутая цепочка: внутренний угол уступа (два вогнутых ребра, третье ребро вершины — выпуклое вертикальное).
            // Заполнения продолжаются за вершину до биссекторной плоскости: + 2·∫∫a dA = 2 r³(10 − 3π)/12
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Unite(bl, Box(0, 0, 20, 30, 20, 10), new List<string>());
            var b = bl[0];
            list.Add(("step concave chain R2", b, new() { EdgeRef.Of(EdgeAt(b, new(30, 0, 20), new(30, 20, 20))), EdgeRef.Of(EdgeAt(b, new(0, 20, 20), new(30, 20, 20))) }, 2,
                60 * 40 * 20 + 30 * 20 * 10 + k * 4 * (20 + 30) + 2 * 8 * (10 - 3 * Math.PI) / 12));
        }
        {   // замкнутая цепочка: все четыре верхних ребра (четыре стыка, торцов нет)
            var b = Box(0, 0, 0, 60, 40, 20);
            var c = new[] { new Vector3d(0, 0, 20), new Vector3d(60, 0, 20), new Vector3d(60, 40, 20), new Vector3d(0, 40, 20) };
            list.Add(("box closed top chain R3", b, Enumerable.Range(0, 4).Select(i => EdgeRef.Of(EdgeAt(b, c[i], c[(i + 1) % 4]))).ToList(), 3,
                60 * 40 * 20 - k * 9 * 200 + 4 * 27 * m));
        }
        double oct = 1 - Math.PI / 6;       // угловой куб r³ минус октант шара
        {   // угловой узел: три ребра одной вершины (сферический треугольник)
            var b = Box(0, 0, 0, 60, 40, 20);
            var V = new Vector3d(60, 40, 20);
            list.Add(("box corner 3 edges R4", b, new() { EdgeRef.Of(EdgeAt(b, new(0, 40, 20), V)), EdgeRef.Of(EdgeAt(b, new(60, 0, 20), V)), EdgeRef.Of(EdgeAt(b, new(60, 40, 0), V)) }, 4,
                60 * 40 * 20 - k * 16 * (56 + 36 + 16) - 64 * oct));
        }
        {   // вогнутый угловой узел: угол дна кармана (три вогнутых ребра); полость скругляется как выпуклый брусок
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, Box(10, 10, 10, 30, 20, 10), new List<string>());
            var b = bl[0]; var V = new Vector3d(10, 10, 10);
            list.Add(("pocket corner 3 concave edges R2", b, new() { EdgeRef.Of(EdgeAt(b, V, new(40, 10, 10))), EdgeRef.Of(EdgeAt(b, V, new(10, 30, 10))), EdgeRef.Of(EdgeAt(b, V, new(10, 10, 20))) }, 2,
                60 * 40 * 20 - 30 * 20 * 10 + k * 4 * (28 + 18 + 8) + 8 * oct));
        }
        {   // все 12 рёбер бруска: 8 углов
            var b = Box(0, 0, 0, 60, 40, 20);
            list.Add(("box all edges R3", b, b.Edges.Select(EdgeRef.Of).ToList(), 3, 60 * 40 * 20 - k * 9 * (480 - 24 * 3) - 8 * 27 * oct));
        }
        {   // все рёбра повёрнутого бруска
            var rot = TransformOp.Rotate(new Vector3d(1, -2, 3), new Vector3d(0.6, -0.3, 0.74).Normalized(), 29);
            var b = TransformOp.Copy(Box(0, 0, 0, 60, 40, 20), rot);
            list.Add(("rotated box all edges R2.5", b, b.Edges.Select(EdgeRef.Of).ToList(), 2.5, 60 * 40 * 20 - k * 6.25 * (480 - 24 * 2.5) - 8 * 15.625 * oct));
        }
        {   // цепочка на повёрнутом бруске
            var rot = TransformOp.Rotate(new Vector3d(-3, 2, 7), new Vector3d(-0.4, 0.5, 0.75).Normalized(), 71);
            var b = TransformOp.Copy(Box(0, 0, 0, 60, 40, 20), rot);
            list.Add(("rotated box chain R5", b, new() { EdgeRef.Of(EdgeAt(b, rot.Point(new(0, 40, 0)), rot.Point(new(0, 40, 20)))), EdgeRef.Of(EdgeAt(b, rot.Point(new(0, 40, 20)), rot.Point(new(60, 40, 20)))) }, 5,
                60 * 40 * 20 - k * 25 * 20 - k * 25 * 60 + 125 * m));
        }
        {   // вогнутое ребро: брусок + брусок сверху (уступ), скругление внутреннего угла
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Unite(bl, Box(0, 0, 20, 30, 40, 10), new List<string>());
            var b = bl[0];
            var e = EdgeAt(b, new(30, 0, 20), new(30, 40, 20));
            list.Add(("step concave edge R2", b, new() { EdgeRef.Of(e) }, 2, 60 * 40 * 20 + 30 * 40 * 10 + k * 4 * 40));
        }
        {   // клин: ребро под 60° (призма) — выпуклое непрямое
            var prof = new List<Parametric3D.Kernel.Curves.ICurve3>();
            var pts = new[] { new Vector3d(0, 0, 0), new Vector3d(40, 0, 0), new Vector3d(20, 20 * Math.Sqrt(3), 0) };
            for (int i = 0; i < 3; i++) prof.Add(new Parametric3D.Kernel.Curves.LineCurve3(pts[i], pts[(i + 1) % 3]));
            var b = ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), prof), new Vector3d(0, 0, 25));
            var e = EdgeAt(b, pts[2], pts[2] + new Vector3d(0, 0, 25));
            // угол 60°: снимаемая площадь сечения = r²(cot(α/2) − (π − α)/2), α = π/3
            double a = Math.PI / 3, area = 9 * (1 / Math.Tan(a / 2) - (Math.PI - a) / 2);
            list.Add(("prism 60° edge R3", b, new() { EdgeRef.Of(e) }, 3, 0.5 * 40 * 20 * Math.Sqrt(3) * 25 - area * 25));
        }
        {   // цепочка на призме: вертикальное ребро 60° + верхнее наклонное — сопряжённые полудиаметры эллипса стыка не
            // ортогональны; объём формулой не задан (NaN) — проверяется, что стык лежит на обоих цилиндрах
            var prof = new List<Parametric3D.Kernel.Curves.ICurve3>();
            var pts = new[] { new Vector3d(0, 0, 0), new Vector3d(40, 0, 0), new Vector3d(20, 20 * Math.Sqrt(3), 0) };
            for (int i = 0; i < 3; i++) prof.Add(new Parametric3D.Kernel.Curves.LineCurve3(pts[i], pts[(i + 1) % 3]));
            // наклонное выдавливание вдоль биссектрисы угла при (40,0): грани стыка симметричны, двугранные углы не прямые
            var top = new Vector3d(-5 * Math.Sqrt(3) / 2, 2.5, 25);
            var b = ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), prof), top);
            list.Add(("oblique prism chain R3", b, new() { EdgeRef.Of(EdgeAt(b, pts[0] + top, pts[1] + top)), EdgeRef.Of(EdgeAt(b, pts[1] + top, pts[2] + top)) }, 3, double.NaN));
        }
        return list;
    }

    /// <summary>06.15: фаска расстояние/расстояние (равные) — объёмы по формулам: торец ½d²L; стык цепочки −d³/3; угол из трёх
    /// рёбер — треугольная грань: в угловом кубе удалено 5d³/6 (сохранено x'+y'+z' ≥ 2d).</summary>
    internal static List<(string Name, TSolid Body, List<EdgeRef> Edges, double R, double Expected)> DirectChamferCases()
    {
        var list = new List<(string, TSolid, List<EdgeRef>, double, double)>();
        {
            var b = Box(0, 0, 0, 60, 40, 20);
            list.Add(("box vertical edge C5", b, new() { EdgeRef.Of(EdgeAt(b, new(60, 40, 0), new(60, 40, 20))) }, 5, 48000 - 12.5 * 20));
        }
        {
            var rot = TransformOp.Rotate(new Vector3d(5, 5, 5), new Vector3d(0.3, 0.7, 0.2).Normalized(), 37);
            var b = TransformOp.Copy(Box(0, 0, 0, 60, 40, 20), rot);
            list.Add(("rotated box edge C4", b, new() { EdgeRef.Of(EdgeAt(b, rot.Point(new(60, 40, 0)), rot.Point(new(60, 40, 20)))) }, 4, 48000 - 8 * 20));
        }
        {
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Unite(bl, Box(0, 0, 20, 30, 40, 10), new List<string>());
            var b = bl[0];
            list.Add(("step concave edge C2", b, new() { EdgeRef.Of(EdgeAt(b, new(30, 0, 20), new(30, 40, 20))) }, 2, 60000 + 2 * 40));
        }
        {
            var prof = new List<Parametric3D.Kernel.Curves.ICurve3>();
            var pts = new[] { new Vector3d(0, 0, 0), new Vector3d(40, 0, 0), new Vector3d(20, 20 * Math.Sqrt(3), 0) };
            for (int i = 0; i < 3; i++) prof.Add(new Parametric3D.Kernel.Curves.LineCurve3(pts[i], pts[(i + 1) % 3]));
            var b = ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), prof), new Vector3d(0, 0, 25));
            list.Add(("prism 60° edge C3", b, new() { EdgeRef.Of(EdgeAt(b, pts[1], pts[1] + new Vector3d(0, 0, 25))) }, 3, 0.5 * 40 * 20 * Math.Sqrt(3) * 25 - 0.5 * 9 * Math.Sin(Math.PI / 3) * 25));
        }
        {
            var b = Box(0, 0, 0, 60, 40, 20);
            list.Add(("box chain 2 top edges C4", b, new() { EdgeRef.Of(EdgeAt(b, new(0, 0, 20), new(60, 0, 20))), EdgeRef.Of(EdgeAt(b, new(60, 0, 20), new(60, 40, 20))) }, 4, 48000 - 8 * 100 + 64.0 / 3));
        }
        {
            var b = Box(0, 0, 0, 60, 40, 20); var V = new Vector3d(60, 40, 20);
            list.Add(("box corner 3 edges C4", b, new() { EdgeRef.Of(EdgeAt(b, new(0, 40, 20), V)), EdgeRef.Of(EdgeAt(b, new(60, 0, 20), V)), EdgeRef.Of(EdgeAt(b, new(60, 40, 0), V)) }, 4,
                48000 - 8 * (120 - 12) - 5 * 64.0 / 6));
        }
        {
            var b = Box(0, 0, 0, 60, 40, 20);
            list.Add(("box all edges C3", b, b.Edges.Select(EdgeRef.Of).ToList(), 3, 48000 - 4.5 * (480 - 72) - 8 * 5 * 27.0 / 6));
        }
        {
            var rot = TransformOp.Rotate(new Vector3d(1, -2, 3), new Vector3d(0.6, -0.3, 0.74).Normalized(), 29);
            var b = TransformOp.Copy(Box(0, 0, 0, 60, 40, 20), rot);
            list.Add(("rotated box all edges C2.5", b, b.Edges.Select(EdgeRef.Of).ToList(), 2.5, 48000 - 3.125 * (480 - 60) - 8 * 5 * 15.625 / 6));
        }
        {
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, Box(10, 10, 10, 30, 20, 10), new List<string>());
            var b = bl[0]; var V = new Vector3d(10, 10, 10);
            list.Add(("pocket corner 3 concave edges C2", b, new() { EdgeRef.Of(EdgeAt(b, V, new(40, 10, 10))), EdgeRef.Of(EdgeAt(b, V, new(10, 30, 10))), EdgeRef.Of(EdgeAt(b, V, new(10, 10, 20))) }, 2,
                48000 - 6000 + 2 * 54 + 5 * 8.0 / 6));
        }
        return list;
    }

    /// <summary>Точный объём прочитанного тела: копия приводится к соглашению, затем аналитический объём (null — нет).</summary>
    static double? ExactVolumeOf(TSolid b)
    {
        if (b.TopologyConvention >= TopologyConventions.Version) return AnalyticVolume.Of(b);
        var c = TopologyClone.Deep(b); TopologyConventions.Normalize(c); return AnalyticVolume.Of(c);
    }

    static TEdge CircleAt(TSolid b, double z, double R) =>
        b.Edges.First(e => e.Curve is Parametric3D.Kernel.Curves.ArcCurve3 { IsClosed: true } a && Math.Abs(a.Frame.Origin.Z - z) < 1e-6 && Math.Abs(a.Radius - R) < 1e-6);

    /// <summary>06.11: плоскость/цилиндр (ось ⟂ плоскости). Объём — по Паппу: площадь сечения × 2π × радиус её центра тяжести.
    /// Скругление: сечение k·r², центр тяжести «щеки» в g·r от угла, g = (10 − 3π)/(3(4 − π)); фаска: ½d², центр в d/3.</summary>
    internal static List<(string Name, TSolid Body, List<EdgeRef> Edges, double R, double Expected, bool Chamfer)> DirectCircleCases()
    {
        var list = new List<(string, TSolid, List<EdgeRef>, double, double, bool)>();
        double k = 1 - Math.PI / 4, g = (10 - 3 * Math.PI) / (3 * (4 - Math.PI)), R = 8;
        TSolid Plate() { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, -2), R, 24), new List<string>()); return bl[0]; }
        TSolid Boss() { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Unite(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, 20), R, 10), new List<string>()); return bl[0]; }
        double vPlate = 48000 - Math.PI * R * R * 20, vBoss = 48000 + Math.PI * R * R * 10;
        { var b = Plate(); list.Add(("hole top edge R2", b, new() { EdgeRef.Of(CircleAt(b, 20, R)) }, 2, vPlate - k * 4 * 2 * Math.PI * (R + g * 2), false)); }
        { var b = Boss(); list.Add(("boss top edge R3", b, new() { EdgeRef.Of(CircleAt(b, 30, R)) }, 3, vBoss - k * 9 * 2 * Math.PI * (R - g * 3), false)); }
        { var b = Boss(); list.Add(("boss base edge R2 (concave)", b, new() { EdgeRef.Of(CircleAt(b, 20, R)) }, 2, vBoss + k * 4 * 2 * Math.PI * (R + g * 2), false)); }
        { var b = Plate(); list.Add(("hole top edge C2", b, new() { EdgeRef.Of(CircleAt(b, 20, R)) }, 2, vPlate - 2 * 2 * Math.PI * (R + 2.0 / 3), true)); }
        { var b = Boss(); list.Add(("boss top edge C3", b, new() { EdgeRef.Of(CircleAt(b, 30, R)) }, 3, vBoss - 4.5 * 2 * Math.PI * (R - 1), true)); }
        { var b = Boss(); list.Add(("boss base edge C2 (concave)", b, new() { EdgeRef.Of(CircleAt(b, 20, R)) }, 2, vBoss + 2 * 2 * Math.PI * (R + 2.0 / 3), true)); }
        { var b = Plate(); list.Add(("hole both edges R2", b, new() { EdgeRef.Of(CircleAt(b, 20, R)), EdgeRef.Of(CircleAt(b, 0, R)) }, 2, vPlate - 2 * k * 4 * 2 * Math.PI * (R + g * 2), false)); }
        {   // глухое отверстие R5 глубиной 8: кромка дна — вогнутая (диск дна внутри окружности), кромка входа — выпуклая
            TSolid Blind() { var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) }; BooleanOp.Subtract(bl, PrimitiveOp.Cylinder(new Vector3d(30, 20, 12), 5, 20), new List<string>()); return bl[0]; }
            double vBlind = 48000 - Math.PI * 25 * 8;
            var b = Blind(); list.Add(("blind hole bottom edge R2 (concave)", b, new() { EdgeRef.Of(CircleAt(b, 12, 5)) }, 2, vBlind + k * 4 * 2 * Math.PI * (5 - g * 2), false));
            var b2 = Blind(); list.Add(("blind hole both edges R1.5", b2, new() { EdgeRef.Of(CircleAt(b2, 12, 5)), EdgeRef.Of(CircleAt(b2, 20, 5)) }, 1.5,
                vBlind + k * 2.25 * 2 * Math.PI * (5 - g * 1.5) - k * 2.25 * 2 * Math.PI * (5 + g * 1.5), false));
            var b3 = Blind(); list.Add(("blind hole bottom edge C2 (concave)", b3, new() { EdgeRef.Of(CircleAt(b3, 12, 5)) }, 2, vBlind + 2 * 2 * Math.PI * (5 - 2.0 / 3), true));
        }
        {   // прямое ребро плоскость/цилиндр вдоль оси: полудиск R15 × 20 (выпуклое, 90°); площадь «щеки» в сечении —
            // сектор R²φ/2 минус треугольник Cx·r/2 и сектор скругления r²(π/2 + φ)/2, Cx = √((R−r)² − r²), φ = atan2(r, Cx)
            var prof = new List<Parametric3D.Kernel.Curves.ICurve3>
            {
                new Parametric3D.Kernel.Curves.LineCurve3(new Vector3d(-15, 0, 0), new Vector3d(15, 0, 0)),
                new Parametric3D.Kernel.Curves.ArcCurve3(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), 15, 0, Math.PI),
            };
            TSolid D() => ExtrudeOp.Build(ProfileBuilder.Build(new Frame3(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY), prof), new Vector3d(0, 0, 20));
            double Rd = 15, rr = 3, Cx = Math.Sqrt((Rd - rr) * (Rd - rr) - rr * rr), phi = Math.Atan2(rr, Cx);
            double cheek = Rd * Rd * phi / 2 - Cx * rr / 2 - rr * rr * (Math.PI / 2 + phi) / 2;
            var b = D(); list.Add(("D-profile flat/round edge R3", b, new() { EdgeRef.Of(EdgeAt(b, new(15, 0, 0), new(15, 0, 20))) }, rr, (Math.PI * Rd * Rd / 2 - cheek) * 20, false));
            var b2 = D(); list.Add(("D-profile both edges R3", b2, new() { EdgeRef.Of(EdgeAt(b2, new(15, 0, 0), new(15, 0, 20))), EdgeRef.Of(EdgeAt(b2, new(-15, 0, 0), new(-15, 0, 20))) }, rr, (Math.PI * Rd * Rd / 2 - 2 * cheek) * 20, false));
        }
        {   // вогнутое прямое ребро: вал R8 вдоль X, ось на верхней грани плиты (полувал сверху); заполнение — треугольник
            // Cu·r/2 минус сектор вала R²ψ/2 и сектор скругления r²(π/2 − ψ)/2, Cu = √((R+r)² − r²), ψ = atan2(r, Cu)
            var bl = new List<TSolid> { Box(0, 0, 0, 60, 40, 20) };
            BooleanOp.Unite(bl, TransformOp.Copy(PrimitiveOp.Cylinder(new Vector3d(0, 20, 20), 8, 60), TransformOp.Rotate(new Vector3d(0, 20, 20), Vector3d.UnitY, 90)), new List<string>());
            var b = bl[0];
            var e = b.Edges.FirstOrDefault(x => x.Curve is Parametric3D.Kernel.Curves.LineCurve3 && Math.Abs(x.V0.P.Z - 20) < 1e-6 && Math.Abs(x.V1.P.Z - 20) < 1e-6 && Math.Abs(x.V0.P.Y - 28) < 1e-6 && Math.Abs(x.V1.P.Y - 28) < 1e-6);
            double Rv = 8, r2 = 2, Cu = Math.Sqrt((Rv + r2) * (Rv + r2) - r2 * r2), psi = Math.Atan2(r2, Cu);
            double fill = Cu * r2 / 2 - Rv * Rv * psi / 2 - r2 * r2 * (Math.PI / 2 - psi) / 2;
            if (e != null) list.Add(("shaft on plate concave edge R2", b, new() { EdgeRef.Of(e) }, r2, 48000 + Math.PI * 64 / 2 * 60 + fill * 60, false));
        }
        {   // повёрнутая плита: ось отверстия не по Z
            var rot = TransformOp.Rotate(new Vector3d(3, -1, 2), new Vector3d(0.2, 0.9, -0.4).Normalized(), 53);
            var b = TransformOp.Copy(Plate(), rot); var c = rot.Point(new Vector3d(30, 20, 20));
            var e = b.Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3 { IsClosed: true } a && (a.Frame.Origin - c).Length < 1e-6);
            list.Add(("rotated hole top edge R2", b, new() { EdgeRef.Of(e) }, 2, vPlate - k * 4 * 2 * Math.PI * (R + g * 2), false));
            var b2 = TransformOp.Copy(Plate(), rot);
            var e2 = b2.Edges.First(x => x.Curve is Parametric3D.Kernel.Curves.ArcCurve3 { IsClosed: true } a && (a.Frame.Origin - c).Length < 1e-6);
            list.Add(("rotated hole top edge C1.5", b2, new() { EdgeRef.Of(e2) }, 1.5, vPlate - 1.125 * 2 * Math.PI * (R + 0.5), true));
        }
        return list;
    }

    /// <summary>Наибольшее отклонение точек рёбер между двумя гранями сопряжения от обоих цилиндров (стыки цепочек).</summary>
    static double MiterDeviation(TSolid b, out int miters)
    {
        double worst = 0; miters = 0;
        foreach (var e in b.Edges)
        {
            if (e.FaceA is not { Tag: "round:face" } fa || e.FaceB is not { Tag: "round:face" } fb) continue;
            miters++;
            foreach (var f in new[] { fa, fb })
            {
                var cyl = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) as Parametric3D.Kernel.Surfaces.CylinderSurface;
                if (cyl == null) return double.PositiveInfinity;
                for (int k = 0; k <= 8; k++)
                {
                    var p = e.Curve.PointAt(e.Curve.T0 + (e.Curve.T1 - e.Curve.T0) * k / 8.0);
                    var o = cyl.Axis.Origin; var w = cyl.Axis.W; var d = p - o; var radial = d - w * Vector3d.Dot(d, w);
                    worst = Math.Max(worst, Math.Abs(radial.Length - cyl.Radius));
                }
            }
        }
        return worst;
    }

    static void DirectRoundChecksRun()
    {
        void RunCases(string check, bool chamfer, List<(string Name, TSolid Body, List<EdgeRef> Edges, double R, double Expected)> cases) => Check(check, () =>
        {
            var fails = new List<string>(); string lastWhy = "";
            DirectRound.Debug = m => { lastWhy = m; if (Environment.GetEnvironmentVariable("DRDBG") == "1") Console.WriteLine("DR " + m); };
            foreach (var (name, body, edges, r, expected) in cases)
            {
                var bl = new List<TSolid> { body }; var w = new List<string>();
                long built0 = DirectRound.Built;
                if (Environment.GetEnvironmentVariable("DRDBG") == "2" && name.StartsWith("rotated")) TopologyConventions.AnchorDebug = m => Console.WriteLine("ANCHOR " + m);
                DirectRound.Prof = Environment.GetEnvironmentVariable("DRTIME") == "1" ? m => Console.WriteLine("  stage " + m) : null;
                if (Environment.GetEnvironmentVariable("DRTIME") == "2") { Parametric3D.Kernel.Diagnostics.Prof.Enabled = true; Parametric3D.Kernel.Diagnostics.Prof.Reset(); }
                var swOp = System.Diagnostics.Stopwatch.StartNew();
                try { if (chamfer) FilletOp.ChamferEdges(bl, edges, r, r, w); else FilletOp.RoundEdges(bl, edges, r, w); }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                finally { swOp.Stop(); if (Environment.GetEnvironmentVariable("DRTIME") == "1") Console.WriteLine($"TIME {check} {name} {swOp.Elapsed.TotalMilliseconds:F1} ms");
                    if (Environment.GetEnvironmentVariable("DRTIME") == "2") { Console.WriteLine($"PROF {name}\n" + Parametric3D.Kernel.Diagnostics.Prof.Report()); Parametric3D.Kernel.Diagnostics.Prof.Enabled = false; } }
                TopologyConventions.AnchorDebug = null;
                if (DirectRound.Built == built0) { fails.Add($"{name}: direct builder declined ({lastWhy})"); continue; }
                var b = bl[0]; double v = AnalyticVolume.Of(b) ?? double.NaN;
                var rep = SolidValidator.Validate(b);
                if (!rep.IsValid) fails.Add($"{name}: {rep.Summary()}");
                if (double.IsNaN(expected)) { double dv = MiterDeviation(b, out int nm); if (nm == 0 || !(dv < 1e-9)) fails.Add($"{name}: miter edges {nm}, off the cylinders by {dv:G3}"); }
                else if (!(Math.Abs(v - expected) <= 1e-6 * Math.Abs(expected))) fails.Add($"{name}: volume {v:R} vs formula {expected:R}");
                if (b.Faces.Any(f => f.Surface is Parametric3D.Kernel.Surfaces.FacetedSurface fs && fs.Source == null)) fails.Add($"{name}: faceted face without a source");
                // точка грани (InteriorHint) плоской грани — строго внутри её контуров (по ней решается сторона и разметка)
                foreach (var f in b.Faces)
                {
                    if (f.InteriorHint is not Vector3d h || GeometryAudit.AuthoritativeSurface(f) is not { IsPlanar: true } ps) continue;
                    var fr = Frame3.FromNormal(h, ps.NormalAt(ps.ProjectToUV(h)).Normalized());
                    bool ins = false; double dmin = double.MaxValue;
                    foreach (var l in f.Loops)
                    {
                        var poly = (l.Ordered() ?? l.CoEdges.ToList()).SelectMany(ce =>
                                            {
                                                var cv = ce.Edge.Curve; bool fw = ReferenceEquals(ce.Edge.V0, ce.Edge.V1) || (cv.PointAt(cv.T0) - ce.Start.P).Length <= (cv.PointAt(cv.T0) - ce.End.P).Length;
                                                return Enumerable.Range(0, 48).Select(k2 => cv.PointAt(cv.T0 + (cv.T1 - cv.T0) * (fw ? k2 / 48.0 : 1 - k2 / 48.0)) - h);
                                            })
                                            .Select(q => new Vector2d(Vector3d.Dot(q, fr.U), Vector3d.Dot(q, fr.V))).ToList();
                        for (int i2 = 0, j2 = poly.Count - 1; i2 < poly.Count; j2 = i2++)
                        {
                            var a2 = poly[j2]; var b2 = poly[i2];
                            if ((b2.Y > 0) != (a2.Y > 0) && 0 < (a2.X - b2.X) * (0 - b2.Y) / (a2.Y - b2.Y) + b2.X) ins = !ins;
                            var ab = b2 - a2; double tt = Math.Clamp(-Vector2d.Dot(a2, ab) / Math.Max(1e-300, ab.LengthSquared), 0, 1);
                            dmin = Math.Min(dmin, (a2 + ab * tt).Length);
                        }
                    }
                    if (!ins || dmin < 1e-9) fails.Add($"{name}: hint of face {f.Tag} is not inside it (inside {ins}, distance {dmin:G3})");
                }
                {   // обмен: точный STEP туда и обратно — тот же объём (цилиндры, эллипсы стыков, сферические углы)
                    var ws = new List<string>(); var sw = new StringWriter();
                    Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { b }, "dr", ws);
                    var back = Parametric3D.Kernel.Export.StepImport.ReadExactSolids(sw.ToString(), ws);
                    // импорт даёт точный объём не всегда — тогда по сетке, допуск как у K10-STEP-ROUNDTRIP (1e-3)
                    double? va = back.Count == 1 ? ExactVolumeOf(back[0]) : null;
                    double vb = back.Count == 1 ? (va ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(back[0]))) : double.NaN;
                    if (!(Math.Abs(vb - v) <= (va != null ? 1e-6 : 1e-3) * Math.Abs(v))) fails.Add($"{name}: STEP round trip {vb:R} vs {v:R} ({string.Join(" | ", ws.Where(x => x.Contains("not")).Take(2))})");
                }
                // IGES: цилиндр-выдавливание с осью не по Z (повёрнутое отверстие, вал вдоль X) не читается обратно и без
                // скругления — известный дефект обмена IGES (KRN-09, «igesprobe»); для таких тел проверка IGES не делается
                bool tiltedCyl = b.Faces.Any(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.ExtrudedSurface { Base: Parametric3D.Kernel.Curves.ArcCurve3 ta }
                                                     && Math.Abs(Math.Abs(Vector3d.Dot(ta.Frame.W.Normalized(), Vector3d.UnitZ)) - 1) > 1e-9);
                if (!tiltedCyl)
                {   // IGES туда и обратно (цилиндр — поверхность вращения 120, эллипс — рациональный сплайн)
                    var wi = new List<string>(); TSolid? bi = null;
                    try { bi = Parametric3D.Kernel.Export.IgesImport.Read(Parametric3D.Kernel.Export.IgesSolid.Write(b, wi), wi); } catch (Exception ex) { wi.Add("exception " + ex.Message); }
                    double? viA = bi != null ? ExactVolumeOf(bi) : null;
                    double vi = bi != null ? (viA ?? Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(bi))) : double.NaN;
                    if (!(Math.Abs(vi - v) <= (viA != null ? 1e-6 : 1e-3) * Math.Abs(v)) || bi == null || !SolidValidator.Validate(bi).IsValid)
                        fails.Add($"{name}: IGES round trip {vi:R} vs {v:R} valid {(bi == null ? "-" : SolidValidator.Validate(bi).IsValid.ToString())} ({string.Join(" | ", wi.Where(x => x.Contains("not") || x.Contains("exception")).Take(2))})");
                }
                Log(new { test = check, name, volume = v, expected = double.IsNaN(expected) ? (double?)null : expected, faces = b.Faces.Count() });
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        RunCases("KRN06-DIRECT-ROUND-PLANE-PLANE", false, DirectRoundCases());
        RunCases("KRN06-DIRECT-CHAMFER-PLANE-PLANE", true, DirectChamferCases());
        foreach (var chf in new[] { false, true })
            RunCases(chf ? "KRN06-DIRECT-CHAMFER-PLANE-CYLINDER" : "KRN06-DIRECT-ROUND-PLANE-CYLINDER", chf,
                DirectCircleCases().Where(c => c.Chamfer == chf).Select(c => (c.Name, c.Body, c.Edges, c.R, c.Expected)).ToList());
        Check("KRN06-DIRECT-ROUND-LOOP-ORIENTATION-INDEPENDENT", () =>
        {
            // метаморфная: у копии бруска обход КАЖДОГО контура обращён (порядок полурёбер и их признаки) — тело то же;
            // прямое скругление всех рёбер и фаска угла дают те же объёмы (выпуклость берётся по геометрии, не по обходу)
            double k = 1 - Math.PI / 4, oct = 1 - Math.PI / 6;
            var fails = new List<string>();
            foreach (var chamfer in new[] { false, true })
            {
                var b = TopologyClone.Deep(Box(0, 0, 0, 60, 40, 20));
                foreach (var f in b.Faces)
                    foreach (var l in f.Loops)
                    {
                        var ces = l.CoEdges.ToList();
                        foreach (var ce in ces) ce.Reversed = !ce.Reversed;
                        l.CoEdges.Clear(); for (int i = ces.Count - 1; i >= 0; i--) l.CoEdges.Add(ces[i]);
                    }
                var bl = new List<TSolid> { b }; var w = new List<string>(); long built0 = DirectRound.Built;
                if (chamfer) FilletOp.ChamferEdges(bl, b.Edges.Select(EdgeRef.Of).ToList(), 3, 3, w); else FilletOp.RoundEdges(bl, b.Edges.Select(EdgeRef.Of).ToList(), 3, w);
                double v = AnalyticVolume.Of(bl[0]) ?? double.NaN;
                double expected = chamfer ? 48000 - 4.5 * (480 - 72) - 8 * 5 * 27.0 / 6 : 48000 - k * 9 * (480 - 72) - 8 * 27 * oct;
                if (DirectRound.Built == built0) fails.Add($"{(chamfer ? "chamfer" : "round")}: direct builder declined");
                else if (!(Math.Abs(v - expected) <= 1e-9 * expected)) fails.Add($"{(chamfer ? "chamfer" : "round")}: volume {v:R} vs {expected:R}");
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
        Check("KRN10-SPHERE-POLE-FAST", () =>
        {
            // выбор полюса карты сферической грани: быстрый (кандидаты по близости к краю, первый вне грани) даёт тот же
            // полюс, что прежний перебор, и делает единицы проверок «внутри грани» вместо сотен
            var faces = new List<TFace>();
            foreach (var (name, body, edges, r, _) in DirectRoundCases().Where(c => c.Name.Contains("corner") || c.Name.Contains("all edges")))
            {
                var bl = new List<TSolid> { body }; FilletOp.RoundEdges(bl, edges, r, new List<string>());
                faces.AddRange(bl[0].Faces.Where(f => f.Tag == "round:corner"));
            }
            foreach (var id in new[] { "S-SPHERE", "S-SPHERE-IN" }) faces.AddRange(Scenario(id, new List<string>())[0].Faces.Where(f => Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)!) is Parametric3D.Kernel.Geometry.Ssi.Sphere));
            Require(faces.Count >= 20, $"only {faces.Count} sphere faces");
            long fastTests = 0, slowTests = 0; int differ = 0;
            foreach (var f in faces)
            {
                var sp = (Parametric3D.Kernel.Geometry.Ssi.Sphere)Parametric3D.Kernel.Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)!)!;
                long t0 = SurfaceDomain.PoleInsideTests;
                SurfaceDomain.PoleFastForTest = null; var a = SurfaceDomain.SpherePoleOutside(f, sp.C, sp.R);   // как в работе (по умолчанию)
                long t1 = SurfaceDomain.PoleInsideTests;
                SurfaceDomain.PoleFastForTest = false; var b = SurfaceDomain.SpherePoleOutside(f, sp.C, sp.R);
                long t2 = SurfaceDomain.PoleInsideTests;
                SurfaceDomain.PoleFastForTest = null;
                fastTests += t1 - t0; slowTests += t2 - t1;
                if (a.HasValue != b.HasValue || (a.HasValue && (a.Value - b!.Value).Length > 1e-15)) differ++;
            }
            Log(new { test = "KRN10-SPHERE-POLE-FAST", faces = faces.Count, fastTests, slowTests, differ });
            Require(differ == 0, $"{differ} faces got another pole");
            Require(fastTests * 10 <= slowTests, $"fast choice made {fastTests} inside tests vs {slowTests}");
        });
        Check("KRN06-DIRECT-ROUND-SEQUENTIAL-NAMES", () =>
        {
            // второе скругление по ссылке, взятой ДО первого: ссылка переживает перестройку, результат прямого
            // построителя — годный вход для него же, объём — по формуле
            double k = 1 - Math.PI / 4;
            var b = Box(0, 0, 0, 60, 40, 20);
            var ra = EdgeRef.Of(EdgeAt(b, new(60, 40, 0), new(60, 40, 20)));
            var rb = EdgeRef.Of(EdgeAt(b, new(0, 0, 0), new(0, 0, 20)));
            var rc = EdgeRef.Of(EdgeAt(b, new(0, 0, 20), new(60, 0, 20)));
            var bl = new List<TSolid> { b }; var w = new List<string>();
            long built0 = DirectRound.Built;
            FilletOp.RoundEdges(bl, new List<EdgeRef> { ra }, 5, w);
            FilletOp.RoundEdges(bl, new List<EdgeRef> { rb }, 4, w);
            Require(DirectRound.Built - built0 == 2, $"direct builder used {DirectRound.Built - built0} of 2 times");
            var e = FilletOp.Resolve(bl, rc, out _);
            // ребро rc укорочено скруглением rb (вершина (0,0,20) срезана): ссылка должна найти укороченное ребро
            Require(e != null && (e.V0.P.Y < 1e-9 && e.V1.P.Y < 1e-9 && Math.Abs(e.V0.P.Z - 20) < 1e-9 && Math.Abs(e.V1.P.Z - 20) < 1e-9), $"reference resolved to a wrong edge {e?.V0.P} {e?.V1.P}");
            double v = AnalyticVolume.Of(bl[0]) ?? double.NaN, expected = 60 * 40 * 20 - k * 25 * 20 - k * 16 * 20;
            Require(Math.Abs(v - expected) <= 1e-6 * expected, $"volume {v:R} vs {expected:R}");
            Require(SolidValidator.Validate(bl[0]).IsValid, "invalid");
            Require(bl[0].Faces.Count(f => f.Tag == "round:face") == 2, "round faces not tagged");
        });
    }
}
