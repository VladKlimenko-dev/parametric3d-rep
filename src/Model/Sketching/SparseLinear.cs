namespace Parametric3D.Model.Sketching;

/// <summary>
/// KRN-10 (10.8): РАЗРЕЖЕННАЯ ЛИНЕЙНАЯ АЛГЕБРА РЕШАТЕЛЯ ЭСКИЗА. Связный большой эскиз (сетка, массив с общими размерами)
/// решался плотными матрицами: шаг Левенберга — Марквардта O(n³), ранг якобиана — плотное исключение O(m·n²), доводка —
/// плотная J·Jᵀ. Здесь: упорядочение обратным Катхиллом — Макки (лента), разложение Холецкого в профиле (skyline) для
/// симметричных положительно определённых систем, ранг — исключение Гаусса по разреженным строкам с выбором главного
/// элемента по столбцу (тот же порог на нормированных строках, что у плотного).
/// </summary>
internal static class SparseLinear
{
    /// <summary>Порядок обратного Катхилла — Макки для симметричного шаблона (соседи вершин).</summary>
    public static int[] Rcm(List<int>[] adj)
    {
        int n = adj.Length;
        var order = new List<int>(n); var seen = new bool[n];
        var deg = adj.Select(a => a.Count).ToArray();
        while (order.Count < n)
        {
            int start = -1;
            for (int i = 0; i < n; i++) if (!seen[i] && (start < 0 || deg[i] < deg[start])) start = i;
            var q = new Queue<int>(); q.Enqueue(start); seen[start] = true;
            while (q.Count > 0)
            {
                int v = q.Dequeue(); order.Add(v);
                foreach (int w in adj[v].Where(w => !seen[w]).OrderBy(w => deg[w])) { seen[w] = true; q.Enqueue(w); }
            }
        }
        order.Reverse();
        return order.ToArray();
    }

    /// <summary>
    /// Решение A·x = b для симметричной положительно определённой разреженной A (строки — словари столбец → значение,
    /// заданы обе половины). Холецкий в профиле после RCM. false — не положительно определена (опорный элемент ≤ 0).
    /// </summary>
    public static bool SolveSpd(Dictionary<int, double>[] A, double[] b, out double[] x)
    {
        int n = A.Length; x = new double[n];
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = A[i].Keys.Where(j => j != i).ToList();
        var perm = Rcm(adj); var inv = new int[n]; for (int k = 0; k < n; k++) inv[perm[k]] = k;
        // профиль: для строки i (в новом порядке) — первый ненулевой столбец
        var first = new int[n];
        for (int i = 0; i < n; i++)
        {
            int f = i;
            foreach (var j in A[perm[i]].Keys) { int jj = inv[j]; if (jj < f) f = jj; }
            first[i] = f;
        }
        var start = new int[n + 1];
        for (int i = 0; i < n; i++) start[i + 1] = start[i] + (i - first[i] + 1);
        var L = new double[start[n]];
        double Get(int i, int j) => j < first[i] ? 0 : L[start[i] + (j - first[i])];
        for (int i = 0; i < n; i++)
            foreach (var (j, v) in A[perm[i]]) { int jj = inv[j]; if (jj <= i) L[start[i] + (jj - first[i])] = v; }
        // разложение: L·Lᵀ, по строкам
        double maxDiag = 0; for (int i = 0; i < n; i++) maxDiag = Math.Max(maxDiag, Math.Abs(Get(i, i)));
        for (int i = 0; i < n; i++)
        {
            for (int j = first[i]; j <= i; j++)
            {
                double s = L[start[i] + (j - first[i])];
                int k0 = Math.Max(first[i], first[j]);
                for (int k = k0; k < j; k++) s -= L[start[i] + (k - first[i])] * L[start[j] + (k - first[j])];
                if (j == i)
                {
                    if (!(s > 1e-300) || s < 1e-15 * maxDiag) return false;
                    L[start[i] + (j - first[i])] = Math.Sqrt(s);
                }
                else L[start[i] + (j - first[i])] = s / L[start[j] + (j - first[j])];
            }
        }
        // прямой и обратный ход
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = b[perm[i]];
            for (int k = first[i]; k < i; k++) s -= L[start[i] + (k - first[i])] * y[k];
            y[i] = s / L[start[i] + (i - first[i])];
        }
        var z = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            z[i] = y[i] / L[start[i] + (i - first[i])];
            for (int k = first[i]; k < i; k++) y[k] -= L[start[i] + (k - first[i])] * z[i];
        }
        for (int i = 0; i < n; i++) x[perm[i]] = z[i];
        return true;
    }

    /// <summary>
    /// Ранг разреженной матрицы m×n (строки — словари, уже нормированные): исключение по столбцам в порядке RCM шаблона
    /// JᵀJ, опорный элемент — наибольший по модулю в столбце среди оставшихся строк, порог eps (как у плотного).
    /// </summary>
    public static int Rank(List<Dictionary<int, double>> rows, int n, double eps)
    {
        int m = rows.Count;
        var colRows = new HashSet<int>[n];
        for (int j = 0; j < n; j++) colRows[j] = new HashSet<int>();
        for (int r = 0; r < m; r++) foreach (var j in rows[r].Keys) colRows[j].Add(r);
        var adj = new List<int>[n];
        for (int j = 0; j < n; j++) adj[j] = new List<int>();
        var nb = new HashSet<int>[n]; for (int j = 0; j < n; j++) nb[j] = new HashSet<int>();
        foreach (var row in rows) foreach (var a in row.Keys) foreach (var c in row.Keys) if (a != c) nb[a].Add(c);
        for (int j = 0; j < n; j++) adj[j] = nb[j].ToList();
        var order = Rcm(adj);
        var used = new bool[m]; int rank = 0;
        foreach (int col in order)
        {
            int piv = -1; double best = eps;
            foreach (int r in colRows[col]) { if (used[r]) continue; double a = Math.Abs(rows[r].TryGetValue(col, out var v) ? v : 0); if (a > best) { best = a; piv = r; } }
            if (piv < 0) continue;
            used[piv] = true; rank++;
            var prow = rows[piv]; double pv = prow[col];
            foreach (int r in colRows[col].ToList())
            {
                if (r == piv || used[r]) continue;
                if (!rows[r].TryGetValue(col, out var rv) || rv == 0) { colRows[col].Remove(r); continue; }
                double f = rv / pv;
                foreach (var (j, v) in prow)
                {
                    double nv = (rows[r].TryGetValue(j, out var old) ? old : 0) - f * v;
                    if (j == col || Math.Abs(nv) < 1e-300) { rows[r].Remove(j); colRows[j].Remove(r); }
                    else { if (!rows[r].ContainsKey(j)) colRows[j].Add(r); rows[r][j] = nv; }
                }
            }
        }
        return rank;
    }
}
