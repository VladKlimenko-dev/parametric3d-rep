using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Geometry;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Analytic;

/// <summary>
/// АНАЛИТИЧЕСКОЕ ЯДРО, ЭТАП 2, БЛОК 1: ЛЕЖИТ ЛИ ТОЧКА ВНУТРИ ТЕЛА — ПО ТОЧНЫМ ПОВЕРХНОСТЯМ.
///
/// Луч из точки (одно из постоянных «общих» направлений — ответ воспроизводим) пересекает поверхности граней точно
/// (плоскость — линейное, цилиндр — квадратное уравнение). Попадание в грань — чётностью пересечений в параметрах грани:
/// у плоской — в 2D рамке плоскости, у цилиндрической — в развёртке (θ, h) лучом вдоль образующей (периодичность — сама
/// собой: пояс между двумя окружностями изнутри пересекается один раз, снаружи — ни разу). Границы граней — точные, где
/// можно (отрезки; окружности в плоскости плоской грани; горизонтальные окружности и вертикали цилиндра), прочие кривые —
/// по мелкой ломаной.
///
/// Сомнительный луч (попадание ближе допуска к границе грани, касательное) отбрасывается целиком — берётся следующий
/// направление (как у сеточного Voxels.PointInSolid). Поддерживаются плоскости и цилиндры (поверхности этапа 2а);
/// для прочих — null («не знаю»).
/// </summary>
public sealed class Classifier
{
    /// <summary>Допуск: точка на поверхности грани и «сомнительная» близость попадания к границе, мм.</summary>
    /// <summary>KRN-02: допуск классификации — 10 модельных точностей (1e-6 мм по умолчанию).</summary>
    public static double Tol => 10 * ToleranceContext.Current.ModelAccuracy;

    readonly List<AnalyticFace> _faces = new();
    public string? Unsupported { get; private set; }

    public Classifier(TSolid solid) : this(solid.Faces) { }

    /// <summary>Классификатор по набору граней (наружная оболочка без полостей — проверка вложенности полости).</summary>
    public Classifier(IEnumerable<TFace> faces)
    {
        foreach (var f in faces)
        {
            var af = AnalyticFace.Of(f, out var why);
            if (af == null) { Unsupported = why; return; }
            _faces.Add(af);
        }
    }

    static readonly Vector3d[] Dirs =
    {
        new Vector3d(0.5383, 0.6189, 0.5718).Normalized(), new Vector3d(-0.3271, 0.8147, -0.4789).Normalized(),
        new Vector3d(0.7193, -0.2841, 0.6338).Normalized(), new Vector3d(-0.6114, -0.5237, 0.5932).Normalized(),
        new Vector3d(0.2917, 0.3361, -0.8957).Normalized(), new Vector3d(-0.8461, 0.1712, 0.5047).Normalized(),
        new Vector3d(0.1093, -0.9372, -0.3313).Normalized(), new Vector3d(0.6627, 0.7012, -0.2627).Normalized(),
    };

    /// <summary>+1 — внутри, −1 — снаружи, 0 — на границе (на грани в пределах Tol); null — не знаю.</summary>
    [ThreadStatic] public static Action<string>? Debug;
    public int? Classify(Vector3d p)
    {
        if (Unsupported != null) return null;
        // на границе?
        foreach (var f in _faces)
            if (f.SurfaceDistance(p) < Tol && f.Contains(f.ProjectTo(p)) != false) return 0;
        foreach (var dir in Dirs)
        {
            int hits = 0; bool doubtful = false;
            foreach (var f in _faces)
            {
                foreach (var (t, q, grazing) in RayHits(f, p, dir))
                {
                    Debug?.Invoke($"dir {dir} face {(f.Cone ? "cone" : f.Sph ? "sphere" : f.Cyl ? "cyl" : "plane")} {f.Face.Tag} t {t:G6} grazing {grazing} contains {(t > Tol && !grazing ? f.Contains(q) : null)}");
                    if (t <= Tol) continue;
                    if (grazing) { doubtful = true; break; }
                    var inside = f.Contains(q);
                    if (inside == null) { doubtful = true; break; }
                    if (inside == true) hits++;
                }
                if (doubtful) break;
            }
            if (!doubtful) return (hits & 1) == 1 ? 1 : -1;
        }
        return null;
    }

    static IEnumerable<(double T, Vector3d Q, bool Grazing)> RayHits(AnalyticFace f, Vector3d p, Vector3d dir)
    {
        if (f.Band) { foreach (var h in f.BandRayHits(p, dir)) yield return h; yield break; }
        if (f.Param) { foreach (var h in f.ParamRayHits(p, dir)) yield return h; yield break; }
        if (f.Tor)
        {
            // ((|w|² + R² − r²)² − 4R²ρ²) на луче — многочлен 4-й степени по t; t — в пределах шара R + r вокруг центра
            var wt = p - f.O; double bq = Vector3d.Dot(wt, dir), cq = wt.LengthSquared - (f.R + f.Rm) * (f.R + f.Rm), dq = bq * bq - cq;
            if (dq < 0) yield break;
            double sq0 = Math.Sqrt(dq), tlo = -bq - sq0 - 1e-9, thi = -bq + sq0 + 1e-9;
            double K = f.R * f.R - f.Rm * f.Rm;
            double Qt(double t) { var x = wt + dir * t; double z = Vector3d.Dot(x, f.W); double rho2 = x.LengthSquared - z * z; double s = x.LengthSquared + K; return s * s - 4 * f.R * f.R * rho2; }
            double mid = (tlo + thi) / 2, half = (thi - tlo) / 2;
            var roots = SsiEvents.PolyRootsOn(s => Qt(mid + half * s), 4);
            foreach (var sr in roots)
            {
                double t = mid + half * sr;
                // уточнение Ньютоном по самой функции; касание — малая производная (кратный корень)
                for (int it = 0; it < 4; it++) { double h = 1e-7 * Math.Max(1, Math.Abs(t)); double g = Qt(t), dg = (Qt(t + h) - Qt(t - h)) / (2 * h); if (Math.Abs(dg) < 1e-300) break; t -= g / dg; }
                double hh = 1e-6 * Math.Max(1, f.R); double slope = (Qt(t + hh) - Qt(t - hh)) / (2 * hh);
                double scale = 4 * (f.R + f.Rm) * (f.R + f.Rm) * (f.R + f.Rm) * 4;       // порядок |∇Q|
                bool grazing = Math.Abs(slope) < 1e-7 * scale;
                yield return (t, p + dir * t, grazing);
            }
            yield break;
        }
        if (f.Cone)
        {
            // |w⊥|² = tg²α·(w·W)², w·W ≥ 0 (одна пола), w = p + t·d − O
            var w0 = p - f.O; double k2 = f.TanA * f.TanA;
            double wa = Vector3d.Dot(w0, f.W), da = Vector3d.Dot(dir, f.W);
            double Ak = dir.LengthSquared - da * da - k2 * da * da;
            double Bk = 2 * (Vector3d.Dot(w0, dir) - wa * da - k2 * wa * da);
            double Ck = w0.LengthSquared - wa * wa - k2 * wa * wa;
            if (Math.Abs(Ak) < 1e-15) { if (Math.Abs(Bk) > 1e-15) { double t = -Ck / Bk; if (wa + t * da >= 0) yield return (t, p + dir * t, false); } yield break; }
            double dk = Bk * Bk - 4 * Ak * Ck; if (dk < 0) yield break;
            double qk = Math.Sqrt(dk);
            if (qk < 1e-9 * Math.Max(1, Math.Abs(Bk))) { double t = -Bk / (2 * Ak); yield return (t, p + dir * t, true); yield break; }
            foreach (double t in new[] { (-Bk - qk) / (2 * Ak), (-Bk + qk) / (2 * Ak) })
                if (wa + t * da >= -1e-12) yield return (t, p + dir * t, false);
            yield break;
        }
        if (f.Sph)
        {
            var ws = p - f.O; double Bs = 2 * Vector3d.Dot(ws, dir), Cs = ws.LengthSquared - f.R * f.R, ds = Bs * Bs - 4 * Cs;
            if (ds < 0) yield break;
            double qs = Math.Sqrt(ds);
            if (qs < 1e-9 * Math.Max(1, Math.Abs(Bs))) { double t = -Bs / 2; yield return (t, p + dir * t, true); yield break; }
            foreach (double t in new[] { (-Bs - qs) / 2, (-Bs + qs) / 2 }) yield return (t, p + dir * t, false);
            yield break;
        }
        if (!f.Cyl)
        {
            double dn = Vector3d.Dot(dir, f.W);
            if (Math.Abs(dn) < 1e-9) { if (Math.Abs(Vector3d.Dot(p - f.O, f.W)) < Tol) yield return (1, p, true); yield break; }
            double t = Vector3d.Dot(f.O - p, f.W) / dn;
            yield return (t, p + dir * t, false);
            yield break;
        }
        // |(p + t·d − O)⊥|² = R²
        var w = p - f.O; var wp = w - f.W * Vector3d.Dot(w, f.W); var dp = dir - f.W * Vector3d.Dot(dir, f.W);
        double A = dp.LengthSquared, B = 2 * Vector3d.Dot(wp, dp), C = wp.LengthSquared - f.R * f.R;
        if (A < 1e-18) yield break;                                   // луч вдоль оси
        double disc = B * B - 4 * A * C;
        if (disc < 0) yield break;
        double sq = Math.Sqrt(disc);
        if (sq < 1e-9 * Math.Max(1, Math.Abs(B))) { double t = -B / (2 * A); yield return (t, p + dir * t, true); yield break; }   // касание
        foreach (double t in new[] { (-B - sq) / (2 * A), (-B + sq) / (2 * A) })
            yield return (t, p + dir * t, false);
    }
}

/// <summary>
/// ГРАНЬ В СВОИХ ПАРАМЕТРАХ (этап 2): поверхность (плоскость или цилиндр), рамка и точная граница в параметрах грани
/// (плоскость — 2D рамка; цилиндр — развёртка (θ, h)). Попадание точки в грань — чётностью; знаковое расстояние до
/// границы — для отсечения кривых пересечения гранью.
/// </summary>
public sealed partial class AnalyticFace
{
    public readonly TFace Face;
    public Ssi.Shape Shape = null!;
    public bool Cyl;
    /// <summary>
    /// СФЕРА (этап 2б): параметры — стереографическая проекция из полюса W (единичный, ВНЕ грани): O — центр, U, V — базис
    /// плоскости проекции. Окружности сферы переходят в окружности плоскости (граница точная); ветка плоских граней
    /// (чётность, обход графа) работает без изменений; расстояния — пересчётом по метрике 2|dq|/(1+|q|²)·R.
    /// FullSurface — граница только из швов (полная сфера): грань — вся поверхность.
    /// </summary>
    public bool Sph, FullSurface;
    /// <summary>
    /// КОНУС (этап 2б) — по ветке цилиндра (Cyl = true): развёртка (θ·R, h), h — вдоль оси от вершины O, радиус
    /// r(h) = h·tg α (R — опорный радиус грани, масштаб u); образующие — вертикали, окружности поперёк оси — горизонтали.
    /// </summary>
    public bool Cone; public double TanA;
    public double RadiusAt(double h) => Cone ? Math.Max(1e-12, h * TanA) : R;
    public Vector3d O, U, V, W;          // плоскость: начало, оси, нормаль; цилиндр: точка оси, базис ⟂ оси, ось
    /// <summary>
    /// g.05: ТОР — центр O, ось W, базис U, V; большой радиус R, малый Rm. Параметры: θ — вокруг оси, φ — вокруг трубки
    /// (0 — снаружи на экваторе, к +W — вверх). По умолчанию включено; P3D_ANALYTICTORUS=0 — тор многогранным путём.
    /// </summary>
    public bool Tor; public double Rm;
    internal readonly List<List<Vector3d>> TorBounds = new();
    internal Vector3d TorHint;
    internal static readonly bool AnalyticTorus = Environment.GetEnvironmentVariable("P3D_ANALYTICTORUS") != "0";
    public (double Th, double Ph) TorusAngles(Vector3d p)
    {
        var q = p - O; double z = Vector3d.Dot(q, W); var rv = q - W * z; double rho = rv.Length;
        double th = Math.Atan2(Vector3d.Dot(rv, V), Vector3d.Dot(rv, U));
        return (th, Math.Atan2(z, rho - R));
    }
    public Vector3d TorusPoint(double th, double ph)
    {
        var radial = U * Math.Cos(th) + V * Math.Sin(th);
        return O + radial * (R + Rm * Math.Cos(ph)) + W * (Rm * Math.Sin(ph));
    }
    public double R;
    internal readonly List<Seg> Bounds = new();

    /// <summary>
    /// KRN-06/KRN-05: ГРАНЬ ЛЕНТЫ КАТЯЩЕГОСЯ ШАРА (поверхность по формуле сечения, кусок u ∈ [BandU0, BandU1], v ∈ [0, 1]) —
    /// «пассивная» грань общей булевой: пересечение с другой гранью не строится (габариты врозь — пусто, иначе — отказ
    /// маршрута), а классификатор пересекает её лучом точно (затравка — треугольники грани, уточнение Ньютоном на самой
    /// поверхности), принадлежность точки — по (u, v). Так операции после скругления (отверстие в стороне от ленты) остаются
    /// точными. Мутация P3D_BANDBOOL=0 — прежний отказ («unrecognised surface»).
    /// </summary>
    public bool Band;
    internal Surfaces.SectionSurface? BandS;
    internal double BandU0, BandU1;
    internal List<(Vector3d A, Vector3d B, Vector3d C)>? BandTris;
    internal Vector3d BandLo, BandHi;
    internal static readonly bool BandBoolOff = Environment.GetEnvironmentVariable("P3D_BANDBOOL") == "0";
    internal static readonly bool ConeMetricOff = Environment.GetEnvironmentVariable("P3D_CONEMETRIC") == "0";
    double BandPer => BandS!.UMax - BandS.UMin;
    bool BandFull => Math.Abs((BandU1 - BandU0) - BandPer) < 1e-12 * Math.Max(1, BandPer);
    /// <summary>Сдвиг u от начала куска по периоду (у пояса — весь период).</summary>
    double BandDu(double u) { double per = BandPer, d = (u - BandU0) % per; if (d < 0) d += per; return d; }

    void InitBand()
    {
        var tris = new List<(Vector3d, Vector3d, Vector3d)>();
        if (Face.Surface is Surfaces.FacetedSurface fs && fs.Facets.Count > 0) tris.AddRange(fs.Facets.ReadView);
        else
        {
            var mb = new Tess.MeshBuilder(); BandS!.TessellateFace(Face, mb); var md = new Tess.MeshData(); mb.FillInto(md);
            for (int i = 0; i + 2 < md.Indices.Length; i += 3) tris.Add((mb.PositionsD[md.Indices[i]], mb.PositionsD[md.Indices[i + 1]], mb.PositionsD[md.Indices[i + 2]]));
        }
        BandTris = tris;
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue); double rmax = 0;
        for (int i = 0; i <= 128; i++)
        {
            double u = BandU0 + (BandU1 - BandU0) * i / 128.0; var sec = BandS!.SectionAt(u); rmax = Math.Max(rmax, sec.R);
            for (int k = 0; k <= 16; k++) { var q = sec.Point(k / 16.0); lo = Vector3d.ComponentMin(lo, q); hi = Vector3d.ComponentMax(hi, q); }
        }
        foreach (var (a, b, c) in tris) { lo = Vector3d.ComponentMin(lo, Vector3d.ComponentMin(a, Vector3d.ComponentMin(b, c))); hi = Vector3d.ComponentMax(hi, Vector3d.ComponentMax(a, Vector3d.ComponentMax(b, c))); }
        var m = new Vector3d(0.05 * rmax + 1e-6);                 // между пробами поверхность отходит меньше этого
        BandLo = lo - m; BandHi = hi + m;
    }

    /// <summary>
    /// Пересечения луча с поверхностью ленты (в пределах куска с запасом): затравки — треугольники грани (и их окрестность —
    /// поверхность отходит от хорд), уточнение Ньютоном по (t, u, v) на самой формуле; одинаковые корни сливаются.
    /// Касательный луч (|d·n| мал) — сомнительный.
    /// </summary>
    internal List<(double T, Vector3d Q, bool Grazing)> BandRayHits(Vector3d p, Vector3d dir)
    {
        var res = new List<(double T, Vector3d Q, bool Grazing)>();
        // габарит: луч мимо — пересечений нет
        {
            double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
            for (int k = 0; k < 3; k++)
            {
                double o = k == 0 ? p.X : k == 1 ? p.Y : p.Z, dd = k == 0 ? dir.X : k == 1 ? dir.Y : dir.Z;
                double lo = k == 0 ? BandLo.X : k == 1 ? BandLo.Y : BandLo.Z, hi = k == 0 ? BandHi.X : k == 1 ? BandHi.Y : BandHi.Z;
                if (Math.Abs(dd) < 1e-300) { if (o < lo || o > hi) return res; continue; }
                double a = (lo - o) / dd, b = (hi - o) / dd; if (a > b) (a, b) = (b, a);
                t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
            }
            if (t0 > t1) return res;
        }
        var S = BandS!; double hU = 1e-7 * Math.Max(1, BandPer), hV = 1e-7;
        foreach (var (a, b, c) in BandTris!)
        {
            var e1 = b - a; var e2 = c - a; var pv = Vector3d.Cross(dir, e2); double det = Vector3d.Dot(e1, pv);
            if (Math.Abs(det) < 1e-18) continue;
            var sv = p - a; double bu = Vector3d.Dot(sv, pv) / det; if (bu < -0.25 || bu > 1.25) continue;
            var qv = Vector3d.Cross(sv, e1); double bv = Vector3d.Dot(dir, qv) / det; if (bv < -0.25 || bu + bv > 1.25) continue;
            double tt = Vector3d.Dot(e2, qv) / det;
            var uv = S.ProjectToUV(p + dir * tt); double u = uv.X, v = uv.Y; bool conv = false;
            for (int it = 0; it < 40; it++)
            {
                var X = S.PointAt(new Vector2d(u, v)); var G = p + dir * tt - X;
                if (G.Length < 1e-12) { conv = true; break; }
                var Su = (S.PointAt(new Vector2d(u + hU, v)) - S.PointAt(new Vector2d(u - hU, v))) / (2 * hU);
                var Sv = (S.PointAt(new Vector2d(u, v + hV)) - S.PointAt(new Vector2d(u, v - hV))) / (2 * hV);
                // dir·Δt − Su·Δu − Sv·Δv = −G
                var dx = Cols3(dir, -Su, -Sv, -G);
                if (dx == null) break;
                tt += dx.Value.X; u += dx.Value.Y; v += dx.Value.Z;
                if (dx.Value.Length < 1e-15) { conv = (p + dir * tt - S.PointAt(new Vector2d(u, v))).Length < 1e-9; break; }
            }
            if (!conv) continue;
            if (res.Any(x => Math.Abs(x.T - tt) < 1e-8)) continue;
            var q = p + dir * tt; var n = S.NormalAt(new Vector2d(u, v));
            bool grazing = Math.Abs(Vector3d.Dot(n, dir)) < 1e-4;
            // за пределами куска (с запасом допуска) — не пересечение грани (Contains решит у края)
            res.Add((tt, q, grazing));
        }
        return res;
    }

    /// <summary>Решение [c1 c2 c3]·x = r (столбцы) по Крамеру; null — вырождено.</summary>
    static Vector3d? Cols3(Vector3d c1, Vector3d c2, Vector3d c3, Vector3d r)
    {
        double det = Vector3d.Dot(c1, Vector3d.Cross(c2, c3));
        if (Math.Abs(det) < 1e-30) return null;
        return new Vector3d(Vector3d.Dot(r, Vector3d.Cross(c2, c3)), Vector3d.Dot(c1, Vector3d.Cross(r, c3)), Vector3d.Dot(c1, Vector3d.Cross(c2, r))) / det;
    }

    /// <summary>Расстояние (мм) от точки ленты до края куска; знак — плюс внутри.</summary>
    double BandSignedBoundary(Vector3d q)
    {
        var uv = BandS!.ProjectToUV(q); double u = uv.X, v = uv.Y;
        var sec = BandS.SectionAt(u); double arc = sec.R * sec.Phi;
        double dv = Math.Min(v, 1 - v) * arc;
        if (BandFull) return dv;
        double h = 1e-6 * Math.Max(1, BandPer);
        double speed = ((BandS.PointAt(new Vector2d(u + h, v)) - BandS.PointAt(new Vector2d(u - h, v))) / (2 * h)).Length;
        double du = BandDu(u), len = BandU1 - BandU0;
        double dur = du <= len ? Math.Min(du, len - du) * speed : -Math.Min(du - len, BandPer - du) * speed;
        return dv < 0 || dur < 0 ? Math.Min(dv, dur) : Math.Min(dv, dur);
    }
    public static double Tol => Classifier.Tol;
    /// <summary>KRN-03 замер: сколько раз знаменатель стереографической карты ограничивался (точка у полюса).</summary>
    public static long PoleClampHits;
    internal static readonly bool PoleFromBRep = Environment.GetEnvironmentVariable("P3D_SPHPOLE") != "facets";
    internal static readonly bool SphStat = Environment.GetEnvironmentVariable("P3D_SPHSTAT") == "1";

    /// <summary>Кусок границы в параметрах грани: отрезок, дуга окружности (плоская грань), горизонталь или вертикаль цилиндра.</summary>
    internal readonly record struct Seg(int Kind, Vector2d A, Vector2d B, Vector2d C, double R, double T0, double Sweep)
    {
        public const int Line = 0, Arc = 1, Horizontal = 2, Vertical = 3;
    }

    /// <summary>null — поверхность грани не плоскость и не цилиндр (why).</summary>
    public static AnalyticFace? Of(TFace f, out string? why)
    {
        if (!NurbsBoolOff && GeometryAudit.AuthoritativeSurface(f) is Surfaces.NurbsSurface nsf) return OfNurbs(f, nsf, out why);
        if (!BandBoolOff && GeometryAudit.AuthoritativeSurface(f) is Surfaces.SectionSurface bs && bs.IsProcedural && Topology.AnalyticVolume.BandRange(f, bs) is { } brg)
        {
            var fb = new AnalyticFace(f) { Band = true, BandS = bs, BandU0 = brg.Ua, BandU1 = brg.Ub };
            fb.InitBand();
            why = null; return fb;
        }
        var sh = ExactEdges.ShapeOf(GeometryAudit.AuthoritativeSurface(f));
        var fd = new AnalyticFace(f) { Shape = sh! };
        switch (sh)
        {
            case Ssi.Plane pl:
                {
                    var n = pl.N.Normalized();
                    var u0 = Math.Abs(n.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
                    fd.O = pl.P; fd.W = n; fd.U = (u0 - n * Vector3d.Dot(u0, n)).Normalized(); fd.V = Vector3d.Cross(n, fd.U);
                    break;
                }
            case Ssi.Cylinder cy:
                {
                    var a = cy.Axis.Normalized();
                    var u0 = Math.Abs(a.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
                    fd.Cyl = true; fd.O = cy.P; fd.W = a; fd.U = (u0 - a * Vector3d.Dot(u0, a)).Normalized(); fd.V = Vector3d.Cross(a, fd.U); fd.R = cy.R;
                    break;
                }
            case Ssi.Cone kn:
                {
                    var a = kn.Axis.Normalized();
                    var u0 = Math.Abs(a.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
                    fd.Cyl = true; fd.Cone = true; fd.O = kn.Apex; fd.W = a; fd.TanA = Math.Tan(kn.HalfAngle);
                    fd.U = (u0 - a * Vector3d.Dot(u0, a)).Normalized(); fd.V = Vector3d.Cross(a, fd.U);
                    var hs = new List<double>(); var pts0 = new List<Vector3d>();
                    foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) ce.Tessellate(pts0, true);
                    foreach (var p in pts0) hs.Add(Vector3d.Dot(p - kn.Apex, a));
                    fd.R = hs.Count > 0 ? Math.Max(1e-6, hs.Average() * fd.TanA) : 1;
                    break;
                }
            case Ssi.Sphere sp:
                {
                    fd.Sph = true; fd.O = sp.C; fd.R = sp.R;
                    // KRN-03: полюс проекции — по B-rep (SurfaceDomain: вне грани, наиболее удалён от рёбер, без фасетов); фасеты
                    // и сырая развёртка — только у полной сферы (рёбер нет). P3D_SPHPOLE=facets — прежний выбор (мутация).
                    if (PoleFromBRep && SurfaceDomain.SpherePoleOutside(f, sp.C, sp.R) is { } outside)
                    {
                        fd.W = outside;
                        var uo = Math.Abs(outside.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
                        fd.U = (uo - outside * Vector3d.Dot(uo, outside)).Normalized(); fd.V = Vector3d.Cross(outside, fd.U);
                        break;
                    }
                    // полюс — направление, наиболее удалённое от точек грани (треугольники готовой грани или сырая развёртка)
                    var pts = new List<Vector3d>();
                    if (f.Surface is Surfaces.FacetedSurface ffs && ffs.Facets.Count > 0) foreach (var (a, b, c) in ffs.Facets.ReadView) { pts.Add(a); pts.Add(b); pts.Add(c); }
                    else { try { var mb = new Tess.MeshBuilder(); GeometryAudit.AuthoritativeSurface(f)!.TessellateFace(f, mb); pts.AddRange(mb.PositionsD); } catch { } }
                    foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) ce.Tessellate(pts, true);
                    var dirs = pts.Select(p => (p - sp.C).Normalized()).ToList();
                    Vector3d best = Vector3d.UnitZ; double bestD = -2;
                    int N = 400; double ga = Math.PI * (3 - Math.Sqrt(5));
                    for (int i = 0; i < N; i++)
                    {
                        double z = 1 - 2 * (i + 0.5) / N, r = Math.Sqrt(1 - z * z); var cand = new Vector3d(r * Math.Cos(ga * i), r * Math.Sin(ga * i), z);
                        double near = dirs.Count == 0 ? 2 : dirs.Max(d => Vector3d.Dot(d, cand));      // косинус до ближайшей точки грани
                        if (-near > bestD) { bestD = -near; best = cand; }
                    }
                    fd.W = best;
                    if (SphStat)
                    {
                        // KRN-03 замер: запас полюса до точек грани (угол) и наибольший множитель карты на границе грани
                        double minAng = dirs.Count == 0 ? Math.PI : dirs.Min(d => Math.Acos(Math.Clamp(Vector3d.Dot(d, best), -1, 1)));
                        var bp = new List<Vector3d>(); foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) ce.Tessellate(bp, true);
                        double minB = bp.Count == 0 ? Math.PI : bp.Min(p => Math.Acos(Math.Clamp(Vector3d.Dot((p - sp.C).Normalized(), best), -1, 1)));
                        Console.Error.WriteLine($"SPHPOLE face {f.Id} pts {dirs.Count} facets {(f.Surface is Surfaces.FacetedSurface fz ? fz.Facets.Count : -1)} minAngAll {minAng:0.####} minAngBoundary {minB:0.####} maxScale {(1 + Math.Pow(Math.Tan((Math.PI - minB) / 2), 2)) / 2:G4}");
                    }
                    var u0 = Math.Abs(best.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
                    fd.U = (u0 - best * Vector3d.Dot(u0, best)).Normalized(); fd.V = Vector3d.Cross(best, fd.U);
                    break;
                }
            case Ssi.Torus to when AnalyticTorus:
                {
                    var a = to.Axis.Normalized();
                    var u0 = Math.Abs(a.X) < 0.9 ? Vector3d.UnitX : Vector3d.UnitY;
                    fd.Tor = true; fd.O = to.C; fd.W = a; fd.U = (u0 - a * Vector3d.Dot(u0, a)).Normalized(); fd.V = Vector3d.Cross(a, fd.U);
                    fd.R = to.R; fd.Rm = to.r;
                    break;
                }
            default:
                why = $"face {f.Id}: {(sh == null ? "unrecognised surface" : sh.GetType().Name)}";
                return null;
        }
        // ШОВ — ребро, встречающееся в грани дважды (боковая грань сырого цилиндра): не граница, а место внутри поверхности;
        // как граница он рвал кривые пересечения на куски (вал × поперечное отверстие — 3 разомкнутых куска вместо 2).
        var uses = new Dictionary<TEdge, int>(ReferenceEqualityComparer.Instance);
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) uses[ce.Edge] = uses.GetValueOrDefault(ce.Edge) + 1;
        if (fd.Tor)
        {
            // ТОР: граница — ломаными в пространстве (кривые грани), принадлежность — чётностью пересечений пути от точки
            // грани (InteriorHint) в развёртке (θ, φ). Граница грани разделяет поверхность, поэтому чётность не зависит от
            // пути. Целый тор (только швы) — вся поверхность.
            foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
            {
                if (uses[ce.Edge] != 1) continue;
                var pts = new List<Vector3d>(); var c = ce.Edge.Curve; int m = 512;
                for (int i = 0; i <= m; i++) pts.Add(c.PointAt(c.T0 + (c.T1 - c.T0) * i / m));
                fd.TorBounds.Add(pts);
            }
            fd.FullSurface = fd.TorBounds.Count == 0;
            if (!fd.FullSurface)
            {
                if (f.InteriorHint is not { } hint || fd.SurfaceDistance(hint) > 1e-6 * Math.Max(1, fd.R)) { why = $"face {f.Id}: torus face without an interior point"; return null; }
                fd.TorHint = fd.ProjectTo(hint);
            }
            why = null;
            return fd;
        }
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges)) if (uses[ce.Edge] == 1) fd.AddBounds(ce);
        fd.FullSurface = fd.Sph && fd.Bounds.Count == 0;
        why = null;
        return fd;
    }

    AnalyticFace(TFace f) { Face = f; }
    static AnalyticFace() { if (SphStat) AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Error.WriteLine($"SPHCLAMP {PoleClampHits}"); }

    public Vector2d PlaneUV(Vector3d p)
    {
        if (Param) { var uv = ParamS!.ProjectToUV(p); return new(uv.X * PSpeedU / 1.5, uv.Y * PSpeedV / 1.5); }
        if (Sph)
        {
            var u = (p - O) / R; double den = 1 - Vector3d.Dot(u, W);
            // Полюс проекции по построению ВНЕ грани (SurfaceDomain.SpherePoleOutside); точка у самого полюса — «бесконечность»
            // плоскости проекции: далёкая точка, вне любой границы грани. Замер корпуса: таких точек 0 (PoleClampHits).
            if (den < 1e-15) { System.Threading.Interlocked.Increment(ref PoleClampHits); den = 1e-15; }
            return new(Vector3d.Dot(u, U) / den, Vector3d.Dot(u, V) / den);
        }
        var q = p - O; return new(Vector3d.Dot(q, U), Vector3d.Dot(q, V));
    }

    /// <summary>Точка поверхности по параметрам (плоскость — (u, v); сфера — обратная стереографическая проекция).</summary>
    public Vector3d FromPlaneUV(Vector2d q)
    {
        if (Param) return ParamS!.PointAt(new Vector2d(q.X / (PSpeedU / 1.5), q.Y / (PSpeedV / 1.5)));
        if (!Sph) return O + U * q.X + V * q.Y;
        double q2 = q.LengthSquared; var w = U * q.X + V * q.Y;
        var u = (w * 2 + W * (q2 - 1)) / (q2 + 1);
        return O + u * R;
    }

    /// <summary>Множитель «мм на единицу параметра» в точке (сфера — метрика стереографической проекции).</summary>
    public double Scale(Vector2d q) => Sph ? 2 * R / (1 + q.LengthSquared) : 1;
    public double Theta(Vector3d p) { var q = p - O; return Math.Atan2(Vector3d.Dot(q, V), Vector3d.Dot(q, U)); }
    public double H(Vector3d p) => Vector3d.Dot(p - O, W);

    void AddBounds(TCoEdge ce)
    {
        var f = this;
        var c = ce.Edge.Curve;
        var a = ce.Edge.V0.P; var b = ce.Edge.V1.P;
        if (Sph && c is ArcCurve3 sarc)
        {
            // окружность сферы в проекции — окружность (по трём точкам) или прямая (через полюс)
            var pa = sarc.PointAt(sarc.T0); var pm = sarc.PointAt((sarc.T0 + sarc.T1) / 2); var pb = sarc.PointAt(sarc.T1);
            var pq = sarc.PointAt(sarc.T0 + (sarc.T1 - sarc.T0) / 4);
            Vector2d A2 = PlaneUV(pa), M2 = PlaneUV(pm), B2 = PlaneUV(pb), Q2 = PlaneUV(pq);
            bool closedS = (pa - pb).Length < 1e-12;
            var P3 = closedS ? (A2, Q2, M2) : (A2, M2, B2);
            double ax = P3.Item1.X, ay = P3.Item1.Y, bx = P3.Item2.X, by = P3.Item2.Y, cx = P3.Item3.X, cy = P3.Item3.Y;
            double dd = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(dd) > 1e-12 * Math.Max(1, (P3.Item1 - P3.Item3).LengthSquared))
            {
                double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
                var cc = new Vector2d((a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / dd, (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / dd);
                double rr = (A2 - cc).Length;
                double t0 = Ang(A2 - cc), tm = Ang((closedS ? Q2 : M2) - cc), t1 = Ang(B2 - cc);
                double sweep = closedS ? 2 * Math.PI : (Wrap(tm - t0) < Wrap(t1 - t0) ? Wrap(t1 - t0) : Wrap(t1 - t0) - 2 * Math.PI);
                Bounds.Add(new Seg(Seg.Arc, A2, B2, cc, rr, t0, sweep));
                return;
            }
        }
        if (!f.Cyl)
        {
            if (c is LineCurve3) { Bounds.Add(new Seg(Seg.Line, PlaneUV(a), PlaneUV(b), default, 0, 0, 0)); return; }
            if (!Sph && c is ArcCurve3 arc && Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), f.W)) - 1) < 1e-9)
            {
                var cc = PlaneUV(arc.Frame.Origin);
                var pa = arc.PointAt(arc.T0); var pm = arc.PointAt((arc.T0 + arc.T1) / 2); var pb = arc.PointAt(arc.T1);
                double t0 = Ang(PlaneUV(pa) - cc), tm = Ang(PlaneUV(pm) - cc), t1 = Ang(PlaneUV(pb) - cc);
                bool closed = (pa - pb).Length < 1e-12;
                double sweep = closed ? 2 * Math.PI * Math.Sign(Wrap(tm - t0) < Math.PI ? 1 : -1)
                                      : (Wrap(tm - t0) < Wrap(t1 - t0) ? Wrap(t1 - t0) : Wrap(t1 - t0) - 2 * Math.PI);
                Bounds.Add(new Seg(Seg.Arc, PlaneUV(pa), PlaneUV(pb), cc, arc.Radius, t0, sweep));
                return;
            }
        }
        else
        {
            if (c is LineCurve3)
            {
                double ta = Theta(a), tb = Theta(b);
                if (Math.Abs(Wrap(ta - tb + Math.PI) - Math.PI) < 1e-9)   // вертикаль (образующая)
                { Bounds.Add(new Seg(Seg.Vertical, new(ta, H(a)), new(ta, H(b)), default, 0, 0, 0)); return; }
            }
            if (c is ArcCurve3 arc && Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), f.W)) - 1) < 1e-9
                && Math.Abs(arc.Radius - f.RadiusAt(H(arc.Frame.Origin))) < 1e-7 && (arc.Frame.Origin - f.O - f.W * H(arc.Frame.Origin)).Length < 1e-7)
            {
                // горизонтальная окружность цилиндра: h постоянна, θ — от начала до конца в сторону обхода дуги
                var pa = arc.PointAt(arc.T0); var pm = arc.PointAt((arc.T0 + arc.T1) / 2); var pb = arc.PointAt(arc.T1);
                double t0 = Theta(pa), tm = Theta(pm), t1 = Theta(pb);
                bool closed = (pa - pb).Length < 1e-12;
                double sweep = closed ? 2 * Math.PI : (Wrap(tm - t0) < Wrap(t1 - t0) ? Wrap(t1 - t0) : Wrap(t1 - t0) - 2 * Math.PI);
                Bounds.Add(new Seg(Seg.Horizontal, new(t0, H(pa)), new(t0 + sweep, H(pa)), default, 0, t0, sweep));
                return;
            }
        }
        // прочие кривые — мелкой ломаной
        int m = 512;
        Vector3d prev = c.PointAt(c.T0);
        for (int i = 1; i <= m; i++)
        {
            var q = c.PointAt(c.T0 + (c.T1 - c.T0) * i / m);
            if (!f.Cyl) Bounds.Add(new Seg(Seg.Line, PlaneUV(prev), PlaneUV(q), default, 0, 0, 0));
            else
            {
                double t0 = Theta(prev), t1 = t0 + (Wrap(Theta(q) - t0 + Math.PI) - Math.PI);
                // сырые углы концов (atan2, −π → π) — в T0 и Sweep, R = 1 — признак: соседние отрезки делят их побитно
                Bounds.Add(new Seg(Seg.Line, new(t0, H(prev)), new(t1, H(q)), default, 1, Canon(Theta(prev)), Canon(Theta(q))));
            }
            prev = q;
        }
    }


    /// <summary>Точка внутри грани (в параметрах); null — сомнительно (ближе допуска к границе).</summary>
    /// <summary>Точка (на поверхности) внутри грани; null — сомнительно (ближе допуска к границе).</summary>
    public bool? Contains(Vector3d q) => Contains(q, Tol);

    /// <summary>То же с заданным допуском у границы (0 — без допуска: точная чётность пересечений).</summary>
    public bool? Contains(Vector3d q, double tol)
    {
        var f = this;
        if (Band) { double sd = BandSignedBoundary(q); return Math.Abs(sd) < tol ? null : sd > 0; }
        if (Param) { double sd = ParamSignedBoundary(q); return Math.Abs(sd) < tol ? null : sd > 0; }
        if (FullSurface) return true;
        if (Tor) return TorusContains(q);
        int crossings = 0;
        if (!f.Cyl)
        {
            var p = PlaneUV(q);
            // луч в 2D под общим углом
            var dir = new Vector2d(Math.Cos(0.613), Math.Sin(0.613));
            foreach (var s in f.Bounds)
            {
                if (DistToSeg(s, p) * Scale(p) < tol) return null;
                if (s.Kind == Seg.Line)
                {
                    var e = s.B - s.A; double den = dir.X * e.Y - dir.Y * e.X; if (Math.Abs(den) < 1e-300) continue;
                    var w = s.A - p; double t = (w.X * e.Y - w.Y * e.X) / den, u = (w.X * dir.Y - w.Y * dir.X) / den;
                    if (t > 0 && u >= 0 && u < 1) crossings++;
                }
                else
                {
                    var fo = p - s.C; double bq = Vector2d.Dot(fo, dir), cq = fo.LengthSquared - s.R * s.R, disc = bq * bq - cq;
                    if (disc <= 0) continue; double sq = Math.Sqrt(disc);
                    foreach (double t in new[] { -bq - sq, -bq + sq })
                    {
                        if (t <= 0) continue;
                        var x = p + dir * t; double ang = Ang(x - s.C);
                        double d = (ang - s.T0) * Math.Sign(s.Sweep); d = Wrap(d);
                        double uu = d / Math.Abs(s.Sweep);
                        if (Math.Abs(Math.Abs(s.Sweep) - 2 * Math.PI) < 1e-12 || (uu >= 0 && uu < 1)) crossings++;
                    }
                }
            }
            return (crossings & 1) == 1;
        }
        // цилиндр: луч вдоль образующей (+h) в развёртке
        double th = Theta(q), h = H(q); double Rq = f.RadiusAt(h);
        double tolH = Cone && !ConeMetricOff ? tol / Math.Sqrt(1 + TanA * TanA) : tol;   // допуск по h: мм вдоль образующей → h
        foreach (var s in f.Bounds)
        {
            switch (s.Kind)
            {
                case Seg.Vertical:
                    {
                        double dth = Math.Abs(Wrap(th - s.A.X + Math.PI) - Math.PI) * Rq;
                        double lo = Math.Min(s.A.Y, s.B.Y), hi = Math.Max(s.A.Y, s.B.Y);
                        if (dth < tol && h >= lo - tolH && h <= hi + tolH) return null;     // на вертикали
                        if (dth < tol && h < lo) return null;                             // луч идёт вдоль вертикали
                        break;
                    }
                case Seg.Horizontal:
                    {
                        // ПОЛУОТКРЫТЫЙ интервал по θ, приведённый к возрастанию: [начало, начало + длина) — соседние куски
                        // одной окружности делят θ без пропусков и двойного счёта (стык полудуг — не угол границы; прежде
                        // «у конца дуги» здесь давало «сомнительно» и рвало кривые пересечения на куски).
                        double len = Math.Abs(s.Sweep);
                        bool full = Math.Abs(len - 2 * Math.PI) < 1e-12;
                        double lo = s.Sweep >= 0 ? s.T0 : s.T0 + s.Sweep;
                        bool within = full || Wrap(th - lo) < len;
                        if (!within) break;
                        if (Math.Abs(s.A.Y - h) < tolH) return null;                      // на горизонтали
                        if (s.A.Y > h) crossings++;
                        break;
                    }
                default:
                    {
                        if (s.R == 1 && !RayExactOff)
                        {
                            // g.05/g.13: ПРИНАДЛЕЖНОСТЬ θ ОТРЕЗКУ — ПО СЫРЫМ УГЛАМ КОНЦОВ, без сдвигов на 2π в плавающей точке.
                            // Соседние отрезки ломаной делят конец побитно, интервал полуоткрыт в сторону роста угла — каждый θ
                            // ровно в одном отрезке. Прежде концы сдвигались на 2π с округлением: луч точки ровно на шве
                            // развёртки (θ = π) проходил через вершину кромки и считался дважды или ни разу в зависимости от
                            // знака 1e-16 в координате точки кривой (KRN04-REFINE-AT-CLOSED-EDGE-SEAM). Мутация P3D_CYLRAYEXACT=0.
                            double r0 = s.T0, r1 = s.Sweep, tq = Canon(th);
                            if (r0 == r1) break;
                            double mn = Math.Min(r0, r1), mx = Math.Max(r0, r1);
                            bool wraps = mx - mn > Math.PI;
                            if (!(wraps ? (tq >= mx || tq < mn) : (tq >= mn && tq < mx))) break;
                            double a0x = s.A.X, a1x = s.B.X, lo0 = Math.Min(a0x, a1x);
                            double tt0 = th; while (tt0 < lo0) tt0 += 2 * Math.PI; while (tt0 - 2 * Math.PI >= lo0) tt0 -= 2 * Math.PI;
                            double u0 = Math.Abs(a1x - a0x) < 1e-300 ? 0 : Math.Clamp((tt0 - a0x) / (a1x - a0x), 0, 1);
                            double hh0 = s.A.Y + (s.B.Y - s.A.Y) * u0;
                            if (Math.Abs(hh0 - h) < tolH) return null;
                            if (hh0 > h) crossings++;
                            break;
                        }
                        // отрезок ломаной в развёртке (θ — непрерывно вдоль отрезка): θ точки — со сдвигом на 2πk
                        double a0 = s.A.X, a1 = s.B.X; double lo = Math.Min(a0, a1), hiA = Math.Max(a0, a1);
                        double tt = th; while (tt < lo) tt += 2 * Math.PI; while (tt - 2 * Math.PI >= lo) tt -= 2 * Math.PI;
                        if (tt >= hiA || Math.Abs(a1 - a0) < 1e-15) break;
                        double u = (tt - a0) / (a1 - a0); double hh = s.A.Y + (s.B.Y - s.A.Y) * u;
                        if (Math.Abs(hh - h) < tolH) return null;
                        if (hh > h) crossings++;
                        break;
                    }
            }
        }
        return (crossings & 1) == 1;
    }

    /// <summary>
    /// Тор: путь от точки грани h до q в развёртке (θ, φ) — по кратчайшему подъёму (|Δθ|, |Δφ| ≤ π); пересечения с ломаными
    /// границы (все сдвиги на 2π по обоим параметрам) считаются; чётное — внутри. Близко к границе — null.
    /// </summary>
    bool? TorusContains(Vector3d q)
    {
        if (BoundaryDistance(q) < Tol) return null;
        var (th0, ph0) = TorusAngles(TorHint); var (th1, ph1) = TorusAngles(q);
        double dth = WrapPi(th1 - th0), dph = WrapPi(ph1 - ph0);
        var A = new Vector2d(th0, ph0); var B = new Vector2d(th0 + dth, ph0 + dph);
        int crossings = 0;
        foreach (var poly in TorBounds)
        {
            var (pa, pb) = TorusAngles(poly[0]); var prev = new Vector2d(pa, pb);
            for (int i = 1; i < poly.Count; i++)
            {
                var (ta, tb) = TorusAngles(poly[i]);
                var cur = new Vector2d(prev.X + WrapPi(ta - prev.X), prev.Y + WrapPi(tb - prev.Y));
                // сдвиг отрезка границы к пути: опорная точка — ближе к середине пути
                var mid = (A + B) / 2; var segMid = (prev + cur) / 2;
                double kx = Math.Round((mid.X - segMid.X) / (2 * Math.PI)), ky = Math.Round((mid.Y - segMid.Y) / (2 * Math.PI));
                for (int sx = -1; sx <= 1; sx++)
                    for (int sy = -1; sy <= 1; sy++)
                    {
                        var sh = new Vector2d((kx + sx) * 2 * Math.PI, (ky + sy) * 2 * Math.PI);
                        var c0 = prev + sh; var c1 = cur + sh;
                        var r = B - A; var e = c1 - c0; double den = r.X * e.Y - r.Y * e.X;
                        if (Math.Abs(den) < 1e-300) continue;
                        var w = c0 - A; double t = (w.X * e.Y - w.Y * e.X) / den, u = (w.X * r.Y - w.Y * r.X) / den;
                        if (t > 0 && t <= 1 && u >= 0 && u < 1) crossings++;
                    }
                prev = cur;
            }
        }
        return (crossings & 1) == 0;
    }
    static double WrapPi(double a) { a = Math.IEEERemainder(a, 2 * Math.PI); return a; }

    double DistToSeg(Seg s, Vector2d p)
    {
        var f = this;
        if (s.Kind == Seg.Line)
        {
            var e = s.B - s.A; double l2 = e.LengthSquared; double t = l2 > 0 ? Math.Clamp(Vector2d.Dot(p - s.A, e) / l2, 0, 1) : 0;
            return (s.A + e * t - p).Length;
        }
        // дуга плоской грани: расстояние до окружности, если угол на дуге, иначе до концов
        double ang = Ang(p - s.C); double d = Wrap((ang - s.T0) * Math.Sign(s.Sweep));
        bool on = Math.Abs(Math.Abs(s.Sweep) - 2 * Math.PI) < 1e-12 || d <= Math.Abs(s.Sweep);
        double dc = on ? Math.Abs((p - s.C).Length - s.R) : double.MaxValue;
        return Math.Min(dc, Math.Min((p - s.A).Length, (p - s.B).Length));
    }


    /// <summary>Расстояние (мм) от точки на поверхности до границы грани в параметрах; знак — плюс внутри, минус снаружи.</summary>
    public double SignedBoundaryDistance(Vector3d q)
    {
        double d = BoundaryDistance(q);
        if (d < Tol) return 0;
        return Contains(q) == true ? d : -d;
    }

    /// <summary>Беззнаковое расстояние (мм) от точки на поверхности до границы грани в параметрах (без допуска).</summary>
    public double BoundaryDistance(Vector3d q)
    {
        double d = double.MaxValue;
        if (Band) return Math.Abs(BandSignedBoundary(q));
        if (Param) return Math.Abs(ParamSignedBoundary(q));
        if (FullSurface) return double.MaxValue;
        if (Tor)
        {
            foreach (var poly in TorBounds)
                for (int i = 0; i + 1 < poly.Count; i++)
                {
                    var a = poly[i]; var e = poly[i + 1] - a; double l2 = e.LengthSquared;
                    double t = l2 > 0 ? Math.Clamp(Vector3d.Dot(q - a, e) / l2, 0, 1) : 0;
                    d = Math.Min(d, (a + e * t - q).Length);
                }
            return d;
        }
        if (!Cyl) { var p = PlaneUV(q); foreach (var s in Bounds) d = Math.Min(d, DistToSeg(s, p)); if (Sph) d *= Scale(p); }
        else
        {
            double th = Theta(q), h = H(q); double Rh = RadiusAt(h);
            // KRN-02 (02.5), KRN-03 (03.9): МЕТРИКА КОНУСА. Вдоль образующей длина — Δh / cos α, а не Δh: расстояние до
            // окружности-края грани на конусе с полууглом 60° выходило вдвое меньше настоящего, и точка в 1.5 допуска от края
            // считалась «на границе». У цилиндра множитель 1. Мутация P3D_CONEMETRIC=0.
            double ks = Cone && !ConeMetricOff ? Math.Sqrt(1 + TanA * TanA) : 1;
            foreach (var s in Bounds)
            {
                switch (s.Kind)
                {
                    case Seg.Vertical:
                        {
                            double dth = Math.Abs(Wrap(th - s.A.X + Math.PI) - Math.PI) * Rh;
                            double lo = Math.Min(s.A.Y, s.B.Y), hi = Math.Max(s.A.Y, s.B.Y);
                            double dh = (h < lo ? lo - h : h > hi ? h - hi : 0) * ks;
                            d = Math.Min(d, Math.Sqrt(dth * dth + dh * dh)); break;
                        }
                    case Seg.Horizontal:
                        {
                            double dd = Wrap((th - s.T0) * Math.Sign(s.Sweep)), len = Math.Abs(s.Sweep);
                            double dth = (Math.Abs(len - 2 * Math.PI) < 1e-12 || dd <= len) ? 0 : Math.Min(dd - len, 2 * Math.PI - dd) * Rh;
                            double dh = (h - s.A.Y) * ks;
                            d = Math.Min(d, Math.Sqrt(dth * dth + dh * dh)); break;
                        }
                    default:
                        {
                            // отрезок ломаной в развёртке: θ точки приводится к ближайшему к отрезку обороту
                            double mid = (s.A.X + s.B.X) / 2, tt = th + 2 * Math.PI * Math.Round((mid - th) / (2 * Math.PI));
                            var a = new Vector2d(s.A.X * Rh, s.A.Y * ks); var b = new Vector2d(s.B.X * Rh, s.B.Y * ks); var p = new Vector2d(tt * Rh, h * ks);
                            var e = b - a; double l2 = e.LengthSquared; double t = l2 > 0 ? Math.Clamp(Vector2d.Dot(p - a, e) / l2, 0, 1) : 0;
                            d = Math.Min(d, (a + e * t - p).Length); break;
                        }
                }
            }
        }
        return d;
    }

    public double SurfaceDistance(Vector3d p)
    {
        if (Band) return (BandS!.PointAt(BandS.ProjectToUV(p)) - p).Length;
        if (Param) return (ParamS!.PointAt(ParamS.ProjectToUV(p)) - p).Length;
        if (Sph) return Math.Abs((p - O).Length - R);
        if (Tor)
        {
            var qt = p - O; double zt = Vector3d.Dot(qt, W); double rho = (qt - W * zt).Length;
            return Math.Abs(Math.Sqrt((rho - R) * (rho - R) + zt * zt) - Rm);
        }
        if (Cone)
        {
            var w = p - O; double hc = Vector3d.Dot(w, W), rho = (w - W * hc).Length;
            double ca = 1 / Math.Sqrt(1 + TanA * TanA), sa = TanA * ca;
            return Math.Abs(rho * ca - hc * sa);
        }
        if (!Cyl) return Math.Abs(Vector3d.Dot(p - O, W));
        var q = p - O; q -= W * Vector3d.Dot(q, W);
        return Math.Abs(q.Length - R);
    }

    public Vector3d ProjectTo(Vector3d p)
    {
        if (Band) return BandS!.PointAt(BandS.ProjectToUV(p));
        if (Param) return ParamS!.PointAt(ParamS.ProjectToUV(p));
        if (Sph) { var d = p - O; return d.Length < 1e-300 ? O + W * R : O + d * (R / d.Length); }
        if (Tor) { var (th, ph) = TorusAngles(p); return TorusPoint(th, ph); }
        if (Cone)
        {
            var w = p - O; double hc = Vector3d.Dot(w, W); var rv = w - W * hc; double rho = rv.Length;
            var gen = (W + (rho > 1e-300 ? rv / rho : U) * TanA).Normalized();      // образующая через точку
            double t = Math.Max(0, Vector3d.Dot(w, gen));
            return O + gen * t;
        }
        if (!Cyl) return p - W * Vector3d.Dot(p - O, W);
        var q = p - O; double h = Vector3d.Dot(q, W); var r = q - W * h;
        return r.Length < 1e-300 ? p : O + W * h + r * (R / r.Length);
    }

    static double Ang(Vector2d v) => Math.Atan2(v.Y, v.X);
    static double Wrap(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }
    static double Canon(double a) => a == -Math.PI ? Math.PI : a;
    static readonly bool RayExactOff = Environment.GetEnvironmentVariable("P3D_CYLRAYEXACT") == "0";
}
