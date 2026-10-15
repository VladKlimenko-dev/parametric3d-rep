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
    [ThreadStatic] public static Action<string>? Debug;
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
            foreach (var x in cuts)
            {
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
        foreach (var p in pieces)
        {
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
                int m = Math.Max(16, (int)Math.Ceiling(Length(d) * 4));
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
            bool InRegion(Vector2d q)
            {
                int cr = 0;
                foreach (var poly in polys) cr += Crossings(poly, q, f.Cyl ? 2 * Math.PI * f.R : 0);
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
                    double cl = Clearance(polys, q, per);
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
        for (int i = 0; i + 1 < poly.Count; i++)
        {
            double ua = poly[i].X, ub = poly[i + 1].X; double lo = Math.Min(ua, ub), hi = Math.Max(ua, ub);
            if (hi - lo < 1e-15) continue;
            double uu = q.X;
            // q в своём периоде — без сдвига: q + per − per ≠ q в плавающей точке, и куски по обе стороны вершины с u = q
            // считались оба (тройник равных труб: вершина в верхней точке линзы — дырка не вычиталась). Мутация P3D_CROSSWRAP=0.
            if (per > 0 && CrossWrapOld) { while (uu < lo) uu += per; while (uu - per >= lo) uu -= per; }
            else if (per > 0 && !(uu >= lo && uu < hi))
            {
                double j0 = Math.Floor((lo - q.X) / per); bool found = false;
                for (double j = j0 - 1; j <= j0 + 2 && !found; j++) { if (j == 0) continue; double x = q.X + j * per; if (x >= lo && x < hi) { uu = x; found = true; } }
                if (!found) continue;
            }
            if (uu < lo || uu >= hi) continue;
            double t = (uu - ua) / (ub - ua); double v = poly[i].Y + (poly[i + 1].Y - poly[i].Y) * t;
            if (v > q.Y) n++;
        }
        return n;
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
    public static double ClosestParam(ICurve3 c, Vector3d x)
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
