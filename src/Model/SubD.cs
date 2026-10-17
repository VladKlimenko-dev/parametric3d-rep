using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Model;

/// <summary>
/// u13k: SUBD-КЛЕТКА (FreeForm, решение 2026-10-14): вершины, грани (многоугольники), складки (резкие рёбра). Тело —
/// подразделение Катмулла–Кларка заданного уровня (складки — правила острого ребра и угла); «в NURBS» — по лоскуту на
/// каждый четырёхугольник первого уровня: NURBS, проходящая через точки подразделения (общие края лоскутов совпадают
/// точно, тело замкнуто).
/// </summary>
public sealed class SubDCage
{
    public readonly List<Vector3d> V = new();
    public readonly List<int[]> F = new();
    /// <summary>Складки: рёбра (меньший, больший номер вершины).</summary>
    public readonly HashSet<(int, int)> Creases = new();
    /// <summary>Уровень сглаживания тела (число подразделений).</summary>
    public int Level = 2;

    public SubDCage Clone()
    {
        var c = new SubDCage { Level = Level };
        c.V.AddRange(V); c.F.AddRange(F.Select(f => (int[])f.Clone())); foreach (var e in Creases) c.Creases.Add(e);
        return c;
    }

    public static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>Клетка-брусок: восемь вершин, шесть граней (обход — наружу), центр в начале рамки.</summary>
    public static SubDCage Box(Frame3 fr, double w, double d, double h)
    {
        var c = new SubDCage();
        double x = w / 2, y = d / 2;
        foreach (var (px, py, pz) in new[] { (-x, -y, 0.0), (x, -y, 0.0), (x, y, 0.0), (-x, y, 0.0), (-x, -y, h), (x, -y, h), (x, y, h), (-x, y, h) })
            c.V.Add(fr.ToWorld(new Vector3d(px, py, pz)));
        c.F.Add(new[] { 0, 3, 2, 1 }); c.F.Add(new[] { 4, 5, 6, 7 });
        c.F.Add(new[] { 0, 1, 5, 4 }); c.F.Add(new[] { 1, 2, 6, 5 }); c.F.Add(new[] { 2, 3, 7, 6 }); c.F.Add(new[] { 3, 0, 4, 7 });
        return c;
    }

    /// <summary>Рёбра клетки → грани (по ключу ребра).</summary>
    public Dictionary<(int, int), List<int>> EdgeFaces()
    {
        var m = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < F.Count; f++)
            for (int i = 0; i < F[f].Length; i++)
            {
                var k = Key(F[f][i], F[f][(i + 1) % F[f].Length]);
                if (!m.TryGetValue(k, out var l)) m[k] = l = new List<int>();
                l.Add(f);
            }
        return m;
    }

    /// <summary>Замкнута ли клетка (каждое ребро — у двух граней).</summary>
    public bool IsClosed() => EdgeFaces().Values.All(l => l.Count == 2);

    // ------------------------------------------------------------------ правки клетки

    /// <summary>
    /// ВЫДАВИТЬ ГРАНИ: область граней (общие рёбра остаются внутри) сдвигается по средней нормали её граней на d, по краю
    /// области — боковые четырёхугольники.
    /// </summary>
    public SubDCage Extrude(IReadOnlyCollection<int> faces, double d)
    {
        if (faces.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "SubD extrude: no faces.");
        if (faces.Any(f => f < 0 || f >= F.Count)) throw new RebuildException(OperationOutcome.InvalidInput, "SubD extrude: a face is not in the cage.");
        var c = Clone();
        var set = faces.ToHashSet();
        var verts = set.SelectMany(f => F[f]).Distinct().ToList();
        var nrm = verts.ToDictionary(v => v, _ => Vector3d.Zero);
        foreach (var f in set) { var n = Normal(f); foreach (var v in F[f]) nrm[v] += n; }
        var map = new Dictionary<int, int>();
        foreach (var v in verts) { map[v] = c.V.Count; c.V.Add(V[v] + nrm[v].Normalized() * d); }
        // край области: рёбра граней области, встречающиеся в области один раз (в направлении обхода грани)
        var count = new Dictionary<(int, int), int>();
        foreach (var f in set) for (int i = 0; i < F[f].Length; i++) { var k = Key(F[f][i], F[f][(i + 1) % F[f].Length]); count[k] = count.GetValueOrDefault(k) + 1; }
        foreach (var f in set)
        {
            var poly = F[f];
            for (int i = 0; i < poly.Length; i++)
            {
                int a = poly[i], b = poly[(i + 1) % poly.Length];
                if (count[Key(a, b)] != 1) continue;
                c.F.Add(new[] { a, b, map[b], map[a] });
            }
            c.F[f] = poly.Select(v => map[v]).ToArray();
        }
        return c;
    }

    public Vector3d Normal(int f)
    {
        var p = F[f]; var n = Vector3d.Zero;
        for (int i = 0; i < p.Length; i++) n += Vector3d.Cross(V[p[i]], V[p[(i + 1) % p.Length]]);
        return n.LengthSquared < 1e-30 ? Vector3d.UnitZ : n.Normalized();
    }

    /// <summary>
    /// ПЕТЛЯ РЁБЕР: кольцо четырёхугольников через ребро (a, b) — каждый пересекается новой петлёй в доле t: рёбра кольца
    /// делятся, четырёхугольники — на два.
    /// </summary>
    public SubDCage EdgeLoop(int a, int b, double t = 0.5)
    {
        if (!(t > 0.01 && t < 0.99)) throw new RebuildException(OperationOutcome.InvalidInput, "Edge loop: the position must be between 0 and 1.");
        var ef = EdgeFaces();
        if (!ef.ContainsKey(Key(a, b))) throw new RebuildException(OperationOutcome.InvalidInput, "Edge loop: the edge is not in the cage.");
        // кольцо: от ребра через противоположные рёбра четырёхугольников, в обе стороны
        var ring = new List<(int A, int B)> { (a, b) };
        var quads = new List<int>();
        var seenF = new HashSet<int>();
        void Walk((int A, int B) start, int fromFace)
        {
            var e = start;
            int prev = fromFace;
            while (true)
            {
                var nf = ef[Key(e.A, e.B)].FirstOrDefault(f => f != prev && !seenF.Contains(f), -1);
                if (nf < 0 || F[nf].Length != 4) return;
                seenF.Add(nf); quads.Add(nf);
                var q = F[nf]; int ia = Array.IndexOf(q, e.A), ib = Array.IndexOf(q, e.B);
                // противоположное ребро: две другие вершины; ориентация — параллельно (A→A', B→B')
                int oa = q[(ia + 2) % 4], ob = q[(ib + 2) % 4];
                // у четырёхугольника (A,B,…) соседи: A — рядом с ob? противоположное A по стороне — вершина, соседняя с A, не B
                int nA = q[(ia + 1) % 4] == e.B ? q[(ia + 3) % 4] : q[(ia + 1) % 4];
                int nB = q[(ib + 1) % 4] == e.A ? q[(ib + 3) % 4] : q[(ib + 1) % 4];
                var next = (nA, nB);
                if (Key(next.nA, next.nB) == Key(a, b)) return;            // кольцо замкнулось
                ring.Add(next);
                e = next; prev = nf;
            }
        }
        Walk((a, b), -1);
        // в другую сторону — от исходного ребра через вторую грань (если кольцо не замкнулось)
        var around = ef[Key(a, b)];
        if (around.Any(f => !seenF.Contains(f))) Walk((a, b), around.FirstOrDefault(f => seenF.Contains(f), -1));
        if (quads.Count == 0) throw new RebuildException(OperationOutcome.InvalidInput, "Edge loop: the edge has no four-sided faces around it.");
        var c = Clone();
        var split = new Dictionary<(int, int), int>();
        int Mid(int p, int q)
        {
            var k = Key(p, q);
            if (split.TryGetValue(k, out var m)) return m;
            // доля t — от вершины кольца «A» стороны (p — со стороны A)
            m = c.V.Count; c.V.Add(V[p] + (V[q] - V[p]) * t);
            split[k] = m;
            if (Creases.Contains(k)) { c.Creases.Remove(k); c.Creases.Add(Key(p, m)); c.Creases.Add(Key(m, q)); }
            return m;
        }
        foreach (var (ea, eb) in ring) Mid(ea, eb);
        foreach (var f in quads)
        {
            var q = F[f];
            // два ребра кольца у этой грани
            int k0 = -1, k1 = -1;
            for (int i = 0; i < 4; i++) if (split.ContainsKey(Key(q[i], q[(i + 1) % 4]))) { if (k0 < 0) k0 = i; else k1 = i; }
            if (k0 < 0 || k1 < 0) continue;
            int m0 = split[Key(q[k0], q[(k0 + 1) % 4])], m1 = split[Key(q[k1], q[(k1 + 1) % 4])];
            // грань (q0 q1 q2 q3), рёбра k0 = (q[k0], q[k0+1]) и k1 = (q[k1], q[k1+1]) напротив (k1 = k0 + 2)
            var f1 = new[] { q[k0], m0, m1, q[(k1 + 1) % 4] };
            var f2 = new[] { m0, q[(k0 + 1) % 4], q[k1], m1 };
            c.F[f] = f1; c.F.Add(f2);
        }
        // соседние не четырёхугольники с разделённым ребром — получают новую вершину в контуре
        for (int f = 0; f < c.F.Count; f++)
        {
            if (quads.Contains(f)) continue;
            var p = c.F[f]; var np = new List<int>();
            for (int i = 0; i < p.Length; i++)
            {
                np.Add(p[i]);
                if (split.TryGetValue(Key(p[i], p[(i + 1) % p.Length]), out var m) && !np.Contains(m)) np.Add(m);
            }
            c.F[f] = np.ToArray();
        }
        return c;
    }

    // ------------------------------------------------------------------ Катмулл — Кларк

    /// <summary>Один шаг подразделения (любые многоугольники → четырёхугольники); складки — правила острого ребра.</summary>
    public SubDCage Subdivide() => SubdivideTracked(out _, out _);

    /// <summary>Перенос клетки отображением точек (подразделение аффинно-инвариантно); зеркало — обход граней обратный.</summary>
    public SubDCage Mapped(Func<Vector3d, Vector3d> map, bool flips)
    {
        var c = Clone();
        for (int i = 0; i < c.V.Count; i++) c.V[i] = map(c.V[i]);
        if (flips) for (int f = 0; f < c.F.Count; f++) c.F[f] = c.F[f].Reverse().ToArray();
        return c;
    }

    /// <summary>Ребро клетки между двумя гранями (общие соседние вершины) или null.</summary>
    public (int, int)? SharedEdge(int f1, int f2)
    {
        if (f1 < 0 || f2 < 0 || f1 >= F.Count || f2 >= F.Count) return null;
        var a = F[f1];
        for (int i = 0; i < a.Length; i++)
        {
            var k = Key(a[i], a[(i + 1) % a.Length]);
            var b = F[f2];
            for (int j = 0; j < b.Length; j++) if (Key(b[j], b[(j + 1) % b.Length]) == k) return k;
        }
        return null;
    }

    /// <summary>Номер грани клетки по подписи грани тела («…subd{k}»).</summary>
    public static int FaceOfTag(string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag ?? "", @"subd(\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    /// <summary>Шаг подразделения с картами: вершина → новая вершина, ребро → точка ребра, грань → точка грани.</summary>
    public SubDCage SubdivideTracked(out Dictionary<(int, int), int> edgePt, out int[] facePt)
    {
        var ef = EdgeFaces();
        var c = new SubDCage();
        // точки граней
        facePt = new int[F.Count];
        var fp = new Vector3d[F.Count];
        for (int f = 0; f < F.Count; f++) { fp[f] = F[f].Aggregate(Vector3d.Zero, (s, v) => s + V[v]) / F[f].Length; }
        // точки рёбер
        var ep = new Dictionary<(int, int), Vector3d>();
        foreach (var (k, fs) in ef)
        {
            var mid = (V[k.Item1] + V[k.Item2]) / 2;
            ep[k] = fs.Count == 2 && !Creases.Contains(k) ? (V[k.Item1] + V[k.Item2] + fp[fs[0]] + fp[fs[1]]) / 4 : mid;
        }
        // вершины
        var vEdges = new Dictionary<int, List<(int, int)>>();
        foreach (var k in ef.Keys) { foreach (var v in new[] { k.Item1, k.Item2 }) { if (!vEdges.TryGetValue(v, out var l)) vEdges[v] = l = new(); l.Add(k); } }
        var vFaces = new Dictionary<int, List<int>>();
        for (int f = 0; f < F.Count; f++) foreach (var v in F[f]) { if (!vFaces.TryGetValue(v, out var l)) vFaces[v] = l = new(); l.Add(f); }
        var vmap = new int[V.Count];
        for (int v = 0; v < V.Count; v++)
        {
            if (!vEdges.TryGetValue(v, out var es)) { vmap[v] = c.V.Count; c.V.Add(V[v]); continue; }
            var sharp = es.Where(k => Creases.Contains(k) || ef[k].Count < 2).ToList();
            Vector3d np;
            if (sharp.Count >= 3) np = V[v];                                  // угол
            else if (sharp.Count == 2)
            {
                int o1 = sharp[0].Item1 == v ? sharp[0].Item2 : sharp[0].Item1, o2 = sharp[1].Item1 == v ? sharp[1].Item2 : sharp[1].Item1;
                np = (6 * V[v] + V[o1] + V[o2]) / 8;                          // острое ребро
            }
            else
            {
                int n = es.Count;
                var fAvg = vFaces[v].Aggregate(Vector3d.Zero, (s, f) => s + fp[f]) / vFaces[v].Count;
                var eAvg = es.Aggregate(Vector3d.Zero, (s, k) => s + (V[k.Item1] + V[k.Item2]) / 2) / n;
                np = (fAvg + 2 * eAvg + (n - 3) * V[v]) / n;
            }
            vmap[v] = c.V.Count; c.V.Add(np);
        }
        edgePt = new Dictionary<(int, int), int>();
        foreach (var (k, p) in ep) { edgePt[k] = c.V.Count; c.V.Add(p); }
        for (int f = 0; f < F.Count; f++) { facePt[f] = c.V.Count; c.V.Add(fp[f]); }
        for (int f = 0; f < F.Count; f++)
        {
            var p = F[f]; int n = p.Length;
            for (int i = 0; i < n; i++)
            {
                int prev = p[(i - 1 + n) % n], cur = p[i], next = p[(i + 1) % n];
                c.F.Add(new[] { vmap[cur], edgePt[Key(cur, next)], facePt[f], edgePt[Key(prev, cur)] });
            }
        }
        foreach (var k in Creases)
            if (edgePt.TryGetValue(k, out var m)) { c.Creases.Add(Key(vmap[k.Item1], m)); c.Creases.Add(Key(m, vmap[k.Item2])); }
        return c;
    }

    /// <summary>Тело уровня level: треугольники подразделения, грани тела — по граням клетки (подпись «subd{k}»).</summary>
    public TSolid ToSolid(int level)
    {
        if (!IsClosed()) throw new RebuildException(OperationOutcome.InvalidInput, "SubD: the cage is not closed.");
        level = Math.Clamp(level, 0, 5);
        // происхождение граней: номер грани клетки у каждой подграни
        var cur = this; var origin = Enumerable.Range(0, F.Count).ToList();
        for (int l = 0; l < level; l++)
        {
            var next = cur.SubdivideTracked(out _, out _);
            var no = new List<int>();
            for (int f = 0; f < cur.F.Count; f++) for (int i = 0; i < cur.F[f].Length; i++) no.Add(origin[f]);
            cur = next; origin = no;
        }
        var polys = new List<CsgPolygon>();
        var names = new Dictionary<uint, string>();
        for (int f = 0; f < cur.F.Count; f++)
        {
            uint tag = (uint)(origin[f] + 1);
            names[tag] = "subd" + origin[f];
            var p = cur.F[f];
            for (int i = 1; i + 1 < p.Length; i++)
            {
                var a = cur.V[p[0]]; var b = cur.V[p[i]]; var c = cur.V[p[i + 1]];
                if (Vector3d.Cross(b - a, c - a).LengthSquared > 1e-24) polys.Add(new CsgPolygon(new List<Vector3d> { a, b, c }, tag));
            }
        }
        return BooleanOp.SolidFromPolygons(polys, new Dictionary<uint, ISurface3?>(), names);
    }

    /// <summary>
    /// «В NURBS»: каждый четырёхугольник первого уровня — NURBS-лоскут через точки подразделения уровня level (сетка
    /// 2^(level−1)+1 в сторону, параметры равномерные): лоскут проходит через свои точки, общие края соседей совпадают.
    /// </summary>
    public TSolid ToNurbs(int level, out int patches)
    {
        if (!IsClosed()) throw new RebuildException(OperationOutcome.InvalidInput, "SubD: the cage is not closed.");
        level = Math.Clamp(level, 2, 5);
        var c1 = Subdivide();                         // все грани — четырёхугольники
        // сетки вершин по каждому четырёхугольнику первого уровня
        var grids = c1.F.Select(q => new int[,] { { q[0], q[3] }, { q[1], q[2] } }).ToList();
        var cur = c1;
        for (int l = 1; l < level; l++)
        {
            var next = cur.SubdivideTracked(out var ep, out _);
            // точки граней текущего уровня: грани cur — четырёхугольники сеток по порядку
            var faceIndex = new Dictionary<(int, int, int, int), int>();
            for (int f = 0; f < cur.F.Count; f++) { var q = cur.F[f]; faceIndex[Norm4(q)] = f; }
            var ng = new List<int[,]>();
            foreach (var g in grids)
            {
                int m = g.GetLength(0) - 1, m2 = 2 * m;
                var h = new int[m2 + 1, m2 + 1];
                for (int i = 0; i <= m; i++) for (int j = 0; j <= m; j++) h[2 * i, 2 * j] = VertexMap(cur, next, g[i, j]);
                for (int i = 0; i < m; i++) for (int j = 0; j <= m; j++) h[2 * i + 1, 2 * j] = ep[Key(g[i, j], g[i + 1, j])];
                for (int i = 0; i <= m; i++) for (int j = 0; j < m; j++) h[2 * i, 2 * j + 1] = ep[Key(g[i, j], g[i, j + 1])];
                for (int i = 0; i < m; i++) for (int j = 0; j < m; j++)
                {
                    int fi = faceIndex[Norm4(new[] { g[i, j], g[i + 1, j], g[i + 1, j + 1], g[i, j + 1] })];
                    h[2 * i + 1, 2 * j + 1] = next.V.Count - cur.F.Count + fi;           // точки граней — в конце списка вершин
                }
                ng.Add(h);
            }
            grids = ng; cur = next;
        }
        var polys = new List<CsgPolygon>();
        var sources = new Dictionary<uint, ISurface3?>();
        uint tag = 1;
        var lim = cur.LimitPositions();               // точки предельной поверхности (лоскуты — через них)
        foreach (var g in grids)
        {
            int m = g.GetLength(0) - 1;
            var q = new Vector3d[m + 1, m + 1];
            for (int i = 0; i <= m; i++) for (int j = 0; j <= m; j++) q[i, j] = lim[g[i, j]];
            var s = FFSurfaces.InterpolateUniform(q);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                {
                    var a = q[i, j]; var b = q[i + 1, j]; var cc = q[i + 1, j + 1]; var d = q[i, j + 1];
                    if (Vector3d.Cross(b - a, cc - a).LengthSquared > 1e-24) polys.Add(new CsgPolygon(new List<Vector3d> { a, b, cc }, tag));
                    if (Vector3d.Cross(cc - a, d - a).LengthSquared > 1e-24) polys.Add(new CsgPolygon(new List<Vector3d> { a, cc, d }, tag));
                }
            sources[tag] = s; tag++;
        }
        patches = grids.Count;
        return BooleanOp.SolidFromPolygons(polys, sources);
    }

    /// <summary>
    /// ПРЕДЕЛЬНЫЕ ПОЛОЖЕНИЯ вершин четырёхугольной сетки (маска Катмулла — Кларка): гладкая вершина валентности n —
    /// (n²·V + 4·ΣE + ΣF) / (n(n+5)) (E — соседи по рёбрам, F — противоположные вершины граней); на складке —
    /// (A + 4V + B)/6; угол (три складки и больше) — на месте.
    /// </summary>
    public List<Vector3d> LimitPositions()
    {
        var ef = EdgeFaces();
        var nbr = new Dictionary<int, List<int>>(); var diag = new Dictionary<int, List<int>>();
        foreach (var k in ef.Keys) { foreach (var (a, b) in new[] { (k.Item1, k.Item2), (k.Item2, k.Item1) }) { if (!nbr.TryGetValue(a, out var l)) nbr[a] = l = new(); l.Add(b); } }
        foreach (var f in F)
            if (f.Length == 4)
                for (int i = 0; i < 4; i++) { if (!diag.TryGetValue(f[i], out var l)) diag[f[i]] = l = new(); l.Add(f[(i + 2) % 4]); }
        var res = new List<Vector3d>(V.Count);
        for (int v = 0; v < V.Count; v++)
        {
            if (!nbr.TryGetValue(v, out var ns)) { res.Add(V[v]); continue; }
            var sharp = ns.Where(o => Creases.Contains(Key(v, o)) || ef[Key(v, o)].Count < 2).ToList();
            if (sharp.Count >= 3) { res.Add(V[v]); continue; }
            if (sharp.Count == 2) { res.Add((V[sharp[0]] + 4 * V[v] + V[sharp[1]]) / 6); continue; }
            int n = ns.Count;
            var es = ns.Aggregate(Vector3d.Zero, (a, o) => a + V[o]);
            var fs = diag.TryGetValue(v, out var dl) ? dl.Aggregate(Vector3d.Zero, (a, o) => a + V[o]) : Vector3d.Zero;
            res.Add((n * n * V[v] + 4 * es + fs) / (n * (n + 5)));
        }
        return res;
    }

    static (int, int, int, int) Norm4(int[] q)
    {
        var s = q.OrderBy(x => x).ToArray();
        return (s[0], s[1], s[2], s[3]);
    }

    /// <summary>Номер вершины в следующем уровне: вершины идут первыми в том же порядке.</summary>
    static int VertexMap(SubDCage cur, SubDCage next, int v) => v;

    // ------------------------------------------------------------------ документ

    public List<double> Pack()
    {
        var r = new List<double> { -Level - 1, V.Count };
        foreach (var p in V) { r.Add(p.X); r.Add(p.Y); r.Add(p.Z); }
        r.Add(F.Count);
        foreach (var f in F) { r.Add(f.Length); r.AddRange(f.Select(x => (double)x)); }
        r.Add(Creases.Count);
        foreach (var (a, b) in Creases) { r.Add(a); r.Add(b); }
        return r;
    }

    public static SubDCage? Unpack(List<double>? a)
    {
        if (a == null || a.Count < 2) return null;
        var c = new SubDCage(); int k = 0;
        if (a[0] < 0) c.Level = (int)(-a[k++]) - 1;
        int nv = (int)a[k++]; for (int i = 0; i < nv; i++) { c.V.Add(new Vector3d(a[k], a[k + 1], a[k + 2])); k += 3; }
        int nf = (int)a[k++]; for (int i = 0; i < nf; i++) { int n = (int)a[k++]; c.F.Add(Enumerable.Range(0, n).Select(j => (int)a[k + j]).ToArray()); k += n; }
        int nc = k < a.Count ? (int)a[k++] : 0; for (int i = 0; i < nc; i++) { c.Creases.Add(((int)a[k], (int)a[k + 1])); k += 2; }
        return c;
    }
}
