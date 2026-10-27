using OpenTK.Mathematics;

namespace Parametric3D.Kernel.MeshFit;

/// <summary>Область сетки: её треугольники и поверхность, на которой они лежат.</summary>
public sealed class FitRegion
{
    public List<int> Tris = new();
    public FitSurface S = null!;
    /// <summary>Каноничная нормаль поверхности смотрит наружу тела (+1) или внутрь (−1, отверстие).</summary>
    public int Sense = 1;
    public bool Alive = true;
    public double Area;
    /// <summary>Части одной поверхности (половины целой сферы, куски, распавшиеся при слиянии): номер общей группы или −1.</summary>
    public int Group = -1;
}

/// <summary>
/// РАЗБИЕНИЕ СЕТКИ НА ОБЛАСТИ ПОВЕРХНОСТЕЙ (k119). По одному углу между треугольниками грани не отделить: скругление
/// касается соседних граней, и любой порог, сохраняющий скругление целым, склеивает его с ними. Поэтому область растёт
/// ВМЕСТЕ со своей поверхностью: от затравки (сначала крупные треугольники) пробуются плоскость, цилиндры (ось — по
/// нормалям затравки и каждого соседа) и сфера; каждая растёт по соседям, все вершины которых лежат на ней в пределах
/// допуска, с переподгонкой при каждом удвоении; берётся покрывшая больше площади (кривой — в полтора раза больше
/// плоскости). Потом соседние кривые области, вместе лежащие на одной поверхности (цилиндр, конус, тор, сфера; ось — по
/// прямым нормалей, Плюккер), сливаются — так собираются конусы и торы из полос; мелкие области, лежащие на поверхности
/// соседа, присоединяются к нему.
/// </summary>
public sealed class Segmenter
{
    readonly PreparedMesh m;
    public readonly double Tol;
    public double AngleTolDeg = 12, SharpDeg = 50;
    /// <summary>Только плоскости (импорт без преобразования: плоские грани — гранями, кривое остаётся гранёным).</summary>
    public bool PlanesOnly;
    readonly int[] owner;
    public readonly List<FitRegion> Regions = new();
    readonly List<int>[] vt;
    public int Candidates, Merges, Absorbed;
    public Action<string>? Trace, Trace2;
    public static readonly bool MergeOff = Environment.GetEnvironmentVariable("P3D_MESHFITMERGE") == "0";

    public Segmenter(PreparedMesh mesh, double tol)
    {
        m = mesh; Tol = tol;
        owner = Enumerable.Repeat(-1, m.TriCount).ToArray();
        vt = new List<int>[m.V.Length];
        for (int i = 0; i < vt.Length; i++) vt[i] = new List<int>(6);
        for (int t = 0; t < m.TriCount; t++) for (int k = 0; k < 3; k++) vt[m.Vx(t, k)].Add(t);
    }

    public int Owner(int t) => owner[t];

    public void Run(Func<bool>? stop = null)
    {
        var seeds = Enumerable.Range(0, m.TriCount).OrderByDescending(t => m.TA[t]).ToList();
        foreach (int seed in seeds)
        {
            if (owner[seed] >= 0) continue;
            if (stop?.Invoke() == true) return;
            var best = BestFrom(seed);
            int id = Regions.Count;
            best.Area = best.Tris.Sum(t => m.TA[t]);
            Regions.Add(best);
            foreach (int t in best.Tris) owner[t] = id;
        }
        Trace2?.Invoke($"  seeds done: regions {Regions.Count}, candidates {Candidates}");
        if (!MergeOff && !PlanesOnly) MergeRegions();
        Trace2?.Invoke($"  merges done: {Merges}");
        if (!MergeOff) Absorb();
        Trace2?.Invoke($"  absorb done: {Absorbed}");
        Compact();
    }

    // ------------------------------------------------------------------ рост

    bool OnSurface(int t, FitSurface s, int sense)
    {
        for (int k = 0; k < 3; k++) if (Math.Abs(s.Distance(m.P(t, k))) > Tol) return false;
        var c = (m.P(t, 0) + m.P(t, 1) + m.P(t, 2)) / 3;
        var n = s.Normal(s.Project(c));
        if (double.IsNaN(n.X)) return true;
        double cos = Vector3d.Dot(n, m.TN[t]) * sense;
        return cos >= Math.Cos(AngleTolDeg * Math.PI / 180);
    }

    int SenseOf(int t, FitSurface s)
    {
        var c = (m.P(t, 0) + m.P(t, 1) + m.P(t, 2)) / 3;
        var n = s.Normal(c);
        if (double.IsNaN(n.X)) return 1;
        return Vector3d.Dot(n, m.TN[t]) >= 0 ? 1 : -1;
    }

    bool Sharp(int t, int k) { double d = m.Dihedral(t, k); return d < 0 || d > SharpDeg; }

    /// <summary>Рост области от затравки по поверхности s (переподгонка при удвоении); null — затравка не на ней.</summary>
    FitRegion? Grow(int seed, FitSurface s, bool refit, int limitTris = int.MaxValue)
    {
        int sense = SenseOf(seed, s);
        if (!OnSurface(seed, s, sense)) return null;
        var mark = new HashSet<int> { seed };
        var tris = new List<int> { seed };
        var q = new Queue<int>(); q.Enqueue(seed);
        int fitAt = 8;
        while (q.Count > 0)
        {
            int t = q.Dequeue();
            for (int k = 0; k < 3; k++)
            {
                if (Sharp(t, k)) continue;
                int u = m.Nb[t * 3 + k];
                if (u < 0 || owner[u] >= 0 || mark.Contains(u)) continue;
                if (!OnSurface(u, s, sense)) continue;
                mark.Add(u); tris.Add(u); q.Enqueue(u);
                if (tris.Count > limitTris) return null;
                if (refit && tris.Count >= fitAt && s.Kind != FitKind.Plane)
                {
                    fitAt *= 2;
                    var ns = Fits.Refine(s, Verts(tris));
                    if (MaxDist(ns, tris) <= MaxDist(s, tris) + Tol * 0.01) s = ns;
                }
            }
        }
        if (refit && tris.Count >= 3)
        {
            var ns = s.Kind == FitKind.Plane ? Fits.Plane(Verts(tris), s.A) ?? s : Fits.Refine(s, Verts(tris));
            if (MaxDist(ns, tris) <= Tol) s = ns;
        }
        return new FitRegion { Tris = tris, S = s, Sense = sense };
    }

    List<Vector3d> Verts(IEnumerable<int> tris)
    {
        var seen = new HashSet<int>(); var l = new List<Vector3d>();
        foreach (int t in tris) for (int k = 0; k < 3; k++) if (seen.Add(m.Vx(t, k))) l.Add(m.P(t, k));
        return l;
    }

    double MaxDist(FitSurface s, IEnumerable<int> tris)
    {
        double w = 0; foreach (int t in tris) for (int k = 0; k < 3; k++) w = Math.Max(w, Math.Abs(s.Distance(m.P(t, k)))); return w;
    }

    /// <summary>Затравка и её соседи на rings колец (не через острые рёбра, ещё ничьи).</summary>
    List<int> Patch(int seed, int rings)
    {
        var res = new List<int> { seed }; var seen = new HashSet<int> { seed };
        var front = new List<int> { seed };
        for (int r = 0; r < rings; r++)
        {
            var next = new List<int>();
            foreach (int t in front)
                for (int k = 0; k < 3; k++)
                {
                    if (Sharp(t, k)) continue;
                    int u = m.Nb[t * 3 + k];
                    if (u < 0 || owner[u] >= 0 || !seen.Add(u)) continue;
                    res.Add(u); next.Add(u);
                }
            front = next;
        }
        return res;
    }

    FitRegion BestFrom(int seed)
    {
        var plane = new FitSurface { Kind = FitKind.Plane, P = (m.P(seed, 0) + m.P(seed, 1) + m.P(seed, 2)) / 3, A = m.TN[seed] };
        var best = Grow(seed, plane, true)!;
        if (PlanesOnly) return best;
        double bestArea = best.Tris.Sum(t => m.TA[t]) * 1.5;   // кривая должна покрыть в полтора раза больше
        var patch = Patch(seed, 2);
        var cands = new List<FitSurface>();
        var axes = new List<Vector3d>();
        var pv = Verts(patch);
        // ЦИЛИНДР ОТ ПАРЫ «ЗАТРАВКА + СОСЕД»: ось — поперёк обеих нормалей, окружность — по вершинам только этих двух
        // треугольников (и плоского напарника затравки): прежде окружность подгонялась по всему кусочку в два кольца, а
        // он у скругления заходит на касательную плоскость — радиус R3 выходил 3.49, скругление не находилось.
        var partner = new List<int> { seed };
        for (int k = 0; k < 3; k++) { int u = m.Nb[seed * 3 + k]; if (u >= 0 && owner[u] < 0 && Vector3d.Dot(m.TN[u], m.TN[seed]) > Math.Cos(0.05 * Math.PI / 180)) partner.Add(u); }
        foreach (int u in patch.Skip(1))
        {
            var ax = Vector3d.Cross(m.TN[seed], m.TN[u]);
            if (ax.Length < Math.Sin(0.3 * Math.PI / 180)) continue;
            ax.Normalize();
            if (axes.Any(a => Math.Abs(Vector3d.Dot(a, ax)) > Math.Cos(2 * Math.PI / 180))) continue;
            axes.Add(ax);
            var local = Verts(partner.Append(u));
            foreach (var src in new[] { local, pv })
                if (Fits.CylinderAbout(src, ax) is { } c && c.R < 1e3 * m.Diag)
                {
                    var cr = Fits.Refine(c, src);
                    cands.Add(MaxDist(cr, partner) < MaxDist(c, partner) ? cr : c);
                }
            if (axes.Count > 6) break;
        }
        if (axes.Count >= 2 && pv.Count >= 6 && Fits.Sphere(pv) is { } sp && sp.R < 1e3 * m.Diag) cands.Add(Fits.Refine(sp, pv));
        // конус и тор: ось — по прямым нормалей кусочка (треугольники в центрах и вершины), затем уточнение по вершинам
        if (axes.Count >= 1 && !ConeTorusOff)
        {
            var big = Patch(seed, 3);
            var bv = Verts(big);
            if (Fits.ConeFromNormals(bv, big.Select(t => (m.TN[t], m.TA[t])).ToList()) is { } cn0) cands.Add(Fits.Refine(cn0, bv));
            if (bv.Count >= 8 && Fits.RevolutionAxis(NormalLines(big)) is { } rax)
            {
                if (Fits.ConeAbout(bv, rax.Point, rax.Dir) is { } cn) cands.Add(Fits.Refine(cn, bv));
                if (Fits.TorusAbout(bv, rax.Point, rax.Dir) is { } tr && tr.R < 1e3 * m.Diag) cands.Add(Fits.Refine(tr, bv));
            }
        }
        foreach (var c in cands)
        {
            Candidates++;
            var g = Grow(seed, c, true);
            Trace?.Invoke($"  seed {seed}: cand {c} → {(g == null ? "null" : g.Tris.Count + " tris, curved " + CurvedEnough(g.Tris) + ", area " + g.Tris.Sum(t => m.TA[t]).ToString("0.##") + " vs " + bestArea.ToString("0.##"))}");
            if (g == null || g.Tris.Count < 3 || !CurvedEnough(g.Tris)) continue;
            double a = g.Tris.Sum(t => m.TA[t]);
            if (a > bestArea) { best = g; bestArea = a; }
        }
        return best;
    }

    /// <summary>Разбор: что даёт затравка (кандидаты конуса и тора).</summary>
    public string DebugSeed(int seed)
    {
        var sb = new System.Text.StringBuilder();
        var big = Patch(seed, 3); var bv = Verts(big);
        sb.Append($"patch {big.Count} tris {bv.Count} verts; ");
        var ax = Fits.RevolutionAxis(NormalLines(big));
        sb.Append($"axis {ax?.Dir} at {ax?.Point}; ");
        if (Fits.ConeFromNormals(bv, big.Select(t => (m.TN[t], m.TA[t])).ToList()) is { } cf) sb.Append($"coneN {cf} apex {cf.P} max {MaxDist(cf, big):g3} refined {MaxDist(Fits.Refine(cf, bv), big):g3}; "); else sb.Append("coneN null " + Fits.Why + "; ");
        if (ax is { } a && Fits.ConeAbout(bv, a.Point, a.Dir) is { } cn)
        {
            sb.Append($"cone0 {cn} apex {cn.P} max {MaxDist(cn, big):g3}; ");
            var r = Fits.Refine(cn, bv);
            sb.Append($"cone1 {r} apex {r.P} max {MaxDist(r, big):g3}; seedOn {OnSurface(seed, r, SenseOf(seed, r))}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// КРИВИЗНУ ДОКАЗЫВАЮТ ГРАНИ СЕТКИ, А НЕ ВЕРШИНЫ. Вершины двух соседних плоских стенок всегда лежат на каком-то
    /// цилиндре (через два отрезка проходит цилиндр с любой осью, параллельной им): на детали из кубиков стенки под 40°
    /// уходили в «цилиндр R92», грань выпучивалась, объём +5%. Кривая область должна состоять хотя бы из трёх разных
    /// плоских кусочков (нормали — разные), и ни один не должен занимать больше 60% её площади. Мутация P3D_MESHFITCURVED=0.
    /// </summary>
    bool CurvedEnough(List<int> tris)
    {
        if (CurvedOff) return true;
        var cl = new List<(Vector3d N, double A)>();
        double total = 0;
        foreach (int t in tris)
        {
            total += m.TA[t];
            int k = cl.FindIndex(c => Vector3d.Dot(c.N, m.TN[t]) > Math.Cos(0.25 * Math.PI / 180));
            if (k < 0) cl.Add((m.TN[t], m.TA[t])); else cl[k] = (cl[k].N, cl[k].A + m.TA[t]);
            if (cl.Count > 64) return true;
        }
        return cl.Count >= 3 && cl.Max(c => c.A) <= 0.6 * total;
    }
    public static readonly bool CurvedOff = Environment.GetEnvironmentVariable("P3D_MESHFITCURVED") == "0";

    public static readonly bool ConeTorusOff = Environment.GetEnvironmentVariable("P3D_MESHFITCONE") == "0";

    /// <summary>Прямые нормалей: центр и нормаль каждого треугольника, вершины с нормалью, усреднённой по треугольникам набора.</summary>
    List<(Vector3d, Vector3d)> NormalLines(List<int> tris)
    {
        var set = new HashSet<int>(tris);
        var pn = new List<(Vector3d, Vector3d)>();
        var seen = new HashSet<int>();
        foreach (int t in tris)
        {
            pn.Add(((m.P(t, 0) + m.P(t, 1) + m.P(t, 2)) / 3, m.TN[t]));
            for (int k = 0; k < 3; k++)
            {
                int v = m.Vx(t, k);
                if (!seen.Add(v)) continue;
                var n = Vector3d.Zero;
                foreach (int u in vt[v]) if (set.Contains(u)) n += m.TN[u] * m.TA[u];
                if (n.LengthSquared > 0) pn.Add((m.V[v], n.Normalized()));
            }
        }
        return pn;
    }

    // ------------------------------------------------------------------ слияния

    IEnumerable<int> Neighbours(FitRegion r, int self)
    {
        var res = new HashSet<int>();
        foreach (int t in r.Tris)
            for (int k = 0; k < 3; k++)
            {
                int u = m.Nb[t * 3 + k];
                if (u >= 0 && owner[u] != self && owner[u] >= 0 && !Sharp(t, k)) res.Add(owner[u]);
            }
        return res;
    }

    bool AllOn(FitSurface s, IEnumerable<int> tris, int sense)
    {
        foreach (int t in tris) if (!OnSurface(t, s, sense)) return false;
        return true;
    }

    /// <summary>Поверхность, на которой лежат все треугольники tris (кривые виды по оси нормалей), или null.</summary>
    FitSurface? UnionSurface(List<int> tris, int sense)
    {
        var pts = Verts(tris);
        if (pts.Count < 6) return null;
        var pn = NormalLines(tris);
        var cands = new List<FitSurface>();
        if (Fits.ConeFromNormals(pts, tris.Select(t => (m.TN[t], m.TA[t])).ToList()) is { } cn0) cands.Add(cn0);
        if (Fits.RevolutionAxis(pn) is { } ax)
        {
            if (Fits.CylinderAbout(pts, ax.Dir) is { } c) cands.Add(c);
            if (Fits.ConeAbout(pts, ax.Point, ax.Dir) is { } cn) cands.Add(cn);
            if (Fits.TorusAbout(pts, ax.Point, ax.Dir) is { } tr) cands.Add(tr);
        }
        // ось цилиндра — наименьший собственный вектор разброса нормалей
        {
            var mm = new double[3, 3];
            foreach (var (_, n) in pn) { mm[0, 0] += n.X * n.X; mm[0, 1] += n.X * n.Y; mm[0, 2] += n.X * n.Z; mm[1, 1] += n.Y * n.Y; mm[1, 2] += n.Y * n.Z; mm[2, 2] += n.Z * n.Z; }
            mm[1, 0] = mm[0, 1]; mm[2, 0] = mm[0, 2]; mm[2, 1] = mm[1, 2];
            var (vals, vecs) = Fits.Eigen3(mm);
            if (vals[1] > 1e-6 * vals[2] && Fits.CylinderAbout(pts, vecs[0]) is { } c2) cands.Add(c2);
        }
        if (Fits.Sphere(pts) is { } sp) cands.Add(sp);
        foreach (var c in cands)
        {
            if (c.R > 1e3 * m.Diag) continue;
            var cr = Fits.Refine(c, pts);
            foreach (var s in new[] { cr, c })
                if (AllOn(s, tris, sense) || (SenseFor(s, tris) is var sn && sn != sense && AllOn(s, tris, sn))) return s;
        }
        return null;
    }

    int SenseFor(FitSurface s, List<int> tris)
    {
        double a = 0; foreach (int t in tris) a += SenseOf(t, s) * m.TA[t]; return a >= 0 ? 1 : -1;
    }

    void MergeRegions()
    {
        bool any = true;
        for (int round = 0; round < 6 && any; round++)
        {
            any = false;
            var order = Enumerable.Range(0, Regions.Count).Where(i => Regions[i].Alive && Regions[i].S.Kind != FitKind.Plane || Regions[i].Alive && Regions[i].Tris.Count <= 6)
                .OrderByDescending(i => Regions[i].Area).ToList();
            foreach (int i in order)
            {
                var a = Regions[i];
                if (!a.Alive) continue;
                foreach (int j in Neighbours(a, i).OrderByDescending(j => Regions[j].Area).ToList())
                {
                    var b = Regions[j];
                    if (!b.Alive || ReferenceEquals(a, b)) continue;
                    if (b.S.Kind == FitKind.Plane && b.Tris.Count > 6) continue;
                    var union = a.Tris.Concat(b.Tris).ToList();
                    int sense = SenseForMix(a, b, union);
                    var s = UnionSurface(union, sense);
                    if (s == null || !CurvedEnough(union)) continue;
                    a.Tris = union; a.S = s; a.Sense = SenseFor(s, union); a.Area += b.Area;
                    b.Alive = false; b.Tris = new List<int>();
                    foreach (int t in a.Tris) owner[t] = i;
                    Merges++; any = true;
                }
            }
        }
    }

    int SenseForMix(FitRegion a, FitRegion b, List<int> union) => a.Area >= b.Area ? a.Sense : b.Sense;

    void Absorb()
    {
        bool any = true;
        for (int round = 0; round < 6 && any; round++)
        {
            any = false;
            foreach (int i in Enumerable.Range(0, Regions.Count).Where(i => Regions[i].Alive && Regions[i].Tris.Count <= 12).OrderBy(i => Regions[i].Area).ToList())
            {
                var r = Regions[i];
                if (!r.Alive) continue;
                foreach (int j in Neighbours(r, i).OrderByDescending(j => Regions[j].Area))
                {
                    var b = Regions[j];
                    if (!b.Alive || b.Tris.Count <= r.Tris.Count) continue;
                    if (!AllOn(b.S, r.Tris, b.Sense)) continue;
                    b.Tris.AddRange(r.Tris); b.Area += r.Area;
                    foreach (int t in r.Tris) owner[t] = j;
                    r.Alive = false; r.Tris = new List<int>();
                    Absorbed++; any = true;
                    break;
                }
            }
        }
    }

    void Compact()
    {
        var alive = Regions.Where(r => r.Alive && r.Tris.Count > 0).ToList();
        Regions.Clear(); Regions.AddRange(alive);
        for (int i = 0; i < Regions.Count; i++) foreach (int t in Regions[i].Tris) owner[t] = i;
        // области — связные (кусок, распавшийся при слиянии или поглощении, — своя область на той же поверхности)
        for (int i = 0, n = Regions.Count; i < n; i++)
        {
            var r = Regions[i];
            var pieces = Pieces(r.Tris, i);
            if (pieces.Count <= 1) continue;
            r.Tris = pieces[0];
            if (r.Group < 0) r.Group = i;
            for (int p = 1; p < pieces.Count; p++)
            {
                var nr = new FitRegion { Tris = pieces[p], S = r.S.Clone(), Sense = r.Sense, Group = r.Group };
                Regions.Add(nr);
                foreach (int t in nr.Tris) owner[t] = Regions.Count - 1;
            }
        }
        SplitClosed();
        foreach (var r in Regions) { r.Area = r.Tris.Sum(t => m.TA[t]); r.S.Canonical(); }
    }

    /// <summary>
    /// ЗАМКНУТАЯ ОБЛАСТЬ (целая сфера, целый тор — без границы) делится плоскостью через центр (сфера — поперёк Z, тор —
    /// поперёк своей оси) на две области той же поверхности: у грани должен быть контур. У сетки «по параллелям» граница
    /// половин — сама окружность экватора.
    /// </summary>
    void SplitClosed()
    {
        for (int i = 0, n = Regions.Count; i < n; i++)
        {
            var r = Regions[i];
            bool open = false;
            foreach (int t in r.Tris) { for (int k = 0; k < 3 && !open; k++) { int u = m.Nb[t * 3 + k]; if (u < 0 || owner[u] != i) open = true; } if (open) break; }
            if (open || r.Tris.Count < 2) continue;
            var axis = r.S.Kind == FitKind.Sphere || r.S.Kind == FitKind.Plane ? Vector3d.UnitZ : r.S.A;
            var c = r.S.P;
            if (r.S.Kind == FitKind.Cone) c = r.S.P + r.S.A * r.Tris.Average(t => Vector3d.Dot((m.P(t, 0) + m.P(t, 1) + m.P(t, 2)) / 3 - r.S.P, r.S.A));
            if (r.S.Kind == FitKind.Torus) { SplitTorus(i, r); continue; }
            double Side(int t) => Vector3d.Dot((m.P(t, 0) + m.P(t, 1) + m.P(t, 2)) / 3 - c, axis);
            var up = r.Tris.Where(t => Side(t) >= 0).ToList(); var dn = r.Tris.Where(t => Side(t) < 0).ToList();
            if (up.Count == 0 || dn.Count == 0) continue;
            r.Tris = up;
            if (r.Group < 0) r.Group = i;
            var nr = new FitRegion { Tris = dn, S = r.S.Clone(), Sense = r.Sense, Group = r.Group };
            Regions.Add(nr);
            foreach (int t in dn) owner[t] = Regions.Count - 1;
            ClosedSplits++;
        }
    }

    /// <summary>
    /// ЦЕЛЫЙ ТОР — ЧЕТЫРЕ ГРАНИ: верх, низ (срезы на кольцах вершин около ±r/2), наружный и внутренний пояса. Половины по
    /// экватору (и вообще кусок больше полуоборота образующей) импорт не различает: обе ограничены одними окружностями,
    /// и каждая бралась меньшей частью — тело выходило нулевого объёма.
    /// </summary>
    void SplitTorus(int i, FitRegion r)
    {
        var s = r.S; var c = s.P; var axis = s.A;
        var heights = r.Tris.SelectMany(t => new[] { m.Vx(t, 0), m.Vx(t, 1), m.Vx(t, 2) }).Distinct().Select(v => Vector3d.Dot(m.V[v] - c, axis)).ToList();
        double Ring(int sign)
        {
            var hs = heights.Where(h => sign * h > 0.25 * s.Rm && sign * h < 0.75 * s.Rm).ToList();
            if (hs.Count == 0) return sign * 0.5 * s.Rm;
            return hs.GroupBy(h => Math.Round(h / (Tol * 2))).OrderByDescending(g => g.Count()).ThenBy(g => Math.Abs(Math.Abs(g.Key * Tol * 2) - 0.5 * s.Rm)).First().Average();
        }
        double top = Ring(1), bot = Ring(-1);
        var parts = new List<int>[4];
        for (int k = 0; k < 4; k++) parts[k] = new List<int>();
        foreach (int t in r.Tris)
        {
            var g = (m.P(t, 0) + m.P(t, 1) + m.P(t, 2)) / 3;
            var (h, rho) = Fits.HeightRadius(g, c, axis);
            parts[h >= top ? 0 : h <= bot ? 1 : rho >= s.R ? 2 : 3].Add(t);
        }
        var nonEmpty = parts.Where(p => p.Count > 0).ToList();
        if (nonEmpty.Count < 2) return;
        r.Tris = nonEmpty[0];
        if (r.Group < 0) r.Group = i;
        foreach (var p in nonEmpty.Skip(1))
        {
            var nr = new FitRegion { Tris = p, S = s.Clone(), Sense = r.Sense, Group = r.Group };
            Regions.Add(nr);
            foreach (int t in p) owner[t] = Regions.Count - 1;
        }
        ClosedSplits++;
    }
    public int ClosedSplits;

    List<List<int>> Pieces(List<int> tris, int id)
    {
        var set = new HashSet<int>(tris); var res = new List<List<int>>();
        var done = new HashSet<int>();
        foreach (int s in tris)
        {
            if (!done.Add(s)) continue;
            var piece = new List<int> { s }; var q = new Queue<int>(); q.Enqueue(s);
            while (q.Count > 0)
            {
                int t = q.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    int u = m.Nb[t * 3 + k];
                    if (u >= 0 && set.Contains(u) && done.Add(u)) { piece.Add(u); q.Enqueue(u); }
                }
            }
            res.Add(piece);
        }
        return res.OrderByDescending(p => p.Count).ToList();
    }
}
