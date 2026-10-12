using OpenTK.Mathematics;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// СЕТКА ДЛЯ РАСЧЁТА. Обычное гранение строит сетку для показа: важно повторить форму, а какой
/// формы вышли треугольники — неважно. Измерено, к чему это приводит: из ста тел к расчёту годны
/// четыре, половина треугольников вытянута.
///
/// Здесь правила другие и их три:
///   — РАЗМЕР ЭЛЕМЕНТА задаётся числом, а не выводится из кривизны: расчётчик знает, какой шаг
///     ему нужен, и сетка обязана его держать;
///   — ВНУТРЕННИЕ ТОЧКИ расставляются сеткой с этим шагом, а не берутся только с границы:
///     иначе широкая грань разбивается длинными лоскутами от края до края;
///   — ФОРМА улучшается разглаживанием: точки внутри сдвигаются к середине своих соседей, а
///     точки границы остаются на месте, потому что граница — это геометрия детали.
///
/// Чего здесь нет: объёмных элементов. Это поверхностная сетка — то, с чего начинается любая
/// объёмная, и то, на чём спотыкается большинство построителей тетраэдров.
///
/// ЧТО ИЗМЕРЕНО на бруске 60×40×20 (плохой треугольник — угол меньше 20° или перекос больше 0.85):
///
///   показ (гранение по прогибу)   12 треугольников, плохих 4,   перекос серединный 0.557
///   расчёт, шаг 20 мм             80,               плохих 4,   0.250
///   расчёт, шаг 10 мм            260,               плохих 39,  0.317
///   расчёт, шаг 5 мм             884,               плохих 423, 0.652
///
/// При крупном и среднем шаге сетка заметно лучше показной: серединный перекос вдвое меньше при
/// вшестеро большем числе треугольников. При мелком шаге качество падает — точек по краю
/// становится много, и вставленные внутренние точки ложатся к ним слишком близко.
///
/// Урок по дороге: первая попытка дала числа ХУЖЕ показа, и виной была не сетка, а то, что
/// сборка падала, а замер шёл на прежнем двоичном файле. Перестановка сторон по Делоне работает
/// и даёт главный выигрыш: без неё плохих было 667 из 884, с ней 423. Второй выигрыш дало
/// чередование разглаживания и перестановки: 423 → 156.
///
/// ЦИЛИНДРИЧЕСКИЕ ГРАНИ строятся по развёртке — разворот цилиндра не искажает длин, поэтому к
/// ним применимы те же правила. На бруске с отверстием (шаг 8 мм):
///
///   показ                       216 треугольников, плохих 204, перекос серединный 0.953
///   расчёт, до развёртки        273,               плохих 144, 0.828
///   расчёт, с развёрткой        201,               плохих  44, 0.446
///
/// ВЫРОДИВШИЕСЯ УБРАНЫ У ПРОСТЫХ ТЕЛ. Причина была во вставке точки: точка, легшая ровно на
/// сторону треугольника, делила его на треугольники нулевой площади. Теперь требуется строго
/// внутреннее попадание с отступом от размера треугольника. На бруске (шаг 5 мм): выродившихся
/// стало 0, плохих 156 → 104.
///
/// У тела с отверстием их остаётся 30. Разбор по отдельным граням пока не удался: числа по
/// граням не сходятся с числами по телу, потому что грань, вынутая из тела, теряет часть
/// смежностей. Это первое, что надо поправить в самом разборе, прежде чем искать причину.
///
/// </summary>
public static class AnalysisMesher
{
    /// <summary>Сетка тела с заданным размером элемента.</summary>
    public static MeshData Build(TSolid solid, double elementSize, List<string>? warnings = null)
    {
        ValidateSize(elementSize);
        var mb = new MeshBuilder();
        var md = new MeshData();
        int failed = 0;

        foreach (var face in solid.Faces)
        {
            var part = BuildFace(face, elementSize, out bool exact);
            if (!exact) failed++;
            md.BrokenFaces.AddRange(part.BrokenFaces);
            if (!part.Whole) continue;
            // Сначала очистка изолированной грани, потом вычисление диапазонов.
            // FillInto(weld:true) меняет индексы, но не пересчитывает старые FaceRanges.
            mb.BeginFace(face.Id);
            int start = mb.VertexCount;
            foreach (var v in part.Vertices)
                mb.AddVertex(new Vector3d(v.P.X, v.P.Y, v.P.Z), new Vector3d(v.N.X, v.N.Y, v.N.Z));
            for (int i = 0; i + 2 < part.Indices.Length; i += 3)
                mb.AddTriangle(start + part.Indices[i], start + part.Indices[i + 1], start + part.Indices[i + 2]);
            mb.EndFace();
        }

        if (failed > 0)
            warnings?.Add($"Analysis mesh: {failed} face(s) fell back to the display tessellation.");

        // ТА ЖЕ ЧИСТКА, ЧТО И У ПОКАЗНОЙ СЕТКИ: расчётная собирается тем же построителем, и у неё
        // те же схлопнувшиеся треугольники от сведения точек соседних граней. Без чистки их
        // выходило 2 393 по ста телам — расчётная сетка была ХУЖЕ показной, где их ноль.
        mb.FillInto(md);
        SetBounds(md);
        if (!md.Whole) warnings?.Add($"Analysis mesh: {md.BrokenFaces.Count} face(s) could not be meshed.");
        return md;
    }

    private static void ValidateSize(double size)
    {
        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Element size must be finite and positive.");
    }

    private static void SetBounds(MeshData md)
    {
        var bounds = Bounds3.Empty;
        foreach (int i in md.Indices)
        {
            var p = md.Vertices[i].P;
            bounds.Add(new Vector3d(p.X, p.Y, p.Z));
        }
        md.Bounds = bounds;
    }

    /// <summary>
    /// Сколько раз чередовать разглаживание с перестановкой. Задаётся числом, чтобы можно было
    /// измерить, окупаются ли лишние заходы.
    /// </summary>
    public static int Rounds =
        int.TryParse(Environment.GetEnvironmentVariable("P3D_SMOOTH"), out int r) && r > 0 ? r : 4;

    /// <summary>Разумный размер элемента: двадцатая доля наибольшего габарита тела.</summary>
    public static double SuggestSize(TSolid solid)
    {
        Vector3d lo = solid.Bounds.Min, hi = solid.Bounds.Max;

        if (hi.X < lo.X) return 1;
        double span = Math.Max(hi.X - lo.X, Math.Max(hi.Y - lo.Y, hi.Z - lo.Z));
        return span > 0 ? span / 20 : 1;
    }

    /// <summary>
    /// Сетка ОДНОЙ грани на месте, без вынимания её из тела. Нужна для разбора: грань, перенесённая
    /// в отдельное тело, теряет смежности, и числа по ней перестают сходиться с числами по телу —
    /// на этом уже один разбор зашёл в тупик.
    /// </summary>
    public static MeshData BuildFace(TFace face, double size, out bool exact)
    {
        ValidateSize(size);
        var md = new MeshData();
        exact = false;
        // Нельзя публиковать половину грани, если её запасной mesher упал после AddTriangle.
        var mb = new MeshBuilder();
        try
        {
            mb.BeginFace(face.Id);
            exact = FaceInto(face, size, mb);
            mb.EndFace();
        // ТА ЖЕ ЧИСТКА, ЧТО И У ПОКАЗНОЙ СЕТКИ: расчётная собирается тем же построителем, и у неё
        // те же схлопнувшиеся треугольники от сведения точек соседних граней. Без чистки их
        // выходило 2 393 по ста телам — расчётная сетка была ХУЖЕ показной, где их ноль.
            mb.FillInto(md, weld: true);
            if (md.Indices.Length == 0) throw new InvalidOperationException("Face produced no nondegenerate triangles.");
            md.FaceRanges[face.Id] = new FaceRange {
                VertexStart = 0, VertexCount = md.Vertices.Length,
                IndexStart = 0, IndexCount = md.Indices.Length
            };
            SetBounds(md);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            exact = false;
            md = new MeshData();
            md.BrokenFaces.Add((face.Id, ex.Message));
        }
        return md;
    }

    /// <summary>Сетка одной грани. Возвращает false, если грань не берётся этим путём.</summary>
    private static bool FaceInto(TFace face, double size, MeshBuilder mb)
    {
        var src = face.Surface is FacetedSurface fs ? fs.Source : face.Surface;

        // Плоскость (в том числе записанная выдавливанием отрезка) — случай, где всё считается
        // прямо, без развёртки.
        Frame3? frame = null;
        if (src is PlaneSurface pl) frame = pl.F;
        else if (src is ExtrudedSurface ex && ex.Base is Curves.LineCurve3 ln)
        {
            Vector3d u = (ln.B - ln.A).Normalized();
            Vector3d n = Vector3d.Cross(u, ex.Dir.Normalized());
            if (n.LengthSquared > 1e-18) frame = Frame3.FromNormal(ln.A, n.Normalized());
        }

        // ЦИЛИНДР РАЗВОРАЧИВАЕТСЯ В ПЛОСКОСТЬ БЕЗ ИСКАЖЕНИЯ. Длины по развёртке те же, что на
        // поверхности, поэтому к нему применимы ровно те же правила: шаг элемента, шахматная
        // расстановка, перестановка по Делоне. Отсюда и качество то же, а не «как получится».
        if (src is ExtrudedSurface cyl && cyl.Base is Curves.ArcCurve3 arc)
            return CylinderInto(face, cyl, arc, size, mb);

        // ПОВЕРХНОСТЬ ВРАЩЕНИЯ И ПРОЧИЕ — ПО ПАРАМЕТРАМ. Для них нет разворота в плоскость без
        // искажения, но есть своя область (u, v): в ней грань — обычный плоский участок, и его
        // можно разметить теми же приёмами, а потом отобразить обратно.
        //
        // Шаг в параметрах подбирается так, чтобы в пространстве он был близок к заданному: для
        // этого область опрашивается и меряется, насколько быстро по ней бежит точка.
        if (frame == null && (src is RevolvedSurface || src is SectionSurface { Closed: false }))
            return ByParameters(face, src, size, mb);

        if (frame is not { } f2) return Fallback(face, mb);
        // РЁБРА КОНТУРА СОБИРАЮТСЯ В КОЛЬЦА ПО СВЯЗЯМ. Булева кладёт ВСЕ рёбра грани в один
        // контур и без порядка: у грани с отверстием там и наружная граница, и кромка
        // отверстия вперемешку. Если брать их подряд, ломаная идёт зигзагом, и треугольники
        // выходят нулевыми — отсюда и были все выродившиеся (30 на теле с отверстием).
        //
        // Кольца строятся по вершинам: конец одного ребра ищется началом следующего. Самое
        // крупное кольцо — наружная граница, остальные — отверстия.
        var loops = new List<List<Vector2d>>();

        foreach (var loop in face.Loops)
        {
            var rings = RingsOf(loop, size, p => new Vector2d(
                         Vector3d.Dot(p - f2.Origin, f2.U), Vector3d.Dot(p - f2.Origin, f2.V)));
            if (rings.Count == 0) return Fallback(face, mb, "незамкнутый или вырожденный контур");
            foreach (var ring in rings)
                loops.Add(ring);
        }

        if (loops.Count == 0) return Fallback(face, mb);
        OuterFirst(loops);

        if (loops.Count == 0) return Fallback(face, mb);
        // слишком близко к границе: иначе у края получаются лоскуты.
        var inner = InnerPoints(loops, size);

        var outer = loops[0];
        var holes = loops.Skip(1).Cast<IReadOnlyList<Vector2d>>().ToList();

        var tris = Cdt.Triangulate(outer, holes, out var all, inner);
        if (tris == null) return Fallback(face, mb);

        int boundaryCount = outer.Count + loops.Skip(1).Sum(h => h.Count);

        // РАЗГЛАЖИВАНИЕ И ПЕРЕСТАНОВКА ЧЕРЕДУЮТСЯ. Разглаживание двигает точки и тем нарушает
        // условие Делоне, которое перестановка только что навела; одно без другого не работает,
        // и измерено, что одно разглаживание качества не меняет.
        for (int round = 0; round < Rounds; round++)
        {
            Smooth(all, tris, boundaryCount, 3);
            Cdt.FlipToDelaunay(tris, all);
        }

        int start = mb.VertexCount;
        Vector3d normal = f2.W.Normalized();
        if (face.Reversed) normal = -normal;

        foreach (var uv in all)
            mb.AddVertex(f2.Origin + f2.U * uv.X + f2.V * uv.Y, normal);

        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            Vector2d a = all[tris[i]], b = all[tris[i + 1]], c = all[tris[i + 2]];
            double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            bool flip = face.Reversed ? area > 0 : area < 0;

            if (flip) mb.AddTriangle(start + tris[i], start + tris[i + 2], start + tris[i + 1]);
            else mb.AddTriangle(start + tris[i], start + tris[i + 1], start + tris[i + 2]);
        }

        return true;
    }

    /// <summary>
    /// Сетка цилиндрической грани по её развёртке: вдоль окружности — длина дуги, вдоль оси —
    /// расстояние. Обратное отображение точное, потому что разворот цилиндра не искажает длин.
    /// </summary>
    private static bool CylinderInto(TFace face, ExtrudedSurface cyl, Curves.ArcCurve3 arc,
                                     double size, MeshBuilder mb)
    {
        Vector3d axis = cyl.Dir.LengthSquared < 1e-18 ? Vector3d.UnitZ : cyl.Dir.Normalized();
        Vector3d centre = arc.Frame.Origin;
        double radius = arc.Radius;
        if (radius <= 0) return Fallback(face, mb, "радиус не годится");
        double alignment = Vector3d.Dot(arc.Frame.W.Normalized(), axis);
        if (Math.Abs(alignment) < 1 - 1e-9)
            return Fallback(face, mb, "косое выдавливание не разворачивается как цилиндр");
        Vector2d ToFlat(Vector3d p)
        {
            Vector3d rel = p - centre;
            double y = Vector3d.Dot(rel, axis);
            Vector3d radial = rel - axis * y;
            double a = Math.Atan2(Vector3d.Dot(radial, arc.Frame.V), Vector3d.Dot(radial, arc.Frame.U));
            return new Vector2d(a * radius, y);
        }

        Vector3d ToSpace(Vector2d f)
        {
            double a = f.X / radius;
            return centre + (arc.Frame.U * Math.Cos(a) + arc.Frame.V * Math.Sin(a)) * radius + axis * f.Y;
        }

        // КОЛЬЦА СОБИРАЮТСЯ ПО СВЯЗЯМ, А НЕ В ТОМ ПОРЯДКЕ, В КАКОМ ИХ СЛОЖИЛА БУЛЕВА. Она кладёт
        // рёбра грани без порядка; у плоских граней это уже учтено, а здесь бралось подряд — и
        // ломаная развёртки складывалась сама в себя. Измерено: площадь со знаком выходила −36
        // при должных 1380, и триангуляция отказывала на всех 63 цилиндрах прогона.
        //
        // Шов учитывается при развороте точки: угол продолжается по ближайшему обороту к
        // предыдущей точке КОЛЬЦА, а не к предыдущей по списку.
        double previous = double.NaN;

        Vector2d Flatten(Vector3d p)
        {
            var flat = ToFlat(p);

            if (!double.IsNaN(previous))
            {
                double turn = 2 * Math.PI * radius;
                while (flat.X - previous > turn / 2) flat.X -= turn;
                while (previous - flat.X > turn / 2) flat.X += turn;
            }

            previous = flat.X;
            return flat;
        }

        var loops = new List<List<Vector2d>>();

        foreach (var loop in face.Loops)
        {
            var rings = RingsOf(loop, size, Flatten, () => previous = double.NaN);
            if (rings.Count == 0) return Fallback(face, mb, "незамкнутый цилиндрический контур");
            foreach (var ring in rings)
            {
                loops.Add(ring);
            }
        }

        if (loops.Count == 0) return Fallback(face, mb, "колец не вышло");
        // строится прямо, а шов проходит по краю: точки на нём задваиваются, но лежат в тех же
        // местах пространства, поэтому тело остаётся сходящимся, а качество выходит то же, что
        // у плоской грани.
        double around = 2 * Math.PI * radius;
        double width = loops[0].Max(p => p.X) - loops[0].Min(p => p.X);

        if (width > around - size)
        {
            // Прямоугольник допустим ТОЛЬКО для полосы между двумя полными плоскими кольцами.
            // Раньше этот shortcut выбрасывал все остальные контуры, в том числе отверстия.
            bool fullBand = loops.Count == 2 && loops.All(l =>
                l.Max(p => p.Y) - l.Min(p => p.Y) < 1e-8 &&
                l.Max(p => p.X) - l.Min(p => p.X) > around - size);
            // У примитива два кольца могут быть соединены двумя использованиями seam-edge:
            // после развёртки это один настоящий прямоугольный контур, а не два кольца.
            bool seamRectangle = false;
            if (loops.Count == 1)
            {
                var ring = loops[0];
                double x0 = ring.Min(p => p.X), x1 = ring.Max(p => p.X);
                double y0 = ring.Min(p => p.Y), y1 = ring.Max(p => p.Y);
                double area2 = 0;
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i] - new Vector2d(x0, y0);
                    var b = ring[(i + 1) % ring.Count] - new Vector2d(x0, y0);
                    area2 += a.X * b.Y - a.Y * b.X;
                }
                double rectangleArea = (x1-x0)*(y1-y0);
                seamRectangle = rectangleArea > 0 && Math.Abs(x1-x0-around) < 1e-7 * around &&
                    Math.Abs(Math.Abs(area2)/2-rectangleArea) < rectangleArea*1e-8 &&
                    ring.All(p => Math.Abs(p.X-x0)<1e-8 || Math.Abs(p.X-x1)<1e-8 ||
                                  Math.Abs(p.Y-y0)<1e-8 || Math.Abs(p.Y-y1)<1e-8);
            }
            if (!fullBand && !seamRectangle)
                return Fallback(face, mb, "периодический контур требует разреза по шву");
            double yLow = loops.SelectMany(l => l).Min(p => p.Y);
            double yHigh = loops.SelectMany(l => l).Max(p => p.Y);
            if (yHigh - yLow <= 1e-9) return Fallback(face, mb, "высота нулевая");

            // ВЫРАВНИВАНИЕ ШАГА ПО КРАЮ И ВНУТРИ ПРОБОВАЛ — СТАЛО ХУЖЕ: плохих на боковой
            // поверхности 93 → 114. Значит, лоскуты берутся не от разницы шагов, и мельчить
            // внутреннюю сетку под краевую незачем.
            int nx = Math.Max(3, (int)Math.Ceiling(around / size));
            int ny = Math.Max(1, (int)Math.Ceiling((yHigh - yLow) / size));


            var rect = new List<Vector2d>();
            for (int i = 0; i < nx; i++) rect.Add(new Vector2d(around * i / nx, yLow));
            for (int j = 0; j < ny; j++) rect.Add(new Vector2d(around, yLow + (yHigh - yLow) * j / ny));
            for (int i = nx; i > 0; i--) rect.Add(new Vector2d(around * i / nx, yHigh));
            for (int j = ny; j > 0; j--) rect.Add(new Vector2d(0, yLow + (yHigh - yLow) * j / ny));

            loops = new List<List<Vector2d>> { rect };
        }
        else OuterFirst(loops);

        var inner = InnerPoints(loops, size);
        var holes = loops.Skip(1).Cast<IReadOnlyList<Vector2d>>().ToList();

        var tris = Cdt.Triangulate(loops[0], holes, out var all, inner);
        if (tris == null) return Fallback(face, mb, "триангуляция не вышла");

        int boundaryCount = loops.Sum(l => l.Count);

        for (int round = 0; round < Rounds; round++)
        {
            Smooth(all, tris, boundaryCount, 3);
            Cdt.FlipToDelaunay(tris, all);
        }

        int start = mb.VertexCount;
        foreach (var f in all)
        {
            Vector3d p = ToSpace(f);
            Vector3d n = (p - centre) - axis * Vector3d.Dot(p - centre, axis);
            n = n.LengthSquared < 1e-18 ? axis : n.Normalized();
            n *= Math.Sign(alignment * arc.Sweep);
            if (face.Reversed) n = -n;
            mb.AddVertex(p, n);
        }

        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            Vector2d a = all[tris[i]], b = all[tris[i + 1]], c = all[tris[i + 2]];
            double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            bool reversed = face.Reversed ^ (arc.Sweep < 0);
            bool flip = reversed ? area > 0 : area < 0;

            if (flip) mb.AddTriangle(start + tris[i], start + tris[i + 2], start + tris[i + 1]);
            else mb.AddTriangle(start + tris[i], start + tris[i + 1], start + tris[i + 2]);
        }

        return true;
    }

    // ПРИЧИНА ВЫРОДИВШИХСЯ НАЙДЕНА И УСТРАНЕНА, но не там, где я искал сперва.
    //
    // Сначала я решил, что булева создаёт перемычку от наружной границы к отверстию, и пробовал
    // резать контур по повторяющейся точке: выродившихся стало меньше (30 → 26), а плохих больше
    // (45 → 75) — размен не в нашу пользу, откатил.
    //
    // На деле булева НЕ СОЗДАЁТ перемычки: она кладёт все рёбра грани в ОДИН контур и без
    // порядка. Ломаная, взятая в этом порядке, шла зигзагом, и треугольники выходили нулевыми.
    // Сборка колец по связям вершин это снимает целиком:
    //
    //   тело с отверстием, шаг 8:  выродившихся 30 → 0, плохих 45 → 12, перекос 0.492 → 0.348
    //   тело с отверстием, шаг 4:  выродившихся 75 → 0
    //
    // Урок: прежде чем чинить, стоит посмотреть, что именно отдаёт предыдущий шаг. Я потратил
    // ход на догадку о перемычке, которой нет.

    /// <summary>
    /// Кольца контура: рёбра связываются по общим вершинам. Возвращает столько наборов точек,
    /// сколько замкнутых колец нашлось. Незамкнутый остаток отвергает весь контур, чтобы
    /// незамкнутое отверстие не было молча залито. beginRing сбрасывает периодическую развёртку.
    /// </summary>
    private static List<List<Vector2d>> RingsOf(TLoop loop, double size, Func<Vector3d, Vector2d> flatten,
                                                Action? beginRing = null)
    {
        var rings = new List<List<Vector2d>>();
        var left = new List<TCoEdge>(loop.CoEdges);

        while (left.Count > 0)
        {
            // Начать с seam/обычного ребра, если оно есть: одно замкнутое ребро окружности
            // может быть частью границы цилиндрической полосы, соединённой швом с другим.
            int seed = left.FindIndex(ce => !Same(ce.Start, ce.End));
            if (seed < 0) seed = 0;
            // Храним фактическое направление каждого ребра, включая первое.
            var chain = new List<(TCoEdge Edge, bool Reverse)> { (left[seed], left[seed].Reversed) };
            left.RemoveAt(seed);

            var start = chain[0].Edge.Start;
            var at = chain[0].Edge.End;

            bool grew = true;
            while (grew && at != null)
            {
                grew = false;
                for (int i = 0; i < left.Count; i++)
                {
                    if (!Same(left[i].Start, at) && !Same(left[i].End, at)) continue;

                    bool forward = Same(left[i].Start, at);
                    chain.Add((left[i], forward ? left[i].Reversed : !left[i].Reversed));
                    at = forward ? left[i].End : left[i].Start;
                    left.RemoveAt(i);
                    grew = true;
                    break;
                }
            }

            // КОЛЬЦО ИЗ ОДНОГО ЗАМКНУТОГО РЕБРА ТОЖЕ КОЛЬЦО. Торец цилиндра ограничен одной
            // окружностью: у неё начало и конец — одна и та же вершина, и требование «не меньше
            // трёх рёбер» её отвергало. Измерено: обе торцевые грани уходили на обычное гранение,
            // то есть на веер из вершины — худшее, что бывает для расчёта.
            bool closedAlone = chain.Count == 1 && Same(chain[0].Edge.Start, chain[0].Edge.End);

            // Две дуги тоже ограничивают допустимую область. Любой незамкнутый остаток
            // отвергает ВЕСЬ контур: игнорировать его значит молча заполнить отверстие.
            if (!closedAlone && (at == null || !Same(at, start))) return new();

            var pts = new List<Vector2d>();
            beginRing?.Invoke();

            foreach (var (ce, reversed) in chain)
            {
                var c = ce.Edge.Curve;
                if (c == null) return new();

                int n = Math.Max(1, (int)Math.Ceiling(c.Length / size));
                if (c.IsClosed) n = Math.Max(8, n);
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)n;
                    double tt = reversed ? c.T1 + (c.T0 - c.T1) * t : c.T0 + (c.T1 - c.T0) * t;
                    pts.Add(flatten(c.PointAt(tt)));
                }
            }

            if (pts.Count < 3) return new();
            rings.Add(pts);
        }

        return rings;
    }

    private static bool Same(TVertex? a, TVertex? b) =>
        a != null && b != null && (ReferenceEquals(a, b) || (a.P - b.P).LengthSquared < 1e-12);

    /// <summary>Наружным считается кольцо с наибольшим охватом.</summary>
    private static void OuterFirst(List<List<Vector2d>> loops)
    {
        int biggest = 0;
        double best = -1;

        for (int i = 0; i < loops.Count; i++)
        {
            double w = loops[i].Max(p => p.X) - loops[i].Min(p => p.X);
            double h = loops[i].Max(p => p.Y) - loops[i].Min(p => p.Y);
            if (w * h <= best) continue;
            best = w * h;
            biggest = i;
        }

        if (biggest != 0) (loops[0], loops[biggest]) = (loops[biggest], loops[0]);
    }

    /// <summary>
    /// Разметка грани в её собственных параметрах. Годится там, где грань — связный участок
    /// области (u, v): у поверхности вращения это так, если она не замыкается сама на себя.
    /// </summary>
    private static bool ByParameters(TFace face, ISurface3 src, double size, MeshBuilder mb)
    {
        var uvLoops = new List<List<Vector2d>>();
        foreach (var loop in face.Loops)
        {
            var rings = RingsOf(loop, size, src.ProjectToUV);
            if (rings.Count == 0) return Fallback(face, mb, "незамкнутый параметрический контур");
            uvLoops.AddRange(rings);
        }
        if (uvLoops.Count == 0) return Fallback(face, mb);
        double uMin = uvLoops.SelectMany(l => l).Min(p => p.X);
        double uMax = uvLoops.SelectMany(l => l).Max(p => p.X);
        double vMin = uvLoops.SelectMany(l => l).Min(p => p.Y);
        double vMax = uvLoops.SelectMany(l => l).Max(p => p.Y);
        // SectionSurface.u — номер полосы либо параметр хребта, НЕ всегда [0,1].
        if (!double.IsFinite(uMin + uMax + vMin + vMax) || uMax <= uMin || vMax <= vMin)
            return Fallback(face, mb, "параметрическая область вырождена");
        // Насколько быстро точка бежит по области: нужно, чтобы шаг в параметрах отвечал шагу в
        // пространстве.
        double du = 0, dv = 0;

        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
            {
                var at = new Vector2d(uMin + (uMax-uMin)*i/4.0, vMin + (vMax-vMin)*j/4.0);
                double eu = (uMax-uMin)*1e-4, ev = (vMax-vMin)*1e-4;
                double u = i == 4 ? at.X-eu : at.X+eu;
                double v = j == 4 ? at.Y-ev : at.Y+ev;
                du = Math.Max(du, (src.PointAt(new Vector2d(u, at.Y)) - src.PointAt(at)).Length / eu);
                dv = Math.Max(dv, (src.PointAt(new Vector2d(at.X, v)) - src.PointAt(at)).Length / ev);
            }

        if (!double.IsFinite(du + dv) || du < 1e-9 || dv < 1e-9) return Fallback(face, mb);
        var loops = uvLoops.Select(l => l.Select(uv =>
            new Vector2d((uv.X-uMin)*du, (uv.Y-vMin)*dv)).ToList()).ToList();

        if (loops.Count == 0) return Fallback(face, mb);
        OuterFirst(loops);

        var inner = InnerPoints(loops, size);
        var holes = loops.Skip(1).Cast<IReadOnlyList<Vector2d>>().ToList();

        var tris = Cdt.Triangulate(loops[0], holes, out var all, inner);
        if (tris == null) return Fallback(face, mb);

        int boundaryCount = loops.Sum(l => l.Count);

        for (int round = 0; round < Rounds; round++)
        {
            Smooth(all, tris, boundaryCount, 3);
            Cdt.FlipToDelaunay(tris, all);
        }

        int start = mb.VertexCount;

        foreach (var q in all)
        {
            var uv = new Vector2d(q.X / du + uMin, q.Y / dv + vMin);

            Vector3d p = src.PointAt(uv);
            Vector3d n = src.NormalAt(uv);
            if (face.Reversed) n = -n;

            mb.AddVertex(p, n.LengthSquared > 1e-18 ? n.Normalized() : Vector3d.UnitZ);
        }

        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            Vector2d a = all[tris[i]], b = all[tris[i + 1]], c = all[tris[i + 2]];
            double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            bool flip = face.Reversed ? area > 0 : area < 0;

            if (flip) mb.AddTriangle(start + tris[i], start + tris[i + 2], start + tris[i + 1]);
            else mb.AddTriangle(start + tris[i], start + tris[i + 1], start + tris[i + 2]);
        }

        return true;
    }

    /// <summary>Точки внутри грани по сетке с заданным шагом.</summary>
    private static List<Vector2d> InnerPoints(List<List<Vector2d>> loops, double size)
    {
        var res = new List<Vector2d>();
        var accepted = new PointGrid(size * 0.75);
        var outer = loops[0];

        double xMin = outer.Min(p => p.X), xMax = outer.Max(p => p.X);
        double yMin = outer.Min(p => p.Y), yMax = outer.Max(p => p.Y);

        // КРАЕВОЙ РЯД. Вдоль каждого контура кладётся отдельный ряд точек, смещённый внутрь на
        // высоту равностороннего треугольника, и только за ним идёт общая сетка. Иначе у края
        // выходит полоса, где треугольники тянутся от частых краевых точек к редким внутренним:
        // измерено — на боковой поверхности цилиндра 93 плохих из 286, и ни разглаживание, ни
        // выравнивание шага, ни шов тут ни при чём (все три проверены и отвергнуты).
        double inset = size * Math.Sqrt(3) / 2;

        foreach (var loop in loops)
        {
            int n = loop.Count;
            if (n < 3) continue;

            for (int i = 0; i < n; i++)
            {
                Vector2d a = loop[i], b = loop[(i + 1) % n];
                Vector2d along = b - a;
                double len = along.Length;
                if (len < 1e-12) continue;

                along /= len;
                var into = new Vector2d(-along.Y, along.X);     // перпендикуляр влево

                // Сторона обходится в своём направлении, поэтому «внутрь» выбирается по пробе.
                Vector2d probe = (a + b) / 2 + into * inset;
                if (!Inside(loops[0], probe) || loops.Skip(1).Any(h => Inside(h, probe))) into = -into;

                int parts = Math.Max(1, (int)Math.Round(len / size));

                for (int k = 0; k <= parts; k++)
                {
                    Vector2d p = a + along * (len * k / parts) + into * inset;

                    if (!Inside(loops[0], p)) continue;
                    if (loops.Skip(1).Any(h => Inside(h, p))) continue;
                    if (loops.Any(l => NearBoundary(l, p, inset * 0.8))) continue;
                    if (accepted.Near(p, size * 0.7)) continue;

                    res.Add(p);
                    accepted.Add(p);
                }
            }
        }

        // ШАХМАТНАЯ РАССТАНОВКА, А НЕ КВАДРАТНАЯ. У квадратной сетки соседи стоят под прямым
        // углом, и треугольники выходят прямоугольными; при смещении через ряд получается
        // правильный шестиугольник, а его треугольники близки к равносторонним.
        double rowStep = size * Math.Sqrt(3) / 2;

        // ОТСТУП ОТ ГРАНИЦЫ считается и до её ТОЧЕК, а не только до отрезков. При мелком шаге
        // точек по краю много, и внутренняя точка, легшая рядом с одной из них, даёт лоскут:
        // измерено — при шаге 5 мм плохих выходило 423 из 884.
        double keepEdge = size * 0.55;
        double keepPoint = size * 0.75;

        var boundaryPoints = new PointGrid(size * 0.75);
        foreach (var p in loops.SelectMany(l => l)) boundaryPoints.Add(p);

        int row = 0;
        for (double y = yMin + rowStep / 2; y < yMax; y += rowStep, row++)
        {
            double shift = (row % 2 == 0) ? 0 : size / 2;

            for (double x = xMin + size / 2 + shift; x < xMax; x += size)
            {
                var p = new Vector2d(x, y);
                if (!Inside(outer, p)) continue;
                if (loops.Skip(1).Any(h => Inside(h, p))) continue;
                if (loops.Any(l => NearBoundary(l, p, keepEdge))) continue;
                if (boundaryPoints.Near(p, keepPoint)) continue;
                if (accepted.Near(p, size * 0.7)) continue;   // не ближе краевого ряда
                res.Add(p);
                accepted.Add(p);
            }
        }

        return res;
    }

    // Только поиск соседних samples; порядок вставки и точный distance-test сохранены.
    // Линейный res.Any на каждом кандидате давал квадратичную работу на мелкой сетке.
    private sealed class PointGrid(double cell)
    {
        private readonly Dictionary<(long X, long Y), List<Vector2d>> _cells = new();
        private (long X, long Y) Key(Vector2d p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
        public void Add(Vector2d p)
        {
            var key = Key(p);
            if (!_cells.TryGetValue(key, out var bucket)) _cells[key] = bucket = new();
            bucket.Add(p);
        }
        public bool Near(Vector2d p, double distance)
        {
            var key = Key(p);
            int reach = (int)Math.Ceiling(distance / cell);
            for (int x = -reach; x <= reach; x++)
                for (int y = -reach; y <= reach; y++)
                    if (_cells.TryGetValue((key.X + x, key.Y + y), out var bucket))
                        foreach (var q in bucket)
                            if ((q - p).Length < distance) return true;
            return false;
        }
    }

    private static bool Inside(IReadOnlyList<Vector2d> poly, Vector2d p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if (poly[i].Y > p.Y == poly[j].Y > p.Y) continue;
            double x = (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X;
            if (p.X < x) inside = !inside;
        }
        return inside;
    }

    private static bool NearBoundary(IReadOnlyList<Vector2d> poly, Vector2d p, double limit)
    {
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector2d a = poly[j], b = poly[i];
            Vector2d d = b - a;
            double len2 = d.LengthSquared;
            if (len2 < 1e-18) continue;

            double t = Math.Clamp(Vector2d.Dot(p - a, d) / len2, 0, 1);
            if ((p - (a + d * t)).Length < limit) return true;
        }
        return false;
    }

    /// <summary>
    /// Разглаживание по Лапласу: точка внутри переезжает в середину своих соседей. Граница не
    /// трогается — это геометрия детали, а не свобода построителя.
    /// </summary>
    private static void Smooth(List<Vector2d> points, List<int> tris, int boundaryCount, int passes)
    {
        var neighbours = new List<HashSet<int>>();
        var incident = new List<List<int>>();
        for (int i = 0; i < points.Count; i++) { neighbours.Add(new HashSet<int>()); incident.Add(new()); }

        for (int i = 0; i + 2 < tris.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = tris[i + k], b = tris[i + (k + 1) % 3];
                neighbours[a].Add(b);
                neighbours[b].Add(a);
                incident[a].Add(i);
            }

        for (int pass = 0; pass < passes; pass++)
            for (int i = boundaryCount; i < points.Count; i++)
            {
                if (neighbours[i].Count == 0) continue;

                Vector2d sum = Vector2d.Zero;
                foreach (int n in neighbours[i]) sum += points[n];
                var before = points[i];
                var target = sum / neighbours[i].Count;
                // Среднее соседей может выйти из ядра невыпуклой звезды вершины.
                // Принимаем только шаг, сохраняющий ориентацию всех инцидентных треугольников.
                for (double step = 1; step >= 1.0 / 1024; step *= 0.5)
                {
                    var candidate = before + (target - before) * step;
                    bool valid = true;
                    foreach (int t in incident[i])
                    {
                        var a = points[tris[t]]; var b = points[tris[t + 1]]; var c = points[tris[t + 2]];
                        double oldArea = (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
                        if (tris[t] == i) a = candidate;
                        if (tris[t + 1] == i) b = candidate;
                        if (tris[t + 2] == i) c = candidate;
                        double area = (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
                        if (!double.IsFinite(area) || area * Math.Sign(oldArea) <= Math.Abs(oldArea) * 1e-10)
                        { valid = false; break; }
                    }
                    if (valid) { points[i] = candidate; break; }
                }
            }
    }

    /// <summary>Сколько раз отступили, по видам поверхностей — для разбора охвата.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FellBack = new();

    /// <summary>Грань, которую этот построитель не берёт, гранится как обычно.</summary>
    private static bool Fallback(TFace face, MeshBuilder mb, string why = "")
    {
        var kind = (face.Surface is FacetedSurface fsk ? fsk.Source : face.Surface)?.GetType().Name ?? "?";
        FellBack.AddOrUpdate(kind.Replace("Surface", "") + (why == "" ? "" : ": " + why), 1, (_, had) => had + 1);


        // Отказ проходит в BuildFace: там он атомарно превращается в BrokenFaces.
        face.Surface.TessellateFace(face, mb);
        return false;
    }
}
