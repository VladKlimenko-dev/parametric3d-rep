using OpenTK.Mathematics;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// Грань, заданная готовыми треугольниками.
///
/// Появляется после булевой операции: разбиение пространства режет грани
/// на плоские куски, и восстановить по ним аналитическое описание нельзя.
/// Поэтому грань несёт треугольники напрямую.
///
/// ИСХОДНАЯ ПОВЕРХНОСТЬ СОХРАНЯЕТСЯ, если она известна. Это не роскошь:
/// по ней работают «плоская ли грань» и «нормаль в точке», а на них
/// держится выбор грани как плоскости-базиса эскиза. Без этого плоская
/// стенка после выреза перестала бы быть плоской для приложения.
/// </summary>
public sealed class FacetedSurface : ISurface3, Topology.IFreezable
{
    /// <summary>Треугольники грани: тройки точек в мировых координатах.</summary>
    /// <summary>Треугольники поверхности — её форма. Поставка 2: неизменяемы, когда грань опубликована (Freeze).</summary>
    public readonly Topology.TopoList<(Vector3d A, Vector3d B, Vector3d C)> Facets;
    /// <summary>KRN-10: треугольники построены разметчиком по контурам грани (годятся донором той же грани по ключу).</summary>
    public bool FromLoops { get; set; }
    private bool _frozen;
    bool Topology.IFreezable.IsFrozen => _frozen;
    string Topology.IFreezable.FreezeName => "FacetedSurface";
    /// <summary>
    /// Поставка 3: СЫРОЙ РЕЗУЛЬТАТ РАЗМЕТКИ ГРАНИ (до общего разбора тела) и его точный ключ — донор для той же
    /// грани в следующем перестроении детали (BooleanOp.ReuseFaceMeshes). Производные данные: пишутся при
    /// разметке, до публикации; отдаются только копиями.
    /// </summary>
    internal (string Key, (List<Vector3d> P, List<int> T)? M, string? Why, List<Vector3d>? N)? RawMesh;

    /// <summary>Сделать треугольники неизменяемыми (публикация тела). Видимая сетка (Display) — производное, её можно менять.</summary>
    internal void Freeze() => _frozen = true;

    /// <summary>
    /// ВИДИМАЯ СЕТКА — ОДНОЙ НЕИЗМЕНЯЕМОЙ ПАРОЙ ЗА ОДНОЙ ССЫЛКОЙ (поставка 2, остаток: гонка достройки видимой сетки).
    /// Прежде треугольники и нормали были двумя открытыми полями, писатели присваивали их по очереди, а отрисовка читала
    /// оба несколько раз: при перестройке сетки опубликованного тела одновременно с чтением она могла увидеть новые
    /// треугольники со старыми нормалями. Теперь писатель собирает новую пару и подменяет ссылку одним присваиванием
    /// (SetDisplay), читатель снимает ссылку один раз — видит либо прежнюю сетку, либо новую целиком.
    /// </summary>
    public sealed class DisplayMesh
    {
        public readonly List<(Vector3d A, Vector3d B, Vector3d C)> Tris;
        public readonly List<(Vector3d A, Vector3d B, Vector3d C)>? Normals;
        public DisplayMesh(List<(Vector3d A, Vector3d B, Vector3d C)> tris, List<(Vector3d A, Vector3d B, Vector3d C)>? normals)
        { Tris = tris; Normals = normals; }
    }
    private volatile DisplayMesh? _display;

    /// <summary>Снимок видимой сетки (null — показывать Facets). Читать один раз и пользоваться снимком.</summary>
    public DisplayMesh? DisplaySnapshot => _display;

    /// <summary>
    /// Видимые треугольники (этап 3): те же, что Facets, но вершины швов — на точных кривых рёбер, внутренние — на
    /// точной поверхности. null — показывать Facets. Только чтение; запись — SetDisplay.
    /// </summary>
    public List<(Vector3d A, Vector3d B, Vector3d C)>? Display => _display?.Tris;

    /// <summary>
    /// Нормали точной поверхности в вершинах видимой сетки (по три на треугольник, в порядке Display). Только чтение.
    /// </summary>
    public List<(Vector3d A, Vector3d B, Vector3d C)>? DisplayNormals => _display?.Normals;

    /// <summary>Заменить видимую сетку целиком (треугольники и нормали — одной ссылкой). null — показывать Facets.</summary>
    public void SetDisplay(List<(Vector3d A, Vector3d B, Vector3d C)>? tris, List<(Vector3d A, Vector3d B, Vector3d C)>? normals) =>
        _display = tris == null ? null : new DisplayMesh(tris, normals);

    /// <summary>Поверхность, из которой грань получилась. null — неизвестна.</summary>
    public readonly ISurface3? Source;

    /// <summary>Нормаль, если исходная поверхность неизвестна.</summary>
    private readonly Vector3d _fallbackNormal;
    /// <summary>Нормаль знака стороны (для копии поверхности при делении грани на области).</summary>
    internal Vector3d FallbackNormal => _fallbackNormal;

    public FacetedSurface(ISurface3? source, Vector3d fallbackNormal)
    {
        Facets = new(this, nameof(Facets));
        Source = source;
        _fallbackNormal = fallbackNormal.LengthSquared > 1e-18
            ? fallbackNormal.Normalized()
            : new Vector3d(0, 0, 1);
    }

    public bool IsPlanar => Source?.IsPlanar ?? AllFacetsCoplanar();

    public Vector3d PointAt(Vector2d uv)
    {
        if (Source != null) return Source.PointAt(uv);
        return Facets.Count > 0 ? Facets[0].A : Vector3d.Zero;
    }

    /// <summary>
    /// НОРМАЛЬ ИСХОДНОЙ ПОВЕРХНОСТИ, РАЗВЁРНУТАЯ НАРУЖУ.
    ///
    /// ИСПРАВЛЕНО. Здесь стояло просто Source.NormalAt(uv), и знак терялся.
    /// Сторону грани задавала не поверхность, а флаг TFace.Reversed:
    /// у выдавливания нижняя крышка и верхняя несут ОДНУ И ТУ ЖЕ плоскость
    /// и различаются только им. Булева же собирает грани заново и ставит
    /// Reversed = false всем подряд — флаг до результата не доживает.
    /// Половина граней после первой же операции сообщала нормаль внутрь
    /// тела.
    ///
    /// Заметить это по картинке нельзя: отрисовка берёт нормали из самих
    /// треугольников и остаётся правильной. Врут те, кто спрашивает грань:
    /// выпуклость ребра, направление среза фаски, распознавание
    /// тангенциального стыка.
    ///
    /// Знак восстанавливается по _fallbackNormal — это нормаль исходного
    /// многоугольника, пришедшая из разбиения пространства уже
    /// ориентированной наружу. Направление берётся у поверхности,
    /// сторона — у неё.
    /// </summary>
    public Vector3d NormalAt(Vector2d uv)
    {
        if (Source == null) return _fallbackNormal;

        Vector3d n = Source.NormalAt(uv);
        if (n.LengthSquared < 1e-18) return _fallbackNormal;

        // ЕДИНЫЙ СМЫСЛ ГРАНИ (sense): постоянный знак нормали исходной поверхности на всю грань,
        // определённый по самим треугольникам грани. Прежде знак выбирался в каждой точке по
        // средней нормали грани — у грани, охватывающей цилиндр по кругу (стенка вала, стенка
        // отверстия), средняя почти нулевая, и нормали разворачивались в одну полусферу: половина
        // нормалей смотрела внутрь (5 993 из 10 490 у стенки вала с глухим отверстием), и
        // тесселятор переставлял по ним верный обход треугольников.
        int sense = Sense;
        if (sense != 0) return sense > 0 ? n : -n;
        return Vector3d.Dot(n, _fallbackNormal) < 0 ? -n : n;
    }

    private int _sense;
    private int _senseKey = -1;
    private bool _senseConsistent = true;

    /// <summary>
    /// Знак нормали Source, согласный с обходом треугольников грани: +1, −1 или 0 (не определён:
    /// нет Source или нет годных треугольников). Считается по треугольникам (до 256 равномерно)
    /// и пересчитывается, если число треугольников изменилось.
    /// </summary>
    public int Sense { get { if (Facets.IsPending && LazySense != 0) return LazySense; if (_senseKey != Facets.Count) ComputeSense(); return _sense; } }

    /// <summary>
    /// k126: ОТЛОЖЕННАЯ РАЗМЕТКА. Знак нормали Source, который дадут треугольники, когда их построят (разметчик
    /// разворачивает их по стороне грани): пока треугольников нет, сторона известна без них. 0 — не отложено.
    /// </summary>
    public int LazySense { get; internal set; }
    /// <summary>k126: отложенная разметка не удалась (грань без треугольников; принятие тела отказывает).</summary>
    public string? LazyFailure { get; internal set; }

    /// <summary>Знаки треугольников согласны между собой (иначе грань несогласованна — диагностика).</summary>
    public bool SenseConsistent { get { if (Facets.IsPending && LazySense != 0) return true; if (_senseKey != Facets.Count) ComputeSense(); return _senseConsistent; } }

    private void ComputeSense()
    {
        _senseKey = Facets.Count; _sense = 0; _senseConsistent = true;
        if (Source == null || Facets.Count == 0) return;
        int pos = 0, neg = 0, step = Math.Max(1, Facets.Count / 256);
        for (int i = 0; i < Facets.Count; i += step)
        {
            var (a, b, c) = Facets[i];
            var fn = Vector3d.Cross(b - a, c - a);
            double l = fn.Length;
            if (l < 1e-14) continue;
            var centre = (a + b + c) / 3;
            Vector3d sn;
            try { sn = Source.NormalAt(Source.ProjectToUV(centre)); } catch { continue; }
            if (sn.LengthSquared < 1e-18) continue;
            double d = Vector3d.Dot(fn / l, sn.Normalized());
            if (d > 0.2) pos++; else if (d < -0.2) neg++;
        }
        if (pos == 0 && neg == 0) return;
        _sense = pos >= neg ? 1 : -1;
        _senseConsistent = pos == 0 || neg == 0;
    }

    public Vector2d ProjectToUV(Vector3d p) =>
        Source?.ProjectToUV(p) ?? Vector2d.Zero;

    /// <summary>
    /// Отдаёт готовые треугольники.
    ///
    /// Контуры грани при этом НЕ используются: после булевой операции они
    /// служат только для рёбер и выбора мышью, а форма уже посчитана.
    ///
    /// ИСКЛЮЧЕНИЕ — КРИВАЯ ГРАНЬ, КОТОРУЮ БУЛЕВА НЕ ТРОНУЛА. Треугольники
    /// разбиения описывают её ломаной, и скругление выглядит гранёным,
    /// хотя аналитическая поверхность у грани есть и она точна. Если
    /// грань уцелела целиком, форму лучше взять у поверхности.
    /// </summary>
    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        var d = _display;                                   // один снимок на всё гранение
        Tess(face, mb, d?.Tris ?? Facets.ReadView, d?.Normals);
    }

    /// <summary>
    /// Гранение по треугольникам разбиения (не по видимой сетке): для внутренних проверок ядра
    /// — они сравнивают с точным телом, а видимая сетка (этап 3) ближе к настоящей
    /// поверхности и отличается от него в пределах допуска гранения. Защитная проверка
    /// скругления, сравнивавшая тело на новой сетке с телом на прежней, видела «рост объёма»
    /// и откатывала рез.
    /// </summary>
    public void TessellateFacets(TFace face, MeshBuilder mb) => Tess(face, mb, Facets.ReadView, null);

    /// <summary>
    /// Треугольники разбиения без гладких нормалей: проверкам объёма, замкнутости и «точка
    /// внутри ли» нужны только положения, а гладкая нормаль стоит проекции каждой вершины на
    /// точную поверхность. Нормаль грани по соседним треугольникам (NormalNear) считает
    /// иначе — ей нужен гладкий вариант.
    /// </summary>
    public void TessellateFacetsFlat(TFace face, MeshBuilder mb)
    {
        // Щепки (нулевая площадь) НЕ отбрасываются: по ним считается парность сторон в
        // проверках замкнутости — без них защитная проверка видит открытые рёбра и
        // откатывает рез.
        foreach (var (a, b, c) in Facets)
        {
            if (a == b || b == c || c == a) continue;
            Vector3d n = Vector3d.Cross(b - a, c - a);
            n = n.LengthSquared > 1e-24 ? n.Normalized() : Vector3d.UnitZ;
            mb.AddTriangle(mb.AddVertex(a, n), mb.AddVertex(b, n), mb.AddVertex(c, n));
        }
    }

    private void Tess(TFace face, MeshBuilder mb, List<(Vector3d A, Vector3d B, Vector3d C)> shown,
                      List<(Vector3d A, Vector3d B, Vector3d C)>? shownNormals)
    {
        // Готовые нормали видимой сетки — без проекций (из того же снимка, что и треугольники).
        if (shownNormals != null && shownNormals.Count == shown.Count)
        {
            for (int i = 0; i < shown.Count; i++)
            {
                var (a, b, c) = shown[i];
                if (a == b || b == c || c == a) continue;
                var (na, nb, nc) = shownNormals[i];
                Vector3d flat = Vector3d.Cross(b - a, c - a);
                if (flat.LengthSquared < 1e-24) continue;
                if (Vector3d.Dot(na + nb + nc, flat) < 0) { na = -na; nb = -nb; nc = -nc; }
                Vector3d fn = flat.Normalized();
                // Та же защита, что у прежнего гранения: нормаль поверхности, разошедшаяся с
                // треугольником больше чем на 30° (особая точка — вершина конуса), заменяется
                // нормалью треугольника.
                Vector3d Safe(Vector3d n) => n.LengthSquared > 0.5 && Vector3d.Dot(n, fn) >= 0.866 ? n : fn;
                int j0 = mb.AddVertex(a, Safe(na));
                int j1 = mb.AddVertex(b, Safe(nb));
                int j2 = mb.AddVertex(c, Safe(nc));
                mb.AddTriangle(j0, j1, j2);
            }
            return;
        }

        // ПОЛОЖЕНИЯ ТОЧЕК — ТОЛЬКО ИЗ ТРЕУГОЛЬНИКОВ РАЗБИЕНИЯ.
        //
        // ИСПРАВЛЕНО, И ЭТО ОТКАТ МОЕЙ ЖЕ ПРАВКИ. Ради гладкого вида
        // кривая грань рисовалась по аналитической поверхности: точки
        // брались с дуги, а не с её хорд. Выглядело верно и было неверно —
        // СОСЕДНИЕ грани остались на хордах, и между ними появилась щель
        // по всей длине скругления.
        //
        // Щель не видна: она уже, чем пиксель. Но оболочка перестаёт быть
        // замкнутой, а по ней считают объём — и объём поплыл. Дальше поплыло
        // всё, что на него опирается: проверка «вычитание не может добавить
        // материала» стала срабатывать на исправных резах, фаска начала
        // откатываться.
        //
        // Урок простой: менять ГЕОМЕТРИЮ ради внешнего вида нельзя.
        // Вид чинится нормалями.
        bool smooth = Source != null && !Source.IsPlanar;

        // ГРАНЬ БЕЗ ИСХОДНОЙ ПОВЕРХНОСТИ — СГЛАЖИВАНИЕ ПО СОБСТВЕННЫМ ТРЕУГОЛЬНИКАМ.
        //
        // ДОБАВЛЕНО. Грань протянутой фаски (и любая, собранная из сечений) исходной
        // гладкой поверхности не имеет и рисовалась плоскими нормалями треугольников —
        // полосами. Нормаль в вершине — сумма нормалей треугольников этой грани, сходящихся
        // в ней (с весом по площади); треугольник, повёрнутый больше чем на 30°, в сумму
        // не входит — настоящие изломы остаются острыми. Геометрия не меняется, только вид.
        Dictionary<(double, double, double), List<Vector3d>>? around = null;
        if (Source == null && shown.Count > 1)
        {
            around = new Dictionary<(double, double, double), List<Vector3d>>();
            foreach (var (a, b, c) in shown)
            {
                Vector3d cr = Vector3d.Cross(b - a, c - a);
                if (cr.LengthSquared < 1e-30) continue;
                foreach (var v in new[] { a, b, c })
                {
                    var k = (v.X, v.Y, v.Z);
                    if (!around.TryGetValue(k, out var l)) around[k] = l = new List<Vector3d>();
                    l.Add(cr);
                }
            }
        }
        double crease = Math.Cos(30 * Math.PI / 180);
        Vector3d Averaged(Vector3d v, Vector3d flat)
        {
            if (around == null || !around.TryGetValue((v.X, v.Y, v.Z), out var l)) return flat;
            Vector3d sum = Vector3d.Zero;
            foreach (var n in l)
                if (Vector3d.Dot(n.Normalized(), flat) > crease) sum += n;
            return sum.LengthSquared > 1e-30 ? sum.Normalized() : flat;
        }

        // НОРМАЛИ ВЕРШИН СЧИТАЮТСЯ ОДИН РАЗ НА ПОВЕРХНОСТЬ.
        //
        // ДОБАВЛЕНО (этап 2). Грань гранится многократно — отрисовка, вход в булеву,
        // гладкая нормаль у фаски (NormalNear), проверки. У грани с процедурной исходной
        // поверхностью (SectionSurface) нормаль вершины — это точная проекция на неё, и
        // пересчёт при каждом гранении стоил 3.9 млн проекций на одну последовательность
        // проверочного прогона (51 с вместо долей секунды). Треугольники грани после
        // сборки не меняются; если их число изменилось — нормали считаются заново.
        // Поверхность по сечениям особых точек не имеет: нормаль — одна на вершину, а не
        // на каждый сходящийся в ней треугольник (вшестеро меньше точных проекций).
        Dictionary<(double, double, double), Vector3d>? perVertex = Source is SectionSurface ? new() : null;

        var cache = ReferenceEquals(shown, Facets) ? _normals : _normalsShown;
        bool reuse = cache != null && cache.Count == shown.Count;
        var fresh = reuse ? null : new Vector3d[shown.Count * 3];
        int fi = -1;

        foreach (var (a, b, c) in shown)
        {
            fi++;
            if (reuse)
            {
                if (a == b || b == c || c == a) continue;
                var nn = cache!.N;
                int j0 = mb.AddVertex(a, nn[3 * fi]), j1 = mb.AddVertex(b, nn[3 * fi + 1]), j2 = mb.AddVertex(c, nn[3 * fi + 2]);
                mb.AddTriangle(j0, j1, j2);
                continue;
            }

            // ЩЕПКИ НЕ ОТБРАСЫВАЮТСЯ.
            //
            // ИСПРАВЛЕНО. Здесь стоял порог |cross|² < 1e-24. Точное ядро даёт
            // разные вершины на расстоянии до 1e-10 мм, и треугольник между ними —
            // настоящий, хоть и тончайший, кусок поверхности. Порог принимал его
            // за вырожденный, на его месте оставалась щель, и экранная сетка
            // получала открытые рёбра при замкнутом теле. Отбрасывается только
            // треугольник с буквально совпавшими вершинами: у него нет площади,
            // а его стороны дублируют одна другую.
            if (a == b || b == c || c == a) continue;

            Vector3d flat = Vector3d.Cross(b - a, c - a);
            flat = flat.LengthSquared > 1e-30 ? flat.Normalized() : _fallbackNormal;

            // ГЛАДКОСТЬ ДАЮТ НОРМАЛИ, А НЕ ТОЧКИ.
            //
            // У каждой вершины берётся нормаль аналитической поверхности
            // в этой точке. Треугольники остаются теми же, силуэт остаётся
            // гранёным, но затенение идёт как у гладкого цилиндра —
            // а это и есть всё, что было нужно от прежней правки.
            //
            // Плоской грани такое не нужно: там нормаль и так одна.
            // Нормаль спрашивается в точке, сдвинутой от вершины на тысячную
            // внутрь своего треугольника. Для обычной вершины разница ничтожна.
            // А вершина конуса лежит на оси, где угол не определён: в ней самой
            // поверхность отвечает за случайную образующую, а сдвинутая точка
            // лежит на образующей именно этого треугольника.
            Vector3d mid = (a + b + c) / 3;
            Vector3d na = smooth && perVertex != null ? AtVertex(a, flat) : smooth ? At(a + (mid - a) * 1e-3, flat) : around != null ? Averaged(a, flat) : flat;
            Vector3d nb = smooth && perVertex != null ? AtVertex(b, flat) : smooth ? At(b + (mid - b) * 1e-3, flat) : around != null ? Averaged(b, flat) : flat;
            Vector3d nc = smooth && perVertex != null ? AtVertex(c, flat) : smooth ? At(c + (mid - c) * 1e-3, flat) : around != null ? Averaged(c, flat) : flat;
            fresh![3 * fi] = na; fresh[3 * fi + 1] = nb; fresh[3 * fi + 2] = nc;
            int i0 = mb.AddVertex(a, na);
            int i1 = mb.AddVertex(b, nb);
            int i2 = mb.AddVertex(c, nc);

            mb.AddTriangle(i0, i1, i2);
        }
        if (!reuse)
        {
            if (ReferenceEquals(shown, Facets)) _normals = new NormalCache(shown.Count, fresh!);
            else _normalsShown = new NormalCache(shown.Count, fresh!);
        }

        // Нормаль поверхности в точке, развёрнутая по нормали треугольника.
        // Если поверхность ответить не смогла, остаётся плоская.
        Vector3d At(Vector3d p, Vector3d flat)
        {
            if (Source == null) return flat;

            Vector3d n = Source.NormalAt(Source.ProjectToUV(p));

            if (n.LengthSquared < 1e-18) return flat;

            n = n.Normalized();
            if (Vector3d.Dot(n, flat) < 0) n = -n;

            // ОСОБАЯ ТОЧКА — НОРМАЛЬ ТРЕУГОЛЬНИКА. При обычном гранении нормаль
            // поверхности расходится с треугольником на единицы градусов.
            // Больше тридцати бывает только там, где поверхность ответить не может:
            // вершина конуса, в которой сходятся образующие всех направлений.
            // Там нормаль своего треугольника и есть лучший ответ.
            return Vector3d.Dot(n, flat) < 0.866 ? flat : n;
        }

        // Нормаль поверхности в вершине (одна на вершину), знак и отказ — по треугольнику.
        Vector3d AtVertex(Vector3d p, Vector3d flat)
        {
            var key = (p.X, p.Y, p.Z);
            if (!perVertex!.TryGetValue(key, out var n))
            {
                n = Source!.NormalAt(Source.ProjectToUV(p));
                n = n.LengthSquared < 1e-18 ? Vector3d.Zero : n.Normalized();
                perVertex[key] = n;
            }
            if (n.LengthSquared < 0.5) return flat;
            if (Vector3d.Dot(n, flat) < 0) n = -n;
            return Vector3d.Dot(n, flat) < 0.866 ? flat : n;
        }
    }

    private sealed record NormalCache(int Count, Vector3d[] N);
    private NormalCache? _normals, _normalsShown;

    private bool AllFacetsCoplanar()
    {
        if (Facets.Count == 0) return false;

        foreach (var (a, _, _) in Facets)
            if (Math.Abs(Vector3d.Dot(_fallbackNormal, a - Facets[0].A)) > 1e-6) return false;

        return true;
    }
}
