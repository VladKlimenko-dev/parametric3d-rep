using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Voxels;

public enum SetOp { Unite, Subtract, Intersect }

/// <summary>
/// СВЕРКА БУЛЕВОЙ ОПЕРАЦИИ С НЕЗАВИСИМЫМ ОТВЕТОМ.
///
/// Булева операция над B-Rep не может проверить сама себя: то, что
/// проверяют, и есть то, чем проверяют. Здесь второй ответ берётся
/// из объёмной модели — по ячейкам пространства, где «внутри» решается
/// заливом, а не пересечением поверхностей. Способ другой, значит
/// и ошибается он по-другому, и совпадение двух ответов что-то значит.
///
/// СРАВНИВАЕТСЯ НЕ С ИСТИНОЙ, А С ОЖИДАНИЕМ. Сначала строится объёмная
/// модель каждого операнда, затем над НИМИ выполняется та же операция —
/// это поячеечное И/ИЛИ, ошибиться там негде. Полученное ожидание
/// сверяется с объёмной моделью того, что построила настоящая операция.
///
/// Обе модели строятся на ОДНОЙ сетке — иначе ячейки не соответствуют
/// друг другу и сравнивать нечего.
///
/// СОМНИТЕЛЬНОЕ НЕ СЧИТАЕТСЯ ОШИБКОЙ. Если хоть одна из трёх моделей
/// назвала ячейку граничной, про неё ничего не утверждается: там проходит
/// поверхность, и ответ зависит от разрешения, а не от правильности
/// операции. Расхождением считаются только ячейки, где все три стороны
/// высказались определённо и разошлись.
/// </summary>
public sealed class BooleanAudit
{
    /// <summary>Материал есть в результате, а по ожиданию его быть не должно.</summary>
    public int Extra { get; private set; }

    /// <summary>Материал должен быть, а в результате его нет.</summary>
    public int Missing { get; private set; }

    /// <summary>Ячейки, про которые ничего не утверждается: рядом поверхность.</summary>
    public int Unsure { get; private set; }

    public double CellVolume { get; private set; }

    public double ExtraVolume => Extra * CellVolume;
    public double MissingVolume => Missing * CellVolume;

    /// <summary>Точки расхождения — куда смотреть. Не больше горсти.</summary>
    public readonly List<Vector3d> ExtraAt = new();
    public readonly List<Vector3d> MissingAt = new();

    /// <summary>Объёмы по объёмным моделям: ожидаемый и полученный.</summary>
    public double ExpectedVolume { get; private set; }
    public double ResultVolume { get; private set; }

    public bool Ok => Extra == 0 && Missing == 0;

    private const int MaxSamples = 16;

    /// <summary>Общая рамка для нескольких тел: сетка обязана быть одна.</summary>
    public static Bounds3 Frame(params Bounds3[] boxes)
    {
        var all = Bounds3.Empty;
        foreach (var b in boxes) all.Add(b);
        return all;
    }

    /// <summary>
    /// Строит объёмные модели обоих операндов и результата на одной сетке
    /// и сверяет. Тела берутся до операции, поэтому звать надо, сохранив
    /// копии операндов.
    /// </summary>
    public static BooleanAudit Compare(IReadOnlyList<TSolid> a, IReadOnlyList<TSolid> b,
                                       IReadOnlyList<TSolid> result, SetOp op,
                                       int depth = Occupancy.DefaultDepth)
    {
        Bounds3 frame = Frame(BoundsOf(a), BoundsOf(b), BoundsOf(result));

        Occupancy oa = BuildOn(a, frame, depth);
        Occupancy ob = BuildOn(b, frame, depth);
        Occupancy orr = BuildOn(result, frame, depth);

        return Compare(oa, ob, orr, op);
    }

    public static BooleanAudit Compare(Occupancy a, Occupancy b, Occupancy result, SetOp op)
    {
        var audit = new BooleanAudit { CellVolume = result.CellVolume };

        if (a.Side != b.Side || a.Side != result.Side)
            throw new InvalidOperationException(
                "the three volumetric models must share one grid; build them with a common frame");

        int side = result.Side;
        int expectedInside = 0, resultInside = 0;

        for (int k = 0; k < side; k++)
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    CellState want = Expected(a.At(i, j, k), b.At(i, j, k), op);
                    CellState got = result.At(i, j, k);

                    if (want == CellState.Inside) expectedInside++;
                    if (got == CellState.Inside) resultInside++;

                    // Граница с любой стороны — не судим.
                    if (want == CellState.Boundary || got == CellState.Boundary)
                    {
                        audit.Unsure++;
                        continue;
                    }

                    if (want == got) continue;

                    Vector3d at = result.CentreOf(i, j, k);

                    if (got == CellState.Inside)
                    {
                        audit.Extra++;
                        if (audit.ExtraAt.Count < MaxSamples) audit.ExtraAt.Add(at);
                    }
                    else
                    {
                        audit.Missing++;
                        if (audit.MissingAt.Count < MaxSamples) audit.MissingAt.Add(at);
                    }
                }

        audit.ExpectedVolume = expectedInside * result.CellVolume;
        audit.ResultVolume = resultInside * result.CellVolume;

        return audit;
    }

    /// <summary>
    /// Чем ячейка обязана быть после операции.
    ///
    /// Определённый ответ даётся, только когда его дают оба операнда:
    /// «внутри минус граница» — это граница, а не материал.
    /// </summary>
    private static CellState Expected(CellState a, CellState b, SetOp op)
    {
        switch (op)
        {
            case SetOp.Unite:
                if (a == CellState.Inside || b == CellState.Inside) return CellState.Inside;
                if (a == CellState.Outside && b == CellState.Outside) return CellState.Outside;
                return CellState.Boundary;

            case SetOp.Subtract:
                if (b == CellState.Inside) return CellState.Outside;
                if (a == CellState.Outside) return CellState.Outside;
                if (a == CellState.Inside && b == CellState.Outside) return CellState.Inside;
                return CellState.Boundary;

            default:
                if (a == CellState.Inside && b == CellState.Inside) return CellState.Inside;
                if (a == CellState.Outside || b == CellState.Outside) return CellState.Outside;
                return CellState.Boundary;
        }
    }

    private static Occupancy BuildOn(IReadOnlyList<TSolid> bodies, Bounds3 frame, int depth)
    {
        var tris = new List<(Vector3d, Vector3d, Vector3d)>();
        var bounds = Bounds3.Empty;

        foreach (var body in bodies)
        {
            var md = Tess.Tessellator.Tessellate(body, facets: true);
            tris.AddRange(Occupancy.Triangles(md));
            bounds.Add(md.Bounds);
        }

        return Occupancy.Build(tris, bounds, depth, frame);
    }

    private static Bounds3 BoundsOf(IReadOnlyList<TSolid> bodies)
    {
        var b = Bounds3.Empty;
        foreach (var s in bodies) b.Add(s.Bounds);
        return b;
    }

    /// <summary>Короткий отчёт для строки состояния или предупреждений.</summary>
    public string Describe()
    {
        if (Ok)
            return $"Boolean audit: agrees with the volumetric model " +
                   $"(volume {ResultVolume:0.###}, {Unsure} cells near the surface not judged).";

        var parts = new List<string>();

        if (Extra > 0) parts.Add($"{Extra} cells of extra material ({ExtraVolume:0.###})");
        if (Missing > 0) parts.Add($"{Missing} cells of missing material ({MissingVolume:0.###})");

        string where = "";

        if (ExtraAt.Count > 0) where += $" first extra at {Show(ExtraAt[0])};";
        if (MissingAt.Count > 0) where += $" first missing at {Show(MissingAt[0])};";

        return $"Boolean audit: {string.Join(", ", parts)}. " +
               $"Expected volume {ExpectedVolume:0.###}, got {ResultVolume:0.###}.{where}";
    }

    private static string Show(Vector3d p) => $"({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})";
}
