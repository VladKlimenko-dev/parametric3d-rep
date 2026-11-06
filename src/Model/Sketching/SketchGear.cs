using OpenTK.Mathematics;
using Parametric3D.Core;

namespace Parametric3D.Model.Sketching;

/// <summary>Вид профиля зуба шестерни эскиза.</summary>
public enum GearProfile
{
    /// <summary>Эвольвентный (ГОСТ 13755 / ISO 53, исходный контур рейки).</summary>
    Involute = 0,
    /// <summary>Циклоидальный: головка — эпициклоида, ножка — гипоциклоида (часовые, насосные зацепления).</summary>
    Cycloidal = 1,
    /// <summary>Прямобочный: боковая сторона — отрезок под углом профиля к радиусу (храповики, шлицы, простые передачи).</summary>
    StraightFlank = 2,
}

/// <summary>
/// k176: ПАРАМЕТРЫ ШЕСТЕРНИ ЭСКИЗА. Смещение исходного контура x > 0 — от центра колеса и у наружного, и у внутреннего венца
/// (у наружного зуб на делительной окружности толще, у внутреннего — тоньше). Модуль — в единицах длины эскиза (мм).
/// </summary>
public sealed class GearParams
{
    public GearProfile Profile = GearProfile.Involute;
    /// <summary>Внутренний венец (зубья внутрь, как у коронной шестерни планетарной передачи).</summary>
    public bool Internal;
    public int Teeth = 20;
    /// <summary>Модуль m (делительный диаметр d = m·z).</summary>
    public double Module = 2;
    /// <summary>Угол профиля α, градусы (у прямобочного — угол стороны к радиусу).</summary>
    public double PressureAngle = 20;
    /// <summary>Коэффициент смещения x.</summary>
    public double ProfileShift;
    /// <summary>Коэффициент высоты головки h*a.</summary>
    public double AddendumCoef = 1.0;
    /// <summary>Коэффициент радиального зазора c* (высота ножки h*f = h*a + c*).</summary>
    public double ClearanceCoef = 0.25;
    /// <summary>Коэффициент радиуса переходной кривой у впадины ρ*f (радиус — ρ*f·m).</summary>
    public double RootFilletCoef = 0.38;
    /// <summary>Коэффициент радиуса скругления вершины зуба (0 — острая кромка).</summary>
    public double TipRoundCoef;
    /// <summary>Боковой зазор: уменьшение толщины зуба по делительной окружности, мм.</summary>
    public double Backlash;
    /// <summary>Циклоидальный профиль: радиус производящей окружности в модулях (0 — z/4: ножка прямая радиальная).</summary>
    public double RollingCircleCoef;
    /// <summary>Поворот венца, градусы (ось первого зуба от оси X эскиза).</summary>
    public double Rotation;
    public double CenterX, CenterY;
    /// <summary>Допуск приближения кривых боковых сторон, мм.</summary>
    public double Tolerance = 1e-3;
    /// <summary>Делительная окружность — вспомогательной линией.</summary>
    public bool PitchCircle = true;

    public GearParams Clone() => (GearParams)MemberwiseClone();

    public double PitchRadius => Module * Teeth / 2;
    public double Alpha => PressureAngle * Math.PI / 180;
    public double BaseRadius => PitchRadius * Math.Cos(Alpha);
    /// <summary>Радиус вершин: у наружного — r + m(h*a + x), у внутреннего — r − m(h*a − x).</summary>
    public double TipRadius => Internal ? PitchRadius - Module * (AddendumCoef - ProfileShift) : PitchRadius + Module * (AddendumCoef + ProfileShift);
    /// <summary>Радиус впадин: у наружного — r − m(h*f − x), у внутреннего — r + m(h*f + x).</summary>
    public double RootRadius => Internal ? PitchRadius + Module * (AddendumCoef + ClearanceCoef + ProfileShift) : PitchRadius - Module * (AddendumCoef + ClearanceCoef - ProfileShift);
    /// <summary>Толщина зуба по делительной окружности (дуга).</summary>
    public double ToothThickness => Module * (Math.PI / 2 + (Internal ? -2 : 2) * ProfileShift * Math.Tan(Alpha)) - Backlash;
    public double CircularPitch => Math.PI * Module;
    /// <summary>Диаметральный шаг (зубьев на дюйм делительного диаметра).</summary>
    public double DiametralPitch => 25.4 / Module;

    /// <summary>Причина, по которой шестерню не построить; null — годна.</summary>
    public string? Check()
    {
        if (Teeth < 3 || Teeth > 2000) return "число зубьев — от 3 до 2000";
        if (!(Module > 0) || !double.IsFinite(Module)) return "модуль должен быть больше нуля";
        if (Profile != GearProfile.Cycloidal && !(PressureAngle > 0 && PressureAngle < 45)) return "угол профиля — от 0 до 45°";
        if (Profile == GearProfile.StraightFlank && !(PressureAngle >= 0 && PressureAngle < 45)) return "угол стороны — от 0 до 45°";
        if (!(AddendumCoef >= 0) || !(ClearanceCoef >= 0) || !(RootFilletCoef >= 0) || !(TipRoundCoef >= 0)) return "коэффициенты высоты, зазора и скруглений не могут быть отрицательными";
        if (!(Tolerance > 0)) return "допуск должен быть больше нуля";
        if (!(ToothThickness > 0) || ToothThickness >= CircularPitch) return "толщина зуба выходит за шаг (смещение или боковой зазор слишком велики)";
        if (Internal ? !(RootRadius > TipRadius) : !(TipRadius > RootRadius)) return "высота зуба не положительна";
        if (!(RootRadius > 0) || !(TipRadius > 0)) return "окружность впадин или вершин вырождена (мало зубьев или большое смещение)";
        return null;
    }
}

/// <summary>k176: шестерня эскиза — параметры и номера её элементов (замкнутый контур, центр, делительная окружность).</summary>
public sealed class SkGear
{
    public uint Id { get; internal set; } = Ids.New();
    public GearParams P = new();
    /// <summary>Номера элементов эскиза, построенных по параметрам (точки — тоже).</summary>
    public readonly List<uint> Items = new();
    /// <summary>Номер центральной точки (вспомогательной).</summary>
    public uint CenterId;
    public string? Error;
    public SkGear Copy() { var g = new SkGear { Id = Id, P = P.Clone(), CenterId = CenterId, Error = Error }; g.Items.AddRange(Items); return g; }
}

/// <summary>
/// k176: ПОСТРОЕНИЕ ПРОФИЛЯ ШЕСТЕРНИ. Профиль одного шага считается в системе зуба (ось зуба — ось X): половинная угловая
/// толщина h(ρ) по радиусу ρ, сторона зуба — точки ρ(cos h, sin h); переходная кривая у впадины и скругление вершины — дуги,
/// касательные к стороне и к окружности впадин (вершин); кривые стороны — кривые Безье с концами и касательными точно на
/// профиле, степень от 3 до 9 подбирается под допуск (иначе — делением). Шаг поворачивается z раз; соседние элементы
/// делят концевые точки (контур замкнут без связей). Внутренний венец — дополнение впадины наружного:
/// h_вн(ρ) = π/z − h_нар(ρ) при толщине s_нар = πm − s_вн.
/// </summary>
public static class GearGeometry
{
    public enum SegKind { Line, Arc, Bezier }

    /// <summary>Элемент профиля: отрезок (P), дуга (центр C, концы P[0], P[^1], направление Ccw) или кривая Безье (P — опорные точки).</summary>
    public sealed class Seg
    {
        public SegKind Kind;
        public List<Vector2d> P = new();
        public Vector2d C;
        public bool Ccw = true;
        public Vector2d Start => P[0];
        public Vector2d End => P[^1];
        public Seg Map(Func<Vector2d, Vector2d> f, bool flip)
        {
            var s = new Seg { Kind = Kind, C = f(C), Ccw = Ccw };
            s.P = P.Select(f).ToList();
            if (flip) s.P.Reverse();
            return s;
        }
    }

    sealed class Profile
    {
        public double Ra, Rf, Rlo, Rhi;      // вершины, впадины; диапазон определённости h
        public Func<double, double> H = null!;
        /// <summary>Радиусы, где сторона меняет вид (эвольвента/радиальный отрезок, головка/ножка циклоиды).</summary>
        public List<double> Breaks = new();
        /// <summary>Участки стороны, которые — отрезки (прямобочный зуб, продолжение ниже основной окружности).</summary>
        public Func<double, double, bool> IsLine = (_, _) => false;
    }

    static double Inv(double a) => Math.Tan(a) - a;

    /// <summary>Половинная толщина наружного зуба h(ρ) для толщины s по делительной окружности.</summary>
    static Profile External(GearParams p, double s, double ra, double rf)
    {
        double r = p.PitchRadius, z = p.Teeth;
        var pr = new Profile { Ra = ra, Rf = rf };
        double psi = s / (2 * r);
        switch (p.Profile)
        {
            case GearProfile.Involute:
            {
                double rb = p.BaseRadius, a = p.Alpha;
                pr.H = rho => psi + Inv(a) - Inv(Math.Acos(Math.Min(1, rb / Math.Max(rho, rb))));
                pr.Rlo = 0; pr.Rhi = double.MaxValue;
                if (rb > Math.Min(ra, rf) && rb < Math.Max(ra, rf)) pr.Breaks.Add(rb);
                pr.IsLine = (a0, a1) => Math.Max(a0, a1) <= rb * (1 + 1e-12);
                break;
            }
            case GearProfile.StraightFlank:
            {
                double a = p.Alpha;
                // прямая через точку делительной окружности под углом α к радиусу, к оси зуба наружу
                var p0 = new Vector2d(Math.Cos(psi), Math.Sin(psi)) * r;
                var er = new Vector2d(Math.Cos(psi), Math.Sin(psi)); var et = new Vector2d(-Math.Sin(psi), Math.Cos(psi));
                var u = er * Math.Cos(a) - et * Math.Sin(a);
                double rmin = r * Math.Sin(a);
                pr.H = rho =>
                {
                    double q = Math.Max(rho, rmin);
                    double disc = r * r * Math.Cos(a) * Math.Cos(a) - r * r + q * q;
                    double lam = -r * Math.Cos(a) + Math.Sqrt(Math.Max(0, disc));
                    var pt = p0 + u * lam;
                    return Math.Atan2(pt.Y, pt.X);
                };
                pr.Rlo = 0; pr.Rhi = double.MaxValue;
                pr.IsLine = (_, _) => true;
                break;
            }
            default:
            {
                double rr = p.RollingCircleCoef > 0 ? p.RollingCircleCoef * p.Module : r / 2 * 0.5;
                rr = Math.Min(rr, r / 2);
                // эпициклоида от точки делительной окружности: радиус растёт с φ до r + 2ρ; гипоциклоида — убывает до r − 2ρ
                (double R, double Th) Epi(double ph) { double k = (r + rr) / rr; var q = new Vector2d((r + rr) * Math.Cos(ph) - rr * Math.Cos(k * ph), (r + rr) * Math.Sin(ph) - rr * Math.Sin(k * ph)); return (q.Length, Math.Atan2(q.Y, q.X)); }
                (double R, double Th) Hypo(double ph) { double k = (r - rr) / rr; var q = new Vector2d((r - rr) * Math.Cos(ph) + rr * Math.Cos(k * ph), (r - rr) * Math.Sin(ph) - rr * Math.Sin(k * ph)); return (q.Length, Math.Atan2(q.Y, q.X)); }
                double phMaxE = Math.PI * rr / r, phMaxH = Math.PI * rr / r;   // полуоборот производящей окружности
                double Solve(Func<double, (double R, double Th)> f, double ph1, double rho)
                {
                    double lo = 0, hi = ph1; bool up = f(ph1).R > f(0).R;
                    for (int i = 0; i < 100; i++) { double m = (lo + hi) / 2; if ((f(m).R < rho) == up) lo = m; else hi = m; }
                    return (lo + hi) / 2;
                }
                double rTop = r + 2 * rr, rBot = r - 2 * rr;
                pr.H = rho =>
                {
                    if (rho >= r) return psi - Epi(Solve(Epi, phMaxE, Math.Min(rho, rTop))).Th;
                    if (rr >= r / 2 * (1 - 1e-12)) return psi;
                    return psi + Hypo(Solve(Hypo, phMaxH, Math.Max(rho, rBot))).Th;
                };
                pr.Rlo = rBot; pr.Rhi = rTop;
                if (r > Math.Min(ra, rf) && r < Math.Max(ra, rf)) pr.Breaks.Add(r);
                pr.IsLine = (a0, a1) => rr >= r / 2 * (1 - 1e-12) && Math.Max(a0, a1) <= r * (1 + 1e-12);
                break;
            }
        }
        return pr;
    }

    static Profile Make(GearParams p)
    {
        double ra = p.TipRadius, rf = p.RootRadius, z = p.Teeth;
        if (!p.Internal) return External(p, p.ToothThickness, ra, rf);
        var ext = External(p, Math.PI * p.Module - p.ToothThickness, ra, rf);
        var hx = ext.H;
        ext.H = rho => Math.PI / z - hx(rho);
        return ext;
    }

    /// <summary>Контур шестерни (замкнутый, против часовой стрелки) в координатах эскиза; null и причина — не построить.</summary>
    public static List<Seg>? Build(GearParams p, out string? why)
    {
        why = p.Check(); if (why != null) return null;
        var pr = Make(p);
        double z = p.Teeth, half = Math.PI / z, m = p.Module;
        bool inner = p.Internal;
        double ra = pr.Ra, rf = pr.Rf;
        // у эвольвенты внутреннего венца вершины — не ниже основной окружности
        if (inner && p.Profile == GearProfile.Involute && ra < p.BaseRadius) ra = p.BaseRadius * (1 + 1e-9);
        if (p.Profile == GearProfile.Cycloidal)
        {
            double rr = p.RollingCircleCoef > 0 ? p.RollingCircleCoef * m : p.PitchRadius / 4; rr = Math.Min(rr, p.PitchRadius / 2);
            double cTop = p.PitchRadius + 2 * rr, cBot = p.PitchRadius - 2 * rr;
            ra = Math.Clamp(ra, cBot, cTop); rf = Math.Clamp(rf, cBot, cTop);
        }
        Func<double, double> H = pr.H;
        Vector2d F(double rho) { double h = H(rho); return new Vector2d(Math.Cos(h), Math.Sin(h)) * rho; }
        // заострённый зуб: вершина — где толщина обращается в ноль
        if (H(ra) <= 1e-9)
        {
            double lo = rf, hi = ra;
            for (int i = 0; i < 200; i++) { double mm = (lo + hi) / 2; if (H(mm) > 1e-9) lo = mm; else hi = mm; }
            ra = lo;
        }
        if (H(rf) >= half - 1e-9) { why = "впадина у окружности впадин закрыта (зубья смыкаются) — уменьшите высоту ножки или смещение"; return null; }

        // направление к впадине у стороны +1 и единичная касательная
        Vector2d Tan(double rho) { double d = 1e-7 * Math.Max(1, rho); var t = F(rho + d) - F(rho - d); return t.Normalized(); }
        Vector2d GapN(double rho)
        {
            var t = Tan(rho); var n = new Vector2d(-t.Y, t.X); var pt = F(rho);
            var eth = new Vector2d(-pt.Y, pt.X); if (Vector2d.Dot(n, eth) < 0) n = -n;
            return n;
        }
        // дуга радиуса q, касательная к стороне и к окружности радиуса R; sigma — центр снаружи (+1) или внутри (−1) окружности;
        // toGap — дуга во впадине (иначе в теле зуба). Возвращает радиус точки касания на стороне, центр и точку на окружности.
        (double Rho, Vector2d C, Vector2d Pc)? Tangent(double R, double q, int sigma, bool toGap, double rhoFar)
        {
            if (q <= 0) return null;
            double G(double rho) { var n = GapN(rho) * (toGap ? 1 : -1); var c = F(rho) + n * q; return c.Length - (R + sigma * q); }
            double a = R, b = rhoFar; double ga = G(a), gb = G(b);
            if (!(ga * gb < 0)) return null;
            for (int i = 0; i < 200; i++) { double mm = (a + b) / 2; double gm = G(mm); if ((gm < 0) == (ga < 0)) { a = mm; ga = gm; } else b = mm; }
            double rs = (a + b) / 2; var cc = F(rs) + GapN(rs) * (toGap ? 1 : -1) * q;
            return (rs, cc, cc * (R / cc.Length));
        }

        // переходная кривая впадины: дуга во впадине, центр — снаружи окружности впадин у наружного венца, внутри — у внутреннего;
        // точка на окружности впадин не дальше середины впадины (иначе радиус уменьшается)
        double qf = p.RootFilletCoef * m;
        (double Rho, Vector2d C, Vector2d Pc)? fil = null;
        for (int it = 0; it < 40 && qf > 1e-9 * m; it++)
        {
            fil = Tangent(rf, qf, inner ? -1 : 1, true, ra);
            if (fil is { } f0 && Math.Atan2(f0.Pc.Y, f0.Pc.X) <= half * (1 - 1e-9)) break;
            fil = null; qf *= 0.8;
        }
        // скругление вершины: дуга в теле зуба, центр внутри окружности вершин у наружного, снаружи — у внутреннего
        double qa = p.TipRoundCoef * m;
        (double Rho, Vector2d C, Vector2d Pc)? tip = null;
        for (int it = 0; it < 40 && qa > 1e-9 * m; it++)
        {
            tip = Tangent(ra, qa, inner ? 1 : -1, false, fil?.Rho ?? rf);
            if (tip is { } t0 && Math.Atan2(t0.Pc.Y, t0.Pc.X) >= 1e-9) break;
            tip = null; qa *= 0.8;
        }
        double rhoTop = tip?.Rho ?? ra, rhoBot = fil?.Rho ?? rf;

        // сторона +1 — от вершины к впадине
        var side = new List<Seg>();
        if (tip is { } tp) side.Add(ArcSeg(tp.C, tp.Pc, F(rhoTop)));
        var cuts = new List<double> { rhoTop };
        foreach (var br in pr.Breaks) if ((br - rhoTop) * (br - rhoBot) < 0) cuts.Add(br);
        cuts.Add(rhoBot);
        cuts = inner ? cuts.OrderBy(x => x).ToList() : cuts.OrderByDescending(x => x).ToList();
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            double a0 = cuts[i], a1 = cuts[i + 1];
            if (Math.Abs(a1 - a0) < 1e-12 * Math.Max(1, ra)) continue;
            if (pr.IsLine(a0, a1)) side.Add(new Seg { Kind = SegKind.Line, P = { F(a0), F(a1) } });
            else foreach (var cv in FitBezier(F, a0, a1, p.Tolerance)) side.Add(new Seg { Kind = SegKind.Bezier, P = cv });
        }
        if (fil is { } fl) side.Add(ArcSeg(fl.C, F(rhoBot), fl.Pc));

        // шаг: сторона −1 (зеркально, обратным ходом), дуга вершины, сторона +1, дуга впадины до следующего зуба
        Vector2d Mir(Vector2d v) => new(v.X, -v.Y);
        var pitch = new List<Seg>();
        for (int i = side.Count - 1; i >= 0; i--) pitch.Add(side[i].Map(Mir, flip: true));
        var top = side.Count > 0 ? side[0].Start : F(ra);
        if (top.Y > 1e-12 * ra) pitch.Add(new Seg { Kind = SegKind.Arc, C = Vector2d.Zero, Ccw = true, P = { Mir(top), top } });
        pitch.AddRange(side);
        var bot = side.Count > 0 ? side[^1].End : F(rf);
        double thBot = Math.Atan2(bot.Y, bot.X);
        var next = Rot(Mir(bot), 2 * half);
        if (2 * half - 2 * thBot > 1e-12) pitch.Add(new Seg { Kind = SegKind.Arc, C = Vector2d.Zero, Ccw = true, P = { bot, next } });

        // поворот на z шагов и перенос в центр
        var all = new List<Seg>();
        double rot = p.Rotation * Math.PI / 180;
        var c0 = new Vector2d(p.CenterX, p.CenterY);
        for (int k = 0; k < p.Teeth; k++)
        {
            double ang = rot + 2 * half * k;
            foreach (var s in pitch) all.Add(s.Map(v => c0 + Rot(v, ang), flip: false));
        }
        return all;
    }

    static Vector2d Rot(Vector2d v, double a) => new(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a));

    static Seg ArcSeg(Vector2d c, Vector2d a, Vector2d b)
    {
        var u = a - c; var v = b - c;
        return new Seg { Kind = SegKind.Arc, C = c, Ccw = u.X * v.Y - u.Y * v.X > 0, P = { a, b } };
    }

    // ------------------------------------------------------------ приближение кривой стороны

    /// <summary>
    /// Кривые Безье по кривой f на [a, b]: концы и касательные — точно; степень 3…9 — наименьшая, при которой отход не больше
    /// tol (по плотной выборке, расстояние до ближайшей точки кривой Безье); иначе отрезок делится пополам.
    /// </summary>
    public static List<List<Vector2d>> FitBezier(Func<double, Vector2d> f, double a, double b, double tol, int depth = 0)
    {
        for (int n = 3; n <= 9; n++)
            if (FitDegree(f, a, b, n) is { } cp && Deviation(f, a, b, cp) <= tol) return new() { cp };
        if (depth >= 6)
        {
            var best = FitDegree(f, a, b, 9) ?? new List<Vector2d> { f(a), f(b) };
            return new() { best };
        }
        double m = (a + b) / 2;
        var res = FitBezier(f, a, m, tol, depth + 1);
        res.AddRange(FitBezier(f, m, b, tol, depth + 1));
        return res;
    }

    static Vector2d Bez(List<Vector2d> cp, double t)
    {
        var tmp = cp.ToArray();
        for (int k = tmp.Length - 1; k > 0; k--) for (int i = 0; i < k; i++) tmp[i] = tmp[i] * (1 - t) + tmp[i + 1] * t;
        return tmp[0];
    }
    static Vector2d BezD(List<Vector2d> cp, double t)
    {
        int n = cp.Count - 1; var d = new List<Vector2d>(n);
        for (int i = 0; i < n; i++) d.Add((cp[i + 1] - cp[i]) * n);
        return n == 1 ? d[0] : Bez(d, t);
    }
    static double Bern(int n, int j, double t)
    {
        double c = 1; for (int i = 1; i <= j; i++) c = c * (n - j + i) / i;
        return c * Math.Pow(t, j) * Math.Pow(1 - t, n - j);
    }

    static List<Vector2d>? FitDegree(Func<double, Vector2d> f, double a, double b, int n)
    {
        const int N = 64;
        var pts = new Vector2d[N + 1]; var ts = new double[N + 1];
        for (int i = 0; i <= N; i++) pts[i] = f(a + (b - a) * i / N);
        double len = 0; ts[0] = 0;
        for (int i = 1; i <= N; i++) { len += (pts[i] - pts[i - 1]).Length; ts[i] = len; }
        if (!(len > 0)) return null;
        for (int i = 0; i <= N; i++) ts[i] /= len;
        double d = 1e-7 * Math.Max(1e-12, Math.Abs(b - a));
        var P0 = pts[0]; var Pn = pts[N];
        var T0 = (f(a + d * Math.Sign(b - a)) - P0).Normalized(); var T1 = (Pn - f(b - d * Math.Sign(b - a))).Normalized();
        List<Vector2d>? cp = null;
        for (int pass = 0; pass < 4; pass++)
        {
            int free = n - 3;                       // внутренние точки P2…P(n−2)
            int m = 2 + 2 * Math.Max(0, free);
            var A = new double[m, m]; var B = new double[m];
            for (int i = 0; i <= N; i++)
            {
                double t = ts[i];
                var K = P0 * (Bern(n, 0, t) + Bern(n, 1, t)) + Pn * (Bern(n, n - 1, t) + Bern(n, n, t));
                var r = pts[i] - K;
                var rowX = new double[m]; var rowY = new double[m];
                double b1 = Bern(n, 1, t), bn1 = Bern(n, n - 1, t);
                rowX[0] = b1 * T0.X; rowY[0] = b1 * T0.Y;
                rowX[1] = -bn1 * T1.X; rowY[1] = -bn1 * T1.Y;
                for (int j = 2; j <= n - 2; j++) { double bj = Bern(n, j, t); int k = 2 + 2 * (j - 2); rowX[k] = bj; rowY[k + 1] = bj; }
                for (int u = 0; u < m; u++)
                {
                    B[u] += rowX[u] * r.X + rowY[u] * r.Y;
                    for (int v = 0; v < m; v++) A[u, v] += rowX[u] * rowX[v] + rowY[u] * rowY[v];
                }
            }
            for (int u = 0; u < m; u++) A[u, u] += 1e-14 * (1 + A[u, u]);
            if (!SolveLin(A, B)) return cp;
            var c = new List<Vector2d> { P0, P0 + T0 * B[0] };
            for (int j = 2; j <= n - 2; j++) { int k = 2 + 2 * (j - 2); c.Add(new Vector2d(B[k], B[k + 1])); }
            c.Add(Pn - T1 * B[1]); c.Add(Pn);
            if (B[0] <= 0 || B[1] <= 0) return cp;   // касательная развернулась — степень не годится
            cp = c;
            // уточнение параметров: ближайшая точка кривой Безье (Ньютон)
            for (int i = 1; i < N; i++)
            {
                double t = ts[i];
                for (int it = 0; it < 4; it++)
                {
                    var q = Bez(cp, t) - pts[i]; var dq = BezD(cp, t);
                    double den = Vector2d.Dot(dq, dq); if (!(den > 1e-300)) break;
                    t = Math.Clamp(t - Vector2d.Dot(q, dq) / den, 0, 1);
                }
                ts[i] = t;
            }
        }
        return cp;
    }

    /// <summary>Наибольшее расстояние от точек кривой f до кривой Безье и обратно (по 200 точкам).</summary>
    public static double Deviation(Func<double, Vector2d> f, double a, double b, List<Vector2d> cp)
    {
        const int N = 200; double worst = 0;
        var bs = new Vector2d[N + 1]; for (int i = 0; i <= N; i++) bs[i] = Bez(cp, i / (double)N);
        var fs = new Vector2d[N + 1]; for (int i = 0; i <= N; i++) fs[i] = f(a + (b - a) * i / N);
        double Dist(Vector2d q, Vector2d[] poly)
        {
            double best = double.MaxValue;
            for (int i = 1; i < poly.Length; i++)
            {
                var ab = poly[i] - poly[i - 1]; double l2 = ab.LengthSquared;
                double t = l2 < 1e-300 ? 0 : Math.Clamp(Vector2d.Dot(q - poly[i - 1], ab) / l2, 0, 1);
                best = Math.Min(best, (q - (poly[i - 1] + ab * t)).Length);
            }
            return best;
        }
        // точки кривой — до ломаной Безье (плотной), и точки Безье — до ломаной кривой: стрела ломаной при 200 звеньях мала
        for (int i = 0; i <= N; i++) worst = Math.Max(worst, Dist(fs[i], bs));
        for (int i = 0; i <= N; i += 4) worst = Math.Max(worst, Dist(bs[i], fs));
        return worst;
    }

    static bool SolveLin(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int c = 0; c < n; c++)
        {
            int piv = c; for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (!(Math.Abs(a[piv, c]) > 1e-300)) return false;
            if (piv != c) { for (int k = 0; k < n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]); (b[c], b[piv]) = (b[piv], b[c]); }
            for (int r = c + 1; r < n; r++) { double f = a[r, c] / a[c, c]; if (f == 0) continue; for (int k = c; k < n; k++) a[r, k] -= f * a[c, k]; b[r] -= f * b[c]; }
        }
        for (int c = n - 1; c >= 0; c--) { double s = b[c]; for (int k = c + 1; k < n; k++) s -= a[c, k] * b[k]; b[c] = s / a[c, c]; }
        return true;
    }
}

/// <summary>k176: шестерни эскиза — добавление, перестроение по параметрам, поиск по элементу.</summary>
public static class SketchGears
{
    /// <summary>Шестерня, которой принадлежит элемент (или его точка); null — не шестерня.</summary>
    public static SkGear? GearOf(this Sketch sk, SkEntity e)
    {
        uint id = e.Id;
        foreach (var g in sk.Gears) if (g.Items.Contains(id)) return g;
        if (e is SkPoint pt && pt.Owner is { } o) foreach (var g in sk.Gears) if (g.Items.Contains(o.Id)) return g;
        return null;
    }

    /// <summary>Новая шестерня по параметрам; null — параметры негодны (причина — в why).</summary>
    public static SkGear? AddGear(this Sketch sk, GearParams p, out string? why)
    {
        var g = new SkGear { P = p.Clone() };
        if (!Regenerate(sk, g, out why)) return null;
        sk.Gears.Add(g);
        return g;
    }

    /// <summary>
    /// Перестроить элементы шестерни по её параметрам: прежние элементы (и связи, размеры на них) удаляются, новые —
    /// закреплены (решатель их не двигает: форма задаётся параметрами), соседние делят концевые точки.
    /// </summary>
    public static bool Regenerate(this Sketch sk, SkGear g, out string? why)
    {
        var segs = GearGeometry.Build(g.P, out why);
        if (segs == null) { g.Error = why; return false; }
        g.Error = null;
        var old = new HashSet<uint>(g.Items);
        if (old.Count > 0)
        {
            var gone = sk.Entities.Where(e => old.Contains(e.Id)).ToList();
            var pts = new HashSet<SkEntity>(gone.SelectMany(e => e.Handles.Cast<SkEntity>().Prepend(e)), ReferenceEqualityComparer.Instance);
            sk.Entities.RemoveAll(e => old.Contains(e.Id));
            sk.Constraints.RemoveAll(c => c.Items.Any(pts.Contains));
            sk.Dimensions.RemoveAll(d => d.Items.Any(pts.Contains));
        }
        g.Items.Clear();
        T Add<T>(T e) where T : SkEntity { e.Fixed = true; sk.Entities.Add(e); g.Items.Add(e.Id); return e; }

        var centre = Add(new SkPoint(new Vector2d(g.P.CenterX, g.P.CenterY)) { Role = SkRole.Construction });
        g.CenterId = centre.Id;
        if (g.P.PitchCircle) Add(new SkCircle(centre, g.P.PitchRadius) { Role = SkRole.Construction });

        SkPoint? first = null, prev = null;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i]; bool last = i == segs.Count - 1;
            var a = prev ?? new SkPoint(s.Start);
            first ??= a;
            var b = last && first != null && (first.P - s.End).Length < 1e-6 * Math.Max(1, g.P.PitchRadius) ? first : new SkPoint(s.End);
            a.Fixed = b.Fixed = true;
            switch (s.Kind)
            {
                case GearGeometry.SegKind.Line: Add(new SkLine(a, b)); break;
                case GearGeometry.SegKind.Arc: Add(new SkArc(new SkPoint(s.C) { Fixed = true, Role = SkRole.Construction }, a, b, s.Ccw)); break;
                default:
                {
                    var ctrl = new List<SkPoint> { a };
                    for (int k = 1; k < s.P.Count - 1; k++) ctrl.Add(new SkPoint(s.P[k]) { Fixed = true });
                    ctrl.Add(b);
                    Add(new SkSpline(ctrl));
                    break;
                }
            }
            prev = b;
        }
        sk.BumpStructure();
        sk.NotifyChanged(sk);
        return true;
    }
}
