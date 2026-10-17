using OpenTK.Mathematics;

namespace Parametric3D.Render.Engine;

/// <summary>
/// u12: СЕТКА FEA ДЛЯ ПОКАЗА — равномерные треугольники заданного размера по сетке показа каждой грани. Деление
/// длиннейших сторон пополам (Ривара) до размера h, затем перевороты к Делоне в плоскости пары (только у почти
/// компланарных пар — форма кривой грани сохраняется) и касательное сглаживание внутренних узлов. Стороны между гранями
/// не переворачиваются и не сдвигаются: граница грани остаётся на месте. Это сетка вида расчёта (размер и качество
/// элементов), не решатель: точки лежат на треугольниках показа.
/// </summary>
public static class FeaRemesh
{
    public static List<(Vector3 A, Vector3 B)> Lines(Vector3[] pos, int[] idx, uint[] pick, float h, int maxTris = 400_000)
    {
        var P = new List<Vector3>(pos);
        var T = new List<int>(idx.Length);
        // треугольники по граням: номер выбора первой вершины — грань
        var face = new List<uint>();
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            if ((pos[idx[t]] - pos[idx[t + 1]]).LengthSquared == 0 || (pos[idx[t + 1]] - pos[idx[t + 2]]).LengthSquared == 0) continue;
            T.Add(idx[t]); T.Add(idx[t + 1]); T.Add(idx[t + 2]); face.Add(pick.Length > idx[t] ? pick[idx[t]] : 0);
        }
        float h2 = h * h;
        // ДЕЛЕНИЕ: очередь по длине стороны
        var mid = new Dictionary<(int, int), int>();
        for (int pass = 0; pass < 40 && T.Count / 3 < maxTris; pass++)
        {
            bool any = false;
            int n = T.Count / 3;
            for (int ti = 0; ti < n && T.Count / 3 < maxTris; ti++)
            {
                int t = ti * 3;
                int bk = -1; float bl = h2;
                for (int k = 0; k < 3; k++) { float l = (P[T[t + k]] - P[T[t + (k + 1) % 3]]).LengthSquared; if (l > bl) { bl = l; bk = k; } }
                if (bk < 0) continue;
                int a = T[t + bk], b = T[t + (bk + 1) % 3], c = T[t + (bk + 2) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!mid.TryGetValue(key, out int m)) { m = P.Count; P.Add((P[a] + P[b]) * 0.5f); mid[key] = m; }
                T[t] = a; T[t + 1] = m; T[t + 2] = c;
                T.Add(m); T.Add(b); T.Add(c); face.Add(face[ti]);
                any = true;
            }
            if (!any) break;
        }
        // сосед, разделивший сторону, оставил висячую точку: треугольник со стороной (a, b), у которой есть середина, делится
        for (int rep = 0; rep < 8; rep++)
        {
            bool fixedAny = false;
            int n = T.Count / 3;
            for (int ti = 0; ti < n; ti++)
            {
                int t = ti * 3;
                for (int k = 0; k < 3; k++)
                {
                    int a = T[t + k], b = T[t + (k + 1) % 3], c = T[t + (k + 2) % 3];
                    if (!mid.TryGetValue(a < b ? (a, b) : (b, a), out int m)) continue;
                    T[t] = a; T[t + 1] = m; T[t + 2] = c;
                    T.Add(m); T.Add(b); T.Add(c); face.Add(face[ti]);
                    fixedAny = true; break;
                }
            }
            if (!fixedAny) break;
        }
        // ПЕРЕВОРОТЫ к Делоне у почти компланарных пар одной грани
        for (int pass = 0; pass < 60; pass++)
        {
            var edge = new Dictionary<(int, int), int>();
            for (int t = 0; t < T.Count; t += 3)
                for (int k = 0; k < 3; k++) edge[(T[t + k], T[t + (k + 1) % 3])] = t;
            int flips = 0; var used = new HashSet<int>();
            for (int t = 0; t < T.Count; t += 3)
            {
                if (used.Contains(t)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = T[t + k], b = T[t + (k + 1) % 3], c = T[t + (k + 2) % 3];
                    if (!edge.TryGetValue((b, a), out int u) || used.Contains(u) || face[u / 3] != face[t / 3]) continue;
                    int d = T[u] + T[u + 1] + T[u + 2] - a - b;
                    var n1 = Vector3.Cross(P[b] - P[a], P[c] - P[a]); var n2 = Vector3.Cross(P[a] - P[b], P[d] - P[b]);
                    if (n1.LengthSquared == 0 || n2.LengthSquared == 0 || Vector3.Dot(n1.Normalized(), n2.Normalized()) < 0.985f) continue;
                    float ac = Angle(P[a] - P[c], P[b] - P[c]), ad = Angle(P[a] - P[d], P[b] - P[d]);
                    if (ac + ad <= MathF.PI + 1e-3f) continue;
                    var m1 = Vector3.Cross(P[d] - P[a], P[c] - P[a]); var m2 = Vector3.Cross(P[c] - P[b], P[d] - P[b]);
                    if (Vector3.Dot(m1, n1) <= 0 || Vector3.Dot(m2, n1) <= 0) continue;
                    T[t] = a; T[t + 1] = d; T[t + 2] = c;
                    T[u] = d; T[u + 1] = b; T[u + 2] = c;
                    used.Add(t); used.Add(u); flips++;
                    break;
                }
            }
            if (flips == 0) break;
        }
        // стороны (каждая один раз)
        var res = new List<(Vector3, Vector3)>();
        var seen = new HashSet<(int, int)>();
        for (int t = 0; t < T.Count; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = T[t + k], b = T[t + (k + 1) % 3];
                if (seen.Add(a < b ? (a, b) : (b, a))) res.Add((P[a], P[b]));
            }
        return res;
    }

    static float Angle(Vector3 u, Vector3 v)
    {
        float l = u.Length * v.Length;
        return l == 0 ? 0 : MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / l, -1f, 1f));
    }
}
