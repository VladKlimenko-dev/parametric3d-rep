using OpenTK.Mathematics;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

/// <summary>Объёмная сетка: узлы и четвёрки их номеров.</summary>
public sealed class TetMesh
{
    /// <summary>
    /// ГРАНИЦА СЕТКИ: грани, принадлежащие ровно одному тетраэдру. Это и есть поверхность
    /// построенного тела — та, на которой расчёт задаёт нагрузки и закрепления.
    ///
    /// Она может не совпадать по разбиению с поверхностной сеткой, из которой строилась: на
    /// плоской грани диагональ могла лечь иначе. Геометрически это та же поверхность — те же
    /// плоскости и тот же обвод, — и для расчёта важна именно эта, своя: на неё опираются
    /// тетраэдры.
    /// </summary>
    public List<(int A, int B, int C)> BoundaryFaces()
    {
        var count = new Dictionary<(int, int, int), int>();
        var shape = new Dictionary<(int, int, int), (int, int, int)>();

        foreach (var t in Tets)
            foreach (var f in new[] { (t.A, t.B, t.C), (t.A, t.C, t.D), (t.A, t.D, t.B), (t.B, t.D, t.C) })
            {
                int a = f.Item1, b = f.Item2, c = f.Item3;
                var key = (Math.Min(a, Math.Min(b, c)), a + b + c - Math.Min(a, Math.Min(b, c)) - Math.Max(a, Math.Max(b, c)), Math.Max(a, Math.Max(b, c)));
                count[key] = count.GetValueOrDefault(key) + 1;
                shape[key] = f;
            }

        var res = new List<(int, int, int)>();
        foreach (var (key, n) in count)
            if (n == 1) res.Add(shape[key]);

        return res;
    }

    /// <summary>Площадь границы сетки — мерка её совпадения с поверхностью тела.</summary>
    public double BoundaryArea()
    {
        double sum = 0;
        foreach (var (a, b, c) in BoundaryFaces())
            sum += Vector3d.Cross(Nodes[b] - Nodes[a], Nodes[c] - Nodes[a]).Length / 2;
        return sum;
    }

    public readonly List<Vector3d> Nodes = new();
    public readonly List<(int A, int B, int C, int D)> Tets = new();

    public int NodeCount => Nodes.Count;
    public int TetCount => Tets.Count;

    /// <summary>Объём тетраэдра со знаком: отрицательный означает вывернутый.</summary>
    public double Volume((int A, int B, int C, int D) t)
    {
        Vector3d a = Nodes[t.A], b = Nodes[t.B], c = Nodes[t.C], d = Nodes[t.D];
        return Vector3d.Dot(b - a, Vector3d.Cross(c - a, d - a)) / 6;
    }

    /// <summary>Общий объём сетки: он и сравнивается с объёмом тела.</summary>
    public double TotalVolume()
    {
        double sum = 0;
        foreach (var t in Tets) sum += Math.Abs(Volume(t));
        return sum;
    }
}

/// <summary>Мерки качества одного тетраэдра.</summary>
public readonly struct TetQuality
{
    /// <summary>Объём (по модулю).</summary>
    public readonly double Volume;

    /// <summary>
    /// Отношение сторон: длиннейшее ребро к вписанной сфере, приведённое так, что у правильного
    /// тетраэдра выходит единица. Мерка принята в расчётных пакетах.
    /// </summary>
    public readonly double AspectRatio;

    /// <summary>Перекос: 0 у правильного, 1 у выродившегося.</summary>
    public readonly double Skewness;

    public TetQuality(Vector3d a, Vector3d b, Vector3d c, Vector3d d)
    {
        Volume = Math.Abs(Vector3d.Dot(b - a, Vector3d.Cross(c - a, d - a))) / 6;

        double[] edges =
        {
            (b - a).Length, (c - a).Length, (d - a).Length,
            (c - b).Length, (d - b).Length, (d - c).Length
        };

        double longest = edges.Max();

        // Площадь поверхности — для вписанной сферы: r = 3V / S.
        double surface = Area(a, b, c) + Area(a, b, d) + Area(a, c, d) + Area(b, c, d);

        if (Volume <= 1e-15 || surface <= 0 || longest <= 0)
        {
            AspectRatio = double.MaxValue;
            Skewness = 1;
            return;
        }

        double r = 3 * Volume / surface;

        // У правильного тетраэдра длиннейшее ребро равно r·2√6.
        AspectRatio = longest / (r * 2 * Math.Sqrt(6));

        // Перекос через объём: сравнение с правильным тетраэдром на том же длиннейшем ребре.
        double ideal = longest * longest * longest / (6 * Math.Sqrt(2));
        Skewness = Math.Clamp(1 - Volume / ideal, 0, 1);
    }

    private static double Area(Vector3d a, Vector3d b, Vector3d c) =>
        Vector3d.Cross(b - a, c - a).Length / 2;

    public bool Good(double maxAspect = 10, double maxSkewness = 0.9) =>
        Volume > 1e-12 && AspectRatio <= maxAspect && Skewness <= maxSkewness;
}

/// <summary>Сводка по объёмной сетке.</summary>
public readonly struct TetMeshReport
{
    public readonly int Tets, Nodes, Degenerate, Poor, Inverted;
    public readonly double WorstAspect, MedianSkewness, Volume;

    public TetMeshReport(int tets, int nodes, int degenerate, int poor, int inverted,
                         double worstAspect, double medianSkewness, double volume)
    {
        Tets = tets; Nodes = nodes; Degenerate = degenerate; Poor = poor; Inverted = inverted;
        WorstAspect = worstAspect; MedianSkewness = medianSkewness; Volume = volume;
    }

    public bool FitForAnalysis => Tets > 0 && Degenerate == 0 && Inverted == 0;

    public override string ToString() =>
        $"тетраэдров {Tets}, узлов {Nodes}, выродившихся {Degenerate}, вывернутых {Inverted}, " +
        $"плохих {Poor}; перекос серединный {MedianSkewness:0.###}, " +
        $"наибольшее отношение сторон {WorstAspect:0.#}, объём {Volume:0.###}";
}

/// <summary>
/// ОБЪЁМНАЯ СЕТКА — то, чем считают напряжения. Поверхностная сетка нужна как её граница, но
/// сами уравнения решают на тетраэдрах.
///
/// Построение идёт по Делоне, вставкой точек (приём Бойера — Ватсона): все точки собираются
/// заранее, накрываются одним большим тетраэдром, затем вставляются по одной — каждая сносит
/// тетраэдры, в чьи описанные сферы она попала, и заполняет освободившееся место заново.
///
/// Затем лишнее отбрасывается: Делоне строит выпуклую оболочку, а деталь выпуклой не обязана
/// быть. Тетраэдр остаётся, если его середина лежит ВНУТРИ тела — это проверяется лучом по
/// поверхностной сетке, тем же способом, каким считают принадлежность точки телу.
///
/// ПЕРВЫЕ ЗАМЕРЫ на бруске 40×30×20 (расчётный объём 24 000):
///
///   шаг 20 мм    15 тетраэдров,  объём 19 000,  перекос серединный 0.741
///   шаг 12 мм   118,             объём 22 753,  0.730
///   шаг  8 мм   393,             объём 31 166,  0.739, выродившихся 16
///
/// Сетка строится, но объём пока не сходится: при крупном шаге он меньше тела (границу
/// тетраэдры не повторяют), при мелком — БОЛЬШЕ, а это значит, что часть тетраэдров
/// перекрывается.
///
/// НАЛОЖЕНИЯ УСТРАНЕНЫ, и решило дело не то, что я пробовал сперва:
///   — знак объёма к единому виду и пропуск невидимых граней полости убрали выродившиеся
///     тетраэдры (16 → 0), но объём почти не изменили (31 166 → 30 557);
///   — ПРОВЕРКА «ВНУТРИ ОПИСАННОЙ СФЕРЫ» В ЦЕЛЫХ ЧИСЛАХ сняла наложения разом:
///     30 557 → 21 147 при расчётных 24 000, плохих 76 → 42, наибольшее отношение сторон 44 → 12.
///
/// Причина именно такая: у бруска множество точек лежит на ОБЩИХ сферах — углы, середины рёбер,
/// узлы правильной сетки. Для них определитель равен нулю, и дробный счёт даёт то чуть больше,
/// то чуть меньше нуля; полость выходит незамкнутой. В целых числах ноль — ровно ноль.
///
/// НЕДОБОР ТОЖЕ УСТРАНЁН, и снова не там, где казалось. Я думал, что дело в согласовании с
/// границей — что тетраэдры не повторяют поверхность тела. Замер показал обратное: сама
/// триангуляция даёт РОВНО 24 000, а теряет ОТБОР. Он проверял принадлежность точки телу одним
/// лучом вида (1,1,1), а у прямоугольного тела такой луч идёт вдоль рёбер сетки: пересечения
/// считались дважды или терялись, и годные тетраэдры отбрасывались (до 5 000 мм³ из 24 000).
///
/// Три косых луча с решением по большинству снимают это:
///
///   шаг 20 мм    32 тетраэдра,  объём 24 000 ровно, отброшено 0
///   шаг 12 мм   129,            объём 24 000 ровно, отброшено 0
///   шаг  8 мм   358,            отброшено 320 из 24 000 (1.3%)
///
/// ПОЛОСТИ ВЫРЕЗАЮТСЯ. Это далось не сразу, и отвергнутые замыслы стоит помнить:
///   — отбор по нескольким точкам тетраэдра: отверстию помогает, но брусок теряет объём у
///     границы сильнее (23 680 → 21 624);
///   — восстановление границы вставкой середин недостающих граней: расшатывает построение
///     (брусок 24 000 → 36 886);
///   — вставка точек в местах, где ребро протыкает поверхность: таких мест НЕТ ВОВСЕ (ноль при
///     любом шаге), приём бессилен.
///
/// Сработало иное: тетраэдр не наш, если хоть одно его РЕБРО протыкает поверхность. Тетраэдр,
/// перекрывший отверстие, обязательно проткнёт его стенку, хотя серединой лежит в материале. У
/// выпуклого тела таких рёбер нет — поэтому брусок от проверки не пострадал:
///
///   тело с отверстием, шаг 10   объём 21 764 при расчётных 21 738 (0.1%)
///   брусок, шаг 20 и 12         24 000 ровно
///   брусок, шаг 8               23 680
///
/// НАЛОЖЕНИЯ СНЯТЫ И НА ТЕЛАХ С ПОЛОСТЯМИ. Причина оказалась в СВЯЗНОСТИ полости: заполнение
/// гранями «встреченными однажды» верно только тогда, когда снесённые тетраэдры образуют один
/// кусок. У тела с отверстием они распадались на два, и общая граница описывала две отдельные
/// области — отсюда наложения.
///
/// До и после проверки связности:
///
///   тело с отверстием, триангуляция   30 459  →  24 000 (ровно выпуклая оболочка)
///   тело с отверстием, шаг 7          не строилось  →  564 тетраэдра, объём 21 448
///   тело с отверстием, шаг 10         21 764 (со взаимным гашением)  →  20 104 (честно)
///   брусок                            24 000 ровно  →  24 000 ровно
///
/// Расчётный объём тела с отверстием 21 738: теперь сетка его НЕДОБИРАЕТ (на 1.3% при шаге 7),
/// а не набирает случайным гашением лишнего и недостающего. Недобор идёт от точек, пропущенных
/// из-за несвязной полости, и это следующая задача — вставлять их иначе, а не пропускать.
///
/// Отвергнутые по дороге замыслы записаны выше; каждый стоил хода, но каждый сузил поиск.
///
/// Чего здесь нет и что нужно для настоящего расчёта: согласования с границей (сейчас
/// поверхность тела не является в точности гранями тетраэдров) и улучшения формы. Это следующие
/// шаги; сперва надо получить сетку, объём которой сходится.
/// </summary>
public static class TetMesher
{
    /// <summary>Построить объёмную сетку с заданным размером элемента.</summary>
    public static TetMesh Build(TSolid solid, double size, List<string>? warnings = null)
    {
        var surface = AnalysisMesher.Build(solid, size, warnings);
        return Build(surface, size, warnings);
    }

    /// <summary>Построить объёмную сетку по готовой поверхностной.</summary>
    public static TetMesh Build(MeshData surface, double size, List<string>? warnings = null)
    {
        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Element size must be finite and positive.");
        var mesh = new TetMesh();
        if (!surface.Whole)
        {
            warnings?.Add("Tet mesh: input surface has broken faces; volume meshing was not started.");
            return mesh;
        }

        var boundary = Unique(surface);
        if (boundary.Count < 4)
        {
            warnings?.Add("Tet mesh: the surface mesh is too small.");
            return mesh;
        }

        var inner = InnerPoints(surface, boundary, size);

        // ПОЧТИ СОВПАДАЮЩИЕ ТОЧКИ ОТСЕИВАЮТСЯ. Вставка двух точек, стоящих вплотную, ломает
        // построение по Делоне: полость вокруг второй выходит вырожденной, и заполнение даёт
        // груду тетраэдров. Измерено на теле с отверстием: 172 узла давали 36 449 тетраэдров,
        // хотя должно быть около восьмисот.
        var points = new List<Vector3d>();
        double apart = size * 0.05;

        foreach (var p in boundary.Concat(inner))
            if (!points.Any(q => (q - p).Length < apart)) points.Add(p);

        // ВОССТАНОВЛЕНИЕ ГРАНИЦЫ. Делоне ничего не знает о том, что поверхность тела должна
        // лежать на гранях тетраэдров; пока она там не лежит, тетраэдр может перекрыть полость.
        //
        // Вставка середин недостающих граней уже пробовалась и расшатывала построение (брусок
        // 24 000 → 36 886). Здесь иначе: точка ставится там, где РЕБРО тетраэдра пересекает
        // поверхность. Заход принимается, только если недостающих граней стало МЕНЬШЕ.
        //
        // ИЗМЕРЕНО, ЧТО ЭТОТ ХОД НЕ СРАБАТЫВАЕТ, и это ценное знание: пересечений НЕТ ВОВСЕ (ноль
        // при любом шаге), а недостающих граней мало — 2 из 28, 8 из 68, 20 из 116. Значит, дело
        // не в том, что рёбра протыкают поверхность: все точки поверхности в сетке есть, и
        // тетраэдры её не пересекают. Грань не совпадает потому, что плоский участок поверхности
        // разбит на треугольники ОДНОЙ диагональю, а сетка выбрала другую.
        //
        // Отсюда правильный следующий шаг: не вставлять точки, а согласовать разбиение —
        // перестроить поверхностные треугольники по диагоналям, выбранным объёмной сеткой, либо
        // развернуть грани сетки под поверхность. Вставки здесь бессильны по существу.
        // ТОЧКИ ПРИВОДЯТСЯ К ТОЙ ЖЕ СЕТКЕ, НА КОТОРОЙ СЧИТАЮТ ПРЕДИКАТЫ. Проверка описанной сферы
        // и знак объёма считаются в целых числах с шагом 1e-4 мм, а сами точки лежат где угодно.
        // Пока это так, «точность» мнимая: два предиката могут разойтись на величине меньше шага
        // округления, и полость выходит несвязной. Если же точки ЛЕЖАТ на этой сетке, целая
        // арифметика описывает их в точности.
        // ВОЗМУЩЕНИЕ ДО ПОСТРОЕНИЯ, А НЕ В ПРЕДИКАТЕ. Точки приводятся к сетке предикатов
        // (шаг 1e-6 мм) и тут же сдвигаются на величину, зависящую от НОМЕРА точки. Сдвиг меньше
        // допуска склейки в тысячу раз, то есть геометрии не меняет, но делает невозможным точное
        // равенство: пять точек больше не лежат на одной сфере, и определитель не обращается в
        // ноль. Полость тогда не может развалиться.
        //
        // Возмущать надо ДО построения: попытка решать ничьи внутри предиката сравнением номеров
        // дала худшие наложения из виденных (брусок 24 000 → 28 000), потому что такое правило не
        // согласовано между четвёрками.
        for (int i = 0; i < points.Count; i++)
        {
            double jitter = ((i * 2654435761L) % 1000) * 1e-6;

            points[i] = new Vector3d(Math.Round(points[i].X * 1e6 + jitter) / 1e6,
                                     Math.Round(points[i].Y * 1e6 + jitter * 0.37) / 1e6,
                                     Math.Round(points[i].Z * 1e6 + jitter * 0.73) / 1e6);
        }

        var tets = Delaunay(points);

        // ВОССТАНОВЛЕНИЕ ГРАНИЦЫ ИДЁТ В ДВА ЭТАПА, И ПОРЯДОК ЗДЕСЬ НЕ ПРОИЗВОЛЕН. Сперва рёбра,
        // потом грани: грань не может лечь на место, пока её стороны не являются рёбрами сетки.
        // Так устроено во всех настоящих построителях, и на то есть причина — восстановление
        // грани вставкой точек рушит уже восстановленные грани, если их стороны не закреплены.
        int missingEdges = RecoverEdges(surface, points, ref tets, size, warnings);
        if (missingEdges > 0) warnings?.Add($"Tet mesh: {missingEdges} boundary edge(s) not recovered.");

        // СПЕРВА ПЕРЕСТАНОВКИ, ПОТОМ ВСТАВКИ — тот же порядок, что и у рёбер. Грань, сквозь
        // которую проходят рёбра сетки, восстанавливается удалением этих рёбер перестановкой
        // 3→2; точка Штейнера нужна лишь там, где перестановка невозможна.
        {
            var place = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < points.Count; i++) place[PlaceKey(points[i])] = i;

            int byFlips = 0, byCavity = 0;
            for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
            {
                if (!place.TryGetValue(PlaceKey(At(surface, surface.Indices[i])), out int ia)) continue;
                if (!place.TryGetValue(PlaceKey(At(surface, surface.Indices[i + 1])), out int ib)) continue;
                if (!place.TryGetValue(PlaceKey(At(surface, surface.Indices[i + 2])), out int ic)) continue;

                if (TetFlips.HasFacet(tets, ia, ib, ic)) continue;

                // Сперва перестановки, затем полостное восстановление: второе мощнее, но и
                // рискованнее — оно сносит целый кусок сетки и заполняет заново.
                if (TetFlips.RecoverFacet(points, tets, ia, ib, ic)) { byFlips++; continue; }
                if (TetFlips.RecoverFacetByCavity(points, tets, ia, ib, ic)) byCavity++;
            }

            // Разбор оставшихся: мешают ли грани рёбра сетки. Если ни одно ребро её не
            // пересекает, грань «недостаёт» лишь потому, что на плоском участке диагональ легла
            // иначе, и вреда от этого нет — у бруска объём при таких гранях точен.
            int blocked = 0, harmless = 0;

            for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
            {
                if (!place.TryGetValue(PlaceKey(At(surface, surface.Indices[i])), out int ja)) continue;
                if (!place.TryGetValue(PlaceKey(At(surface, surface.Indices[i + 1])), out int jb)) continue;
                if (!place.TryGetValue(PlaceKey(At(surface, surface.Indices[i + 2])), out int jc)) continue;
                if (TetFlips.HasFacet(tets, ja, jb, jc)) continue;

                bool crossed = false;
                foreach (var t in tets)
                {
                    var n = new[] { t.A, t.B, t.C, t.D };
                    for (int x = 0; x < 4 && !crossed; x++)
                        for (int y = x + 1; y < 4 && !crossed; y++)
                        {
                            if (n[x] == ja || n[x] == jb || n[x] == jc) continue;
                            if (n[y] == ja || n[y] == jb || n[y] == jc) continue;
                            if (Crosses(points[n[x]], points[n[y]], points[ja], points[jb], points[jc]))
                                crossed = true;
                        }
                    if (crossed) break;
                }

                if (!crossed) { harmless++; continue; }

                blocked++;

                // Разбор поимённо: чем именно мешает и почему не вышло. Граней таких единицы, и
                // статистика здесь не нужна — нужен точный ответ по каждой.
                int ring = 0;
                foreach (var t in tets)
                {
                    var n = new[] { t.A, t.B, t.C, t.D };
                    for (int x = 0; x < 4; x++)
                        for (int y = x + 1; y < 4; y++)
                        {
                            if (n[x] == ja || n[x] == jb || n[x] == jc) continue;
                            if (n[y] == ja || n[y] == jb || n[y] == jc) continue;
                            if (!Crosses(points[n[x]], points[n[y]], points[ja], points[jb], points[jc])) continue;

                            int count = 0;
                            foreach (var u in tets)
                            {
                                var m = new[] { u.A, u.B, u.C, u.D };
                                if (m.Contains(n[x]) && m.Contains(n[y])) count++;
                            }

                            if (ring == 0) ring = count;
                        }
                }

                warnings?.Add($"Tet mesh: blocked facet, ring {ring}, refusal: {TetFlips.LastRefusal}.");
            }

            warnings?.Add($"Tet mesh: missing facets — blocked by edges {blocked}, diagonal only {harmless}.");

            int stillMissing = MissingFacets(surface, points, tets).Count;
            warnings?.Add($"Tet mesh: facets recovered {byFlips} by flips, {byCavity} by cavity; " +
                          $"{stillMissing} of {surface.Indices.Length / 3} still missing.");
        }

        int missingBefore = MissingFacets(surface, points, tets).Count;
        int added = 0;

        for (int round = 0; round < 5 && missingBefore > 0 && tets.Count > 0; round++)
        {
            // ТОЧКИ СТАВЯТСЯ НА САМИХ МЕШАЮЩИХ ГРАНЯХ. Прежде они ставились в местах, где ребро
            // протыкает поверхность, и это ничего не давало: мешающие грани оставались целыми.
            // Разбор показал, что мешают граничные рёбра, которые нельзя убрать перестановкой —
            // их кольцо не замкнуто. Такую грань надо разделить, и делить её надо саму.
            var extra = BlockedFacetCentres(surface, points, tets, size);
            if (extra.Count == 0) extra = CrossingPoints(surface, points, tets, size);
            if (extra.Count == 0) break;

            // ТОЧКА ШТЕЙНЕРА ВХОДИТ В ОБЕ СЕТКИ СРАЗУ. Прежде она вставлялась только в объёмную,
            // а поверхностная оставалась прежней: треугольник, сквозь который прошла вставка,
            // после этого не мог совпасть ни с одной гранью — он крупнее любой из них. Оттого
            // недостающих граней и становилось БОЛЬШЕ (28 → 30, 61 → 69).
            //
            // Теперь точка делит треугольник поверхности на три, и обе сетки остаются
            // согласованными.
            var splitSurface = SplitSurfaceAt(surface, extra);

            var trial = new List<Vector3d>(points);
            trial.AddRange(extra);

            var candidate = Delaunay(trial);
            if (candidate.Count == 0) break;

            // УСПЕХ МЕРЯЕТСЯ ЧИСЛОМ МЕШАЮЩИХ ГРАНЕЙ. Ни счёт всех недостающих, ни их площадь не
            // годятся: деление грани на три добавляет цели, а площади не меняет — обе мерки
            // отвергали заход всегда. Важны же только те грани, сквозь которые проходят рёбра
            // сетки: безвредные (там, где разошлась лишь диагональ) считать незачем.
            int wasBlocked = BlockedFacetCentres(surface, points, tets, size).Count;
            int nowBlocked = BlockedFacetCentres(splitSurface, trial, candidate, size).Count;
            if (nowBlocked >= wasBlocked) break;

            int missingNow = MissingFacets(splitSurface, trial, candidate).Count;

            points.Clear();
            points.AddRange(trial);
            tets = candidate;
            surface = splitSurface;
            missingBefore = missingNow;
            added += extra.Count;
        }

        if (added > 0)
            warnings?.Add($"Tet mesh: {added} point(s) added to recover the boundary, " +
                          $"{missingBefore} facet(s) still missing.");

        if (tets.Count == 0)
        {
            warnings?.Add("Tet mesh: the Delaunay construction did not converge on this body.");
            return mesh;
        }

        double whole = 0, dropped = 0;
        foreach (var t in tets)
            whole += Math.Abs(Vector3d.Dot(points[t.B] - points[t.A],
                              Vector3d.Cross(points[t.C] - points[t.A], points[t.D] - points[t.A]))) / 6;

        // ОТБОР РАСТЕКАНИЕМ. Прежде каждый тетраэдр отбирался порознь — по середине и по проколу
        // рёбер, — и это оставляло ДЫРЫ внутри тела: там, где прокол случаен (почти касательное
        // ребро), годный тетраэдр отбрасывался. Измерено по площади границы: у тела с отверстием
        // она выходила 5 984 при расчётных 5 728, и излишек — это стенки пустот.
        //
        // Здесь иначе: от заведомо внутреннего тетраэдра отбор РАСТЕКАЕТСЯ по соседям и
        // останавливается на гранях, лежащих на поверхности тела. Дыр не возникает по
        // устройству: внутрь области попадает всё, до чего можно дойти, не пересекая границы.
        var neighbours = new Dictionary<(int, int, int), List<int>>();

        for (int k = 0; k < tets.Count; k++)
            foreach (var f in FacesOf(tets[k]))
            {
                var key = Key(f);
                if (!neighbours.TryGetValue(key, out var list)) neighbours[key] = list = new List<int>(2);
                list.Add(k);
            }

        // Начало: самый глубокий внутри тела тетраэдр — у него ошибиться труднее всего.
        int seed = -1;
        double deepest = 0;

        for (int k = 0; k < tets.Count; k++)
        {
            var t = tets[k];
            Vector3d c = (points[t.A] + points[t.B] + points[t.C] + points[t.D]) / 4;
            if (!Inside(surface, c)) continue;

            double d = DistanceToSurface(surface, c);
            if (d <= deepest) continue;
            deepest = d;
            seed = k;
        }

        if (seed < 0)
        {
            warnings?.Add("Tet mesh: no tetrahedron lies inside the body.");
            return mesh;
        }

        // Треугольники поверхности — по положениям вершин: у объёмной сетки свои номера.
        var surfaceFacets = new HashSet<(long, long, long)>();
        for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
            surfaceFacets.Add(FaceKey(At(surface, surface.Indices[i]),
                                      At(surface, surface.Indices[i + 1]),
                                      At(surface, surface.Indices[i + 2])));

        var keep = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);

        while (queue.Count > 0)
        {
            int k = queue.Dequeue();

            foreach (var f in FacesOf(tets[k]))
            {
                // СТЕНА ОПРЕДЕЛЯЕТСЯ ПЕРЕХОДОМ, А НЕ САМОЙ ГРАНЬЮ. Совпадение грани с
                // треугольником поверхности не годится: на стенке отверстия разбиение иное, и
                // растекание уходило сквозь неё (объём выходил как у сплошного тела). Мерка по
                // расстоянию тоже не годится — при малом пороге та же утечка, при большом
                // растекание глохнет (объём падал вчетверо).
                //
                // Здесь проверяется сам переход: отрезок между серединами двух соседних
                // тетраэдров. Если он пересекает поверхность — значит, соседи по разные стороны
                // тела, и ходу туда нет. Совпадения разбиений это не требует.
                Vector3d hereMid = Centre(points, tets[k]);

                foreach (int other in neighbours[Key(f)])
                {
                    if (other == k || keep.Contains(other)) continue;

                    Vector3d thereMid = Centre(points, tets[other]);
                    if (CrossesSurfaceSegment(surface, hereMid, thereMid)) continue;

                    keep.Add(other);
                    queue.Enqueue(other);
                }
            }
        }

        // Узлы кладутся ДО подсчёта объёма: он спрашивает их у сетки.
        mesh.Nodes.AddRange(points);

        foreach (int k in keep)
        {
            var t = tets[k];

            // РАСТЕКАНИЕ ДОПОЛНЯЕТСЯ ПРОВЕРКОЙ ПРОКОЛА. Одного растекания мало: на снимке вида
            // объёмной сетки видны рёбра, протянутые СКВОЗЬ отверстие. Такой тетраэдр растекание
            // пропускает — переход к соседу обходит стенку стороной, — но ребро его обязательно
            // протыкает поверхность, и это ловится точно.
            //
            // Проверка по СЕРЕДИНЕ для этого не годится: она отсекает и годные тетраэдры у
            // границы (брусок терял объём, 24 000 → 23 680), а прокол у выпуклого тела не
            // случается вовсе.
            if (CrossesSurface(surface, points, t)) continue;

            double v = Vector3d.Dot(points[t.B] - points[t.A],
                                    Vector3d.Cross(points[t.C] - points[t.A], points[t.D] - points[t.A]));

            mesh.Tets.Add(v < 0 ? (t.A, t.C, t.B, t.D) : t);
        }

        dropped = whole - mesh.TotalVolume();

        warnings?.Add($"Tet mesh: {mesh.TetCount} tetrahedra on {mesh.NodeCount} nodes " +
                      $"({inner.Count} inner points); Delaunay volume {whole:0.#}, dropped {dropped:0.#}.");
        return mesh;
    }

    /// <summary>Сводка качества.</summary>
    public static TetMeshReport Measure(TetMesh mesh, double maxAspect = 10, double maxSkewness = 0.9)
    {
        if (mesh.TetCount == 0) return new TetMeshReport(0, mesh.NodeCount, 0, 0, 0, 0, 0, 0);

        int degenerate = 0, poor = 0, inverted = 0;
        double worstAspect = 0, volume = 0;
        var skews = new List<double>();

        foreach (var t in mesh.Tets)
        {
            var q = new TetQuality(mesh.Nodes[t.A], mesh.Nodes[t.B], mesh.Nodes[t.C], mesh.Nodes[t.D]);

            if (q.Volume <= 1e-12) { degenerate++; continue; }
            if (mesh.Volume(t) < 0) inverted++;
            if (!q.Good(maxAspect, maxSkewness)) poor++;

            worstAspect = Math.Max(worstAspect, q.AspectRatio);
            skews.Add(q.Skewness);
            volume += q.Volume;
        }

        skews.Sort();
        double median = skews.Count == 0 ? 1 : skews[skews.Count / 2];

        return new TetMeshReport(mesh.TetCount, mesh.NodeCount, degenerate, poor, inverted,
                                 worstAspect, median, volume);
    }

    // ------------------------------------------------------------------ построение

    /// <summary>
    /// ВОССТАНОВЛЕНИЕ РЁБЕР ГРАНИЦЫ. Каждая сторона поверхностного треугольника обязана быть
    /// ребром объёмной сетки: иначе грань на неё не обопрётся, и граница не восстановится.
    ///
    /// Приём тот же, что в промышленных построителях: недостающее ребро делится точкой Штейнера
    /// пополам, и обе половины восстанавливаются заново. Деление повторяется, пока ребро не
    /// станет ребром сетки или пока не кончится терпение — бесконечно дробить нельзя, и предел
    /// назван явно.
    ///
    /// Возвращает, сколько рёбер осталось невосстановленными.
    ///
    /// ПЕРЕСТАНОВКИ ПОСТАВЛЕНЫ ПЕРВЫМИ, И ЭТО ДАЛО РЕЗУЛЬТАТ. Прежде здесь было только деление
    /// пополам, и оно не сходилось вовсе (оставались 20, 67, 1, 4, 28 рёбер, объёмы не менялись).
    /// Теперь ребро сперва «пробивается» сквозь сетку перестройкой тех тетраэдров, которые оно
    /// пересекает, и только затем идёт деление:
    ///
    ///   тело с отверстием, шаг 10   объём 20 538 → 21 613 при расчётных 21 738 (0.6%)
    ///   тело с отверстием, шаг 7    21 214 → 21 324 (1.9%)
    ///   брусок                      24 000 ровно, без изменений
    ///
    /// ТОЧКИ ШТЕЙНЕРА ПОСТАВЛЕНЫ, НО ОСТАТОК ИМИ НЕ БЕРЁТСЯ, и разбор объясняет почему. Середины
    /// недостающих рёбер строятся исправно (18, 63, 28 штук по телам), сетка перестраивается,
    /// перестановки запускаются заново для половин — и общая длина невосстановленного не убывает.
    ///
    /// Значит, половины не восстанавливаются тоже, и причина у них та же, что у целых рёбер:
    /// перестановка ищет грань, которую ребро пересекает СТРОГО ВНУТРИ. У рёбер, лежащих в
    /// плоскости грани тела (а таких на плоской поверхности большинство), пересекаемых граней в
    /// этом смысле нет вовсе — знаки объёмов обращаются в ноль, и поиск ничего не находит.
    ///
    /// Это известный трудный случай: множество точек в одной плоскости.
    ///
    /// ОТБОР РАСТЕКАНИЕМ СНЯЛ ГЛАВНОЕ. Прежде каждый тетраэдр отбирался порознь, и это оставляло
    /// дыры внутри тела. Теперь отбор идёт от заведомо внутреннего тетраэдра по соседям и
    /// останавливается там, где переход между соседями пересекает поверхность.
    ///
    /// Стену пришлось определять с третьей попытки, и обе неудачные измерены:
    ///   — по расстоянию до поверхности: при малом пороге растекание уходит сквозь стенку
    ///     отверстия (объём как у сплошного тела), при большом глохнет (объём падает вчетверо);
    ///   — по совпадению грани с треугольником поверхности: на стенке отверстия разбиение иное,
    ///     и утечка та же;
    ///   — по ПЕРЕХОДУ между серединами соседей: работает, совпадения разбиений не требует.
    ///
    /// Что вышло:
    ///
    ///   брусок, все шаги             24 000 ровно (прежде при шаге 8 было 23 680)
    ///   тело с отверстием, шаг 10    22 017 при расчётных 21 738 (1.3%)
    ///   тело с отверстием, шаг 7     22 132 (1.8%), площадь границы 5 721.8 при расчётных 5 727.8
    ///
    /// Пустоты ушли: площадь границы при мелком шаге сходится с поверхностью тела почти точно.
    ///
    /// ОСТАТОК В 1.3–1.8% — ОТ НЕСОГЛАСОВАННОЙ ГРАНИЦЫ, и попытка её восстановить упирается в
    /// устойчивость перестройки. Измерено: у тела с отверстием недостающих граней 24 и 66, а
    /// проколов рёбер — 4 и 3, то есть вставлять есть что. Но перестройка со вставленными
    /// точками РАЗВАЛИВАЕТСЯ: срабатывает защита от разрастания, и заход отменяется целиком.
    ///
    /// У бруска проколов нет вовсе (0 при всех шагах), и недостающие грани там безвредны —
    /// объём и площадь точны.
    ///
    /// Значит, до восстановления граней надо довести устойчивость самой вставки: пока полость
    /// разваливается на почти соосферических наборах, любые добавочные точки будут её ломать.
    /// Это и есть корень, к которому сходятся все нерешённые части.
    /// </summary>
    private static int RecoverEdges(MeshData surface, List<Vector3d> points,
                                    ref List<(int A, int B, int C, int D)> tets,
                                    double size, List<string>? warnings)
    {
        var wanted = BoundaryEdges(surface);
        int added = 0;

        // Номера точек по их положению: восстановление работает с номерами, а границы заданы
        // местами.
        var byPlace = new Dictionary<(long, long, long), int>();
        for (int i = 0; i < points.Count; i++)
            byPlace[PlaceKey(points[i])] = i;

        int Index(Vector3d p) => byPlace.TryGetValue(PlaceKey(p), out int i) ? i : -1;

        for (int round = 0; round < 8; round++)
        {
            var have = MeshEdges(points, tets);
            var missing = wanted.Where(e => !have.Contains(EdgeKey(e.A, e.B))).ToList();
            if (missing.Count == 0) return 0;

            // СПЕРВА ПЕРЕСТАНОВКИ. Ребро «пробивается» сквозь сетку перестройкой тех тетраэдров,
            // которые оно пересекает; точку Штейнера ставят только там, где это не вышло.
            int byFlips = 0;
            foreach (var (a, b) in missing)
            {
                int ia = Index(a), ib = Index(b);
                if (ia < 0 || ib < 0) continue;
                if (TetFlips.RecoverEdge(points, tets, ia, ib)) byFlips++;
            }

            if (byFlips > 0)
            {
                have = MeshEdges(points, tets);
                missing = wanted.Where(e => !have.Contains(EdgeKey(e.A, e.B))).ToList();
                warnings?.Add($"Tet mesh: {byFlips} edge(s) recovered by flips, {missing.Count} left.");
                if (missing.Count == 0) return 0;
            }

            // ТОЧКА ШТЕЙНЕРА СТАВИТСЯ ТАМ, ГДЕ ПЕРЕСТАНОВКА ОТКАЗАЛА. Середина недостающего ребра
            // делит его надвое, и каждая половина восстанавливается заново — уже перестановками.
            // Так и устроено в настоящих построителях: дробление обслуживает перестройку, а не
            // заменяет её.
            var extra = new List<Vector3d>();
            foreach (var (a, b) in missing)
            {
                Vector3d mid = (a + b) / 2;
                if (points.Any(q => (q - mid).Length < size * 0.05)) continue;
                if (extra.Any(q => (q - mid).Length < size * 0.05)) continue;
                extra.Add(mid);
            }

            if (extra.Count == 0) return missing.Count;

            var trial = new List<Vector3d>(points);
            trial.AddRange(extra);

            var candidate = Delaunay(trial);
            if (candidate.Count == 0) return missing.Count;

            // Цели пересчитываются: у поделённых рёбер теперь по две половины.
            var goals = BoundaryEdges(surface, trial);

            // И снова перестановки — уже для половин.
            var place = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < trial.Count; i++) place[PlaceKey(trial[i])] = i;

            foreach (var (a, b) in goals)
            {
                if (!place.TryGetValue(PlaceKey(a), out int ia)) continue;
                if (!place.TryGetValue(PlaceKey(b), out int ib)) continue;
                TetFlips.RecoverEdge(trial, candidate, ia, ib);
            }

            var nowHave = MeshEdges(trial, candidate);
            var nowMissingList = goals.Where(e => !nowHave.Contains(EdgeKey(e.A, e.B))).ToList();

            // УСПЕХ МЕРЯЕТСЯ ДЛИНОЙ, А НЕ ЧИСЛОМ. После деления целей становится вдвое больше, и
            // счёт с прежним несравним: восемнадцать невосстановленных рёбер превращаются в
            // тридцать шесть половин, и любой заход выглядел бы неудачей. Общая длина
            // невосстановленного при делении убывает — её и сравниваем.
            double wasLength = missing.Sum(e => (e.B - e.A).Length);
            double nowLength = nowMissingList.Sum(e => (e.B - e.A).Length);

            if (nowLength >= wasLength - 1e-9) return missing.Count;

            int nowMissing = nowMissingList.Count;

            points.Clear();
            points.AddRange(trial);
            tets = candidate;
            added += extra.Count;
            wanted = goals;
        }

        if (added > 0) warnings?.Add($"Tet mesh: {added} Steiner point(s) added to recover edges.");
        return 0;
    }

    /// <summary>Стороны поверхностных треугольников — то, что обязано стать рёбрами сетки.</summary>
    private static List<(Vector3d A, Vector3d B)> BoundaryEdges(MeshData surface, List<Vector3d>? split = null)
    {
        var res = new List<(Vector3d, Vector3d)>();
        var seen = new HashSet<(long, long)>();

        for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                Vector3d a = At(surface, surface.Indices[i + k]);
                Vector3d b = At(surface, surface.Indices[i + (k + 1) % 3]);

                var key = EdgeKey(a, b);
                if (!seen.Add(key)) continue;

                // Если ребро уже поделено точкой Штейнера, целями становятся половинки.
                Vector3d? cut = split?.FirstOrDefault(p => OnSegment(a, b, p));
                if (cut is { } c && (c - a).Length > 1e-9 && (c - b).Length > 1e-9)
                {
                    res.Add((a, c));
                    res.Add((c, b));
                }
                else res.Add((a, b));
            }

        return res;
    }

    /// <summary>Лежит ли точка на отрезке (в пределах допуска сетки).</summary>
    private static bool OnSegment(Vector3d a, Vector3d b, Vector3d p)
    {
        Vector3d d = b - a;
        double len2 = d.LengthSquared;
        if (len2 < 1e-18) return false;

        double t = Vector3d.Dot(p - a, d) / len2;
        if (t <= 1e-6 || t >= 1 - 1e-6) return false;

        return (p - (a + d * t)).Length < 1e-6;
    }

    /// <summary>Рёбра объёмной сетки — по положениям концов.</summary>
    private static HashSet<(long, long)> MeshEdges(List<Vector3d> points, List<(int A, int B, int C, int D)> tets)
    {
        var res = new HashSet<(long, long)>();

        foreach (var t in tets)
        {
            void Add(int i, int j) => res.Add(EdgeKey(points[i], points[j]));
            Add(t.A, t.B); Add(t.A, t.C); Add(t.A, t.D);
            Add(t.B, t.C); Add(t.B, t.D); Add(t.C, t.D);
        }

        return res;
    }

    private static (long, long, long) PlaceKey(Vector3d p) =>
        ((long)Math.Round(p.X * 1e3), (long)Math.Round(p.Y * 1e3), (long)Math.Round(p.Z * 1e3));

    private static (long, long) EdgeKey(Vector3d a, Vector3d b)
    {
        long Key(Vector3d p) =>
            (long)Math.Round(p.X * 1e3) * 73856093 ^
            (long)Math.Round(p.Y * 1e3) * 19349663 ^
            (long)Math.Round(p.Z * 1e3) * 83492791;

        long ka = Key(a), kb = Key(b);
        return ka <= kb ? (ka, kb) : (kb, ka);
    }

    /// <summary>
    /// Точки, где РЁБРА тетраэдров пересекают поверхность тела. Именно они мешают грани лечь на
    /// поверхность: пока ребро проходит сквозь неё, ни один тетраэдр не может опереться на эту
    /// грань. Точки, оказавшиеся слишком близко к уже имеющимся, отбрасываются — вплотную
    /// стоящие точки ломают построение по Делоне.
    /// </summary>
    private static List<Vector3d> CrossingPoints(MeshData surface, List<Vector3d> points,
                                                 List<(int A, int B, int C, int D)> tets, double size)
    {
        var res = new List<Vector3d>();
        double apart = size * 0.25;

        var edges = new HashSet<(int, int)>();
        foreach (var t in tets)
        {
            void Add(int a, int b) => edges.Add(a < b ? (a, b) : (b, a));
            Add(t.A, t.B); Add(t.A, t.C); Add(t.A, t.D);
            Add(t.B, t.C); Add(t.B, t.D); Add(t.C, t.D);
        }

        foreach (var (i, j) in edges)
        {
            Vector3d p = points[i], q = points[j];
            Vector3d dir = q - p;
            double len = dir.Length;
            if (len < 1e-9) continue;

            for (int k = 0; k + 2 < surface.Indices.Length; k += 3)
            {
                Vector3d a = At(surface, surface.Indices[k]);
                Vector3d b = At(surface, surface.Indices[k + 1]);
                Vector3d c = At(surface, surface.Indices[k + 2]);

                if (!SegmentHitsTriangle(p, dir, a, b, c, out double t0)) continue;

                Vector3d hit = p + dir * t0;

                // Пересечение у самого конца ребра — это и есть вершина на поверхности, а не
                // помеха: такие пропускаются.
                if ((hit - p).Length < apart || (hit - q).Length < apart) continue;
                if (points.Any(x => (x - hit).Length < apart)) continue;
                if (res.Any(x => (x - hit).Length < apart)) continue;

                res.Add(hit);
                break;
            }
        }

        return res;
    }

    /// <summary>Протыкает ли хоть одно ребро тетраэдра поверхность тела.</summary>
    private static bool CrossesSurface(MeshData surface, List<Vector3d> points, (int A, int B, int C, int D) t)
    {
        var pairs = new[]
        {
            (t.A, t.B), (t.A, t.C), (t.A, t.D), (t.B, t.C), (t.B, t.D), (t.C, t.D)
        };

        foreach (var (i, j) in pairs)
        {
            Vector3d p = points[i], dir = points[j] - points[i];
            if (dir.LengthSquared < 1e-18) continue;

            for (int k = 0; k + 2 < surface.Indices.Length; k += 3)
                if (SegmentHitsTriangle(p, dir,
                                        At(surface, surface.Indices[k]),
                                        At(surface, surface.Indices[k + 1]),
                                        At(surface, surface.Indices[k + 2]), out _)) return true;
        }

        return false;
    }

    /// <summary>Пересекает ли отрезок треугольник; возвращает долю вдоль отрезка.</summary>
    private static bool SegmentHitsTriangle(Vector3d origin, Vector3d dir,
                                            Vector3d a, Vector3d b, Vector3d c, out double t)
    {
        t = 0;

        Vector3d e1 = b - a, e2 = c - a;
        Vector3d h = Vector3d.Cross(dir, e2);
        double det = Vector3d.Dot(e1, h);
        if (Math.Abs(det) < 1e-12) return false;

        double inv = 1 / det;
        Vector3d s = origin - a;
        double u = inv * Vector3d.Dot(s, h);
        if (u < 1e-9 || u > 1 - 1e-9) return false;

        Vector3d q = Vector3d.Cross(s, e1);
        double v = inv * Vector3d.Dot(dir, q);
        if (v < 1e-9 || u + v > 1 - 1e-9) return false;

        t = inv * Vector3d.Dot(e2, q);
        return t > 1e-6 && t < 1 - 1e-6;
    }

    /// <summary>
    /// Треугольники поверхности, которых НЕТ среди граней сетки. Возвращает их середины — точки,
    /// которые надо вставить, чтобы сетка притянулась к поверхности.
    ///
    /// Сравнение идёт по положению вершин, а не по номерам: у поверхностной сетки свои номера, у
    /// объёмной свои, и связать их можно только через места точек.
    /// </summary>
    private static List<Vector3d> MissingFacets(MeshData surface, List<Vector3d> points,
                                                List<(int A, int B, int C, int D)> tets)
    {
        // Грани сетки — по ключам положений.
        var have = new HashSet<(long, long, long)>();

        foreach (var t in tets)
            foreach (var f in FacesOf(t))
                have.Add(FaceKey(points[f.Item1], points[f.Item2], points[f.Item3]));

        var res = new List<Vector3d>();
        var added = new HashSet<(long, long, long)>();

        for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
        {
            Vector3d a = At(surface, surface.Indices[i]);
            Vector3d b = At(surface, surface.Indices[i + 1]);
            Vector3d c = At(surface, surface.Indices[i + 2]);

            var key = FaceKey(a, b, c);
            if (have.Contains(key) || !added.Add(key)) continue;

            res.Add((a + b + c) / 3);
        }

        return res;
    }

    /// <summary>Ключ грани по положениям её вершин: порядок вершин не важен.</summary>
    private static (long, long, long) FaceKey(Vector3d a, Vector3d b, Vector3d c)
    {
        long Key(Vector3d p) =>
            (long)Math.Round(p.X * 1e3) * 73856093 ^
            (long)Math.Round(p.Y * 1e3) * 19349663 ^
            (long)Math.Round(p.Z * 1e3) * 83492791;

        long ka = Key(a), kb = Key(b), kc = Key(c);
        if (ka > kb) (ka, kb) = (kb, ka);
        if (kb > kc) (kb, kc) = (kc, kb);
        if (ka > kb) (ka, kb) = (kb, ka);
        return (ka, kb, kc);
    }

    /// <summary>Середина тетраэдра.</summary>
    private static Vector3d Centre(List<Vector3d> points, (int A, int B, int C, int D) t) =>
        (points[t.A] + points[t.B] + points[t.C] + points[t.D]) / 4;

    /// <summary>Пересекает ли отрезок треугольник (для разбора недостающих граней).</summary>
    private static bool Crosses(Vector3d p, Vector3d q, Vector3d a, Vector3d b, Vector3d c) =>
        SegmentHitsTriangle(p, q - p, a, b, c, out _);

    /// <summary>Пересекает ли отрезок поверхность тела.</summary>
    private static bool CrossesSurfaceSegment(MeshData surface, Vector3d a, Vector3d b)
    {
        Vector3d dir = b - a;
        if (dir.LengthSquared < 1e-18) return false;

        for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
            if (SegmentHitsTriangle(a, dir,
                                    At(surface, surface.Indices[i]),
                                    At(surface, surface.Indices[i + 1]),
                                    At(surface, surface.Indices[i + 2]), out _)) return true;

        return false;
    }

    /// <summary>
    /// Середины граней, которым мешают рёбра сетки. Именно их деление и нужно: грань, сквозь
    /// которую проходит ребро, не ляжет на место, пока остаётся целой.
    /// </summary>
    private static List<Vector3d> BlockedFacetCentres(MeshData surface, List<Vector3d> points,
                                                      List<(int A, int B, int C, int D)> tets, double size)
    {
        var place = new Dictionary<(long, long, long), int>();
        for (int i = 0; i < points.Count; i++) place[PlaceKey(points[i])] = i;

        var res = new List<Vector3d>();

        for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
        {
            Vector3d a = At(surface, surface.Indices[i]);
            Vector3d b = At(surface, surface.Indices[i + 1]);
            Vector3d c = At(surface, surface.Indices[i + 2]);

            if (!place.TryGetValue(PlaceKey(a), out int ia)) continue;
            if (!place.TryGetValue(PlaceKey(b), out int ib)) continue;
            if (!place.TryGetValue(PlaceKey(c), out int ic)) continue;
            if (TetFlips.HasFacet(tets, ia, ib, ic)) continue;

            bool crossed = false;
            foreach (var t in tets)
            {
                var n = new[] { t.A, t.B, t.C, t.D };
                for (int x = 0; x < 4 && !crossed; x++)
                    for (int y = x + 1; y < 4 && !crossed; y++)
                    {
                        if (n[x] == ia || n[x] == ib || n[x] == ic) continue;
                        if (n[y] == ia || n[y] == ib || n[y] == ic) continue;
                        if (Crosses(points[n[x]], points[n[y]], a, b, c)) crossed = true;
                    }
                if (crossed) break;
            }

            if (!crossed) continue;

            Vector3d mid = (a + b + c) / 3;
            if (points.Any(q => (q - mid).Length < size * 0.05)) continue;
            if (res.Any(q => (q - mid).Length < size * 0.05)) continue;

            res.Add(mid);
        }

        return res;
    }

    /// <summary>Общая площадь граней поверхности, которых нет в объёмной сетке.</summary>
    private static double MissingArea(MeshData surface, List<Vector3d> points,
                                      List<(int A, int B, int C, int D)> tets)
    {
        var have = new HashSet<(long, long, long)>();
        foreach (var t in tets)
            foreach (var f in FacesOf(t))
                have.Add(FaceKey(points[f.Item1], points[f.Item2], points[f.Item3]));

        double sum = 0;
        for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
        {
            Vector3d a = At(surface, surface.Indices[i]);
            Vector3d b = At(surface, surface.Indices[i + 1]);
            Vector3d c = At(surface, surface.Indices[i + 2]);

            if (have.Contains(FaceKey(a, b, c))) continue;
            sum += Vector3d.Cross(b - a, c - a).Length / 2;
        }

        return sum;
    }

    /// <summary>
    /// Поверхностная сетка с добавленными точками: каждая точка, лежащая внутри треугольника,
    /// делит его на три. Так поверхность остаётся той же геометрически, но её разбиение
    /// согласуется с объёмной сеткой.
    /// </summary>
    private static MeshData SplitSurfaceAt(MeshData surface, List<Vector3d> extra)
    {
        var verts = new List<Vector3d>();
        foreach (var v in surface.Vertices) verts.Add(new Vector3d(v.P.X, v.P.Y, v.P.Z));

        var tris = new List<(int A, int B, int C)>();
        for (int i = 0; i + 2 < surface.Indices.Length; i += 3)
            tris.Add((surface.Indices[i], surface.Indices[i + 1], surface.Indices[i + 2]));

        foreach (var p in extra)
        {
            int at = -1;
            for (int k = 0; k < tris.Count && at < 0; k++)
                if (PointInTriangle(p, verts[tris[k].A], verts[tris[k].B], verts[tris[k].C])) at = k;

            if (at < 0) continue;

            var t = tris[at];
            int n = verts.Count;
            verts.Add(p);

            tris[at] = (t.A, t.B, n);
            tris.Add((t.B, t.C, n));
            tris.Add((t.C, t.A, n));
        }

        var mb = new MeshBuilder();
        foreach (var v in verts) mb.AddVertex(v, Vector3d.UnitZ);
        foreach (var t in tris) mb.AddTriangle(t.A, t.B, t.C);

        var md = new MeshData();
        mb.FillInto(md);
        return md;
    }

    /// <summary>Лежит ли точка в треугольнике (в его плоскости, с малым допуском).</summary>
    private static bool PointInTriangle(Vector3d p, Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d n = Vector3d.Cross(b - a, c - a);
        double area2 = n.Length;
        if (area2 < 1e-18) return false;

        n /= area2;
        if (Math.Abs(Vector3d.Dot(p - a, n)) > 1e-6) return false;

        double u = Vector3d.Dot(Vector3d.Cross(b - a, p - a), n);
        double v = Vector3d.Dot(Vector3d.Cross(c - b, p - b), n);
        double w = Vector3d.Dot(Vector3d.Cross(a - c, p - c), n);

        return u > 1e-12 && v > 1e-12 && w > 1e-12;
    }

    /// <summary>Расстояние от точки до поверхности тела.</summary>
    private static double DistanceToSurface(MeshData mesh, Vector3d p)
    {
        double best = double.MaxValue;

        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            Vector3d a = At(mesh, mesh.Indices[i]);
            Vector3d b = At(mesh, mesh.Indices[i + 1]);
            Vector3d c = At(mesh, mesh.Indices[i + 2]);

            Vector3d n = Vector3d.Cross(b - a, c - a);
            if (n.LengthSquared < 1e-24) continue;

            n = n.Normalized();
            Vector3d proj = p - n * Vector3d.Dot(p - a, n);

            bool inside = Vector3d.Dot(Vector3d.Cross(b - a, proj - a), n) >= -1e-9
                       && Vector3d.Dot(Vector3d.Cross(c - b, proj - b), n) >= -1e-9
                       && Vector3d.Dot(Vector3d.Cross(a - c, proj - c), n) >= -1e-9;

            double d = inside ? (p - proj).Length
                              : Math.Min((p - a).Length, Math.Min((p - b).Length, (p - c).Length));

            if (d < best) best = d;
        }

        return best;
    }

    /// <summary>Узлы поверхностной сетки без повторов: у граней свои копии точек.</summary>
    private static List<Vector3d> Unique(MeshData mesh)
    {
        var res = new List<Vector3d>();
        var seen = new Dictionary<(long, long, long), int>();

        foreach (var v in mesh.Vertices)
        {
            var p = new Vector3d(v.P.X, v.P.Y, v.P.Z);
            var key = ((long)Math.Round(p.X * 1e4), (long)Math.Round(p.Y * 1e4), (long)Math.Round(p.Z * 1e4));
            if (seen.ContainsKey(key)) continue;

            seen[key] = res.Count;
            res.Add(p);
        }

        return res;
    }

    /// <summary>Точки внутри тела по сетке с заданным шагом, не ближе половины шага к границе.</summary>
    private static List<Vector3d> InnerPoints(MeshData surface, List<Vector3d> boundary, double size)
    {
        var res = new List<Vector3d>();

        Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
        foreach (var p in boundary) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }

        double keep = size * 0.5;

        for (double x = lo.X + size / 2; x < hi.X; x += size)
            for (double y = lo.Y + size / 2; y < hi.Y; y += size)
                for (double z = lo.Z + size / 2; z < hi.Z; z += size)
                {
                    var p = new Vector3d(x, y, z);
                    if (!Inside(surface, p)) continue;
                    if (boundary.Any(b => (b - p).Length < keep)) continue;
                    res.Add(p);
                }

        return res;
    }

    /// <summary>
    /// Внутри ли точка тела: луч в произвольную сторону и счёт пересечений с поверхностной
    /// сеткой. Направление берётся заведомо косым, чтобы луч не шёл вдоль граней.
    /// </summary>
    private static bool Inside(MeshData mesh, Vector3d p)
    {
        // ТРИ ЛУЧА И БОЛЬШИНСТВО. Один луч ненадёжен: направление вида (1,1,1) у прямоугольного
        // тела идёт ровно вдоль рёбер сетки, пересечение засчитывается дважды или теряется, и
        // точка внутри объявляется снаружи. Измерено: из-за этого отбрасывались годные
        // тетраэдры — до 5000 мм³ из 24 000, при том что сама триангуляция давала ровно 24 000.
        //
        // Направления взяты заведомо косыми и несоизмеримыми с осями, чтобы луч не ложился ни на
        // грань, ни на ребро.
        Vector3d[] dirs =
        {
            new Vector3d(0.5257311, 0.3250575, 0.7861514),
            new Vector3d(-0.7071068, 0.4082483, 0.5773503),
            new Vector3d(0.2672612, -0.8017837, 0.5345225)
        };

        int votes = 0;

        foreach (var dir in dirs)
        {
            int hits = 0;

            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                Vector3d a = At(mesh, mesh.Indices[i]);
                Vector3d b = At(mesh, mesh.Indices[i + 1]);
                Vector3d c = At(mesh, mesh.Indices[i + 2]);

                if (RayHitsTriangle(p, dir, a, b, c)) hits++;
            }

            if (hits % 2 == 1) votes++;
        }

        return votes >= 2;
    }

    private static Vector3d At(MeshData mesh, int i)
    {
        var v = mesh.Vertices[i].P;
        return new Vector3d(v.X, v.Y, v.Z);
    }

    private static bool RayHitsTriangle(Vector3d origin, Vector3d dir, Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d e1 = b - a, e2 = c - a;
        Vector3d h = Vector3d.Cross(dir, e2);
        double det = Vector3d.Dot(e1, h);
        if (Math.Abs(det) < 1e-12) return false;

        double inv = 1 / det;
        Vector3d s = origin - a;
        double u = inv * Vector3d.Dot(s, h);
        if (u < 0 || u > 1) return false;

        Vector3d q = Vector3d.Cross(s, e1);
        double v = inv * Vector3d.Dot(dir, q);
        if (v < 0 || u + v > 1) return false;

        return inv * Vector3d.Dot(e2, q) > 1e-9;
    }

    /// <summary>
    /// Триангуляция Делоне в пространстве вставкой точек. Всё начинается с одного тетраэдра,
    /// накрывающего весь набор; в конце он и его потомки убираются.
    /// </summary>
    /// <summary>
    /// Триангуляция Делоне по набору точек. Открыта соседям по построителю: заполнение полости
    /// при восстановлении граней пользуется тем же приёмом, и переписывать его заново не нужно.
    /// </summary>
    internal static List<(int A, int B, int C, int D)> Delaunay(List<Vector3d> points)
    {
        Vector3d lo = new(double.MaxValue), hi = new(double.MinValue);
        foreach (var p in points) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }

        Vector3d centre = (lo + hi) / 2;
        double span = Math.Max(hi.X - lo.X, Math.Max(hi.Y - lo.Y, hi.Z - lo.Z)) * 10 + 1;

        int n = points.Count;
        points.Add(centre + new Vector3d(0, 0, span));
        points.Add(centre + new Vector3d(span, -span / 2, -span / 2));
        points.Add(centre + new Vector3d(-span, -span / 2, -span / 2));
        points.Add(centre + new Vector3d(0, span, -span / 2));

        var first = (n, n + 1, n + 2, n + 3);
        if (Vector3d.Dot(points[first.Item2] - points[first.Item1],
                         Vector3d.Cross(points[first.Item3] - points[first.Item1],
                                        points[first.Item4] - points[first.Item1])) < 0)
            first = (n, n + 2, n + 1, n + 3);

        var tets = new List<(int A, int B, int C, int D)> { first };
        bool blewUp = false;
        int cavityBroken = 0;

        for (int i = 0; i < n; i++)
        {
            var bad = new List<int>();
            for (int k = 0; k < tets.Count; k++)
                if (InSphereExact(points, tets[k], points[i], i)) bad.Add(k);

            if (bad.Count == 0) continue;

            // Границы полости: грани, встретившиеся ровно раз.
            var faces = new Dictionary<(int, int, int), int>();
            foreach (int k in bad)
                foreach (var f in FacesOf(tets[k]))
                {
                    var key = Key(f);
                    faces[key] = faces.GetValueOrDefault(key) + 1;
                }

            var hull = new List<(int, int, int)>();
            foreach (int k in bad)
                foreach (var f in FacesOf(tets[k]))
                    if (faces[Key(f)] == 1) hull.Add(f);

            // ПРОПУСК ТОЧКИ ПРИ НЕВЕРНОЙ ПОЛОСТИ — ПРОБОВАЛ, ОТКАТИЛ. Замысел: если вставляемая
            // точка не видит какую-то грань полости, заполнение будет неверным, и точку лучше
            // пропустить. На деле отсекаются и нужные точки: объём тела с отверстием ухудшился с
            // 21 764 до 22 754 при расчётных 21 738, а мелкий шаг всё равно не сошёлся.
            // Вырожденные грани и так пропускаются поштучно ниже — этого достаточно.

            // СВЯЗНОСТЬ ПОЛОСТИ. Заполнение гранями «встреченными однажды» верно только для
            // связной полости: если снесённые тетраэдры распались на два куска, их общая граница
            // описывает две отдельные области, и заполнение даёт наложение. Проверяется обходом
            // по общим граням: от первого снесённого должны достигаться все остальные.
            // ПОЛОСТЬ РАСПАЛАСЬ — ТОЧКА ПРОПУСКАЕТСЯ. Пробовал заполнять по тому её куску, где
            // лежит точка: наложения вернулись (триангуляция тела с отверстием 24 000 → 33 839),
            // а мелкий шаг снова перестал сходиться. Причина понятна: оставшиеся куски полости
            // содержат точку в своих описанных сферах, и сетка перестаёт быть сеткой Делоне —
            // дальнейшие вставки идут по неверной основе.
            //
            // Пропуск обходится недобором объёма (тело с отверстием: 20 104 при расчётных 21 738),
            // и это честная цена за верную сетку.
            if (bad.Count > 1 && !CavityConnected(tets, bad))
            {
                cavityBroken++;
                continue;
            }

            // ЗАЩИТА ОТ РАЗРАСТАНИЯ. У правильного построения число тетраэдров держится около
            // шести на узел. Если полость вышла незамкнутой, заполнение добавляет их без меры:
            // измерено на теле с отверстием — 172 узла давали 36 449 тетраэдров вместо примерно
            // восьмисот. Лучше честно остановиться и сказать, чем вернуть груду.
            if (tets.Count > 12 * points.Count + 100) { blewUp = true; break; }

            for (int k = bad.Count - 1; k >= 0; k--) tets.RemoveAt(bad[k]);

            // ЗНАК ОБЪЁМА ПРИВОДИТСЯ СРАЗУ. Проверка описанной сферы ветвится по нему, и если
            // тетраэдры хранятся как придётся, одна и та же точка для одного тетраэдра
            // оказывается внутри сферы, а для другого — снаружи. Отсюда и наложения.
            foreach (var f in hull)
            {
                var t = (f.Item1, f.Item2, f.Item3, i);

                // ЗНАК ОБЪЁМА СЧИТАЕТСЯ ЦЕЛЫМИ ЧИСЛАМИ. Дробный порог здесь отбрасывал грани,
                // которые на деле не вырождены: у тонкого тетраэдра объём мал по величине, но не
                // равен нулю, а пропуск такой грани оставляет в полости дыру — и заполнение
                // выходит с наложением. В целых числах ноль — ровно ноль.
                double v = (double)OrientExact(points[t.Item1], points[t.Item2], points[t.Item3], points[i]);
                // Грань, не видимая из вставляемой точки, ПРОПУСКАЕТСЯ. Разворачивать её нельзя:
                // полость тогда заполняется с наложением, и объём сетки выходит больше объёма
                // тела (измерено: 31 166 против 24 000).
                if (v == 0) continue;
                tets.Add(v < 0 ? (t.Item1, t.Item3, t.Item2, i) : t);
            }
        }

        // ВОЗВРАТ ПРОПУЩЕННЫХ ТОЧЕК СО СМЕЩЕНИЕМ — ПРОБОВАЛ, ОТКАТИЛ. Замысел обычный: полость
        // распадается у точек, стоящих слишком правильно (на общих сферах, в узлах ровной
        // сетки), и слабое смещение эту правильность снимает. На деле вышло хуже:
        //
        //   тело с отверстием, шаг 10   объём 20 104 → 17 652
        //   тело с отверстием, шаг 7    21 448 → 20 326
        //   триангуляция                24 000 → 24 222 (появились лёгкие наложения)
        //
        // Причина, видимо, в том, что смещённая точка вставляется в уже построенную сетку, а не
        // в общем порядке: соседние тетраэдры к этому времени опираются на её прежнее место.
        // Возвращать такие точки надо не после всех, а перестраивая сетку целиком со смещённым
        // набором — то есть смещать до построения, а не после.

        // Всё, что опирается на накрывающий тетраэдр, убирается.
        tets.RemoveAll(t => t.A >= n || t.B >= n || t.C >= n || t.D >= n);
        points.RemoveRange(n, 4);

        if (cavityBroken > 0) LastCavityBroken = cavityBroken;
        if (blewUp) tets.Clear();
        return tets;
    }

    /// <summary>Сколько точек было пропущено из-за несвязной полости — для разбора.</summary>
    public static int LastCavityBroken;

    /// <summary>
    /// Кусок полости, в котором лежит вставляемая точка. Полость сносимых тетраэдров бывает
    /// несвязной; заполнение по всей такой полости даёт наложения, а по одному её куску — верно.
    /// Кусок выбирается тот, который содержит точку; если ни один не содержит (точка на общей
    /// границе), берётся наибольший.
    /// </summary>
    private static List<int> CavityPiece(List<Vector3d> points, List<(int A, int B, int C, int D)> tets,
                                         List<int> bad, Vector3d p)
    {
        var pieces = Components(tets, bad);
        if (pieces.Count <= 1) return bad;

        foreach (var piece in pieces)
            foreach (int k in piece)
                if (InTet(points, tets[k], p)) return piece;

        return pieces.OrderByDescending(x => x.Count).First();
    }

    /// <summary>Внутри ли точка тетраэдра: знаки объёмов всех четырёх подтетраэдров совпадают.</summary>
    private static bool InTet(List<Vector3d> points, (int A, int B, int C, int D) t, Vector3d p)
    {
        Vector3d a = points[t.A], b = points[t.B], c = points[t.C], d = points[t.D];

        double Vol(Vector3d x, Vector3d y, Vector3d z, Vector3d w) =>
            Vector3d.Dot(y - x, Vector3d.Cross(z - x, w - x));

        double v0 = Vol(a, b, c, d);
        if (Math.Abs(v0) < 1e-15) return false;

        double v1 = Vol(p, b, c, d), v2 = Vol(a, p, c, d), v3 = Vol(a, b, p, d), v4 = Vol(a, b, c, p);
        return Math.Sign(v1) == Math.Sign(v0) && Math.Sign(v2) == Math.Sign(v0)
            && Math.Sign(v3) == Math.Sign(v0) && Math.Sign(v4) == Math.Sign(v0);
    }

    /// <summary>Куски полости: тетраэдры, связанные общими гранями.</summary>
    private static List<List<int>> Components(List<(int A, int B, int C, int D)> tets, List<int> bad)
    {
        var byFace = new Dictionary<(int, int, int), List<int>>();

        foreach (int k in bad)
            foreach (var f in FacesOf(tets[k]))
            {
                var key = Key(f);
                if (!byFace.TryGetValue(key, out var list)) byFace[key] = list = new List<int>(2);
                list.Add(k);
            }

        var left = new HashSet<int>(bad);
        var res = new List<List<int>>();

        while (left.Count > 0)
        {
            int start = left.First();
            var piece = new List<int>();
            var queue = new Queue<int>();

            queue.Enqueue(start);
            left.Remove(start);

            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                piece.Add(k);

                foreach (var f in FacesOf(tets[k]))
                    foreach (int other in byFace[Key(f)])
                        if (left.Remove(other)) queue.Enqueue(other);
            }

            res.Add(piece);
        }

        return res;
    }

    /// <summary>
    /// Связна ли полость: от первого снесённого тетраэдра должны достигаться все остальные через
    /// общие грани.
    /// </summary>
    private static bool CavityConnected(List<(int A, int B, int C, int D)> tets, List<int> bad)
    {
        var byFace = new Dictionary<(int, int, int), List<int>>();

        foreach (int k in bad)
            foreach (var f in FacesOf(tets[k]))
            {
                var key = Key(f);
                if (!byFace.TryGetValue(key, out var list)) byFace[key] = list = new List<int>(2);
                list.Add(k);
            }

        var seen = new HashSet<int> { bad[0] };
        var queue = new Queue<int>();
        queue.Enqueue(bad[0]);

        while (queue.Count > 0)
        {
            int k = queue.Dequeue();
            foreach (var f in FacesOf(tets[k]))
                foreach (int other in byFace[Key(f)])
                    if (seen.Add(other)) queue.Enqueue(other);
        }

        return seen.Count == bad.Count;
    }

    private static IEnumerable<(int, int, int)> FacesOf((int A, int B, int C, int D) t)
    {
        yield return (t.A, t.B, t.C);
        yield return (t.A, t.B, t.D);
        yield return (t.A, t.C, t.D);
        yield return (t.B, t.C, t.D);
    }

    private static (int, int, int) Key((int, int, int) f)
    {
        int a = f.Item1, b = f.Item2, c = f.Item3;
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>
    /// ТОЧНАЯ ПРОВЕРКА «ВНУТРИ ОПИСАННОЙ СФЕРЫ». Координаты узлов сетки — обычные дробные числа,
    /// но определитель можно посчитать В ЦЕЛЫХ, умножив их на масштаб и округлив: узлы всё равно
    /// стоят с шагом сетки, и дробная часть у них не бесконечная.
    ///
    /// Зачем это нужно. У бруска множество точек лежит на ОБЩИХ сферах — углы, середины рёбер,
    /// узлы правильной сетки. Для таких равноудалённых точек определитель равен нулю, и дробный
    /// счёт даёт то чуть больше нуля, то чуть меньше: полость выходит незамкнутой, а тетраэдры
    /// накладываются. В целых числах ноль — это ровно ноль, и ответ один и тот же всегда.
    ///
    /// Равенство решается в пользу «снаружи»: так полость остаётся замкнутой, а сетка — без
    /// наложений.
    /// </summary>
    private static bool InSphereExact(List<Vector3d> pts, (int A, int B, int C, int D) t, Vector3d p) =>
        InSphereExact(pts, t, p, -1);

    /// <summary>
    /// То же с СИМВОЛИЧЕСКИМ ВОЗМУЩЕНИЕМ. Когда определитель равен ровно нулю, точка лежит на
    /// описанной сфере, и геометрия ответа не даёт: оба решения равноправны. Дробный счёт в таком
    /// месте отвечает случайно, и полость выходит несвязной — это и есть корень всех отказов.
    ///
    /// Приём промышленных построителей: считать, что каждая точка чуть смещена на величину,
    /// зависящую от её НОМЕРА, а не от координат. Тогда равенств не бывает вовсе, ответ
    /// однозначен и одинаков при любом порядке обхода. Здесь роль смещения играет сравнение
    /// номеров: точка с бо́льшим номером считается «чуть внутри».
    ///
    /// ПРОСТОЕ СРАВНЕНИЕ НОМЕРОВ НЕ ГОДИТСЯ — ИЗМЕРЕНО. Правило «точка с бо́льшим номером внутри»
    /// выглядит разумным, но оно НЕ СОГЛАСОВАНО: для разных четвёрок оно даёт ответы, которые не
    /// складываются в единую картину, и сетка перестаёт быть сеткой Делоне. Брусок дал 25 333,
    /// 28 000 и 28 427 вместо 24 000 — то есть наложения, худшие из всех виденных.
    ///
    /// Настоящее символическое возмущение устроено иначе: каждой точке приписывают смещение
    /// ε^(номер) и раскрывают определитель по степеням ε, беря первый ненулевой член.
    ///
    /// ЗДЕСЬ СДЕЛАНО ПРОЩЕ И ЧЕСТНЕЕ: возмущение внесено ДО построения, прямо в координаты.
    /// Точки приводятся к сетке предикатов (шаг 1e-6 мм) и сдвигаются на величину, зависящую от
    /// номера; сдвиг в тысячу раз меньше допуска склейки, то есть геометрии не меняет, но делает
    /// точные равенства невозможными. Разложение по степеням ε при этом не нужно вовсе.
    ///
    /// Что дало: тело с отверстием при шаге 10 — 22 017 → 21 932 при расчётных 21 738; брусок
    /// остался точным при всех шагах. Выигрыш небольшой, но настоящий, и главное — источник
    /// случайных ответов устранён у корня.
    ///
    /// ПОДТВЕРЖДЕНИЕ: перестройка со вставленными точками БОЛЬШЕ НЕ РАЗВАЛИВАЕТСЯ. Прежде она
    /// упиралась в защиту от разрастания и заход отменялся; теперь из 4 и 8 проколов выходят
    /// сетки в 271 и 728 тетраэдров. Корень устранён — это и была проверка.
    ///
    /// НО ГРАНИ ОТ ЭТОГО НЕ ВОССТАНАВЛИВАЮТСЯ: недостающих стало БОЛЬШЕ (28 → 30 и 61 → 69).
    /// Причина по существу: точка вставляется в объёмную сетку, а поверхностная остаётся прежней.
    /// Треугольник поверхности, сквозь который прошла вставка, теперь не может совпасть ни с
    /// одной гранью — он крупнее любой из них.
    ///
    /// Значит, точку Штейнера надо вставлять В ПОВЕРХНОСТЬ ТОЖЕ, деля её треугольник, и только
    /// потом в объём. Иначе две сетки расходятся всё сильнее с каждой вставкой.
    ///
    /// СОГЛАСОВАННАЯ ВСТАВКА СДЕЛАНА: точка делит треугольник поверхности на три и одновременно
    /// входит в объёмную сетку; успех меряется ПЛОЩАДЬЮ недостающего, а не числом (число растёт
    /// от самого деления — на этом уже споткнулось восстановление рёбер).
    ///
    /// Но площадь недостающего от этого не убывает, и заход по-прежнему отменяется. Причина
    /// понятна: деление треугольника на три не заставляет доли совпасть с гранями сетки — оно
    /// лишь даёт им такую возможность. Чтобы возможность стала совпадением, после вставки нужны
    /// ПЕРЕСТАНОВКИ, подводящие грани сетки под доли, — те самые 2↔3 и 3↔2, что уже написаны и
    /// работают на рёбрах.
    ///
    /// СКОЛЬКО ГРАНЕЙ НА САМОМ ДЕЛЕ МЕШАЕТ. Разбор оставшихся показал, что «недостающие» грани
    /// бывают двух совсем разных родов:
    ///
    ///   тело                   мешают рёбра   только диагональ
    ///   с отверстием, шаг 10        4               24
    ///   с отверстием, шаг 7         2               50
    ///   брусок, все шаги            0            2, 8, 22
    ///
    /// «Только диагональ» — это грань, которую ни одно ребро сетки не пересекает: на плоском
    /// участке диагональ легла иначе, и вреда от этого нет вовсе. У бруска ВСЕ недостающие грани
    /// такого рода, и объём при них точен до единицы.
    ///
    /// Разбор поимённо показал, ЧЕМ они мешают: вокруг мешающего ребра оказывается 4, 5 или 9
    /// тетраэдров — никогда три. Оттого перестановка 3→2 и не срабатывала почти никогда: она
    /// работает только при трёх. Добавлено общее УДАЛЕНИЕ РЕБРА с кольцом любой длины (кольцо из
    /// N тетраэдров заменяется на 2(N−2)); мешающих граней стало 4 и 1 вместо 4 и 2.
    ///
    /// Значит, по-настоящему мешают лишь 4 и 1 грань — они и дают остаток в 0.9–1.9% объёма.
    ///
    /// ЧТО ПРОБОВАЛ ПРОТИВ ЭТИХ ЧЕТЫРЁХ ГРАНЕЙ, и что из этого вышло:
    ///   — удаление мешающего ребра перестановкой 3→2: неприменимо, вокруг него 4, 5 или 9
    ///     тетраэдров, а не три;
    ///   — общее удаление ребра с кольцом любой длины: отклоняется, «вырожденный тетраэдр при
    ///     заполнении»;
    ///   — веер от каждой вершины кольца по очереди: без изменений;
    ///   — перебор ВСЕХ разбиений многоугольника (429 у кольца из девяти): без изменений;
    ///   — деление самой мешающей грани точкой Штейнера: без изменений — пересекающее ребро
    ///     протыкает и любую из трёх долей.
    ///
    /// Вывод: эти грани упираются в граничные рёбра, которые нельзя ни убрать (кольцо вокруг них
    /// не замкнуто, оно упирается в поверхность), ни обойти делением. Промышленные построители
    /// разбирают такой случай защитой отрезков сферами: вокруг каждого граничного ребра держат
    /// область, куда не допускают чужих точек, и тем не дают ему стать пересечённым вообще. Это
    /// не доделка, а отдельное устройство, которое надо закладывать с самого начала построения.
    /// Граница восстановлена почти целиком, и это меняет оценку задачи: осталось не «доделать
    /// восстановление», а разобрать несколько частных случаев.
    ///
    /// ВОССТАНОВЛЕНИЕ ГРАНЕЙ ПЕРЕСТАНОВКАМИ срабатывает редко: за прогон
    /// восстановлена ОДНА грань. Причина в условии применимости — перестановка 3→2 возможна
    /// только тогда, когда вокруг мешающего ребра ровно три тетраэдра; чаще их четыре и больше,
    /// и такое ребро убирается не одной перестановкой, а цепочкой, которую надо искать перебором.
    ///
    /// Это известное место: в промышленных построителях восстановление граней — самая сложная
    /// часть, и делается оно не одиночными перестановками, а построением ПОЛОСТИ вокруг всей
    /// недостающей грани с последующим её перезаполнением. Наши перестановки — необходимое
    /// основание для этого, но не замена.
    private static bool InSphereExact(List<Vector3d> pts, (int A, int B, int C, int D) t, Vector3d p, int index)
    {
        const double scale = 1e6;

        System.Numerics.BigInteger R(double v) => new(Math.Round(v * scale));

        System.Numerics.BigInteger Det3(
            System.Numerics.BigInteger a1, System.Numerics.BigInteger a2, System.Numerics.BigInteger a3,
            System.Numerics.BigInteger b1, System.Numerics.BigInteger b2, System.Numerics.BigInteger b3,
            System.Numerics.BigInteger c1, System.Numerics.BigInteger c2, System.Numerics.BigInteger c3) =>
            a1 * (b2 * c3 - b3 * c2) - a2 * (b1 * c3 - b3 * c1) + a3 * (b1 * c2 - b2 * c1);

        Vector3d a = pts[t.A], b = pts[t.B], c = pts[t.C], d = pts[t.D];

        var ax = R(a.X - p.X); var ay = R(a.Y - p.Y); var az = R(a.Z - p.Z);
        var bx = R(b.X - p.X); var by = R(b.Y - p.Y); var bz = R(b.Z - p.Z);
        var cx = R(c.X - p.X); var cy = R(c.Y - p.Y); var cz = R(c.Z - p.Z);
        var dx = R(d.X - p.X); var dy = R(d.Y - p.Y); var dz = R(d.Z - p.Z);

        var aw = ax * ax + ay * ay + az * az;
        var bw = bx * bx + by * by + bz * bz;
        var cw = cx * cx + cy * cy + cz * cz;
        var dw = dx * dx + dy * dy + dz * dz;

        var det = aw * Det3(bx, by, bz, cx, cy, cz, dx, dy, dz)
                - bw * Det3(ax, ay, az, cx, cy, cz, dx, dy, dz)
                + cw * Det3(ax, ay, az, bx, by, bz, dx, dy, dz)
                - dw * Det3(ax, ay, az, bx, by, bz, cx, cy, cz);

        // Ориентация — тоже точно: тетраэдры хранятся положительными, но проверить дешевле, чем
        // полагаться.
        var orient = Det3(bx - ax, by - ay, bz - az,
                          cx - ax, cy - ay, cz - az,
                          dx - ax, dy - ay, dz - az);

        if (orient.Sign <= 0) return false;
        return det.Sign > 0;
    }

    /// <summary>
    /// Знак объёма тетраэдра в целых числах: положителен, отрицателен или ровно ноль. Тот же
    /// приём, что и в проверке описанной сферы, — и по той же причине: у почти вырожденных
    /// наборов дробный счёт даёт случайный знак.
    /// </summary>
    private static System.Numerics.BigInteger OrientExact(Vector3d a, Vector3d b, Vector3d c, Vector3d d)
    {
        const double scale = 1e6;
        System.Numerics.BigInteger R(double v) => new(Math.Round(v * scale));

        var bx = R(b.X - a.X); var by = R(b.Y - a.Y); var bz = R(b.Z - a.Z);
        var cx = R(c.X - a.X); var cy = R(c.Y - a.Y); var cz = R(c.Z - a.Z);
        var dx = R(d.X - a.X); var dy = R(d.Y - a.Y); var dz = R(d.Z - a.Z);

        return bx * (cy * dz - cz * dy) - by * (cx * dz - cz * dx) + bz * (cx * dy - cy * dx);
    }

    /// <summary>Лежит ли точка внутри сферы, описанной около тетраэдра.</summary>
    private static bool InSphere(List<Vector3d> pts, (int A, int B, int C, int D) t, Vector3d p)
    {
        Vector3d a = pts[t.A], b = pts[t.B], c = pts[t.C], d = pts[t.D];

        // Тетраэдры хранятся положительно ориентированными, поэтому ветвления по знаку больше
        // не нужно — остаётся проверка на вырождение.
        double orient = Vector3d.Dot(b - a, Vector3d.Cross(c - a, d - a));
        if (orient < 1e-15) return false;

        Vector3d ae = a - p, be = b - p, ce = c - p, de = d - p;

        double det =
            ae.LengthSquared * Vector3d.Dot(be, Vector3d.Cross(ce, de)) -
            be.LengthSquared * Vector3d.Dot(ae, Vector3d.Cross(ce, de)) +
            ce.LengthSquared * Vector3d.Dot(ae, Vector3d.Cross(be, de)) -
            de.LengthSquared * Vector3d.Dot(ae, Vector3d.Cross(be, ce));

        return det > 1e-12;
    }
}
