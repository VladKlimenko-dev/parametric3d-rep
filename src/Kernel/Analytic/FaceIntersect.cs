using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// АНАЛИТИЧЕСКОЕ ЯДРО, ЭТАП 2, БЛОК 2: ПЕРЕСЕЧЕНИЕ ДВУХ ГРАНЕЙ — куски кривой пересечения их поверхностей (Ssi: прямая,
/// окружность, эллипс, пара прямых; прочее — трассировкой), лежащие внутри ОБЕИХ граней. Вдоль кривой — знаковое
/// расстояние до границы каждой грани в её параметрах (плюс внутри); переходы знака уточняются бисекцией до 1e-12 по
/// параметру. Совпадающие поверхности и касания — отдельными видами результата (их разбирает следующий блок).
///
/// Ограничение: кривая сэмплируется (не реже 256 точек и 20 на мм) — участок внутри обеих граней короче шага может быть
/// пропущен.
/// </summary>
public static class FaceIntersect
{
    public readonly record struct Piece(ICurve3 Curve, double T0, double T1, Vector3d P0, Vector3d P1, bool Closed);

    public enum Kind { Pieces, Empty, Coincident, Tangent, Unresolved }
    [ThreadStatic] public static Action<string>? Debug;

    public sealed class Result
    {
        public Kind Kind;
        public readonly List<Piece> Pieces = new();
        /// <summary>KRN-04: события не согласуются с классификацией интервалов (пропущенное событие) — отказ.</summary>
        public string? Unresolved;
        public override string ToString() => $"{Kind}: {Pieces.Count} piece(s)";
    }

    static (Vector3d Lo, Vector3d Hi) BoxOf(TFace f)
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue); var pts = new List<Vector3d>();
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) { pts.Clear(); ce.Tessellate(pts, true); foreach (var p in pts) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); } }
        return (lo, hi);
    }

    public static Result Of(AnalyticFace a, AnalyticFace b)
    {
        var res = new Result { Kind = Kind.Empty };
        // ЛЕНТА КАТЯЩЕГОСЯ ШАРА — пассивная грань: габариты врозь — пусто; иначе — отказ маршрута (пересечение с лентой не строится)
        if (a.Band || b.Band)
        {
            if (a.Band && b.Band) { res.Kind = Kind.Unresolved; res.Unresolved = "two blend faces"; return res; }
            var band = a.Band ? a : b; var oth = a.Band ? b : a;
            var (blo, bhi) = BoxOf(oth.Face);
            var mo = new Vector3d((oth.Cone ? 4 * oth.R : oth.Cyl ? oth.R : oth.Sph ? 2 * oth.R : oth.Tor ? oth.R + oth.Rm : 0) + 1e-6);
            if (oth.Sph) { blo = oth.O - new Vector3d(oth.R); bhi = oth.O + new Vector3d(oth.R); }
            if (oth.Tor) { blo = oth.O - new Vector3d(oth.R + oth.Rm); bhi = oth.O + new Vector3d(oth.R + oth.Rm); }
            blo -= mo; bhi += mo;
            if (band.BandLo.X > bhi.X || blo.X > band.BandHi.X || band.BandLo.Y > bhi.Y || blo.Y > band.BandHi.Y || band.BandLo.Z > bhi.Z || blo.Z > band.BandHi.Z) return res;
            res.Kind = Kind.Unresolved; res.Unresolved = $"a blend face meets face {oth.Face.Tag} (boolean through a rolling-ball blend is not supported)";
            return res;
        }
        var (la, ha) = BoxOf(a.Face); var (lb, hb) = BoxOf(b.Face);
        // сферическая грань — габарит шара (у полной сферы-примитива контур без полурёбер — габарит по рёбрам пуст)
        if (a.Sph) { la = a.O - new Vector3d(a.R); ha = a.O + new Vector3d(a.R); }
        if (b.Sph) { lb = b.O - new Vector3d(b.R); hb = b.O + new Vector3d(b.R); }
        // тор — габарит целого тора (у целого тора-примитива рёбра — только швы)
        if (a.Tor) { la = a.O - new Vector3d(a.R + a.Rm); ha = a.O + new Vector3d(a.R + a.Rm); }
        if (b.Tor) { lb = b.O - new Vector3d(b.R + b.Rm); hb = b.O + new Vector3d(b.R + b.Rm); }
        // цилиндрическая грань выходит за габарит своих рёбер на прогиб — запас по радиусу
        // (у полной сферы рёбра — только шов-меридиан: запас на диаметр)
        var m = new Vector3d(Math.Max(a.Cone ? 4 * a.R : a.Cyl ? a.R : a.Sph ? 2 * a.R : 0, b.Cone ? 4 * b.R : b.Cyl ? b.R : b.Sph ? 2 * b.R : 0) + 1e-6);
        if (la.X > hb.X + m.X || lb.X > ha.X + m.X || la.Y > hb.Y + m.Y || lb.Y > ha.Y + m.Y || la.Z > hb.Z + m.Z || lb.Z > ha.Z + m.Z) return res;
        var lo = Vector3d.ComponentMin(la, lb) - m; var hi = Vector3d.ComponentMax(ha, hb) + m;
        var centre = (lo + hi) / 2; double radius = (hi - lo).Length / 2 * 1.01;

        var ssi = Ssi.Intersect(a.Shape, b.Shape, centre, radius);
        if (ssi.Uncertified != null) { res.Kind = Kind.Unresolved; res.Unresolved = ssi.Uncertified; return res; }
        if (ssi.Kind == Ssi.Kind.Coincident) { res.Kind = Kind.Coincident; return res; }
        if (ssi.Kind == Ssi.Kind.Tangent) { res.Kind = Kind.Tangent; return res; }
        foreach (var c in ssi.Curves) Clip(c, a, b, res);
        if (!EventCertOff && !EventsOff && res.Unresolved == null) CertifyEvents(a, b, ssi, res);
        if (Debug != null && (a.Tor || b.Tor))
        {
            Debug($"pair {a.Face.Id}/{b.Face.Id} ssi {ssi.Kind} curves {ssi.Curves.Count} → pieces {res.Pieces.Count} unresolved {res.Unresolved}");
            foreach (var c in ssi.Curves) { var q0 = c.PointAt(c.T0); var q1 = c.PointAt(c.T1); var bl = q0; var bh = q0; for (int i = 0; i <= 200; i++) { var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / 200); bl = Vector3d.ComponentMin(bl, q); bh = Vector3d.ComponentMax(bh, q); } Debug($"   curve {c.GetType().Name} {q0:0.###}→{q1:0.###} box {bl:0.##}..{bh:0.##}"); }
            foreach (var pc in res.Pieces) Debug($"   piece {pc.P0:0.###}→{pc.P1:0.###} closed {pc.Closed}");
        }
        res.Kind = res.Unresolved != null ? Kind.Unresolved : res.Pieces.Count > 0 ? Kind.Pieces : Kind.Empty;
        return res;
    }

    static readonly bool EventCertOff = Environment.GetEnvironmentVariable("P3D_EVENTCERT") == "0";
    public static long EventCertFailures;
    /// <summary>
    /// g.05: ПОЛНОТА НА ГРАНИЦЕ. Точка ребра одной грани на поверхности другой лежит на обеих поверхностях — значит, на их
    /// пересечении. Если ни одна найденная кривая через неё не проходит, прослеживание потеряло ветвь, пересекающую границу:
    /// отказ (Unresolved), а не тихий результат без куска. Мутация P3D_EVENTCERT=0. Замкнутые ветви целиком внутри обеих
    /// граней этим не ловятся.
    /// </summary>
    /// <summary>Проверка полноты на границе для данного набора кривых: причина отказа или null (для проверок).</summary>
    public static string? CertifyAgainst(AnalyticFace a, AnalyticFace b, Ssi.Result ssi)
    {
        if (EventCertOff) return null;
        var r = new Result(); CertifyEvents(a, b, ssi, r); return r.Unresolved;
    }

    static void CertifyEvents(AnalyticFace a, AnalyticFace b, Ssi.Result ssi, Result res)
    {
        foreach (var (f, other) in new[] { (a, b.Shape), (b, a.Shape) })
            foreach (var e in f.Face.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge).Distinct())
            {
                if (e.Curve == null) continue;
                foreach (var te in SsiEvents.CurveSurface(e.Curve, other, out _))
                {
                    var q = e.Curve.PointAt(te);
                    // касание ребра поверхностью (кратный корень) кривой пересечения может и не дать — только поперечные
                    var (_, gO) = Ssi.Implicit(other, q); var tng = e.Curve.TangentAt(te);
                    if (tng.LengthSquared < 1e-24 || Math.Abs(Vector3d.Dot(gO, tng.Normalized())) < 1e-6) continue;
                    bool on = false;
                    foreach (var c in ssi.Curves) { double tc = FaceSplit.ClosestParam(c, q); if ((c.PointAt(tc) - q).Length <= 1e-6) { on = true; break; } }
                    if (!on)
                    {
                        Interlocked.Increment(ref EventCertFailures);
                        res.Unresolved = $"edge point {q:0.######} lies on both surfaces but on no traced intersection curve";
                        return;
                    }
                }
            }
    }

    /// <summary>Знак «внутри обеих граней»: минимум знаковых расстояний до их границ (плюс — внутри обеих).</summary>
    static double G(ICurve3 c, double t, AnalyticFace a, AnalyticFace b)
    {
        var x = c.PointAt(t);
        return Math.Min(a.SignedBoundaryDistance(x), b.SignedBoundaryDistance(x));
    }

    /// <summary>
    /// Уточнить параметр конца куска: среди рёбер границы обеих граней вблизи точки — решение C(t) = E(s) методом
    /// Гаусса — Ньютона (у двух прямых — за один шаг); ответ с наименьшей невязкой (не больше 1e-9 мм), иначе null.
    /// </summary>
    /// <summary>Замер: уточнение конца куска не нашло ребра с невязкой ≤ 1e-9.</summary>
    public static long RefineFails;

    internal static double? RefineOnEdge(ICurve3 c, double t0, AnalyticFace a, AnalyticFace? b)
    {
        var x0 = c.PointAt(t0);
        double? best = null; double bestRes = 1e-9;
        foreach (var e in a.Face.Loops.Concat(b?.Face.Loops ?? Enumerable.Empty<TLoop>()).SelectMany(l => l.CoEdges).Select(ce => ce.Edge).Distinct())
        {
            var ec = e.Curve;
            double s = FaceSplit.ClosestParam(ec, x0);
            if ((ec.PointAt(s) - x0).Length > 1e-3) continue;
            double t = t0;
            for (int it = 0; it < 30; it++)
            {
                var C = c.PointAt(t); var E = ec.PointAt(s); var r = C - E;
                if (r.Length < 1e-14) break;
                double h = 1e-7 * Math.Max(1, Math.Abs(c.T1 - c.T0)), g = 1e-7 * Math.Max(1, Math.Abs(ec.T1 - ec.T0));
                var dc = (c.PointAt(t + h) - c.PointAt(t - h)) / (2 * h); var de = (ec.PointAt(s + g) - ec.PointAt(s - g)) / (2 * g);
                // min |C(t) − E(s)|²: [dc·dc, −dc·de; −dc·de, de·de] [Δt, Δs] = [−dc·r, de·r]
                double A11 = Vector3d.Dot(dc, dc), A12 = -Vector3d.Dot(dc, de), A22 = Vector3d.Dot(de, de);
                double b1 = -Vector3d.Dot(dc, r), b2 = Vector3d.Dot(de, r);
                double det = A11 * A22 - A12 * A12;
                if (Math.Abs(det) < 1e-24 * Math.Max(1, A11 * A22)) break;              // касание — не уточнять
                double dt = (b1 * A22 - A12 * b2) / det, ds = (A11 * b2 - A12 * b1) / det;
                t += dt; s += ds;
                if (Math.Abs(dt) < 1e-16 && Math.Abs(ds) < 1e-16) break;
            }
            double res = (c.PointAt(t) - ec.PointAt(s)).Length;
            if (res < bestRes && Math.Abs(t - t0) < 1e-3 * Math.Max(1, Math.Abs(c.T1 - c.T0))) { bestRes = res; best = t; }
        }
        if (best == null) Interlocked.Increment(ref RefineFails);
        return best;
    }

    /// <summary>Куски данной кривой (лежащей на обеих поверхностях), находящиеся внутри обеих граней.</summary>
    public static List<Piece> ClipCurve(ICurve3 c, AnalyticFace a, AnalyticFace b)
    {
        var r = new Result(); Clip(c, a, b, r);
        if (r.Unresolved != null) { r = new Result(); ClipSampled(c, a, b, r); }
        return r.Pieces;
    }

    static readonly bool EventsOff = Environment.GetEnvironmentVariable("P3D_EVENTS") == "0";
    /// <summary>Замер: сколько кривых обрезано по событиям, сколько интервалов не согласовались (отказ).</summary>
    public static long EventClips, UnresolvedClips;

    static void Clip(ICurve3 c, AnalyticFace a, AnalyticFace b, Result res)
    {
        if (EventsOff) ClipSampled(c, a, b, res); else ClipEvents(c, a, b, res);
    }

    /// <summary>
    /// KRN-04 (g.05): ОБРЕЗКА ПО СОБЫТИЯМ. Границы интервалов кривой — точки, где она пересекает рёбра границ обеих граней
    /// (SsiEvents: ребро ∩ поверхность другой грани, алгебраически), и её концы. Каждый интервал классифицируется в трёх
    /// точках (¼, ½, ¾): внутри обеих граней — кусок. Разные знаки внутри одного интервала — событие пропущено: ОТКАЗ
    /// (Kind.Unresolved), а не молчаливое принятие найденного. Прежде — знак в отсчётах с шагом 0.05 мм: участок внутри
    /// обеих граней короче шага пропускался в зависимости от фазы отсчёта (ребро 0.03 мм: из 4 сдвигов на 0.01 мм — 1 отказ
    /// маршрута; 0.01 мм — 3 отказа и точный результат с ошибками B-rep). Мутация P3D_EVENTS=0 — прежняя выборка.
    /// </summary>
    static void ClipEvents(ICurve3 c, AnalyticFace a, AnalyticFace b, Result res)
    {
        Interlocked.Increment(ref EventClips);
        double T0 = c.T0, T1 = c.T1, P = T1 - T0;
        var p0 = c.PointAt(T0); var p1 = c.PointAt(T1);
        bool closed = (p0 - p1).Length < 1e-9;
        var ts = new List<double>();
        foreach (var (f, other) in new[] { (a, b.Shape), (b, a.Shape) })
            foreach (var e in f.Face.Loops.SelectMany(l => l.CoEdges).Select(ce => ce.Edge).Distinct())
            {
                if (e.Curve == null) continue;
                foreach (var te in SsiEvents.CurveSurface(e.Curve, other, out _))
                {
                    var q = e.Curve.PointAt(te);
                    double tc = FaceSplit.ClosestParam(c, q);
                    if ((c.PointAt(tc) - q).Length <= 1e-6) ts.Add(tc);
                    else if (Debug != null && (a.Tor || b.Tor)) Debug($"   event dropped: q {q:0.######} curve dist {(c.PointAt(tc) - q).Length:G3} tc {tc:G6} [{c.T0:G4},{c.T1:G4}]");
                }
            }
        if (closed) { for (int i = 0; i < ts.Count; i++) if (ts[i] >= T1 - 1e-12 * P) ts[i] = T0; }
        else { ts.Add(T0); ts.Add(T1); }
        ts.Sort();
        var ev = new List<double>();
        foreach (var t in ts) if (ev.Count == 0 || t - ev[^1] > 1e-10 * P) ev.Add(t);
        if (closed && ev.Count > 1 && ev[0] + P - ev[^1] <= 1e-10 * P) ev.RemoveAt(ev.Count - 1);

        double Wrap(double t) => closed ? (t >= T1 ? t - P : t) : t;
        // интервалы: (s, e); у замкнутой — и через конец параметра (e > T1)
        var iv = new List<(double S, double E)>();
        if (closed)
        {
            if (ev.Count == 0) iv.Add((T0, T1));
            else for (int i = 0; i < ev.Count; i++) iv.Add((ev[i], i + 1 < ev.Count ? ev[i + 1] : ev[0] + P));
        }
        else for (int i = 0; i + 1 < ev.Count; i++) iv.Add((ev[i], ev[i + 1]));
        var inside = new bool[iv.Count];
        for (int i = 0; i < iv.Count; i++)
        {
            var (s, e) = iv[i];
            double g1 = G(c, Wrap(s + (e - s) * 0.25), a, b), g2 = G(c, Wrap(s + (e - s) * 0.5), a, b), g3 = G(c, Wrap(s + (e - s) * 0.75), a, b);
            bool anyIn = g1 > 0 || g2 > 0 || g3 > 0, anyOut = g1 < 0 || g2 < 0 || g3 < 0;
            if (anyIn && anyOut && e - s > 1e-9 * P)
            {
                Interlocked.Increment(ref UnresolvedClips);
                res.Unresolved = $"interval [{s:R}, {e:R}] of {c.GetType().Name} changes side without an event ({g1:G3}, {g2:G3}, {g3:G3})";
                return;
            }
            inside[i] = g2 > 0;
        }
        // соседние внутренние интервалы — один кусок (событие без смены стороны: шов, касание ребра изнутри)
        var runs = new List<(double S, double E)>();
        for (int i = 0; i < iv.Count; i++)
        {
            if (!inside[i]) continue;
            if (runs.Count > 0 && runs[^1].E == iv[i].S) runs[^1] = (runs[^1].S, iv[i].E); else runs.Add(iv[i]);
        }
        if (runs.Count == 0) return;
        if (closed)
        {
            if (runs.Count == 1 && (iv.Count == 1 || inside.All(x => x))) { res.Pieces.Add(new Piece(c, T0, T1, p0, p0, true)); return; }
            // кусок через конец параметра склеивается с первым
            if (runs.Count > 1 && Math.Abs(runs[^1].E - P - runs[0].S) <= 1e-12 * P)
            {
                var last = runs[^1]; runs.RemoveAt(runs.Count - 1); runs[0] = (last.S, runs[0].E + P);
                if (runs.Count > 1 && runs[0].S >= T1) { }
            }
        }
        foreach (var (s0, e0) in runs)
        {
            double s = s0, e = e0;
            if (!(!closed && s == T0)) s = RefineOnEdge(c, Wrap(s), a, b) is { } rs ? rs + (s >= T1 ? P : 0) : s;
            if (!(!closed && e == T1)) { double ew = Wrap(e); e = RefineOnEdge(c, ew, a, b) is { } re ? re + (e - ew) : e; }
            // кусок через конец параметра: начало — в [T0, T1), конец — за T1
            if (closed && s >= T1) { s -= P; e -= P; }
            if (e - s < 1e-12) continue;
            res.Pieces.Add(new Piece(c, s, e, c.PointAt(s), c.PointAt(e > T1 ? e - P : e), false));
        }
    }

    [ThreadStatic] static int _denseN;

    /// <summary>
    /// Проверка KRN-04: куски пересечения двух граней по событиям и по ГУСТОЙ выборке (n точек на кривую) — для сравнения
    /// (события не должны терять ничего, что видит выборка любой густоты).
    /// </summary>
    public static (Result Events, Result Dense) CompareWithDense(AnalyticFace a, AnalyticFace b, int n)
    {
        var ev = Of(a, b);
        _denseN = n;
        try
        {
            var dn = new Result { Kind = Kind.Empty };
            var (la, ha) = BoxOf(a.Face); var (lb, hb) = BoxOf(b.Face);
            if (a.Sph) { la = a.O - new Vector3d(a.R); ha = a.O + new Vector3d(a.R); }
            if (b.Sph) { lb = b.O - new Vector3d(b.R); hb = b.O + new Vector3d(b.R); }
            var m = new Vector3d(Math.Max(a.Cone ? 4 * a.R : a.Cyl ? a.R : a.Sph ? 2 * a.R : 0, b.Cone ? 4 * b.R : b.Cyl ? b.R : b.Sph ? 2 * b.R : 0) + 1e-6);
            var lo = Vector3d.ComponentMin(la, lb) - m; var hi = Vector3d.ComponentMax(ha, hb) + m;
            var ssi = Ssi.Intersect(a.Shape, b.Shape, (lo + hi) / 2, (hi - lo).Length / 2 * 1.01);
            if (ssi.Kind == Ssi.Kind.Curves) foreach (var c in ssi.Curves) ClipSampled(c, a, b, dn);
            dn.Kind = dn.Pieces.Count > 0 ? Kind.Pieces : Kind.Empty;
            return (ev, dn);
        }
        finally { _denseN = 0; }
    }

    static void ClipSampled(ICurve3 c, AnalyticFace a, AnalyticFace b, Result res)
    {
        double T0 = c.T0, T1 = c.T1;
        var p0 = c.PointAt(T0); var p1 = c.PointAt(T1);
        bool closed = (p0 - p1).Length < 1e-9;
        // длина — по ломаной
        double len = 0; var prev = p0; for (int i = 1; i <= 64; i++) { var q = c.PointAt(T0 + (T1 - T0) * i / 64); len += (q - prev).Length; prev = q; }
        int n = _denseN > 0 ? _denseN : Math.Max(256, (int)Math.Ceiling(len * 20));
        var ts = new double[n + 1]; var gs = new double[n + 1];
        for (int i = 0; i <= n; i++) { ts[i] = T0 + (T1 - T0) * i / n; gs[i] = G(c, ts[i], a, b); }

        double Root(double tOut, double tIn)
        {
            // g(tOut) ≤ 0 < g(tIn): бисекция до 1e-12 по параметру (относительно длины диапазона)
            for (int k = 0; k < 80 && Math.Abs(tIn - tOut) > 1e-12 * Math.Max(1, Math.Abs(T1 - T0)); k++)
            {
                double tm = (tOut + tIn) / 2;
                if (G(c, tm, a, b) > 0) tIn = tm; else tOut = tm;
            }
            // КОНЕЦ КУСКА — где кривая пересекает границу (расстояние до границы — ноль). Знак у самой границы не
            // определён (ближе допуска — «на границе»), бисекция останавливается на ~1e-6 мм внутри; конец уточняется
            // тернарным поиском минимума беззнакового расстояния вокруг найденной точки (включая сторону снаружи).
            var x0 = c.PointAt(tIn); double hh = Math.Max(1e-9, Math.Abs(T1 - T0) * 1e-6);
            double speed = (c.PointAt(tIn + hh) - x0).Length / hh; if (speed < 1e-12) return tIn;
            double span = 8 * AnalyticFace.Tol / speed;
            double lo2 = Math.Min(tOut, tIn) - span, hi2 = Math.Max(tOut, tIn) + span;
            double Dist(double t) { var x = c.PointAt(t); return Math.Min(a.BoundaryDistance(x), b.BoundaryDistance(x)); }
            for (int k = 0; k < 200 && hi2 - lo2 > 1e-15 * Math.Max(1, Math.Abs(T1 - T0)); k++)
            {
                double m1 = lo2 + (hi2 - lo2) / 3, m2 = hi2 - (hi2 - lo2) / 3;
                if (Dist(m1) < Dist(m2)) hi2 = m2; else lo2 = m1;
            }
            double tApprox = (lo2 + hi2) / 2;
            // ТОЧНЫЙ КОНЕЦ: пересечение кривой куска с ребром границы (Гаусс — Ньютон на «точка кривой = точка ребра»).
            // Тернарный поиск у угла, где сходятся три плоскости, давал вершину с ошибкой ~1e-5 (фаска вогнутого угла:
            // (0, 0, 8.9999925) вместо (0, 0, 9), объём — 6e-5).
            return RefineOnEdge(c, tApprox, a, b) ?? tApprox;
        }

        // участки «внутри обеих»
        var runs = new List<(double S, double E)>();
        int iStart = -1;
        for (int i = 0; i <= n; i++)
        {
            bool inside = gs[i] > 0;
            if (inside && iStart < 0) iStart = i;
            if ((!inside || i == n) && iStart >= 0)
            {
                int iEnd = inside ? n : i - 1;
                double s = iStart == 0 ? T0 : Root(ts[iStart - 1], ts[iStart]);
                double e = (inside && i == n) ? T1 : Root(ts[i], ts[iEnd]);
                runs.Add((s, e));
                iStart = -1;
            }
        }
        if (runs.Count == 0) return;
        // замкнутая кривая: целиком внутри — один замкнутый кусок; участок через конец параметра — склеить с первым
        if (closed && runs.Count == 1 && runs[0].S == T0 && runs[0].E == T1)
        {
            res.Pieces.Add(new Piece(c, T0, T1, p0, p0, true));
            return;
        }
        if (closed && runs.Count > 1 && runs[0].S == T0 && runs[^1].E == T1)
        {
            var first = runs[0]; var last = runs[^1];
            runs.RemoveAt(runs.Count - 1); runs[0] = (last.S, first.E + (T1 - T0));   // кусок через конец — параметр за T1
        }
        foreach (var (s, e) in runs)
        {
            if (e - s < 1e-12) continue;
            res.Pieces.Add(new Piece(c, s, e, c.PointAt(s), c.PointAt(e > T1 ? e - (T1 - T0) : e), false));
        }
    }
}
