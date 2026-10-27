using OpenTK.Mathematics;

namespace Parametric3D.Kernel.MeshFit;

/// <summary>
/// ВЫРАВНИВАНИЕ ПО ЗАМЫСЛУ (k119; у QymCAD этого шага нет). Подогнанная поверхность повторяет сетку, а не замысел:
/// ось отверстия отклонена на 10⁻⁵ рад, радиус 4.99987 вместо 5, две соосные ступени вала — на разных осях. Каждое
/// такое отличие делает тело «кривым»: рёбра не встают в плоскость, размеры читаются дробными, соседние грани сходятся
/// под углом в долю градуса. Поэтому после подгонки, по порядку:
/// 1) направления (нормали плоскостей, оси) — к осям координат, затем друг к другу (параллельность), если отличие
///    меньше 1°;
/// 2) соосность: оси вращения, лежащие на одной прямой в пределах допуска, — одна общая ось;
/// 3) числа — радиусы, смещения плоскостей вдоль осей координат, координаты осей и центров — к «круглым» значениям
///    (шаг 10, 5, 1, 0.5 … 0.001), самым крупным шагом, который меньше допуска.
/// Каждый шаг проверяется: все вершины области остаются на поверхности в пределах допуска — иначе он не делается.
/// Мутация P3D_MESHFITSNAP=0.
/// </summary>
public sealed class Regularizer
{
    public static readonly bool Off = Environment.GetEnvironmentVariable("P3D_MESHFITSNAP") == "0";
    readonly PreparedMesh m;
    readonly List<FitRegion> regs;
    readonly double tol;
    readonly List<Vector3d>[] verts;
    public int DirSnaps, Coaxial, ValueSnaps, Tangents;
    public readonly List<string> Notes = new();
    readonly int[] own;
    static readonly double[] Grids = { 10, 5, 1, 0.5, 0.25, 0.1, 0.05, 0.01, 0.005, 0.001 };

    public Regularizer(PreparedMesh mesh, List<FitRegion> regions, double tol)
    {
        m = mesh; regs = regions; this.tol = tol;
        own = new int[m.TriCount];
        for (int i = 0; i < regions.Count; i++) foreach (int t in regions[i].Tris) own[t] = i;
        verts = regions.Select(r =>
        {
            var seen = new HashSet<int>(); var l = new List<Vector3d>();
            foreach (int t in r.Tris) for (int k = 0; k < 3; k++) if (seen.Add(m.Vx(t, k))) l.Add(m.P(t, k));
            return l;
        }).ToArray();
    }

    /// <summary>
    /// Выровненная поверхность годится: вершины в пределах допуска и среднеквадратичный отход не больше чем втрое
    /// прежнего (или 2·10⁻⁷ диагонали): выравнивание не должно «покупаться» точностью — иначе соседние грани перестают
    /// сходиться (касание, общие рёбра).
    /// </summary>
    bool Holds(int i, FitSurface s)
    {
        foreach (var p in verts[i]) if (Math.Abs(s.Distance(p)) > tol) return false;
        return Rms(i, s) <= Math.Max(3 * Rms(i, regs[i].S), 2e-7 * m.Diag);
    }

    public void Run()
    {
        if (Off) return;
        Directions();
        CoaxialAxes();
        Values();
        if (!TangentOff) Tangency();
        // части одной поверхности — одна поверхность (выровненная у самой большой части), если она держит их вершины
        foreach (var g in Enumerable.Range(0, regs.Count).Where(i => regs[i].Group >= 0).GroupBy(i => regs[i].Group))
        {
            int lead = g.OrderByDescending(i => regs[i].Area).First();
            foreach (int i in g) if (i != lead && Holds(i, regs[lead].S)) regs[i].S = regs[lead].S.Clone();
        }
    }

    double Rms(int i, FitSurface s)
    {
        double a = 0; foreach (var p in verts[i]) { double d = s.Distance(p); a += d * d; }
        return verts[i].Count == 0 ? 0 : Math.Sqrt(a / verts[i].Count);
    }

    /// <summary>
    /// НАСКОЛЬКО МОЖНО СДВИНУТЬ ЧИСЛО ПРИ ОКРУГЛЕНИИ: не больше допуска и не больше 20 среднеквадратичных отходов вершин
    /// от подогнанной поверхности. Деталь в дюймах (R 0.9215in = 23.4061 мм) прежде округлялась до 23.41 — на 0.004 мм,
    /// в пределах допуска 0.006, но в 300 раз больше погрешности подгонки: касательная плоскость переставала касаться.
    /// </summary>
    double SnapLimit(int i) => Math.Min(tol, Math.Max(20 * Rms(i, regs[i].S), 1e-7 * m.Diag));

    // ------------------------------------------------------------------ направления

    void Directions()
    {
        var targets = new List<Vector3d> { Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ };
        double lim = Math.Cos(1.0 * Math.PI / 180);
        foreach (int i in Enumerable.Range(0, regs.Count).OrderByDescending(i => regs[i].Area))
        {
            var s = regs[i].S;
            if (s.Kind == FitKind.Sphere) continue;
            Vector3d? hit = null;
            foreach (var t in targets)
            {
                double d = Vector3d.Dot(t, s.A);
                if (Math.Abs(d) >= lim) { hit = d > 0 ? t : -t; break; }
            }
            if (hit is { } h)
            {
                if ((h - s.A).Length < 1e-15) continue;
                if (WithAxis(i, h) is { } ns) { regs[i].S = ns; DirSnaps++; }
            }
            else targets.Add(s.A);
        }
    }

    /// <summary>Поверхность области i с осью (нормалью) a и подогнанными остальными параметрами; null — не держит вершин.</summary>
    FitSurface? WithAxis(int i, Vector3d a)
    {
        var s = regs[i].S; var pts = verts[i];
        FitSurface? ns = null;
        switch (s.Kind)
        {
            case FitKind.Plane:
                ns = s.Clone(); ns.A = a; ns.P = Fits.Mean(pts); break;
            case FitKind.Cylinder:
                ns = Fits.CylinderAbout(pts, a);
                if (ns != null) ns = Fits.Refine(ns, pts, new[] { true, true, false, false, false });
                break;
            case FitKind.Cone:
            {
                var on = s.P;
                ns = Fits.ConeAbout(pts, on, a);
                if (ns != null) { if (Vector3d.Dot(ns.A, a) < 0) { } ns = Fits.Refine(ns, pts, new[] { true, true, false, false, false, false }); }
                break;
            }
            case FitKind.Torus:
                ns = Fits.TorusAbout(pts, s.P, a);
                if (ns != null) ns = Fits.Refine(ns, pts, new[] { true, true, false, false, false, false, false });
                break;
        }
        if (ns == null || !Holds(i, ns)) return null;
        ns.Canonical();
        return ns;
    }

    // ------------------------------------------------------------------ касание

    public static readonly bool TangentOff = Environment.GetEnvironmentVariable("P3D_MESHFITTANGENT") == "0";

    /// <summary>
    /// КАСАНИЕ — ТОЧНО. Плоскость, касательная к цилиндру (стенка кармана, переходящая в скругление), или к конусу по
    /// образующей подгоняются каждая по своим вершинам и после подгонки расходятся на долю допуска: точки касания нет,
    /// вершина тела в этом месте не находится (уходила на 0.68 мм), ребро отходило от граней на 6·10⁻³ мм — тело негодно.
    /// Поэтому кривая область, гладко переходящая в соседнюю (угол сетки через границу до 15°, нормали поверхностей на
    /// границе — до 3°), переподгоняется вместе с условием касания: проходит через точки границы на соседней поверхности
    /// с её нормалью. Соседи-образцы — плоскости и уже выровненные большие кривые. Мутация P3D_MESHFITTANGENT=0.
    /// </summary>
    void Tangency()
    {
        var bnd = new Dictionary<(int, int), HashSet<int>>();
        for (int t = 0; t < m.TriCount; t++)
            for (int k = 0; k < 3; k++)
            {
                int u = m.Nb[t * 3 + k];
                if (u < 0 || own[u] == own[t]) continue;
                if (m.Dihedral(t, k) > 15) continue;
                var key = (Math.Min(own[t], own[u]), Math.Max(own[t], own[u]));
                if (!bnd.TryGetValue(key, out var set)) bnd[key] = set = new HashSet<int>();
                set.Add(m.Vx(t, k)); set.Add(m.Vx(t, (k + 1) % 3));
            }
        var done = new HashSet<int>();
        foreach (int i in Enumerable.Range(0, regs.Count).Where(i => regs[i].S.Kind != FitKind.Plane).OrderByDescending(i => regs[i].Area))
        {
            var samples = new List<(Vector3d Q, Vector3d N)>();
            foreach (var ((a, b), vs) in bnd)
            {
                if (a != i && b != i) continue;
                int j = a == i ? b : a;
                if (regs[i].Group >= 0 && regs[j].Group == regs[i].Group) continue;   // половина той же поверхности
                if (regs[j].S.Kind != FitKind.Plane && !done.Contains(j)) continue;   // образец — плоскость или уже выровненная
                var sj = regs[j].S; var si = regs[i].S;
                var qs = vs.Select(v => sj.Project(m.V[v])).ToList();
                bool tangent = qs.All(q => { var ni = si.Normal(q); var nj = sj.Normal(q); return !double.IsNaN(ni.X) && !double.IsNaN(nj.X) && Math.Abs(Vector3d.Dot(ni, nj)) > Math.Cos(3 * Math.PI / 180); });
                if (!tangent) { Notes.Add($"not tangent: {si} vs {sj}: worst {qs.Min(q => Math.Abs(Vector3d.Dot(si.Normal(q), sj.Normal(q)))):0.#####}"); continue; }
                foreach (var q in Fits.Pick(qs, 24)) samples.Add((q, sj.Normal(q)));
            }
            done.Add(i);
            if (samples.Count == 0) continue;
            // уже касается (в пределах точности координат) — не трогать: уточнение лишь размывает точную подгонку
            {
                var si0 = regs[i].S;
                double dmax = samples.Max(x => Math.Abs(si0.Distance(x.Q)));
                double amax = samples.Max(x => { var n0 = si0.Normal(x.Q); return double.IsNaN(n0.X) ? 0 : Vector3d.Cross(n0, x.N).Length; });
                if (dmax < 2e-7 * m.Diag && amax < 1e-6) continue;
            }
            var ns = RefineTangent(regs[i].S, verts[i], samples);
            if (ns != null && Holds(i, ns)) { ns.Canonical(); regs[i].S = ns; Tangents++; }
            else Notes.Add($"tangency refused for {regs[i].S}: samples {samples.Count}, max {(ns == null ? -1 : verts[i].Max(p => Math.Abs(ns.Distance(p)))):g3}, tan miss {(ns == null ? -1 : samples.Max(x => Math.Abs(ns.Distance(x.Q)))):g3}");
        }
    }

    /// <summary>Уточнение по вершинам и условиям касания (точка на поверхности, нормали параллельны), Левенберг — Марквардт.</summary>
    FitSurface? RefineTangent(FitSurface s, IReadOnlyList<Vector3d> pts0, List<(Vector3d Q, Vector3d N)> tan)
    {
        var pts = Fits.Pick(pts0, 200);
        double L = Math.Max(Fits.Span(pts), 1e-6);
        const double W = 30;
        int n = s.ParamCount;
        double[] Res(FitSurface f)
        {
            var r = new List<double>(pts.Count + tan.Count * 4);
            foreach (var p in pts) r.Add(f.Distance(p));
            foreach (var (q, nq) in tan)
            {
                r.Add(W * f.Distance(q));
                var nf = f.Normal(q);
                if (double.IsNaN(nf.X)) { r.Add(0); r.Add(0); r.Add(0); continue; }
                if (Vector3d.Dot(nf, nq) < 0) nf = -nf;
                var d = (nf - nq) * (W * L * 0.01);
                r.Add(d.X); r.Add(d.Y); r.Add(d.Z);
            }
            return r.ToArray();
        }
        double Cost(double[] r) => r.Sum(x => x * x);
        var r0 = Res(s); double cur = Cost(r0);
        double lambda = 1e-3;
        for (int it = 0; it < 40; it++)
        {
            var J = new double[r0.Length, n];
            for (int k = 0; k < n; k++)
            {
                double h = k < 2 || (s.Kind == FitKind.Cone && k == 5) ? 1e-7 : 1e-7 * L;
                var q = new double[n]; q[k] = h; var rp = Res(s.Moved(q));
                q[k] = -h; var rm = Res(s.Moved(q));
                for (int i = 0; i < r0.Length; i++) J[i, k] = (rp[i] - rm[i]) / (2 * h);
            }
            var jt = new double[n, n]; var g = new double[n];
            for (int i = 0; i < r0.Length; i++)
                for (int a = 0; a < n; a++) { g[a] += J[i, a] * r0[i]; for (int b = 0; b < n; b++) jt[a, b] += J[i, a] * J[i, b]; }
            bool ok = false;
            for (int tries = 0; tries < 8; tries++)
            {
                var mm = (double[,])jt.Clone();
                for (int a = 0; a < n; a++) mm[a, a] += lambda * Math.Max(jt[a, a], 1e-30);
                var step = Fits.Solve(mm, g.Select(x => -x).ToArray());
                if (step == null) { lambda *= 10; continue; }
                var cand = s.Moved(step);
                if (cand.R < 0 || cand.Kind == FitKind.Torus && cand.Rm <= 0 || cand.Kind == FitKind.Cone && (cand.R <= 1e-4 || cand.R >= Math.PI / 2 - 1e-4)) { lambda *= 10; continue; }
                var rc = Res(cand); double c = Cost(rc);
                if (c < cur) { s = cand; r0 = rc; cur = c; lambda = Math.Max(lambda * 0.3, 1e-12); ok = true; break; }
                lambda *= 10;
            }
            if (!ok) break;
        }
        return s;
    }

    // ------------------------------------------------------------------ соосность

    void CoaxialAxes()
    {
        var rev = Enumerable.Range(0, regs.Count).Where(i => regs[i].S.Revolves).OrderByDescending(i => regs[i].Area).ToList();
        var used = new HashSet<int>();
        foreach (int i in rev)
        {
            if (used.Contains(i)) continue;
            var a = regs[i].S;
            var group = new List<int> { i };
            foreach (int j in rev)
            {
                if (j == i || used.Contains(j)) continue;
                var b = regs[j].S;
                if (Math.Abs(Vector3d.Dot(a.A, b.A)) < Math.Cos(1e-3)) continue;
                if (LineDist(a.P, a.A, b.P) > 10 * tol) continue;
                group.Add(j);
            }
            if (group.Count < 2) continue;
            // общая прямая — прямая самой большой области (её подгонка точнее)
            var dir = a.A; var on = a.P;
            var applied = new List<(int, FitSurface)>();
            foreach (int j in group.Skip(1))
            {
                var ns = OnLine(j, on, Vector3d.Dot(regs[j].S.A, dir) >= 0 ? dir : -dir);
                if (ns != null) applied.Add((j, ns));
            }
            foreach (var (j, ns) in applied) { regs[j].S = ns; used.Add(j); Coaxial++; }
            used.Add(i);
        }
    }

    static double LineDist(Vector3d on, Vector3d dir, Vector3d p) { var d = p - on; return (d - dir * Vector3d.Dot(d, dir)).Length; }

    FitSurface? OnLine(int i, Vector3d on, Vector3d dir)
    {
        var s = regs[i].S; var pts = verts[i];
        FitSurface? ns = null;
        switch (s.Kind)
        {
            case FitKind.Cylinder:
                ns = s.Clone(); ns.A = dir; ns.P = on; ns.R = pts.Average(p => Fits.HeightRadius(p, on, dir).R); break;
            case FitKind.Cone:
                ns = Fits.ConeAbout(pts, on, dir); break;
            case FitKind.Torus:
                ns = Fits.TorusAbout(pts, on, dir);
                if (ns != null) ns = Fits.Refine(ns, pts, new[] { true, true, false, false, false, false, false });   // центр уточняется вдоль оси
                if (ns != null && LineDist(on, dir, ns.P) > tol * 0.01) { var c = ns.P; ns.P = on + dir * Vector3d.Dot(c - on, dir); }
                break;
        }
        if (ns == null || !Holds(i, ns)) return null;
        ns.Canonical();
        return ns;
    }

    // ------------------------------------------------------------------ числа

    void Values()
    {
        // общие оси (одна прямая у нескольких областей) округляются вместе
        var lines = new Dictionary<int, List<int>>();
        for (int i = 0; i < regs.Count; i++) if (regs[i].S.Revolves || regs[i].S.Kind == FitKind.Sphere)
        {
            int key = -1;
            foreach (var (k, l) in lines)
            {
                var a = regs[l[0]].S; var b = regs[i].S;
                if (a.Kind == FitKind.Sphere || b.Kind == FitKind.Sphere) { if ((a.P - b.P).Length < 1e-12) { key = k; break; } continue; }
                if (Math.Abs(Vector3d.Dot(a.A, b.A)) > 1 - 1e-12 && LineDist(a.P, a.A, b.P) < 1e-12) { key = k; break; }
            }
            if (key < 0) lines[key = lines.Count] = new List<int>();
            lines[key].Add(i);
        }
        foreach (var l in lines.Values) SnapAxisPoint(l);
        for (int i = 0; i < regs.Count; i++)
        {
            var s = regs[i].S;
            switch (s.Kind)
            {
                case FitKind.Plane:
                    if (WorldAxis(s.A) is int ax)
                    {
                        double off = Vector3d.Dot(s.P, s.A);
                        TrySnap(i, off, v => { var n = s.Clone(); n.P = n.A * v; return n; });
                    }
                    break;
                case FitKind.Cylinder:
                case FitKind.Sphere:
                    TrySnap(i, s.R, v => { var n = regs[i].S.Clone(); n.R = v; return n; });
                    break;
                case FitKind.Torus:
                    TrySnap(i, s.R, v => { var n = regs[i].S.Clone(); n.R = v; return n; });
                    TrySnap(i, regs[i].S.Rm, v => { var n = regs[i].S.Clone(); n.Rm = v; return n; });
                    break;
                case FitKind.Cone:
                {
                    // вершина на оси координат — её координата вдоль оси к круглому значению, угол — заново по точкам
                    if (WorldAxis(s.A) is int cax)
                    {
                        double v0 = cax == 0 ? s.P.X : cax == 1 ? s.P.Y : s.P.Z;
                        TrySnap(i, v0, v =>
                        {
                            var n = regs[i].S.Clone();
                            n.P = cax == 0 ? new Vector3d(v, n.P.Y, n.P.Z) : cax == 1 ? new Vector3d(n.P.X, v, n.P.Z) : new Vector3d(n.P.X, n.P.Y, v);
                            return Fits.Refine(n, verts[i], new[] { true, true, true, true, true, false });
                        });
                    }
                    var sc = regs[i].S;
                    double deg = sc.R * 180 / Math.PI;
                    foreach (double g in new[] { 5, 1, 0.5, 0.1 })
                    {
                        double v = Math.Round(deg / g) * g;
                        if (Math.Abs(v - deg) < 1e-12) break;
                        var n = sc.Clone(); n.R = v * Math.PI / 180;
                        var n2 = AdjustApex(i, n);
                        if (n2 != null && Holds(i, n2)) { regs[i].S = n2; ValueSnaps++; break; }
                    }
                    break;
                }
            }
        }
    }

    /// <summary>Конус с заданным углом: вершина сдвигается вдоль оси так, чтобы точки легли на него (среднее).</summary>
    FitSurface? AdjustApex(int i, FitSurface n)
    {
        // d = rho cos − z sin; сдвиг вершины на h по оси: z → z − h ⇒ d → d + h sin; h = −mean(d)/sin
        double mean = verts[i].Average(p => n.Distance(p));
        var r = n.Clone(); r.P = n.P - n.A * (mean / Math.Sin(n.R));
        return r;
    }

    static int? WorldAxis(Vector3d a)
    {
        if (Math.Abs(Math.Abs(a.X) - 1) < 1e-14) return 0;
        if (Math.Abs(Math.Abs(a.Y) - 1) < 1e-14) return 1;
        if (Math.Abs(Math.Abs(a.Z) - 1) < 1e-14) return 2;
        return null;
    }

    /// <summary>Округлить величину v области i: самым крупным шагом, при котором поверхность держит вершины.</summary>
    void TrySnap(int i, double v, Func<double, FitSurface> with)
    {
        foreach (double g in Grids)
        {
            double r = Math.Round(v / g) * g;
            if (Math.Abs(r - v) > SnapLimit(i)) continue;
            if (Math.Abs(r - v) < 1e-13) return;
            var ns = with(r);
            if (Holds(i, ns)) { regs[i].S = ns; ValueSnaps++; return; }
        }
    }

    /// <summary>Точка общей оси (или центр сфер): координаты поперёк оси — к круглым значениям, для всех областей сразу.</summary>
    void SnapAxisPoint(List<int> group)
    {
        var s0 = regs[group[0]].S;
        var p = s0.P;
        int? ax = s0.Kind == FitKind.Sphere ? null : WorldAxis(s0.A);
        for (int c = 0; c < 3; c++)
        {
            if (ax == c) continue;                       // вдоль оси координата точки ничего не значит
            if (s0.Kind != FitKind.Sphere && ax == null) return;   // наклонная ось: точку не округляем
            double v = c == 0 ? p.X : c == 1 ? p.Y : p.Z;
            foreach (double g in Grids)
            {
                double r = Math.Round(v / g) * g;
                if (Math.Abs(r - v) > group.Min(SnapLimit)) continue;
                if (Math.Abs(r - v) < 1e-13) break;
                var delta = c == 0 ? new Vector3d(r - v, 0, 0) : c == 1 ? new Vector3d(0, r - v, 0) : new Vector3d(0, 0, r - v);
                var moved = group.Select(i => { var n = regs[i].S.Clone(); n.P += delta; return n; }).ToList();
                if (group.Select((i, k) => Holds(i, moved[k])).All(x => x))
                {
                    for (int k = 0; k < group.Count; k++) regs[group[k]].S = moved[k];
                    p += delta; ValueSnaps++;
                    break;
                }
            }
        }
    }
}
