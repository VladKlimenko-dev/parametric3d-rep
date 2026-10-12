using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// Объём «уголка» по теореме о дивергенции, независимо от AnalyticVolume: три полосы — лента (по её поверхности, сечение
    /// при параметре, найденном проекцией линии касания), полоса на отверстии (образующая от линии касания до вала) и полоса
    /// на валу (по цилиндрическим координатам вала от той же точки ребра до линии касания). Вал — ось X радиуса R.
    /// </summary>
    static double CuspByDivergence(TSolid s, double R, Vector3d holeAxis)
    {
        var blends = s.Faces.Where(f => GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface).ToList();
        var ss = (Parametric3D.Kernel.Surfaces.SectionSurface)GeometryAudit.AuthoritativeSurface(blends[0])!;
        // линии касания: ребро ленты на валу (|yz| = R) и на отверстии
        var cont = blends.SelectMany(f => f.Edges).Where(e => e.Curve is Parametric3D.Kernel.Curves.ProcCurve3).Distinct().ToList();
        Func<double, Vector3d>? qa = null, qb = null; // по углу
        double gx(int i) => new[] { -0.9739065285171717, -0.8650633666889845, -0.6794095682990244, -0.4333953941292472, -0.1488743389816312, 0.1488743389816312, 0.4333953941292472, 0.6794095682990244, 0.8650633666889845, 0.9739065285171717 }[i];
        double gw(int i) => new[] { 0.0666713443086881, 0.1494513491505806, 0.2190863625159820, 0.2692667193099963, 0.2955242247147529, 0.2955242247147529, 0.2692667193099963, 0.2190863625159820, 0.1494513491505806, 0.0666713443086881 }[i];
        var shaftEdges = cont.Where(e => Math.Abs(new Vector2d(e.Curve.PointAt(e.Curve.T0 * 0.5 + e.Curve.T1 * 0.5).Y, e.Curve.PointAt(e.Curve.T0 * 0.5 + e.Curve.T1 * 0.5).Z).Length - R) < 1e-6).ToList();
        var holeEdges = cont.Except(shaftEdges).ToList();
        double total = 0;
        // по каждой половине: параметр t кривых касания общий (угол вокруг оси отверстия)
        foreach (var ea in shaftEdges)
        {
            var c = ea.Curve; var eb = holeEdges.First(e => Math.Abs(e.Curve.T0 - c.T0) < 1e-12 && Math.Abs(e.Curve.T1 - c.T1) < 1e-12);
            Vector3d QA(double t) => c.PointAt(t); Vector3d QB(double t) => eb.Curve.PointAt(t);
            Vector3d Edge(double t) { var q = QB(t); var w = holeAxis.Normalized(); double wy = w.Y, wz = w.Z, A2 = wy * wy + wz * wz, B2 = q.Y * wy + q.Z * wz, C2 = q.Y * q.Y + q.Z * q.Z - R * R; double sgn = 1; double sx = (-B2 + Math.Sqrt(B2 * B2 - A2 * C2)) / A2, sy = (-B2 - Math.Sqrt(B2 * B2 - A2 * C2)) / A2; double sp = Math.Abs(sx) < Math.Abs(sy) ? sx : sy; _ = sgn; return q + w * sp; }
            Vector3d Band(double t, double v) { var u = ss.ProjectToUV(QA(t)).X; return ss.ExactSectionAt(u).Point(v); }
            Vector3d HoleStrip(double t, double l) => Edge(t) + (QB(t) - Edge(t)) * l;
            Vector3d ShaftStrip(double t, double l)
            {
                var e = Edge(t); var q = QA(t);
                double te = Math.Atan2(e.Z, e.Y), tq = Math.Atan2(q.Z, q.Y), th = te + (tq - te) * l, x = e.X + (q.X - e.X) * l;
                return new Vector3d(x, R * Math.Cos(th), R * Math.Sin(th));
            }
            int nT = 200;
            double Patch(Func<double, double, Vector3d> P, double t0, double t1)
            {
                double sum = 0, h = 1e-6;
                for (int i = 0; i < nT; i++) for (int a = 0; a < 10; a++)
                {
                    double t = t0 + (t1 - t0) * (i + (gx(a) + 1) / 2) / nT, wt = gw(a) / 2 * (t1 - t0) / nT;
                    for (int b = 0; b < 10; b++)
                    {
                        double l = (gx(b) + 1) / 2, wl = gw(b) / 2;
                        var p = P(t, l);
                        var du = (P(t + h, l) - P(t - h, l)) / (2 * h);
                        var dv = (P(t, Math.Min(1, l + h)) - P(t, Math.Max(0, l - h))) / (Math.Min(1, l + h) - Math.Max(0, l - h));
                        sum += Vector3d.Dot(p, Vector3d.Cross(du, dv)) / 3 * wt * wl;
                    }
                }
                return sum;
            }
            // ориентация: каждая полоса берётся с её знаком так, чтобы поверхность была замкнутой: лента (t, v: A→B),
            // полоса отверстия (t, l: E→B), полоса вала (t, l: E→A) — граница [A→B] − [E→B] + [E→A] сходится
            total += Patch(Band, c.T0, c.T1) - Patch(HoleStrip, c.T0, c.T1) + Patch(ShaftStrip, c.T0, c.T1);
        }
        return Math.Abs(total);
    }
}
