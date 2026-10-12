using OpenTK.Mathematics;

static partial class Program
{
    /// <summary>
    /// Проба геометрии катящегося шара между двумя цилиндрами с перпендикулярными пересекающимися осями: вал (ось X, R) и
    /// отверстие/бобышка (ось Z, ρ). convex: кромка отверстия (материал — внутри вала и снаружи отверстия); иначе — стык
    /// бобышки (пустота — снаружи обоих). Объём «уголка» между ребром и полосой — по дивергенции, квадратура Гаусса.
    /// </summary>
    static double RollingBallCusp(double R, double rho, double r, bool convex, int nPhi = 400, int nL = 24)
    {
        double sR = convex ? R - r : R + r, sRho = rho + r;           // хребет: |(y,z)| = sR, |(x,y)| = sRho
        Vector3d Spine(double phi) { double x = sRho * Math.Cos(phi), y = sRho * Math.Sin(phi); return new(x, y, Math.Sqrt(sR * sR - y * y)); }
        Vector3d C1(double phi) { var s = Spine(phi); var yz = new Vector2d(s.Y, s.Z); var k = R / yz.Length; return new(s.X, s.Y * k, s.Z * k); }   // на валу
        Vector3d C2(double phi) { var s = Spine(phi); return new(rho * Math.Cos(phi), rho * Math.Sin(phi), s.Z); }                                  // на отверстии/бобышке
        Vector3d E(double phi) { double x = rho * Math.Cos(phi), y = rho * Math.Sin(phi); return new(x, y, Math.Sqrt(R * R - y * y)); }            // исходное ребро
        // узлы Гаусса–Лежандра на [0,1]
        double[] gx = { -0.9739065285171717, -0.8650633666889845, -0.6794095682990244, -0.4333953941292472, -0.1488743389816312, 0.1488743389816312, 0.4333953941292472, 0.6794095682990244, 0.8650633666889845, 0.9739065285171717 };
        double[] gw = { 0.0666713443086881, 0.1494513491505806, 0.2190863625159820, 0.2692667193099963, 0.2955242247147529, 0.2955242247147529, 0.2692667193099963, 0.2190863625159820, 0.1494513491505806, 0.0666713443086881 };
        double total = 0;
        // поверхность (φ, λ) → p; нормаль наружу из области задаётся функцией out(p, φ)
        void Patch(Func<double, double, Vector3d> P, Func<Vector3d, double, Vector3d> outDir)
        {
            double h = 1e-6;
            for (int i = 0; i < nPhi; i++)
                for (int a = 0; a < gx.Length; a++)
                {
                    double phi = 2 * Math.PI * (i + (gx[a] + 1) / 2) / nPhi, wphi = gw[a] / 2 * 2 * Math.PI / nPhi;
                    for (int j = 0; j < nL; j++)
                        for (int b = 0; b < gx.Length; b++)
                        {
                            double lam = (j + (gx[b] + 1) / 2) / nL, wl = gw[b] / 2 / nL;
                            var p = P(phi, lam);
                            var du = (P(phi + h, lam) - P(phi - h, lam)) / (2 * h);
                            var dv = (P(phi, Math.Min(1, lam + h)) - P(phi, Math.Max(0, lam - h))) / (Math.Min(1, lam + h) - Math.Max(0, lam - h));
                            var n = Vector3d.Cross(du, dv);
                            if (Vector3d.Dot(n, outDir(p, phi)) < 0) n = -n;
                            total += Vector3d.Dot(p, n) / 3 * wphi * wl;
                        }
                }
        }
        // полоса шара: дуга от C1 к C2 вокруг хребта (короткая), наружу из уголка — к центру шара
        Patch((phi, lam) =>
        {
            var s = Spine(phi); var a = (C1(phi) - s).Normalized(); var b = (C2(phi) - s).Normalized();
            double ang = Math.Acos(Math.Clamp(Vector3d.Dot(a, b), -1, 1)); var bp = (b - a * Vector3d.Dot(a, b)).Normalized();
            return s + (a * Math.Cos(ang * lam) + bp * Math.Sin(ang * lam)) * r;
        }, (p, phi) => Spine(phi) - p);
        // полоса на валу: от ребра E к C1 — по карте вала (угол вокруг X, x)
        Patch((phi, lam) =>
        {
            var e = E(phi); var c = C1(phi);
            double te = Math.Atan2(e.Z, e.Y), tc = Math.Atan2(c.Z, c.Y), t = te + (tc - te) * lam, x = e.X + (c.X - e.X) * lam;
            return new Vector3d(x, R * Math.Cos(t), R * Math.Sin(t));
        }, (p, phi) => convex ? new Vector3d(0, p.Y, p.Z) : new Vector3d(0, -p.Y, -p.Z));
        // полоса на отверстии/бобышке: от ребра E к C2 — по образующей
        Patch((phi, lam) => { var e = E(phi); var c = C2(phi); return e + (c - e) * lam; },
              (p, phi) => new Vector3d(-p.X, -p.Y, 0));
        return Math.Abs(total);
    }

    static void RollingBallProbe()
    {
        {
            var pr = new List<Vector2d> { new(0, 0), new(20, 0), new(12, 20), new(0, 20) };
            Console.WriteLine($"pappus frustum {PappusCorner(MCurve.Line(0, 20, 12, 20), MCurve.Line(20, 0, 12, 20), new(12, 20), 1, true, pr):R} (python 6.127600864041801)");
            double zc = 20 - Math.Sqrt(225 - 100);
            var ps = new List<Vector2d> { new(0, 0), new(10, 0), new(10, 20) };
            for (int i = 1; i < 200; i++) { double a = Math.Atan2(20 - zc, 10) + (Math.PI / 2 - Math.Atan2(20 - zc, 10)) * i / 199.0; ps.Add(new(15 * Math.Cos(a), zc + 15 * Math.Sin(a))); }
            Console.WriteLine($"pappus cyl/sphere {PappusCorner(MCurve.Line(10, 0, 10, 20), MCurve.Circle(0, zc, 15), new(10, 20), 1.5, true, ps):R} (python 4.194436238699856)");
        }
        Console.WriteLine($"convex rim R20 ρ4 r1: cusp {RollingBallCusp(20, 4, 1, true):R} (OpenCascade removed 7.7480)");
        Console.WriteLine($"concave boss R20 ρ8 r3: cusp {RollingBallCusp(20, 8, 3, false):R} (OpenCascade added 62.9369)");
        foreach (var (R, rho, r, cv) in new[] { (20.0, 4.0, 2.0, true), (20.0, 4.0, 3.0, true), (20.0, 15.0, 1.0, true), (20.0, 8.0, 1.0, false), (20.0, 8.0, 5.0, false) })
            Console.WriteLine($"   R{R} rho{rho} r{r} {(cv ? "convex" : "concave")}: {RollingBallCusp(R, rho, r, cv):R}");
        foreach (int n in new[] { 100, 200, 800 }) Console.WriteLine($"   convergence nPhi {n}: {RollingBallCusp(20, 4, 1, true, n):R}");
    }
}
