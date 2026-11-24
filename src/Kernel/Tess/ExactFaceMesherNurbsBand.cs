using OpenTK.Mathematics;
using Parametric3D.Core;
using Parametric3D.Kernel.Surfaces;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Tess;

public static partial class ExactFaceMesher
{
    /// <summary>
    /// Обобщённая сетка периодической NURBS-ленты (ClosedU) с двумя контурами — трубчатая стенка, канавка, кольцо.
    /// Строит регулярную сетку по u/v самой поверхности: контуры проецируются и разворачиваются по периоду,
    /// затем лента замыкается шовной колонкой, которая в 3D совпадает с первой (для сварки).
    /// Срабатывает после MeshLoops, когда ленточный замыкатель не смог сшить шов.
    /// </summary>
    internal static (List<Vector3d> P, List<int> T)? MeshPeriodicNurbsBand(TFace f, NurbsSurface ns, List<List<(TEdge E, bool Rev)>> cycles, out string? why)
    {
        why = null;
        if (!ns.ClosedU || cycles.Count != 2) return null;
        var a3 = LoopPoints3(cycles[0]);
        var b3 = LoopPoints3(cycles[1]);
        if (a3.Count < 4 || b3.Count < 4) return null;

        var au = Unwrapped(a3.Select(ns.ProjectToUV).ToList(), 1.0);
        var bu = Unwrapped(b3.Select(ns.ProjectToUV).ToList(), 1.0);
        // Развернём оба контура в одну сторону (возрастание u), если обход оказался обратным.
        if (au[^1].X < au[0].X) { au.Reverse(); a3.Reverse(); }
        if (bu[^1].X < bu[0].X) { bu.Reverse(); b3.Reverse(); }
        double av0 = au.Average(q => q.Y), bv0 = bu.Average(q => q.Y);
        if (av0 > bv0) { (a3, b3) = (b3, a3); (au, bu) = (bu, au); (av0, bv0) = (bv0, av0); }
        double v0 = av0, v1 = bv0;
        if (v1 - v0 < 1e-9) return null;

        var uA = au.Select(q => q.X).ToList();
        var uB = bu.Select(q => q.X).ToList();
        double shift = Math.Min(uA.Min(), uB.Min());
        for (int i = 0; i < uA.Count; i++) uA[i] -= shift;
        for (int i = 0; i < uB.Count; i++) uB[i] -= shift;

        // Общая колонка u: используем колонку A, для B интерполируем/ищем ближайшую точку.
        var uCols = uA;
        var b3r = new List<Vector3d>();
        for (int i = 0; i < uCols.Count; i++)
        {
            int bi = 0; double best = double.MaxValue;
            for (int k = 0; k < uB.Count; k++) { double d = Math.Abs(uB[k] - uCols[i]); if (d < best) { best = d; bi = k; } }
            if (best < 1e-6) { b3r.Add(b3[bi]); continue; }
            // линейная интерполяция по u между соседями B
            int k0 = bi > 0 && uB[bi] > uCols[i] ? bi - 1 : bi;
            int k1 = k0 + 1;
            if (k1 >= b3.Count) { b3r.Add(b3[^1]); continue; }
            double du = uB[k1] - uB[k0]; if (Math.Abs(du) < 1e-12) { b3r.Add(b3[k0]); continue; }
            double tt = (uCols[i] - uB[k0]) / du;
            b3r.Add(b3[k0] + (b3[k1] - b3[k0]) * tt);
        }

        int nu0 = uCols.Count, nu = nu0 + 1;
        // Адаптивное подразбиение по v по отклонению хорды.
        var vs = new List<double> { v0, v1 };
        int guard = 0;
        while (guard++ < 40 && vs.Count - 2 < 64)
        {
            bool ok = true;
            for (int j = 0; j + 1 < vs.Count; j++)
            {
                double vm = (vs[j] + vs[j + 1]) / 2;
                double worst = 0;
                for (int i = 0; i < nu0; i++)
                {
                    var p = ns.PointAt(new Vector2d(uCols[i], vm));
                    var lo = ns.PointAt(new Vector2d(uCols[i], vs[j]));
                    var hi = ns.PointAt(new Vector2d(uCols[i], vs[j + 1]));
                    worst = Math.Max(worst, (p - (lo + hi) / 2).Length);
                }
                if (worst > Tol.Chord) { vs.Insert(j + 1, vm); ok = false; break; }
            }
            if (ok) break;
        }
        int nv = vs.Count;
        if ((long)nu * nv > 200000) { why = "периодическая лента: слишком мелкая сетка"; return null; }

        var P = new List<Vector3d>(nu * nv);
        var UV = new List<Vector2d>(nu * nv);
        for (int i = 0; i < nu; i++)
        {
            bool seam = i == nu0;
            int ip = seam ? 0 : i;
            double u = seam ? 1.0 : uCols[i];
            for (int j = 0; j < nv; j++)
            {
                Vector3d p;
                if (j == 0) p = a3[ip];
                else if (j == nv - 1) p = b3r[ip];
                else p = ns.PointAt(new Vector2d(uCols[ip], vs[j]));
                P.Add(p); UV.Add(new Vector2d(u, vs[j]));
            }
        }

        var T = new List<int>();
        for (int i = 0; i < nu0; i++)
        {
            int i1 = i + 1;
            for (int j = 0; j < nv - 1; j++)
            {
                int a = i * nv + j;
                int b = i1 * nv + j;
                int c = i1 * nv + j + 1;
                int d = i * nv + j + 1;
                T.Add(a); T.Add(b); T.Add(d);
                T.Add(b); T.Add(c); T.Add(d);
            }
        }

        // Проверим только отсутствие вырожденных треугольников и положительную площадь; сетка по построению
        // лежит на поверхности. Финальная сварка и проверка открытых рёбер выполняются на уровне тела.
        double area = 0; int neg = 0, zero = 0;
        for (int t = 0; t + 2 < T.Count; t += 3)
        {
            var pa = P[T[t]]; var pb = P[T[t + 1]]; var pc = P[T[t + 2]];
            var n = Vector3d.Cross(pb - pa, pc - pa);
            double a2 = n.Length; if (a2 < 1e-18) { zero++; continue; }
            double s = Vector3d.Dot(n, ns.NormalAt(ns.ProjectToUV((pa + pb + pc) / 3)));
            if (s < 0) neg++;
            area += a2 / 2;
        }
        if (area < 1e-12 || zero > T.Count / 12) { why = $"периодическая лента: вырожденная сетка (area {area:G3}, zero {zero}, neg {neg})"; return null; }
        Interlocked.Increment(ref NurbsBandGrids);
        return (P, T);
    }

    static List<Vector3d> LoopPoints3(List<(TEdge E, bool Rev)> cycle)
    {
        var pts = new List<Vector3d>();
        foreach (var (e, _) in cycle)
        {
            var q = new List<Vector3d>();
            if (e.Curve != null) e.Curve.Tessellate(q, pts.Count == 0);
            if (pts.Count > 0 && q.Count > 0 && (pts[^1] - q[0]).LengthSquared < 1e-18) q.RemoveAt(0);
            pts.AddRange(q);
        }
        // Убираем замыкающую копию: шовная колонка создаётся отдельно и в 3D повторяет первую.
        if (pts.Count > 1 && (pts[^1] - pts[0]).LengthSquared < 1e-18) pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    static long NurbsBandGrids;
}
