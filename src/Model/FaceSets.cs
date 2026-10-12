using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model;

/// <summary>
/// ИМЕНОВАННЫЙ НАБОР ГРАНЕЙ: «опорная поверхность», «нагруженная площадка», «стык с крышкой».
///
/// Без него расчёт не задать: нагрузку и закрепление вешают на грани, и эти грани надо назвать
/// один раз и сохранить. Иначе после перестроения модели указания слетают, и всё приходится
/// назначать заново.
///
/// Грани хранятся УСТОЙЧИВЫМИ ссылками — теми же, что уже переживают перестроение у скруглений и
/// фасок. Хранить номера граней нельзя: они меняются при малейшей правке дерева.
/// </summary>
public sealed class FaceSet
{
    public string Name;

    /// <summary>Для чего набор заведён — чтобы через полгода было понятно.</summary>
    public string Purpose = "";

    /// <summary>Устойчивые ссылки на грани.</summary>
    public readonly List<FaceRef> Faces = new();

    public FaceSet(string name) { Name = name; }

    public override string ToString() =>
        $"{Name}: граней {Faces.Count}" + (string.IsNullOrEmpty(Purpose) ? "" : $" ({Purpose})");
}

/// <summary>
/// УСТОЙЧИВАЯ ССЫЛКА НА ГРАНЬ. Грань узнаётся по точке на ней и по направлению её нормали в этой
/// точке: номера граней меняются при каждой правке дерева, а место в пространстве — нет.
///
/// Так же устроены ссылки на рёбра, которыми пользуются скругление и фаска; там это уже
/// проверено перестроениями.
/// </summary>
public readonly struct FaceRef
{
    /// <summary>Точка на грани — по ней грань и ищется.</summary>
    public readonly Vector3d Point;

    /// <summary>Нормаль в этой точке: отличает две грани, сошедшиеся в одном месте.</summary>
    public readonly Vector3d Normal;

    /// <summary>Подпись грани, если она есть: лишняя проверка при поиске.</summary>
    public readonly string Tag;

    public FaceRef(Vector3d point, Vector3d normal, string tag = "")
    {
        Point = point;
        Normal = normal.LengthSquared < 1e-18 ? Vector3d.UnitZ : normal.Normalized();
        Tag = tag;
    }

    /// <summary>Ссылка на грань тела: точка берётся в середине грани.</summary>
    public static FaceRef Of(TFace face)
    {
        var mb = new Kernel.Tess.MeshBuilder();
        face.Surface.TessellateFace(face, mb);

        Vector3d sum = Vector3d.Zero;
        foreach (var p in mb.PositionsD) sum += p;

        Vector3d centre = mb.PositionsD.Count > 0 ? sum / mb.PositionsD.Count : Vector3d.Zero;
        Vector3d n = face.Surface.NormalAt(face.Surface.ProjectToUV(centre));
        if (face.Reversed) n = -n;

        return new FaceRef(centre, n, face.Tag ?? "");
    }

    /// <summary>
    /// Найти грань в теле. Сначала по подписи и близости, затем по одной близости: подпись может
    /// измениться при перестроении, а место — нет.
    /// </summary>
    public TFace? Resolve(TSolid solid, double tolerance = 0.5)
    {
        TFace? best = null;
        double bestDistance = double.MaxValue;

        foreach (var f in solid.Faces)
        {
            var mb = new Kernel.Tess.MeshBuilder();
            f.Surface.TessellateFace(f, mb);
            if (mb.PositionsD.Count == 0) continue;

            // РАССТОЯНИЕ ДО САМОЙ ГРАНИ, А НЕ ДО ЕЁ ВЕРШИН. Мерка по вершинам не годится: у
            // прямоугольной грани середина отстоит от каждого угла на десятки миллиметров, и
            // поиск не находил ничего. Считается расстояние до треугольников грани.
            double near = double.MaxValue;
            var tri = mb.IndicesList;
            for (int i = 0; i + 2 < tri.Count; i += 3)
            {
                double d = DistanceToTriangle(Point, mb.PositionsD[tri[i]],
                                              mb.PositionsD[tri[i + 1]], mb.PositionsD[tri[i + 2]]);
                if (d < near) near = d;
            }

            if (near > bestDistance) continue;

            Vector3d n = f.Surface.NormalAt(f.Surface.ProjectToUV(Point));
            if (f.Reversed) n = -n;

            // Нормаль должна смотреть туда же: иначе выберется грань с другой стороны стенки.
            if (Vector3d.Dot(n, Normal) < 0.5) continue;

            best = f;
            bestDistance = near;
        }

        return bestDistance <= tolerance ? best : null;
    }

    /// <summary>Расстояние от точки до треугольника: сперва до плоскости, затем до сторон.</summary>
    private static double DistanceToTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d n = Vector3d.Cross(b - a, c - a);
        if (n.LengthSquared < 1e-24) return Math.Min((p - a).Length, Math.Min((p - b).Length, (p - c).Length));

        n = n.Normalized();
        Vector3d proj = p - n * Vector3d.Dot(p - a, n);

        bool inside = Vector3d.Dot(Vector3d.Cross(b - a, proj - a), n) >= -1e-9
                   && Vector3d.Dot(Vector3d.Cross(c - b, proj - b), n) >= -1e-9
                   && Vector3d.Dot(Vector3d.Cross(a - c, proj - c), n) >= -1e-9;

        if (inside) return (p - proj).Length;

        return Math.Min(ToSegment(p, a, b), Math.Min(ToSegment(p, b, c), ToSegment(p, c, a)));
    }

    private static double ToSegment(Vector3d p, Vector3d a, Vector3d b)
    {
        Vector3d d = b - a;
        double len2 = d.LengthSquared;
        if (len2 < 1e-24) return (p - a).Length;

        double t = Math.Clamp(Vector3d.Dot(p - a, d) / len2, 0, 1);
        return (p - (a + d * t)).Length;
    }

    public override string ToString() => $"грань у {Point}" + (string.IsNullOrEmpty(Tag) ? "" : $" [{Tag}]");
}
