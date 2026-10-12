using OpenTK.Mathematics;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// KRN-07 (07.8): КУСКИ БЕЗЬЕ поверхности NURBS — для строгих оценок: рациональный кусок с положительными весами лежит в
/// выпуклой оболочке своих опорных точек; деление пополам (де Кастельжо в однородных координатах) даёт опорные точки
/// половин. Габарит опорных точек куска — строгий габарит его образа.
/// </summary>
public sealed class BezierPatch
{
    public readonly double U0, U1, V0, V1;          // параметры поверхности, которые покрывает кусок
    public readonly Vector4d[,] Pw;                  // (x·w, y·w, z·w, w)
    public BezierPatch(double u0, double u1, double v0, double v1, Vector4d[,] pw) { U0 = u0; U1 = u1; V0 = v0; V1 = v1; Pw = pw; }

    public (Vector3d Lo, Vector3d Hi) Box()
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue);
        foreach (var q in Pw) { var p = new Vector3d(q.X / q.W, q.Y / q.W, q.Z / q.W); lo = Vector3d.ComponentMin(lo, p); hi = Vector3d.ComponentMax(hi, p); }
        return (lo, hi);
    }

    /// <summary>Пополам по u (alongU) или по v: де Кастельжо при 1/2 по рядам однородных точек.</summary>
    public (BezierPatch A, BezierPatch B) Split(bool alongU)
    {
        int nu = Pw.GetLength(0), nv = Pw.GetLength(1);
        var a = new Vector4d[nu, nv]; var b = new Vector4d[nu, nv];
        int n = alongU ? nu : nv, m = alongU ? nv : nu;
        var tmp = new Vector4d[n];
        for (int o = 0; o < m; o++)
        {
            for (int i = 0; i < n; i++) tmp[i] = alongU ? Pw[i, o] : Pw[o, i];
            // левая половина — первые точки на каждом уровне, правая — последние (в обратном порядке)
            for (int r = 0; r < n; r++)
            {
                var left = tmp[0]; var right = tmp[n - 1 - r];
                if (alongU) { a[r, o] = left; b[n - 1 - r, o] = right; } else { a[o, r] = left; b[o, n - 1 - r] = right; }
                for (int i = 0; i < n - 1 - r; i++) tmp[i] = (tmp[i] + tmp[i + 1]) * 0.5;
            }
        }
        double um = (U0 + U1) / 2, vm = (V0 + V1) / 2;
        return alongU ? (new BezierPatch(U0, um, V0, V1, a), new BezierPatch(um, U1, V0, V1, b))
                      : (new BezierPatch(U0, U1, V0, vm, a), new BezierPatch(U0, U1, vm, V1, b));
    }
}

public static class NurbsTools
{
    /// <summary>Куски Безье: каждый внутренний узел поднимается до кратности степени (вставкой), опорные точки делятся на блоки.</summary>
    public static List<BezierPatch> Beziers(NurbsSurface s)
    {
        var cur = s;
        foreach (double t in s.KnotsU.Skip(s.DegreeU + 1).Take(s.KnotsU.Count - 2 * (s.DegreeU + 1)).Distinct().ToList())
            while (cur.MultiplicityU(t) < cur.DegreeU) cur = cur.InsertKnotU(t);
        foreach (double t in s.KnotsV.Skip(s.DegreeV + 1).Take(s.KnotsV.Count - 2 * (s.DegreeV + 1)).Distinct().ToList())
            while (cur.MultiplicityV(t) < cur.DegreeV) cur = cur.InsertKnotV(t);
        int du = cur.DegreeU, dv = cur.DegreeV;
        var ku = cur.KnotsU.Distinct().OrderBy(x => x).ToList(); var kv = cur.KnotsV.Distinct().OrderBy(x => x).ToList();
        var res = new List<BezierPatch>();
        for (int i = 0; i + 1 < ku.Count; i++)
            for (int j = 0; j + 1 < kv.Count; j++)
            {
                var pw = new Vector4d[du + 1, dv + 1];
                for (int a = 0; a <= du; a++)
                    for (int b = 0; b <= dv; b++)
                    {
                        var p = cur.ControlPoint(i * du + a, j * dv + b); double w = cur.Weight(i * du + a, j * dv + b);
                        pw[a, b] = new Vector4d(p.X * w, p.Y * w, p.Z * w, w);
                    }
                res.Add(new BezierPatch(ku[i], ku[i + 1], kv[j], kv[j + 1], pw));
            }
        return res;
    }
}
