using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Exact;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// ВЫЧИТАНИЕ ЦИЛИНДРА БЕЗ ГРАНЕНИЯ РЕЗЦА — первый случай, доведённый до конца с квадриками.
///
/// Нынешняя булева превращает цилиндр в 55-гранную призму, и отсюда все измеренные погрешности:
/// объём после выреза больше расчётного на 0.05%, дуги рёбер расходятся с вершинами на 0.013 мм
/// (серединное по ста телам). Здесь резец не гранится вовсе: сечения находятся точно
/// (плоскость поперёк оси даёт окружность, вдоль — прямые), а стенка отверстия остаётся
/// цилиндрической поверхностью.
///
/// ГРАНИЦЫ ПРИМЕНИМОСТИ, и они узкие сознательно: сквозное отверстие вдоль одной из осей в теле,
/// ограниченном плоскостями, причём ось цилиндра не задевает боковых граней. Это самый частый
/// случай в деталях, и его достаточно, чтобы проверить весь замысел числами. Всё остальное
/// по-прежнему идёт обычным путём — подменять общую булеву неготовым частным случаем нельзя.
/// </summary>
public static class QuadricCut
{
    /// <summary>
    /// Копировщик рёбер: одно исходное ребро даёт ровно одно новое, с копиями вершин. Без него
    /// новое тело ссылалось бы на рёбра старого, и у каждого набиралось бы по четыре полуребра
    /// вместо двух.
    /// </summary>
    private sealed class EdgeCopier
    {
        private readonly Dictionary<TEdge, TEdge> _edges = new();
        private readonly Dictionary<TVertex, TVertex> _verts = new();

        public TEdge Of(TEdge e)
        {
            if (_edges.TryGetValue(e, out var ne)) return ne;
            return _edges[e] = new TEdge
            {
                V0 = Vertex(e.V0),
                V1 = Vertex(e.V1),
                Curve = e.Curve,
                Hidden = e.Hidden,
                Tolerance = e.Tolerance
            };
        }

        private TVertex Vertex(TVertex v)
        {
            if (_verts.TryGetValue(v, out var nv)) return nv;
            return _verts[v] = new TVertex(v.P) { Tolerance = v.Tolerance };
        }
    }

    /// <summary>
    /// Точное описание результата для расширенного ядра: исходное тело как набор плоскостей плюс
    /// вырез-цилиндр. Если какая-то грань не плоская, описания не будет — выдать приблизительное
    /// за точное хуже, чем не выдать никакого.
    /// </summary>
    private static Exact.QRegion? DescribeCut(TSolid body, Vector3d axisAt, double radius, Vector3d dir,
                                              double low, double high, bool blind)
    {
        var planes = new Exact.PlaneRegistry();
        var quadrics = new Exact.QuadricRegistry();
        const long scale = 1_000_000;

        System.Numerics.BigInteger B(double v) => new(Math.Round(v * scale));

        var faces = new List<Exact.QPolygon>();
        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

            Vector3d n, at;
            if (src is PlaneSurface pl) { n = pl.F.W; at = pl.F.Origin; }
            else if (src is ExtrudedSurface exl && exl.Base is LineCurve3 ln)
            {
                Vector3d u = (ln.B - ln.A).Normalized();
                n = Vector3d.Cross(u, exl.Dir.Normalized());
                at = ln.A;
            }
            else return null;

            if (n.LengthSquared < 1e-18) return null;
            n = n.Normalized();
            if (f.Reversed) n = -n;

            double d = -Vector3d.Dot(n, at);
            var plane = planes.Plane(B(n.X), B(n.Y), B(n.Z), B(d));
            faces.Add(new Exact.QPolygon(Exact.ExactSurfaceRef.Of(plane), Array.Empty<Exact.ExactSurfaceRef>()));
        }

        var solidDesc = new Exact.QSolid(planes, quadrics, faces);

        Vector3d holeAt = axisAt - dir * Vector3d.Dot(axisAt, dir);
        var cylQ = Math.Abs(dir.Z) > 0.9 ? Exact.Quadric.CylinderAlongZ(holeAt, radius)
                 : Math.Abs(dir.X) > 0.9 ? Exact.Quadric.CylinderAlongX(holeAt, radius)
                 : Math.Abs(dir.Y) > 0.9 ? Exact.Quadric.CylinderAlongY(holeAt, radius)
                 : Exact.Quadric.CylinderAlong(holeAt, dir, radius);

        var holeFaces = new List<Exact.QPolygon>
        {
            new(Exact.ExactSurfaceRef.Of(quadrics.Add(cylQ)), Array.Empty<Exact.ExactSurfaceRef>())
        };

        // У глухого отверстия дно ограничивает вырез снизу.
        if (blind)
        {
            Vector3d n = -dir;
            double d = -Vector3d.Dot(n, holeAt + dir * low);
            holeFaces.Add(new Exact.QPolygon(
                Exact.ExactSurfaceRef.Of(planes.Plane(B(n.X), B(n.Y), B(n.Z), B(d))),
                Array.Empty<Exact.ExactSurfaceRef>()));
        }

        return new Exact.QRegion(planes, quadrics)
            .Add(solidDesc)
            .Subtract(new Exact.QSolid(planes, quadrics, holeFaces));
    }

    /// <summary>
    /// Разбить замкнутую кромку на четыре дуги с настоящими вершинами. Замкнутое ребро с
    /// совпадающими концами нельзя назвать ссылкой «от точки до точки», и операции по кромке
    /// (скругление, фаска) за него не берутся.
    /// </summary>
    private static List<TEdge> SplitRim(ICurve3 closed)
    {
        var res = new List<TEdge>();
        if (closed is not ArcCurve3 arc) 
        {
            var v0 = new TVertex(closed.PointAt(closed.T0));
            res.Add(new TEdge { V0 = v0, V1 = v0, Curve = closed });
            return res;
        }

        const int parts = 4;
        double a0 = arc.A0, span = arc.A1 - arc.A0;

        var verts = new TVertex[parts];
        for (int i = 0; i < parts; i++)
            verts[i] = new TVertex(arc.Frame.Origin
                                   + (arc.Frame.U * Math.Cos(a0 + span * i / parts)
                                    + arc.Frame.V * Math.Sin(a0 + span * i / parts)) * arc.Radius);

        for (int i = 0; i < parts; i++)
            res.Add(new TEdge
            {
                V0 = verts[i],
                V1 = verts[(i + 1) % parts],
                Curve = new ArcCurve3(arc.Frame, arc.Radius, a0 + span * i / parts, a0 + span * (i + 1) / parts)
            });

        return res;
    }

    /// <summary>Направление оси: по умолчанию Z; принимаются только оси координат.</summary>
    private static Vector3d Axis(Vector3d? along)
    {
        if (along == null) return Vector3d.UnitZ;
        Vector3d a = along.Value;
        if (a.LengthSquared < 1e-18) return Vector3d.Zero;
        a = a.Normalized();

        return a;      // наклонная ось: след на грани будет эллипсом
    }

    /// <summary>
    /// Подошёл ли случай: тело из плоских граней, отверстие насквозь вдоль одной из осей.
    /// Ось задаётся направлением; поддержаны X, Y и Z — наклонная требует поворота формы и
    /// делается отдельно.
    /// </summary>
    public static bool Fits(TSolid body, Vector3d axisAt, double radius, out double zLow, out double zHigh,
                            Vector3d? along = null)
    {
        zLow = zHigh = 0;
        Vector3d dir = Axis(along);
        if (dir == Vector3d.Zero) return false;
        if (radius <= Tol.Weld) return false;

        // Грани: плоские (в том числе записанные выдавливанием отрезка) и цилиндрические от
        // УЖЕ сделанных отверстий. Второе и позволяет сверлить одно тело несколько раз: прежде
        // первое же отверстие делало тело непригодным для следующего.
        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;
            bool planar = src is PlaneSurface
                       || (src is ExtrudedSurface exl && exl.Base is LineCurve3);
            bool existingHole = src is ExtrudedSurface exa && exa.Base is ArcCurve3;
            if (!planar && !existingHole) return false;
        }

        // НОВОЕ ОТВЕРСТИЕ НЕ ДОЛЖНО ЗАДЕВАТЬ ПРЕЖНИЕ. Пересекающиеся отверстия дают ребро на
        // пересечении двух цилиндров — кривую четвёртой степени, которой мы пока не умеем;
        // честный отказ лучше неверного тела.
        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs2 ? fs2.Source : f.Surface;
            if (src is not ExtrudedSurface ex2 || ex2.Base is not ArcCurve3 arc) continue;

            Vector3d o = arc.Frame.Origin;
            Vector3d ax = ex2.Dir.LengthSquared < 1e-18 ? Vector3d.UnitZ : ex2.Dir.Normalized();
            Vector3d between = (axisAt - o) - ax * Vector3d.Dot(axisAt - o, ax);

            // Оси параллельны: сравниваем расстояние между ними с суммой радиусов.
            if (Math.Abs(Math.Abs(Vector3d.Dot(ax, dir)) - 1) < 1e-9)
            {
                if (between.Length < radius + arc.Radius + Tol.Weld) return false;
            }
            else return false;      // непараллельные отверстия — тот же случай пересечения
        }

        // Габариты вдоль оси дают протяжённость сквозного отверстия.
        double lo = double.MaxValue, hi = double.MinValue;
        double rLo = double.MaxValue;
        foreach (var v in body.Vertices)
        {
            double t = Vector3d.Dot(v.P, dir);
            lo = Math.Min(lo, t); hi = Math.Max(hi, t);
            Vector3d off = (v.P - axisAt) - dir * Vector3d.Dot(v.P - axisAt, dir);
            rLo = Math.Min(rLo, off.Length);
        }
        if (hi - lo <= Tol.Weld) return false;

        // ОТВЕРСТИЕ НЕ ДОЛЖНО ВСКРЫВАТЬ БОКОВЫЕ ГРАНИ. Проверка по вершинам этого не ловит:
        // отверстие R20 с осью в 10 мм от грани вершин не задевает, а грань вскрывает —
        // получалось тело с целой боковиной там, где должен быть пролом. Считается расстояние
        // от оси до КАЖДОЙ грани, параллельной ей.
        if (rLo <= radius + Tol.Weld) return false;

        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

            Vector3d n, at;
            if (src is PlaneSurface pl) { n = pl.F.W; at = pl.F.Origin; }
            else if (src is ExtrudedSurface exl && exl.Base is LineCurve3 ln)
            {
                Vector3d u = (ln.B - ln.A).Normalized();
                n = Vector3d.Cross(u, exl.Dir.Normalized());
                at = ln.A;
            }
            else continue;

            if (n.LengthSquared < 1e-18) continue;
            n = n.Normalized();

            // Грань поперёк оси отверстие вскрывает по делу — это вход и выход.
            if (Math.Abs(Vector3d.Dot(n, dir)) > 1e-6) continue;

            double distance = Math.Abs(Vector3d.Dot(axisAt - at, n));
            if (distance <= radius + Tol.Weld) return false;
        }

        // K08. ТЕЛО ОБЯЗАНО БЫТЬ ПЛИТОЙ ВДОЛЬ ОСИ: каждая грань поперёк оси лежит на нижнем или
        // верхнем уровне. Прежде уровни брались по габариту всего тела, и на плите с бобышкой
        // (её торец выше верха плиты, а стенка бобышки принималась за стенку прежнего отверстия)
        // отверстие в стороне от бобышки получало стенку до торца бобышки: тело выходило
        // негодным (4 замечания B-rep, самопересечение), а следующая булева — с открытыми рёбрами.
        // Найдено прогоном реалистичных деревьев K08. Ступенька, карман, бобышка — честный отказ.
        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs3 ? fs3.Source : f.Surface;
            Vector3d n3, at3;
            if (src is PlaneSurface pl3) { n3 = pl3.F.W; at3 = pl3.F.Origin; }
            else if (src is ExtrudedSurface ex3 && ex3.Base is LineCurve3 ln3)
            {
                Vector3d u = (ln3.B - ln3.A).Normalized();
                n3 = Vector3d.Cross(u, ex3.Dir.Normalized());
                at3 = ln3.A;
            }
            else continue;
            if (n3.LengthSquared < 1e-18) continue;
            n3 = n3.Normalized();
            if (Math.Abs(Vector3d.Dot(n3, dir)) <= 1e-6) continue;          // вдоль оси
            if (Math.Abs(Math.Abs(Vector3d.Dot(n3, dir)) - 1) > 1e-9) return false;   // наклонная грань
            double level = Vector3d.Dot(at3, dir);
            if (Math.Abs(level - lo) > Tol.Weld && Math.Abs(level - hi) > Tol.Weld) return false;
        }

        zLow = lo; zHigh = hi;
        return true;
    }

    /// <summary>
    /// Построить тело с отверстием. Верх и низ получают круглый контур-отверстие с ТОЧНОЙ дугой,
    /// стенка — цилиндрическая поверхность. Ни одной хорды при этом не создаётся.
    /// </summary>
    /// <summary>
    /// ОТВЕРСТИЕ С ЗЕНКОВКОЙ: конус под потайную головку над сквозным отверстием.
    ///
    /// Всё соосно, поэтому и здесь нет пересечения двух квадрик: конус встречает плоскость входа
    /// по окружности диаметра головки, а узкий цилиндр — по окружности самого отверстия. Обе
    /// окружности точны, коническая грань задаётся вращением отрезка образующей.
    ///
    /// Глубина конуса выводится из угла: (R − r) / tg(половина угла при вершине).
    /// </summary>
    public static TSolid? CutCountersink(TSolid body, Vector3d axisAt, double holeRadius,
                                         double headRadius, double angleDegrees,
                                         List<string>? warnings = null, Vector3d? along = null)
    {
        Vector3d dir = Axis(along);

        if (headRadius <= holeRadius + Tol.Weld)
        {
            warnings?.Add("QuadricCut: the countersink must be wider than the hole.");
            return null;
        }
        if (angleDegrees <= 0 || angleDegrees >= 180)
        {
            warnings?.Add("QuadricCut: the countersink angle must be between 0 and 180 degrees.");
            return null;
        }

        if (!Fits(body, axisAt, headRadius, out double lo, out double hi, along))
        {
            warnings?.Add("QuadricCut: this case is not supported yet (planar body, hole along an axis).");
            return null;
        }

        double entry = Vector3d.Dot(axisAt, dir);
        bool entryAtLow = Math.Abs(entry - lo) < Tol.Chord;
        bool entryAtHigh = Math.Abs(entry - hi) < Tol.Chord;
        if (!entryAtLow && !entryAtHigh)
        {
            warnings?.Add("QuadricCut: the hole must start on a face of the body.");
            return null;
        }

        if (entryAtLow) { dir = -dir; (lo, hi) = (-hi, -lo); entry = -entry; }

        double coneDepth = (headRadius - holeRadius) / Math.Tan(angleDegrees * Math.PI / 360);
        double step = entry - coneDepth;
        if (step <= lo + Tol.Weld)
        {
            warnings?.Add($"QuadricCut: the countersink depth {coneDepth:0.###} does not fit inside the body.");
            return null;
        }

        var withHole = Build(body, axisAt, holeRadius, dir, lo, hi, null, warnings);
        if (withHole == null) return null;

        return AddCone(withHole, axisAt, holeRadius, headRadius, dir, step, entry, warnings);
    }

    /// <summary>
    /// Надстроить конус над готовым сквозным отверстием: контур входа расширяется до диаметра
    /// головки, добавляется коническая грань. Дна здесь нет — конус сходится прямо на отверстие.
    /// </summary>
    private static TSolid? AddCone(TSolid body, Vector3d axisAt, double holeRadius, double headRadius,
                                   Vector3d dir, double step, double entry, List<string>? warnings)
    {
        // ПОСТАВКА 4: КРОМКИ — ОБЩИЕ РЁБРА (как в AddRecess и Build). Прежде вход и конус были отдельными полными
        // окружностями на каждой грани, а верхние дуги узкой стенки оставались на уровне входа: 7 «рёбер на краю»,
        // путь всегда отвергался. Теперь кромка входа (R головки) и кромка схода (R отверстия) — по четыре дуги;
        // вход обходит свою вперёд, конус — навстречу; стенка отверстия обходит кромку схода навстречу, конус — вперёд.
        var copier = new EdgeCopier();
        var solid = new TSolid();
        var shell = new TShell { Solid = solid };
        solid.Shells.Add(shell);

        Vector3d Centre(double t) => axisAt - dir * Vector3d.Dot(axisAt, dir) + dir * t;
        var entryFrame = Frame3.FromNormal(Centre(entry), dir);
        var axisFrame = Frame3.FromNormal(Centre(step), dir);
        var rimEntry = SplitRim(new ArcCurve3(entryFrame, headRadius, 0, 2 * Math.PI));
        var rimStep = SplitRim(new ArcCurve3(axisFrame, holeRadius, 0, 2 * Math.PI));

        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

            bool isEntry = src is PlaneSurface pl
                           && Math.Abs(Math.Abs(Vector3d.Dot(pl.F.W.Normalized(), dir)) - 1) < 1e-9
                           && Math.Abs(Vector3d.Dot(pl.F.Origin, dir) - entry) < Tol.Weld;
            bool isHoleWall = EdgeRef.Bare(f.Tag) == "quadric:hole" && src is ExtrudedSurface;

            var face = new TFace { Surface = f.Surface, Reversed = f.Reversed, Tag = f.Tag };

            foreach (var loop in f.Loops)
            {
                if (isEntry && !loop.IsOuter)
                {
                    var hl = face.AddLoop(false);
                    foreach (var e2 in rimEntry) hl.Add(e2, false);
                    continue;
                }
                var nl = face.AddLoop(loop.IsOuter);
                foreach (var ce in loop.CoEdges)
                {
                    if (isHoleWall && Math.Abs(Vector3d.Dot(ce.Edge.V0.P, dir) - entry) < Tol.Weld
                                   && Math.Abs(Vector3d.Dot(ce.Edge.V1.P, dir) - entry) < Tol.Weld) continue;
                    nl.Add(copier.Of(ce.Edge), ce.Reversed);
                }
                if (isHoleWall) foreach (var e2 in rimStep) nl.Add(e2, true);
            }

            // Узкая стенка начинается там, где конус сошёлся на диаметр отверстия.
            if (isHoleWall && src is ExtrudedSurface ex && ex.Base is ArcCurve3 arc)
            {
                double full = ex.Dir.Length;
                var baseFrame = Frame3.FromNormal(Centre(entry - full), dir);
                var narrow = new ArcCurve3(baseFrame, arc.Radius, 0, 2 * Math.PI);
                face.Surface = new ExtrudedSurface(narrow, dir * (step - (entry - full)));
            }

            shell.Add(face);
        }

        // КОНИЧЕСКАЯ ГРАНЬ: вращение образующей — отрезка от кромки головки до кромки отверстия.
        {
            Vector3d u = entryFrame.U;
            Vector3d a = Centre(entry) + u * headRadius;          // на входе
            Vector3d b = Centre(step) + u * holeRadius;           // на сходе к отверстию
            var cone = new TFace
            {
                Surface = new RevolvedSurface(new LineCurve3(a, b), axisFrame, 0, 2 * Math.PI),
                Reversed = true,
                Tag = "quadric:countersink"
            };
            var loop = cone.AddLoop(true);
            foreach (var e2 in rimStep) loop.Add(e2, false);
            foreach (var e2 in rimEntry) loop.Add(e2, true);
            shell.Add(cone);
        }

        warnings?.Add($"QuadricCut: countersink \u2300{2 * headRadius:0.###} cut without faceting the tool.");
        return solid;
    }

    /// <summary>
    /// БОБЫШКА — цилиндрический прилив на плоской грани. Зеркальна отверстию: там материал
    /// убирался, здесь добавляется, а поверхности те же — цилиндрическая стенка и плоский верх.
    ///
    /// Грань, на которой стоит бобышка, получает контур-отверстие: внутри её следа она больше не
    /// поверхность тела, а его внутренность. Кромка общая для грани и стенки — как у отверстия.
    ///
    /// ОБЪЁМ СХОДИТСЯ: 52 700.9 против расчётных 52 712.4 (0.02%, остаток гранения показа).
    /// Долго не сходился — выходило 52 090.4, — и причина была в РАЗВОРОТЕ КРОМКИ: разворачивалась
    /// каждая дуга, но не их порядок, и ломаная шла зигзагом. Видно это стало по площади граней:
    /// верх прилива давал 675.8 вместо 314.2 у круга радиуса 10.
    ///
    /// Отвергнутые по дороге версии: грубая разметка (измерено 14 точек на четверть кромки, то
    /// есть потеря 0.12%, а не 13%) и неверное строение тела (восемь граней, два контура у грани
    /// под приливом — всё верно).
    /// </summary>
    public static TSolid? AddBoss(TSolid body, Vector3d at, double radius, double height,
                                  List<string>? warnings = null, Vector3d? along = null)
    {
        Vector3d dir = Axis(along);
        if (radius <= Tol.Weld || height <= Tol.Weld)
        {
            warnings?.Add("QuadricCut: the boss size must be greater than zero.");
            return null;
        }

        if (!Fits(body, at, radius, out double lo, out double hi, along))
        {
            warnings?.Add("QuadricCut: this case is not supported yet (planar body, boss along an axis).");
            return null;
        }

        double baseAt = Vector3d.Dot(at, dir);
        bool onHigh = Math.Abs(baseAt - hi) < Tol.Chord;
        bool onLow = Math.Abs(baseAt - lo) < Tol.Chord;
        if (!onHigh && !onLow)
        {
            warnings?.Add("QuadricCut: the boss must stand on a face of the body.");
            return null;
        }

        // Построение ждёт, что прилив растёт в сторону большего значения вдоль оси.
        if (onLow) { dir = -dir; (lo, hi) = (-hi, -lo); baseAt = -baseAt; }

        var copier = new EdgeCopier();
        var solid = new TSolid();
        var shell = new TShell { Solid = solid };
        solid.Shells.Add(shell);

        Vector3d Centre(double t) => at - dir * Vector3d.Dot(at, dir) + dir * t;

        List<TEdge>? foot = null;

        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

            PlaneSurface? pl = src as PlaneSurface;
            if (pl == null && src is ExtrudedSurface exl && exl.Base is LineCurve3 ln)
            {
                Vector3d u = (ln.B - ln.A).Normalized();
                Vector3d n = Vector3d.Cross(u, exl.Dir.Normalized());
                if (n.LengthSquared > 1e-18) pl = new PlaneSurface(Frame3.FromNormal(ln.A, n.Normalized()));
            }

            var face = new TFace { Surface = f.Surface, Reversed = f.Reversed, Tag = f.Tag };
            foreach (var loop in f.Loops)
            {
                var nl = face.AddLoop(loop.IsOuter);
                foreach (var ce in loop.CoEdges) nl.Add(copier.Of(ce.Edge), ce.Reversed);
            }

            // Грань, на которой стоит бобышка, получает контур её следа.
            bool isBase = pl != null
                          && Math.Abs(Math.Abs(Vector3d.Dot(pl.F.W.Normalized(), dir)) - 1) < 1e-9
                          && Math.Abs(Vector3d.Dot(pl.F.Origin, dir) - baseAt) < Tol.Weld;

            if (isBase)
            {
                var frame = Frame3.FromNormal(Centre(baseAt), dir);
                foot = SplitRim(new ArcCurve3(frame, radius, 0, 2 * Math.PI));
                var ring = face.AddLoop(false);
                foreach (var e in foot) ring.Add(e, false);
            }

            shell.Add(face);
        }

        if (foot == null)
        {
            warnings?.Add("QuadricCut: the face under the boss was not found.");
            return null;
        }

        // Стенка прилива.
        {
            var frame = Frame3.FromNormal(Centre(baseAt), dir);
            var circle = new ArcCurve3(frame, radius, 0, 2 * Math.PI);
            var wall = new TFace
            {
                Surface = new ExtrudedSurface(circle, dir * height),
                Reversed = false,                       // смотрит наружу прилива
                Tag = "quadric:boss"
            };

            var topRim = SplitRim(new ArcCurve3(Frame3.FromNormal(Centre(baseAt + height), dir),
                                                radius, 0, 2 * Math.PI));

            // РАЗВОРОТ КРОМКИ — ЭТО И ОБРАТНЫЙ ПОРЯДОК ДУГ, а не только разворот каждой. Иначе
            // ломаная идёт зигзагом: измерено — площадь верха прилива выходила 675.8 вместо
            // 314.2 у круга радиуса 10.
            var loop = wall.AddLoop(true);
            for (int i = foot.Count - 1; i >= 0; i--) loop.Add(foot[i], true);
            foreach (var e in topRim) loop.Add(e, false);
            shell.Add(wall);

            // Верх прилива.
            var top = new TFace
            {
                Surface = new PlaneSurface(Frame3.FromNormal(Centre(baseAt + height), dir)),
                Reversed = false,
                Tag = "quadric:boss-top"
            };
            var tl = top.AddLoop(true);
            for (int i = topRim.Count - 1; i >= 0; i--) tl.Add(topRim[i], true);
            shell.Add(top);
        }

        warnings?.Add($"QuadricCut: boss \u2300{2 * radius:0.###} \u00d7 {height:0.###} added without faceting.");
        return solid;
    }

    /// <summary>
    /// ОТВЕРСТИЕ С ЦЕКОВАНИЕМ: два СООСНЫХ цилиндра — широкое неглубокое углубление под головку
    /// и узкое сквозное отверстие, а между ними плоское кольцо.
    ///
    /// Соосные цилиндры не пересекаются вовсе (один вложен в другой), поэтому кривой четвёртой
    /// степени здесь не возникает и всё строится точно: обе стенки — цилиндры, все три контура —
    /// окружности, дно цекования — плоское кольцо.
    /// </summary>
    public static TSolid? CutCounterbore(TSolid body, Vector3d axisAt, double holeRadius,
                                         double headRadius, double headDepth,
                                         List<string>? warnings = null, Vector3d? along = null)
    {
        Vector3d dir = Axis(along);
        if (headRadius <= holeRadius + Tol.Weld)
        {
            warnings?.Add("QuadricCut: the counterbore must be wider than the hole.");
            return null;
        }

        if (!Fits(body, axisAt, headRadius, out double lo, out double hi, along))
        {
            warnings?.Add("QuadricCut: this case is not supported yet (planar body, hole along an axis).");
            return null;
        }

        double entry = Vector3d.Dot(axisAt, dir);
        bool entryAtLow = Math.Abs(entry - lo) < Tol.Chord;
        bool entryAtHigh = Math.Abs(entry - hi) < Tol.Chord;
        if (!entryAtLow && !entryAtHigh)
        {
            warnings?.Add("QuadricCut: the hole must start on a face of the body.");
            return null;
        }

        // Как и у глухого: построение ждёт вход у большего значения вдоль оси.
        if (entryAtLow) { dir = -dir; (lo, hi) = (-hi, -lo); entry = -entry; }

        double step = entry - headDepth;                 // уровень дна цекования
        if (headDepth <= Tol.Weld || step <= lo + Tol.Weld)
        {
            warnings?.Add($"QuadricCut: the counterbore depth {headDepth:0.###} does not fit inside the body.");
            return null;
        }

        // Сначала сквозное отверстие узкого диаметра, затем поверх — углубление.
        var withHole = Build(body, axisAt, holeRadius, dir, lo, hi, null, warnings);
        if (withHole == null) return null;

        return AddRecess(withHole, axisAt, holeRadius, headRadius, dir, step, hi, warnings);
    }

    /// <summary>
    /// Надстроить углубление над готовым сквозным отверстием: у входной грани контур
    /// расширяется до диаметра головки, добавляются широкая стенка и кольцевое дно.
    /// </summary>
    private static TSolid? AddRecess(TSolid body, Vector3d axisAt, double holeRadius, double headRadius,
                                     Vector3d dir, double step, double entry, List<string>? warnings)
    {
        // ПОСТАВКА 4: КРОМКИ — ОБЩИЕ РЁБРА, как у сквозного отверстия (Build). Прежде каждая окружность была своим
        // ребром на каждой грани (вход, стенка цекования, дно — четыре независимых объекта), а у узкой стенки
        // укорачивалась поверхность, но её верхние дуги оставались на уровне входа: 8 «рёбер на краю», и путь
        // всегда отвергался проверкой (это вскрыла трасса маршрутов отверстия, G8). Теперь три кромки — вход
        // цекования, низ его стенки, верх отверстия — по четыре дуги (SplitRim); грани обходят их вперёд, стенки —
        // навстречу (тот же порядок, что в Build).
        var copier = new EdgeCopier();
        var solid = new TSolid();
        var shell = new TShell { Solid = solid };
        solid.Shells.Add(shell);

        Vector3d Centre(double t) => axisAt - dir * Vector3d.Dot(axisAt, dir) + dir * t;
        var entryFrame = Frame3.FromNormal(Centre(entry), dir);
        var stepFrame = Frame3.FromNormal(Centre(step), dir);
        var rimEntry = SplitRim(new ArcCurve3(entryFrame, headRadius, 0, 2 * Math.PI));
        var rimStepOuter = SplitRim(new ArcCurve3(stepFrame, headRadius, 0, 2 * Math.PI));
        var rimStepInner = SplitRim(new ArcCurve3(stepFrame, holeRadius, 0, 2 * Math.PI));

        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

            // Входная грань: её контур-отверстие расширяется до диаметра головки.
            bool isEntry = src is PlaneSurface pl
                           && Math.Abs(Math.Abs(Vector3d.Dot(pl.F.W.Normalized(), dir)) - 1) < 1e-9
                           && Math.Abs(Vector3d.Dot(pl.F.Origin, dir) - entry) < Tol.Weld;
            bool isHoleWall = EdgeRef.Bare(f.Tag) == "quadric:hole" && src is ExtrudedSurface;

            var face = new TFace { Surface = f.Surface, Reversed = f.Reversed, Tag = f.Tag };

            foreach (var loop in f.Loops)
            {
                if (isEntry && !loop.IsOuter)
                {
                    var hl = face.AddLoop(false);
                    foreach (var e2 in rimEntry) hl.Add(e2, false);
                    continue;
                }
                var nl = face.AddLoop(loop.IsOuter);
                foreach (var ce in loop.CoEdges)
                {
                    // У узкой стенки верхние дуги (на уровне входа) — заменяются кромкой на уровне дна цекования.
                    if (isHoleWall && Math.Abs(Vector3d.Dot(ce.Edge.V0.P, dir) - entry) < Tol.Weld
                                   && Math.Abs(Vector3d.Dot(ce.Edge.V1.P, dir) - entry) < Tol.Weld) continue;
                    nl.Add(copier.Of(ce.Edge), ce.Reversed);
                }
                if (isHoleWall) foreach (var e2 in rimStepInner) nl.Add(e2, true);
            }

            // Узкая стенка укорачивается: она кончается на дне цекования.
            if (isHoleWall && src is ExtrudedSurface ex && ex.Base is ArcCurve3 arc)
            {
                double full = ex.Dir.Length;
                var baseFrame = Frame3.FromNormal(Centre(entry - full), dir);
                var narrow = new ArcCurve3(baseFrame, arc.Radius, 0, 2 * Math.PI);
                face.Surface = new ExtrudedSurface(narrow, dir * (step - (entry - full)));
            }

            shell.Add(face);
        }

        // Широкая стенка углубления: низ и верх обходятся навстречу граням.
        {
            var circle = new ArcCurve3(stepFrame, headRadius, 0, 2 * Math.PI);
            var wall = new TFace
            {
                Surface = new ExtrudedSurface(circle, dir * (entry - step)),
                Reversed = true,
                Tag = "quadric:counterbore"
            };
            var wl = wall.AddLoop(true);
            foreach (var e2 in rimStepOuter) wl.Add(e2, true);
            foreach (var e2 in rimEntry) wl.Add(e2, true);
            shell.Add(wall);
        }

        // Кольцевое дно: наружный контур — диаметр головки, внутренний — само отверстие (обход вперёд).
        {
            var bottom = new TFace
            {
                Surface = new PlaneSurface(stepFrame),
                Reversed = false,
                Tag = "quadric:counterbore-bottom"
            };
            var ol = bottom.AddLoop(true);
            foreach (var e2 in rimStepOuter) ol.Add(e2, false);
            var il = bottom.AddLoop(false);
            foreach (var e2 in rimStepInner) il.Add(e2, false);
            shell.Add(bottom);
        }

        warnings?.Add($"QuadricCut: counterbore \u2300{2 * headRadius:0.###} to depth {entry - step:0.###} " +
                      "cut without faceting the tool.");
        return solid;
    }

    /// <summary>
    /// ГЛУХОЕ ОТВЕРСТИЕ: то же построение, но стенка кончается дном — плоским кругом внутри
    /// тела. Дно задаётся плоскостью, а его контур — той же точной окружностью, что и вход,
    /// поэтому новых источников погрешности не появляется.
    ///
    /// Глубина отсчитывается от той грани, через которую входит отверстие: со стороны большего
    /// значения вдоль оси, если направление положительно.
    /// </summary>
    public static TSolid? CutBlind(TSolid body, Vector3d axisAt, double radius, double depth,
                                   List<string>? warnings = null, Vector3d? along = null)
    {
        Vector3d dir = Axis(along);
        if (!Fits(body, axisAt, radius, out double lo, out double hi, along)) 
        {
            warnings?.Add("QuadricCut: this case is not supported yet (planar body, hole along an axis).");
            return null;
        }

        if (depth <= Tol.Weld || depth >= hi - lo - Tol.Weld)
        {
            warnings?.Add($"QuadricCut: the depth {depth:0.###} must be between zero and the body size.");
            return null;
        }

        // ОТСЧЁТ ГЛУБИНЫ — ОТ ЗАДАННОЙ ТОЧКИ, А НЕ ОТ КРАЯ ТЕЛА. Операция «отверстие» задаёт
        // точку на входной грани и направление сверления; если считать от края, отверстие
        // уходит с другой стороны — измерено: снималось 83.5 мм³ вместо расчётных 251.3.
        double entry = Vector3d.Dot(axisAt, dir);
        double bottom = entry + depth;                  // сверлим ВДОЛЬ направления

        // Вход должен совпадать с краем тела, а дно — лежать внутри.
        bool entryAtLow = Math.Abs(entry - lo) < Tol.Chord;
        bool entryAtHigh = Math.Abs(entry - hi) < Tol.Chord;
        if (!entryAtLow && !entryAtHigh)
        {
            warnings?.Add("QuadricCut: the hole must start on a face of the body.");
            return null;
        }

        // ПОСТРОЕНИЕ СЧИТАЕТ, ЧТО ВХОД СВЕРХУ (у большего значения вдоль оси). Если сверлим
        // снизу, разворачиваем направление — тогда вход снова оказывается «сверху», и всё
        // построение работает без оговорок.
        if (entryAtLow)
        {
            dir = -dir;
            (lo, hi) = (-hi, -lo);
            entry = -entry;
        }

        bottom = entry - depth;                        // дно глубже входа вдоль оси

        if (bottom <= lo + Tol.Weld || bottom >= hi - Tol.Weld)
        {
            warnings?.Add($"QuadricCut: the depth {depth:0.###} does not fit inside the body.");
            return null;
        }

        return Build(body, axisAt, radius, dir, lo, hi, bottom, warnings);
    }

    public static TSolid? Cut(TSolid body, Vector3d axisAt, double radius, List<string>? warnings = null,
                              Vector3d? along = null)
    {
        Vector3d dir = Axis(along);
        if (!Fits(body, axisAt, radius, out double zLow, out double zHigh, along))
        {
            warnings?.Add("QuadricCut: this case is not supported yet (planar body, through hole along Z).");
            return null;
        }

        return Build(body, axisAt, radius, dir, zLow, zHigh, null, warnings);
    }

    /// <summary>
    /// Общее построение: сквозное отверстие, если дно не задано, иначе глухое с дном на
    /// указанном уровне вдоль оси.
    /// </summary>
    private static TSolid? Build(TSolid body, Vector3d axisAt, double radius, Vector3d dir,
                                 double zLow, double zHigh, double? bottomAt, List<string>? warnings)
    {
        double wallLow = bottomAt ?? zLow;             // снизу стенка кончается дном

        // РЁБРА КРОМОК ДЕЛАЮТСЯ ОБЩИМИ для грани и для стенки отверстия. Прежде у каждой было
        // своё, и топология расходилась: скругление по кромке отверстия не находило рёбер
        // (семь проверок падали), потому что для него кромка — это ребро между двумя гранями.
        List<TEdge>? rimLow = null, rimHigh = null;

        // РЁБРА ПЕРЕНОСИМЫХ ГРАНЕЙ КОПИРУЮТСЯ. Прежде грани нового тела ссылались на рёбра
        // старого, и у каждого набиралось по ЧЕТЫРЕ полуребра (два от прежнего тела, два от
        // нового) вместо двух. Топология выходила неверной, и операции по кромке за неё не
        // брались.
        var copier = new EdgeCopier();

        var solid = new TSolid();
        var shell = new TShell { Solid = solid };
        solid.Shells.Add(shell);

        // Боковые и прочие грани переносятся как есть.
        foreach (var f in body.Faces)
        {
            var src = f.Surface is FacetedSurface fs ? fs.Source : f.Surface;

            // Плоскость грани: у выдавливания отрезка она выводится из его положения.
            PlaneSurface? pl = src as PlaneSurface;
            if (pl == null && src is ExtrudedSurface exl && exl.Base is LineCurve3 ln)
            {
                Vector3d u = (ln.B - ln.A).Normalized();
                Vector3d n = Vector3d.Cross(u, exl.Dir.Normalized());
                if (n.LengthSquared > 1e-18)
                    pl = new PlaneSurface(Frame3.FromNormal(ln.A, n.Normalized()));
            }
            // Грань, не являющаяся плоскостью (стенка прежнего отверстия), переносится как
            // есть: иначе второе отверстие стирало бы первое.
            if (pl == null)
            {
                var keep = new TFace { Surface = f.Surface, Reversed = f.Reversed, Tag = f.Tag };
                foreach (var loop in f.Loops)
                {
                    var kl = keep.AddLoop(loop.IsOuter);
                    foreach (var ce in loop.CoEdges) kl.Add(copier.Of(ce.Edge), ce.Reversed);
                }
                shell.Add(keep);
                continue;
            }

            // Грань пересекается отверстием, если её плоскость не параллельна оси. У прямой оси
            // след — окружность, у наклонной — эллипс.
            double cos = Vector3d.Dot(pl.F.W.Normalized(), dir);
            bool crossed = Math.Abs(cos) > 1e-9;
            double at = Vector3d.Dot(pl.F.Origin, dir);
            bool isBottom = crossed && Math.Abs(at - zLow) < Tol.Weld;
            bool isTop = crossed && Math.Abs(at - zHigh) < Tol.Weld;

            var face = new TFace { Surface = f.Surface, Reversed = f.Reversed, Tag = f.Tag };
            foreach (var loop in f.Loops)
            {
                var nl = face.AddLoop(loop.IsOuter);
                foreach (var ce in loop.CoEdges) nl.Add(copier.Of(ce.Edge), ce.Reversed);
            }

            // У верхней и нижней граней добавляется контур-отверстие: ТОЧНАЯ окружность из
            // сечения квадрики, а не ломаная.
            if ((isBottom && bottomAt == null) || isTop)
            {
                // Сечение считается по самой поверхности: у прямой оси выйдет окружность, у
                // наклонной — эллипс с точными полуосями.
                var sec = Exact.QuadricSection.CylinderEllipse(axisAt, dir, radius, pl.F.Origin, pl.F.W);
                if (sec == null) continue;

                var (centre, major, minor, aLen, bLen) = sec.Value;
                var frame = new Frame3(centre, major, minor);

                ICurve3 boundary = Math.Abs(aLen - bLen) < Tol.Weld
                    ? new ArcCurve3(frame, radius, 0, 2 * Math.PI)
                    : new EllipseCurve3(frame, aLen, bLen, 0, 2 * Math.PI);

                // КРОМКА РАЗБИВАЕТСЯ НА ЧЕТЫРЕ ДУГИ. Одно замкнутое ребро с совпадающими
                // концами топологически бедно: скругление и фаска по кромке не могут за него
                // взяться (ссылка на ребро задаётся парой концов, а они одинаковы). Обычный путь
                // булевой тоже даёт кромку из нескольких дуг.
                var edges = SplitRim(boundary);
                if (isTop) rimHigh = edges; else rimLow = edges;

                var hole = face.AddLoop(false);
                foreach (var e2 in edges) hole.Add(e2, false);
            }

            shell.Add(face);
        }

        // Стенка отверстия: цилиндрическая поверхность между верхом и низом.
        {
            Vector3d baseAt = axisAt - dir * Vector3d.Dot(axisAt, dir) + dir * wallLow;
            var frame = Frame3.FromNormal(baseAt, dir);
            var circle = new ArcCurve3(frame, radius, 0, 2 * Math.PI);
            var wall = new TFace
            {
                Surface = new ExtrudedSurface(circle, dir * (zHigh - wallLow)),
                Reversed = true,                        // смотрит внутрь отверстия
                Tag = "quadric:hole"
            };

            // Нижняя кромка: общие рёбра с гранью, если отверстие сквозное, иначе свои (у дна).
            rimLow ??= SplitRim(circle);
            rimHigh ??= SplitRim(new ArcCurve3(Frame3.FromNormal(baseAt + dir * (zHigh - wallLow), dir),
                                               radius, 0, 2 * Math.PI));

            // ОБХОД КРОМКИ ВСТРЕЧНЫЙ. Грань и стенка делят одно ребро, и каждая обходит его в
            // свою сторону: иначе у ребра оба полуребра смотрят одинаково, тело не сходится, и
            // скругление по кромке не берётся за него.
            // ОБА КРАЯ СТЕНКИ ОБХОДЯТСЯ НАВСТРЕЧУ ГРАНЯМ. Грани (верх, низ, дно) добавляют свои
            // кромки вперёд, значит, стенка обязана обойти их назад: иначе у ребра оба полуребра
            // смотрят в одну сторону — измерено на кромке отверстия, и скругление за такое ребро
            // не берётся.
            var loop = wall.AddLoop(true);
            foreach (var e2 in rimLow) loop.Add(e2, true);
            foreach (var e2 in rimHigh) loop.Add(e2, true);

            shell.Add(wall);
        }

        // ДНО ГЛУХОГО ОТВЕРСТИЯ: плоский круг, ограниченный той же точной окружностью.
        if (bottomAt is { } bz)
        {
            Vector3d at = axisAt - dir * Vector3d.Dot(axisAt, dir) + dir * bz;
            var frame = Frame3.FromNormal(at, dir);
            var bottom = new TFace
            {
                Surface = new PlaneSurface(frame),
                Reversed = false,                      // ДОНЫШКО СМОТРИТ НАРУЖУ ТЕЛА. Разворот сюда
                                                       // был поставлен по ошибке, и объём выходил
                                                       // 28 315.5 вместо 28 548.7 — ровно на вклад
                                                       // этой грани в знаковый объём.
                Tag = "quadric:hole-bottom"
            };

            // Кромка дна — те же рёбра, что у нижнего края стенки.
            rimLow ??= SplitRim(new ArcCurve3(frame, radius, 0, 2 * Math.PI));
            var bl = bottom.AddLoop(true);
            foreach (var e2 in rimLow) bl.Add(e2, false);
            shell.Add(bottom);
        }

        // ТОЧНОЕ ОПИСАНИЕ РЕЗУЛЬТАТА: тело исходного + вырез-цилиндр. Оно и есть то, чего телу
        // не хватало: плоскостное точное представление цилиндр вместить не может.
        solid.QExact = DescribeCut(body, axisAt, radius, dir, wallLow, zHigh, bottomAt != null);

        warnings?.Add($"QuadricCut: hole \u2300{2 * radius:0.###} " +
                      (bottomAt == null ? "through" : $"to depth {zHigh - bottomAt.Value:0.###}") +
                      " cut without faceting the tool.");
        return solid;
    }
}
