using OpenTK.Mathematics;
using Parametric3D.Kernel;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Ops;
using Parametric3D.Kernel.Topology;

static partial class Program
{
    /// <summary>
    /// НЕЗАВИСИМЫЙ ЭТАЛОН: объём бруска W×D×H со скруглёнными вертикальными рёбрами R после скругления верхнего контура
    /// катящимся шаром радиуса r(F) (F — доля длины контура от точки s0 в сторону dir). Горизонтальные сечения: на высоте z
    /// граница — по каждой точке контура s либо стенка (z ≤ H − r(s)), либо точка характеристической окружности шара на
    /// высоте z (решение A cos t + B sin t = c на дуге от Q₁ к Q₂), на стыках с разной окружностью — дуга сечения шара стыка
    /// плоскостью z. Площадь — многоугольник с уточнением Ричардсона (узлы — стыки, переходы стенка/лента, узлы закона),
    /// объём — Гаусс по z с заменой, гасящей корневые особенности на краях. Ничего из ядра, кроме закона радиуса.
    /// </summary>
    static (double V, double Spread) LawLoopVolumeRef(double W, double D, double H, double R, Func<double, double> rF, Func<double, double> dF, double[] lawPts, double sStart, double dir, double sgm = -1, double ox = 0, double oy = 0)
    {
        // контур CCW от (R, 0): 8 звеньев
        var pcs = new List<(bool Arc, Vector2d A, Vector2d B, Vector2d O, double a0, double len)>();
        double Lx = W - 2 * R, Ly = D - 2 * R, qa = Math.PI / 2 * R;
        var o2 = new Vector2d(ox, oy);
        pcs.Add((false, o2 + new Vector2d(R, 0), o2 + new Vector2d(W - R, 0), default, 0, Lx));
        pcs.Add((true, default, default, o2 + new Vector2d(W - R, R), -Math.PI / 2, qa));
        pcs.Add((false, o2 + new Vector2d(W, R), o2 + new Vector2d(W, D - R), default, 0, Ly));
        pcs.Add((true, default, default, o2 + new Vector2d(W - R, D - R), 0, qa));
        pcs.Add((false, o2 + new Vector2d(W - R, D), o2 + new Vector2d(R, D), default, 0, Lx));
        pcs.Add((true, default, default, o2 + new Vector2d(R, D - R), Math.PI / 2, qa));
        pcs.Add((false, o2 + new Vector2d(0, D - R), o2 + new Vector2d(0, R), default, 0, Ly));
        pcs.Add((true, default, default, o2 + new Vector2d(R, R), Math.PI, qa));
        var cum = new double[pcs.Count + 1]; for (int i = 0; i < pcs.Count; i++) cum[i + 1] = cum[i] + pcs[i].len;
        double L = cum[^1];
        (Vector2d P, Vector2d T, double kap) Geo(int k, double sg)
        {
            var p = pcs[k];
            if (!p.Arc) { var t = (p.B - p.A).Normalized(); return (p.A + t * sg, t, 0); }
            double a = p.a0 + sg / R; return (p.O + new Vector2d(Math.Cos(a), Math.Sin(a)) * R, new Vector2d(-Math.Sin(a), Math.Cos(a)), 1 / R);
        }
        double Fk(int k, double sg) { double f = dir * (cum[k] + sg - sStart) / L, fm = dir * (cum[k] + pcs[k].len / 2 - sStart) / L; return f - Math.Floor(fm); }
        double Rk(int k, double sg) => rF(Fk(k, sg));
        double dRk(int k, double sg) => dF(Fk(k, sg)) * dir / L;
        // σ = −1: выпуклый верхний контур (центр шара в материале, лента ниже H); σ = +1: вогнутый контур у основания
        // бобышки (центр шара в пустоте, лента выше H)
        var np = Vector3d.UnitZ;
        // сечение звена k в точке sg: центр окружности, радиус, базис от Q₁ к Q₂, угол; шар (центр, радиус)
        (Vector3d Cc, double rho, Vector3d e1, Vector3d e2, double phi, Vector3d C, double r) Sec(int k, double sg)
        {
            var (P2, T2, kap) = Geo(k, sg); double r = Rk(k, sg), rp = dRk(k, sg);
            var P = new Vector3d(P2.X, P2.Y, H); var T = new Vector3d(T2.X, T2.Y, 0); var n2 = new Vector3d(T2.Y, -T2.X, 0);
            var C = P + (np + n2) * (sgm * r);
            var dC = T * (1 + sgm * r * kap) + (np + n2) * (sgm * rp);
            double dl = dC.Length, del = -r * rp / dl; var Cc = C + dC * (del / dl); double rho = Math.Sqrt(r * r - del * del);
            var q1 = P + n2 * (sgm * r); var q2 = P + np * (sgm * r);
            var e1 = (q1 - Cc) / rho; var w = q2 - Cc; var e2 = (w - e1 * Vector3d.Dot(w, e1)).Normalized();
            return (Cc, rho, e1, e2, Math.Atan2(Vector3d.Dot(w, e2), Vector3d.Dot(w, e1)), C, r);
        }
        Vector2d Bnd(int k, double sg, double z)
        {
            var (P2, _, _) = Geo(k, sg);
            if (sgm < 0 ? z <= H - Rk(k, sg) : z >= H + Rk(k, sg)) return P2;
            var s = Sec(k, sg);
            double A = s.rho * s.e1.Z, B = s.rho * s.e2.Z, c = z - s.Cc.Z, m = Math.Sqrt(A * A + B * B);
            double base0 = Math.Atan2(B, A), ac = Math.Acos(Math.Clamp(c / m, -1, 1));
            double best = double.NaN, bd = double.MaxValue;
            foreach (double t0 in new[] { base0 + ac, base0 - ac })
                for (int w = -2; w <= 2; w++)
                {
                    double t = t0 + 2 * Math.PI * w; double d = t < 0 ? -t : t > s.phi ? t - s.phi : 0;
                    if (d < bd) { bd = d; best = t; }
                }
            double tt = Math.Clamp(best, 0, s.phi);
            var X = s.Cc + (s.e1 * Math.Cos(tt) + s.e2 * Math.Sin(tt)) * s.rho;
            return new Vector2d(X.X, X.Y);
        }
        double rMin = lawPts.Min(), rMax = lawPts.Max();
        // площадь сечения z (Ричардсон по N и 2N)
        // стык j (между звеном j−1 и j) на высоте z: перекрытие — точки встречи срезов лент (обрезки tEnd звена j−1 и
        // tStart звена j), щель — дуга сечения шара стыка (поправка к хорде)
        (double TEnd, double TStart, double Corr) JointAt(int k, double z)
        {
            int kp = (k - 1 + pcs.Count) % pcs.Count; double lp = pcs[kp].len;
            var xa = Bnd(kp, lp, z); var xb = Bnd(k, 0, z);
            if ((xa - xb).Length <= 1e-14) return (0, 0, 0);
            var sa = Sec(kp, lp); var sb = Sec(k, 0);
            var ma = sa.Cc + (sa.e1 * Math.Cos(sa.phi / 2) + sa.e2 * Math.Sin(sa.phi / 2)) * sa.rho;
            var mb = sb.Cc + (sb.e1 * Math.Cos(sb.phi / 2) + sb.e2 * Math.Sin(sb.phi / 2)) * sb.rho;
            var (_, T2, _) = Geo(k, 0);
            bool ahead = Vector3d.Dot(mb - ma, new Vector3d(T2.X, T2.Y, 0)) > 0;
            if (ahead)
            {
                double rz = Math.Sqrt(Math.Max(0, sb.r * sb.r - (z - sb.C.Z) * (z - sb.C.Z)));
                var cxy = new Vector2d(sb.C.X, sb.C.Y);
                double al = Math.Atan2(xa.Y - cxy.Y, xa.X - cxy.X), be = Math.Atan2(xb.Y - cxy.Y, xb.X - cxy.X);
                double dth = be - al; while (dth > Math.PI) dth -= 2 * Math.PI; while (dth < -Math.PI) dth += 2 * Math.PI;
                double arcI = rz * rz * dth + rz * (cxy.X * (Math.Sin(al + dth) - Math.Sin(al)) - cxy.Y * (Math.Cos(al + dth) - Math.Cos(al)));
                return (0, 0, arcI - (xa.X * xb.Y - xb.X * xa.Y));
            }
            // перекрытие: срез звена k (t2 ≥ 0 от начала) и звена kp (t1 ≥ 0 от конца) — встреча
            double win = 0.45 * Math.Min(Math.Min(lp, pcs[k].len), sb.r);
            double Dist2(Vector2d x, out double tb)
            {
                double lo = 0, hi = win; const double g = 0.6180339887498949;
                double F(double t) => (Bnd(k, t, z) - x).LengthSquared;
                double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = F(x1), f2 = F(x2);
                for (int it = 0; it < 90; it++)
                {
                    if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = F(x1); }
                    else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = F(x2); }
                }
                tb = (lo + hi) / 2; return F(tb);
            }
            // встреча — наименьшее расстояние между срезами (касательная встреча у стенки обусловлена плохо, площадь от
            // выбора точки там не зависит: кривые совпадают до 1e-10)
            {
                double lo = 0, hi = win; const double g = 0.6180339887498949;
                double F(double t) => Dist2(Bnd(kp, lp - t, z), out _);
                double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = F(x1), f2 = F(x2);
                for (int it = 0; it < 90; it++)
                {
                    if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = F(x1); }
                    else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = F(x2); }
                }
                double t1f = (lo + hi) / 2; double d2 = Dist2(Bnd(kp, lp - t1f, z), out double t2f);
                if (Environment.GetEnvironmentVariable("P3D_REFDBG") == "1") Console.WriteLine($"   ref joint {k} z {z:F4}: t1 {t1f:E3} t2 {t2f:E3} d {Math.Sqrt(d2):E2}");
                if (Math.Sqrt(d2) > 1e-9) return (0, 0, double.NaN);
                return (t1f, t2f, 0);
            }
        }
        double AreaN(double z, int N)
        {
            double tot = 0; Vector2d? first = null, prev = null;
            void Add(Vector2d q) { if (prev is { } p) tot += p.X * q.Y - q.X * p.Y; else first = q; prev = q; }
            var J = Enumerable.Range(0, pcs.Count).Select(k => JointAt(k, z)).ToArray();
            if (J.Any(j => double.IsNaN(j.Corr))) return double.NaN;
            for (int k = 0; k < pcs.Count; k++)
            {
                double len = pcs[k].len, sA = J[k].TStart, sB = len - J[(k + 1) % pcs.Count].TEnd;
                _luneCorr += J[k].Corr;
                // узлы звена: концы (с обрезкой), переходы z = H − r(s), узлы закона
                var nodes = new List<double> { sA, sB };
                const int M = 400;
                for (int i = 0; i < M; i++)
                {
                    double a = len * i / M, b = len * (i + 1) / M;
                    double fa = Rk(k, a) - Math.Abs(H - z), fb = Rk(k, b) - Math.Abs(H - z);
                    if (fa == 0) nodes.Add(a);
                    else if (fa * fb < 0) { for (int it = 0; it < 80; it++) { double mm = (a + b) / 2, fm = Rk(k, mm) - Math.Abs(H - z); if (fa * fm <= 0) b = mm; else { a = mm; fa = fm; } } nodes.Add((a + b) / 2); }
                }
                foreach (var kf in _lawKnots)
                    for (int w = -1; w <= 1; w++) nodes.Add(sStart + dir * (kf + w) * L - cum[k]);
                nodes = nodes.Where(x => x >= sA && x <= sB).Distinct().OrderBy(x => x).ToList();
                for (int j = 0; j + 1 < nodes.Count; j++)
                {
                    double a = nodes[j], b = nodes[j + 1];
                    if (b - a < 1e-13) continue;
                    for (int i = 0; i < N; i++) Add(Bnd(k, a + (b - a) * i / N, z));
                }
                Add(Bnd(k, sB, z));
            }
            if (prev is { } pl && first is { } f0) tot += pl.X * f0.Y - f0.X * pl.Y;
            return tot;
        }
        double Area(double z, int N)
        {
            _luneCorr = 0; double a1 = AreaN(z, N);
            _luneCorr = 0; double a2 = AreaN(z, 2 * N); double c = _luneCorr;
            // хорды внутри звеньев — O(h²): Ричардсон; хорды стыков одинаковы в обоих и заменены точной дугой (c)
            return 0.5 * ((4 * a2 - a1) / 3 + c);
        }
        double A0 = W * D - (4 - Math.PI) * R * R;
        // разбиение по z: H − r в точках закона
        var zs = lawPts.Select(r => H + sgm * r).Append(H).Append(H + sgm * rMax).Distinct().OrderBy(x => x).ToList();
        double[] gx = { -0.9931285991850949, -0.9639719272779138, -0.9122344282513259, -0.8391169718222188, -0.7463319064601508, -0.6360536807265150, -0.5108670019508271, -0.3737060887154195, -0.2277858511416451, -0.0765265211334973, 0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271, 0.6360536807265150, 0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949 };
        double[] gw = { 0.0176140071391521, 0.0406014298003869, 0.0626720483341091, 0.0832767415767048, 0.1019301198172404, 0.1181945319615184, 0.1316886384491766, 0.1420961093183820, 0.1491729864726037, 0.1527533871307258, 0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766, 0.1181945319615184, 0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521 };
        double Vrem(int N)
        {
            double v = 0;
            for (int i = 0; i + 1 < zs.Count; i++)
            {
                double a = zs[i], b = zs[i + 1];
                const int Pc = 3;
                for (int pc = 0; pc < Pc; pc++)
                {
                    double ta = (double)pc / Pc, tb = (double)(pc + 1) / Pc;
                    for (int q = 0; q < 20; q++)
                    {
                        double tau = (ta + tb) / 2 + (tb - ta) / 2 * gx[q];
                        double sm = 3 * tau * tau - 2 * tau * tau * tau, dsm = 6 * tau - 6 * tau * tau;
                        double z = a + (b - a) * sm;
                        v += gw[q] * (tb - ta) / 2 * (b - a) * dsm * (Area(z, N) - A0);
                    }
                }
            }
            return v;
        }
        double v1 = Vrem(96), v2 = Vrem(192);
        return (v2, Math.Abs(v2 - v1));          // ∫ (A(z) − A0) dz: у выпуклого — минус снятый объём, у вогнутого — добавленный
    }
    /// <summary>
    /// НЕЗАВИСИМЫЙ ЭТАЛОН ОТКРЫТОЙ ЦЕПОЧКИ: брусок W×D×H, скруглены два правых вертикальных ребра (R); выбран верхний контур от
    /// левой стенки x = 0 по переду, правым углам и заду обратно к x = 0 (концы — на плоскости левой стенки). Закон r(F), F —
    /// доля длины цепочки от (0, 0). Сечение z: многоугольник по срезам лент (продлённых за концы, до встречи с x = 0), стыки —
    /// как у замкнутого контура, замыкание — по прямой x = 0. Возвращает ∫(A(z) − A0) dz.
    /// </summary>
    static (double DV, double Spread) OpenChainVolumeRef(double W, double D, double H, double R, Func<double, double> rF, Func<double, double> dF, double[] lawPts, double[] knots, bool mitreCase = false)
    {
        var pcs = new List<(bool Arc, Vector2d A, Vector2d B, Vector2d O, double a0, double len)>();
        double Lx = W - R, Ly = D - 2 * R, qa = Math.PI / 2 * R;
        Func<Vector2d, double> endPlane = q => q.X; var closing = new List<Vector2d>();
        if (mitreCase)
        {
            // брусок без скруглений: перёд (0, 0) → (W, 0) и правое ребро (W, 0) → (W, D); стык «на ус» в (W, 0); концы — на
            // плоскостях x = 0 и y = D; замыкание — через угол (0, D)
            pcs.Add((false, new(0, 0), new(W, 0), default, 0, W));
            pcs.Add((false, new(W, 0), new(W, D), default, 0, D));
            endPlane = q => q.Y - D; closing.Add(new Vector2d(0, D));
        }
        else
        {
        pcs.Add((false, new(0, 0), new(W - R, 0), default, 0, Lx));
        pcs.Add((true, default, default, new(W - R, R), -Math.PI / 2, qa));
        pcs.Add((false, new(W, R), new(W, D - R), default, 0, Ly));
        pcs.Add((true, default, default, new(W - R, D - R), 0, qa));
        pcs.Add((false, new(W - R, D), new(0, D), default, 0, Lx));
        }
        int NP = pcs.Count;
        var cum = new double[NP + 1]; for (int i = 0; i < NP; i++) cum[i + 1] = cum[i] + pcs[i].len;
        double L = cum[^1];
        (Vector2d P, Vector2d T, double kap) Geo(int k, double sg)
        {
            var p = pcs[k];
            if (!p.Arc) { var t = (p.B - p.A).Normalized(); return (p.A + t * sg, t, 0); }
            double a = p.a0 + sg / R; return (p.O + new Vector2d(Math.Cos(a), Math.Sin(a)) * R, new Vector2d(-Math.Sin(a), Math.Cos(a)), 1 / R);
        }
        double Rk(int k, double sg) => rF((cum[k] + sg) / L);
        double dRk(int k, double sg) => dF((cum[k] + sg) / L) / L;
        const double sgm = -1; var np = Vector3d.UnitZ;
        (Vector3d Cc, double rho, Vector3d e1, Vector3d e2, double phi, Vector3d C, double r) Sec(int k, double sg)
        {
            var (P2, T2, kap) = Geo(k, sg); double r = Rk(k, sg), rp = dRk(k, sg);
            var P = new Vector3d(P2.X, P2.Y, H); var T = new Vector3d(T2.X, T2.Y, 0); var n2 = new Vector3d(T2.Y, -T2.X, 0);
            var C = P + (np + n2) * (sgm * r);
            var dC = T * (1 + sgm * r * kap) + (np + n2) * (sgm * rp);
            double dl = dC.Length, del = -r * rp / dl; var Cc = C + dC * (del / dl); double rho = Math.Sqrt(r * r - del * del);
            var q1 = P + n2 * (sgm * r); var q2 = P + np * (sgm * r);
            var e1 = (q1 - Cc) / rho; var w = q2 - Cc; var e2 = (w - e1 * Vector3d.Dot(w, e1)).Normalized();
            return (Cc, rho, e1, e2, Math.Atan2(Vector3d.Dot(w, e2), Vector3d.Dot(w, e1)), C, r);
        }
        Vector2d Bnd(int k, double sg, double z)
        {
            var (P2, _, _) = Geo(k, sg);
            if (z <= H - Rk(k, sg)) return P2;
            var s = Sec(k, sg);
            double A = s.rho * s.e1.Z, B = s.rho * s.e2.Z, c = z - s.Cc.Z, m = Math.Sqrt(A * A + B * B);
            double base0 = Math.Atan2(B, A), ac = Math.Acos(Math.Clamp(c / m, -1, 1));
            double best = double.NaN, bd = double.MaxValue;
            foreach (double t0 in new[] { base0 + ac, base0 - ac })
                for (int w = -2; w <= 2; w++) { double t = t0 + 2 * Math.PI * w; double d = t < 0 ? -t : t > s.phi ? t - s.phi : 0; if (d < bd) { bd = d; best = t; } }
            double tt = Math.Clamp(best, 0, s.phi);
            var X = s.Cc + (s.e1 * Math.Cos(tt) + s.e2 * Math.Sin(tt)) * s.rho;
            return new Vector2d(X.X, X.Y);
        }
        // стык k (между k−1 и k): как у замкнутого
        (double TEnd, double TStart, double Corr) JointAt(int k, double z)
        {
            int kp = k - 1; double lp = pcs[kp].len;
            var xa = Bnd(kp, lp, z); var xb = Bnd(k, 0, z);
            if ((xa - xb).Length <= 1e-14) return (0, 0, 0);
            var sa = Sec(kp, lp); var sb = Sec(k, 0);
            var ma = sa.Cc + (sa.e1 * Math.Cos(sa.phi / 2) + sa.e2 * Math.Sin(sa.phi / 2)) * sa.rho;
            var mb = sb.Cc + (sb.e1 * Math.Cos(sb.phi / 2) + sb.e2 * Math.Sin(sb.phi / 2)) * sb.rho;
            var (_, T2, _) = Geo(k, 0); var (_, T1, _) = Geo(kp, lp);
            bool sharp = Vector2d.Dot(T1, T2) < 1 - 1e-9;
            if (!sharp && Vector3d.Dot(mb - ma, new Vector3d(T2.X, T2.Y, 0)) > 0)
            {
                double rz = Math.Sqrt(Math.Max(0, sb.r * sb.r - (z - sb.C.Z) * (z - sb.C.Z))); var cxy = new Vector2d(sb.C.X, sb.C.Y);
                double al = Math.Atan2(xa.Y - cxy.Y, xa.X - cxy.X), be = Math.Atan2(xb.Y - cxy.Y, xb.X - cxy.X);
                double dth = be - al; while (dth > Math.PI) dth -= 2 * Math.PI; while (dth < -Math.PI) dth += 2 * Math.PI;
                double arcI = rz * rz * dth + rz * (cxy.X * (Math.Sin(al + dth) - Math.Sin(al)) - cxy.Y * (Math.Cos(al + dth) - Math.Cos(al)));
                return (0, 0, arcI - (xa.X * xb.Y - xb.X * xa.Y));
            }
            double win = sharp ? 2 * Math.Max(sa.r, sb.r) : 0.45 * Math.Min(Math.Min(lp, pcs[k].len), sb.r);
            double Dist2(Vector2d x, out double tb)
            {
                double lo = sharp ? -win : 0, hi = win; const double g = 0.6180339887498949; double F(double t) => (Bnd(k, t, z) - x).LengthSquared;
                double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = F(x1), f2 = F(x2);
                for (int it = 0; it < 90; it++) { if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = F(x1); } else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = F(x2); } }
                tb = (lo + hi) / 2; return F(tb);
            }
            {
                double lo = 0, hi = win; const double g = 0.6180339887498949; double F(double t) => Dist2(Bnd(kp, lp - t, z), out _);
                double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = F(x1), f2 = F(x2);
                for (int it = 0; it < 90; it++) { if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = F(x1); } else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = F(x2); } }
                double t1f = (lo + hi) / 2; double d2 = Dist2(Bnd(kp, lp - t1f, z), out double t2f);
                return Math.Sqrt(d2) > 1e-9 ? (0, 0, double.NaN) : (t1f, t2f, 0);
            }
        }
        // встреча среза концевой ленты с x = 0: корень по s на продлении
        double CrossX0(int k, double z, bool atStart)
        {
            Func<Vector2d, double> pl = atStart ? (q => q.X) : endPlane;
            double ext = 4 * Math.Max(Rk(k, atStart ? 0 : pcs[k].len), 1) + 1;
            double a = atStart ? -ext : pcs[k].len - ext, b = atStart ? ext : pcs[k].len + ext;
            double fa = pl(Bnd(k, a, z));
            for (int it = 0; it < 200; it++) { double m = (a + b) / 2, fm = pl(Bnd(k, m, z)); if (fa * fm <= 0) b = m; else { a = m; fa = fm; } }
            return (a + b) / 2;
        }
        double AreaN(double z, int N, out double corr)
        {
            corr = 0; double tot = 0; Vector2d? first = null, prev = null;
            void Add(Vector2d q) { if (prev is { } p) tot += p.X * q.Y - q.X * p.Y; else first = q; prev = q; }
            var J = new (double TEnd, double TStart, double Corr)[NP + 1];
            for (int k = 1; k < NP; k++) J[k] = JointAt(k, z);
            if (J.Any(j => double.IsNaN(j.Corr))) return double.NaN;
            double sA0 = CrossX0(0, z, true), sBn = CrossX0(NP - 1, z, false);
            for (int k = 0; k < NP; k++)
            {
                double len = pcs[k].len, sA = k == 0 ? sA0 : J[k].TStart, sB = k == NP - 1 ? sBn : len - J[k + 1].TEnd;
                if (k > 0) corr += J[k].Corr;
                var nodes = new List<double> { sA, sB };
                const int M = 400;
                for (int i = 0; i < M; i++)
                {
                    double a = sA + (sB - sA) * i / M, b = sA + (sB - sA) * (i + 1) / M;
                    double fa = Rk(k, a) - (H - z), fb = Rk(k, b) - (H - z);
                    if (fa == 0) nodes.Add(a);
                    else if (fa * fb < 0) { for (int it = 0; it < 80; it++) { double mm = (a + b) / 2, fm = Rk(k, mm) - (H - z); if (fa * fm <= 0) b = mm; else { a = mm; fa = fm; } } nodes.Add((a + b) / 2); }
                }
                foreach (var kf in knots) nodes.Add(kf * L - cum[k]);
                if (k == 0 || k == NP - 1) { nodes.Add(0); nodes.Add(len); }
                nodes = nodes.Where(x => x >= sA && x <= sB).Distinct().OrderBy(x => x).ToList();
                for (int j = 0; j + 1 < nodes.Count; j++)
                {
                    double a = nodes[j], b = nodes[j + 1]; if (b - a < 1e-13) continue;
                    for (int i = 0; i < N; i++) Add(Bnd(k, a + (b - a) * i / N, z));
                }
                Add(Bnd(k, sB, z));
            }
            foreach (var cpt in closing) Add(cpt);
            if (prev is { } pl && first is { } f0) tot += pl.X * f0.Y - f0.X * pl.Y;     // замыкание по x = 0
            return tot;
        }
        double Area(double z, int N) { double a1 = AreaN(z, N, out _); double a2 = AreaN(z, 2 * N, out double c); return 0.5 * ((4 * a2 - a1) / 3 + c); }
        double A0 = mitreCase ? W * D : W * D - 2 * (1 - Math.PI / 4) * R * R;
        double rMax = lawPts.Max();
        var zs = lawPts.Select(r => H - r).Append(H).Append(H - rMax).Distinct().OrderBy(x => x).ToList();
        double[] gx = { -0.9931285991850949, -0.9639719272779138, -0.9122344282513259, -0.8391169718222188, -0.7463319064601508, -0.6360536807265150, -0.5108670019508271, -0.3737060887154195, -0.2277858511416451, -0.0765265211334973, 0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271, 0.6360536807265150, 0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949 };
        double[] gw = { 0.0176140071391521, 0.0406014298003869, 0.0626720483341091, 0.0832767415767048, 0.1019301198172404, 0.1181945319615184, 0.1316886384491766, 0.1420961093183820, 0.1491729864726037, 0.1527533871307258, 0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766, 0.1181945319615184, 0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521 };
        double Vrem(int N)
        {
            double v = 0;
            for (int i = 0; i + 1 < zs.Count; i++)
            {
                double a = zs[i], b = zs[i + 1];
                for (int pc = 0; pc < 3; pc++)
                {
                    double ta = pc / 3.0, tb = (pc + 1) / 3.0;
                    for (int q = 0; q < 20; q++)
                    {
                        double tau = (ta + tb) / 2 + (tb - ta) / 2 * gx[q], sm = 3 * tau * tau - 2 * tau * tau * tau, dsm = 6 * tau - 6 * tau * tau;
                        v += gw[q] * (tb - ta) / 2 * (b - a) * dsm * (Area(a + (b - a) * sm, N) - A0);
                    }
                }
            }
            return v;
        }
        double v1 = Vrem(96), v2 = Vrem(192);
        return (v2, Math.Abs(v2 - v1));
    }

    [ThreadStatic] static double _luneCorr;
    [ThreadStatic] static double[] _lawKnots = Array.Empty<double>();

    static void Krn06LawLoopChecks()
    {
        Check("KRN06-RADIUS-LAW-TANGENT-LOOP", () =>
        {
            // 06.2, 06.14: ЗАКОН РАДИУСА НА ЦЕПОЧКЕ — касательный верхний контур бруска 60×40×20 со скруглёнными вертикальными
            // рёбрами R8 (4 отрезка, 4 дуги), радиус по точкам доли длины контура от начала переднего ребра. Эталон —
            // независимые горизонтальные сечения (LawLoopVolumeRef). Постоянный закон — тот же объём, что у постоянного
            // радиуса (касательный контур). Стыки отрезок/дуга при r′ ≠ 0 — «луночки» шара стыка.
            var fails = new List<string>();
            const double W = 60, D = 40, H = 20, R = 8;
            TSolid Body()
            {
                var bl = new List<TSolid> { Box(0, 0, 0, W, D, H) };
                FilletOp.RoundEdges(bl, bl[0].Edges.Where(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), R, new List<string>());
                return bl[0];
            }
            List<EdgeRef> TopLoop(TSolid b)
            {
                var es = b.Edges.Where(e => Math.Abs(e.V0.P.Z - H) < 1e-7 && Math.Abs(e.V1.P.Z - H) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - H) < 1e-7).ToList();
                // первым — передний отрезок y = 0, от (R, 0) к (W − R, 0)
                var front = es.First(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Y) < 1e-7 && Math.Abs(e.V1.P.Y) < 1e-7);
                var refs = new List<EdgeRef> { (front.V0.P.X < front.V1.P.X) ? EdgeRef.Of(front) : new EdgeRef(front.V1.P, front.V0.P, front.Curve.PointAt(0.5), front.FaceA?.Tag, front.FaceB?.Tag) };
                refs.AddRange(es.Where(e => e != front).Select(EdgeRef.Of));
                return refs;
            }
            var cases = new (string Name, (double F, double R)[] Pts)[]
            {
                ("constant 3 (law = constant radius)", new[] { (0.0, 3.0), (1.0, 3.0) }),
                ("bump 3 → 4 → 3 inside the front line (r′ = 0 at joints)", new[] { (0.0, 3.0), (0.05, 3.0), (0.12, 4.0), (0.19, 3.0), (1.0, 3.0) }),
                ("bump 3 → 3.6 → 3 inside a corner arc", new[] { (0.0, 3.0), (0.24, 3.0), (0.27, 3.6), (0.30, 3.0), (1.0, 3.0) }),
                ("rise across a line → arc joint (lune)", new[] { (0.0, 3.0), (0.2, 3.0), (0.27, 3.5), (0.55, 3.5), (0.65, 3.0), (1.0, 3.0) }),
                ("fall across a line → arc joint (crease)", new[] { (0.0, 3.0), (0.1, 3.0), (0.15, 3.5), (0.2, 3.5), (0.27, 3.0), (1.0, 3.0) }),
                ("fall across an arc → line joint", new[] { (0.0, 3.0), (0.2, 3.0), (0.25, 3.5), (0.28, 3.5), (0.33, 3.0), (1.0, 3.0) }),
                ("rise across an arc → line joint", new[] { (0.0, 3.0), (0.27, 3.0), (0.33, 3.5), (0.45, 3.5), (0.48, 3.0), (1.0, 3.0) }),
                ("kink of r′ at the loop start", new[] { (0.0, 3.0), (0.1, 3.5), (0.9, 3.5), (1.0, 3.0) }),
                ("fall across a whole arc (creases at both ends)", new[] { (0.0, 3.0), (0.38, 3.0), (0.41, 3.6), (0.52, 3.0), (1.0, 3.0) }),
                ("rise across a whole arc", new[] { (0.0, 3.0), (0.38, 3.0), (0.41, 2.6), (0.52, 3.0), (1.0, 3.0) }),
                ("ramp 3 → 3.5 across the line/arc joint", new[] { (0.0, 3.0), (0.2, 3.0), (0.27, 3.5), (0.35, 3.5), (1.0, 3.0) }),
                ("2 → 4 → 2.5 → 2", new[] { (0.0, 2.0), (0.3, 4.0), (0.65, 2.5), (1.0, 2.0) }),
                ("3 → 1.5 → 3.5 → 3 (knots inside arcs)", new[] { (0.0, 3.0), (0.2, 1.5), (0.55, 3.5), (1.0, 3.0) }),
            };
            if (Environment.GetEnvironmentVariable("P3D_LAWPTS") is { } lp)
                cases = new[] { ("custom", lp.Split(';').Select(t => t.Split(',')).Select(t => (double.Parse(t[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture))).ToArray()) };
            double vConst = double.NaN;
            {
                var bl = new List<TSolid> { Body() }; FilletOp.RoundEdges(bl, TopLoop(bl[0]), 3, new List<string>());
                vConst = AnalyticVolume.Of(bl[0]) ?? double.NaN;
            }
            foreach (var (name, pts) in cases)
            {
                if (Environment.GetEnvironmentVariable("P3D_LAWCASE") is { } lc && !name.Contains(lc)) continue;
                var b0 = Body(); var bl = new List<TSolid> { b0 }; var w = new List<string>();
                long c0 = DirectRound.LawLoops;
                if (Environment.GetEnvironmentVariable("P3D_LAWDBG") == "1") DirectRound.Debug = m => { if (m.Contains("law")) Console.WriteLine("   dr: " + m); };
                bool prevMode = Environment.GetEnvironmentVariable("P3D_LAWPREVIEW") == "1";
                try
                {
                    if (prevMode) { using (new Parametric3D.Kernel.KernelContext(Parametric3D.Kernel.KernelSettings.Capture() with { Preview = true }).Enter()) FilletOp.RoundEdgesLaw(bl, TopLoop(b0), pts, w); }
                    else FilletOp.RoundEdgesLaw(bl, TopLoop(b0), pts, w);
                }
                catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                var s = bl[0]; double v = AnalyticVolume.Of(s) ?? double.NaN;
                if (Environment.GetEnvironmentVariable("P3D_LAWDBG") == "1")
                {
                    foreach (var iss in SolidValidator.Validate(s).Issues.Take(6)) Console.WriteLine("   issue: " + iss);
                    if (Environment.GetEnvironmentVariable("P3D_LAWVTX") is { } vx)
                    {
                        var xyz = vx.Split(',').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); var V = new Vector3d(xyz[0], xyz[1], xyz[2]);
                        foreach (var e in s.Edges.Where(e => (e.V0.P - V).Length < 1e-6 || (e.V1.P - V).Length < 1e-6))
                        {
                            var pl = new List<Vector3d>(); e.Curve.Tessellate(pl, true);
                            Console.WriteLine($"   edge {e.Id} {e.Curve.GetType().Name} {(e.Curve is ProcCurve3 pc ? pc.Kind : "")} {e.V0.P} → {e.V1.P} faces {string.Join(",", e.CoEdges.Select(ce => ce.Loop?.Face?.Id + ":" + GeometryAudit.AuthoritativeSurface(ce.Loop!.Face!)?.GetType().Name))} pts {pl.Count} near {string.Join(" ", pl.OrderBy(q => (q - V).Length).Take(3).Select(q => $"{(q - V).Length:E1}"))}");
                        }
                    }
                    double vf = Parametric3D.Kernel.Tess.MassProps.Volume(Parametric3D.Kernel.Tess.Tessellator.Tessellate(s));
                    Console.WriteLine($"   facets V {vf:R}");
                    foreach (var f in s.Faces)
                    {
                        var src = Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f);
                        var mb = new Parametric3D.Kernel.Tess.MeshBuilder(); f.Surface.TessellateFace(f, mb); var md = new Parametric3D.Kernel.Tess.MeshData(); mb.FillInto(md);
                        double fv = 0; for (int i = 0; i + 2 < md.Indices.Length; i += 3) { var a0 = md.Vertices[md.Indices[i]].P; var a1 = md.Vertices[md.Indices[i + 1]].P; var a2 = md.Vertices[md.Indices[i + 2]].P; fv += Vector3d.Dot(new Vector3d(a0.X, a0.Y, a0.Z), Vector3d.Cross(new Vector3d(a1.X, a1.Y, a1.Z), new Vector3d(a2.X, a2.Y, a2.Z))) / 6; }
                        if (src is Parametric3D.Kernel.Surfaces.RevolvedSurface) { AnalyticVolume.Debug = m => Console.WriteLine("    av: " + m); foreach (var ce in f.Loops[0].CoEdges) Console.WriteLine($"    lune edge {ce.Edge.Curve.GetType().Name} {ce.Edge.V0.P} → {ce.Edge.V1.P} rev {ce.Reversed} mid {ce.Edge.Curve.PointAt(0.5)}");
                            var rvs = (Parametric3D.Kernel.Surfaces.RevolvedSurface)src; var bc = ((ArcCurve3)rvs.Base).Frame.Origin; double rr = ((ArcCurve3)rvs.Base).Radius;
                            Console.WriteLine($"    lune hint {f.InteriorHint} on sphere {((f.InteriorHint ?? bc) - bc).Length - rr:E2} side {AnalyticVolume.SphereSideByHint(f, bc, rr)} reversed {f.Reversed} normal·out {Vector3d.Dot(f.NormalAt(f.InteriorHint ?? bc), ((f.InteriorHint ?? bc) - bc).Normalized()):F3}"); }
                        Console.WriteLine($"   face {src?.GetType().Name} {f.Tag} analytic {(AnalyticVolume.FaceIntegral(f) ?? double.NaN) / 3:F4} facets {fv:F4}");
                        AnalyticVolume.Debug = null;
                    }
                    foreach (var f in s.Faces) if (Parametric3D.Kernel.Topology.GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface ssf)
                    {
                        var br = AnalyticVolume.BandRange(f, ssf); var bo = br == null ? AnalyticVolume.BandOpenRange(f, ssf) : null;
                        Console.WriteLine($"   band u {ssf.UMin:F3}..{ssf.UMax:F3} edges {f.Loops[0].CoEdges.Count} range {(br is { } q ? $"{q.Ua:F4}..{q.Ub:F4}" : bo is { } o ? $"open L(0) {o.L(0):F5} L(.5) {o.L(0.5):F5} L(1) {o.L(1):F5} R(0) {o.R(0):F5} R(.5) {o.R(0.5):F5} R(1) {o.R(1):F5}" : "none")}");
                    }
                }
                var law = DirectRound.RadiusLaw.Points(Vector3d.Zero, Vector3d.UnitX, pts)!;
                _lawKnots = law.Knots;
                var (dv, spread) = LawLoopVolumeRef(W, D, H, R, law.R, law.dR, pts.Select(p => p.R).ToArray(), 0, 1); double want = (W * D - (4 - Math.PI) * R * R) * H + dv;
                Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}, ref spread {spread:E1}) {s.Guarantee}, {s.Faces.Count()} faces, lunes {DirectRound.LawLunes}, creases {DirectRound.LawCreases} | {string.Join(" | ", w.Take(2))}");
                if (DirectRound.LawLoops == c0) fails.Add($"{name}: not the law-loop builder ({string.Join(" | ", w.Take(2))})");
                if (s.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(s).IsValid) fails.Add($"{name}: {s.Guarantee} {SolidValidator.Validate(s).Summary()}");
                if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                if (name.StartsWith("constant") && !(Math.Abs(v - vConst) <= 1e-9 * vConst)) fails.Add($"{name}: V {v:R} vs constant radius {vConst:R}");
                var sw = new StringWriter(); var ws = new List<string>();
                if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, "law", ws) || ws.Any(x => x.Contains("not written exactly"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Take(2))})");
            }
            // ОТКРЫТАЯ ЦЕПОЧКА: брусок 60×40×20, скруглены два правых вертикальных ребра R8; верхний контур от левой стенки по
            // переду, правым углам и заду — концы на плоскости левой стенки (торец), лента продлевается и обрезается ею
            {
                TSolid Half()
                {
                    var bb = new List<TSolid> { Box(0, 0, 0, W, D, H) };
                    FilletOp.RoundEdges(bb, bb[0].Edges.Where(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1 && e.V0.P.X > W - 1).Select(EdgeRef.Of).ToList(), R, new List<string>());
                    return bb[0];
                }
                List<EdgeRef> Chain(TSolid b)
                {
                    var es = b.Edges.Where(e => Math.Abs(e.V0.P.Z - H) < 1e-7 && Math.Abs(e.V1.P.Z - H) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - H) < 1e-7 && e.Curve.PointAt(0.5).X > 1e-3).ToList();
                    var front = es.First(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Y) < 1e-7 && Math.Abs(e.V1.P.Y) < 1e-7);
                    var refs = new List<EdgeRef> { (front.V0.P.X < front.V1.P.X) ? EdgeRef.Of(front) : new EdgeRef(front.V1.P, front.V0.P, front.Curve.PointAt(0.5), front.FaceA?.Tag, front.FaceB?.Tag) };
                    refs.AddRange(es.Where(e => e != front).Select(EdgeRef.Of));
                    return refs;
                }
                double A0h = W * D - 2 * (1 - Math.PI / 4) * R * R;
                foreach (var (name, pts) in new (string, (double F, double R)[])[]
                {
                    ("open: constant 3", new[] { (0.0, 3.0), (1.0, 3.0) }),
                    ("open: 2 → 4 → 2.5", new[] { (0.0, 2.0), (0.4, 4.0), (1.0, 2.5) }),
                    ("open: 3 → 1.5 → 3.5 → 2", new[] { (0.0, 3.0), (0.25, 1.5), (0.6, 3.5), (1.0, 2.0) }),
                })
                {
                    if (Environment.GetEnvironmentVariable("P3D_LAWCASE") is { } lc && !name.Contains(lc)) continue;
                    var b0 = Half(); var bl = new List<TSolid> { b0 }; var w = new List<string>(); long c0 = DirectRound.LawOpenChains;
                    try { FilletOp.RoundEdgesLaw(bl, Chain(b0), pts, w); }
                    catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                    var s = bl[0]; double v = AnalyticVolume.Of(s) ?? double.NaN;
                    var law = DirectRound.RadiusLaw.Points(Vector3d.Zero, Vector3d.UnitX, pts)!;
                    if (Environment.GetEnvironmentVariable("P3D_LAWDBG") == "1")
                        foreach (var f in s.Faces) if (GeometryAudit.AuthoritativeSurface(f) is Parametric3D.Kernel.Surfaces.SectionSurface band)
                        {
                            var sp = Parametric3D.Kernel.Export.StepExact.BandSpan(f, band);
                            var fit = sp is { } q ? Parametric3D.Kernel.Surfaces.BandNurbs.Fit(band, q.Ua, q.Ub, 1e-7) : null;
                            Console.WriteLine($"   band u {band.UMin:F3}..{band.UMax:F3} span {sp} fit {(fit == null ? "null" : fit.Value.Deviation.ToString("E1"))} breaks {string.Join(",", band.Breaks.Select(x => x.ToString("F3")))}");
                        }
                    var (dv, spread) = OpenChainVolumeRef(W, D, H, R, law.R, law.dR, pts.Select(p => p.R).ToArray(), law.Knots);
                    double want = A0h * H + dv;
                    Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}, ref spread {spread:E1}) {s.Guarantee}, {s.Faces.Count()} faces | {string.Join(" | ", w.Take(2))}");
                    if (DirectRound.LawOpenChains == c0) fails.Add($"{name}: not the open law-chain builder ({string.Join(" | ", w.Take(2))})");
                    if (s.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(s).IsValid) fails.Add($"{name}: {s.Guarantee} {SolidValidator.Validate(s).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                    var sw = new StringWriter(); var ws = new List<string>();
                    if (!Parametric3D.Kernel.Export.StepExact.Write(sw, new[] { s }, "law", ws) || ws.Any(x => x.Contains("not written exactly"))) fails.Add($"{name}: exact STEP refused ({string.Join(" / ", ws.Where(x => x.Contains("not") || x.Contains("refus")).Take(3))})");
                }
            }
            // СТЫК «НА УС»: брусок 60×40×20 без скруглений, переднее и правое верхние рёбра — концы на левой и задней стенках
            {
                List<EdgeRef> Corner(TSolid b)
                {
                    var front = b.Edges.First(e => Math.Abs(e.V0.P.Z - H) < 1e-7 && Math.Abs(e.V1.P.Z - H) < 1e-7 && Math.Abs(e.V0.P.Y) < 1e-7 && Math.Abs(e.V1.P.Y) < 1e-7);
                    var right = b.Edges.First(e => Math.Abs(e.V0.P.Z - H) < 1e-7 && Math.Abs(e.V1.P.Z - H) < 1e-7 && Math.Abs(e.V0.P.X - W) < 1e-7 && Math.Abs(e.V1.P.X - W) < 1e-7);
                    return new List<EdgeRef> { (front.V0.P.X < front.V1.P.X) ? EdgeRef.Of(front) : new EdgeRef(front.V1.P, front.V0.P, front.Curve.PointAt(0.5), front.FaceA?.Tag, front.FaceB?.Tag), EdgeRef.Of(right) };
                }
                double vConstM = double.NaN;
                { var bl = new List<TSolid> { Box(0, 0, 0, W, D, H) }; FilletOp.RoundEdges(bl, Corner(bl[0]), 3, new List<string>()); vConstM = AnalyticVolume.Of(bl[0]) ?? double.NaN; }
                foreach (var (name, pts) in new (string, (double F, double R)[])[]
                {
                    ("mitre: constant 3", new[] { (0.0, 3.0), (1.0, 3.0) }),
                    ("mitre: 2 → 4 (corner at 0.6)", new[] { (0.0, 2.0), (1.0, 4.0) }),
                    ("mitre: 3 → 1.5 → 3.5 → 2", new[] { (0.0, 3.0), (0.25, 1.5), (0.6, 3.5), (1.0, 2.0) }),
                })
                {
                    if (Environment.GetEnvironmentVariable("P3D_LAWCASE") is { } lc && !name.Contains(lc)) continue;
                    var b0 = Box(0, 0, 0, W, D, H); var bl = new List<TSolid> { b0 }; var w = new List<string>(); long c0 = DirectRound.LawMitres;
                    if (Environment.GetEnvironmentVariable("P3D_LAWDBG") == "1") DirectRound.Debug = m => { if (m.Contains("law") || m.Contains("invalid")) Console.WriteLine("   dr: " + m); };
                    try { FilletOp.RoundEdgesLaw(bl, Corner(b0), pts, w); }
                    catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                    var s = bl[0]; double v = AnalyticVolume.Of(s) ?? double.NaN;
                    var law = DirectRound.RadiusLaw.Points(Vector3d.Zero, Vector3d.UnitX, pts)!;
                    var (dv, spread) = OpenChainVolumeRef(W, D, H, 0, law.R, law.dR, pts.Select(p => p.R).ToArray(), law.Knots, true);
                    double want = W * D * H + dv;
                    Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}, ref spread {spread:E1}) {s.Guarantee}, {s.Faces.Count()} faces | {string.Join(" | ", w.Take(2))}");
                    if (DirectRound.LawMitres == c0) fails.Add($"{name}: not the mitre law-chain builder ({string.Join(" | ", w.Take(2))})");
                    if (s.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(s).IsValid) fails.Add($"{name}: {s.Guarantee} {SolidValidator.Validate(s).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                    if (name.Contains("constant") && !(Math.Abs(v - vConstM) <= 1e-9 * vConstM)) fails.Add($"{name}: V {v:R} vs constant radius {vConstM:R}");
                }
            }
            // ВОГНУТЫЙ стык «на ус»: дно кармана 60×40 глубиной 10 в плите 100×80×20 — переднее и правое рёбра дна (угол грани
            // дна выпуклый); постоянный закон — объём постоянного радиуса; переменный — точное годное тело
            {
                TSolid Prism(double x, double y, double z, double w, double d, double h)
                {
                    Vector3d P(double a, double c) => new(a, c, z);
                    var prof = ProfileBuilder.Build(new Frame3(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY), new ICurve3[] { new LineCurve3(P(x, y), P(x + w, y)), new LineCurve3(P(x + w, y), P(x + w, y + d)), new LineCurve3(P(x + w, y + d), P(x, y + d)), new LineCurve3(P(x, y + d), P(x, y)) });
                    return ExtrudeOp.Build(prof, new Vector3d(0, 0, h));
                }
                TSolid PB() { var pl = new List<TSolid> { Prism(0, 0, 0, 100, 80, 20) }; BooleanOp.Subtract(pl, Prism(20, 20, 10, 60, 40, 20), new List<string>()); return pl[0]; }
                List<EdgeRef> Base(TSolid b)
                {
                    var front = b.Edges.First(e => Math.Abs(e.V0.P.Z - 10) < 1e-7 && Math.Abs(e.V1.P.Z - 10) < 1e-7 && Math.Abs(e.V0.P.Y - 20) < 1e-7 && Math.Abs(e.V1.P.Y - 20) < 1e-7 && e.Curve.PointAt(0.5).X > 21 && e.Curve.PointAt(0.5).X < 79);
                    var right = b.Edges.First(e => Math.Abs(e.V0.P.Z - 10) < 1e-7 && Math.Abs(e.V1.P.Z - 10) < 1e-7 && Math.Abs(e.V0.P.X - 80) < 1e-7 && Math.Abs(e.V1.P.X - 80) < 1e-7 && e.Curve.PointAt(0.5).Y > 21 && e.Curve.PointAt(0.5).Y < 59);
                    return new List<EdgeRef> { (front.V0.P.X < front.V1.P.X) ? EdgeRef.Of(front) : new EdgeRef(front.V1.P, front.V0.P, front.Curve.PointAt(0.5), front.FaceA?.Tag, front.FaceB?.Tag), EdgeRef.Of(right) };
                }
                if (Environment.GetEnvironmentVariable("P3D_LAWCASE") is not { } lc0 || "concave mitre".Contains(lc0) || lc0.Contains("concave mitre"))
                {
                    var bc = new List<TSolid> { PB() }; FilletOp.RoundEdges(bc, Base(bc[0]), 3, new List<string>()); double vc = AnalyticVolume.Of(bc[0]) ?? double.NaN;
                    foreach (var (name, pts) in new (string, (double F, double R)[])[] { ("concave mitre: constant 3", new[] { (0.0, 3.0), (1.0, 3.0) }), ("concave mitre: 2 → 4 → 2.5", new[] { (0.0, 2.0), (0.5, 4.0), (1.0, 2.5) }) })
                    {
                        var b0 = PB(); var bl = new List<TSolid> { b0 }; var w = new List<string>(); long c0 = DirectRound.LawMitres;
                        if (Environment.GetEnvironmentVariable("P3D_LAWDBG") == "1") { Console.WriteLine($"   base: {b0.Guarantee}, {b0.Faces.Count()} faces"); DirectRound.Debug = m => Console.WriteLine("   dr: " + m); }
                        try { FilletOp.RoundEdgesLaw(bl, Base(b0), pts, w); } catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                        var s = bl[0]; double v = AnalyticVolume.Of(s) ?? double.NaN;
                        Console.WriteLine($"  {name}: V {v:R} (constant radius {vc:R}) {s.Guarantee}, {s.Faces.Count()} faces | {string.Join(" | ", w.Take(2))}");
                        if (DirectRound.LawMitres == c0) fails.Add($"{name}: not the mitre law-chain builder ({string.Join(" | ", w.Take(2))})");
                        if (s.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(s).IsValid) fails.Add($"{name}: {s.Guarantee} {SolidValidator.Validate(s).Summary()}");
                        if (name.Contains("constant") && !(Math.Abs(v - vc) <= 1e-9 * vc)) fails.Add($"{name}: V {v:R} vs constant radius {vc:R}");
                    }
                }
            }
            // ВОГНУТЫЙ контур: основание бобышки 60×40 R8 (высота 20) на плите 100×80×10 — шар в пустоте, лента добавляет материал
            {
                TSolid Boss()
                {
                    var bb = new List<TSolid> { Box(20, 20, 10, 60, 40, 20) };
                    FilletOp.RoundEdges(bb, bb[0].Edges.Where(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Z - e.V1.P.Z) > 1).Select(EdgeRef.Of).ToList(), R, new List<string>());
                    var pl = new List<TSolid> { Box(0, 0, 0, 100, 80, 10) };
                    BooleanOp.Unite(pl, bb[0], new List<string>());
                    return pl[0];
                }
                List<EdgeRef> BottomLoop(TSolid b)
                {
                    var es = b.Edges.Where(e => Math.Abs(e.V0.P.Z - 10) < 1e-7 && Math.Abs(e.V1.P.Z - 10) < 1e-7 && Math.Abs(e.Curve.PointAt(0.5).Z - 10) < 1e-7
                                                && e.Curve.PointAt(0.5).X > 19 && e.Curve.PointAt(0.5).X < 81 && e.Curve.PointAt(0.5).Y > 19 && e.Curve.PointAt(0.5).Y < 61).ToList();
                    var front = es.First(e => e.Curve is LineCurve3 && Math.Abs(e.V0.P.Y - 20) < 1e-7 && Math.Abs(e.V1.P.Y - 20) < 1e-7);
                    var refs = new List<EdgeRef> { (front.V0.P.X < front.V1.P.X) ? EdgeRef.Of(front) : new EdgeRef(front.V1.P, front.V0.P, front.Curve.PointAt(0.5), front.FaceA?.Tag, front.FaceB?.Tag) };
                    refs.AddRange(es.Where(e => e != front).Select(EdgeRef.Of));
                    return refs;
                }
                double A0b = 60 * 40 - (4 - Math.PI) * R * R, vBase = 100 * 80 * 10 + A0b * 20;
                foreach (var (name, pts) in new (string, (double F, double R)[])[]
                {
                    ("concave: constant 3", new[] { (0.0, 3.0), (1.0, 3.0) }),
                    ("concave: 2 → 4 → 2.5 → 2", new[] { (0.0, 2.0), (0.3, 4.0), (0.65, 2.5), (1.0, 2.0) }),
                    ("concave: 3 → 1.5 → 3.5 → 3", new[] { (0.0, 3.0), (0.2, 1.5), (0.55, 3.5), (1.0, 3.0) }),
                })
                {
                    if (Environment.GetEnvironmentVariable("P3D_LAWCASE") is { } lc && !name.Contains(lc)) continue;
                    var b0 = Boss(); var bl = new List<TSolid> { b0 }; var w = new List<string>(); long c0 = DirectRound.LawLoops;
                    if (b0.Guarantee != GuaranteeLevel.ExactAnalytic) { fails.Add($"{name}: plate with boss {b0.Guarantee}"); continue; }
                    try { FilletOp.RoundEdgesLaw(bl, BottomLoop(b0), pts, w); }
                    catch (Exception ex) { fails.Add($"{name}: {ex.GetType().Name} {ex.Message}"); continue; }
                    var s = bl[0]; double v = AnalyticVolume.Of(s) ?? double.NaN;
                    var law = DirectRound.RadiusLaw.Points(Vector3d.Zero, Vector3d.UnitX, pts)!;
                    _lawKnots = law.Knots;
                    var (dv, spread) = LawLoopVolumeRef(60, 40, 10, R, law.R, law.dR, pts.Select(p => p.R).ToArray(), 0, 1, +1, 20, 20);
                    double want = vBase + dv;
                    Console.WriteLine($"  {name}: V {v:R} want {want:R} ({(v - want) / want:E1}, ref spread {spread:E1}) {s.Guarantee}, {s.Faces.Count()} faces | {string.Join(" | ", w.Take(2))}");
                    if (DirectRound.LawLoops == c0) fails.Add($"{name}: not the law-loop builder ({string.Join(" | ", w.Take(2))})");
                    if (s.Guarantee != GuaranteeLevel.ExactAnalytic || !SolidValidator.Validate(s).IsValid) fails.Add($"{name}: {s.Guarantee} {SolidValidator.Validate(s).Summary()}");
                    if (!(Math.Abs(v - want) <= 1e-8 * want)) fails.Add($"{name}: V {v:R} vs {want:R}");
                }
            }
            Require(fails.Count == 0, string.Join("; ", fails));
        });
    }
}
