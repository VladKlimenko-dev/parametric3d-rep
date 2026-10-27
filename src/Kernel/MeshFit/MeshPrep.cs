using OpenTK.Mathematics;

namespace Parametric3D.Kernel.MeshFit;

/// <summary>
/// ПОДГОТОВКА СЕТКИ (k119). Треугольники STL приходят «россыпью»: каждая вершина повторена в каждом треугольнике,
/// бывают треугольники без площади, повторы и разнобой обхода. Здесь: вершины сливаются по сетке ячеек (допуск —
/// миллионная доля диагонали: в 20 раз больше шага float у координаты 100), треугольники без площади и повторы
/// отбрасываются, обход согласуется обходом в ширину по общим сторонам, каждая связная часть разворачивается нормалями
/// наружу (по знаку объёма), находятся соседи через каждую сторону. Сторона, общая для трёх и более треугольников,
/// соседа не получает: какие два из них вместе, сетка не говорит.
/// </summary>
public sealed class PreparedMesh
{
    public Vector3d[] V = Array.Empty<Vector3d>();
    /// <summary>Вершины треугольников: t*3+k.</summary>
    public int[] T = Array.Empty<int>();
    /// <summary>Сосед через сторону k (от вершины k к k+1) или −1.</summary>
    public int[] Nb = Array.Empty<int>();
    public Vector3d[] TN = Array.Empty<Vector3d>();
    public double[] TA = Array.Empty<double>();
    public double Diag;
    public int Dropped, Flipped, NonManifold, BorderSides;
    public int TriCount => T.Length / 3;
    public bool Closed => BorderSides == 0 && NonManifold == 0;

    public int Vx(int t, int k) => T[t * 3 + k];
    public Vector3d P(int t, int k) => V[T[t * 3 + k]];

    /// <summary>Угол между нормалями треугольника t и соседа через сторону k, градусы (−1 — соседа нет).</summary>
    public double Dihedral(int t, int k)
    {
        int u = Nb[t * 3 + k];
        if (u < 0) return -1;
        double c = Math.Clamp(Vector3d.Dot(TN[t], TN[u]), -1, 1);
        return Math.Acos(c) * 180 / Math.PI;
    }

    [ThreadStatic] public static Action<string>? Debug;

    public static PreparedMesh Prepare(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> tris)
    {
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        var m = new PreparedMesh();
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
        foreach (var (a, b, c) in tris)
            foreach (var p in new[] { a, b, c })
                if (double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z)) { lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }
        m.Diag = tris.Count == 0 ? 0 : (hi - lo).Length;
        double weld = Math.Max(1e-9, m.Diag * 1e-6);

        // слияние вершин
        var verts = new List<Vector3d>();
        var grid = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) Cell(Vector3d p) => ((long)Math.Floor(p.X / weld), (long)Math.Floor(p.Y / weld), (long)Math.Floor(p.Z / weld));
        int Weld(Vector3d p)
        {
            var c = Cell(p);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var l))
                            foreach (int i in l) if ((verts[i] - p).LengthSquared <= weld * weld) return i;
            verts.Add(p);
            if (!grid.TryGetValue(c, out var cl)) grid[c] = cl = new List<int>();
            cl.Add(verts.Count - 1);
            return verts.Count - 1;
        }

        var tl = new List<int>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var (a, b, c) in tris)
        {
            if (!Finite(a) || !Finite(b) || !Finite(c)) { m.Dropped++; continue; }
            int ia = Weld(a), ib = Weld(b), ic = Weld(c);
            if (ia == ib || ib == ic || ia == ic) { m.Dropped++; continue; }
            var n = Vector3d.Cross(verts[ib] - verts[ia], verts[ic] - verts[ia]);
            double longest = Math.Max((verts[ib] - verts[ia]).Length, Math.Max((verts[ic] - verts[ib]).Length, (verts[ia] - verts[ic]).Length));
            if (n.Length / Math.Max(longest, 1e-300) < weld * 0.5) { m.Dropped++; continue; }   // высота меньше допуска слияния
            var key = Sorted(ia, ib, ic);
            if (!seen.Add(key)) { m.Dropped++; continue; }
            tl.Add(ia); tl.Add(ib); tl.Add(ic);
        }
        m.V = verts.ToArray();
        m.T = tl.ToArray();
        Debug?.Invoke($"  weld {sw0.ElapsedMilliseconds} ms: {verts.Count} verts");
        int nt = m.TriCount;

        // согласование обхода
        var sides = Sides(m.T);
        Debug?.Invoke($"  sides {sw0.ElapsedMilliseconds} ms: {sides.Count}, max {sides.Values.Max(l => l.Count)}");
        var flip = new int[nt];   // 0 — не решено, 1 — как есть, 2 — развернуть
        var comps = new List<List<int>>();
        for (int s = 0; s < nt; s++)
        {
            if (flip[s] != 0) continue;
            var comp = new List<int> { s };
            flip[s] = 1;
            var q = new Queue<int>(); q.Enqueue(s);
            while (q.Count > 0)
            {
                int t = q.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    int a = m.T[t * 3 + k], b = m.T[t * 3 + (k + 1) % 3];
                    if (!sides.TryGetValue(Key(a, b), out var on) || on.Count != 2) continue;
                    int u = on[0] == t ? on[1] : on[0];
                    if (flip[u] != 0) continue;
                    // t (с учётом своего разворота) проходит сторону a→b; сосед должен проходить b→a
                    bool tForward = flip[t] == 1;
                    bool uRunsAB = Runs(m.T, u, a, b);
                    flip[u] = (uRunsAB == tForward) ? 2 : 1;
                    comp.Add(u); q.Enqueue(u);
                }
            }
            comps.Add(comp);
        }
        for (int t = 0; t < nt; t++) if (flip[t] == 2) { (m.T[t * 3 + 1], m.T[t * 3 + 2]) = (m.T[t * 3 + 2], m.T[t * 3 + 1]); m.Flipped++; }
        // нормали наружу: по знаку объёма связной части
        foreach (var comp in comps)
        {
            double vol = 0;
            foreach (int t in comp) vol += Vector3d.Dot(m.V[m.T[t * 3]], Vector3d.Cross(m.V[m.T[t * 3 + 1]], m.V[m.T[t * 3 + 2]]));
            if (vol < 0)
                foreach (int t in comp) { (m.T[t * 3 + 1], m.T[t * 3 + 2]) = (m.T[t * 3 + 2], m.T[t * 3 + 1]); m.Flipped++; }
        }

        Debug?.Invoke($"  orient {sw0.ElapsedMilliseconds} ms: comps {comps.Count}");
        // соседи
        sides = Sides(m.T);
        m.Nb = new int[nt * 3];
        for (int t = 0; t < nt; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = m.T[t * 3 + k], b = m.T[t * 3 + (k + 1) % 3];
                var on = sides[Key(a, b)];
                if (on.Count == 2) m.Nb[t * 3 + k] = on[0] == t ? on[1] : on[0];
                else { m.Nb[t * 3 + k] = -1; if (on.Count > 2) m.NonManifold++; else m.BorderSides++; }
            }
        m.TN = new Vector3d[nt]; m.TA = new double[nt];
        for (int t = 0; t < nt; t++)
        {
            var n = Vector3d.Cross(m.P(t, 1) - m.P(t, 0), m.P(t, 2) - m.P(t, 0));
            m.TA[t] = n.Length * 0.5;
            m.TN[t] = n.Length > 0 ? n / n.Length : Vector3d.UnitZ;
        }
        return m;
    }

    static bool Finite(Vector3d p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
    public static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    static Dictionary<long, List<int>> Sides(int[] T)
    {
        var d = new Dictionary<long, List<int>>(T.Length, MixedLong.Instance);
        for (int t = 0; t < T.Length / 3; t++)
            for (int k = 0; k < 3; k++)
            {
                var key = Key(T[t * 3 + k], T[t * 3 + (k + 1) % 3]);
                if (!d.TryGetValue(key, out var l)) d[key] = l = new List<int>(2);
                l.Add(t);
            }
        return d;
    }
    /// <summary>
    /// Хэш стороны (пара номеров вершин в одном long): у long хэш — младшие 32 бита XOR старшие, у соседних вершин это
    /// a XOR b — горстка значений на миллионы сторон, словарь вырождался в список (сетка 624 тыс. треугольников не
    /// готовилась за минуты). Перемешивание — как у SplitMix64.
    /// </summary>
    sealed class MixedLong : IEqualityComparer<long>
    {
        public static readonly MixedLong Instance = new();
        public bool Equals(long x, long y) => x == y;
        public int GetHashCode(long v)
        {
            ulong z = (ulong)v + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (int)(z ^ (z >> 31));
        }
    }

    static bool Runs(int[] T, int t, int a, int b)
    {
        for (int k = 0; k < 3; k++) if (T[t * 3 + k] == a && T[t * 3 + (k + 1) % 3] == b) return true;
        return false;
    }
}
