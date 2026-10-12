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

    /// <summary>Точка многочлена куска при параметрах поверхности (u, v) — и за пределами куска (продолжение).</summary>
    public Vector3d Eval(double u, double v)
    {
        int nu = Pw.GetLength(0), nv = Pw.GetLength(1);
        double a = (u - U0) / (U1 - U0), b = (v - V0) / (V1 - V0);
        var col = new Vector4d[nu]; var row = new Vector4d[nv];
        for (int i = 0; i < nu; i++)
        {
            for (int j = 0; j < nv; j++) row[j] = Pw[i, j];
            for (int r = 1; r < nv; r++) for (int j = 0; j < nv - r; j++) row[j] = row[j] * (1 - b) + row[j + 1] * b;
            col[i] = row[0];
        }
        for (int r = 1; r < nu; r++) for (int i = 0; i < nu - r; i++) col[i] = col[i] * (1 - a) + col[i + 1] * a;
        return new Vector3d(col[0].X / col[0].W, col[0].Y / col[0].W, col[0].Z / col[0].W);
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

    /// <summary>Кусок того же многочлена над [u0,u1]×[v0,v1] (параметры поверхности; можно и за пределами куска — продолжение
    /// многочлена): опорные точки — полярные формы (блоссом) в концах.</summary>
    public BezierPatch Sub(double u0, double u1, double v0, double v1)
    {
        int nu = Pw.GetLength(0), nv = Pw.GetLength(1);
        double la = (u0 - U0) / (U1 - U0), lb = (u1 - U0) / (U1 - U0), ma = (v0 - V0) / (V1 - V0), mb = (v1 - V0) / (V1 - V0);
        var t = new Vector4d[nu, nv];
        var row = new Vector4d[Math.Max(nu, nv)];
        for (int j = 0; j < nv; j++)
        {
            for (int i = 0; i < nu; i++) row[i] = Pw[i, j];
            var r = Reparam(row, nu, la, lb); for (int i = 0; i < nu; i++) t[i, j] = r[i];
        }
        var o = new Vector4d[nu, nv];
        for (int i = 0; i < nu; i++)
        {
            for (int j = 0; j < nv; j++) row[j] = t[i, j];
            var r = Reparam(row, nv, ma, mb); for (int j = 0; j < nv; j++) o[i, j] = r[j];
        }
        return new BezierPatch(u0, u1, v0, v1, o);
    }

    static Vector4d[] Reparam(Vector4d[] P, int count, double a, double b)
    {
        int n = count - 1; var res = new Vector4d[count]; var tmp = new Vector4d[count];
        for (int i = 0; i <= n; i++)
        {
            for (int k = 0; k <= n; k++) tmp[k] = P[k];
            // блоссом: i аргументов b, n − i аргументов a
            for (int r = 0; r < n; r++)
            {
                double s = r < i ? b : a;
                for (int k = 0; k < n - r; k++) tmp[k] = tmp[k] * (1 - s) + tmp[k + 1] * s;
            }
            res[i] = tmp[0];
        }
        return res;
    }

    /// <summary>СТРОГИЕ интервалы производных Su, Sv (на единицу параметра поверхности) по куску: S = P/w, Su = (Pu − S·wu)/w;
    /// Pu, wu — по выпуклой оболочке годографа однородного многочлена (центрированная форма), S — по габариту, w — по весам
    /// (нужны w &gt; 0).</summary>
    public (Vector3d Lo, Vector3d Hi, Vector3d SuLo, Vector3d SuHi, Vector3d SvLo, Vector3d SvHi)? DerivBounds()
    {
        int nu = Pw.GetLength(0), nv = Pw.GetLength(1);
        double wmin = double.MaxValue, wmax = double.MinValue;
        foreach (var q in Pw) { wmin = Math.Min(wmin, q.W); wmax = Math.Max(wmax, q.W); }
        if (!(wmin > 0)) return null;
        var (lo, hi) = Box();
        // центрированная форма (без зависимости S и wu): Pu − S·wu = (P − c·w)u − (S − c)·wu, c — центр габарита;
        // первое — годограф многочлена P − c·w (оболочка его опорных точек), второе — не больше полуширины габарита на |wu|
        var cen = (lo + hi) / 2; var half = (hi - lo) / 2;
        (Vector3d, Vector3d) Der(bool alongU)
        {
            int p = (alongU ? nu : nv) - 1; double len = alongU ? U1 - U0 : V1 - V0;
            var dlo = new Vector4d(double.MaxValue); var dhi = new Vector4d(double.MinValue);
            Vector4d Cq(int i, int j) { var q = Pw[i, j]; return new Vector4d(q.X - cen.X * q.W, q.Y - cen.Y * q.W, q.Z - cen.Z * q.W, q.W); }
            if (p == 0) { dlo = dhi = Vector4d.Zero; }
            else
                for (int i = 0; i < (alongU ? p : nu); i++)
                    for (int j = 0; j < (alongU ? nv : p); j++)
                    {
                        var d = (alongU ? Cq(i + 1, j) - Cq(i, j) : Cq(i, j + 1) - Cq(i, j)) * (p / len);
                        dlo = Vector4d.ComponentMin(dlo, d); dhi = Vector4d.ComponentMax(dhi, d);
                    }
            double wuAbs = Math.Max(Math.Abs(dlo.W), Math.Abs(dhi.W));
            var rlo = new Vector3d(); var rhi = new Vector3d();
            for (int k = 0; k < 3; k++)
            {
                double nl = dlo[k] - half[k] * wuAbs, nh = dhi[k] + half[k] * wuAbs;
                double q1 = nl / wmin, q2 = nl / wmax, q3 = nh / wmin, q4 = nh / wmax;
                rlo[k] = Math.Min(Math.Min(q1, q2), Math.Min(q3, q4)); rhi[k] = Math.Max(Math.Max(q1, q2), Math.Max(q3, q4));
            }
            return (rlo, rhi);
        }
        var (ul, uh) = Der(true); var (vl, vh) = Der(false);
        return (lo, hi, ul, uh, vl, vh);
    }

    /// <summary>СТРОГИЙ интервал g·S_w (производная S вдоль направления w = (wu, wv) в параметрах, спроектированная на g) по
    /// куску — по скалярному многочлену g·(P − c·w): без «обёртки» покомпонентных интервалов (у почти касания g·S_w — малая
    /// разность больших чисел).</summary>
    public (double Lo, double Hi)? DirBound(Vector3d g, Vector2d w)
    {
        int nu = Pw.GetLength(0), nv = Pw.GetLength(1);
        double wmin = double.MaxValue, wmax = double.MinValue;
        foreach (var q in Pw) { wmin = Math.Min(wmin, q.W); wmax = Math.Max(wmax, q.W); }
        if (!(wmin > 0)) return null;
        var (lo, hi) = Box(); var cen = (lo + hi) / 2; var half = (hi - lo) / 2;
        double N(int i, int j) { var q = Pw[i, j]; return g.X * (q.X - cen.X * q.W) + g.Y * (q.Y - cen.Y * q.W) + g.Z * (q.Z - cen.Z * q.W); }
        (double, double, double, double) Hod(bool alongU)
        {
            int p = (alongU ? nu : nv) - 1; double len = alongU ? U1 - U0 : V1 - V0;
            if (p == 0) return (0, 0, 0, 0);
            double nl = double.MaxValue, nh = double.MinValue, wl = double.MaxValue, wh = double.MinValue;
            for (int i = 0; i < (alongU ? p : nu); i++)
                for (int j = 0; j < (alongU ? nv : p); j++)
                {
                    double dn = (alongU ? N(i + 1, j) - N(i, j) : N(i, j + 1) - N(i, j)) * (p / len);
                    double dw = (alongU ? Pw[i + 1, j].W - Pw[i, j].W : Pw[i, j + 1].W - Pw[i, j].W) * (p / len);
                    nl = Math.Min(nl, dn); nh = Math.Max(nh, dn); wl = Math.Min(wl, dw); wh = Math.Max(wh, dw);
                }
            return (nl, nh, wl, wh);
        }
        var (unl, unh, uwl, uwh) = w.X == 0 ? (0, 0, 0, 0) : Hod(true); var (vnl, vnh, vwl, vwh) = w.Y == 0 ? (0.0, 0.0, 0.0, 0.0) : Hod(false);
        static (double, double) Sc(double a, double b, double k) => k >= 0 ? (a * k, b * k) : (b * k, a * k);
        var (a1, b1) = Sc(unl, unh, w.X); var (a2, b2) = Sc(vnl, vnh, w.Y);
        var (c1, d1) = Sc(uwl, uwh, w.X); var (c2, d2) = Sc(vwl, vwh, w.Y);
        double wd = Math.Max(Math.Abs(c1 + c2), Math.Abs(d1 + d2));
        double gs = Math.Abs(g.X) * half.X + Math.Abs(g.Y) * half.Y + Math.Abs(g.Z) * half.Z;
        double nlo = a1 + a2 - gs * wd, nhi = b1 + b2 + gs * wd;
        double q1 = nlo / wmin, q2 = nlo / wmax, q3 = nhi / wmin, q4 = nhi / wmax;
        return (Math.Min(Math.Min(q1, q2), Math.Min(q3, q4)), Math.Max(Math.Max(q1, q2), Math.Max(q3, q4)));
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

    /// <summary>Строгие интервалы Su, Sv по прямоугольнику параметров [u0,u1]×[v0,v1] (может выходить за область — продолжение
    /// крайних кусков) по всем кускам Безье, которые он задевает; null — вес ≤ 0 у куска.</summary>
    public static (Vector3d SuLo, Vector3d SuHi, Vector3d SvLo, Vector3d SvHi)? DerivRange(List<BezierPatch> bez, double kU0, double kU1, double kV0, double kV1,
                                                                                          double u0, double u1, double v0, double v1)
    {
        var a = new Vector3d(double.MaxValue); var b = new Vector3d(double.MinValue); var c = a; var d = b; bool any = false;
        foreach (var bp in bez)
        {
            double eu0 = bp.U0 <= kU0 ? double.NegativeInfinity : bp.U0, eu1 = bp.U1 >= kU1 ? double.PositiveInfinity : bp.U1;
            double ev0 = bp.V0 <= kV0 ? double.NegativeInfinity : bp.V0, ev1 = bp.V1 >= kV1 ? double.PositiveInfinity : bp.V1;
            double su0 = Math.Max(u0, eu0), su1 = Math.Min(u1, eu1), sv0 = Math.Max(v0, ev0), sv1 = Math.Min(v1, ev1);
            if (su0 >= su1 || sv0 >= sv1) continue;
            if (bp.Sub(su0, su1, sv0, sv1).DerivBounds() is not { } db) return null;
            a = Vector3d.ComponentMin(a, db.SuLo); b = Vector3d.ComponentMax(b, db.SuHi);
            c = Vector3d.ComponentMin(c, db.SvLo); d = Vector3d.ComponentMax(d, db.SvHi); any = true;
        }
        return any ? (a, b, c, d) : null;
    }
}
