using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Ops;

/// <summary>
/// Выдавливание профиля в призматическое тело.
/// Строит ПОЛНУЮ топологию: две крышки, боковые грани по каждой кривой контура,
/// общие рёбра и вершины. Именно поэтому потом можно скруглить конкретное ребро.
/// </summary>
public static class ExtrudeOp
{
    /// <param name="profile">Замкнутый плоский профиль.</param>
    /// <param name="dir">Вектор выдавливания (длина = глубина).</param>
    public static TSolid Build(Profile profile, Vector3d dir)
    {
        if (profile.IsEmpty) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Extrude: the profile is empty or not closed.");
        if (profile.HasSelfIntersections) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Extrude: the sketch contour crosses itself.");
        if (dir.Length < Tol.Weld) throw new RebuildException(Parametric3D.Kernel.OperationOutcome.InvalidInput, "Extrude: zero depth.");

        var tb = new TopoBuilder();
        var solid = new TSolid();
        var shell = solid.AddShell();

        Frame3 basePlane = profile.Plane;
        Frame3 topPlane = basePlane.Translated(dir);

        // Куда выдавливаем относительно нормали плоскости профиля.
        //
        // Раньше молча предполагалось, что вдоль +W: крышка на базовой
        // плоскости считалась нижней, вторая верхней. При выдавливании
        // в обратную сторону они менялись местами, и наружные нормали
        // указывали внутрь тела.
        bool along = Vector3d.Dot(dir, basePlane.W) > 0;

        // По ПАРЕ крышек на каждую ОБЛАСТЬ, а не одна пара на весь профиль.
        //
        // Грань плоскости несёт один внешний контур и его отверстия. Если
        // ссыпать в неё все контуры профиля, то при вложенности глубже двух
        // уровней — например у четырёх концентрических окружностей —
        // внутренний диск оказывается отверстием внешнего кольца, и крышка
        // строится неверно. Профиль сам знает, какое отверстие какому
        // контуру принадлежит (Profile.Regions).
        int regionIndex = -1;
        foreach (var region in profile.Regions())
        {
            regionIndex++;
            // УСТОЙЧИВЫЕ ИМЕНА. Подпись грани говорит, чем она была создана: низ, верх или
            // боковая от k-й кривой контура. Она переживает изменение размеров эскиза, в отличие
            // от координат, по которым ссылки искались раньше.
            string region0 = $"extrude[{regionIndex}]";
            var bottom = new TFace { Surface = new PlaneSurface(basePlane), Reversed = along };
            var top = new TFace { Surface = new PlaneSurface(topPlane), Reversed = !along };

            var loops = new List<ProfileLoop> { region.Outer };
            loops.AddRange(region.Holes);

            int loopIndex = -1;
            foreach (var loop in loops)
            {
                loopIndex++;
                int n = loop.Curves.Count;

                // Вершины низа и верха
                var vb = new TVertex[n];
                var vt = new TVertex[n];
                for (int i = 0; i < n; i++)
                {
                    Vector3d p = loop.Curves[i].PointAt(0);
                    vb[i] = tb.Vertex(p);
                    vt[i] = tb.Vertex(p + dir);
                }

                var eb = new TEdge[n]; var ebRev = new bool[n];
                var et = new TEdge[n]; var etRev = new bool[n];
                var ev = new TEdge[n]; var evRev = new bool[n];

                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    ICurve3 c = loop.Curves[i];
                    eb[i] = tb.Edge(vb[i], vb[j], c, out ebRev[i]);
                    et[i] = tb.Edge(vt[i], vt[j], Translate(c, dir), out etRev[i]);
                    ev[i] = tb.Edge(vb[i], vt[i], new LineCurve3(vb[i].P, vt[i].P), out evRev[i]);
                }

                // Боковые грани. Нормаль такой поверхности — cross(касательная, dir).
                //
                // Контуры нормализованы: внешний обходится против часовой
                // стрелки, отверстия — по часовой. Поэтому cross(касательная, dir)
                // сам по себе смотрит наружу тела в ОБОИХ случаях: у внешнего
                // контура от центра, у отверстия внутрь отверстия, а это и есть
                // наружу материала. Разворот зависит только от направления
                // выдавливания.
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    var f = new TFace
                    {
                        Surface = new ExtrudedSurface(loop.Curves[i], dir),
                        Reversed = !along
                    };
                    var fl = f.AddLoop(true);
                    fl.Add(eb[i], ebRev[i]);
                    fl.Add(ev[j], evRev[j]);
                    fl.Add(et[i], !etRev[i]);
                    fl.Add(ev[i], !evRev[i]);
                    // ПОДПИСЬ ПО МЕСТУ В ЭСКИЗЕ, А НЕ ПО ХЕШ-КОДУ ОБЪЕКТА. Прежде здесь стоял
                    // loop.GetHashCode(): при каждом перестроении создаётся новый объект контура,
                    // хеш другой, и подпись менялась — то есть «устойчивое имя» устойчивым не
                    // было. Номер области, номер контура и номер кривой в нём от перестроения не
                    // зависят.
                    f.Tag = $"{region0}:side[{loopIndex}.{i}]";
                    shell.Add(f);
                }

                // Контуры крышек этой области
                var lb = bottom.AddLoop(loop.IsOuter);
                var lt = top.AddLoop(loop.IsOuter);
                for (int i = 0; i < n; i++)
                {
                    lb.Add(eb[i], ebRev[i]);
                    lt.Add(et[i], etRev[i]);
                }
            }

            bottom.Tag = region0 + (along ? ":bottom" : ":top");
            top.Tag = region0 + (along ? ":top" : ":bottom");
            shell.Add(bottom);
            shell.Add(top);
        }

        return solid;
    }

    /// <summary>Сдвиг кривой на вектор (для верхней крышки).</summary>
    internal static ICurve3 Translate(ICurve3 c, Vector3d d)
    {
        return c switch
        {
            LineCurve3 l => new LineCurve3(l.A + d, l.B + d),
            ArcCurve3 a => new ArcCurve3(a.Frame.Translated(d), a.Radius, a.A0, a.A1),
            EllipseCurve3 e => new EllipseCurve3(e.Frame.Translated(d), e.RU, e.RV, e.A0, e.A1),
            BezierCurve3 b => new BezierCurve3(b.P.Select(p => p + d)),
            PolylineCurve3 p => new PolylineCurve3(p.Pts.Select(v => v + d)),
            // СПЛАЙН ПЕРЕНОСИТСЯ ОПОРНЫМИ ТОЧКАМИ. Без этого случая он уходил в общий путь
            // «пересчитать точками», то есть в ломаную: верхняя кромка выдавленного сплайнового
            // эскиза переставала быть сплайном, и в обменный файл уходила ломаная.
            SplineCurve3 sp => new SplineCurve3(sp.ControlPoints.Select(v => v + d).ToList(),
                                                sp.Degree, sp.Weights, sp.Knots),
            _ => TranslateBySampling(c, d)
        };
    }

    private static ICurve3 TranslateBySampling(ICurve3 c, Vector3d d)
    {
        var pts = new List<Vector3d>();
        c.Tessellate(pts, true);
        return new PolylineCurve3(pts.Select(p => p + d));
    }
}
