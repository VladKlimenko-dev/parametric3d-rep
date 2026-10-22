using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// Вращение профиля вокруг оси. При полном обороте крышки не создаются,
/// при частичном — две плоские крышки в начальном и конечном положении.
/// Вершины, лежащие на оси, схлопываются (вырожденное ребро не создаётся).
/// </summary>
public static class RevolveOp
{
    /// <param name="angles">
    /// Необязательные углы разбиения всех граней вращения — по возрастанию, от a0 до a1.
    /// Нужны, когда тело вращения режет вдоль уже гранёной поверхности (резец по дуге):
    /// его вершины по углу должны совпасть с вершинами той поверхности.
    /// </param>
    public static readonly bool HintOff = Environment.GetEnvironmentVariable("P3D_REVOLVEHINT") == "0";
    public static long HintsSet;

    public static TSolid Build(Profile profile, Frame3 axis, double a0, double a1,
                               IReadOnlyList<double>? angles = null)
    {
        if (profile.IsEmpty) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Revolve: the profile is empty or not closed.");
        if (profile.HasSelfIntersections) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Revolve: the sketch contour crosses itself.");

        // ОТРИЦАТЕЛЬНЫЙ УГОЛ — ТА ЖЕ ОБЛАСТЬ, ЧТО И ПОЛОЖИТЕЛЬНЫЙ В ОБРАТНОМ ПОРЯДКЕ.
        //
        // ИСПРАВЛЕНО. Ориентация крышек и сторона граней вращения рассчитаны
        // на a1 > a0. При обратном вращении нормали граней выходили вывернутыми
        // на 180° — затенение показывало тело наизнанку, хотя сама сетка
        // была ориентирована верно. Поворот от 0 до −270° заметает ровно
        // то же, что от −270° до 0, поэтому концы просто меняются местами.
        if (a1 < a0) (a0, a1) = (a1, a0);

        double sweep = a1 - a0;
        if (Math.Abs(sweep) < 1e-9) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Revolve: zero angle.");
        bool full = Math.Abs(Math.Abs(sweep) - Math.PI * 2) < 1e-9;

        // Число шагов по углу — одно на всё тело, от наибольшего радиуса профиля.
        double profileMaxR = 0;
        foreach (var lp in profile.Loops)
            foreach (var cv in lp.Curves)
            {
                var pts = new List<Vector3d>();
                cv.Tessellate(pts, true);
                foreach (var q in pts)
                {
                    Vector3d l = q - axis.Origin;
                    profileMaxR = Math.Max(profileMaxR, (l - axis.W * Vector3d.Dot(l, axis.W)).Length);
                }
            }
        int angularSegments = GeomUtil.ArcSegments(profileMaxR, sweep);

        var tb = new TopoBuilder();
        var solid = new TSolid();
        var shell = solid.AddShell();

        TFace? capStart = null, capEnd = null;
        if (!full)
        {
            Frame3 fs = RotateFrame(profile.Plane, axis, a0);
            Frame3 fe = RotateFrame(profile.Plane, axis, a1);
            capStart = new TFace { Surface = new PlaneSurface(fs), Reversed = true, Tag = "cap:start" };
            capEnd = new TFace { Surface = new PlaneSurface(fe), Reversed = false, Tag = "cap:end" };
        }

        int loopIndex = -1;
        foreach (var loop in profile.Loops)
        {
            loopIndex++;
            int n = loop.Curves.Count;

            var vs = new TVertex[n];  // вершины при угле a0
            var ve = new TVertex[n];  // вершины при угле a1
            var onAxis = new bool[n];

            for (int i = 0; i < n; i++)
            {
                Vector3d p = loop.Curves[i].PointAt(0);
                onAxis[i] = RadiusOf(p, axis) < Tol.Weld;
                vs[i] = tb.Vertex(Rot(p, axis, a0));
                ve[i] = full ? vs[i] : tb.Vertex(Rot(p, axis, a1));
            }

            var es = new TEdge[n]; var esRev = new bool[n];
            var ee = new TEdge[n]; var eeRev = new bool[n];
            var ea = new TEdge?[n]; var eaRev = new bool[n];   // дуги, заметаемые вершинами

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                ICurve3 c = loop.Curves[i];
                es[i] = tb.Edge(vs[i], vs[j], RotateCurve(c, axis, a0), out esRev[i]);
                if (full)
                {
                    ee[i] = es[i]; eeRev[i] = esRev[i];
                }
                else
                {
                    ee[i] = tb.Edge(ve[i], ve[j], RotateCurve(c, axis, a1), out eeRev[i]);
                }

                // Окружность, заметаемая узлом профиля, создаётся и при
                // полном обороте.
                //
                // Раньше при полном обороте она пропускалась, и у тела
                // не оказывалось ни одного ребра между соседними
                // поверхностями вращения: в режиме «с рёбрами» показывать
                // было нечего. Между тем ребро там настоящее — по нему
                // сходятся две разные поверхности.
                //
                // При полном обороте начальная и конечная вершины совпадают,
                // и ребро получается замкнутым — та самая окружность.
                if (onAxis[i])
                {
                    ea[i] = null;      // узел на оси не заметает ничего
                }
                else
                {
                    ICurve3 arc = TrailArc(loop.Curves[i].PointAt(0), axis, a0, a1);
                    ea[i] = tb.Edge(vs[i], ve[i], arc, out eaRev[i]);
                }
            }

            // Боковые (поверхности вращения)
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                // кривая целиком на оси — грань вырождается
                if (IsCurveOnAxis(loop.Curves[i], axis)) continue;

                var f = new TFace
                {
                    // Одно число шагов по углу на все грани тела: иначе соседние
                    // грани разбивают общую окружность по-разному (см. AngularSegments).
                    Surface = new RevolvedSurface(loop.Curves[i], axis, a0, a1)
                    {
                        AngularSegments = angularSegments,
                        Angles = angles?.ToArray()
                    },
                    // Контуры нормализованы (см. ProfileBuilder), поэтому
                    // отверстия разворачивать не нужно: нормаль поверхности
                    // вращения уже смотрит наружу материала.
                    Reversed = false,
                    // ПОДПИСЬ ПО МЕСТУ В ЭСКИЗЕ, А НЕ ПО ХЕШ-КОДУ: объект контура при каждом
                    // перестроении новый, и подпись менялась — имя не было устойчивым.
                    Tag = $"revolve:side[{loopIndex}.{i}]"
                };
                // Граница поверхности вращения. При полном обороте это только
                // две заметаемые окружности: шов по профилю входит в неё
                // дважды с противоположным обходом и сокращается.
                // k94: точка грани ПОЛНОГО оборота — середина кривой профиля, повёрнутая на средний угол. Без неё классификатор
                // общего пути отказывал по тору («torus face without an interior point»). P3D_REVOLVEHINT=0 — прежнее.
                if (!HintOff && full) { var cc = loop.Curves[i]; f.InteriorHint = Rot(cc.PointAt(0.5 * (cc.T0 + cc.T1)), axis, 0.5 * (a0 + a1)); System.Threading.Interlocked.Increment(ref HintsSet); }
                var fl = f.AddLoop(true);
                if (!full) fl.Add(es[i], esRev[i]);
                if (ea[j] != null) fl.Add(ea[j]!, eaRev[j]);
                if (!full) fl.Add(ee[i], !eeRev[i]);
                if (ea[i] != null) fl.Add(ea[i]!, !eaRev[i]);
                shell.Add(f);
            }

            if (!full)
            {
                var ls = capStart!.AddLoop(loop.IsOuter);
                var le = capEnd!.AddLoop(loop.IsOuter);
                for (int i = 0; i < n; i++)
                {
                    ls.Add(es[i], esRev[i]);
                    le.Add(ee[i], eeRev[i]);
                }
            }
        }

        if (!full) { shell.Add(capStart!); shell.Add(capEnd!); }
        // K08. ГРАНИ ТЕЛА ВРАЩЕНИЯ СМОТРЕЛИ ВНУТРЬ. Нормаль грани (и обход её треугольников)
        // выходили внутрь тела у шара, конуса и тора — у всех тел вращения из примитивов. Потребители
        // это компенсировали знаковым объёмом всей сетки, но B-rep оставался вывернутым: проверка
        // нормалей по наружной сетке давала 0 согласий из 2 208 у шара, а соглашение K05 по такой
        // грани выворачивало тело (объём со знаком минус). Ориентация выправляется здесь, по знаковому
        // объёму собственного гранения: отрицательный — все грани разворачиваются.
        FixOrientation(solid);
        return solid;
    }

    private static double RadiusOf(Vector3d p, Frame3 axis)
    {
        Vector3d l = p - axis.Origin;
        return (l - axis.W * Vector3d.Dot(l, axis.W)).Length;
    }

    private static Vector3d Rot(Vector3d p, Frame3 axis, double a)
        => RevolvedSurface.RotateAbout(p, axis.Origin, axis.W, a);

    private static Frame3 RotateFrame(Frame3 f, Frame3 axis, double a)
    {
        Vector3d o = Rot(f.Origin, axis, a);
        Vector3d u = Rot(f.Origin + f.U, axis, a) - o;
        Vector3d v = Rot(f.Origin + f.V, axis, a) - o;
        return new Frame3(o, u, v);
    }

    private static ICurve3 RotateCurve(ICurve3 c, Frame3 axis, double a)
    {
        if (Math.Abs(a) < 1e-12) return c;
        return c switch
        {
            LineCurve3 l => new LineCurve3(Rot(l.A, axis, a), Rot(l.B, axis, a)),
            ArcCurve3 ac => new ArcCurve3(RotateFrame(ac.Frame, axis, a), ac.Radius, ac.A0, ac.A1),
            EllipseCurve3 ec => new EllipseCurve3(RotateFrame(ec.Frame, axis, a), ec.RU, ec.RV, ec.A0, ec.A1),
            BezierCurve3 b => new BezierCurve3(b.P.Select(p => Rot(p, axis, a))),
            PolylineCurve3 p => new PolylineCurve3(p.Pts.Select(v => Rot(v, axis, a))),
            _ => RotateBySampling(c, axis, a)
        };
    }

    private static ICurve3 RotateBySampling(ICurve3 c, Frame3 axis, double a)
    {
        var pts = new List<Vector3d>();
        c.Tessellate(pts, true);
        return new PolylineCurve3(pts.Select(p => Rot(p, axis, a)));
    }

    /// <summary>Дуга, которую заметает точка при вращении вокруг оси.</summary>
    private static ICurve3 TrailArc(Vector3d p, Frame3 axis, double a0, double a1)
    {
        Vector3d l = p - axis.Origin;
        double h = Vector3d.Dot(l, axis.W);
        Vector3d radial = l - axis.W * h;
        double r = radial.Length;
        Vector3d center = axis.Origin + axis.W * h;
        Vector3d u = radial / r;
        Vector3d v = Vector3d.Cross(axis.W, u);
        return new ArcCurve3(new Frame3(center, u, v), r, a0, a1);
    }

    private static bool IsCurveOnAxis(ICurve3 c, Frame3 axis)
    {
        for (int i = 0; i <= 4; i++)
            if (RadiusOf(c.PointAt(i / 4.0), axis) > Tol.Weld) return false;
        return true;
    }

    internal static void FixOrientation(TSolid solid)
    {
        var mb = new Tess.MeshBuilder();
        foreach (var f in solid.Faces) f.Surface.TessellateFace(f, mb);
        var md = new Tess.MeshData();
        mb.FillInto(md);
        if (Tess.MassProps.SignedVolume(md) < 0)
            foreach (var f in solid.Faces) f.Reversed = !f.Reversed;
    }
}
