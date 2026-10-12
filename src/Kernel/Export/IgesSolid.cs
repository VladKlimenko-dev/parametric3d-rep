using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Export;

/// <summary>
/// ЗАПИСЬ ТЕЛА В IGES. Каждая грань выходит обрезанной поверхностью: сама поверхность плюс её
/// контуры. Так файл несёт не проволоку, а грани — то, что принимающая система и ждёт.
///
/// Что пишется точно: плоскость, цилиндр (выдавливанием окружности), поверхность вращения,
/// сплайновая поверхность. Контуры — отрезками, дугами и сплайнами.
///
/// ЧЕГО МЫ НЕ ДЕЛАЕМ и почему. Не пишем кривые на поверхности (сущность 142) в параметрах грани:
/// для этого нужна развёртка каждого ребра в параметры своей поверхности, а при разных
/// поверхностях у одного ребра — две развёртки. Пока контуры записаны в пространстве, и это
/// честнее: приёмная система сама спроецирует их на поверхность, а не получит приблизительную
/// развёртку под видом точной.
/// </summary>
public static class IgesSolid
{
    /// <summary>Записать тело.</summary>
    public static string Write(TSolid solid, List<string>? warnings = null) =>
        Write(new[] { solid }, warnings);

    /// <summary>
    /// Записать НЕСКОЛЬКО тел в один файл. Деталь может состоять из нескольких тел, и терять их
    /// при выводе нельзя: сущности всех тел идут в одном файле подряд, как и положено IGES.
    /// </summary>
    public static string Write(IEnumerable<TSolid> solids, List<string>? warnings = null)
    {
        if (solids.Any(b => b.Guarantee == GuaranteeLevel.Approximate))
            warnings?.Add("IGES: an approximate body is exported (built by an approximate route).");
        var f = new IgesFile();
        int written = 0, skipped = 0, bodies = 0;

        foreach (var solid in solids)
        {
            bodies++;
            foreach (var face in solid.Faces)
        {
                int surface = Surface(f, face, warnings);
                if (surface == 0) { skipped++; continue; }

                int outer = 0;
                var holes = new List<int>();

                foreach (var loop in face.Loops)
                {
                    int curve = Loop(f, loop);
                    if (curve == 0) continue;

                    if (loop.IsOuter && outer == 0) outer = curve;
                    else holes.Add(curve);
                }

                // Имя грани переживает обмен: оно кладётся в раздел описаний.
                int trimmed = f.AddTrimmed(surface, outer, holes);
                if (!string.IsNullOrEmpty(face.Tag)) f.Label(trimmed, face.Tag);

                written++;
            }
        }

        if (skipped > 0)
            warnings?.Add($"IGES: {skipped} face(s) skipped — the surface type is not written yet.");

        warnings?.Add($"IGES: {written} face(s) of {bodies} body(ies) written.");
        return f.Write();
    }

    /// <summary>Поверхность грани. Возвращает 0, если вид поверхности не записывается.</summary>
    private static int Surface(IgesFile f, TFace face, List<string>? warnings)
    {
        var src = face.Surface is FacetedSurface fs ? fs.Source : face.Surface;

        switch (src)
        {
            case PlaneSurface pl:
                return f.AddPlane(pl.F.W, pl.F.Origin);

            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc:
            {
                // Цилиндр: окружность плюс перенос.
                Vector3d c = arc.Frame.Origin;
                Vector3d start = arc.PointAt(arc.T0);
                int circle = f.AddArcXY(c, start, start);
                return f.AddExtruded(circle, ex.Dir);
            }

            case ExtrudedSurface ex2 when ex2.Base is LineCurve3 ln:
            {
                // Плоская боковая грань: записывается плоскостью, как и любая другая.
                Vector3d u = (ln.B - ln.A).Normalized();
                Vector3d n = Vector3d.Cross(u, ex2.Dir.Normalized());
                if (n.LengthSquared < 1e-18) return 0;
                return f.AddPlane(n.Normalized(), ln.A);
            }

            // KRN-06: точный цилиндр произвольной оси (лента прямого скругления) — поверхность вращения отрезка-образующей
            // вокруг оси (120); длина образующей — по проекциям вершин грани на ось. Мутация P3D_IGESCYL=0.
            case CylinderSurface cyl when !IgesCylOff:
                return CylinderAsRevolved(f, face, cyl.Axis.Origin, cyl.Axis.W.Normalized(), cyl.Axis.U.Normalized(), cyl.Radius);

            case RevolvedSurface rev:
            {
                int axis = f.AddLine(rev.Axis.Origin, rev.Axis.Origin + rev.Axis.W);
                int gen = Curve(f, rev.Base);
                return gen == 0 ? 0 : f.AddRevolved(axis, gen, rev.A0, rev.A1);
            }

            case NurbsSurface ns:
                return f.AddSplineSurface(ns);
        }

        warnings?.Add($"IGES: surface {src?.GetType().Name ?? "unknown"} is not written yet.");
        return 0;
    }

    /// <summary>
    /// Контур грани целиком — составной кривой. Прежде сохранялась только первая кривая, и от
    /// четырёхугольной грани оставался один отрезок.
    /// </summary>
    private static int Loop(IgesFile f, TLoop loop)
    {
        var parts = new List<int>();
        foreach (var ce in loop.CoEdges)
        {
            int id = Curve(f, ce.Edge.Curve);
            if (id != 0) parts.Add(id);
        }

        if (parts.Count == 0) return 0;
        return parts.Count == 1 ? parts[0] : f.AddComposite(parts);
    }

    /// <summary>Кривая: отрезок, дуга или сплайн.</summary>
    private static int Curve(IgesFile f, ICurve3? c) => c switch
    {
        LineCurve3 ln => f.AddLine(ln.A, ln.B),

        // Дуга пишется, только если лежит в плоскости, параллельной XY: у наклонной в IGES
        // нужна матрица преобразования, и подменять её поворотом координат нельзя.
        ArcCurve3 arc when Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), Vector3d.UnitZ)) - 1) < 1e-9
            // Дуга IGES (100) идёт от начала к концу ПРОТИВ часовой стрелки в плоскости XY.
            // Дуга, идущая по часовой (размах со знаком оси меньше нуля), пишется с переставленными
            // концами — иначе файл описывал дополнительную дугу, и кромка отверстия при чтении
            // становилась вдвое длиннее. Направление ребра читатель восстанавливает по стыкам.
            => arc.Sweep * Math.Sign(Vector3d.Dot(arc.Frame.W, Vector3d.UnitZ)) >= 0 || arc.IsClosed
                ? f.AddArcXY(arc.Frame.Origin, arc.PointAt(arc.T0), arc.PointAt(arc.T1))
                : f.AddArcXY(arc.Frame.Origin, arc.PointAt(arc.T1), arc.PointAt(arc.T0)),

        SplineCurve3 sp => f.AddSpline(sp),

        // K10: наклонная дуга (образующая шара и тора, кромка в вертикальной плоскости) —
        // ТОЧНЫЙ рациональный квадратичный B-сплайн; прежде такая кривая не писалась вовсе, и
        // грань вращения выпадала из файла.
        ArcCurve3 tilted => f.AddSpline(ArcAsRational(tilted)),

        // KRN-06: эллипс (стык цепочки, наклонный торец ленты) — аффинный образ дуги: точный рациональный B-сплайн.
        EllipseCurve3 el when !IgesCylOff => f.AddSpline(EllipseAsRational(el)),

        _ => 0
    };

    /// <summary>
    /// Дуга окружности как рациональный квадратичный B-сплайн (точное представление): куски
    /// не больше 90°, средний вес — cos(половины угла куска), внутренние узлы двойные.
    /// </summary>
    /// <summary>Цилиндр как поверхность вращения (120): ось и отрезок-образующая; длина — по проекциям точек рёбер грани на ось.</summary>
    static int CylinderAsRevolved(IgesFile f, TFace face, Vector3d o, Vector3d wv, Vector3d uv, double radius)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var ce in face.Loops.SelectMany(l => l.CoEdges))
            for (int k = 0; k <= 8; k++)
            {
                var q = ce.Edge.Curve.PointAt(ce.Edge.Curve.T0 + (ce.Edge.Curve.T1 - ce.Edge.Curve.T0) * k / 8.0);
                double h = Vector3d.Dot(q - o, wv); lo = Math.Min(lo, h); hi = Math.Max(hi, h);
            }
        if (lo > hi) return 0;
        double pad = Math.Max(1e-3, 0.01 * (hi - lo));
        int axis = f.AddLine(o, o + wv);
        int gen = f.AddLine(o + uv * radius + wv * (lo - pad), o + uv * radius + wv * (hi + pad));
        return f.AddRevolved(axis, gen, 0, 2 * Math.PI);
    }

    static readonly bool IgesCylOff = Environment.GetEnvironmentVariable("P3D_IGESCYL") == "0";

    internal static SplineCurve3 EllipseAsRational(EllipseCurve3 e)
    {
        var u = e.Frame.U.Normalized(); var v = e.Frame.V.Normalized(); var o = e.Frame.Origin;
        var unit = ArcAsRational(new ArcCurve3(new Frame3(o, u, v), 1, e.A0, e.A1));
        var pts = unit.ControlPoints.Select(p => o + u * (e.RU * Vector3d.Dot(p - o, u)) + v * (e.RV * Vector3d.Dot(p - o, v))).ToList();
        return new SplineCurve3(pts, 2, unit.Weights, unit.Knots);
    }

    internal static SplineCurve3 ArcAsRational(ArcCurve3 a)
    {
        double sweep = a.Sweep;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-12));
        double step = sweep / n;
        var pts = new List<Vector3d>(); var w = new List<double>(); var knots = new List<double> { 0, 0, 0 };
        double wm = Math.Cos(Math.Abs(step) / 2);
        Vector3d P(double ang) => a.Frame.Origin + a.Radius * (Math.Cos(ang) * a.Frame.U.Normalized() + Math.Sin(ang) * a.Frame.V.Normalized());
        for (int i = 0; i < n; i++)
        {
            double t0 = a.A0 + step * i, t1 = t0 + step, tm = (t0 + t1) / 2;
            if (i == 0) { pts.Add(P(t0)); w.Add(1); }
            // Вершина угла касательных: на биссектрисе на расстоянии R / cos(θ/2).
            var mid = a.Frame.Origin + (a.Radius / wm) * (Math.Cos(tm) * a.Frame.U.Normalized() + Math.Sin(tm) * a.Frame.V.Normalized());
            pts.Add(mid); w.Add(wm);
            pts.Add(P(t1)); w.Add(1);
            if (i + 1 < n) { knots.Add((i + 1.0) / n); knots.Add((i + 1.0) / n); }
        }
        knots.AddRange(new[] { 1.0, 1.0, 1.0 });
        return new SplineCurve3(pts, 2, w, knots);
    }
}
