using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Surfaces;

namespace Parametric3D.Kernel.Topology;

/// <summary>
/// K08. ОБЪЁМ ПО АВТОРИТЕТНОЙ ГЕОМЕТРИИ, БЕЗ СЕТКИ.
///
/// V = ⅓ Σ ∫∫_грань x·n dA (теорема о дивергенции для поля x/3). Каждый поверхностный интеграл
/// переводится формулой Стокса в интеграл по краю грани — по её НАСТОЯЩИМ кривым рёбер, а не по
/// хордам:
///   • плоскость (опора p₀, базис u, v): ∫∫ x·n dA = (n·p₀)·∮ ½(u dv − v du);
///   • цилиндр (ось o, w, радиус R, базис u, v ⊥ w): ∫∫ x·n dA = ∮ −z·g(θ) dθ,
///     g(θ) = R² + R(o_u cos θ + o_v sin θ). Форма −z·g(θ)dθ определена на всём цилиндре
///     (g периодична), поэтому ни шов, ни обход оси поправок не требуют;
///   • сфера (центр c, радиус R, полюс w): ∫∫ x·n dA = ∮ [R³(1 − cos φ) + ψ(φ, θ)] dθ, где ψ —
///     первообразная R² sin φ (c·n̂) по φ, равная нулю на полюсе φ = 0. Форма гладкая везде,
///     кроме противоположного полюса; полюс w берётся по направлению грани, чтобы особая точка
///     лежала вне неё.
/// Знак выходит сам: контуры грани обходят её наружную нормаль против часовой стрелки
/// (соглашение K05), и та же ориентация нужна формуле Стокса.
///
/// Если хоть одна грань — другой поверхности или тело не приведено к соглашению K05, возвращается
/// null: вызывающий берёт объём по сетке с её границей ошибки.
/// </summary>
public static class AnalyticVolume
{
    /// <summary>Шагов квадратуры на кривое ребро (трапеции по параметру, ошибка ~ N⁻²).</summary>
    public static readonly int Steps = int.TryParse(Environment.GetEnvironmentVariable("P3D_VOLSTEPS"), out var vsx) ? vsx : 1024;

    public static Action<string>? Debug;

    /// <summary>
    /// KRN-02: начало отсчёта интегралов ∫∫ x·n dA. Объём тела от начала не зависит (∮ n dA = 0 по замкнутой оболочке), но
    /// в абсолютных координатах слагаемые порядка |x|·площадь гасят друг друга: деталь, сдвинутая на 100 м, теряла 1.5e-10
    /// относительной точности. Of считает от центра габарита вершин.
    /// </summary>
    [ThreadStatic] private static Vector3d _origin;
    /// <summary>Разбор: P3D_VOLORIGIN=0 — прежнее начало отсчёта (0, 0, 0).</summary>
    public static bool OriginShift = Environment.GetEnvironmentVariable("P3D_VOLORIGIN") != "0";

    /// <summary>
    /// g.02: у тела с АВТОРИТЕТНОЙ МНОГОГРАННОЙ формой (многогранный путь) границы граней — подогнанные к фасетам кривые, а
    /// источники граней — аналитические поверхности; интеграл по ним может не быть объёмом тела (объединение тора с конусом:
    /// 62.9 вместо 641.5 мм³ — деталь показывала такой объём; найдено тождествами булевых). Такой интеграл принимается только
    /// при согласии с точной многогранной формой (AgreesWithPolyhedron). Мутация P3D_POLYVOL=0.
    /// </summary>
    static readonly bool PolyVolumeOld = Environment.GetEnvironmentVariable("P3D_POLYVOL") == "0";
    static bool PolyhedralAuthoritative(TSolid s) => !PolyVolumeOld && s.Exact != null && s.ExactAuthoritative;

    [ThreadStatic] static double _quadErr;
    /// <summary>Разбор: грань, тонкий и грубый проход квадратуры.</summary>
    [ThreadStatic] public static Action<TFace, double, double>? QuadDebug;

    /// <summary>
    /// g.04: объём и ОЦЕНКА ОШИБКИ интегрирования (квадратура по кривым без замкнутой формулы; у отрезков, дуг, эллипсов и
    /// окружностей на сфере — только округление). null — объём не считается точно.
    /// </summary>
    public static (double Volume, double QuadratureError)? OfWithError(TSolid solid)
    {
        double prev = _quadErr; _quadErr = 0;
        try
        {
            var v = Of(solid);
            return v is { } vv ? (vv, _quadErr / 3) : null;
        }
        finally { _quadErr = prev; }
    }

    /// <summary>Мутация P3D_NURBSVOL=0: грань NURBS без аналитического интеграла (объём тела — null).</summary>
    static readonly bool NurbsVolOff = Environment.GetEnvironmentVariable("P3D_NURBSVOL") == "0";

    public static double? Of(TSolid solid)
    {
        if (solid.TopologyConvention < TopologyConventions.Version) return null;
        // u11: ОБЪЁМ ТЕЛА ЗАПОМИНАЕТСЯ по телу, его ревизии, отпечатку состава (TopoFingerprint) и настройкам ядра. Тело после построения не меняется (правка —
        // новая ревизия), а объём одного и того же тела считался 3–4 раза за операцию: построитель, его проверка, проверка
        // детали — у ленты процедурного скругления это 0.2–0.3 с каждый раз. Повтор добавляет ту же ошибку квадратуры.
        // Мутация P3D_VOLCACHE=0.
        KernelSettings? key = VolCacheOff ? null : KernelSettings.Capture() with { Preview = false, LivePreview = false };
        ulong fp = key == null ? 0 : TopoFingerprint.Of(solid);
        if (key != null && _volCache.TryGetValue(solid, out var hit) && hit.Revision == solid.Revision && hit.Print == fp && hit.Key.Equals(key))
        {
            _quadErr += hit.Quad;
            return hit.Volume;
        }
        long rev = solid.Revision;
        // ошибка квадратуры этого тела — отдельно от накопленной (накопленная бывает бесконечной: ∞ − ∞ давало NaN в границе)
        double q0 = _quadErr; _quadErr = 0;
        double? v; double quad;
        try
        {
            v = OfCore(solid);
            if (v is { } vv && PolyhedralAuthoritative(solid) && !AgreesWithPolyhedron(solid, vv)) { PolyVolumeRejected++; v = null; }
        }
        finally { quad = _quadErr; _quadErr = q0 + quad; }
        if (key != null && solid.Revision == rev && TopoFingerprint.Of(solid) == fp)
            _volCache.AddOrUpdate(solid, new VolEntry { Revision = rev, Print = fp, Key = key, Volume = v, Quad = quad });
        return v;
    }

    private sealed class VolEntry { public long Revision; public ulong Print; public KernelSettings Key = null!; public double? Volume; public double Quad; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TSolid, VolEntry> _volCache = new();
    static readonly bool VolCacheOff = Environment.GetEnvironmentVariable("P3D_VOLCACHE") == "0";

    /// <summary>Разбор: сколько раз интеграл многогранного тела отвергнут сверкой с его точной многогранной формой.</summary>
    public static int PolyVolumeRejected;

    /// <summary>
    /// Интеграл по источникам граней многогранного тела годен, только если согласен с объёмом точной многогранной формы в
    /// пределах ошибки гранения (1%: вписанный многогранник с шагом угла 7.5° меньше до ~0.6%). Иначе — null.
    /// </summary>
    static bool AgreesWithPolyhedron(TSolid s, double v)
    {
        double vp;
        try { vp = s.Exact!.Solid.VolumeMm3().ToDouble(); } catch (Exception ex) when (!KernelContext.IsCancellation(ex)) { return false; }
        return Math.Abs(v - vp) <= 1e-2 * Math.Abs(vp) + 1e-9;
    }

    static double? OfCore(TSolid solid)
    {
        var prev = _origin;
        _origin = OriginShift ? CentreOf(solid) : Vector3d.Zero;
        try
        {
            double sum = 0;
            foreach (var f in solid.Faces)
            {
                var c = FaceIntegral(f);
                if (c == null || double.IsNaN(c.Value)) return null;
                sum += c.Value;
            }
            return sum / 3;
        }
        finally { _origin = prev; }
    }

    /// <summary>Разбор: вклад грани в объём (∫x·n/3) относительно заданного начала.</summary>
    public static double? FaceVolumeAt(TFace f, Vector3d origin)
    {
        var prev = _origin; _origin = origin;
        try { return FaceIntegral(f) / 3; } finally { _origin = prev; }
    }

    static Vector3d CentreOf(TSolid solid)
    {
        var lo = new Vector3d(double.MaxValue); var hi = new Vector3d(double.MinValue); bool any = false;
        foreach (var v in solid.Vertices) { lo = Vector3d.ComponentMin(lo, v.P); hi = Vector3d.ComponentMax(hi, v.P); any = true; }
        return any ? (lo + hi) / 2 : Vector3d.Zero;
    }

    /// <summary>
    /// Площадь поверхности тела по настоящим кривым рёбер. Для каждой грани интеграл края даёт
    /// ±площадь (знак — сторона нормали грани относительно параметризации), поэтому берётся модуль.
    /// </summary>
    public static double? AreaOf(TSolid solid)
    {
        if (solid.TopologyConvention < TopologyConventions.Version) return null;
        if (PolyhedralAuthoritative(solid) && Of(solid) == null) return null;   // интеграл не согласен с многогранной формой
        double sum = 0;
        foreach (var f in solid.Faces)
        {
            var a = FaceArea(f);
            if (a == null || double.IsNaN(a.Value)) return null;
            sum += Math.Abs(a.Value);
        }
        return sum;
    }

    /// <summary>Мутация g.02 (P3D_FLATREV=0): плоская крышка тела вращения — общей формулой вращения, как прежде.</summary>
    static readonly bool FlatRevOff = Environment.GetEnvironmentVariable("P3D_FLATREV") == "0";

    /// <summary>
    /// g.02: КРЫШКА ТЕЛА ВРАЩЕНИЯ (образующая — отрезок поперёк оси) — это плоскость; её грань, отсечённая хордой (конус,
    /// повёрнутый и срезанный бруском: отрезок + дуга), формулой вращения считалась неверно (интеграл 1487 вместо 432 —
    /// объём пересечения на 352 мм³ больше; разностное сравнение с OpenCascade). Возвращает плоскую рамку или null.
    /// </summary>
    static (Vector3d P0, Vector3d U, Vector3d V)? FlatRevolved(ISurface3? s)
    {
        if (FlatRevOff || s is not RevolvedSurface rv || rv.Base is not LineCurve3 gl) return null;
        var w = rv.Axis.W.Normalized();
        if (Math.Abs(Vector3d.Dot(gl.B - gl.A, w)) > 1e-12 * Math.Max(1, (gl.B - gl.A).Length)) return null;
        var p0 = rv.Axis.Origin + w * Vector3d.Dot(gl.A - rv.Axis.Origin, w);
        var u = rv.Axis.U.Normalized(); u = (u - w * Vector3d.Dot(u, w)).Normalized();
        return (p0, u, Vector3d.Cross(w, u));
    }

    /// <summary>Мутация P3D_AREARICH=0: площадь грани с кривыми общего вида — ломаной без поправки Ричардсона (прежнее).</summary>
    static readonly bool AreaRichOff = Environment.GetEnvironmentVariable("P3D_AREARICH") == "0";

    /// <summary>
    /// u12: ПЛОЩАДЬ ДЛЯ САМОПРОВЕРКИ РАЗМЕТКИ — допуск проверки 0.5 %, точность 1e-9 там не нужна: у лент по формуле сечения
    /// квадратура в 8 раз реже и без прохода Ричардсона (2 с → 0.1 с на ленту переменного радиуса). Прочие грани — как FaceArea.
    /// Мутация P3D_ROUGHAREA=0 — полная площадь.
    /// </summary>
    public static double? FaceAreaRough(TFace f)
    {
        if (RoughAreaOff || GeometryAudit.AuthoritativeSurface(f) is not SectionSurface) return FaceArea(f);
        _rough = true;
        try { return FaceAreaCore(f); } finally { _rough = false; }
    }
    [ThreadStatic] static bool _rough;
    static readonly bool RoughAreaOff = Environment.GetEnvironmentVariable("P3D_ROUGHAREA") == "0";

    public static double? FaceArea(TFace f)
    {
        // KRN-07 (g.14, сверка с OpenCascade): площадь грани с кривыми общего вида (сплайн, кривая пересечения) — как у
        // интеграла объёма: проход с половиной шагов и поправка Ричардсона (ломаная — ошибка ∝ шаг²; четверть круга с краем
        // «сплайн» и «кривая NURBS × плоскость» отходила на 1e-6 мм² — у тела 9e-8 отн. против 1e-8 у OpenCascade)
        if (!AreaRichOff && _coarse == 0 && (HasTracedCurves(f) || f.Loops.Any(l => l.CoEdges.Any(ce => ce.Edge.Curve is EllipseCurve3 or ArcCurve3))))
        {
            double? fine = FaceAreaCore(f);
            if (fine == null) return null;
            _coarse = 0.5; double? coarse;
            try { coarse = FaceAreaCore(f); } finally { _coarse = 0; }
            return coarse == null ? fine : fine + (fine - coarse) / 3;
        }
        return FaceAreaCore(f);
    }

    static double? FaceAreaCore(TFace f)
    {
        if (FlatRevolved(GeometryAudit.AuthoritativeSurface(f)) is { } fr) return PlaneArea(f, fr.P0, fr.U, fr.V);
        switch (GeometryAudit.AuthoritativeSurface(f))
        {
            case PlaneSurface pl: return PlaneArea(f, pl.F.Origin, pl.F.U, pl.F.V);
            case NurbsSurface when !NurbsVolOff: return AnalyticFlux.FaceAreaOf(f);
            case CornerPatchSurface cpa when WholePatch(f, cpa): return CornerIntegral(f, cpa, (x, n) => n.Length, area: true);
            case ExtrudedSurface exl when exl.Base is LineCurve3 ln:
            {
                var u = (ln.B - ln.A).Normalized();
                var n = Vector3d.Cross(u, exl.Dir.Normalized());
                if (n.LengthSquared < 1e-18) return null;
                return PlaneArea(f, ln.A, u, Vector3d.Cross(n.Normalized(), u));
            }
            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc && ex.Dir.LengthSquared > 1e-18
                                      && Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), ex.Dir.Normalized())) > 1 - 1e-12:
                return CylinderArea(f, arc.Frame.Origin, ex.Dir.Normalized(), arc.Frame.U.Normalized(), arc.Radius);
            case CylinderSurface cs:
                return CylinderArea(f, cs.Axis.Origin, cs.Axis.W.Normalized(), cs.Axis.U.Normalized(), cs.Radius);
            case RevolvedSurface rv when rv.Base is ArcCurve3 ba && Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) < 1e-9:
            {
                var axis = rv.Axis.W.Normalized();
                var d = ba.Frame.Origin - rv.Axis.Origin;
                if ((d - axis * Vector3d.Dot(d, axis)).Length > 1e-9) return RevolvedIntegral(f, rv, area: true);
                return SphereArea(f, ba.Frame.Origin, ba.Radius);
            }
            case RevolvedSurface rva:
                return RevolvedIntegral(f, rva, area: true);
            case SectionSurface band when !BandOff && BandRange(f, band) != null:
                return BandIntegral(f, band, area: true);
            case SectionSurface ob when Prof1("лента: область", () => BandOpenRange(f, ob)) is { } orr:
                return Prof1("лента: площадь", () => BandOpenIntegral(f, ob, orr, area: true));
            default: return null;
        }
    }

    /// <summary>
    /// ∮ ½(u dv − v du) по контурам плоской грани — ТОЧНО для отрезков и дуг окружности
    /// (замкнутая формула по углам), плотной ломаной — для прочих кривых.
    /// </summary>
    private static double PlaneLoopArea(TFace f, Vector3d p0, Vector3d u, Vector3d v)
    {
        u = u.Normalized(); v = (v - u * Vector3d.Dot(v, u)).Normalized();
        var n = Vector3d.Cross(u, v);
        double U(Vector3d q) => Vector3d.Dot(q - p0, u);
        double V(Vector3d q) => Vector3d.Dot(q - p0, v);
        double area = 0;
        foreach (var loop in f.Loops)
        {
            Vector3d? firstStart = null, prevEnd = null;
            foreach (var ce in loop.CoEdges)
            {
                var e = ce.Edge; var c = e.Curve;
                bool curveForward = (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared || ReferenceEquals(e.V0, e.V1);
                bool forward = ce.Reversed ? !curveForward : curveForward;
                // ЗАМКНУТОСТЬ КОНТУРА (аналитическое ядро). Вершины тел из многогранного пути стоят не точно на кривой
                // (стык отверстия R3 со стенкой уступа — 0.0063 мм от окружности). Вклад дуги по её концам и отрезков по
                // вершинам давал НЕЗАМКНУТЫЙ контур, а формула Грина для него зависит от начала координат: у одной и той
                // же грани с рамками разных начал площадь расходилась на 0.17 мм². Кривые — авторитетны: каждая даёт
                // точный вклад от своих концов, а разрывы между концом одной и началом следующей замыкаются
                // соединительными хордами (порядка самого разрыва). Контур замкнут, площадь от начала не зависит.
                double Chord(Vector3d a, Vector3d b) => 0.5 * (U(a) * V(b) - V(a) * U(b));
                var cs0 = forward ? c.PointAt(c.T0) : c.PointAt(c.T1);
                var ce0 = forward ? c.PointAt(c.T1) : c.PointAt(c.T0);
                if (prevEnd is { } pe) area += Chord(pe, cs0); else firstStart = cs0;
                prevEnd = ce0;
                if (c is ArcCurve3 arc && Math.Abs(Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), n)) - 1) < 1e-9)
                {
                    // Окружность в плоскости грани: x = cx + R cos t, y = cy + R sin t в базисе (u, v).
                    double R = arc.Radius;
                    double cx = U(arc.Frame.Origin), cy = V(arc.Frame.Origin);
                    var pa = forward ? c.PointAt(c.T0) : c.PointAt(c.T1);
                    var pb = forward ? c.PointAt(c.T1) : c.PointAt(c.T0);
                    double ta = Math.Atan2(V(pa) - cy, U(pa) - cx);
                    double sweep = arc.Sweep * Math.Sign(Vector3d.Dot(arc.Frame.W, n)) * (forward ? 1 : -1);
                    double tb = ta + sweep;
                    double full = 0.5 * (R * R * sweep + R * cx * (Math.Sin(tb) - Math.Sin(ta)) - R * cy * (Math.Cos(tb) - Math.Cos(ta)));
                    area += full;
                    continue;
                }
                if (c is EllipseCurve3 el && Math.Abs(Math.Abs(Vector3d.Dot(el.Frame.W.Normalized(), n)) - 1) < 1e-9)
                {
                    // G4: эллипс в плоскости грани — точно: P(t) = C + A cos t + B sin t в базисе (u, v);
                    // ∫(x dy − y dx) = A×B·Δt + C×A·(cos tb − cos ta) + C×B·(sin tb − sin ta).
                    var A = new Vector2d(Vector3d.Dot(el.Frame.U, u), Vector3d.Dot(el.Frame.U, v)) * el.RU;
                    var B = new Vector2d(Vector3d.Dot(el.Frame.V, u), Vector3d.Dot(el.Frame.V, v)) * el.RV;
                    var C = new Vector2d(U(el.Frame.Origin), V(el.Frame.Origin));
                    double X(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
                    double ta = forward ? el.A0 : el.A1, tb = forward ? el.A1 : el.A0;
                    double full = 0.5 * (X(A, B) * (tb - ta) + X(C, A) * (Math.Cos(tb) - Math.Cos(ta)) + X(C, B) * (Math.Sin(tb) - Math.Sin(ta)));
                    area += full;
                    continue;
                }
                int m = c is LineCurve3 ? 1 : StepsFor(c);
                Vector3d prev = cs0;
                for (int i = 1; i <= m; i++)
                {
                    double t = forward ? c.T0 + (c.T1 - c.T0) * i / m : c.T1 - (c.T1 - c.T0) * i / m;
                    var q = c.PointAt(t);
                    area += 0.5 * (U(prev) * V(q) - V(prev) * U(q));
                    prev = q;
                }
            }
            // замыкание петли: конец последней кривой — начало первой
            if (prevEnd is { } last && firstStart is { } first0)
                area += 0.5 * (U(last) * V(first0) - V(last) * U(first0));
        }
        return area;
    }

    private static double PlaneArea(TFace f, Vector3d p0, Vector3d u, Vector3d v) => PlaneLoopArea(f, p0, u, v);

    private static double CylinderArea(TFace f, Vector3d o, Vector3d w, Vector3d u0, double R)
    {
        var u = (u0 - w * Vector3d.Dot(u0, w)).Normalized(); var v = Vector3d.Cross(w, u);
        double sum = 0;
        foreach (var pts in Loops(f))
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i] - o; var b = pts[i + 1] - o;
                double dt = Wrap(Math.Atan2(Vector3d.Dot(b, v), Vector3d.Dot(b, u)) - Math.Atan2(Vector3d.Dot(a, v), Vector3d.Dot(a, u)));
                sum += -0.5 * (Vector3d.Dot(a, w) + Vector3d.Dot(b, w)) * R * dt;     // ω = −R z dθ
            }
        return sum;
    }

    /// <summary>Мутация g.14/KRN-02 (P3D_SPHCLOSED=0): площадь и интеграл сферической грани — ломаной, как прежде.</summary>
    static readonly bool SphereClosedOff = Environment.GetEnvironmentVariable("P3D_SPHCLOSED") == "0";
    /// <summary>Разбор: сколько раз замкнутая форма сферической грани применена / отвергнута сверкой с ломаной.</summary>
    public static int SphereClosedUsed, SphereClosedRejected;

    /// <summary>
    /// g.14 (найдено сверкой с OpenCascade): ЗАМКНУТАЯ ФОРМА для сферической грани, чьи рёбра — окружности на сфере.
    /// Прежде — трапеции по ломаной края (ошибка ∝ шаг²: у выреза угла куба шаром R10 объём на 6.7e-3 мм³, площадь на
    /// 2e-3 мм² от точного). Площадь — Гаусс–Бонне: A = R²(2πχ − Σ∫k_g ds − Σθ), χ = 2 − число контуров, k_g дуги
    /// постоянна, θ — внешние углы в вершинах (обход — грань слева, если смотреть со стороны нормали грани N).
    /// Векторная площадь ∫N dA = ½∮x×dx (Стокс) — точно по дугам. Тогда ∫(x−o)·N dA = (c−o)·∫N dA + s·R·A (s = ±1 —
    /// наружу ли от центра нормаль грани). false — условия не выполнены (рёбра не окружности на сфере, шов, разрыв
    /// контура) или результат не согласован с ломаной (1e-2: ловит обращённую сторону — дополнение до сферы; ошибка ломаной у малой грани — до 1e-4): прежний способ.
    /// </summary>
    static bool SphereClosed(TFace f, Vector3d c, double R, out double area, out Vector3d vecArea, out double side)
    {
        area = 0; vecArea = Vector3d.Zero; side = 1;
        if (SphereClosedOff) return false;
        var loops = f.Loops.Where(l => l.CoEdges.Count > 0).ToList();
        if (loops.Count == 0) return false;
        var uses = new HashSet<TEdge>(ReferenceEqualityComparer.Instance);
        double tol = Math.Max(1e-9, 1e-12 * R);
        foreach (var ce in loops.SelectMany(l => l.CoEdges))
        {
            if (!uses.Add(ce.Edge)) return false;                                   // шов
            if (ce.Edge.Curve is not ArcCurve3 a) return false;
            var d = a.Frame.Origin - c; var w = a.Frame.W.Normalized();
            if (Math.Abs(d.LengthSquared + a.Radius * a.Radius - R * R) > 1e-9 * R * R) return false;   // окружность не на сфере
            if ((d - w * Vector3d.Dot(d, w)).Length > 1e-9 * R) return false;     // плоскость окружности не ⟂ радиусу
            foreach (var v in new[] { ce.Edge.V0.P, ce.Edge.V1.P })
                if (Math.Abs((v - c).Length - R) > 1e-9 * R) return false;          // вершины не на сфере (многогранный путь)
        }
        var p0 = loops[0].CoEdges[0].Edge.Curve.PointAt(0.5);
        double sd0 = SphereSideByHint(f, c, R) ?? (Vector3d.Dot(f.NormalAt(p0), p0 - c) >= 0 ? 1 : -1); side = sd0;
        Vector3d N(Vector3d x) => (x - c).Normalized() * sd0;
        double kgSum = 0, turn = 0; var va = Vector3d.Zero;
        foreach (var l in loops)
        {
            var seq = new List<(Vector3d S, Vector3d E, Vector3d Ts, Vector3d Te)>();
            foreach (var ce in l.CoEdges)
            {
                var a = (ArcCurve3)ce.Edge.Curve;
                var e = ce.Edge;
                bool curveForward = (a.PointAt(0) - e.V0.P).LengthSquared <= (a.PointAt(1) - e.V0.P).LengthSquared || ReferenceEquals(e.V0, e.V1);
                bool fwd = ce.Reversed ? !curveForward : curveForward;
                double sgn = fwd ? 1 : -1;
                double as_ = fwd ? a.A0 : a.A1, ae = fwd ? a.A1 : a.A0;
                var S = a.PointAt(fwd ? 0 : 1); var E = a.PointAt(fwd ? 1 : 0);
                var Ts = a.TangentAt(fwd ? 0 : 1) * sgn; var Te = a.TangentAt(fwd ? 1 : 0) * sgn;
                // ∫k_g ds: кривизна окружности к её центру, проекция на «влево» L = N×T; постоянна вдоль дуги
                var L = Vector3d.Cross(N(S), Ts);
                double kg = Vector3d.Dot((a.Frame.Origin - S) / (a.Radius * a.Radius), L);
                kgSum += kg * a.Radius * Math.Abs(a.Sweep);
                // ½∫x'×dx', x' = x − c
                var U = a.Frame.U.Normalized(); var V = a.Frame.V.Normalized(); var W = Vector3d.Cross(U, V);
                Vector3d Ev(double t) => U * Math.Cos(t) + V * Math.Sin(t);
                var Cc = a.Frame.Origin - c;
                va += 0.5 * (a.Radius * Vector3d.Cross(Cc, Ev(ae) - Ev(as_)) + a.Radius * a.Radius * W * (ae - as_));
                seq.Add((S, E, Ts, Te));
            }
            for (int i = 0; i < seq.Count; i++)
            {
                var cur = seq[i]; var nxt = seq[(i + 1) % seq.Count];
                if ((cur.E - nxt.S).Length > Math.Max(1e-7, 1e-9 * R)) return false;   // контур не по порядку / разрыв
                va += 0.5 * Vector3d.Cross(cur.E - c, nxt.S - c);                       // замыкающая хорда (порядка разрыва)
                var n = N(cur.E);
                turn += Math.Atan2(Vector3d.Dot(n, Vector3d.Cross(cur.Te, nxt.Ts)), Vector3d.Dot(cur.Te, nxt.Ts));
            }
        }
        int chi = 2 - loops.Count;
        area = R * R * (2 * Math.PI * chi - kgSum - turn);
        vecArea = va;
        return area > 0 && area < 4 * Math.PI * R * R * (1 + 1e-12);
    }

    private static double SphereArea(TFace f, Vector3d centre, double R)
    {
        // целая сфера (грань без края, полость-шар): 4πR², знак — как у интеграла (наружу от центра — плюс)
        if (f.Loops.Count == 0 || f.Loops.All(l => l.CoEdges.Count == 0)
            || (!SeamWholeOff && f.Loops.SelectMany(l => l.CoEdges).GroupBy(ce => ce.Edge).All(g => g.Count() == 2)))
        {
            // g.05/KRN-09: и грань, вся граница которой — швы (каждое ребро дважды): целая сфера (прочитанная из STEP). P3D_SEAMWHOLE=0.
            var px = centre + Vector3d.UnitX * R;
            return Vector3d.Dot(f.NormalAt(px), Vector3d.UnitX) >= 0 ? 4 * Math.PI * R * R : -4 * Math.PI * R * R;
        }
        // g.05: площадь — телесным углом контуров со стороной по точке грани, как у интеграла (прежде — по полюсу от средней
        // точки фасет: у грани с чужими фасетами — площадь дополнения). Мутация P3D_SPHHINTSIDE=0 (через сторону).
        double old = SphereAreaPolyline(f, centre, R);
        // знак — как прежде (сторона относительно параметризации прежнего способа: на него опираются вызывающие), величина —
        // телесным углом со стороной по точке грани
        if (!SphAreaOff && !SphHintSideOff && f.InteriorHint != null && SphereAreaBySolidAngle(f, centre, R) is { } sa0)
            return (double.IsNaN(old) ? sa0.Side : old >= 0 ? 1 : -1) * sa0.Area;
        if (SphereClosed(f, centre, R, out double a, out _, out _))
        {
            // Знак площади — как у прежнего способа (сторона нормали относительно его параметризации); величина — замкнутая.
            // Сверка величины с ломаной не делается: у грани больше полусферы ломаная с полюсом по средней точке края
            // считала ШАПКУ (найдено разностным сравнением g.02), а сверка отвергала верную замкнутую форму.
            SphereClosedUsed++; return (double.IsNaN(old) || old >= 0 ? 1 : -1) * a;
        }
        return old;
    }

    private static double SphereAreaPolyline(TFace f, Vector3d centre, double R)
    {
        var w = PoleFor(f, centre);
        if (w == null) return double.NaN;
        var (u, v) = Basis(w.Value);
        double sum = 0;
        foreach (var pts in Loops(f))
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = (pts[i] - centre).Normalized(); var b = (pts[i + 1] - centre).Normalized();
                double ca = Vector3d.Dot(a, w.Value), cb = Vector3d.Dot(b, w.Value);
                double dt = Wrap(Math.Atan2(Vector3d.Dot(b, v), Vector3d.Dot(b, u)) - Math.Atan2(Vector3d.Dot(a, v), Vector3d.Dot(a, u)));
                sum += R * R * (1 - 0.5 * (ca + cb)) * dt;                             // ω = R²(1 − cos φ) dθ
            }
        return sum;
    }

    private static double Wrap(double d)
    {
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }

    private static (Vector3d U, Vector3d V) Basis(Vector3d w)
    {
        var u = Math.Abs(w.X) < 0.9 ? Vector3d.Cross(w, Vector3d.UnitX).Normalized() : Vector3d.Cross(w, Vector3d.UnitY).Normalized();
        return (u, Vector3d.Cross(w, u));
    }

    /// <summary>Полюс для сферической грани: по средней её точке; null — особая точка на грани.</summary>
    private static Vector3d? PoleFor(TFace f, Vector3d centre)
    {
        if (f.Loops.Count == 0 || f.Loops.All(l => l.CoEdges.Count == 0)) return null;
        var mean = Vector3d.Zero;
        if (f.Surface is FacetedSurface fsm)
            foreach (var (x, y, z) in fsm.Facets)
                mean += ((x + y + z) / 3 - centre) * Vector3d.Cross(y - x, z - x).Length;
        else foreach (var pts in Loops(f)) foreach (var q in pts) mean += q - centre;
        if (mean.LengthSquared < 1e-18) return null;
        var w = mean.Normalized();
        if (f.Surface is FacetedSurface fsp)
            foreach (var (x, y, z) in fsp.Facets)
                if (Vector3d.Dot(((x + y + z) / 3 - centre).Normalized(), -w) > 1 - 1e-4) return null;
        return w;
    }

    /// <summary>
    /// G4. Доля шага для кривых без замкнутой формулы (кривые пересечения, сплайны): 1 — обычный шаг.
    /// Интеграл по таким кривым идёт ломаной (ошибка трапеций ∝ шаг²); экстраполяция Ричардсона по
    /// двум шагам (h и 2h) убирает главный член: I ≈ I_h + (I_h − I_2h)/3 — порядок h⁴ при цене
    /// полутора проходов. На поперечном отверстии в вале ошибка объёма 5e-4 → 1e-8 мм³ без
    /// удлинения прогона, как давало восьмикратное измельчение шага.
    /// </summary>
    [ThreadStatic] private static double _coarse;
    /// <summary>Мутация P3D_VOLBAND=0: грань-пояс поверхности по формуле сечения не поддержана (null).</summary>
    static readonly bool BandOff = Environment.GetEnvironmentVariable("P3D_VOLBAND") == "0";

    /// <summary>
    /// KRN-06 (06.12): ГРАНЬ ЛЕНТЫ — часть замкнутой поверхности по формуле сечения между её краями v = 0 и v = 1 (лента
    /// катящегося шара): либо весь пояс (два контура — по замкнутому ребру на v = 0 и на v = 1), либо кусок u ∈ [ua, ub]
    /// (один контур: рёбра на v = 0 и v = 1 и два шва — дуги сечений при постоянном u). Промежуток u — тот из двух между
    /// швами, где лежит середина ребра края. null — грань не такая.
    /// </summary>
    static readonly bool LensOff = Environment.GetEnvironmentVariable("P3D_VOLLENS") == "0";
    internal static (double Ua, double Ub)? BandRange(TFace f, SectionSurface ss)
    {
        if (!ss.IsProcedural) return null;
        // 06.12: ЛЕНТА-«ЛИНЗА» (сечения стягиваются в точку на обоих концах промежутка: стык труб равного радиуса) — контур из
        // двух рёбер, v = 0 и v = 1, на всём промежутке u. Мутация P3D_VOLLENS=0.
        if (!ss.Closed)
        {
            // 06.2: «ВЕЕР» (луночка шара стыка лент: все сечения через две общие точки, v = 0 и v = 1) — контур из двух сечений
            // u = UMin и u = UMax. Мутация P3D_VOLFAN=0.
            if (!FanOff && f.Loops.Count == 1 && f.Loops[0].CoEdges.Count == 2)
            {
                var a0 = ss.ExactSectionAt(ss.UMin); var a1 = ss.ExactSectionAt(ss.UMax); var am = ss.ExactSectionAt((ss.UMin + ss.UMax) / 2);
                double sc = Math.Max(1, (a0.Point(0) - a0.Point(1)).Length);
                bool fanned = (a0.Point(0) - a1.Point(0)).Length < 1e-9 * sc && (a0.Point(1) - a1.Point(1)).Length < 1e-9 * sc
                              && (am.Point(0) - a0.Point(0)).Length < 1e-9 * sc && (am.Point(1) - a0.Point(1)).Length < 1e-9 * sc;
                if (fanned)
                {
                    bool u0 = false, u1 = false;
                    foreach (var ce in f.Loops[0].CoEdges)
                    {
                        var c = ce.Edge.Curve; double ulo = 1e300, uhi = -1e300;
                        for (int k = 0; k <= 8; k++) { var uv = ss.ProjectToUV(c.PointAt(c.T0 + (c.T1 - c.T0) * (k + 0.5) / 9.5)); ulo = Math.Min(ulo, uv.X); uhi = Math.Max(uhi, uv.X); }
                        double span = Math.Max(1e-300, ss.UMax - ss.UMin);
                        if (Math.Abs(ulo - ss.UMin) < 1e-7 * span && Math.Abs(uhi - ss.UMin) < 1e-7 * span) u0 = true;
                        else if (Math.Abs(ulo - ss.UMax) < 1e-7 * span && Math.Abs(uhi - ss.UMax) < 1e-7 * span) u1 = true;
                    }
                    if (u0 && u1) return (ss.UMin, ss.UMax);
                }
            }
            if (LensOff || f.Loops.Count != 1 || f.Loops[0].CoEdges.Count != 2) return null;
            bool l0 = false, l1 = false;
            foreach (var ce in f.Loops[0].CoEdges)
            {
                var c = ce.Edge.Curve; double vlo = 1e9, vhi = -1e9;
                for (int k = 0; k <= 8; k++) { var uv = ss.ProjectToUV(c.PointAt(c.T0 + (c.T1 - c.T0) * (k + 0.5) / 9.5)); vlo = Math.Min(vlo, uv.Y); vhi = Math.Max(vhi, uv.Y); }
                if (Math.Abs(vlo) < 1e-6 && Math.Abs(vhi) < 1e-6) l0 = true; else if (Math.Abs(vlo - 1) < 1e-6 && Math.Abs(vhi - 1) < 1e-6) l1 = true;
            }
            var s0 = ss.ExactSectionAt(ss.UMin); var s1 = ss.ExactSectionAt(ss.UMax);
            bool pinched = (s0.Point(0) - s0.Point(1)).Length < 1e-9 && (s1.Point(0) - s1.Point(1)).Length < 1e-9;
            return l0 && l1 && pinched ? (ss.UMin, ss.UMax) : null;
        }
        double per = ss.UMax - ss.UMin;
        double Wr(double d) { d %= per; if (d < 0) d += per; return d; }
        var cuts = new List<double>(); bool at0 = false, at1 = false; double? edgeMidU = null;
        foreach (var ce in f.Loops.SelectMany(l => l.CoEdges))
        {
            var c = ce.Edge.Curve; double vlo = 1e9, vhi = -1e9, u0 = double.NaN, uSpread = 0; double uMid = double.NaN;
            for (int k = 0; k <= 8; k++)
            {
                var uv = ss.ProjectToUV(c.PointAt(c.T0 + (c.T1 - c.T0) * (k + 0.5) / 9.5));
                vlo = Math.Min(vlo, uv.Y); vhi = Math.Max(vhi, uv.Y);
                if (double.IsNaN(u0)) u0 = uv.X;
                double du = Wr(uv.X - u0); if (du > per / 2) du -= per; uSpread = Math.Max(uSpread, Math.Abs(du));
                if (k == 4) uMid = uv.X;
            }
            if (Math.Abs(vlo) < 1e-6 && Math.Abs(vhi) < 1e-6) { at0 = true; edgeMidU ??= uMid; }
            else if (Math.Abs(vlo - 1) < 1e-6 && Math.Abs(vhi - 1) < 1e-6) { at1 = true; edgeMidU ??= uMid; }
            else if (uSpread < 1e-7 * Math.Max(1, per)) cuts.Add(u0);
            else return null;
        }
        if (!at0 || !at1) return null;
        if (cuts.Count == 0)
            return f.Loops.Count == 2 && f.Loops.All(l => l.CoEdges.Count == 1 && ReferenceEquals(l.CoEdges[0].Edge.V0, l.CoEdges[0].Edge.V1)) ? (ss.UMin, ss.UMax) : null;
        if (cuts.Count != 2 || edgeMidU == null) return null;
        double ua = cuts[0], ub = cuts[1], len = Wr(ub - ua);
        if (len < 1e-9 * per || len > per - 1e-9 * per) return null;
        return Wr(edgeMidU.Value - ua) < len ? (ua, ua + len) : (ub, ub + per - len);
    }

    /// <summary>
    /// ∫∫ (x − o)·N dA (или площадь) грани ленты по u ∈ [ua, ub], v ∈ [0, 1]: составной Гаусс 12 точек по u (96 кусков, грубый
    /// проход — 48) и Гаусс 12 по v; производная по u — центральной разностью. Сторона — по векторной площади контуров (½∮x×dx,
    /// их обход задан нормализацией тела) против ∫∫ S_u × S_v: обе — та же величина с точностью до знака.
    /// </summary>
    static double? BandIntegral(TFace f, SectionSurface ss, bool area)
    {
        if (BandRange(f, ss) is not { } rg) return null;
        double per = ss.UMax - ss.UMin, h = 1e-6 * Math.Max(1e-9, per);
        int m = _rough ? 16 : _coarse > 0 ? 48 : 96;
        var (gx, gw) = Gauss12;
        double vol = 0, ar = 0; var va = Vector3d.Zero; double pw = (rg.Ub - rg.Ua) / m;
        for (int i = 0; i < m; i++)
            for (int j = 0; j < gx.Length; j++)
            {
                double u = rg.Ua + pw * (i + (gx[j] + 1) / 2), wu = gw[j] / 2 * pw;
                var s0 = ss.SectionAt(u); var sp = ss.SectionAt(u + h); var sm = ss.SectionAt(u - h);
                for (int k = 0; k < gx.Length; k++)
                {
                    double v = (gx[k] + 1) / 2, w = gw[k] / 2 * wu;
                    var P = s0.Point(v); var su = (sp.Point(v) - sm.Point(v)) / (2 * h); var sv = s0.Deriv(v);
                    var c = Vector3d.Cross(su, sv);
                    vol += Vector3d.Dot(P - _origin, c) * w; ar += c.Length * w; va += c * w;
                }
            }
        Vector3d loopsVa; using (Diagnostics.Prof.Time("лента: нормаль по контуру")) loopsVa = NormalIntegral(f);
        if (loopsVa.Length < 1e-6 * Math.Max(1e-12, ar) || va.Length < 1e-6 * Math.Max(1e-12, ar)) return null;   // знак не определить
        double cosv = Vector3d.Dot(loopsVa, va) / (loopsVa.Length * va.Length);
        if (Math.Abs(cosv) < 0.999) return null;                                                      // контуры не той поверхности
        return area ? ar : (cosv > 0 ? vol : -vol);
    }

    /// <summary>Мутация P3D_VOLBANDOPEN=0: грань незамкнутой ленты с краями общего вида не поддержана (null).</summary>
    static readonly bool BandOpenOff = Environment.GetEnvironmentVariable("P3D_VOLBANDOPEN") == "0";

    /// <summary>
    /// KRN-06 (06.12, 06.14): ГРАНЬ НЕЗАМКНУТОЙ ЛЕНТЫ — часть поверхности по формуле сечения между её краями v = 0 и v = 1 и двумя
    /// боковыми рёбрами общего вида (край ленты на торце: v монотонно от 0 до 1). Область в (u, v): v ∈ [0, 1],
    /// u ∈ [L(v), R(v)]; L и R — точки боковых рёбер с данным v (бисекция по параметру ребра через проекцию). null — грань не такая.
    /// </summary>
    internal static (Func<double, double> L, Func<double, double> R)? BandOpenRange(TFace f, SectionSurface ss)
    {
        if (BandOpenOff || !ss.IsProcedural || ss.Closed || f.Loops.Count != 1) return null;
        // u12: ОБЛАСТЬ ЛЕНТЫ — ИЗ ПАМЯТИ. Площадь (самопроверка разметки), объём (три раза за операцию) и поток спрашивают одни
        // и те же края L(v), R(v) в тех же узлах Гаусса; каждый — Иллинойс по ребру с проекцией (у края на торце — ещё и корень
        // процедурной кривой): 2.9 с из 3.0 с разметки ленты переменного радиуса. Память — по грани, её поверхности и кривым
        // контура (ссылки), значения — по v. Мутация P3D_BANDRANGECACHE=0.
        if (!BandRangeCacheOff)
        {
            var curves = f.Loops[0].CoEdges.Select(ce => (object)ce.Edge.Curve).ToArray();
            if (_bandRange.TryGetValue(f, out var hit) && ReferenceEquals(hit.S, ss) && hit.Curves.Length == curves.Length && hit.Curves.Zip(curves).All(z => ReferenceEquals(z.First, z.Second)))
                return hit.R;
            var r = BandOpenRangeCore(f, ss);
            if (r is { } rr)
            {
                var mL = new System.Collections.Concurrent.ConcurrentDictionary<double, double>(); var mR = new System.Collections.Concurrent.ConcurrentDictionary<double, double>();
                Func<double, double> L = v => mL.GetOrAdd(v, rr.L), R = v => mR.GetOrAdd(v, rr.R);
                r = (L, R);
            }
            _bandRange.AddOrUpdate(f, new BandRangeEntry { S = ss, Curves = curves, R = r });
            return r;
        }
        return BandOpenRangeCore(f, ss);
    }
    private sealed class BandRangeEntry { public SectionSurface S = null!; public object[] Curves = null!; public (Func<double, double> L, Func<double, double> R)? R; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TFace, BandRangeEntry> _bandRange = new();
    static T Prof1<T>(string n, Func<T> f) { using (Diagnostics.Prof.Time(n)) return f(); }
    static readonly bool LinearSideOff = Environment.GetEnvironmentVariable("P3D_BANDLINSIDE") == "0";
    static readonly bool BandRangeCacheOff = Environment.GetEnvironmentVariable("P3D_BANDRANGECACHE") == "0";

    static (Func<double, double> L, Func<double, double> R)? BandOpenRangeCore(TFace f, SectionSurface ss)
    {
        bool at0 = false, at1 = false; var sides = new List<(ICurve3 C, double UMid)>();
        foreach (var ce in f.Loops[0].CoEdges)
        {
            var c = ce.Edge.Curve;
            var uv = Enumerable.Range(0, 11).Select(k => ss.ProjectToUV(c.PointAt(c.T0 + (c.T1 - c.T0) * k / 10.0))).ToList();
            if (uv.All(q => Math.Abs(q.Y) < 1e-6)) { at0 = true; continue; }
            if (uv.All(q => Math.Abs(q.Y - 1) < 1e-6)) { at1 = true; continue; }
            double va = uv[0].Y, vb = uv[^1].Y;
            if (!(Math.Min(Math.Abs(va), Math.Abs(vb)) < 1e-6 && Math.Abs(Math.Max(va, vb) - 1) < 1e-6)) return null;
            int sg = Math.Sign(vb - va);
            for (int k = 1; k < uv.Count; k++) if (Math.Sign(uv[k].Y - uv[k - 1].Y) != sg) return null;
            sides.Add((c, uv[5].X));
        }
        if (!at0 || !at1 || sides.Count != 2) return null;
        sides.Sort((a, b) => a.UMid.CompareTo(b.UMid));
        Func<double, double> Make(ICurve3 c)
        {
            bool inc = ss.ProjectToUV(c.PointAt(c.T1)).Y > ss.ProjectToUV(c.PointAt(c.T0)).Y;
            // u12: БОКОВОЕ РЕБРО — САМО СЕЧЕНИЕ ИЛИ КРАЙ ПО ДОЛЕ v: параметр ребра линеен по v (сечение-дуга по углу, сечение-
            // отрезок, край ленты на торце из корней по v) — точка с данным v берётся прямо, без поиска по ребру.
            // только сечения (дуга, отрезок) и края ленты на торце, построенные корнем по доле v; шов «на ус» (кубика у вершины)
            // и прочие процедурные кривые — прежним поиском
            if (!LinearSideOff && (c is ArcCurve3 or LineCurve3 || c is ProcCurve3 { Kind: "blend end" or "rolling-ball end" or "chamfer end" }))
            {
                bool lin = true;
                foreach (double t in new[] { 0, 1e-3, 3e-3, 0.01, 0.05, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 0.95, 0.99, 0.997, 0.999, 1 })
                {
                    if (!lin) break;
                    double vq = ss.ProjectToUV(c.PointAt(c.T0 + (c.T1 - c.T0) * t)).Y;
                    if (Math.Abs(vq - (inc ? t : 1 - t)) > 1e-10) lin = false;
                }
                if (lin) return v => ss.ProjectToUV(c.PointAt(c.T0 + (c.T1 - c.T0) * (inc ? v : 1 - v))).X;
            }
            // точка ребра с данным v — Иллинойс по параметру ребра (v монотонно), до 1e-14 доли промежутка
            return v =>
            {
                double lo = c.T0, hi = c.T1;
                double G(double s) { double vm = ss.ProjectToUV(c.PointAt(s)).Y - v; return inc ? vm : -vm; }
                double flo = G(lo), fhi = G(hi);
                if (flo >= 0) return ss.ProjectToUV(c.PointAt(lo)).X;
                if (fhi <= 0) return ss.ProjectToUV(c.PointAt(hi)).X;
                int side = 0; double md = (lo + hi) / 2;
                for (int it = 0; it < 100; it++)
                {
                    md = (lo * fhi - hi * flo) / (fhi - flo); double fm = G(md);
                    if (fm == 0 || hi - lo < 1e-14 * Math.Max(1, Math.Abs(c.T1 - c.T0))) break;
                    if (fm < 0) { lo = md; flo = fm; if (side == -1) fhi /= 2; side = -1; }
                    else { hi = md; fhi = fm; if (side == 1) flo /= 2; side = 1; }
                    if (Math.Abs(fm) < 1e-15) break;
                }
                return ss.ProjectToUV(c.PointAt(md)).X;
            };
        }
        return (Make(sides[0].C), Make(sides[1].C));
    }

    /// <summary>∫∫ (x − o)·N dA (или площадь) грани незамкнутой ленты: Гаусс 12 по v (16 кусков, грубый — 8), по u — Гаусс 12 на
    /// [L(v), R(v)] (48 кусков, грубый — 24); сторона — как у BandIntegral.</summary>
    static double? BandOpenIntegral(TFace f, SectionSurface ss, (Func<double, double> L, Func<double, double> R) rg, bool area)
    {
        double per = ss.UMax - ss.UMin, h = 1e-6 * Math.Max(1e-9, per);
        int mv = _rough ? 3 : _coarse > 0 ? 8 : 16, mu = _rough ? 8 : _coarse > 0 ? 24 : 48;
        var (gx, gw) = Gauss12;
        double vol = 0, ar = 0; var va = Vector3d.Zero;
        for (int iv = 0; iv < mv; iv++)
            for (int kv = 0; kv < gx.Length; kv++)
            {
                double v = (iv + (gx[kv] + 1) / 2) / mv, wv = gw[kv] / 2 / mv;
                double uL = rg.L(v), uR = rg.R(v); if (!(uR > uL)) return null;
                // куски по стыкам формулы (узлы закона радиуса): через излом квадратура теряет порядок
                var cutsU = new List<double> { uL };
                foreach (var bk in ss.Breaks) if (bk > uL + 1e-12 && bk < uR - 1e-12 && !BandBreaksOff) cutsU.Add(bk);
                cutsU.Add(uR);
                for (int piece = 0; piece + 1 < cutsU.Count; piece++)
                {
                double pa = cutsU[piece], pb = cutsU[piece + 1];
                int mp = Math.Max(4, (int)Math.Ceiling(mu * (pb - pa) / (uR - uL)));
                double pw = (pb - pa) / mp;
                for (int i = 0; i < mp; i++)
                    for (int j = 0; j < gx.Length; j++)
                    {
                        double u = pa + pw * (i + (gx[j] + 1) / 2), w = gw[j] / 2 * pw * wv;
                        var s0 = ss.SectionAt(u);
                        var P = s0.Point(v); var su = (ss.SectionAt(u + h).Point(v) - ss.SectionAt(u - h).Point(v)) / (2 * h); var sv = s0.Deriv(v);
                        var c = Vector3d.Cross(su, sv);
                        vol += Vector3d.Dot(P - _origin, c) * w; ar += c.Length * w; va += c * w;
                    }
                }
            }
        Vector3d loopsVa; using (Diagnostics.Prof.Time("лента: нормаль по контуру")) loopsVa = NormalIntegral(f);
        if (loopsVa.Length < 1e-6 * Math.Max(1e-12, ar) || va.Length < 1e-6 * Math.Max(1e-12, ar)) return null;
        double cosv = Vector3d.Dot(loopsVa, va) / (loopsVa.Length * va.Length);
        if (Math.Abs(cosv) < 0.999) return null;
        return area ? ar : (cosv > 0 ? vol : -vol);
    }
    static readonly bool BandBreaksOff = Environment.GetEnvironmentVariable("P3D_VOLBANDBREAKS") == "0";

    static readonly (double[] X, double[] W) Gauss12 = (
        new[] { -0.9815606342467192, -0.9041172563704749, -0.7699026741943047, -0.5873179542866175, -0.3678314989981802, -0.1252334085114689,
                 0.1252334085114689, 0.3678314989981802, 0.5873179542866175, 0.7699026741943047, 0.9041172563704749, 0.9815606342467192 },
        new[] { 0.0471753363865118, 0.1069393259953184, 0.1600783285433462, 0.2031674267230659, 0.2334925365383548, 0.2491470458134028,
                0.2491470458134028, 0.2334925365383548, 0.2031674267230659, 0.1600783285433462, 0.1069393259953184, 0.0471753363865118 });

    static int StepsFor(ICurve3 c) => _coarse > 0 ? Math.Max(8, (int)(Steps * _coarse)) : Steps;

    static bool HasTracedCurves(TFace f) =>
        f.Loops.Any(l => l.CoEdges.Any(ce => ce.Edge.Curve is not (LineCurve3 or ArcCurve3 or EllipseCurve3)))
        // g.05/g.04: и кривые грани, которые интегрируются трапециями (дуги и эллипсы на сфере, конусе, торе): их ошибка тоже
        // измеряется проходом Ричардсона. Плоскость и цилиндр — в замкнутом виде. Мутация P3D_QUADALL=0.
        || (!QuadAllOff && QuadratureFace(f) && f.Loops.Any(l => l.CoEdges.Any(ce => ce.Edge.Curve is ArcCurve3 or EllipseCurve3)));

    static bool QuadratureFace(TFace f)
    {
        var s = GeometryAudit.AuthoritativeSurface(f);
        if (s == null || s.IsPlanar || FlatRevolved(s) != null) return false;
        if (s is CylinderSurface) return CylClosedOff;
        if (s is ExtrudedSurface ex) return !(ex.Base is ArcCurve3) || CylClosedOff;
        return true;
    }

    /// <summary>∫∫ x·n dA по грани, через край. null — поверхность не поддержана.</summary>
    public static double? FaceIntegral(TFace f)
    {
        if (_coarse == 0 && HasTracedCurves(f))
        {
            double? fine = FaceIntegralCore(f);
            if (fine == null) return null;
            _coarse = 0.5;
            double? coarse;
            try { coarse = FaceIntegralCore(f); } finally { _coarse = 0; }
            // g.04: ОЦЕНКА ОШИБКИ КВАДРАТУРЫ — |I_h − I_2h| / 3 (главный член ошибки I_h; экстраполированное значение точнее —
            // оценка консервативна). Без грубого прохода ошибка неизвестна: 1e-6 от величины.
            _quadErr += coarse == null ? 1e-6 * Math.Abs(fine.Value) : Math.Abs(fine.Value - coarse.Value) / 3;
            QuadDebug?.Invoke(f, fine.Value, coarse ?? double.NaN);
            return coarse == null ? fine : fine + (fine - coarse) / 3;
        }
        return FaceIntegralCore(f);
    }

    private static double? FaceIntegralCore(TFace f)
    {
        var s = GeometryAudit.AuthoritativeSurface(f);
        Func<Vector3d, (double A, double B)>? coords = null;   // параметры для 1-формы
        Func<double, double, double, double, double>? form = null; // (a0,b0,a1,b1) → ∫ ω по малому отрезку
        double factor = 1;
        bool periodicB = false;

        if (FlatRevolved(s) is { } frv) return PlaneIntegral(f, frv.P0, frv.U, frv.V);
        switch (s)
        {
            case PlaneSurface pl:
                return PlaneIntegral(f, pl.F.Origin, pl.F.U, pl.F.V);
            case ExtrudedSurface exl when exl.Base is LineCurve3 ln:
            {
                var u = (ln.B - ln.A).Normalized();
                var n = Vector3d.Cross(u, exl.Dir.Normalized());
                if (n.LengthSquared < 1e-18) return null;
                n = n.Normalized();
                return PlaneIntegral(f, ln.A, u, Vector3d.Cross(n, u));
            }
            case ExtrudedSurface ex when ex.Base is ArcCurve3 arc && ex.Dir.LengthSquared > 1e-18
                                      && Math.Abs(Vector3d.Dot(arc.Frame.W.Normalized(), ex.Dir.Normalized())) > 1 - 1e-12:
                return CylinderIntegral(f, arc.Frame.Origin, ex.Dir.Normalized(), arc.Frame.U.Normalized(), arc.Radius);
            case CylinderSurface cs:
                return CylinderIntegral(f, cs.Axis.Origin, cs.Axis.W.Normalized(), cs.Axis.U.Normalized(), cs.Radius);
            case RevolvedSurface rv when rv.Base is ArcCurve3 ba && Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) < 1e-9:
            {
                var axis = rv.Axis.W.Normalized();
                var d = ba.Frame.Origin - rv.Axis.Origin;
                if ((d - axis * Vector3d.Dot(d, axis)).Length > 1e-9) return RevolvedIntegral(f, rv);   // тор и т.п.
                return SphereIntegral(f, ba.Frame.Origin, ba.Radius);
            }
            case RevolvedSurface rv2:
                return RevolvedIntegral(f, rv2);
            case SectionSurface band when !BandOff && BandRange(f, band) != null:
                return BandIntegral(f, band, area: false);
            case SectionSurface ob when BandOpenRange(f, ob) is { } orr:
                return BandOpenIntegral(f, ob, orr, area: false);
            case CornerPatchSurface cpv when WholePatch(f, cpv):
            {
                var oc = _origin;
                return CornerIntegral(f, cpv, (x, n) => Vector3d.Dot(x - oc, n), area: false);
            }
            case NurbsSurface when !NurbsVolOff:
            {
                // KRN-07 N1b: грань NURBS — поток (x − o) по краю (AnalyticFlux, общая параметризация (u, v))
                var o = _origin;
                return AnalyticFlux.FaceFluxOf(f, x => x - o);
            }
            default:
                _ = coords; _ = form; _ = factor; _ = periodicB;
                return null;
        }
    }

    // ------------------------------------------------------------------ угловой лоскут
    /// <summary>
    /// KRN-06: грань — весь угловой лоскут (один контур из всех сторон): интеграл по самой области параметров (2D квадратура,
    /// CornerPatchSurface.Integrate), а не через край — через край каждый узел края стоил внутреннего интеграла с особыми
    /// производными в вершинах области (5 с на грань). Ошибка — разность N и 2N (в бюджет квадратуры). Часть лоскута
    /// (грань, обрезанная после) — не поддержана (null). Мутация P3D_CORNERQUAD=0 — интеграла нет.
    /// </summary>
    static bool WholePatch(TFace f, CornerPatchSurface cp) => !CornerQuadOff && f.Loops.Count == 1 && f.Loops[0].CoEdges.Count == cp.SideCount;
    static readonly bool CornerQuadOff = Environment.GetEnvironmentVariable("P3D_CORNERQUAD") == "0";
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CornerPatchSurface, System.Collections.Concurrent.ConcurrentDictionary<(bool, Vector3d), (double V, double Err)>> CornerMemo = new();
    static double? CornerIntegral(TFace f, CornerPatchSurface cp, Func<Vector3d, Vector3d, double> g, bool area)
    {
        // сходимость — четвёртого порядка (удвоение N — ошибка /16): N = 16 и 8, экстраполяция Ричардсона, остаток —
        // разность /15 (замер: площадь 1e-7 → 3e-8 отн., поток — 2e-9 отн.). Значение — в памяти лоскута (по началу отсчёта).
        var memo = CornerMemo.GetOrCreateValue(cp);
        var key = (area, area ? Vector3d.Zero : _origin);
        if (!memo.TryGetValue(key, out var res))
        {
            double fine = cp.Integrate(g, 16), coarse = cp.Integrate(g, 8);
            if (!double.IsFinite(fine) || !double.IsFinite(coarse)) return null;
            res = (fine + (fine - coarse) / 15, Math.Abs(fine - coarse) / 15);
            memo[key] = res;
        }
        _quadErr += res.Err;
        if (area) return Math.Abs(res.V);
        // сторона — по обходу контура в области параметров (соглашение K05: внешний контур — против часовой вокруг наружной
        // нормали): обход против часовой в (u, v) — наружу S_u × S_v. Флаг Reversed для этого не годится: зеркальный перенос
        // меняет сторону через обход, а не через флаг.
        var uvl = f.Loops[0].CoEdges.Select(ce => cp.ProjectToUV(ce.Start.P)).ToList();
        double sa = 0; for (int i = 0; i < uvl.Count; i++) { var a0 = uvl[i]; var b0 = uvl[(i + 1) % uvl.Count]; sa += a0.X * b0.Y - b0.X * a0.Y; }
        if (Math.Abs(sa) < 1e-9) return null;
        return res.V * Math.Sign(sa);
    }

    // ------------------------------------------------------------------ обход края

    /// <summary>Точки каждого контура грани по настоящим кривым рёбер, в направлении обхода.</summary>
    private static IEnumerable<List<Vector3d>> Loops(TFace f)
    {
        foreach (var loop in f.Loops)
        {
            var pts = new List<Vector3d>();
            foreach (var ce in loop.CoEdges)
            {
                var e = ce.Edge;
                var c = e.Curve;
                // Направление кривой относительно V0 → V1.
                bool curveForward = (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared
                                    || ReferenceEquals(e.V0, e.V1);
                bool forward = ce.Reversed ? !curveForward : curveForward;
                // Шагов по ребру: отрезку — один; дуге и эллипсу — по углу (не меньше 32 на оборот
                // доли, ошибка трапеций ~ шаг²); прочим кривым — полное число.
                int n = c switch
                {
                    LineCurve3 => 1,
                    ArcCurve3 ac => Math.Max(32, (int)Math.Ceiling(Math.Abs(ac.Sweep) / (2 * Math.PI) * Steps)),
                    EllipseCurve3 ec => Math.Max(32, (int)Math.Ceiling(Math.Abs(ec.Sweep) / (2 * Math.PI) * Steps)),
                    _ => StepsFor(c)
                };
                // g.04: дуги грани с кривыми без замкнутой формулы — тоже вдвое реже на грубом проходе Ричардсона: иначе их
                // ошибка трапеций (шаг 0.075 мм у дуги края на шаре — 1e-4 от интеграла) не исключалась и не входила в оценку.
                if (_coarse > 0 && !ArcCoarseOff && c is ArcCurve3 or EllipseCurve3) n = Math.Max(8, (int)(n * _coarse));
                var seg = new List<Vector3d>(n + 1);
                for (int i = 0; i <= n; i++)
                {
                    double t = c.T0 + (c.T1 - c.T0) * i / n;
                    seg.Add(c.PointAt(t));
                }
                if (!forward) seg.Reverse();
                if (pts.Count > 0) seg.RemoveAt(0);
                pts.AddRange(seg);
            }
            yield return pts;
        }
    }

    /// <summary>Диагностика: площадь каждого контура плоской грани в её базисе.</summary>
    public static List<double> LoopAreas(TFace f, Vector3d p0, Vector3d u, Vector3d v)
    {
        var res = new List<double>();
        foreach (var pts in Loops(f))
        {
            double area = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double ua = Vector3d.Dot(pts[i] - p0, u), va = Vector3d.Dot(pts[i] - p0, v);
                double ub = Vector3d.Dot(pts[i + 1] - p0, u), vb = Vector3d.Dot(pts[i + 1] - p0, v);
                area += 0.5 * (ua * vb - va * ub);
            }
            res.Add(area);
            res.Add((pts[0] - pts[^1]).Length);
        }
        return res;
    }

    private static double PlaneIntegral(TFace f, Vector3d p0, Vector3d u, Vector3d v)
    {
        u = u.Normalized(); v = (v - u * Vector3d.Dot(v, u)).Normalized();
        return Vector3d.Dot(Vector3d.Cross(u, v), p0 - _origin) * PlaneLoopArea(f, p0, u, v);
    }

    static readonly bool SeamWholeOff = Environment.GetEnvironmentVariable("P3D_SEAMWHOLE") == "0";
    static readonly bool CylClosedOff = Environment.GetEnvironmentVariable("P3D_CYLCLOSED") == "0";
    static readonly bool QuadAllOff = Environment.GetEnvironmentVariable("P3D_QUADALL") == "0";

    /// <summary>
    /// g.05/g.04: ∮ −z R² dθ ПО ГРАНИ ЦИЛИНДРА — у плоских кривых (дуга, эллипс) В ЗАМКНУТОМ ВИДЕ: точка кривой на цилиндре
    /// и в плоскости n·(x − o) = K, откуда z(θ) = (K − R(n·u cos θ + n·v sin θ)) / n·w и ∫z dθ = (Kθ − R(n·u sin θ − n·v cos θ))/n·w.
    /// Прежде — трапеции по 1024 точкам на оборот: у эллипса (наклонный срез цилиндра) z(θ) — синусоида, ошибка трапеций
    /// 0.015 мм³ на повёрнутом цилиндре минус брусок (эталон — интеграл площадей сечений, scipy, и OpenCascade — совпали) при
    /// заявленной границе 1.5e-4 (ошибка квадратуры не оценивалась: эллипс считался «точной» кривой). Прочие кривые — трапеции.
    /// Мутация P3D_CYLCLOSED=0.
    /// </summary>
    private static double CylinderClosed(TFace f, Vector3d o, Vector3d w, Vector3d u, Vector3d v, double R)
    {
        double sum = 0;
        double Th(Vector3d p) { var a = p - o; return Math.Atan2(Vector3d.Dot(a, v), Vector3d.Dot(a, u)); }
        double Wr(double d) { while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; return d; }
        foreach (var loop in f.Loops)
            foreach (var ce in loop.CoEdges)
            {
                var e = ce.Edge; var c = e.Curve;
                bool curveForward = (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared || ReferenceEquals(e.V0, e.V1);
                bool forward = ce.Reversed ? !curveForward : curveForward;
                Vector3d P(double s) => c.PointAt(forward ? c.T0 + (c.T1 - c.T0) * s : c.T1 - (c.T1 - c.T0) * s);
                Vector3d? nc = c switch { ArcCurve3 ar => ar.Frame.W.Normalized(), EllipseCurve3 el => el.Frame.W.Normalized(), _ => null };
                Vector3d? oc = c switch { ArcCurve3 ar => ar.Frame.Origin, EllipseCurve3 el => el.Frame.Origin, _ => null };
                if (nc is { } n && oc is { } q0 && Math.Abs(Vector3d.Dot(n, w)) > 1e-9)
                {
                    // полный поворот угла вдоль кривой — по выборке (кривая на цилиндре — график над θ)
                    const int m = 64;
                    double ta = Th(P(0)), acc = 0, prev = ta;
                    for (int i = 1; i <= m; i++) { double t = Th(P((double)i / m)); acc += Wr(t - prev); prev = t; }
                    double K = Vector3d.Dot(n, q0 - o), nu = Vector3d.Dot(n, u), nv = Vector3d.Dot(n, v), nw = Vector3d.Dot(n, w);
                    double F(double t) => (K * t - R * (nu * Math.Sin(t) - nv * Math.Cos(t))) / nw;
                    sum += -R * R * (F(ta + acc) - F(ta));
                    continue;
                }
                int k = c switch { LineCurve3 => 1, _ => StepsFor(c) };
                if (_coarse > 0 && !ArcCoarseOff && c is ArcCurve3 or EllipseCurve3) k = Math.Max(8, (int)(Math.Max(32, Steps) * _coarse));
                var a0 = P(0) - o;
                for (int i = 1; i <= k; i++)
                {
                    var b0 = P((double)i / k) - o;
                    double dt = Wr(Math.Atan2(Vector3d.Dot(b0, v), Vector3d.Dot(b0, u)) - Math.Atan2(Vector3d.Dot(a0, v), Vector3d.Dot(a0, u)));
                    sum += -0.5 * (Vector3d.Dot(a0, w) + Vector3d.Dot(b0, w)) * R * R * dt;
                    a0 = b0;
                }
            }
        return sum;
    }

    private static double CylinderIntegral(TFace f, Vector3d o, Vector3d w, Vector3d u0, double R)
    {
        var u = (u0 - w * Vector3d.Dot(u0, w)).Normalized();
        var v = Vector3d.Cross(w, u);
        // Координаты отсчитываются от o: тогда o_u = o_v = 0 и g = R².
        double sum = 0;
        if (!CylClosedOff) return CylinderClosed(f, o, w, u, v, R) + Vector3d.Dot(o - _origin, NormalIntegral(f));
        foreach (var pts in Loops(f))
        {
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i] - o; var b = pts[i + 1] - o;
                double ta = Math.Atan2(Vector3d.Dot(a, v), Vector3d.Dot(a, u));
                double tb = Math.Atan2(Vector3d.Dot(b, v), Vector3d.Dot(b, u));
                double dt = tb - ta;
                while (dt > Math.PI) dt -= 2 * Math.PI;
                while (dt < -Math.PI) dt += 2 * Math.PI;
                double za = Vector3d.Dot(a, w), zb = Vector3d.Dot(b, w);
                sum += -0.5 * (za + zb) * R * R * dt;
            }
        }
        // ∫∫ x·n dA для координат с началом в o; сдвиг начала добавляет o·∫∫ n dA.
        return sum + Vector3d.Dot(o - _origin, NormalIntegral(f));
    }

    private static double SphereIntegral(TFace f, Vector3d centre, double R)
    {
        // KRN-03: ЦЕЛАЯ СФЕРА (грань без края): ∫∫ (x − c)·n dA = ±R·4πR², знак — по наружной нормали грани; сдвиг начала
        // вклада не даёт (∮ n dA = 0 по замкнутой поверхности). Прежде — NaN (Стокс через край неприменим), объём шара и
        // знак сферической полости не считались.
        if (f.Loops.Count == 0 || f.Loops.All(l => l.CoEdges.Count == 0)
            || (!SeamWholeOff && f.Loops.SelectMany(l => l.CoEdges).GroupBy(ce => ce.Edge).All(g => g.Count() == 2)))
        {
            // g.05/KRN-09: и грань, вся граница которой — швы (каждое ребро дважды): целая сфера (прочитанная из STEP). P3D_SEAMWHOLE=0.
            var px = centre + Vector3d.UnitX * R;
            return Vector3d.Dot(f.NormalAt(px), Vector3d.UnitX) >= 0 ? 4 * Math.PI * R * R * R : -4 * Math.PI * R * R * R;
        }
        if (SphereClosed(f, centre, R, out double ar0, out var vecA0, out double sd0))
        {
            SphereClosedUsed++; return Vector3d.Dot(centre - _origin, vecA0) + sd0 * R * ar0;
        }
        // g.05: ПЛОЩАДЬ СФЕРИЧЕСКОЙ ГРАНИ — ТЕЛЕСНЫМ УГЛОМ КОНТУРОВ, без выбора полюса формы. Прежде форма R³(1 − cos φ)dθ
        // требовала полюс, противоположный которому лежит ВНЕ грани; у шара с малым отверстием (цилиндр задевает шар на 0.1 мм,
        // отверстие 0.8 × 2.8 мм) выбранный по треугольникам полюс промахивался мимо отверстия, интеграл грани выходил −28 вместо
        // 12538, объём вычитания — −0.13 (отказ маршрута). Теперь ∫∫(x − c)·n dA = ±R·S, S — по телесным углам веера каждого
        // контура (обход «материал слева»), без особых точек. Мутация P3D_SPHAREA=0.
        if (!SphAreaOff && SphereAreaBySolidAngle(f, centre, R) is { } sa)
            return sa.Side * R * sa.Area + Vector3d.Dot(centre - _origin, NormalIntegral(f));
        double poly = SphereIntegralPolyline(f, centre, R);
        if (SphereClosed(f, centre, R, out double ar, out var vecA, out double sd))
        {
            double closed = Vector3d.Dot(centre - _origin, vecA) + sd * R * ar;
            SphereClosedUsed++; return closed;
        }
        return poly;
    }

    static readonly bool ArcCoarseOff = Environment.GetEnvironmentVariable("P3D_ARCCOARSE") == "0";
    static readonly bool PoleDeepOff = Environment.GetEnvironmentVariable("P3D_SPHPOLEDEEP") == "0";

    private static double SphereIntegralPolyline(TFace f, Vector3d centre, double R)
    {
        // Полюс — по средней ТОЧКЕ грани на сфере (особая точка формы — противоположный полюс,
        // он должен лежать вне грани). Не по нормали грани: у полости нормаль смотрит к центру,
        // и полюс по ней попадал прямо на грань — объём уходил на 4πR³/3.
        var mean = Vector3d.Zero;
        if (f.Surface is FacetedSurface fsm)
            foreach (var (x, y, z) in fsm.Facets)
                mean += ((x + y + z) / 3 - centre) * Vector3d.Cross(y - x, z - x).Length;
        else foreach (var pts in Loops(f)) foreach (var q in pts) mean += q - centre;
        Vector3d w = mean.LengthSquared > 1e-18 ? mean.Normalized() : Vector3d.UnitZ;
        // g.04: ПОЛЮС — ГЛУБЖЕ В ГРАНИ. Трапеции по θ точны, пока край не проходит рядом с полюсом w (там θ меняется быстро).
        // Средняя точка грани-пояса (шар внутри конуса между двумя кольцами) лежала у края: интеграл грани на 0.019 мм³ мимо
        // (пересечение 1.3395 вместо 1.3333, найдено разностным сравнением, граница 1.8e-4). Теперь из центров треугольников
        // грани берётся наиболее удалённый от края. Мутация P3D_SPHPOLEDEEP=0.
        if (!PoleDeepOff && f.Surface is FacetedSurface fsd && fsd.Facets.Count > 0)
        {
            var edgeDirs = Loops(f).SelectMany(l => l.Where((_, k) => k % 8 == 0)).Select(q => (q - centre).Normalized()).ToList();
            if (edgeDirs.Count > 0)
            {
                double best = 2; Vector3d? bw = null;
                foreach (var (x, y, z) in fsd.Facets.ReadView.Where((_, k) => k % Math.Max(1, fsd.Facets.Count / 200) == 0))
                {
                    var d = ((x + y + z) / 3 - centre).Normalized();
                    double near = edgeDirs.Max(e => Vector3d.Dot(e, d));
                    if (near < best) { best = near; bw = d; }
                }
                if (bw is { } bb) w = bb;
            }
        }
        var u = Math.Abs(w.X) < 0.9 ? Vector3d.Cross(w, Vector3d.UnitX).Normalized() : Vector3d.Cross(w, Vector3d.UnitY).Normalized();
        var v = Vector3d.Cross(w, u);
        // Особый полюс −w не должен лежать на грани: проверка по её треугольникам.
        if (f.Surface is FacetedSurface fsp)
            foreach (var (x, y, z) in fsp.Facets)
                if (Vector3d.Dot(((x + y + z) / 3 - centre).Normalized(), -w) > 1 - 1e-4) return double.NaN;
        double sum = 0;
        foreach (var pts in Loops(f))
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = (pts[i] - centre).Normalized(); var b = (pts[i + 1] - centre).Normalized();
                double pa = Math.Acos(Math.Clamp(Vector3d.Dot(a, w), -1, 1)), pb = Math.Acos(Math.Clamp(Vector3d.Dot(b, w), -1, 1));
                if (Math.PI - Math.Max(pa, pb) < 1e-6) return double.NaN;    // край у особого полюса
                double ta = Math.Atan2(Vector3d.Dot(a, v), Vector3d.Dot(a, u)), tb = Math.Atan2(Vector3d.Dot(b, v), Vector3d.Dot(b, u));
                double dt = tb - ta;
                while (dt > Math.PI) dt -= 2 * Math.PI;
                while (dt < -Math.PI) dt += 2 * Math.PI;
                // Центр в начале координат: x·n̂ = R, форма R³(1 − cos φ) dθ.
                sum += R * R * R * (1 - 0.5 * (Math.Cos(pa) + Math.Cos(pb))) * dt;
            }
        if (SphDbg) Console.Error.WriteLine($"SPHDBG face {f.Tag} w {w} sum {sum} nint {NormalIntegral(f)} coarse {_coarse}");
        return sum + Vector3d.Dot(centre - _origin, NormalIntegral(f));
    }

    static readonly bool SphHintSideOff = Environment.GetEnvironmentVariable("P3D_SPHHINTSIDE") == "0";
    /// <summary>
    /// g.05: СТОРОНА СФЕРИЧЕСКОЙ ГРАНИ (наружу ли от центра её нормаль) — по точке грани, а не по нормали фасеты на краю (у
    /// граней с фасетами это нормаль ближайшей фасеты, и на краю у вырезанной области она бывала чужой: шар минус конус,
    /// шар ∪ тор — площадь 80 вместо ~1674, серия 8, случаи 16, 47). Область слева от обхода контуров при взгляде снаружи
    /// (сторона +1) содержит точку грани ⟺ дуга большого круга от точки грани к точке слева от контура пересекает контуры
    /// чётное число раз. null — нет точки грани или неоднозначно (точка у края, антипод). Мутация P3D_SPHHINTSIDE=0.
    /// </summary>
    internal static double? SphereSideByHint(TFace f, Vector3d c, double R)
    {
        if (SphHintSideOff || f.InteriorHint is not { } hint) return null;
        if (Math.Abs((hint - c).Length - R) > 1e-6 * Math.Max(1, R)) return null;     // точка не на сфере грани — устарела
        var loops = Loops(f).Where(l => l.Count >= 3).ToList();
        if (loops.Count == 0) return null;
        var h = (hint - c).Normalized();
        // опорная точка: слева от самого длинного звена первого контура (взгляд снаружи), чуть внутрь
        var l0 = loops[0]; int k = 0; double bl = -1;
        for (int i = 0; i + 1 < l0.Count; i++) { double d = (l0[i + 1] - l0[i]).Length; if (d > bl) { bl = d; k = i; } }
        var m = ((l0[k] + l0[k + 1]) / 2 - c).Normalized(); var t = (l0[k + 1] - l0[k]).Normalized();
        var left = Vector3d.Cross(m, t);                      // N_out × T
        double eps = Math.Min(1e-3, 0.05 * bl / R);
        var r = (m + left * eps).Normalized();
        if (Vector3d.Dot(h, r) < -0.999) return null;           // почти антипод — дуга не определена
        var nAB = Vector3d.Cross(h, r); if (nAB.LengthSquared < 1e-24) return 1;
        int cross = 0;
        foreach (var l in loops)
            for (int i = 0; i + 1 < l.Count; i++)
            {
                var P = (l[i] - c).Normalized(); var Q = (l[i + 1] - c).Normalized();
                double sp = Vector3d.Dot(nAB, P), sq = Vector3d.Dot(nAB, Q);
                if ((sp > 0) == (sq > 0)) continue;
                var nPQ = Vector3d.Cross(P, Q); double sh = Vector3d.Dot(nPQ, h), sr = Vector3d.Dot(nPQ, r);
                if ((sh > 0) == (sr > 0)) continue;
                var x = Vector3d.Cross(nAB, nPQ); if (x.LengthSquared < 1e-30) return null;
                x.Normalize(); if (Vector3d.Dot(x, h + r) < 0) x = -x;
                if (Vector3d.Dot(x, P + Q) < 0) continue;
                cross++;
            }
        return (cross % 2 == 0) ? 1 : -1;
    }

    /// <summary>
    /// Телесный угол области грани: для контура i — сумма знаковых сферических треугольников веера ИЗ ТОЧКИ ГРАНИ h (телесный
    /// угол его левой стороны по модулю 4π; веер из точки самого контура вырождался у контура по большому кругу: полусфера —
    /// площадь 23.5 вместо 1413.7), сторона грани — чётностью пути от h (SphereSideByHint); Ω = Σ(сторона·Ω_i mod 4π) − 4π(L − 1).
    /// </summary>
    static (double Area, double Side)? SphereFanByHint(TFace f, Vector3d c, double R)
    {
        if (SphHintSideOff || f.InteriorHint is not { } hint) return null;
        if (SphereSideByHint(f, c, R) is not double side) return null;
        var loops = Loops(f).Where(l => l.Count >= 3).ToList();
        if (loops.Count == 0) return null;
        var h = (hint - c).Normalized();
        double om = 0;
        foreach (var pts in loops)
        {
            double sum = 0;
            int n = pts.Count; bool closedL = (pts[0] - pts[^1]).Length <= 1e-9;
            for (int i = 0; i < (closedL ? n - 1 : n); i++)
            {
                var b = (pts[i] - c).Normalized(); var cc = (pts[(i + 1) % n] - c).Normalized();
                double den = 1 + Vector3d.Dot(h, b) + Vector3d.Dot(b, cc) + Vector3d.Dot(cc, h);
                if (Math.Abs(den) < 1e-12 && Math.Abs(Vector3d.Dot(h, Vector3d.Cross(b, cc))) < 1e-12) return null;   // −h на контуре
                sum += 2 * Math.Atan2(Vector3d.Dot(h, Vector3d.Cross(b, cc)), den);
            }
            double o = (side * sum) % (4 * Math.PI); if (o < 0) o += 4 * Math.PI;
            om += o;
        }
        om -= 4 * Math.PI * (loops.Count - 1);
        if (!(om > 0 && om < 4 * Math.PI * (1 + 1e-12))) return null;
        return (R * R * om, side);
    }

    static readonly bool SphDbg = Environment.GetEnvironmentVariable("P3D_SPHDBG") == "1";
    static readonly bool FanOff = Environment.GetEnvironmentVariable("P3D_VOLFAN") == "0";

    static readonly bool SphAreaOff = Environment.GetEnvironmentVariable("P3D_SPHAREA") == "0";

    /// <summary>
    /// Площадь сферической грани по её контурам: телесный угол веера Ω = Σ 2·atan2(a·(b×c), 1 + a·b + b·c + c·a) по ломаной
    /// контура (точки — по настоящим кривым, Loops); область слева от контура (взгляд со стороны нормали грани) — Ω mod 4π; у
    /// грани из L контуров S = R²(Σ(Ω_i mod 4π) − 4π(L − 1)). Сторона — нормаль грани от центра (+1) или к центру (−1).
    /// null — нет контуров (целая сфера обрабатывается выше) или площадь вне (0, 4πR²).
    /// </summary>
    static (double Area, double Side)? SphereAreaBySolidAngle(TFace f, Vector3d c, double R)
    {
        if (SphereFanByHint(f, c, R) is { } fan) return fan;
        var loops = Loops(f).Where(l => l.Count >= 3).ToList();
        if (loops.Count == 0) return null;
        var p0 = loops[0][0];
        double side = Vector3d.Dot(f.NormalAt(p0), p0 - c) >= 0 ? 1 : -1;
        double total = 0;
        foreach (var pts in loops)
        {
            var u = pts.Select(q => (q - c).Normalized()).ToList();
            if ((pts[0] - pts[^1]).Length < 1e-12) u.RemoveAt(u.Count - 1);
            double om = 0;
            for (int i = 1; i + 1 < u.Count; i++)
            {
                var a = u[0]; var b = u[i]; var cc = u[i + 1];
                om += 2 * Math.Atan2(Vector3d.Dot(a, Vector3d.Cross(b, cc)), 1 + Vector3d.Dot(a, b) + Vector3d.Dot(b, cc) + Vector3d.Dot(cc, a));
            }
            om *= side;
            om %= 4 * Math.PI; if (om < 0) om += 4 * Math.PI;
            total += om;
        }
        double area = R * R * (total - 4 * Math.PI * (loops.Count - 1));
        if (!(area > 0 && area < 4 * Math.PI * R * R * (1 + 1e-12))) return null;
        return (area, side);
    }

    /// <summary>
    /// Поверхность вращения общего вида (конус, тор, вращение отрезка): точка = o + z(s)w + ρ(s)r̂(θ),
    /// где (z, ρ) — образующая в меридиане. (x − o)·(r_θ × r_s) = ρ²z′ − zρρ′ =: h(s), и
    /// ∫∫ (x − o)·n dA = ∮ −H(s) dθ, H′ = h. H табулируется по образующей; s точки края — по её
    /// (z, ρ). Грань, содержащая полюс (ρ = 0 внутри), не поддержана: форма там особая.
    /// </summary>
    /// <summary>KRN-02: отказы замкнутой формулы образующей-дуги (дуга не в меридиане и т. п.) — разбор.</summary>
    public static int ArcFormulaRejected;

    /// <summary>Площадь всей поверхности полного вращения: 2π·∫ρ ds по образующей.</summary>
    internal static double WholeRevolvedArea(RevolvedSurface rv)
    {
        var o = rv.Axis.Origin; var w = rv.Axis.W.Normalized();
        double Rho(Vector3d p) { var d = p - o; return (d - w * Vector3d.Dot(d, w)).Length; }
        if (rv.Base is ArcCurve3 arc)
        {
            var U = arc.Frame.U.Normalized(); var W = arc.Frame.W.Normalized(); var V = Vector3d.Cross(W, U);
            var dc = arc.Frame.Origin - o;
            // плоскость дуги содержит ось: её нормаль ⟂ оси и центр дуги в плоскости (ось, нормаль⊥)
            if (Math.Abs(Vector3d.Dot(W, w)) < 1e-12 && Math.Abs(Vector3d.Dot(dc, W)) < 1e-9 * Math.Max(1, dc.Length))
            {
                var e = (dc - w * Vector3d.Dot(dc, w)); double rc = e.Length;
                if (rc > arc.Radius * (1 + 1e-12))      // образующая не касается оси: ρ = rc + r·(cosφ U·e + sinφ V·e) > 0
                {
                    e /= rc;
                    double a0 = arc.A0, a1 = arc.A1, r = arc.Radius, ue = Vector3d.Dot(U, e), ve = Vector3d.Dot(V, e);   // углы дуги (параметр t ∈ [0, 1])
                    double integral = r * (rc * (a1 - a0) + r * (ue * (Math.Sin(a1) - Math.Sin(a0)) - ve * (Math.Cos(a1) - Math.Cos(a0))));
                    return 2 * Math.PI * Math.Abs(integral);
                }
            }
        }
        const int N = 8192;               // Симпсон по параметру образующей
        double h = (rv.Base.T1 - rv.Base.T0) / N, sum = 0;
        double F(double t) { var p = rv.Base.PointAt(t); var d1 = (rv.Base.PointAt(t + h * 1e-3) - rv.Base.PointAt(t - h * 1e-3)).Length / (2 * h * 1e-3); return Rho(p) * d1; }
        for (int i = 0; i <= N; i++) sum += F(rv.Base.T0 + i * h) * (i == 0 || i == N ? 1 : i % 2 == 1 ? 4 : 2);
        return 2 * Math.PI * Math.Abs(sum * h / 3);
    }

    static readonly bool TorusBandOff = Environment.GetEnvironmentVariable("P3D_TORUSBAND") == "0";
    static readonly bool TorusGenCutOff = Environment.GetEnvironmentVariable("P3D_TORUSGENCUT") == "0";
    static readonly bool GenCutSplitOff = Environment.GetEnvironmentVariable("P3D_GENCUTSPLIT") == "0";
    public static long GeneralCutRefused;

    /// <summary>Интеграл грани на замкнутой поверхности вращения общим разрезом (см. вызов); null — не определён (отказ).</summary>
    static double? GeneralCut(TFace f, RevolvedSurface rv, bool area, int N, Func<Vector3d, double> SAt, Func<Vector3d, double> Theta, Func<double, double> HAtS, double Htot)
    {
        var loops = Loops(f).Select(l => l.Select(q => (Th: Theta(q), S: SAt(q))).ToList()).ToList();
        if (loops.Count == 0 || loops.Any(l => l.Count < 3)) return null;
        double TwoPi = 2 * Math.PI;
        double WrapS(double d) { d = Math.IEEERemainder(d, N); return d; }
        double WrapT(double d) => Math.IEEERemainder(d, TwoPi);
        // s* — середина наибольшего промежутка s вершин контуров
        var ss = loops.SelectMany(l => l.Select(x => x.S)).OrderBy(x => x).ToList();
        double sgap = -1, sStar = 0;
        for (int i = 0; i < ss.Count; i++) { double nx = i + 1 < ss.Count ? ss[i + 1] : ss[0] + N; if (nx - ss[i] > sgap) { sgap = nx - ss[i]; sStar = (ss[i] + sgap / 2) % N; } }
        if (sgap < 1e-9 * N) return null;
        var hp = f.InteriorHint!.Value; double thH = Theta(hp), sH = SAt(hp);
        if ((rv.PointAt(rv.ProjectToUV(hp)) - hp).Length > 1e-6 * Math.Max(1, (hp - rv.Axis.Origin).Length)) return null;   // точка не на поверхности — устарела
        // пересечения контуров с параллелью s* (θ, знак ds) и с меридианом θ_h (s, знак dθ)
        var lat = new List<(double Phi, int Ds)>(); var mer = new List<(double Sig, int Dt)>();
        foreach (var l in loops)
            for (int i = 0; i < l.Count; i++)
            {
                var a = l[i]; var b = l[(i + 1) % l.Count];
                if (i == l.Count - 1 && Math.Abs(WrapS(b.S - a.S)) < 1e-12 && Math.Abs(WrapT(b.Th - a.Th)) < 1e-12) continue;
                double ds = WrapS(b.S - a.S), dt = WrapT(b.Th - a.Th);
                if (Math.Abs(ds) > N / 4.0 || Math.Abs(dt) > Math.PI / 2) return null;            // шаг контура слишком крупный
                double r0 = WrapS(a.S - sStar), r1 = r0 + ds;
                if ((r0 < 0) != (r1 < 0) && r0 != r1)
                {
                    double u = r0 / (r0 - r1);
                    double phi = WrapT(a.Th + dt * u - thH); if (phi < 0) phi += TwoPi;
                    lat.Add((phi, Math.Sign(ds)));
                }
                double q0 = WrapT(a.Th - thH), q1 = q0 + dt;
                if ((q0 < 0) != (q1 < 0) && q0 != q1)
                {
                    double u = q0 / (q0 - q1);
                    double sg = WrapS(a.S + ds * u - sH); if (sg < 0) sg += N;
                    mer.Add((sg, Math.Sign(dt)));
                }
            }
        // точка (θ_h, s*) внутри? — чётность пересечений меридиана от точки грани до s* (по +s)
        double toStar = sStar - sH; while (toStar < 0) toStar += N; while (toStar >= N) toStar -= N;
        bool insideAtStar = mer.Count(m => m.Sig < toStar) % 2 == 0;
        // сторона ε: по каждому пересечению; все должны совпасть
        int? eps = null; bool conflict = false;
        void Vote(int e) { if (eps == null) eps = e; else if (eps != e) conflict = true; }
        lat.Sort((x, y) => x.Phi.CompareTo(y.Phi));
        if (lat.Count % 2 != 0) return null;
        double L = 0, prev = 0; bool st = insideAtStar;
        foreach (var (phi, dsg) in lat)
        {
            if (st) L += phi - prev;
            // область слева от обхода: ε·(−ds, dθ); область по −θ от пересечения (st до него) ⇒ ε = sign(ds), иначе −sign(ds)
            if (dsg != 0) Vote(st ? dsg : -dsg);
            st = !st; prev = phi;
        }
        if (st) L += TwoPi - prev;
        mer.Sort((x, y) => x.Sig.CompareTo(y.Sig));
        bool sm = true;          // от точки грани по +s — внутри
        foreach (var (sig, dtg) in mer) { if (dtg != 0) Vote(sm ? -dtg : dtg); sm = !sm; }
        if (eps == null && !conflict)
        {
            // ни параллель s*, ни меридиан точки грани контуров не пересекают (диски в стороне) — меридиан через вершину
            // контура: состояние на нём у s_h — чётностью пересечений параллели s_h от θ_h, далее чередование
            // меридиан — через середину размаха контура по θ (через вершину на краю размаха он может пройти мимо)
            var l0 = loops[0]; double tmin = 0, tmax = 0, acc = 0;
            for (int i = 0; i + 1 < l0.Count; i++) { acc += WrapT(l0[i + 1].Th - l0[i].Th); tmin = Math.Min(tmin, acc); tmax = Math.Max(tmax, acc); }
            double thM = l0[0].Th + (tmin + tmax) / 2 + 1e-9;
            double toM = WrapT(thM - thH); if (toM < 0) toM += TwoPi;
            int crossLat = 0; var merM = new List<(double Sig, int Dt)>();
            foreach (var l in loops)
                for (int i = 0; i + 1 < l.Count; i++)
                {
                    var a = l[i]; var b = l[i + 1]; double ds = WrapS(b.S - a.S), dt = WrapT(b.Th - a.Th);
                    double r0 = WrapS(a.S - sH), r1 = r0 + ds;
                    if ((r0 < 0) != (r1 < 0) && r0 != r1) { double u = r0 / (r0 - r1); double ph = WrapT(a.Th + dt * u - thH); if (ph < 0) ph += TwoPi; if (ph < toM) crossLat++; }
                    double q0 = WrapT(a.Th - thM), q1 = q0 + dt;
                    if ((q0 < 0) != (q1 < 0) && q0 != q1) { double u = q0 / (q0 - q1); double sg = WrapS(a.S + ds * u - sH); if (sg < 0) sg += N; merM.Add((sg, Math.Sign(dt))); }
                }
            merM.Sort((x, y) => x.Sig.CompareTo(y.Sig));
            bool sm2 = crossLat % 2 == 0;
            foreach (var (sig, dtg) in merM) { if (dtg != 0) Vote(sm2 ? -dtg : dtg); sm2 = !sm2; }
        }
        if (conflict || eps == null)
        {
            Interlocked.Increment(ref GeneralCutRefused);
            if (Environment.GetEnvironmentVariable("P3D_BANDDBG2") == "1") Console.WriteLine($"GENCUT refused face {f.Id}: conflict {conflict} eps {eps} lat {lat.Count} mer {mer.Count} s* {sStar:0.#} sH {sH:0.#} thH {thH:0.###} loops {loops.Count} [{string.Join(" | ", loops.Select(l => $"th {l.Min(x => x.Th):0.##}..{l.Max(x => x.Th):0.##} s {l.Min(x => x.S):0}..{l.Max(x => x.S):0} n {l.Count}"))}]");
            return null;
        }
        // ∮ α по контурам с H, непрерывной на (s*, s* + N]
        double HB(double si) { double x = si; while (x <= sStar) x += N; while (x > sStar + N) x -= N; return x >= N ? HAtS(x - N) + Htot : HAtS(x); }
        // звено, пересекающее параллель s*, делится в точке пересечения: по разные стороны H берётся со своей ветви (H(s*)
        // снизу, H(s*) + H_полн сверху); трапеция через разрыв давала ошибку первого порядка по шагу (брусок минус тор,
        // серия 8, случай 91: −0.025 мм³ при 1024 шагах, −0.009 при 4096). Мутация P3D_GENCUTSPLIT=0.
        double hStar = HAtS(sStar);
        double sum = 0;
        foreach (var l in loops)
            for (int i = 0; i + 1 < l.Count; i++)
            {
                double dth = WrapT(l[i + 1].Th - l[i].Th);
                double r0 = WrapS(l[i].S - sStar), r1 = r0 + WrapS(l[i + 1].S - l[i].S);
                if (!GenCutSplitOff && (r0 < 0) != (r1 < 0) && r0 != r1)
                {
                    double u = r0 / (r0 - r1);
                    double h0 = r0 > 0 ? hStar : hStar + Htot, h1 = r1 > 0 ? hStar : hStar + Htot;
                    sum += -0.5 * (HB(l[i].S) + h0) * dth * u - 0.5 * (h1 + HB(l[i + 1].S)) * dth * (1 - u);
                }
                else sum += -0.5 * (HB(l[i].S) + HB(l[i + 1].S)) * dth;
            }
        if (Environment.GetEnvironmentVariable("P3D_BANDDBG2") == "1") Console.WriteLine($"GENCUT face {f.Id} area {area} s* {sStar:0.#} L {L:0.####} eps {eps} Htot {Htot:G6} sum {sum:G6} lat {lat.Count} mer {mer.Count} insideAtStar {insideAtStar}");
        return area ? Math.Abs(eps.Value * sum + Htot * L) : sum + eps.Value * Htot * L;
    }
    static readonly bool TorusRestOff = Environment.GetEnvironmentVariable("P3D_TORUSREST") == "0";
    private static double RevolvedIntegral(TFace f, RevolvedSurface rv, bool area = false)
    {
        // KRN-02: грань — ВСЯ поверхность вращения (нет контура или только швы: рёбра, оба раза использованные этой гранью) —
        // интеграл по всему прямоугольнику (s, θ): 2π·H(полный оборот образующей), знак — по наружной нормали.
        bool whole = f.Loops.Count == 0 || f.Loops.All(l => l.CoEdges.Count == 0)
                  || f.Loops.SelectMany(l => l.CoEdges).GroupBy(ce => ce.Edge).All(g => g.Count() == 2);
        // KRN-03 (03.8): ПЛОЩАДЬ ВСЕЙ ЗАМКНУТОЙ ПОВЕРХНОСТИ ВРАЩЕНИЯ (целый тор — края одни швы): 2π·∫ρ ds по образующей
        // (Папп). У дуги в меридиональной плоскости — в замкнутом виде; прочие — Симпсоном. Прежде — NaN, и самопроверка
        // площади сетки такой грани молча пропускалась.
        if (whole && area && Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) < 1e-9) return WholeRevolvedArea(rv);
        if (whole && (area || Math.Abs(Math.Abs(rv.Sweep) - 2 * Math.PI) > 1e-9)) return double.NaN;
        var o = rv.Axis.Origin; var w = rv.Axis.W.Normalized();
        var (u, v) = (rv.Axis.U.Normalized(), Vector3d.Cross(w, rv.Axis.U.Normalized()));
        const int N = 4096;
        var zs = new double[N + 1]; var rs = new double[N + 1]; var H = new double[N + 1];
        for (int i = 0; i <= N; i++)
        {
            var p = rv.Base.PointAt(rv.Base.T0 + (rv.Base.T1 - rv.Base.T0) * i / N) - o;
            zs[i] = Vector3d.Dot(p, w); rs[i] = (p - w * zs[i]).Length;
        }
        // Полюса образующей (ρ = 0) — только на концах и только вне грани.
        for (int i = 1; i < N; i++) if (rs[i] < 1e-9) return double.NaN;
        for (int i = 1; i <= N; i++)
        {
            double dz = zs[i] - zs[i - 1], dr = rs[i] - rs[i - 1];
            double rm = 0.5 * (rs[i] + rs[i - 1]), zm = 0.5 * (zs[i] + zs[i - 1]);
            // Объём: ∫ (ρ² dz − zρ dρ); площадь: ∫ ρ |dC| (элемент площади ρ dθ ds).
            H[i] = H[i - 1] + (area ? rm * Math.Sqrt(dz * dz + dr * dr) : rm * rm * dz - zm * rm * dr);
        }
        // Ближайший отсчёт образующей к (z, ρ) точки: дробный индекс.
        // Постоянная интегрирования: H = 0 в полюсе образующей (ρ = 0 на конце), иначе форма
        // −H dθ особая в полюсе, который может лежать на грани (крышка тела вращения).
        bool pole0 = rs[0] < 1e-9, poleN = rs[N] < 1e-9;
        if (pole0 && poleN) return double.NaN;
        if (poleN) { double hN = H[N]; for (int i = 0; i <= N; i++) H[i] -= hN; }

        // Параметр точки на образующей — через проекцию поверхности (поворот в меридиан и
        // ближайшая точка кривой), а не перебором отсчётов: перебор стоил 4 096 сравнений на точку.
        double span = rv.Base.T1 - rv.Base.T0;
        double SAt(Vector3d x)
        {
            double t = rv.ProjectToUV(x).X;
            return Math.Clamp((t - rv.Base.T0) / span * N, 0, N);
        }
        // ПОСТАВКА 4: ОБРАЗУЮЩАЯ-ОТРЕЗОК (цилиндр, конус, плоское кольцо вращением) — H В ЗАМКНУТОМ ВИДЕ. Таблица по
        // правилу средней точки с линейной интерполяцией давала ошибку ~1e-4 мм³ на отверстии с цекованием, собранном
        // вращением (составной инструмент), против 3e-11 у тех же стенок выдавливанием. Для отрезка ρ(t) = ρ0 + Δρ·t,
        // z(t) = z0 + Δz·t, подынтегральное — многочлен, и H(t) = ∫0^t (ρ²z′ − zρρ′) dτ считается точно.
        Func<double, double>? exactH = null;
        if (rv.Base is LineCurve3 && !rs.Skip(1).Take(N - 1).Any(r0 => r0 < 1e-9))
        {
            double z0 = zs[0], r0 = rs[0], dz = zs[N] - zs[0], dr = rs[N] - rs[0];
            double len = Math.Sqrt(dz * dz + dr * dr);
            double Hraw(double t) => area
                ? len * (r0 * t + dr * t * t / 2)
                : dz * (r0 * r0 * t + r0 * dr * t * t + dr * dr * t * t * t / 3)
                  - dr * (z0 * r0 * t + (z0 * dr + dz * r0) * t * t / 2 + dz * dr * t * t * t / 3);
            double shiftN = poleN ? Hraw(1) : 0;
            exactH = t => Hraw(t) - shiftN;
        }
        // KRN-02: ОБРАЗУЮЩАЯ-ДУГА В МЕРИДИАНЕ (тор, сфера со смещённым центром) — H в замкнутом виде. В меридиане
        // ρ = ρc + R cos φ, z = zc + R sin φ, h = ρ²z′ − zρρ′, первообразная
        // F(φ) = R[(ρc² + R²) sin φ + ρcR(3φ/2 + sin 2φ/4) − zcρc cos φ + zcR sin²φ/2]; φ(t) = φ0 + σ(t − T0).
        if (exactH == null && rv.Base is ArcCurve3 bar)
        {
            var oc = bar.Frame.Origin - o; double zc = Vector3d.Dot(oc, w); var radial = oc - w * zc; double rc = radial.Length;
            var U = bar.Frame.U.Normalized(); var W = bar.Frame.W.Normalized(); var V = Vector3d.Cross(W, U);
            var er = rc > 1e-12 ? radial / rc : Vector3d.Zero;
            double R = bar.Radius;
            bool meridian = rc > 1e-12 && Math.Abs(Vector3d.Dot(W, Vector3d.Cross(er, w))) > 1 - 1e-12 && rc > R * (1 + 1e-12);
            if (meridian)
            {
                double sigma = Vector3d.Dot(Vector3d.Cross(U, V), Vector3d.Cross(er, w)) > 0 ? 1 : -1;
                double phi0 = Math.Atan2(Vector3d.Dot(U, w), Vector3d.Dot(U, er));
                double F(double p) => R * ((rc * rc + R * R) * Math.Sin(p) + rc * R * (1.5 * p + Math.Sin(2 * p) / 4) - zc * rc * Math.Cos(p) + zc * R * Math.Sin(p) * Math.Sin(p) / 2);
                double Phi(double t) => phi0 + sigma * t;
                double a0 = bar.A0, a1 = bar.A1, f0 = F(Phi(a0));
                if (area)
                {
                    // KRN-03: ПЛОЩАДЬ — тоже в замкнутом виде: ρ|C′| dt = R(ρc + R cos φ) dt, A(t) = ρc t + σR sin φ(t)
                    // (по таблице 4096 отсчётов — 1e-7 относительной ошибки у грани тора, «точная» площадь не была точной).
                    double A(double t) => rc * t + sigma * R * Math.Sin(Phi(t));
                    double sg = Math.Sign(a1 - a0), A0v = A(a0);
                    exactH = u => R * sg * (A(a0 + (a1 - a0) * u) - A0v);
                }
                else exactH = u => F(Phi(a0 + (a1 - a0) * u)) - f0;
                H[N] = exactH(1);
            }
            else ArcFormulaRejected++;
        }
        if (whole)
        {
            if (!closedBaseEarly()) return double.NaN;
            double htot = exactH != null ? exactH(1) : H[N];
            // знак: на наружном экваторе (наибольший ρ образующей) наружная нормаль грани смотрит от оси — тело внутри
            int iMax = 0; for (int i = 1; i <= N; i++) if (rs[i] > rs[iMax]) iMax = i;
            var pEq = rv.Base.PointAt(rv.Base.T0 + (rv.Base.T1 - rv.Base.T0) * iMax / N);
            var rad = pEq - o; rad -= w * Vector3d.Dot(rad, w);
            double sign = Vector3d.Dot(f.NormalAt(pEq), rad) >= 0 ? 1 : -1;
            return sign * 2 * Math.PI * Math.Abs(htot);
        }
        bool closedBaseEarly() => (rv.Base.PointAt(rv.Base.T0) - rv.Base.PointAt(rv.Base.T1)).Length < 1e-9;
        double HAtS(double si)
        {
            if (exactH != null) return exactH(si / N);
            int i0 = Math.Clamp((int)Math.Floor(si), 0, N - 1);
            double t = si - i0;
            return H[i0] + t * (H[i0 + 1] - H[i0]);
        }
        // Полюс на грани допустим только тот, где H = 0 (одна сторона образующей); проверки
        // тут нет — двуполюсная образующая отвергнута выше.
        if (false && f.Surface is FacetedSurface fsp)
            foreach (var (x, y, z) in fsp.Facets)
                foreach (var q in new[] { x, y, z })
                {
                    var d = q - o; if ((d - w * Vector3d.Dot(d, w)).Length < 1e-6) return double.NaN;
                }

        double Theta(Vector3d x) { var d = x - o; return Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u)); }
        bool closedBase = (rv.Base.PointAt(rv.Base.T0) - rv.Base.PointAt(rv.Base.T1)).Length < 1e-9;
        double sum = 0;

        if (!closedBase)
        {
            // Образующая открыта: H однозначна, форма −H(s) dθ определена на всей грани.
            foreach (var pts in Loops(f))
            {
                double hPrev = HAtS(SAt(pts[0]));
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    double dt = Wrap(Theta(pts[i + 1]) - Theta(pts[i]));
                    double hNext = HAtS(SAt(pts[i + 1]));
                    sum += -0.5 * (hPrev + hNext) * dt;
                    hPrev = hNext;
                }
            }
        }
        else
        {
            // Образующая замкнута (тор): H многозначна по s. Берётся форма θ·h(s) ds с θ,
            // однозначным на грани: разрез по θ — в наибольшем промежутке углов грани. Грань,
            // охватывающая ось целиком, не поддержана.
            var angles = new List<double>();
            // g.05: разрез — в промежутке углов КОНТУРОВ, не содержащем точку грани (InteriorHint): контуры авторитетны, а
            // фасеты — производное (у неверной сетки пояса тора промежуток выходил ложным, интеграл грани — 0).
            bool byHint = !TorusBandOff && f.InteriorHint.HasValue;
            // g.05: ОБЩИЙ РАЗРЕЗ. Форма α = −H(s) dθ однозначна вне параллели s = s* (s* — любая, вне вершин контуров); по
            // Стоксу ∫_R h dθ∧ds = ∮_∂R α + H_полн · L, где L — длина по θ участков параллели s* внутри области R. Знак
            // стороны грани ε относительно (θ, s) — из обхода контуров в точках пересечения с параллелью (область слева от
            // обхода), и он обязан совпасть во всех таких точках (иначе — отказ). Объём: ∮ + ε·H_полн·L; площадь: |ε·∮ + H·L|.
            // Не требует разреза вне грани: прежние ветви (пояс, остаток, разрез по θ) ошибались у косых поясов (цилиндр
            // поперёк трубки, серия 10, случай 22: объём 547 вместо 1077 по OpenCascade) и у знака остатка. P3D_TORUSGENCUT=0.
            // Отказ общего разреза (несогласная сторона, крупный шаг контура) — NaN: маршрут отказывает, а не берёт частную
            // ветвь ниже (они остаются только для P3D_TORUSGENCUT=0 — прежнее поведение).
            if (byHint && closedBase && !TorusGenCutOff)
                return GeneralCut(f, rv, area, N, SAt, Theta, HAtS, exactH != null ? exactH(1) : H[N]) is double gc ? (area ? gc : gc + Vector3d.Dot(o - _origin, NormalIntegral(f))) : double.NaN;
            if (!byHint && f.Surface is FacetedSurface fsa) foreach (var (x, y, z) in fsa.Facets) { angles.Add(Theta(x)); angles.Add(Theta(y)); angles.Add(Theta(z)); }
            else foreach (var pts in Loops(f)) foreach (var q in pts) angles.Add(Theta(q));
            angles.Sort();
            double gap = 0, cut = 0;
            double hintTh = byHint ? Theta(f.InteriorHint!.Value) : double.NaN;
            for (int i = 0; i < angles.Count; i++)
            {
                double next = i + 1 < angles.Count ? angles[i + 1] : angles[0] + 2 * Math.PI;
                if (byHint) { double ht = hintTh; while (ht < angles[i]) ht += 2 * Math.PI; if (ht < next) continue; }   // промежуток — внутри грани
                if (next - angles[i] > gap) { gap = next - angles[i]; cut = angles[i] + gap / 2; }
            }
            // охват оси — по ОБОРОТАМ контура вокруг оси, не по промежуткам отсчётов (у полной окружности отсчёты через
            // 0.012 рад — «промежуток» внутри грани)
            bool wrapsAxis = false;
            if (byHint)
                foreach (var pts in Loops(f)) { double tot = 0; for (int i = 0; i + 1 < pts.Count; i++) tot += Wrap(Theta(pts[i + 1]) - Theta(pts[i])); if (Math.Abs(tot) > Math.PI) wrapsAxis = true; }
            // g.05: ГРАНЬ — ТОР БЕЗ ДИСКОВ (все контуры стягиваемы, точка грани вне каждого из них): ∫грань = ∫весь тор +
            // ∮контуры (разрез — в наибольшем промежутке углов контуров, без учёта точки грани): дополнение диска обходит ось
            // целиком, хотя контур — нет. Объединение тора с шаром на экваторе: объём −426 вместо 1793. Мутация P3D_TORUSREST=0.
            if (byHint && !wrapsAxis && !TorusRestOff && closedBase)
            {
                var loopsL = Loops(f).ToList();
                bool allContractible = true;
                foreach (var pts in loopsL)
                {
                    double tot = 0, sp = SAt(pts[0]);
                    for (int i = 0; i + 1 < pts.Count; i++) { double sn = SAt(pts[i + 1]); double ds = sn - sp; if (ds > N / 2.0) ds -= N; else if (ds < -N / 2.0) ds += N; tot += ds; sp = sn; }
                    if (Math.Abs(tot) > N / 2.0) allContractible = false;
                }
                if (allContractible && loopsL.Count > 0)
                {
                    // точка грани внутри диска какого-либо контура? (развёртка (θ, s·2π/N), подъём к контуру)
                    var hp = f.InteriorHint!.Value; double hth = Theta(hp), hs = SAt(hp) * 2 * Math.PI / N;
                    bool insideSome = false;
                    foreach (var pts in loopsL)
                    {
                        var poly = new List<Vector2d>(); Vector2d? pv = null;
                        foreach (var q in pts)
                        {
                            var a = new Vector2d(Theta(q), SAt(q) * 2 * Math.PI / N);
                            if (pv is { } p0) a = new Vector2d(p0.X + Math.IEEERemainder(a.X - p0.X, 2 * Math.PI), p0.Y + Math.IEEERemainder(a.Y - p0.Y, 2 * Math.PI));
                            poly.Add(a); pv = a;
                        }
                        double cx = poly.Average(x => x.X), cy = poly.Average(x => x.Y);
                        var hq = new Vector2d(cx + Math.IEEERemainder(hth - cx, 2 * Math.PI), cy + Math.IEEERemainder(hs - cy, 2 * Math.PI));
                        bool inn = false;
                        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                            if ((poly[i].Y > hq.Y) != (poly[j].Y > hq.Y) && hq.X < (poly[j].X - poly[i].X) * (hq.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inn = !inn;
                        if (inn) { insideSome = true; break; }
                    }
                    if (!insideSome)
                    {
                        // разрез — в наибольшем промежутке углов контуров
                        var angs = new List<double>(); foreach (var pts in loopsL) foreach (var q in pts) angs.Add(Theta(q));
                        angs.Sort(); double g2 = 0, cut2 = 0;
                        for (int i = 0; i < angs.Count; i++) { double nx = i + 1 < angs.Count ? angs[i + 1] : angs[0] + 2 * Math.PI; if (nx - angs[i] > g2) { g2 = nx - angs[i]; cut2 = angs[i] + g2 / 2; } }
                        if (g2 < 1e-3) return double.NaN;
                        double ThetaCut2(Vector3d x) { double t = Theta(x); while (t <= cut2) t += 2 * Math.PI; while (t > cut2 + 2 * Math.PI) t -= 2 * Math.PI; return t; }
                        double HtotR = exactH != null ? exactH(1) : H[N], sumR = 0;
                        foreach (var pts in loopsL)
                        {
                            double sPrev = SAt(pts[0]);
                            for (int i = 0; i + 1 < pts.Count; i++)
                            {
                                double sNext = SAt(pts[i + 1]); double dH = HAtS(sNext) - HAtS(sPrev);
                                if (sNext - sPrev > N / 2.0) dH -= HtotR; else if (sPrev - sNext > N / 2.0) dH += HtotR;
                                sumR += 0.5 * (ThetaCut2(pts[i]) + ThetaCut2(pts[i + 1])) * dH;
                                sPrev = sNext;
                            }
                        }
                        // весь тор со стороной грани (как у целой поверхности)
                        int iMax = 0; for (int i = 1; i <= N; i++) if (rs[i] > rs[iMax]) iMax = i;
                        var pEq = rv.Base.PointAt(rv.Base.T0 + (rv.Base.T1 - rv.Base.T0) * iMax / N);
                        var rad = pEq - o; rad -= w * Vector3d.Dot(rad, w);
                        double sgn = Vector3d.Dot(f.NormalAt(pEq), rad) >= 0 ? 1 : -1;
                        double whole2 = area ? WholeRevolvedArea(rv) : sgn * 2 * Math.PI * Math.Abs(HtotR);
                        double total = whole2 + sumR;
                        return area ? total : total + Vector3d.Dot(o - _origin, NormalIntegral(f));
                    }
                }
            }
            if (gap < 1e-3 || wrapsAxis)
            {
                // g.05: ГРАНЬ ОХВАТЫВАЕТ ОСЬ, НО НЕ ВСЮ ОБРАЗУЮЩУЮ (пояс тора между двумя параллелями — тор минус соосный
                // цилиндр): H однозначна на грани, если разрезать образующую в наибольшем промежутке её параметра, — форма
                // −H dθ. Охватывает и ось, и всю образующую — не поддержано (NaN). Мутация P3D_TORUSBAND=0.
                if (TorusBandOff) return double.NaN;
                var ss = new List<double>();
                if (!byHint && f.Surface is FacetedSurface fss && fss.Facets.Count > 0) foreach (var (x, y, z) in fss.Facets) { ss.Add(SAt(x)); ss.Add(SAt(y)); ss.Add(SAt(z)); }
                else foreach (var pts in Loops(f)) foreach (var q in pts) ss.Add(SAt(q));
                ss.Sort();
                double sgap = 0, scut = 0, hintS = byHint ? SAt(f.InteriorHint!.Value) : double.NaN;
                for (int i = 0; i < ss.Count; i++)
                {
                    double next = i + 1 < ss.Count ? ss[i + 1] : ss[0] + N;
                    if (byHint) { double hs = hintS; while (hs < ss[i]) hs += N; if (hs < next) continue; }
                    if (next - ss[i] > sgap) { sgap = next - ss[i]; scut = ss[i] + sgap / 2; }
                }
                if (sgap < 1e-3 * N) return double.NaN;
                // контур, обходящий образующую (через её шов), — разреза по s нет
                foreach (var pts in Loops(f))
                {
                    double tot = 0, sp = SAt(pts[0]);
                    for (int i = 0; i + 1 < pts.Count; i++) { double sn = SAt(pts[i + 1]); double ds = sn - sp; if (ds > N / 2.0) ds -= N; else if (ds < -N / 2.0) ds += N; tot += ds; sp = sn; }
                    if (Math.Abs(tot) > N / 2.0) return double.NaN;
                }
                double HtotB = exactH != null ? exactH(1) : H[N];
                double HUnwrapped(double si) { double x = si; while (x <= scut) x += N; while (x > scut + N) x -= N; return x >= N ? HAtS(x - N) + HtotB : HAtS(x); }
                double sumB = 0;
                foreach (var pts in Loops(f))
                {
                    double hPrev = HUnwrapped(SAt(pts[0]));
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        double dt = Wrap(Theta(pts[i + 1]) - Theta(pts[i]));
                        double hNext = HUnwrapped(SAt(pts[i + 1]));
                        sumB += -0.5 * (hPrev + hNext) * dt;
                        hPrev = hNext;
                    }
                }
                if (Environment.GetEnvironmentVariable("P3D_BANDDBG2") == "1")
                    foreach (var pts in Loops(f)) { double tot = 0; for (int i = 0; i + 1 < pts.Count; i++) tot += Wrap(Theta(pts[i + 1]) - Theta(pts[i])); Console.WriteLine($"  BANDLOOP s {SAt(pts[0]):0.#} H' {HUnwrapped(SAt(pts[0])):G6} dtheta {tot:0.###} closed {(pts[0] - pts[^1]).Length:E1} axisW {w:0.###} baseT {rv.Base.T0:0.###}..{rv.Base.T1:0.###} sweep {rv.Sweep:0.###} pt {pts[0]:0.##}"); }
                if (Environment.GetEnvironmentVariable("P3D_BANDDBG2") == "1") Console.WriteLine($"BAND face {f.Id} area {area} scut {scut:0.##} sgap {sgap:0.##} hintS {hintS:0.##} HtotB {HtotB:G6} sum {sumB:G6} loops {Loops(f).Count()} s-range {string.Join(" | ", Loops(f).Select(l => $"{l.Min(SAt):0.#}..{l.Max(SAt):0.#}"))}");
                return area ? sumB : sumB + Vector3d.Dot(o - _origin, NormalIntegral(f));
            }
            double ThetaCut(Vector3d x) { double t = Theta(x); while (t <= cut) t += 2 * Math.PI; while (t > cut + 2 * Math.PI) t -= 2 * Math.PI; return t; }
            double Htot = H[N];
            foreach (var pts in Loops(f))
            {
                if (Debug != null)
                {
                    double tmin = pts.Min(ThetaCut), tmax = pts.Max(ThetaCut), dsum = 0, sp = SAt(pts[0]);
                    for (int i = 0; i + 1 < pts.Count; i++) { double sn = SAt(pts[i + 1]); double dh = HAtS(sn) - HAtS(sp); if (sn - sp > N / 2.0) dh -= Htot; else if (sp - sn > N / 2.0) dh += Htot; dsum += dh; sp = sn; }
                    Debug($"loop: points {pts.Count}, theta {tmin:0.###}..{tmax:0.###}, sum dH {dsum:0.###}, Htot {Htot:0.###}, cut {cut:0.###}");
                }
                double sPrev = SAt(pts[0]);
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    double sNext = SAt(pts[i + 1]);
                    double dH = HAtS(sNext) - HAtS(sPrev);
                    if (sNext - sPrev > N / 2.0) dH -= Htot;          // через шов образующей
                    else if (sPrev - sNext > N / 2.0) dH += Htot;
                    sum += 0.5 * (ThetaCut(pts[i]) + ThetaCut(pts[i + 1])) * dH;
                    sPrev = sNext;
                }
            }
        }
        return area ? sum : sum + Vector3d.Dot(o - _origin, NormalIntegral(f));
    }

    /// <summary>
    /// ∫∫ n dA по грани = ½ ∮ x × dx (векторная площадь; верна для любой поверхности с
    /// краем — через край по настоящим кривым).
    /// </summary>
    /// <summary>u12: ∮ ½ x × dx по контурам грани — из памяти (грань, кривые и обход контуров, начало отсчёта, шаг): у ленты с
    /// процедурными краями — 20 мс на вызов, 31 вызов за скругление. Мутация P3D_NORMALCACHE=0.</summary>
    public static Vector3d NormalIntegral(TFace f)
    {
        if (NormalCacheOff) return NormalIntegralCore(f);
        var key = new List<object> { _origin, _coarse };
        foreach (var l in f.Loops) foreach (var ce in l.CoEdges) { key.Add(ce.Edge.Curve); key.Add(ce.Reversed); key.Add(ce.Edge.V0); key.Add(ce.Edge.V0.P); }
        var arr = key.ToArray();
        if (_normCache.TryGetValue(f, out var hit) && hit.Key.Length == arr.Length && hit.Key.Zip(arr).All(z => z.First is ValueType ? z.First.Equals(z.Second) : ReferenceEquals(z.First, z.Second)))
            return hit.V;
        var v = NormalIntegralCore(f);
        _normCache.AddOrUpdate(f, new NormEntry { Key = arr, V = v });
        return v;
    }
    private sealed class NormEntry { public object[] Key = null!; public Vector3d V; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TFace, NormEntry> _normCache = new();
    static readonly bool NormalCacheOff = Environment.GetEnvironmentVariable("P3D_NORMALCACHE") == "0";

    static Vector3d NormalIntegralCore(TFace f)
    {
        var sum = Vector3d.Zero;
        foreach (var loop in f.Loops)
            foreach (var ce in loop.CoEdges)
            {
                var e = ce.Edge; var c = e.Curve;
                bool curveForward = (c.PointAt(c.T0) - e.V0.P).LengthSquared <= (c.PointAt(c.T1) - e.V0.P).LengthSquared || ReferenceEquals(e.V0, e.V1);
                bool forward = ce.Reversed ? !curveForward : curveForward;
                if (c is ArcCurve3 arc)
                {
                    // ½∫ x × dx по дуге — замкнутой формулой: x = o + R(cos t U + sin t V).
                    var U = arc.Frame.U.Normalized(); var W = arc.Frame.W.Normalized(); var V = Vector3d.Cross(W, U);
                    double a0 = forward ? arc.A0 : arc.A1, a1 = forward ? arc.A1 : arc.A0;
                    Vector3d E(double t) => U * Math.Cos(t) + V * Math.Sin(t);
                    double R = arc.Radius;
                    sum += 0.5 * (R * Vector3d.Cross(arc.Frame.Origin - _origin, E(a1) - E(a0)) + R * R * W * (a1 - a0));
                    continue;
                }
                if (c is EllipseCurve3 el)
                {
                    // G4: эллипс — тоже замкнутой формулой: x = o + a cos t U + b sin t V;
                    // ½∫ x × dx = ½[a(cos t₁ − cos t₀) o×U + b(sin t₁ − sin t₀) o×V + ab(t₁ − t₀) U×V].
                    // По ломаной (Steps на кривую) глухое наклонное отверстие теряло 4e-3 мм³.
                    var U = el.Frame.U.Normalized(); var V = el.Frame.V.Normalized(); var o = el.Frame.Origin - _origin;
                    double t0 = forward ? el.A0 : el.A1, t1 = forward ? el.A1 : el.A0;
                    sum += 0.5 * (el.RU * (Math.Cos(t1) - Math.Cos(t0)) * Vector3d.Cross(o, U) + el.RV * (Math.Sin(t1) - Math.Sin(t0)) * Vector3d.Cross(o, V)
                                  + el.RU * el.RV * (t1 - t0) * Vector3d.Cross(U, V));
                    continue;
                }
                int m = c is LineCurve3 ? 1 : StepsFor(c);
                Vector3d prev = (forward ? c.PointAt(c.T0) : c.PointAt(c.T1)) - _origin;
                for (int i = 1; i <= m; i++)
                {
                    double t = forward ? c.T0 + (c.T1 - c.T0) * i / m : c.T1 - (c.T1 - c.T0) * i / m;
                    var q = c.PointAt(t) - _origin;
                    sum += 0.5 * Vector3d.Cross(prev, q);
                    prev = q;
                }
            }
        return sum;
    }
}
