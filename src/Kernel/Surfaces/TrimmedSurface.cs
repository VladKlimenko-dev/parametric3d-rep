using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// ОБРЕЗАННАЯ ПОВЕРХНОСТЬ: исходная поверхность и контуры обрезки, заданные В ЕЁ ПАРАМЕТРАХ.
///
/// В обменных файлах грань сложной формы задана именно так: поверхность плюс контуры на ней.
/// Наши грани хранят контуры в пространстве, и для плоскости или цилиндра этого хватает, а у
/// поверхности двойной кривизны перевод контура в параметры каждый раз — и дорого, и неточно.
/// Здесь контур хранится уже в параметрах, поэтому гранение идёт по нему точно.
///
/// Первый контур — внешний, остальные — отверстия.
/// </summary>
public sealed class TrimmedSurface : ISurface3
{
    public readonly ISurface3 Base;
    public readonly List<List<Vector2d>> Loops;

    public TrimmedSurface(ISurface3 baseSurface, IEnumerable<IEnumerable<Vector2d>> loops)
    {
        Base = baseSurface ?? throw new ArgumentNullException(nameof(baseSurface));
        Loops = loops.Select(l => l.ToList()).Where(l => l.Count >= 3).ToList();
        if (Loops.Count == 0) throw new ArgumentException("контур обрезки пуст");
    }

    public Vector3d PointAt(Vector2d uv) => Base.PointAt(uv);
    public Vector3d NormalAt(Vector2d uv) => Base.NormalAt(uv);
    public Vector2d ProjectToUV(Vector3d p) => Base.ProjectToUV(p);
    public bool IsPlanar => Base.IsPlanar;

    /// <summary>
    /// Лежит ли точка в пределах обрезки: внутри внешнего контура и вне отверстий. Счёт по
    /// числу пересечений луча — способ простой и не требующий выпуклости.
    /// </summary>
    public bool Contains(Vector2d uv)
    {
        if (!Inside(Loops[0], uv)) return false;
        for (int i = 1; i < Loops.Count; i++)
            if (Inside(Loops[i], uv)) return false;
        return true;
    }

    private static bool Inside(List<Vector2d> loop, Vector2d p)
    {
        bool inside = false;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            var a = loop[i];
            var b = loop[j];
            if (a.Y > p.Y == b.Y > p.Y) continue;
            double x = (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (p.X < x) inside = !inside;
        }
        return inside;
    }

    /// <summary>Площадь обрезанного куска в параметрах — мерка того, насколько обрезка велика.</summary>
    public double ParameterArea()
    {
        double Area(List<Vector2d> l)
        {
            double s = 0;
            for (int i = 0, j = l.Count - 1; i < l.Count; j = i++) s += (l[j].X + l[i].X) * (l[j].Y - l[i].Y);
            return Math.Abs(s) / 2;
        }

        double a = Area(Loops[0]);
        for (int i = 1; i < Loops.Count; i++) a -= Area(Loops[i]);
        return Math.Max(0, a);
    }

    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        // Сетка строится по контурам обрезки, а не по контурам грани: они уже в параметрах
        // поверхности, и переводить ничего не нужно.
        var holes = Loops.Skip(1).Cast<IReadOnlyList<Vector2d>>().ToList();
        var tris = Cdt.Triangulate(Loops[0], holes, out _);
        if (tris == null) { Base.TessellateFace(face, mb); return; }

        var all = new List<Vector2d>(Loops[0]);
        foreach (var h in Loops.Skip(1)) all.AddRange(h);

        int start = mb.VertexCount;
        foreach (var uv in all)
        {
            Vector3d n = NormalAt(uv);
            if (face.Reversed) n = -n;
            mb.AddVertex(PointAt(uv), n);
        }

        for (int i = 0; i + 2 < tris.Count; i += 3)
            if (face.Reversed) mb.AddTriangle(start + tris[i], start + tris[i + 2], start + tris[i + 1]);
            else mb.AddTriangle(start + tris[i], start + tris[i + 1], start + tris[i + 2]);
    }
}
