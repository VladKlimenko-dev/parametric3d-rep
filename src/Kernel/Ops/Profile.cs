using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;

namespace Parametric3D.Kernel.Ops;

/// <summary>Замкнутая цепочка кривых на плоскости профиля.</summary>
public sealed class ProfileLoop
{
    public readonly List<ICurve3> Curves = new();
    public bool IsOuter = true;
    /// <summary>Площадь (знаковая, в системе координат плоскости профиля).</summary>
    public double Area;

    /// <summary>
    /// Контур сам себя пересекает. Такой контур не ограничивает область:
    /// у него нет однозначного «внутри», поэтому ни заливать его, ни
    /// выдавливать нельзя.
    /// </summary>
    public bool SelfIntersects;

    /// <summary>
    /// Меняет направление обхода контура на противоположное.
    /// Порядок кривых переворачивается, и каждая кривая — тоже.
    /// </summary>
    public void Reverse()
    {
        Curves.Reverse();
        for (int i = 0; i < Curves.Count; i++) Curves[i] = Curves[i].Reversed();
        Area = -Area;
    }

    public List<Vector2d> Sample(Frame3 plane)
    {
        var pts3 = new List<Vector3d>();
        for (int i = 0; i < Curves.Count; i++)
            Curves[i].Tessellate(pts3, i == 0);
        if (pts3.Count > 1 && (pts3[0] - pts3[^1]).LengthSquared < Tol.Weld * Tol.Weld)
            pts3.RemoveAt(pts3.Count - 1);
        return pts3.Select(plane.ToLocal2).ToList();
    }
}

/// <summary>Плоский профиль: плоскость + набор замкнутых контуров с определённой вложенностью.</summary>
public sealed class Profile
{
    public Frame3 Plane;
    public readonly List<ProfileLoop> Loops = new();

    public IEnumerable<ProfileLoop> Outers => Loops.Where(l => l.IsOuter);
    public IEnumerable<ProfileLoop> Inners => Loops.Where(l => !l.IsOuter);
    public bool IsEmpty => Loops.Count == 0;

    /// <summary>Хотя бы один контур сам себя пересекает.</summary>
    public bool HasSelfIntersections => Loops.Any(l => l.SelfIntersects);

    /// <summary>Внешний контур и принадлежащие ему отверстия.</summary>
    public sealed class Region
    {
        public ProfileLoop Outer = null!;
        public readonly List<ProfileLoop> Holes = new();
    }

    /// <summary>
    /// Разбивает профиль на области: внешний контур плюс отверстия,
    /// лежащие НЕПОСРЕДСТВЕННО в нём.
    ///
    /// Тонкость вся во вложенности. У четырёх концентрических окружностей
    /// внешних контуров два (первый и третий), отверстий тоже два (второй
    /// и четвёртый). Четвёртый лежит внутри первого — но принадлежит он
    /// третьему, ближайшему из охватывающих. Если отдать его обоим, первая
    /// область получит отверстие внутри своего же отверстия, и разбиение
    /// на треугольники развалится.
    ///
    /// Ближайший определяется по наименьшей площади среди охватывающих:
    /// вложенные контуры всегда меньше объемлющих.
    /// </summary>
    public List<Region> Regions()
    {
        var samples = Loops.Select(l => l.Sample(Plane)).ToList();

        var regions = new List<Region>();
        var index = new Dictionary<ProfileLoop, Region>();

        for (int i = 0; i < Loops.Count; i++)
        {
            if (!Loops[i].IsOuter) continue;
            var r = new Region { Outer = Loops[i] };
            regions.Add(r);
            index[Loops[i]] = r;
        }

        for (int j = 0; j < Loops.Count; j++)
        {
            if (Loops[j].IsOuter || samples[j].Count == 0) continue;

            Region? host = null;
            double bestArea = double.MaxValue;

            for (int i = 0; i < Loops.Count; i++)
            {
                if (!Loops[i].IsOuter || i == j || samples[i].Count < 3) continue;
                if (!GeomUtil.PointInPolygon(samples[i], samples[j][0])) continue;

                double area = Math.Abs(Loops[i].Area);
                if (area >= bestArea) continue;

                bestArea = area;
                host = index[Loops[i]];
            }

            host?.Holes.Add(Loops[j]);
        }

        return regions;
    }
}

/// <summary>
/// Собирает профиль из «супа» кривых эскиза: сшивает цепочки по совпадающим
/// концам, замыкает контуры и определяет, какой контур внешний, а какой —
/// отверстие. Ровно этого шага не хватало, чтобы эскиз стал телом.
/// </summary>
public static class ProfileBuilder
{
    public static Profile Build(Frame3 plane, IEnumerable<ICurve3> curves)
    {
        var pr = new Profile { Plane = plane };
        var pool = curves.Where(c => c.Length > Tol.Weld).ToList();

        while (pool.Count > 0)
        {
            var chain = new List<ICurve3>();
            ICurve3 first = pool[0];
            pool.RemoveAt(0);
            chain.Add(first);

            Vector3d start = first.PointAt(0);
            Vector3d end = first.PointAt(1);

            if (first.IsClosed || (start - end).LengthSquared < Tol.Weld * Tol.Weld)
            {
                AddLoop(pr, chain);
                continue;
            }

            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = 0; i < pool.Count; i++)
                {
                    ICurve3 c = pool[i];
                    Vector3d a = c.PointAt(0), b = c.PointAt(1);
                    if (Near(end, a)) { chain.Add(c); end = b; }
                    else if (Near(end, b)) { chain.Add(c.Reversed()); end = a; }
                    else if (Near(start, b)) { chain.Insert(0, c); start = a; }
                    else if (Near(start, a)) { chain.Insert(0, c.Reversed()); start = b; }
                    else continue;

                    pool.RemoveAt(i);
                    grew = true;
                    break;
                }
                if (Near(start, end) && chain.Count > 1) break;
            }

            if (Near(start, end)) AddLoop(pr, chain);
            // Незамкнутые цепочки в профиль не попадают (открытый контур нельзя выдавить в тело).
        }

        ResolveNesting(pr);
        NormalizeOrientation(pr);
        return pr;
    }

    private static bool Near(Vector3d a, Vector3d b) => (a - b).LengthSquared < Tol.Weld * Tol.Weld * 100.0;

    private static void AddLoop(Profile pr, List<ICurve3> chain)
    {
        var loop = new ProfileLoop();
        loop.Curves.AddRange(chain);
        var sample = loop.Sample(pr.Plane);
        loop.Area = GeomUtil.SignedArea(sample);
        loop.SelfIntersects = GeomUtil.SelfIntersects(sample);
        pr.Loops.Add(loop);
    }

    /// <summary>
    /// Приводит контуры к каноническому направлению обхода: внешний — против
    /// часовой стрелки в системе координат плоскости профиля, отверстия —
    /// по часовой.
    ///
    /// Без этого шага направление обхода зависело от того, КАК пользователь
    /// нарисовал контур. Окружность параметризована против часовой всегда,
    /// поэтому цилиндр получался правильным; а прямоугольник, растянутый
    /// мышью справа налево, давал обход по часовой — и у выдавливания
    /// выворачивались обе крышки. Операции построения тел вправе исходить
    /// из того, что профиль уже нормализован.
    /// </summary>
    private static void NormalizeOrientation(Profile pr)
    {
        foreach (var loop in pr.Loops)
        {
            if (Math.Abs(loop.Area) < 1e-12) continue;
            bool ccw = loop.Area > 0;
            if (ccw != loop.IsOuter) loop.Reverse();
        }
    }

    /// <summary>Контур внутри нечётного числа других контуров — отверстие.</summary>
    private static void ResolveNesting(Profile pr)
    {
        var samples = pr.Loops.Select(l => l.Sample(pr.Plane)).ToList();
        for (int i = 0; i < pr.Loops.Count; i++)
        {
            if (samples[i].Count == 0) { pr.Loops[i].IsOuter = true; continue; }
            Vector2d probe = samples[i][0];
            int depth = 0;
            for (int j = 0; j < pr.Loops.Count; j++)
            {
                if (i == j) continue;
                if (GeomUtil.PointInPolygon(samples[j], probe)) depth++;
            }
            pr.Loops[i].IsOuter = (depth % 2) == 0;
        }
    }
}
