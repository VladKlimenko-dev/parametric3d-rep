using OpenTK.Mathematics;
using Parametric3D.Kernel.Curves;
using Parametric3D.Kernel.Tess;
using Parametric3D.Kernel.Topology;

namespace Parametric3D.Kernel.Surfaces;

/// <summary>
/// KRN-06 (06.6, 06.13): N-СТОРОННИЙ УГЛОВОЙ ЛОСКУТ — трансфинитная поверхность по сторонам (Kato; Várady, «side-based»).
///
/// Область параметров — правильный m-угольник, вписанный в единичную окружность, нижняя сторона горизонтальна (у отрезка
/// снизу вверх внутри области — поток через край). У стороны i: кривая C_i(s), s ∈ [0, 1] от вершины i к вершине i + 1
/// области, и ЛЕНТА — поперечная производная T_i(s) (касательная к соседней грани, внутрь лоскута). В точке области:
///   d_k — расстояние до прямой стороны k (доля ширины области), s_k = d_{k−1} / (d_{k−1} + d_{k+1}) (у правильного
///   многоугольника на стороне k — линейно по стороне);
///   R_k = C_k(s_k) + d_k·T_k(s_k);  S = Σ w_k R_k / Σ w_k,  w_k = Π_{j≠k} d_j².
/// На стороне k все w_j (j ≠ k) равны нулю вместе с первой производной поперёк: S = C_k(s_k) и ∂S/∂n = C_k'·∂s_k/∂n +
/// T_k·∂d_k/∂n — лоскут ТОЧНО проходит через кривую стороны, и касательная плоскость вдоль неё натянута на C_k' и T_k
/// (касание соседней грани G1 — тождественно, а не в допуске). Совместимость в вершинах (лента стороны вдоль соседней
/// кривой) — забота строителя.
/// Вне многоугольника формула продолжается гладко (проекция точки края — Ньютоном без отсечения).
/// </summary>
public sealed class CornerPatchSurface : ISurface3
{
    public sealed class Side
    {
        public readonly ICurve3 Curve;
        public readonly bool Forward;
        public readonly Func<double, Vector3d> Ribbon;
        /// <summary>
        /// Поверхность ленты R(s, d) (необязательно): R(s, 0) = C(s), ∂R/∂d(s, 0) = T(s). null — прямая лента C + d·T.
        /// Угловой лоскут скругления берёт ленты НА ШАРАХ радиуса r (характеристические окружности канала шаров вдоль стороны,
        /// угол поворота по d с насыщением): точки лент не уходят от шаров, и лоскут — выпуклая смесь точек на них.
        /// </summary>
        public readonly Func<double, double, Vector3d>? Surf;
        /// <summary>
        /// Лента как точка на шаре радиуса BallR: центр c(s, d) и единичное направление w(s, d), R = c + BallR·w. Если она задана у
        /// всех сторон, смешиваются ОТДЕЛЬНО центры и направления: S = Σ B c + BallR·норм(Σ B w) — при общем центре лоскут
        /// лежит на сфере (смесь точек сферы легла бы внутрь, по хорде: угол без отступа был бы «площе» шара на 2 %).
        /// На стороне k — та же лента; поперечная производная — та же (производная нормировки единичного вектора — его же
        /// проекция, ∂w ⟂ w): касание сохраняется.
        /// </summary>
        public readonly Func<double, double, (Vector3d C, Vector3d W)>? Ball;
        public Side(ICurve3 curve, bool forward, Func<double, Vector3d> ribbon, Func<double, double, Vector3d>? surf = null, Func<double, double, (Vector3d C, Vector3d W)>? ball = null)
        { Curve = curve; Forward = forward; Ribbon = ribbon; Surf = surf; Ball = ball; }
        public Vector3d At(double s) { double t = Forward ? s : 1 - s; return Curve.PointAt(Curve.T0 + (Curve.T1 - Curve.T0) * t); }
    }

    public readonly IReadOnlyList<Side> Sides;
    readonly int _m;
    readonly Vector2d[] _vert, _nrm;
    readonly double[] _c;
    readonly double _scale;
    readonly (Vector2d Uv, Vector3d P)[] _seeds;

    /// <summary>Радиус шаров лент (0 — ленты точками).</summary>
    public readonly double BallR;
    public CornerPatchSurface(IReadOnlyList<Side> sides, double ballR = 0)
    {
        BallR = ballR > 0 && sides.All(x => x.Ball != null) && !BallOff ? ballR : 0;
        if (sides.Count < 3) throw new ArgumentException("corner patch: at least three sides");
        Sides = sides; _m = sides.Count;
        _vert = new Vector2d[_m]; _nrm = new Vector2d[_m]; _c = new double[_m];
        double phi0 = -Math.PI / 2 - Math.PI / _m;
        for (int k = 0; k < _m; k++) _vert[k] = new Vector2d(Math.Cos(phi0 + 2 * Math.PI * k / _m), Math.Sin(phi0 + 2 * Math.PI * k / _m));
        for (int k = 0; k < _m; k++)
        {
            var e = _vert[(k + 1) % _m] - _vert[k];
            _nrm[k] = new Vector2d(-e.Y, e.X).Normalized();            // обход против часовой: внутрь — левая нормаль
            _c[k] = Vector2d.Dot(_nrm[k], _vert[k]);
        }
        double width = _m % 2 == 0 ? 2 * Math.Cos(Math.PI / _m) : Math.Cos(Math.PI / _m) + 1;
        _scale = 1 / width;
        var seeds = new List<(Vector2d, Vector3d)>();
        foreach (double rho in new[] { 0.0, 0.2, 0.4, 0.6, 0.75, 0.88, 0.97 })
        {
            int na = rho == 0 ? 1 : Math.Max(8, 4 * _m);
            for (int a = 0; a < na; a++)
            {
                double ang = 2 * Math.PI * (a + 0.5) / na;
                var uv = new Vector2d(Math.Cos(ang), Math.Sin(ang)) * (rho * Math.Cos(Math.PI / _m));
                seeds.Add((uv, PointAt(uv)));
            }
        }
        _seeds = seeds.ToArray();
    }

    public int SideCount => _m;
    /// <summary>
    /// Множитель согласованной ленты: у вершины области сдвиг по стороне k на долю Δs меняет расстояние до прямой стороны
    /// k − 1 на Δd = Δs·L·sin(2π/m)/ширину. Лента стороны k − 1 в её конце, равная C_k'(0)·κ, совпадает с производной
    /// соседней кривой — в вершине нет кручения (без этого лоскут выгибался над плоскостью грани на 5e-3 мм при r = 3).
    /// </summary>
    public static double RibbonScale(int m)
    {
        double width = m % 2 == 0 ? 2 * Math.Cos(Math.PI / m) : Math.Cos(Math.PI / m) + 1;
        return width / (2 * Math.Sin(Math.PI / m) * Math.Sin(2 * Math.PI / m));
    }
    /// <summary>Вершина k области (начало стороны k).</summary>
    public Vector2d Vertex(int k) => _vert[((k % _m) + _m) % _m];
    /// <summary>
    /// Интеграл ∫∫ g(S, S_u × S_v) du dv по всей области (грань лоскута — вся область: её контур — стороны). Многоугольник
    /// режется на m треугольников от центра, каждый — на N² подтреугольников, в каждом — правило Дунавана 5-й степени
    /// (7 точек). Производные в вершинах области особы (интегрируемо) — сходимость подразбиением.
    /// </summary>
    public double Integrate(Func<Vector3d, Vector3d, double> g, int N)
    {
        double[] ba = { 1.0 / 3, 0.0597158717897698, 0.4701420641051151, 0.4701420641051151, 0.7974269853530873, 0.1012865073234563, 0.1012865073234563 };
        double[] bb = { 1.0 / 3, 0.4701420641051151, 0.0597158717897698, 0.4701420641051151, 0.1012865073234563, 0.7974269853530873, 0.1012865073234563 };
        double[] ww = { 0.225, 0.1323941527885062, 0.1323941527885062, 0.1323941527885062, 0.1259391805448271, 0.1259391805448271, 0.1259391805448271 };
        double sum = 0;
        for (int k = 0; k < _m; k++)
        {
            var A = Vector2d.Zero; var B = _vert[k]; var C = _vert[(k + 1) % _m];
            // подтреугольники решётки N×N: вершины A + (B − A)·i/N + (C − A)·j/N
            Vector2d L(int i, int j) => A + (B - A) * ((double)i / N) + (C - A) * ((double)j / N);
            for (int i = 0; i < N; i++)
                for (int j = 0; i + j < N; j++)
                {
                    void Tri(Vector2d p0, Vector2d p1, Vector2d p2)
                    {
                        double area = Math.Abs((p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y)) / 2;
                        for (int q = 0; q < 7; q++)
                        {
                            var uv = p0 * (1 - ba[q] - bb[q]) + p1 * ba[q] + p2 * bb[q];
                            var (su, sv) = Derivs(uv);
                            sum += ww[q] * area * g(PointAt(uv), Vector3d.Cross(su, sv));
                        }
                    }
                    Tri(L(i, j), L(i + 1, j), L(i, j + 1));
                    if (i + j + 1 < N) Tri(L(i + 1, j), L(i + 1, j + 1), L(i, j + 1));
                }
        }
        return sum;
    }

    public double UMin => -1; public double UMax => 1; public double VMin => _vert[0].Y; public double VMax => 1;

    void Dist(Vector2d uv, Span<double> d)
    {
        for (int k = 0; k < _m; k++) d[k] = (Vector2d.Dot(_nrm[k], uv) - _c[k]) * _scale;
    }

    public bool IsPlanar => false;
    static readonly bool BallOff = Environment.GetEnvironmentVariable("P3D_CORNERBALL") == "0";
    static double Wt(double d) => d * d;

    public Vector3d PointAt(Vector2d uv)
    {
        Span<double> d = stackalloc double[_m];
        Dist(uv, d);
        // w_k = Π_{j≠k} d_j² — префиксы и суффиксы произведений (без деления на нулевое d)
        Span<double> pre = stackalloc double[_m + 1], suf = stackalloc double[_m + 1];
        pre[0] = 1; for (int k = 0; k < _m; k++) pre[k + 1] = pre[k] * Wt(d[k]);
        suf[_m] = 1; for (int k = _m - 1; k >= 0; k--) suf[k] = suf[k + 1] * Wt(d[k]);
        double sw = 0; var acc = Vector3d.Zero; var accW = Vector3d.Zero;
        for (int k = 0; k < _m; k++)
        {
            double w = pre[k] * suf[k + 1];
            if (w == 0) continue;
            double a = d[(k - 1 + _m) % _m], b = d[(k + 1) % _m], den = a + b;
            double s = Math.Abs(den) < 1e-300 ? 0.5 : Math.Clamp(a / den, -0.5, 1.5);
            var sd = Sides[k];
            if (BallR > 0) { var (bc, bw) = sd.Ball!(s, d[k]); acc += bc * w; accW += bw * w; sw += w; continue; }
            var R = sd.Surf != null ? sd.Surf(s, d[k]) : sd.At(s) + sd.Ribbon(s) * d[k];
            acc += R * w; sw += w;
        }
        if (sw > 0 && double.IsFinite(sw)) return BallR > 0 ? acc / sw + accW.Normalized() * BallR : acc / sw;
        // вершина области (две стороны сразу): общая точка кривых
        int best = 0; double bd = double.MaxValue;
        for (int k = 0; k < _m; k++) { double q = (_vert[k] - uv).LengthSquared; if (q < bd) { bd = q; best = k; } }
        return Sides[best].At(0);
    }

    /// <summary>
    /// Производные по u и v — разностью четвёртого порядка с шагом 2e-4 области (у вершин области высшие производные велики: шаг 1e-3 давал
    /// нормаль с ошибкой 6e-8; 2e-4 — до 1e-9;
    /// для нормали и интегралов). Проекция берёт грубые (DerivsFast): точность якобиана влияет лишь на скорость Ньютона.
    /// </summary>
    public (Vector3d Su, Vector3d Sv) Derivs(Vector2d uv)
    {
        const double h = 2e-4;
        Vector3d D(Vector2d e) => (PointAt(uv - e * 2) - PointAt(uv + e * 2) + (PointAt(uv + e) - PointAt(uv - e)) * 8) / (12 * h);
        return (D(new Vector2d(h, 0)), D(new Vector2d(0, h)));
    }
    (Vector3d Su, Vector3d Sv) DerivsFast(Vector2d uv)
    {
        const double h = 1e-5;
        var du = new Vector2d(h, 0); var dv = new Vector2d(0, h);
        return ((PointAt(uv + du) - PointAt(uv - du)) / (2 * h), (PointAt(uv + dv) - PointAt(uv - dv)) / (2 * h));
    }

    public Vector3d NormalAt(Vector2d uv)
    {
        var (su, sv) = Derivs(uv);
        var n = Vector3d.Cross(su, sv);
        return n.LengthSquared > 0 ? n.Normalized() : n;
    }

    /// <summary>Номер экземпляра (подпись для памяти разметки граней).</summary>
    public readonly long Id = Interlocked.Increment(ref _ids);
    static long _ids;

    public Vector2d ProjectToUV(Vector3d p)
    {
        Vector2d uv = default; double bd = double.MaxValue;
        foreach (var (su, sp) in _seeds) { double q = (sp - p).LengthSquared; if (q < bd) { bd = q; uv = su; } }
        double prevLen = double.MaxValue;
        for (int it = 0; it < 80; it++)
        {
            var S = PointAt(uv); var (Su, Sv) = DerivsFast(uv); var r = p - S;
            double a11 = Vector3d.Dot(Su, Su), a12 = Vector3d.Dot(Su, Sv), a22 = Vector3d.Dot(Sv, Sv);
            double b1 = Vector3d.Dot(Su, r), b2 = Vector3d.Dot(Sv, r), det = a11 * a22 - a12 * a12;
            if (!(Math.Abs(det) > 1e-300)) break;
            var du = new Vector2d((b1 * a22 - b2 * a12) / det, (a11 * b2 - a12 * b1) / det);
            double len = du.Length;
            if (!double.IsFinite(len)) break;
            if (len > 0.25) du *= 0.25 / len;
            uv += du;
            // сходимость: шаг ниже 1e-14 или застой у шума вычисления (шаг перестал убывать вдвое) — прежде шаг 1e-15
            // недостижим, и каждая проекция шла все 80 шагов
            if (len < 1e-14 || (len < 1e-11 && len > 0.5 * prevLen)) break;
            prevLen = len;
        }
        return uv;
    }

    /// <summary>Тот же лоскут под аффинным отображением точек (стороны — образы кривых, ленты — образы векторов).</summary>
    public CornerPatchSurface Mapped(Func<Vector3d, Vector3d> x)
    {
        var sides = Sides.Select(sd =>
        {
            var src = sd;
            var c = new ProcCurve3(s => x(src.At(s)), 0, 1, false, "corner patch side");
            Func<double, double, Vector3d>? surf = src.Surf == null ? null : (s, d) => x(src.Surf(s, d));
            Func<double, double, (Vector3d, Vector3d)>? ball = src.Ball == null ? null : (s, d) => { var (bc, bw) = src.Ball(s, d); var xc = x(bc); return (xc, x(bc + bw) - xc); };
            return new Side(c, true, s => { var p = src.At(s); return x(p + src.Ribbon(s)) - x(p); }, surf, ball);
        }).ToList();
        return new CornerPatchSurface(sides, BallR);
    }

    public void TessellateFace(TFace face, MeshBuilder mb)
    {
        var m = ExactFaceMesher.Mesh(face, out _);
        if (m is not { } mm) return;
        int b = mb.VertexCount;
        foreach (var p in mm.P) mb.AddVertex(p, NormalAt(ProjectToUV(p)) * (face.Reversed ? -1 : 1));
        for (int i = 0; i + 2 < mm.T.Count; i += 3) mb.AddTriangle(b + mm.T[i], b + mm.T[i + 1], b + mm.T[i + 2]);
    }
}
