using System.Globalization;
using System.Text;
using OpenTK.Mathematics;

namespace Parametric3D.Kernel.MeshFit;

/// <summary>
/// ТЕЛО ИЗ ОБЛАСТЕЙ (k119): границы областей → вершины и рёбра → точные кривые → текст STEP (точный B-rep), который
/// читает тот же импорт, что и чужие файлы (один проверенный путь сборки тела, а не второй построитель).
///
/// Граница области — стороны треугольников, за которыми другая область; полурёбра связываются в контуры поворотом
/// вокруг конечной вершины по треугольникам своей области. Где область «справа» меняется вдоль контура — угол тела
/// (вершина). Замкнутая граница без углов (кромка отверстия) делится двумя вершинами: наименьший номер и самая дальняя
/// от неё точка — их видят обе стороны одинаково. Вершина ставится в точку, лежащую на всех своих поверхностях
/// (наименьшие квадраты на поводке у вершины сетки). Ребро: прямая, окружность или эллипс, если на них ложатся все точки
/// цепочки и они лежат на обеих поверхностях; иначе — ломаная из точек цепочки, спроектированных на пересечение обеих
/// поверхностей и сгущённая до допуска (у QymCAD ломаная остаётся по точкам сетки).
/// </summary>
public sealed class BRepFromRegions
{
    readonly PreparedMesh m;
    readonly List<FitRegion> regs;
    readonly int[] owner;
    readonly double tol;
    public int Lines, Circles, Ellipses, Polylines, ArtificialCorners, LeashFallbacks;
    public readonly List<string> LeashNotes = new();
    public string Problem = "";

    public BRepFromRegions(PreparedMesh mesh, List<FitRegion> regions, double tol)
    {
        m = mesh; regs = regions; this.tol = tol;
        owner = new int[m.TriCount];
        for (int i = 0; i < regs.Count; i++) foreach (int t in regs[i].Tris) owner[t] = i;
    }

    // ------------------------------------------------------------------ контуры

    readonly record struct Half(int T, int K)
    {
    }

    int From(Half h) => m.Vx(h.T, h.K);
    int To(Half h) => m.Vx(h.T, (h.K + 1) % 3);
    int Right(Half h) { int u = m.Nb[h.T * 3 + h.K]; return u < 0 ? -1 : owner[u]; }
    bool IsBoundary(int t, int k) { int u = m.Nb[t * 3 + k]; return u < 0 || owner[u] != owner[t]; }

    /// <summary>Следующее полуребро контура: поворот вокруг конца по треугольникам своей области.</summary>
    Half Next(Half h)
    {
        int b = To(h);
        int t = h.T; int k = (h.K + 1) % 3;   // сторона t, начинающаяся в b
        for (int guard = 0; guard < 4096; guard++)
        {
            if (IsBoundary(t, k)) return new Half(t, k);
            int u = m.Nb[t * 3 + k];
            // в u сторона идёт из (вершина после b в t) в b; нужна сторона u, начинающаяся в b
            int kk = -1;
            for (int j = 0; j < 3; j++) if (m.Vx(u, j) == b) kk = j;
            t = u; k = kk;
        }
        throw new InvalidOperationException("mesh fit: loop walk did not close");
    }

    List<List<Half>>[] Loops()
    {
        var loops = new List<List<Half>>[regs.Count];
        for (int i = 0; i < regs.Count; i++) loops[i] = new List<List<Half>>();
        var used = new HashSet<Half>();
        for (int t = 0; t < m.TriCount; t++)
            for (int k = 0; k < 3; k++)
            {
                if (!IsBoundary(t, k)) continue;
                var h0 = new Half(t, k);
                if (used.Contains(h0)) continue;
                var loop = new List<Half>();
                var h = h0;
                do
                {
                    if (!used.Add(h)) throw new InvalidOperationException("mesh fit: loops cross");
                    loop.Add(h);
                    h = Next(h);
                } while (h != h0 && loop.Count < 10_000_000);
                loops[owner[t]].Add(loop);
            }
        return loops;
    }

    // ------------------------------------------------------------------ сборка

    sealed class Chain { public List<int> Vs = new(); public int Left, Right; public int Edge = -1; public bool Forward; }

    sealed class EdgeGeo
    {
        public int V0, V1;            // номера углов
        public List<Vector3d> Pts = new();   // точки от V0 к V1 (с концами)
        public string Kind = "line";
        public Vector3d C, N, U; public double R1, R2;   // окружность/эллипс: центр, ось (против часовой от начала через середину), опорное направление
    }

    public string? Build(string name)
    {
        var loops = Loops();
        // углы
        var corner = new HashSet<int>();
        void MarkChanges()
        {
            for (int i = 0; i < regs.Count; i++)
                foreach (var loop in loops[i])
                {
                    var seen = new HashSet<int>();
                    for (int j = 0; j < loop.Count; j++)
                    {
                        var a = loop[j]; var b = loop[(j + 1) % loop.Count];
                        if (Right(a) != Right(b)) corner.Add(To(a));
                        if (!seen.Add(To(a))) corner.Add(To(a));   // область касается себя в вершине
                    }
                }
        }
        MarkChanges();
        // разрезание: у каждой цепочки — разные концы
        for (int pass = 0; pass < 8; pass++)
        {
            bool added = false;
            for (int i = 0; i < regs.Count; i++)
                foreach (var loop in loops[i])
                {
                    var vs = loop.Select(From).ToList();
                    var cs = vs.Where(corner.Contains).ToList();
                    if (cs.Count >= 2) continue;
                    // замкнутая цепочка или с одним углом: разрезать в самой дальней точке (и в наименьшем номере, если углов нет)
                    int a0 = cs.Count == 1 ? cs[0] : vs.Min();
                    var pa = m.V[a0];
                    int far = vs.OrderByDescending(v => (m.V[v] - pa).LengthSquared).ThenBy(v => v).First();
                    if (cs.Count == 0) corner.Add(a0);
                    corner.Add(far); ArtificialCorners += cs.Count == 0 ? 2 : 1;
                    added = true;
                }
            // цепочка между углами, у которой начало = конец (два угла, но одна цепочка проходит петлёй) — тоже режется
            if (!added) break;
        }

        // цепочки
        var chains = new List<Chain>();
        var regionLoops = new List<List<Chain>>[regs.Count];
        for (int i = 0; i < regs.Count; i++)
        {
            regionLoops[i] = new List<List<Chain>>();
            foreach (var loop in loops[i])
            {
                int start = loop.FindIndex(h => corner.Contains(From(h)));
                if (start < 0) { Problem = "a loop without corners"; return null; }
                var lc = new List<Chain>();
                Chain? cur = null;
                for (int j = 0; j < loop.Count; j++)
                {
                    var h = loop[(start + j) % loop.Count];
                    if (corner.Contains(From(h)))
                    {
                        cur = new Chain { Left = i, Right = Right(h) };
                        cur.Vs.Add(From(h));
                        lc.Add(cur);
                    }
                    cur!.Vs.Add(To(h));
                }
                regionLoops[i].Add(lc);
                chains.AddRange(lc);
            }
        }
        // общие рёбра: цепочка и её обратная у соседа
        var edgeKey = new Dictionary<(int, int, int), int>();
        var edges = new List<EdgeGeo>();
        foreach (var c in chains)
        {
            if (c.Vs[0] == c.Vs[^1] && c.Vs.Count > 2) { Problem = "a closed chain"; return null; }
            var fwd = (c.Vs[0], c.Vs[1], c.Vs[^1]);
            var bwd = (c.Vs[^1], c.Vs[^2], c.Vs[0]);
            if (edgeKey.TryGetValue(bwd, out int e)) { c.Edge = e; c.Forward = false; continue; }
            if (edgeKey.TryGetValue(fwd, out e)) { Problem = "a chain used twice in one direction"; return null; }
            edgeKey[fwd] = edges.Count; c.Edge = edges.Count; c.Forward = true;
            edges.Add(new EdgeGeo { V0 = c.Vs[0], V1 = c.Vs[^1] });
        }

        // вершины
        var cpos = new Dictionary<int, Vector3d>();
        foreach (int v in corner) cpos[v] = PlaceCorner(v);

        // кривые рёбер
        foreach (var c in chains.Where(c => c.Forward))
        {
            var e = edges[c.Edge];
            var sa = regs[c.Left].S; FitSurface? sb = c.Right >= 0 ? regs[c.Right].S : null;
            var pts = new List<Vector3d> { cpos[c.Vs[0]] };
            for (int j = 1; j + 1 < c.Vs.Count; j++) pts.Add(OnBoth(m.V[c.Vs[j]], sa, sb));
            pts.Add(cpos[c.Vs[^1]]);
            e.Pts = pts;
            FitCurve(e, sa, sb);
        }

        // текст STEP
        return WriteStep(name, regionLoops, edges, cpos);
    }

    // ------------------------------------------------------------------ точки

    Dictionary<int, HashSet<int>>? _regsAt;
    HashSet<int> RegionsAt(int v)
    {
        if (_regsAt == null)
        {
            _regsAt = new Dictionary<int, HashSet<int>>();
            for (int t = 0; t < m.TriCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int x = m.Vx(t, k);
                    if (!_regsAt.TryGetValue(x, out var s)) _regsAt[x] = s = new HashSet<int>();
                    s.Add(owner[t]);
                }
        }
        return _regsAt[v];
    }

    Vector3d PlaceCorner(int v)
    {
        var p0 = m.V[v];
        var ss = RegionsAt(v).Select(i => regs[i].S).ToList();
        var p = Settle(p0, ss);
        double start = ss.Max(s => Math.Abs(s.Distance(p0)));
        double after = ss.Max(s => Math.Abs(s.Distance(p)));
        // поводок: у касающихся граней точка вдоль касания уходит на √(2·допуск·R) — это не «вскользь», а норма
        double rmax = ss.Where(x => x.Kind != FitKind.Plane).Select(x => x.Kind == FitKind.Torus ? x.R + x.Rm : x.Kind == FitKind.Cone ? m.Diag * 0.05 : x.R).DefaultIfEmpty(0).Max();
        double leash = Math.Max(Math.Max(10 * tol, 4 * start + tol), 3 * Math.Sqrt(2 * tol * Math.Min(rmax, m.Diag)));
        if ((p - p0).Length > leash || after > 1e-7)
        {
            LeashFallbacks++;
            LeashNotes.Add($"corner {v} at {p0}: kinds {string.Join(",", ss.Select(x => x.ToString()))}; start {start:g3} moved {(p - p0).Length:g3} after {after:g3}");
            return p0;   // грани сходятся вскользь: точка уходит — остаётся у вершины сетки
        }
        return p;
    }

    /// <summary>Точка у p, лежащая на всех поверхностях (наименьшие квадраты, шаг наименьшей длины).</summary>
    static Vector3d Settle(Vector3d p, List<FitSurface> ss)
    {
        for (int it = 0; it < 30; it++)
        {
            int n = ss.Count;
            var d = new double[n]; var J = new Vector3d[n];
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                d[i] = ss[i].Distance(p);
                var g = ss[i].Normal(p);
                J[i] = double.IsNaN(g.X) ? Vector3d.Zero : g;
                worst = Math.Max(worst, Math.Abs(d[i]));
            }
            if (worst < 1e-12) break;
            // Δ = −Jᵀ (J Jᵀ + μ I)⁻¹ d  (n×n), при n > 3 — нормальные уравнения 3×3
            Vector3d step;
            if (n <= 3)
            {
                var a = new double[n, n];
                for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) a[i, j] = Vector3d.Dot(J[i], J[j]) + (i == j ? 1e-12 : 0);
                var y = Fits.Solve(a, d.Select(x => -x).ToArray());
                if (y == null) break;
                step = Vector3d.Zero; for (int i = 0; i < n; i++) step += J[i] * y[i];
            }
            else
            {
                var a = new double[3, 3]; var b = new double[3];
                for (int i = 0; i < n; i++)
                {
                    var g = J[i];
                    double[] gr = { g.X, g.Y, g.Z };
                    for (int r = 0; r < 3; r++) { b[r] -= gr[r] * d[i]; for (int c = 0; c < 3; c++) a[r, c] += gr[r] * gr[c]; }
                }
                for (int r = 0; r < 3; r++) a[r, r] += 1e-12;
                var y = Fits.Solve(a, b);
                if (y == null) break;
                step = new Vector3d(y[0], y[1], y[2]);
            }
            p += step;
            if (step.Length < 1e-14) break;
        }
        return p;
    }

    static Vector3d OnBoth(Vector3d p, FitSurface a, FitSurface? b) => b == null ? a.Project(p) : Settle(p, new List<FitSurface> { a, b });

    // ------------------------------------------------------------------ кривые

    void FitCurve(EdgeGeo e, FitSurface sa, FitSurface? sb)
    {
        var pts = e.Pts;
        var a = pts[0]; var z = pts[^1];
        double len = (z - a).Length;
        // прямая
        if (len > tol && pts.All(p => LineDev(p, a, z) <= tol) && Holds(sa, sb, i => a + (z - a) * i))
        { e.Kind = "line"; Lines++; return; }
        // две плоскости пересекаются только по прямой
        if (sb != null && sa.Kind == FitKind.Plane && sb.Kind == FitKind.Plane) goto poly;
        // окружность: плоскость точек и окружность в ней; аналитически — для плоскости поперёк оси и соосных поверхностей
        if (CircleOf(sa, sb, pts) is { } circ)
        {
            var (c, n, r) = circ;
            if (ArcFrom(e, c, n, r) && pts.All(p => Math.Abs((p - c - n * Vector3d.Dot(p - c, n)).Length - r) <= tol && Math.Abs(Vector3d.Dot(p - c, n)) <= tol)
                && Holds(sa, sb, t => ArcPoint(e, t)))
            { e.Kind = "circle"; Circles++; return; }
        }
        if (EllipseOf(sa, sb, pts) is { } el)
        {
            e.Kind = "ellipse"; e.C = el.C; e.N = el.N; e.U = el.U; e.R1 = el.A; e.R2 = el.B;
            if (OrientEllipse(e) && pts.All(p => EllipseDev(e, p) <= tol) && Holds(sa, sb, t => EllipsePoint(e, t)))
            { Ellipses++; return; }
        }
        // ломаная по пересечению, сгущённая до допуска
        poly:
        e.Kind = "polyline";
        var dense = new List<Vector3d> { pts[0] };
        for (int i = 1; i < pts.Count; i++) Densify(dense, pts[i - 1], pts[i], sa, sb, 0);
        e.Pts = dense;
        Polylines++;
    }

    void Densify(List<Vector3d> outp, Vector3d p, Vector3d q, FitSurface sa, FitSurface? sb, int depth)
    {
        var mid = (p + q) / 2;
        double dev = Math.Max(Math.Abs(sa.Distance(mid)), sb == null ? 0 : Math.Abs(sb.Distance(mid)));
        if (depth < 6 && dev > tol * 0.25 && (q - p).Length > tol)
        {
            var mm = OnBoth(mid, sa, sb);
            if ((mm - mid).Length < (q - p).Length)
            {
                Densify(outp, p, mm, sa, sb, depth + 1);
                Densify(outp, mm, q, sa, sb, depth + 1);
                return;
            }
        }
        outp.Add(q);
    }

    bool Holds(FitSurface sa, FitSurface? sb, Func<double, Vector3d> at)
    {
        for (int i = 1; i < 16; i++)
        {
            var p = at(i / 16.0);
            if (Math.Abs(sa.Distance(p)) > tol || sb != null && Math.Abs(sb.Distance(p)) > tol) return false;
        }
        return true;
    }

    static double LineDev(Vector3d p, Vector3d a, Vector3d z)
    {
        var d = z - a; double l2 = d.LengthSquared;
        if (l2 < 1e-300) return (p - a).Length;
        double t = Vector3d.Dot(p - a, d) / l2;
        return (p - (a + d * t)).Length;
    }

    (Vector3d C, Vector3d N, double R)? CircleOf(FitSurface sa, FitSurface? sb, List<Vector3d> pts)
    {
        if (sb != null)
        {
            // плоскость поперёк оси (или сфера) и тело вращения: центр — на оси
            foreach (var (pl, rv) in new[] { (sa, sb), (sb, sa) })
            {
                if (pl.Kind != FitKind.Plane) continue;
                if (rv.Kind == FitKind.Sphere)
                {
                    var c = rv.P - pl.A * pl.Distance(rv.P);
                    return (c, pl.A, pts.Average(p => (p - c).Length));
                }
                if (rv.Revolves && Math.Abs(Vector3d.Dot(pl.A, rv.A)) > 1 - 1e-9)
                {
                    double t = -pl.Distance(rv.P) / Vector3d.Dot(pl.A, rv.A);
                    var c = rv.P + rv.A * t;
                    return (c, rv.A, pts.Average(p => (p - c).Length));
                }
            }
            // соосные тела вращения (и сфера с центром на оси)
            FitSurface? ax = sa.Revolves ? sa : sb.Revolves ? sb : null;
            if (ax != null)
            {
                var other = ReferenceEquals(ax, sa) ? sb : sa;
                bool coax = other.Kind == FitKind.Sphere ? LineDistP(ax, other.P) < tol
                          : other.Revolves && Math.Abs(Vector3d.Dot(other.A, ax.A)) > 1 - 1e-9 && LineDistP(ax, other.P) < tol;
                if (coax)
                {
                    double z = pts.Average(p => Vector3d.Dot(p - ax.P, ax.A));
                    var c = ax.P + ax.A * z;
                    return (c, ax.A, pts.Average(p => Fits.HeightRadius(p, ax.P, ax.A).R));
                }
            }
        }
        // общий случай: окружность по точкам (не менее трёх)
        if (pts.Count < 3) return null;
        var pl2 = Fits.Plane(pts, Vector3d.UnitZ);
        if (pl2 == null || pts.Any(p => Math.Abs(pl2.Distance(p)) > tol)) return null;
        var cyl = Fits.CylinderAbout(pts, pl2.A);
        if (cyl == null) return null;
        var cc = cyl.P + pl2.A * Vector3d.Dot(pl2.P - cyl.P, pl2.A);
        return (cc, pl2.A, cyl.R);
    }

    static double LineDistP(FitSurface ax, Vector3d p) { var d = p - ax.P; return (d - ax.A * Vector3d.Dot(d, ax.A)).Length; }

    /// <summary>Окружность от начала ребра через его середину к концу: ось n (против часовой); false — дуга не выходит.</summary>
    bool ArcFrom(EdgeGeo e, Vector3d c, Vector3d n, double r)
    {
        if (r < tol) return false;
        var a = e.Pts[0]; var z = e.Pts[^1]; var mid = e.Pts[e.Pts.Count / 2];
        if (e.Pts.Count == 2) mid = (a + z) / 2;
        var u = (a - c - n * Vector3d.Dot(a - c, n));
        if (u.Length < 1e-12) return false;
        u.Normalize();
        double Ang(Vector3d p, Vector3d nn) { var w = Vector3d.Cross(nn, u); var d = p - c; double t = Math.Atan2(Vector3d.Dot(d, w), Vector3d.Dot(d, u)); return t < 0 ? t + 2 * Math.PI : t; }
        foreach (var nn in new[] { n, -n })
        {
            double am = Ang(mid, nn), az = Ang(z, nn);
            if (az < 1e-9) az = 2 * Math.PI;
            if (am < az) { e.C = c; e.N = nn; e.U = u; e.R1 = r; _sweep[e] = az; return true; }
        }
        return false;
    }
    readonly Dictionary<EdgeGeo, double> _sweep = new();
    Vector3d ArcPoint(EdgeGeo e, double t)
    {
        double a = _sweep[e] * t; var w = Vector3d.Cross(e.N, e.U);
        return e.C + (e.U * Math.Cos(a) + w * Math.Sin(a)) * e.R1;
    }

    (Vector3d C, Vector3d N, Vector3d U, double A, double B)? EllipseOf(FitSurface sa, FitSurface? sb, List<Vector3d> pts)
    {
        if (sb == null) return null;
        foreach (var (pl, cy) in new[] { (sa, sb), (sb, sa) })
        {
            if (pl.Kind != FitKind.Plane || cy.Kind != FitKind.Cylinder) continue;
            double cosA = Vector3d.Dot(pl.A, cy.A);
            if (Math.Abs(cosA) < 1e-6 || Math.Abs(cosA) > 1 - 1e-9) continue;
            double t = -pl.Distance(cy.P) / cosA;
            var c = cy.P + cy.A * t;
            var minor = Vector3d.Cross(pl.A, cy.A).Normalized();
            var major = Vector3d.Cross(pl.A, minor).Normalized();
            return (c, pl.A, major, cy.R / Math.Abs(cosA), cy.R);
        }
        return null;
    }

    bool OrientEllipse(EdgeGeo e)
    {
        var a = e.Pts[0]; var z = e.Pts[^1]; var mid = e.Pts.Count == 2 ? (a + z) / 2 : e.Pts[e.Pts.Count / 2];
        foreach (var nn in new[] { e.N, -e.N })
        {
            var w = Vector3d.Cross(nn, e.U);
            double Ang(Vector3d p) { var d = p - e.C; double t = Math.Atan2(Vector3d.Dot(d, w) / e.R2, Vector3d.Dot(d, e.U) / e.R1); return t; }
            double a0 = Ang(a), am = Ang(mid), az = Ang(z);
            double M(double x) { x -= a0; while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
            double sm = M(am), sz = M(az); if (sz < 1e-9) sz = 2 * Math.PI;
            if (sm < sz) { e.N = nn; _ell[e] = (a0, sz); return true; }
        }
        return false;
    }
    readonly Dictionary<EdgeGeo, (double A0, double Sweep)> _ell = new();
    Vector3d EllipsePoint(EdgeGeo e, double t)
    {
        var (a0, sw) = _ell[e]; double a = a0 + sw * t; var w = Vector3d.Cross(e.N, e.U);
        return e.C + e.U * (e.R1 * Math.Cos(a)) + w * (e.R2 * Math.Sin(a));
    }
    double EllipseDev(EdgeGeo e, Vector3d p)
    {
        var w = Vector3d.Cross(e.N, e.U); var d = p - e.C;
        double off = Math.Abs(Vector3d.Dot(d, e.N));
        double x = Vector3d.Dot(d, e.U) / e.R1, y = Vector3d.Dot(d, w) / e.R2;
        double rr = Math.Sqrt(x * x + y * y);
        return Math.Max(off, Math.Abs(rr - 1) * Math.Min(e.R1, e.R2));
    }

    // ------------------------------------------------------------------ STEP

    string WriteStep(string name, List<List<Chain>>[] regionLoops, List<EdgeGeo> edges, Dictionary<int, Vector3d> cpos)
    {
        var sb = new StringBuilder();
        int id = 0;
        var lines = new List<string>();
        int E(string s) { lines.Add($"#{++id}={s};"); return id; }
        static string N(double v) { var t = v.ToString("0.###############", CultureInfo.InvariantCulture); return t.Contains('.') || t.Contains('E') ? t : t + "."; }
        int Pt(Vector3d p) => E($"CARTESIAN_POINT('',({N(p.X)},{N(p.Y)},{N(p.Z)}))");
        int Dir(Vector3d d) { d.Normalize(); return E($"DIRECTION('',({N(d.X)},{N(d.Y)},{N(d.Z)}))"); }
        int Ax(Vector3d o, Vector3d z, Vector3d x)
        {
            z.Normalize(); x -= z * Vector3d.Dot(x, z);
            if (x.Length < 1e-9) x = FitSurface.Perp(z).U;
            return E($"AXIS2_PLACEMENT_3D('',#{Pt(o)},#{Dir(z)},#{Dir(x)})");
        }

        var vid = new Dictionary<int, int>();
        int Vtx(int v) => vid.TryGetValue(v, out int x) ? x : vid[v] = E($"VERTEX_POINT('',#{Pt(cpos[v])})");

        var eid = new int[edges.Count];
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            int curve;
            switch (e.Kind)
            {
                case "line":
                {
                    var d = e.Pts[^1] - e.Pts[0];
                    curve = E($"LINE('',#{Pt(e.Pts[0])},#{E($"VECTOR('',#{Dir(d)},{N(d.Length)})")})");
                    break;
                }
                case "circle":
                    curve = E($"CIRCLE('',#{Ax(e.C, e.N, e.U)},{N(e.R1)})"); break;
                case "ellipse":
                    curve = E($"ELLIPSE('',#{Ax(e.C, e.N, e.U)},{N(e.R1)},{N(e.R2)})"); break;
                default:
                    curve = E($"POLYLINE('',({string.Join(",", e.Pts.Select(p => "#" + Pt(p)))}))"); break;
            }
            eid[i] = E($"EDGE_CURVE('',#{Vtx(e.V0)},#{Vtx(e.V1)},#{curve},.T.)");
        }

        var faces = new List<int>();
        for (int r = 0; r < regs.Count; r++)
        {
            var reg = regs[r]; var s = reg.S;
            int sense = SenseOf(reg);
            int surf = s.Kind switch
            {
                FitKind.Plane => E($"PLANE('',#{Ax(s.P, s.A, FitSurface.Perp(s.A).U)})"),
                FitKind.Cylinder => E($"CYLINDRICAL_SURFACE('',#{Ax(s.P, s.A, FitSurface.Perp(s.A).U)},{N(s.R)})"),
                FitKind.Sphere => E($"SPHERICAL_SURFACE('',#{Ax(s.P, Vector3d.UnitZ, Vector3d.UnitX)},{N(s.R)})"),
                FitKind.Torus => E($"TOROIDAL_SURFACE('',#{Ax(s.P, s.A, FitSurface.Perp(s.A).U)},{N(s.R)},{N(s.Rm)})"),
                _ => ConeEntity(reg),
            };
            int ConeEntity(FitRegion rg)
            {
                double zm = rg.Tris.Average(t => Vector3d.Dot((m.P(t, 0) + m.P(t, 1) + m.P(t, 2)) / 3 - rg.S.P, rg.S.A));
                if (zm <= 0) zm = 1;
                var o = rg.S.P + rg.S.A * zm;
                return E($"CONICAL_SURFACE('',#{Ax(o, rg.S.A, FitSurface.Perp(rg.S.A).U)},{N(zm * Math.Tan(rg.S.R))},{N(rg.S.R)})");
            }
            // контуры: внешний — первым (у плоскости — наибольшей положительной площади вокруг наружной нормали)
            var ls = regionLoops[r].ToList();
            if (s.Kind == FitKind.Plane && ls.Count > 1)
            {
                var nOut = s.A * sense;
                ls = ls.OrderByDescending(l => LoopArea(l, cpos, nOut)).ToList();
            }
            else if (ls.Count > 1) ls = ls.OrderByDescending(l => l.Sum(c => c.Vs.Count)).ToList();
            var bounds = new List<int>();
            for (int li = 0; li < ls.Count; li++)
            {
                var oes = ls[li].Select(c => E($"ORIENTED_EDGE('',*,*,#{eid[c.Edge]},{(c.Forward ? ".T." : ".F.")})")).ToList();
                int loop = E($"EDGE_LOOP('',({string.Join(",", oes.Select(x => "#" + x))}))");
                bounds.Add(E($"{(li == 0 ? "FACE_OUTER_BOUND" : "FACE_BOUND")}('',#{loop},.T.)"));
            }
            faces.Add(E($"ADVANCED_FACE('',({string.Join(",", bounds.Select(x => "#" + x))}),#{surf},{(sense > 0 ? ".T." : ".F.")})"));
        }
        int shell = E($"CLOSED_SHELL('',({string.Join(",", faces.Select(x => "#" + x))}))");
        E($"MANIFOLD_SOLID_BREP('{Esc(name)}',#{shell})");

        sb.AppendLine("ISO-10303-21;");
        sb.AppendLine("HEADER;");
        sb.AppendLine("FILE_DESCRIPTION(('Parametric3D mesh fit'),'2;1');");
        sb.AppendLine($"FILE_NAME('{Esc(name)}','',(''),(''),'Parametric3D','Parametric3D','');");
        sb.AppendLine("FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));");
        sb.AppendLine("ENDSEC;");
        sb.AppendLine("DATA;");
        foreach (var l in lines) sb.AppendLine(l);
        sb.AppendLine("ENDSEC;");
        sb.AppendLine("END-ISO-10303-21;");
        return sb.ToString();
    }

    static string Esc(string s) => s.Replace("'", "''");

    int SenseOf(FitRegion r)
    {
        double a = 0;
        foreach (int t in r.Tris)
        {
            var c = (m.P(t, 0) + m.P(t, 1) + m.P(t, 2)) / 3;
            var n = r.S.Normal(c);
            if (double.IsNaN(n.X)) continue;
            a += Vector3d.Dot(n, m.TN[t]) * m.TA[t];
        }
        return a >= 0 ? 1 : -1;
    }

    double LoopArea(List<Chain> loop, Dictionary<int, Vector3d> cpos, Vector3d n)
    {
        var pts = new List<Vector3d>();
        foreach (var c in loop) for (int j = 0; j + 1 < c.Vs.Count; j++) pts.Add(m.V[c.Vs[j]]);
        var s = Vector3d.Zero;
        for (int i = 0; i < pts.Count; i++) s += Vector3d.Cross(pts[i], pts[(i + 1) % pts.Count]);
        return Vector3d.Dot(s, n) * 0.5;
    }
}
