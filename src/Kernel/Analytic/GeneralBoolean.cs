using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;
using Parametric3D.Kernel.Geometry;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// АНАЛИТИЧЕСКОЕ ЯДРО, ЭТАП 2, БЛОК 4: ОБЩАЯ АНАЛИТИЧЕСКАЯ БУЛЕВА (плоскости любой ориентации, цилиндры любого направления).
///
/// 1. Пары граней A × B — куски пересечения (блок 2). 2. Каждая грань обоих тел разбивается своими кусками (блок 3);
/// точки разреза рёбер границы — ОБЩИЕ для всего тела (ребро делят две грани — резать в одних точках). 3. Области
/// отбираются классификатором другого тела (блок 1): объединение — снаружи; пересечение — внутри; разность — A снаружи B
/// и B внутри A (обращённые). 4. Сборка: рёбра — из реестра по ключу «объект кривой + диапазон параметра» (кусок
/// пересечения — один и тот же объект в разбиении граней A и B, ребро общее само), вершины — по положению.
///
/// Первая версия ОТКАЗЫВАЕТ при совпадающих поверхностях (копланарные грани) и касаниях — их правила — следующий шаг.
/// </summary>
public static class GeneralBoolean
{
    /// <summary>Мутация P3D_PAIRBOX=0: все пары граней идут в FaceIntersect.Of (прежде).</summary>
    static readonly bool PairBoxOff = Environment.GetEnvironmentVariable("P3D_PAIRBOX") == "0";
    static readonly bool PartitionOff = Environment.GetEnvironmentVariable("P3D_PARTITIONCHECK") == "0";
    static readonly bool PartStat = Environment.GetEnvironmentVariable("P3D_PARTSTAT") == "1";
    /// <summary>Проверка KRN05: внести потерю последней области у первой разрезанной грани (один раз).</summary>
    [ThreadStatic] public static bool DropRegionForTest;
    /// <summary>То же, но только у цилиндрической или конической грани.</summary>
    [ThreadStatic] public static bool DropCurvedRegionForTest;

    /// <summary>Площадь области плоской грани (знаковая, ½∮(x dy − y dx) в осях грани): 20-точечная квадратура по каждому
    /// куску контура — точна для отрезков и до ~1e-15 для дуг и эллипсов.</summary>
    /// <summary>
    /// Аддитивная мера области для проверки разбиения: плоскость — площадь; цилиндр, конус, сфера, тор — ∬ dh∧dθ в осях грани
    /// (h — по оси, θ — угол вокруг оси) = −∮ h dθ, где dθ/ds = (x y′ − y x′)/(x² + y²): без развёртки по шву (у сферы
    /// dA = R dh dθ — мера пропорциональна площади; ось тора тор не пересекает). Для проверки «сумма
    /// областей = грань» годится любая аддитивная мера. Мутация кривых граней — P3D_PARTITIONCURVED=0.
    /// </summary>
    static double Measure(AnalyticFace f, FaceSplit.Region r, Vector3d origin, out double lenW)
    {
        lenW = 0;
        if (!f.Cyl && !f.Cone && !f.Sph && !f.Tor) return PlanarArea(f, r, origin, out lenW);
        // сфера: высота от «южного» полюса (−W), где 1-форма (z + R) dθ гладкая; северный полюс W — вне грани (выбран так)
        double h0 = f.Sph ? -f.R : 0;
        double a = 0;
        foreach (var loop in r.Loops)
            foreach (var d in loop)
                for (int i = 0; i < GaussX.Length; i++)
                {
                    double sNode = (GaussX[i] + 1) / 2;
                    var p = d.At(sNode) - f.O;
                    Vector3d D(double h) => (d.At(sNode + h) - d.At(sNode - h)) / (2 * h);
                    var dp = (D(5e-4) * 4 - D(1e-3)) / 3;
                    double x = Vector3d.Dot(p, f.U), y = Vector3d.Dot(p, f.V), hgt = Vector3d.Dot(p, f.W) - h0;
                    double rr = x * x + y * y; if (rr < 1e-24) continue;
                    double dth = (x * Vector3d.Dot(dp, f.V) - y * Vector3d.Dot(dp, f.U)) / rr;
                    a += GaussW[i] / 2 * (-hgt * dth);
                    lenW += GaussW[i] / 2 * dp.Length / Math.Sqrt(rr);      // мера полоски ширины 1 мм вдоль границы ≈ ds / r
                }
        return a;
    }
    static readonly bool PartitionCurvedOff = Environment.GetEnvironmentVariable("P3D_PARTITIONCURVED") == "0";

    static double PlanarArea(AnalyticFace f, FaceSplit.Region r, Vector3d origin, out double len)
    {
        double a = 0; len = 0;
        foreach (var loop in r.Loops)
            foreach (var d in loop)
                for (int i = 0; i < GaussX.Length; i++)
                {
                    double sNode = (GaussX[i] + 1) / 2;
                    var p = d.At(sNode) - origin;
                    // производная по s — Ричардсон по центральным разностям (h = 1e-3 и 5e-4): погрешность O(h⁴), без потери
                    // точности у деталей далеко от начала (с h = 1e-6 у детали на 3e4 мм — 1.5e-7, измерено)
                    Vector3d D(double h) => (d.At(sNode + h) - d.At(sNode - h)) / (2 * h);
                    var dp = (D(5e-4) * 4 - D(1e-3)) / 3;
                    double x = Vector3d.Dot(p, f.U), y = Vector3d.Dot(p, f.V), dx = Vector3d.Dot(dp, f.U), dy = Vector3d.Dot(dp, f.V);
                    a += GaussW[i] / 2 * 0.5 * (x * dy - y * dx);
                    len += GaussW[i] / 2 * dp.Length;
                }
        return a;
    }
    static readonly double[] GaussX = { -0.9931285991850949, -0.9639719272779138, -0.9122344282513259, -0.8391169718222188, -0.7463319064601508, -0.6360536807265150, -0.5108670019508271, -0.3737060887154195, -0.2277858511416451, -0.0765265211334973,
                                         0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271, 0.6360536807265150, 0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949 };
    static readonly double[] GaussW = { 0.0176140071391521, 0.0406014298003869, 0.0626720483341091, 0.0832767415767048, 0.1019301198172404, 0.1181945319615184, 0.1316886384491766, 0.1420961093183820, 0.1491729864726037, 0.1527533871307258,
                                         0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766, 0.1181945319615184, 0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521 };
    // KRN-02: допуск операции (как в разбиении граней)
    static double TolV => FaceSplit.TolOverride ?? Region2Ops.Eps;
    /// <summary>KRN-02: отказы «сторона грани не определена» (счётчик разбора).</summary>
    public static int SideUndecided;
    internal static readonly bool FaceLossOld = Environment.GetEnvironmentVariable("P3D_FACELOSS") == "0";
    internal static readonly bool NestedOff = Environment.GetEnvironmentVariable("P3D_NESTED") == "0";
    /// <summary>KRN-03, замер: переворот оси сферы по результату сетки / отказ разметки сферы.</summary>
    [ThreadStatic] public static Action<string, Vector3d>? PointDebug;
    [ThreadStatic] public static Action<string>? TangentDebug;
    /// <summary>Разбор: пропускать касания по рёбрам (P3D_GENTANGENT=1) — НЕ ДЛЯ РАБОТЫ (неверные результаты, r45).</summary>
    public static readonly bool AllowTangentAlongEdge = Environment.GetEnvironmentVariable("P3D_GENTANGENT") == "1";

    static readonly bool EmptyOff = Environment.GetEnvironmentVariable("P3D_GENEMPTY") == "0";
    /// <summary>Последний вызов Run в этом потоке доказал пустой результат (null с этим признаком — не отказ).</summary>
    [ThreadStatic] public static bool LastProvedEmpty;
    [ThreadStatic] public static Action<string>? EmptyDebug;

    /// <summary>Подтверждение пустого результата другого маршрута (слои 2.5D) — теми же точками и классификаторами.</summary>
    internal static bool ConfirmEmpty(TSolid a, TSolid b, Bool2 op)
    {
        if (EmptyOff) return false;
        var clA = new Classifier(a); var clB = new Classifier(b);
        return clA.Unsupported == null && clB.Unsupported == null && EmptyConfirmed(a, clA, clB, op);
    }

    static bool EmptyConfirmed(TSolid a, Classifier clA, Classifier clB, Bool2 op)
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
        foreach (var v in a.Vertices) { lo = Vector3d.ComponentMin(lo, v.P); hi = Vector3d.ComponentMax(hi, v.P); }
        foreach (var f in a.Faces)
            if (f.Surface is Surfaces.FacetedSurface fs) foreach (var (p, q, r) in fs.Facets.ReadView) { lo = Vector3d.ComponentMin(lo, Vector3d.ComponentMin(p, Vector3d.ComponentMin(q, r))); hi = Vector3d.ComponentMax(hi, Vector3d.ComponentMax(p, Vector3d.ComponentMax(q, r))); }
        // габарит — и по сетке: у цилиндра вершины рёбер лежат в начале окружностей, грани выходят за них
        foreach (var v in Tess.Tessellator.Tessellate(a).Vertices) { var q = new Vector3d(v.P.X, v.P.Y, v.P.Z); lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q); }
        if (!(lo.X <= hi.X)) { EmptyDebug?.Invoke("no extent of A"); return false; }
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var pt = new Vector3d(lo.X + (hi.X - lo.X) * rnd.NextDouble(), lo.Y + (hi.Y - lo.Y) * rnd.NextDouble(), lo.Z + (hi.Z - lo.Z) * rnd.NextDouble());
            var ca = clA.Classify(pt); if (ca == null) { EmptyDebug?.Invoke($"A unclassified at {pt}"); return false; }
            if (ca != 1) continue;                               // вне A (или на границе) — результату (∩, −) не принадлежит
            var cb = clB.Classify(pt); if (cb == null) { EmptyDebug?.Invoke($"B unclassified at {pt}"); return false; }
            bool inRes = op switch { Bool2.Union => true, Bool2.Intersect => cb == 1, _ => cb == -1 };
            if (inRes) { EmptyDebug?.Invoke($"point {pt} in the result: A {ca} B {cb}"); return false; }
        }
        return true;
    }

    /// <summary>KRN-08 (08.2): происхождение граней последнего результата Run в этом потоке (до доводки маршрутом).</summary>
    [ThreadStatic] public static Topology.Provenance? LastProvenance;
    [ThreadStatic] static Dictionary<TFace, TFace>? _src;             // грань результата → грань операнда
    [ThreadStatic] static List<(TFace Source, TFace Into)>? _merged;   // слившиеся при объединении граней (совпадающие области)
    static readonly bool ProvOff = Environment.GetEnvironmentVariable("P3D_PROVENANCE") == "0";

    public static TSolid? Run(TSolid a, TSolid b, Bool2 op, out string why)
    {
        LastProvedEmpty = false; LastProvenance = null;
        _src = ProvOff ? null : new Dictionary<TFace, TFace>(ReferenceEqualityComparer.Instance); _merged = new();
        var keptCount = new Dictionary<TFace, int>(ReferenceEqualityComparer.Instance);
        var cutFace = new HashSet<TFace>(ReferenceEqualityComparer.Instance);
        var boundaryOf = new Dictionary<TFace, TFace>(ReferenceEqualityComparer.Instance);   // область на совпадающей грани другого
        // Операнд, не приведённый к соглашению о полурёбрах (копия TransformOp.Copy, сырой резец), — по нормализованной
        // копии: у копии флаги обращения сброшены, и обе окружности стенки цилиндра шли в одну сторону.
        static TSolid Conventional(TSolid t)
        {
            if (t.TopologyConvention >= TopologyConventions.Version) return t;
            var c = TopologyClone.Deep(t); TopologyConventions.Normalize(c); return c;
        }
        var a0 = a; var b0 = b;
        a = Conventional(a); b = Conventional(b);
        // копия ради соглашения — те же грани в том же порядке: происхождение — на грани исходных операндов
        var orig = new Dictionary<TFace, TFace>(ReferenceEqualityComparer.Instance);
        foreach (var (x, x0) in new[] { (a, a0), (b, b0) })
            if (!ReferenceEquals(x, x0))
            {
                var fx = x.Faces.ToList(); var fx0 = x0.Faces.ToList();
                if (fx.Count == fx0.Count) for (int i = 0; i < fx.Count; i++) orig[fx[i]] = fx0[i];
            }
        TFace Orig(TFace f) => orig.TryGetValue(f, out var o) ? o : f;
        var fa = new List<AnalyticFace>(); var fb = new List<AnalyticFace>();
        foreach (var f in a.Faces) { var x = AnalyticFace.Of(f, out var w); if (x == null) { why = "first operand: " + w; return null; } fa.Add(x); }
        foreach (var f in b.Faces) { var x = AnalyticFace.Of(f, out var w); if (x == null) { why = "second operand: " + w; return null; } fb.Add(x); }

        // 1. Куски пересечения пар граней.
        var onA = fa.ToDictionary(x => x, _ => new List<FaceIntersect.Piece>());
        var onB = fb.ToDictionary(x => x, _ => new List<FaceIntersect.Piece>());
        var coincident = new List<(AnalyticFace X, AnalyticFace Y)>();
        var swPairs = System.Diagnostics.Stopwatch.StartNew();
        // KRN-10 (10.1): габариты граней — один раз; пара с разошедшимися габаритами не идёт в FaceIntersect.Of (там габарит
        // каждой грани считался заново для каждой пары — гранением рёбер). Порядок пар прежний. Мутация P3D_PAIRBOX=0.
        var boxA = PairBoxOff ? null : fa.Select(FaceIntersect.PairBox).ToList();
        var boxB = PairBoxOff ? null : fb.Select(FaceIntersect.PairBox).ToList();
        for (int ia = 0; ia < fa.Count; ia++)
            for (int ib = 0; ib < fb.Count; ib++)
            {
                var x = fa[ia]; var y = fb[ib];
                Diagnostics.Prof.Count("общая: пар граней", 1);
                if (boxA != null && !FaceIntersect.BoxesMeet(boxA[ia], boxB![ib])) { Diagnostics.Prof.Count("общая: пар отброшено габаритом", 1); continue; }
                var r = FaceIntersect.Of(x, y);
                if (r.Kind == FaceIntersect.Kind.Coincident) { coincident.Add((x, y)); continue; }
                if (r.Kind == FaceIntersect.Kind.Unresolved) { why = "face intersection: " + r.Unresolved; return null; }
                // КАСАНИЕ (резец скругления касается граней тела вдоль своих же рёбер — граница его плоских граней, а они
                // совпадают с гранями тела): новых кусков не даёт — нужные разрезы приходят от совпадающих поверхностей.
                // Внутреннее касание без разреза ловят проверки маршрута (годность B-rep, согласие с CSG).
                if (r.Kind == FaceIntersect.Kind.Tangent)
                {
                    // Безопасно, только если касание идёт по ребру границы (резец скругления — по своему ребру). Касание
                    // ВНУТРИ обеих граней требует разреза, которого нет: скругление всех рёбер принималось с ошибкой 14 мм³.
                    // ПОКА — ОТКАЗ. Пропуск касаний по рёбрам (TangentOnBoundary) давал на корпусе неверные результаты, которые
                    // проходили проверки маршрута: скругление всех рёбер — объём с ошибкой 14 мм³ (2.9e-4), сетка — 20
                    // замечаний (K09-FILLET-MATRIX, K09-PRISM-ROUND-ALL-EDGES-VALID). Причина не в касании на рёбрах (проверка
                    // его подтверждала) — разобрать; до тех пор касание — многогранным путём.
                    bool onEdge = TangentOnBoundary(x, y);
                    // КАСАНИЕ ВДОЛЬ ОБРАЗУЮЩЕЙ — НАСТОЯЩАЯ ГРАНИЦА (P3D_GENTANGENT=1): цилиндр резца скругления касается грани
                    // тела по прямой; часть грани от прямой до кромки — внутри резца, за прямой — остаётся. Линия касания
                    // — кусок разреза в обе грани (на границе грани — отбрасывается, как любой кусок на границе).
                    // касание в ТОЧКЕ ничего не делит: сфера × плоскость, сфера × сфера; плоскость ⟂ оси конуса через его
                    // вершину (точка — вершина)
                    if (AllowTangentAlongEdge && (x.Sph || y.Sph) && !(x.Cyl || y.Cyl)) continue;
                    if (AllowTangentAlongEdge && (x.Cone ^ y.Cone) && !(x.Cone ? y : x).Cyl && !(x.Cone ? y : x).Sph)
                    {
                        var kn = x.Cone ? x : y; var pl = x.Cone ? y : x;
                        if (Math.Abs(Math.Abs(Vector3d.Dot(kn.W, pl.W)) - 1) < 1e-9 && Math.Abs(Vector3d.Dot(kn.O - pl.O, pl.W)) < 1e-7) continue;
                    }
                    if (AllowTangentAlongEdge && TangentLine(x, y) is { } tl)
                    {
                        foreach (var p in FaceIntersect.ClipCurve(tl, x, y))
                        {
                            if (!OnBoundary(p, x)) onA[x].Add(p);
                            if (!OnBoundary(p, y)) onB[y].Add(p);
                        }
                        continue;
                    }
                    string Kind(AnalyticFace q) => q.Sph ? "sphere" : q.Cone ? "cone" : q.Cyl ? "cylinder" : "plane";
                    why = onEdge ? "tangent surfaces along an edge (not yet supported)" : $"tangent contact inside both faces (not supported): {Kind(x)} × {Kind(y)}";
                    return null;
                }
                // Кусок, лежащий НА ГРАНИЦЕ грани (соединение «на ус»: пересечение цилиндров — в плоскости среза, совпадает с
                // эллипсом-границей), её не делит — для неё отбрасывается (для другой грани может быть настоящим разрезом).
                foreach (var p in r.Pieces)
                {
                    if (!OnBoundary(p, x)) onA[x].Add(p);
                    if (!OnBoundary(p, y)) onB[y].Add(p);
                }
            }
        var extraCuts = new List<Vector3d>();
        // СОВПАДАЮЩИЕ ПОВЕРХНОСТИ: куски рёбер границы одной грани внутри другой — куски разреза на другой (и наоборот);
        // параметры концов — той же функцией, что точки разреза (ребро — общее с границей грани-источника); точки
        // пересечения границ — в общие точки разреза обоих тел.
        foreach (var (x, y) in coincident)
        {
            var probs = new List<string>();
            foreach (var p in BoundaryInside(y, x, extraCuts, probs)) onA[x].Add(p);
            foreach (var p in BoundaryInside(x, y, extraCuts, probs)) onB[y].Add(p);
            if (probs.Count > 0) { why = "coincident faces: " + probs[0]; return null; }
        }
        // KRN-05/g.14: БЕЗ ПЕРЕСЕЧЕНИЯ (вложенные или раздельные тела) — тот же отбор целых граней по классификаторам: грани,
        // оставшиеся от двух тел, собираются в одну оболочку, а инвариант оболочек (ShellComponents) делит её на наружную
        // оболочку и полость или на два тела. Прежде — отказ маршрута и многогранный путь (полость-шар — фасетами).
        // Мутация P3D_NESTED=0 — прежний отказ.
        if (onA.Values.All(l => l.Count == 0) && coincident.Count == 0 && NestedOff) { why = "no intersection (disjoint or nested bodies — not handled by the general boolean)"; return null; }

        Diagnostics.Prof.Add("общая: пересечение пар граней", swPairs.Elapsed.Ticks);
        if (PointDebug != null)
        {
            foreach (var (fx, l) in onA) foreach (var p in l) { PointDebug($"A-piece {fx.Face.Tag} {p.Curve.GetType().Name} P0", p.P0); PointDebug($"A-piece {fx.Face.Tag} P1", p.P1); }
            foreach (var (fy, l) in onB) foreach (var p in l) { PointDebug($"B-piece {fy.Face.Tag} {p.Curve.GetType().Name} P0", p.P0); PointDebug($"B-piece {fy.Face.Tag} P1", p.P1); }
            foreach (var x in extraCuts) PointDebug("extra cut", x);
        }
        // 2. Разбиение граней; точки разреза — общие на тело.
        var cutsA = onA.Values.SelectMany(l => l).Where(p => !p.Closed).SelectMany(p => new[] { p.P0, p.P1 }).Concat(extraCuts).ToList();
        var cutsB = onB.Values.SelectMany(l => l).Where(p => !p.Closed).SelectMany(p => new[] { p.P0, p.P1 }).Concat(extraCuts).ToList();
        var clA = new Classifier(a); var clB = new Classifier(b);
        if (clA.Unsupported != null || clB.Unsupported != null) { why = "classifier: " + (clA.Unsupported ?? clB.Unsupported); return null; }

        var kept = new List<(AnalyticFace F, FaceSplit.Region R, bool Flip)>();
        bool Select(List<AnalyticFace> faces, Dictionary<AnalyticFace, List<FaceIntersect.Piece>> on, List<Vector3d> cuts, Classifier other, bool isA, out string w)
        {
            foreach (var f in faces)
            {
                // лента катящегося шара (пассивная грань) — целиком одна область, с теми же рёбрами
                List<FaceSplit.Region>? regs; string? sw;
                if (f.Band) { regs = BandRegion(f); sw = null; } else regs = FaceSplit.Split(f, on[f], cuts, out sw);
                if (regs == null) { w = $"face split: {sw}"; return false; }
                // ЦЕЛАЯ ЗАМКНУТАЯ ПОВЕРХНОСТЬ без кусков разреза (полость-шар у тела, вложенного в другое): областей разбиение
                // не строит (контуров нет) — грань целиком одна область. Прежде грань молча терялась: шар-полость исчезал
                // из результата следующей булевой (две полости в кубе — объём на 905 мм³ больше, принято). Иная грань без
                // областей — отказ маршрута, а не потеря грани.
                if (regs.Count == 0)
                {
                    if (f.FullSurface && on[f].Count == 0 && f.Tor)
                    {
                        var whole = new FaceSplit.Region { Inside = f.TorusPoint(0.37, 0.41) };
                        whole.InsideCandidates.Add(f.TorusPoint(2.1, -1.3)); whole.InsideCandidates.Add(f.TorusPoint(-2.6, 2.7));
                        regs = new List<FaceSplit.Region> { whole };
                    }
                    else if (f.FullSurface && on[f].Count == 0)
                    {
                        var whole = new FaceSplit.Region { Inside = f.O + f.W * f.R };
                        whole.InsideCandidates.Add(f.O - f.W * f.R);
                        var e1 = Vector3d.Cross(f.W, Math.Abs(f.W.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
                        whole.InsideCandidates.Add(f.O + e1 * f.R);
                        regs = new List<FaceSplit.Region> { whole };
                    }
                    else if (!FaceLossOld) { w = "face split: no region for a face (the face would be lost)"; return false; }
                }
                // KRN-05 (05.5): РАЗБИЕНИЕ ГРАНИ ПРОВЕРЯЕТСЯ — области покрывают грань без потерь и наложений: сумма их
                // площадей (в той же мере развёртки) равна площади грани без разреза. Прежде не проверялось: потерянная или
                // задвоенная область обнаруживалась (если обнаруживалась) лишь сборкой тела, без названной причины.
                // Мутация P3D_PARTITIONCHECK=0; внесение потери для проверки — DropRegionForTest.
                if ((DropRegionForTest || (DropCurvedRegionForTest && (f.Cyl || f.Cone))) && regs.Count > 1) { DropRegionForTest = DropCurvedRegionForTest = false; regs.Remove(regs.OrderByDescending(x => Math.Abs(x.Area)).First()); }
                bool curved = f.Cyl || f.Cone || f.Sph || f.Tor;
                bool measurable = (!curved && !f.FullSurface)
                                  || (curved && !PartitionCurvedOff && !f.FullSurface && f.W.LengthSquared > 0.5 && !regs.Any(r => r.ApexAbove || r.Unbounded));
                // только грани с аналитическими кривыми контуров: мера по процедурной кривой пересечения несёт её
                // погрешность приближения (замер: до 2.6e-4 у цилиндра и тора при верном разбиении)
                measurable &= regs.All(r => r.Loops.All(l => l.All(d => d.Curve is Curves.LineCurve3 or Curves.ArcCurve3 or Curves.EllipseCurve3)));
                if (!PartitionOff && on[f].Count > 0 && regs.Count > 0 && measurable)
                {
                    // плоская грань: площади точно (квадратура Гаусса по кривым областей); по ломаным разметка областей и
                    // целой грани разная — расхождение до 2.6e-4 при верном разбиении (измерено на krn05)
                    var whole = FaceSplit.Split(f, Array.Empty<FaceIntersect.Piece>(), new List<Vector3d>(), out _);
                    if (whole is { Count: 1 })
                    {
                        var org = whole[0].Loops[0][0].At(0);
                        double sum = Math.Abs(regs.Sum(r => Measure(f, r, org, out _))), a0 = Math.Abs(Measure(f, whole[0], org, out double lw));
                        // допуск: точки разреза сводятся в пределах TolV — щель/наложение шириной TolV вдоль всей границы
                        double tolAbs = 2 * TolV * (lw + regs.Sum(r => { Measure(f, r, org, out double l2); return l2; }));
                        if (PartStat) Console.Error.WriteLine($"PARTSTAT {(f.Cyl ? "cyl" : f.Cone ? "cone" : f.Tor ? "tor" : f.Sph ? "sph" : "plane")} regs {regs.Count} rel {(sum - a0) / a0:E2} a0 {a0:G6}");
                        if (Math.Abs(sum - a0) > 1e-7 * Math.Abs(a0) + tolAbs + 1e-12)
                        { w = $"face split: the regions of face {f.Face.Tag} cover {sum:G10} of {a0:G10} (partition check)"; return false; }
                    }
                }
                if (on[f].Count > 0 || regs.Count > 1) cutFace.Add(f.Face);
                foreach (var r in regs)
                {
                    // точка «на границе» без совпадающей грани (касание) — следующий кандидат
                    var c = other.Classify(r.Inside);
                    if (c == 0 && !(isA ? fb : fa).Any(g => g.SurfaceDistance(r.Inside) < 1e-7 && g.Contains(r.Inside) != false && g.Shape is Geometry.Ssi.Plane == f.Shape is Geometry.Ssi.Plane && FaceIntersect.Of(f, g).Kind == FaceIntersect.Kind.Coincident))
                        foreach (var alt in r.InsideCandidates)
                        {
                            var c2 = other.Classify(alt);
                            if (c2 is 1 or -1) { c = c2; r.Inside = alt; break; }
                        }
                    if (c == null) { w = "classification failed"; return false; }
                    bool keep;
                    if (c == 0)
                    {
                        // НА ГРАНИЦЕ ДРУГОГО ТЕЛА (совпадающие грани): по наружным нормалям.
                        var oth = (isA ? fb : fa).FirstOrDefault(g => g.SurfaceDistance(r.Inside) < 1e-7 && g.Contains(r.Inside) != false);
                        if (oth == null) { w = "a region on the other body's boundary without a coincident face"; return false; }
                        bool same = Vector3d.Dot(f.Face.NormalAt(r.Inside), oth.Face.NormalAt(r.Inside)) > 0;
                        boundaryOf[f.Face] = oth.Face;
                        keep = op switch
                        {
                            Bool2.Union => isA && same,          // совпадают — одна копия (A); навстречу — внутренний стык
                            Bool2.Intersect => isA && same,
                            _ => isA && !same,                   // разность: A навстречу резцу — остаётся; вдоль — снята
                        };
                    }
                    else
                    {
                        bool inside = c == 1;
                        keep = op switch
                        {
                            Bool2.Union => !inside,
                            Bool2.Intersect => inside,
                            _ => isA ? !inside : inside,
                        };
                    }
                    if (keep) { kept.Add((f, r, !isA && op == Bool2.Subtract)); keptCount[f.Face] = keptCount.GetValueOrDefault(f.Face) + 1; }
                    FaceSplit.Debug?.Invoke($"region class: {(isA ? "A" : "B")} {f.Face.Tag} {f.Shape?.GetType().Name} class {c} keep {keep} inside {r.Inside}");
                    if (f.Sph) FaceSplit.Debug?.Invoke($"sphere region: unbounded {r.Unbounded} inside {r.Inside} class {c} keep {keep} loops {r.Loops.Count}");
                }
            }
            w = ""; return true;
        }
        if (!Select(fa, onA, cutsA, clB, true, out why)) return null;
        if (!Select(fb, onB, cutsB, clA, false, out why)) return null;
        if (kept.Count == 0)
        {
            // KRN-05 (05.8): ПУСТО ДОКАЗАНО — каждая область каждой грани классифицирована (иначе отказ выше) и ни одна не
            // остаётся (A − A, A ⊂ B для разности, пересечение без общего объёма). Подтверждение: точки в габарите A
            // классификаторами операндов — ни одна не должна принадлежать результату. Прежде — отказ маршрута, и
            // многогранный путь строил щепки по совпадающим граням (тор − тор: «успех» вместо пустого) или шёл минутами.
            // Мутация P3D_GENEMPTY=0.
            why = "the result is empty";
            if (!EmptyOff && EmptyConfirmed(a, clA, clB, op)) LastProvedEmpty = true;
            return null;
        }

        // 3. Сборка: вершины по положению, рёбра по ключу «кривая + диапазон».
        var solid = new TSolid(); var shell = new TShell { Solid = solid }; solid.Shells.Add(shell);
        var verts = new List<TVertex>();
        TVertex Vert(Vector3d p) { foreach (var v in verts) if ((v.P - p).Length < TolV) return v; var nv = new TVertex(p); verts.Add(nv); PointDebug?.Invoke("new vertex", p); return nv; }
        var edges = new Dictionary<(object Curve, long Lo, long Hi), TEdge>();
        (TEdge E, bool Forward) EdgeOf(FaceSplit.Dart d)
        {
            double lo = Math.Min(d.T0, d.T1), hi = Math.Max(d.T0, d.T1);
            var key = ((object)d.Curve, BitConverter.DoubleToInt64Bits(lo), BitConverter.DoubleToInt64Bits(hi));
            bool forward = d.T1 > d.T0;
            if (edges.TryGetValue(key, out var e)) return (e, forward);
            var sub = SubCurve(d.Curve, lo, hi);
            e = new TEdge { V0 = Vert(sub.PointAt(sub.T0)), V1 = Vert(sub.PointAt(sub.T1)), Curve = sub };
            edges[key] = e;
            return (e, forward);
        }
        foreach (var (f, r, flip) in kept)
        {
            if (FaceSplit.Debug != null && f.Cyl)
                foreach (var loop in r.Loops)
                    foreach (var d in loop)
                    {
                        var a0x = d.At(0); var m0x = d.At(0.25);
                        double dth = f.Theta(m0x) - f.Theta(a0x); while (dth > Math.PI) dth -= 2 * Math.PI; while (dth < -Math.PI) dth += 2 * Math.PI;
                        FaceSplit.Debug($"region dart {f.Face.Tag} flip {flip} h {f.H(a0x):0.###} dθ {dth:+0.###;-0.###} T {d.T0:0.###}→{d.T1:0.###} boundary {d.BoundaryEdge != null} piece {d.IsPiece}");
                    }
            var src = GeometryAudit.AuthoritativeSurface(f.Face)!;
            var face = new TFace { Surface = src, Reversed = f.Face.Reversed ^ flip, Tag = f.Face.Tag, InteriorHint = r.Inside };
            // ФИЗИЧЕСКОЕ ПРАВИЛО: наружная нормаль грани результата смотрит наружу из результата. Флаг обращения операнда
            // мог не согласовываться с обходом его контура (резец: контур по часовой относительно нормали — нормализация
            // теряла кольцо, результат — аналитический объём); точка области, сдвинутая вдоль нормали, не должна быть
            // внутри результата (по классификаторам операндов и виду операции). Контуры не трогаются (согласованы рёбрами).
            // KRN-02: шаг пробы — от бюджета (четверть минимального элемента: проба не перепрыгивает стенку тоньше элемента),
            // при неопределённости (точка на границе операнда) шаг делится пополам, но не меньше 4 допусков классификатора;
            // так и не решено — отказ маршрута (раньше флаг молча оставался как есть).
            // KRN-03: ДВУСТОРОННЯЯ ПРОБА. Грань отделяет результат от не-результата: по нормали и против неё ответы различны.
            // Решение — только когда внутри результата ровно одна сторона; иначе — следующая точка области (запасные), затем
            // отказ. Прежде — одна сторона: у маленькой детали в 3e5 мм точка области у чужой границы давала «внутри» и грань
            // стенки отверстия переворачивалась (контуры против нормали — теперь ловит проверка loop-orientation).
            {
                bool decided = false;
                foreach (var pt in new[] { r.Inside }.Concat(r.InsideCandidates))
                {
                    var n = face.NormalAt(pt).Normalized();
                    double floor = 4 * Classifier.Tol;
                    for (double step = ToleranceContext.Current.MinFeature / 4; step >= floor && !decided; step /= 2)
                    {
                        bool? In(Vector3d q)
                        {
                            int? qa = clA.Classify(q), qb = clB.Classify(q);
                            if (qa is not (1 or -1) || qb is not (1 or -1)) return null;
                            return op switch { Bool2.Union => qa == 1 || qb == 1, Bool2.Intersect => qa == 1 && qb == 1, _ => qa == 1 && qb != 1 };
                        }
                        var plus = In(pt + n * step); var minus = In(pt - n * step);
                        if (FaceSplit.Debug != null && (f.Cyl || f.Tor)) FaceSplit.Debug($"side probe {f.Face.Tag} flip {flip} rev0 {face.Reversed} at {pt:0.###} n {n:0.###} step {step:G3} plus {plus} minus {minus}");
                        if (plus == null || minus == null || plus == minus) continue;
                        if (plus == true) face.Reversed = !face.Reversed;   // по нормали — внутри результата: нормаль внутрь, развернуть
                        decided = true;
                    }
                    if (decided) break;
                }
                if (!decided) { SideUndecided++; why = "outward side undecided"; return null; }
            }
            // СФЕРА — КАРТА ОПЕРАНДА, ПОКА ОНА ВЫПОЛНЯЕТ КОНТРАКТ ОБЛАСТИ (KRN-03, SurfaceDomain): булева не подбирает
            // параметризацию под разметчик. Карта меняется (по правилу SurfaceDomain.SphereAxis) только если полюс карты
            // операнда лежит на ребре грани результата — тогда p-кривая ребра разрывна (замер: грани бруска через центр шара
            // — меридианы через полюс). Проверка — после сборки контуров (нужны рёбра грани). P3D_SPHAXIS=rule — всегда по
            // правилу (прежнее поведение), keep — карта операнда без проверки (мутация: ловит pole-on-boundary).
            Vector3d sphOutward = default; bool sphOwn = false;
            if (f.Sph)
            {
                sphOutward = face.NormalAt(r.Inside);
                if (SphereChartMode != "rule" && GeometryAudit.AuthoritativeSurface(f.Face) is RevolvedSurface own) { face.Surface = own; sphOwn = true; }
                else face.Surface = RuleChart();
                face.Reversed = false; face.Reversed = Vector3d.Dot(face.NormalAt(r.Inside), sphOutward) < 0;
            }
            RevolvedSurface RuleChart()
            {
                var rim = r.Loops.Count == 1 && r.Loops[0].Count == 1 ? r.Loops[0][0].Curve as ArcCurve3 : null;
                return SurfaceDomain.SphereChart(f.O, f.R, rim == null && CapAxis() is { } ca ? ca : SurfaceDomain.SphereAxis(rim, r.Inside, f.W));
            }
            // g.05: грань с одним контуром не по окружности (кривая пересечения) — ось карты к центру «шапки»: векторная площадь
            // контура (материал слева) по стороне нормали грани. Полюс — в глубине грани, противоположный — вне её.
            Vector3d? CapAxis()
            {
                if (BothPolesOff || face.Loops.Count != 1) return null;
                var la = Vector3d.Zero; var pts = new List<Vector3d>();
                foreach (var ce in face.Loops[0].CoEdges) for (int i = 0; i < 64; i++) pts.Add(Topology.PCurves.At(ce, i / 64.0));
                for (int i = 0; i < pts.Count; i++) la += Vector3d.Cross(pts[i] - f.O, pts[(i + 1) % pts.Count] - f.O) / 2;
                if (la.Length < 1e-9 * f.R * f.R) return null;
                double nside = Vector3d.Dot(face.NormalAt(r.Inside), r.Inside - f.O) >= 0 ? 1 : -1;
                return la.Normalized() * nside;
            }
            bool firstLoop = true;
            foreach (var loop in r.Loops)
            {
                var tl = face.AddLoop(firstLoop); firstLoop = false;
                var seq = flip ? loop.AsEnumerable().Reverse().ToList() : loop;
                foreach (var d in seq)
                {
                    var (e, fwd) = EdgeOf(d);
                    tl.Add(e, flip ? fwd : !fwd);
                }
            }
            if (sphOwn && SphereChartMode != "keep" && (SurfaceDomain.PoleClearance(face) < ToleranceContext.Current.MinFeature
                                                       || !BothPolesOff && SurfaceDomain.PolesInside(face, f.O) == 2))
            {
                face.Surface = RuleChart(); SphereChartsChanged++;
                face.Reversed = false; face.Reversed = Vector3d.Dot(face.NormalAt(r.Inside), sphOutward) < 0;
            }
            shell.Add(face);
            if (_src != null) _src[face] = f.Face;
            if (FaceSplit.Debug != null && f.Cyl)
                foreach (var l in face.Loops)
                    foreach (var ce in l.CoEdges)
                    {
                        var a0x = Topology.PCurves.At(ce, 0); var m0x = Topology.PCurves.At(ce, 0.25);
                        double th(Vector3d p) => f.Theta(p);
                        double dth = th(m0x) - th(a0x); while (dth > Math.PI) dth -= 2 * Math.PI; while (dth < -Math.PI) dth += 2 * Math.PI;
                        FaceSplit.Debug($"assembled {face.Tag} flip {flip} rev {face.Reversed} h {f.H(a0x):0.###} dθ {dth:+0.###;-0.###} ceRev {ce.Reversed} edgeClosed {ReferenceEquals(ce.Edge.V0, ce.Edge.V1)}");
                    }
        }
        Sew(shell);
        if (!LoopMergeOff) MergeOpenLoops(shell);
        // KRN-03: СФЕРИЧЕСКИЕ ГРАНИ БОЛЬШЕ НЕ ПРОВЕРЯЮТСЯ И НЕ ПЕРЕСТРАИВАЮТСЯ ПО СЕТКЕ. Прежде шапка с одной окружностью
        // размечалась к «первому» полюсу оси, и булева переворачивала ось поверхности, пока сетка не ляжет на сторону
        // области (8 переворотов из 14 сцен, 2 отказа). Теперь разметчик выбирает полюс по обходу края (материал слева),
        // и топология не зависит от разметчика.
        UnifyFaces(shell);
        PrismBuild.MergeCollinearEdges(shell);
        if (_src != null)
        {
            // ПРОИСХОЖДЕНИЕ: грань операнда с одной оставшейся областью без разреза — сохранена; с разрезом — изменена;
            // несколько областей (несколько граней результата) — разделена; ни одной — удалена, если её область не легла на
            // совпадающую грань другого операнда, оставшуюся в результате (тогда — слилась)
            var pv = new Topology.Provenance { Operation = "boolean." + op };
            var bySource = shell.Faces.Where(x => _src.ContainsKey(x)).GroupBy(x => _src[x], ReferenceEqualityComparer.Instance).ToDictionary(g => (TFace)g.Key!, g => g.ToList(), ReferenceEqualityComparer.Instance);
            foreach (var rf in shell.Faces)
            {
                if (!_src.TryGetValue(rf, out var sf)) { pv.Faces.Add(new Topology.FaceProvenance(rf, null, Topology.FaceOrigin.Generated, "no source recorded")); continue; }
                var kind = bySource[sf].Count > 1 ? Topology.FaceOrigin.Split
                         : cutFace.Contains(sf) ? Topology.FaceOrigin.Modified : Topology.FaceOrigin.Kept;
                pv.Faces.Add(new Topology.FaceProvenance(rf, Orig(sf), kind));
            }
            foreach (var sf in a.Faces.Concat(b.Faces))
            {
                if (bySource.ContainsKey(sf)) continue;
                var into = _merged!.FirstOrDefault(m => ReferenceEquals(m.Source, sf)).Into
                           ?? (boundaryOf.TryGetValue(sf, out var oth) && bySource.TryGetValue(oth, out var ofaces) ? ofaces[0] : null);
                if (into != null) pv.Merged.Add((Orig(sf), into)); else pv.Deleted.Add(Orig(sf));
            }
            LastProvenance = pv;
        }
        why = "";
        return solid;
    }

    /// <summary>
    /// СЛИЯНИЕ ГРАНЕЙ НА ОДНОЙ ПОВЕРХНОСТИ (в духе UnifySameDomain): грани с общим ребром на одной поверхности (та же
    /// плоскость или тот же цилиндр) с одной стороной (наружные нормали совпадают) — в группу; общие рёбра удаляются,
    /// оставшиеся полурёбра собираются в контуры по вершинам; у плоской грани контур с площадью > 0 относительно наружной
    /// нормали — наружный. Неоднозначная вершина (касание контуров) — группа не сливается.
    /// </summary>
    static void UnifyFaces(TShell shell)
    {
        for (bool again = true; again;)
        {
            again = false;
            var faces = shell.Faces.ToList();
            var shape = faces.ToDictionary(f => f, f => Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f)), (IEqualityComparer<TFace>)ReferenceEqualityComparer.Instance);
            var byEdge = new Dictionary<TEdge, List<TFace>>(ReferenceEqualityComparer.Instance);
            foreach (var f in faces) foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) { if (!byEdge.TryGetValue(ce.Edge, out var l)) byEdge[ce.Edge] = l = new(); l.Add(f); }
            foreach (var (e, fs) in byEdge)
            {
                if (fs.Count != 2 || ReferenceEquals(fs[0], fs[1])) continue;
                var f1 = fs[0]; var f2 = fs[1];
                if (!SameSurface(shape[f1], shape[f2])) continue;
                var m = e.Curve.PointAt((e.Curve.T0 + e.Curve.T1) / 2);
                if (Vector3d.Dot(f1.NormalAt(m), f2.NormalAt(m)) <= 0) continue;
                if (Merge(shell, f1, f2)) { again = true; break; }
            }
        }
    }

    internal static bool SameSurface(Geometry.Ssi.Shape? a, Geometry.Ssi.Shape? b)
    {
        switch (a, b)
        {
            case (Geometry.Ssi.Plane p, Geometry.Ssi.Plane q):
                {
                    var n = p.N.Normalized(); var m = q.N.Normalized();
                    return Math.Abs(Math.Abs(Vector3d.Dot(n, m)) - 1) < 1e-12 && Math.Abs(Vector3d.Dot(q.P - p.P, n)) < 1e-9;
                }
            case (Geometry.Ssi.Cylinder c, Geometry.Ssi.Cylinder d):
                {
                    var u = c.Axis.Normalized(); var v = d.Axis.Normalized();
                    if (Math.Abs(Math.Abs(Vector3d.Dot(u, v)) - 1) > 1e-12 || Math.Abs(c.R - d.R) > 1e-9) return false;
                    var w = d.P - c.P; w -= u * Vector3d.Dot(w, u);
                    return w.Length < 1e-9;
                }
        }
        return false;
    }

    static readonly bool MergeProofOff = Environment.GetEnvironmentVariable("P3D_MERGEPROOF") == "0";
    /// <summary>Проверка KRN05: выбросить последний контур при следующем слиянии (один раз).</summary>
    [ThreadStatic] public static bool CorruptMergeForTest;
    /// <summary>Разбор: сколько слияний отвергнуто доказательством площади.</summary>
    public static int MergesRefused;

    /// <summary>Вклад ребра (по ходу полуребра) в знаковую площадь плоского контура: ½∫((p − o) × p′)·n.</summary>
    static double EdgeArea(TEdge e, bool reversed, Vector3d n, Vector3d o)
    {
        var c = e.Curve; double t0 = c.T0, t1 = c.T1, a = 0;
        for (int i = 0; i < GaussX.Length; i++)
        {
            double t = t0 + (t1 - t0) * (GaussX[i] + 1) / 2;
            double span = t1 - t0;
            Vector3d D(double h) => (c.PointAt(t + h * span) - c.PointAt(t - h * span)) / (2 * h * span);
            var dp = (D(5e-4) * 4 - D(1e-3)) / 3;
            a += GaussW[i] / 2 * span * 0.5 * Vector3d.Dot(Vector3d.Cross(c.PointAt(t) - o, dp), n);
        }
        return reversed ? -a : a;
    }

    static bool Merge(TShell shell, TFace f1, TFace f2)
    {
        var members = new[] { f1, f2 };
        var cnt = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in members.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges))) cnt[ce.Edge] = cnt.GetValueOrDefault(ce.Edge) + 1;
        var rest = members.SelectMany(f => f.Loops.SelectMany(l => l.CoEdges)).Where(ce => cnt[ce.Edge] == 1)
                          .Select(ce => (ce.Edge, ce.Reversed, Start: ce.Start, End: ce.End)).ToList();
        // неоднозначная вершина — не сливать
        var starts = rest.GroupBy(x => x.Start, ReferenceEqualityComparer.Instance);
        if (starts.Any(g => g.Count() > 1)) return false;
        var used = new bool[rest.Count]; var loops = new List<List<(TEdge, bool)>>();
        for (int i = 0; i < rest.Count; i++)
        {
            if (used[i]) continue;
            var loop = new List<(TEdge, bool)>(); int cur = i; var origin = rest[i].Start;
            for (int guard = 0; guard <= rest.Count; guard++)
            {
                used[cur] = true; loop.Add((rest[cur].Edge, rest[cur].Reversed));
                if (ReferenceEquals(rest[cur].End, origin)) break;
                int nx = rest.FindIndex(x => ReferenceEquals(x.Start, rest[cur].End));
                if (nx < 0 || used[nx]) return false;
                cur = nx;
            }
            loops.Add(loop);
        }
        if (loops.Count == 0) return false;
        if (CorruptMergeForTest && loops.Count > 1) { CorruptMergeForTest = false; loops.RemoveAt(loops.Count - 1); }
        // KRN-05 (05.7): СЛИЯНИЕ — ТОЛЬКО ПРИ ДОКАЗАННОЙ ЭКВИВАЛЕНТНОСТИ ОБЛАСТИ. У плоских граней точная знаковая площадь
        // контуров слитой грани (квадратура по кривым рёбер) равна сумме площадей двух граней: общие рёбра взаимно
        // уничтожаются, потерянный или лишний контур — нет. Иначе слияние не делается (грани остаются раздельными, тело
        // годно). Мутация P3D_MERGEPROOF=0.
        if (!MergeProofOff && Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f1)) is Geometry.Ssi.Plane plProof)
        {
            var nrm = plProof.N.Normalized(); var org = f1.Loops[0].CoEdges[0].Start.P;
            double A(IEnumerable<(TEdge E, bool R)> es) => es.Sum(x => EdgeArea(x.E, x.R, nrm, org));
            double a1 = f1.Loops.Sum(l => A(l.CoEdges.Select(ce => (ce.Edge, ce.Reversed))));
            double a2 = f2.Loops.Sum(l => A(l.CoEdges.Select(ce => (ce.Edge, ce.Reversed))));
            double am = loops.Sum(l => A(l));
            if (Math.Abs(am - (a1 + a2)) > 1e-9 * (Math.Abs(a1) + Math.Abs(a2)) + 1e-12) { MergesRefused++; return false; }
        }
        var nf = new TFace { Surface = f1.Surface, Reversed = f1.Reversed, Tag = f1.Tag, MaterialSlot = f1.MaterialSlot };
        // Наружный контур плоской грани — с НАИБОЛЬШЕЙ по модулю площадью (не по знаку относительно нормали: у обращённой
        // грани резца знак уходил не туда — единственный контур помечался отверстием, «ни одно кольцо не ограничивает
        // область», и результат терял аналитический объём); единственный контур — наружный; у цилиндров — все (пояса).
        var shp = Geometry.ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f1));
        var areas = loops.Select(l =>
        {
            if (shp is not Geometry.Ssi.Plane pl) return 0.0;
            var pts = new List<Vector3d>();
            foreach (var (e, r) in l) { var q = new List<Vector3d>(); e.Curve.Tessellate(q, true); if (r) q.Reverse(); pts.AddRange(q.Skip(pts.Count == 0 ? 0 : 1)); }
            var nrm = Vector3d.Zero; for (int i = 0; i < pts.Count; i++) nrm += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
            return Math.Abs(Vector3d.Dot(nrm, pl.N.Normalized())) / 2;
        }).ToList();
        int outerIdx = shp is Geometry.Ssi.Plane ? areas.IndexOf(areas.Max()) : -1;
        for (int li = 0; li < loops.Count; li++)
        {
            bool outer = outerIdx < 0 || li == outerIdx;
            var tl = nf.AddLoop(outer); foreach (var (e, r) in loops[li]) tl.Add(e, r);
        }
        foreach (var m in members)
        {
            foreach (var ce in m.Loops.SelectMany(l => l.CoEdges)) ce.Edge.CoEdges.Remove(ce);
            int at = shell.Faces.IndexOf(m); if (at >= 0) shell.Faces.RemoveAt(at);
        }
        shell.Add(nf);
        if (_src != null && _src.TryGetValue(f1, out var s1))
        {
            _src[nf] = s1;
            if (_src.TryGetValue(f2, out var s2) && !ReferenceEquals(s1, s2)) _merged?.Add((s2, nf));
            _src.Remove(f1); _src.Remove(f2);
        }
        return true;
    }

    /// <summary>
    /// СШИВКА: у совпадающих граней границы ложатся друг на друга — ребро A и ребро B разные объекты с одной геометрией,
    /// каждое у одной грани («ребро на краю»). Открытое ребро, внутри которого лежит вершина другого открытого ребра,
    /// режется в ней; два открытых ребра с одними концами и совпадающей серединой — сливаются в одно.
    /// </summary>
    static readonly bool LoopMergeOff = Environment.GetEnvironmentVariable("P3D_LOOPMERGE") == "0";
    /// <summary>Разбор: сколько разомкнутых контуров слито в кольца.</summary>
    public static int OpenLoopsMerged;

    /// <summary>
    /// g.04: РАЗОМКНУТЫЕ КОНТУРЫ — В КОЛЬЦА. Разбиение грани иногда отдаёт кольцо из двух дуг двумя «контурами» по одной
    /// дуге (концы совпадают после сшивки). Такая грань проходила проверку (контур из одного полуребра не проверялся на
    /// замкнутость), а интеграл по контуру замыкал каждый кусок хордой: пересечение конуса и шара — 1.3395 вместо 1.3333 мм³
    /// при заявленной границе 1.8e-4 (разностное сравнение). Контуры одной грани, чьи концы совпадают, сцепляются.
    /// Мутация P3D_LOOPMERGE=0.
    /// </summary>
    static void MergeOpenLoops(TShell shell)
    {
        static TVertex Start(TCoEdge c) => c.Reversed ? c.Edge.V1 : c.Edge.V0;
        static TVertex End(TCoEdge c) => c.Reversed ? c.Edge.V0 : c.Edge.V1;
        foreach (var f in shell.Faces)
            for (bool again = true; again;)
            {
                again = false;
                var open = f.Loops.Where(l => l.CoEdges.Count > 0 && !ReferenceEquals(End(l.CoEdges[^1]), Start(l.CoEdges[0]))).ToList();
                foreach (var a in open)
                {
                    var b = open.FirstOrDefault(x => !ReferenceEquals(x, a) && ReferenceEquals(End(a.CoEdges[^1]), Start(x.CoEdges[0])));
                    if (b == null) continue;
                    foreach (var ce in b.CoEdges.ToList()) { b.CoEdges.Remove(ce); ce.Loop = a; a.CoEdges.Add(ce); }
                    f.Loops.Remove(b);
                    if (b.IsOuter) a.IsOuter = true;
                    OpenLoopsMerged++; again = true; break;
                }
            }
    }

    static void Sew(TShell shell)
    {
        for (int pass = 0; pass < 1000; pass++)
        {
            var uses = new Dictionary<TEdge, List<(TFace F, int Loop, int Index)>>(ReferenceEqualityComparer.Instance);
            foreach (var f in shell.Faces)
                for (int li = 0; li < f.Loops.Count; li++)
                    for (int k = 0; k < f.Loops[li].CoEdges.Count; k++)
                    {
                        var e = f.Loops[li].CoEdges[k].Edge;
                        if (!uses.TryGetValue(e, out var l)) uses[e] = l = new(); l.Add((f, li, k));
                    }
            var open = uses.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToList();
            if (open.Count == 0) return;
            bool changed = false;
            // 1. разрез открытого ребра в вершине другого открытого ребра, лежащей внутри него
            var vs = open.SelectMany(e => new[] { e.V0, e.V1 }).Distinct(ReferenceEqualityComparer.Instance).Cast<TVertex>().ToList();
            foreach (var e in open)
            {
                foreach (var v in vs)
                {
                    if (ReferenceEquals(v, e.V0) || ReferenceEquals(v, e.V1)) continue;
                    double t = FaceSplit.ClosestParam(e.Curve, v.P);
                    if ((e.Curve.PointAt(t) - v.P).Length > TolV || t <= e.Curve.T0 + 1e-12 || t >= e.Curve.T1 - 1e-12) continue;
                    var e1 = new TEdge { V0 = e.V0, V1 = v, Curve = SubCurve(e.Curve, e.Curve.T0, t) };
                    var e2 = new TEdge { V0 = v, V1 = e.V1, Curve = SubCurve(e.Curve, t, e.Curve.T1) };
                    var (f, li, k) = uses[e][0];
                    ReplaceInLoop(f, li, k, rev => rev ? new[] { (e2, true), (e1, true) } : new[] { (e1, false), (e2, false) });
                    changed = true; break;
                }
                if (changed) break;
            }
            if (changed) continue;
            // 2. слияние пар открытых рёбер с одними концами и совпадающей серединой
            for (int i = 0; i < open.Count && !changed; i++)
                for (int j = i + 1; j < open.Count && !changed; j++)
                {
                    var a = open[i]; var b = open[j];
                    bool same = ReferenceEquals(a.V0, b.V0) && ReferenceEquals(a.V1, b.V1);
                    bool opp = ReferenceEquals(a.V0, b.V1) && ReferenceEquals(a.V1, b.V0);
                    if (!same && !opp) continue;
                    var ma = a.Curve.PointAt((a.Curve.T0 + a.Curve.T1) / 2); var mb = b.Curve.PointAt((b.Curve.T0 + b.Curve.T1) / 2);
                    if ((ma - mb).Length > 1e-6) continue;
                    var (f, li, k) = uses[b][0];
                    ReplaceInLoop(f, li, k, rev => new[] { (a, same ? rev : !rev) });
                    changed = true;
                }
            if (!changed) return;
        }
    }

    /// <summary>Заменить полуребро k контура li грани f последовательностью (ребро, обращено) — по флагу прежнего.</summary>
    static void ReplaceInLoop(TFace f, int li, int k, Func<bool, (TEdge E, bool Rev)[]> by)
    {
        var loop = f.Loops[li]; var ces = loop.CoEdges.ToList();
        var list = new List<(TEdge, bool)>();
        for (int i = 0; i < ces.Count; i++)
        {
            if (i == k) list.AddRange(by(ces[i].Reversed).Select(x => (x.E, x.Rev)));
            else list.Add((ces[i].Edge, ces[i].Reversed));
        }
        bool outer = loop.IsOuter;
        foreach (var ce in ces) ce.Edge.CoEdges.Remove(ce);
        f.Loops.RemoveAt(li);
        var nl = new TLoop { IsOuter = outer, Face = f };
        foreach (var (e, r) in list) nl.Add(e, r);
        f.Loops.Insert(li, nl);
    }

    /// <summary>
    /// Куски рёбер границы грани src (без швов), лежащие внутри грани dst (на той же поверхности): по знаковому расстоянию
    /// до границы dst вдоль ребра; концы на границе dst — параметр той же функцией, что у точек разреза (FaceSplit.
    /// ClosestParam), и в общие точки разреза. Куски, лежащие на границе dst (общие отрезки границ), пропускаются.
    /// </summary>
    /// <summary>KRN-03: точка разреза и у конца ребра, лежащего на границе приёмника (P3D_BNDCUT=0 — прежнее, для мутации).</summary>
    /// <summary>KRN-03: карта сферической грани результата — contract (по умолчанию: карта операнда, другая — только при нарушении
    /// контракта SurfaceDomain), rule (всегда по правилу, прежнее), keep (карта операнда без проверки — мутация).</summary>
    internal static readonly string SphereChartMode = Environment.GetEnvironmentVariable("P3D_SPHAXIS") ?? "contract";
    /// <summary>KRN-03 замер: сколько сферических граней получили другую карту из-за полюса на ребре.</summary>
    public static int SphereChartsChanged;
    /// <summary>g.05: карта сферы меняется и при обоих полюсах внутри грани (P3D_SPHBOTHPOLES=0 — прежнее).</summary>
    internal static readonly bool BothPolesOff = Environment.GetEnvironmentVariable("P3D_SPHBOTHPOLES") == "0";
    internal static readonly bool BoundaryEndCuts = Environment.GetEnvironmentVariable("P3D_BNDCUT") != "0";
    static readonly bool BndEventsOff = Environment.GetEnvironmentVariable("P3D_BNDEVENTS") == "0";

    /// <summary>
    /// KRN-04 (g.05): ИНТЕРВАЛЫ РЕБРА ГРАНИ-ИСТОЧНИКА ВНУТРИ ГРАНИ-ПРИЁМНИКА (совпадающие поверхности) — ПО СОБЫТИЯМ: где ребро
    /// пересекает рёбра границы приёмника. Ребро границы g лежит на общей поверхности; вспомогательная поверхность, режущая
    /// общую по носителю g (плоская грань: отрезок — плоскость через него и нормаль, дуга — цилиндр её оси; кривая грань:
    /// окружность — её плоскость, образующая — плоскость через неё и ось), даёт кандидатов SsiEvents; годны лежащие на g.
    /// Интервалы — по трём точкам; разные знаки — отказ. null — событие для g не построить (эллипс на плоскости, кривая
    /// пересечения): тогда прежняя выборка. Прежде — знак в 512 точках ребра: участок короче 1/512 длины ребра пропускался.
    /// Мутация P3D_BNDEVENTS=0.
    /// </summary>
    static List<(double S, double E)>? EdgeRunsByEvents(ICurve3 c, AnalyticFace dst, out string? unresolved)
    {
        unresolved = null;
        double T0 = c.T0, T1 = c.T1, P = T1 - T0;
        var ts = new List<double> { T0, T1 };
        foreach (var g in dst.Face.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge).Distinct())
        {
            if (g.Curve == null) continue;
            var aux = AuxSurface(g.Curve, dst);
            if (aux == null) return null;
            foreach (var te in SsiEvents.CurveSurface(c, aux, out bool coinc))
            {
                var q = c.PointAt(te);
                double tg = FaceSplit.ClosestParam(g.Curve, q);
                if ((g.Curve.PointAt(tg) - q).Length <= 1e-7) ts.Add(te);
            }
            // ребро и g на одном носителе (совпадающий интервал): события — концы g на ребре
            foreach (var q in new[] { g.V0.P, g.V1.P })
            {
                double tc = FaceSplit.ClosestParam(c, q);
                if ((c.PointAt(tc) - q).Length <= 1e-7) ts.Add(tc);
            }
        }
        ts.Sort();
        var ev = new List<double>();
        foreach (var t in ts) if (ev.Count == 0 || t - ev[^1] > 1e-10 * P) ev.Add(t);
        if (ev[^1] < T1) ev[^1] = T1;
        var runs = new List<(double S, double E)>();
        for (int i = 0; i + 1 < ev.Count; i++)
        {
            double s = ev[i], e = ev[i + 1];
            double g1 = dst.SignedBoundaryDistance(c.PointAt(s + (e - s) * 0.25)), g2 = dst.SignedBoundaryDistance(c.PointAt(s + (e - s) * 0.5)), g3 = dst.SignedBoundaryDistance(c.PointAt(s + (e - s) * 0.75));
            bool anyIn = g1 > 0 || g2 > 0 || g3 > 0, anyOut = g1 < 0 || g2 < 0 || g3 < 0;
            if (anyIn && anyOut && e - s > 1e-9 * P) { unresolved = $"edge interval [{s:R}, {e:R}] changes side without an event"; return runs; }
            if (!(g2 > 0)) continue;
            if (runs.Count > 0 && runs[^1].E == s) runs[^1] = (runs[^1].S, e); else runs.Add((s, e));
        }
        return runs;
    }

    /// <summary>Поверхность, которая вместе с поверхностью грани задаёт носитель ребра g (см. EdgeRunsByEvents); null — нет.</summary>
    static Ssi.Shape? AuxSurface(ICurve3 g, AnalyticFace f)
    {
        switch (f.Shape)
        {
            case Ssi.Plane pl:
            {
                var n = pl.N.Normalized();
                if (g is LineCurve3 l) { var d = l.B - l.A; var m = Vector3d.Cross(d, n); return m.LengthSquared > 1e-24 ? new Ssi.Plane(l.A, m.Normalized()) : null; }
                if (g is ArcCurve3 a) return new Ssi.Cylinder(a.Frame.Origin, a.Frame.W.Normalized(), a.Radius);
                return null;
            }
            default:
            {
                if (g is ArcCurve3 a) return new Ssi.Plane(a.Frame.Origin, a.Frame.W.Normalized());
                if (g is EllipseCurve3 el) return new Ssi.Plane(el.Frame.Origin, el.Frame.W.Normalized());
                if (g is LineCurve3 l)
                {
                    Vector3d? axisP = f.Shape switch { Ssi.Cylinder cy => cy.P, Ssi.Cone k => k.Apex, _ => null };
                    Vector3d? axisW = f.Shape switch { Ssi.Cylinder cy => cy.Axis, Ssi.Cone k => k.Axis, _ => null };
                    if (axisP is not { } ap || axisW is not { } aw) return null;
                    var d = l.B - l.A; var m = Vector3d.Cross(d, l.A - ap);
                    if (m.LengthSquared < 1e-20) m = Vector3d.Cross(d, aw);
                    return m.LengthSquared > 1e-24 ? new Ssi.Plane(l.A, m.Normalized()) : null;
                }
                return null;
            }
        }
    }

    static IEnumerable<FaceIntersect.Piece> BoundaryInside(AnalyticFace src, AnalyticFace dst, List<Vector3d> cuts, List<string> problems)
    {
        var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in src.Face.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
        foreach (var e in uses.Where(kv => kv.Value == 1).Select(kv => kv.Key))
        {
            var c = e.Curve; int n = 512;
            var ts = new double[n + 1]; var gs = new double[n + 1];
            for (int i = 0; i <= n; i++) { ts[i] = c.T0 + (c.T1 - c.T0) * i / n; gs[i] = dst.SignedBoundaryDistance(c.PointAt(ts[i])); }
            bool closed = (c.PointAt(c.T0) - c.PointAt(c.T1)).Length < TolV;
            // события (KRN-04); null — выборка ниже
            string? unres = null;
            var evRuns = BndEventsOff ? null : EdgeRunsByEvents(c, dst, out unres);
            if (unres != null) { problems.Add(unres); yield break; }
            if (evRuns != null) BndEventEdges++; else BndSampledEdges++;
            var found = new List<(double T0, double T1)>();
            if (evRuns != null)
                foreach (var (s0, e0) in evRuns)
                    found.Add((s0, e0));
            else
            {
            int iStart = -1;
            for (int i = 0; i <= n; i++)
            {
                bool inside = gs[i] > 0;
                if (inside && iStart < 0) iStart = i;
                if ((!inside || i == n) && iStart >= 0)
                {
                    int iEnd = inside ? n : i - 1;
                    double t0s = iStart == 0 ? c.T0 : CrossParam(c, dst, ts[iStart - 1], ts[iStart]);
                    double t1s = (inside && i == n) ? c.T1 : CrossParam(c, dst, ts[i], ts[iEnd]);
                    iStart = -1;
                    found.Add((t0s, t1s));
                }
            }
            }
            foreach (var (fa, fb) in found)
            {
                {
                    double t0 = fa, t1 = fb;
                    if (t1 - t0 < 1e-12) continue;
                    // точка разреза — p; параметр куска — ClosestParam(c, p): та же функция от той же точки, что в разбиении
                    // грани-источника (ключ ребра совпадает побитово)
                    var p0 = c.PointAt(t0); var p1 = c.PointAt(t1);
                    // KRN-03: ПЕРЕСЕЧЕНИЕ С ГРАНИЦЕЙ У САМОГО КОНЦА РЕБРА — это конец ребра. Прежде параметр уточнялся
                    // ближайшей точкой и выходил ЗА область кривой (−7e-13, 1+8e-13): кусок разреза кончался не в вершине,
                    // точка разреза на ребре приёмника не ставилась, грань кармана не делилась (скругление вогнутого угла).
                    if (BoundaryEndCuts && t0 != c.T0 && (p0 - c.PointAt(c.T0)).Length <= TolV) { t0 = c.T0; p0 = c.PointAt(c.T0); }
                    if (BoundaryEndCuts && t1 != c.T1 && (p1 - c.PointAt(c.T1)).Length <= TolV) { t1 = c.T1; p1 = c.PointAt(c.T1); }
                    if (t0 > c.T0) { cuts.Add(p0); t0 = FaceSplit.ClosestParam(c, p0); }
                    if (t1 < c.T1) { cuts.Add(p1); t1 = FaceSplit.ClosestParam(c, p1); }
                    // KRN-03: КОНЕЦ РЕБРА НА ГРАНИЦЕ ГРАНИ-ПРИЁМНИКА (дуга «сфера × плоскость» кончается на ребре кармана): знак
                    // расстояния у самого конца — шум (±1e-15), и при «внутри» точка разреза не ставилась — ребро приёмника не
                    // делилось, грань не разбивалась и оставалась целиком (скругление вогнутого угла: vertex-pinch).
                    if (BoundaryEndCuts && t0 == c.T0 && Math.Abs(gs[0]) <= TolV) cuts.Add(p0);
                    if (BoundaryEndCuts && t1 == c.T1 && Math.Abs(gs[n]) <= TolV) cuts.Add(p1);
                    PointDebug?.Invoke($"bnd-inside {src.Face.Tag}->{dst.Face.Tag} curve [{c.T0:R}..{c.T1:R}] piece [{t0:R}..{t1:R}] g0 {gs[0]:E2} gn {gs[n]:E2} iStartWas0 {t0 == c.T0}", p0);
                    bool whole = closed && t0 == c.T0 && t1 == c.T1;
                    yield return new FaceIntersect.Piece(c, t0, t1, p0, p1, whole);
                }
            }
        }
    }
    /// <summary>Замер KRN-04: рёбра совпадающих граней, обрезанные по событиям и выборкой.</summary>
    public static long BndEventEdges, BndSampledEdges;

    /// <summary>Параметр пересечения ребра с границей грани между tOut (снаружи) и tIn (внутри): точка — уточнённая,
    /// параметр — FaceSplit.ClosestParam (та же функция, что режет ребро в разбиении грани-источника).</summary>
    static double CrossParam(ICurve3 c, AnalyticFace dst, double tOut, double tIn)
    {
        double lo = Math.Min(tOut, tIn), hi = Math.Max(tOut, tIn);
        double span = hi - lo; lo -= span; hi += span; lo = Math.Max(lo, c.T0); hi = Math.Min(hi, c.T1);
        for (int k = 0; k < 200 && hi - lo > 1e-15; k++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (dst.BoundaryDistance(c.PointAt(m1)) < dst.BoundaryDistance(c.PointAt(m2))) hi = m2; else lo = m1;
        }
        double tA = (lo + hi) / 2;
        // точно — пересечение ребра с ребром границы dst (Гаусс — Ньютон): тернарный поиск давал ~1e-5 (фаска вогнутого
        // угла: вершина (0, 0, 8.9999925) вместо (0, 0, 9), объём — 6e-5)
        return FaceIntersect.RefineOnEdge(c, tA, dst, null) ?? tA;
    }

    /// <summary>
    /// Касание плоскости с цилиндром (или параллельных цилиндров) идёт по образующей; безопасно, если каждая её точка,
    /// лежащая внутри обеих граней, лежит на границе хотя бы одной из них. Прочие касания — небезопасны.
    /// </summary>
    static bool TangentOnBoundary(AnalyticFace x, AnalyticFace y)
    {
        if (x.Cone || y.Cone || x.Sph || y.Sph) return false;               // касания конусов и сфер — не разбираются
        Vector3d p0, dir;
        if (x.Cyl != y.Cyl)
        {
            var cy = x.Cyl ? x : y; var pl = x.Cyl ? y : x;
            dir = cy.W; double s = Vector3d.Dot(cy.O - pl.O, pl.W);
            p0 = cy.O - pl.W * Math.Sign(s) * cy.R;                       // ближайшая к плоскости образующая
        }
        else if (x.Cyl && y.Cyl && Math.Abs(Math.Abs(Vector3d.Dot(x.W, y.W)) - 1) < 1e-12)
        {
            var d = y.O - x.O; d -= x.W * Vector3d.Dot(d, x.W); double L = d.Length; if (L < 1e-12) return false;
            dir = x.W; p0 = x.O + d / L * x.R * (L > Math.Max(x.R, y.R) ? 1 : (y.R > x.R ? -1 : 1));
        }
        else return false;
        // образующая в пределах габарита обеих граней
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var f in new[] { x, y })
            foreach (var ce in f.Face.Loops.SelectMany(l => l.CoEdges)) { var q = new List<Vector3d>(); ce.Tessellate(q, true); foreach (var p in q) { double t = Vector3d.Dot(p - p0, dir); lo = Math.Min(lo, t); hi = Math.Max(hi, t); } }
        for (int i = 0; i <= 200; i++)
        {
            var p = p0 + dir * (lo + (hi - lo) * i / 200);
            if (x.SurfaceDistance(p) > 1e-7 || y.SurfaceDistance(p) > 1e-7) return false;      // не касание по образующей
            bool inX = x.Contains(p) != false, inY = y.Contains(p) != false;
            if (!inX || !inY) continue;
            if (x.BoundaryDistance(p) > TolV && y.BoundaryDistance(p) > TolV) return false;    // внутреннее касание
        }
        return true;
    }

    /// <summary>Линия касания вдоль образующей (плоскость × цилиндр, параллельные цилиндры) в пределах габарита граней.</summary>
    static ICurve3? TangentLine(AnalyticFace x, AnalyticFace y)
    {
        // СФЕРА × ЦИЛИНДР (центр на оси, радиусы равны) — касание по окружности: угловое сопряжение скругления касается
        // цилиндров скруглений соседних кромок по большому кругу в плоскости ⟂ оси.
        if (x.Sph != y.Sph && (x.Cyl || y.Cyl) && !(x.Cone || y.Cone))
        {
            var sp = x.Sph ? x : y; var cy = x.Sph ? y : x;
            var d = sp.O - cy.O; var off = d - cy.W * Vector3d.Dot(d, cy.W);
            if (off.Length < 1e-7 && Math.Abs(sp.R - cy.R) < 1e-7)
            {
                var u = Vector3d.Cross(cy.W, Math.Abs(cy.W.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY).Normalized();
                return ArcCurve3.FullCircle(new Frame3(sp.O, u, Vector3d.Cross(cy.W, u)), sp.R);
            }
            return null;
        }
        // ПЛОСКОСТЬ × КОНУС — касание вдоль образующей через вершину: g = cos α·a + sin α·m, m ⟂ a со стороны,
        // противоположной нормали плоскости (знак — тот, при котором g лежит в плоскости); одна пола (от вершины).
        if ((x.Cone && !y.Cyl && !y.Sph) || (y.Cone && !x.Cyl && !x.Sph))
        {
            var kn = x.Cone ? x : y; var pl = x.Cone ? y : x;
            var a = kn.W; var nperp = pl.W - a * Vector3d.Dot(pl.W, a);
            if (nperp.Length < 1e-12) return null;
            double alpha = Math.Atan(kn.TanA);
            Vector3d? g = null; double bestDot = 1e-6;
            foreach (double sgn in new[] { -1.0, 1.0 })
            {
                var cand = a * Math.Cos(alpha) + nperp.Normalized() * (sgn * Math.Sin(alpha));
                double dd = Math.Abs(Vector3d.Dot(cand, pl.W));
                if (dd < bestDot) { bestDot = dd; g = cand.Normalized(); }
            }
            TangentDebug?.Invoke($"plane×cone: apex off plane {Vector3d.Dot(kn.O - pl.O, pl.W):G3}, best |g·n| {bestDot:G3}, g {(g == null ? "none" : "ok")}, tanA {kn.TanA:G4}, a·n {Vector3d.Dot(a, pl.W):G4}");
            if (g == null || Math.Abs(Vector3d.Dot(kn.O - pl.O, pl.W)) > 1e-7) return null;     // вершина — в плоскости
            double hiK = 0;
            foreach (var f in new[] { x, y })
                foreach (var ce in f.Face.Loops.SelectMany(l => l.CoEdges)) { var q = new List<Vector3d>(); ce.Tessellate(q, true); foreach (var pt in q) hiK = Math.Max(hiK, Vector3d.Dot(pt - kn.O, g.Value)); }
            if (hiK <= 0) return null;
            return new LineCurve3(kn.O, kn.O + g.Value * (hiK + 1));
        }
        if (x.Cone || y.Cone || x.Sph || y.Sph) return null;
        Vector3d p0, dir;
        if (x.Cyl != y.Cyl)
        {
            var cy = x.Cyl ? x : y; var pl = x.Cyl ? y : x;
            dir = cy.W; double sgn = Math.Sign(Vector3d.Dot(cy.O - pl.O, pl.W));
            p0 = cy.O - pl.W * sgn * cy.R;
        }
        else if (x.Cyl && y.Cyl && Math.Abs(Math.Abs(Vector3d.Dot(x.W, y.W)) - 1) < 1e-12)
        {
            var d = y.O - x.O; d -= x.W * Vector3d.Dot(d, x.W); double L = d.Length; if (L < 1e-12) return null;
            dir = x.W; p0 = x.O + d / L * x.R * (L > Math.Max(x.R, y.R) ? 1 : (y.R > x.R ? -1 : 1));
        }
        else return null;
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var f in new[] { x, y })
            foreach (var ce in f.Face.Loops.SelectMany(l => l.CoEdges)) { var q = new List<Vector3d>(); ce.Tessellate(q, true); foreach (var pt in q) { double t = Vector3d.Dot(pt - p0, dir); lo = Math.Min(lo, t); hi = Math.Max(hi, t); } }
        if (!(hi > lo)) return null;
        return new LineCurve3(p0 + dir * (lo - 1), p0 + dir * (hi + 1));
    }

    /// <summary>Лежит ли кусок на границе грани (точки на 1/4, 1/2, 3/4 — ближе допуска совпадения точек).</summary>
    static bool OnBoundary(FaceIntersect.Piece p, AnalyticFace f)
    {
        foreach (double s in new[] { 0.25, 0.5, 0.75 })
        {
            double t = p.T0 + (p.T1 - p.T0) * s, span = p.Curve.T1 - p.Curve.T0;
            if (t > p.Curve.T1) t -= span;
            if (f.BoundaryDistance(p.Curve.PointAt(t)) > TolV) return false;
        }
        return true;
    }

    /// <summary>Часть кривой на [lo, hi] (параметр за T1 — по кругу: у дуги и эллипса — углы дальше, у трассы — затравка).</summary>
    /// <summary>Грань ленты целиком — одна область: её контуры дугами-рёбрами в направлении полурёбер (как у границы в FaceSplit).</summary>
    static List<FaceSplit.Region> BandRegion(AnalyticFace f)
    {
        var r = new FaceSplit.Region();
        foreach (var l in f.Face.Loops)
        {
            var ord = l.Ordered() ?? l.CoEdges.ToList();
            var darts = new List<FaceSplit.Dart>();
            foreach (var ce in ord)
            {
                var c = ce.Edge.Curve;
                darts.Add(new FaceSplit.Dart { Curve = c, T0 = ce.Reversed ? c.T1 : c.T0, T1 = ce.Reversed ? c.T0 : c.T1, BoundaryEdge = ce.Edge });
            }
            r.Loops.Add(darts);
        }
        var S = f.BandS!; double um = f.BandU0 + (f.BandU1 - f.BandU0) * 0.5;
        r.Inside = S.PointAt(new Vector2d(um, 0.5));
        foreach (double fr in new[] { 0.31, 0.69, 0.13, 0.87 }) r.InsideCandidates.Add(S.PointAt(new Vector2d(f.BandU0 + (f.BandU1 - f.BandU0) * fr, 0.5)));
        return new List<FaceSplit.Region> { r };
    }

    static ICurve3 SubCurve(ICurve3 c, double lo, double hi)
    {
        switch (c)
        {
            case LineCurve3:
                return new LineCurve3(c.PointAt(lo), c.PointAt(hi));
            case ArcCurve3 arc:
                return new ArcCurve3(arc.Frame, arc.Radius, arc.A0 + lo * (arc.A1 - arc.A0), arc.A0 + hi * (arc.A1 - arc.A0));
            case EllipseCurve3 el:
                return new EllipseCurve3(el.Frame, el.RU, el.RV, el.A0 + lo * (el.A1 - el.A0), el.A0 + hi * (el.A1 - el.A0));
            case ProcCurve3 pc:
                return pc.Sub(lo, hi);
            case IntersectionCurve3 ic:
                {
                    var seed = new List<Vector3d>(); int n = 128; double span = c.T1 - c.T0;
                    for (int i = 0; i <= n; i++)
                    {
                        double t = lo + (hi - lo) * i / n; if (t > c.T1) t -= span;
                        seed.Add(c.PointAt(t));
                    }
                    return new IntersectionCurve3(ic.A, ic.B, seed);
                }
            default:
                throw new InvalidOperationException("sub-curve of " + c.GetType().Name);
        }
    }
}
