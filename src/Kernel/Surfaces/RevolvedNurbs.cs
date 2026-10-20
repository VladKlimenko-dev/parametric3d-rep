using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// m.t3: ПОВЕРХНОСТЬ ВРАЩЕНИЯ ДУГИ — ТОЧНО рациональным B-сплайном (биквадратным): образующая — куски дуги ≤ 90°
/// (рациональные квадратичные: P0, P1 на пересечении касательных, P2; веса 1, cos(φ/2), 1), оборот — так же четвертями.
/// Опорная точка сетки — опорная точка образующей, повёрнутая опорной точкой окружности (радиальная часть × (cx, cy)),
/// вес — произведение весов. Параметры: u — по образующей (0…1), v — по обороту (0…1). Нужна для булевой над гранью
/// веретенообразного тора с полюсами («лимон», «яблоко»): общий маршрут ведёт такую грань как NURBS.
/// </summary>
public static class RevolvedNurbs
{
    /// <summary>Окружность образующей пересекает ось (веретенообразный тор), полный оборот.</summary>
    public static bool IsSpindle(RevolvedSurface rv)
    {
        if (rv.Base is not ArcCurve3 a || Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) > 1e-9) return false;
        var w = rv.Axis.W.Normalized(); var d = a.Frame.Origin - rv.Axis.Origin; var rad = d - w * Vector3d.Dot(d, w);
        if (rad.Length < 1e-9 * Math.Max(1, a.Radius)) return false;          // шар
        if (Math.Abs(Vector3d.Dot(a.Frame.W.Normalized(), Vector3d.Cross(w, rad.Normalized()))) < 1 - 1e-9) return false;   // не меридиан
        return rad.Length < a.Radius * (1 - 1e-9);
    }

    static (List<Vector3d> P, List<double> W, double[] K) ArcPieces(Func<double, Vector3d> at, Func<double, Vector3d> tan, double rad, double a0, double a1)
    {
        int m = Math.Max(1, (int)Math.Ceiling(Math.Abs(a1 - a0) / (Math.PI / 2) - 1e-9));
        var P = new List<Vector3d>(); var W = new List<double>(); var K = new List<double> { 0, 0, 0 };
        for (int k = 0; k < m; k++)
        {
            double t0 = a0 + (a1 - a0) * k / m, t1 = a0 + (a1 - a0) * (k + 1) / m, h = Math.Abs(t1 - t0) / 2;
            var p0 = at(t0); var p2 = at(t1);
            var p1 = p0 + tan(t0) * (rad * Math.Tan(h));          // пересечение касательных в концах куска
            if (k == 0) { P.Add(p0); W.Add(1); }
            P.Add(p1); W.Add(Math.Cos(h)); P.Add(p2); W.Add(1);
            if (k < m - 1) { K.Add((double)(k + 1) / m); K.Add((double)(k + 1) / m); }
        }
        K.Add(1); K.Add(1); K.Add(1);
        return (P, W, K.ToArray());
    }

    /// <summary>
    /// k78: образующая — дуга (шар, тор: биквадратная) или ОТРЕЗОК (конус, цилиндр вращения: степень 1 по образующей) —
    /// для неравномерного масштаба тела (TopologyTransform.ScaledAxes: рациональная сетка переносится аффинно точно).
    /// </summary>
    public static NurbsSurface? Of(RevolvedSurface rv, bool lines)
    {
        if (!lines || rv.Base is not LineCurve3 ln) return Of(rv);
        return Build(rv, new List<Vector3d> { ln.A, ln.B }, new List<double> { 1, 1 }, new double[] { 0, 0, 1, 1 }, 1);
    }

    public static NurbsSurface? Of(RevolvedSurface rv)
    {
        if (rv.Base is not ArcCurve3 a) return null;
        var F = a.Frame; double R = a.Radius; var U = F.U.Normalized(); var V = Vector3d.Cross(F.W.Normalized(), U);
        Vector3d At(double t) => F.Origin + (U * Math.Cos(t) + V * Math.Sin(t)) * R;
        double sg = Math.Sign(a.A1 - a.A0);
        Vector3d Tan(double t) => (V * Math.Cos(t) - U * Math.Sin(t)) * sg;
        var (bp, bw, ku) = ArcPieces(At, Tan, R, a.A0, a.A1);
        return Build(rv, bp, bw, ku, 2);
    }

    static NurbsSurface Build(RevolvedSurface rv, List<Vector3d> bp, List<double> bw, double[] ku, int degU)
    {
        // окружность оборота: единичные (cx, cy) от угла A0 до A1
        var (cp, cw, kv) = ArcPieces(t => new Vector3d(Math.Cos(t), Math.Sin(t), 0), t => new Vector3d(-Math.Sin(t), Math.Cos(t), 0) * Math.Sign(rv.A1 - rv.A0), 1, rv.A0, rv.A1);
        var o = rv.Axis.Origin; var w = rv.Axis.W.Normalized();
        var net = new Vector3d[bp.Count, cp.Count]; var wt = new double[bp.Count, cp.Count];
        for (int i = 0; i < bp.Count; i++)
        {
            var d = bp[i] - o; double z = Vector3d.Dot(d, w); var rad = d - w * z; var perp = Vector3d.Cross(w, rad);
            for (int j = 0; j < cp.Count; j++) { net[i, j] = o + w * z + rad * cp[j].X + perp * cp[j].Y; wt[i, j] = bw[i] * cw[j]; }
        }
        // параметры: u ∈ [0, 1] по образующей (узлы ku), v ∈ [0, 1] по обороту
        return new NurbsSurface(net, degU, 2, wt, ku, kv);
    }
}
