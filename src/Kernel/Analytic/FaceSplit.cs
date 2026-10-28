using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// АНАЛИТИЧЕСКОЕ ЯДРО, ЭТАП 2, БЛОК 3: РАЗБИЕНИЕ ГРАНИ КУСКАМИ ПЕРЕСЕЧЕНИЯ.
///
/// Куски от разных граней второго тела внутри грани не пересекаются (грани тела пересекаются по его рёбрам — там куски
/// сходятся концами), поэтому раскладка — граф: рёбра границы грани, разрезанные в концах кусков (каждое — один раз,
/// грань слева), и куски — каждый дважды, в обе стороны (по обе стороны — грань). Обход граней графа — наибольший левый
/// поворот в узле (направления — малым шагом вдоль кривой в параметрах грани: плоскость (u, v), цилиндр (θ·R, h) —
/// развёртка цилиндра сохраняет площадь). Площадь цикла — −∮ v du (корректна и для петель вокруг цилиндра); ориентация
/// параметров — так, чтобы площадь самой грани была положительной. Циклы без оборота: площадь > 0 — наружная граница
/// области, &lt; 0 — отверстие (в наименьшую содержащую область); петли с оборотом вокруг цилиндра — пояса: область над
/// петлёй по +θ тянется до ближайшей петли сверху по −θ.
/// </summary>
public static partial class FaceSplit
{
    /// <summary>Кусок кривой с направлением: точки — curve.PointAt(t0 + (t1 − t0)·s), параметр за T1 — по кругу.</summary>
    public sealed class Dart
    {
        public ICurve3 Curve = null!;
        public double T0, T1;
        public int From, To;              // узлы
        public bool IsPiece;              // кусок пересечения (иначе — часть ребра границы)
        public TEdge? BoundaryEdge;       // для части границы — исходное ребро
        public Dart? Twin;
        public bool Used;
        public Vector3d At(double s)
        {
            double t = T0 + (T1 - T0) * s; double span = Curve.T1 - Curve.T0;
            if (t > Curve.T1 + 1e-15) t -= span; else if (t < Curve.T0 - 1e-15) t += span;
            return Curve.PointAt(t);
        }
    }

    public sealed class Region
    {
        public readonly List<List<Dart>> Loops = new();   // первый — наружный (у пояса — нижний), прочие — отверстия/вторая петля
        public double Area;                               // площадь (мм²)
        /// <summary>Неограниченная в проекции область сферы (вокруг полюса): контуры — только «отверстия».</summary>
        public bool Unbounded;
        /// <summary>KRN-02: область конуса до вершины, лежащей ВЫШЕ контура в развёртке (луч вверх контур не пересекает).</summary>
        public bool ApexAbove;
        /// <summary>Знак развёртки грани (обход наружных контуров в параметрах): нужен мере области вокруг полюса.</summary>
        public double ChartSign = 1;
        public Vector3d Inside;                           // точка внутри области (на поверхности грани)
        public readonly List<Vector3d> InsideCandidates = new();   // запасные точки внутри (глубже — раньше)
    }

    sealed class Cycle
    {
        public List<Dart> Darts = new();
        public double Area;       // −∮ v du
        public double Turns;      // обороты вокруг цилиндра (Δu / (2πR))
        public List<Vector2d> Poly = new();   // ломаная в параметрах (u развёрнуто вдоль цикла)
    }

    /// <summary>
    /// Допуск совпадения точек, мм — соизмерим с сеткой точного ядра (как Region2Ops.Eps): вершины тел из многогранного
    /// пути округлены, и конец куска пересечения у угла отстоял от вершины на 3.5e-6 мм (повёрнутые бруски ~400 мм) —
    /// при допуске 1e-7 кусок «висел» с одним концом.
    /// </summary>
    /// <summary>KRN-02: допуск операции на время общей булевой (BooleanOp); иначе — Region2Ops.Eps.</summary>
    [ThreadStatic] internal static double? TolOverride;
    /// <summary>Разбор: P3D_CONEHALF=0 — прежний выбор точки (за вершину конуса).</summary>
    static readonly bool ConeHalf = Environment.GetEnvironmentVariable("P3D_CONEHALF") != "0";
    static readonly bool BandDbg = Environment.GetEnvironmentVariable("P3D_BANDDBG") == "1";
    /// <summary>04.4/04.6: сведений узлов и точек разреза дальше округления одного события (1e-9 мм), но ближе допуска.</summary>
    public static long NodeMergedFar, CutFar;
    static readonly bool FarDbg = Environment.GetEnvironmentVariable("P3D_FARDBG") == "1";
    /// <summary>Точки ближе этого — одно событие (точность модели); дальше, но ближе допуска слияния — разные точки.</summary>
    internal static double MergeExact => ToleranceContext.Current.ModelAccuracy;
    internal static readonly bool MergeGuardOff = Environment.GetEnvironmentVariable("P3D_MERGEGUARD") == "0";
    static double TolV => TolOverride ?? Region2Ops.Eps;
    [ThreadStatic] static Action<string>? _debug;
    public static Action<string>? Debug { get => _debug ?? EnvDebug; set => _debug = value; }
    /// <summary>k116: P3D_SPLITDBG=1 — разбор разбиения граней в stderr (из любого потока).</summary>
    static readonly Action<string>? EnvDebug = Environment.GetEnvironmentVariable("P3D_SPLITDBG") == "1" ? m => Console.Error.WriteLine("SPLIT " + m) : null;
    /// <summary>k94: кусок пересечения, совпавший с ребром границы грани, отбрасывается. P3D_FSDUPPIECE=0 — прежнее.</summary>
    public static readonly bool DupPieceOff = Environment.GetEnvironmentVariable("P3D_FSDUPPIECE") == "0";
    public static long DupPiecesDropped;
    static readonly bool ConeCapHoleOff = Environment.GetEnvironmentVariable("P3D_CONECAPHOLE") == "0";

    public static List<Region>? Split(AnalyticFace f, IReadOnlyList<FaceIntersect.Piece> pieces, out string why) =>
        Split(f, pieces, null, out why);

    /// <summary>
    /// globalCuts — концы кусков пересечения со ВСЕГО тела: ребро границы делят две грани, и обе обязаны резать его в
    /// одних и тех же точках (параметр точки на кривой ищется детерминированно — побитово один и тот же), иначе сшивка
    /// не сойдётся.
    /// </summary>
    public static List<Region>? Split(AnalyticFace f, IReadOnlyList<FaceIntersect.Piece> pieces, IReadOnlyList<Vector3d>? globalCuts, out string why)
    {
        if (f.Tor) return TorusSplit(f, pieces, globalCuts, out why);
        why = "";
        // ориентация параметров: площадь самой грани > 0
        double sign = 1;
        {
            double a = 0;
            var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
            foreach (var ce in f.Face.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
            foreach (var loop in f.Face.Loops)
            {
                var pts = new List<Vector3d>();
                foreach (var ce in loop.CoEdges) { if (uses[ce.Edge] > 1) continue; ce.Tessellate(pts, pts.Count == 0); }
                a += LoopArea(f, pts, 1);
            }
            if (a < 0) sign = -1;
            // ПОЛНАЯ СФЕРА (контуров нет — знак по площади не определить): стереографическая проекция из полюса W меняет
            // ориентацию относительно наружной нормали (у противоположной точки нормаль −W, базис проекции — по +W);
            // нормаль грани от центра — параметры обращаются (купол объединения входил с обратной ориентацией).
            if (f.Sph && f.FullSurface)
            {
                var p0 = f.O - f.W * f.R;
                bool outward = Vector3d.Dot(f.Face.NormalAt(p0), p0 - f.O) > 0;
                sign = outward ? -1 : 1;
                Debug?.Invoke($"sphere full: outward {outward} sign {sign} reversed {f.Face.Reversed}");
            }
        }
        Debug?.Invoke($"split {f.Face.Tag} param sign {sign} cyl {f.Cyl} pieces {pieces.Count} closed {pieces.Count(p => p.Closed)}");
        if (Debug != null && Environment.GetEnvironmentVariable("P3D_FSCYC") == "1")
            foreach (var pc in pieces) Debug($"  piece {pc.Curve.GetType().Name} T {pc.T0:G6}..{pc.T1:G6} P0 {pc.P0:0.####} P1 {pc.P1:0.####} at(T0) {pc.Curve.PointAt(pc.T0):0.####} at(T1) {pc.Curve.PointAt(pc.T1):0.####} closed {pc.Closed}");
        Vector2d P(Vector3d x, double uRef)
        {
            if (!f.Cyl) { var pp = f.PlaneUV(x); return new(pp.X, sign * pp.Y); }
            double th = f.Theta(x) * f.R; double k = Math.Round((uRef - th) / (2 * Math.PI * f.R));
            return new(th + k * 2 * Math.PI * f.R, sign * f.H(x));
        }

        // узлы
        var nodes = new List<Vector3d>();
        string? farMerge = null;
        int Node(Vector3d x)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                double dn = (nodes[i] - x).Length;
                if (dn < TolV)
                {
                    if (dn > MergeExact)
                    {
                        Interlocked.Increment(ref NodeMergedFar); farMerge ??= $"two distinct points {dn:G3} mm apart (above the model accuracy, below the merge tolerance) at {x:0.#########}";
                        if (FarDbg) Console.WriteLine($"NODEFAR {dn:G3} at {x:0.#########} face {f.Face.Tag} {f.Shape?.GetType().Name}");
                    }
                    return i;
                }
            }
            nodes.Add(x); return nodes.Count - 1;
        }
        var darts = new List<Dart>();

        // концы кусков — точки разреза рёбер границы
        var cuts = new List<Vector3d>();
        if (globalCuts != null) cuts.AddRange(globalCuts);
        else foreach (var p in pieces) if (!p.Closed) { cuts.Add(p.P0); cuts.Add(p.P1); }

        // рёбра границы (без швов), разрезанные в точках кусков; направление — как у полуребра (грань слева)
        var useCount = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in f.Face.Loops.SelectMany(l => l.CoEdges)) useCount[ce.Edge] = useCount.GetValueOrDefault(ce.Edge) + 1;
        foreach (var ce in f.Face.Loops.SelectMany(l => l.CoEdges))
        {
            if (useCount[ce.Edge] > 1) continue;
            var c = ce.Edge.Curve;
            double a0 = ce.Reversed ? c.T1 : c.T0, a1 = ce.Reversed ? c.T0 : c.T1;
            var ts = new List<double> { a0 };
            // k126: кривая пересечения — точки разреза дальше её уточнённой ломаной (габарит + наибольшее звено + допуск) её не
            // режут: поиск ближайшего параметра (уточнения на обе поверхности) не нужен. Мутация P3D_CUTBOX=0.
            (Vector3d Lo, Vector3d Hi)? near = null;
            if (!CutBoxOff && cuts.Count > 2 && c is LineCurve3 lnb)
            {
                var mg = new Vector3d(2 * TolV + 1e-9);
                near = (Vector3d.ComponentMin(lnb.A, lnb.B) - mg, Vector3d.ComponentMax(lnb.A, lnb.B) + mg);
            }
            else if (!CutBoxOff && cuts.Count > 2 && c is ArcCurve3 acb)
            {
                // габарит всей окружности (диск поперёк оси)
                var w = acb.Frame.W.Normalized(); double r = acb.Radius;
                var ext = new Vector3d(r * Math.Sqrt(Math.Max(0, 1 - w.X * w.X)), r * Math.Sqrt(Math.Max(0, 1 - w.Y * w.Y)), r * Math.Sqrt(Math.Max(0, 1 - w.Z * w.Z))) + new Vector3d(2 * TolV + 1e-9 + 1e-9 * r);
                near = (acb.Frame.Origin - ext, acb.Frame.Origin + ext);
            }
            else if (!CutBoxOff && c is IntersectionCurve3 icb && cuts.Count > 2)
            {
                var (rp, _) = icb.RefinedWithParams();
                var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue); double seg = 0;
                for (int i = 0; i < rp.Length; i++) { lo = Vector3d.ComponentMin(lo, rp[i]); hi = Vector3d.ComponentMax(hi, rp[i]); if (i > 0) seg = Math.Max(seg, (rp[i] - rp[i - 1]).Length); }
                var mg = new Vector3d(seg + 2 * TolV + 1e-9);
                near = (lo - mg, hi + mg);
            }
            foreach (var x in cuts)
            {
                if (near is { } nb && (x.X < nb.Lo.X || x.Y < nb.Lo.Y || x.Z < nb.Lo.Z || x.X > nb.Hi.X || x.Y > nb.Hi.Y || x.Z > nb.Hi.Z)) { Interlocked.Increment(ref CutsSkippedByBox); continue; }
                double t = ClosestParam(c, x);
                double dc = (c.PointAt(t) - x).Length;
                if (dc < TolV && Math.Min(Math.Abs(t - c.T0), Math.Abs(t - c.T1)) > 1e-12 * Math.Max(1, c.T1 - c.T0))
                {
                    ts.Add(t);
                    if (dc > MergeExact) { Interlocked.Increment(ref CutFar); farMerge ??= $"a cut point {dc:G3} mm off the edge it cuts (above the model accuracy) at {x:0.#########}"; }
                }
            }
            ts.Add(a1);
            var inner = ts.Skip(1).Take(ts.Count - 2).OrderBy(t => (t - a0) / (a1 - a0)).ToList();
            var seq = new List<double> { a0 }; seq.AddRange(inner); seq.Add(a1);
            // замкнутое — по-настоящему (окружность заметной длины); ребро короче допуска слияния (остаток многогранных
            // операций) схлопывается в точку и пропускается (давало петлю «узел → тот же узел»)
            double edgeLen = 0; { var pp = c.PointAt(c.T0); for (int k = 1; k <= 8; k++) { var qq = c.PointAt(c.T0 + (c.T1 - c.T0) * k / 8); edgeLen += (qq - pp).Length; pp = qq; } }
            bool closedEdge = (c.PointAt(c.T0) - c.PointAt(c.T1)).Length < TolV && edgeLen > 10 * TolV;
            for (int i = 0; i + 1 < seq.Count; i++)
            {
                if (Math.Abs(seq[i + 1] - seq[i]) < 1e-15) continue;
                var d = new Dart { Curve = c, T0 = seq[i], T1 = seq[i + 1], BoundaryEdge = ce.Edge };
                // Концы полуребра — по его ВЕРШИНАМ (авторитетны в B-rep): у рёбер из многогранного пути восстановленная
                // кривая расходится с вершинами (на скруглении — 0.004 мм), и граница грани по концам кривых рвалась.
                d.From = Node(i == 0 ? ce.Start.P : d.At(0)); d.To = Node(i + 2 == seq.Count ? ce.End.P : d.At(1));
                // вырожденный кусок ребра (оба конца — один узел, а ребро не замкнуто целиком): почти совпадающие точки
                // разреза — пропустить
                if (d.From == d.To && !(closedEdge && seq.Count == 2)) continue;
                darts.Add(d);
            }
            _ = closedEdge;
        }
        // куски — в обе стороны
        int nBoundary = darts.Count;
        foreach (var p in pieces)
        {
            // k94: КУСОК ПО РЕБРУ ГРАНИЦЫ ИЛИ ПОВТОР КУСКА — совпадающая поверхность другого тела (шар на шаре) режет грань ровно по её же
            // ребру. Дубль границы ломал обход («a face cycle runs into a used piece»); граница уже делит грань там же.
            if (!DupPieceOff && !p.Closed)
            {
                var pd = new Dart { Curve = p.Curve, T0 = p.T0, T1 = p.T1 };   // концы — как у дартов куска (с переходом через шов)
                int a0 = Node(pd.At(0)), a1 = Node(pd.At(1));
                bool dup = false;
                for (int k = 0; k < darts.Count && !dup; k++)
                {
                    var bd = darts[k];
                    if (k >= nBoundary && ((k - nBoundary) & 1) == 1) continue;   // у куска — одна проверка из пары
                    if (!((bd.From == a0 && bd.To == a1) || (bd.From == a1 && bd.To == a0))) continue;
                    // расстояние до дуги дарта (по его доле s ∈ [0, 1]: параметры кривых границы и куска разные)
                    double DistTo(Vector3d x)
                    {
                        int m = 32; double bs = 0, bdist = double.MaxValue;
                        for (int k2 = 0; k2 <= m; k2++) { double dd = (bd.At((double)k2 / m) - x).Length; if (dd < bdist) { bdist = dd; bs = (double)k2 / m; } }
                        double lo = Math.Max(0, bs - 1.0 / m), hi = Math.Min(1, bs + 1.0 / m);
                        for (int it = 0; it < 60; it++)
                        {
                            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                            if ((bd.At(m1) - x).Length < (bd.At(m2) - x).Length) hi = m2; else lo = m1;
                        }
                        return Math.Min(bdist, (bd.At(0.5 * (lo + hi)) - x).Length);
                    }
                    dup = DistTo(pd.At(0.5)) < TolV && DistTo(pd.At(0.25)) < TolV && DistTo(pd.At(0.75)) < TolV;
                }
                if (dup) { Interlocked.Increment(ref DupPiecesDropped); Debug?.Invoke($"duplicate piece dropped {p.P0:0.###} → {p.P1:0.###}"); continue; }
            }
            var d1 = new Dart { Curve = p.Curve, T0 = p.T0, T1 = p.T1, IsPiece = true };
            var d2 = new Dart { Curve = p.Curve, T0 = p.T1, T1 = p.T0, IsPiece = true };
            d1.From = Node(d1.At(0)); d1.To = Node(d1.At(1)); d2.From = d1.To; d2.To = d1.From;
            d1.Twin = d2; d2.Twin = d1;
            darts.Add(d1); darts.Add(d2);
        }

        // 04.4/04.6: общие вершины и точки разреза — одно событие до округления (точности модели); две РАЗНЫЕ точки ближе
        // допуска слияния, но дальше точности модели слились бы молча — отказ. Мутация P3D_MERGEGUARD=0.
        if (farMerge != null && !MergeGuardOff) { why = farMerge; return null; }
        // направление выхода из узла — малым шагом вдоль кривой, в параметрах грани
        Vector2d OutDir(Dart d)
        {
            var x0 = d.At(0); var a = P(x0, 0);
            for (double s = 1e-6; s <= 0.25; s *= 4)
            {
                var b = P(d.At(s), a.X); var v = b - a;
                if (v.Length > 1e-9) return v.Normalized();
            }
            return Vector2d.UnitX;
        }
        Vector2d InDir(Dart d)
        {
            var x1 = d.At(1); var b = P(x1, 0);
            for (double s = 1e-6; s <= 0.25; s *= 4)
            {
                var a = P(d.At(1 - s), b.X); var v = b - a;
                if (v.Length > 1e-9) return v.Normalized();
            }
            return Vector2d.UnitX;
        }
        var outOf = new Dictionary<int, List<Dart>>();
        foreach (var d in darts) { if (!outOf.TryGetValue(d.From, out var l)) outOf[d.From] = l = new(); l.Add(d); }

        // обход: из узла — наибольший левый поворот (кроме возврата по тому же куску)
        var cycles = new List<Cycle>();
        foreach (var start in darts)
        {
            if (start.Used) continue;
            var cyc = new Cycle(); var cur = start;
            for (int guard = 0; ; guard++)
            {
                if (guard > darts.Count) { why = "a face cycle does not close"; return null; }
                cur.Used = true; cyc.Darts.Add(cur);
                var nx = Next(cur, outOf.GetValueOrDefault(cur.To) ?? new());
                if (nx == null)
                {
                    if (Debug != null)
                    {
                        Debug($"DEAD END at node {cur.To} {nodes[cur.To]}; face {f.Face.Tag} cyl {f.Cyl} sph {f.Sph}; arriving {(cur.IsPiece ? "P" : "B")} {cur.Curve.GetType().Name}");
                        foreach (var (node, outs) in outOf) Debug($"  node {node} {nodes[node]}: out {outs.Count} in {darts.Count(d => d.To == node)}: " + string.Join(" | ", outs.Select(d => $"{(d.IsPiece ? "P" : "B")} {d.Curve.GetType().Name[0]} to {d.To}")));
                        foreach (var n in Enumerable.Range(0, nodes.Count).Where(n => !outOf.ContainsKey(n))) Debug($"  node {n} {nodes[n]}: NO OUT, in {darts.Count(d => d.To == n)}");
                    }
                    why = "a dead end in the face graph"; return null;
                }
                if (ReferenceEquals(nx, start)) break;
                if (nx.Used)
                {
                    if (Debug != null)
                    {
                        Debug($"FAIL at node {cur.To} {nodes[cur.To]}; cycle so far {cyc.Darts.Count} darts; face {f.Face.Tag} cyl {f.Cyl}");
                        foreach (var (node, outs) in outOf.Where(kv => kv.Value.Count > 1 || true).Take(60))
                        {
                            var ins = darts.Where(d => d.To == node).ToList();
                            Debug($"  node {node} {nodes[node]}: out {outs.Count} in {ins.Count}: " + string.Join(" | ", outs.Select(d => $"{(d.IsPiece ? "P" : "B")} {d.Curve.GetType().Name[0]} dir {OutDir(d).X:0.####},{OutDir(d).Y:0.####} to {d.To} used {d.Used}")));
                        }
                    }
                    why = "a face cycle runs into a used piece"; return null;
                }
                cur = nx;
            }
            cycles.Add(cyc);
        }
        // СЕКУЩИЕ — для почти параллельных и почти встречных касательных: резец скругления больше тела на 0.001 мм, его грань
        // параллельна стороне тела, кусок пересечения идёт вдоль ребра границы — в узле направления совпадают до 4-го
        // знака, поворот ≈ ±π и его знак — шум; обход то сворачивал в щель, то нет. Секущая на ~10 % длины различает надёжно.
        Vector2d OutSecant(Dart d) { var a = P(d.At(0), 0); return (P(d.At(0.1), a.X) - a).Normalized(); }
        Vector2d InSecant(Dart d) { var b = P(d.At(1), 0); return (b - P(d.At(0.9), b.X)).Normalized(); }
        Dart? Next(Dart cur, List<Dart> cand)
        {
            var din = InDir(cur);
            Dart? best = null; double bestTurn = double.MinValue;
            foreach (var d in cand)
            {
                if (ReferenceEquals(d, cur.Twin) && cand.Count > 1) continue;
                var dout = OutDir(d);
                double cr = din.X * dout.Y - din.Y * dout.X, dt = Vector2d.Dot(din, dout);
                if (Math.Abs(cr) < 1e-6)
                {
                    // почти параллельно или встречно: сторона — по секущим
                    var si = InSecant(cur); var so = OutSecant(d);
                    double cr2 = si.X * so.Y - si.Y * so.X;
                    cr = Math.Abs(cr2) > Math.Abs(cr) ? cr2 * 1e-3 : cr;      // знак — от секущих, величина — малая
                }
                double turn = Math.Atan2(cr, dt);
                if (turn > bestTurn) { bestTurn = turn; best = d; }
            }
            return best;
        }

        // площади, обороты, ломаные
        foreach (var cy in cycles)
        {
            double uRef = 0; bool first = true;
            foreach (var d in cy.Darts)
            {
                int? ms = StraightSteps(f, d); if (ms != null) Interlocked.Increment(ref StraightDarts);
                int m = ms ?? Math.Max(16, (int)Math.Ceiling(Length(d) * 4));
                for (int i = first ? 0 : 1; i <= m; i++)
                {
                    var q = P(d.At((double)i / m), uRef); uRef = q.X; cy.Poly.Add(q);
                }
                first = false;
            }
            double area = 0;
            for (int i = 0; i + 1 < cy.Poly.Count; i++) area += -0.5 * (cy.Poly[i].Y + cy.Poly[i + 1].Y) * (cy.Poly[i + 1].X - cy.Poly[i].X);
            cy.Area = area;
            cy.Turns = f.Cyl ? (cy.Poly[^1].X - cy.Poly[0].X) / (2 * Math.PI * f.R) : 0;
        }
        if (Debug != null && Environment.GetEnvironmentVariable("P3D_FSCYC") == "1")
            foreach (var cy in cycles)
                Debug($"  cycle area {cy.Area:G6}: " + string.Join(" ", cy.Darts.Select(d => $"{(d.IsPiece ? "P" : "B")}{d.Curve.GetType().Name[0]}[{d.T0:0.####}-{d.T1:0.####}]{d.From}>{d.To}")) + " | nodes " + string.Join(" ", cy.Darts.Select(d => $"{d.From}={nodes[d.From]:G12}")));

        // области
        var regions = new List<Region>();
        var contractible = cycles.Where(c => Math.Abs(c.Turns) < 0.5).ToList();
        var wrapping = cycles.Where(c => Math.Abs(c.Turns) >= 0.5).ToList();
        var outers = contractible.Where(c => c.Area > 0).ToList();
        var holes = contractible.Where(c => c.Area <= 0).ToList();
        var regionOf = new Dictionary<Cycle, Region>();
        foreach (var o in outers) { var r = new Region(); r.Loops.Add(o.Darts); r.Area = o.Area; regions.Add(r); regionOf[o] = r; }
        // пояса: петля по +θ (снизу) — до ближайшей сверху по −θ
        double HAt(Cycle c, double u)
        {
            // высота петли при данном u (по ломаной, u по модулю периода)
            double per = 2 * Math.PI * f.R; double best = double.NaN;
            for (int i = 0; i + 1 < c.Poly.Count; i++)
            {
                double ua = c.Poly[i].X, ub = c.Poly[i + 1].X;
                double lo = Math.Min(ua, ub), hi = Math.Max(ua, ub);
                // KRN-02: сдвиг на период — одним округлением к середине куска, сравнение — с допуском. Прежде
                // (u + per) − per ≠ u в double: при масштабе ×100 (период 8796 мм) первая точка петли оказывалась на 1 ulp
                // вне своего же куска, высота петли — NaN, разбиение отказывало («a band has no upper boundary»).
                double uu = u + per * Math.Round(((lo + hi) / 2 - u) / per);
                double tu = Math.Max(TolV, 4e-16 * Math.Max(Math.Abs(lo), Math.Abs(hi)));
                if (uu >= lo - tu && uu <= hi + tu && hi - lo > tu)
                { double t = Math.Clamp((uu - ua) / (ub - ua), 0, 1); best = c.Poly[i].Y + (c.Poly[i + 1].Y - c.Poly[i].Y) * t; break; }
            }
            return best;
        }
        var bottoms = wrapping.Where(c => c.Turns > 0).ToList(); var tops = wrapping.Where(c => c.Turns < 0).ToList();
        var caps = new List<(Cycle C, bool Above)>();   // g.05: области конуса до вершины — для отверстий внутри них
        if (BandDbg && f.Cyl)
            Console.WriteLine($"BANDDBG {(f.Cone ? "cone" : "cyl")} {f.Face.Tag} R {f.R:G4} sign {sign} reversed {f.Face.Reversed} wrapping: {string.Join(" | ", wrapping.Select(c => $"turns {c.Turns:0.##} h {HAt(c, c.Poly[0].X):G6} area {c.Area:G6}"))}; others {cycles.Count - wrapping.Count}");
        foreach (var b in bottoms)
        {
            double u0 = b.Poly[0].X, hb = HAt(b, u0);
            Cycle? top = null; double ht = double.MaxValue;
            // KRN-02: верхняя граница — выше нижней больше чем на допуск (двойник той же окружности на высоте hb + ε —
            // не граница полосы, а соседняя полоса; прежде его отсекал NaN ошибки сдвига на период)
            foreach (var t in tops) { double h = HAt(t, u0); if (h > hb + TolV && h < ht) { ht = h; top = t; } }
            // KRN-02: КОНУС С ВЕРШИНОЙ ВВЕРХУ РАЗВЁРТКИ. Ориентация развёртки (sign) выбирается так, чтобы площадь грани была
            // положительной; при sign = −1 (Y = −H) вершина (H = 0) — ВЫШЕ петель, и область до вершины ограничена непарной
            // НИЖНЕЙ петлёй (прежде правило знало только вершину внизу — непарную верхнюю петлю; зенковка в части ориентаций
            // детали отказывала «a band has no upper boundary»).
            if (top == null && f.Cone && hb < 0)
            {
                var cap = new Region { ApexAbove = true }; cap.Loops.Add(b.Darts); cap.Area = b.Area; regions.Add(cap); regionOf[b] = cap; caps.Add((b, true));
                continue;
            }
            if (top == null) { why = $"a band has no upper boundary (kind {(f.Cone ? "cone" : f.Cyl ? "cyl" : "other")}, R {f.R:G6}, u0 {u0:G6}, hb {hb:G9}, bottom turns {b.Turns:G6} area {b.Area:G6}; tops: {string.Join(";", tops.Select(t => $"turns {t.Turns:G6} h {HAt(t, u0):G9} area {t.Area:G6} pts {t.Poly.Count}"))})"; return null; }
            var r = new Region(); r.Loops.Add(b.Darts); r.Loops.Add(top.Darts); r.Area = b.Area + top.Area; regions.Add(r);
            regionOf[b] = r; regionOf[top] = r;
        }
        // КОНУС С ВЕРШИНОЙ: петля по −θ без пары снизу ограничивает область, тянущуюся до вершины (h = 0 — нижний край
        // развёртки; зенковка: окружность разреза делит конус на верхний пояс и «шапку» вокруг вершины)
        if (f.Cone)
            foreach (var t in tops.Where(t => !regionOf.ContainsKey(t)).ToList())
            {
                var r = new Region(); r.Loops.Add(t.Darts); r.Area = t.Area; regions.Add(r); regionOf[t] = r; caps.Add((t, false));
            }
        if (tops.Any(t => !regionOf.ContainsKey(t)))
        {
            why = $"a band has no lower boundary (bottoms {string.Join(";", bottoms.Select(c => $"turns {c.Turns:0.##} h {HAt(c, c.Poly[0].X):0.###} area {c.Area:0.##} darts {c.Darts.Count}"))} | tops {string.Join(";", tops.Select(c => $"turns {c.Turns:0.##} h {HAt(c, bottoms.Count > 0 ? bottoms[0].Poly[0].X : 0):0.###} area {c.Area:0.##} darts {c.Darts.Count} assigned {regionOf.ContainsKey(c)}"))})";
            return null;
        }
        // отверстия — в наименьшую содержащую область (точка отверстия; для пояса — между петлями по высоте)
        foreach (var h in holes)
        {
            // Пробная точка — СО СТОРОНЫ ОБЛАСТИ: чуть левее цикла отверстия (не на нём: его точка лежит и на границе
            // двойника — у окружности внутри грани круг «содержал» точку на своей границе, и отверстие уходило в круг).
            var q = h.Poly[0];
            {
                int k0 = h.Poly.Count / 2; var a0 = h.Poly[k0]; var tng = h.Poly[Math.Min(k0 + 1, h.Poly.Count - 1)] - h.Poly[Math.Max(k0 - 1, 0)];
                if (tng.Length > 1e-15) q = a0 + new Vector2d(-tng.Y, tng.X).Normalized() * 1e-4;
            }
            Region? best = null; double bestA = double.MaxValue;
            foreach (var o in outers)
                if (o.Area < bestA && Inside(o.Poly, q)) { best = regionOf[o]; bestA = o.Area; }
            if (best == null)
                foreach (var b in bottoms)
                {
                    var r = regionOf[b]; var top = cycles.First(c => r.Loops[1] == c.Darts);
                    double hb = HAt(b, q.X), ht = HAt(top, q.X);
                    if (q.Y > hb && q.Y < ht && r.Area < bestA) { best = r; bestA = r.Area; }
                }
            // g.05: КОНУС — отверстие в области «до вершины» (петля пересечения шара с боковой стенкой острого конуса): прежде
            // такие области не рассматривались — «a hole lies in no region», отказ маршрута. Мутация P3D_CONECAPHOLE=0.
            if (best == null && f.Cone && !ConeCapHoleOff)
                foreach (var (cc, above) in caps)
                {
                    double hc = HAt(cc, q.X);
                    if (!double.IsNaN(hc) && (above ? q.Y > hc : q.Y < hc)) { best = regionOf[cc]; break; }
                }
            if (best == null)
            {
                // сфера: отверстие вне всех ограниченных областей — граница НЕОГРАНИЧЕННОЙ области вокруг полюса
                // (полная сфера, разрезанная окружностью, — две шапки: в проекции диск и всё снаружи)
                if (!f.Sph) { why = "a hole lies in no region"; return null; }
                var ub = regions.FirstOrDefault(x => x.Unbounded);
                if (ub == null) { ub = new Region { Unbounded = true }; regions.Add(ub); }
                ub.Loops.Add(h.Darts); ub.Area += h.Area;
                continue;
            }
            best.Loops.Add(h.Darts); best.Area += h.Area;
        }
        // точка внутри каждой области: сдвиг влево от середины самого длинного куска её контуров, с проверкой
        foreach (var r in regions)
        {
            var polys = r.Loops.Select(l => cycles.First(c => c.Darts == l).Poly).ToList();
            double perR = f.Cyl ? 2 * Math.PI * f.R : 0;
            // k126: сетка ячеек по отрезкам контуров области — чётность и зазор кандидатов без обхода всех точек контура
            // (у грани трубы — 4 650 точек на кандидат, 30 000 кандидатов за расчёт). Ответы те же побитово. P3D_SPLITGRID=0.
            var grid = PolyGrid.For(polys, perR);
            bool InRegion(Vector2d q)
            {
                int cr = 0;
                if (grid != null) cr = grid.Crossings(q);
                else foreach (var poly in polys) cr += Crossings(poly, q, perR);
                return ((cr & 1) == 1) ^ r.Unbounded ^ r.ApexAbove;   // неограниченная (и шапка с вершиной сверху) — снаружи своих контуров
            }
            // Точка внутри — КАК МОЖНО ГЛУБЖЕ (сначала большие сдвиги): у линии касания цилиндра с плоскостью плоскость
            // отстоит от цилиндра на d²/(2R) — точка в 0.001 мм от контура признавалась лежащей «на» цилиндре.
            // KRN-02: ТОЧКА С НАИБОЛЬШИМ ЗАЗОРОМ ДО ГРАНИЦЫ ОБЛАСТИ. Прежде бралась первая подошедшая из сдвигов 1, 0.3, 0.1 …
            // мм — абсолютных: у детали в масштабе 0.05 (высота 1 мм) сдвиг 1.0 от нижней кромки полосы попадал ТОЧНО на
            // верхнюю (зазор 7e-13 мм); «внутри» ли она — решало округление, и после поворота/сдвига детали стенка отверстия
            // отбрасывалась классификацией (открытые рёбра). Теперь кандидаты — абсолютные и пропорциональные размеру области
            // сдвиги от длинных кусков контура; выбирается наибольший зазор, прочие — запасные по убыванию зазора.
            Vector3d? inside = null;
            double per = f.Cyl ? 2 * Math.PI * f.R : 0, rs = Math.Sqrt(Math.Abs(r.Area));
            var offsets = new[] { 1.0, 0.3, 0.1, 0.03, 1e-2, 3e-3, 1e-3, 3e-4, 1e-4, 0.5 * rs, 0.25 * rs, 0.1 * rs, 0.03 * rs }.Where(k => k > 0).Distinct().ToArray();
            var cands = new List<(double C, Vector3d X)>();
            foreach (var d in r.Loops.SelectMany(l => l).OrderByDescending(Length).Take(6))
            {
                var a = P(d.At(0.5), 0); var t = P(d.At(0.5001), a.X) - P(d.At(0.4999), a.X);
                if (t.Length < 1e-15) continue;
                var left = new Vector2d(-t.Y, t.X).Normalized();
                foreach (double k in offsets)
                {
                    var q = a + left * k;
                    // KRN-03: у конуса точка — на рабочей полости (H > 0): развёртка за вершиной не имеет смысла, а чётность
                    // считала точку за вершиной «внутри» шапки (точка на оси под остриём сверла — грань переворачивалась);
                    // зазор — и до вершины (H = 0)
                    if (f.Cone && ConeHalf && sign * q.Y <= 0) continue;
                    if (!InRegion(q)) continue;
                    double cl = grid != null ? grid.Clearance(q) : Clearance(polys, q, per);
                    if (f.Cone && ConeHalf) cl = Math.Min(cl, Math.Abs(q.Y));
                    cands.Add((cl, FromParam(f, q, sign)));
                }
            }
            foreach (var (_, x) in cands.OrderByDescending(c => c.C)) { inside ??= x; if (r.InsideCandidates.Count < 6) r.InsideCandidates.Add(x); }
            if (inside == null)
            {
                why = $"no interior point for a region (loops {r.Loops.Count}, darts {string.Join(",", r.Loops.Select(l => l.Count))}, area {r.Area:G6}, poly points {string.Join(",", polys.Select(p => p.Count))}, first {polys[0][0]} last {polys[0][^1]})";
                return null;
            }
            r.Inside = inside.Value;
            if (Debug != null && Environment.GetEnvironmentVariable("P3D_FSCYC") == "1")
            {
                var qq = P(inside.Value, 0);
                                foreach (var poly in polys) Debug($"  poly n {poly.Count} u {poly.Min(p => p.X):G6}..{poly.Max(p => p.X):G6} v {poly.Min(p => p.Y):G6}..{poly.Max(p => p.Y):G6} first {poly[0]} last {poly[^1]} cross {Crossings(poly, qq, per)} q {qq}");
            }
            if (Debug != null)
            {
                var qi = P(inside.Value, 0);
                Debug($"region inside: kind {(f.Cone ? "cone" : f.Cyl ? "cyl" : "plane")} q ({qi.X:G9}, {qi.Y:G9}) clearance {Clearance(polys, qi, f.Cyl ? 2 * Math.PI * f.R : 0):G6} area {r.Area:G6}");
            }
        }
        foreach (var rg in regions) rg.ChartSign = sign;
        return regions;
    }

    /// <summary>KRN-02: расстояние от точки развёртки до ближайшего контура области (у цилиндра — с учётом периода).</summary>
    static double Clearance(List<List<Vector2d>> polys, Vector2d q, double per)
    {
        if (ClearFastOff) return ClearanceOld(polys, q, per);
        // k125: без выделения массива сдвигов на каждый отрезок (4 ГБ мусора на детали пользователя) и с отсевом отрезка,
        // чей габарит дальше уже найденного зазора — минимум тот же побитово. Мутация P3D_CLEARFAST=0.
        double best = double.MaxValue;
        var shs = per > 0 ? new[] { -per, 0, per } : ZeroShift;
        foreach (var poly in polys)
            for (int i = 0; i + 1 < poly.Count; i++)
                foreach (double sh in shs)
                {
                    var a = poly[i]; var b = poly[i + 1]; var p = q + new Vector2d(sh, 0);
                    if (per > 0) { double mid = (a.X + b.X) / 2; p = new Vector2d(q.X + per * Math.Round((mid - q.X) / per) + sh, q.Y); }
                    double dx = Math.Max(0, Math.Max(Math.Min(a.X, b.X) - p.X, p.X - Math.Max(a.X, b.X)));
                    double dy = Math.Max(0, Math.Max(Math.Min(a.Y, b.Y) - p.Y, p.Y - Math.Max(a.Y, b.Y)));
                    if (dx * dx + dy * dy > best * best * (1 + 1e-9)) continue;
                    var ab = b - a; double t = ab.LengthSquared > 0 ? Math.Clamp(Vector2d.Dot(p - a, ab) / ab.LengthSquared, 0, 1) : 0;
                    best = Math.Min(best, (a + ab * t - p).Length);
                }
        return best;
    }
    static readonly double[] ZeroShift = { 0.0 };
    static readonly bool ClearFastOff = Environment.GetEnvironmentVariable("P3D_CLEARFAST") == "0";
    static double ClearanceOld(List<List<Vector2d>> polys, Vector2d q, double per)
    {
        double best = double.MaxValue;
        foreach (var poly in polys)
            for (int i = 0; i + 1 < poly.Count; i++)
                foreach (double sh in per > 0 ? new[] { -per, 0, per } : new[] { 0.0 })
                {
                    var a = poly[i]; var b = poly[i + 1]; var p = q + new Vector2d(sh, 0);
                    // ближайший период к куску
                    if (per > 0) { double mid = (a.X + b.X) / 2; p = new Vector2d(q.X + per * Math.Round((mid - q.X) / per) + sh, q.Y); }
                    var ab = b - a; double t = ab.LengthSquared > 0 ? Math.Clamp(Vector2d.Dot(p - a, ab) / ab.LengthSquared, 0, 1) : 0;
                    best = Math.Min(best, (a + ab * t - p).Length);
                }
        return best;
    }

    static Vector3d FromParam(AnalyticFace f, Vector2d q, double sign)
    {
        if (!f.Cyl) return f.FromPlaneUV(new Vector2d(q.X, sign * q.Y));
        double th = q.X / f.R, hh = sign * q.Y;
        return f.O + f.W * hh + (f.U * Math.Cos(th) + f.V * Math.Sin(th)) * f.RadiusAt(hh);
    }

    /// <summary>Пересечения луча +v из точки с ломаной (у цилиндра u — по модулю периода per).</summary>
    static readonly bool CrossWrapOld = Environment.GetEnvironmentVariable("P3D_CROSSWRAP") == "0";
    static int Crossings(List<Vector2d> poly, Vector2d q, double per)
    {
        int n = 0;
        for (int i = 0; i + 1 < poly.Count; i++) n += CrossOne(poly[i], poly[i + 1], q, per);
        return n;
    }

    /// <summary>Пересекает ли луч +v из q отрезок a–b (у цилиндра u — по модулю периода per): 1 или 0.</summary>
    static int CrossOne(Vector2d pa, Vector2d pb, Vector2d q, double per)
    {
        double ua = pa.X, ub = pb.X; double lo = Math.Min(ua, ub), hi = Math.Max(ua, ub);
        if (hi - lo < 1e-15) return 0;
        double uu = q.X;
        // q в своём периоде — без сдвига: q + per − per ≠ q в плавающей точке, и куски по обе стороны вершины с u = q
        // считались оба (тройник равных труб: вершина в верхней точке линзы — дырка не вычиталась). Мутация P3D_CROSSWRAP=0.
        if (per > 0 && CrossWrapOld) { while (uu < lo) uu += per; while (uu - per >= lo) uu -= per; }
        else if (per > 0 && !(uu >= lo && uu < hi))
        {
            double j0 = Math.Floor((lo - q.X) / per); bool found = false;
            for (double j = j0 - 1; j <= j0 + 2 && !found; j++) { if (j == 0) continue; double x = q.X + j * per; if (x >= lo && x < hi) { uu = x; found = true; } }
            if (!found) return 0;
        }
        if (uu < lo || uu >= hi) return 0;
        double t = (uu - ua) / (ub - ua); double v = pa.Y + (pb.Y - pa.Y) * t;
        return v > q.Y ? 1 : 0;
    }

    /// <summary>Зазор от q до отрезка a–b (у цилиндра — до ближайших периодов, как в Clearance); best — текущий минимум.</summary>
    static double ClearOne(Vector2d a, Vector2d b, Vector2d q, double per, double best)
    {
        if (per > 0)
        {
            double mid = (a.X + b.X) / 2; double baseX = q.X + per * Math.Round((mid - q.X) / per);
            best = ClearAt(a, b, new Vector2d(baseX - per, q.Y), best);
            best = ClearAt(a, b, new Vector2d(baseX, q.Y), best);
            return ClearAt(a, b, new Vector2d(baseX + per, q.Y), best);
        }
        return ClearAt(a, b, q, best);
    }
    static double ClearAt(Vector2d a, Vector2d b, Vector2d p, double best)
    {
        double dx = Math.Max(0, Math.Max(Math.Min(a.X, b.X) - p.X, p.X - Math.Max(a.X, b.X)));
        double dy = Math.Max(0, Math.Max(Math.Min(a.Y, b.Y) - p.Y, p.Y - Math.Max(a.Y, b.Y)));
        if (dx * dx + dy * dy > best * best * (1 + 1e-9)) return best;
        var ab = b - a; double t = ab.LengthSquared > 0 ? Math.Clamp(Vector2d.Dot(p - a, ab) / ab.LengthSquared, 0, 1) : 0;
        return Math.Min(best, (a + ab * t - p).Length);
    }

    /// <summary>
    /// k126 (проверки): сетка ячеек против полного обхода — случайные ломаные (плоские и развёртки цилиндра с периодом) и
    /// случайные точки: чётность и зазор должны совпасть побитово. Возвращает (сравнений, расхождений).
    /// </summary>
    public static (int Compared, int Mismatches) GridSelfCheck(int seed, int polys, int queries)
    {
        var rnd = new Random(seed); int cmp = 0, bad = 0;
        for (int pi = 0; pi < polys; pi++)
        {
            double per = rnd.Next(2) == 0 ? 0 : 2 * Math.PI * (5 + 50 * rnd.NextDouble());
            int n = 300 + rnd.Next(1500);
            var poly = new List<Vector2d>();
            double cx = rnd.NextDouble() * 10, cy = rnd.NextDouble() * 10, r0 = 5 + 20 * rnd.NextDouble();
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n, r = r0 * (1 + 0.3 * Math.Sin(7 * t + pi));
                poly.Add(new Vector2d(cx + r * Math.Cos(t) * (per > 0 ? per / (2 * Math.PI) / r0 : 1), cy + r * Math.Sin(t)));
            }
            poly.Add(poly[0]);
            var polysL = new List<List<Vector2d>> { poly };
            var g = PolyGrid.For(polysL, per);
            if (g == null) continue;
            for (int k = 0; k < queries; k++)
            {
                var q = new Vector2d(cx + (rnd.NextDouble() - 0.5) * 4 * r0 * (per > 0 ? per / (2 * Math.PI) / r0 : 1), cy + (rnd.NextDouble() - 0.5) * 4 * r0);
                int c1 = g.Crossings(q), c2 = Crossings(poly, q, per);
                double d1 = g.Clearance(q), d2 = Clearance(polysL, q, per);
                cmp++; if (c1 != c2 || d1 != d2) bad++;
            }
        }
        return (cmp, bad);
    }

    static readonly bool SplitGridOff = Environment.GetEnvironmentVariable("P3D_SPLITGRID") == "0";
    static readonly bool SplitGridCheck = Environment.GetEnvironmentVariable("P3D_SPLITGRIDCHECK") == "1";
    /// <summary>k126: разбор — расхождений сетки ячеек с полным обходом (P3D_SPLITGRIDCHECK=1).</summary>
    public static long SplitGridMismatches;
    /// <summary>k126: областей с сеткой ячеек / прямых кусков ломаной из концов / точек разреза, отсеянных габаритом (проверки).</summary>
    public static long GridRegions, StraightDarts, CutsSkippedByBox;

    /// <summary>
    /// k126: СЕТКА ЯЧЕЕК ОТРЕЗКОВ КОНТУРОВ ОБЛАСТИ. Чётность луча — только отрезки столбцов, где лежат периодические образы
    /// q (прочие отрезки луч не пересекают); зазор — обход ячеек кольцами от образов q, пока кольцо не дальше найденного.
    /// Для каждого найденного отрезка — та же формула, что в полном обходе (CrossOne, ClearOne), поэтому ответы те же.
    /// </summary>
    sealed class PolyGrid
    {
        readonly Vector2d[] _a, _b; readonly double _per;
        readonly double _x0, _y0, _cw, _ch, _x1, _y1; readonly int _nx, _ny;
        readonly int[] _start = null!, _items = null!; readonly int[] _stamp; int _gen;
        readonly List<List<Vector2d>> _polys;

        PolyGrid(List<List<Vector2d>> polys, double per, int n)
        {
            _polys = polys; _per = per;
            _a = new Vector2d[n]; _b = new Vector2d[n]; _stamp = new int[n];
            int k = 0; double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var po in polys)
                for (int i = 0; i + 1 < po.Count; i++)
                {
                    _a[k] = po[i]; _b[k] = po[i + 1]; k++;
                    x0 = Math.Min(x0, Math.Min(po[i].X, po[i + 1].X)); x1 = Math.Max(x1, Math.Max(po[i].X, po[i + 1].X));
                    y0 = Math.Min(y0, Math.Min(po[i].Y, po[i + 1].Y)); y1 = Math.Max(y1, Math.Max(po[i].Y, po[i + 1].Y));
                }
            double w = Math.Max(x1 - x0, 1e-12), h = Math.Max(y1 - y0, 1e-12);
            int cells = Math.Clamp(n / 8, 16, 1 << 16);
            _nx = Math.Clamp((int)Math.Round(Math.Sqrt(cells * w / h)), 1, 1024); _ny = Math.Clamp(cells / _nx, 1, 1024);
            _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1; _cw = w / _nx; _ch = h / _ny;
            // ячейки — сжатыми строками (счёт, затем заполнение): без списка на ячейку
            _start = new int[_nx * _ny + 1];
            for (int i = 0; i < n; i++)
            {
                int cx0 = Col(Math.Min(_a[i].X, _b[i].X)), cx1 = Col(Math.Max(_a[i].X, _b[i].X));
                int cy0 = Row(Math.Min(_a[i].Y, _b[i].Y)), cy1 = Row(Math.Max(_a[i].Y, _b[i].Y));
                for (int cx = cx0; cx <= cx1; cx++) for (int cy = cy0; cy <= cy1; cy++) _start[cx * _ny + cy + 1]++;
            }
            for (int k2 = 0; k2 < _nx * _ny; k2++) _start[k2 + 1] += _start[k2];
            _items = new int[_start[^1]];
            var fill = new int[_nx * _ny];
            for (int i = 0; i < n; i++)
            {
                int cx0 = Col(Math.Min(_a[i].X, _b[i].X)), cx1 = Col(Math.Max(_a[i].X, _b[i].X));
                int cy0 = Row(Math.Min(_a[i].Y, _b[i].Y)), cy1 = Row(Math.Max(_a[i].Y, _b[i].Y));
                for (int cx = cx0; cx <= cx1; cx++) for (int cy = cy0; cy <= cy1; cy++) { int ci = cx * _ny + cy; _items[_start[ci] + fill[ci]++] = i; }
            }
        }
        int Col(double x) => Math.Clamp((int)Math.Floor((x - _x0) / _cw), 0, _nx - 1);
        int Row(double y) => Math.Clamp((int)Math.Floor((y - _y0) / _ch), 0, _ny - 1);
        double X1 => _x1;
        double Y1 => _y1;

        public static PolyGrid? For(List<List<Vector2d>> polys, double per)
        {
            if (SplitGridOff || CrossWrapOld) return null;
            int n = 0; foreach (var po in polys) n += Math.Max(0, po.Count - 1);
            if (n < 256) return null;
            foreach (var po in polys) foreach (var v in po) if (!double.IsFinite(v.X) || !double.IsFinite(v.Y)) return null;
            Interlocked.Increment(ref GridRegions);
            return new PolyGrid(polys, per, n);
        }

        /// <summary>Образы q.X по периоду в пределах [lo, hi] (сам q.X — всегда первым).</summary>
        IEnumerable<double> Images(double x, double lo, double hi)
        {
            yield return x;
            if (_per <= 0) yield break;
            for (double j = Math.Floor((lo - x) / _per); x + j * _per <= hi; j++) if (j != 0 && x + j * _per >= lo) yield return x + j * _per;
        }

        public int Crossings(Vector2d q)
        {
            int n = 0; int gen = ++_gen;
            foreach (double xi in Images(q.X, _x0, X1))
            {
                if (xi < _x0 || xi > X1) continue;
                int c = Col(xi);
                // соседний столбец — граница ячейки (xi ровно на ней)
                // пересечение выше q — у отрезка, чей верх выше q: он записан и в строках не ниже строки q
                int ry = Row(q.Y);
                for (int cx = Math.Max(0, c - 1); cx <= Math.Min(_nx - 1, c + 1); cx++)
                    for (int cy = ry; cy < _ny; cy++)
                    {
                        int ci = cx * _ny + cy;
                        for (int k = _start[ci]; k < _start[ci + 1]; k++)
                        {
                            int i = _items[k];
                            if (_stamp[i] == gen) continue;
                            _stamp[i] = gen;
                            n += CrossOne(_a[i], _b[i], q, _per);
                        }
                    }
            }
            if (SplitGridCheck)
            {
                int full = 0; foreach (var po in _polys) full += FaceSplit.Crossings(po, q, _per);
                if (full != n) { Interlocked.Increment(ref SplitGridMismatches); return full; }
            }
            return n;
        }

        public double Clearance(Vector2d q)
        {
            double best = double.MaxValue; int gen = ++_gen;
            double lim = Math.Min(_cw, _ch);
            foreach (double xi in Images(q.X, _x0 - 2 * _per, X1 + 2 * _per))
            {
                var c = new Vector2d(xi, q.Y);
                // расстояние от образа до габарита сетки (образ снаружи — кольца считаются от ближайшей ячейки)
                double ox = Math.Max(0, Math.Max(_x0 - c.X, c.X - X1)), oy = Math.Max(0, Math.Max(_y0 - c.Y, c.Y - Y1));
                double outside = Math.Sqrt(ox * ox + oy * oy);
                if (outside * outside > best * best * (1 + 1e-9)) continue;
                int cx = Col(c.X), cy = Row(c.Y);
                int rmax = Math.Max(_nx, _ny);
                for (int r = 0; r <= rmax; r++)
                {
                    if (r >= 2 && Math.Max(outside, (r - 1) * lim) > best * (1 + 1e-9)) break;
                    for (int ix = cx - r; ix <= cx + r; ix++)
                    {
                        if (ix < 0 || ix >= _nx) continue;
                        bool edgeCol = ix == cx - r || ix == cx + r;
                        for (int iy = cy - r; iy <= cy + r; iy += edgeCol ? 1 : 2 * r)
                        {
                            if (iy < 0 || iy >= _ny) { if (r == 0) break; continue; }
                            int ci = ix * _ny + iy;
                            for (int k = _start[ci]; k < _start[ci + 1]; k++)
                            {
                                int i = _items[k];
                                if (_stamp[i] == gen) continue;
                                _stamp[i] = gen;
                                best = ClearOne(_a[i], _b[i], q, _per, best);
                            }
                            if (r == 0) break;
                        }
                    }
                }
            }
            if (SplitGridCheck)
            {
                double full = FaceSplit.Clearance(_polys, q, _per);
                if (full != best) { Interlocked.Increment(ref SplitGridMismatches); return full; }
            }
            return best;
        }
    }

    static bool Inside(List<Vector2d> poly, Vector2d q) => (Crossings(poly, q, 0) & 1) == 1;

    static double LoopArea(AnalyticFace f, List<Vector3d> pts, double sign)
    {
        double a = 0, uRef = 0; Vector2d? prev = null;
        foreach (var x in pts)
        {
            Vector2d q;
            if (!f.Cyl) { var pp = f.PlaneUV(x); q = new(pp.X, sign * pp.Y); }
            else { double th = f.Theta(x) * f.R; double k = Math.Round((uRef - th) / (2 * Math.PI * f.R)); q = new(th + k * 2 * Math.PI * f.R, sign * f.H(x)); uRef = q.X; }
            if (prev is { } p) a += -0.5 * (p.Y + q.Y) * (q.X - p.X);
            prev = q;
        }
        return a;
    }

    static readonly bool CutBoxOff = Environment.GetEnvironmentVariable("P3D_CUTBOX") == "0";
    static readonly bool PolyStraightOff = Environment.GetEnvironmentVariable("P3D_POLYSTRAIGHT") == "0";
    /// <summary>
    /// k126: КУСОК, ПРЯМОЙ В ПАРАМЕТРАХ ГРАНИ — ломаная из его концов (у дуги — через каждые 45°, чтобы развёртка угла не
    /// теряла оборот): отрезок плоской грани; образующая и окружность поперёк оси у цилиндра и конуса. Прежде — 4 точки на мм
    /// (у окружности трубы R 92 — 2 300 точек, и каждая — в чётности и зазорах кандидатов точки внутри). Ломаная та же
    /// прямая. null — кривая не такая (прежняя густота). Мутация P3D_POLYSTRAIGHT=0.
    /// </summary>
    static int? StraightSteps(AnalyticFace f, Dart d)
    {
        if (PolyStraightOff || f.Sph || f.Tor || f.Band || f.Param) return null;
        if (d.Curve is LineCurve3 ln)
        {
            if (!f.Cyl) return 1;
            var dir = ln.B - ln.A; double L = dir.Length; if (L <= 0) return null;
            dir /= L;
            if (f.Cone)
            {
                var ra = ln.A - f.O; var rb = ln.B - f.O;
                double sc = Math.Max(ra.Length, rb.Length);
                return Vector3d.Cross(dir, (ra.Length >= rb.Length ? ra : rb)).Length <= 1e-12 * Math.Max(1, sc) ? 1 : null;
            }
            return Vector3d.Cross(dir, f.W.Normalized()).Length <= 1e-12 ? 1 : null;
        }
        if (d.Curve is ArcCurve3 arc && f.Cyl)
        {
            var w = f.W.Normalized();
            if (Vector3d.Cross(arc.Frame.W.Normalized(), w).Length > 1e-12) return null;
            var oc = arc.Frame.Origin - f.O;
            if ((oc - w * Vector3d.Dot(oc, w)).Length > 1e-9 * Math.Max(1, arc.Radius)) return null;
            double span = Math.Abs(d.T1 - d.T0) * Math.Abs(arc.Sweep);
            return Math.Max(1, (int)Math.Ceiling(span / (Math.PI / 4)));
        }
        return null;
    }

    static double Length(Dart d)
    {
        double l = 0; var p = d.At(0);
        for (int i = 1; i <= 16; i++) { var q = d.At(i / 16.0); l += (q - p).Length; p = q; }
        return l;
    }

    /// <summary>Параметр ближайшей к x точки кривой (сэмплы + тернарное уточнение).</summary>
    static readonly bool ClosestLocalMinimaOff = Environment.GetEnvironmentVariable("P3D_CLOSESTMINIMA") == "0";
    /// <summary>
    /// Параметр ближайшей точки кривой. Уточняются ВСЕ локальные минимумы выборки (не только лучший отсчёт): у замкнутой
    /// кривой ближайшая точка у шва бывает по другую сторону параметра, а лучший отсчёт — на краю (g.05, брусок минус тор:
    /// событие ребра в 0.007 мм от начала кривой терялось, вершок овала у угла не вырезался). Мутация P3D_CLOSESTMINIMA=0.
    /// </summary>
    /// <summary>
    /// k126: БЛИЖАЙШИЙ ПАРАМЕТР — у отрезка и дуги по формуле (проекция; у дуги — угол проекции в пределах дуги, иначе
    /// ближний конец), у прочих кривых — память по (кривая, точка): функция чистая, а точки разреза и концы кусков
    /// спрашиваются по нескольку раз (у каждой из двух граней ребра, при обрезке и при сборке). Прежде — 257 отсчётов и
    /// тернарный поиск каждый раз (1.8 с на детали пользователя). Мутация P3D_CLOSESTFAST=0.
    /// </summary>
    public static double ClosestParam(ICurve3 c, Vector3d x)
    {
        if (ClosestFastOff) return ClosestParamCore(c, x);
        if (ClosestCheck && c is IntersectionCurve3)
        {
            ClosestFastOffForCheck = true; double tc = ClosestParamCore(c, x); ClosestFastOffForCheck = false;
            double tf = ClosestFast(c, x);
            if ((c.PointAt(tc) - x).Length < 1 && ((c.PointAt(tf) - x).Length > (c.PointAt(tc) - x).Length + 1e-9 || Math.Abs(tf - tc) > 1e-6))
            {
                if (Interlocked.Increment(ref ClosestMismatches) < 12)
                    Console.Error.WriteLine($"CLOSEST {c.GetType().Name} {(c is ArcCurve3 ac0 ? $"sweep {ac0.Sweep:R} r {ac0.Radius}" : "")} tf {tf:R} tc {tc:R} df {(c.PointAt(tf) - x).Length:G4} dc {(c.PointAt(tc) - x).Length:G4}");
            }
            return tf;
        }
        return ClosestFast(c, x);
    }
    // отрезок и дуга — прежним поиском (с памятью): его конечная разрешающая способность сводит почти совпадающие точки
    // к одному параметру, и ключи рёбер у соседних граней совпадают побитово (формула давала разные — KRN04-COMMON-VERTICES)
    [ThreadStatic] static bool ClosestFastOffForCheck;
    static readonly bool ClosestCheck = Environment.GetEnvironmentVariable("P3D_CLOSESTCHECK") == "1";
    public static long ClosestMismatches;
    static double ClosestFast(ICurve3 c, Vector3d x)
    {
        switch (ClosestAnalyticOn ? c : null)
        {
            case LineCurve3 ln:
            {
                var d = ln.B - ln.A; double l2 = d.LengthSquared;
                if (l2 <= 0) return 0;
                return Math.Clamp(Vector3d.Dot(x - ln.A, d) / l2, 0, 1);
            }
            case ArcCurve3 arc when arc.Radius > 0 && Math.Abs(arc.Sweep) > 1e-12:
            {
                var q = x - arc.Frame.Origin;
                double u = Vector3d.Dot(q, arc.Frame.U), v = Vector3d.Dot(q, arc.Frame.V);
                if (u * u + v * v < 1e-24) break;                               // центр — любая точка: прежний поиск
                double phi = Math.Atan2(v, u), sw = arc.Sweep, sg = Math.Sign(sw);
                double dlt = (phi - arc.A0) * sg; dlt -= 2 * Math.PI * Math.Floor(dlt / (2 * Math.PI));
                if (dlt <= Math.Abs(sw)) return dlt / Math.Abs(sw);
                // вне дуги — ближний конец (у замкнутой не бывает)
                return (arc.PointAt(0) - x).LengthSquared <= (arc.PointAt(1) - x).LengthSquared ? 0 : 1;
            }
        }
        if (c is IntersectionCurve3 ic && ClosestOnTrace(ic, x) is double ti) return ti;
        var box = _closestMemo.GetValue(c, _ => new System.Collections.Concurrent.ConcurrentDictionary<(long, long, long), double>());
        var key = (BitConverter.DoubleToInt64Bits(x.X), BitConverter.DoubleToInt64Bits(x.Y), BitConverter.DoubleToInt64Bits(x.Z));
        if (box.TryGetValue(key, out double t0)) { Interlocked.Increment(ref ClosestMemoHits); return t0; }
        double t = ClosestParamCore(c, x);
        if (box.Count < 4096) box[key] = t;
        return t;
    }
    static readonly bool ClosestFastOff = Environment.GetEnvironmentVariable("P3D_CLOSESTFAST") == "0";
    static readonly bool ClosestAnalyticOn = Environment.GetEnvironmentVariable("P3D_CLOSESTANALYTIC") == "1";

    /// <summary>
    /// k126: КРИВАЯ ПЕРЕСЕЧЕНИЯ — по её уточнённой ломаной: местные минимумы расстояния до звеньев (как у отсчётов прежнего
    /// поиска) и золотое сечение по параметру кривой в окне соседних звеньев каждого; ответ — наименьшее расстояние.
    /// Прежде — 257 точек кривой (каждая — уточнение на обе поверхности) и тернарный поиск у каждого минимума.
    /// </summary>
    static double? ClosestOnTrace(IntersectionCurve3 c, Vector3d x)
    {
        var (P, T) = c.RefinedWithParams();
        int n = P.Length; if (n < 2) return null;
        var d = new double[n - 1];
        for (int i = 0; i + 1 < n; i++)
        {
            var a = P[i]; var ab = P[i + 1] - a; double l2 = ab.LengthSquared;
            double t = l2 > 0 ? Math.Clamp(Vector3d.Dot(x - a, ab) / l2, 0, 1) : 0;
            d[i] = (a + ab * t - x).LengthSquared;
        }
        double bestT = 0, bestD = double.MaxValue;
        const double g = 0.6180339887498949;
        for (int i = 0; i < d.Length; i++)
        {
            bool min = (i == 0 || d[i] <= d[i - 1]) && (i == d.Length - 1 || d[i] <= d[i + 1]);
            if (!min) continue;
            double lo = T[Math.Max(0, i - 1)], hi = T[Math.Min(n - 1, i + 2)];
            double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo);
            double f1 = (c.PointAt(x1) - x).LengthSquared, f2 = (c.PointAt(x2) - x).LengthSquared;
            for (int it = 0; it < 200 && hi - lo > 1e-15; it++)
            {
                if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = (c.PointAt(x1) - x).LengthSquared; }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = (c.PointAt(x2) - x).LengthSquared; }
            }
            double tm = (lo + hi) / 2, dm = (c.PointAt(tm) - x).LengthSquared;
            // концы окна (ближайшая точка у конца кривой)
            if (dm < bestD) { bestD = dm; bestT = tm; }
        }
        foreach (double te in new[] { 0.0, 1.0 }) { double de = (c.PointAt(te) - x).LengthSquared; if (de < bestD) { bestD = de; bestT = te; } }
        return bestT;
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ICurve3, System.Collections.Concurrent.ConcurrentDictionary<(long, long, long), double>> _closestMemo = new();
    /// <summary>k126: ответов ближайшего параметра из памяти (разбор).</summary>
    public static long ClosestMemoHits;

    static double ClosestParamCore(ICurve3 c, Vector3d x)
    {
        int n = 256; double T0 = c.T0, T1 = c.T1, step = (T1 - T0) / n;
        var d = new double[n + 1];
        for (int i = 0; i <= n; i++) d[i] = (c.PointAt(T0 + step * i) - x).LengthSquared;
        int bi = 0; for (int i = 1; i <= n; i++) if (d[i] < d[bi]) bi = i;
        double Refine(int k)
        {
            double lo = Math.Max(T0, T0 + step * (k - 1)), hi = Math.Min(T1, T0 + step * (k + 1));
            for (int it = 0; it < 200 && hi - lo > 1e-15 * Math.Max(1, T1 - T0); it++)
            {
                double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if ((c.PointAt(m1) - x).LengthSquared < (c.PointAt(m2) - x).LengthSquared) hi = m2; else lo = m1;
            }
            return (lo + hi) / 2;
        }
        if (ClosestLocalMinimaOff) return Refine(bi);
        double bt = Refine(bi), bd = (c.PointAt(bt) - x).LengthSquared;
        for (int i = 0; i <= n; i++)
        {
            bool min = (i == 0 || d[i] <= d[i - 1]) && (i == n || d[i] <= d[i + 1]);
            if (!min || i == bi) continue;
            double t = Refine(i), dd = (c.PointAt(t) - x).LengthSquared;
            if (dd < bd) { bd = dd; bt = t; }
        }
        return bt;
    }
}
