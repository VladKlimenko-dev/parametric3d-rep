using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

public static partial class ExactFaceMesher
{
    /// <summary>
    /// KRN-11 (11.2): РАЗМЕТКА ГРАНИ ВРАЩЕНИЯ В КОНФОРМНОЙ ПЛОСКОСТИ. Квалификационный набор показал: 14 из 60 случайных пар
    /// примитивов уходили в многогранный путь не из-за булевой, а из-за разметки одной грани по контурам — пояс цилиндра между
    /// торцом и кривой стыка (кольцо в развёртке, «непарная сторона вне контура»), шапка шара или конуса с полюсом внутри
    /// («полюс внутри грани»), пояс конуса («сетка отходит от поверхности на 2–4 мм»). Развёртка (u, v) у таких граней —
    /// не односвязная область: шов периода и сжатая в точку линия полюса.
    ///
    /// Здесь грань переносится на плоскость отображением, которое сохраняет углы и взаимно однозначно на всей поверхности:
    /// цилиндр — w = e^{h/R}·e^{iθ}, конус — w = (r/r₀)^c·e^{iθ}, c = √(1+k²)/|k| (k — наклон образующей), плоский диск —
    /// полярные координаты, шар — стереографическая проекция из точки вне грани. Кольцо становится областью между двумя
    /// вложенными контурами, полюс — обычной внутренней точкой (0), шва нет. Триангуляция Делоне с ограничениями и
    /// внутренними точками, сгущаемыми, пока центр каждого треугольника не ляжет на поверхность с допуском гранения; точки
    /// контура — те же, что у соседних граней. Сторона области — по внутренней точке грани (InteriorHint).
    /// Только когда прежние способы отказали; результат — через общую самопроверку (Validate). Мутация P3D_CONFORMAL=0.
    /// </summary>
    static readonly bool ConformalOff = Environment.GetEnvironmentVariable("P3D_CONFORMAL") == "0";
    public static int ConformalMeshed, ConformalFailed;
    public static Action<string>? ConformalDebug;
    public static Action<string>? TorusDebug;

    /// <summary>Отображение поверхности на плоскость (прямое и обратное) и проверка, применимо ли оно к точке.</summary>
    sealed class ConformalMap
    {
        public required Func<Vector3d, Vector2d> Fwd;
        public required Func<Vector2d, Vector3d> Inv;
        public required string Kind;
        /// <summary>Нормаль поверхности (знак — любой, но один на всей поверхности); null — по грани.</summary>
        public Func<Vector3d, Vector3d>? Normal;
    }

    static (Vector3d U, Vector3d V) Perp(Vector3d w)
    {
        var a = Math.Abs(w.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
        var u = Vector3d.Cross(w, a).Normalized();
        return (u, Vector3d.Cross(w, u));
    }

    /// <summary>Отображения-кандидаты для поверхности (у шара — несколько полюсов проекции); пусто — поверхность не того вида.</summary>
    static List<ConformalMap> ConformalMaps(ISurface3 src, List<List<Vector3d>> loops, Vector3d? hint)
    {
        var maps = new List<ConformalMap>();
        var all = loops.SelectMany(l => l).ToList();
        if (all.Count < 3) return maps;

        ConformalMap? Axial(Vector3d o, Vector3d w, Func<double, double, double> logRho, Func<double, (double H, double R)?> back, string kind)
        {
            var (u, v) = Perp(w);
            double Th(Vector3d p) { var d = p - o; return Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u)); }
            (double h, double r) HR(Vector3d p) { var d = p - o; double h = Vector3d.Dot(d, w); return (h, (d - w * h).Length); }
            // масштаб: середина диапазона log ρ контура — в ноль, иначе e^{h/R} уходит за пределы точности
            var lr = all.Select(p => { var (h, r) = HR(p); return logRho(h, r); }).Where(double.IsFinite).ToList();
            if (lr.Count == 0) return null;
            double lo = lr.Min(), hi = lr.Max(), mid = (lo + hi) / 2;
            if (hi - lo > 12) return null;                    // ρ от e^-6 до e^6 — дальше триангуляция теряет точность
            return new ConformalMap
            {
                Kind = kind,
                Fwd = p =>
                {
                    var (h, r) = HR(p); double l = logRho(h, r);
                    if (double.IsNegativeInfinity(l)) return Vector2d.Zero;
                    double rho = Math.Exp(l - mid), th = Th(p);
                    return new Vector2d(rho * Math.Cos(th), rho * Math.Sin(th));
                },
                Inv = q =>
                {
                    double rho = q.Length;
                    if (rho < 1e-300) return back(double.NegativeInfinity) is { } b0 ? o + w * b0.H : o;
                    double th = Math.Atan2(q.Y, q.X);
                    if (back(Math.Log(rho) + mid) is not { } hr) return new Vector3d(double.NaN);
                    return o + w * hr.H + (u * Math.Cos(th) + v * Math.Sin(th)) * hr.R;
                },
            };
        }

        switch (src)
        {
            case ExtrudedSurface { Base: ArcCurve3 arc } ex:
            {
                var w = ex.Dir.Normalized(); double R = arc.Radius;
                if (Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), w)) < 1 - 1e-9) break;   // косой срез — не круговой цилиндр
                var m = Axial(arc.Frame.Origin, w, (h, r) => h / R, l => double.IsNegativeInfinity(l) ? null : (l * R, R), "cylinder");
                if (m != null) maps.Add(m);
                break;
            }
            case RevolvedSurface { Base: LineCurve3 ln } rv:
            {
                var o = rv.Axis.Origin; var w = rv.Axis.W.Normalized();
                (double h, double r) HR(Vector3d p) { var d = p - o; double h = Vector3d.Dot(d, w); return (h, (d - w * h).Length); }
                var (hA, rA) = HR(ln.PointAt(ln.T0)); var (hB, rB) = HR(ln.PointAt(ln.T1));
                double len = Math.Sqrt((hB - hA) * (hB - hA) + (rB - rA) * (rB - rA));
                if (len < 1e-12) break;
                if (Math.Abs(rB - rA) < 1e-9 * len)
                {
                    double R = (rA + rB) / 2; if (R < 1e-9) break;
                    var m = Axial(o, w, (h, r) => h / R, l => double.IsNegativeInfinity(l) ? null : (l * R, R), "cylinder");
                    if (m != null) maps.Add(m);
                }
                else if (Math.Abs(hB - hA) < 1e-9 * len)
                {
                    double h0 = (hA + hB) / 2;
                    var m = Axial(o, w, (h, r) => r <= 0 ? double.NegativeInfinity : Math.Log(r), l => (h0, double.IsNegativeInfinity(l) ? 0 : Math.Exp(l)), "disk");
                    if (m != null) maps.Add(m);
                }
                else
                {
                    double k = (rB - rA) / (hB - hA), c = Math.Sqrt(1 + k * k) / Math.Abs(k);
                    var m = Axial(o, w, (h, r) => r <= 0 ? double.NegativeInfinity : c * Math.Log(r),
                                  l => { double r = double.IsNegativeInfinity(l) ? 0 : Math.Exp(l / c); return (hA + (r - rA) / k, r); }, "cone");
                    if (m != null) maps.Add(m);
                }
                break;
            }
            case RevolvedSurface { Base: ArcCurve3 a } rv2:
            {
                var o = rv2.Axis.Origin; var w = rv2.Axis.W.Normalized();
                var dc = a.Frame.Origin - o;
                if ((dc - w * Vector3d.Dot(dc, w)).Length > 1e-9 * Math.Max(1, a.Radius))      // центр дуги не на оси — тор
                {
                    if (hint is { } th && !TorusOff) maps.AddRange(TorusMaps(o + w * Vector3d.Dot(dc, w), w, (dc - w * Vector3d.Dot(dc, w)).Length, a.Radius, all, loops, th));
                    break;
                }
                var C = a.Frame.Origin; double R = a.Radius;
                // полюса проекции: точки шара дальше всего от контуров (по возрастанию близости); сторона — правилом «грань слева»
                var dirs = new List<Vector3d> { w, -w, Vector3d.UnitX, -Vector3d.UnitX, Vector3d.UnitY, -Vector3d.UnitY, Vector3d.UnitZ, -Vector3d.UnitZ };
                var mean = all.Aggregate(Vector3d.Zero, (s, p) => s + (p - C)) / all.Count;
                if (mean.Length > 1e-6 * R) { dirs.Insert(0, mean.Normalized()); dirs.Insert(1, -mean.Normalized()); }
                if (hint is { } hp && (hp - C).Length > 1e-6 * R) dirs.Insert(0, -(hp - C).Normalized());   // напротив точки грани
                foreach (var loop in loops)
                {
                    var lm = loop.Aggregate(Vector3d.Zero, (s, p) => s + (p - C)) / loop.Count;
                    if (lm.Length > 1e-6 * R) dirs.Add(lm.Normalized());
                }
                foreach (var d in dirs)
                {
                    if (all.Min(p => ((p - C).Normalized() - d).Length) < 1e-4) continue;     // полюс на самом контуре
                    var (e1, e2) = Perp(d); var dd = d;
                    maps.Add(new ConformalMap
                    {
                        Kind = "sphere",
                        Fwd = p =>
                        {
                            var q = (p - C).Normalized(); double x = Vector3d.Dot(q, e1), y = Vector3d.Dot(q, e2), z = Vector3d.Dot(q, dd);
                            return new Vector2d(x, y) / (1 - z);
                        },
                        Inv = s =>
                        {
                            double n2 = s.LengthSquared, den = 1 + n2;
                            return C + (e1 * (2 * s.X / den) + e2 * (2 * s.Y / den) + dd * ((n2 - 1) / den)) * R;
                        },
                    });
                }
                break;
            }
        }
        return maps;
    }

    // Тор — НЕ ВКЛЮЧЁН (P3D_CONFORMALTORUS=1 — включить для разбора). Отображение проверено (якобиан одного знака на сетке
    // 24×24, обратное — до 1e-6), но на грани тора с вырезом бруска (пара #45) 482 из 7898 треугольников сетки ложатся против
    // нормали при согласованном обходе в плоскости — причина не найдена; самопроверка такую сетку отвергает. Контур с
    // ненулевым числом оборотов по координате разреза делает разрез негодным (Winding).
    static readonly bool TorusOff = Environment.GetEnvironmentVariable("P3D_CONFORMALTORUS") != "1";

    /// <summary>
    /// ТОР: (θ — вокруг оси, ψ — конформная координата вдоль трубки, dψ = a·dφ/(R + a·cos φ)) — плоский тор, метрика
    /// (R + a·cos φ)²(dθ² + dψ²). Разрез по окружности ψ = ψc вне грани (середина наибольшего промежутка ψ точек контура,
    /// вне грани — по чётности пересечений контура на пути от внутренней точки) — w = e^{ψ̃ + iθ}; разрез по меридиану
    /// θ = θc — w = e^{k θ̃ + i k ψ}, k = 2π/Ψ. Грань, обходящая и ось, и трубку, так не переносится.
    /// </summary>
    static List<ConformalMap> TorusMaps(Vector3d o, Vector3d w, double R, double a, List<Vector3d> all, List<List<Vector3d>> loops, Vector3d hint)
    {
        var maps = new List<ConformalMap>();
        if (R <= a * (1 + 1e-9)) return maps;
        var (u, v) = Perp(w);
        double q = Math.Sqrt(R * R - a * a), kq = Math.Sqrt((R - a) / (R + a)), Psi = 2 * Math.PI * a / q;
        double PsiOf(double phi) => 2 * a / q * Math.Atan(kq * Math.Tan(phi / 2));
        double PhiOf(double psi) => 2 * Math.Atan(Math.Tan(psi * q / (2 * a)) / kq);
        (double Th, double Ps) TP(Vector3d p)
        {
            var d = p - o; double h = Vector3d.Dot(d, w); var rad = d - w * h; double rr = rad.Length;
            double th = Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u)), phi = Math.Atan2(h, rr - R);
            return (th, PsiOf(phi));
        }
        Vector3d FromTP(double th, double ps)
        {
            ps = ps - Psi * Math.Round(ps / Psi);
            double phi = PhiOf(ps);
            return o + w * (a * Math.Sin(phi)) + (u * Math.Cos(th) + v * Math.Sin(th)) * (R + a * Math.Cos(phi));
        }
        static double Wrap(double x, double per) => x - per * Math.Floor(x / per);          // [0, per)
        static double WrapS(double x, double per) => x - per * Math.Round(x / per);         // (−per/2, per/2]
        Vector3d TorN(Vector3d p) { var d = p - o; var rad = d - w * Vector3d.Dot(d, w); return (p - (o + rad.Normalized() * R)).Normalized(); }
        var tp = loops.Select(l => l.Select(TP).ToList()).ToList();
        var hp = TP(hint);
        // чётность пересечений контуров на пути от точки грани вдоль одной координаты до разреза: нечётно — разрез вне грани
        bool CutOutside(bool alongPsi, double cut)
        {
            double per = alongPsi ? Psi : 2 * Math.PI, otherPer = alongPsi ? 2 * Math.PI : Psi;
            double start = alongPsi ? hp.Ps : hp.Th, fixedC = alongPsi ? hp.Th : hp.Ps;
            double span = Wrap(cut - start, per); int cross = 0;
            foreach (var l in tp)
                for (int i = 0; i < l.Count; i++)
                {
                    var p1 = l[i]; var p2 = l[(i + 1) % l.Count];
                    double o1 = WrapS((alongPsi ? p1.Th : p1.Ps) - fixedC, otherPer), o2 = WrapS((alongPsi ? p2.Th : p2.Ps) - fixedC, otherPer);
                    if (Math.Abs(o1 - o2) > otherPer / 2 || (o1 > 0) == (o2 > 0)) continue;
                    double m1 = alongPsi ? p1.Ps : p1.Th, m2 = m1 + WrapS((alongPsi ? p2.Ps : p2.Th) - m1, per);
                    double t = o1 / (o1 - o2), m = m1 + (m2 - m1) * t;
                    double sm = Wrap(m - start, per);
                    if (sm > 0 && sm < span) cross++;
                }
            TorusDebug?.Invoke($"cut {(alongPsi ? "psi" : "theta")} {cut:0.###} from {start:0.###} span {span:0.###} crossings {cross}; loops θ [{string.Join("; ", tp.Select(l => $"{l.Min(x => x.Th):0.##}..{l.Max(x => x.Th):0.##}"))}] ψ [{string.Join("; ", tp.Select(l => $"{l.Min(x => x.Ps):0.##}..{l.Max(x => x.Ps):0.##}"))}] Ψ {Psi:0.###}");
            return cross % 2 == 1;
        }
        double? GapMid(bool psi)
        {
            double per = psi ? Psi : 2 * Math.PI;
            var vals = tp.SelectMany(l => l).Select(x => Wrap(psi ? x.Ps : x.Th, per)).OrderBy(x => x).ToList();
            if (vals.Count == 0) return null;
            double best = -1, mid = 0;
            for (int i = 0; i < vals.Count; i++)
            {
                double g = i + 1 < vals.Count ? vals[i + 1] - vals[i] : vals[0] + per - vals[i];
                if (g > best) { best = g; mid = vals[i] + g / 2; }
            }
            return best > 1e-6 * per ? mid : null;
        }
        // контур, обходящий тор по координате (число оборотов ≠ 0), пересекает любой разрез по ней — такой разрез негоден
        int Winding(List<(double Th, double Ps)> l, bool psi)
        {
            double per = psi ? Psi : 2 * Math.PI, tot = 0;
            for (int i = 0; i < l.Count; i++) { var p1 = l[i]; var p2 = l[(i + 1) % l.Count]; tot += WrapS(psi ? p2.Ps - p1.Ps : p2.Th - p1.Th, per); }
            return (int)Math.Round(tot / per);
        }
        bool psiFree = tp.All(l => Winding(l, true) == 0), thetaFree = tp.All(l => Winding(l, false) == 0);
        TorusDebug?.Invoke($"windings ψ [{string.Join(",", tp.Select(l => Winding(l, true)))}] θ [{string.Join(",", tp.Select(l => Winding(l, false)))}]");
        if (psiFree && GapMid(true) is { } pc && CutOutside(true, pc))
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var x in tp.SelectMany(l => l)) { double s2 = Wrap(x.Ps - pc, Psi); lo = Math.Min(lo, s2); hi = Math.Max(hi, s2); }
            double mid = (lo + hi) / 2;
            if (hi - lo <= 12)
                maps.Add(new ConformalMap
                {
                    Kind = "torus-psi", Normal = TorN,
                    Fwd = p => { var (th, ps) = TP(p); double rho = Math.Exp(Wrap(ps - pc, Psi) - mid); return new Vector2d(rho * Math.Cos(th), rho * Math.Sin(th)); },
                    Inv = z => FromTP(Math.Atan2(z.Y, z.X), Math.Log(z.Length) + mid + pc),
                });
        }
        if (thetaFree && GapMid(false) is { } tc && CutOutside(false, tc))
        {
            double k = 2 * Math.PI / Psi, lo = double.MaxValue, hi = double.MinValue;
            foreach (var x in tp.SelectMany(l => l)) { double s2 = k * Wrap(x.Th - tc, 2 * Math.PI); lo = Math.Min(lo, s2); hi = Math.Max(hi, s2); }
            double mid = (lo + hi) / 2;
            if (hi - lo <= 12)
                maps.Add(new ConformalMap
                {
                    Kind = "torus-theta", Normal = TorN,
                    Fwd = p => { var (th, ps) = TP(p); double rho = Math.Exp(k * Wrap(th - tc, 2 * Math.PI) - mid); return new Vector2d(rho * Math.Cos(k * ps), rho * Math.Sin(k * ps)); },
                    Inv = z => FromTP((Math.Log(z.Length) + mid) / k + tc, Math.Atan2(z.Y, z.X) / k),
                });
        }
        return maps;
    }

    static double SignedArea(List<Vector2d> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++) { var q = p[i]; var r = p[(i + 1) % p.Count]; a += q.X * r.Y - r.X * q.Y; }
        return a / 2;
    }

    /// <summary>Разметка грани в конформной плоскости; null — неприменимо или не удалось (причина — в why).</summary>
    private static (List<Vector3d> P, List<int> T)? MeshConformal(TFace f, ISurface3 src, List<List<(TEdge E, bool Rev)>> cycles, out string? why)
    {
        why = null;
        if (ConformalOff) { why = "conformal: off"; return null; }
        using var _pc = Diagnostics.Prof.Time("сетка: конформная плоскость");
        // контуры в 3D — по обходу рёбер, без повтора первой точки; соседние совпадения — сварены
        // Шов периода (ребро, пройденное контуром дважды — туда и обратно) в конформной плоскости не нужен: он лишь
        // разрезает область. Без швов куски рёбер сцепляются заново в замкнутые контуры (пояс цилиндра со швом — два кольца).
        var uses = new Dictionary<TEdge, int>();
        foreach (var cyc in cycles) foreach (var (e, _) in cyc) uses[e] = uses.GetValueOrDefault(e) + 1;
        var pieces = new List<List<Vector3d>>();
        foreach (var cyc in cycles)
            foreach (var (e, rev) in cyc)
            {
                if (uses[e] > 1) continue;
                var ep = EdgePoints(e).ToList(); if (rev) ep.Reverse();
                if (ep.Count >= 2) pieces.Add(ep);
            }
        var loops = new List<List<Vector3d>>();
        while (pieces.Count > 0)
        {
            var lp = new List<Vector3d>(pieces[0]); pieces.RemoveAt(0);
            bool grew = true;
            while (grew && (lp[0] - lp[^1]).Length >= Tol.Weld)
            {
                grew = false;
                for (int i = 0; i < pieces.Count; i++)
                {
                    var pc = pieces[i];
                    if ((pc[0] - lp[^1]).Length < Tol.Weld) { lp.AddRange(pc.Skip(1)); }
                    else if ((pc[^1] - lp[^1]).Length < Tol.Weld) { var r = new List<Vector3d>(pc); r.Reverse(); lp.AddRange(r.Skip(1)); }
                    else continue;
                    pieces.RemoveAt(i); grew = true; break;
                }
            }
            if ((lp[0] - lp[^1]).Length >= Tol.Weld && lp.Count > 2) { why = "conformal: a loop does not close without the seam"; return null; }
            while (lp.Count > 1 && (lp[0] - lp[^1]).Length < Tol.Weld) lp.RemoveAt(lp.Count - 1);
            for (int i = lp.Count - 1; i > 0; i--) if ((lp[i] - lp[i - 1]).Length < 1e-9) lp.RemoveAt(i);
            if (lp.Count < 3) { why = "conformal: degenerate loop"; return null; }
            loops.Add(lp);
        }
        if (loops.Count == 0) { why = "conformal: no loops"; return null; }
        if (ConformalDebug != null)
        {
            double mind = double.MaxValue; Vector3d at = default;
            var allB = loops.SelectMany(l => l).ToList();
            for (int i = 0; i < allB.Count; i++) for (int j = i + 1; j < allB.Count; j++) { double d = (allB[i] - allB[j]).Length; if (d < mind) { mind = d; at = allB[i]; } }
            ConformalDebug($"face {f.Id}: loops {loops.Count} [{string.Join(",", loops.Select(l => l.Count))}], closest boundary points {mind:G3} at {at}");
        }
        var maps = ConformalMaps(src, loops, f.InteriorHint);
        if (maps.Count == 0) { why = "conformal: surface kind not supported"; return null; }
        double tol = 2 * Math.Max(Tol.Chord, 1e-4);
        string? lastWhy = "conformal: no map fits";
        var whys = new List<string>();
        foreach (var map in maps)
        {
            if (lastWhy != null && lastWhy != "conformal: no map fits") whys.Add(lastWhy);
            var w = loops.Select(l => l.Select(map.Fwd).ToList()).ToList();
            if (w.Any(l => l.Any(q => !double.IsFinite(q.X) || !double.IsFinite(q.Y)))) { lastWhy = "conformal: non-finite image"; continue; }
            // вложенность: внешний — единственный ни в ком; отверстия — ровно в одном
            int nL = w.Count; var depth = new int[nL];
            for (int i = 0; i < nL; i++)
                for (int j = 0; j < nL; j++)
                    if (i != j && Inside(w[j], w[i][0])) depth[i]++;
            if (depth.Count(d => d == 0) != 1 || depth.Any(d => d > 1)) { lastWhy = $"conformal ({map.Kind}): loops nest as [{string.Join(",", depth)}]"; continue; }
            int oi = Array.IndexOf(depth, 0);
            // сторона области: точка внутри грани (InteriorHint) должна лечь внутрь внешнего контура и вне отверстий.
            // Обход контуров здесь не мерило: Cycles сцепляет рёбра без учёта стороны (у пояса цилиндра оба контура —
            // в одну сторону). У шара без такой точки полюс проекции не выбрать — отказ.
            if (f.InteriorHint is { } hint)
            {
                var wh = map.Fwd(hint);
                bool inReg = Inside(w[oi], wh) && !Enumerable.Range(0, nL).Any(i => i != oi && Inside(w[i], wh));
                if (!inReg) ConformalDebug?.Invoke($"face {f.Id} {map.Kind}: hint {hint} -> {wh}; loops {nL}, outer {oi} n={w[oi].Count} bbox ({w[oi].Min(q => q.X):0.###},{w[oi].Min(q => q.Y):0.###})-({w[oi].Max(q => q.X):0.###},{w[oi].Max(q => q.Y):0.###}); dist hint to loop {loops.SelectMany(l => l).Min(q => (q - hint).Length):0.###}");
                if (!inReg) { lastWhy = $"conformal ({map.Kind}): the face's interior point maps outside the region (projection pole inside the face)"; continue; }
            }
            else if (map.Kind == "sphere") { lastWhy = "conformal (sphere): no interior point to choose the projection pole"; continue; }
            // точка плоскости -> точка контура (точные точки соседей)
            var lookup = new Dictionary<(double, double), Vector3d>();
            for (int i = 0; i < nL; i++) for (int k = 0; k < w[i].Count; k++) lookup[(w[i][k].X, w[i][k].Y)] = loops[i][k];
            // триангулятору — внешний против часовой, отверстия по часовой (в плоскости)
            List<Vector2d> Dir(List<Vector2d> l, bool ccw) { var c = new List<Vector2d>(l); if ((SignedArea(c) > 0) != ccw) c.Reverse(); return c; }
            var outer = Dir(w[oi], true);
            var holes = Enumerable.Range(0, nL).Where(i => i != oi).Select(i => (IReadOnlyList<Vector2d>)Dir(w[i], false)).ToList();
            int nB = w.Sum(l => l.Count);
            var inner = new List<Vector2d>(); var tried = new HashSet<(double, double)>();
            List<int>? T = null; List<Vector2d> pts = new();
            for (int pass = 0; pass < 20; pass++)
            {
                T = Cdt.Triangulate(outer, holes, out pts, inner);
                if (T == null) { lastWhy = $"conformal ({map.Kind}): triangulation failed"; break; }
                if (pts.Count(q => lookup.ContainsKey((q.X, q.Y))) != nB) { T = null; lastWhy = $"conformal ({map.Kind}): the triangulator dropped contour points"; break; }
                var bad = new List<Vector2d>();
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    Vector2d a = pts[T[i]], b = pts[T[i + 1]], c = pts[T[i + 2]];
                    Vector3d A = P3(a), B = P3(b), Cc = P3(c);
                    var g = (a + b + c) / 3;
                    double dev = (map.Inv(g) - (A + B + Cc) / 3).Length;
                    // и середины сторон (длинная сторона у кромки — тот же прогиб)
                    // (сторона между двумя точками контура — хорда ребра, её прогиб задан гранением ребра: не мерится)
                    double Mid(Vector2d x, Vector2d y, Vector3d X, Vector3d Y) =>
                        lookup.ContainsKey((x.X, x.Y)) && lookup.ContainsKey((y.X, y.Y)) ? 0 : (map.Inv((x + y) / 2) - (X + Y) / 2).Length;
                    dev = Math.Max(dev, Math.Max(Mid(a, b, A, B), Math.Max(Mid(b, c, B, Cc), Mid(c, a, Cc, A))));
                    // мельче 0.01 мм не дробится: прогиб такого треугольника уже не от размера (угол у кромки)
                    double lmax = Math.Max((B - A).Length, Math.Max((Cc - B).Length, (A - Cc).Length));
                    if (dev > tol && lmax > 0.01) bad.Add(g);
                }
                // центр, уже вставленный (или вне области), второй раз не берётся: такие треугольники — на суд самопроверки
                bad.RemoveAll(q => tried.Contains((q.X, q.Y)));
                if (bad.Count == 0) break;
                if (inner.Count + bad.Count > 40000) { T = null; lastWhy = $"conformal ({map.Kind}): too many points"; break; }
                foreach (var q in bad) tried.Add((q.X, q.Y));
                inner.AddRange(bad);
            }
            // вырожденные треугольники (вставленная точка легла на сторону: площадь ~0) — такая точка убирается
            for (int fix = 0; fix < 8 && T != null; fix++)
            {
                var drop = new HashSet<(double, double)>();
                var areas = new double[T.Count / 3]; double total = 0;
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    Vector3d A3 = P3(pts[T[i]]), B3 = P3(pts[T[i + 1]]), C3 = P3(pts[T[i + 2]]);
                    total += areas[i / 3] = Vector3d.Cross(B3 - A3, C3 - A3).Length / 2;
                }
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    // мерило самопроверки (Validate): площадь меньше 1e-9 площади грани — вырожденный
                    if (areas[i / 3] >= 1e-8 * total) continue;
                    foreach (var q in new[] { pts[T[i]], pts[T[i + 1]], pts[T[i + 2]] }) if (!lookup.ContainsKey((q.X, q.Y))) drop.Add((q.X, q.Y));
                }
                if (drop.Count == 0) break;
                inner.RemoveAll(q => drop.Contains((q.X, q.Y)));
                T = Cdt.Triangulate(outer, holes, out pts, inner);
            }
            if (T == null) continue;
            var P = pts.Select(P3).ToList();
            if (P.Any(p => !double.IsFinite(p.X))) { lastWhy = $"conformal ({map.Kind}): non-finite inverse"; continue; }
            // разные точки плоскости, совпавшие в пространстве (вставка у кромки на точке контура), — одна точка;
            // треугольник с двумя одинаковыми вершинами — щель нулевой ширины, убирается
            {
                var canon = new Dictionary<(long, long, long), int>(); var remap = new int[P.Count];
                for (int i = 0; i < P.Count; i++)
                {
                    var key = ((long)Math.Round(P[i].X * 1e6), (long)Math.Round(P[i].Y * 1e6), (long)Math.Round(P[i].Z * 1e6));
                    remap[i] = canon.TryGetValue(key, out var j) ? j : (canon[key] = i);
                }
                var T2 = new List<int>(T.Count);
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    int a = remap[T[i]], b = remap[T[i + 1]], c = remap[T[i + 2]];
                    if (a != b && b != c && c != a) { T2.Add(a); T2.Add(b); T2.Add(c); }
                }
                T = T2;
            }
            Orient(f, P, T);
            {
                int against = 0;
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    var nT = Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]);
                    var gN = (P[T[i]] + P[T[i + 1]] + P[T[i + 2]]) / 3;
                    if (Vector3d.Dot(nT, map.Normal?.Invoke(gN) ?? f.NormalAt(gN)) < 0) against++;
                }
                if (against > 0 && against < T.Count / 3)
                {
                    int negPlane = 0;
                    for (int i = 0; i + 2 < T.Count; i += 3) { Vector2d a2 = pts[T[i]], b2 = pts[T[i + 1]], c2 = pts[T[i + 2]]; if ((b2.X - a2.X) * (c2.Y - a2.Y) - (b2.Y - a2.Y) * (c2.X - a2.X) < 0) negPlane++; }
                    lastWhy = $"conformal ({map.Kind}): folded — {against} of {T.Count / 3} triangles against the face normal (in the plane: {negPlane} clockwise)"; continue;
                }
            }
            if (ConformalDebug != null)
            {
                int agree = 0;
                for (int i = 0; i + 2 < T.Count; i += 3)
                {
                    var nT = Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]); var g3 = (P[T[i]] + P[T[i + 1]] + P[T[i + 2]]) / 3;
                    if (Vector3d.Dot(nT, f.NormalAt(g3)) > 0) agree++;
                }
                double area = 0; for (int i = 0; i + 2 < T.Count; i += 3) area += Vector3d.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]).Length / 2;
                ConformalDebug($"face {f.Id}: conformal {map.Kind}: {T.Count / 3} triangles, {inner.Count} inner points, normals agree {agree}, area {area:0.###}, rev {f.Reversed}");
            }
            return (P, T);

            Vector3d P3(Vector2d q) => lookup.TryGetValue((q.X, q.Y), out var p3) ? p3 : map.Inv(q);
        }
        if (lastWhy != null) whys.Add(lastWhy);
        why = string.Join(" / ", whys.Distinct());
        return null;
    }
}
