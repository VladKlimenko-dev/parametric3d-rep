using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

/// <summary>Мерки одного треугольника.</summary>
public readonly struct TriangleQuality
{
    /// <summary>Наименьший угол, градусы. У равностороннего 60.</summary>
    public readonly double MinAngle;

    /// <summary>Наибольший угол, градусы.</summary>
    public readonly double MaxAngle;

    /// <summary>
    /// Отношение сторон: длиннейшая сторона к вписанной окружности, приведённое так, что у
    /// равностороннего выходит единица. Чем больше, тем хуже.
    /// </summary>
    public readonly double AspectRatio;

    /// <summary>
    /// Перекос: 0 у равностороннего, 1 у выродившегося. Эта мерка принята в расчётных пакетах,
    /// и по ней же обычно задают порог годности сетки.
    /// </summary>
    public readonly double Skewness;

    public readonly double Area;

    public TriangleQuality(Vector3d a, Vector3d b, Vector3d c)
    {
        double la = (b - c).Length, lb = (c - a).Length, lc = (a - b).Length;
        Area = Vector3d.Cross(b - a, c - a).Length / 2;

        if (la <= 0 || lb <= 0 || lc <= 0 || Area <= 0)
        {
            MinAngle = 0; MaxAngle = 180; AspectRatio = double.MaxValue; Skewness = 1;
            return;
        }

        // K01: УГЛЫ — ЧЕРЕЗ atan2(|u×v|, u·v), А НЕ ЧЕРЕЗ acos ПО ТЕОРЕМЕ КОСИНУСОВ. У иглы
        // площадью 1.8·10⁻⁷ мм² (сферический вырез, грань z=20) косинус малого угла округлялся
        // ровно до единицы, acos давал 0°, и годный треугольник объявлялся выродившимся;
        // сварка сетки его выбрасывала и открывала оболочку. atan2 сохраняет относительную
        // точность малых углов: 3·10⁻⁷° считается как 3·10⁻⁷°, а не как 0.
        double A = VertexAngle(b - a, c - a), B = VertexAngle(c - b, a - b), C = VertexAngle(a - c, b - c);

        MinAngle = Math.Min(A, Math.Min(B, C));
        MaxAngle = Math.Max(A, Math.Max(B, C));

        // Вписанная окружность: r = 2S / периметр. У равностороннего длиннейшая сторона равна
        // 2√3·r, на это и делим, чтобы единица отвечала лучшему случаю.
        double r = 2 * Area / (la + lb + lc);
        AspectRatio = Math.Max(la, Math.Max(lb, lc)) / (2 * Math.Sqrt(3) * r);

        // Перекос по углам: насколько наихудший угол отошёл от 60°.
        Skewness = Math.Max((MaxAngle - 60) / 120, (60 - MinAngle) / 60);
    }

    /// <summary>Угол между сторонами u и v при общей вершине, в градусах; устойчив для малых углов.</summary>
    private static double VertexAngle(Vector3d u, Vector3d v) =>
        Math.Atan2(Vector3d.Cross(u, v).Length, Vector3d.Dot(u, v)) * 180 / Math.PI;

    /// <summary>Годен ли треугольник при обычных для расчёта требованиях.</summary>
    public bool Good(double minAngle = 20, double maxSkewness = 0.85) =>
        MinAngle >= minAngle && Skewness <= maxSkewness && Area > 0;
}

/// <summary>Сводка по сетке целиком.</summary>
public readonly struct MeshQualityReport
{
    public readonly int Triangles;
    public readonly int Degenerate;
    public readonly int Poor;
    public readonly double WorstSkewness;
    public readonly double MinAngle;
    public readonly double MedianSkewness;
    public readonly double WorstAspect;

    public MeshQualityReport(int triangles, int degenerate, int poor,
                             double worstSkewness, double minAngle, double medianSkewness, double worstAspect)
    {
        Triangles = triangles; Degenerate = degenerate; Poor = poor;
        WorstSkewness = worstSkewness; MinAngle = minAngle;
        MedianSkewness = medianSkewness; WorstAspect = worstAspect;
    }

    /// <summary>Готова ли сетка к расчёту: без выродившихся и с приемлемым перекосом.</summary>
    public bool FitForAnalysis => Triangles > 0 && Degenerate == 0 && WorstSkewness <= 0.95;

    public override string ToString() =>
        $"треугольников {Triangles}, выродившихся {Degenerate}, плохих {Poor}; " +
        $"перекос серединный {MedianSkewness:0.###}, наихудший {WorstSkewness:0.###}; " +
        $"наименьший угол {MinAngle:0.#}°, наибольшее отношение сторон {WorstAspect:0.#}";
}

/// <summary>
/// КАЧЕСТВО СЕТКИ. Наша сетка строилась для показа: важно было, чтобы она повторяла форму, а не
/// чтобы треугольники были хороши собой. Расчёту нужно обратное — вытянутый или перекошенный
/// элемент даёт неверные напряжения, и большинство отказов построителей объёмных сеток идёт
/// именно от плохой поверхностной.
///
/// Здесь считаются те же мерки, что приняты в расчётных пакетах: наименьший угол, отношение
/// сторон, перекос. По ним и говорится, годна ли сетка, — числом, а не на глаз.
///
/// ОТКУДА БЕРУТСЯ ВЫРОДИВШИЕСЯ — замер по видам поверхностей на худших телах прогона:
///
///   Extruded×8  + Plane×3 + Revolved×2            207 выродившихся из 4050
///   Extruded×7  + Plane×4 + Revolved×1            143 из 4370
///   Extruded×15 + Plane×7                         122 из 1518   ← БЕЗ поверхностей вращения
///   Extruded×10 + Plane×8 + Revolved×3 + Section×2 118 из 5818
///
/// Третья строка решает дело: тело без единой поверхности вращения даёт 122 выродившихся.
/// Значит, полюса шара и конуса (их исправили) были лишь одним из источников.
///
/// А ГДЕ ГЛАВНЫЙ — показало сравнение граней порознь с собранным телом:
///
///   граней размечено порознь 11 из 13, выродившихся в них 0, в собранном теле 207
///   граней порознь 20 из 22,            выродившихся в них 0, в собранном теле 122
///   граней порознь 13 из 16,            выродившихся в них 0, в собранном теле  83
///
/// Каждая грань по отдельности размечается ЧИСТО. Выродившиеся появляются при СБОРКЕ тела:
/// точки соседних граней сводятся к общим местам (привязка), и треугольник, у которого две
/// вершины сошлись, вырождается.
///
/// Значит, чинить надо не разметку граней, а сведение точек при сборке: после него нужно
/// убирать треугольники, у которых две вершины совпали. Заодно видно, что часть граней вообще
/// не размечается точным путём (11 из 13, 15 из 23) — это отдельный вопрос.
///
/// Всего по ста телам: 772 выродившихся из 158 812 треугольников.
///
/// ПЕРЕКОС: ГДЕ ОН ЖИВЁТ. Замер по видам поверхностей дал цилиндры с огромным отрывом — 58 837
/// плохих треугольников из 84 123. Деления по высоте (их добавили в гранение выдавливания) часть
/// сняли: годных к расчёту тел стало 11 из ста вместо трёх.
///
/// Но дальше замер показал неожиданное: у ОТДЕЛЬНОГО цилиндра плохих теперь НЕТ ВОВСЕ —
///
///   цилиндр R10 H20: 1600 треугольников, плохих 0, перекос 0.252
///   цилиндр R3  H40: 6144,               плохих 0, перекос 0.465
///   цилиндр R20 H5:   426,               плохих 0, перекос 0.279
///
/// — а у цилиндра ПОСЛЕ БУЛЕВОЙ грань несёт 100 треугольников, и плохи все сто. Причина в том,
/// что после булевой грань хранит СВОИ треугольники, пришедшие из её многоугольников, и гранение
/// поверхности к ним уже не применяется; точный построитель их тоже перенимает.
///
/// Значит, остаток перекоса живёт не в гранении поверхностей, а в многоугольниках булевой: она
/// режет грани длинными полосами. Чинить надо там — либо размечать такие грани заново точным
/// построителем, не перенимая её треугольники.
///
/// ПЕРВЫЙ ЗАМЕР ПО СТА ТЕЛАМ, и он показывает размер задачи:
///
///   годны к расчёту          4 тела из 100
///   треугольников            164 828
///   выродившихся             421
///   плохих (угол < 20° или перекос > 0.85)   83 045, то есть 50.4%
///   перекос наихудший: серединный по телам 0.996, крайний 1
///
/// Иначе говоря, наша сетка для показа хороша, а для расчёта негодна: половина треугольников
/// вытянута, и почти у каждого тела есть хотя бы один выродившийся или предельно перекошенный.
/// Это не изъян гранения — оно и не ставило такой цели: при гранении по прогибу хорды форма
/// треугольника никого не заботит, важно лишь повторить поверхность.
///
/// Значит, для расчёта нужна ОТДЕЛЬНАЯ сетка, строимая по другим правилам, а эти мерки — способ
/// увидеть, когда она получится.
/// </summary>
public static class MeshQuality
{
    /// <summary>Мерки всех треугольников тела.</summary>
    public static List<TriangleQuality> Measure(MeshData mesh)
    {
        var res = new List<TriangleQuality>(mesh.Indices.Length / 3);

        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            Vector3d a = At(mesh, mesh.Indices[i]);
            Vector3d b = At(mesh, mesh.Indices[i + 1]);
            Vector3d c = At(mesh, mesh.Indices[i + 2]);
            res.Add(new TriangleQuality(a, b, c));
        }

        return res;
    }

    /// <summary>Сводка по телу.</summary>
    public static MeshQualityReport Of(TSolid solid, double minAngle = 20, double maxSkewness = 0.85) =>
        Of(Tessellator.Tessellate(solid), minAngle, maxSkewness);

    public static MeshQualityReport Of(MeshData mesh, double minAngle = 20, double maxSkewness = 0.85)
    {
        var all = Measure(mesh);
        if (all.Count == 0) return new MeshQualityReport(0, 0, 0, 0, 0, 0, 0);

        int degenerate = 0, poor = 0;
        double worstSkew = 0, minAng = 180, worstAspect = 0;

        foreach (var t in all)
        {
            if (t.Area <= 0 || t.MinAngle <= 1e-9) { degenerate++; continue; }
            if (!t.Good(minAngle, maxSkewness)) poor++;

            worstSkew = Math.Max(worstSkew, t.Skewness);
            minAng = Math.Min(minAng, t.MinAngle);
            worstAspect = Math.Max(worstAspect, t.AspectRatio);
        }

        var skews = all.Where(t => t.Area > 0).Select(t => t.Skewness).OrderBy(x => x).ToList();
        double median = skews.Count == 0 ? 0 : skews[skews.Count / 2];

        return new MeshQualityReport(all.Count, degenerate, poor, worstSkew,
                                     minAng > 179 ? 0 : minAng, median, worstAspect);
    }

    private static Vector3d At(MeshData mesh, int index)
    {
        var v = mesh.Vertices[index].P;
        return new Vector3d(v.X, v.Y, v.Z);
    }
}
