using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

/// <summary>
/// G2. РАЗМЕТКА ГРАНИ В ЕЁ ПАРАМЕТРАХ: грань = точная поверхность + контуры в (u, v).
///
/// Контуры берутся точками рёбер (общими с соседними гранями, поэтому сетка тела замкнута),
/// переводятся в параметры поверхности и разворачиваются по периоду. Область грани в (u, v):
///  • все контуры замкнуты — наружный и отверстия;
///  • два контура обходят период (стенка отверстия, пояс тора) — многоугольник «нижний контур,
///    шов, верхний контур, шов»; шов делится заранее, и его точки на обеих сторонах совпадают.
/// Область триангулируется, затем внутренние рёбра делятся пополам в (u, v), пока точка
/// поверхности в середине ребра не ляжет на хорду с допуском. Граничные рёбра не делятся —
/// их точки общие с соседями. Грани иного устройства (полюс внутри, один контур через период)
/// не размечаются — возвращается null и причина.
/// </summary>
public static class UvMesher
{
    public static Action<string>? Debug = Environment.GetEnvironmentVariable("P3D_UVDBG") == "1" ? m => Console.Error.WriteLine("UV " + m) : null;
    /// <summary>KRN-03: сколько раз полюс шапки выбран по обходу края / не удалось решить.</summary>
    public static int PolePicksByLoop, PoleUndecided, PoleCutsMoved;
    /// <summary>KRN-03: разрез к полюсу от ближайшей к полюсу точки края (P3D_POLECUT=0 — от начала края, для мутации).</summary>
    /// <summary>KRN-03: треугольник с двумя вершинами в полюсе не считается «через шов» (P3D_POLESLIVER=0 — прежнее).</summary>
    internal static readonly bool PoleSliversAllowed = Environment.GetEnvironmentVariable("P3D_POLESLIVER") != "0";
    internal static readonly bool StereoMeshing = Environment.GetEnvironmentVariable("P3D_STEREOMESH") != "0";
    /// <summary>KRN-03 замер: сферических граней, размеченных в стереографической карте.</summary>
    public static int StereoFaces;
    internal static readonly bool FullPeriodic = Environment.GetEnvironmentVariable("P3D_FULLTORUS") != "0";
    public static int FullPeriodicFaces;
    internal static readonly bool DropPoleSlivers = Environment.GetEnvironmentVariable("P3D_POLEDROP") != "0";
    internal static readonly bool PoleCutFromNearest = Environment.GetEnvironmentVariable("P3D_POLECUT") != "0";
    internal static readonly bool StuckOff = Environment.GetEnvironmentVariable("P3D_UVSTUCK") == "0";
    internal static readonly bool StraightSquashOff = Environment.GetEnvironmentVariable("P3D_UVSTRAIGHT") == "0";
    internal static readonly bool DivergeOff = Environment.GetEnvironmentVariable("P3D_UVDIVERGE") == "0";
    internal static readonly bool UnsquashOff = Environment.GetEnvironmentVariable("P3D_UVUNSQUASH") == "0";
    static readonly bool RimGridOff = Environment.GetEnvironmentVariable("P3D_UVRIMGRID") == "0";
    /// <summary>Перемешивание ключа стороны (у long.GetHashCode = a ^ b — сплошные совпадения у соседних номеров).</summary>
    sealed class LongMix : IEqualityComparer<long>
    {
        public static readonly LongMix Instance = new();
        public bool Equals(long x, long y) => x == y;
        public int GetHashCode(long k) { ulong h = (ulong)k * 0x9E3779B97F4A7C15UL; return (int)(h >> 32) ^ (int)h; }
    }
    static readonly bool IncrDelOff = Environment.GetEnvironmentVariable("P3D_UVINCRDEL") == "0";
    static readonly bool LongKeysOff = Environment.GetEnvironmentVariable("P3D_UVLONGKEYS") == "0";
    static readonly bool BadMemoOff = Environment.GetEnvironmentVariable("P3D_UVBADMEMO") == "0";
    static readonly bool CurvMaxOff = Environment.GetEnvironmentVariable("P3D_UVCURVMAX") != "1";   // включение — P3D_UVCURVMAX=1 (k83: 4 проверки сеток не проходят, разобрать)
    static readonly bool StraightRowsOff = Environment.GetEnvironmentVariable("P3D_UVSTRAIGHTROWS") != "1";   // включение — P3D_UVSTRAIGHTROWS=1
    static readonly double TwoCurvScale = double.TryParse(Environment.GetEnvironmentVariable("P3D_UVTWOCURV"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tcs) ? tcs : 1.0;   // 0.85 и мельче — у тора d8_0091 стороны «в одну сторону» (разобрать: P3D_UVTWOCURV)
    static readonly bool RowLocOff = Environment.GetEnvironmentVariable("P3D_UVROWLOC") == "0";
    public static readonly double[] PhaseMs = new double[6];   // 0 контуры, 1 треугол., 2 Делоне, 3 внутр. точки, 4 доводка, 5 проверка

    /// <summary>
    /// Пояса вокруг периода (стенка цилиндра, кольцо тора) пока не включены: полосовая разметка
    /// даёт замкнутую сетку у простой стенки, но на стенке со вторым краем другой длины и на поясе
    /// с отверстиями (вал с поперечным отверстием) сетка тела не сходится. Такие грани остаются
    /// прежнему построителю по контурам.
    /// </summary>
    private static bool _gAllowBands = true;
    /// <summary>Поставка 2: значение контекста вычисления (KernelSettings.UvAllowBands), если он задан; иначе общее по умолчанию.</summary>
    public static bool AllowBands
    {
        get => Parametric3D.Kernel.KernelContext.Current is { } kc ? kc.Settings.UvAllowBands : _gAllowBands;
        set => Parametric3D.Kernel.KernelContext.SetGlobal(ref _gAllowBands, value, "UvMesher.AllowBands");
    }
    public static (List<Vector3d> P, List<int> T)? Mesh(TFace face, double tol, out string? why, int maxTriangles = 40000)
    {
        why = null;
        var s = GeometryAudit.AuthoritativeSurface(face);
        if (s == null) { why = "no exact surface"; return null; }
        // KRN-03: СФЕРИЧЕСКАЯ ГРАНЬ С КРАЕМ — В СВОЕЙ КОНФОРМНОЙ КАРТЕ (стереографическая проекция из полюса вне грани): сетка не
        // зависит от того, какой картой (осью) описана грань; у карты поверхности полюс внутри грани давал складки веера у
        // полюса (метрика параметров там вырождена). P3D_STEREOMESH=0 — прежняя разметка в параметрах поверхности.
        if (StereoMeshing && Geometry.ExactEdges.ShapeOf(s) is Geometry.Ssi.Sphere sph0 && SurfaceDomain.SpherePoleOutside(face, sph0.C, sph0.R) is { } stereoPole)
        { s = new StereoChart(sph0.C, sph0.R, stereoPole); StereoFaces++; }
        var (perU, perV) = Periods(s);
        Vector3d P(Vector2d q) => Eval(s, q, perU, perV);

        var swPh = System.Diagnostics.Stopwatch.StartNew(); string phName = "uv: контуры";
        void Ph(string next) { if (Diagnostics.Prof.Enabled) Diagnostics.Prof.Add(phName + " " + s.GetType().Name, (long)(swPh.Elapsed.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)); swPh.Restart(); phName = next; }
        // Контуры: (uv, xyz) по точкам рёбер, развёрнутые непрерывно.
        // Шов — ребро, которое грань проходит дважды (в обе стороны): в параметрах это граница
        // периода, а не край области; его полурёбра пропускаются, и контур делится на цепочки
        // между швами (у стенки цилиндра — две кромки).
        var uses = face.Loops.SelectMany(l => l.CoEdges).GroupBy(c => c.Edge).ToDictionary(g => g.Key, g => g.Count());
        var loops = new List<List<(Vector2d Uv, Vector3d X)>>();
        foreach (var loop in face.Loops)
        {
            var ces = loop.CoEdges.ToList();
            // Начать цепочку сразу после шва, чтобы кромка не разрывалась на две части.
            int first = ces.FindIndex(c => uses[c.Edge] > 1);
            if (first >= 0) ces = ces.Skip(first).Concat(ces.Take(first)).ToList();
            var pts = new List<(Vector2d, Vector3d)>();
            Vector2d? prev = null;
            void Flush() { if (pts.Count >= 2) loops.Add(pts); pts = new List<(Vector2d, Vector3d)>(); prev = null; }
            foreach (var ce in ces)
            {
                if (uses[ce.Edge] > 1) { Flush(); continue; }
                var ep = ExactFaceMesher.EdgePoints(ce.Edge).ToList();
                if (ce.Reversed) ep.Reverse();
                for (int i = pts.Count == 0 ? 0 : 1; i < ep.Count; i++)
                {
                    var uv = s.ProjectToUV(ep[i]);
                    if (prev is { } q) uv = Unwrap(uv, q, perU, perV);
                    pts.Add((uv, ep[i])); prev = uv;
                }
            }
            Flush();
        }
        // KRN-03 (03.8): ГРАНЬ — ВСЯ ДВОЯКОПЕРИОДИЧЕСКАЯ ПОВЕРХНОСТЬ (целый тор: края — одни швы). Область — квадрат периодов;
        // его противоположные стороны — одни и те же точки пространства, выборка сторон одинакова (одна и та же кривая —
        // одно и то же адаптивное деление), поэтому сетка замыкается сама. Прежде — отказ «no loops». P3D_FULLTORUS=0 — прежнее.
        List<(Vector2d Uv, Vector3d X)>? fullRect = null;
        if (loops.Count == 0 && perU && perV && FullPeriodic && face.Loops.SelectMany(l => l.CoEdges).Any())
        {
            (Vector2d, Vector3d) Pt(double u, double v) { var q = new Vector2d(u, v); return (q, P(q)); }
            var c00 = Pt(0, 0); var c10 = Pt(1, 0); var c11 = Pt(1, 1); var c01 = Pt(0, 1);
            fullRect = new List<(Vector2d Uv, Vector3d X)> { c00 };
            fullRect.AddRange(Seam(s, c00, c10, tol)); fullRect.Add(c10);
            fullRect.AddRange(Seam(s, c10, c11, tol)); fullRect.Add(c11);
            fullRect.AddRange(Seam(s, c11, c01, tol)); fullRect.Add(c01);
            fullRect.AddRange(Seam(s, c01, c00, tol));
            FullPeriodicFaces++;
        }
        if (loops.Count == 0 && fullRect == null) { why = "no loops"; return null; }
        if (Debug != null)
            foreach (var l in loops)
                for (int i = 0; i < l.Count; i++)
                {
                    double d = (P(l[i].Uv) - l[i].X).Length;
                    if (d > 1e-6 && s is RevolvedSurface) Debug($"loop point {i}/{l.Count}: |P(uv)-x| {d:E2} uv {l[i].Uv} x {l[i].X} proj {s.ProjectToUV(l[i].X)}");
                }

        // Замкнутость в параметрах.
        var closed = new List<List<(Vector2d Uv, Vector3d X)>>();
        var periodic = new List<List<(Vector2d Uv, Vector3d X)>>();
        foreach (var l in loops)
        {
            var d = l[^1].Uv - l[0].Uv;
            if (d.Length < 1e-7) { l.RemoveAt(l.Count - 1); closed.Add(l); }
            else if ((perU && Math.Abs(Math.Abs(d.X) - 1) < 1e-6 && Math.Abs(d.Y) < 1e-6) || (perV && Math.Abs(Math.Abs(d.Y) - 1) < 1e-6 && Math.Abs(d.X) < 1e-6))
                periodic.Add(l);
            else { why = $"a loop is not closed in the surface parameters ({d.Length:E1})"; return null; }
        }

        Debug?.Invoke($"classify {s.GetType().Name} per {perU}/{perV}: closed {closed.Count}, periodic {periodic.Count}, chains {loops.Count}");
        // Области для триангуляции: (наружный многоугольник, отверстия). У пояса — полосы между
        // разрезами поперёк пояса: ушная триангуляция всего пояса соединяла концы шва (точки одного
        // места в пространстве с разницей в целый период) и давала треугольники через весь цилиндр.
        var pieces = new List<(List<(Vector2d Uv, Vector3d X)> Outer, List<List<(Vector2d Uv, Vector3d X)>> Holes)>();
        var rims = new List<(List<(Vector2d Uv, Vector3d X)> Pts, bool Closed)>();     // настоящие края: для пометки границы
        Vector3d? polePt = null;   // KRN-03: точка полюса области (у шапки) — её вершины не «через шов»
        if (fullRect != null) { pieces.Add((fullRect, new())); rims.Add((fullRect, true)); }
        else if (periodic.Count == 0 && perU && perV && !TorusRestMeshOff && face.InteriorHint is { } hintR && closed.Count > 0
                 && closed.All(l => !InClosed(l, s.ProjectToUV(hintR))))
        {
            // g.05: ДВОЯКОПЕРИОДИЧЕСКАЯ ГРАНЬ БЕЗ ДИСКОВ (тор минус стягиваемые отверстия: точка грани вне всех контуров) —
            // квадрат периодов с отверстиями; квадрат сдвинут в промежутки между отверстиями по обоим параметрам, отверстия —
            // в квадрат. Прежде контур брался наружным, и сетка покрывала диск вместо дополнения. Мутация P3D_TORUSRESTMESH=0.
            double Gap(Func<Vector2d, double> c)
            {
                var iv = closed.Select(l => (Lo: l.Min(x => c(x.Uv)), Hi: l.Max(x => c(x.Uv)))).Select(t => (Lo: t.Lo - Math.Floor(t.Lo), Hi: t.Hi - Math.Floor(t.Lo))).ToList();
                // покрытые участки окружности [0,1): по отрезкам (возможно с переходом через 1)
                var marks = new List<(double A, double B)>();
                foreach (var (lo, hi) in iv) { if (hi <= 1) marks.Add((lo, hi)); else { marks.Add((lo, 1)); marks.Add((0, hi - 1)); } }
                marks.Sort((x, y) => x.A.CompareTo(y.A));
                double best = -1, at = 0, cur = 0;
                foreach (var (a, b) in marks) { if (a - cur > best) { best = a - cur; at = (a + cur) / 2; } cur = Math.Max(cur, b); }
                if (1 + (marks.Count > 0 ? marks[0].A : 0) - cur > best) { best = 1 + (marks.Count > 0 ? marks[0].A : 0) - cur; at = cur + best / 2; }
                return best > 1e-6 ? at : double.NaN;
            }
            double u0 = Gap(v => v.X), v0 = Gap(v => v.Y);
            if (double.IsNaN(u0) || double.IsNaN(v0)) { why = "holes leave no free period line on the doubly periodic face"; return null; }
            (Vector2d, Vector3d) Pt(double u, double v) { var q = new Vector2d(u, v); return (q, P(q)); }
            var c00 = Pt(u0, v0); var c10 = Pt(u0 + 1, v0); var c11 = Pt(u0 + 1, v0 + 1); var c01 = Pt(u0, v0 + 1);
            var rect = new List<(Vector2d Uv, Vector3d X)> { c00 };
            rect.AddRange(Seam(s, c00, c10, tol)); rect.Add(c10);
            rect.AddRange(Seam(s, c10, c11, tol)); rect.Add(c11);
            rect.AddRange(Seam(s, c11, c01, tol)); rect.Add(c01);
            rect.AddRange(Seam(s, c01, c00, tol));
            var holesR = closed.Select(l =>
            {
                double cu = l.Average(x => x.Uv.X), cv = l.Average(x => x.Uv.Y);
                double su = Math.Floor(cu - u0), sv = Math.Floor(cv - v0);
                return l.Select(x => (new Vector2d(x.Uv.X - su, x.Uv.Y - sv), x.X)).ToList();
            }).ToList();
            // g.05: обход квадрата — обратный обходу отверстий (у грани с обратной стороной отверстия идут против часовой):
            // прежде квадрат всегда против часовой — у вырезанного тора (брусок минус тор, серия 8, случай 79) сетка
            // смотрела внутрь тела, объём сетки 42 871 вместо 39 560. Мутация P3D_RESTORIENT=0.
            pieces.Add((rect, holesR));
            rims.Add((rect, true)); rims.AddRange(holesR.Select(h => (h, true)));
            System.Threading.Interlocked.Increment(ref TorusRestFaces);
        }
        else if (periodic.Count == 0)
        {
            if (closed.Count == 0) { why = "no loops"; return null; }
            var outer = closed.OrderByDescending(l => Math.Abs(Area(l.Select(x => x.Uv).ToList()))).First();
            var inner = closed.Where(l => l != outer).ToList();
            // Отверстие, касающееся наружного контура в вершине (карман у края), вклеивается в
            // наружный контур в этой вершине: мост ушной триангуляции к такому отверстию
            // накрывал его (площадь сетки 20 228 вместо 15 111 мм²).
            var merged = new List<(Vector2d Uv, Vector3d X)>(outer);
            for (bool again = true; again;)
            {
                again = false;
                foreach (var h in inner.ToList())
                {
                    int io = -1, ih = -1;
                    for (int i = 0; i < merged.Count && io < 0; i++)
                        for (int j = 0; j < h.Count; j++)
                            if ((merged[i].Uv - h[j].Uv).Length < 1e-9) { io = i; ih = j; break; }
                    if (io < 0) continue;
                    var spliced = new List<(Vector2d Uv, Vector3d X)>();
                    spliced.AddRange(merged.Take(io + 1));
                    for (int k = 1; k <= h.Count; k++) spliced.Add(h[(ih + k) % h.Count]);   // по отверстию до той же вершины
                    spliced.AddRange(merged.Skip(io + 1));
                    merged = spliced; inner.Remove(h); again = true;
                }
            }
            pieces.Add((merged, inner));
            rims.AddRange(closed.Select(l => (l, true)));
        }
        else if (periodic.Count == 2 && !AllowBands)
        {
            why = "a band around the period (cylinder wall, torus ring) — meshed by the loop mesher for now";
            return null;
        }
        else if (periodic.Count == 2)
        {
            bool alongU = perU && Math.Abs(Math.Abs((periodic[0][^1].Uv - periodic[0][0].Uv).X) - 1) < 1e-6;
            // g.05: ДВОЯКОПЕРИОДИЧЕСКАЯ ПОВЕРХНОСТЬ (тор): пояс между двумя параллельными петлями — по одну из двух сторон;
            // сторона — та, где точка грани (InteriorHint): вторая петля и отверстия сдвигаются на целый период поперёк пояса
            // так, чтобы точка грани лежала между петлями. Прежде бралась прямая разность параметров — у внешнего пояса тора
            // (через шов образующей) сетка ложилась на дополнение: площадь 22 987 вместо 827 мм². Мутация P3D_TORUSBANDMESH=0.
            if (perU && perV && face.InteriorHint is { } hint && !TorusBandMeshOff)
            {
                double Cx(Vector2d v) => alongU ? v.Y : v.X;
                Vector2d Sx(Vector2d v, double d) => alongU ? new Vector2d(v.X, v.Y + d) : new Vector2d(v.X + d, v.Y);
                double ca = periodic[0].Average(x => Cx(x.Uv)), cb = periodic[1].Average(x => Cx(x.Uv));
                var huv = s.ProjectToUV(hint);
                double ch = Cx(huv);
                // g.05: положение петель — В ТОЧКЕ пересечения с линией через точку грани, а не среднее: у волнистых петель
                // (конус под углом к тору) точка грани лежит в размахе петли, и среднее ставило её не по ту сторону
                // (KRN05-BOOLEAN-IDENTITIES #40). Мутация P3D_BANDPARITY=0.
                if (!BandParityOff)
                {
                    double Al(Vector2d v) => alongU ? v.X : v.Y;
                    double? At(List<(Vector2d Uv, Vector3d X)> lp)
                    {
                        double t = Al(huv);
                        double mn = lp.Min(x => Al(x.Uv)); t -= Math.Floor(t - mn);
                        for (int i = 0; i + 1 < lp.Count; i++)
                        {
                            double a0 = Al(lp[i].Uv), a1 = Al(lp[i + 1].Uv);
                            if ((a0 - t) * (a1 - t) <= 0 && a0 != a1) { double u = (t - a0) / (a1 - a0); return Cx(lp[i].Uv) + (Cx(lp[i + 1].Uv) - Cx(lp[i].Uv)) * u; }
                        }
                        return null;
                    }
                    if (At(periodic[0]) is { } xa && At(periodic[1]) is { } xb) { ca = xa; cb = xb; }
                }
                ch += Math.Round(ca - ch);                          // подъём точки грани к первой петле
                if (Math.Abs(ch - ca) < 1e-12) ch += 1e-9;
                // вторая петля — сдвиг на целый период: точка грани между петлями, ширина пояса меньше периода
                double bestK = double.NaN, bestW = double.MaxValue;
                for (int k = -3; k <= 3; k++)
                {
                    double cbx = cb + k, lo0 = Math.Min(ca, cbx), hi0 = Math.Max(ca, cbx);
                    double chj = ch + Math.Ceiling(lo0 - ch);              // подъём точки грани в [lo0, lo0 + 1)
                    if (hi0 - lo0 >= 1 || !(chj > lo0 && chj < hi0)) continue;
                    if (hi0 - lo0 < bestW) { bestW = hi0 - lo0; bestK = k; }
                }
                double cbk2 = double.IsNaN(bestK) ? cb : cb + bestK;
                double shiftB = cbk2 - cb; double cbk = cbk2;
                if (Math.Abs(shiftB) > 0.5) periodic[1] = periodic[1].Select(x => (Sx(x.Uv, Math.Round(shiftB)), x.X)).ToList();
                double lo = Math.Min(ca, cbk), hi = Math.Max(ca, cbk);
                for (int hi2 = 0; hi2 < closed.Count; hi2++)
                {
                    double ch2 = closed[hi2].Average(x => Cx(x.Uv)); double sh = 0;
                    while (ch2 + sh < lo) sh += 1; while (ch2 + sh > hi) sh -= 1;
                    if (sh != 0) closed[hi2] = closed[hi2].Select(x => (Sx(x.Uv, sh), x.X)).ToList();
                }
            }
            var strips = Strips(s, periodic[0], periodic[1], closed, alongU, tol, out var rimA, out var rimB, out var shiftedHoles);
            if (strips == null) { why = "the band could not be cut into strips around its holes"; return null; }
            pieces.AddRange(strips);
            rims.Add((rimA, false)); rims.Add((rimB, false)); rims.AddRange(shiftedHoles.Select(h => (h, true)));
        }
        else if (periodic.Count == 1 && PoleDomain(s, periodic[0], closed, perU, perV, tol, out var rimUsed, face) is { } pd)
        {
            // Грань с полюсом (чаша шара, конец конуса): край, шов к полюсу, линия полюса, шов назад.
            pieces.Add((pd, closed));
            polePt = pd.GroupBy(x => x.X).Where(g => g.Count() > 2).Select(g => (Vector3d?)g.Key).FirstOrDefault();
            if (Debug != null) Debug("poledomain " + string.Join(";", pd.Select(x => $"{x.Uv.X:R},{x.Uv.Y:R},{x.X.X:R},{x.X.Y:R},{x.X.Z:R}")));
            rims.Add((rimUsed, false)); rims.AddRange(closed.Select(l => (l, true)));
        }
        else { why = $"{periodic.Count} loop(s) go round the period and no pole closes the domain"; return null; }

        // Триангуляция областей; общие точки (разрезы) сводятся по (u, v); точкам границы — их точки рёбер.
        var key = new Dictionary<(double, double), Vector3d>();
        foreach (var (po, ph) in pieces) foreach (var x in po.Concat(ph.SelectMany(h => h))) key[(x.Uv.X, x.Uv.Y)] = x.X;
        Ph("uv: триангуляция");
        var uvList = new List<Vector2d>(); var index = new Dictionary<(double, double), int>();
        var tris = new List<int>();
        foreach (var (po, ph) in pieces)
        {
            var tr = Triangulator.Triangulate(po.Select(x => x.Uv).ToList(), ph.Select(h => (IReadOnlyList<Vector2d>)h.Select(x => x.Uv).ToList()).ToList(), out var uvs);
            if (tr.Count == 0) { why = "the parameter-space triangulation failed"; return null; }
            var map = uvs.Select(u => { var k = (u.X, u.Y); if (!index.TryGetValue(k, out int id)) { id = uvList.Count; uvList.Add(u); index[k] = id; } return id; }).ToList();
            tris.AddRange(tr.Select(i => map[i]));
        }
        var xyz = uvList.Select(u => key.TryGetValue((u.X, u.Y), out var p) ? p : P(u)).ToList();
        var boundary = new HashSet<(int, int)>();

        void Mark(List<(Vector2d Uv, Vector3d X)> l, bool closedRim)
        {
            var idx = l.Select(x => index.TryGetValue((x.Uv.X, x.Uv.Y), out int id) ? id : -1).ToList();
            for (int i = 0; i + 1 < idx.Count + (closedRim ? 1 : 0); i++) { int a = idx[i], b = idx[(i + 1) % idx.Count]; if (a >= 0 && b >= 0) { var e = a < b ? (a, b) : (b, a); boundary.Add(e); } }
        }
        foreach (var r in rims) Mark(r.Pts, r.Closed);
        Debug?.Invoke($"rims {rims.Count}: points {rims.Sum(r => r.Pts.Count)}, missing {rims.Sum(r => r.Pts.Count(x => !index.ContainsKey((x.Uv.X, x.Uv.Y))))}, boundary edges {boundary.Count}, tris {tris.Count / 3}");

        // Деление внутренних рёбер, пока середина ребра на поверхности не ляжет на хорду.
        // Между проходами — перевороты внутренних рёбер к более короткой диагонали в пространстве:
        // ушная триангуляция пояса даёт длинные диагонали через весь цилиндр, и одно деление
        // пополам без переворотов плодило десятки тысяч игл.
        var T = new List<int>(tris);
        double Orient(int a, int b, int c) { var p = uvList[a]; var q = uvList[b]; var r = uvList[c]; return (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X); }
        double Area3() { double ar = 0; for (int t = 0; t < T.Count; t += 3) ar += Vector3d.Cross(xyz[T[t + 1]] - xyz[T[t]], xyz[T[t + 2]] - xyz[T[t]]).Length / 2; return ar; }

        Ph("uv: Делоне и доводка");
        double delMs0 = 0; System.Diagnostics.Stopwatch? swRef = null;
        // G2. ОГРАНИЧЕННАЯ ТРИАНГУЛЯЦИЯ ДЕЛОНЕ В МЕТРИКЕ ПОВЕРХНОСТИ.
        // Начало — ушная триангуляция области (она верна, но с длинными тонкими треугольниками);
        // затем перевороты внутренних рёбер к Делоне в масштабированных параметрах (u·|Su|, v·|Sv|)
        // — края области не трогаются; затем вставка точек в центры «плохих» треугольников с
        // повторными переворотами. Вставка в центр не создаёт перекрытий, поэтому деление всегда
        // сходится (прежнее деление рёбер пополам в поясе цилиндра не сходилось).
        var mid0 = uvList.Aggregate(Vector2d.Zero, (acc, q) => acc + q) / uvList.Count;
        double hU = 1e-4, su = (P(mid0 + new Vector2d(hU, 0)) - P(mid0 - new Vector2d(hU, 0))).Length / (2 * hU);
        double sv = (P(mid0 + new Vector2d(0, hU)) - P(mid0 - new Vector2d(0, hU))).Length / (2 * hU);
        if (s is PlaneSurface || su < 1e-12 || sv < 1e-12) { su = 1; sv = 1; }
        Vector2d M(int i) => new(uvList[i].X * su, uvList[i].Y * sv);
        bool InCircle(int a, int b, int c, int d)
        {
            Vector2d A = M(a) - M(d), B = M(b) - M(d), C = M(c) - M(d);
            double det = (A.X * A.X + A.Y * A.Y) * (B.X * C.Y - C.X * B.Y) - (B.X * B.X + B.Y * B.Y) * (A.X * C.Y - C.X * A.Y) + (C.X * C.X + C.Y * C.Y) * (A.X * B.Y - B.X * A.Y);
            double o = (M(b).X - M(a).X) * (M(c).Y - M(a).Y) - (M(b).Y - M(a).Y) * (M(c).X - M(a).X);
            return o > 0 ? det > 1e-12 * Math.Abs(det + 1) : det < -1e-12 * Math.Abs(det - 1);
        }
        HashSet<long>? boundaryL = null;
        void Delaunay(List<int>? touched = null)
        {
            var swD = System.Diagnostics.Stopwatch.StartNew();
            try { DelaunayCore(touched); } finally { PhaseMs[2] += swD.Elapsed.TotalMilliseconds; }
        }
        void DelaunayCore(List<int>? touched)
        {
            // Перевороты Лоусона по очереди рёбер с соседством треугольников: каждое ребро
            // проверяется, только когда меняется его окрестность. Прежде на каждом круге заново
            // строилась карта всех рёбер (до 200 кругов) — это была основная стоимость разметки.
            int nt = T.Count / 3;
            var nbr = new int[T.Count];
            // 23:50 (скорость): ключ стороны — одно число (long), а не пара: словарь сторон строится на каждом проходе доводки
            // (тор: 15 проходов по 19 000 треугольников), край — тем же ключом. Порядок и решения те же. Мутация P3D_UVLONGKEYS=0.
            if (!LongKeysOff)
            {
                static long K(int a, int b) => ((long)a << 32) | (uint)b;
                var edgeAtL = new Dictionary<long, int>(T.Count, LongMix.Instance);
                for (int t = 0; t < T.Count; t += 3)
                    for (int k = 0; k < 3; k++) edgeAtL[K(T[t + k], T[t + (k + 1) % 3])] = t + k;
                for (int t = 0; t < T.Count; t += 3)
                    for (int k = 0; k < 3; k++)
                        nbr[t + k] = edgeAtL.TryGetValue(K(T[t + (k + 1) % 3], T[t + k]), out int o) ? o / 3 * 3 : -1;
                if (boundaryL == null || boundaryL.Count != boundary.Count) { boundaryL = new HashSet<long>(boundary.Count, LongMix.Instance); foreach (var (ba, bb) in boundary) boundaryL.Add(K(ba, bb)); }
            }
            else
            {
            var edgeAt = new Dictionary<(int, int), int>(T.Count);
            for (int t = 0; t < T.Count; t += 3)
                for (int k = 0; k < 3; k++) edgeAt[(T[t + k], T[t + (k + 1) % 3])] = t + k;
            for (int t = 0; t < T.Count; t += 3)
                for (int k = 0; k < 3; k++)
                    nbr[t + k] = edgeAt.TryGetValue((T[t + (k + 1) % 3], T[t + k]), out int o) ? o / 3 * 3 : -1;
            }
            int EdgeIndex(int tri, int u, int v) { for (int k = 0; k < 3; k++) if (T[tri + k] == u && T[tri + (k + 1) % 3] == v) return k; return -1; }
            void Repoint(int tri, int u, int v, int newNbr) { if (tri < 0) return; int k = EdgeIndex(tri, u, v); if (k >= 0) nbr[tri + k] = newNbr; }
            var stack = new Stack<(int T, int A, int B)>();
            // 23:50 (скорость): после вставки центров — только стороны изменённых треугольников: прочие сошлись к Делоне на прошлом
            // проходе и не менялись (их четвёрки точек те же — перевороты там невозможны). Мутация P3D_UVINCRDEL=0 — все стороны.
            if (touched != null && !IncrDelOff)
            {
                foreach (int t in touched)
                    for (int k = 0; k < 3; k++) if (nbr[t + k] >= 0) stack.Push((t, T[t + k], T[t + (k + 1) % 3]));
            }
            else
            for (int t = 0; t < T.Count; t += 3)
                for (int k = 0; k < 3; k++) if (nbr[t + k] >= 0) stack.Push((t, T[t + k], T[t + (k + 1) % 3]));
            int guard = 0, limit = 50 * T.Count + 1000;
            while (stack.Count > 0 && guard++ < limit)
            {
                var (t, a, b) = stack.Pop();
                int k = EdgeIndex(t, a, b);
                if (k < 0) continue;                                   // треугольник уже изменился
                if (boundaryL != null ? boundaryL.Contains(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a) : boundary.Contains(a < b ? (a, b) : (b, a))) continue;
                int n = nbr[t + k];
                if (n < 0) continue;
                int kn = EdgeIndex(n, b, a);
                if (kn < 0) continue;
                int c = T[t + (k + 2) % 3], d = T[n + (kn + 2) % 3];
                if (!InCircle(a, b, c, d)) continue;
                double sign = Math.Sign(Orient(a, b, c));
                if (sign == 0 || Math.Sign(Orient(a, d, c)) != sign || Math.Sign(Orient(d, b, c)) != sign) continue;
                if (Math.Abs(Orient(a, d, c)) < 1e-18 || Math.Abs(Orient(d, b, c)) < 1e-18) continue;
                int tbc = nbr[t + (k + 1) % 3], tca = nbr[t + (k + 2) % 3];
                int nad = nbr[n + (kn + 1) % 3], ndb = nbr[n + (kn + 2) % 3];
                // t' = (a, d, c), n' = (d, b, c).
                T[t] = a; T[t + 1] = d; T[t + 2] = c;
                T[n] = d; T[n + 1] = b; T[n + 2] = c;
                nbr[t] = nad; nbr[t + 1] = n; nbr[t + 2] = tca;
                nbr[n] = ndb; nbr[n + 1] = tbc; nbr[n + 2] = t;
                Repoint(nad, d, a, t);
                Repoint(tbc, c, b, n);
                Repoint(tca, a, c, t);
                Repoint(ndb, b, d, n);
                stack.Push((t, a, d)); stack.Push((t, c, a)); stack.Push((n, d, b)); stack.Push((n, b, c));
            }
        }
        // Допуск не меньше хордовой ошибки самой границы (её точки общие с соседями).
        double bTol = 0;
        foreach (var (a, b) in boundary) bTol = Math.Max(bTol, (P((uvList[a] + uvList[b]) / 2) - (xyz[a] + xyz[b]) / 2).Length);
        tol = Math.Max(tol, bTol * 1.25);
        Debug?.Invoke($"consistency: max |P(uv)-x| on boundary {onRimDbg():E2}, su {su:0.###} sv {sv:0.###}, tol {tol:0.####}, pts {uvList.Count}, tris {T.Count / 3}");
        double onRimDbg() { double m = 0; int w = -1; foreach (var (a, b) in boundary) { double d = (P(uvList[a]) - xyz[a]).Length; if (d > m) { m = d; w = a; } } if (w >= 0 && m > 1e-9) Debug?.Invoke($"worst boundary point #{w}/{uvList.Count}: uv {uvList[w]} x {xyz[w]} P(uv) {P(uvList[w])}"); return m; }
        Delaunay();
        var onRim = new HashSet<int>(boundary.SelectMany(e => new[] { e.Item1, e.Item2 }));
        // 20:17 (скорость): ОЦЕНКА ТРЕУГОЛЬНИКА — ИЗ ПАМЯТИ ПО ТРОЙКЕ ВЕРШИН. Она зависит только от его вершин (точки, край
        // неизменны), а доводка пересчитывала её у всех треугольников на каждом проходе (4 точки поверхности на треугольник).
        // Мутация P3D_UVBADMEMO=0.
        var badMemo = new Dictionary<(int, int, int), double>();
        double Bad(int t)
        {
            if (BadMemoOff) return BadCore(t);
            int a0 = T[t], a1 = T[t + 1], a2 = T[t + 2];
            if (a0 > a1) (a0, a1) = (a1, a0); if (a1 > a2) (a1, a2) = (a2, a1); if (a0 > a1) (a0, a1) = (a1, a0);
            ref double slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(badMemo, (a0, a1, a2), out bool known);
            if (!known) slot = BadCore(t);
            return slot;
        }
        double BadCore(int t)
        {
            int i0 = T[t], i1 = T[t + 1], i2 = T[t + 2];
            // «Ухо» на краю (все три вершины — точки края, одна сторона — край): его не улучшить,
            // не меняя точек края; вставка в его центр шла бесконечно (у скругления ребра).
            if (onRim.Contains(i0) && onRim.Contains(i1) && onRim.Contains(i2))
            {
                bool hasRimSide = new[] { (i0, i1), (i1, i2), (i2, i0) }.Any(q => boundary.Contains(q.Item1 < q.Item2 ? q : (q.Item2, q.Item1)));
                if (hasRimSide && Math.Abs(Orient(i0, i1, i2)) < 1e-6 * (su * sv > 0 ? 1 : 1) * Math.Max(1e-12, (uvList[i0] - uvList[i1]).LengthSquared + (uvList[i1] - uvList[i2]).LengthSquared)) return 0;
            }
            double w = (P((uvList[i0] + uvList[i1] + uvList[i2]) / 3) - (xyz[i0] + xyz[i1] + xyz[i2]) / 3).Length;
            foreach (var (x, y) in new[] { (i0, i1), (i1, i2), (i2, i0) })
            {
                var e = x < y ? (x, y) : (y, x);
                if (!boundary.Contains(e)) w = Math.Max(w, (P((uvList[x] + uvList[y]) / 2) - (xyz[x] + xyz[y]) / 2).Length);
            }
            return w;
        }
        // Внутренние точки — правильная сетка в метрике поверхности с шагом точек края (их шаг уже
        // подобран по хордовому допуску), не ближе полушага к краю. Каждая вставляется в свой
        // треугольник, затем — перевороты к Делоне. Вставка в центры «плохих» треугольников
        // плодила иглы у края (эффект «фонаря Шварца»: площадь сетки росла выше точной).
        if (!s.IsPlanar)
        {
            var rimEdges = boundary.ToList();
            var segLen = rimEdges.Select(e => (M(e.Item1) - M(e.Item2)).Length).Where(x => x > 1e-12).OrderBy(x => x).ToList();
            double h = segLen.Count > 0 ? segLen[segLen.Count / 2] : 1;
            // Шаг по хордовому допуску поверхности: прогиб на шаге h не больше допуска.
            var pts0 = Enumerable.Range(0, uvList.Count).Select(M).ToList();
            double minX = pts0.Min(q => q.X), maxX = pts0.Max(q => q.X), minY = pts0.Min(q => q.Y), maxY = pts0.Max(q => q.Y);
            // Шаг по каждому направлению — от кривизны поверхности вдоль него: у стенки цилиндра
            // вдоль образующей кривизны нет, и точки там не нужны. Прежде шаг был одинаковым по
            // обоим направлениям (шаг края): цилиндр с высотой в несколько окружностей получал в
            // 30 раз больше треугольников, чем нужно (тест последовательностей A01).
            double CurvAt(Vector2d c0, Vector2d dir)
            {
                double d = 1e-3;
                var p0 = P(c0); var p1 = P(c0 + dir * d); var p2 = P(c0 - dir * d);
                double first = (p1 - p2).Length / (2 * d);
                if (first < 1e-12) return 0;
                double k = ((p1 + p2 - 2 * p0) / (d * d)).Length / (first * first);
                // шум разностной второй производной (~1e-9 1/мм у образующей цилиндра) — не кривизна: радиус больше 10 км — прямая
                if (!CurvMaxOff && k < 1e-7) return 0;
                return double.IsFinite(k) ? k : 0;
            }
            var cMid = (uvList.Aggregate(Vector2d.Zero, (acc, q) => acc + q)) / uvList.Count;
            // 23:50 (скорость): КРИВИЗНА — НАИБОЛЬШАЯ ПО ГРАНИ, шаг — по отходу центра треугольника (k·s²/6, а не середины стороны
            // k·s²/8). Прежде кривизна бралась в одной точке (середина параметров), а у тора она вдоль кольца у внутренней стороны
            // в 1.7 раза больше: правильная сетка выходила грубой, и доводка центрами шла 15 проходов (19 000 треугольников,
            // 0.2–0.4 с на грань). Пробы — середина и до 32 точек края. Мутация P3D_UVCURVMAX=0 — прежнее.
            double Curv(Vector2d dir)
            {
                double k = CurvAt(cMid, dir);
                if (CurvMaxOff) return k;
                int stepB = Math.Max(1, uvList.Count / 32);
                // пробы — на 0.9 пути от середины к точкам края: сама точка края лежит на краю области параметров (NURBS там
                // зажимает параметр — излом, «кривизна» 800 у плоского лоскута), а выпуклая смесь двух точек области — внутри
                for (int i = 0; i < uvList.Count; i += stepB) k = Math.Max(k, CurvAt(cMid + (uvList[i] - cMid) * 0.9, dir));
                return k;
            }
            double hx = h, hy = h;
            double kx = Curv(new Vector2d(1, 0)), ky = Curv(new Vector2d(0, 1));
            double spanX = maxX - minX, spanY = maxY - minY;
            double cf = CurvMaxOff ? 8 : 6;
            hx = kx > 1e-12 ? Math.Max(h, Math.Sqrt(cf * tol / kx)) : Math.Max(h, spanX);
            hy = ky > 1e-12 ? Math.Max(h, Math.Sqrt(cf * tol / ky)) : Math.Max(h, spanY);
            // обе стороны гнутся (тор, шар): отходы по двум направлениям складываются на косой стороне — шаги мельче
            if (!CurvMaxOff && TwoCurvScale < 1 && kx > 1e-12 && ky > 1e-12) { double sh = TwoCurvScale; hx = Math.Max(h, hx * sh); hy = Math.Max(h, hy * sh); }
            // 23:50 (скорость): ПРЯМОЕ НАПРАВЛЕНИЕ — СТРОКИ НЕ РЕЖЕ 4 ШАГОВ КРИВОГО, если грань не «лесенка» (полоса скругления с
            // точками края только на концах — там нужна именно лесенка). Прежде шаг вдоль образующей был во всю грань: одна
            // строка точек посередине, а у стенки цилиндра с вырезом посередине — ни одной; доводка центрами шла 14 проходов
            // (объединение двух цилиндров: 3 грани по 40–120 мс). Мутация P3D_UVSTRAIGHTROWS=0.
            if (!StraightRowsOff)
            {
                bool LadderPre(bool alongY)
                {
                    if (pieces.Any(pc => pc.Item2.Count > 0)) return false;
                    double lo = alongY ? minY : minX, hi = alongY ? maxY : maxX, eps = 1e-6 * (hi - lo);
                    foreach (var (a2, b2) in boundary)
                        foreach (int i in new[] { a2, b2 })
                        {
                            double c = alongY ? M(i).Y : M(i).X;
                            if (Math.Abs(c - lo) > eps && Math.Abs(c - hi) > eps) return false;
                        }
                    return true;
                }
                if (kx > 1e-12 && ky <= 1e-12 && hy > 4 * hx && !LadderPre(alongY: true)) hy = Math.Max(h, 4 * hx);
                else if (ky > 1e-12 && kx <= 1e-12 && hx > 4 * hy && !LadderPre(alongY: false)) hx = Math.Max(h, 4 * hy);
            }
            // Сетка не реже точек края по кривому направлению, не чаще бюджета.
            int budget = Math.Max(0, maxTriangles / 3);
            while (spanX * spanY / (hx * hy) > budget) { hx *= 1.5; hy *= 1.5; }
            h = Math.Min(hx, hy);
            bool InTri(Vector2d q, int t)
            {
                Vector2d A = M(T[t]), B = M(T[t + 1]), C = M(T[t + 2]);
                double d1 = (B.X - A.X) * (q.Y - A.Y) - (B.Y - A.Y) * (q.X - A.X), d2 = (C.X - B.X) * (q.Y - B.Y) - (C.Y - B.Y) * (q.X - B.X), d3 = (A.X - C.X) * (q.Y - C.Y) - (A.Y - C.Y) * (q.X - C.X);
                return (d1 > 0 && d2 > 0 && d3 > 0) || (d1 < 0 && d2 < 0 && d3 < 0);
            }
            double DistSeg(Vector2d q, Vector2d a2, Vector2d b2) { var ab = b2 - a2; double tt = Math.Clamp(Vector2d.Dot(q - a2, ab) / Math.Max(ab.LengthSquared, 1e-300), 0, 1); return (a2 + ab * tt - q).Length; }
            // Поиск треугольника для точки: сначала около предыдущей вставленной (точки идут рядами,
            // соседняя почти всегда в соседнем треугольнике), иначе — перебором.
            var inc = new Dictionary<int, List<int>>();
            void Inc(int v, int t) { if (!inc.TryGetValue(v, out var l)) inc[v] = l = new List<int>(); l.Add(t); }
            for (int t = 0; t < T.Count; t += 3) { Inc(T[t], t); Inc(T[t + 1], t); Inc(T[t + 2], t); }
            // 20:17 (скорость): ПОИСК ТРЕУГОЛЬНИКА ТОЧКИ — ПО ОТРЕЗКАМ СТРОКИ. Точки правильной сетки идут строками; на строке y
            // треугольники дают непересекающиеся отрезки по x (строятся раз на строку, по возрастанию). Вставка точки делит
            // треугольник на три — его отрезок заменяется отрезками частей. Треугольник, строго содержащий точку, один — ответ
            // тот же, что у перебора. Прежде точка вне области (вырез шара) перебирала все тысячи треугольников: 40–60 мс на
            // грань шара. Мутация P3D_UVROWLOC=0.
            List<(double X0, double X1, int T)>? rowIdx = null; double rowY = 0; int rowPos = -1;
            bool Span(int t, double y, out double x0, out double x1)
            {
                Vector2d A = M(T[t]), B = M(T[t + 1]), C = M(T[t + 2]);
                x0 = double.MaxValue; x1 = double.MinValue;
                if (y < Math.Min(A.Y, Math.Min(B.Y, C.Y)) || y > Math.Max(A.Y, Math.Max(B.Y, C.Y))) return false;
                void Side(Vector2d P1, Vector2d P2, ref double a0, ref double a1)
                {
                    double ylo = Math.Min(P1.Y, P2.Y), yhi = Math.Max(P1.Y, P2.Y);
                    if (y < ylo || y > yhi) return;
                    if (yhi - ylo < 1e-300) { a0 = Math.Min(a0, Math.Min(P1.X, P2.X)); a1 = Math.Max(a1, Math.Max(P1.X, P2.X)); return; }
                    double xx = P1.X + (P2.X - P1.X) * (y - P1.Y) / (P2.Y - P1.Y);
                    a0 = Math.Min(a0, xx); a1 = Math.Max(a1, xx);
                }
                Side(A, B, ref x0, ref x1); Side(B, C, ref x0, ref x1); Side(C, A, ref x0, ref x1);
                return x0 <= x1;
            }
            void BuildRow(double y)
            {
                rowIdx ??= new List<(double, double, int)>(); rowIdx.Clear(); rowY = y;
                for (int t = 0; t < T.Count; t += 3) if (Span(t, y, out var x0, out var x1)) rowIdx.Add((x0, x1, t));
                rowIdx.Sort((a, b) => a.X0.CompareTo(b.X0));
            }
            int RowLocate(Vector2d q)
            {
                var ri = rowIdx!; rowPos = -1;
                int lo2 = 0, hi2 = ri.Count - 1, pos = -1;
                while (lo2 <= hi2) { int md = (lo2 + hi2) / 2; if (ri[md].X0 <= q.X) { pos = md; lo2 = md + 1; } else hi2 = md - 1; }
                // отрезки соседних треугольников касаются концами; содержащий точку строго — среди ближайших
                for (int j = Math.Max(0, pos - 3); j <= Math.Min(ri.Count - 1, pos + 1); j++)
                    if (InTri(q, ri[j].T)) { rowPos = j; return ri[j].T; }
                // запасной путь (вырожденные отрезки): перебор строки
                for (int j = 0; j < ri.Count; j++) if (InTri(q, ri[j].T)) { rowPos = j; return ri[j].T; }
                return -1;
            }
            void RowSplit(int tri, int t2, int t3)
            {
                if (rowIdx == null || rowPos < 0) return;
                var parts = new List<(double, double, int)>(3);
                foreach (int t in new[] { tri, t2, t3 }) if (Span(t, rowY, out var x0, out var x1)) parts.Add((x0, x1, t));
                parts.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                rowIdx.RemoveAt(rowPos); rowIdx.InsertRange(rowPos, parts);
            }
            int last = -1;
            int Locate(Vector2d q)
            {
                if (rowIdx != null) return RowLocate(q);
                if (last >= 0 && inc.TryGetValue(last, out var l0))
                {
                    var seen = new HashSet<int>();
                    foreach (int t in l0) { if (seen.Add(t) && InTri(q, t)) return t; }
                    foreach (int t in l0.ToList())
                        for (int k = 0; k < 3; k++)
                            if (inc.TryGetValue(T[t + k], out var l1))
                                foreach (int t2 in l1) if (seen.Add(t2) && InTri(q, t2)) return t2;
                }
                for (int t = 0; t < T.Count; t += 3) if (InTri(q, t)) return t;
                return -1;
            }
            Ph("uv: внутренние точки");
            // 20:17 (скорость): БЛИЗОСТЬ К КРАЮ — ПО СЕТКЕ ЯЧЕЕК. Прежде каждая точка правильной сетки сверялась со ВСЕМИ сторонами
            // края (шар с вырезом: тысячи точек × сотни сторон — 30–40 мс из 70 на грань). Ячейка — не меньше порога: сторона ближе
            // порога к точке задевает ячейку точки или соседнюю; сторона записана во все ячейки своей коробки. Ответ тот же.
            // Мутация P3D_UVRIMGRID=0.
            double nearThr = 0.5 * Math.Min(hx, hy);
            Dictionary<(long, long), List<int>>? rimGrid = null; double cell = nearThr;
            if (!RimGridOff && nearThr > 0 && rimEdges.Count > 16)
            {
                double sx = 0, sy = 0; foreach (var e in rimEdges) { var d = M(e.Item1) - M(e.Item2); sx = Math.Max(sx, Math.Abs(d.X)); sy = Math.Max(sy, Math.Abs(d.Y)); }
                cell = Math.Max(nearThr, Math.Max(sx, sy) * 0.5);   // ячейка не мельче половины самой длинной стороны: сторона — в немногих ячейках
                if (cell > 0 && (maxX - minX) / cell < 4096 && (maxY - minY) / cell < 4096)
                {
                    rimGrid = new Dictionary<(long, long), List<int>>();
                    for (int ei = 0; ei < rimEdges.Count; ei++)
                    {
                        var a2 = M(rimEdges[ei].Item1); var b2 = M(rimEdges[ei].Item2);
                        long x0 = (long)Math.Floor(Math.Min(a2.X, b2.X) / cell), x1 = (long)Math.Floor(Math.Max(a2.X, b2.X) / cell);
                        long y0 = (long)Math.Floor(Math.Min(a2.Y, b2.Y) / cell), y1 = (long)Math.Floor(Math.Max(a2.Y, b2.Y) / cell);
                        for (long gx = x0; gx <= x1; gx++)
                            for (long gy = y0; gy <= y1; gy++)
                            {
                                if (!rimGrid.TryGetValue((gx, gy), out var l)) rimGrid[(gx, gy)] = l = new List<int>();
                                l.Add(ei);
                            }
                    }
                }
            }
            bool NearRim(Vector2d q)
            {
                if (rimGrid == null) return rimEdges.Any(e => DistSeg(q, M(e.Item1), M(e.Item2)) < nearThr);
                long cx = (long)Math.Floor(q.X / cell), cy = (long)Math.Floor(q.Y / cell);
                for (long gx = cx - 1; gx <= cx + 1; gx++)
                    for (long gy = cy - 1; gy <= cy + 1; gy++)
                        if (rimGrid.TryGetValue((gx, gy), out var l))
                            foreach (int ei in l) if (DistSeg(q, M(rimEdges[ei].Item1), M(rimEdges[ei].Item2)) < nearThr) return true;
                return false;
            }
            int row = 0;
            for (double y = minY + hy / 2; y < maxY; y += hy * (hx == hy ? 0.8660254 : 1), row++)
            {
                if (!RowLocOff && T.Count / 3 > 64) BuildRow(y); else rowIdx = null;
                for (double x = minX + (row % 2 == 0 ? hx / 2 : hx); x < maxX; x += hx)
                {
                    var q = new Vector2d(x, y);
                    int tri = Locate(q);
                    if (tri < 0) continue;                                   // вне области
                    if (NearRim(q)) continue;
                    var uvq = new Vector2d(x / su, y / sv);
                    int m = uvList.Count; uvList.Add(uvq); xyz.Add(P(uvq));
                    int i0 = T[tri], i1 = T[tri + 1], i2 = T[tri + 2];
                    int t2 = T.Count, t3 = T.Count + 3;
                    T[tri] = i0; T[tri + 1] = i1; T[tri + 2] = m;
                    T.AddRange(new[] { i1, i2, m, i2, i0, m });
                    RowSplit(tri, t2, t3);
                    inc[i2].Remove(tri);
                    Inc(i0, t3); Inc(i1, t2); Inc(i2, t2); Inc(i2, t3); Inc(m, tri); Inc(m, t2); Inc(m, t3);
                    last = m;
                }
            }
            // u08: ПРЯМОЕ НАПРАВЛЕНИЕ — СЖАТО В МЕТРИКЕ ДЕЛОНЕ. Длинная узкая полоса (скругление ребра 100 мм радиусом
            // 1.4 мм): в метрике поверхности Делоне соединял точки дуги на одном конце с далёкими по углу точками другого
            // конца, такие стороны отходили от поверхности, и доводка центрами плодила 1 258 треугольников вместо 32 (и
            // 100–1 400 мс на одно скругление — ручка операции «думала» секунду). Вдоль образующей поверхность не гнётся,
            // длина треугольника там не стоит ничего: сжатие этого направления до доли шага кривого даёт «лесенку» по
            // одинаковому углу. Мутация P3D_UVSTRAIGHT=0 — прежнее.
            // Только «лесенка»: область без отверстий, и все точки края — на двух концах полосы по прямому направлению (у
            // скругления — дуги концов, стороны — образующие). Иначе сжатая метрика вела точки кривого края (кромка
            // отверстия, срез у шва) к далёким по углу: объём −0.2 % (G2-UV-MESH), точный маршрут → многогранный
            // (KRN04-REFINE-AT-CLOSED-EDGE-SEAM).
            bool Ladder(bool alongY)
            {
                if (pieces.Any(pc => pc.Item2.Count > 0)) return false;
                double lo = alongY ? minY : minX, hi = alongY ? maxY : maxX, eps = 1e-6 * (hi - lo);
                foreach (var (a2, b2) in boundary)
                    foreach (int i in new[] { a2, b2 })
                    {
                        double c = alongY ? M(i).Y : M(i).X;
                        if (Math.Abs(c - lo) > eps && Math.Abs(c - hi) > eps) return false;
                    }
                return true;
            }
            double su0 = su, sv0 = sv; bool squashed = false;
            if (!StraightSquashOff && spanX > 1e-12 && spanY > 1e-12)
            {
                if (kx > 1e-12 && ky <= 1e-12 && spanY > 4 * hx && Ladder(alongY: true)) { sv *= 0.25 * hx / spanY; squashed = true; }
                else if (ky > 1e-12 && kx <= 1e-12 && spanX > 4 * hy && Ladder(alongY: false)) { su *= 0.25 * hy / spanX; squashed = true; }
            }
            Ph("uv: Делоне после точек");
            Delaunay();
            Debug?.Invoke($"pass 0: interior points {uvList.Count - pts0.Count}, h {h:0.###} hx {hx:0.###} hy {hy:0.###} kx {kx:G3} ky {ky:G3} span {spanX:0.##}x{spanY:0.##}, tris {T.Count / 3}, area3 {Area3():0.###}");
            // Доводка: где поверхность гнётся сильнее, чем позволяет шаг сетки (кольцо тора малого
            // сечения), — точки в центры «плохих» треугольников и снова Делоне.
            Ph("uv: доводка");
            delMs0 = PhaseMs[2]; swRef = System.Diagnostics.Stopwatch.StartNew();
            double prevWorst = double.MaxValue; int stuck = 0; int tris0 = Math.Max(1, T.Count / 3);
            // u12: снимок перед вставкой центров — если доводка разошлась (худшая ошибка выросла втрое: косые полосы у
            // скошенного края цилиндра, ошибка 0.04 → 1.7 мм), сетка возвращается к снимку. Мутация P3D_UVDIVERGE=0.
            List<int>? snapT = null; int snapN = 0; double snapWorst = double.MaxValue; double lastWorst = double.NaN;
            for (int pass = 1; pass < 25 && T.Count / 3 < maxTriangles; pass++)
            {
                var bad = new List<int>();
                double worstNow = 0;
                for (int t = 0; t < T.Count; t += 3) { double bt = Bad(t); if (bt > tol) { bad.Add(t); if (bt > worstNow) worstNow = bt; } }
                // u10: ДОВОДКА БЕЗ УЛУЧШЕНИЯ — СТОП. Худший треугольник у вырожденного места (тор-веретено на углу скругления
                // радиусом больше угла, параметр у полюса) центрами не исправляется: ошибка 1.34 при допуске 0.02 оставалась
                // на всех 25 проходах, а сетка росла до 5 000+ треугольников (4 с на грань). Два прохода подряд без
                // уменьшения худшей ошибки хотя бы на 2 % — конец доводки (дальше решает самопроверка разметки).
                // Мутация P3D_UVSTUCK=0.
                lastWorst = bad.Count == 0 ? 0 : worstNow;
                if (!StuckOff && bad.Count > 0)
                {
                    // только если сетка уже выросла в 25+ раз: у обычной доводки худшая ошибка (у полюса) может стоять, пока остальные
                    // треугольники ещё улучшаются (G8-CROSS-HOLE-UV-ALL-FACES)
                    if (worstNow > 0.98 * prevWorst) { if (++stuck >= 3 && T.Count / 3 > 25 * tris0) { Debug?.Invoke($"pass {pass}: no progress (worst {worstNow:0.####}) — refinement stopped"); break; } }
                    else stuck = 0;
                    prevWorst = Math.Min(prevWorst, worstNow);
                }
                Debug?.Invoke($"pass {pass}: bad {bad.Count} of {T.Count / 3}, worst {worstNow:0.####} tol {tol:0.####}");
                // u12: «ЛЕСЕНКА» НЕ ЛЕГЛА — назад к метрике поверхности. Перекошенная полоса (концы с разным числом точек, скошенный
                // конец: цилиндр скругления, срезанный лентой переменного радиуса) давала в сжатой метрике косые иглы; доводка
                // центрами там расходилась (ошибка 0.02 → 1.7 мм, «рваный» край и гранение на экране). Мутация P3D_UVUNSQUASH=0.
                if (squashed && bad.Count > 0 && !UnsquashOff)
                {
                    squashed = false; su = su0; sv = sv0;
                    Delaunay();
                    Debug?.Invoke("ladder squash undone: refining in the surface metric");
                    continue;
                }
                if (Debug != null && Environment.GetEnvironmentVariable("P3D_UVDUMP") is { } dumpDir)
                {
                    // разбор: область и сетка в параметрах (точки, треугольники, стороны края) — по проходам
                    var sbd = new System.Text.StringBuilder();
                    sbd.Append("P ").AppendJoin(' ', uvList.Select(q => $"{q.X * su:R},{q.Y * sv:R}")).Append('\n');
                    sbd.Append("T ").AppendJoin(' ', T).Append('\n');
                    sbd.Append("B ").AppendJoin(' ', boundary.Select(e => $"{e.Item1},{e.Item2}")).Append('\n');
                    sbd.Append("X ").AppendJoin(' ', bad).Append('\n');
                    System.IO.Directory.CreateDirectory(dumpDir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dumpDir, $"{s.GetType().Name}_{face.GetHashCode():x}_p{pass}.txt"), sbd.ToString());
                }
                if (bad.Count == 0) break;
                if (snapT == null || worstNow < snapWorst) { snapT = new List<int>(T); snapN = uvList.Count; snapWorst = worstNow; }
                var touchedT = new List<int>(bad.Count * 3);
                foreach (int t in bad)
                {
                    int i0 = T[t], i1 = T[t + 1], i2 = T[t + 2];
                    var c = (uvList[i0] + uvList[i1] + uvList[i2]) / 3;
                    int m = uvList.Count; uvList.Add(c); xyz.Add(P(c));
                    T[t] = i0; T[t + 1] = i1; T[t + 2] = m;
                    touchedT.Add(t); touchedT.Add(T.Count); touchedT.Add(T.Count + 3);
                    T.AddRange(new[] { i1, i2, m, i2, i0, m });
                }
                Delaunay(touchedT);
            }
            // доводка кончилась без сходимости (стоп «нет улучшения», предел проходов или треугольников), а лучшая сетка
            // была раньше — возврат к ней
            if (!DivergeOff && snapT != null && T.Count / 3 > 0)
            {
                double fin = 0; for (int t = 0; t < T.Count; t += 3) { double bt = Bad(t); if (bt > tol && bt > fin) fin = bt; }
                if (fin > snapWorst * 1.5)
                {
                    T.Clear(); T.AddRange(snapT);
                    uvList.RemoveRange(snapN, uvList.Count - snapN); xyz.RemoveRange(snapN, xyz.Count - snapN); badMemo.Clear();
                    Debug?.Invoke($"refinement did not converge (worst {fin:0.####}, best {snapWorst:0.####}) — best mesh kept");
                }
            }
        }

        // Вырожденные треугольники (три точки на одной прямой в параметрах — у стенки цилиндра это
        // точки одной образующей) удаляются переворотом их длинной стороны: в пространстве они
        // нулевой площади, сварка сетки их выбрасывала, и на их месте оставались непарные стороны
        // (50 таких у стенки бобышки — сетка тела открывалась в ядре).
        if (Debug != null && !s.IsPlanar) Debug($"refine timing: total {swRef?.Elapsed.TotalMilliseconds:0.0} ms, delaunay {PhaseMs[2] - delMs0:0.0} ms, tris {T.Count / 3}");
        Ph("uv: вырожденные");
        for (int round = 0; round < 50; round++)
        {
            double scaleUv = 0; for (int i = 0; i < uvList.Count; i++) scaleUv = Math.Max(scaleUv, uvList[i].LengthSquared);
            double tinyO = 1e-12 * Math.Max(scaleUv, 1e-300);
            var et = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < T.Count; t += 3)
                for (int k = 0; k < 3; k++) { int a = T[t + k], b = T[t + (k + 1) % 3]; var e = a < b ? (a, b) : (b, a); if (!et.TryGetValue(e, out var l)) et[e] = l = new List<int>(); l.Add(t); }
            int fixedCount = 0; var touched = new HashSet<int>();
            for (int t = 0; t < T.Count; t += 3)
            {
                if (touched.Contains(t)) continue;
                {
                    // Вырожден — в параметрах или в пространстве (точки одной образующей цилиндра,
                    // чуть разошедшиеся по u: в (u, v) площадь не нулевая, а в пространстве — нулевая).
                    var A3 = xyz[T[t]]; var B3 = xyz[T[t + 1]]; var C3 = xyz[T[t + 2]];
                    double longest = Math.Max((A3 - B3).LengthSquared, Math.Max((B3 - C3).LengthSquared, (C3 - A3).LengthSquared));
                    bool flat3 = Vector3d.Cross(B3 - A3, C3 - A3).Length <= 1e-9 * longest;
                    if (Math.Abs(Orient(T[t], T[t + 1], T[t + 2])) > tinyO && !flat3) continue;
                }
                // Длинная сторона — против точки, лежащей между двумя другими.
                int bestK = 0; double bl = -1;
                for (int k = 0; k < 3; k++) { double len = (xyz[T[t + k]] - xyz[T[t + (k + 1) % 3]]).LengthSquared; if (len > bl) { bl = len; bestK = k; } }
                int a0 = T[t + bestK], b0 = T[t + (bestK + 1) % 3], c0 = T[t + (bestK + 2) % 3];
                var e0 = a0 < b0 ? (a0, b0) : (b0, a0);
                if (boundary.Contains(e0) || !et.TryGetValue(e0, out var lt) || lt.Count != 2) continue;
                int t2 = lt[0] == t ? lt[1] : lt[0];
                if (touched.Contains(t2)) continue;
                int d0 = T[t2] + T[t2 + 1] + T[t2 + 2] - a0 - b0;
                // (a0, b0, c0) вырожден, c0 между a0 и b0; соседний (b0, a0, d0) делится на (a0, c0, d0) и (c0, b0, d0).
                double s2 = Math.Sign(Orient(T[t2], T[t2 + 1], T[t2 + 2]));
                int k2 = T[t2] == d0 ? 0 : T[t2 + 1] == d0 ? 1 : 2;
                int x = T[t2 + (k2 + 1) % 3], y = T[t2 + (k2 + 2) % 3];       // t2 = (x, y, d0) по обходу
                double o1 = Orient(x, c0, d0), o2 = Orient(c0, y, d0);
                if (Math.Sign(o1) != s2 || Math.Sign(o2) != s2) continue;
                if (et.ContainsKey(c0 < d0 ? (c0, d0) : (d0, c0))) continue;     // такое ребро уже есть — было бы неманифолдно
                T[t] = x; T[t + 1] = c0; T[t + 2] = d0;
                T[t2] = c0; T[t2 + 1] = y; T[t2 + 2] = d0;
                touched.Add(t); touched.Add(t2); fixedCount++;
            }
            if (fixedCount == 0) break;
        }

        Ph("uv: самопроверка");
        // САМОПРОВЕРКА. Разметка принимается, только если (1) треугольники покрывают область в
        // (u, v) ровно — сумма их площадей равна площади области, и все обходятся в одну сторону;
        // (2) площадь сетки сходится с точной площадью грани. Иначе — отказ с причиной: ушная
        // триангуляция плоской грани с многими отверстиями, касающимися края, накрывала отверстия
        // (площадь 20 228 вместо 15 111 мм²), и такая сетка не должна уходить дальше.
        {
            double domain = pieces.Sum(pc => Math.Abs(Area(pc.Outer.Select(x => x.Uv).ToList())) - pc.Holes.Sum(h => Math.Abs(Area(h.Select(x => x.Uv).ToList()))));
            double cover = 0; int pos = 0, neg = 0;
            double tiny = 1e-9 * Math.Max(domain, 1e-300);
            for (int t = 0; t < T.Count; t += 3) { double o2 = Orient(T[t], T[t + 1], T[t + 2]); cover += Math.Abs(o2) / 2; if (o2 > tiny) pos++; else if (o2 < -tiny) neg++; }
            if (Math.Abs(cover - domain) > 1e-6 * Math.Max(domain, 1e-12) || (pos > 0 && neg > 0))
            { why = $"the parameter-space triangles do not cover the face domain exactly ({cover:G6} vs {domain:G6}, orientations {pos}/{neg}, {s.GetType().Name}, pieces {pieces.Count}, holes {pieces.Sum(pc => pc.Holes.Count)})"; return null; }
            // Треугольник через полпериода по замкнутому параметру соединяет точки по разные стороны шва
            // (в пространстве они рядом, в параметрах — через всю ленту): после сварки такие стороны
            // обходились бы дважды в одну сторону (стенка вала с глухим отверстием).
            if (perU || perV)
                for (int t = 0; t < T.Count; t += 3)
                {
                    var a0 = uvList[T[t]]; var b0 = uvList[T[t + 1]]; var c0 = uvList[T[t + 2]];
                    double spanU = Math.Max(a0.X, Math.Max(b0.X, c0.X)) - Math.Min(a0.X, Math.Min(b0.X, c0.X));
                    double spanV = Math.Max(a0.Y, Math.Max(b0.Y, c0.Y)) - Math.Min(a0.Y, Math.Min(b0.Y, c0.Y));
                    // KRN-03: треугольник с двумя вершинами на линии полюса (одна точка пространства) сварка убирает — его
                    // размах по периоду не соединяет разные стороны шва (шапка сферы между дугами: отказ, сетка донора со складками).
                    if (PoleSliversAllowed && polePt is { } pp)
                    {
                        // Вершина в полюсе — любой угол: размах считается по прочим вершинам.
                        var rest = new[] { T[t], T[t + 1], T[t + 2] }.Where(i => xyz[i] != pp).Select(i => uvList[i]).ToList();
                        if (rest.Count < 3)
                        {
                            spanU = rest.Count < 2 ? 0 : Math.Abs(rest[0].X - rest[1].X);
                            spanV = rest.Count < 2 ? 0 : Math.Abs(rest[0].Y - rest[1].Y);
                        }
                    }
                    if ((perU && spanU > 0.5) || (perV && spanV > 0.5)) { Debug?.Invoke($"seam span: uv {a0} {b0} {c0} xyz {xyz[T[t]]} {xyz[T[t + 1]]} {xyz[T[t + 2]]}"); why = "a triangle spans half the period across the seam"; return null; }
                }
            // Треугольники нулевой площади в пространстве сварка сетки выбросит, и на их месте
            // останутся непарные стороны: такая разметка не принимается.
            int flat = 0;
            for (int t = 0; t < T.Count; t += 3)
            {
                var A3 = xyz[T[t]]; var B3 = xyz[T[t + 1]]; var C3 = xyz[T[t + 2]];
                double longest = Math.Max((A3 - B3).LengthSquared, Math.Max((B3 - C3).LengthSquared, (C3 - A3).LengthSquared));
                // Две совпавшие вершины (линия полюса, концы шва) — такой треугольник сварка убирает
                // чисто; опасен только треугольник из трёх РАЗНЫХ точек на одной прямой.
                double shortest = Math.Min((A3 - B3).LengthSquared, Math.Min((B3 - C3).LengthSquared, (C3 - A3).LengthSquared));
                if (shortest > 1e-24 && Vector3d.Cross(B3 - A3, C3 - A3).Length <= 1e-9 * longest) flat++;
            }
            if (flat > 0) { why = $"{flat} triangle(s) of zero area in space remain"; return null; }
            double? exact; using (Diagnostics.Prof.Time("uv: самопроверка — точная площадь " + s.GetType().Name)) exact = AnalyticVolume.FaceAreaRough(face);
            if (exact is { } ex && Math.Abs(ex) > 1e-9)
            {
                double a3 = 0; for (int t = 0; t < T.Count; t += 3) a3 += Vector3d.Cross(xyz[T[t + 1]] - xyz[T[t]], xyz[T[t + 2]] - xyz[T[t]]).Length / 2;
                double perim = 0; foreach (var (pa, pb) in boundary) perim += (xyz[pa] - xyz[pb]).Length;
                if (fullRect != null) perim = 0;      // стороны квадрата периодов — не края грани (общих точек с соседями нет)
                if (Math.Abs(a3 - Math.Abs(ex)) > 5e-3 * Math.Abs(ex) + 2 * bTol * perim)
                { why = $"the mesh area {a3:0.###} differs from the exact face area {Math.Abs(ex):0.###}"; return null; }
            }
        }
        // Сторона: по треугольникам гранения самой грани (у них сторона верная), а если их нет —
        // по нормали грани. Нормаль гранёной грани берёт знак по средней нормали её треугольников,
        // а у полной стенки цилиндра средняя ≈ 0, и знак выходил случайным (стенки отверстий
        // вывернуты, найдено объёмом).
        var refTris = face.Surface is FacetedSurface fsr && fsr.Facets.Count > 0 ? fsr.Facets.ToList() : null;
        Vector3d RefNormal(Vector3d q)
        {
            if (refTris == null) return face.NormalAt(q);
            var bestT = refTris[0]; double bd = double.MaxValue;
            foreach (var t in refTris) { double d = ((t.A + t.B + t.C) / 3 - q).LengthSquared; if (d < bd) { bd = d; bestT = t; } }
            return Vector3d.Cross(bestT.B - bestT.A, bestT.C - bestT.A);
        }
        double vote = 0;
        int stride = Math.Max(3, (T.Count / 3 / 64) * 3);
        for (int t = 0; t < T.Count; t += stride)
        {
            var nt = Vector3d.Cross(xyz[T[t + 1]] - xyz[T[t]], xyz[T[t + 2]] - xyz[T[t]]);
            var nr = RefNormal((xyz[T[t]] + xyz[T[t + 1]] + xyz[T[t + 2]]) / 3);
            if (nt.LengthSquared > 0 && nr.LengthSquared > 0) vote += Math.Sign(Vector3d.Dot(nt, nr));
        }
        if (vote < 0) for (int t = 0; t < T.Count; t += 3) (T[t + 1], T[t + 2]) = (T[t + 2], T[t + 1]);
        // KRN-03: ТРЕУГОЛЬНИКИ С ДВУМЯ ВЕРШИНАМИ В ОДНОЙ ТОЧКЕ ПРОСТРАНСТВА (линия полюса — одна точка) убираются здесь, а не
        // «при сварке»: сварки на этом пути нет, и они оставались в гранях — нулевые стороны, обходимые в одну сторону
        // (шапка сферы с полюсом карты внутри грани: 48 таких треугольников, 6 ошибок ориентации). P3D_POLEDROP=0 — прежнее.
        if (DropPoleSlivers)
        {
            var kept = new List<int>(T.Count);
            for (int t = 0; t < T.Count; t += 3)
            {
                var A3 = xyz[T[t]]; var B3 = xyz[T[t + 1]]; var C3 = xyz[T[t + 2]];
                if (A3 == B3 || B3 == C3 || C3 == A3) continue;
                kept.Add(T[t]); kept.Add(T[t + 1]); kept.Add(T[t + 2]);
            }
            T = kept;
        }
        return (xyz, T);
    }

    private static Vector2d Unwrap(Vector2d uv, Vector2d prev, bool perU, bool perV)
    {
        if (perU) { while (uv.X - prev.X > 0.5) uv.X -= 1; while (uv.X - prev.X < -0.5) uv.X += 1; }
        if (perV) { while (uv.Y - prev.Y > 0.5) uv.Y -= 1; while (uv.Y - prev.Y < -0.5) uv.Y += 1; }
        return uv;
    }

    /// <summary>
    /// Замкнута ли поверхность по u или v (край 0 совпадает с краем 1 по всей длине): цилиндр,
    /// тело вращения на полный оборот. Прежде проверялся сдвиг на 1 за пределы области; у
    /// поверхностей, которые зажимают параметр в [0, 1], это давало «не периодична», и контур
    /// кольца тора прыгал с v ≈ 1 на v ≈ 0 (сетка грани перекрывалась у кромки).
    /// </summary>
    private static (bool U, bool V) Periods(ISurface3 s)
    {
        if (s is PlaneSurface) return (false, false);
        try
        {
            bool Closed(Func<double, Vector2d> a, Func<double, Vector2d> b) =>
                new[] { 0.13, 0.41, 0.77 }.All(t => (s.PointAt(a(t)) - s.PointAt(b(t))).Length < 1e-9 * Math.Max(1, s.PointAt(a(t)).Length));
            return (Closed(t => new Vector2d(0, t), t => new Vector2d(1, t)), Closed(t => new Vector2d(t, 0), t => new Vector2d(t, 1)));
        }
        catch { return (false, false); }
    }

    /// <summary>Точка поверхности по развёрнутым параметрам: периодические сводятся в [0, 1).</summary>
    private static Vector3d Eval(ISurface3 s, Vector2d uv, bool perU, bool perV)
    {
        if (perU) uv.X -= Math.Floor(uv.X);
        if (perV) uv.Y -= Math.Floor(uv.Y);
        return s.PointAt(uv);
    }

    private static double Area(List<Vector2d> p)
    {
        double a = 0; for (int i = 0; i < p.Count; i++) a += p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y;
        return a / 2;
    }

    /// <summary>
    /// Пояс между двумя контурами, обходящими период, — полосами. A — один край (по возрастанию
    /// или убыванию периодической координаты), B — другой, развёрнутый так, чтобы шёл от конца A
    /// к началу A. Разрезы поперёк пояса — каждые ~1/16 периода, в обход отверстий; точки разреза
    /// общие для соседних полос. Отверстия сдвигаются на целый период в развёртку пояса.
    /// </summary>
    /// <summary>
    /// Край A пояса, повёрнутый так, чтобы шов (его начало) не проходил через отверстие. Без пересечения — тот же список.
    /// A — замкнутая цепочка: последняя точка — первая, сдвинутая на период.
    /// </summary>
    private static List<(Vector2d Uv, Vector3d X)> SeamAwayFromHoles(List<(Vector2d Uv, Vector3d X)> a,
        List<List<(Vector2d Uv, Vector3d X)>> holes, bool alongU)
    {
        if (holes.Count == 0 || a.Count < 3) return a;
        double C(Vector2d v) => alongU ? v.X : v.Y;
        // Отверстия по кругу периода: центр и полуширина (контур отверстия в развёртке непрерывен).
        var spans = holes.Select(h => { double lo = h.Min(x => C(x.Uv)), hi = h.Max(x => C(x.Uv)); return ((lo + hi) / 2, (hi - lo) / 2); }).ToList();
        double Dist(double u) => spans.Min(sp => { double d = u - sp.Item1; d -= Math.Round(d); return Math.Abs(d) - sp.Item2; });
        if (Dist(C(a[0].Uv)) > 1e-9) return a;                     // шов не задевает отверстий — как прежде
        int n = a.Count, best = 0; double bestD = double.MinValue;
        for (int i = 1; i < n - 1; i++) { double d = Dist(C(a[i].Uv)); if (d > bestD) { bestD = d; best = i; } }
        if (bestD <= 1e-9) return a;                               // места без отверстий нет — как прежде
        var period = a[^1].Uv - a[0].Uv;                            // сдвиг на один обход
        var res = new List<(Vector2d Uv, Vector3d X)>(n);
        for (int i = best; i < n; i++) res.Add(a[i]);
        for (int i = 1; i <= best; i++) res.Add((a[i].Uv + period, a[i].X));
        return res;
    }

    static readonly bool TorusRestMeshOff = Environment.GetEnvironmentVariable("P3D_TORUSRESTMESH") == "0";
    public static long TorusRestFaces;
    /// <summary>Точка внутри замкнутого контура в параметрах (подъём точки к контуру по обоим периодам).</summary>
    static bool InClosed(List<(Vector2d Uv, Vector3d X)> l, Vector2d q)
    {
        double cu = l.Average(x => x.Uv.X), cv = l.Average(x => x.Uv.Y);
        q = new Vector2d(cu + Math.IEEERemainder(q.X - cu, 1), cv + Math.IEEERemainder(q.Y - cv, 1));
        bool inside = false;
        for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
        {
            var a = l[i].Uv; var b = l[j].Uv;
            if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
    static readonly bool TorusBandMeshOff = Environment.GetEnvironmentVariable("P3D_TORUSBANDMESH") == "0";
    static readonly bool BandParityOff = Environment.GetEnvironmentVariable("P3D_BANDPARITY") == "0";
    private static List<(List<(Vector2d Uv, Vector3d X)>, List<List<(Vector2d Uv, Vector3d X)>>)>? Strips(
        ISurface3 s, List<(Vector2d Uv, Vector3d X)> a, List<(Vector2d Uv, Vector3d X)> b, List<List<(Vector2d Uv, Vector3d X)>> holes,
        bool alongU, double tol, out List<(Vector2d Uv, Vector3d X)> rimA, out List<(Vector2d Uv, Vector3d X)> rimB,
        out List<List<(Vector2d Uv, Vector3d X)>> shiftedHoles)
    {
        double C(Vector2d v) => alongU ? v.X : v.Y;
        Vector2d Shift(Vector2d v, double d) => alongU ? new Vector2d(v.X + d, v.Y) : new Vector2d(v.X, v.Y + d);
        // ПОСТАВКА 4: ШОВ ПОЛОСЫ — В СТОРОНЕ ОТ ОТВЕРСТИЙ. Шов ставился в начале края A и с отверстиями не сверялся:
        // у вала с поперечным отверстием кромка лежала прямо на шве (диапазон [−0.032, 0.032] при окне [0, 1]),
        // половина отверстия выходила за полосу — треугольники покрывали лишнее, 54 выворачивались, и грань уходила к
        // прежнему построителю (сетка грубее). Как в ExactFaceMesher: если отверстие пересекает шов, начало A — в
        // точке, наиболее удалённой от отверстий по кругу периода; иначе всё как прежде.
        a = SeamAwayFromHoles(a, holes, alongU);
        double dirA = Math.Sign(C(a[^1].Uv) - C(a[0].Uv));
        double dirB = Math.Sign(C(b[^1].Uv) - C(b[0].Uv));
        if (dirA == dirB) { b = b.ToList(); b.Reverse(); }
        b = b.Take(b.Count - 1).ToList();
        double endA = C(a[^1].Uv);
        int start = 0; double best = double.MaxValue;
        for (int i = 0; i < b.Count; i++) { double d = C(b[i].Uv) - endA; d -= Math.Round(d); if (Math.Abs(d) < best) { best = Math.Abs(d); start = i; } }
        var bb = new List<(Vector2d Uv, Vector3d X)>();
        Vector2d? prev = null;
        for (int k = 0; k <= b.Count; k++)
        {
            var (uv, x) = b[(start + k) % b.Count];
            if (prev == null) uv = Shift(uv, Math.Round(endA - C(uv)));
            else uv = Unwrap(uv, prev.Value, alongU, !alongU);
            bb.Add((uv, x)); prev = uv;
        }
        rimA = a; rimB = bb;
        // Отверстия — в развёртку пояса.
        double lo = Math.Min(C(a[0].Uv), C(a[^1].Uv)), hi = Math.Max(C(a[0].Uv), C(a[^1].Uv));
        shiftedHoles = new List<List<(Vector2d Uv, Vector3d X)>>();
        var ranges = new List<(double Lo, double Hi)>();
        foreach (var h in holes)
        {
            double c = h.Average(x => C(x.Uv));
            double sh = 0; while (c + sh < lo) sh += 1; while (c + sh > hi) sh -= 1;
            var hs = h.Select(x => (Shift(x.Uv, sh), x.X)).ToList();
            shiftedHoles.Add(hs);
            ranges.Add((hs.Min(x => C(x.Item1)), hs.Max(x => C(x.Item1))));
        }
        bool Free(double u) => ranges.All(r => u < r.Lo - 1e-9 || u > r.Hi + 1e-9);
        // Индексы разрезов на A (0 и последний — шов) и соответствующие на B (B идёт от конца A к началу).
        int nA = a.Count, nB = bb.Count;
        int K = 1;   // одна полоса: длинные треугольники разворачивает триангуляция Делоне
        var cutsA = new List<int> { 0 };
        for (int k = 1; k < K; k++)
        {
            int i = (int)Math.Round((double)k * (nA - 1) / K);
            if (i <= cutsA[^1] || i >= nA - 1) continue;
            if (!Free(C(a[i].Uv))) continue;
            cutsA.Add(i);
        }
        cutsA.Add(nA - 1);
        var cutsB = new List<int>();
        foreach (int i in cutsA)
        {
            double u = C(a[i].Uv);
            int j = 0; double bd = double.MaxValue;
            for (int q = 0; q < nB; q++) { double d = Math.Abs(C(bb[q].Uv) - u); if (d < bd) { bd = d; j = q; } }
            cutsB.Add(j);
        }
        cutsB[0] = nB - 1; cutsB[^1] = 0;                  // шов: конец B у начала A, начало B у конца A
        for (int k = 1; k < cutsB.Count; k++) if (cutsB[k] > cutsB[k - 1]) return null;   // не монотонно
        foreach (int j in cutsB.Skip(1).Take(cutsB.Count - 2)) if (!Free(C(bb[j].Uv))) return null;
        // Точки разрезов — один раз на разрез.
        var cuts = cutsA.Select((i, k) => Seam(s, a[i], bb[cutsB[k]], tol).ToList()).ToList();
        // Два края шва — одна линия пространства: точки второго — те же точки первого, сдвинутые
        // на период в параметрах. Прежде их считали отдельно, и они расходились в последних
        // знаках; в сетке ядра шов оставался открытым (108 сторон без пары у стенки бобышки).
        var shift = a[^1].Uv - a[0].Uv;
        cuts[^1] = cuts[0].Select(x => (x.Item1 + shift, x.Item2)).ToList();
        var res = new List<(List<(Vector2d Uv, Vector3d X)>, List<List<(Vector2d Uv, Vector3d X)>>)>();
        for (int k = 0; k + 1 < cutsA.Count; k++)
        {
            var poly = new List<(Vector2d Uv, Vector3d X)>();
            for (int i = cutsA[k]; i <= cutsA[k + 1]; i++) poly.Add(a[i]);
            poly.AddRange(cuts[k + 1].Select(x => (x.Item1, x.Item2)));
            for (int j = cutsB[k + 1]; j <= cutsB[k]; j++) poly.Add(bb[j]);
            var back = cuts[k].Select(x => (x.Item1, x.Item2)).ToList(); back.Reverse();
            poly.AddRange(back);
            double u0 = Math.Min(C(a[cutsA[k]].Uv), C(a[cutsA[k + 1]].Uv)), u1 = Math.Max(C(a[cutsA[k]].Uv), C(a[cutsA[k + 1]].Uv));
            var hs = shiftedHoles.Where(h => { double c = h.Average(x => C(x.Uv)); return c >= u0 && c <= u1; }).ToList();
            res.Add((poly, hs));
            Debug?.Invoke($"strip {k}: A {cutsA[k]}..{cutsA[k + 1]} B {cutsB[k + 1]}..{cutsB[k]} area {Area(poly.Select(x => x.Uv).ToList()):0.######} n {poly.Count} holes {hs.Count}");
        }
        Debug?.Invoke($"strips: A {nA} pts {C(a[0].Uv):0.####}→{C(a[^1].Uv):0.####}, B {nB} pts {C(bb[0].Uv):0.####}→{C(bb[^1].Uv):0.####}, other coord A {(alongU ? a[0].Uv.Y : a[0].Uv.X):0.####} B {(alongU ? bb[0].Uv.Y : bb[0].Uv.X):0.####}");
        return res;
    }

    /// <summary>
    /// Область грани с полюсом: край обходит период по одной координате, а по другой с одной
    /// стороны поверхность стягивается в точку (полюс). Многоугольник: край, шов к линии полюса,
    /// линия полюса (все её точки — одна точка пространства), шов назад. Отверстия — внутри.
    /// </summary>
    private static List<(Vector2d Uv, Vector3d X)>? PoleDomain(ISurface3 s, List<(Vector2d Uv, Vector3d X)> rim,
        List<List<(Vector2d Uv, Vector3d X)>> holes, bool perU, bool perV, double tol, out List<(Vector2d Uv, Vector3d X)> rimUsed, TFace? face = null)
    {
        rimUsed = rim;
        var d = rim[^1].Uv - rim[0].Uv;
        bool alongU = perU && Math.Abs(Math.Abs(d.X) - 1) < 1e-6;
        // Полюс — край области по другой координате, где поверхность — одна точка.
        Vector3d At(double other, double along) => s.PointAt(alongU ? new Vector2d(along - Math.Floor(along), other) : new Vector2d(other, along - Math.Floor(along)));
        bool IsPole(double other) { var p0 = At(other, 0.1); return new[] { 0.37, 0.64, 0.91 }.All(a => (At(other, a) - p0).Length < 1e-9 * Math.Max(1, p0.Length)); }
        double rimOther = alongU ? rim.Average(x => x.Uv.Y) : rim.Average(x => x.Uv.X);
        // Ближайший к краю полюс со стороны, где нет других контуров.
        var candidates = new[] { 0.0, 1.0 }.Where(IsPole).OrderBy(o => Math.Abs(o - rimOther)).ToList();
        if (candidates.Count == 0) return null;
        double pole = candidates[0];
        Vector2d UV(double along, double other) => alongU ? new Vector2d(along, other) : new Vector2d(other, along);
        double A(Vector2d v) => alongU ? v.X : v.Y;
        double Other(Vector2d v) => alongU ? v.Y : v.X;
        // KRN-03. ПОЛЮСЫ С ОБЕИХ СТОРОН (сфера): брался ближайший к краю, а при кромке по экватору — первый по
        // порядку, и сторона шапки зависела от того, как построена ось поверхности (булева операция переворачивала
        // ось по готовой сетке). Теперь — по обходу края: материал слева от полуребра относительно нормали грани;
        // многоугольник области в параметрах должен обходиться в ту же сторону, что и (Su × Sv) относительно нормали.
        if (candidates.Count == 2 && face != null && ExactFaceMesher.CapByLoop)
        {
            var (pu, pv) = Periods(s);
            var q0 = rim[rim.Count / 2].Uv; double h = 1e-5;
            var su = Eval(s, q0 + new Vector2d(h, 0), pu, pv) - Eval(s, q0 - new Vector2d(h, 0), pu, pv);
            var sv = Eval(s, q0 + new Vector2d(0, h), pu, pv) - Eval(s, q0 - new Vector2d(0, h), pu, pv);
            double want = Vector3d.Dot(Vector3d.Cross(su, sv), face.NormalAt(rim[rim.Count / 2].X));
            // Область края с полюсом p по другой координате: знак площади = знак(ΔA) · знак(p − край) (для alongU), обратный — иначе.
            double dA = A(rim[^1].Uv) - A(rim[0].Uv);
            double SignedFor(double p) => (alongU ? 1 : -1) * Math.Sign(dA) * Math.Sign(p - rimOther);
            var pick = candidates.Where(p => SignedFor(p) * want > 0).ToList();
            if (pick.Count == 1) { pole = pick[0]; PolePicksByLoop++; } else PoleUndecided++;
        }
        var polePoint = At(pole, 0.5);
        // KRN-03. РАЗРЕЗ К ПОЛЮСУ — ОТ ТОЧКИ КРАЯ, БЛИЖАЙШЕЙ К ПОЛЮСУ. Разрез шёл от начала края (вершины контура);
        // у шапки, ограниченной дугами, край у вершины-излома заходит по углу дальше самой вершины (скругление вогнутого
        // угла: на 8e-4 периода), прямой разрез к полюсу пересекал край, многоугольник области был самопересекающимся —
        // складки сетки (8 ошибок ориентации). От ближайшей к полюсу точки края разрез край не пересекает: любая другая
        // точка края с тем же углом дальше от полюса.
        if (PoleCutFromNearest && rim.Count > 3)
        {
            int m = rim.Count - 1;   // rim[m] — rim[0] через период
            int k = 0; for (int i = 1; i < m; i++) if (Math.Abs(Other(rim[i].Uv) - pole) < Math.Abs(Other(rim[k].Uv) - pole)) k = i;
            if (k > 0)
            {
                double per = A(rim[m].Uv) - A(rim[0].Uv);
                var rot = new List<(Vector2d Uv, Vector3d X)>();
                for (int i = k; i < m; i++) rot.Add(rim[i]);
                for (int i = 0; i <= k; i++) rot.Add((UV(A(rim[i].Uv) + per, Other(rim[i].Uv)), rim[i].X));
                rim = rot; rimUsed = rot;
                PoleCutsMoved++;
            }
        }
        var res = new List<(Vector2d, Vector3d)>(rim);
        var end = rim[^1].Uv; var start = rim[0].Uv;
        // Шов от конца края к полюсу.
        var seam1 = Seam(s, rim[^1], (UV(A(end), pole), polePoint), tol).ToList();
        res.AddRange(seam1);
        // Линия полюса от A(end) к A(start), с тем же числом точек, что у края.
        int n = Math.Max(2, rim.Count - 1);
        for (int i = 0; i <= n; i++)
        {
            double a = A(end) + (A(start) - A(end)) * i / n;
            res.Add((UV(a, pole), polePoint));
        }
        var seam2 = Seam(s, (UV(A(start), pole), polePoint), rim[0], tol).ToList();
        res.AddRange(seam2);
        return res;
    }

    private static IEnumerable<(Vector2d, Vector3d)> Seam(ISurface3 s0, (Vector2d Uv, Vector3d X) from, (Vector2d Uv, Vector3d X) to, double tol)
    {
        var (pu, pv) = Periods(s0);
        Vector3d E(Vector2d q) => Eval(s0, q, pu, pv);
        int n = 1;
        for (; n < 256; n *= 2)
        {
            bool ok = true;
            for (int i = 0; i < n && ok; i++)
            {
                var u0 = from.Uv + (to.Uv - from.Uv) * ((double)i / n); var u1 = from.Uv + (to.Uv - from.Uv) * ((double)(i + 1) / n);
                ok = (E((u0 + u1) / 2) - (E(u0) + E(u1)) / 2).Length <= tol;
            }
            if (ok) break;
        }
        for (int i = 1; i < n; i++)
        {
            var u = from.Uv + (to.Uv - from.Uv) * ((double)i / n);
            yield return (u, E(u));
        }
    }
}
